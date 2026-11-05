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
    : `<span class="chip" title="${esc(v.symbol)}">${esc(v.text)}</span>`;

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
    return `<button type="button" class="op" data-src="${esc(op)}" title="What sets ${esc(clean(op))}?">${esc(op)}</button>`;
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
      const tip = v == null ? `${label}${sides ? shown : ""}: no live value (not read, or not found in the PLC)` : `${label}${shown}: ${v ? "passes" : "does not pass"}`;
      // A click asks what sets it; a comparison asks about its first signal
      const src = plainDraw ? null : n.t === "cmp" ? [n.a, n.b].find(isSignal) : n.op;
      const attrs = src ? `data-src="${esc(src)}" aria-label="${esc(tip)}. What sets ${esc(clean(src))}?"` : "";
      return { html: `<${src ? 'button type="button"' : "div"} class="contact ${n.t === "cmp" ? "cmp" : ""} ${v === true ? "on" : v === false ? "off" : "unk"} ${blocker && ctx.coilOff ? "blocker" : ""}" title="${esc(tip)}" ${attrs}>
          <span class="sym">${sym}${v == null ? `<span class="q" aria-hidden="true">?</span>` : ""}</span><span class="nm">${esc(label.replace(/^#/, ""))}</span></${src ? "button" : "div"}>`, out: power && v === true };
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

  function rung(lhs, expr, resolve, box, opt = {}) {
    const toks = tokenize(expr);
    const tree = toks && parseExpr(toks);
    if (!tree) return null;
    const coil = box ? null : resolve(lhs);
    const result = evalNode(tree, resolve);
    const coilOn = coil && typeof coil.raw === "boolean" ? coil.raw : result;
    // A fault or alarm output that is off is the healthy state: nothing on its rung is "stopping" it
    const faultCoil = !box && /fault|alarm|error|\berr|warn|notready|not_?ok|trip|dev$/i.test(lhs || "");
    const ctx = { blocked: false, coilOff: coilOn === false && !faultCoil };
    const body = drawNode(tree, resolve, true, ctx);
    // A plain ( ) output that is off, and the first contact that stops it: for "Why is it off" at the top
    if (opt.why && !box && !opt.coil && ctx.coilOff && ctx.first) opt.why.push({ coil: lhs, first: ctx.first, net: opt.net });
    const end = box
      ? `<div class="contact ${result ? "on" : result === false ? "off" : ""}"><span class="sym">${esc(box.kind)}</span><span class="nm">${esc(box.name)}${box.pt ? " · " + esc(box.pt) : ""}</span></div>`
      : plainDraw ? `<div class="contact coil ${coilOn === true ? "on" : coilOn === false ? "off" : ""}" title="${esc(clean(lhs))}${coil ? " = " + esc(coil.text) : ""}"><span class="sym">${opt.coil ? "(" + opt.coil + ")" : "( )"}</span><span class="nm">${esc(lhs.replace(/^#/, ""))}</span></div>`
      : `<button type="button" class="contact coil ${coilOn === true ? "on" : coilOn === false ? "off" : ""}" data-src="${esc(lhs)}" data-coil
          title="${esc(clean(lhs))}${coil ? " = " + esc(coil.text) : ""}" aria-label="Output ${esc(clean(lhs))}${coil ? ", " + esc(coil.text) : ""}. Where is it used?"><span class="sym">${opt.coil ? "(" + opt.coil + ")" : "( )"}</span><span class="nm">${esc(lhs.replace(/^#/, ""))}</span></button>`;
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

  function netHtml(net, resolve, highlight, curStep, wantNet, why) {
    const parts = [];
    let plain = [];
    let inCur = false;
    const graphLine = (l) => {
      const st = /^\s*Step\s+(\d+)\b/.exec(l);
      if (st) inCur = curStep != null && Number(st[1]) === curStep;
      else if (/^\s{0,2}\S/.test(l)) inCur = false;
      return `<span class="gl ${inCur ? "cur" : ""}">${st && inCur ? `<span class="now-tag">now</span>` : ""}${sclLine(l, resolve)}</span>`;
    };
    const flush = () => { if (plain.length) { parts.push(net.lang === "GRAPH" ? `<div class="stmt graph">${plain.map(graphLine).join("")}</div>` : `<div class="stmt">${plain.map((l) => sclLine(l, resolve)).join("<br>")}</div>`); plain = []; } };
    for (const line of net.lines) {
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
    return `<div class="rung-card ${hl ? "hl" : ""}" ${hl ? 'id="hl"' : ""} data-net="${esc(net.no)}">
      <div class="rung-title">${net.no ? "Network " + esc(net.no) : ""} ${net.lang ? `<span class="tag lang-${esc(net.lang)}">${esc(net.lang)}</span>` : ""}<span class="faint">${esc(net.title)}</span></div>
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
      return `<b>${esc(clean(n.a))} ${esc(n.op)} ${esc(clean(n.b))}</b> is not true${a ? ` (${esc(clean(n.a))} is ${esc(a.text)})` : ""}`;
    }
    const v = f.live;
    const txt = v && typeof v.raw !== "boolean" ? ` (${esc(v.text)})` : "";
    // An input of the block: say which signal is wired to it, so the operator knows what to check
    const wired = n.op.startsWith("#") && v && v.symbol && instPath && !v.symbol.startsWith(instPath) ? `, wired to <span class="mono">${esc(v.symbol)}</span>` : "";
    return `${named(n.op, resolve, v)} is ${n.neg ? "on" : "off"}${txt}${wired}`;
  }

  /** A signal by its plain name from the machine notes when there is one ("Heater E301 on"), with the program name after it. */
  function named(op, resolve, v) {
    v = v || resolve(op);
    const sym = PK.norm(v && v.symbol ? v.symbol : op);
    const t = ((state.model && state.model.tags) || []).find((x) => PK.norm(x.symbol) === sym);
    const label = t && t.label ? t.label : null;
    return label ? `<b>${esc(label)}</b> <span class="mono faint small">${esc(clean(op))}</span>` : `<b>${esc(clean(op))}</b>`;
  }

  function whyHtml(why, resolve, instPath) {
    if (!why.length) return "";
    const MAX = 6;
    return `<div class="why-off"><div class="why-h">${icon("info")}<b>Why ${why.length === 1 ? "an output is" : why.length + " outputs are"} off right now</b></div>
      <ul>${why.slice(0, MAX).map((w) => {
        // The next question, as in the design: "Why is that off?" goes to what sets the contact that stops the output
        const n = w.first.n, src = n.t === "cmp" ? [n.a, n.b].find(isSignal) : n.op;
        const next = src ? `<button type="button" class="linkbtn" data-src="${esc(src)}">${n.t === "cmp" ? "What sets " + esc(clean(src)) + "?" : `Why is it ${n.neg ? "on" : "off"}?`}</button> · ` : "";
        return `<li>${named(w.coil, resolve)} is off because ${because(w.first, resolve, instPath)}.
        ${next}${w.net ? `<button type="button" class="linkbtn" data-goto-net="${esc(w.net)}">Network ${esc(w.net)} ›</button>` : ""}</li>`;
      }).join("")}</ul>
      ${why.length > MAX ? `<p class="faint small" style="margin:6px 0 0">And ${why.length - MAX} more: look for the amber contacts in the rungs.</p>` : ""}</div>`;
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
  const ownState = (n) => n.active.length ? "alarm" : n.eqs.some((e) => e.blockedBy) ? "blocked" : n.seqs.some((q) => q.step != null) ? "run" : "";
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
  const DOT_TEXT = { alarm: "An alarm set here is on", blocked: "A device here is held back", run: "A sequence here is running" };
  const dot = (s) => s ? `<span class="pdot ${s}" title="${DOT_TEXT[s]}" aria-label="${DOT_TEXT[s]}"></span>` : "";
  const plainTitle = (b) => (b.title || "").trim();

  // ---- "What it does": plain words first ----
  function seqHtml(q, compact) {
    const time = q.timeInStep ? ` · ${esc(q.timeInStep)}` : "";
    const step = q.step != null ? `<span class="pill info">Step ${esc(String(q.step))}${q.stepName ? " · " + esc(q.stepName) : ""}${time}</span>` : `<span class="pill">Not in a step</span>`;
    const next = (q.next || []);
    const wait = next.length ? next.map((n, k) => `<p class="pg-wait">${k ? "Or, to" : "To"} go on to step ${esc(String(n.to))}${n.toName ? ` “${esc(n.toName)}”` : ""} it waits for:</p>
        <ul class="conds">${n.conditions.map(PK.cond).join("")}</ul>`).join("") : "";
    const halted = q.halted ? `<div class="notice red" style="margin-top:10px">${icon("warn")}<div><b>The sequence is halted.</b>${q.haltReasons && q.haltReasons.length ? " " + q.haltReasons.map(esc).join(" ") : ""}</div></div>` : "";
    return `<div class="pg-seq">
      <div class="row" style="gap:8px 10px;flex-wrap:wrap"><b>${esc(q.name)}</b>${step}${q.halted ? `<span class="pill bad">Halted</span>` : ""}</div>
      ${q.does ? `<p class="pg-does">${esc(q.does)}</p>` : ""}${halted}${compact ? "" : wait}
      ${compact ? "" : `<a class="small" href="#/sequence/${encodeURIComponent(q.id)}">Step chart and timeline ›</a>`}</div>`;
  }
  function eqRow(e) {
    return `<li><span class="ico">${icon(PK.kindIcon(e.kind))}</span><div style="flex:1;min-width:0">
        <a href="#/plant/devices/${encodeURIComponent(e.id)}"><b>${esc(e.name)}</b></a> <span class="faint small mono">${esc(e.id)}</span>
        ${e.blockedBy ? `<div class="pg-blocked">${esc(e.blockedBy.charAt(0).toUpperCase() + e.blockedBy.slice(1))}</div>` : ""}</div>
      <span class="lv lv-${esc(e.level)}">${esc(e.state)}</span></li>`;
  }
  const alarmRow = (a, on) => `<li><span class="pg-sev ${on ? "on" : ""}">${esc(a.id)}</span><div style="flex:1;min-width:0">
      <a href="#/alarms/${encodeURIComponent(a.id)}">${esc(a.text)}</a>${on && a.condition ? `<div class="faint small">${esc(a.condition)}</div>` : ""}</div>
      ${on ? `<span class="pill bad">On now</span>` : ""}</li>`;

  function whatItDoes(b, n, why, resolve, inst, byName) {
    const parts = [];
    for (const q of n.seqs) parts.push(`<div class="pg-sec"><h3>Sequence in this block</h3>${seqHtml(q)}</div>`);
    if (n.eqs.length) parts.push(`<div class="pg-sec"><h3>Devices it controls</h3><ul class="pg-rows">${n.eqs.map(eqRow).join("")}</ul></div>`);
    if (why.length) parts.push(`<div class="pg-sec">${whyHtml(why, resolve, inst && inst.path)}</div>`);
    if (n.alarms.length || n.active.length) {
      const on = new Set(n.active.map((a) => a.id));
      const off = n.alarms.filter((a) => !on.has(a.id));
      parts.push(`<div class="pg-sec"><h3>Alarms it sets</h3>
        ${n.active.length ? `<ul class="pg-rows">${n.active.map((a) => alarmRow(a, true)).join("")}</ul>` : `<p class="muted small" style="margin:0 0 6px">None of them is on now.</p>`}
        ${off.length ? `<details class="more pg-more"><summary>${n.active.length ? "The other " : off.length > 1 ? "All " : "The "}${off.length} alarm${off.length > 1 ? "s" : ""} it can set</summary><ul class="pg-rows">${off.map((a) => alarmRow(a, false)).join("")}</ul></details>` : ""}</div>`);
    }
    if (!parts.length) {
      const path = callPath(byName, b.name);
      const parent = path && path.length > 1 ? path[path.length - 2] : null;
      parts.push(`<div class="pg-sec"><p class="muted" style="margin:0">PLC-Ki's notes on the machine don't mention this block: it runs no sequence, controls no known device and sets no known alarm${parent ? `. It is part of <a href="${hrefOf(parent)}">${esc(parent)}</a>` : ""}. The code below shows what it does.</p>
        <button type="button" class="btn b small" style="margin-top:10px" data-ask="Explain in plain words what PLC block ${esc(b.name)} does, and what it is doing right now.">Explain this block in plain words</button></div>`);
    }
    return parts.join("");
  }

  const hrefOf = (name, extra = []) => "#/program/" + [name, ...extra].map(encodeURIComponent).join("/");

  // ---- "Show where it is set": where an alarm bit is latched and where its condition is made ----
  /** "network 4" (rungs) or "line 57" (SCL text): where a block writes a signal, or null. */
  function whereWritten(b, sym) {
    if (!b || !sym) return null;
    if (b.logic) { const n = networks(b.logic).find((x) => x.lines.some((l) => writes(l, sym))); return n && n.no ? "network " + n.no : null; }
    const i = String(b.source || "").split(/\r?\n/).findIndex((l) => writes(l, sym));
    return i >= 0 ? "line " + (i + 1) : null;
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
    if (same) parts.push("Set in " + place(c.block, t.latch[0].at || c.at, c.href));
    else {
      if (t.latch.length) parts.push("Latched in " + t.latch.map((l) => place(l.block, l.at, latchHref(l.block))).join(" and "));
      if (c.block && c.symbol) parts.push((t.latch.length ? "caused in " : "Caused in ") + place(c.block, c.at, c.href) + ` (output ${esc(c.symbol)})`);
      else if (c.block) {
        // No output in the notes: say what they say ("FB_Station, alarm input EStop (...)")
        const rest = t.location.split(/;|\blatched in\b/i)[0].replace(new RegExp("^\\s*(?:(?:FB|FC|OB)\\s+)?\"?" + reEsc(c.block) + "\"?\\s*,?\\s*"), "").trim();
        parts.push((t.latch.length ? "comes from " : "Comes from ") + place(c.block, null, c.href) + (rest ? ": " + esc(rest) : ""));
      }
    }
    if (!parts.length) return "";
    const missing = t.symbol && !t.latch.length ? ` <span class="faint">PLC-Ki did not find the block that writes <span class="mono">${esc(t.symbol)}</span>.</span>` : "";
    return `<div class="notice alarm-trail" style="margin-bottom:12px">${icon("info")}<div><a href="#/alarms/${encodeURIComponent(t.id)}"><b>${esc(t.id)}</b></a> · ${parts.join(" · ")}.${missing}</div></div>`;
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
        html = hits.length ? hits.map((b) => item(b)).join("") : `<p class="faint small" style="padding:8px 10px">No block matches “${esc(blockQuery)}”.</p>`;
      } else {
        const curDb = cur && tree.dbs.some((b) => b.name === cur), curLoose = cur && tree.loose.some((b) => b.name === cur);
        html = `<a href="#/program" class="pl-home ${sel && sel.overview ? "on" : ""}">${icon("info")}<span>Overview: what runs now</span></a>`
          + `<div class="grp">In the order the PLC runs them</div>` + tree.rows.map((r) => item(r.b, r.depth)).join("")
          + (tree.loose.length ? `<details class="pl-fold" ${curLoose ? "open" : ""}><summary>Not called from an OB (${tree.loose.length})</summary>${tree.loose.map((b) => item(b)).join("")}</details>` : "")
          + (tree.dbs.length ? `<details class="pl-fold" ${curDb ? "open" : ""}><summary>Data blocks (${tree.dbs.length})</summary>${tree.dbs.map((b) => item(b)).join("")}</details>` : "");
      }
      return `<input type="search" id="blk-q" class="blk-q" placeholder="Find a block" aria-label="Find a block" value="${esc(blockQuery)}">` + html;
    }

    /** The overview: what runs now and where it lives, then what each part of the program is for. */
    function overview(tree, idx, roll) {
      const st = state.status || {};
      const seqs = st.sequences || [], alarms = st.activeAlarms || [], blocked = (st.equipment || []).filter((e) => e.blockedBy);
      const where = (loc) => { const b = PK.blockOf(loc); return b && tree.byName.has(b) ? `<a class="pg-where" href="${hrefOf(b)}">${esc(b)} ›</a>` : ""; };
      const now = [];
      if (alarms.length) now.push(`<div class="pg-sec"><h3>Alarms on now</h3><ul class="pg-rows">${alarms.map((a) => `<li><span class="pg-sev on">${esc(a.id)}</span>
          <div style="flex:1;min-width:0"><a href="#/alarms/${encodeURIComponent(a.id)}">${esc(a.text)}</a></div>${where(a.location)}</li>`).join("")}</ul></div>`);
      if (blocked.length) now.push(`<div class="pg-sec"><h3>Held back</h3><ul class="pg-rows">${blocked.map((e) => `<li><span class="ico">${icon(PK.kindIcon(e.kind))}</span>
          <div style="flex:1;min-width:0"><b>${esc(e.name)}</b><div class="pg-blocked">${esc(e.blockedBy.charAt(0).toUpperCase() + e.blockedBy.slice(1))}</div></div>${where(e.location)}</li>`).join("")}</ul></div>`);
      if (seqs.length) now.push(`<div class="pg-sec"><h3>Sequences</h3>${seqs.map((q) => `<div class="pg-seqrow">${seqHtml(q, false).replace('<div class="pg-seq">', `<div class="pg-seq">${where(q.block)}`)}</div>`).join("")}</div>`);
      const connected = st.connection && st.connection.state === "connected";
      const right = `<div class="card pg-card"><div class="row" style="align-items:baseline"><h2 class="pg-h">Right now</h2><span class="spacer"></span>
          ${connected ? `<span class="pill ok"><span class="dot"></span>Live</span>` : st.connection ? `<span class="pill">${esc(st.connection.state === "snapshot" ? "Test values" : "Not live")}</span>` : ""}</div>
        ${!alarms.length && !blocked.length ? `<div class="notice green" style="margin:10px 0 4px">${icon("check")}<div>No alarm is on and nothing the program asks for is held back.</div></div>` : ""}
        ${now.join("") || (!seqs.length ? `<p class="muted">The machine notes have no sequences to follow.</p>` : "")}</div>`;

      // What each part is for: the units Main calls (one per machine section, as a rule), with everything inside them
      // rolled up, so a library helper deep down does not get a card of its own
      const units = tree.rows.filter((r) => r.depth === 1).map((r) => r.b);
      const parts = (units.length ? units : tree.rows.map((r) => r.b).filter((b) => b.kind !== "OB"))
        .map((b) => ({ b, n: subtree(tree.byName, idx, b.name), size: descendants(tree.byName, b.name) }));
      const MAX = 12;
      const shown = allParts ? parts : parts.slice(0, MAX);
      const card = ({ b, n, size }) => {
        const chips = [];
        for (const q of n.seqs) chips.push(`<span class="pill info">${q.step != null ? `Step ${esc(String(q.step))}${q.stepName ? " · " + esc(q.stepName) : ""}` : "Sequence"}</span>`);
        if (n.eqs.length) chips.push(`<span class="pill ${n.eqs.some((e) => e.blockedBy) ? "warn" : ""}">${n.eqs.length === 1 ? esc(n.eqs[0].name) : n.eqs.length + " devices"}${n.eqs.some((e) => e.blockedBy) ? " · held back" : ""}</span>`);
        if (n.alarms.length || n.active.length) chips.push(`<span class="pill ${n.active.length ? "bad" : ""}">${n.active.length ? n.active.length + " alarm" + (n.active.length > 1 ? "s" : "") + " on" : n.alarms.length + " alarm" + (n.alarms.length > 1 ? "s" : "")}</span>`);
        const lang = (b.languages && b.languages[0]) || b.language || "";
        return `<a class="pg-part" href="${hrefOf(b.name)}"><div class="pg-part-top"><span class="pl-n">${esc(b.name)}</span>${lang ? `<span class="tag lang-${esc(lang)}">${esc(lang)}</span>` : ""}${dot(roll(b.name))}</div>
          <div class="pg-part-t">${esc(plainTitle(b) || b.name)}</div>${b.comment ? `<div class="pg-part-c">${esc(b.comment)}</div>` : ""}
          ${size ? `<div class="pg-part-c">${size} block${size > 1 ? "s" : ""} inside</div>` : ""}
          ${chips.length ? `<div class="row" style="gap:6px;flex-wrap:wrap;margin-top:auto;padding-top:8px">${chips.join("")}</div>` : ""}</a>`;
      };
      const partsHtml = parts.length ? `<div class="card pg-card"><h2 class="pg-h">The parts of the program</h2>
          <p class="muted small" style="margin:4px 0 14px">What Main calls, in the order the PLC runs them, with the sequences, devices and alarms inside each. Open one to see it explained, with the code and live values.</p>
          <div class="pg-parts">${shown.map(card).join("")}</div>
          ${parts.length > MAX ? `<button type="button" class="btn b small" style="margin-top:14px" data-all-parts>${allParts ? "Show fewer" : `Show all ${parts.length}`}</button>` : ""}</div>` : "";
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
          if (state.programError) return PK.hero({ title: "No program loaded yet.",
            sub: "This page explains the PLC program in plain words, with live values, once PLC-Ki has read it from a TIA archive. " + (state.user && state.user.role === "admin"
              ? "Import it under Setup, Program: it takes a few minutes." : "An administrator imports it under Setup, Program."),
            extra: `<p class="faint small" style="margin-top:14px">${esc(state.programError)}</p>`,
            actions: state.user && state.user.role === "admin" ? `<a class="btn" href="#/setup/program">Import the program</a>` : "" });
          return PK.hero({ title: "Loading the program…" });
        }
        if (params.join("/") !== (sel ? sel.params.join("/") : "") && loading == null) select(params);
        const p = state.program;
        const tree = callTree();
        const idx = notesIndex();
        const roll = rollup(tree.byName, idx);
        const head = PK.sec("hero compact", `<div class="eyebrow">${esc(p.project)} · ${esc(p.plc)}${p.cpu ? " · " + esc(p.cpu) : ""}</div>
          <h1>Program</h1>
          <p class="sub">What the PLC program does and why, live. Each part is explained in plain words first; the code with live values is underneath.</p>`);
        const picked = sel && !sel.overview;
        const body = (inner) => head + PK.sec("alt", `<div class="split ${picked ? "has-sel" : ""}"><nav class="blocklist" aria-label="Program blocks">${list(tree, roll)}</nav><div class="stack">${inner}</div></div>`);
        if (!sel || sel.overview || sel.error) watchList = [];
        if (!sel) return body(`<div class="card"><p class="muted">Loading…</p></div>`);
        if (sel.overview) return body(overview(tree, idx, roll));
        const crumbs = (name) => {
          const path = callPath(tree.byName, name) || [name];
          return `<nav class="crumbs" aria-label="Where this block sits"><a href="#/program">Program</a>${path.map((n, k) => ` <i>›</i> ${k === path.length - 1 ? `<b>${esc(n)}</b>` : `<a href="${hrefOf(n)}">${esc(n)}</a>`}`).join("")}</nav>`;
        };
        if (sel.error) return body(crumbs(sel.name) + `<div class="notice red">${icon("warn")}<div>${esc(sel.error)}</div></div>`);
        const b = sel.block;
        const asked = new Map();
        const resolve = resolver(sel.inst, b, asked);
        const eqName = (i) => { const e = ((state.status && state.status.equipment) || []).find((x) => x.location && x.location.includes(i.path)); return e ? " · " + e.name : ""; };
        const instPick = sel.instances.length > 1 ? `<label class="pg-inst"><span class="faint small">Which one</span><select id="inst" aria-label="Instance">${sel.instances.map((i) => `<option value="${esc(i.path)}" ${i === sel.inst ? "selected" : ""}>${esc(i.path + eqName(i))}</option>`).join("")}</select></label>`
          : sel.inst ? `<span class="pill info" title="Instance: where its values live">${esc(sel.inst.path)}</span>` : "";
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
        const switcher = canRungs && canText ? `<div class="segmented" role="group" aria-label="Show the code as">
            <button type="button" data-mode="rungs" class="${mode === "rungs" ? "on" : ""}" aria-pressed="${mode === "rungs"}">Rungs</button>
            <button type="button" data-mode="text" class="${mode === "text" ? "on" : ""}" aria-pressed="${mode === "text"}">Text</button></div>` : "";

        let bodyHtml;
        if (mode === "rungs" && canRungs) {
          const legend = `<div class="legend rung-legend"><span><i class="lg on"></i>Passes power</span><span><i class="lg off"></i>Does not pass</span>
            <span><i class="lg unk"></i>No live value</span><span><i class="lg blocker"></i>First one that stops the output</span></div>`;
          // From an alarm, its own line says where it is set instead
          const where = trail || (sel.highlight && sel.highlight.startsWith('"')
            ? (hl ? `<div class="notice" style="margin-bottom:12px">${icon("info")}<div>The highlighted rungs write <b>${esc(sel.highlight)}</b>: (S) sets it, (R) resets it, ( ) follows the rung. The values are live; amber marks the first contact that stops a rung.</div></div>`
              : `<div class="notice gold" style="margin-bottom:12px">${icon("info")}<div>${esc(sel.highlight)} is not written in this block${sel.network ? `; network ${esc(sel.network)} is highlighted` : ""}.</div></div>`) : "");
          bodyHtml = where + legend + netsHtml
            + (b.source && !b.logic ? `<p class="faint small" style="margin:12px 0 0">Only the on/off statements of this SCL block are drawn as rungs. <button type="button" class="linkbtn" data-mode="text">Show all of it as text</button>.</p>` : "");
        } else if (canText) {
          const hits = find ? String(text).split(/\r?\n/).filter((l) => l.toLowerCase().includes(find.toLowerCase())).length : 0;
          bodyHtml = trail + `<div class="findbar"><input type="search" id="code-find" placeholder="Find in this block" aria-label="Find in this block" value="${esc(find)}">
              <span class="faint small" aria-live="polite">${find ? (hits ? `${hits} line${hits > 1 ? "s" : ""} · Enter for the next` : "Not in this block") : ""}</span></div>` + codeHtml(text, resolve, want, find, sel.line)
            + `<p class="faint small" style="margin:10px 0 0">Click a signal to see what sets it.</p>`;
        } else bodyHtml = `<p class="muted">No code in this block (data only). Its fields are listed below.</p>`;

        const iface = (b.interface || []).filter((m) => m.section !== "Temp" && m.section !== "Constant");
        const isDb = /DB$/.test(b.kind);
        const title = plainTitle(b);
        const tagsHtml = window.PKTags ? window.PKTags.blockTagsHtml(text).replace(/<details([^>]*) open>/, "<details$1>") : "";
        const page = body(crumbs(b.name) + `
            <div class="card pg-card">
              <div class="row" style="gap:10px 12px;flex-wrap:wrap;align-items:center"><h2 class="pg-title">${esc(title || b.name)}${dot(ownState(n))}</h2><span class="spacer"></span>${instPick}</div>
              <div class="pg-meta"><span class="mono">${esc(b.name)}</span><span class="tag">${esc(b.kind)}${b.number != null ? " " + b.number : ""}</span>${lang ? `<span class="tag lang-${esc(lang)}">${esc(lang)}</span>` : ""}
                ${calledBy ? `<span>Called by ${calledBy}</span>` : ""}${calls ? `<span>Calls ${calls}</span>` : ""}
                ${b.instanceOf ? `<span>Values of <a href="${hrefOf(b.instanceOf)}">${esc(b.instanceOf)}</a></span>` : ""}</div>
              ${b.comment ? `<p class="pg-lede">${esc(b.comment)}</p>` : ""}
              ${isDb ? "" : whatItDoes(b, n, why, resolve, sel.inst, tree.byName)}
            </div>
            <div class="card pg-card" id="pg-code">
              <div class="row" style="gap:8px 14px;flex-wrap:wrap;align-items:center"><h2 class="pg-h">${isDb ? "Its values" : "The code"}</h2>${switcher}<span class="faint small">${!switcher ? "" : mode === "rungs" ? "Each network as a rung: green passes power, amber is what stops the output." : "The code as written in TIA, with the live value after each signal."}</span></div>
              <div style="margin-top:14px">${bodyHtml}</div>
            </div>
            ${tagsHtml}
            ${iface.length ? `<div class="card"><details class="more" style="border:0;margin:0;padding:0" ${!text ? "open" : ""}><summary>Inputs, outputs and stored values (${iface.length})</summary>
              <table class="tbl iface" style="margin-top:8px"><tr><th>Name</th><th>Section</th><th>Type</th><th>Now</th><th>Comment</th></tr>
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
        page.querySelectorAll("[data-goto-net]").forEach((b) => b.onclick = () => {
          const net = b.dataset.gotoNet;
          if (sel.mode !== "rungs") setMode("rungs");
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
      },
    };

    /** A contact, coil or signal was clicked: go to where this block sets it, or say in a sheet where it comes from. */
    async function explain(el, page) {
      if (!sel || !sel.block || el.dataset.busy) return;
      const op = el.dataset.src, coil = el.hasAttribute("data-coil"), ctx = { b: sel.block, inst: sel.inst };
      el.dataset.busy = "1";
      let r;
      try { r = coil ? { html: await usesOf(op, ctx) } : await sourceOf(op, ctx); }
      catch (e) { r = { html: `<p class="muted">${esc(e.message)}</p>` }; }
      delete el.dataset.busy;
      if (r.here) { showHere(page, r.here); return; }
      const v = resolver(sel.inst, sel.block)(op);
      PK.sheet(`<span class="eyebrow">${coil ? "Where it is used" : "What sets it"}</span>
        <h2 class="mono" style="font-size:20px;word-break:break-all">${esc(clean(op))}${v ? " " + chip(v) : ""}</h2>
        <div class="src-answer">${r.html}</div><div class="row end"><button class="btn" data-close>OK</button></div>`);
    }

    /** Scroll to a place in this block: its rung, or its line in the text (switching to the text when it has no rung). */
    function showHere(page, here) {
      const n = sel.mode === "rungs" && (sel.nets || []).find((x) => x.lines.some(here.hit));
      if (n) { flash(page.querySelector(`.rung-card[data-net="${CSS.escape(n.no)}"]`)); return; }
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
  const placeText = (p) => (p.net ? "network " + p.net : "line " + p.line);
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
    if (always.length) parts.push(`${always.map(link).join(" and ")} ${always.length > 1 ? "write" : "writes"} this every time ${always.length > 1 ? "they run" : "it runs"}, so a change is overwritten at once.`);
    if (sometimes.length) parts.push(`${sometimes.map(link).join(" and ")} ${sometimes.length > 1 ? "set" : "sets"} this when ${sometimes.length > 1 ? "their conditions are" : "its condition is"} true, so a change may not stay.`);
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
        html: `The program copies it into <b class="mono">${esc(target)}</b> in ${esc(r.block)}, and ${others.map((w) => placeLink(w.block, w.path, w.place, w.op)).join(" and ")} also ${others.length > 1 ? "write" : "writes"} that tag, so the program may not see the held value.` });
    }
    return out;
  }

  // The tag tables and the hardware, read once, for "an input from the field at %I0.3, on this module"
  let ioData = null;
  window.addEventListener("pk:program-changed", () => { ioData = null; });
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
      if (same) ch = { href: `#/hardware/${encodeURIComponent(st.name)}${mo.slot != null ? "/" + encodeURIComponent(mo.slot) : ""}`, where: `${st.name} · ${mo.title || mo.name || "slot " + mo.slot}` };
    }
    return { address: addr, input: addr.startsWith("%I"), ch };
  }

  /** What sets a global signal, as sheet paragraphs; { here } when the only writer is the block on screen. */
  async function globalAnswer(sym, onScreen) {
    const { writers, readers } = await writersOf(sym);
    if (writers.length === 1 && writers[0].block === onScreen && writers[0].place) return { here: writers[0].place, readers };
    const parts = [];
    if (writers.length) parts.push(`<p>Set in ${writers.map((w) => placeLink(w.block, w.path, w.place, w.op)).join(" and in ")}.</p>`);
    const io = await ioOf(sym);
    if (io) {
      const hwLink = io.ch ? ` <a href="${esc(io.ch.href)}" data-close>${esc(io.ch.where)} ›</a>` : "";
      parts.push(io.input ? `<p>An input from the field, at <span class="mono">${esc(io.address)}</span>.${hwLink || " PLC-Ki does not know its module."}</p>`
        : `<p>An output to the field, at <span class="mono">${esc(io.address)}</span>.${hwLink}</p>`);
    }
    if (!parts.length) parts.push(`<p class="muted">Nothing in the program sets it. It comes from outside the program: an input, the HMI or another device.</p>`);
    const others = onScreen ? readers.filter((x) => x.block !== onScreen) : [];   // not inside an input's answer: it would only repeat the caller
    if (others.length) parts.push(`<p class="small muted" style="margin-top:12px">Also read in ${others.slice(0, 8).map((x) => `<a href="${esc(hrefOf(x.block, ["as-text"]))}" data-close>${esc(x.block)}</a>`).join(", ")}${others.length > 8 ? ` and ${others.length - 8} more` : ""}.</p>`);
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
      if (!rest) return { html: `<p><b>${esc(first)}</b> runs ${esc(fb)} inside this block. <a href="${esc(hrefOf(fb, path ? [path] : []))}" data-close>Open ${esc(fb)} for ${esc(first)} ›</a></p>` };
      const p = placeIn(await loadBlock(fb), "#" + rest);
      return { html: `<p>Set in ${placeLink(fb, path, p, "#" + rest)}${path ? `, for <span class="mono">${esc(path)}</span>` : ""}.</p>` };
    }
    const here = placeIn(b, op);
    if (here) return { here };
    if (mem && /^(Input|InOut)$/.test(mem.section)) {
      const c = inst && inst.caller;
      if (!c) {
        const callers = (b.calledBy || []).map((n) => `<a href="${esc(hrefOf(n))}" data-close><b>${esc(n)}</b></a>`).join(", ");
        return { html: `<p>An input: the block that calls ${esc(b.name)} gives it${callers ? `. Called by ${callers}` : ""}.</p>` };
      }
      const cl = callLine(await loadBlock(c.block), c.local, first);
      const w = inst.wires[first];
      const wire = w && typeof w === "object" ? w.text : w;
      const parts = [`<p>An input from ${placeLink(c.block, c.path, cl ? { line: cl.line } : null)}, where it calls <span class="mono">${esc(clean(c.local))}</span>${wire ? ":" : "."}</p>`];
      if (wire) parts.push(`<pre class="code-line">${esc(first)} := ${esc(wire)}</pre>`);
      const g = typeof w === "string" ? w.replace(/^NOT\s+/, "") : null;
      if (g && g.startsWith('"')) {
        const a = await globalAnswer(g, null);
        parts.push(`<p class="small muted" style="margin:12px 0 4px">And <span class="mono">${esc(g)}</span>:</p>` + (a.here ? `<p>Set in ${placeLink(b.name, null, a.here, g)}.</p>` : a.parts.join("")));
      }
      return { html: parts.join("") };
    }
    return { html: `<p class="muted">PLC-Ki did not find where the program sets it. ${mem && mem.section === "Static" ? "It keeps its value from the last cycle, or the HMI sets it." : "The HMI or another device may set it."}</p>` };
  }

  /** Where an output of the block on screen is used: the caller's lines that read it, or the blocks that read a global signal. */
  async function usesOf(op, ctx) {
    const { b, inst } = ctx;
    const set = placeIn(b, op);
    const parts = [set ? `<p>Set in this block, ${esc(placeText(set))}.</p>` : ""];
    if (op.startsWith("#") && inst && inst.caller) {
      const c = inst.caller, ref = c.local + "." + op.slice(1);
      const cb = await loadBlock(c.block);
      const lines = String((cb && (cb.source || cb.logic)) || "").split(/\r?\n/);
      const used = lines.map((l, i) => ({ l: l.trim(), i })).filter((x) => x.l.includes(ref) && !x.l.replace(/\/\/.*$/, "").trim().startsWith("//"));
      parts.push(used.length ? `<p>Read in ${placeLink(c.block, c.path, { line: used[0].i + 1 })}:</p>` + used.slice(0, 6).map((x) => `<pre class="code-line">${esc(x.l)}</pre>`).join("")
        : `<p class="muted">${esc(c.block)} does not read it. The HMI or another device may.</p>`);
    } else if (!op.startsWith("#")) {
      const { readers } = await writersOf(op);
      const links = [];
      for (const x of readers.slice(0, 8)) {
        const xb = await loadBlock(x.block);
        const i = String((xb && (xb.source || xb.logic)) || "").split(/\r?\n/).findIndex((l) => l.includes(op));
        links.push(placeLink(x.block, null, i >= 0 ? { line: i + 1 } : null));
      }
      parts.push(readers.length ? `<p>Read in ${links.join(", ")}${readers.length > 8 ? ` and ${readers.length - 8} more` : ""}.</p>`
        : `<p class="muted">No other block reads it. The HMI or another device may.</p>`);
    } else parts.push(`<p class="muted">An output of ${esc(b.name)}: the block that calls it reads it.</p>`);
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
    try { return netHtml(net, resolve || resolver(inst, b, want), op, null, null, []); }
    finally { plainDraw = false; }
  }
  window.PKProgram = { rungFor, writersOf, whyNotStick, copiesOverwritten };

  PK.view("program", program());
  PK.view("logic", alias("rungs"));
  PK.view("code", alias("text"));
})();
