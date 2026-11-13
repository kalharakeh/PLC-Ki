// The Program page: what the PLC program does, in plain words first, then the code. The left list is the call tree from
// the OBs, with a dot where an alarm is on, a device is held back or a sequence runs. Each block starts with what the machine
// notes say about it (its sequence step and what it waits for, its devices, its alarms, why an output is off), then the code
// as rungs or text with live values. Values are found by symbol: a block's "#member" becomes "<instance>.member", and
// for multi-instances the call in the parent block tells which global signal is wired to each input.
"use strict";

(() => {
  const { esc, icon, state } = PK;
  const blockCache = {};        // name -> block details
  window.addEventListener("pk:program-changed", () => { for (const k of Object.keys(blockCache)) delete blockCache[k]; });
  const KW = new Set(("IF THEN ELSE ELSIF END_IF CASE OF END_CASE FOR TO BY DO END_FOR WHILE END_WHILE REPEAT UNTIL END_REPEAT " +
    "RETURN EXIT CONTINUE AND OR XOR NOT MOD TRUE FALSE REGION END_REGION VAR END_VAR VAR_INPUT VAR_OUTPUT VAR_IN_OUT VAR_TEMP " +
    "VAR_STAT CONSTANT FUNCTION_BLOCK FUNCTION END_FUNCTION_BLOCK END_FUNCTION ORGANIZATION_BLOCK END_ORGANIZATION_BLOCK BEGIN " +
    "DATA_BLOCK END_DATA_BLOCK STRUCT END_STRUCT TON TOF TP R_TRIG F_TRIG").split(" "));

  async function block(name) {
    if (!blockCache[name]) blockCache[name] = await PK.api("GET", "/api/program/blocks/" + encodeURIComponent(name));
    return blockCache[name];
  }

  // ---- instances: where a block's "#x" lives in the PLC ----
  /**
   * All instance paths of a block, each with how its inputs are wired and who calls it:
   * [{ path, wires: {member -> symbol}, caller: { block, path, local } | null }]
   */
  async function instancesOf(name, depth = 0) {
    const p = state.program;
    if (!p || depth > 6) return [];
    const out = [];
    for (const db of p.blocks.filter((b) => b.instanceOf === name)) out.push({ path: `"${db.name}"`, wires: {}, caller: null });
    for (const parent of p.blocks) {
      for (const c of parent.calls || []) {
        if (c.block !== name || !c.instance) continue;
        if (c.instance.startsWith('"')) {
          // Its own instance DB: "DB_BatchSequence"(Start := ...) in FB_Station
          let o = out.find((x) => x.path === c.instance);
          if (!o) out.push(o = { path: c.instance, wires: {}, caller: null });
          if (!o.caller) {
            // The caller's own instance turns its "#LT101.Value" into "Station".LT101.Value
            const pi = (await instancesOf(parent.name, depth + 1))[0] || null;
            o.wires = await wiresIn(parent.name, c.instance, pi);
            o.caller = { block: parent.name, path: pi && pi.path, local: c.instance };
          }
          continue;
        }
        const local = c.instance.replace(/^#/, "");
        for (const pi of await instancesOf(parent.name, depth + 1)) {
          out.push({ path: pi.path + "." + local, wires: await wiresIn(parent.name, c.instance, pi), caller: { block: parent.name, path: pi.path, local: c.instance } });
        }
      }
    }
    return out;
  }

  /** In the parent's SCL: #XV101(Open := ..., ZSO := "DB_IO".XV101_ZSO) -> { ZSO: '"DB_IO".XV101_ZSO', ... } */
  async function wiresIn(parentName, instance, parentInst) {
    let src;
    try { src = (await block(parentName)).source; } catch { return {}; }
    if (!src) return {};
    const at = src.indexOf(instance + "(");
    if (at < 0) return {};
    let i = at + instance.length + 1, depthP = 1, args = "";
    for (; i < src.length && depthP > 0; i++) {
      const ch = src[i];
      if (ch === "(") depthP++;
      if (ch === ")") depthP--;
      if (depthP > 0) args += ch;
    }
    const wires = {};
    let cur = "", d = 0;
    const parts = [];
    for (const ch of args) { if (ch === "(") d++; if (ch === ")") d--; if (ch === "," && d === 0) { parts.push(cur); cur = ""; } else cur += ch; }
    parts.push(cur);
    for (const part of parts) {
      const m = part.replace(/\/\/.*$/gm, "").match(/^\s*(\w+)\s*(?::=|=>)\s*(.+?)\s*$/s);
      if (!m) continue;
      let target = m[2].trim();
      if (/^#/.test(target) && parentInst) target = parentInst.path + "." + target.slice(1);
      if (/^(NOT\s+)?("[^"]+"|#?\w+)(\.[\w"]+)*$/.test(target)) wires[m[1]] = target;
      else if (/\b(AND|OR|NOT)\b|[<>=]/.test(target)) {
        // An input wired to logic, e.g. Permit := #LT101.Value > 20.0 AND "DB_IO".EStop_OK: kept to evaluate live
        const inst = parentInst ? parentInst.path : null;
        wires[m[1]] = { expr: target.replace(/#(\w+)/g, (x, n) => (inst ? inst + "." + n : x)), text: target };
      }
    }
    return wires;
  }

  // ---- values the machine model does not read: the open block's members, asked for while it is shown ----
  const WATCH_TYPES = new Set(["Bool", "Int", "DInt", "Real", "Time"]);   // the types the server turns into values
  const STRUCTURED = /^("|Struct|Array|IEC_|TON|TOF|TP|TONR|R_TRIG|F_TRIG|CTU|CTD|CTUD|DTL|G7_)/i;
  const TIMER = /^(TON|TOF|TP|TONR)(_L?TIME)?$|^IEC_L?TIMER$/i;
  /** A member's type from the block interface: "Out" -> Bool, "tOpen.Q" (a timer) -> Bool; undefined when not known. */
  function memberType(b, name) {
    const parts = name.split(".");
    const m = ((b && b.interface) || []).find((x) => x.name === parts[0]);
    if (!m) return undefined;
    if (parts.length === 1) return m.datatype;
    if (parts.length === 2 && TIMER.test(m.datatype)) return /^(Q|IN)$/i.test(parts[1]) ? "Bool" : /^(ET|PT)$/i.test(parts[1]) ? "Time" : undefined;
    return undefined;
  }
  const seconds = (ms) => (ms < 1000 ? ms + " ms" : (ms / 1000).toFixed(ms % 1000 ? 1 : 0) + " s");
  /** The page's own answer for a symbol (the server sends "True" or numbers as text from the PLC, real values in test mode). */
  function watched(sym, type) {
    const w = window.PKTags && PKTags.value(sym);
    if (!w || w.error || w.value == null) return null;
    let v = w.value;
    if (v === "True" || v === "False") v = v === "True";
    else if (typeof v === "string" && /^-?\d+(\.\d+)?([eE][+-]?\d+)?$/.test(v.trim())) v = Number(v);
    const text = typeof v === "boolean" ? (v ? "TRUE" : "FALSE")
      : typeof v === "number" ? (type === "Time" ? seconds(v) : Number.isInteger(v) ? String(v) : v.toFixed(3).replace(/0+$/, "").replace(/\.$/, ""))
      : String(v);
    return { value: v, text };
  }

  /**
   * A block constant (#cSte_Idle) is not in the PLC's memory, so it can't be read live: its value is the start value
   * the export gives it. Plain numbers, TRUE/FALSE, typed literals (INT#5) and hex (16#FF); anything else (T#5s, text) is
   * shown as written but takes no part in a comparison.
   */
  function constantOf(b, name) {
    const m = ((b && b.interface) || []).find((x) => x.section === "Constant" && x.name === name);
    if (!m || m.startValue == null || String(m.startValue).trim() === "") return null;
    const s = String(m.startValue).trim();
    const out = (raw) => ({ raw, text: typeof raw === "boolean" ? (raw ? "TRUE" : "FALSE") : s, symbol: "#" + name, label: "constant" });
    if (/^(true|false)$/i.test(s)) return out(/^true$/i.test(s));
    const lit = s.replace(/^(?:[A-Za-z]+#)(?=[-+\d]|16#|2#|8#)/, "");     // INT#5 -> 5, DINT#16#FF -> 16#FF
    const radix = lit.match(/^(16|8|2)#([0-9A-Fa-f_]+)$/);
    if (radix) return out(parseInt(radix[2].replace(/_/g, ""), Number(radix[1])));
    if (/^[+-]?\d+(\.\d+)?([eE][+-]?\d+)?$/.test(lit)) return out(Number(lit));
    return out(s);
  }

  /**
   * A temp of the block, or a parameter of an FC: neither has a place in the PLC's memory between calls, so no read can
   * find it. Saying so is better than a "?" that looks like a fault. { temp: true } keeps it out of the read list.
   */
  function localOf(b, name) {
    const m = ((b && b.interface) || []).find((x) => x.name === name);
    if (!m) return null;
    const sec = String(m.section || "").toLowerCase();
    const at = { raw: null, symbol: "#" + name, temp: true };
    if (sec === "temp") return { ...at, text: PK.t("temp"), label: PK.t("temp"), why: PK.t("Temporary value: it exists only while the block runs, so it can't be read.") };
    if (b.kind === "FC" && /^(input|output|inout|return)$/.test(sec))
      return { ...at, text: PK.t("param"), label: PK.t("parameter"), why: PK.t("Parameter of this function: the caller sets it each time it runs, so it has no value of its own to read. Open the caller to see it.") };
    return null;
  }

  /**
   * operand ("#Out", "#Permit", "\"DB_IO\".X") -> its live value for this instance, or null.
   * want (a Map, optional) collects every symbol it needed that the model does not read, with its type, so the page can ask for it.
   */
  function resolver(inst, block, want) {
    const vals = PK.valueBySymbol();
    return (operand) => {
      let sym = operand;
      let negate = false;
      let type;
      if (operand.startsWith("#")) {
        const name = operand.slice(1);
        const first = name.split(".")[0];
        const k = name === first ? constantOf(block, name) : null;
        if (k) return k;
        const local = localOf(block, first);
        if (local) return name === first ? local : { ...local, symbol: operand };
        if (inst && inst.wires[first] && typeof inst.wires[first] === "object" && name === first) {
          const w = inst.wires[first];
          const toks = tokenize(w.expr), tree = toks && parseExpr(toks);
          const v = tree ? evalNode(tree, resolver(null, null, want)) : null;
          return v == null ? null : { raw: v, text: v ? "TRUE" : "FALSE", symbol: w.text, label: w.text };
        }
        if (inst && inst.wires[first] && name === first) {
          sym = inst.wires[first];
          if (/^NOT\s+/.test(sym)) { negate = true; sym = sym.replace(/^NOT\s+/, ""); }
        } else if (inst) { sym = inst.path + "." + name; type = memberType(block, name); }
        else return null;
      } else if (/^[A-Za-z_]\w*\.[A-Za-z_]\w*$/.test(operand) && block && String(block.logic || block.source || "").includes(operand.split(".")[0] + "(")) {
        // Strg_TO_Chars.Cnt: an output pin of an instruction box in the same network, gone once the block has run
        const [box, pin] = operand.split(".");
        return { raw: null, text: PK.t("pin"), symbol: operand, label: PK.t("box output"), temp: true,
          why: PK.t("Output {pin} of the {box} box in this network: it exists only while the block runs, so it can't be read.", { pin, box }) };
      }
      let t = vals.get(PK.norm(sym));
      if (!t) {
        if (type && STRUCTURED.test(type)) return null;          // a timer or struct as a whole has no single value
        const wt = type && WATCH_TYPES.has(type) ? type : null;
        if (want && !want.has(sym)) want.set(sym, wt);
        t = watched(sym, wt);
        if (!t) return null;
      }
      let raw = t.value;
      if (negate && typeof raw === "boolean") raw = !raw;
      return { raw, text: negate ? (raw ? "TRUE" : "FALSE") : t.text, symbol: sym, label: t.label };
    };
  }

  const chip = (v) => !v ? "" : typeof v.raw === "boolean"
    ? `<span class="chip ${v.raw ? "t" : "f"}" title="${esc(v.symbol)}">${v.raw ? "1" : "0"}</span>`
    : `<span class="chip ${v.temp ? "tmp" : ""}" title="${esc(v.temp ? v.why : v.symbol)}">${esc(v.text)}</span>`;

  /** A signal name that may break after "." and "_" instead of being cut: "Alarms".err<wbr>Pressurization */
  const nameHtml = (s) => esc(s).replace(/([._])/g, "$1<wbr>");

  // ---- SCL with live chips ----
  const OPERAND = /(#[A-Za-z_]\w*(?:\.[A-Za-z_]\w*|\[\d+\])*|"[^"]+"(?:\.(?:[A-Za-z_]\w*|"[^"]+")|\[\d+\])*)/;
  function sclLine(line, resolve) {
    let out = "";
    let rest = line;
    const ci = rest.indexOf("//");
    let comment = "";
    if (ci >= 0) { comment = rest.slice(ci); rest = rest.slice(0, ci); }
    const re = new RegExp(OPERAND.source + "|('[^']*')|\\b(\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?|T#\\w+)\\b|\\b([A-Za-z_]\\w*)\\b", "g");
    let last = 0, m;
    while ((m = re.exec(rest))) {
      out += esc(rest.slice(last, m.index));
      if (m[1]) out += opHtml(m[1]) + chip(resolve(m[1]));
      else if (m[2]) out += `<span class="str">${esc(m[2])}</span>`;
      else if (m[3]) out += `<span class="num">${esc(m[3])}</span>`;
      else out += KW.has(m[4].toUpperCase()) ? `<span class="kw">${esc(m[4])}</span>` : esc(m[4]);
      last = re.lastIndex;
    }
    out += esc(rest.slice(last));
    if (comment) out += `<span class="cm">${esc(comment)}</span>`;
    return out;
  }

  /** A signal in the code: a button that answers what sets it. A block or its instance DB ("FB_Valve", "DB_BatchSequence") links to that block. */
  function opHtml(op) {
    if (plainDraw) return esc(op);
    const bare = /^"([^"]+)"$/.exec(op);
    const b = bare && ((state.program && state.program.blocks) || []).find((x) => x.name === bare[1]);
    if (b && !/DB$/.test(b.kind)) return `<a class="op" href="${hrefOf(b.name)}">${esc(op)}</a>`;
    if (b && b.instanceOf) return `<a class="op" href="${hrefOf(b.instanceOf, [op])}">${esc(op)}</a>`;
    return `<button type="button" class="op" data-src="${esc(op)}" title="${esc(PK.t("What sets {name}?", { name: clean(op) }))}">${esc(op)}</button>`;
  }

  function codeHtml(text, resolve, highlight, find, lineNo) {
    const hl = highlight && !highlight.startsWith('"') ? new RegExp("#" + highlight.replace(/^#/, "").replace(/[.*+?^${}()|[\]\\]/g, "\\$&") + "\\b") : null;
    const f = (find || "").toLowerCase();
    let n = 0;
    return `<div class="code">${String(text).split(/\r?\n/).map((l, k) => {
      const hit = f && l.toLowerCase().includes(f);
      const mark = (hl && hl.test(l)) || writes(l, highlight) || (lineNo != null && k + 1 === Number(lineNo));
      return `<span class="ln ${mark ? "hl" : ""} ${hit ? "found" : ""}" ${hit ? `data-hit="${n++}"` : ""}>${sclLine(l, resolve) || " "}</span>`;
    }).join("")}</div>`;
  }

  // ---- LAD-style rungs from boolean statements ----
  const isSignal = (x) => !!x && /^(#|")/.test(x);
  let plainDraw = false;    // true while drawing for another page (rungFor): no buttons, as nothing there answers them
  let drawBlock = null;     // the block being drawn, set by whoever draws: a call to #inst links to the instance's type
  // Program page: all networks, or only those holding back an output now (remembered in this browser)
  let netFilter = "all";
  const readNetFilter = () => { netFilter = PK.prefs.get("netFilter", "all") === "holding" ? "holding" : "all"; };
  readNetFilter();
  document.addEventListener("pk:prefs", readNetFilter);
  function tokenize(expr) {
    const re = /\s*(\(|\)|AND\b|OR\b|XOR\b|NOT\b|<>|>=|<=|=|<|>|"[^"]+"(?:\.(?:[A-Za-z_]\w*|"[^"]+")|\[\d+\])*|#?[A-Za-z_][\w.]*(?:\[\d+\])?|T#\w+|-?\d+(?:\.\d+)?)/gy;
    const toks = [];
    let m, pos = 0;
    while ((m = re.exec(expr)) && m[1] !== undefined) { toks.push(m[1]); pos = re.lastIndex; }
    // Only a full match is logic: "0.9 * #SpeedSP" stops at "*" and is shown as a statement, not as a contact "0.9"
    return toks.length && !expr.slice(pos).trim() ? toks : null;
  }

  function parseExpr(toks) {
    let i = 0;
    const peek = () => toks[i];
    function or() { const items = [and()]; while (peek() === "OR" || peek() === "XOR") { i++; items.push(and()); } return items.length > 1 ? { t: "or", items } : items[0]; }
    function and() { const items = [unary()]; while (peek() === "AND") { i++; items.push(unary()); } return items.length > 1 ? { t: "and", items } : items[0]; }
    function unary() {
      if (peek() === "NOT") { i++; const x = unary(); return x.t === "leaf" ? { ...x, neg: !x.neg } : { t: "not", x }; }
      if (peek() === "(") { i++; const x = or(); if (peek() === ")") i++; return x; }
      const a = toks[i++];
      if (["=", "<>", ">=", "<=", "<", ">"].includes(peek())) { const op = toks[i++]; const b = toks[i++]; return { t: "cmp", a, op, b }; }
      return { t: "leaf", op: a, neg: false };
    }
    const tree = or();
    return i === toks.length ? tree : null;
  }

  function evalNode(n, resolve) {
    if (n.t === "leaf") {
      if (n.op === "TRUE" || n.op === "FALSE") return (n.op === "TRUE") !== n.neg;
      const v = resolve(n.op);
      if (!v || typeof v.raw !== "boolean") return null;
      return v.raw !== n.neg;
    }
    if (n.t === "cmp") {
      const num = (x) => { const v = /^-?\d/.test(x) ? { raw: Number(x) } : resolve(x); return v && typeof v.raw === "number" ? v.raw : null; };
      const a = num(n.a), b = num(n.b);
      if (a == null || b == null) return null;
      return { "=": a === b, "<>": a !== b, ">=": a >= b, "<=": a <= b, "<": a < b, ">": a > b }[n.op];
    }
    if (n.t === "not") { const v = evalNode(n.x, resolve); return v == null ? null : !v; }
    const vals = n.items.map((x) => evalNode(x, resolve));
    if (n.t === "and") return vals.includes(false) ? false : vals.includes(null) ? null : true;
    return vals.includes(true) ? true : vals.includes(null) ? null : false;
  }

  function drawNode(n, resolve, power, ctx) {
    const v = evalNode(n, resolve);
    if (n.t === "leaf" || n.t === "cmp") {
      const label = n.t === "cmp" ? `${n.a} ${n.op} ${n.b}` : n.op;
      const sym = n.t === "cmp" ? "CMP" : n.neg ? "|/|" : "| |";
      const live = n.t === "leaf" ? resolve(n.op) : null;
      const blocker = power && v === false && !ctx.blocked;
      if (blocker) { ctx.blocked = true; if (!ctx.first) ctx.first = { n, live }; }
      // A comparison names both sides' values: "State_Value = #cSte_Idle (5 = 5)"
      const sides = n.t === "cmp" ? [n.a, n.b].map((x) => (/^-?\d/.test(x) ? x : (resolve(x) || {}).text ?? "?")) : null;
      const shown = sides ? ` (${sides[0]} ${n.op} ${sides[1]})` : live ? " = " + live.text : "";
      // A temp or an FC parameter can never be read: its own look, not the "?" of a value that is missing
      const local = v == null ? (n.t === "leaf" ? (live && live.temp ? live : null) : [n.a, n.b].map((x) => (/^-?\d/.test(x) ? null : resolve(x))).find((x) => x && x.temp)) : null;
      const tip = local ? `${label}: ${local.why}`
        : v == null ? `${label}${sides ? shown : ""}: ${PK.t("no live value (not read, or not found in the PLC)")}` : `${label}${shown}: ${v ? PK.t("passes") : PK.t("does not pass")}`;
      // A click asks what sets it; a comparison asks about its first signal
      const src = plainDraw ? null : n.t === "cmp" ? [n.a, n.b].find(isSignal) : n.op;
      const attrs = src ? `data-src="${esc(src)}" aria-label="${esc(tip)}. ${esc(PK.t("What sets {name}?", { name: clean(src) }))}"` : "";
      const state = v === true ? "on" : v === false ? "off" : local ? "tmp" : "unk";
      return { html: `<${src ? 'button type="button"' : "div"} class="contact ${n.t === "cmp" ? "cmp" : ""} ${state} ${blocker && ctx.coilOff ? "blocker" : ""}" title="${esc(tip)}" ${attrs}>
          <span class="sym">${sym}${state === "unk" ? `<span class="q" aria-hidden="true">?</span>` : ""}</span><span class="nm">${nameHtml(label.replace(/^#/, ""))}${local ? `<small class="tmp-tag">${esc(local.label)}</small>` : ""}</span></${src ? "button" : "div"}>`, out: power && v === true };
    }
    if (n.t === "not") {
      const inner = drawNode(n.x, resolve, power, ctx);
      return { html: `<div class="ser"><span class="tag">NOT</span>${inner.html}</div>`, out: power && v === true };
    }
    if (n.t === "and") {
      let p = power, html = "";
      n.items.forEach((x, k) => {
        if (k) html += `<span class="wire ${p ? "on" : ""}"></span>`;
        const r = drawNode(x, resolve, p, ctx);
        html += r.html;
        p = r.out;
      });
      return { html: `<div class="ser">${html}</div>`, out: p };
    }
    // OR: parallel branches; a passing branch is enough, so the others are not marked as blockers
    const anyPass = v === true;
    const sub = { ...ctx, blocked: ctx.blocked || anyPass };
    const branches = n.items.map((x) => drawNode(x, resolve, power, sub));
    if (!anyPass && sub.blocked) { ctx.blocked = true; ctx.first = ctx.first || sub.first; }
    return { html: `<div class="par ${power && anyPass ? "on" : ""}">${branches.map((b) => `<div class="ser"><span class="wire ${power ? "on" : ""}"></span>${b.html}<span class="wire ${b.out ? "on" : ""}"></span></div>`).join("")}</div>`, out: power && anyPass };
  }

  /** Every contact and comparison of a rung, in order. */
  function conditions(n, out = []) {
    if (n.t === "leaf" || n.t === "cmp") out.push(n);
    else if (n.t === "not") conditions(n.x, out);
    else n.items.forEach((x) => conditions(x, out));
    return out;
  }
  /** A side of a comparison that never changes: a number, TRUE/FALSE, or a block constant (#cSte_Idle). */
  const fixed = (x, resolve) => /^[-+]?\d/.test(x) || /^(TRUE|FALSE)$/i.test(x) || ((resolve(x) || {}).label === "constant");
  /** "State_Value = #cSte_Idle" on its own: one state of a state decoder. Returns the signal that is compared, or null. */
  function stateSelect(tree, resolve) {
    if (tree.t !== "cmp" || tree.op !== "=") return null;
    if (fixed(tree.b, resolve) && !fixed(tree.a, resolve)) return tree.a;
    if (fixed(tree.a, resolve) && !fixed(tree.b, resolve)) return tree.b;
    return null;
  }
  /**
   * Is an output that is off expected to be on, so that the contact stopping it is worth pointing at?
   * Not when the stopper is a state selection (X = constant: the other states are off by design), not for a plain copy
   * of one signal, and not when nothing else on the rung is asking for it (no normally open contact on, no comparison
   * that holds): then the output is simply not wanted right now.
   */
  function expectedOn(tree, stopper, resolve) {
    if (stopper.t === "cmp" && stopper.op === "=" && (fixed(stopper.a, resolve) || fixed(stopper.b, resolve))) return false;
    const all = conditions(tree);
    if (all.length < 2) return false;
    return all.some((c) => c !== stopper && !(c.t === "leaf" && (c.neg || /^(TRUE|FALSE)$/i.test(c.op))) && evalNode(c, resolve) === true);
  }

  function rung(lhs, expr, resolve, box, opt = {}) {
    const toks = tokenize(expr);
    const tree = toks && parseExpr(toks);
    if (!tree) return null;
    const coil = box ? null : resolve(lhs);
    const result = evalNode(tree, resolve);
    const coilOn = coil && typeof coil.raw === "boolean" ? coil.raw : result;
    // A fault or alarm output that is off is the healthy state: nothing on its rung is "stopping" it
    const faultCoil = !box && /fault|alarm|error|\berr|warn|notready|not_?ok|trip|dev$/i.test(lhs || "");
    // Amber means "asked for, but held back". A dry pass finds the first contact that stops the rung; it is only
    // marked when the output is expected on. An output that is off because nothing asks for it is plain grey.
    const dry = { blocked: false, coilOff: false };
    drawNode(tree, resolve, true, dry);
    const held = coilOn === false && !faultCoil && !!dry.first && expectedOn(tree, dry.first.n, resolve);
    const ctx = { blocked: false, coilOff: held };
    const body = drawNode(tree, resolve, true, ctx);
    // A plain ( ) output that is held back, and the first contact that stops it: for "Why is it off" at the top
    if (opt.why && !box && !opt.coil && held && ctx.first) opt.why.push({ coil: lhs, first: ctx.first, net: opt.net });
    // One state of a state decoder ("Ste_Idle := State_Value = #cSte_Idle"): summed up in one line, not listed as off
    const sel = !box && !opt.coil && stateSelect(tree, resolve);
    if (sel && opt.why && opt.why.states) opt.why.states.push({ signal: sel, value: (resolve(sel) || {}).text, coil: lhs, on: coilOn, net: opt.net });
    // A coil that is a temp: dotted like a temp contact; its state is the rung's result when that is known
    const coilCls = coilOn === true ? "on" : coilOn === false ? "off" : coil && coil.temp ? "tmp" : "";
    const coilTip = `${clean(lhs)}${coil && coil.temp ? ": " + coil.why : coil ? " = " + coil.text : ""}`;
    const end = box
      ? `<div class="contact ${result ? "on" : result === false ? "off" : ""}"><span class="sym">${esc(box.kind)}</span><span class="nm">${nameHtml(box.name)}${box.pt ? " · " + nameHtml(box.pt) : ""}</span></div>`
      : plainDraw ? `<div class="contact coil ${coilCls}" title="${esc(coilTip)}"><span class="sym">${opt.coil ? "(" + opt.coil + ")" : "( )"}</span><span class="nm">${nameHtml(lhs.replace(/^#/, ""))}</span></div>`
      : `<button type="button" class="contact coil ${coilCls}" data-src="${esc(lhs)}" data-coil
          title="${esc(coilTip)}" aria-label="${esc(PK.t("Output {name}. Where is it used?", { name: coilTip }))}"><span class="sym">${opt.coil ? "(" + opt.coil + ")" : "( )"}</span><span class="nm">${nameHtml(lhs.replace(/^#/, ""))}</span></button>`;
    return `<div class="rung ${opt.hl ? "hl" : ""}"><span class="rail"></span><span class="wire on"></span>${body.html}<span class="wire ${body.out ? "on" : ""}"></span>${end}<span class="wire ${body.out ? "on" : ""}"></span><span class="rail"></span></div>`;
  }

  /** Decoded logic text -> networks of { title, lang, statements[] } */
  function networks(text) {
    const nets = [];
    let cur = null;
    for (const line of String(text || "").split(/\r?\n/)) {
      const h = line.match(/^\/\/\s*Network\s+(\d+)\s*(?:\((\w+)\))?\s*:?\s*(.*)$/);
      if (h) { cur = { no: h[1], lang: h[2] || "", title: h[3] || "", lines: [] }; nets.push(cur); continue; }
      if (!cur) { cur = { no: "", lang: "", title: "", lines: [] }; nets.push(cur); }
      if (line.trim()) cur.lines.push(line);
    }
    return nets;
  }

  /** SCL source -> one "network" per boolean assignment, so SCL blocks get rungs too */
  function sclNetworks(src) {
    const nets = [];
    const text = String(src || "").replace(/\(\*[\s\S]*?\*\)/g, "");
    let comment = "";
    for (const raw of text.split(/\r?\n/)) {
      const line = raw.trim();
      if (line.startsWith("//")) { comment = line.replace(/^\/\/\s*/, ""); continue; }
      const m = line.match(/^(#\w+(?:\.\w+)*|"[^"]+"(?:\.\w+)*)\s*:=\s*(.+);\s*(\/\/.*)?$/);
      if (m && /\b(AND|OR|NOT)\b/.test(m[2]) && !/[+\-*/]/.test(m[2].replace(/"[^"]*"/g, ""))) {
        nets.push({ no: String(nets.length + 1), lang: "SCL", title: comment, lines: [`${m[1]} := ${m[2]};`] });
      }
      if (line && !line.startsWith("//")) comment = "";
    }
    return nets;
  }

  /**
   * Does this line write the highlighted signal? A local "#name" (instance output) or a global symbol
   * ("\"Alarms\".ES1.EN"), as "x := ..." or as a set/reset "IF ... THEN x := TRUE; END_IF;".
   */
  function writes(line, highlight) {
    if (!highlight) return false;
    const sym = (highlight.startsWith('"') || highlight.startsWith("#") ? highlight : "#" + highlight).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    return new RegExp("(^|\\bTHEN\\s+)" + sym + "(?![\\w.\\[])\\s*:=").test(line.trim());
  }

  // ---- block calls as a table: "SequenceProgramTGF_DB"(INIT_SQ := … AND NOT …, Init.X => …); ----
  const CALL = /^(#[A-Za-z_]\w*|"[^"]+"|[A-Za-z_]\w*)\s*\(/;
  /** How many brackets are still open at the end of a line (strings and comments skipped). */
  function depth(line) {
    let d = 0, q = null;
    for (let i = 0; i < line.length; i++) {
      const c = line[i];
      if (q) { if (c === q) q = null; continue; }
      if (c === '"' || c === "'") q = c;
      else if (c === "/" && line[i + 1] === "/") break;
      else if (c === "(") d++;
      else if (c === ")") d--;
    }
    return d;
  }
  /** "A := x, B := f(y, z)" -> ["A := x", "B := f(y, z)"]: commas inside brackets or quotes don't split. */
  function splitTop(s) {
    const out = [];
    let d = 0, q = null, from = 0;
    for (let i = 0; i < s.length; i++) {
      const c = s[i];
      if (q) { if (c === q) q = null; continue; }
      if (c === '"' || c === "'") q = c;
      else if (c === "(" || c === "[") d++;
      else if (c === ")" || c === "]") d--;
      else if (c === "," && d === 0) { out.push(s.slice(from, i).trim()); from = i + 1; }
    }
    out.push(s.slice(from).trim());
    return out.filter(Boolean);
  }
  /** The block a call goes to, as a link when the program has it (an instance DB links to its FB, #inst to its type). */
  function calleeHtml(name, block) {
    const bare = name.replace(/^"|"$/g, "");
    const blocks = (state.program && state.program.blocks) || [];
    if (name.startsWith("#")) {
      const m = ((block && block.interface) || []).find((x) => x.name === name.slice(1));
      const type = m && String(m.datatype || "").replace(/^"|"$/g, "");
      return blocks.some((b) => b.name === type) ? `<span class="mono">${esc(name)}</span> <a href="${hrefOf(type)}">${esc(type)}</a>` : `<span class="mono">${esc(name)}</span>`;
    }
    const b = blocks.find((x) => x.name === bare);
    if (b) return b.instanceOf ? `<a class="mono" href="${hrefOf(b.instanceOf, [name])}">${esc(name)}</a> <span class="faint small">${esc(PK.t("values of {block}", { block: b.instanceOf }))}</span>` : `<a class="mono" href="${hrefOf(b.name)}">${esc(name)}</a>`;
    return `<span class="mono">${esc(name)}</span> <span class="faint small">${PK.t("built-in")}</span>`;
  }
  /**
   * One call statement -> a card with one row per parameter: → inputs (P := expr), ← outputs (Q => target).
   * The value is the operand's live value, or the result of a TRUE/FALSE expression. null when it is not a plain call.
   */
  function callHtml(stmt, resolve, block) {
    const m = stmt.replace(/\s*\/\/.*$/, "").match(/^(#[A-Za-z_]\w*|"[^"]+"|[A-Za-z_]\w*)\s*\(([\s\S]*)\)\s*;$/);
    if (!m || depth(stmt) !== 0) return null;
    const args = m[2].trim() ? splitTop(m[2]) : [];
    const rows = [];
    for (const a of args) {
      const p = a.match(/^([A-Za-z_][\w.]*)\s*(:=|=>)\s*([\s\S]+)$/);
      if (!p) return null;                       // positional or unusual arguments: leave it as text
      rows.push({ name: p[1], out: p[2] === "=>", expr: p[3].trim() });
    }
    if (!rows.length) return null;
    const one = new RegExp("^" + OPERAND.source + "$");
    const value = (expr) => {
      if (one.test(expr)) return chip(resolve(expr));
      if (/^[-+]?\d+(\.\d+)?$|^(TRUE|FALSE)$|^T#/i.test(expr)) return "";
      const toks = tokenize(expr), tree = toks && parseExpr(toks);
      const v = tree ? evalNode(tree, resolve) : null;
      return v == null ? "" : chip({ raw: v, text: v ? "TRUE" : "FALSE", symbol: expr });
    };
    // A plain operand is shown without its chip (the value has its own column); an expression keeps its chips
    const wired = (expr) => one.test(expr) ? opHtml(expr) : sclLine(expr, resolve);
    const comment = (stmt.match(/\/\/\s*(.*)$/) || [])[1];
    // In or out is only known when the code says so ("Q => x"); a decoded call writes both as ":=", and a wrong arrow
    // is worse than none
    const dirs = rows.some((r) => r.out);
    const dir = (r) => dirs ? `<td class="dir" title="${r.out ? PK.t("Output: the block writes it") : PK.t("Input: the block reads it")}">${r.out ? "←" : "→"}</td>` : "";
    return `<div class="call"><div class="call-h"><span class="tag">${PK.t("Call")}</span>${calleeHtml(m[1], block)}${comment ? `<span class="faint small">${esc(comment)}</span>` : ""}</div>
      <table class="tbl call-tbl"><tr>${dirs ? `<th aria-label="${PK.t("In or out")}"></th>` : ""}<th>${PK.t("Parameter")}</th><th>${PK.t("Wired to")}</th><th>${PK.t("Now")}</th></tr>
      ${rows.map((r) => `<tr>${dir(r)}<td class="mono">${esc(r.name)}</td><td class="mono wired">${wired(r.expr)}</td><td>${value(r.expr)}</td></tr>`).join("")}
      </table></div>`;
  }

  function netHtml(net, resolve, highlight, curStep, wantNet, why) {
    const parts = [];
    let plain = [];
    let inCur = false;
    const graphLine = (l) => {
      const st = /^\s*Step\s+(\d+)\b/.exec(l);
      if (st) inCur = curStep != null && Number(st[1]) === curStep;
      else if (/^\s{0,2}\S/.test(l)) inCur = false;
      return `<span class="gl ${inCur ? "cur" : ""}">${st && inCur ? `<span class="now-tag">${PK.t("now")}</span>` : ""}${sclLine(l, resolve)}</span>`;
    };
    const flush = () => { if (plain.length) { parts.push(net.lang === "GRAPH" ? `<div class="stmt graph">${plain.map(graphLine).join("")}</div>` : `<div class="stmt">${plain.map((l) => sclLine(l, resolve)).join("<br>")}</div>`); plain = []; } };
    for (let k = 0; k < net.lines.length; k++) {
      let line = net.lines[k];
      // A block call: a table of its parameters instead of a wall of text. One written over several lines is joined
      // until its brackets close.
      const head = line.trim().match(CALL);
      const isTimer = /^#\w+\s*\(\s*IN\s*:=/.test(line.trim());     // a timer is drawn as a box on its rung below
      if (net.lang !== "GRAPH" && head && !isTimer && !KW.has(head[1].toUpperCase())) {
        const from = k;
        while (depth(line) > 0 && k + 1 < net.lines.length) line += " " + net.lines[++k].trim();
        const call = callHtml(line.trim(), resolve, drawBlock);
        if (call) { flush(); parts.push(call); continue; }
        k = from; line = net.lines[k];             // not a plain call: leave its lines as they were
      }
      const s = line.trim().replace(/\s*\/\/.*$/, "");
      const timer = s.match(/^(#\w+)\s*\(\s*IN\s*:=\s*(.+?),\s*PT\s*:=\s*(.+?)\s*\)\s*;/);
      const assign = s.match(/^(#[\w.]+|"[^"]+"(?:\.(?:\w+|"[^"]+"))*)\s*:=\s*(.+);$/);
      // Set / reset coils, as the decoder writes them: IF <rung> THEN x := TRUE; END_IF;   // set
      const sr = s.match(/^IF\s+(.+)\s+THEN\s+(#[\w.]+|"[^"]+"(?:\.(?:\w+|"[^"]+"))*)\s*:=\s*(TRUE|FALSE);\s*END_IF;$/);
      let r = null;
      if (timer) r = rung(null, timer[2], resolve, { kind: (line.match(/\/\/\s*(TON|TOF|TP)/) || [, "TIMER"])[1], name: timer[1].slice(1), pt: timer[3].replace(/^#/, "") });
      else if (sr) r = rung(sr[2], sr[1], resolve, null, { coil: sr[3] === "TRUE" ? "S" : "R", hl: writes(s, highlight) });
      else if (assign) r = rung(assign[1], assign[2], resolve, null, { hl: writes(s, highlight), why, net: net.no });
      if (r) { flush(); parts.push(r); } else plain.push(line);
    }
    flush();
    const hl = highlight ? net.lines.some((l) => writes(l, highlight)) : wantNet != null && net.no === String(wantNet);
    // For the "Holding back now" filter: a network where a contact holds back an output that is asked for
    const holding = parts.some((p) => /class="contact[^"]*\bblocker\b/.test(p));
    return `<div class="rung-card ${hl ? "hl" : ""}" ${hl ? 'id="hl"' : ""} data-net="${esc(net.no)}" ${holding ? 'data-holding="1"' : ""}>
      <div class="rung-title">${net.no ? PK.t("Network {no}", { no: esc(net.no) }) : ""} ${net.lang ? `<span class="tag lang-${esc(net.lang)}">${esc(net.lang)}</span>` : ""}<span class="faint">${esc(net.title)}</span></div>
      ${parts.join("")}</div>`;
  }

  // ---- the Program page ----
  // #/program/<block>[/<instance>[/<local>]][/network-N|/line-N][/@<symbol>][/as-rungs|/as-text]
  // The old #/logic/... and #/code/... links still work: they open this page as rungs or as text.
  const MODE = /^as-(rungs|text)$/;
  const clean = (x) => String(x || "").replace(/^#/, "");

  /** "Valve_Open is off because EStop_OK is off": the first contact that stops an output, in plain words. */
  function because(f, resolve, instPath) {
    const n = f.n;
    if (n.t === "cmp") {
      const a = /^-?\d/.test(n.a) ? null : resolve(n.a);
      return PK.t("{condition} is not true", { condition: `<b>${esc(clean(n.a))} ${esc(n.op)} ${esc(clean(n.b))}</b>` }) + (a ? ` (${PK.t("{name} is {value}", { name: esc(clean(n.a)), value: esc(a.text) })})` : "");
    }
    const v = f.live;
    const txt = v && typeof v.raw !== "boolean" ? ` (${esc(v.text)})` : "";
    // An input of the block: say which signal is wired to it, so the operator knows what to check
    const wired = n.op.startsWith("#") && v && v.symbol && instPath && !v.symbol.startsWith(instPath) ? `, ${PK.t("wired to {symbol}", { symbol: `<span class="mono">${esc(v.symbol)}</span>` })}` : "";
    return `${n.neg ? PK.t("{name} is on", { name: named(n.op, resolve, v) }) : PK.t("{name} is off", { name: named(n.op, resolve, v) })}${txt}${wired}`;
  }

  /** A signal by its plain name from the machine notes when there is one ("Heater E301 on"), with the program name after it. */
  function named(op, resolve, v) {
    v = v || resolve(op);
    const sym = PK.norm(v && v.symbol ? v.symbol : op);
    const t = ((state.model && state.model.tags) || []).find((x) => PK.norm(x.symbol) === sym);
    const label = t && t.label ? t.label : null;
    return label ? `<b>${esc(label)}</b> <span class="mono faint small">${esc(clean(op))}</span>` : `<b>${esc(clean(op))}</b>`;
  }

  /**
   * A state decoder (Ste_Aborted := State_Value = 1, Ste_Clearing := State_Value = 2, …) in one line:
   * "State_Value is 5: Ste_Idle is on; the other 7 states are off." Only for three or more states on one signal.
   */
  function statesHtml(states) {
    const groups = new Map();
    for (const s of states) { if (!groups.has(s.signal)) groups.set(s.signal, []); groups.get(s.signal).push(s); }
    const lines = [...groups].filter(([, g]) => g.length >= 3).map(([sig, g]) => {
      const val = g.find((s) => s.value != null);
      const on = g.filter((s) => s.on === true);
      const rest = g.length - on.length;
      const head = val ? `<b>${PK.t("{name} is {value}", { name: esc(clean(sig)), value: esc(val.value) })}</b>: ` : PK.t("{name} has no live value yet ({count} states).", { name: `<b>${esc(clean(sig))}</b>`, count: g.length }) + " ";
      const body = !val ? "" : on.length
        ? `${on.map((s) => `<button type="button" class="linkbtn" data-goto-net="${esc(s.net)}">${esc(clean(s.coil))}</button>`).join(", ")} ${PK.t("is on|are on", { n: on.length })}; ${PK.t("the other {n} state is off|the other {n} states are off", { n: rest })}.`
        : PK.t("none of these {count} states is on.", { count: g.length });
      return `<li>${head}${body}</li>`;
    });
    return lines.length ? `<div class="state-sum"><div class="why-h">${icon("list")}<b>${lines.length > 1 ? PK.t("States right now") : PK.t("State right now")}</b></div><ul>${lines.join("")}</ul></div>` : "";
  }

  function whyHtml(why, resolve, instPath) {
    if (!why.length) return "";
    const MAX = 6;
    return `<div class="why-off"><div class="why-h">${icon("info")}<b>${PK.t("Why an output is off right now|Why {n} outputs are off right now", { n: why.length })}</b></div>
      <ul>${why.slice(0, MAX).map((w) => {
        // The next question, as in the design: "Why is that off?" goes to what sets the contact that stops the output
        const n = w.first.n, src = n.t === "cmp" ? [n.a, n.b].find(isSignal) : n.op;
        const next = src ? `<button type="button" class="linkbtn" data-src="${esc(src)}">${n.t === "cmp" ? PK.t("What sets {name}?", { name: esc(clean(src)) }) : n.neg ? PK.t("Why is it on?") : PK.t("Why is it off?")}</button> · ` : "";
        return `<li>${PK.t("{what} is off because {reason}.", { what: named(w.coil, resolve), reason: because(w.first, resolve, instPath) })}
        ${next}${w.net ? `<button type="button" class="linkbtn" data-goto-net="${esc(w.net)}">${PK.t("Network {no}", { no: esc(w.net) })} ›</button>` : ""}</li>`;
      }).join("")}</ul>
      ${why.length > MAX ? `<p class="mt-2 mb-0 faint small">${PK.t("And {count} more: look for the amber contacts in the rungs.", { count: why.length - MAX })}</p>` : ""}</div>`;
  }

  // ---- what the machine model says about each block: its sequence, devices and alarms ----
  const reEsc = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  /** Every program block (not data blocks) a model location names: "FB x, transition y; latched in FC z" -> [x, z]. */
  function blocksIn(text) {
    text = String(text || "");
    const hits = [];
    for (const b of (state.program && state.program.blocks) || []) {
      if (!b.name || /DB$/.test(b.kind)) continue;
      const m = new RegExp(`(^|[^\\w.])${reEsc(b.name)}(?!\\w)`).exec(text);
      if (m) hits.push({ n: b.name, i: m.index + m[1].length });
    }
    // "Alarms" inside "Alarms Common": only the longer name counts
    return hits.filter((h) => !hits.some((o) => o !== h && o.i <= h.i && o.i + o.n.length >= h.i + h.n.length && o.n.length > h.n.length)).map((h) => h.n);
  }

  /** block name -> { seqs, eqs, alarms (all it can raise), active (on now) }, from the model and the live status. */
  function notesIndex() {
    const idx = new Map();
    const of = (n) => { if (!idx.has(n)) idx.set(n, { seqs: [], eqs: [], alarms: [], active: [] }); return idx.get(n); };
    const st = state.status || {};
    for (const q of st.sequences || []) { const b = PK.blockOf(q.block); if (b) of(b).seqs.push(q); }
    for (const e of st.equipment || []) for (const b of blocksIn(e.location)) of(b).eqs.push(e);
    const on = new Map((st.activeAlarms || []).map((a) => [a.id, a]));
    for (const a of (state.model && state.model.alarms) || []) for (const b of blocksIn(a.location)) { of(b).alarms.push(a); if (on.has(a.id)) of(b).active.push(on.get(a.id)); }
    // Alarms that are on but not in the model's list (should not happen): still shown where they are set
    for (const a of on.values()) for (const b of blocksIn(a.location)) if (!of(b).active.includes(a)) of(b).active.push(a);
    return idx;
  }
  const EMPTY = { seqs: [], eqs: [], alarms: [], active: [] };
  /** "alarm" (an alarm set here is on), "blocked" (a device here is asked to run but held back), "run" (a sequence here is in a step), or "". */
  const ownState = (n) => n.active.length ? "alarm" : n.eqs.some((e) => e.blockedBy) ? "blocked" : n.seqs.some((q) => q.step != null) && !PK.cpuStop() ? "run" : "";
  const RANK = { alarm: 3, blocked: 2, run: 1, "": 0 };

  /** The call tree from the OBs (Main first): [{ b, depth }], plus blocks no OB calls and the data blocks. */
  function callTree() {
    const p = state.program;
    const byName = new Map(p.blocks.map((b) => [b.name, b]));
    const seen = new Set(), rows = [];
    const walk = (b, depth) => {
      if (seen.has(b.name)) return;
      seen.add(b.name);
      rows.push({ b, depth });
      for (const c of b.calls || []) { const n = byName.get(c.block); if (n && !/DB$/.test(n.kind)) walk(n, depth + 1); }
    };
    const obs = p.blocks.filter((b) => b.kind === "OB").sort((a, b) => (a.number === 1 ? -1 : b.number === 1 ? 1 : (a.number ?? 999) - (b.number ?? 999)));
    obs.forEach((o) => walk(o, 0));
    return { rows, loose: p.blocks.filter((b) => !seen.has(b.name) && !/DB$/.test(b.kind)), dbs: p.blocks.filter((b) => /DB$/.test(b.kind)), byName };
  }
  /** The worst state of a block and everything it calls, for the dot in the list. */
  function rollup(byName, idx) {
    const memo = new Map();
    const go = (name, stack) => {
      if (memo.has(name)) return memo.get(name);
      if (stack.has(name)) return "";
      stack.add(name);
      let s = ownState(idx.get(name) || EMPTY);
      for (const c of (byName.get(name) || {}).calls || []) { const k = go(c.block, stack); if (RANK[k] > RANK[s]) s = k; }
      stack.delete(name);
      memo.set(name, s);
      return s;
    };
    return (name) => go(name, new Set());
  }
  /** Everything a block and the blocks it calls hold: their sequences, devices and alarms together. */
  function subtree(byName, idx, name) {
    const out = { seqs: [], eqs: [], alarms: [], active: [] };
    const seen = new Set();
    const add = (list, x) => { if (!list.includes(x)) list.push(x); };
    const go = (n) => {
      if (seen.has(n)) return;
      seen.add(n);
      const o = idx.get(n) || EMPTY;
      for (const k of ["seqs", "eqs", "alarms", "active"]) for (const x of o[k]) add(out[k], x);
      for (const c of (byName.get(n) || {}).calls || []) if (byName.has(c.block)) go(c.block);
    };
    go(name);
    return out;
  }
  /** How many program blocks a block calls, directly or further down. */
  function descendants(byName, name) {
    const seen = new Set([name]);
    const go = (n) => { for (const c of (byName.get(n) || {}).calls || []) { const b = byName.get(c.block); if (b && !/DB$/.test(b.kind) && !seen.has(b.name)) { seen.add(b.name); go(b.name); } } };
    go(name);
    return seen.size - 1;
  }
  /** Main -> FB_Station -> FB_Valve */
  function callPath(byName, name) {
    const seen = new Set();
    const dfs = (b, path) => {
      if (b.name === name) return path;
      if (seen.has(b.name)) return null;
      seen.add(b.name);
      for (const c of b.calls || []) { const n = byName.get(c.block); const r = n && dfs(n, [...path, n.name]); if (r) return r; }
      return null;
    };
    for (const ob of [...byName.values()].filter((b) => b.kind === "OB")) { seen.clear(); const r = dfs(ob, [ob.name]); if (r) return r; }
    return null;
  }
  const DOT_TEXT = { alarm: PK.T("An alarm set here is on"), blocked: PK.T("A device here is held back"), run: PK.T("A sequence here is running") };
  const dot = (s) => s ? `<span class="pdot ${s}" title="${PK.t(DOT_TEXT[s])}" aria-label="${PK.t(DOT_TEXT[s])}"></span>` : "";
  const plainTitle = (b) => (b.title || "").trim();

  // ---- "What it does": plain words first ----
  function seqHtml(q, compact) {
    // CPU not in RUN: the step is where the program was when it stopped, not what it does now
    const stop = PK.cpuStop();
    const time = q.timeInStep && !stop ? ` · ${esc(q.timeInStep)}` : "";
    const step = q.step != null ? `<span class="pill ${stop ? "" : "info"}">${stop ? PK.t("Last step {no}", { no: esc(String(q.step)) }) : PK.t("Step {no}", { no: esc(String(q.step)) })}${q.stepName ? " · " + esc(q.stepName) : ""}${time}</span>` : `<span class="pill">${PK.t("Not in a step")}</span>`;
    const next = (q.next || []);
    const wait = next.length ? next.map((n, k) => {
      const to = esc(String(n.to)) + (n.toName ? " " + PK.t("“{name}”", { name: esc(n.toName) }) : "");
      return `<p class="pg-wait">${k ? PK.t("Or, to go on to step {to} it waits for:", { to }) : PK.t("To go on to step {to} it waits for:", { to })}</p>
        <ul class="conds">${n.conditions.map(PK.condWait).join("")}</ul>`;
    }).join("") : "";
    const halted = stop ? `<div class="mt-3 notice red">${icon("warn")}<div>${esc(PK.srv(q.explanation))}</div></div>`
      : q.halted ? `<div class="mt-3 notice red">${icon("warn")}<div><b>${PK.t("The sequence is halted.")}</b>${q.haltReasons && q.haltReasons.length ? " " + q.haltReasons.map(esc).join(" ") : ""}</div></div>` : "";
    return `<div class="pg-seq">
      <div class="row" style="gap:8px 10px;flex-wrap:wrap"><b>${esc(q.name)}</b>${step}${stop ? `<span class="pill bad">${PK.t("Not running")}</span>` : q.halted ? `<span class="pill bad">${PK.t("Halted")}</span>` : ""}</div>
      ${q.does && !stop ? `<p class="pg-does">${esc(q.does)}</p>` : ""}${halted}${compact ? "" : wait}
      ${compact ? "" : `<a class="small" href="#/sequence/${encodeURIComponent(q.id)}">${PK.t("Step chart and timeline ›")}</a>`}</div>`;
  }
  const capFirst = (x) => { x = PK.srv(x); return x.charAt(0).toUpperCase() + x.slice(1); };
  function eqRow(e) {
    return `<li><span class="ico">${icon(PK.kindIcon(e.kind))}</span><div class="grow min0">
        <a href="#/plant/devices/${encodeURIComponent(e.id)}"><b>${esc(e.name)}</b></a> <span class="faint small mono">${esc(e.id)}</span>
        ${e.blockedBy ? `<div class="pg-blocked">${esc(capFirst(e.blockedBy))}</div>` : ""}</div>
      <span class="lv lv-${esc(e.level)}">${esc(PK.srv(e.state))}</span></li>`;
  }
  const alarmRow = (a, on) => `<li><span class="pg-sev ${on ? "on" : ""}">${esc(a.id)}</span><div class="grow min0">
      <a href="#/alarms/${encodeURIComponent(a.id)}">${esc(a.text)}</a>${on && a.condition ? `<div class="faint small">${esc(a.condition)}</div>` : ""}</div>
      ${on ? `<span class="pill bad">${PK.t("On now")}</span>` : ""}</li>`;

  function whatItDoes(b, n, why, resolve, inst, byName) {
    const parts = [];
    for (const q of n.seqs) parts.push(`<div class="pg-sec"><h3>${PK.t("Sequence in this block")}</h3>${seqHtml(q)}</div>`);
    if (n.eqs.length) parts.push(`<div class="pg-sec"><h3>${PK.t("Devices it controls")}</h3><ul class="pg-rows">${n.eqs.map(eqRow).join("")}</ul></div>`);
    const states = statesHtml(why.states || []);
    if (states) parts.push(`<div class="pg-sec">${states}</div>`);
    if (why.length) parts.push(`<div class="pg-sec">${whyHtml(why, resolve, inst && inst.path)}</div>`);
    if (n.alarms.length || n.active.length) {
      const on = new Set(n.active.map((a) => a.id));
      const off = n.alarms.filter((a) => !on.has(a.id));
      parts.push(`<div class="pg-sec"><h3>${PK.t("Alarms it sets")}</h3>
        ${n.active.length ? `<ul class="pg-rows">${n.active.map((a) => alarmRow(a, true)).join("")}</ul>` : `<p class="mt-0 mb-2 muted small">${PK.t("None of them is on now.")}</p>`}
        ${off.length ? `<details class="more pg-more"><summary>${n.active.length ? PK.t("The other {n} alarm it can set|The other {n} alarms it can set", { n: off.length }) : off.length > 1 ? PK.t("All {n} alarms it can set", { n: off.length }) : PK.t("The {n} alarm it can set", { n: off.length })}</summary><ul class="pg-rows">${off.map((a) => alarmRow(a, false)).join("")}</ul></details>` : ""}</div>`);
    }
    if (!parts.length) {
      const path = callPath(byName, b.name);
      const parent = path && path.length > 1 ? path[path.length - 2] : null;
      parts.push(`<div class="pg-sec"><p class="m-0 muted">${PK.t("PLC-Ki's notes on the machine don't mention this block: it runs no sequence, controls no known device and sets no known alarm")}${parent ? `. ${PK.t("It is part of {block}", { block: `<a href="${hrefOf(parent)}">${esc(parent)}</a>` })}` : ""}. ${PK.t("The code below shows what it does.")}</p>
        <button type="button" class="mt-3 btn b small" data-ask="${esc(PK.t("Explain in plain words what PLC block {name} does, and what it is doing right now.", { name: b.name }))}">${PK.t("Explain this block in plain words")}</button></div>`);
    }
    return parts.join("");
  }

  const hrefOf = (name, extra = []) => "#/program/" + [name, ...extra].map(encodeURIComponent).join("/");

  // ---- "Show where it is set": where an alarm bit is latched and where its condition is made ----
  /** "network 4" (rungs) or "line 57" (SCL text): where a block writes a signal, or null. */
  function whereWritten(b, sym) {
    if (!b || !sym) return null;
    if (b.logic) { const n = networks(b.logic).find((x) => x.lines.some((l) => writes(l, sym))); return n && n.no ? PK.t("network {no}", { no: n.no }) : null; }
    const i = String(b.source || "").split(/\r?\n/).findIndex((l) => writes(l, sym));
    return i >= 0 ? PK.t("line {no}", { no: i + 1 }) : null;
  }

  /**
   * An alarm for the Program page: its bit, the blocks that write the bit (from the program search) and the cause the
   * machine notes name (block, instance, output). Without the search (no export, offline) the cause is still known.
   */
  async function alarmTrail(id) {
    const m = state.model || {};
    const def = (m.alarms || []).find((a) => a.id === id) || ((state.status && state.status.activeAlarms) || []).find((a) => a.id === id);
    if (!def) return null;
    const tag = def.tag && (m.tags || []).find((t) => t.id === def.tag);
    const symbol = tag && tag.symbol ? tag.symbol : null;
    const cause = window.PKLive ? PKLive.alarmWhere(def) : {};
    const at = async (name, sym) => { try { return whereWritten(await block(name), sym); } catch { return null; } };
    let latch = [];
    if (symbol) {
      try {
        const r = await PK.api("GET", "/api/program/search?q=" + encodeURIComponent(symbol));
        latch = (r.results || []).filter((x) => (x.lines || []).some((l) => l.writes)).map((x) => ({ block: x.block }));
      } catch { /* the cause alone */ }
      for (const l of latch) l.at = await at(l.block, symbol);
    }
    if (cause.block && cause.symbol) cause.at = await at(cause.block, cause.symbol);
    return { id, text: def.text, location: String(def.location || ""), symbol, latch, cause };
  }

  /**
   * One calm line on top of the code: "A02 · Latched in FB_AlarmHandler, line 57 · caused in FB_Valve, network 4
   * (output FaultNotOpen)". Each place is a link; the block on screen is in bold.
   */
  function alarmLine(t, here) {
    const place = (name, at, href) => (name === here ? `<b>${esc(name)}</b>` : `<a href="${esc(href)}">${esc(name)}</a>`) + (at ? ", " + esc(at) : "");
    const latchHref = (name) => hrefOf(name, ["@" + t.symbol, "alarm-" + t.id]);
    const c = t.cause || {};
    const parts = [];
    const same = c.block && t.latch.length === 1 && t.latch[0].block === c.block;
    if (same) parts.push(PK.t("Set in {place}", { place: place(c.block, t.latch[0].at || c.at, c.href) }));
    else {
      if (t.latch.length) parts.push(PK.t("Latched in {places}", { places: t.latch.map((l) => place(l.block, l.at, latchHref(l.block))).join(` ${PK.t("and")} `) }));
      if (c.block && c.symbol) { const v = { place: place(c.block, c.at, c.href), symbol: esc(c.symbol) }; parts.push(t.latch.length ? PK.t("caused in {place} (output {symbol})", v) : PK.t("Caused in {place} (output {symbol})", v)); }
      else if (c.block) {
        // No output in the notes: say what they say ("FB_Station, alarm input EStop (...)")
        const rest = t.location.split(/;|\blatched in\b/i)[0].replace(new RegExp("^\\s*(?:(?:FB|FC|OB)\\s+)?\"?" + reEsc(c.block) + "\"?\\s*,?\\s*"), "").trim();
        const v = { place: place(c.block, null, c.href) };
        parts.push((t.latch.length ? PK.t("comes from {place}", v) : PK.t("Comes from {place}", v)) + (rest ? ": " + esc(rest) : ""));
      }
    }
    if (!parts.length) return "";
    const missing = t.symbol && !t.latch.length ? ` <span class="faint">${PK.t("PLC-Ki did not find the block that writes {symbol}.", { symbol: `<span class="mono">${esc(t.symbol)}</span>` })}</span>` : "";
    return `<div class="mb-3 notice alarm-trail">${icon("info")}<div><a href="#/alarms/${encodeURIComponent(t.id)}"><b>${esc(t.id)}</b></a> · ${parts.join(" · ")}.${missing}</div></div>`;
  }

  function program() {
    let sel = null;        // { name, params, block, instances, inst, mode } or { overview: true, params }
    let loading = null;
    let blockQuery = "";   // the search above the block list
    let find = "";         // text: find in this block
    let findAt = 0;
    let allParts = false;  // overview: show every part, not only the first ones
    let watchList = [];    // the open block's members the model does not read: asked for while they are drawn

    /** Rungs for LAD/FBD/GRAPH blocks and text for SCL, unless the link or the switch says otherwise. */
    const autoMode = (b) => (b.logic ? "rungs" : "text");

    async function select(params) {
      const p = state.program;
      if (!p) return;
      const want = (params.find((x) => MODE.test(x)) || "").replace(/^as-/, "") || null;
      const rest0 = params.filter((x) => !MODE.test(x));
      const first = rest0[0];
      if (sel && sel.params.join("/") === params.join("/")) return;
      if (!first) { sel = { overview: true, params }; PK.draw(); return; }
      loading = first;
      try {
        const b = await block(first);
        const instances = await instancesOf(first);
        // After the block: an instance path and a local output (devices), or "network-N" and "@<symbol>" (alarms)
        const rest = rest0.slice(1);
        const net = (rest.find((x) => /^network-\d+$/.test(x)) || "").slice(8) || null;
        const line = (rest.find((x) => /^line-\d+$/.test(x)) || "").slice(5) || null;
        const sym = (rest.find((x) => x.startsWith("@")) || "").slice(1) || null;
        const alarmId = (rest.find((x) => x.startsWith("alarm-")) || "").slice(6) || null;
        const plain = rest.filter((x) => !/^(network|line)-\d+$/.test(x) && !x.startsWith("@") && !x.startsWith("alarm-"));
        const inst = instances.find((i) => i.path === plain[0]) || instances[0] || null;
        let mode = want || autoMode(b);
        if (mode === "rungs" && !b.logic && !b.source) mode = "text";
        sel = { name: first, params, block: b, instances, inst, highlight: sym || plain[1] || null, network: net, line, mode, deep: !!(net || line || sym || want || alarmId),
          alarm: alarmId ? await alarmTrail(alarmId) : null };
      } catch (e) {
        sel = { name: first, params, error: e.message };
      }
      loading = null;
      PK.draw();
      // A link to a network or signal (from an alarm) jumps to it; a plain block link starts at the top.
      // Not smooth: a live redraw keeps the scroll position and would stop the animation half way.
      if (sel.deep) setTimeout(() => { const h = document.getElementById("hl") || document.querySelector(".ln.hl"); if (h) h.scrollIntoView({ block: "center" }); }, 60);
      else window.scrollTo({ top: 0 });
    }

    /** Switch rungs/text in place: the address keeps the choice, so a reload or a shared link shows the same. */
    function setMode(mode, net) {
      if (!sel || sel.error || sel.overview) return;
      sel.mode = mode;
      if (net != null) sel.network = String(net);
      sel.params = [...sel.params.filter((x) => !MODE.test(x)), "as-" + mode];
      PK.setHash("#/program/" + sel.params.map(encodeURIComponent).join("/"));
      PK.draw();
    }

    function list(tree, roll) {
      const p = state.program;
      const q = blockQuery.trim().toLowerCase();
      const cur = sel && sel.name;
      const item = (b, depth = 0) => {
        const t = plainTitle(b);
        return `<a href="${hrefOf(b.name)}" class="${cur === b.name ? "on" : ""}" style="padding-left:${10 + Math.min(depth, 5) * 14}px" ${t ? `title="${esc(t)}"` : ""}>
          <span class="pl-t"><span class="pl-n">${esc(b.name)}</span>${t && t !== b.name ? `<span class="pl-d">${esc(t)}</span>` : ""}</span>${dot(roll(b.name))}</a>`;
      };
      let html;
      if (q) {
        const ok = (b) => (b.name + " " + (b.title || "") + " " + (b.comment || "") + " " + (b.number ?? "")).toLowerCase().includes(q);
        const hits = p.blocks.filter(ok);
        html = hits.length ? hits.map((b) => item(b)).join("") : `<p class="faint small" style="padding:8px 10px">${PK.t("No block matches “{query}”.", { query: esc(blockQuery) })}</p>`;
      } else {
        const curDb = cur && tree.dbs.some((b) => b.name === cur), curLoose = cur && tree.loose.some((b) => b.name === cur);
        html = `<a href="#/program" class="pl-home ${sel && sel.overview ? "on" : ""}">${icon("info")}<span>${PK.t("Overview: what runs now")}</span></a>`
          + `<div class="grp">${PK.t("In the order the PLC runs them")}</div>` + tree.rows.map((r) => item(r.b, r.depth)).join("")
          + (tree.loose.length ? `<details class="pl-fold" ${curLoose ? "open" : ""}><summary>${PK.t("Not called from an OB ({n})", { n: tree.loose.length })}</summary>${tree.loose.map((b) => item(b)).join("")}</details>` : "")
          + (tree.dbs.length ? `<details class="pl-fold" ${curDb ? "open" : ""}><summary>${PK.t("Data blocks ({n})", { n: tree.dbs.length })}</summary>${tree.dbs.map((b) => item(b)).join("")}</details>` : "");
      }
      return `<input type="search" id="blk-q" class="blk-q" placeholder="${PK.t("Find a block")}" aria-label="${PK.t("Find a block")}" value="${esc(blockQuery)}">` + html;
    }

    /** The overview: what runs now and where it lives, then what each part of the program is for. */
    function overview(tree, idx, roll) {
      const st = state.status || {};
      const seqs = st.sequences || [], alarms = st.activeAlarms || [], blocked = (st.equipment || []).filter((e) => e.blockedBy);
      const where = (loc) => { const b = PK.blockOf(loc); return b && tree.byName.has(b) ? `<a class="pg-where" href="${hrefOf(b)}">${esc(b)} ›</a>` : ""; };
      const now = [];
      if (alarms.length) now.push(`<div class="pg-sec"><h3>${PK.t("Alarms on now")}</h3><ul class="pg-rows">${alarms.map((a) => `<li><span class="pg-sev on">${esc(a.id)}</span>
          <div class="grow min0"><a href="#/alarms/${encodeURIComponent(a.id)}">${esc(a.text)}</a></div>${where(a.location)}</li>`).join("")}</ul></div>`);
      if (blocked.length) now.push(`<div class="pg-sec"><h3>${PK.t("Held back")}</h3><ul class="pg-rows">${blocked.map((e) => `<li><span class="ico">${icon(PK.kindIcon(e.kind))}</span>
          <div class="grow min0"><b>${esc(e.name)}</b><div class="pg-blocked">${esc(capFirst(e.blockedBy))}</div></div>${where(e.location)}</li>`).join("")}</ul></div>`);
      if (seqs.length) now.push(`<div class="pg-sec"><h3>${PK.t("Sequences")}</h3>${seqs.map((q) => `<div class="pg-seqrow">${seqHtml(q, false).replace('<div class="pg-seq">', `<div class="pg-seq">${where(q.block)}`)}</div>`).join("")}</div>`);
      const connected = st.connection && st.connection.state === "connected";
      const stop = PK.cpuStop(st);
      const right = `<div class="card pg-card"><div class="row" style="align-items:baseline"><h2 class="pg-h">${PK.t("Right now")}</h2><span class="spacer"></span>
          ${connected ? `<span class="pill ok"><span class="dot"></span>${PK.t("Live")}</span>` : st.connection ? `<span class="pill">${esc(st.connection.state === "snapshot" ? PK.t("Test values") : PK.t("Not live"))}</span>` : ""}</div>
        ${stop ? `<div class="mt-3 mb-1 notice red">${icon("warn")}<div><b>${PK.t("The PLC is in {mode} {since}: the program is not running.", { mode: esc(stop.word || "STOP"), since: esc(PK.cpuSince(stop)) })}</b> ${PK.t("What you see is its last state.")} <a href="#/plc">${PK.t("PLC page")}</a></div></div>`
          : !alarms.length && !blocked.length ? `<div class="mt-3 mb-1 notice green">${icon("check")}<div>${PK.t("No alarm is on and nothing the program asks for is held back.")}</div></div>` : ""}
        ${now.join("") || (!seqs.length ? `<p class="muted">${PK.t("The machine notes have no sequences to follow.")}</p>` : "")}</div>`;

      // What each part is for: the units Main calls (one per machine section, as a rule), with everything inside them
      // rolled up, so a library helper deep down does not get a card of its own
      const units = tree.rows.filter((r) => r.depth === 1).map((r) => r.b);
      const parts = (units.length ? units : tree.rows.map((r) => r.b).filter((b) => b.kind !== "OB"))
        .map((b) => ({ b, n: subtree(tree.byName, idx, b.name), size: descendants(tree.byName, b.name) }));
      const MAX = 12;
      const shown = allParts ? parts : parts.slice(0, MAX);
      const card = ({ b, n, size }) => {
        const chips = [];
        for (const q of n.seqs) chips.push(`<span class="pill info">${q.step != null ? `${PK.t("Step {no}", { no: esc(String(q.step)) })}${q.stepName ? " · " + esc(q.stepName) : ""}` : PK.t("Sequence")}</span>`);
        if (n.eqs.length) chips.push(`<span class="pill ${n.eqs.some((e) => e.blockedBy) ? "warn" : ""}">${n.eqs.length === 1 ? esc(n.eqs[0].name) : PK.t("{n} device|{n} devices", { n: n.eqs.length })}${n.eqs.some((e) => e.blockedBy) ? " · " + PK.t("held back") : ""}</span>`);
        if (n.alarms.length || n.active.length) chips.push(`<span class="pill ${n.active.length ? "bad" : ""}">${n.active.length ? PK.t("{n} alarm on|{n} alarms on", { n: n.active.length }) : PK.t("{n} alarm|{n} alarms", { n: n.alarms.length })}</span>`);
        const lang = (b.languages && b.languages[0]) || b.language || "";
        return `<a class="pg-part" href="${hrefOf(b.name)}"><div class="pg-part-top"><span class="pl-n">${esc(b.name)}</span>${lang ? `<span class="tag lang-${esc(lang)}">${esc(lang)}</span>` : ""}${dot(roll(b.name))}</div>
          <div class="pg-part-t">${esc(plainTitle(b) || b.name)}</div>${b.comment ? `<div class="pg-part-c">${esc(b.comment)}</div>` : ""}
          ${size ? `<div class="pg-part-c">${PK.t("{n} block inside|{n} blocks inside", { n: size })}</div>` : ""}
          ${chips.length ? `<div class="row" style="gap:6px;flex-wrap:wrap;margin-top:auto;padding-top:8px">${chips.join("")}</div>` : ""}</a>`;
      };
      const partsHtml = parts.length ? `<div class="card pg-card"><h2 class="pg-h">${PK.t("The parts of the program")}</h2>
          <p class="mt-1 mb-4 muted small">${PK.t("What Main calls, in the order the PLC runs them, with the sequences, devices and alarms inside each. Open one to see it explained, with the code and live values.")}</p>
          <div class="pg-parts">${shown.map(card).join("")}</div>
          ${parts.length > MAX ? `<button type="button" class="mt-4 btn b small" data-all-parts>${allParts ? PK.t("Show fewer") : PK.t("Show all {n}", { n: parts.length })}</button>` : ""}</div>` : "";
      return right + partsHtml;
    }

    return {
      live: true,
      needsValues: true,
      mount(el, params) {
        sel = null;
        watchList = [];
        if (window.PKTags && PKTags.programSymbols) PKTags.programSymbols(() => watchList);
        PK.loadProgram().then(() => select(params));
        PK.draw();
      },
      render(params) {
        if (!state.program) {
          if (state.programError) return PK.hero({ title: PK.t("No program loaded yet."),
            sub: PK.t("This page explains the PLC program in plain words, with live values, once PLC-Ki has read it from a TIA archive.") + " " + (PK.can("settings")
              ? PK.t("Import it under Setup, Import a program: it takes a few minutes.") : PK.t("Ask an administrator to import it into PLC-Ki.")),
            extra: `<details class="pk-detail mt-3"><summary>${PK.t("Details")}</summary><code>${esc(PK.srv(state.programError))}</code></details>`,
            actions: PK.can("settings") ? `<a class="btn" href="#/setup/program">${PK.t("Import the program")}</a>` : "" });
          return PK.hero({ title: PK.t("Program") }) + PK.sec("", PK.ui.loading("cards", { rows: 4, label: PK.t("Loading the program") }));
        }
        if (params.join("/") !== (sel ? sel.params.join("/") : "") && loading == null) select(params);
        const p = state.program;
        const tree = callTree();
        const idx = notesIndex();
        const roll = rollup(tree.byName, idx);
        const head = PK.hero({ cls: "hero compact", eyebrow: `${esc(p.project)} · ${esc(p.plc)}${p.cpu ? " · " + esc(p.cpu) : ""}`, title: PK.t("Program"),
          sub: PK.t("What the PLC program does and why, live. Each part is explained in plain words first; the code with live values is underneath.") });
        const picked = sel && !sel.overview;
        const body = (inner) => head + PK.sec("alt", `<div class="split ${picked ? "has-sel" : ""}"><nav class="blocklist" aria-label="${PK.t("Program blocks")}">${list(tree, roll)}</nav><div class="stack">${inner}</div></div>`);
        if (!sel || sel.overview || sel.error) watchList = [];
        if (!sel) return body(`<div class="card">${PK.ui.loading("table", { rows: 6 })}</div>`);
        if (sel.overview) return body(overview(tree, idx, roll));
        const crumbs = (name) => {
          const path = callPath(tree.byName, name) || [name];
          return `<nav class="crumbs" aria-label="${PK.t("Where this block sits")}"><a href="#/program">${PK.t("Program")}</a>${path.map((n, k) => ` <i>›</i> ${k === path.length - 1 ? `<b>${esc(n)}</b>` : `<a href="${hrefOf(n)}">${esc(n)}</a>`}`).join("")}</nav>`;
        };
        if (sel.error) return body(crumbs(sel.name) + PK.ui.error(sel.error));
        const b = sel.block;
        const asked = new Map();
        const resolve = resolver(sel.inst, b, asked);
        const eqName = (i) => { const e = ((state.status && state.status.equipment) || []).find((x) => x.location && x.location.includes(i.path)); return e ? " · " + e.name : ""; };
        const instPick = sel.instances.length > 1 ? `<label class="pg-inst"><span class="faint small">${PK.t("Which one")}</span><select id="inst" aria-label="${PK.t("Instance")}">${sel.instances.map((i) => `<option value="${esc(i.path)}" ${i === sel.inst ? "selected" : ""}>${esc(i.path + eqName(i))}</option>`).join("")}</select></label>`
          : sel.inst ? `<span class="pill info" title="${PK.t("Instance: where its values live")}">${esc(sel.inst.path)}</span>` : "";
        const lang = (b.languages && b.languages[0]) || b.language || "";
        const calledBy = (b.calledBy || []).map((n) => `<a href="${hrefOf(n)}">${esc(n)}</a>`).join(", ");
        const calls = [...new Set((b.calls || []).map((c) => c.block))].filter((n) => tree.byName.has(n)).map((n) => `<a href="${hrefOf(n)}">${esc(n)}</a>`).join(", ");
        const text = b.source || b.logic;

        // The rungs are worked out in both modes: they give the "why is it off" summary at the top
        const nets = b.logic ? networks(b.logic) : sclNetworks(b.source);
        sel.nets = nets;
        const seq = ((state.status && state.status.sequences) || []).find((q) => q.block && PK.blockOf(q.block) === b.name);
        // From an alarm with no output named: highlight the alarm bit when this block writes it (the latch)
        const alarmSym = sel.alarm && sel.alarm.symbol;
        const want = sel.highlight || (alarmSym && whereWritten(b, alarmSym) ? alarmSym : null);
        // The signal is not written in this block (or the decoder wrote it another way): fall back to the network number
        const hl = want && nets.some((n) => n.lines.some((l) => writes(l, want))) ? want : null;
        const why = [];
        why.states = [];          // the states of state decoders, summed up in one line each (statesHtml)
        drawBlock = b;
        const netsHtml = nets.map((n) => netHtml(n, resolve, hl || (want && !want.startsWith('"') ? want : null), seq ? seq.step : null, hl ? null : sel.network, why)).join("");
        const trail = sel.alarm ? alarmLine(sel.alarm, b.name) : "";

        // Notes for this block; with several instances (FB_Valve for XV101 and XV102) only the chosen one's devices and alarms
        let n = idx.get(b.name) || EMPTY;
        if (sel.instances.length > 1 && sel.inst) {
          const mine = (loc) => !/instance\s+"/.test(loc || "") || String(loc).includes(sel.inst.path + " ") || String(loc).includes(sel.inst.path + ",") || String(loc).includes(sel.inst.path + ")") || String(loc).endsWith(sel.inst.path);
          n = { ...n, eqs: n.eqs.filter((e) => mine(e.location)), alarms: n.alarms.filter((a) => mine(a.location)), active: n.active.filter((a) => mine(a.location)) };
        }

        const canRungs = nets.length > 0, canText = !!text;
        const mode = sel.mode === "rungs" && !canRungs ? (canText ? "text" : "rungs") : sel.mode === "text" && !canText ? "rungs" : sel.mode;
        const switcher = canRungs && canText ? PK.ui.choice(PK.t("Show the code as"), [["rungs", PK.T("Rungs")], ["text", PK.T("Text")]].map(([k, l]) => ({ label: PK.t(l), on: mode === k, data: { "data-mode": k } }))) : "";

        let bodyHtml;
        if (mode === "rungs" && canRungs) {
          const legend = `<div class="legend rung-legend"><span><i class="lg on"></i>${PK.t("Passes power")}</span><span><i class="lg off"></i>${PK.t("Does not pass")}</span>
            <span><i class="lg unk"></i>${PK.t("No live value")}</span><span><i class="lg tmp"></i>${PK.t("Temp or parameter (never readable)")}</span><span><i class="lg blocker"></i>${PK.t("Holds back an output that is asked for")}</span></div>`;
          // From an alarm, its own line says where it is set instead
          const where = trail || (sel.highlight && sel.highlight.startsWith('"')
            ? (hl ? `<div class="mb-3 notice">${icon("info")}<div>${PK.t("The highlighted rungs write {symbol}: (S) sets it, (R) resets it, ( ) follows the rung. The values are live; amber marks the first contact that stops a rung.", { symbol: `<b>${esc(sel.highlight)}</b>` })}</div></div>`
              : `<div class="mb-3 notice gold">${icon("info")}<div>${sel.network ? PK.t("{symbol} is not written in this block; network {no} is highlighted.", { symbol: esc(sel.highlight), no: esc(sel.network) }) : PK.t("{symbol} is not written in this block.", { symbol: esc(sel.highlight) })}</div></div>`) : "");
          // Long blocks: show only the networks where something holds back an output that is asked for
          const nHold = (netsHtml.match(/data-holding="1"/g) || []).length;
          const only = netFilter === "holding";
          const filter = nets.length > 3 ? `<div class="net-filter">${PK.ui.choice(PK.t("Which networks"), [{ label: PK.t("All {n} networks", { n: nets.length }), on: !only, data: { "data-netf": "all" } }, { label: PK.t("Holding back now · {n}", { n: nHold }), on: only, data: { "data-netf": "holding" } }])}
              ${only && !nHold ? `<p class="ok-line">${icon("check")}<span>${PK.t("Nothing in this block is holding back an output right now.")}</span></p>` : ""}</div>` : "";
          bodyHtml = where + filter + legend + `<div class="nets ${only ? "only-holding" : ""}">${netsHtml}</div>`
            + (b.source && !b.logic ? `<p class="mt-3 mb-0 faint small">${PK.t("Only the on/off statements of this SCL block are drawn as rungs.")} <button type="button" class="linkbtn" data-mode="text">${PK.t("Show all of it as text")}</button>.</p>` : "");
        } else if (canText) {
          const hits = find ? String(text).split(/\r?\n/).filter((l) => l.toLowerCase().includes(find.toLowerCase())).length : 0;
          bodyHtml = trail + `<div class="findbar"><input type="search" id="code-find" placeholder="${PK.t("Find in this block")}" aria-label="${PK.t("Find in this block")}" value="${esc(find)}">
              <span class="faint small" aria-live="polite">${find ? (hits ? PK.t("{n} line · Enter for the next|{n} lines · Enter for the next", { n: hits }) : PK.t("Not in this block")) : ""}</span></div>` + codeHtml(text, resolve, want, find, sel.line)
            + `<p class="mt-3 mb-0 faint small">${PK.t("Click a signal to see what sets it.")}</p>`;
        } else bodyHtml = `<p class="muted">${PK.t("No code in this block (data only). Its fields are listed below.")}</p>`;

        const iface = (b.interface || []).filter((m) => m.section !== "Temp" && m.section !== "Constant");
        const isDb = /DB$/.test(b.kind);
        const title = plainTitle(b);
        const tagsHtml = window.PKTags ? window.PKTags.blockTagsHtml(text).replace(/<details([^>]*) open>/, "<details$1>") : "";
        // What goes in and comes out, near the top: one compact chip per signal (type and comment on hover);
        // the stored values stay in the full table at the bottom
        const ofSec = (re) => iface.filter((m) => re.test(String(m.section)) && !/^void$/i.test(String(m.datatype)));
        const ins = ofSec(/^(Input|InOut)$/i), outs = ofSec(/^(Output|Return)$/i);
        const ioItem = (m) => { const v = resolve("#" + m.name); return `<span class="io" title="${esc(m.datatype + (m.comment ? " · " + m.comment : ""))}"><span class="mono">${nameHtml(m.name)}</span>${v && !v.temp ? chip(v) : ""}</span>`; };
        const ioRow = (label, list) => list.length ? `<div class="io-row"><span class="io-h">${label}</span><div class="io-list">${list.map(ioItem).join("")}</div></div>` : "";
        const ioCard = !isDb && (ins.length || outs.length) ? `<div class="card pg-card pg-io"><h2 class="pg-h">${PK.t("Inputs and outputs")}</h2>${ioRow(PK.t("In"), ins)}${ioRow(PK.t("Out"), outs)}</div>` : "";
        const page = body(crumbs(b.name) + `
            <div class="card pg-card">
              <div class="row" style="gap:10px 12px;flex-wrap:wrap;align-items:center"><h2 class="pg-title">${esc(title || b.name)}${dot(ownState(n))}</h2><span class="spacer"></span>${instPick}</div>
              <div class="pg-meta"><span class="mono">${esc(b.name)}</span><span class="tag">${esc(b.kind)}${b.number != null ? " " + b.number : ""}</span>${lang ? `<span class="tag lang-${esc(lang)}">${esc(lang)}</span>` : ""}
                ${calledBy ? `<span>${PK.t("Called by {list}", { list: calledBy })}</span>` : ""}${calls ? `<span>${PK.t("Calls {list}", { list: calls })}</span>` : ""}
                ${b.instanceOf ? `<span>${PK.t("Values of {block}", { block: `<a href="${hrefOf(b.instanceOf)}">${esc(b.instanceOf)}</a>` })}</span>` : ""}</div>
              ${b.comment ? `<p class="pg-lede">${esc(b.comment)}</p>` : ""}
              ${isDb ? "" : whatItDoes(b, n, why, resolve, sel.inst, tree.byName)}
            </div>
            ${ioCard}
            <div class="card pg-card" id="pg-code">
              <div class="row" style="gap:8px 14px;flex-wrap:wrap;align-items:center"><h2 class="pg-h">${isDb ? PK.t("Its values") : PK.t("The code")}</h2>${switcher}<span class="faint small">${!switcher ? "" : mode === "rungs" ? PK.t("Each network as a rung: green passes power, amber holds back an output that is asked for.") : PK.t("The code as written in TIA, with the live value after each signal.")}</span></div>
              <div class="mt-4">${bodyHtml}</div>
            </div>
            ${tagsHtml}
            ${iface.length ? `<div class="card"><details class="more" style="border:0;margin:0;padding:0" ${!text ? "open" : ""}><summary>${PK.t("Inputs, outputs and stored values ({n})", { n: iface.length })}</summary>
              <table class="mt-2 tbl iface"><tr><th>${PK.t("Name")}</th><th>${PK.t("Section")}</th><th>${PK.t("Type")}</th><th>${PK.t("Now")}</th><th>${PK.t("Comment")}</th></tr>
              ${iface.map((m) => { const v = resolve("#" + m.name); return `<tr><td>${esc(m.name)}</td><td class="muted">${esc(m.section)}</td><td class="mono">${esc(m.datatype)}</td><td>${v ? chip(v) : ""}</td><td class="muted">${esc(m.comment || "")}</td></tr>`; }).join("")}
              </table></details></div>` : ""}`);
        // Everything drawn above asked for its value: rungs or text first, then the interface table
        watchList = [...asked].slice(0, 300).map(([symbol, type]) => ({ symbol, type }));
        return page;
      },
      after(page) {
        if (window.PKTags && PKTags.wire) PKTags.wire(page);
        page.querySelectorAll("[data-mode]").forEach((b) => b.onclick = () => setMode(b.dataset.mode));
        page.querySelectorAll("[data-ask]").forEach((b) => b.onclick = () => window.PKAsk && window.PKAsk.open(b.dataset.ask));
        const all = page.querySelector("[data-all-parts]");
        if (all) all.onclick = () => { allParts = !allParts; PK.draw(); };
        page.querySelectorAll("[data-netf]").forEach((b) => b.onclick = () => {
          netFilter = b.dataset.netf === "holding" ? "holding" : "all";
          PK.prefs.set("netFilter", netFilter);
          PK.draw();
        });
        page.querySelectorAll("[data-goto-net]").forEach((b) => b.onclick = () => {
          const net = b.dataset.gotoNet;
          if (sel.mode !== "rungs") setMode("rungs");
          // A network the filter hides: show them all, so the jump lands
          else if (netFilter === "holding" && !page.querySelector(`.rung-card[data-net="${CSS.escape(net)}"][data-holding]`)) { netFilter = "all"; PK.draw(); }
          setTimeout(() => {
            const card = page.querySelector(`.rung-card[data-net="${CSS.escape(net)}"]`);
            if (!card) return;
            card.scrollIntoView({ block: "center", behavior: "smooth" });
            card.animate && card.animate([{ outline: "2px solid var(--accent)" }, { outline: "2px solid transparent" }], { duration: 1600 });
          }, 60);
        });
        const s = page.querySelector("#inst");
        if (s) s.onchange = () => { sel.inst = sel.instances.find((i) => i.path === s.value); PK.draw(); };
        const bq = page.querySelector("#blk-q");
        if (bq) bq.oninput = () => { blockQuery = bq.value; PK.draw(); };
        const cf = page.querySelector("#code-find");
        if (cf) {
          cf.oninput = () => { find = cf.value; findAt = 0; PK.draw(); jump(page, findAt); };
          cf.onkeydown = (e) => { if (e.key === "Enter") { e.preventDefault(); findAt += e.shiftKey ? -1 : 1; jump(page, findAt); } };
        }
        page.querySelectorAll("[data-src]").forEach((o) => o.onclick = () => explain(o, page));
        // A rung wider than its card fades on the right until it is scrolled to the end
        page.querySelectorAll(".rung").forEach((r) => {
          const edge = () => r.classList.toggle("more-right", r.scrollWidth - r.scrollLeft - r.clientWidth > 4);
          edge();
          r.onscroll = edge;
        });
      },
    };

    /** A contact, coil or signal was clicked: go to where this block sets it, or say in a sheet where it comes from. */
    async function explain(el, page) {
      if (!sel || !sel.block || el.dataset.busy) return;
      const op = el.dataset.src, coil = el.hasAttribute("data-coil"), ctx = { b: sel.block, inst: sel.inst };
      el.dataset.busy = "1";
      let r;
      try { r = coil ? { html: await usesOf(op, ctx) } : await sourceOf(op, ctx); }
      catch (e) { r = { html: PK.ui.error(e) }; }
      delete el.dataset.busy;
      if (r.here) { showHere(page, r.here); return; }
      const v = resolver(sel.inst, sel.block)(op);
      PK.sheet(`<span class="eyebrow">${coil ? PK.t("Where it is used") : PK.t("What sets it")}</span>
        <h2 class="mono" style="font-size:20px;word-break:break-all">${esc(clean(op))}${v ? " " + chip(v) : ""}</h2>
        <div class="src-answer">${r.html}</div><div class="row end"><button class="btn" data-close>${PK.t("Done")}</button></div>`);
    }

    /** Scroll to a place in this block: its rung, or its line in the text (switching to the text when it has no rung). */
    function showHere(page, here) {
      const n = sel.mode === "rungs" && (sel.nets || []).find((x) => x.lines.some(here.hit));
      if (n) {
        const card = () => page.querySelector(`.rung-card[data-net="${CSS.escape(n.no)}"]`);
        if (netFilter === "holding" && card() && !card().hasAttribute("data-holding")) { netFilter = "all"; PK.draw(); }
        flash(card());
        return;
      }
      if (sel.mode !== "text") setMode("text");
      setTimeout(() => flash(page.querySelectorAll("#pg-code .code .ln")[here.line - 1]), 60);
    }
  }

  /** Bring a rung or a line into view, mark it for a moment and put the keyboard there. */
  function flash(el) {
    if (!el) return;
    el.scrollIntoView({ block: "center" });
    if (el.animate) el.animate([{ outline: "2px solid var(--accent)", outlineOffset: "2px" }, { outline: "2px solid transparent", outlineOffset: "2px" }], { duration: 1800 });
    el.tabIndex = -1;
    el.focus({ preventScroll: true });
  }

  /** The old Logic and Code addresses open the Program page as rungs or as text, without an extra history step. */
  const alias = (mode) => ({
    transient: true,      // the back chip skips it: it only forwards
    mount(el, params) { location.replace("#/program/" + [...params, "as-" + mode].map(encodeURIComponent).join("/")); },
    render() { return ""; },
  });

  /** Scroll to the next line that matches "Find in this block". */
  function jump(page, at) {
    const hits = page.querySelectorAll(".ln.found");
    if (!hits.length) return;
    const el = hits[((at % hits.length) + hits.length) % hits.length];
    // the line keeps its place across live updates, so the scroll stays where it is
    el.scrollIntoView({ block: "center" });
    el.animate && el.animate([{ outline: "2px solid var(--accent)" }, { outline: "2px solid transparent" }], { duration: 1200 });
  }

  // ---- "What sets it": a click on a contact, a coil or a signal in the code says where its value comes from ----
  const progBlock = (name) => ((state.program && state.program.blocks) || []).find((x) => x.name === name);
  /** '"FB_Valve"' -> "FB_Valve" when it is a function block of this program (a block inside another one). */
  const fbOf = (datatype) => { const m = /^"([^"]+)"$/.exec(datatype || ""); const b = m && progBlock(m[1]); return b && b.kind === "FB" ? b.name : null; };
  const loadBlock = async (name) => { try { return await block(name); } catch { return null; } };

  /**
   * Where a block sets a signal: { net, line, hit } with the network (LAD/FBD) and the line in its text (1-based), or null.
   * Counts "x := ...", set/reset, a timer call for its .Q/.ET ("#tOpen(IN := ...)") and a GRAPH step action ("action N #x").
   */
  function placeIn(b, op) {
    if (!b || !op) return null;
    const name = op.replace(/^#/, "");
    const first = op.startsWith("#") ? name.split(".")[0] : null;
    const m = first && (b.interface || []).find((x) => x.name === first);
    const call = m && name.includes(".") && TIMER.test(m.datatype) ? new RegExp("^#" + reEsc(first) + "\\s*\\(") : null;
    const action = op.startsWith("#") ? new RegExp("^action\\s+\\w+\\s+" + reEsc(op) + "(?![\\w.])") : null;
    const hit = (l) => writes(l, op) || !!(call && call.test(l.trim())) || !!(action && action.test(l.trim()));
    const i = String(b.source || b.logic || "").split(/\r?\n/).findIndex(hit);
    if (i < 0) return null;
    const n = b.logic ? networks(b.logic).find((x) => x.lines.some(hit)) : null;
    return { net: n && n.no && n.lang !== "GRAPH" ? n.no : null, line: i + 1, hit };
  }
  const placeText = (p) => (p.net ? PK.t("network {no}", { no: p.net }) : PK.t("line {no}", { no: p.line }));
  /** A link to a place in a block: rungs at the network when it has one, otherwise the text at the line. */
  function placeHref(name, instPath, p, op) {
    const extra = instPath ? [instPath] : [];
    if (p && p.net) extra.push("network-" + p.net, ...(op ? ["@" + op] : []), "as-rungs");
    else if (p) extra.push("line-" + p.line, "as-text");
    return hrefOf(name, extra);
  }
  /** "FB_Station, line 47" as a link that closes the sheet. */
  const placeLink = (name, instPath, p, op) => `<a href="${esc(placeHref(name, instPath, p, op))}" data-close><b>${esc(name)}</b>${p ? ", " + esc(placeText(p)) : ""}</a>`;

  /** The line of a call in the caller, at the given input when it is named there: #XV101(Open := ..., Permit := ...). */
  function callLine(cb, local, input) {
    const lines = String((cb && (cb.source || cb.logic)) || "").split(/\r?\n/);
    const start = lines.findIndex((l) => l.includes(local + "("));
    if (start < 0) return null;
    const re = input ? new RegExp("(^|[(,\\s])" + reEsc(input) + "\\s*(:=|=>)") : null;
    for (let i = start; re && i < lines.length && i < start + 80; i++) {
      if (re.test(lines[i])) return { line: i + 1, text: lines[i].trim() };
      if (/\)\s*;/.test(lines[i])) break;
    }
    return { line: start + 1, text: lines[start].trim() };
  }

  /** The block and instance that hold a global signal: "Station".XV101.Out -> { FB_Valve, "Station".XV101, #Out }. */
  async function ownerOf(sym) {
    const m = /^"([^"]+)"((?:\.[A-Za-z_]\w*)+)$/.exec(sym);
    const db = m && progBlock(m[1]);
    if (!db || !db.instanceOf) return null;
    let fb = db.instanceOf, path = `"${m[1]}"`;
    const parts = m[2].slice(1).split(".");
    while (parts.length > 1) {
      const b = await loadBlock(fb);
      const inner = b && fbOf(((b.interface || []).find((x) => x.name === parts[0]) || {}).datatype);
      if (!inner) break;
      fb = inner; path += "." + parts.shift();
    }
    return { block: fb, path, op: "#" + parts.join(".") };
  }

  /** Every place that writes a global signal: the program search, plus the block whose instance data holds it. */
  async function writersOf(sym) {
    const out = [];
    let r = null;
    try { r = await PK.api("GET", "/api/program/search?q=" + encodeURIComponent(sym)); } catch { /* the owner alone */ }
    for (const x of (r && r.results) || []) {
      const wl = (x.lines || []).filter((l) => l.writes);
      if (!wl.length) continue;
      // conditional: every write in this block is inside an IF/CASE or after THEN (the server tells per line)
      out.push({ block: x.block, path: null, op: sym, place: placeIn(await loadBlock(x.block), sym), conditional: !wl.some((l) => l.conditional === false) });
    }
    const own = await ownerOf(sym);
    if (own && !out.some((o) => o.block === own.block)) {
      const p = placeIn(await loadBlock(own.block), own.op);
      if (p) out.push({ block: own.block, path: own.path, op: own.op, place: p });
    }
    return { writers: out, readers: ((r && r.results) || []).filter((x) => !(x.lines || []).some((l) => l.writes)) };
  }

  /**
   * Why a one-time change of a global signal may not stay, as one or two sentences of HTML with links to the code,
   * or "" when nothing in the program writes it. An unconditional "x := ..." is written each time its block runs;
   * a write inside IF/CASE only when the condition is true (owner-block writes count as "may", they are not checked).
   */
  async function whyNotStick(sym) {
    const { writers } = await writersOf(sym);
    if (!writers.length) return "";
    const link = (w) => placeLink(w.block, w.path, w.place, w.op);
    const always = writers.filter((w) => w.conditional === false), sometimes = writers.filter((w) => w.conditional !== false);
    const parts = [];
    if (always.length) parts.push(PK.t("{places} writes this every time it runs, so a change is overwritten at once.|{places} write this every time they run, so a change is overwritten at once.", { n: always.length, places: always.map(link).join(` ${PK.t("and")} `) }));
    if (sometimes.length) parts.push(PK.t("{places} sets this when its condition is true, so a change may not stay.|{places} set this when their conditions are true, so a change may not stay.", { n: sometimes.length, places: sometimes.map(link).join(` ${PK.t("and")} `) }));
    return parts.join(" ");
  }

  /**
   * For an input: the tags the program copies it into ("DB_IO".PB_Start := "DI_BluePB";) that other code writes
   * as well, after the copy (a machine simulator, for example). A held input may then change nothing.
   */
  async function copiesOverwritten(sym) {
    const { readers } = await writersOf(sym);
    const bare = String(sym).replace(/^"|"$/g, "");
    const re = new RegExp('^\\s*(.+?)\\s*:=\\s*"?' + reEsc(bare) + '"?\\s*;');
    const out = [];
    for (const r of readers) for (const l of r.lines || []) {
      const m = re.exec(l.text || "");
      if (!m || m[1].trim().startsWith("#")) continue;   // a copy into a block's own local is not shared
      const target = m[1].trim();
      const others = (await writersOf(target)).writers.filter((w) => w.block !== r.block);
      if (others.length) out.push({ target, from: r.block, writers: others,
        html: PK.t("The program copies it into {target} in {block}, and {places} also writes that tag, so the program may not see the held value.|The program copies it into {target} in {block}, and {places} also write that tag, so the program may not see the held value.",
          { n: others.length, target: `<b class="mono">${esc(target)}</b>`, block: esc(r.block), places: others.map((w) => placeLink(w.block, w.path, w.place, w.op)).join(` ${PK.t("and")} `) }) });
    }
    return out;
  }

  // The tag tables and the hardware, read once, for "an input from the field at %I0.3, on this module"
  let ioData = null;
  // A new program or a new hardware read: the addresses and channels may be different
  for (const ev of ["pk:program-changed", "pk:hardware-changed"]) window.addEventListener(ev, () => { ioData = null; });
  async function ioOf(sym) {
    ioData ||= Promise.all([PK.api("GET", "/api/program/tags").catch(() => null), PK.api("GET", "/api/hardware").catch(() => null)]);
    const [tags, hw] = await ioData;
    let tag = null;
    for (const tb of (tags && tags.tables) || []) for (const t of tb.tags) if (PK.norm(t.symbol) === PK.norm(sym)) tag = t;
    const addr = tag && String(tag.address || "").replace(/\s/g, "").toUpperCase();
    if (!addr || !/^%[IQ]/.test(addr)) return null;
    let ch = null;
    for (const st of (hw && hw.available && hw.stations) || []) for (const mo of st.modules || []) for (const c of mo.channels || []) {
      if (ch) break;
      const same = PK.norm(c.symbol) === PK.norm(sym) || (c.tag && PK.norm('"' + c.tag + '"') === PK.norm(sym)) || String(c.address || "").replace(/\s/g, "").toUpperCase() === addr;
      if (same) ch = { href: `#/plc/racks/${encodeURIComponent(st.name)}${mo.slot != null ? "/" + encodeURIComponent(mo.slot) : ""}`, where: `${st.name} · ${mo.title || mo.name || PK.t("slot {no}", { no: mo.slot })}`,
        station: st.name, slot: mo.slot, module: mo.title || mo.name || null, channel: c.n };
    }
    return { address: addr, input: addr.startsWith("%I"), ch, comment: tag.comment || null };
  }

  /**
   * Where an electrician measures a signal (review 41): its input or output address, rack, slot, module and channel.
   * A model signal is often a data block value the program copies from an input ("DB_IO".ZSO := "DI_ZSO";) or into an
   * output ("DQ_Y101" := "DB_IO".OpenCmd;): such a plain copy is followed one step. Scaled or combined values are not.
   * Resolves to { address, input, ch, comment, tag (the PLC tag), via (the block with the copy) } or null.
   */
  const measured = new Map();
  for (const ev of ["pk:program-changed", "pk:hardware-changed"]) window.addEventListener(ev, () => measured.clear());
  function whereToMeasure(sym) {
    const key = PK.norm(sym);
    if (!measured.has(key)) measured.set(key, findMeasure(sym).catch(() => null));
    return measured.get(key);
  }
  async function findMeasure(sym) {
    const direct = await ioOf(sym);
    if (direct) return { ...direct, tag: sym, via: null };
    let r = null;
    try { r = await PK.api("GET", "/api/program/search?q=" + encodeURIComponent(String(sym).replace(/^"([^"]+)"$/, "$1"))); } catch { return null; }
    const plain = /^"[^"]+"$|^[A-Za-z_]\w*$/;     // a whole PLC tag, nothing computed
    for (const x of (r && r.results) || []) for (const l of x.lines || []) {
      const m = /^\s*(.+?)\s*:=\s*(.+?)\s*;\s*(\/\/.*)?$/.exec(l.text || "");
      if (!m) continue;
      const [lhs, rhs] = [m[1].trim(), m[2].trim()];
      const other = PK.norm(lhs) === PK.norm(sym) ? rhs : PK.norm(rhs) === PK.norm(sym) ? lhs : null;
      if (!other || !plain.test(other) || other.startsWith("#")) continue;
      const io = await ioOf(other.startsWith('"') ? other : `"${other}"`);
      if (io) return { ...io, tag: other.replace(/^"|"$/g, ""), via: x.block };
    }
    return null;
  }

  /** What sets a global signal, as sheet paragraphs; { here } when the only writer is the block on screen. */
  async function globalAnswer(sym, onScreen) {
    const { writers, readers } = await writersOf(sym);
    if (writers.length === 1 && writers[0].block === onScreen && writers[0].place) return { here: writers[0].place, readers };
    const parts = [];
    if (writers.length) parts.push(`<p>${PK.t("Set in {places}.", { places: writers.map((w) => placeLink(w.block, w.path, w.place, w.op)).join(` ${PK.t("and in")} `) })}</p>`);
    const io = await ioOf(sym);
    if (io) {
      const hwLink = io.ch ? ` <a href="${esc(io.ch.href)}" data-close>${esc(io.ch.where)} ›</a>` : "";
      parts.push(io.input ? `<p>${PK.t("An input from the field, at {address}.", { address: `<span class="mono">${esc(io.address)}</span>` })}${hwLink || " " + PK.t("PLC-Ki does not know its module.")}</p>`
        : `<p>${PK.t("An output to the field, at {address}.", { address: `<span class="mono">${esc(io.address)}</span>` })}${hwLink}</p>`);
    }
    if (!parts.length) parts.push(`<p class="muted">${PK.t("Nothing in the program sets it. It comes from outside the program: an input, the HMI or another device.")}</p>`);
    const others = onScreen ? readers.filter((x) => x.block !== onScreen) : [];   // not inside an input's answer: it would only repeat the caller
    if (others.length) parts.push(`<p class="mt-3 small muted">${PK.t("Also read in {list}.", { list: others.slice(0, 8).map((x) => `<a href="${esc(hrefOf(x.block, ["as-text"]))}" data-close>${esc(x.block)}</a>`).join(", ") + (others.length > 8 ? " " + PK.t("and {count} more", { count: others.length - 8 }) : "") })}</p>`);
    return { parts, readers };
  }

  /**
   * Where an operand of the block on screen gets its value: { here } for a place in this block (the page scrolls to it),
   * or { html } for a sheet with the answer and links. ctx: { b, inst }.
   */
  async function sourceOf(op, ctx) {
    const { b, inst } = ctx;
    if (!op.startsWith("#")) {
      const a = await globalAnswer(op, b.name);
      return a.here ? { here: a.here } : { html: a.parts.join("") };
    }
    const name = op.slice(1), first = name.split(".")[0], rest = name.split(".").slice(1).join(".");
    const mem = (b.interface || []).find((x) => x.name === first);
    // A block inside this one: #LT101.High is set in FB_AnalogIn, for "Station".LT101
    const fb = mem && fbOf(mem.datatype);
    if (fb) {
      const path = inst ? inst.path + "." + first : null;
      if (!rest) return { html: `<p>${PK.t("{name} runs {block} inside this block.", { name: `<b>${esc(first)}</b>`, block: esc(fb) })} <a href="${esc(hrefOf(fb, path ? [path] : []))}" data-close>${PK.t("Open {block} for {name} ›", { block: esc(fb), name: esc(first) })}</a></p>` };
      const p = placeIn(await loadBlock(fb), "#" + rest);
      return { html: `<p>${PK.t("Set in {places}.", { places: placeLink(fb, path, p, "#" + rest) + (path ? ", " + PK.t("for {path}", { path: `<span class="mono">${esc(path)}</span>` }) : "") })}</p>` };
    }
    const here = placeIn(b, op);
    if (here) return { here };
    if (mem && /^(Input|InOut)$/.test(mem.section)) {
      const c = inst && inst.caller;
      if (!c) {
        const callers = (b.calledBy || []).map((n) => `<a href="${esc(hrefOf(n))}" data-close><b>${esc(n)}</b></a>`).join(", ");
        return { html: `<p>${PK.t("An input: the block that calls {name} gives it", { name: esc(b.name) })}${callers ? `. ${PK.t("Called by {list}", { list: callers })}` : ""}.</p>` };
      }
      const cl = callLine(await loadBlock(c.block), c.local, first);
      const w = inst.wires[first];
      const wire = w && typeof w === "object" ? w.text : w;
      const parts = [`<p>${PK.t("An input from {place}, where it calls {local}", { place: placeLink(c.block, c.path, cl ? { line: cl.line } : null), local: `<span class="mono">${esc(clean(c.local))}</span>` })}${wire ? ":" : "."}</p>`];
      if (wire) parts.push(`<pre class="code-line">${esc(first)} := ${esc(wire)}</pre>`);
      const g = typeof w === "string" ? w.replace(/^NOT\s+/, "") : null;
      if (g && g.startsWith('"')) {
        const a = await globalAnswer(g, null);
        parts.push(`<p class="mt-3 mb-1 small muted">${PK.t("And {name}:", { name: `<span class="mono">${esc(g)}</span>` })}</p>` + (a.here ? `<p>${PK.t("Set in {places}.", { places: placeLink(b.name, null, a.here, g) })}</p>` : a.parts.join("")));
      }
      return { html: parts.join("") };
    }
    return { html: `<p class="muted">${PK.t("PLC-Ki did not find where the program sets it.")} ${mem && mem.section === "Static" ? PK.t("It keeps its value from the last cycle, or the HMI sets it.") : PK.t("The HMI or another device may set it.")}</p>` };
  }

  /** Where an output of the block on screen is used: the caller's lines that read it, or the blocks that read a global signal. */
  async function usesOf(op, ctx) {
    const { b, inst } = ctx;
    const set = placeIn(b, op);
    const parts = [set ? `<p>${PK.t("Set in this block, {place}.", { place: esc(placeText(set)) })}</p>` : ""];
    if (op.startsWith("#") && inst && inst.caller) {
      const c = inst.caller, ref = c.local + "." + op.slice(1);
      const cb = await loadBlock(c.block);
      const lines = String((cb && (cb.source || cb.logic)) || "").split(/\r?\n/);
      const used = lines.map((l, i) => ({ l: l.trim(), i })).filter((x) => x.l.includes(ref) && !x.l.replace(/\/\/.*$/, "").trim().startsWith("//"));
      parts.push(used.length ? `<p>${PK.t("Read in {place}:", { place: placeLink(c.block, c.path, { line: used[0].i + 1 }) })}</p>` + used.slice(0, 6).map((x) => `<pre class="code-line">${esc(x.l)}</pre>`).join("")
        : `<p class="muted">${PK.t("{block} does not read it. The HMI or another device may.", { block: esc(c.block) })}</p>`);
    } else if (!op.startsWith("#")) {
      const { readers } = await writersOf(op);
      const links = [];
      for (const x of readers.slice(0, 8)) {
        const xb = await loadBlock(x.block);
        const i = String((xb && (xb.source || xb.logic)) || "").split(/\r?\n/).findIndex((l) => l.includes(op));
        links.push(placeLink(x.block, null, i >= 0 ? { line: i + 1 } : null));
      }
      parts.push(readers.length ? `<p>${PK.t("Read in {list}.", { list: links.join(", ") + (readers.length > 8 ? " " + PK.t("and {count} more", { count: readers.length - 8 }) : "") })}</p>`
        : `<p class="muted">${PK.t("No other block reads it. The HMI or another device may.")}</p>`);
    } else parts.push(`<p class="muted">${PK.t("An output of {name}: the block that calls it reads it.", { name: esc(b.name) })}</p>`);
    return parts.join("");
  }

  /**
   * One network of a block, drawn as on the Program page, for another page (the Active alarm page shows the rung that
   * sets the alarm). spec: { block, instance, output, network, want }
   * - instance: '"Station".XV101' (default: the block's first instance)
   * - output: the signal the network writes ("FaultNotOpen", "#FaultNotOpen" or '"DB".x'), else network: its number
   * - want: a Map, optional: filled with the symbols the model does not read (symbol -> type), to ask /api/watch for them
   * resolve, optional: operand -> { raw, text, symbol, label } or null, e.g. values at alarm time; default: live values.
   * Returns the HTML (contacts are not clickable there), or "" when the block or the network is not found.
   */
  async function rungFor(spec, resolve) {
    const { block: name, instance, output, network, want } = spec || {};
    if (!name || !state.program) return "";
    const b = await loadBlock(name);
    if (!b) return "";
    const insts = await instancesOf(name);
    const inst = insts.find((i) => i.path === instance) || insts[0] || null;
    const nets = b.logic ? networks(b.logic) : sclNetworks(b.source);
    const op = output ? (/^[#"]/.test(output) ? output : "#" + output) : null;
    const net = (op && nets.find((n) => n.lines.some((l) => writes(l, op)))) || (network != null && nets.find((n) => n.no === String(network)));
    if (!net) return "";
    plainDraw = true;
    const was = drawBlock;
    drawBlock = b;
    try { return netHtml(net, resolve || resolver(inst, b, want), op, null, null, []); }
    finally { plainDraw = false; drawBlock = was; }
  }
  window.PKProgram = { rungFor, writersOf, whyNotStick, copiesOverwritten, whereToMeasure };

  PK.view("program", program());
  PK.view("logic", alias("rungs"));
  PK.view("code", alias("text"));
})();
