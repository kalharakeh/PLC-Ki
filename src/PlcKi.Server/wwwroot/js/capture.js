// The seconds before an alarm (fast capture): every change of the model tags around the moment an alarm came,
// recorded much finer than the normal reads (the PLC sends each change, usually sampled every 100 ms).
// Shown on the Active alarm page and in History › Recordings (#/history/recordings lists them, #/history/recordings/<id> shows one;
// the old #/capture addresses lead there).
// Data: /api/fast, /api/captures and /api/captures/<id>. Plain HTML and SVG, like js/visuals.js.
"use strict";

(() => {
  const { esc, icon, state, sec, hero } = PK;

  // ---------- data ----------
  let list = null, fast = null, listAt = 0, busy = false;
  const details = {};          // id -> capture (a capture never changes once cut)
  const loading = new Set();
  async function loadList() {
    if (busy) return;
    busy = true; listAt = Date.now();
    try {
      [list, fast] = await Promise.all([PK.api("GET", "/api/captures?max=100"), PK.api("GET", "/api/fast")]);
    } catch { list = list || []; }
    busy = false;
    PK.draw();
  }
  /** For render(): the capture list and fast-capture status, refreshed every 4 s while a page shows them. */
  function needList() {
    if (Date.now() - listAt > 4000) setTimeout(loadList, 0);
    return list;
  }
  function detail(id) {
    if (details[id] || loading.has(id)) return details[id] || null;
    loading.add(id);
    PK.api("GET", "/api/captures/" + encodeURIComponent(id))
      .then((c) => { details[id] = c; }, () => { details[id] = { missing: true }; })
      .finally(() => { loading.delete(id); PK.draw(); });
    return null;
  }

  /** The capture of this alarm closest to when it came (within 30 s), or null. */
  function forAlarm(alarmId, since) {
    if (!list) return null;
    const t = since ? Date.parse(since) : null;
    const mine = list.filter((c) => c.alarmId === alarmId);
    if (!mine.length) return null;
    if (t == null) return mine[0];
    const best = mine.reduce((a, b) => Math.abs(Date.parse(a.time) - t) <= Math.abs(Date.parse(b.time) - t) ? a : b);
    return Math.abs(Date.parse(best.time) - t) < 30000 ? best : null;
  }

  // ---------- drawing ----------
  const ms = (v) => v < 1000 ? Math.round(v) + " ms" : (v / 1000).toFixed(v < 10000 ? 2 : 1).replace(/\.?0+$/, "") + " s";
  const isOn = (v) => v === true || (typeof v === "number" && v !== 0);
  const tagOf = (id) => state.model && state.model.tags.find((t) => t.id === id);

  function geom(c) {
    const span = c.before + c.after;
    return { x: (t) => Math.min(100, Math.max(0, ((t + c.before) / span) * 100)), span };
  }

  function axis(c, g) {
    const step = c.before + c.after > 20 ? 5 : c.before + c.after > 8 ? 2 : 1;
    const ticks = [];
    for (let t = -c.before; t <= c.after + 1e-9; t += step) ticks.push(t);
    return `<div class="tr-row axis"><div class="tr-lab"></div><div class="tr-plot axis cap-axis">
      ${ticks.map((t) => `<span class="cap-tick ${t === 0 ? "zero" : ""}" style="left:${g.x(t).toFixed(2)}%">${t === 0 ? "alarm" : (t > 0 ? "+" : "−") + Math.abs(t) + " s"}</span>`).join("")}</div></div>`;
  }

  /** Value at t (seconds from the alarm): the start value, then each change up to t. */
  function valueAt(k, t) {
    let v = k.start, txt = k.startText;
    for (const p of k.changes) { if (p.t > t) break; v = p.value; txt = p.text; }
    return { v, txt };
  }

  function boolPlot(k, c, g, pulses) {
    const bars = [];
    let from = -c.before, on = isOn(k.start);
    const segs = [...k.changes.map((p) => [p.t, isOn(p.value)]), [c.after, null]];
    for (const [t, next] of segs) {
      if (on && t > from) {
        const l = g.x(from), w = Math.max(0.5, g.x(t) - l);
        const pulse = pulses.some((p) => p.tag === k.tag && p.on && Math.abs(p.t - from) < 1e-3);
        bars.push(`<span class="cap-on ${pulse ? "pulse" : ""}" style="left:${l.toFixed(2)}%;width:${w.toFixed(2)}%" title="${esc(`on from ${fmtT(from)} for ${ms((t - from) * 1000)}`)}"></span>`);
      }
      if (next != null) { on = next; from = t; }
    }
    // a short off-gap inside an on-stretch gets a marker too
    const offPulses = pulses.filter((p) => p.tag === k.tag && !p.on)
      .map((p) => `<span class="cap-off-pulse" style="left:${g.x(p.t).toFixed(2)}%" title="${esc(`off for ${ms(p.ms)} at ${fmtT(p.t)}`)}"></span>`);
    return bars.join("") + offPulses.join("");
  }

  function numPlot(k, c, g) {
    const tag = tagOf(k.tag) || {};
    const vals = [k.start, ...k.changes.map((p) => p.value)].filter((v) => typeof v === "number" && isFinite(v));
    if (!vals.length) return "";
    let lo = Math.min(...vals), hi = Math.max(...vals);
    if (hi - lo < 1e-9) { const pad = Math.abs(hi) * 0.1 || 1; lo -= pad; hi += pad; }
    const y = (v) => (92 - ((v - lo) / (hi - lo)) * 84).toFixed(1);
    let d = "", last = typeof k.start === "number" ? k.start : null;
    if (last != null) d = `M0 ${y(last)}`;
    for (const p of k.changes) {
      if (typeof p.value !== "number") continue;
      const px = (g.x(p.t) * 10).toFixed(1);
      d += d ? `L${px} ${y(last ?? p.value)}L${px} ${y(p.value)}` : `M${px} ${y(p.value)}`;
      last = p.value;
    }
    if (last != null) d += `L1000 ${y(last)}`;
    const dec = tag.decimals != null ? tag.decimals : Math.abs(hi) >= 100 ? 0 : Math.abs(hi) >= 10 ? 1 : 2;
    return `<svg viewBox="0 0 1000 100" preserveAspectRatio="none" aria-hidden="true"><path class="tr-line" d="${d}" vector-effect="non-scaling-stroke"/></svg>
      <span class="tr-y hi">${hi.toFixed(dec)}</span><span class="tr-y lo">${lo.toFixed(dec)}</span>`;
  }

  const fmtT = (t) => t === 0 ? "the alarm" : (t < 0 ? ms(-t * 1000) + " before" : ms(t * 1000) + " after");

  function track(k, c, g) {
    const at = valueAt(k, -0.0005);
    const isBool = k.type === "Bool";
    const plot = isBool ? boolPlot(k, c, g, c.pulses) : numPlot(k, c, g);
    const gaps = (c.gaps || []).map((x) => { const l = g.x(x.from), r = g.x(x.to ?? c.after); return `<span class="cap-gap" style="left:${l.toFixed(2)}%;width:${Math.max(0.5, r - l).toFixed(2)}%" title="No data: PLC link down"></span>`; }).join("");
    const n = k.changes.length;
    return `<div class="tr-row ${isBool ? "cap-bool" : ""}">
      <div class="tr-lab"><span class="tr-name" title="${esc(k.symbol + (n ? ", " + n + " change" + (n > 1 ? "s" : "") : ", no change"))}">${k.related ? `<b class="cap-rel" title="In this alarm's own check">●</b> ` : ""}${esc(k.label)}</span>
        <b class="tr-now small" title="Value just before the alarm">${esc(at.txt)}</b>
        <span class="tr-rng">${n ? `${n} change${n > 1 ? "s" : ""}` : "no change"}</span></div>
      <div class="tr-plot ${isBool ? "cap-b" : ""}">${plot}${gaps}<i class="tr-al came cap-zero" style="left:${g.x(0).toFixed(2)}%"></i></div></div>`;
  }

  let showAll = {};
  /** The picture and the plain-words summary of one capture. */
  function body(c, { max = 8, full = false } = {}) {
    const g = geom(c);
    const all = showAll[c.id] || full;
    const tracks = all ? c.tracks : c.tracks.slice(0, max);
    const rest = c.tracks.length - tracks.length;
    return `<ul class="cap-sum">${c.summary.slice(1).map((s) => `<li>${esc(s)}</li>`).join("")}</ul>
      <div class="cap-key small muted"><span><i class="k-on"></i>on</span><span><i class="k-pulse"></i>short pulse (shorter than one normal read)</span><span><i class="k-zero"></i>alarm came</span><span><b class="cap-rel">●</b> in the alarm's own check</span></div>
      <div class="tr-rows cap-rows">${axis(c, g)}${tracks.map((k) => track(k, c, g)).join("")}${tracks.length > 6 ? axis(c, g) : ""}</div>
      ${rest > 0 ? `<button type="button" class="mt-3 btn plain small" data-cap-all="${esc(c.id)}">Show all ${c.tracks.length} tags that changed</button>` : ""}
      <p class="mt-3 mb-0 faint small">${esc(c.summary[0] || "")}</p>`;
  }

  function bind(page) {
    page.querySelectorAll("[data-cap-all]").forEach((b) => b.onclick = () => { showAll[b.dataset.capAll] = true; PK.draw(); });
  }

  // ---------- on the Active alarm page ----------
  /** The recording of the alarm shown on Active alarm, once loaded; null when there is none (yet). */
  function captureOf(a) {
    const l = needList();
    if (!l || a.atStart) return null;
    const info = forAlarm(a.id, a.since);
    const c = info && detail(info.id);
    return c && !c.missing ? c : null;
  }

  /** The values just before the alarm came, by symbol, for the rung on Active alarm: operand -> { raw, text, symbol,
   *  label } like the Program page's resolver. A block's "#member" is read as <instance>.member. null without a recording. */
  function atAlarm(a, instance) {
    const c = captureOf(a);
    if (!c) return null;
    const by = new Map(c.tracks.map((k) => [PK.norm(k.symbol), k]));
    return (op) => {
      const sym = op.startsWith("#") ? (instance ? instance + "." + op.slice(1) : null) : op;
      const k = sym && by.get(PK.norm(sym));
      if (!k) return null;
      const { v, txt } = valueAt(k, -0.0005);
      return { raw: k.type === "Bool" ? isOn(v) : v, text: txt, symbol: k.symbol, label: k.label };
    };
  }

  /** On Active alarm: the seconds before it came as a small chart, only when there is a recording. */
  function alarmSection(a) {
    const c = captureOf(a);
    if (!c) return "";
    const g = geom(c);
    const tracks = c.tracks.slice().sort((x, y) => (y.related ? 1 : 0) - (x.related ? 1 : 0)).slice(0, 5);
    return sec("", `<div class="mb-2 row"><h2 class="m-0">The seconds before it came</h2><span class="spacer"></span>
        <a class="btn b small" href="#/history/recordings/${encodeURIComponent(c.id)}">Open full size</a>
        <button class="btn b small" data-ask="Look at the seconds before alarm ${esc(a.id)} came (get_alarm_capture). What changed right before it, and what does that say about the cause?">Ask what it shows</button></div>
      <p class="mb-3 sub">${esc(c.summary[1] || c.summary[0] || "")}</p>
      <div class="tr-rows cap-rows cap-mini">${tracks.map((k) => track(k, c, g)).join("")}${axis(c, g)}</div>
      ${c.tracks.length > tracks.length ? `<p class="mt-2 mb-0 faint small">${c.tracks.length - tracks.length} more tags changed. Open full size to see them.</p>` : ""}`);
  }

  // ---------- History › Recordings: the list, and one recording on its own page ----------
  function modeLine() {
    if (!fast) return "";
    if (fast.mode === "subscription")
      return `<span class="pill ok"><span class="dot"></span>Fast capture on</span> <span class="muted small">${fast.fastTags} tags, every change sampled every ${fast.cycleMs} ms by the PLC. Keeps the last ${Math.round(fast.keepSeconds)} s; ${fast.beforeSeconds} s before and ${fast.afterSeconds} s after each alarm are saved.</span>`;
    return `<span class="pill warn"><span class="dot"></span>Normal reads only</span> <span class="muted small">Changes shorter than ${ms(fast.readMs)} can be missing${fast.note ? ": " + esc(fast.note) : "."}</span>`;
  }

  /** One recording on its own page (#/history/recordings/<id>): its own header, then every change. */
  function page(id) {
    const c = detail(id);
    if (!c) return hero({ title: "Alarm recording" }) + PK.sec("", PK.ui.loading("chart"));
    if (c.missing) return hero({ title: "This recording is no longer kept.", sub: "PLC-Ki keeps the last 100 alarm recordings.", actions: `<a class="btn" href="#/history/recordings">All recordings</a>` });
    return hero({
      eyebrow: `<span class="pill ${c.severity === "WARNING" ? "warn" : "bad"}"><span class="dot"></span>${esc(c.severity)} · ${esc(c.alarmId)}</span>${c.blip ? ` <span class="pill warn">Blip: on for ${ms(c.onMs)}</span>` : ""}`,
      title: esc(c.alarmText),
      sub: `Came at ${esc(PK.date(c.time, true))}. Every change from ${c.before} s before to ${c.after} s after.`,
      actions: `<a class="btn b" href="#/alarms/${encodeURIComponent(c.alarmId)}">About this alarm</a><a class="btn b" href="#/history/recordings">All recordings</a>
        <button class="btn" data-ask="Look at capture ${esc(c.id)} of alarm ${esc(c.alarmId)} (get_alarm_capture). What changed right before it, and what does that say about the cause?">Ask what it shows</button>`,
    }) + sec("", body(c, { full: true }));
  }

  /** The list of recordings, newest first, for the History tab. */
  function listHtml() {
    const l = needList();
    const rows = (l || []).map((c) => `<li><a class="cap-li" href="#/history/recordings/${encodeURIComponent(c.id)}">
        <span class="ev-time">${esc(PK.date(c.time, true))}</span>
        <span class="ev-kind ${c.blip ? "start" : "came"}">${c.blip ? "Blip" : "Came"}</span>
        <span style="flex:1;min-width:180px"><b>${esc(c.alarmId)}</b> ${esc(c.alarmText)}<br><span class="muted small">${esc(c.headline || "")}</span></span>
        ${c.pulses ? `<span class="pill warn small">${c.pulses} short pulse${c.pulses > 1 ? "s" : ""}</span>` : ""}<span class="chev">›</span></a></li>`).join("");
    return `<p class="sub">The seconds before each alarm came: what changed, in which order, and short signals the normal reads miss. The last 100 are kept.</p>
      <p class="mt-0 mb-4">${modeLine()}</p>`
      + (!l ? PK.ui.loading("list") : rows ? `<ul class="list events">${rows}</ul>`
        : PK.ui.empty({ icon: "clock", title: "No alarm has come since PLC-Ki started recording.", text: "When one comes, the seconds before it are kept here." }));
  }

  function wirePage(el) {
    bind(el);
    el.querySelectorAll("[data-ask]").forEach((b) => b.onclick = () => window.PKAsk && window.PKAsk.open(b.dataset.ask));
  }

  window.PKCapture = { alarmSection, atAlarm, bind, needList, forAlarm, modeLine, page, listHtml, wirePage };
})();
