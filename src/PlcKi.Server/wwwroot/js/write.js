// Change a value in the PLC (administrators only), from the Tags page and the Program page's tag list.
// The server decides what is allowed (GET /api/admin/write/check, ChangeRules): on a PLCSIM the TIA helper confirms,
// inputs, outputs, memory and data values, safety ones after a warning and one click on "I understand"; on a real PLC
// only when an administrator allowed that PLC, with a reason and the PLC address typed again, only memory and data
// values the safety program does not read. Never F-blocks. Every change and every refused one goes into the change log.
// Before a change the sheet says why it may not stay (the code that writes the value). For an input on PLCSIM it
// offers "Hold on PLCSIM" (GET/POST /api/admin/hold): the simulated input stays set until it is released.
"use strict";

(() => {
  const { esc, icon, api, toast } = PK;
  const TYPES = new Set(["Bool", "Byte", "Word", "DWord", "LWord", "SInt", "USInt", "Int", "UInt", "DInt", "UDInt", "LInt", "ULInt",
    "Real", "LReal", "Time", "String", "WString"]);
  const isAdmin = () => PK.can("plcsim");     // may change and hold values (on a real PLC only an administrator; the server decides)
  const typeOf = (t) => String(t.datatype || t.type || "").replace(/\[.*$/, "").trim();

  /** The small "Change" button next to a live value, or nothing (operators, arrays, structs, unknown types). */
  function button(t) {
    if (!isAdmin() || !TYPES.has(typeOf(t)) || !t.symbol) return "";
    // Test values come from a file: there is nothing to change, for anyone (the pen only said so)
    if (PK.testValues()) return "";
    // A technician only gets the pen while the PLC is confirmed PLCSIM (the server refuses anything else anyway)
    if (!PK.can("real") && !(PK.state.changes && PK.state.changes.values === "plcsim")) return "";
    return `<button type="button" class="btn plain small write-btn" data-write="${esc(t.symbol)}" data-wtype="${esc(typeOf(t))}" title="${esc(PK.t("Change this value in the PLC"))}" aria-label="${esc(PK.t("Change {name}", { name: t.symbol }))}">${icon("pen")}</button>`;
  }

  /** Wires the buttons inside a page; current(symbol) gives the live value shown. */
  function wire(root, current) {
    root.querySelectorAll("[data-write]").forEach((b) => b.onclick = (e) => {
      e.preventDefault(); e.stopPropagation();
      open(b.dataset.write, b.dataset.wtype, current ? current(b.dataset.write) : null);
    });
  }

  const shown = (v) => v === true ? "TRUE" : v === false ? "FALSE" : v == null ? "…" : String(v);

  /** A safety warning that must be acknowledged with one click before the Write or Hold button works. */
  const warnHtml = (w) => w ? `<div class="notice red" data-warn>${icon("shield")}<div><b>${esc(PK.srv(w))}</b></div>
      <button type="button" class="no-shrink btn small" data-ack>${PK.t("I understand")}</button></div>` : "";

  /** Wires the "I understand" button; returns () => acknowledged (true when there is no warning). */
  function wireAck(el, update) {
    const b = el.querySelector("[data-ack]");
    if (!b) return () => true;
    let ok = false;
    b.onclick = () => { ok = true; b.disabled = true; b.innerHTML = `${icon("check")} ${PK.t("Understood")}`; update(); };
    return () => ok;
  }

  async function open(symbol, type, now, preset) {
    let check;
    try { check = await api("GET", "/api/admin/write/check?symbol=" + encodeURIComponent(symbol)); }
    catch (e) { toast(e.message, "error"); return; }
    const real = check.target === "real";
    const where = check.target === "plcsim" ? `<span class="pill ok"><span class="dot"></span>PLCSIM ${esc(check.plc)}</span>`
      : real ? `<span class="pill bad"><span class="dot"></span>${PK.t("Real PLC")} ${esc(check.plc)}</span>` : "";
    const isInput = /^%[IE]/i.test(check.address || "");
    const isOutput = /^%[QA]/i.test(check.address || "");
    const canHold = isInput && check.target === "plcsim";
    const noHold = isOutput && check.target === "plcsim"
      ? PK.t("Hold on PLCSIM is not possible for an output: the program rewrites outputs every cycle, and PLCSIM only keeps inputs.") : "";
    if (!check.allowed) {
      PK.sheet(`<span class="eyebrow">${PK.t("Change a value")}</span><h2 class="mono" style="font-size:var(--fs-5);word-break:break-all">${esc(symbol)}</h2>
        <div class="row">${where}</div>
        <div class="notice gold">${icon("shield")}<div>${esc(PK.srv(check.why))}</div></div>
        ${real && check.plcsimWhy && !String(check.why || "").startsWith(check.plcsimWhy) ? `<p class="m-0 faint small">${esc(PK.srv(check.plcsimWhy))} ${PK.t("So PLC-Ki uses the rules for a real PLC.")}</p>` : ""}
        <div class="row end"><button class="btn" data-close>${PK.t("Done")}</button></div>`);
      return;
    }
    const isBool = type === "Bool";
    const hint = { Time: PK.t("milliseconds, or 5s, 2m, T#1M30S"), Real: PK.t("a number, e.g. 12.5"), LReal: PK.t("a number, e.g. 12.5"), String: PK.t("text"), WString: PK.t("text") }[type] || PK.t("a whole number");
    const s = PK.sheet(`<span class="eyebrow" ${real ? 'style="color:var(--fault)"' : ""}>${real ? PK.t("Real PLC · change a value") : PK.t("Change a value")}</span>
      <h2 class="mono" style="font-size:var(--fs-5);word-break:break-all">${esc(symbol)}</h2>
      <div class="row">${where}<span class="tag">${esc(type)}</span>${check.address ? `<span class="tag">${esc(check.address)}</span>` : ""}<span class="muted small">${PK.t("now")} <b>${esc(shown(now))}</b></span></div>
      ${real ? `<div class="notice red">${icon("warn")}<div><b>${PK.t("This changes the running machine.")}</b> ${PK.t("Make sure nobody is in the machine and that the change is safe. It is logged with your name and reason. Inputs, outputs and values the safety program reads are never changed.")}</div></div>` : ""}
      ${warnHtml(check.warning)}
      ${isInput ? `<div class="notice gold">${icon("info")}<div>${PK.t("A one-time change of an input is overwritten at the next cycle: the CPU reads its inputs at the start of every cycle.")}
          ${canHold ? PK.t("To keep a value, hold the simulated input instead.") : ""}</div>${canHold ? `<button type="button" class="no-shrink btn small" data-hold>${PK.t("Hold on PLCSIM")}</button>` : ""}</div>`
        : `<div class="notice gold why-stick" data-why>${noHold ? `${icon("info")}<div>${esc(noHold)}</div>` : ""}</div>`}
      ${isBool ? PK.ui.choice(PK.t("New value"), [{ label: "TRUE", on: preset === "TRUE" || (preset == null && now === false), data: { "data-b": "TRUE" } }, { label: "FALSE", on: preset === "FALSE" || (preset == null && now === true), data: { "data-b": "FALSE" } }], { cls: "start" })
        : `<label class="field">${PK.t("New value")} <span class="hint">${esc(hint)}</span><input id="w-val" autocomplete="off" spellcheck="false" value="${esc(preset ?? (now == null ? "" : shown(now)))}" style="font-family:var(--mono);font-size:var(--fs-4)"></label>`}
      <label class="field">${PK.t("Why")} ${real ? `<span class="hint">${PK.t("needed on a real PLC, goes into the change log")}</span>` : `<span class="hint">${PK.t("optional")}</span>`}<input id="w-why" autocomplete="off" placeholder="${esc(real ? PK.t("e.g. test valve XV101 after repair") : PK.t("e.g. test the door alarm"))}"></label>
      ${real ? `<label class="field"><span>${PK.t("Type {plc} to confirm", { plc: `<b class="mono">${esc(check.plc)}</b>` })}</span><input id="w-ok" autocomplete="off" spellcheck="false" inputmode="decimal" style="font-family:var(--mono)"></label>` : ""}
      <p class="m-0 ink-fault small" data-err></p>
      <div class="row end"><button class="btn gray" data-close>${PK.t("Cancel")}</button><button class="btn ${real ? "red" : ""}" data-go>${real ? PK.t("Write to the real PLC") : PK.t("Write")}</button></div>`);
    const el = s.el, go = el.querySelector("[data-go]");
    const holdBtn = el.querySelector("[data-hold]");
    if (holdBtn) holdBtn.onclick = () => { s.close(); openHold(symbol, type); };
    // Why a change may not stay: the code that writes this value (filled in when the program search answers)
    const whyEl = el.querySelector("[data-why]");
    if (whyEl && window.PKProgram && PKProgram.whyNotStick) {
      PKProgram.whyNotStick(symbol).then((html) => {
        if (!html) return;
        whyEl.innerHTML = `${icon("info")}<div>${noHold ? esc(noHold) + " " : ""}${html} ${check.target === "plcsim" ? PK.t("An S7-1500 can force only inputs and outputs, so to change this for good, change the program, or on PLCSIM change what feeds it.") : PK.t("An S7-1500 can force only inputs and outputs, so to change this for good, change the program.")}</div>`;
      }).catch(() => { /* no program search: say nothing */ });
    }
    let boolVal = isBool ? (el.querySelector("[data-b].on") || {}).dataset?.b || null : null;
    const val = () => { const x = isBool ? boolVal : (el.querySelector("#w-val").value || "").trim(); return /^L?Real$/.test(type) ? x.replace(/^(-?\d+),(\d+)$/, "$1.$2") : x; };   // a decimal comma: 12,5 = 12.5
    const why = () => el.querySelector("#w-why").value.trim();
    let acked = () => true;
    const ready = () => !!val() && acked() && (!real || (why().length >= 5 && (el.querySelector("#w-ok").value || "").trim() === check.plc));
    const update = () => { go.disabled = !ready(); };
    acked = wireAck(el, update);
    el.querySelectorAll("[data-b]").forEach((b) => b.onclick = () => {
      boolVal = b.dataset.b; el.querySelectorAll("[data-b]").forEach((x) => { x.classList.toggle("on", x === b); x.setAttribute("aria-pressed", String(x === b)); }); update();
    });
    el.querySelectorAll("input").forEach((i) => { i.oninput = update; i.onkeydown = (e) => { if (e.key === "Enter" && ready()) go.click(); }; });
    update();
    const first = el.querySelector("#w-val") || el.querySelector("[data-b]");
    if (first) first.focus();
    go.onclick = async () => {
      go.disabled = true; go.textContent = PK.t("Writing…");
      try {
        const r = await api("POST", "/api/admin/write", { symbol, value: val(), reason: why() || null, confirm: real ? el.querySelector("#w-ok").value.trim() : null,
          understood: !!check.warning && acked() });
        s.close();
        done(symbol, type, r);
      } catch (e) {
        el.querySelector("[data-err]").textContent = PK.srv(e.message);
        go.textContent = real ? PK.t("Write to the real PLC") : PK.t("Write"); update();
      }
    };
  }

  function done(symbol, type, r) {
    const s = PK.sheet(`<span class="eyebrow">${r.target === "real" ? PK.t("Real PLC") : "PLCSIM"} · ${PK.t("changed")}</span>
      <h2 class="mono" style="font-size:var(--fs-5);word-break:break-all">${esc(symbol)}</h2>
      <p style="margin:0;font-size:var(--fs-5)"><span class="mono">${esc(r.before)}</span> → <b class="mono">${esc(r.written)}</b></p>
      ${r.held ? `<div class="notice green">${icon("check")}<div>${r.after != null ? PK.t("The PLC holds the new value (read back: {after}).", { after: esc(r.after) }) : PK.t("The PLC holds the new value.")}</div></div>`
        : `<div class="notice gold">${icon("warn")}<div>${esc(r.note ? PK.srv(r.note) : PK.t("The value did not stay."))}</div></div>`}
      <div class="row end"><button class="btn gray" data-back>${PK.t("Put back {value}", { value: esc(r.before) })}</button><button class="btn" data-close>${PK.t("Done")}</button></div>`);
    s.el.querySelector("[data-back]").onclick = () => { s.close(); open(symbol, type, r.after ?? r.written, r.before); };
    toast(`${symbol}: ${r.before} → ${r.written}`);
    loadLog(true);
  }

  /**
   * Hold on PLCSIM: sets the simulated input so it stays (until released). Not a CPU force: TIA's force table does
   * not show it, and an output cannot be held. The server checks the rules (PLCSIM confirmed, inputs only; a safety
   * input needs one click on "I understand" under the warning).
   */
  async function openHold(symbol, type, preset) {
    let c;
    try { c = await api("GET", "/api/admin/hold/check?symbol=" + encodeURIComponent(symbol)); }
    catch (e) { toast(e.message, "error"); return; }
    if (!c.allowed) {
      PK.sheet(`<span class="eyebrow">${PK.t("Hold on PLCSIM")}</span><h2 class="mono" style="font-size:var(--fs-5);word-break:break-all">${esc(symbol)}</h2>
        <div class="notice gold">${icon("shield")}<div>${esc(PK.srv(c.why))}</div></div>
        <div class="row end"><button class="btn" data-close>${PK.t("Done")}</button></div>`);
      return;
    }
    const dt = c.datatype || type;
    const isBool = dt === "Bool";
    const h = c.held;
    const s = PK.sheet(`<span class="eyebrow">${PK.t("Hold on PLCSIM")}</span>
      <h2 class="mono" style="font-size:var(--fs-5);word-break:break-all">${esc(symbol)}</h2>
      <div class="row"><span class="pill ok"><span class="dot"></span>PLCSIM ${esc(c.instance || "")} ${esc(c.plc)}</span><span class="tag">${esc(dt)}</span><span class="tag">${esc(c.address)}</span>
        <span class="muted small">${PK.t("now")} <b>${esc(c.now)}</b></span></div>
      ${h ? `<div class="notice gold">${icon("warn")}<div>${PK.t("Held at {value} by {user}, {time}. Release puts back {before}.", { value: `<b>${esc(h.value)}</b>`, user: esc(h.user), time: esc(PK.date(h.time)), before: `<b>${esc(h.before)}</b>` })}</div>
          <button type="button" class="no-shrink btn small" data-release>${PK.t("Release")}</button></div>` : ""}
      <p class="m-0">${PK.t("This sets the simulated input in PLCSIM. It stays until you release it; release puts back {value}. It is not a CPU force: TIA's force table does not show it.", { value: `<b class="mono">${esc(h ? h.before : c.now)}</b>` })}</p>
      ${warnHtml(c.warning)}
      <div class="notice gold why-stick" data-copies></div>
      ${isBool ? PK.ui.choice(PK.t("Value to hold"), [{ label: "TRUE", on: preset === "TRUE" || (preset == null && c.now === "FALSE"), data: { "data-b": "TRUE" } }, { label: "FALSE", on: preset === "FALSE" || (preset == null && c.now === "TRUE"), data: { "data-b": "FALSE" } }], { cls: "start" })
        : `<label class="field">${PK.t("Value to hold")} <span class="hint">${esc(dt === "Real" ? PK.t("a number, e.g. 12.5") : PK.t("a whole number"))}</span><input id="h-val" autocomplete="off" spellcheck="false" value="${esc(preset ?? c.now ?? "")}" style="font-family:var(--mono);font-size:var(--fs-4)"></label>`}
      <label class="field">${PK.t("Why")} <span class="hint">${PK.t("optional, goes into the change log")}</span><input id="h-why" autocomplete="off" placeholder="${esc(PK.t("e.g. test the door alarm"))}"></label>
      <p class="m-0 ink-fault small" data-err></p>
      <div class="row end"><button class="btn gray" data-close>${PK.t("Cancel")}</button><button class="btn" data-go>${PK.t("Hold")}</button></div>`);
    const el = s.el, go = el.querySelector("[data-go]");
    let boolVal = isBool ? (el.querySelector("[data-b].on") || {}).dataset?.b || null : null;
    const val = () => { const x = isBool ? boolVal : (el.querySelector("#h-val").value || "").trim(); return /^L?Real$/.test(type) ? x.replace(/^(-?\d+),(\d+)$/, "$1.$2") : x; };   // a decimal comma: 12,5 = 12.5
    let acked = () => true;
    const ready = () => !!val() && acked();
    const update = () => { go.disabled = !ready(); };
    acked = wireAck(el, update);
    el.querySelectorAll("[data-b]").forEach((b) => b.onclick = () => {
      boolVal = b.dataset.b; el.querySelectorAll("[data-b]").forEach((x) => { x.classList.toggle("on", x === b); x.setAttribute("aria-pressed", String(x === b)); }); update();
    });
    el.querySelectorAll("input").forEach((i) => { i.oninput = update; i.onkeydown = (e) => { if (e.key === "Enter" && ready()) go.click(); }; });
    update();
    const rel = el.querySelector("[data-release]");
    if (rel) rel.onclick = async () => { s.close(); await release(symbol); };
    // The program may copy this input into a tag that something else (a machine simulator) writes as well
    if (window.PKProgram && PKProgram.copiesOverwritten) {
      PKProgram.copiesOverwritten(symbol).then((list) => {
        if (!list.length) return;
        el.querySelector("[data-copies]").innerHTML = `${icon("info")}<div>${list.map((x) => x.html).join(" ")}
          ${PK.t("On PLCSIM a fault switch of the machine simulator (its data block) is often the better way to test this.")}</div>`;
      }).catch(() => { /* say nothing */ });
    }
    go.onclick = async () => {
      go.disabled = true; go.textContent = PK.t("Holding…");
      try {
        const r = await api("POST", "/api/admin/hold", { symbol, value: val(), reason: el.querySelector("#h-why").value.trim() || null,
          understood: !!c.warning && acked() });
        s.close();
        heldDone(symbol, type, r);
      } catch (e) {
        el.querySelector("[data-err]").textContent = PK.srv(e.message);
        go.textContent = PK.t("Hold"); update();
      }
    };
  }

  function heldDone(symbol, type, r) {
    const ok = r.now === r.value;
    const s = PK.sheet(`<span class="eyebrow">PLCSIM · ${PK.t("held")}</span>
      <h2 class="mono" style="font-size:var(--fs-5);word-break:break-all">${esc(symbol)}</h2>
      <p style="margin:0;font-size:var(--fs-5)"><span class="mono">${esc(r.before)}</span> → <b class="mono">${esc(r.value)}</b></p>
      ${ok ? `<div class="notice green">${icon("check")}<div>${PK.t("PLCSIM holds {address} at {value} until you release it. The strip at the top of every page shows it.", { address: esc(r.address), value: esc(r.value) })}</div></div>`
        : `<div class="notice gold">${icon("warn")}<div>${PK.t("PLCSIM reads {now}, not {value}. Something else sets this input.", { now: esc(r.now), value: esc(r.value) })}</div></div>`}
      <div class="row end"><button class="btn gray" data-rel>${PK.t("Release now")}</button><button class="btn" data-close>${PK.t("Done")}</button></div>`);
    s.el.querySelector("[data-rel]").onclick = async () => { s.close(); await release(symbol); };
    toast(PK.t("{symbol} held at {value} on PLCSIM", { symbol, value: r.value }));
    changed();
  }

  async function release(symbol) {
    try {
      const r = await api("POST", "/api/admin/hold/release", { symbol });
      const x = r.results[0];
      if (!x.ok) toast(x.error, "error");
      else toast(x.note ? `${symbol}: ${PK.srv(x.note)}` : PK.t("{symbol} released: back to {now}.", { symbol, now: x.now }));
    } catch (e) { toast(e.message, "error"); }
    changed();
  }

  /** After a hold or release: the strip on every page, the held list and the change log. */
  function changed() {
    if (PK.loadHeld) PK.loadHeld();
    heldAt = 0;
    loadLog(true);
  }
  window.addEventListener("pk:held-changed", () => { heldAt = 0; loadHeld(); loadLog(true); });

  /** The held inputs for the Tags page (administrators), checked against PLCSIM. */
  let heldList = null, heldAt = 0;
  async function loadHeld() {
    if (!isAdmin() || Date.now() - heldAt < 15000) return;
    heldAt = Date.now();
    try { heldList = await api("GET", "/api/admin/hold"); PK.draw(); } catch { /* next time */ }
  }
  function heldHtml() {
    loadHeld();
    const items = (heldList && heldList.items) || [];
    if (!items.length) return "";
    return `<div class="card"><h3 class="mt-0 mb-1">${PK.t("Inputs held on PLCSIM ({n})", { n: items.length })}</h3>
      <p class="mt-0 mb-2 muted small">${PK.t("Each stays until released; release puts back the value it had before.")}
        ${heldList.checkedNow ? "" : PK.t("PLCSIM could not be read just now ({error}), so this is PLC-Ki's own list.", { error: esc(PK.srv(heldList.error || "")) })}</p>
      <table class="tbl"><tr><th>${PK.t("Input")}</th><th>${PK.t("Held at")}</th><th>${PK.t("Before")}</th><th>${PK.t("Who")}</th><th>${PK.t("Since")}</th><th></th></tr>
      ${items.map((h) => `<tr><td class="mono small" style="word-break:break-all">${esc(h.symbol)} <span class="faint">${esc(h.address)}</span></td>
        <td><b class="mono">${esc(h.value)}</b></td><td class="mono">${esc(h.before)}</td><td>${esc(h.user)}</td>
        <td class="nowrap small">${esc(PK.date(h.time))}</td>
        <td><button type="button" class="btn gray small" data-release-one="${esc(h.symbol)}">${PK.t("Release")}</button></td></tr>`).join("")}
      </table>
      <div class="mt-2 row end"><button type="button" class="btn small" data-release-every>${PK.t("Release all")}</button></div></div>`;
  }

  /** The change log for the Tags page (administrators). */
  let log = null, logAt = 0;
  async function loadLog(force) {
    if (!isAdmin() || (!force && Date.now() - logAt < 10000)) return;
    logAt = Date.now();
    try { log = await api("GET", "/api/admin/write/log?max=30"); PK.draw(); } catch { /* next time */ }
  }
  const ACTION = { hold: PK.T("Held"), release: PK.T("Released"), "release-all": PK.T("Released (all)"), "changed-outside": PK.T("No longer held"), clear: PK.T("Cleared") };
  // The rule that allowed a change, shown only when it came with a warning
  const RULE = { "plcsim-safety-input": PK.T("safety input"), "plcsim-safety-output": PK.T("safety output"), "plcsim-safety-read": PK.T("safety program reads it"),
    "plcsim-input-unchecked": PK.T("F-module not checked"), "plcsim-output-unchecked": PK.T("F-module not checked") };
  function logHtml() {
    if (!isAdmin()) return "";
    loadLog();
    const held = heldHtml();
    // On test values nothing can change, so an empty log needs no line about the pen
    if (!log || !log.length) return held + (PK.testValues() ? "" : `<p class="faint small">${PK.t("Values changed from PLC-Ki are listed here. Administrators can change a value with {icon} next to it.", { icon: icon("pen") })}</p>`);
    return held + `<div class="card"><details class="more" style="border:0;margin:0;padding:0"><summary>${PK.t("Changes made from PLC-Ki ({count})", { count: log.length + (log.length >= 30 ? "+" : "") })}</summary>
      <table class="mt-2 tbl"><tr><th>${PK.t("When")}</th><th>${PK.t("Who")}</th><th>${PK.t("PLC")}</th><th>${PK.t("Value")}</th><th>${PK.t("Change|Changes", { n: 1 })}</th><th>${PK.t("Why")}</th></tr>
      ${log.map((e) => `<tr><td class="nowrap small">${esc(PK.date(e.time))}</td><td>${esc(e.user)}</td>
        <td><span class="pill ${e.target === "real" ? "bad" : "ok"}" style="font-size:var(--fs-1)">${e.target === "real" ? PK.t("Real") : e.target === "plcsim" ? "PLCSIM" : "–"} ${esc(e.plc)}</span></td>
        <td class="mono small" style="word-break:break-all">${esc(e.symbol)}</td>
        <td class="small">${ACTION[e.action] ? `<span class="tag gold">${PK.t(ACTION[e.action])}</span> ` : ""}${RULE[e.rule] ? `<span class="tag red" title="${esc(e.rule)}">${PK.t(RULE[e.rule])}</span> ` : ""}${e.action === "clear" ? esc(e.value) : e.ok ? `${esc(e.before)} → <b>${esc(e.value)}</b>${e.held === false && !e.action ? ` <span class="faint">${PK.t("(set back to {value})", { value: esc(e.after) })}</span>` : ""}` : `<span class="ink-fault">${PK.t("Refused:")} ${esc(PK.srv(e.error))}</span>`}</td>
        <td class="muted small">${esc(PK.srv(e.reason || ""))}</td></tr>`).join("")}
      </table></details></div>`;
  }

  /** Wires the Tags page: value buttons, and the Release buttons of the held list. */
  function wireAll(root, current) {
    wire(root, current);
    root.querySelectorAll("[data-release-one]").forEach((b) => b.onclick = () => release(b.dataset.releaseOne));
    root.querySelectorAll("[data-release-every]").forEach((b) => b.onclick = () => PK.releaseAll && PK.releaseAll());
  }

  window.PKWrite = { button, wire: wireAll, logHtml, openHold, refreshLog: () => loadLog(true) };
})();
