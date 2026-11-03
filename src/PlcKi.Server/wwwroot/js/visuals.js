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
  const hhmmss = (ms) => new Date(ms).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
  function num(v, tag) {
    if (v == null || !isFinite(v)) return "–";
    const d = tag && tag.decimals != null ? tag.decimals : Math.abs(v) >= 100 ? 0 : Math.abs(v) >= 10 ? 1 : 2;
    return Number(v).toFixed(d);
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

  // ---------- trend data, fetched while a page that shows it is open ----------
  let trend = null, trendAt = 0, trendBusy = false, trendMin = null;
  const TKEY = "plcki.trendTags";
  let picked = (() => { try { return JSON.parse(localStorage.getItem(TKEY) || "null"); } catch { return null; } })();
  function savePicked() { try { picked ? localStorage.setItem(TKEY, JSON.stringify(picked)) : localStorage.removeItem(TKEY); } catch { /* private window */ } }
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

  function chartRow(sr, tr) {
    const tag = tagOf(sr.id) || {};
    const vals = sr.points.map((p) => p[1]).filter((v) => v != null);
    const unit = sr.unit ? " " + esc(sr.unit) : "";
    let lo = Math.min(...vals), hi = Math.max(...vals);
    if (!vals.length) { lo = 0; hi = 1; }
    if (hi - lo < 1e-9) { const pad = Math.abs(hi) * 0.1 || 1; lo -= pad; hi += pad; }
    const y = (v) => 92 - ((v - lo) / (hi - lo)) * 84;      // a little room at top and bottom
    let d = "", area = "", started = false, firstX = 0, lastX = 0;
    for (const [t, v] of sr.points) {
      if (v == null) { started = false; continue; }
      const px = (x(t, tr) * 10).toFixed(1), py = y(v).toFixed(1);
      if (!started) { d += `M${px} ${py}`; started = true; if (!area) firstX = px; } else d += `L${px} ${py}`;
      lastX = px;
    }
    // the last value holds until now
    const last = vals.length ? vals[vals.length - 1] : null;
    if (last != null && started) { d += `L1000 ${y(last).toFixed(1)}`; lastX = 1000; }
    area = d ? `${d}L${lastX} 100L${firstX} 100Z` : "";
    const marks = tr.alarms.map((a) => `<i class="tr-al ${a.came ? "came" : "went"}" style="left:${x(a.time, tr).toFixed(2)}%" title="${esc(hhmmss(a.time) + " " + a.text)}"></i>`).join("");
    const t = hover != null ? tr.from + hover * (tr.to - tr.from) : null;
    const hv = t != null ? valueAt(sr.points, t) : null;
    return `<div class="tr-row">
      <div class="tr-lab"><span class="tr-name" title="${esc(sr.id)}">${esc(sr.label)}</span>
        <b class="tr-now">${num(hover != null ? hv : last, tag)}${unit}</b>
        <span class="tr-rng">${vals.length ? `${num(Math.min(...vals), tag)} to ${num(Math.max(...vals), tag)}` : "no values yet"}</span>
        <button type="button" class="tr-x" data-tr-remove="${esc(sr.id)}" aria-label="Remove ${esc(sr.label)} from the chart" title="Remove from the chart">×</button></div>
      <div class="tr-plot" data-plot>
        <svg viewBox="0 0 1000 100" preserveAspectRatio="none" aria-hidden="true">
          <line x1="0" y1="50" x2="1000" y2="50" class="tr-grid"/>
          ${area ? `<path class="tr-area" d="${area}"/><path class="tr-line" d="${d}" vector-effect="non-scaling-stroke"/>` : ""}
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
      const to = i === seq.spans.length - 1 ? tr.to : s.to;       // the step it is in now runs to "now"
      const l = Math.max(0, x(s.from, tr)), w = Math.max(0.3, x(to, tr) - l);
      const name = stepName(def, s.step);
      return `<span class="tr-span ${stepColour(def, s.step)}" style="left:${l.toFixed(2)}%;width:${w.toFixed(2)}%" title="${esc(`Step ${s.step} ${name}: ${dur(to - s.from)}, from ${hhmmss(s.from)}`)}">${w > 4 ? s.step : ""}</span>`;
    }).join("");
    const t = hover != null ? tr.from + hover * (tr.to - tr.from) : null;
    const at = t != null ? [...seq.spans].reverse().find((s) => s.from <= t) : null;
    const show = at ? at.step : now ? now.step : null;
    return `<div class="tr-row band ${label ? "" : "nolab"}">
      ${label ? `<div class="tr-lab"><span class="tr-name">${esc(seq.name)}</span><b class="tr-now small">${show != null ? `Step ${show}` : "–"}</b><span class="tr-rng">${esc(show != null ? stepName(def, show) : "")}</span></div>` : ""}
      <div class="tr-plot band" data-plot>${spans || `<span class="tr-empty">No step values yet</span>`}
        ${hover != null ? `<i class="tr-cur" style="left:${(hover * 100).toFixed(2)}%"></i>` : ""}</div></div>`;
  }

  function axisRow(tr, label = true) {
    const span = tr.to - tr.from;
    const ticks = [0, 0.25, 0.5, 0.75, 1].map((f) => {
      const ago = (1 - f) * span;
      const txt = f === 1 ? "now" : span <= 180000 ? "−" + Math.round(ago / 1000) + " s" : "−" + (Math.round(ago / 6000) / 10).toString().replace(/\.0$/, "") + " min";
      return `<span class="tr-tick" style="left:${f * 100}%">${txt}</span>`;
    }).join("");
    const t = hover != null ? tr.from + hover * span : null;
    return `<div class="tr-row axis ${label ? "" : "nolab"}">${label ? `<div class="tr-lab"></div>` : ""}<div class="tr-plot axis">${ticks}
      ${t != null ? `<span class="tr-tick at" style="left:${(hover * 100).toFixed(2)}%">${hhmmss(t)}</span>` : ""}</div></div>`;
  }

  function windowChoice(tr) {
    const keep = tr.keepMinutes;
    const opts = [...new Set([5, 15, 60, keep].filter((m) => m <= keep))];
    const cur = trendMin || keep;
    return opts.length > 1 ? `<div class="segmented" role="group" aria-label="Time shown">${opts.map((m) =>
      `<button type="button" class="${m === cur ? "on" : ""}" data-tr-min="${m}" aria-pressed="${m === cur}">${m < 60 ? m + " min" : m / 60 + " h"}</button>`).join("")}</div>` : "";
  }

  /** "Last 15 minutes": the measured values stacked over one time axis, the step band and alarm marks. */
  function trends() {
    const tr = needTrend();
    if (!tr) return `<p class="muted">Loading the values…</p>`;
    const short = (trendMin || tr.keepMinutes) * 60000 - (tr.to - tr.from) > 30000;
    const note = short ? `PLC-Ki started watching ${dur(tr.to - tr.from)} ago, so the charts start there.` : "";
    const ids = tr.series.map((s) => s.id);
    return `<div class="trends">
      <div class="toolbar tr-tools">${windowChoice(tr)}
        <input type="search" list="tr-choices" id="tr-add" placeholder="Add a value to the chart" aria-label="Add a value to the chart" autocomplete="off">
        <datalist id="tr-choices">${tr.choices.filter((c) => !ids.includes(c.id)).map((c) => `<option value="${esc(c.id)}">${esc(c.label)}${c.unit ? " (" + esc(c.unit) + ")" : ""}</option>`).join("")}</datalist>
        ${picked ? `<button type="button" class="linkbtn" data-tr-reset>Back to the usual values</button>` : ""}
      </div>
      <div class="tr-rows" data-tr-rows>
        ${tr.series.length ? tr.series.map((s) => chartRow(s, tr)).join("") : `<p class="muted">No measured values in this machine model to draw. Add one above.</p>`}
        ${tr.steps.map((q) => bandRow(q, tr)).join("")}
        ${axisRow(tr)}
      </div>
      <p class="faint small" style="margin:10px 0 0">${note ? esc(note) + " " : ""}Move along a chart to read the values at that time. Red marks: an alarm came. Grey marks: it went. PLC-Ki keeps ${tr.keepMinutes} minutes, in memory only.</p>
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
    page.querySelectorAll("[data-tr-min]").forEach((b) => b.onclick = () => { trendMin = Number(b.dataset.trMin); loadTrend(true); });
    page.querySelectorAll("[data-tr-remove]").forEach((b) => b.onclick = () => {
      picked = (trend ? trend.series.map((s) => s.id) : []).filter((id) => id !== b.dataset.trRemove);
      savePicked(); loadTrend(true);
    });
    const r = page.querySelector("[data-tr-reset]");
    if (r) r.onclick = () => { picked = null; savePicked(); loadTrend(true); };
    const add = page.querySelector("#tr-add");
    if (add) add.onchange = () => {
      const v = add.value.trim();
      const c = trend && trend.choices.find((x) => x.id === v || x.label.toLowerCase() === v.toLowerCase());
      if (!c) { if (v) PK.toast("No number value with that name. Pick one from the list."); return; }
      picked = [...new Set([...(trend ? trend.series.map((s) => s.id) : []), c.id])];
      add.value = "";
      savePicked(); loadTrend(true);
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
    if (!spans.length) return `<p class="muted">No step values in the last ${tr.keepMinutes} minutes yet.</p>`;
    const st = stepStats(spans, tr);
    const rows = def.steps.filter((s) => st.has(s.no));
    const maxAvg = Math.max(1, ...rows.map((s) => { const e = st.get(s.no); return e.done ? e.total / e.done : 0; }));
    const changes = spans.length - 1;
    return `<div class="tr-rows" data-tr-rows>${bandRow({ id: def.id, name: def.name, spans }, tr, false)}${axisRow(tr, false)}</div>
      <p class="faint small" style="margin:8px 0 18px">${changes ? `${changes} step change${changes > 1 ? "s" : ""}` : "No step change"} in the last ${Math.round((tr.to - tr.from) / 60000)} minutes. Move along the band to see which step ran when.</p>
      <table class="tbl steptimes"><tr><th>Step</th><th>Ran</th><th>Last</th><th>Average</th><th class="bar-h"></th></tr>
      ${rows.map((s) => {
        const e = st.get(s.no), avg = e.done ? e.total / e.done : null;
        return `<tr><td><button type="button" class="linkbtn" data-gs-go="${s.no}"><b>${s.no}</b> ${esc(s.name)}</button></td>
          <td>${e.n}×</td><td>${e.last != null ? dur(e.last) : "–"}${e.open ? ` <span class="now-tag">now</span>` : ""}</td><td>${avg != null ? dur(avg) : "–"}</td>
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
    const time = isCur ? (q.timeInStep ? "now, " + q.timeInStep : "now") : e && e.last != null ? "took " + dur(e.last) : "";
    const condsOf = (t) => {
      const l = live.find((n) => n.to === t.to);
      if (l) return `<ul class="conds gt-conds">${l.conditions.map(cond).join("")}</ul>`;
      const cs = t.conditions || [];
      if (!cs.length) return `<span class="gt-text faint">no condition</span>`;
      const shown = sel ? cs : cs.slice(0, 1);
      return `<span class="gt-text">${shown.map((c) => esc(fill(c.text))).join("; ")}${cs.length > shown.length ? ` <span class="faint">+${cs.length - shown.length} more</span>` : ""}</span>`;
    };
    const ready = (t) => { const l = live.find((n) => n.to === t.to); return l ? (l.ready ? "ready" : l.ready === false ? "wait" : "") : ""; };
    const from = incoming.get(s.no) || [];
    return `<li class="gs ${isCur ? "cur" + (q.halted ? " halted" : "") : ""} ${sel ? "sel" : ""} ${i === 0 ? "init" : ""} ${main ? "link" : "end"}" id="gs-${s.no}">
      ${from.length ? `<div class="gs-in">${from.map((f) => `<button type="button" class="linkbtn" data-gs-go="${f}">from ${f}</button>`).join(", ")} <span aria-hidden="true">↴</span></div>` : ""}
      <button type="button" class="gs-box" data-gs="${s.no}" aria-expanded="${sel}"><b class="gs-no">${s.no}</b><span class="gs-name">${esc(s.name)}</span>${time ? `<span class="gs-t">${esc(time)}</span>` : ""}</button>
      ${sel ? `<div class="gs-more">${s.does ? `<p><span class="muted">This step should:</span> ${esc(fill(s.does))}</p>` : ""}${s.limit ? `<p class="faint">${esc(fill(s.limit))}</p>` : ""}
        ${e ? `<p class="faint small">In the last minutes: ran ${e.n}×${e.done ? `, on average ${dur(e.total / e.done)}, longest ${dur(e.max)}` : ""}.</p>` : ""}
        ${!s.does && !s.limit && !e ? `<p class="faint small">No more is known about this step.</p>` : ""}</div>` : ""}
      ${jumps.map((t) => `<div class="gt jump ${ready(t)}"><i class="gt-bar" aria-hidden="true"></i>${condsOf(t)}
        <button type="button" class="gt-to" data-gs-go="${t.to}" title="Jump to step ${t.to}">↷ ${t.to} ${esc(stepName(def, t.to))}</button></div>`).join("")}
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

  PK.view("sequence", {
    live: true,
    needsValues: true,          // to show settings such as {LevelA} with their value
    mount(page, params) {
      selStep = params[1] != null ? Number(params[1]) : null;
      jumpTo = selStep;
      seqShown = params[0];
      loadTrend(true);
      PK.loadProgram && PK.loadProgram().then(PK.draw);
      PK.draw();
    },
    leave() { hover = null; },
    render(params) {
      const s = state.status, m = state.model;
      if (!s || !m) return hero({ title: "Loading the sequence…", sub: "One moment." });
      const def = seqDef(params[0]) || m.sequences[0];
      if (!def) return hero({ title: "This machine has no sequence.", sub: "The imported program has no GRAPH sequence or step chain.", actions: `<a class="btn" href="#/plant">Back to the plant</a>` });
      const q = s.sequences.find((x) => x.id === def.id);
      const tr = needTrend();
      const spans = tr ? ((tr.steps.find((x) => x.id === def.id) || {}).spans || []) : [];
      const stats = tr ? stepStats(spans, tr) : new Map();
      const [stText, stCls] = !q ? ["", ""] : q.halted ? ["Halted", "bad"] : q.next.some((n) => n.ready === false) ? ["Waiting", "warn"] : ["Running", "ok"];
      const others = m.sequences.filter((d) => d.id !== def.id);
      const blk = (def.block || "").split(/[ ,(]/)[0];
      return hero({
        cls: "hero compact",
        eyebrow: `<a class="crumb" href="#/plant">Plant</a> › ${esc(def.name)}${q ? ` <span class="pill ${stCls}"><span class="dot"></span>${stText}</span>` : ""}`,
        title: q && q.step != null ? `Step ${q.step}: ${esc(q.stepName)}` : esc(def.name),
        sub: esc(q ? q.explanation : ""),
        actions: `${q && q.step != null ? `<button class="btn" data-gs-go="${q.step}">Go to step ${q.step} in the chart</button>` : ""}
          ${blk ? `<a class="btn b" href="#/logic/${encodeURIComponent(blk)}">Show the logic</a>` : ""}
          <button class="btn b" data-ask="Explain what ${esc(def.name)} does, step by step, and where it is now.">Ask</button>`,
        extra: others.length ? `<div class="seq-others small">Other sequences: ${others.map((d) => `<a href="#/sequence/${encodeURIComponent(d.id)}">${esc(d.name)}</a>`).join(" · ")}</div>` : "",
      })
        + sec("alt", `<h2>Step timeline</h2><p class="sub">Which step ran when, and how long each one took.</p>${tr ? timeline(def, spans, tr) : `<p class="muted">Loading…</p>`}`)
        + sec("", `<h2>Step chart</h2><p class="sub">${def.steps.length} steps, read like the GRAPH editor: each step, the transition under it, and jumps (↷) to other steps. Tap a step for its details.</p>
            <div class="gwrap">${stepChart(def, q, stats)}</div>`);
    },
    after(page, params) {
      page.querySelectorAll("[data-gs]").forEach((b) => b.onclick = () => {
        const no = Number(b.dataset.gs);
        selStep = selStep === no ? null : no;
        PK.setHash(`#/sequence/${encodeURIComponent(params[0] || "")}${selStep != null ? "/" + selStep : ""}`);
        PK.draw();
      });
      page.querySelectorAll("[data-gs-go]").forEach((b) => b.onclick = () => {
        selStep = Number(b.dataset.gsGo); jumpTo = selStep;
        PK.setHash(`#/sequence/${encodeURIComponent(params[0] || "")}/${selStep}`);
        PK.draw();
      });
      if (jumpTo != null) {
        const el = page.querySelector(`#gs-${jumpTo}`);
        // after this redraw has put the page back where it was
        if (el) { jumpTo = null; setTimeout(() => el.scrollIntoView({ block: "center", behavior: "instant" }), 0); }
      }
      bindTrends(page);
      page.querySelectorAll("[data-ask]").forEach((b) => b.onclick = () => window.PKAsk && window.PKAsk.open(b.dataset.ask));
    },
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
  /** The station a device belongs to, from its location: FB_VFD, instance "Station".M201 → Station;
   *  FB_Station (heater interlock line) → Station (programmed inside that block). */
  function stationOf(loc) {
    const m = /instance\s+"([^"]+)"/.exec(loc || "") || /"([^"]+)"/.exec(loc || "");
    if (m) return m[1];
    const b = /^\s*(?:FB|FC)_?([A-Za-z0-9_]+)/.exec(loc || "");
    return b ? b[1] : "";
  }
  /** Every device as a symbol in its state colour, grouped by station. Uses the Plant page's [data-eq] selection. */
  function plantMap(equipment, selected) {
    if (!equipment.length) return "";
    const groups = new Map();
    equipment.forEach((e) => { const g = stationOf(e.location); groups.set(g, [...(groups.get(g) || []), e]); });
    const many = groups.size > 1;
    const tile = (e) => {
      const lvl = e.blockedBy ? "blocked" : e.level;
      return `<div class="pm-dev lv-${esc(lvl)} ${e.id === selected ? "sel" : ""}" data-eq="${esc(e.id)}" role="button" tabindex="0" aria-label="${esc(e.name)}, ${esc(e.state)}" title="${esc(e.name)}: ${esc(e.state)}">
        <span class="pm-sym">${symbolFor(e.kind, e.id)}</span><b>${esc(e.id)}</b><span class="pm-st">${esc(e.blockedBy ? "blocked" : e.state)}</span></div>`;
    };
    return `<div class="pmap">${[...groups].map(([g, list]) => `<div class="pm-grp">${many ? `<div class="pm-h">${esc(g || "Other devices")}</div>` : ""}<div class="pm-devs">${list.map(tile).join("")}</div></div>`).join("")}
      <div class="pm-key small"><span class="lv-ok">running or fine</span><span class="lv-idle">off</span><span class="lv-blocked">asked for but blocked</span><span class="lv-warn">warning</span><span class="lv-fault">fault</span></div></div>`;
  }

  window.PKVis = { trends, bindTrends, plantMap, loadTrend, symbolFor };
})();
