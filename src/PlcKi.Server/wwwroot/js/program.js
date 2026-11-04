// Code and Logic views: the program from the import (block list, SCL source, decoded LAD/FBD/GRAPH networks)
// with live values on top. Values are found by symbol: a block's "#member" becomes "<instance>.member", and
// for multi-instances the call in the parent block tells which global signal is wired to each input.
"use strict";

(() => {
  const { esc, icon, state } = PK;
  const blockCache = {};        // name -> block details
  const KW = new Set(("IF THEN ELSE ELSIF END_IF CASE OF END_CASE FOR TO BY DO END_FOR WHILE END_WHILE REPEAT UNTIL END_REPEAT " +
    "RETURN EXIT CONTINUE AND OR XOR NOT MOD TRUE FALSE REGION END_REGION VAR END_VAR VAR_INPUT VAR_OUTPUT VAR_IN_OUT VAR_TEMP " +
    "VAR_STAT CONSTANT FUNCTION_BLOCK FUNCTION END_FUNCTION_BLOCK END_FUNCTION ORGANIZATION_BLOCK END_ORGANIZATION_BLOCK BEGIN " +
    "DATA_BLOCK END_DATA_BLOCK STRUCT END_STRUCT TON TOF TP R_TRIG F_TRIG").split(" "));

  async function block(name) {
    if (!blockCache[name]) blockCache[name] = await PK.api("GET", "/api/program/blocks/" + encodeURIComponent(name));
    return blockCache[name];
  }

  // ---- instances: where a block's "#x" lives in the PLC ----
  /** All instance paths of a block, each with how its inputs are wired: [{ path, wires: {member -> symbol} }] */
  async function instancesOf(name, depth = 0) {
    const p = state.program;
    if (!p || depth > 6) return [];
    const out = [];
    for (const db of p.blocks.filter((b) => b.instanceOf === name)) out.push({ path: `"${db.name}"`, wires: {} });
    for (const parent of p.blocks) {
      for (const c of parent.calls || []) {
        if (c.block !== name || !c.instance) continue;
        if (c.instance.startsWith('"')) { if (!out.some((o) => o.path === c.instance)) out.push({ path: c.instance, wires: await wiresIn(parent.name, c.instance, null) }); continue; }
        const local = c.instance.replace(/^#/, "");
        for (const pi of await instancesOf(parent.name, depth + 1)) {
          out.push({ path: pi.path + "." + local, wires: await wiresIn(parent.name, c.instance, pi) });
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

  function resolver(inst) {
    const vals = PK.valueBySymbol();
    return (operand) => {
      let sym = operand;
      let negate = false;
      if (operand.startsWith("#")) {
        const name = operand.slice(1);
        const first = name.split(".")[0];
        if (inst && inst.wires[first] && typeof inst.wires[first] === "object" && name === first) {
          const w = inst.wires[first];
          const toks = tokenize(w.expr), tree = toks && parseExpr(toks);
          const v = tree ? evalNode(tree, resolver(null)) : null;
          return v == null ? null : { raw: v, text: v ? "TRUE" : "FALSE", symbol: w.text, label: w.text };
        }
        if (inst && inst.wires[first] && name === first) {
          sym = inst.wires[first];
          if (/^NOT\s+/.test(sym)) { negate = true; sym = sym.replace(/^NOT\s+/, ""); }
        } else if (inst) sym = inst.path + "." + name;
        else return null;
      }
      const t = vals.get(PK.norm(sym));
      if (!t) return null;
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
      if (m[1]) out += (m[1].startsWith('"') ? `<span class="op" data-used="${esc(m[1])}" title="Where is this used?">${esc(m[1])}</span>` : esc(m[1])) + chip(resolve(m[1]));
      else if (m[2]) out += `<span class="str">${esc(m[2])}</span>`;
      else if (m[3]) out += `<span class="num">${esc(m[3])}</span>`;
      else out += KW.has(m[4].toUpperCase()) ? `<span class="kw">${esc(m[4])}</span>` : esc(m[4]);
      last = re.lastIndex;
    }
    out += esc(rest.slice(last));
    if (comment) out += `<span class="cm">${esc(comment)}</span>`;
    return out;
  }

  function codeHtml(text, resolve, highlight, find) {
    const hl = highlight && !highlight.startsWith('"') ? new RegExp("#" + highlight.replace(/[.*+?^${}()|[\]\\]/g, "\\$&") + "\\b") : null;
    const f = (find || "").toLowerCase();
    let n = 0;
    return `<div class="code">${String(text).split(/\r?\n/).map((l) => {
      const hit = f && l.toLowerCase().includes(f);
      return `<span class="ln ${(hl && hl.test(l)) || writes(l, highlight) ? "hl" : ""} ${hit ? "found" : ""}" ${hit ? `data-hit="${n++}"` : ""}>${sclLine(l, resolve) || " "}</span>`;
    }).join("")}</div>`;
  }

  // ---- LAD-style rungs from boolean statements ----
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
      if (blocker) ctx.blocked = true;
      const tip = v == null ? `${label}: no live value (not read, or not found in the PLC)` : `${label}${live ? " = " + live.text : ""}: ${v ? "passes" : "does not pass"}`;
      return { html: `<div class="contact ${n.t === "cmp" ? "cmp" : ""} ${v === true ? "on" : v === false ? "off" : "unk"} ${blocker && ctx.coilOff ? "blocker" : ""}" title="${esc(tip)}">
          <span class="sym">${sym}${v == null ? `<span class="q" aria-hidden="true">?</span>` : ""}</span><span class="nm">${esc(label.replace(/^#/, ""))}</span></div>`, out: power && v === true };
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
    if (!anyPass && sub.blocked) ctx.blocked = true;
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
    const end = box
      ? `<div class="contact ${result ? "on" : result === false ? "off" : ""}"><span class="sym">${esc(box.kind)}</span><span class="nm">${esc(box.name)}${box.pt ? " · " + esc(box.pt) : ""}</span></div>`
      : `<div class="contact coil ${coilOn === true ? "on" : coilOn === false ? "off" : ""}"><span class="sym">${opt.coil ? "(" + opt.coil + ")" : "( )"}</span><span class="nm">${esc(lhs.replace(/^#/, ""))}</span></div>`;
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

  function netHtml(net, resolve, highlight, curStep, wantNet) {
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
      else if (assign) r = rung(assign[1], assign[2], resolve, null, { hl: writes(s, highlight) });
      if (r) { flush(); parts.push(r); } else plain.push(line);
    }
    flush();
    const hl = highlight ? net.lines.some((l) => writes(l, highlight)) : wantNet != null && net.no === String(wantNet);
    return `<div class="rung-card ${hl ? "hl" : ""}" ${hl ? 'id="hl"' : ""}>
      <div class="rung-title">${net.no ? "Network " + esc(net.no) : ""} ${net.lang ? `<span class="tag lang-${esc(net.lang)}">${esc(net.lang)}</span>` : ""}<span class="faint">${esc(net.title)}</span></div>
      ${parts.join("")}</div>`;
  }

  // ---- shared page for Code and Logic ----
  function program(kind) {
    let sel = null;        // { name, params, block, instances, inst }
    let loading = null;
    let blockQuery = "";   // the search above the block list
    let find = "";         // Code: find in this block
    let findAt = 0;

    async function select(params) {
      const name = params[0];
      const p = state.program;
      if (!p) return;
      const first = name || (kind === "logic" ? (p.blocks.find((b) => b.hasLogic) || p.blocks.find((b) => b.hasSource)) : p.blocks.find((b) => b.kind === "OB") || p.blocks[0])?.name;
      if (!first) return;
      if (sel && sel.name === first && sel.params.join("/") === params.join("/")) return;
      loading = first;
      try {
        const b = await block(first);
        const instances = await instancesOf(first);
        // After the block: an instance path and a local output (devices), or "network-N" and "@<symbol>" (alarms)
        const rest = params.slice(1);
        const net = (rest.find((x) => /^network-\d+$/.test(x)) || "").slice(8) || null;
        const sym = (rest.find((x) => x.startsWith("@")) || "").slice(1) || null;
        const plain = rest.filter((x) => !/^network-\d+$/.test(x) && !x.startsWith("@"));
        const inst = instances.find((i) => i.path === plain[0]) || instances[0] || null;
        sel = { name: first, params, block: b, instances, inst, highlight: sym || plain[1] || null, network: net };
      } catch (e) {
        sel = { name: first, params, error: e.message };
      }
      loading = null;
      PK.draw();
      setTimeout(() => { const h = document.getElementById("hl") || document.querySelector(".ln.hl"); if (h) h.scrollIntoView({ block: "center", behavior: "smooth" }); }, 60);
    }

    function list() {
      const p = state.program;
      const groups = [["OB", "Organization blocks"], ["FB", "Function blocks"], ["FC", "Functions"], ["DB", "Data blocks"]];
      const q = blockQuery.trim().toLowerCase();
      const ok = (b) => (kind === "code" || b.hasLogic || b.hasSource) && (!q || (b.name + " " + (b.title || "") + " " + (b.number ?? "")).toLowerCase().includes(q));
      const html = groups.map(([k, label]) => {
        const items = p.blocks.filter((b) => (k === "DB" ? /DB$/.test(b.kind) : b.kind === k) && ok(b));
        if (!items.length) return "";
        return `<div class="grp">${label}</div>` + items.map((b) => {
          const lang = (b.languages && b.languages[0]) || b.language || "";
          return `<a href="#/${kind}/${encodeURIComponent(b.name)}" class="${sel && sel.name === b.name ? "on" : ""}">${esc(b.name)}${lang && lang !== "DB" ? `<span class="tag lang-${esc(lang)}">${esc(lang)}</span>` : ""}</a>`;
        }).join("");
      }).join("");
      return `<input type="search" id="blk-q" class="blk-q" placeholder="Find a block" aria-label="Find a block" value="${esc(blockQuery)}">`
        + (html || `<p class="faint small" style="padding:8px 10px">No block matches “${esc(blockQuery)}”.</p>`);
    }

    return {
      live: true,
      needsValues: true,
      mount(el, params) {
        sel = null;
        PK.loadProgram().then(() => select(params));
        PK.draw();
      },
      render(params) {
        if (!state.program) {
          if (state.programError) return PK.hero({ title: "No program loaded yet.",
            sub: "This page shows the PLC program with live values once PLC-Ki has read it from a TIA archive. " + (state.user && state.user.role === "admin"
              ? "Import it under Setup, Program: it takes a few minutes." : "An administrator imports it under Setup, Program."),
            extra: `<p class="faint small" style="margin-top:14px">${esc(state.programError)}</p>`,
            actions: state.user && state.user.role === "admin" ? `<a class="btn" href="#/setup/program">Import the program</a>` : "" });
          return PK.hero({ title: "Loading the program…" });
        }
        if (params.join("/") !== (sel ? sel.params.join("/") : "") && loading == null) select(params);
        const p = state.program;
        const head = PK.sec("hero compact", `<div class="eyebrow">${esc(p.project)} · ${esc(p.plc)}${p.cpu ? " · " + esc(p.cpu) : ""}</div>
          <h1>${kind === "code" ? "Code" : "Logic"}</h1>
          <p class="sub">${kind === "code" ? "Every block as written, with live values. Click a signal in quotes to see where else it is used." : "Why an output is on, or not: each network as a rung with live values. The amber contact is the first one that stops the output."}</p>`);
        const body = (inner) => head + PK.sec("alt", `<div class="split"><nav class="blocklist">${list()}</nav><div class="stack">${inner}</div></div>`);
        if (!sel) return body(`<div class="card"><p class="muted">Loading…</p></div>`);
        if (sel.error) return body(`<div class="notice red">${icon("warn")}<div>${esc(sel.error)}</div></div>`);
        const b = sel.block;
        const resolve = resolver(sel.inst);
        const instPick = sel.instances.length > 1 ? `<select id="inst" style="width:auto">${sel.instances.map((i) => `<option ${i === sel.inst ? "selected" : ""}>${esc(i.path)}</option>`).join("")}</select>`
          : sel.inst ? `<span class="pill info">${esc(sel.inst.path)}</span>` : "";
        const lang = (b.languages && b.languages[0]) || b.language || "";
        const calledBy = (b.calledBy || []).map((n) => `<a href="#/${kind}/${encodeURIComponent(n)}">${esc(n)}</a>`).join(", ");
        const calls = [...new Set((b.calls || []).map((c) => c.block))].map((n) => `<a href="#/${kind}/${encodeURIComponent(n)}">${esc(n)}</a>`).join(", ");
        let bodyHtml;
        if (kind === "code") {
          const text = b.source || b.logic;
          const hits = text && find ? String(text).split(/\r?\n/).filter((l) => l.toLowerCase().includes(find.toLowerCase())).length : 0;
          bodyHtml = text ? `<div class="findbar"><input type="search" id="code-find" placeholder="Find in this block" aria-label="Find in this block" value="${esc(find)}">
              <span class="faint small" aria-live="polite">${find ? (hits ? `${hits} line${hits > 1 ? "s" : ""} · Enter for the next` : "Not in this block") : ""}</span></div>` + codeHtml(text, resolve, sel.highlight, find)
            : `<p class="muted">No code in this block (data only). Its fields are listed below.</p>`;
        } else {
          let nets = b.logic ? networks(b.logic) : sclNetworks(b.source);
          const legend = `<div class="legend rung-legend"><span><i class="lg on"></i>Passes power</span><span><i class="lg off"></i>Does not pass</span>
            <span><i class="lg unk"></i>No live value</span><span><i class="lg blocker"></i>First one that stops the output</span></div>`;
          const seq = ((state.status && state.status.sequences) || []).find((q) => q.block && PK.blockOf(q.block) === b.name);
          const now = seq && seq.step != null ? `<p class="small" style="margin:0 0 10px"><span class="now-tag">now</span> ${esc(seq.name)} is in step ${esc(String(seq.step))} “${esc(seq.stepName || "")}”${seq.halted ? ", halted" : ""}.</p>` : "";
          // The signal is not written in this block (or the decoder wrote it another way): fall back to the network number
          const hl = sel.highlight && nets.some((n) => n.lines.some((l) => writes(l, sel.highlight))) ? sel.highlight : null;
          const where = sel.highlight && sel.highlight.startsWith('"')
            ? (hl ? `<div class="notice" style="margin-bottom:12px">${icon("info")}<div>The highlighted rungs write <b>${esc(sel.highlight)}</b>: (S) sets it, (R) resets it, ( ) follows the rung. The values are live; amber marks the first contact that stops a rung.</div></div>`
              : `<div class="notice gold" style="margin-bottom:12px">${icon("info")}<div>${esc(sel.highlight)} is not written in this block${sel.network ? `; network ${esc(sel.network)} is highlighted` : ""}.</div></div>`) : "";
          bodyHtml = nets.length ? where + now + legend + nets.map((n) => netHtml(n, resolve, hl || (sel.highlight && !sel.highlight.startsWith('"') ? sel.highlight : null), seq ? seq.step : null, hl ? null : sel.network)).join("")
            : `<p class="muted">No boolean logic to draw in this block. <a href="#/code/${encodeURIComponent(b.name)}">Show the code</a>.</p>`;
        }
        const iface = (b.interface || []).filter((m) => m.section !== "Temp" && m.section !== "Constant");
        return body(`
            <div class="card">
              <div class="row"><h2 style="margin:0;font-size:26px">${esc(b.name)}</h2>${lang ? `<span class="tag lang-${esc(lang)}">${esc(lang)}</span>` : ""}<span class="tag">${esc(b.kind)}${b.number != null ? " " + b.number : ""}</span><span class="spacer"></span>${instPick}
                ${kind === "logic" ? (b.source || b.logic ? `<button type="button" class="btn plain small" data-switch="code">Show the code ›</button>` : "") : (b.logic || b.source ? `<button type="button" class="btn plain small" data-switch="logic">Show the logic ›</button>` : "")}</div>
              ${b.title || b.comment ? `<p class="muted" style="margin:10px 0 0">${esc([b.title, b.comment].filter(Boolean).join(" · "))}</p>` : ""}
              <div class="row small" style="margin-top:10px;gap:18px">${calledBy ? `<span class="faint">Called by ${calledBy}</span>` : ""}${calls ? `<span class="faint">Calls ${calls}</span>` : ""}
                ${sel.inst && Object.keys(sel.inst.wires).length ? `<span class="faint">Inputs wired in ${esc(sel.inst.path.split(".")[0])}</span>` : ""}</div>
              <div style="margin-top:14px">${bodyHtml}</div>
            </div>
            ${window.PKTags ? window.PKTags.blockTagsHtml(b.source || b.logic) : ""}
            ${iface.length ? `<div class="card"><details class="more" style="border:0;margin:0;padding:0" ${kind === "code" && !b.source && !b.logic ? "open" : ""}><summary>Interface (${iface.length})</summary>
              <table class="tbl iface" style="margin-top:8px"><tr><th>Name</th><th>Section</th><th>Type</th><th>Now</th><th>Comment</th></tr>
              ${iface.map((m) => { const v = resolve("#" + m.name); return `<tr><td>${esc(m.name)}</td><td class="muted">${esc(m.section)}</td><td class="mono">${esc(m.datatype)}</td><td>${v ? chip(v) : ""}</td><td class="muted">${esc(m.comment || "")}</td></tr>`; }).join("")}
              </table></details></div>` : ""}`);
      },
      after(page) {
        page.querySelectorAll("[data-switch]").forEach((b) => b.onclick = () => {
          const rest = sel ? "/" + sel.params.map(encodeURIComponent).join("/") : "";
          PK.go("#/" + b.dataset.switch + rest);
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
        page.querySelectorAll("[data-used]").forEach((o) => o.onclick = () => usedIn(o.dataset.used));
      },
    };
  }

  /** Scroll to the next line that matches "Find in this block". */
  function jump(page, at) {
    const hits = page.querySelectorAll(".ln.found");
    if (!hits.length) return;
    const el = hits[((at % hits.length) + hits.length) % hits.length];
    // the line keeps its place across live updates, so the scroll stays where it is
    el.scrollIntoView({ block: "center" });
    el.animate && el.animate([{ outline: "2px solid var(--accent)" }, { outline: "2px solid transparent" }], { duration: 1200 });
  }

  /** "Where is this used": every block that mentions the signal, from /api/program/search. */
  async function usedIn(symbol) {
    const s = PK.sheet(`<span class="eyebrow">Where is this used</span><h2 class="mono" style="font-size:20px;word-break:break-all">${esc(symbol)}</h2><div class="used-list"><p class="muted">Searching the program…</p></div>
      <div class="row end"><button class="btn" data-close>OK</button></div>`);
    let r;
    try { r = await PK.api("GET", "/api/program/search?q=" + encodeURIComponent(symbol)); } catch (e) { r = { results: [], error: e.message }; }
    const box = s.el.querySelector(".used-list");
    if (!box) return;
    box.innerHTML = r.error ? `<p class="muted">${esc(r.error)}</p>` : !r.results.length ? `<p class="muted">Not found in any block.</p>`
      : `<p class="muted small" style="margin:0 0 8px">In ${r.results.length} block${r.results.length > 1 ? "s" : ""}.</p><ul class="list">${r.results.map((x) => `<li style="display:block">
          <a href="#/code/${encodeURIComponent(x.block)}" data-close><b>${esc(x.block)}</b></a> <span class="faint small">${esc(x.kind)} · ${x.count} line${x.count > 1 ? "s" : ""}</span>
          ${x.lines.map((l) => `<div class="mono small faint" style="margin-top:4px;overflow-wrap:anywhere">${esc(l.text)}</div>`).join("")}</li>`).join("")}</ul>`;
  }

  PK.view("code", program("code"));
  PK.view("logic", program("logic"));
})();
