// Reports page: the shift report with OEE, a summary for maintenance (down time, MTTR, MTBF, every shift), and the report settings
// (shifts, part counters, ideal cycle time). Everything comes from what PLC-Ki records while it watches the
// machine (/api/reports/...). Read-only towards the PLC.
"use strict";

(() => {
  const { esc, icon, api, toast } = PK;
  const r = { cfg: null, shift: null, faults: null, err: null, days: 7, loading: false, draft: null, saving: false,
    log: null,     // the shift log: { key, list (newest first), err, f (filter), all (show every line) }
    ho: null };    // the shift's handover note: { key, data: { note, canWrite, maxLength }, err, draft, saving }

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
  const STATE = PK.ui.states;
  /** "2026-11-10 06:00": a local time as Ki's tools take it */
  const local = (t) => { const d = new Date(t); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")} ${hm(d)}`; };
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
    if (r.shift) { loadLog(r.shift); loadHandover(r.shift); }
  }
  // The shift's handover note (/api/reports/handover). A draft being typed is kept through the 30 s refresh.
  async function loadHandover(x) {
    const key = x.day + "/" + x.shiftIndex;
    const same = r.ho && r.ho.key === key;
    const ho = same ? r.ho : { key, data: null, err: null, draft: null, saving: false };
    try { ho.data = await api("GET", `/api/reports/handover?day=${x.day}&shift=${x.shiftIndex}`); ho.err = null; }
    catch (e) { ho.err = e.message; }
    r.ho = ho;
    PK.draw();
  }
  async function saveHandover() {
    const ho = r.ho;
    if (!ho || ho.draft == null) return;
    const [day, shift] = ho.key.split("/");
    ho.saving = true; PK.draw();
    try {
      ho.data = await api("PUT", "/api/reports/handover", { day, shift: +shift, text: ho.draft });
      ho.draft = null;
      PK.dirty(null);
      toast(ho.data.note ? "Saved. The next shift sees it when they open PLC-Ki." : "Removed the handover note.");
      if (PK.loadHandover) PK.loadHandover();
    } catch (e) { toast(e.message, "error"); }
    ho.saving = false; PK.draw();
  }
  // What happened in the shift, from the event day files (/api/events): every step, alarm, halt, value changed
  const LOG_MAX = 5000;
  async function loadLog(x) {
    const key = x.from + "|" + x.to;
    const same = r.log && r.log.key === key;
    const keep = same ? r.log : { key, list: null, err: null, f: (r.log && r.log.f) || "all", all: false };
    if (!same) r.markSel = -1;
    // Two lists: steps and outputs are many, so they are capped on their own and never push an alarm out
    const q = (kinds) => api("GET", `/api/events?from=${encodeURIComponent(x.from)}&to=${encodeURIComponent(x.to)}&max=${LOG_MAX}&kind=${kinds}`);
    try {
      const [rare, busy] = await Promise.all([q("alarm,halt,blocked,state,cpu,link,write,monitor"), q("step,output")]);
      keep.busyCut = busy.length >= LOG_MAX;
      keep.list = rare.concat(busy).sort((a, b) => new Date(b.time) - new Date(a.time));
      keep.err = null;
    } catch (e) { keep.err = e.message; }
    r.log = keep;
    PK.draw();
  }
  async function loadFaults() {
    r.loading = true; PK.draw();
    try { r.faults = await api("GET", "/api/reports/faults?days=" + r.days); r.err = null; } catch (e) { r.err = e.message; }
    r.loading = false; PK.draw();
  }
  // The old Fault summary address (#/reports/faults) leads to History › Ranking (core.js), where its alarm list is now
  function tab(params) { return ["shift", "summary", "settings"].includes(params[0]) ? params[0] : "shift"; }
  async function load(params) {
    if (!r.cfg) await loadCfg();
    const t = tab(params);
    if (t === "shift") await loadShift(params[1], +(params[2] || 0));
    else if (t === "summary") await loadFaults();
    else { r.draft = r.cfg ? JSON.parse(JSON.stringify(r.cfg.settings)) : null; PK.draw(); }
  }
  // A shift that is still running is refreshed every 30 s; the summary every minute
  let timer = null;
  function startTimer() {
    stopTimer();
    timer = setInterval(() => {
      if (document.hidden || !PK.current || PK.current.name !== "reports") return;
      const t = tab(PK.current.params);
      if (t === "shift" && r.shift && r.shift.inProgress) loadShift(r.shift.day, r.shift.shiftIndex);
      else if (t === "summary" && Date.now() % 60000 < 30000) loadFaults();
    }, 30000);
  }
  function stopTimer() { if (timer) clearInterval(timer); timer = null; }

  // ---- pieces ----
  function tabs(t) {
    const admin = PK.state.user && PK.state.user.role === "admin";
    return PK.ui.tabs("Reports", [["shift", "Shift report"], ["summary", "Summary"], ["settings", "Report setup"]].map(([id, label]) => ({ label, on: t === id, href: "#/reports/" + id })));
  }

  // One report tile: the number, what it is, how it was worked out
  const kpi = (value, label, note, state) => ({ html: value, label, note, state });
  const kpiRow = (tiles) => PK.ui.tiles(tiles, { boxed: true, cls: "rp-kpis" });

  function legend(t) {
    const m = { running: t.running, idle: t.idle, down: t.down, nolink: t.noLink, off: t.off };
    return `<div class="legend">${Object.entries(STATE).filter(([k]) => m[k] > 0.05 || k === "running" || k === "down")
      .map(([k, [name, col]]) => `<span><span class="sw" style="background:${col}"></span>${name} ${mins(m[k])}</span>`).join("")}</div>`;
  }

  function lossTable(losses, total) {
    if (!losses.length) return `<p class="m-0 muted">No stops or waiting time recorded.</p>`;
    const max = Math.max(...losses.map((l) => l.minutes), 0.01);
    return `<table class="tbl rp-loss"><tr><th>Why it was not running</th><th class="num">Times</th><th class="num">Time</th></tr>
      ${losses.map((l) => `<tr><td><span class="sw" style="background:${(STATE[l.state] || [])[1]}"></span>${l.alarm ? `<a href="#/alarms/${encodeURIComponent(l.alarm)}">${esc(l.why)}</a>` : esc(l.why || STATE[l.state][0])}
        <div class="rp-meter"><span style="width:${(l.minutes / max * 100).toFixed(1)}%;background:${(STATE[l.state] || [])[1]}"></span></div></td>
        <td class="num">${l.times}</td><td class="num">${mins(l.minutes)}${total ? `<div class="faint">${Math.round(l.minutes / total * 100)} %</div>` : ""}</td></tr>`).join("")}</table>`;
  }

  function faultTable(faults, withFix) {
    if (!faults.length) return `<p class="m-0 muted">No alarms in this time.</p>`;
    return `<table class="tbl rp-faults"><tr><th>Alarm</th><th class="num">Times</th><th class="num">Total</th><th class="num">Longest</th></tr>
      ${faults.map((f) => `<tr><td><a href="#/alarms/${encodeURIComponent(f.id)}"><b>${esc(f.id)}</b></a> ${esc(f.text)}
          ${f.stillOn ? `<span class="pill bad"><span class="dot"></span>on now</span>` : ""}
          <div class="faint">${[f.severity === "FAULT" ? "Fault" : f.severity === "WARNING" ? "Warning" : f.severity, f.location, "last " + PK.when(f.last)].filter(Boolean).map(esc).join(" · ")}</div>
          ${withFix && (f.causes.length || f.fix.length) ? `<details class="more rp-fix"><summary>Likely causes and fix</summary>
            ${f.causes.length ? `<p class="mt-2 mb-1 small"><b>Likely causes</b></p><ul class="plainlist small">${f.causes.map((c) => `<li>${esc(c)}</li>`).join("")}</ul>` : ""}
            ${f.fix.length ? `<p class="mt-2 mb-1 small"><b>Fix</b></p><ol class="plainlist small">${f.fix.map((c) => `<li>${esc(c)}</li>`).join("")}</ol>` : ""}</details>` : ""}</td>
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
      <a class="btn gray small" href="/api/reports/shift.csv?day=${x.day}&shift=${x.shiftIndex}" download>${icon("download")}<span>Download for Excel</span></a>
      <button class="btn gray small" type="button" data-print>${icon("file")}<span>Print or PDF</span></button>
    </div>`;
  }

  // The shift log's filters: which kinds each one shows (null: all)
  const LOG_F = [["all", "Everything", null], ["alarms", "Alarms and stops", ["alarm", "halt", "blocked", "state", "cpu", "link", "monitor"]],
    ["steps", "Steps and outputs", ["step", "output"]], ["changes", "Values changed", ["write"]]];
  const logShown = () => PK.screenSize() === "phone" ? 30 : 40;
  /** The time asked for in the address (#/reports/shift/day/index/@ms: a mark clicked on Plant), or null. */
  const logTarget = () => { const p = (PK.current && PK.current.params) || []; return p[3] && p[3][0] === "@" ? +p[3].slice(1) : null; };
  function shiftLog(x) {
    const L = r.log && r.log.key === x.from + "|" + x.to ? r.log : null;
    const h = `<h3 id="shift-log">What happened</h3>`;
    if (!L || (!L.list && !L.err)) return `<div class="card">${h}${PK.ui.loading("list", { rows: 5, label: "Loading the shift log" })}</div>`;
    if (!L.list) return `<div class="card">${h}${PK.ui.error(L.err)}</div>`;
    const [, , kinds] = LOG_F.find(([k]) => k === L.f) || LOG_F[0];
    const count = (ks) => ks ? L.list.filter((e) => ks.includes(e.kind)).length : L.list.length;
    const pick = PK.ui.choice("Show", LOG_F.map(([k, label, ks]) => ({ label: `${label}&nbsp;<span class="faint">${count(ks)}</span>`, on: L.f === k, data: { "data-logf": k } })));
    const csv = `/api/events.csv?from=${encodeURIComponent(x.from)}&to=${encodeURIComponent(x.to)}${kinds ? "&kind=" + kinds.join(",") : ""}`;
    const bar = PK.ui.toolbar({ left: pick, right: `<a class="btn gray small no-print" href="${csv}" download>${icon("download")}<span>Download for Excel</span></a>` });
    const list = kinds ? L.list.filter((e) => kinds.includes(e.kind)) : L.list;
    const hit = logTarget();
    if (hit && L.hitFor !== hit) { L.hitFor = hit; L.hitDone = false; L.f = "all"; L.all = true; }   // the line asked for must be in the list
    const shown = L.all ? list : list.slice(0, logShown());
    const body = !list.length
      ? PK.ui.empty({ icon: "clock", title: L.list.length ? "Nothing of this kind in this shift." : "Nothing recorded in this shift.",
        text: L.list.length ? "" : "PLC-Ki records what happens while it runs and is connected to the PLC." })
      : PK.ui.events(shown, { cls: "rp-log", hit })
        + (list.length > shown.length ? `<div class="mt-2 row no-print"><button class="btn gray small" type="button" data-logall>Show all ${list.length}</button></div>` : "")
        + (L.busyCut && kinds !== LOG_F[1][2] ? `<p class="mt-2 mb-0 faint small">Steps and outputs: the latest ${LOG_MAX.toLocaleString()}. Download for Excel has every line.</p>` : "");
    return `<div class="card">${h}<p class="mt-0 muted small">Newest first. Steps, alarms, halts, values changed from PLC-Ki: everything PLC-Ki saw in this shift.</p>${bar}${body}</div>`;
  }

  /** The shift's handover note: written during this shift or the next one by anyone logged in, then read-only. */
  function handoverCard(x) {
    const ho = r.ho && r.ho.key === x.day + "/" + x.shiftIndex ? r.ho : null;
    const h = `<h3 class="m-0">Handover note</h3>`;
    if (!ho || (!ho.data && !ho.err)) return `<div class="card stack rp-ho">${h}${PK.ui.loading("lines")}</div>`;
    if (!ho.data) return `<div class="card stack rp-ho">${h}${PK.ui.error(ho.err)}</div>`;
    const { note, canWrite, maxLength } = ho.data;
    const hmOf = (t) => hm(t) + (new Date(t).toDateString() !== new Date(x.from).toDateString() ? ` (${esc(dayName(String(t).slice(0, 10)))})` : "");
    const who = note ? `<span class="faint small">${esc(note.by)}, ${hmOf(note.at)}${note.editedBy ? ` · changed by ${esc(note.editedBy)}, ${hmOf(note.editedAt)}` : ""}</span>` : "";
    const head = `<div class="row">${h}<span class="spacer"></span>${who}</div>`;
    if (canWrite && PK.state.user) {
      const text = ho.draft != null ? ho.draft : note ? note.text : "";
      return `<div class="card stack rp-ho no-print">${head}
        <label class="field m-0"><span class="sr">Handover note for the next shift</span><textarea id="ho-text" rows="4" maxlength="${maxLength}"
          placeholder="What the next shift should know: what was done, what is still open, what to watch.">${esc(text)}</textarea></label>
        <div class="row"><span class="faint small">The next shift sees it on every page when they open PLC-Ki. It can be changed until the next shift ends.</span><span class="spacer"></span>
          <button class="btn small" type="button" data-ho-save ${ho.saving || ho.draft == null ? "disabled" : ""}>${ho.saving ? "Saving…" : "Save note"}</button></div></div>`
        + (note ? `<div class="card stack rp-ho print-only">${head}<p class="ho-note">${esc(note.text)}</p></div>` : "");
    }
    const body = note ? `<p class="ho-note">${esc(note.text)}</p>`
      : `<p class="m-0 muted">${canWrite ? "No handover note yet." : "No handover note was left for this shift."}</p>`;
    const login = canWrite && !PK.state.user ? `<p class="m-0 small no-print"><a href="/login.html">Log in</a> to write the handover note.</p>` : "";
    return `<div class="card stack rp-ho">${head}${body}${login}</div>`;
  }

  function shiftPage() {
    const x = r.shift;
    if (!x) return PK.sec("tight rp", r.err ? PK.ui.error(r.err) : PK.ui.loading("tiles", { label: "Making the report" }));
    const t = x.totals, o = x.oee, p = x.parts, m = x.maint;
    const watched = t.running + t.idle + t.down;
    const title = `${esc(x.shift)} shift, ${esc(dayName(x.day))}`;
    const status = x.inProgress ? `<span class="pill info"><span class="dot"></span>Running now · so far</span>` : `<span class="pill">Finished</span>`;
    const head = `<div class="rp-head"><div><div class="eyebrow">${status}<span class="faint">${hm(x.from)}–${hm(x.to)} · ${esc(x.machine)}</span></div><h2 class="m-0">${title}</h2></div></div>`;

    const empty = watched < 0.5;
    const kpis = empty ? `<div class="notice gold">${icon("info")}<div>PLC-Ki has no values for this shift${t.noLink > 0.5 ? " (no PLC connection)" : ""}. It records while it runs and is connected to the PLC.</div></div>`
      : kpiRow([
        kpi(pct(o.value), "OEE", "Availability × Performance × Quality", oeeCls(o.value)),
        kpi(pct(o.availability), "Availability", `Ran ${mins(o.runMinutes)} of ${mins(o.plannedMinutes)} planned`),
        kpi(pct(o.performance), "Performance", p.idealCycleSeconds ? `Ideal cycle ${p.idealCycleSeconds} s${p.idealEstimated ? " (estimated)" : ""}` : "Needs an ideal cycle time"),
        kpi(pct(o.quality), "Quality", p.badKnown ? `${p.bad} reject${p.bad === 1 ? "" : "s"}` : "No reject counter set"),
      ]);

    const L = r.log && r.log.key === x.from + "|" + x.to && r.log.list ? r.log.list : [];
    const marks = L.filter((e) => e.mark).reverse();
    const time = `<div class="card"><h3>Where the time went</h3>${PK.ui.timeline({ spans: x.timeline, from: x.from, to: x.to, marks, sel: r.markSel ?? -1, label: "Machine state over the shift" })}${marks[r.markSel] ? `<p class="tl-sel"><time>${esc(PK.clock(marks[r.markSel].time))}</time> ${esc(marks[r.markSel].text)}</p>` : ""}${legend(t)}</div>`;
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
    const faults = `<div class="card"><h3>Alarms in this shift</h3>${faultTable(x.faults, true)}<p class="mt-3 mb-0 faint small no-print">Every past alarm, with what fixed it: <a href="#/history">History</a>.</p></div>`;
    const ask = PK.state.user ? `<div class="mt-2 row no-print"><button class="btn b small" type="button" data-rpask>${icon("chat")}<span>Ask ${PK.AI} for a handover note</span></button></div>` : "";
    return PK.sec("tight rp", shiftPicker(x) + head + handoverCard(x) + `<div class="mt-4"></div>` + kpis + `<div class="rp-grid">${time}<div class="grid two rp-two">${parts}${maint}</div><div class="grid two rp-two">${losses}${faults}</div>${shiftLog(x)}</div>` + notes(x.notes) + ask);
  }

  // ---- summary over a time window: down time, MTTR and MTBF, where the time went, every shift's OEE ----
  function faultsPage() {
    const x = r.faults;
    const range = PK.ui.toolbar({ cls: "no-print",
      left: PK.ui.choice("Time range", [[1, "24 h"], [7, "7 days"], [30, "30 days"], [365, "1 year"]].map(([d, l]) => ({ label: l, on: r.days === d, data: { "data-days": d } }))),
      right: `<a class="btn gray small" href="/api/reports/faults.csv?days=${r.days}" download>${icon("download")}<span>Download for Excel</span></a>
      <button class="btn gray small" type="button" data-print>${icon("file")}<span>Print or PDF</span></button>` });
    if (!x) return PK.sec("tight rp", range + (r.err ? PK.ui.error(r.err) : PK.ui.loading("tiles", { label: "Making the summary" })));
    const t = x.totals, m = x.maint;
    const kpis = kpiRow([
      kpi(mins(t.down), "Down by faults", `${m.stops} stop${m.stops === 1 ? "" : "s"}`, t.down > 0 ? "bad" : "ok"),
      kpi(mins(m.mttrMinutes), "Average repair (MTTR)", "Down time per stop"),
      kpi(mins(m.mtbfMinutes), "Running between faults (MTBF)", "Running time per stop"),
      kpi(String(x.faults.length), "Different alarms", x.faults.filter((f) => f.count >= 3).length + " repeat 3 times or more"),
    ]);
    const shifts = x.shifts.length ? `<table class="tbl rp-shifts"><tr><th>Shift</th><th class="num">OEE</th><th class="num">Avail.</th><th class="num">Parts</th><th class="num">Down</th><th>Biggest loss</th></tr>
      ${x.shifts.map((s) => `<tr><td><a href="#/reports/shift/${s.day}/${s.shiftIndex}">${esc(dayName(s.day))}, ${esc(s.shift)}</a>${s.inProgress ? ` <span class="tag blue">now</span>` : ""}</td>
        <td class="num"><b class="rp-oee ${oeeCls(s.oee)}">${pct(s.oee)}</b></td><td class="num">${pct(s.availability)}</td><td class="num">${s.parts}</td><td class="num">${mins(s.downMinutes)}</td>
        <td class="faint">${esc(s.topLoss || "")}</td></tr>`).join("")}</table>` : `<p class="m-0 muted">No shift recorded yet.</p>`;
    return PK.sec("tight rp", range + `<div class="rp-head"><div><div class="eyebrow"><span class="faint">${esc(PK.date(x.from, false))} to now · ${esc(x.machine)}</span></div><h2 class="m-0">Summary for maintenance</h2></div></div>`
      + kpis + `<div class="rp-grid"><a class="card plink no-print" href="#/history/ranking/downtime"><span class="ico">${icon("clock")}</span><span><b>Which alarms cost the most time</b><span class="muted small">${x.faults.length ? `${x.faults.length} different alarm${x.faults.length === 1 ? "" : "s"} in this time. History ranks them by how long they were on, with what fixed them.` : "History ranks past alarms by how long they were on, with what fixed them."}</span></span><span class="chev">›</span></a>
      <div class="card"><h3>Why it was not running</h3>${lossTable(x.losses, t.idle + t.down + t.noLink)}</div>
      <div class="card"><h3>Shifts</h3><div class="tbl-wrap">${shifts}</div></div></div>` + notes(x.notes));
  }

  // ---- settings ----
  function settingsPage() {
    const c = r.cfg;
    if (!c) return PK.sec("tight rp", r.err ? PK.ui.error(r.err) : PK.ui.loading("tiles"));
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
      <div class="card stack"><h3 class="m-0">Shifts</h3>
        <p class="m-0 muted small">An end before the start runs past midnight. Planned stops (breaks, cleaning) are not counted as lost time.</p>
        <div class="rp-shtbl"><table class="tbl"><tr><th>Name</th><th>Start</th><th>End</th><th>Planned stops, min</th><th></th></tr>${rows}</table></div>
        ${admin ? `<div class="row"><button class="btn gray small" type="button" data-shadd>${icon("plus")}<span>Add a shift</span></button></div>` : ""}
      </div>
      <div class="card stack"><h3 class="m-0">Parts and cycles</h3>
        <label class="field">Good part counter<select data-f="goodCountTag" ${dis}>${counterOpts(s.goodCountTag, "good")}</select>
          <span class="hint">A tag that goes up by one per part. Without one, every finished cycle counts as one part.${sugg.length ? " ★ looks like a counter." : ""}</span></label>
        <label class="field">Reject counter<select data-f="badCountTag" ${dis}>${counterOpts(s.badCountTag, "bad")}</select>
          <span class="hint">Without one, quality counts as 100 % and the report says so.</span></label>
        <label class="field">Cycle sequence<select data-f="cycleSequence" ${dis}>${seqOpts}</select>
          <span class="hint">One cycle is finished when this sequence comes back to its first step.</span></label>
        <label class="field">Ideal cycle time, seconds<input type="number" min="0" step="0.1" data-f="idealCycleSeconds" value="${s.idealCycleSeconds ?? ""}" placeholder="Empty: the fastest cycles PLC-Ki has seen" ${dis}>
          <span class="hint">The fastest the machine can make one part, for Performance.</span></label>
      </div></div>
      <div class="mt-4 card stack"><h3 class="m-0">How PLC-Ki counts</h3>
        <ul class="plainlist small">
          <li><b>Running</b>: a sequence is past its first step and no fault alarm is on. With no sequences in the model, a device switched on.</li>
          <li><b>Down</b>: a fault alarm is on. The reason is the alarm that came first.</li>
          <li><b>Waiting or stopped</b>: no fault, but the sequence waits in its first step or is halted.</li>
          <li><b>No PLC connection</b> and <b>Not watched</b> (PLC-Ki was off) are left out of OEE.</li>
          <li><b>OEE</b> = Availability (running ÷ planned time) × Performance (ideal cycle × parts ÷ running time) × Quality (good ÷ all parts).</li>
        </ul>
        <p class="m-0 faint small">Recorded in ${esc(c.folder)}, one small file per day.</p>
      </div>
      ${admin ? `<div class="mt-4 row end"><button class="btn" type="button" data-rpsave ${r.saving ? "disabled" : ""}>${r.saving ? "Saving…" : "Save"}</button></div>` : ""}`);
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
      const head = PK.hero({ cls: "hero compact", title: "Reports",
        sub: "Shift report with OEE, and a summary for maintenance. Made from what PLC-Ki records while it watches the machine. Past alarms are in History.", extra: tabs(t) });
      const body = t === "shift" ? shiftPage() : t === "summary" ? faultsPage() : settingsPage();
      return head + body;
    },
    after(el, params) {
      el.querySelectorAll("[data-print]").forEach((b) => PK.on(b, "click", () => window.print()));
      const day = el.querySelector("#rp-day"), sh = el.querySelector("#rp-shift");
      const pick = () => day.value && PK.go(`#/reports/shift/${day.value}/${sh.value}`);
      if (day) PK.on(day, "change", pick);
      if (sh) PK.on(sh, "change", pick);
      el.querySelectorAll("[data-days]").forEach((b) => PK.on(b, "click", () => { r.days = +b.dataset.days; loadFaults(); }));
      const hitLi = el.querySelector(".tl-list li.hit");
      if (hitLi && r.log && !r.log.hitDone) {
        r.log.hitDone = true;
        // At once, not smooth: a redraw during a smooth scroll would leave the page somewhere else
        setTimeout(() => { const li = document.querySelector(".tl-list li.hit"); if (li) window.scrollTo({ top: li.getBoundingClientRect().top + scrollY - innerHeight / 2, behavior: "instant" }); }, 0);
      }
      const hoText = el.querySelector("#ho-text"), hoSave = el.querySelector("[data-ho-save]");
      if (hoText) PK.on(hoText, "input", () => {
        r.ho.draft = hoText.value;
        PK.dirty(saveHandover);
        if (hoSave) hoSave.disabled = false;
      });
      if (hoSave) PK.on(hoSave, "click", saveHandover);
      el.querySelectorAll("[data-logf]").forEach((b) => PK.on(b, "click", () => { r.log.f = b.dataset.logf; r.log.all = false; PK.draw(); }));
      const all = el.querySelector("[data-logall]");
      if (all) PK.on(all, "click", () => { r.log.all = true; PK.draw(); });
      el.querySelectorAll("[data-tlmk]").forEach((b) => PK.on(b, "click", () => { const i = +b.dataset.tlmk; r.markSel = r.markSel === i ? -1 : i; PK.draw(); }));
      const ask = el.querySelector("[data-rpask]");
      if (ask) PK.on(ask, "click", async () => {
        const x = r.shift;
        // The note the shift before left: what it asked to watch belongs in this handover too
        const shifts = r.cfg ? r.cfg.settings.shifts : [];
        const [pd, pi] = x.shiftIndex > 0 ? [x.day, x.shiftIndex - 1] : [addDays(x.day, -1), Math.max(0, shifts.length - 1)];
        let prev = null;
        try { prev = (await api("GET", `/api/reports/handover?day=${pd}&shift=${pi}`)).note; } catch { /* asked without it */ }
        const q = `Write a short shift handover for the ${x.shift} shift (${hm(x.from)}–${hm(x.to)}): OEE ${pct(x.oee.value)}, ${x.parts.total} parts, down ${mins(x.totals.down)} in ${x.maint.stops} stops. ` +
          `Biggest losses: ${x.losses.slice(0, 3).map((l) => `${l.why} (${mins(l.minutes)}, ${l.times}x)`).join("; ") || "none"}. ` +
          `Alarms: ${x.faults.slice(0, 5).map((f) => `${f.id} ${f.text} ${f.count}x`).join("; ") || "none"}. ` +
          `Read what happened in this shift with get_recent_events from "${local(x.from)}" to "${local(x.inProgress ? new Date() : x.to)}" (the shift log: steps, alarms, halts, values changed). ` +
          (prev ? `The ${prev.shift} shift before left this handover note: "${prev.text}". Say what of it is still open. ` : "") +
          `Say what the next shift should watch and what maintenance should check.`;
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
