// Change a value in the PLC (administrators only), from the Tags page and the Program page's tag list.
// The server decides what is allowed (GET /api/admin/write/check): on PLCSIM any value except safety ones; on a real
// PLC only when an administrator allowed that PLC, with a reason and the PLC address typed again, never inputs,
// outputs or safety values. Every change and every refused one goes into the change log.
"use strict";

(() => {
  const { esc, icon, api, toast } = PK;
  const TYPES = new Set(["Bool", "Byte", "Word", "DWord", "LWord", "SInt", "USInt", "Int", "UInt", "DInt", "UDInt", "LInt", "ULInt",
    "Real", "LReal", "Time", "String", "WString"]);
  const isAdmin = () => !!(PK.state.user && PK.state.user.role === "admin");
  const typeOf = (t) => String(t.datatype || t.type || "").replace(/\[.*$/, "").trim();

  /** The small "Change" button next to a live value, or nothing (operators, arrays, structs, unknown types). */
  function button(t) {
    if (!isAdmin() || !TYPES.has(typeOf(t)) || !t.symbol) return "";
    return `<button type="button" class="btn plain small write-btn" data-write="${esc(t.symbol)}" data-wtype="${esc(typeOf(t))}" title="Change this value in the PLC" aria-label="Change ${esc(t.symbol)}">${icon("pen")}</button>`;
  }

  /** Wires the buttons inside a page; current(symbol) gives the live value shown. */
  function wire(root, current) {
    root.querySelectorAll("[data-write]").forEach((b) => b.onclick = (e) => {
      e.preventDefault(); e.stopPropagation();
      open(b.dataset.write, b.dataset.wtype, current ? current(b.dataset.write) : null);
    });
  }

  const shown = (v) => v === true ? "TRUE" : v === false ? "FALSE" : v == null ? "…" : String(v);

  async function open(symbol, type, now, preset) {
    let check;
    try { check = await api("GET", "/api/admin/write/check?symbol=" + encodeURIComponent(symbol)); }
    catch (e) { toast(e.message, "error"); return; }
    const real = check.target === "real";
    const where = check.target === "plcsim" ? `<span class="pill ok"><span class="dot"></span>PLCSIM ${esc(check.plc)}</span>`
      : real ? `<span class="pill bad"><span class="dot"></span>Real PLC ${esc(check.plc)}</span>` : "";
    if (!check.allowed) {
      PK.sheet(`<span class="eyebrow">Change a value</span><h2 class="mono" style="font-size:22px;word-break:break-all">${esc(symbol)}</h2>
        <div class="row">${where}</div>
        <div class="notice gold">${icon("shield")}<div>${esc(check.why)}</div></div>
        ${real && check.plcsimWhy ? `<p class="faint small" style="margin:0">Why PLC-Ki treats it as a real PLC: ${esc(check.plcsimWhy)}</p>` : ""}
        <div class="row end"><button class="btn" data-close>OK</button></div>`);
      return;
    }
    const isBool = type === "Bool";
    const hint = { Time: "milliseconds, or 5s, 2m, T#1M30S", Real: "a number, e.g. 12.5", LReal: "a number, e.g. 12.5", String: "text", WString: "text" }[type] || "a whole number";
    const s = PK.sheet(`<span class="eyebrow" ${real ? 'style="color:var(--fault)"' : ""}>${real ? "Real PLC · change a value" : "Change a value"}</span>
      <h2 class="mono" style="font-size:22px;word-break:break-all">${esc(symbol)}</h2>
      <div class="row">${where}<span class="tag">${esc(type)}</span>${check.address ? `<span class="tag">${esc(check.address)}</span>` : ""}<span class="muted small">now <b>${esc(shown(now))}</b></span></div>
      ${real ? `<div class="notice red">${icon("warn")}<div><b>This changes the running machine.</b> Make sure nobody is in the machine and that the change is safe.
        It is logged with your name and reason. Safety values, inputs and outputs are never changed.</div></div>` : ""}
      ${isBool ? `<div class="segmented" role="group" aria-label="New value" style="align-self:flex-start">
          <button type="button" data-b="TRUE" class="${preset === "TRUE" || (preset == null && now === false) ? "on" : ""}">TRUE</button>
          <button type="button" data-b="FALSE" class="${preset === "FALSE" || (preset == null && now === true) ? "on" : ""}">FALSE</button></div>`
        : `<label class="field">New value <span class="hint">${esc(hint)}</span><input id="w-val" autocomplete="off" spellcheck="false" value="${esc(preset ?? (now == null ? "" : shown(now)))}" style="font-family:var(--mono);font-size:18px"></label>`}
      <label class="field">Why ${real ? "<span class=\"hint\">needed on a real PLC, goes into the change log</span>" : "<span class=\"hint\">optional</span>"}<input id="w-why" autocomplete="off" placeholder="${real ? "e.g. test valve XV101 after repair" : "e.g. test the door alarm"}"></label>
      ${real ? `<label class="field"><span>Type <b class="mono">${esc(check.plc)}</b> to confirm</span><input id="w-ok" autocomplete="off" spellcheck="false" inputmode="decimal" style="font-family:var(--mono)"></label>` : ""}
      <p class="small" data-err style="margin:0;color:var(--fault-ink)"></p>
      <div class="row end"><button class="btn gray" data-close>Cancel</button><button class="btn ${real ? "red" : ""}" data-go>${real ? "Write to the real PLC" : "Write"}</button></div>`);
    const el = s.el, go = el.querySelector("[data-go]");
    let boolVal = isBool ? (el.querySelector("[data-b].on") || {}).dataset?.b || null : null;
    const val = () => isBool ? boolVal : (el.querySelector("#w-val").value || "").trim();
    const why = () => el.querySelector("#w-why").value.trim();
    const ready = () => !!val() && (!real || (why().length >= 5 && (el.querySelector("#w-ok").value || "").trim() === check.plc));
    const update = () => { go.disabled = !ready(); };
    el.querySelectorAll("[data-b]").forEach((b) => b.onclick = () => {
      boolVal = b.dataset.b; el.querySelectorAll("[data-b]").forEach((x) => x.classList.toggle("on", x === b)); update();
    });
    el.querySelectorAll("input").forEach((i) => { i.oninput = update; i.onkeydown = (e) => { if (e.key === "Enter" && ready()) go.click(); }; });
    update();
    const first = el.querySelector("#w-val") || el.querySelector("[data-b]");
    if (first) first.focus();
    go.onclick = async () => {
      go.disabled = true; go.textContent = "Writing…";
      try {
        const r = await api("POST", "/api/admin/write", { symbol, value: val(), reason: why() || null, confirm: real ? el.querySelector("#w-ok").value.trim() : null });
        s.close();
        done(symbol, type, r);
      } catch (e) {
        el.querySelector("[data-err]").textContent = e.message;
        go.textContent = real ? "Write to the real PLC" : "Write"; update();
      }
    };
  }

  function done(symbol, type, r) {
    const s = PK.sheet(`<span class="eyebrow">${r.target === "real" ? "Real PLC" : "PLCSIM"} · changed</span>
      <h2 class="mono" style="font-size:22px;word-break:break-all">${esc(symbol)}</h2>
      <p style="margin:0;font-size:20px"><span class="mono">${esc(r.before)}</span> → <b class="mono">${esc(r.written)}</b></p>
      ${r.held ? `<div class="notice green">${icon("check")}<div>The PLC holds the new value${r.after != null ? ` (read back: ${esc(r.after)})` : ""}.</div></div>`
        : `<div class="notice gold">${icon("warn")}<div>${esc(r.note || "The value did not stay.")}</div></div>`}
      <div class="row end"><button class="btn gray" data-back>Put back ${esc(r.before)}</button><button class="btn" data-close>Done</button></div>`);
    s.el.querySelector("[data-back]").onclick = () => { s.close(); open(symbol, type, r.after ?? r.written, r.before); };
    toast(`${symbol}: ${r.before} → ${r.written}`);
    loadLog(true);
  }

  /** The change log for the Tags page (administrators). */
  let log = null, logAt = 0;
  async function loadLog(force) {
    if (!isAdmin() || (!force && Date.now() - logAt < 10000)) return;
    logAt = Date.now();
    try { log = await api("GET", "/api/admin/write/log?max=30"); PK.draw(); } catch { /* next time */ }
  }
  function logHtml() {
    if (!isAdmin()) return "";
    loadLog();
    if (!log || !log.length) return `<p class="faint small">Values changed from PLC-Ki are listed here. Administrators can change a value with ${icon("pen")} next to it.</p>`;
    return `<div class="card"><details class="more" style="border:0;margin:0;padding:0"><summary>Changes made from PLC-Ki (${log.length}${log.length >= 30 ? "+" : ""})</summary>
      <table class="tbl" style="margin-top:8px"><tr><th>When</th><th>Who</th><th>PLC</th><th>Value</th><th>Change</th><th>Why</th></tr>
      ${log.map((e) => `<tr><td class="small" style="white-space:nowrap">${esc(PK.date(e.time))}</td><td>${esc(e.user)}</td>
        <td><span class="pill ${e.target === "real" ? "bad" : "ok"}" style="font-size:12px">${e.target === "real" ? "Real" : e.target === "plcsim" ? "PLCSIM" : "–"} ${esc(e.plc)}</span></td>
        <td class="mono small" style="word-break:break-all">${esc(e.symbol)}</td>
        <td class="small">${e.ok ? `${esc(e.before)} → <b>${esc(e.value)}</b>${e.held === false ? ` <span class="faint">(set back to ${esc(e.after)})</span>` : ""}` : `<span style="color:var(--fault-ink)">Refused: ${esc(e.error)}</span>`}</td>
        <td class="muted small">${esc(e.reason || "")}</td></tr>`).join("")}
      </table></details></div>`;
  }

  window.PKWrite = { button, wire, logHtml, refreshLog: () => loadLog(true) };
})();
