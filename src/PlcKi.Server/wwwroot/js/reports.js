// Reports page: the shift report with OEE, the fault summary for maintenance, and the report settings
// (shifts, part counters, ideal cycle time). Everything comes from what PLC-Ki records while it watches the
// machine (/api/reports/...). Read-only towards the PLC.
"use strict";

(() => {
  const { esc, icon, api, toast } = PK;
  const r = { cfg: null, shift: null, faults: null, err: null, days: 7, loading: false, draft: null, saving: false };

  // ---- formatting ----
  const pct = (x) => (x == null ? "—" : `${(x * 100).toFixed(x >= 0.995 || x === 0 ? 0 : 1)} %`);
  const mins = (m) => {
    if (m == null) return "—";
    if (m < 1) return `${Math.round(m * 60)} s`;
    if (m < 60) return `${Math.round(m)} min`;
    const h = Math.floor(m / 60), rest = Math.round(m - h * 60);
    return rest ? `${h} h ${rest} min` : `${h} h`;
  };
  const hm = (t) => { const d = new Date(t); return `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}`; };
  const dayName = (day) => {
    const d = new Date(day + "T12:00:00");
    const today = new Date(); today.setHours(12, 0, 0, 0);
    const diff = Math.round((today - d) / 86400000);
    return diff === 0 ? "Today" : diff === 1 ? "Yesterday" : d.toLocaleDateString(undefined, { weekday: "short", day: "numeric", month: "short" });
  };
  const oeeCls = (x) => (x == null ? "" : x >= 0.85 ? "ok" : x >= 0.6 ? "warn" : "bad");
  const STATE = {
    running: ["Running", "var(--ok)"], idle: ["Waiting or stopped", "var(--steel)"], down: ["Down (fault)", "var(--fault)"],
    nolink: ["No PLC connection", "var(--warn)"], off: ["Not watched", "var(--line-strong)"],
  };
  const addDays = (day, n) => { const d = new Date(day + "T12:00:00"); d.setDate(d.getDate() + n); return d.toISOString().slice(0, 10); };

  // ---- loading ----
  async function loadCfg() {
    try { r.cfg = await api("GET", "/api/reports/settings"); r.err = null; } catch (e) { r.err = e.message; }
  }
  async function loadShift(day, index) {
    r.loading = true; PK.draw();
    try {
      const q = day ? `?day=${encodeURIComponent(day)}&shift=${index || 0}` : "";
      r.shift = await api("GET", "/api/reports/shift" + q); r.err = null;
    } catch (e) { r.err = e.message; }
    r.loading = false; PK.draw();
  }
  async function loadFaults() {
    r.loading = true; PK.draw();
    try { r.faults = await api("GET", "/api/reports/faults?days=" + r.days); r.err = null; } catch (e) { r.err = e.message; }
    r.loading = false; PK.draw();
  }
  function tab(params) { return ["shift", "faults", "settings"].includes(params[0]) ? params[0] : "shift"; }
  async function load(params) {
    if (!r.cfg) await loadCfg();
    const t = tab(params);
    if (t === "shift") await loadShift(params[1], +(params[2] || 0));
    else if (t === "faults") await loadFaults();
    else { r.draft = r.cfg ? JSON.parse(JSON.stringify(r.cfg.settings)) : null; PK.draw(); }
  }
  // A shift that is still running is refreshed every 30 s; the fault summary every minute
  let timer = null;
  function startTimer() {
    stopTimer();
    timer = setInterval(() => {
      if (document.hidden || !PK.current || PK.current.name !== "reports") return;
      const t = tab(PK.current.params);
      if (t === "shift" && r.shift && r.shift.inProgress) loadShift(r.shift.day, r.shift.shiftIndex);
      else if (t === "faults" && Date.now() % 60000 < 30000) loadFaults();
    }, 30000);
  }
  function stopTimer() { if (timer) clearInterval(timer); timer = null; }

  // ---- pieces ----
  function tabs(t) {
    const admin = PK.state.user && PK.state.user.role === "admin";
    const b = (id, label) => `<a class="${t === id ? "on" : ""}" href="#/reports/${id}" role="tab" aria-selected="${t === id}">${label}</a>`;
    return `<div class="segmented rp-tabs" role="tablist">${b("shift", "Shift report")}${b("faults", "Fault summary")}${b("settings", admin ? "Settings" : "How it counts")}</div>`;
  }

  function kpi(value, label, sub, cls) {
    return `<div class="k ${cls || ""}"><b>${value}</b><span>${esc(label)}</span>${sub ? `<span class="rp-sub">${esc(sub)}</span>` : ""}</div>`;
  }

  /** The shift as a coloured bar: running, waiting, down, no link, not watched, with hour marks. */
  function timelineBar(spans, from, to) {
    const a = new Date(from).getTime(), b = new Date(to).getTime(), len = Math.max(1, b - a);
    const segs = spans.map((s) => {
      const x = (new Date(s.from).getTime() - a) / len * 100, w = (new Date(s.to).getTime() - new Date(s.from).getTime()) / len * 100;
      const [name, col] = STATE[s.state] || [s.state, "var(--muted)"];
      const tip = `${hm(s.from)}–${hm(s.to)} ${name}${s.why ? ": " + s.why : ""}`;
      return `<span class="rp-seg rp-${s.state}" style="left:${x.toFixed(3)}%;width:${Math.max(w, 0.15).toFixed(3)}%;background:${col}" title="${esc(tip)}"></span>`;
    }).join("");
    const ticks = [];
    const first = new Date(a); first.setMinutes(0, 0, 0);
    const hours = len / 3600000;
    const step = hours > 12 ? 4 : hours > 6 ? 2 : 1;
    for (let t = first.getTime() + 3600000; t < b; t += 3600000) {
      if (new Date(t).getHours() % step) continue;
      ticks.push(`<span class="rp-tick" style="left:${((t - a) / len * 100).toFixed(2)}%">${hm(t)}</span>`);
    }
    return `<div class="rp-bar" role="img" aria-label="Machine state over the shift">${segs}</div><div class="rp-ticks"><span>${hm(a)}</span>${ticks.join("")}<span class="rp-end">${hm(b)}</span></div>`;
  }

  function legend(t) {
    const m = { running: t.running, idle: t.idle, down: t.down, nolink: t.noLink, off: t.off };
    return `<div class="legend">${Object.entries(STATE).filter(([k]) => m[k] > 0.05 || k === "running" || k === "down")
      .map(([k, [name, col]]) => `<span><span class="sw" style="background:${col}"></span>${name} ${mins(m[k])}</span>`).join("")}</div>`;
  }

  function lossTable(losses, total) {
    if (!losses.length) return `<p class="muted" style="margin:0">No stops or waiting time recorded.</p>`;
    const max = Math.max(...losses.map((l) => l.minutes), 0.01);
    return `<table class="tbl rp-loss"><tr><th>Why it was not running</th><th class="num">Times</th><th class="num">Time</th></tr>
      ${losses.map((l) => `<tr><td><span class="sw" style="background:${(STATE[l.state] || [])[1]}"></span>${l.alarm ? `<a href="#/alarms/${encodeURIComponent(l.alarm)}">${esc(l.why)}</a>` : esc(l.why || STATE[l.state][0])}
        <div class="rp-meter"><span style="width:${(l.minutes / max * 100).toFixed(1)}%;background:${(STATE[l.state] || [])[1]}"></span></div></td>
        <td class="num">${l.times}</td><td class="num">${mins(l.minutes)}${total ? `<div class="faint">${Math.round(l.minutes / total * 100)} %</div>` : ""}</td></tr>`).join("")}</table>`;
  }

  function faultTable(faults, withFix) {
    if (!faults.length) return `<p class="muted" style="margin:0">No alarms in this time.</p>`;
    return `<table class="tbl rp-faults"><tr><th>Alarm</th><th class="num">Times</th><th class="num">Total</th><th class="num">Longest</th></tr>
      ${faults.map((f) => `<tr><td><a href="#/alarms/${encodeURIComponent(f.id)}"><b>${esc(f.id)}</b></a> ${esc(f.text)}
          ${f.stillOn ? `<span class="pill bad"><span class="dot"></span>on now</span>` : ""}
          <div class="faint">${[f.severity === "FAULT" ? "Fault" : f.severity === "WARNING" ? "Warning" : f.severity, f.location, "last " + PK.when(f.last)].filter(Boolean).map(esc).join(" · ")}</div>
          ${withFix && (f.causes.length || f.fix.length) ? `<details class="more rp-fix"><summary>Likely causes and fix</summary>
            ${f.causes.length ? `<p class="small" style="margin:8px 0 4px"><b>Likely causes</b></p><ul class="plainlist small">${f.causes.map((c) => `<li>${esc(c)}</li>`).join("")}</ul>` : ""}
            ${f.fix.length ? `<p class="small" style="margin:8px 0 4px"><b>Fix</b></p><ol class="plainlist small">${f.fix.map((c) => `<li>${esc(c)}</li>`).join("")}</ol>` : ""}</details>` : ""}</td>
        <td class="num">${f.count}${f.count >= 3 ? `<div class="faint rp-rep">repeats</div>` : ""}</td><td class="num">${mins(f.minutes)}</td><td class="num">${mins(f.longestMinutes)}</td></tr>`).join("")}</table>`;
  }

  function notes(list) {
    return list && list.length ? `<ul class="plainlist faint rp-notes">${list.map((n) => `<li>${esc(n)}</li>`).join("")}</ul>` : "";
  }

  // ---- shift report ----
  function shiftPicker(x) {
    const shifts = r.cfg ? r.cfg.settings.shifts : [];
    const prev = x.shiftIndex > 0 ? [x.day, x.shiftIndex - 1] : [addDays(x.day, -1), shifts.length - 1];
    const next = x.shiftIndex < shifts.length - 1 ? [x.day, x.shiftIndex + 1] : [addDays(x.day, 1), 0];
    const nextStarted = !x.inProgress && new Date(x.to) <= new Date();
    const opts = shifts.map((s, i) => `<option value="${i}" ${i === x.shiftIndex ? "selected" : ""}>${esc(s.name)} ${esc(s.start)}–${esc(s.end)}</option>`).join("");
    return `<div class="toolbar rp-pick no-print">
      <a class="btn gray small" href="#/reports/shift/${prev[0]}/${prev[1]}" aria-label="Shift before">‹</a>
      <input type="date" id="rp-day" value="${esc(x.day)}" aria-label="Day the shift started">
      <select id="rp-shift" aria-label="Shift">${opts}</select>
      ${nextStarted ? `<a class="btn gray small" href="#/reports/shift/${next[0]}/${next[1]}" aria-label="Next shift">›</a>` : `<span class="btn gray small" aria-disabled="true" style="opacity:.4">›</span>`}
      ${r.cfg && r.cfg.current && !(r.cfg.current.day === x.day && r.cfg.current.index === x.shiftIndex) ? `<a class="btn b small" href="#/reports/shift">This shift</a>` : ""}
      <span class="spacer"></span>
      <a class="btn gray small" href="/api/reports/shift.csv?day=${x.day}&shift=${x.shiftIndex}" download>${icon("download")}<span>Spreadsheet</span></a>
      <button class="btn gray small" type="button" data-print>${icon("file")}<span>Print or PDF</span></button>
    </div>`;
  }

  function shiftPage() {
    const x = r.shift;
    if (!x) return PK.sec("tight rp", `<p class="muted">${r.err ? esc(r.err) : "Making the report…"}</p>`);
    const t = x.totals, o = x.oee, p = x.parts, m = x.maint;
    const watched = t.running + t.idle + t.down;
    const title = `${esc(x.shift)} shift, ${esc(dayName(x.day))}`;
    const status = x.inProgress ? `<span class="pill info"><span class="dot"></span>Running now · so far</span>` : `<span class="pill">Finished</span>`;
    const head = `<div class="rp-head"><div><div class="eyebrow">${status}<span class="faint">${hm(x.from)}–${hm(x.to)} · ${esc(x.machine)}</span></div><h2 style="margin:0">${title}</h2></div></div>`;

    const empty = watched < 0.5;
    const kpis = empty ? `<div class="notice gold">${icon("info")}<div>PLC-Ki has no values for this shift${t.noLink > 0.5 ? " (no PLC connection)" : ""}. It records while it runs and is connected to the PLC.</div></div>`
      : `<div class="kpis rp-kpis">
        ${kpi(pct(o.value), "OEE", "Availability × Performance × Quality", oeeCls(o.value))}
        ${kpi(pct(o.availability), "Availability", `Ran ${mins(o.runMinutes)} of ${mins(o.plannedMinutes)} planned`)}
        ${kpi(pct(o.performance), "Performance", p.idealCycleSeconds ? `Ideal cycle ${p.idealCycleSeconds} s${p.idealEstimated ? " (estimated)" : ""}` : "Needs an ideal cycle time")}
        ${kpi(pct(o.quality), "Quality", p.badKnown ? `${p.bad} reject${p.bad === 1 ? "" : "s"}` : "No reject counter set")}
      </div>`;

    const time = `<div class="card"><h3>Where the time went</h3>${timelineBar(x.timeline, x.from, x.to)}${legend(t)}</div>`;
    const parts = `<div class="card"><h3>Parts</h3><div class="kv">
        <span>Made</span><span>${p.total}</span>
        <span>Good</span><span>${p.good}</span>
        <span>Rejects</span><span>${p.badKnown ? p.bad : "not counted"}</span>
        <span>Counted from</span><span>${p.from === "counter" ? "part counter" : `finished cycles (${p.cycles})`}</span>
        <span>Average cycle</span><span>${p.avgCycleSeconds != null ? p.avgCycleSeconds + " s" : "—"}</span>
        <span>Ideal cycle</span><span>${p.idealCycleSeconds != null ? p.idealCycleSeconds + " s" + (p.idealEstimated ? " (fastest seen)" : "") : "—"}</span></div></div>`;
    const maint = `<div class="card"><h3>For maintenance</h3><div class="kv">
        <span>Stops by a fault</span><span>${m.stops}</span>
        <span>Down time</span><span>${mins(t.down)}</span>
        <span>Average repair (MTTR)</span><span>${mins(m.mttrMinutes)}</span>
        <span>Running between faults (MTBF)</span><span>${mins(m.mtbfMinutes)}</span>
        <span>No PLC connection</span><span>${mins(t.noLink)}</span></div></div>`;
    const losses = `<div class="card"><h3>Biggest losses</h3>${lossTable(x.losses, t.idle + t.down + t.noLink)}</div>`;
    const faults = `<div class="card"><h3>Alarms in this shift</h3>${faultTable(x.faults, true)}</div>`;
    const ask = PK.state.user ? `<div class="row no-print" style="margin-top:6px"><button class="btn b small" type="button" data-rpask>${icon("chat")}<span>Ask ${PK.AI} for a handover note</span></button></div>` : "";
    return PK.sec("tight rp", shiftPicker(x) + head + kpis + `<div class="rp-grid">${time}<div class="grid two rp-two">${parts}${maint}</div><div class="grid two rp-two">${losses}${faults}</div></div>` + notes(x.notes) + ask);
  }

  // ---- fault summary ----
  function faultsPage() {
    const x = r.faults;
    const range = `<div class="toolbar no-print"><div class="segmented" role="group" aria-label="Time range">
      ${[[1, "Today"], [7, "7 days"], [30, "30 days"]].map(([d, l]) => `<button type="button" data-days="${d}" class="${r.days === d ? "on" : ""}">${l}</button>`).join("")}</div>
      <span class="spacer"></span>
      <a class="btn gray small" href="/api/reports/faults.csv?days=${r.days}" download>${icon("download")}<span>Spreadsheet</span></a>
      <button class="btn gray small" type="button" data-print>${icon("file")}<span>Print or PDF</span></button></div>`;
    if (!x) return PK.sec("tight rp", range + `<p class="muted">${r.err ? esc(r.err) : "Making the summary…"}</p>`);
    const t = x.totals, m = x.maint;
    const kpis = `<div class="kpis rp-kpis">
      ${kpi(mins(t.down), "Down by faults", `${m.stops} stop${m.stops === 1 ? "" : "s"}`, t.down > 0 ? "bad" : "ok")}
      ${kpi(mins(m.mttrMinutes), "Average repair (MTTR)", "Down time per stop")}
      ${kpi(mins(m.mtbfMinutes), "Running between faults (MTBF)", "Running time per stop")}
      ${kpi(String(x.faults.length), "Different alarms", x.faults.filter((f) => f.count >= 3).length + " repeat 3 times or more")}
    </div>`;
    const shifts = x.shifts.length ? `<table class="tbl rp-shifts"><tr><th>Shift</th><th class="num">OEE</th><th class="num">Avail.</th><th class="num">Parts</th><th class="num">Down</th><th>Biggest loss</th></tr>
      ${x.shifts.map((s) => `<tr><td><a href="#/reports/shift/${s.day}/${s.shiftIndex}">${esc(dayName(s.day))}, ${esc(s.shift)}</a>${s.inProgress ? ` <span class="tag blue">now</span>` : ""}</td>
        <td class="num"><b class="rp-oee ${oeeCls(s.oee)}">${pct(s.oee)}</b></td><td class="num">${pct(s.availability)}</td><td class="num">${s.parts}</td><td class="num">${mins(s.downMinutes)}</td>
        <td class="faint">${esc(s.topLoss || "")}</td></tr>`).join("")}</table>` : `<p class="muted" style="margin:0">No shift recorded yet.</p>`;
    return PK.sec("tight rp", range + `<div class="rp-head"><div><div class="eyebrow"><span class="faint">${esc(PK.date(x.from, false))} to now · ${esc(x.machine)}</span></div><h2 style="margin:0">Fault summary for maintenance</h2></div></div>`
      + kpis + `<div class="rp-grid"><div class="card"><h3>Alarms, longest down time first</h3>${faultTable(x.faults, true)}</div>
      <div class="card"><h3>Why it was not running</h3>${lossTable(x.losses, t.idle + t.down + t.noLink)}</div>
      <div class="card"><h3>Shifts</h3><div class="tbl-wrap">${shifts}</div></div></div>` + notes(x.notes));
  }

  // ---- settings ----
  function settingsPage() {
    const c = r.cfg;
    if (!c) return PK.sec("tight rp", `<p class="muted">${r.err ? esc(r.err) : "Loading…"}</p>`);
    const admin = PK.state.user && PK.state.user.role === "admin";
    const s = r.draft || c.settings;
    const dis = admin ? "" : "disabled";
    const counterOpts = (sel, kind) => `<option value="">${kind === "good" ? "None: count finished cycles" : "None: quality not counted"}</option>` +
      c.counters.map((t) => `<option value="${esc(t.id)}" ${t.id === sel ? "selected" : ""}>${t.suggest === kind ? "★ " : ""}${esc(t.label)} (${esc(t.symbol)})</option>`).join("");
    const seqOpts = `<option value="">${c.sequences.length ? "The first sequence: " + esc(c.sequences[0].name) : "No sequence in the model"}</option>` +
      c.sequences.map((q) => `<option value="${esc(q.id)}" ${q.id === s.cycleSequence ? "selected" : ""}>${esc(q.name)} (back to step ${q.home ?? "?"} "${esc(q.homeName || "")}")</option>`).join("");
    const rows = s.shifts.map((x, i) => `<tr>
        <td><input type="text" data-sh="${i}" data-f="name" value="${esc(x.name)}" ${dis} aria-label="Shift name"></td>
        <td><input type="time" data-sh="${i}" data-f="start" value="${esc(x.start)}" ${dis} aria-label="Start"></td>
        <td><input type="time" data-sh="${i}" data-f="end" value="${esc(x.end)}" ${dis} aria-label="End"></td>
        <td><input type="number" min="0" max="600" data-sh="${i}" data-f="plannedStopMinutes" value="${x.plannedStopMinutes || 0}" ${dis} aria-label="Planned stops in minutes"></td>
        <td>${admin && s.shifts.length > 1 ? `<button class="btn gray small" type="button" data-shdel="${i}" aria-label="Remove shift">${icon("x")}</button>` : ""}</td></tr>`).join("");
    const sugg = c.counters.filter((t) => t.suggest);
    return PK.sec("tight rp", `<div class="grid two rp-two">
      <div class="card stack"><h3 style="margin:0">Shifts</h3>
        <p class="muted small" style="margin:0">An end before the start runs past midnight. Planned stops (breaks, cleaning) are not counted as lost time.</p>
        <div class="rp-shtbl"><table class="tbl"><tr><th>Name</th><th>Start</th><th>End</th><th>Planned stops, min</th><th></th></tr>${rows}</table></div>
        ${admin ? `<div class="row"><button class="btn gray small" type="button" data-shadd>${icon("plus")}<span>Add a shift</span></button></div>` : ""}
      </div>
      <div class="card stack"><h3 style="margin:0">Parts and cycles</h3>
        <label class="field">Good part counter<select data-f="goodCountTag" ${dis}>${counterOpts(s.goodCountTag, "good")}</select>
          <span class="hint">A tag that goes up by one per part. Without one, every finished cycle counts as one part.${sugg.length ? " ★ looks like a counter." : ""}</span></label>
        <label class="field">Reject counter<select data-f="badCountTag" ${dis}>${counterOpts(s.badCountTag, "bad")}</select>
          <span class="hint">Without one, quality counts as 100 % and the report says so.</span></label>
        <label class="field">Cycle sequence<select data-f="cycleSequence" ${dis}>${seqOpts}</select>
          <span class="hint">One cycle is finished when this sequence comes back to its first step.</span></label>
        <label class="field">Ideal cycle time, seconds<input type="number" min="0" step="0.1" data-f="idealCycleSeconds" value="${s.idealCycleSeconds ?? ""}" placeholder="Empty: the fastest cycles PLC-Ki has seen" ${dis}>
          <span class="hint">The fastest the machine can make one part, for Performance.</span></label>
      </div></div>
      <div class="card stack" style="margin-top:18px"><h3 style="margin:0">How PLC-Ki counts</h3>
        <ul class="plainlist small">
          <li><b>Running</b>: a sequence is past its first step and no fault alarm is on. With no sequences in the model, a device switched on.</li>
          <li><b>Down</b>: a fault alarm is on. The reason is the alarm that came first.</li>
          <li><b>Waiting or stopped</b>: no fault, but the sequence waits in its first step or is halted.</li>
          <li><b>No PLC connection</b> and <b>Not watched</b> (PLC-Ki was off) are left out of OEE.</li>
          <li><b>OEE</b> = Availability (running ÷ planned time) × Performance (ideal cycle × parts ÷ running time) × Quality (good ÷ all parts).</li>
        </ul>
        <p class="faint small" style="margin:0">Recorded in ${esc(c.folder)}, one small file per day.</p>
      </div>
      ${admin ? `<div class="row end" style="margin-top:18px"><button class="btn" type="button" data-rpsave ${r.saving ? "disabled" : ""}>${r.saving ? "Saving…" : "Save"}</button></div>` : ""}`);
  }

  async function save() {
    r.saving = true; PK.draw();
    try {
      const body = JSON.parse(JSON.stringify(r.draft));
      body.idealCycleSeconds = body.idealCycleSeconds === "" || body.idealCycleSeconds == null ? null : +body.idealCycleSeconds;
      r.cfg = await api("PUT", "/api/admin/reports/settings", body);
      r.draft = JSON.parse(JSON.stringify(r.cfg.settings));
      PK.dirty(null);
      toast("Saved. The reports use these settings from now on.");
    } catch (e) { toast(e.message, "error"); }
    r.saving = false; PK.draw();
  }

  PK.view("reports", {
    mount(page, params) { r.shift = null; r.faults = null; load(params); startTimer(); PK.draw(); },
    leave() { stopTimer(); },
    render(params) {
      const t = tab(params);
      const head = PK.hero({ cls: "hero compact", title: "Reports.",
        sub: "Shift report with OEE, and a fault summary for maintenance. Made from what PLC-Ki records while it watches the machine.", extra: tabs(t) });
      const body = t === "shift" ? shiftPage() : t === "faults" ? faultsPage() : settingsPage();
      return head + body;
    },
    after(el, params) {
      el.querySelectorAll("[data-print]").forEach((b) => PK.on(b, "click", () => window.print()));
      const day = el.querySelector("#rp-day"), sh = el.querySelector("#rp-shift");
      const pick = () => day.value && PK.go(`#/reports/shift/${day.value}/${sh.value}`);
      if (day) PK.on(day, "change", pick);
      if (sh) PK.on(sh, "change", pick);
      el.querySelectorAll("[data-days]").forEach((b) => PK.on(b, "click", () => { r.days = +b.dataset.days; loadFaults(); }));
      const ask = el.querySelector("[data-rpask]");
      if (ask) PK.on(ask, "click", () => {
        const x = r.shift;
        const q = `Write a short shift handover for the ${x.shift} shift (${hm(x.from)}–${hm(x.to)}): OEE ${pct(x.oee.value)}, ${x.parts.total} parts, down ${mins(x.totals.down)} in ${x.maint.stops} stops. ` +
          `Biggest losses: ${x.losses.slice(0, 3).map((l) => `${l.why} (${mins(l.minutes)}, ${l.times}x)`).join("; ") || "none"}. ` +
          `Alarms: ${x.faults.slice(0, 5).map((f) => `${f.id} ${f.text} ${f.count}x`).join("; ") || "none"}. Say what the next shift should watch and what maintenance should check.`;
        if (window.PKAsk) window.PKAsk.open(q);
      });
      // Settings: edit a copy; the unsaved-changes bar offers Save
      if (tab(params) === "settings" && r.draft) {
        const mark = () => PK.dirty(save);
        el.querySelectorAll("[data-sh]").forEach((inp) => PK.on(inp, "input", () => {
          const s = r.draft.shifts[+inp.dataset.sh];
          s[inp.dataset.f] = inp.dataset.f === "plannedStopMinutes" ? +inp.value || 0 : inp.value;
          mark();
        }));
        el.querySelectorAll("select[data-f], input[data-f]:not([data-sh])").forEach((inp) => PK.on(inp, "change", () => {
          r.draft[inp.dataset.f] = inp.value || null;
          mark();
        }));
        const add = el.querySelector("[data-shadd]");
        if (add) PK.on(add, "click", () => { r.draft.shifts.push({ name: "Shift " + (r.draft.shifts.length + 1), start: "06:00", end: "14:00", plannedStopMinutes: 0 }); mark(); PK.draw(); });
        el.querySelectorAll("[data-shdel]").forEach((b) => PK.on(b, "click", () => { r.draft.shifts.splice(+b.dataset.shdel, 1); mark(); PK.draw(); }));
        const sv = el.querySelector("[data-rpsave]");
        if (sv) PK.on(sv, "click", save);
      }
    },
  });
})();
