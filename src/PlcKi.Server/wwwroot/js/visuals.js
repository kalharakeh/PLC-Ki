// Pictures for the live pages: value trends, the step chart (like the GRAPH editor), the step timeline and the
// plant map. All of it is drawn from the machine model, /api/status and /api/trend (the values PLC-Ki keeps in
// memory), so it works for any imported program. Plain SVG and CSS, no chart library: plant PCs may be offline.
"use strict";

(() => {
  const { esc, icon, state, sec, hero, cond } = PK;

  // ---------- small helpers ----------
  /** "45 s", "2 min 10 s", "1 h 5 min" */
  function dur(ms) {
    const s = Math.max(0, Math.round(ms / 1000));
    if (s < 60) return s + " s";
    const m = Math.floor(s / 60);
    if (m < 60) return m + " min" + (s % 60 ? " " + (s % 60) + " s" : "");
    return Math.floor(m / 60) + " h" + (m % 60 ? " " + (m % 60) + " min" : "");
  }
  const hhmmss = (ms) => PK.clock(ms);
  /** A time on a chart: the clock for a few hours, with the weekday and date once the chart reaches over a day. */
  const elapsed = (ms) => { const s = Math.max(0, Math.round(ms / 1000)); return Math.floor(s / 60) + ":" + String(s % 60).padStart(2, "0"); };
  const stampAt = (ms, span, tr) => tr && tr.cycle ? "+" + elapsed(ms - tr.from) : span > 20 * 3600000
    ? `${PK.weekdayDay(ms)}, ${PK.hm(ms)}`
    : hhmmss(ms);
  function num(v, tag) {
    if (v == null || !isFinite(v)) return "–";
    const d = tag && tag.decimals != null ? tag.decimals : Math.abs(v) >= 100 ? 0 : Math.abs(v) >= 10 ? 1 : 2;
    return PK.num(v, d);
  }
  const tagOf = (id) => state.model && state.model.tags.find((t) => t.id === id);
  const seqDef = (id) => state.model && state.model.sequences.find((d) => d.id === id);
  /** Each step gets one of a few calm colours, by its place in the sequence, so neighbours differ. */
  function stepColour(def, no) {
    const i = def ? def.steps.findIndex((s) => s.no === no) : -1;
    return "sc" + ((i < 0 ? no : i) % 6);
  }
  /** Model texts name settings like {LevelA}; show the live value, or the setting's name until values come. */
  function fill(text) {
    const vals = state.values && state.values.tags;
    return String(text || "").replace(/\{([A-Za-z0-9_]+)\}/g, (m, id) => {
      const v = vals && vals.find((t) => t.id === id);
      if (v && v.text) return v.text;
      const t = tagOf(id);
      return t ? (t.label || t.id) : id;
    });
  }
  const stepName = (def, no) => { const s = def && def.steps.find((x) => x.no === no); return s ? s.name : ""; };
  /** The only state word the server writes itself is "unknown"; the others are the model's own texts and stay as they are. */
  const stT = (text) => (text === "unknown" ? PK.t("unknown") : text);

  // ---------- trend data, fetched while a page that shows it is open ----------
  let trend = null, trendAt = 0, trendBusy = false, trendMin = null;
  // The values picked for the chart: kept per login (review 52), or in this browser when logged out
  let picked = PK.prefs.get("trendTags", null);
  document.addEventListener("pk:prefs", () => { picked = PK.prefs.get("trendTags", null); });
  function savePicked() { PK.prefs.set("trendTags", picked || null); }
  async function loadTrend(force) {
    if (trendBusy || (!force && Date.now() - trendAt < 4000)) return;
    trendBusy = true; trendAt = Date.now();
    const q = new URLSearchParams();
    if (trendMin) q.set("minutes", trendMin);
    if (picked) q.set("tags", picked.length ? picked.join(",") : "-");
    try { trend = await PK.api("GET", "/api/trend?" + q); } catch { /* keep what we had */ }
    trendBusy = false;
    PK.draw();
  }
  // The cycle views: "This cycle" (the running one, else the last finished) and "Compare with the previous cycle", on a time axis
  // that starts at 0:00 with the cycle. The previous cycle's values are drawn grey and dashed over this one's.
  let mode = null, cyc = null, cycAt = 0, cycBusy = false, cycInfo = { count: 0, sequence: null }, cycInfoAt = 0, cycAsked = false;
  async function loadCycle() {
    if (cycBusy) return;
    cycBusy = true; cycAt = Date.now(); cycInfoAt = cycAt;
    try {
      const list = await PK.api("GET", "/api/trend/cycles");
      cycInfo = { count: list.cycles.length, sequence: list.sequence };
      const a = list.cycles[0], b = mode === "compare" ? list.cycles[1] : null;
      if (!a || (mode === "compare" && !b)) cyc = null;
      else {
        const get = (c) => {
          const q = new URLSearchParams({ from: Math.floor(c.start), to: Math.ceil(c.end) });
          if (picked) q.set("tags", picked.length ? picked.join(",") : "-");
          return PK.api("GET", "/api/trend?" + q);
        };
        const [ta, tb] = await Promise.all([get(a), b ? get(b) : null]);
        const durA = a.end - a.start, durB = b ? b.end - b.start : 0;
        ta.from = a.start; ta.to = a.start + Math.max(durA, durB, 10000);
        ta.endAt = a.end; ta.cycle = { a, b, sequence: list.sequence };
        if (tb) {
          ta.cmp = {};
          tb.series.forEach((s) => { ta.cmp[s.id] = s.points.map((p) => [p[0] - b.start + a.start, p[1]]); });
          ta.cmpEnd = a.start + durB;
        }
        cyc = ta;
      }
    } catch { /* keep what we had */ }
    cycBusy = false; cycAsked = true;
    PK.draw();
  }
  /** Just the number of cycles, so the chips can say whether there is a cycle to show or compare. */
  async function loadCycleCount() {
    cycInfoAt = Date.now();
    try { const list = await PK.api("GET", "/api/trend/cycles"); const n = list.cycles.length; if (n !== cycInfo.count) { cycInfo = { count: n, sequence: list.sequence }; PK.draw(); } } catch { /* no chips */ }
  }
  /** For render(): the trend data, asking for fresh data every 5 seconds. */
  function needTrend() {
    if (Date.now() - trendAt > 5000) setTimeout(loadTrend, 0);
    return trend;
  }

  // ---------- trends ----------
  let hover = null;                 // 0..1 across the time axis while a finger or the mouse is on a chart
  const x = (t, tr) => ((t - tr.from) / (tr.to - tr.from)) * 100;

  /** The value at time t (the last point at or before t). */
  function valueAt(points, t) {
    let lo = 0, hi = points.length - 1, best = null;
    while (lo <= hi) { const mid = (lo + hi) >> 1; if (points[mid][0] <= t) { best = points[mid]; lo = mid + 1; } else hi = mid - 1; }
    return best ? best[1] : null;
  }

  /** The scale of a value: 0 to 100 for a percentage, else from 0 (or below, for negative values) to the highest value,
   *  so a flat level reads as a level and not as a line in the middle of −1..1. */
  function scaleOf(unit, values) {
    let lo = values.length ? Math.min(0, ...values) : 0, hi = values.length ? Math.max(0, ...values) : 1;
    if (String(unit || "").trim() === "%") { lo = Math.min(0, lo); hi = Math.max(100, hi); }
    if (hi - lo < 1e-9) hi = lo + 1;
    return { lo, hi };
  }
  /** The SVG path of a series over the time window; the last value holds until now (or until endAt, for a cycle that ended). */
  function pathOf(points, tr, y, endAt) {
    let d = "", area = "", started = false, firstX = 0, lastX = 0, segX = 0, last = null;
    // Each stretch between gaps is closed on its own, so a gap (PLC-Ki was off) is not filled in
    const close = () => { if (started) area += `L${lastX} 100L${segX} 100Z`; };
    for (const [t, v] of points) {
      if (v == null) { close(); started = false; continue; }
      const px = (x(t, tr) * 10).toFixed(1), py = y(v).toFixed(1);
      if (!started) { if (!d) firstX = px; segX = px; d += `M${px} ${py}`; area += `M${px} ${py}`; started = true; } else { d += `L${px} ${py}`; area += `L${px} ${py}`; }
      lastX = px; last = v;
    }
    const endX = endAt != null ? Math.min(1000, Math.max(0, x(endAt, tr) * 10)).toFixed(1) : 1000;
    if (last != null && started) { d += `L${endX} ${y(last).toFixed(1)}`; area += `L${endX} ${y(last).toFixed(1)}`; lastX = endX; }
    close();
    return { d, area, firstX, lastX, last };
  }
  /** A setpoint series is drawn dashed on the chart of its value: TempSP on Temp, M201_SpeedSP on M201_SpeedPV. */
  function baseOf(sp, list) {
    if (!/SP$/i.test(sp.id) && !/setpoint/i.test(sp.label || "")) return null;
    const ids = [sp.id.replace(/_?SP$/i, ""), sp.id.replace(/SP$/i, "PV")];
    return list.find((b) => b !== sp && ids.includes(b.id) && (b.unit || "") === (sp.unit || "")) || null;
  }

  function chartRow(sr, tr, sp) {
    const tag = tagOf(sr.id) || {};
    const vals = sr.points.map((p) => p[1]).filter((v) => v != null);
    const spVals = sp ? sp.points.map((p) => p[1]).filter((v) => v != null) : [];
    // The previous cycle, shifted onto this one's time axis (Compare with the previous cycle)
    const cmp = tr.cmp && tr.cmp[sr.id] ? tr.cmp[sr.id] : null;
    const cmpVals = cmp ? cmp.map((p) => p[1]).filter((v) => v != null) : [];
    const unit = sr.unit ? " " + esc(sr.unit) : "";
    const { lo, hi } = scaleOf(sr.unit, [...vals, ...spVals, ...cmpVals]);
    const y = (v) => 92 - ((v - lo) / (hi - lo)) * 84;      // a little room at top and bottom
    const { d, area, last } = pathOf(sr.points, tr, y, tr.endAt);
    const spPath = sp ? pathOf(sp.points, tr, y) : null;
    const cmpPath = cmp ? pathOf(cmp, tr, y, tr.cmpEnd) : null;
    const marks = tr.alarms.map((a) => `<i class="tr-al ${a.came ? "came" : "went"}" style="left:${x(a.time, tr).toFixed(2)}%" title="${esc(stampAt(a.time, tr.to - tr.from, tr) + " " + a.text)}"></i>`).join("");
    const t = hover != null ? tr.from + hover * (tr.to - tr.from) : null;
    const hv = t != null ? valueAt(sr.points, t) : null;
    const spNow = sp ? (t != null ? valueAt(sp.points, t) : spPath.last) : null;
    const cmpNow = cmp ? (t != null ? valueAt(cmp, t) : cmpPath.last) : null;
    return `<div class="tr-row">
      <div class="tr-lab"><span class="tr-name" title="${esc(sr.id)}">${esc(sr.label)}</span>
        <b class="tr-now">${num(hover != null ? hv : last, tag)}${unit}</b>
        <span class="tr-rng">${cmp ? `<span class="tr-cmp-k" aria-hidden="true"></span>${PK.t("previous cycle {value}", { value: num(cmpNow, tag) + unit })}` : sp ? `<span class="tr-sp-k" aria-hidden="true"></span>${PK.t("setpoint {value}", { value: num(spNow, tagOf(sp.id)) + unit })}` : vals.length ? PK.t("{a} to {b}", { a: num(Math.min(...vals), tag), b: num(Math.max(...vals), tag) }) : PK.t("no values yet")}</span>
        <button type="button" class="tr-x" data-tr-remove="${esc(sr.id)}" aria-label="${PK.t("Remove {label} from the chart", { label: esc(sr.label) })}" title="${PK.t("Remove from the chart")}">×</button></div>
      <div class="tr-plot" data-plot>
        <svg viewBox="0 0 1000 100" preserveAspectRatio="none" aria-hidden="true">
          <line x1="0" y1="50" x2="1000" y2="50" class="tr-grid"/>
          ${cmpPath && cmpPath.d ? `<path class="tr-cmp" d="${cmpPath.d}" vector-effect="non-scaling-stroke"/>` : ""}
          ${area ? `<path class="tr-area" d="${area}"/><path class="tr-line" d="${d}" vector-effect="non-scaling-stroke"/>` : ""}
          ${spPath && spPath.d ? `<path class="tr-sp" d="${spPath.d}" vector-effect="non-scaling-stroke"/>` : ""}
        </svg>
        <span class="tr-y hi">${num(hi, tag)}</span><span class="tr-y lo">${num(lo, tag)}</span>
        ${marks}
        ${hover != null ? `<i class="tr-cur" style="left:${(hover * 100).toFixed(2)}%"></i>` : ""}
      </div></div>`;
  }

  function bandRow(seq, tr, label = true) {
    const def = seqDef(seq.id);
    const now = state.status && state.status.sequences.find((q) => q.id === seq.id);
    const spans = seq.spans.map((s, i) => {
      const to = i === seq.spans.length - 1 ? (tr.endAt || tr.to) : s.to;       // the step it is in now runs to "now"
      const l = Math.max(0, x(s.from, tr)), w = Math.max(0.3, x(to, tr) - l);
      const name = stepName(def, s.step);
      return `<span class="tr-span ${stepColour(def, s.step)}" style="left:${l.toFixed(2)}%;width:${w.toFixed(2)}%" title="${esc(PK.t("Step {n} {name}: {dur}, from {from}", { n: s.step, name, dur: dur(to - s.from), from: stampAt(s.from, tr.to - tr.from, tr) }))}">${w > 4 ? s.step : ""}</span>`;
    }).join("");
    const t = hover != null ? tr.from + hover * (tr.to - tr.from) : null;
    const at = t != null ? [...seq.spans].reverse().find((s) => s.from <= t) : null;
    const show = at ? at.step : now ? now.step : null;
    return `<div class="tr-row band ${label ? "" : "nolab"}">
      ${label ? `<div class="tr-lab"><span class="tr-name">${esc(seq.name)}</span><b class="tr-now small">${show != null ? PK.t("Step {n}", { n: show }) : "–"}</b><span class="tr-rng">${esc(show != null ? stepName(def, show) : "")}</span></div>` : ""}
      <div class="tr-plot band" data-plot>${spans || `<span class="tr-empty">${PK.t("No step values yet")}</span>`}
        ${hover != null ? `<i class="tr-cur" style="left:${(hover * 100).toFixed(2)}%"></i>` : ""}</div></div>`;
  }

  const WEEKDAY_SHORT = [PK.T("Sun"), PK.T("Mon"), PK.T("Tue"), PK.T("Wed"), PK.T("Thu"), PK.T("Fri"), PK.T("Sat")];
  function axisRow(tr, label = true) {
    const span = tr.to - tr.from;
    // Clock times ("18:12"), with seconds when the window is short; the right end is "now"
    const cycle = !!tr.cycle;
    const clock = (ms) => cycle ? elapsed(ms - tr.from) : span > 36 * 3600000 ? PK.weekdayDay(ms)
      : span > 20 * 3600000 ? `${PK.t(WEEKDAY_SHORT[new Date(ms).getDay()])} ${PK.hm(ms)}`
      : span <= 600000 ? PK.clock(ms) : PK.hm(ms);
    const ticks = [0, 0.25, 0.5, 0.75, 1].map((f) =>
      `<span class="tr-tick" style="left:${f * 100}%">${f === 1 && !cycle ? PK.t("now") : clock(tr.from + f * span)}</span>`).join("");
    const t = hover != null ? tr.from + hover * span : null;
    return `<div class="tr-row axis ${label ? "" : "nolab"}">${label ? `<div class="tr-lab"></div>` : ""}<div class="tr-plot axis">${ticks}
      ${t != null ? `<span class="tr-tick at" style="left:${(hover * 100).toFixed(2)}%">${stampAt(t, span, tr)}</span>` : ""}</div></div>`;
  }

  function windowChoice(tr) {
    const keep = tr.keepMinutes;
    // What the live buffer holds, and what is saved on disk (an hour, 6 h, a day, a week, 30 days) as far back as it goes
    const saved = tr.savedFrom ? (Date.now() - tr.savedFrom) / 60000 : 0;
    const more = [60, 360, 1440, 10080, 43200];
    const reach = more.filter((m) => m <= saved);
    const nextMore = saved ? more.find((m) => m > saved) : null;
    const opts = [...new Set([5, 15, ...more.filter((m) => m <= keep), keep, ...reach, ...(nextMore ? [nextMore] : [])].filter((m) => m <= keep || m <= (nextMore || 0)))].sort((a, b) => a - b);
    const cur = trendMin || keep;
    const label = (m) => m < 60 ? m + " min" : m < 2880 ? Math.round(m / 60) + " h" : Math.round(m / 1440) + " d";
    const items = opts.map((m) => ({ label: label(m), on: !mode && m === cur, data: { "data-tr-min": m } }));
    // The cycle of the machine (concept view 6: This batch, Compare with the previous batch)
    if (cycInfo.count >= 1) items.push({ label: PK.t("This cycle"), on: mode === "cycle", data: { "data-tr-mode": "cycle" } });
    if (cycInfo.count >= 2) items.push({ label: PK.t("Compare with the previous cycle"), on: mode === "compare", data: { "data-tr-mode": "compare" } });
    return items.length > 1 ? PK.ui.choice(PK.t("Time shown"), items) : "";
  }

  /** "Last 15 minutes": the measured values stacked over one time axis, the step band and alarm marks. */
  function trends() {
    let tr;
    if (mode) {
      if (Date.now() - cycAt > 5000) setTimeout(loadCycle, 0);
      tr = cyc;
      if (!tr && cycAsked && !cycBusy) {
        const none = mode === "compare" ? PK.t("There are not two cycles with saved values yet.") : PK.t("No cycle with saved values yet.");
        const what = cycInfo.sequence ? PK.t(`A cycle is one run of the sequence "{name}", from its first step and back (set on the Reports page).`, { name: esc(cycInfo.sequence) })
          : PK.t("A cycle is one run of the sequence, from its first step and back (set on the Reports page).");
        return `<div class="trends"><div class="toolbar tr-tools">${windowChoice(trend || { keepMinutes: 15 })}</div><p class="muted">${none} ${what}</p></div>`;
      }
    } else {
      if (Date.now() - cycInfoAt > 20000) setTimeout(loadCycleCount, 0);
      tr = needTrend();
    }
    if (!tr) return PK.ui.loading("chart", { label: PK.t("Loading the values") });
    const short = !tr.cycle && (trendMin || tr.keepMinutes) * 60000 - (tr.to - tr.from) > 30000;
    const cy = tr.cycle;
    const clockAt = (ms) => PK.clock(ms);
    const cycNote = cy ? (cy.a.running ? PK.t("The cycle running now: {from} to now, {dur}.", { from: clockAt(cy.a.start), dur: dur(cy.a.end - cy.a.start) })
        : PK.t("The last finished cycle: {from} to {to}, {dur}.", { from: clockAt(cy.a.start), to: clockAt(cy.a.end), dur: dur(cy.a.end - cy.a.start) }))
      + (cy.b ? " " + PK.t("Grey dashed: the previous cycle, {from} to {to}, {dur}.", { from: clockAt(cy.b.start), to: clockAt(cy.b.end), dur: dur(cy.b.end - cy.b.start) }) : "")
      + " " + PK.t("The time axis starts at 0:00 with the cycle.") : "";
    const note = cy ? cycNote : short ? (tr.source === "disk" ? PK.t("PLC-Ki has values from {dur} back, so the charts start there.", { dur: dur(tr.to - tr.from) })
      : PK.t("PLC-Ki started watching {dur} ago, so the charts start there.", { dur: dur(tr.to - tr.from) })) : "";
    const ids = tr.series.map((s) => s.id);
    // A setpoint goes on its value's chart as a dashed line, not on a chart of its own
    const spFor = new Map(), onBase = new Set();
    tr.series.forEach((s) => { const b = baseOf(s, tr.series); if (b && !spFor.has(b.id)) { spFor.set(b.id, s); onBase.add(s.id); } });
    const admin = PK.can("settings");
    const res = tr.source === "disk" && tr.resolutionSec && !cy ? PK.t("One point on a chart is {dur}; a step shorter than that does not show.", { dur: dur(tr.resolutionSec * 1000) }) + " " : "";
    const keep = `${res}${PK.t("Values are saved on this PC (every read for the newest days, a minute at a time for older ones), so charts survive a restart.")}`
      + (admin ? ` <a href="#/setup/settings">${PK.t("How long they are kept is set in Setup › Settings › Storage.")}</a>` : " " + PK.t("An administrator sets how long they are kept."));
    return `<div class="trends">
      <div class="toolbar tr-tools">${windowChoice(tr)}
        <input type="search" list="tr-choices" id="tr-add" placeholder="${esc(PK.t("Add a value to the chart"))}" aria-label="${esc(PK.t("Add a value to the chart"))}" autocomplete="off">
        <datalist id="tr-choices">${tr.choices.filter((c) => !ids.includes(c.id)).map((c) => `<option value="${esc(c.id)}">${esc(c.label)}${c.unit ? " (" + esc(c.unit) + ")" : ""}</option>`).join("")}</datalist>
        ${picked ? `<button type="button" class="linkbtn" data-tr-reset>${PK.t("Back to the usual values")}</button>` : ""}
      </div>
      <div class="tr-rows" data-tr-rows>
        ${tr.series.length ? tr.series.filter((s) => !onBase.has(s.id)).map((s) => chartRow(s, tr, spFor.get(s.id))).join("") : `<p class="muted">${PK.t("No measured values in this machine model to draw. Add one above.")}</p>`}
        ${tr.steps.map((q) => bandRow(q, tr)).join("")}
        ${axisRow(tr)}
      </div>
      <p class="mt-3 mb-0 faint small">${note ? esc(note) + " " : ""}${PK.t("Move along a chart to read the values at that time. Dashed line: the setpoint. Red marks: an alarm came. Grey marks: it went.")} ${keep}</p>
    </div>`;
  }

  /** Hover and the tools of the trends (called from after()). */
  function bindTrends(page) {
    const rows = page.querySelector("[data-tr-rows]");
    if (rows) {
      let raf = 0;
      const move = (e) => {
        const plot = e.target.closest("[data-plot]") || rows.querySelector(".tr-plot.axis");
        if (!plot) return;
        const r = plot.getBoundingClientRect();
        const f = (e.clientX - r.left) / r.width;
        if (f < 0 || f > 1) { if (hover != null) { hover = null; PK.draw(); } return; }
        hover = f;
        if (!raf) raf = requestAnimationFrame(() => { raf = 0; PK.draw(); });
      };
      PK.on(rows, "pointermove", move);
      PK.on(rows, "pointerdown", move);
      PK.on(rows, "pointerleave", () => { hover = null; PK.draw(); });
    }
    page.querySelectorAll("[data-tr-min]").forEach((b) => b.onclick = () => { mode = null; trendMin = Number(b.dataset.trMin); loadTrend(true); });
    page.querySelectorAll("[data-tr-mode]").forEach((b) => b.onclick = () => { mode = b.dataset.trMode; cyc = null; cycAsked = false; loadCycle(); PK.draw(); });
    page.querySelectorAll("[data-tr-remove]").forEach((b) => b.onclick = () => {
      const cur = mode ? cyc : trend;
      picked = (cur ? cur.series.map((s) => s.id) : []).filter((id) => id !== b.dataset.trRemove);
      savePicked(); mode ? loadCycle() : loadTrend(true);
    });
    const r = page.querySelector("[data-tr-reset]");
    if (r) r.onclick = () => { picked = null; savePicked(); mode ? loadCycle() : loadTrend(true); };
    const add = page.querySelector("#tr-add");
    if (add) add.onchange = () => {
      const v = add.value.trim();
      const c = trend && trend.choices.find((x) => x.id === v || x.label.toLowerCase() === v.toLowerCase());
      if (!c) { if (v) PK.toast(PK.t("No number value with that name. Pick one from the list.")); return; }
      const cur = mode ? cyc : trend;
      picked = [...new Set([...(cur ? cur.series.map((s) => s.id) : []), c.id])];
      add.value = "";
      savePicked(); mode ? loadCycle() : loadTrend(true);
    };
  }

  // ---------- step chart and step timeline ----------
  let selStep = null, jumpTo = null, seqShown = null;

  /** Per step: how often it ran in the window, last and average time. The step it is in now is not finished. */
  function stepStats(spans, tr) {
    const st = new Map();
    spans.forEach((s, i) => {
      const open = i === spans.length - 1;
      const ms = (open ? tr.to : s.to) - s.from;
      const e = st.get(s.step) || { n: 0, done: 0, total: 0, max: 0, last: null, open: false };
      e.n++;
      if (!open) { e.done++; e.total += ms; e.max = Math.max(e.max, ms); e.last = ms; }
      else e.open = true;
      st.set(s.step, e);
    });
    return st;
  }

  function timeline(def, spans, tr) {
    if (!spans.length) return `<p class="muted">${PK.t("No step values in the last {n} minutes yet.", { n: tr.keepMinutes })}</p>`;
    const st = stepStats(spans, tr);
    const rows = def.steps.filter((s) => st.has(s.no));
    const maxAvg = Math.max(1, ...rows.map((s) => { const e = st.get(s.no); return e.done ? e.total / e.done : 0; }));
    const changes = spans.length - 1;
    return `<div class="tr-rows" data-tr-rows>${bandRow({ id: def.id, name: def.name, spans }, tr, false)}${axisRow(tr, false)}</div>
      <p class="mt-2 mb-4 faint small">${changes ? PK.t("{n} step change in the last {m} minutes.|{n} step changes in the last {m} minutes.", { n: changes, m: Math.round((tr.to - tr.from) / 60000) }) : PK.t("No step change in the last {m} minutes.", { m: Math.round((tr.to - tr.from) / 60000) })} ${PK.t("Move along the band to see which step ran when.")}</p>
      <table class="tbl steptimes"><tr><th>${PK.t("Step")}</th><th>${PK.t("Ran")}</th><th>${PK.t("Last")}</th><th>${PK.t("Average")}</th><th class="bar-h"></th></tr>
      ${rows.map((s) => {
        const e = st.get(s.no), avg = e.done ? e.total / e.done : null;
        return `<tr><td><button type="button" class="linkbtn" data-gs-go="${s.no}"><b>${s.no}</b> ${esc(s.name)}</button></td>
          <td>${e.n}×</td><td>${e.last != null ? dur(e.last) : "–"}${e.open ? ` <span class="now-tag">${PK.t("now")}</span>` : ""}</td><td>${avg != null ? dur(avg) : "–"}</td>
          <td class="bar-c"><i class="tbar ${stepColour(def, s.no)}" style="width:${avg != null ? Math.max(2, (avg / maxAvg) * 100).toFixed(1) : 0}%"></i></td></tr>`;
      }).join("")}</table>`;
  }

  /** One step of the chain: its box, its transitions, and jumps in and out, like a GRAPH sequence. */
  function chainStep(def, q, s, i, incoming, stats) {
    const nextInList = def.steps[i + 1];
    const isCur = q && q.step === s.no;
    const sel = selStep === s.no;
    const live = isCur ? q.next : [];
    const main = s.next.find((t) => nextInList && t.to === nextInList.no);
    const jumps = s.next.filter((t) => t !== main);
    const e = stats.get(s.no);
    const time = isCur ? (q.timeInStep ? PK.t("now, {time}", { time: q.timeInStep }) : PK.t("now")) : e && e.last != null ? PK.t("took {dur}", { dur: dur(e.last) }) : "";
    const condsOf = (t) => {
      const l = live.find((n) => n.to === t.to);
      if (l) return `<ul class="conds gt-conds">${l.conditions.map(cond).join("")}</ul>`;
      const cs = t.conditions || [];
      if (!cs.length) return `<span class="gt-text faint">${PK.t("no condition")}</span>`;
      const shown = sel ? cs : cs.slice(0, 1);
      return `<span class="gt-text">${shown.map((c) => esc(fill(c.text))).join("; ")}${cs.length > shown.length ? ` <span class="faint">${PK.t("+{n} more", { n: cs.length - shown.length })}</span>` : ""}</span>`;
    };
    const ready = (t) => { const l = live.find((n) => n.to === t.to); return l ? (l.ready ? "ready" : l.ready === false ? "wait" : "") : ""; };
    const from = incoming.get(s.no) || [];
    return `<li class="gs ${isCur ? "cur" + (q.halted ? " halted" : "") : ""} ${sel ? "sel" : ""} ${i === 0 ? "init" : ""} ${main ? "link" : "end"}" id="gs-${s.no}">
      ${from.length ? `<div class="gs-in">${from.map((f) => `<button type="button" class="linkbtn" data-gs-go="${f}">${PK.t("from {n}", { n: f })}</button>`).join(", ")} <span aria-hidden="true">↴</span></div>` : ""}
      <button type="button" class="gs-box" data-gs="${s.no}" aria-expanded="${sel}"><b class="gs-no">${s.no}</b><span class="gs-name">${esc(s.name)}</span>${time ? `<span class="gs-t">${esc(time)}</span>` : ""}</button>
      ${jumps.map((t) => `<div class="gt jump ${ready(t)}"><i class="gt-bar" aria-hidden="true"></i>${condsOf(t)}
        <button type="button" class="gt-to" data-gs-go="${t.to}" title="${esc(PK.t("Jump to step {n}", { n: t.to }))}">↷ ${t.to} ${esc(stepName(def, t.to))}</button></div>`).join("")}
      ${main ? `<div class="gt main ${ready(main)}"><i class="gt-bar" aria-hidden="true"></i>${condsOf(main)}</div>` : ""}
    </li>`;
  }

  function stepChart(def, q, stats) {
    // Where each step is reached from, when that is not simply the step above it (GRAPH jumps)
    const incoming = new Map();
    def.steps.forEach((s, i) => s.next.forEach((t) => {
      const target = def.steps.findIndex((x) => x.no === t.to);
      if (target !== i + 1) incoming.set(t.to, [...(incoming.get(t.to) || []), s.no]);
    }));
    return `<ol class="gchain">${def.steps.map((s, i) => chainStep(def, q, s, i, incoming, stats)).join("")}</ol>`;
  }

  /** "1 min 20 s", "45 s", "1 h 5 min" -> ms (the status sends the time in step as text). */
  function msOf(text) {
    const m = String(text || "").match(/(\d+(?:[.,]\d+)?)\s*(h|min|s|ms)\b/g);
    if (!m) return null;
    return m.reduce((t, p) => { const [, n, u] = /(\d+(?:[.,]\d+)?)\s*(h|min|s|ms)/.exec(p); return t + parseFloat(n.replace(",", ".")) * { h: 3600000, min: 60000, s: 1000, ms: 1 }[u]; }, 0);
  }
  /** The time limit of a step from its model text: "Longer than 600 s raises A18" -> 600 000 ms. */
  const limitMs = (s) => (s && s.limit ? msOf((fill(s.limit).match(/\d+(?:[.,]\d+)?\s*(?:h|min|s)\b/) || [])[0]) : null);

  /** The selected step on the right of the chart: its time against the limit, what it does, and one row per condition. */
  function stepPanel(def, q, stats, no) {
    const s = def.steps.find((x) => x.no === no);
    if (!s) return `<div class="card"><p class="m-0 muted">${PK.t("Tap a step in the chart to see it here.")}</p></div>`;
    const isCur = q && q.step === s.no;
    const e = stats.get(s.no);
    const lim = limitMs(s);
    const t = isCur ? msOf(q.timeInStep) : e && e.last != null ? e.last : null;
    const over = lim && t != null && t > lim;
    const bar = t != null && lim ? `<div class="sp-time"><div class="row"><span>${isCur ? PK.t("In this step") : PK.t("Last time it took")} <b>${dur(t)}</b></span><span class="spacer"></span><span class="faint">${PK.t("limit {dur}", { dur: dur(lim) })}</span></div>
        <div class="vb-track"><i class="vb-fill ${over ? "bad" : t > lim * 0.8 ? "warn" : ""}" style="width:${Math.min(100, (t / lim) * 100).toFixed(1)}%"></i></div></div>`
      : t != null ? `<p class="m-0">${isCur ? PK.t("In this step for") : PK.t("Last time it took")} <b>${dur(t)}</b>. ${PK.t("No time limit is known.")}</p>` : "";
    const live = isCur ? q.next : [];
    const trans = s.next.map((tn) => {
      const l = live.find((n) => n.to === tn.to);
      const rows = l ? l.conditions.map((c) => PK.condHeld(c)).join("")
        : (tn.conditions || []).map((c) => `<li><span class="mark unk" aria-label="${esc(PK.t("not checked now"))}">·</span><span>${esc(fill(c.text))}</span></li>`).join("");
      return `<div class="sp-tr"><div class="row"><b>${PK.t("To step {n} {name}", { n: tn.to, name: esc(stepName(def, tn.to)) })}</b><span class="spacer"></span>${l ? `<span class="pill ${l.ready ? "ok" : l.ready === false ? "warn" : ""} small-pill">${l.ready ? PK.t("ready") : l.ready === false ? PK.t("waiting") : PK.t("not known")}</span>` : ""}</div>
        ${rows ? `<ul class="conds">${rows}</ul>` : `<p class="mt-1 mb-0 faint small">${PK.t("No condition.")}</p>`}</div>`;
    }).join("");
    return `<div class="card stack sp-card">
      <div class="row"><h3 class="m-0">${PK.t("Step {n} {name}", { n: s.no, name: esc(s.name) })}</h3>${isCur ? `<span class="now-tag">${PK.t("now")}</span>` : ""}</div>
      ${bar}
      ${s.does ? `<p class="m-0"><span class="muted">${PK.t("This step should:")}</span> ${esc(fill(s.does))}</p>` : ""}
      ${s.limit ? `<p class="m-0 faint small">${esc(fill(s.limit))}</p>` : ""}
      ${e ? `<p class="m-0 faint small">${PK.t("In the last minutes: ran {n}×{more}.", { n: e.n, more: e.done ? PK.t(", on average {avg}, longest {max}", { avg: dur(e.total / e.done), max: dur(e.max) }) : "" })}</p>` : ""}
      ${trans ? `<div class="gap-3 stack">${trans}</div>` : `<p class="m-0 faint small">${PK.t("The last step: no transition goes on from here.")}</p>`}
    </div>`;
  }

  // The Plant tab "Sequence": #/plant/sequence/<id>/<step>. The old #/sequence/<id>/<step> links lead there.
  let stepParam;          // the step in the address last time, to follow it when it changes
  const seqBase = (def) => "#/plant/sequence" + (def ? "/" + encodeURIComponent(def.id) : "");
  /** The step chart with the selected step beside it, then the step timeline. "" when the machine has no sequence. */
  function sequenceBody(id, step) {
    const s = state.status, m = state.model;
    if (!s || !m) return PK.ui.loading("chart", { label: PK.t("Loading the sequence") });
    const def = seqDef(id) || m.sequences[0];
    if (!def) return `<p class="muted">${PK.t("The imported program has no GRAPH sequence or step chain.")}</p>`;
    const q = s.sequences.find((x) => x.id === def.id);
    if (step !== stepParam || def.id !== seqShown) {
      stepParam = step; seqShown = def.id;
      selStep = step != null && step !== "" ? Number(step) : null;
      jumpTo = selStep;
    }
    const tr = needTrend();
    const spans = tr ? ((tr.steps.find((x) => x.id === def.id) || {}).spans || []) : [];
    const stats = tr ? stepStats(spans, tr) : new Map();
    const others = m.sequences.filter((d) => d.id !== def.id);
    const blk = PK.blockOf(def.block);
    const shown = selStep != null ? selStep : q ? q.step : null;
    return `<div class="row seq-tools">${m.sequences.length > 1 ? `<span class="small">${[def, ...others].map((d) => d === def ? `<b>${esc(d.name)}</b>` : `<a href="${seqBase(d)}">${esc(d.name)}</a>`).join(" · ")}</span>` : `<b>${esc(def.name)}</b>`}
        <span class="spacer"></span>${q && q.step != null ? `<button class="btn b small" data-gs-go="${q.step}">${PK.t("Go to step {n}", { n: q.step })}</button>` : ""}
        ${blk ? `<a class="btn b small" href="#/program/${encodeURIComponent(blk)}/as-rungs">${PK.t("Show in the program")}</a>` : ""}
        <button class="btn b small" data-ask="${esc(PK.t("Explain what {name} does, step by step, and where it is now.", { name: def.name }))}">${PK.t("Ask")}</button></div>
      <div class="seq-cols">
        <div><p class="mt-0 mb-3 faint small">${PK.t("{n} steps, read like the GRAPH editor: each step, the transition under it, and jumps (↷) to other steps. Tap a step to see it on the right.", { n: def.steps.length })}</p>
          <div class="gwrap">${stepChart(def, q, stats)}</div></div>
        <aside class="seq-side">${stepPanel(def, q, stats, shown)}</aside>
      </div>
      <h2 class="mt-6">${PK.t("Step timeline")}</h2><p class="sub">${PK.t("Which step ran when, and how long each one took.")}</p>${tr ? timeline(def, spans, tr) : PK.ui.loading("chart")}`;
  }
  /** Clicks on the step chart (called from the Plant page's after()). */
  function bindSequence(page, id) {
    const def = seqDef(id) || (state.model && state.model.sequences[0]);
    const base = seqBase(def);
    const pick = (no) => { selStep = no; stepParam = no != null ? String(no) : undefined; PK.setHash(base + (no != null ? "/" + no : "")); PK.draw(); };
    page.querySelectorAll("[data-gs]").forEach((b) => b.onclick = () => {
      const no = Number(b.dataset.gs);
      pick(selStep === no ? null : no);
      // One column (phone): the step's details are above the chart, so bring them into view
      if (PK.screenSize() !== "desktop") setTimeout(() => { const p = document.querySelector(".seq-side"); if (p) p.scrollIntoView({ block: "start", behavior: "instant" }); }, 0);
    });
    page.querySelectorAll("[data-gs-go]").forEach((b) => b.onclick = () => { jumpTo = Number(b.dataset.gsGo); pick(jumpTo); });
    if (jumpTo != null) {
      const el = page.querySelector(`#gs-${jumpTo}`);
      // after this redraw has put the page back where it was
      if (el) { jumpTo = null; setTimeout(() => el.scrollIntoView({ block: "center", behavior: "instant" }), 0); }
    }
  }

  // Old links (#/sequence/<id>/<step>, from notes, QR pages or bookmarks) open the Plant tab
  PK.view("sequence", {
    mount(page, params) {
      location.replace("#/plant/sequence" + params.filter((p) => p != null && p !== "").map((p) => "/" + encodeURIComponent(p)).join(""));
    },
    render() { return ""; },
  });

  // ---------- plant map ----------
  /** A drawn symbol per kind of device (P&ID-like), in the device's state colour. */
  function symbolFor(kind, id) {
    const k = String(kind || "").toLowerCase();
    const has = (...w) => w.some((x) => k.includes(x));
    const svg = (body) => `<svg viewBox="0 0 48 48" aria-hidden="true">${body}</svg>`;
    if (has("valve")) return svg(`<path d="M8 16 24 26 8 36Z M40 16 24 26 40 36Z"/><path d="M24 26V12"/><rect x="17" y="6" width="14" height="6" rx="1.5"/>`);
    if (has("pump")) return svg(`<circle cx="24" cy="24" r="15"/><path d="M18 15 35 24 18 33Z"/>`);
    if (has("heater", "heat")) return svg(`<rect x="8" y="12" width="32" height="24" rx="3"/><path d="M12 24h4l2-6 4 12 4-12 4 12 2-6h4"/>`);
    if (has("robot")) return svg(`<rect x="10" y="36" width="18" height="6" rx="1.5"/><path d="M19 36V26l12-10 7 6"/><circle cx="19" cy="26" r="2.5"/><circle cx="31" cy="16" r="2.5"/><path d="M38 22l3 4M38 22l4-1"/>`);
    if (has("conveyor", "belt")) return svg(`<rect x="6" y="18" width="36" height="12" rx="6"/><circle cx="12" cy="24" r="3"/><circle cx="36" cy="24" r="3"/>`);
    if (has("cylinder", "gripper", "clamp", "pneumatic")) return svg(`<rect x="6" y="16" width="24" height="16" rx="2"/><path d="M18 16v16M18 24h24"/><rect x="38" y="19" width="4" height="10" rx="1"/>`);
    if (has("camera", "scanner", "vision", "reader", "keyence")) return svg(`<rect x="8" y="14" width="26" height="20" rx="3"/><path d="M34 20l8-4v16l-8-4"/><circle cx="21" cy="24" r="5"/>`);
    if (has("tank", "vessel")) return svg(`<path d="M12 10h24v26a6 6 0 0 1-6 6H18a6 6 0 0 1-6-6Z"/><path d="M12 26h24"/>`);
    if (has("motor", "vfd", "drive", "fan", "agitator", "spindle")) return svg(`<circle cx="24" cy="24" r="15"/><text x="24" y="29.5" text-anchor="middle">M</text>`);
    if (has("analog", "sensor", "transmitter", "detector", "gauge", "measur", "leak")) {
      const letters = (String(id).match(/^[A-Z]{1,3}/) || [""])[0].slice(0, 3) || "S";
      return svg(`<circle cx="24" cy="24" r="15"/><text x="24" y="28.5" text-anchor="middle" class="sm">${esc(letters)}</text>`);
    }
    return svg(`<rect x="9" y="9" width="30" height="30" rx="6"/><circle cx="24" cy="24" r="4"/>`);
  }
  // ---------- which device an alarm is about ----------
  /** The instance path in a model location: FB_Valve, instance "Station".XV101 (output …) → "Station".XV101. */
  const instPath = (loc) => { const m = /instance\s+("[^"]+"(?:\.[A-Za-z0-9_]+)*)/.exec(loc || ""); return m ? m[1] : ""; };
  const word = (id) => new RegExp("(^|[^A-Za-z0-9])" + String(id).replace(/[.*+?^${}()|[\]\\]/g, "\\$&") + "(?![A-Za-z0-9])");
  /** Is this alarm about this device? The same instance ("Station".XV101) when the alarm names one; when it names
   *  no device instance (FB_Station, "DB_IO".E301_OverTemp) the device id must appear in its text or location.
   *  An alarm on an instance that is not a device (a sensor, the sequence) matches nothing: no guessing. */
  function alarmIsOn(a, e, devPaths) {
    const ap = instPath(a.location);
    if (ap && devPaths.has(ap)) return instPath(e.location) === ap;
    if (ap && ap.includes(".")) return false;                     // a member that is not a device
    return word(e.id).test(a.text + " " + (a.location || ""));
  }
  /** The active alarms about device e, read from the status now (FAULTs first). */
  function deviceAlarms(e, s = state.status) {
    if (!s || !e) return [];
    const paths = new Set(s.equipment.map((x) => instPath(x.location)).filter(Boolean));
    return s.activeAlarms.filter((a) => alarmIsOn(a, e, paths)).sort((x, y) => (x.severity === "WARNING") - (y.severity === "WARNING"));
  }
  /** How to show a device: its own state, made worse by an alarm on it. FAULT → fault, WARNING → warn.
   *  { lvl: fault|warn|blocked|own level, word: short label, alarms } */
  function devLook(e, s = state.status) {
    const alarms = deviceAlarms(e, s);
    const fault = alarms.find((a) => a.severity !== "WARNING");
    if (fault || e.level === "fault" || e.level === "bad")
      return { lvl: "fault", word: fault ? fault.id : stT(e.state), alarms };
    if (alarms.length) return { lvl: "warn", word: alarms[0].id, alarms };
    if (e.blockedBy) return { lvl: "blocked", word: PK.t("blocked"), alarms };
    return { lvl: e.level, word: stT(e.state), alarms };
  }
  PK.deviceAlarms = deviceAlarms;
  PK.devLook = devLook;

  /** The station a device belongs to, from its location: FB_VFD, instance "Station".M201 → Station;
   *  FB_Station (heater interlock line) → Station (programmed inside that block). */
  function stationOf(loc) {
    const m = /instance\s+"([^"]+)"/.exec(loc || "") || /"([^"]+)"/.exec(loc || "");
    if (m) return m[1];
    const b = /^\s*(?:FB|FC)_?([A-Za-z0-9_]+)/.exec(loc || "");
    return b ? b[1] : "";
  }
  // ---------- why a device runs or not, and the cause chain ----------
  const cap = (t) => String(t || "").charAt(0).toUpperCase() + String(t || "").slice(1);
  /** "XV101 open switch" (tag XV101_ZSO) -> "Open switch ZSO": the device id is already said, the short tag name helps
   *  find the switch on the drawings. */
  function sigName(text, e, v) {
    const name = cap(String(text).split(":")[0].replace(new RegExp("^" + e.id.replace(/[.*+?^${}()|[\]\\]/g, "\\$&") + "\\s+"), ""));
    const short = v && v.id && v.id.startsWith(e.id + "_") ? v.id.slice(e.id.length + 1) : "";
    return /^[A-Z]{2,4}$/.test(short) && !name.includes(short) ? name + " " + short : name;
  }
  /**
   * The four steps of a device: asked for → interlocks → output → feedback (or its state when the model knows no
   * feedback). Each is { k, v, note, st: ok | off | warn | bad, alarm }. Only the first broken step is red. A FAULT
   * alarm on the device makes its last step red when nothing before it is: the alarm says the result failed, even when
   * a halt has switched everything off since.
   */
  function devSteps(e, s = state.status) {
    const look = devLook(e, s);
    const fault = look.alarms.find((a) => a.severity !== "WARNING");
    const halted = s && s.sequences.find((q) => q.halted);
    const blocking = e.permits.filter((p) => p.met === false);
    const permOk = !blocking.length;
    const by = (id) => (s && s.equipment.find((x) => x.id === id)) || null;
    const steps = [];
    steps.push(e.requested === true ? { k: PK.T("Requested"), v: PK.t("yes"), st: "ok" }
      : e.requested === false ? { k: PK.T("Requested"), v: PK.t("no"), st: "off", note: halted ? (fault ? PK.t("{name} is halted by {id}", { name: halted.name, id: fault.id }) : PK.t("{name} is halted", { name: halted.name })) : PK.t("not in this step") }
      : { k: PK.T("Requested"), v: PK.t("not known"), st: "off" });
    if (permOk) steps.push({ k: PK.T("Interlocks"), v: e.permits.length ? PK.t("OK") : PK.t("none"), st: e.permits.length ? "ok" : "off" });
    else {
      const p = blocking[0], held = p.heldBy && by(p.heldBy);
      steps.push({ k: PK.T("Interlocks"), v: PK.t("{n} missing", { n: blocking.length }), st: e.requested === true ? "bad" : "warn",
        note: p.text + (held ? " " + PK.t("(waits for {name})", { name: held.name }) : "") });
    }
    steps.push(e.commanded === true ? { k: PK.T("Output"), v: PK.t("on"), st: "ok" }
      : e.commanded === false ? { k: PK.T("Output"), v: PK.t("off"), st: e.requested === true && permOk ? "warn" : "off", note: e.requested === true && permOk ? PK.t("interlocks are OK, but it stays off") : "" }
      : { k: PK.T("Output"), v: PK.t("not known"), st: "off" });
    const fb = e.feedback || [];
    if (fb.length) {
      // Feedback only counts while the output is on: a closed valve nobody asked to open is not "missing" its open switch
      const on = e.commanded === true;
      const miss = fb.filter((f) => f.met === false), f = miss[0] || fb[0], v = f.values && f.values[0];
      const st = !miss.length ? (on ? "ok" : "off") : on ? (look.lvl === "fault" ? "bad" : "warn") : "off";
      steps.push({ k: PK.T("Feedback"), v: `${sigName(f.text, e, v)}: ${v ? v.text : f.met ? PK.t("yes") : f.met === false ? PK.t("no") : PK.t("not known")}`, st,
        mark: on ? (f.met === false ? "✗" : f.met ? "✓" : "") : "", note: on && miss.length > 1 ? PK.t("and {n} more", { n: miss.length - 1 }) : "" });
    } else {
      const map = { ok: "ok", idle: "off", info: "off", warn: "warn", blocked: "warn", fault: "bad", bad: "bad" };
      steps.push({ k: PK.T("State"), v: stT(e.state), st: map[look.lvl] || "off" });
    }
    if (fault && !steps.some((x) => x.st === "bad")) {
      const last = steps[steps.length - 1];
      Object.assign(last, { st: "bad", alarm: fault.id, mark: last.k === "Feedback" ? "✗" : "" });
    }
    let red = false;
    steps.forEach((x) => { if (x.st === "bad") { if (red) x.st = "warn"; red = true; } });
    return steps;
  }
  /** The device's steps as one full-width strip; the first broken step is red. */
  function devStrip(e) {
    return `<ol class="dstrip" aria-label="${PK.t("Why {name} runs or not", { name: esc(e.name) })}">${devSteps(e).map((x) => `<li class="ds ${x.st}">
        <span class="ds-k">${esc(PK.t(x.k))}</span><b>${esc(x.v)}${x.mark ? ` <span class="ds-m">${x.mark}</span>` : ""}</b>
        ${x.alarm ? `<span class="ds-n">${PK.t("alarm {id}", { id: esc(x.alarm) })}</span>` : x.note ? `<span class="ds-n">${esc(x.note)}</span>` : ""}</li>`).join("")}</ol>`;
  }

  /**
   * Blocked devices as chains, root first: what is missing at the root, the devices that wait on each other (from the
   * server's blockedVia links), and the step conditions that cannot come while they are blocked (heldBy).
   * [{ why: the root's missing interlock or null, devices: [root … last], waits: [{ seq, cond }] }]
   */
  function causeChains(s = state.status) {
    if (!s) return [];
    const by = new Map(s.equipment.map((e) => [e.id, e]));
    const blocked = s.equipment.filter((e) => e.blockedBy);
    const pointed = new Set(blocked.map((e) => e.blockedVia).filter(Boolean));
    return blocked.filter((e) => !pointed.has(e.id)).map((last) => {
      const devices = [last], ids = new Set([last.id]);
      for (let cur = last; cur.blockedVia && by.has(cur.blockedVia) && !ids.has(cur.blockedVia);) {
        cur = by.get(cur.blockedVia); ids.add(cur.id); devices.unshift(cur);
      }
      const root = devices[0];
      const why = (root.blocking || []).find((p) => !p.heldBy) || (root.blocking || [])[0] || null;
      const waits = s.sequences.flatMap((q) => q.next.flatMap((n) => n.conditions.filter((c) => c.heldBy && ids.has(c.heldBy)).map((c) => ({ seq: q, cond: c }))));
      return { why, devices, waits };
    });
  }
  const devWord = (e) => e.blockedBy ? PK.t("blocked") : devLook(e).lvl === "fault" ? PK.t("fault") : stT(e.state);
  /** One chain as a line of links: Level 15 % (needs above 20 %) → M201 blocked → E301 blocked → step 30 can't finish. */
  function chainHtml(ch) {
    const parts = [];
    if (ch.why) {
      const val = ch.why.values && ch.why.values.length ? ch.why.values.map((v) => `${esc(v.label)} <b>${esc(v.text)}</b>`).join(", ") : "";
      parts.push(`<li class="cc cc-root"><span class="cc-box"><span class="cc-t">${val || esc(cap(ch.why.text))}</span><span class="cc-n">${val ? PK.t("needs: {what}", { what: esc(ch.why.text.charAt(0).toLowerCase() + ch.why.text.slice(1)) }) : PK.t("missing")}</span></span></li>`);
    }
    ch.devices.forEach((e) => parts.push(`<li class="cc cc-dev"><a href="#/plant/devices/${encodeURIComponent(e.id)}"><span class="cc-t">${esc(e.name)}</span><span class="cc-n">${esc(devWord(e))}</span></a></li>`));
    const bySeq = new Map();
    ch.waits.forEach((w) => bySeq.set(w.seq, [...(bySeq.get(w.seq) || []), w.cond]));
    bySeq.forEach((conds, q) => parts.push(`<li class="cc cc-step"><a href="#/plant/sequence/${encodeURIComponent(q.id)}${q.step != null ? "/" + q.step : ""}"><span class="cc-t">${PK.t("Step {n} can't go on", { n: esc(q.step ?? "?") })}</span><span class="cc-n">${conds.map((c) => esc(c.text)).join("; ")}</span></a></li>`));
    return `<ol class="cchain" aria-label="${PK.t("Cause, root first")}">${parts.join("")}</ol>`;
  }
  /** A condition line (PK.cond) that also says which blocked device it waits for. */
  function condHeld(c, s = state.status) {
    const html = PK.cond(c);
    const d = c.heldBy && s && s.equipment.find((e) => e.id === c.heldBy);
    return d ? html.replace(/<\/span><\/li>$/, `<span class="held">${PK.t("Can't come while {link} is {word}.", { link: `<a href="#/plant/devices/${encodeURIComponent(d.id)}">${esc(d.name)}</a>`, word: esc(devWord(d)) })}</span></span></li>`) : html;
  }
  Object.assign(PK, { devSteps, devStrip, causeChains, chainHtml, condHeld });

  /** Every device as a symbol in its state colour, grouped by station. Uses the Plant page's [data-eq] selection.
   *  ring: device ids to ring (the device an alarm is about, on Active alarm). */
  /**
   * A state text the model wrote ("running at 1200 rpm (setpoint 1200 rpm)", "heating (48.2 °C, target 60 °C)") as
   * a short word and the rest, for a big tile: the value is shown on its own line, so it is left out of both.
   */
  function splitState(word, valueText) {
    // The model's texts write 48.2, the tile writes 48,2 in German: a . and a , count as the same
    const body = valueText ? valueText.split(/[.,]/).map((x) => x.replace(/[*+?^${}()|[\]\\]/g, "\\$&")).join("[.,]") : null;
    const same = body ? new RegExp("^" + body + "$") : null;
    let head = String(word), detail = "";
    const m = /^(.*?)\s*\((.*)\)\s*$/.exec(head);
    if (m && m[1]) { head = m[1]; detail = m[2].split(/,\s+/).filter((x) => x && !(same && same.test(x))).join(", "); }
    if (body) head = head.replace(new RegExp("\\s*(?:\\b(?:at|bei|with|mit)\\s+|@\\s*)?" + body), "").replace(/\s{2,}/g, " ").trim() || head;
    return { head, detail };
  }

  function plantMap(equipment, selected, ring, opts = {}) {
    if (!equipment.length) return "";
    const groups = new Map();
    equipment.forEach((e) => { const g = stationOf(e.location); groups.set(g, [...(groups.get(g) || []), e]); });
    const many = groups.size > 1;
    const tile = (e) => {
      const look = devLook(e);
      const kv = keyValue(e);
      const sp = opts.split && !look.alarms.length && !e.blockedBy ? splitState(look.word, kv && kv.text) : null;
      const said = `${e.name}: ${stT(e.state)}${kv ? `, ${kv.label} ${kv.text}` : ""}` + look.alarms.map((a) => ". " + (a.severity === "WARNING" ? PK.t("Warning {id}: {text}", { id: a.id, text: a.text }) : PK.t("Alarm {id}: {text}", { id: a.id, text: a.text }))).join("");
      return `<div class="pm-dev lv-${esc(look.lvl)} ${e.id === selected ? "sel" : ""} ${ring && ring.includes(e.id) ? "ring" : ""}" data-eq="${esc(e.id)}" role="button" tabindex="0" aria-label="${esc(said)}" title="${esc(said)}">
        <span class="pm-sym">${symbolFor(e.kind, e.id)}</span><b>${esc(e.id)}</b><span class="pm-st">${esc(sp ? sp.head : look.word)}</span>${kv ? `<span class="pm-v" title="${esc(kv.label)}">${esc(kv.text)}</span>` : ""}${sp && sp.detail ? `<span class="pm-d">${esc(sp.detail)}</span>` : ""}</div>`;
    };
    return `<div class="pmap">${[...groups].map(([g, list]) => `<div class="pm-grp">${many ? `<div class="pm-h">${esc(g || PK.t("Other devices"))}</div>` : ""}<div class="pm-devs">${list.map(tile).join("")}</div></div>`).join("")}
      <div class="pm-key small"><span class="lv-ok">${PK.t("running or fine")}</span><span class="lv-idle">${PK.t("off")}</span><span class="lv-blocked">${PK.t("asked for but blocked")}</span><span class="lv-warn">${PK.t("warning")}</span><span class="lv-fault">${PK.t("fault")}</span></div></div>`;
  }

  // ---------- the Plant header: measured values as bars ----------
  const reEsc = (s) => String(s).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  /** Interlock limits on a tag from the model: "Level > 20" in M201's interlocks -> { v: 20, op: ">", dev, met, asked }. */
  function limitsOf(id) {
    const m = state.model, s = state.status;
    if (!m) return [];
    const re = new RegExp("^\\s*" + reEsc(id) + "\\s*(>=|>|<=|<)\\s*(-?\\d+(?:\\.\\d+)?)\\s*$");
    const out = [];
    m.equipment.forEach((e) => (e.permits || []).forEach((p, i) => {
      const mm = re.exec(p.expr || "");
      if (!mm) return;
      const st = s && s.equipment.find((x) => x.id === e.id);
      out.push({ v: Number(mm[2]), op: mm[1], dev: e, met: st && st.permits[i] ? st.permits[i].met : null, asked: !!(st && st.requested === true) });
    }));
    return out;
  }
  /** The setpoint series of a value in the trend data (TempSP for Temp), or null. */
  const setpointOf = (id, tr) => tr && tr.series.find((sp) => { const b = baseOf(sp, tr.series); return b && b.id === id; }) || null;
  /** A small line of the last minutes, scaled to its own range: the shape is what matters here. */
  function sparkline(sr) {
    const pts = sr ? sr.points.filter((p) => p[1] != null) : [];
    if (pts.length < 2) return "";
    const t0 = pts[0][0], t1 = pts[pts.length - 1][0] || t0 + 1;
    const vs = pts.map((p) => p[1]);
    let lo = Math.min(...vs), hi = Math.max(...vs);
    if (hi - lo < 1e-9) { lo -= 1; hi += 1; }
    const d = pts.map(([t, v], i) => `${i ? "L" : "M"}${(((t - t0) / Math.max(1, t1 - t0)) * 100).toFixed(1)} ${(22 - ((v - lo) / (hi - lo)) * 20).toFixed(1)}`).join("");
    return `<svg class="vb-spark" viewBox="0 0 100 24" preserveAspectRatio="none" aria-hidden="true"><path d="${d}" vector-effect="non-scaling-stroke"/></svg>`;
  }
  /**
   * The Plant header's values: a number with a unit gets a bar with each interlock limit marked and a sparkline
   * ("Tank level 15 %, ▏20 % needed for Agitator M201"); switches and counters stay plain numbers.
   */
  function valueBars(overview) {
    const tr = needTrend();
    const unitOf = (v) => { const t = tagOf(v.id); return t && t.unit ? t.unit : ""; };
    const isBar = (v) => typeof v.raw === "number" && unitOf(v);
    const bars = overview.filter(isBar), plain = overview.filter((v) => !isBar(v));
    const html = bars.map((v) => {
      const unit = unitOf(v), tag = tagOf(v.id) || {};
      const lims = limitsOf(v.id);
      const spSr = setpointOf(v.id, tr);
      const sp = spSr ? (spSr.points.filter((p) => p[1] != null).pop() || [])[1] : null;
      const sr = tr && tr.series.find((x) => x.id === v.id);
      const max = unit.trim() === "%" ? 100 : Math.max(1, v.raw, sp || 0, ...lims.map((l) => l.v)) * 1.2;
      const pos = (n) => Math.max(0, Math.min(100, (n / max) * 100)).toFixed(1);
      // The limit to name: one that is not met for a device the program asks for now. A limit of a device that is
      // not asked for (the agitator in the fill step) is only marked on the bar: it holds nothing back yet.
      const miss = lims.find((l) => l.met === false && l.asked);
      const need = miss ? (miss.op.startsWith(">") ? PK.t("{v} {unit} needed for {name}", { v: miss.v, unit, name: miss.dev.name }) : PK.t("{v} {unit} at most for {name}", { v: miss.v, unit, name: miss.dev.name })) : "";
      return `<div class="vbar ${miss ? "short" : ""}">
          <div class="vb-h"><span class="vb-l">${esc(v.label)}</span><b>${esc(v.text)}</b></div>
          <div class="vb-track" title="${esc(lims.map((l) => PK.t("{name} needs {label} {op} {v} {unit}", { name: l.dev.name, label: v.label, op: l.op, v: l.v, unit })).join("\n"))}">
            <i class="vb-fill ${miss ? "warn" : ""}" style="width:${pos(Math.max(0, v.raw))}%"></i>
            ${lims.map((l) => `<i class="vb-lim ${l.met === false ? "no" : ""}" style="left:${pos(l.v)}%"></i>`).join("")}
            ${sp != null ? `<i class="vb-sp" style="left:${pos(sp)}%" title="${esc(PK.t("Setpoint {v} {unit}", { v: num(sp, tag), unit }))}"></i>` : ""}</div>
          <div class="vb-f"><span>${need ? `<span class="vb-need">▏${esc(need)}</span>` : sp != null ? esc(PK.t("setpoint {v} {unit}", { v: num(sp, tag), unit })) : ""}</span>${sparkline(sr)}</div>
        </div>`;
    }).join("");
    // Switches and counters share the row with the bars, as a small block of numbers, so the header stays short
    const nums = plain.length ? `<div class="vb-plain">${plain.map((v) => `<div class="k"><b>${esc(v.text)}</b><span>${esc(v.label)}</span></div>`).join("")}</div>` : "";
    return html || nums ? `<div class="vbars">${html}${nums}</div>` : "";
  }

  /** The value that says most about a device on its tile: its actual speed, its temperature… (not a setpoint). */
  function keyValue(e) {
    const nums = (e.values || []).filter((v) => typeof v.raw === "number" && !/SP$/i.test(v.id) && !/setpoint/i.test(v.label));
    return nums.find((v) => /actual|PV$|\bvalue\b/i.test(v.label + " " + v.id)) || nums[0] || null;
  }

  /** The devices around the given ones, for a small map: their own station, at most max, the given ones first. */
  function around(ids, max = 12) {
    const all = (state.status && state.status.equipment) || [];
    const mine = all.filter((e) => ids.includes(e.id));
    const st = new Set(mine.map((e) => stationOf(e.location)));
    const near = all.filter((e) => !ids.includes(e.id) && st.has(stationOf(e.location)));
    return [...mine, ...near].slice(0, Math.max(max, mine.length)).sort((x, y) => all.indexOf(x) - all.indexOf(y));
  }

  window.PKVis = { trends, bindTrends, plantMap, around, valueBars, sequenceBody, bindSequence, loadTrend, symbolFor };
})();
