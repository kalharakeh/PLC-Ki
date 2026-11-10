// PLC-Ki app core: routing, live data, small helpers. Plain JavaScript, no libraries, so a plant PC
// without internet shows the same page. Views register with PK.view(name, {...}) in the other files.
"use strict";

const PK = (() => {
  /** The assistant's name in PLC-Ki (it runs on Claude): "Ki is writing the blocks…". */
  const AI = "Ki";
  const $ = (id) => document.getElementById(id);
  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  // ---- icons (SF Symbols-like line icons) ----
  const P = {
    bell: '<path d="M6 16V11a6 6 0 1 1 12 0v5l2 2H4l2-2Z"/><path d="M10 20a2 2 0 0 0 4 0"/>',
    plant: '<path d="M3 21V10l5 3V10l5 3V6h4l1 15"/><path d="M3 21h18"/><path d="M7 17h2M12 17h2"/>',
    list: '<path d="M9 6h11M9 12h11M9 18h11"/><circle cx="4.5" cy="6" r="1.2"/><circle cx="4.5" cy="12" r="1.2"/><circle cx="4.5" cy="18" r="1.2"/>',
    rung: '<path d="M3 4v16M21 4v16M3 12h4M17 12h4"/><path d="M8 8v8M11 8v8M15 9a3 3 0 0 1 0 6"/><path d="M11 12h2"/>',
    code: '<path d="m8 7-5 5 5 5M16 7l5 5-5 5M14 4l-4 16"/>',
    table: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M3 10h18M3 15h18M9 10v10"/>',
    link: '<path d="M10 14a4 4 0 0 0 5.66 0l3-3a4 4 0 0 0-5.66-5.66l-1 1"/><path d="M14 10a4 4 0 0 0-5.66 0l-3 3a4 4 0 0 0 5.66 5.66l1-1"/>',
    import: '<path d="M12 3v12M7 10l5 5 5-5"/><path d="M4 17v3h16v-3"/>',
    spark: '<path d="M12 3v4M12 17v4M3 12h4M17 12h4M6 6l2.5 2.5M15.5 15.5 18 18M6 18l2.5-2.5M15.5 8.5 18 6"/>',
    gear: '<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1Z"/>',
    chat: '<path d="M21 12a8 8 0 0 1-11.6 7.1L4 20l1-4.6A8 8 0 1 1 21 12Z"/>',
    check: '<path d="m5 12 5 5 9-10"/>',
    x: '<path d="M6 6l12 12M18 6 6 18"/>',
    warn: '<path d="M12 3 2 20h20L12 3Z"/><path d="M12 10v4M12 17v.5"/>',
    info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v6M12 7.5v.5"/>',
    valve: '<path d="M3 8v8l9-4-9-4ZM21 8v8l-9-4 9-4Z"/><path d="M12 12V5M9 5h6"/>',
    motor: '<circle cx="12" cy="12" r="7"/><path d="M12 9v6M9.5 10.5 12 9l2.5 1.5"/><path d="M19 12h3"/>',
    pump: '<circle cx="11" cy="13" r="6"/><path d="M11 7h9v4"/><path d="m8 16 3-6 3 6"/>',
    heater: '<path d="M8 4c-2 3 2 5 0 8s2 5 0 8M12 4c-2 3 2 5 0 8s2 5 0 8M16 4c-2 3 2 5 0 8s2 5 0 8"/>',
    sensor: '<path d="M12 21a5 5 0 0 0 3-9V5a3 3 0 0 0-6 0v7a5 5 0 0 0 3 9Z"/><path d="M12 17v-6"/>',
    device: '<rect x="4" y="4" width="16" height="16" rx="4"/><path d="M9 12h6M12 9v6"/>',
    pc: '<rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/>',
    laptop: '<rect x="5" y="5" width="14" height="10" rx="1.5"/><path d="M2 19h20l-2-4H4l-2 4Z"/>',
    server: '<rect x="4" y="3" width="16" height="7" rx="2"/><rect x="4" y="14" width="16" height="7" rx="2"/><path d="M8 6.5h.01M8 17.5h.01"/>',
    vm: '<rect x="3" y="4" width="18" height="14" rx="2"/><rect x="7" y="8" width="10" height="6" rx="1"/><path d="M8 21h8"/>',
    rack: '<rect x="3" y="4" width="18" height="16" rx="1.5"/><path d="M8 4v16M12 4v16M16 4v16"/><path d="M5 7h1M10 7h0.5M14 7h0.5M18 7h0.5"/>',
    cpu: '<rect x="5" y="5" width="14" height="14" rx="2"/><rect x="9" y="9" width="6" height="6"/><path d="M9 2v3M15 2v3M9 19v3M15 19v3M2 9h3M2 15h3M19 9h3M19 15h3"/>',
    tia: '<path d="M4 6h16M4 12h10M4 18h7"/><path d="m16 15 3 3 3-6"/>',
    play: '<path d="M7 4v16l13-8L7 4Z"/>',
    stop: '<rect x="6" y="6" width="12" height="12" rx="2"/>',
    download: '<path d="M12 4v11M7 10l5 5 5-5M4 20h16"/>',
    file: '<path d="M14 3H6v18h12V7l-4-4Z"/><path d="M14 3v4h4"/>',
    eye: '<path d="M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12Z"/><circle cx="12" cy="12" r="3"/>',
    clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
    shield: '<path d="M12 3 4 6v6c0 5 3.5 8 8 9 4.5-1 8-4 8-9V6l-8-3Z"/>',
    search: '<circle cx="11" cy="11" r="7"/><path d="m20 20-4-4"/>',
    user: '<circle cx="12" cy="8" r="4"/><path d="M4 21c1-4 4-6 8-6s7 2 8 6"/>',
    plus: '<path d="M12 5v14M5 12h14"/>',
    pen: '<path d="M4 20h4L19 9l-4-4L4 16v4z"/><path d="m14 6 4 4"/>',
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>',
    moon: '<path d="M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5Z"/>',
    help: '<circle cx="12" cy="12" r="9"/><path d="M9.5 9.5a2.5 2.5 0 1 1 3.5 2.3c-.6.3-1 .9-1 1.6V14M12 17.5v.01"/>',
    sound: '<path d="M4 9v6h4l5 4V5L8 9H4Z"/><path d="M16.5 8.5a5 5 0 0 1 0 7M19 6a8.5 8.5 0 0 1 0 12"/>',
    key: '<circle cx="8" cy="15" r="4"/><path d="m11 12 9-9M17 6l3 3M15 8l2 2"/>',
    chart: '<path d="M3 20h18"/><path d="M6 16v-5M11 16V6M16 16v-8M21 16v-3"/>',
    logout: '<path d="M15 4h4v16h-4"/><path d="M10 8l-4 4 4 4M6 12h10"/>',
    book: '<path d="M4 5a2 2 0 0 1 2-2h13v15H6a2 2 0 0 0-2 2V5Z"/><path d="M4 20a2 2 0 0 0 2 2h13v-4"/><path d="M9 7h6"/>',
    more: '<circle cx="5" cy="12" r="1.6"/><circle cx="12" cy="12" r="1.6"/><circle cx="19" cy="12" r="1.6"/>',
    chevron: '<path d="m9 6 6 6-6 6"/>',
    drawing: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M7 9h4v6H7zM11 12h6M17 9v6"/>',
  };
  const icon = (name, cls = "") =>
    `<svg class="pk-i ${cls}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${P[name] || P.device}</svg>`;
  const fillIcons = (root = document) => root.querySelectorAll("[data-icon]").forEach((el) => {
    if (!el.firstElementChild) el.innerHTML = icon(el.dataset.icon);
  });
  const kindIcon = (kind) => ({ valve: "valve", motor: "motor", vfd: "motor", drive: "motor", pump: "pump", heater: "heater",
    analog: "sensor", sensor: "sensor", transmitter: "sensor" }[String(kind || "").toLowerCase()] || "device");

  // ---- server calls ----
  async function api(method, url, body, raw) {
    const opt = { method, headers: {} };
    if (raw) opt.body = raw;
    else if (body !== undefined) { opt.headers["Content-Type"] = "application/json"; opt.body = JSON.stringify(body); }
    const r = await fetch(url, opt);
    if (r.status === 401) { location.href = "/login.html?next=" + encodeURIComponent(location.pathname + location.hash); throw new Error("Please log in."); }
    const text = await r.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch { data = text; }
    if (!r.ok) throw new Error((data && data.error) || `Request failed (${r.status}).`);
    return data;
  }

  // ---- shared state ----
  const state = { status: null, model: null, values: null, info: null, user: null, program: null, programError: null, lastOk: 0, jobs: [] };

  // ---- What this login may do (review 55). The server checks the same (Roles.cs); this only hides what would be refused.
  //   settings: Setup, logins, imports, PLCSIM Start/Stop · plcsim: change and hold values on PLCSIM
  //   real: change values on a real PLC · docs: add documents and change their details · notes: alarm and handover notes
  const ROLE_CAN = {
    admin: ["settings", "plcsim", "real", "docs", "notes"],
    technician: ["plcsim", "docs", "notes"],
    operator: ["notes"],
  };
  const can = (what) => !!(state.user && (ROLE_CAN[state.user.role] || []).includes(what));
  const ROLE_TITLE = { admin: "Administrator", technician: "Technician", operator: "Operator" };
  const roleTitle = (role) => ROLE_TITLE[role] || "Operator";
  const views = {};
  let current = null;       // { name, view, params }
  let lastHtml = "";
  let tabsIntoView = false;
  // Page building blocks: sections with a centred column on one calm background. The old "alt" and "dark"
  // band colours are dropped here, so every page reads as one work surface with cards (UX review 2).
  const sec = (cls, html) => `<section class="${cls.replace(/\b(alt|dark)\b/g, "").replace(/\s+/g, " ").trim()}"><div class="wrap">${html}</div></section>`;
  /** The one page header: (before, e.g. a row of sub-pages) eyebrow, a plain title, one line under it, the main actions. */
  const hero = ({ eyebrow = "", title, sub = "", actions = "", cls = "hero", extra = "", extra0 = "", before = "" }) =>
    sec(cls, `${before}${eyebrow ? `<div class="eyebrow">${eyebrow}</div>` : ""}<h1>${title}</h1>${sub ? `<p class="sub">${sub}</p>` : ""}${extra0}${actions ? `<div class="actions">${actions}</div>` : ""}${extra}`);

  // ---- shared building blocks (UX review 3, item 32): every page draws these the same way ----
  // Labels are HTML (a page may add a count); everything a page did not write itself must be escaped by the page.
  const attrs = (o) => Object.entries(o || {}).filter(([, v]) => v != null && v !== false).map(([k, v]) => v === true ? k : `${k}="${esc(v)}"`).join(" ");
  const ui = {
    /** Tabs move to another view of the same page: [{ label, href | data, on, count, countCls, sub }].
     *  Links when there is an href, buttons with data-* attributes otherwise. */
    tabs(label, items, { cls = "" } = {}) {
      // One tab is in the Tab order (the open one); the arrow keys move between them (item 37, see the keydown below)
      const first = items.some((t) => t.on) ? -1 : 0;
      return `<nav class="tabs ${cls}" aria-label="${esc(label)}"><div class="tabs-in" role="tablist">${items.map((t, i) => {
        const inner = `<span class="tab-l">${t.label}${t.count ? ` <span class="count ${t.countCls || "warn"}">${t.count}</span>` : ""}</span>${t.sub ? `<span class="tab-sub">${t.sub}</span>` : ""}`;
        const a = attrs({ role: "tab", class: t.on ? "on" : null, "aria-selected": String(!!t.on), "aria-current": t.on && t.href ? "page" : null, tabindex: t.on || i === first ? "0" : "-1", title: t.title, ...t.data });
        return t.href ? `<a href="${esc(t.href)}" ${a}>${inner}</a>` : `<button type="button" ${a}>${inner}</button>`;
      }).join("")}</div></nav>`;
    },
    /** Choose one value: a row of pills. [{ label, on, href | data, title, hot }]. Buttons say aria-pressed; links aria-current.
     *  data: data-* attributes on the row itself, for a page that listens on it. */
    choice(label, items, { cls = "", data } = {}) {
      return `<div class="segmented ${cls}" role="group" aria-label="${esc(label)}"${data ? " " + attrs(data) : ""}>${items.map((o) => {
        const c = [o.on ? "on" : "", o.hot ? "hot" : ""].filter(Boolean).join(" ") || null;
        return o.href
          ? `<a href="${esc(o.href)}" ${attrs({ class: c, "aria-current": o.on ? "true" : null, title: o.title })}>${o.label}</a>`
          : `<button type="button" ${attrs({ class: c, "aria-pressed": String(!!o.on), title: o.title, disabled: o.disabled, ...o.data })}>${o.label}</button>`;
      }).join("")}</div>`;
    },
    /** Number tiles: [{ value (or html), label, note, state: "ok" | "warn" | "bad" }]. small: a row of small facts; boxed: report cards;
     *  two: two across, for long words such as order numbers. */
    tiles(items, { small = false, boxed = false, two = false, cls = "" } = {}) {
      return `<div class="kpis ${small ? "kpis-s" : ""} ${boxed ? "boxed" : ""} ${two ? "two" : ""} ${cls}">${items.map((t) =>
        `<div class="k ${t.state || ""}"${t.title ? ` title="${esc(t.title)}"` : ""}><b>${t.html != null ? t.html : esc(t.value)}</b><span>${esc(t.label)}</span>${t.note ? `<span class="k-note">${esc(t.note)}</span>` : ""}</div>`).join("")}</div>`;
    },
    /** Nothing to show: an icon, one bold line, what it means or what to do, an optional button. */
    /** While data comes: grey placeholders in the shape of what will be there, so nothing jumps (item 34).
     *  shape: "lines" (text), "list" (rows with an icon), "table", "cards", "tiles" (numbers), "chart" (a drawing or plot). */
    loading(shape = "lines", { rows = 4, label = "Loading" } = {}) {
      const b = (cls) => `<span class="sk-b ${cls}"></span>`;
      const n = (f) => Array.from({ length: rows }, f).join("");
      const inner = {
        lines: () => b("w90") + b("w75") + b("w50"),
        list: () => n(() => `<span class="sk-row">${b("sk-ico")}<span class="sk-col">${b("w60")}${b("w40 thin")}</span></span>`),
        table: () => b("w100 thin") + n(() => b("w100")),
        cards: () => n(() => `<span class="sk-card">${b("w60")}${b("w90 thin")}${b("w75 thin")}</span>`),
        tiles: () => n(() => `<span class="sk-tile">${b("w50 big")}${b("w75 thin")}</span>`),
        chart: () => b("sk-chart"),
      }[shape] || (() => b("w90"));
      return `<div class="sk sk-${shape}" role="status" aria-busy="true"><span class="sr">${esc(label)}…</span>${inner()}</div>`;
    },
    /** Something went wrong: what happened and what to do, in plain words; the system's own text folded under "Details".
     *  level "warn" for "not there yet" (no program loaded), "bad" for a failure. */
    error(message, { level = "bad", action = "" } = {}) {
      const raw = String((message && message.message) || message || "");
      const p = plainFor(raw);
      return `<div class="notice ${level === "warn" ? "gold" : "red"} pk-error" role="alert">${icon("warn")}<div><b>${esc(p.text)}</b>${p.todo ? ` ${esc(p.todo)}` : ""}
        ${p.text !== raw ? `<details class="pk-detail"><summary>Details</summary><code>${esc(raw)}</code></details>` : ""}${action ? `<div class="row mt-2">${action}</div>` : ""}</div></div>`;
    },
    empty({ icon: ic = "check", title, text = "", action = "", cls = "" }) {
      return `<div class="empty ${cls}">${icon(ic)}<b>${title}</b>${text ? `<span>${text}</span>` : ""}${action}</div>`;
    },
    /** The machine's states, the same words and colours on every page: [name, colour]. */
    states: {
      running: ["Running", "var(--ok)"], idle: ["Waiting or stopped", "var(--steel)"], down: ["Down (fault)", "var(--fault)"],
      nolink: ["No PLC connection", "var(--warn)"], off: ["Not watched", "var(--line-strong)"],
    },
    /** What a mark on the timeline stands for (EventLog.Mark on the server). Colour only where it is a state. */
    marks: { alarm: "Alarm came", warn: "Warning came", halt: "Halted", stop: "PLC in STOP", link: "PLC connection lost", write: "Value changed" },
    /**
     * The machine's state over a time as one coloured bar (running, waiting, down, no PLC connection, not watched), time
     * under it, and marks above it for what happened: [{ time, mark, text }]. Each mark is a button with data-tlmk="i";
     * sel is the index shown as picked. Hours under a day or so; days (Mon 9) over longer times.
     */
    timeline({ spans, from, to, marks = [], sel = -1, label = "Machine state over time" }) {
      const a = new Date(from).getTime(), b = new Date(to).getTime(), len = Math.max(1, b - a);
      const at = (t) => ((new Date(t).getTime() - a) / len * 100);
      const hm = (t) => { const d = new Date(t); return `${pad(d.getHours())}:${pad(d.getMinutes())}`; };
      const days = len > 36 * 3600000;
      const dayName = (t) => { const d = new Date(t); return `${["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"][d.getDay()]} ${d.getDate()}`; };
      const segs = spans.map((s) => {
        const [name, col] = ui.states[s.state] || [s.state, "var(--muted)"];
        const w = (new Date(s.to).getTime() - new Date(s.from).getTime()) / len * 100;
        const tip = `${days ? dayName(s.from) + " " : ""}${hm(s.from)}–${hm(s.to)} ${name}${s.why ? ": " + s.why : ""}`;
        return `<span class="rp-seg rp-${esc(s.state)}" style="left:${at(s.from).toFixed(3)}%;width:${Math.max(w, 0.15).toFixed(3)}%;background:${col}" title="${esc(tip)}"></span>`;
      }).join("");
      const ticks = [];
      if (days) {
        const d = new Date(a); d.setHours(24, 0, 0, 0);
        for (let t = d.getTime(); t < b; t += 86400000) ticks.push(`<span class="rp-tick" style="left:${at(t).toFixed(2)}%">${dayName(t)}</span>`);
      } else {
        const first = new Date(a); first.setMinutes(0, 0, 0);
        const hours = len / 3600000, step = hours > 12 ? 4 : hours > 6 ? 2 : 1;
        for (let t = first.getTime() + 3600000; t < b; t += 3600000)
          if (!(new Date(t).getHours() % step)) ticks.push(`<span class="rp-tick" style="left:${at(t).toFixed(2)}%">${hm(t)}</span>`);
      }
      // A window that ends now says "now" at its end; a shift still running shows where now is
      const nowT = Date.now(), endIsNow = Math.abs(b - nowT) < 120000;
      let endLabel = endIsNow ? "now" : hm(b);
      if (!endIsNow && nowT > a && nowT < b) {
        if (at(nowT) > 80) endLabel = "";      // "now" close to the end: the end time would sit on it
        const x = at(nowT);
        for (let i = ticks.length - 1; i >= 0; i--) if (Math.abs(+ticks[i].match(/left:([\d.]+)%/)[1] - x) < 6) ticks.splice(i, 1);   // no overlap
        ticks.push(`<span class="rp-tick rp-now" style="left:${x.toFixed(2)}%">now</span>`);
      }
      const mk = marks.map((m, i) => `<button type="button" class="tl-mk tl-${esc(m.mark)}${i === sel ? " on" : ""}" style="left:${at(m.time).toFixed(3)}%" data-tlmk="${i}"
        title="${esc(`${days ? dayName(m.time) + " " : ""}${clock(m.time)} ${m.text}`)}" aria-label="${esc(`${clock(m.time)} ${ui.marks[m.mark] || m.mark}: ${m.text}`)}"></button>`).join("");
      return `<div class="tl">${marks.length ? `<div class="tl-marks">${mk}</div>` : ""}<div class="rp-bar" role="img" aria-label="${esc(label)}">${segs}</div>
        <div class="rp-ticks"><span>${days ? dayName(a) : hm(a)}</span>${ticks.join("")}${endLabel ? `<span class="rp-end">${endLabel}</span>` : ""}</div></div>`;
    },
    /** Each kind of machine event in plain words (the same names as the shift log's spreadsheet). */
    kinds: { alarm: "Alarm", step: "Step", halt: "Halt", blocked: "Blocked", output: "Output", state: "Device state", cpu: "PLC mode",
      link: "PLC connection", write: "Value changed", monitor: "PLC-Ki" },
    /** Machine events as lines: time, kind, what happened (an alarm id links to its page). day: show the day too.
     *  hit: a time (ms); lines of that moment are marked. */
    events(list, { cls = "", day = false, hit = null } = {}) {
      const t = (x) => day ? date(x) : clock(x);
      const text = (e) => e.alarm && e.text.startsWith(e.alarm)
        ? `<a href="#/alarms/${encodeURIComponent(e.alarm)}">${esc(e.alarm)}</a>${esc(e.text.slice(e.alarm.length))}` : esc(e.text);
      return `<ul class="tl-list ${cls}">${list.map((e) => `<li${hit != null && new Date(e.time).getTime() === hit ? ` class="hit"` : ""}><span class="tl-dot${e.mark ? " tl-" + esc(e.mark) : ""}"></span><time datetime="${esc(e.time)}">${t(e.time)}</time>
        <span class="tl-k">${esc(ui.kinds[e.kind] || e.kind)}</span><span class="tl-t">${text(e)}</span></li>`).join("")}</ul>`;
    },
    /** Search, filters and actions over a list: { search: { id, placeholder, value, label }, left, right }. */
    toolbar({ search, left = "", right = "", cls = "" }) {
      const s = search ? `<input type="search" id="${esc(search.id)}" placeholder="${esc(search.placeholder)}" aria-label="${esc(search.label || search.placeholder)}" value="${esc(search.value || "")}">` : "";
      return `<div class="toolbar ${cls}">${s}${left}${right ? `<span class="spacer"></span>${right}` : ""}</div>`;
    },
  };

  // ---- screen sizes: the same three as ui.css (phone up to 600 px, tablet up to 1100 px, desktop above) ----
  const BP = { phone: 600, tablet: 1100 };
  const screenSize = () => innerWidth <= BP.phone ? "phone" : innerWidth <= BP.tablet ? "tablet" : "desktop";

  function view(name, def) { views[name] = def; }

  function parseRoute() {
    const h = location.hash.replace(/^#\/?/, "");
    const parts = h.split("/").filter(Boolean).map(decodeURIComponent);
    if (parts[0] === "setup") return { name: "setup/" + (parts[1] || "connections"), params: parts.slice(2) };
    return { name: parts[0] || "alarm", params: parts.slice(1) };
  }

  // Pages that moved keep their old addresses (bookmarks, notes, QR labels): Hardware is PLC › Racks and I/O,
  // the alarm recordings and the fault ranking are History tabs. #/sequence leads to Plant › Sequence (visuals.js).
  const MOVED = [
    [/^#\/hardware(?=\/|$)/, "#/plc/racks"],
    [/^#\/capture(?=\/|$)/, "#/history/recordings"],
    [/^#\/reports\/faults(?:\/.*)?$/, "#/history/ranking/downtime"],
  ];
  const movedTo = (hash) => { for (const [re, to] of MOVED) if (re.test(hash)) return hash.replace(re, to); return null; };

  function go(hash) { if (location.hash !== hash) location.hash = hash; else route(); }

  // ---- unsaved changes: a bar with Discard / Save, and a question before leaving the page ----
  let dirty = null;          // { save } while the page has changes that are not saved
  let shownHash = location.hash;
  function setDirty(save) {
    dirty = save ? { save } : null;
    const bar = $("dirty-bar");
    if (bar) bar.classList.toggle("on", !!dirty);
    document.documentElement.classList.toggle("has-dirty", !!dirty);
  }
  async function onHash() {
    if (dirty && location.hash !== shownHash) {
      const target = location.hash;
      history.replaceState(current ? { pkBack: current.back || null } : null, "", shownHash || location.pathname);
      const leave = await confirmSheet({ title: "Leave without saving?", text: "Your changes on this page are not saved yet.", ok: "Leave without saving", cancel: "Stay", danger: true });
      if (!leave) return;
      setDirty(null);
      location.hash = target;
      return;
    }
    route();
  }

  function route() {
    setDirty(null);
    const moved = movedTo(location.hash);
    if (moved) history.replaceState(history.state, "", moved);
    const leftHash = shownHash;
    shownHash = location.hash;
    let { name, params } = parseRoute();
    let v = views[name];
    if (!v) { params = [name, ...params]; name = "notfound"; v = views.notfound; }   // say so, instead of showing Active alarm
    const back = backFor(leftHash);
    const navName = v.nav || name;   // a page can light another menu item (Stations lights Plant)
    document.querySelectorAll("#nav a").forEach((a) => a.classList.toggle("active", a.dataset.route === navName || (a.dataset.route === "setup" && name.startsWith("setup/") && name !== "setup/generator")));
    // Phone: a page that lives under "More" lights the More tab
    const more = $("nav-more");
    if (more) more.classList.toggle("active", !!document.querySelector("#nav a.more-item.active"));
    const page = $("page");
    if (current && current.view.leave) current.view.leave();
    current = { name, view: v, params, back };
    showBack();
    lastHtml = "";
    page.innerHTML = "";
    quiet(2000);            // a new page: its first values are not "changes"
    tabsIntoView = true;    // and its open tab is shown, also at the end of a long row on a phone
    if (v.mount) v.mount(page, params);
    else draw();
    page.scrollTop = 0;
    window.scrollTo(0, 0);
    showAccess();
    showHandover();
    if (can("plcsim") && (!state.changes || Date.now() - state.changes.at > 30000)) loadChanges();
    setTitle();
  }

  // ---- What PLC-Ki may change: one answer for the top badge, its tooltip, the footer, the connection sheet and Help ----
  // Administrators ask the server (/api/admin/write/state: the same checks a change makes, minus the tag).
  // Operators and visitors can never change anything, so they need no question.
  let changesBusy = false;
  async function loadChanges() {
    if (changesBusy || !can("plcsim")) return;
    changesBusy = true;
    try { state.changes = { ...(await api("GET", "/api/admin/write/state")), at: Date.now() }; }
    catch { /* keeps the last answer */ }
    changesBusy = false;
    showAccess();
  }

  /**
   * What may be changed on this page, for this login: { badge, short, level, text }.
   * level: "" reads only · "except" this page can change something · "real" changes are allowed on a real PLC.
   * badge is null while an administrator's answer is not known yet (the badge stays hidden rather than guess).
   */
  function access(name = current ? current.name : "alarm") {
    const reads = (text) => ({ badge: "Reads only", short: "Reads only", level: "", text });
    if (!state.user) return reads("Without a login, PLC-Ki only reads the PLC.");
    if (!can("plcsim")) return reads("With an operator login, PLC-Ki only reads the PLC.");
    const ch = state.changes;
    if (!ch) return { badge: null, short: null, level: "", text: "" };
    const plc = ch.plc || "the PLC";
    const valuePage = name === "tags" || name === "program";
    let a;
    if (name === "setup/generator") a = { badge: "Loads blocks here", short: "Blocks", level: "except",
      text: "The block generator on this page loads blocks into PLCSIM, or into a real PLC after two people confirm." };
    else if (valuePage && ch.values === "plcsim") a = { badge: "Can change values here", short: "Can change", level: "except",
      text: "On this page you can change a value with the pen, on PLCSIM: inputs, outputs, memory and data values, safety ones after a warning, never F-blocks. Every change is logged." };
    else if (valuePage && ch.values === "real") a = { badge: "Can change values here", short: "Can change", level: "except",
      text: `On this page you can change memory and data values on the real PLC ${plc} with the pen, after typing a reason and the PLC address; never inputs, outputs or values the safety program reads. Every change is logged.` };
    else if (name === "plc" && ch.startStop && !["racks", "diagnostics"].includes((current && current.params[0]) || "")) a = { badge: "Can start and stop PLCSIM", short: "Start/Stop", level: "except",
      text: "Start and Stop on this page switch the PLCSIM CPU, after you confirm. A real PLC is never started or stopped." };
    else a = reads("This page only reads the PLC."
      + (ch.values === "plcsim" ? " On Tags and Program, you can change values on PLCSIM."
        : ch.values === "real" ? ` On Tags and Program, administrators can change values on the real PLC ${plc}.`
        : !can("real") && ch.plc ? " A technician login changes values on PLCSIM only." : ""));
    // A real PLC that may be changed is said on every page, in amber
    if (ch.values === "real") { a.badge = `Changes allowed on the real PLC ${plc}`; a.short = "Real PLC"; a.level = "real"; }
    return a;
  }

  function showAccess() {
    const a = access();
    const ro = $("ro-badge");
    if (ro) {
      ro.classList.toggle("hidden", !a.badge);
      ro.classList.toggle("except", a.level === "except");
      ro.classList.toggle("real", a.level === "real");
      const html = a.badge ? `<span class="ro-l">${esc(a.badge)}</span><span class="ro-s">${esc(a.short)}</span>` : "";
      if (ro.innerHTML !== html) ro.innerHTML = html;
      ro.title = a.text;
    }
    const f = $("foot-text");
    if (f && f.textContent !== a.text) f.textContent = a.text;
  }

  // ---- "← Back to A02": each history entry keeps the page it was opened from ----
  // Kept in history.state, so the browser's own Back and Forward and a reload stay right. A click on the main menu starts afresh.
  let fromMenu = false;
  /** Where the page now opening came from: { hash, label } or null. */
  function backFor(leftHash) {
    const hs = history.state;
    if (hs && "pkBack" in hs) { fromMenu = false; return hs.pkBack; }      // an entry seen before: Back, Forward, reload
    let back = null;
    if (current && !fromMenu) back = current.view.transient ? current.back : leftHash && leftHash !== location.hash ? { hash: leftHash, label: crumbOf(current.name, current.params) } : null;
    fromMenu = false;
    history.replaceState({ ...(hs || {}), pkBack: back }, "");
    return back;
  }
  /** A short name for a page, as the chip says it: "A02", "FB_Station", "Plant". */
  function crumbOf(name, params) {
    const page = (HELP[name] || [""])[0] || name;
    const p = (params || []).filter((x) => !/^(as-(rungs|text)|network-\d+|line-\d+|alarm-.+|@.*)$/.test(x));
    let label = page;
    if (name === "alarm") { const al = (state.status && state.status.activeAlarms) || []; if (al.length) label = al[0].id; }
    else if (name === "plant" && p[0] === "devices" && p[1]) label = p[1];
    else if (name === "tags" && p[0] === "db" && p[1]) label = p[1];
    else if (name === "plc" && p[0] === "racks" && p[1]) label = p[1];
    else if (name === "history" && p[0] && !["ranking", "recordings"].includes(p[0])) label = p[0] + " history";
    else if (["alarms", "program", "sequence", "station"].includes(name) && p[0]) label = p[0];
    return label.length > 28 ? label.slice(0, 27) + "…" : label;
  }
  function showBack() {
    const bar = $("back-bar");
    if (!bar) return;
    const b = current && current.back;
    bar.classList.toggle("hidden", !b);
    document.documentElement.classList.toggle("has-back", !!b);
    if (!b) return;
    const a = bar.querySelector("a");
    a.href = b.hash || "#/";
    a.querySelector(".back-l").textContent = "Back to " + b.label;
  }

  /** Change the address shown (e.g. #/alarms/A02 for an opened alarm) without leaving the page, so it can be shared. */
  function setHash(h) {
    if (location.hash === h) return;
    history.replaceState(history.state, "", h);     // keeps where the page came from
    shownHash = location.hash;
    if (current) current.params = parseRoute().params;
  }

  // ---- updating the page in place ----
  // A live page is rebuilt from its HTML every second or so. Swapping the whole page would lose focus, text
  // selection, an open <details> and the screen reader's place, so only what changed is touched.
  function morph(from, to) {
    const olds = [...from.childNodes], news = [...to.childNodes];
    news.forEach((n, i) => {
      const o = olds[i];
      if (!o) { from.appendChild(n); return; }
      // A different element in the same place (other tag, id or data-* hooks) is swapped, so no handler from the old one stays on it
      if (o.nodeType !== n.nodeType || (o.nodeType === 1 && (o.tagName !== n.tagName || o.id !== n.id || o.type !== n.type || hooks(o) !== hooks(n)))) { from.replaceChild(n, o); return; }
      if (o.nodeType !== 1) { if (o.nodeValue !== n.nodeValue) { o.nodeValue = n.nodeValue; if (o.nodeType === 3) changed(o.parentElement); } return; }
      syncAttrs(o, n);
      if (o.dataset.icon && !n.firstChild) return;          // its icon is filled in once
      if (o.tagName === "TEXTAREA") { if (document.activeElement !== o && o.value !== n.value) o.value = n.value; return; }
      morph(o, n);
      if (o.tagName === "SELECT" && document.activeElement !== o) {
        const sel = n.querySelector("option[selected]");
        if (sel && o.value !== sel.value) o.value = sel.value;
      }
    });
    for (let i = news.length; i < olds.length; i++) olds[i].remove();
  }
  // ---- live values: a short soft highlight when one changes (item 39) ----
  // Only measured values (.val in lists, value bars, tag and channel chips, anything marked data-live), and only on a
  // live update: not right after a click, a key or a page change, when the person changed what is shown themselves.
  // Timers ("45 s in this step") are not marked, so they do not blink every second. Nothing moves with reduced motion.
  const LIVE = ".val, .vb-h b, .vb-plain .k b, .chip, [data-live]";
  let quietUntil = 0;
  const quiet = (ms = 1500) => { quietUntil = Math.max(quietUntil, Date.now() + ms); };
  ["pointerdown", "keydown", "input"].forEach((t) => document.addEventListener(t, () => quiet(), true));
  const lessMotion = window.matchMedia ? matchMedia("(prefers-reduced-motion: reduce)") : { matches: false };
  function changed(el) {
    const v = el && el.closest(LIVE);
    if (!v || !v.animate || lessMotion.matches || Date.now() < quietUntil) return;
    const c = getComputedStyle(document.documentElement).getPropertyValue("--live-flash").trim();
    v.animate([{ backgroundColor: c }, { backgroundColor: "transparent" }], { duration: 1400, easing: "ease-out" });
  }
  const hooks = (el) => { let k = ""; for (const a of el.attributes) if (a.name.startsWith("data-")) k += a.name + "=" + a.value + ";"; return k; };
  function syncAttrs(o, n) {
    for (const a of [...o.attributes]) {
      if (o.tagName === "DETAILS" && a.name === "open") continue;      // the person opened or closed it
      if (!n.hasAttribute(a.name)) o.removeAttribute(a.name);
    }
    for (const a of n.attributes) {
      if (o.tagName === "DETAILS" && a.name === "open") continue;
      if (o.getAttribute(a.name) !== a.value) o.setAttribute(a.name, a.value);
    }
    if (o.tagName === "INPUT" && document.activeElement !== o) {
      if (o.type === "checkbox" || o.type === "radio") { if (o.checked !== n.hasAttribute("checked")) o.checked = n.hasAttribute("checked"); }
      else if (o.value !== (n.getAttribute("value") ?? "")) o.value = n.getAttribute("value") ?? "";
    }
  }
  /** Sets an event handler on an element that may be kept between updates, replacing the one set before. */
  function on(el, type, fn) {
    el._pk ||= {};
    if (el._pk[type]) el.removeEventListener(type, el._pk[type]);
    el._pk[type] = fn;
    el.addEventListener(type, fn);
  }

  // Live views return their HTML from render(); it is only put on the page when it changed
  function draw() {
    if (!current || !current.view.render) return;
    let html;
    try { html = current.view.render(current.params); }
    catch (e) { html = sec("", `<div class="notice red" role="alert">${icon("warn")}<div><b>This page hit a problem.</b> Reload the page. If it happens again, tell an administrator what you clicked.
      <details class="pk-detail"><summary>Details</summary><code>${esc(e.message)}</code></details></div></div>`); console.error(e); }
    if (html === lastHtml) return;
    const page = $("page");
    const y = window.scrollY;
    // A live update must not take the cursor out of a field someone is typing in
    const f = document.activeElement;
    let keep = null;
    if (f && f !== page && page.contains(f)) {
      const attr = f.id ? null : [...f.attributes].find((x) => x.name.startsWith("data-"));
      const sel = f.id ? "#" + CSS.escape(f.id) : attr ? `[${attr.name}="${CSS.escape(attr.value)}"]` : null;
      if (sel) keep = { sel, a: f.selectionStart, b: f.selectionEnd };
    }
    const t = document.createElement("template");
    t.innerHTML = html;
    morph(page, t.content);
    lastHtml = html;
    fillIcons(page);
    if (tabsIntoView) {
      tabsIntoView = false;
      page.querySelectorAll(".tabs-in").forEach((row) => {
        const on = row.querySelector(".on");
        if (on && (on.offsetLeft < row.scrollLeft || on.offsetLeft + on.offsetWidth > row.scrollLeft + row.clientWidth)) row.scrollLeft = on.offsetLeft - 16;
      });
    }
    if (current.view.after) current.view.after(page, current.params);
    if (keep) {
      const el = page.querySelector(keep.sel);
      if (el && el !== document.activeElement) {
        el.focus({ preventScroll: true });
        try { if (keep.a != null) el.setSelectionRange(keep.a, keep.b); } catch { /* not a text field */ }
      }
    }
    window.scrollTo(0, y);
  }

  // ---- live polling ----
  /** "updated 1 s ago" beside the connection pill (item 39): a frozen page counts up, a live one stays at a second or two. */
  function renderAge() {
    const el = $("conn-age");
    if (!el) return;
    const c = state.status && state.status.connection;
    if (!state.lastOk || !c || !["connected", "snapshot"].includes(c.state)) { el.textContent = ""; return; }
    const s = Math.max(1, Math.round((Date.now() - state.lastOk) / 1000));
    const text = s < 60 ? `updated ${s} s ago` : `updated ${ago(state.lastOk)}`;
    if (el.textContent !== text) el.textContent = text;
    el.classList.toggle("stale", s > 5);
  }
  setInterval(renderAge, 1000);

  function renderConn() {
    const pill = $("conn");
    const c = state.status && state.status.connection;
    const stale = Date.now() - state.lastOk > 5000;
    let text, cls;
    if (!state.status || stale) { text = "PLC-Ki not reachable"; cls = "bad"; }
    else {
      const map = {
        connected: ["Live|" + c.address, "ok"],
        snapshot: ["Test values|" + c.address.split(/[\\/]/).pop(), "warn"],
        connecting: ["Connecting|" + c.address, "warn"],
        disconnected: ["Not connected|" + c.address, "bad"],
        waiting: ["Waiting for Link|", "warn"],
        notset: ["No PLC set yet|", "warn"],
      };
      [text, cls] = map[c.state] || [c.state, "warn"];
    }
    pill.className = "pill " + cls;
    const [main, extra] = String(text).split("|");
    // Short in the bar; the address or test file is in the tooltip (a long file name wrapped into the menu).
    // A mode read that failed is only said here, quietly: it is not a STOP.
    const cpu = !stale && state.status && state.status.cpu;
    pill.title = (extra ? main + ": " + extra + ". " : "") + (cpu && cpu.error ? "PLC mode unknown. " : "") + "What does this mean?";
    renderCpuPill(stale);
    const html = `<span class="dot"></span><span>${esc(main)}</span>`;
    if (pill.innerHTML !== html) pill.innerHTML = html;
    const alarms = (state.status && state.status.activeAlarms) || [];
    for (const id of ["nav-alarm-badge", "nav-alarms-badge"]) {
      const b = $(id);
      if (!b) continue;
      b.textContent = alarms.length;
      b.classList.toggle("hidden", alarms.length === 0);
      b.classList.toggle("warn", alarms.length > 0 && alarms.every((a) => a.severity === "WARNING"));
    }
    if (state.status && $("machine").textContent !== state.status.machine) $("machine").textContent = state.status.machine;
    machineLine();
    renderBanner(stale);
    renderNotices(stale);
    setTitle();
  }

  /**
   * Notices the server sends with the status (review 43): test switches that are on in the PLC ("a fault may be caused
   * on purpose") and blocks in the PLC that differ from the imported program. Read-only: PLC-Ki only says it.
   * Forces set in TIA cannot be shown: the S7 driver cannot read the CPU's force list.
   */
  let noticesStale = false;
  function renderNotices(stale = noticesStale) {
    noticesStale = stale;
    const bar = $("notice-bar");
    if (!bar) return;
    const list = (!stale && state.status && state.status.notices) || [];
    const ho = handoverShown();     // the last shift's handover note: one entry in the same strip (review 47)
    const admin = can("settings");
    const html = list.map((n) => {
      const items = n.items || [];
      const shown = items.slice(0, 4).map((i) => `<span class="mono">${esc(i)}</span>`).join(", ") + (items.length > 4 ? `, and ${items.length - 4} more` : "");
      const link = n.link && (n.kind !== "program" || can("settings"))
        ? `<a class="btn gray small" href="${esc(n.link)}">${n.kind === "program" ? "Import again" : "Show"}</a>` : "";
      const who = n.kind === "program" && !admin ? " Tell an administrator." : "";
      return `<div class="notice gold notice-strip" role="status" data-kind="${esc(n.kind)}">${icon(n.kind === "program" ? "file" : "warn")}
        <div><b>${esc(n.text)}</b>${who}${shown ? `<div class="small">${shown}</div>` : ""}</div>${link}</div>`;
    }).join("");
    const all = (ho ? handoverHtml(ho) : "") + html;
    const full = all ? `<div class="wrap">${all}</div>` : "";
    if (bar.dataset.html !== full) { bar.innerHTML = full; bar.dataset.html = full; }
    bar.classList.toggle("hidden", !list.length && !ho);
  }

  /** Under the machine name in the top bar: the CPU and the program it was read from, as far as known. */
  function machineLine() {
    const i = state.info || {}, p = state.program || {};
    const plc = p.cpu ? p.cpu : i.plc && i.plc.address ? "PLC " + i.plc.address : "";
    const prog = p.archive || p.project || (i.model ? String(i.model).split(/[\\/]/).pop() : "");
    const t = [plc, prog && "program " + prog].filter(Boolean).join(" · ");
    const el = $("machine-sub");
    if (el && el.textContent !== t) { el.textContent = t; el.title = t; }
  }

  /** The CPU's mode when it is really not in RUN (STOP, startup, hold, defect), else null. A failed mode read is never STOP. */
  const cpuStop = (s = state.status) => (s && s.cpu && s.cpu.stopped ? s.cpu : null);
  /** "STOP since 09:12", "STOP since at least 09:12" (already so when PLC-Ki started watching), with the day when not today. */
  function cpuSince(c) {
    if (!c || !c.since) return "";
    const d = new Date(c.since), today = d.toDateString() === new Date().toDateString();
    return (c.atStart ? "since at least " : "since ") + (today ? `${pad(d.getHours())}:${pad(d.getMinutes())}` : date(c.since));
  }

  /** The red STOP chip next to the connection: only for a mode that was really read and is not RUN. */
  function renderCpuPill(stale) {
    const p = $("cpu-pill");
    if (!p) return;
    const c = !stale && cpuStop();
    p.classList.toggle("hidden", !c);
    if (!c) return;
    const html = `<span class="dot"></span><span>${esc(c.word || "STOP")}</span>`;
    if (p.innerHTML !== html) p.innerHTML = html;
    p.title = `The PLC is in ${c.word || "STOP"} ${cpuSince(c)}: the program is not running. Open the PLC page.`;
  }

  /** Says loudly when the values on the page are not live: PLC-Ki not answering, PLC not read, PLC in STOP, or test values. */
  function renderBanner(stale) {
    const c = state.status && state.status.connection;
    const stop = cpuStop();
    let text = "", kind = "", link = "";
    if (state.lastOk && stale) { text = `PLC-Ki is not answering. Values frozen since ${clock(state.lastOk)}.`; kind = "frozen"; }
    else if (c && (c.state === "disconnected" || c.state === "connecting" || c.state === "waiting") && c.lastGoodRead) {
      text = `No connection to the PLC. Values are from the last good read at ${clock(c.lastGoodRead)}.`; kind = "frozen";
    }
    else if (stop) {
      text = `PLC in ${stop.word || "STOP"} ${cpuSince(stop)}. The program is not running; what you see is its last state.`;
      kind = "stop"; link = `<a href="#/plc">PLC page</a>`;
    }
    else if (c && c.state === "snapshot") { text = "Test values from a file, not the real machine."; kind = "test"; }
    document.documentElement.classList.toggle("test-values", !!c && c.state === "snapshot" && !(state.lastOk && stale));
    document.documentElement.classList.toggle("cpu-stop", kind === "stop");
    const b = $("banner");
    if (!b) return;
    const html = text ? `<span class="dot"></span><span>${esc(text)}${link ? " " + link : ""}</span>` : "";
    // Compared with what was last written, not with innerHTML: the link must not be replaced under a click
    if (b.dataset.html !== html) { b.innerHTML = html; b.dataset.html = html; }
    b.className = "banner " + kind + (text ? "" : " hidden");
    document.documentElement.classList.toggle("frozen", kind === "frozen");
  }

  /**
   * Tab title and icon: a screen in another tab or on the wall shows the alarm, and two tabs stay apart by
   * their page name, e.g. "(1) A02 · Program · PLC-Ki" or "Plant · PLC-Ki". The icon turns red or amber.
   */
  function setTitle() {
    const al = (state.status && state.status.activeAlarms) || [];
    const page = current ? (HELP[current.name] || [""])[0] : "";
    const stop = Date.now() - state.lastOk <= 5000 && cpuStop();
    const t = [stop ? stop.word || "STOP" : "", al.length ? `(${al.length}) ${al[0].id}` : "", page, "PLC-Ki"].filter(Boolean).join(" · ");
    if (document.title !== t) document.title = t;
    const level = stop ? "fault" : !al.length ? "ok" : al.some((a) => a.severity === "FAULT") ? "fault" : "warn";
    if (setTitle.level !== level) {
      setTitle.level = level;
      const fill = { ok: "#2a64c9", fault: "#d93025", warn: "#c27c0e" }[level];
      const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32"><rect width="32" height="32" rx="8" fill="${fill}"/><path d="M9 9v14M9 16h6M15 9v14M19 9v14M19 16l6-7M19 16l6 7" stroke="#fff" stroke-width="2.6" stroke-linecap="round" fill="none"/></svg>`;
      let link = document.querySelector("link[rel=icon]");
      if (!link) { link = document.createElement("link"); link.rel = "icon"; document.head.append(link); }
      link.href = "data:image/svg+xml," + encodeURIComponent(svg);
    }
    alertNew(al);
    alertStop();
  }

  // ---- This screen: alert on new alarms, keep the screen on. Saved per browser. ----
  const screenOpt = (() => { try { return JSON.parse(localStorage.getItem("plcki.screen") || "{}"); } catch { return {}; } })();
  const saveScreenOpt = () => { try { localStorage.setItem("plcki.screen", JSON.stringify(screenOpt)); } catch { /* private window */ } };

  // ---- Spacing (item 36): Compact for control-room PCs, Touch for tablets and gloves. "auto" is Touch on a touch screen. ----
  const coarse = window.matchMedia ? matchMedia("(pointer: coarse)") : { matches: false, addEventListener() {} };
  const densityChoice = () => screenOpt.density || "auto";
  function applyDensity() {
    const c = densityChoice();
    document.documentElement.dataset.density = c === "auto" ? (coarse.matches ? "touch" : "normal") : c;
  }
  applyDensity();
  coarse.addEventListener("change", applyDensity);
  let seenAlarms = null;
  function alertNew(al) {
    if (!state.status || Date.now() - state.lastOk > 5000) return;
    const ids = new Set(al.map((a) => a.id));
    const fresh = seenAlarms ? al.filter((a) => !seenAlarms.has(a.id)) : [];
    seenAlarms = ids;
    if (!fresh.length) return;
    if (screenOpt.sound) beep(fresh.some((a) => a.severity === "FAULT"));
    if (screenOpt.notify && "Notification" in window && Notification.permission === "granted") {
      const a = fresh[0];
      try { const n = new Notification(`${a.severity} ${a.id}: ${a.text}`, { body: state.status.machine || "PLC-Ki", tag: "plcki-" + a.id }); n.onclick = () => { window.focus(); go("#/alarm"); }; } catch { /* not allowed here */ }
    }
  }
  // The CPU going to STOP alerts like a new fault alarm. The first status only sets where things are, as for alarms.
  let seenStop = null;
  function alertStop() {
    if (!state.status || Date.now() - state.lastOk > 5000) return;
    const c = cpuStop();
    const was = seenStop;
    seenStop = !!c;
    if (!c || was !== false) return;
    if (screenOpt.sound) beep(true);
    if (screenOpt.notify && "Notification" in window && Notification.permission === "granted") {
      try {
        const n = new Notification(`The PLC is in ${c.word || "STOP"}`, { body: `${state.status.machine || "PLC-Ki"}: the program is not running.`, tag: "plcki-cpu" });
        n.onclick = () => { window.focus(); go("#/alarm"); };
      } catch { /* not allowed here */ }
    }
  }
  let audio;
  function beep(fault) {
    try {
      audio = audio || new (window.AudioContext || window.webkitAudioContext)();
      const tones = fault ? [880, 660, 880] : [660];
      tones.forEach((f, i) => {
        const o = audio.createOscillator(), g = audio.createGain();
        o.frequency.value = f; o.connect(g); g.connect(audio.destination);
        const t0 = audio.currentTime + i * 0.22;
        g.gain.setValueAtTime(0.0001, t0); g.gain.exponentialRampToValueAtTime(0.25, t0 + 0.02); g.gain.exponentialRampToValueAtTime(0.0001, t0 + 0.18);
        o.start(t0); o.stop(t0 + 0.2);
      });
    } catch { /* no sound on this browser */ }
  }
  let wakeLock = null;
  async function keepAwake(on) {
    try {
      if (on && !wakeLock && "wakeLock" in navigator) { wakeLock = await navigator.wakeLock.request("screen"); wakeLock.addEventListener("release", () => { wakeLock = null; }); }
      if (!on && wakeLock) { await wakeLock.release(); wakeLock = null; }
    } catch { wakeLock = null; }
  }
  // The browser drops the lock when the tab is hidden; take it again when it comes back.
  document.addEventListener("visibilitychange", () => { if (screenOpt.awake && document.visibilityState === "visible") keepAwake(true); });

  /**
   * Set a password: one's own (asks for the current one) or, for an administrator, someone else's.
   * Asks twice so a typing mistake does not lock anyone out.
   */
  function passwordSheet(forName) {
    const own = !forName;
    const s = sheet(`<h2>${own ? "Change my password" : "New password for " + esc(forName)}</h2>
      <form class="gap-3 stack" data-pw-form>
        ${own ? `<label class="field">Current password<input type="password" id="pw-cur" autocomplete="current-password" required></label>` : ""}
        <label class="field">New password <span class="hint">at least 8 characters</span><input type="password" id="pw-new" autocomplete="new-password" minlength="8" required ${own ? "" : "autofocus"}></label>
        <label class="field">New password again<input type="password" id="pw-rep" autocomplete="new-password" required></label>
        <p class="m-0 ink-fault muted small" id="pw-err" role="alert"></p>
        <div class="row end"><button type="button" class="btn gray" data-close>Cancel</button><button type="submit" class="btn">Set password</button></div>
      </form>`);
    const f = s.el.querySelector("[data-pw-form]");
    const err = (t) => { s.el.querySelector("#pw-err").textContent = t; };
    if (own) setTimeout(() => s.el.querySelector("#pw-cur").focus(), 50);
    f.onsubmit = async (e) => {
      e.preventDefault();
      const pw = f.querySelector("#pw-new").value, rep = f.querySelector("#pw-rep").value;
      if (pw.length < 8) return err("The new password needs at least 8 characters.");
      if (pw !== rep) return err("The two new passwords are not the same. Type them again.");
      try {
        if (own) await api("POST", "/api/auth/password", { current: f.querySelector("#pw-cur").value, password: pw });
        else await api("PUT", `/api/admin/users/${encodeURIComponent(forName)}/password`, { password: pw });
        s.close();
        toast(own ? "Your password is changed." : `Password for ${forName} is changed.`);
      } catch (x) { err(x.message); }
    };
  }

  // ---- Light or dark: follows this PC unless chosen here (a bright wall screen may want dark) ----
  const sysDark = window.matchMedia ? window.matchMedia("(prefers-color-scheme: dark)") : { matches: false, addEventListener() {} };
  const themeChoice = () => document.documentElement.dataset.theme || "auto";
  const darkNow = () => themeChoice() === "dark" || (themeChoice() === "auto" && sysDark.matches);
  function setTheme(t) {
    if (t === "auto") delete document.documentElement.dataset.theme; else document.documentElement.dataset.theme = t;
    try { t === "auto" ? localStorage.removeItem("plcki.theme") : localStorage.setItem("plcki.theme", t); } catch { /* private window */ }
    showTheme();
  }
  function showTheme() {
    const b = $("theme-btn");
    if (!b) return;
    const dark = darkNow();
    b.innerHTML = icon(dark ? "sun" : "moon");
    b.title = dark ? "Switch to a light screen" : "Switch to a dark screen";
    b.setAttribute("aria-label", b.title);
  }
  if (sysDark.addEventListener) sysDark.addEventListener("change", showTheme);

  function screenSheet() {
    const secure = window.isSecureContext;
    const canNotify = secure && "Notification" in window;
    const canWake = secure && "wakeLock" in navigator;
    const row = (key, title, text, ok, why) => `<label class="opt-row ${ok ? "" : "off"}"><input type="checkbox" data-opt="${key}" ${screenOpt[key] && ok ? "checked" : ""} ${ok ? "" : "disabled"}>
      <span><b>${esc(title)}</b><br><span class="muted small">${esc(ok ? text : why)}</span></span></label>`;
    const notHere = "Not possible on this connection: the browser allows it only on the PLC-Ki PC itself (localhost) or over https.";
    const s = sheet(`<span class="eyebrow">This screen</span><h2>Alarm sound and screen</h2>
      <p class="m-0 muted">Only for this browser on this device. Nothing here changes the machine.</p>
      <div class="gap-3 stack">
        ${row("sound", "Beep when a new alarm comes", "A short tone: three beeps for a fault, one for a warning. Leave this page open in a tab.", true, "")}
        ${row("notify", "Show a notice when a new alarm comes", "A message from the browser, also when PLC-Ki is in a background tab.", canNotify, notHere)}
        ${row("awake", "Keep this screen on", "For a screen on the line: the display does not go dark while this page is open.", canWake, notHere)}
        <div class="opt-row" style="cursor:default"><span class="grow"><b>Screen colours</b><br><span class="muted small">Dark suits a wall screen in a bright hall.</span></span>
          ${ui.choice("Screen colours", [["auto", "Like this PC"], ["light", "Light"], ["dark", "Dark"]].map(([k, l]) => ({ label: l, on: themeChoice() === k, data: { "data-theme-set": k } })))}</div>
        <div class="opt-row" style="cursor:default"><span class="grow"><b>Spacing</b><br><span class="muted small">Compact fits more on a control-room PC. Touch makes buttons bigger for a tablet or gloves.</span></span>
          ${ui.choice("Spacing", [["auto", "Like this device"], ["compact", "Compact"], ["touch", "Touch"]].map(([k, l]) => ({ label: l, on: densityChoice() === k, data: { "data-density-set": k } })))}</div>
      </div>
      <div class="row end"><button class="btn plain" type="button" data-test-beep>Test the beep</button><button class="btn" data-close>Done</button></div>`);
    s.el.addEventListener("change", async (e) => {
      const box = e.target.closest("[data-opt]"); if (!box) return;
      const key = box.dataset.opt;
      if (key === "notify" && box.checked && Notification.permission !== "granted") {
        const p = await Notification.requestPermission().catch(() => "denied");
        if (p !== "granted") { box.checked = false; toast("The browser did not allow notices. Allow them in the browser's site settings.", "error"); return; }
      }
      screenOpt[key] = box.checked; saveScreenOpt();
      if (key === "awake") keepAwake(box.checked);
      if (key === "sound" && box.checked) beep(false);
    });
    s.el.querySelector("[data-test-beep]").onclick = () => beep(true);
    s.el.querySelectorAll("[data-theme-set]").forEach((b) => b.onclick = () => {
      setTheme(b.dataset.themeSet);
      s.el.querySelectorAll("[data-theme-set]").forEach((x) => { x.classList.toggle("on", x === b); x.setAttribute("aria-pressed", x === b); });
    });
    s.el.querySelectorAll("[data-density-set]").forEach((b) => b.onclick = () => {
      screenOpt.density = b.dataset.densitySet; saveScreenOpt(); applyDensity();
      s.el.querySelectorAll("[data-density-set]").forEach((x) => { x.classList.toggle("on", x === b); x.setAttribute("aria-pressed", x === b); });
    });
  }

  async function pollStatus() {
    try {
      const r = await fetch("/api/status", { cache: "no-store" });
      if (r.status === 401) { location.href = "/login.html?next=" + encodeURIComponent("/" + location.hash); return; }
      if (r.ok) { state.status = await r.json(); state.lastOk = Date.now(); }
      const run = r.headers.get("X-PlcKi-Run");
      if (run && state.run && run !== state.run) {
        // PLC-Ki was restarted (maybe with another model or program): start this page fresh
        if (!dirty) { location.reload(); return; }
        toast("PLC-Ki was restarted. Save, then reload the page to see the current program.", "error");
      }
      if (run) state.run = run;
    } catch { /* shown as not reachable */ }
    renderConn();
    if (current && current.view.live) draw();
    setTimeout(pollStatus, 1000);
  }

  async function pollValues() {
    if (current && current.view.needsValues) {
      try { state.values = await api("GET", "/api/values"); } catch { /* next time */ }
      draw();
    }
    setTimeout(pollValues, 2000);
  }

  const refreshing = new Set();
  async function pollJobs() {
    if (can("settings")) {
      try {
        state.jobs = await api("GET", "/api/admin/jobs");
        // A "Refresh from TIA" finished (also the one the block generator starts by itself): show the new program
        for (const j of state.jobs.filter((x) => x.kind === "refresh")) {
          if (j.state === "running") refreshing.add(j.id);
          else if (refreshing.delete(j.id) && j.state === "done") {
            state.program = null; state.programError = null;
            window.dispatchEvent(new Event("pk:program-changed"));
            loadProgram().then(draw);
          }
        }
        const run = state.jobs.find((j) => j.state === "running");
        const pill = $("job-pill");
        pill.classList.toggle("hidden", !run);
        if (run) {
          const text = run.waiting ? "Needs you: " + run.title : run.title;
          pill.innerHTML = `<span class="dot"></span><span class="jp-t">${esc(text)}</span><span class="jp-s">${run.waiting ? "Needs you" : "Job"}</span>`;
          pill.title = text;
          pill.className = "pill " + (run.waiting ? "bad" : "info");
          pill.style.cursor = "pointer";
          pill.onclick = () => go(run.kind === "develop" ? "#/setup/generator" : "#/setup/program");
        }
        if (current && current.view.onJobs) current.view.onJobs(state.jobs);
      } catch { /* next time */ }
    }
    setTimeout(pollJobs, 2500);
  }

  async function loadProgram() {
    if (state.program || state.programError) return state.program;
    try {
      const p = await api("GET", "/api/program");
      if (p.available) state.program = p; else state.programError = p.error;
    } catch (e) { state.programError = e.message; }
    return state.program;
  }

  /**
   * The block a model location points at. Real plants write "FC 0.4. Alarms Common, network 2" or
   * "FB 1.5. SequenceProgramRobot, transition X; latched in FC 0.5. Robot Sequence Alarms, network 7"; the demo writes
   * "FB_VFD, instance ...". With the program loaded, the names are matched against its blocks ("latched in" wins,
   * then the first and longest name); without it, the first word after an optional FB/FC/OB/DB.
   */
  function blockOf(text) {
    text = String(text || "");
    if (!text.trim()) return undefined;
    const blocks = (state.program && state.program.blocks) || [];
    if (blocks.length) {
      const hits = [];
      for (const b of blocks) {
        const n = b.name;
        if (!n) continue;
        let i = text.indexOf(n);
        while (i >= 0) {
          const before = i === 0 ? " " : text[i - 1], after = text[i + n.length] || " ";
          if (!/[\w.]/.test(before) && !/\w/.test(after) && !(after === "." && /\w/.test(text[i + n.length + 1] || ""))) { hits.push({ n, i }); break; }
          i = text.indexOf(n, i + 1);
        }
      }
      if (hits.length) {
        const latched = text.search(/latched in /i);
        const pick = (latched >= 0 && hits.filter((h) => h.i > latched)) || [];
        const list = pick.length ? pick : hits;
        list.sort((a, b) => a.i - b.i || b.n.length - a.n.length);
        return list[0].n;
      }
    }
    const m = text.match(/^\s*(?:(?:FB|FC|OB|DB)\s+)?("?)([^",;(]+?)\1\s*(?:[,;(]|$)/);
    const name = m ? m[2].trim() : "";
    return /^(FB|FC|OB|DB)$/i.test(name) ? undefined : name || undefined;
  }

  // ---- values by symbol, for live chips on the Program page ----
  const norm = (s) => String(s || "").replace(/\s+/g, "").toLowerCase();
  function valueBySymbol() {
    const map = new Map();
    for (const t of (state.values && state.values.tags) || []) map.set(norm(t.symbol), t);
    return map;
  }

  // ---- small UI helpers ----
  let toastTimer;
  /** A short message at the bottom. An error (text or Error) is said in plain words with what to do (item 33). */
  function toast(text, kind) {
    if (kind === "error") {
      const p = plainFor(text);
      if (p.text !== String((text && text.message) || text)) console.warn("PLC-Ki:", (text && text.message) || text);   // the original, for whoever debugs
      text = p.todo ? `${p.text} ${p.todo}` : p.text;
    }
    const t = $("toast");
    t.textContent = text;
    t.classList.toggle("err", kind === "error");
    t.classList.add("on");
    clearTimeout(toastTimer);
    // Long enough to read: 7 s for an error, a little more for a long one
    toastTimer = setTimeout(() => t.classList.remove("on"), kind === "error" ? Math.min(14000, 7000 + String(text).length * 30) : 3200);
  }

  /**
   * Puts text on the clipboard; true when it worked. The clipboard API exists only on HTTPS and localhost, and
   * PLC-Ki is usually opened over plain HTTP at the plant's address, so the old copy command is the fallback.
   */
  async function copyText(text) {
    try { if (navigator.clipboard && window.isSecureContext) { await navigator.clipboard.writeText(text); return true; } } catch { /* try the old way */ }
    const ta = document.createElement("textarea");
    ta.value = text;
    ta.setAttribute("readonly", "");
    ta.style.cssText = "position:fixed;top:0;left:0;opacity:0";
    document.body.append(ta);
    ta.select();
    let ok = false;
    try { ok = document.execCommand("copy"); } catch { /* not allowed here */ }
    ta.remove();
    return ok;
  }

  /**
   * A modal sheet; returns {el, close}. Esc, a click outside it and any [data-close] button close it,
   * unless dismiss is false (sheets that wait for a job, such as a real-PLC confirmation or a restart).
   */
  function sheet(html, { dismiss = true, onClose } = {}) {
    const back = document.activeElement;
    const wrap = document.createElement("div");
    wrap.className = "sheet-wrap";
    wrap.innerHTML = `<div class="sheet" role="dialog" aria-modal="true" tabindex="-1">${html}</div>`;
    document.body.append(wrap);
    fillIcons(wrap);
    // The Esc key was only in Help: the close buttons say it (item 37)
    if (dismiss) wrap.querySelectorAll("[data-close]").forEach((b) => { if (!b.title) b.title = b.textContent.trim() + " (Esc)"; b.setAttribute("aria-keyshortcuts", "Escape"); });
    const el = wrap.firstElementChild;
    const h = el.querySelector("h2, h1");   // screen readers name the dialog by its heading
    if (h) { h.id = h.id || "sheet-h-" + Math.random().toString(36).slice(2, 8); el.setAttribute("aria-labelledby", h.id); }
    let closed = false;
    const close = () => {
      if (closed) return;
      closed = true;
      wrap.classList.add("out");
      setTimeout(() => wrap.remove(), matchMedia("(prefers-reduced-motion: reduce)").matches ? 0 : 160);
      if (back && back.isConnected && back.focus) back.focus({ preventScroll: true });
      if (onClose) onClose();
    };
    const focusables = () => [...el.querySelectorAll("a[href],button:not([disabled]),input:not([disabled]),select,textarea,summary,[tabindex]:not([tabindex='-1'])")].filter((x) => x.offsetParent !== null);
    wrap.addEventListener("keydown", (e) => {
      if (e.key === "Escape") { e.stopPropagation(); if (dismiss) close(); }
      if (e.key === "Tab") {           // keep the keyboard inside the sheet
        const f = focusables(); if (!f.length) return;
        if (e.shiftKey && document.activeElement === f[0]) { e.preventDefault(); f[f.length - 1].focus(); }
        else if (!e.shiftKey && document.activeElement === f[f.length - 1]) { e.preventDefault(); f[0].focus(); }
      }
    });
    if (dismiss) wrap.addEventListener("mousedown", (e) => { if (e.target === wrap) close(); });
    el.addEventListener("click", (e) => { if (e.target.closest("[data-close]")) close(); });
    requestAnimationFrame(() => { if (!el.contains(document.activeElement)) (el.querySelector("[autofocus]") || el.querySelector(".row.end .btn:last-child") || el).focus({ preventScroll: true }); });
    return { el, close };
  }

  function confirmSheet({ title, text, ok = "OK", cancel = "Cancel", danger = false }) {
    return new Promise((resolve) => {
      let answer = false;
      const s = sheet(`<h2>${esc(title)}</h2><div class="muted">${text}</div>
        <div class="row end"><button class="btn gray" data-r="0">${esc(cancel)}</button><button class="btn ${danger ? "red" : ""}" data-r="1">${esc(ok)}</button></div>`,
        { onClose: () => resolve(answer) });
      s.el.addEventListener("click", (e) => {
        const b = e.target.closest("[data-r]");
        if (b) { answer = b.dataset.r === "1"; s.close(); }
      });
    });
  }

  function cond(c) {
    const [cls, sym] = c.met === true ? ["yes", "✓"] : c.met === false ? ["no", "✗"] : ["unk", "?"];
    const now = c.values && c.values.length ? `<span class="now">now ${c.values.map((v) => esc(v.label) + " <span class=val>" + esc(v.text) + "</span>").join(", ")}</span>` : "";
    return `<li><span class="mark ${cls}" aria-label="${cls === "yes" ? "met" : cls === "no" ? "not met" : "unknown"}">${sym}</span><span>${esc(c.text)} ${now}</span></li>`;
  }

  /**
   * The part of Markdown that REVIEW.md and Claude's answers use: headings, lists, bold, inline code, fenced code
   * blocks, tables and links. Block names of the loaded program become links to their logic or code.
   */
  function md(text) {
    const blocks = state.program ? new Map(state.program.blocks.map((b) => [b.name, b])) : null;
    const blockLink = (name) => {
      const b = blocks && blocks.get(name);
      return b ? `<a href="#/program/${encodeURIComponent(name)}">${esc(name)}</a>` : null;
    };
    const inline = (raw) => {
      // Pull out code spans and links first, so their text is not touched by the rest
      const keep = [];
      const hold = (html) => `\u0000${keep.push(html) - 1}\u0000`;
      let t = String(raw)
        .replace(/`([^`]+)`/g, (m, c) => hold(blockLink(c) ? `<code>${blockLink(c)}</code>` : `<code>${esc(c)}</code>`))
        .replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+|#\/[^\s)]*)\)/g, (m, label, url) =>
          hold(`<a href="${esc(url)}"${url.startsWith("http") ? ' target="_blank" rel="noopener"' : ""}>${esc(label)}</a>`))
        .replace(/\bhttps?:\/\/[^\s<>()]+[^\s<>().,;:!?]/g, (url) => hold(`<a href="${esc(url)}" target="_blank" rel="noopener">${esc(url)}</a>`));
      t = esc(t).replace(/\*\*(.+?)\*\*/g, "<strong>$1</strong>").replace(/(^|[\s(])\*(?!\s)([^*]+?)\*(?=$|[\s.,;:!?)])/g, "$1<em>$2</em>");
      if (blocks) t = t.replace(/\b([A-Z][A-Za-z]{1,3}_[A-Za-z0-9_]+)\b/g, (m) => blockLink(m) || m);
      return t.replace(/\u0000(\d+)\u0000/g, (m, i) => keep[Number(i)]);
    };
    const out = []; let list = null; let para = []; let code = null; let table = null;
    const fp = () => { if (para.length) { out.push("<p>" + para.map(inline).join("<br>") + "</p>"); para = []; } };
    // Lists keep their numbers (an "ol" that starts at 3 shows 3), and an indented item under another becomes a sub-list of it
    const fl = () => {
      if (!list) return;
      const sub = (it) => it.sub.length ? `<${it.sub[0].t}>${it.sub.map((x) => `<li>${inline(x.text)}</li>`).join("")}</${it.sub[0].t}>` : "";
      out.push(`<${list.t}${list.t === "ol" && list.start > 1 ? ` start="${list.start}"` : ""}>` + list.items.map((i) => `<li>${inline(i.text)}${sub(i)}</li>`).join("") + `</${list.t}>`);
      list = null;
    };
    const cells = (line) => line.trim().replace(/^\|/, "").replace(/\|$/, "").split("|").map((c) => c.trim());
    const ft = () => {
      if (!table) return;
      const [head, ...rows] = table;
      out.push(`<div class="md-table"><table class="tbl"><tr>${head.map((c) => `<th>${inline(c)}</th>`).join("")}</tr>${rows.map((r) => `<tr>${r.map((c) => `<td>${inline(c)}</td>`).join("")}</tr>`).join("")}</table></div>`);
      table = null;
    };
    for (const raw of String(text || "").split(/\r?\n/)) {
      if (code) {
        if (/^\s*```/.test(raw)) { out.push(`<pre class="md-code"${code.lang ? ` data-lang="${esc(code.lang)}"` : ""}><code>${esc(code.lines.join("\n"))}</code></pre>`); code = null; }
        else code.lines.push(raw);
        continue;
      }
      const line = raw.trimEnd();
      const fence = line.match(/^\s*```\s*([\w+-]*)/);
      if (fence) { fp(); fl(); ft(); code = { lang: fence[1], lines: [] }; continue; }
      if (/^\s*\|.*\|\s*$/.test(line)) {
        fp(); fl();
        if (/^\s*\|?\s*:?-{2,}/.test(line)) continue;           // the |---|---| line under the head
        (table ||= []).push(cells(line));
        continue;
      }
      ft();
      const h = line.match(/^(#{1,4})\s+(.*)$/);
      const b = line.match(/^\s*[-*•]\s+(.*)$/);
      const n = line.match(/^\s*(\d+)[.)]\s+(.*)$/);
      if (h) { fp(); fl(); out.push(`<h3>${inline(h[2])}</h3>`); }
      else if ((b || n) && list && /^\s{2,}/.test(raw)) list.items[list.items.length - 1].sub.push({ t: b ? "ul" : "ol", text: b ? b[1] : n[2] });
      else if (b || n) {
        fp();
        const t = b ? "ul" : "ol";
        if (!list || list.t !== t) { fl(); list = { t, items: [], start: n ? Number(n[1]) : 1 }; }
        list.items.push({ text: b ? b[1] : n[2], sub: [] });
      }
      else if (!line.trim()) { if (!list) fp(); }   // a blank line inside a list does not end it; the next plain line does
      else if (list && /^\s{2,}/.test(raw)) { const it = list.items[list.items.length - 1]; (it.sub.length ? it.sub[it.sub.length - 1] : it).text += " " + line.trim(); }
      else { fl(); para.push(line); }
    }
    if (code) out.push(`<pre class="md-code"><code>${esc(code.lines.join("\n"))}</code></pre>`);
    fp(); fl(); ft();
    return out.join("");
  }

  // ---- dates: one format everywhere, "3 Nov 2026, 14:02" (no ambiguous 03/11) ----
  const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
  const pad = (n) => String(n).padStart(2, "0");
  /** "14:02:13" */
  const clock = (t) => { const d = new Date(t); return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`; };
  /** "3 Nov 2026" or, with time, "3 Nov 2026, 14:02" */
  const date = (t, withTime = true) => {
    const d = new Date(t);
    if (isNaN(d)) return "";
    return `${d.getDate()} ${MONTHS[d.getMonth()]} ${d.getFullYear()}` + (withTime ? `, ${pad(d.getHours())}:${pad(d.getMinutes())}` : "");
  };
  /** "14:02:13" today, "3 Nov, 14:02" before */
  const when = (t) => {
    const d = new Date(t), now = new Date();
    return d.toDateString() === now.toDateString() ? clock(d) : `${d.getDate()} ${MONTHS[d.getMonth()]}, ${pad(d.getHours())}:${pad(d.getMinutes())}`;
  };

  const ago = (t) => {
    const s = Math.round((Date.now() - new Date(t).getTime()) / 1000);
    if (s < 60) return s + " s ago";
    if (s < 3600) return Math.round(s / 60) + " min ago";
    if (s < 86400) return Math.round(s / 3600) + " h ago";
    return date(t, false);
  };

  // ---- plain language: what a status or error means and what to do ----
  /** Turns a technical error into { text, todo } a person can act on. */
  function plain(message, where = {}) {
    const m = String(message || "");
    const plc = where.plc || (state.status && state.status.connection && state.status.connection.address) || "the PLC";
    if (/failed to fetch|networkerror|load failed/i.test(m)) return { text: "PLC-Ki itself is not answering.", todo: "Check that the PLC-Ki window on the PC is still open. Start it again with Start-PLC-Ki in the PLC-Ki folder." };
    if (/timed out|timeout|within \d+ s|no answer/i.test(m) && /5090|tia|helper|importer/i.test(m)) return { text: "The TIA helper did not answer.", todo: "On the VM (or TIA PC): check the window \"PLC-Ki TIA helper\" is open. If not, double-click Start-TIA-helper in C:\\PLCKi\\PLC-Ki\\tools\\TiaImporter." };
    if (/refused the token|401/i.test(m)) return { text: "The TIA helper does not accept the token.", todo: "Type the same token here as the one shown in the TIA helper window on the VM." };
    if (/timed out|timeout|no answer|unreachable|no route|host is down/i.test(m)) return { text: `${plc} does not answer.`, todo: `Check the PLC is on and the cable or virtual switch is connected. On the PC, ping ${plc} should answer. For PLCSIM: is the virtual PLC started in PLCSIM Advanced?` };
    if (/no such host|name or service not known|nodename nor servname|not known on this network|host not found/i.test(m)) return { text: `${plc} is not known on the network.`, todo: "Check the spelling. Use the PLC's IP address from TIA (Device configuration › PROFINET interface), for example 192.168.0.10." };
    if (/is not an IP address/i.test(m)) return { text: m, todo: "" };
    if (/refused/i.test(m)) return { text: `Something is at ${plc}, but it does not accept the connection.`, todo: "Check it is really the PLC's address, and that PUT/GET or the access level allows reading (TIA: CPU properties, Protection & Security)." };
    if (/already been opened|project\/library .* cannot be accessed/i.test(m)) return { text: "TIA could not open the project: it is open in TIA Portal.", todo: "Close the project in TIA Portal on the TIA PC (or wait 2 minutes after a crash), then start the job again. Nothing was imported." };
    if (/password|access.?(level|protection|denied|rights)|legitimation|protection/i.test(m)) return { text: "The PLC asks for a password.", todo: "Enter the CPU's access password under Setup, Connections (More PLC options).", op: "Ask an administrator to enter the PLC's access password in PLC-Ki." };
    if (/api key|anthropic/i.test(m)) return { text: "No Claude API key is set.", todo: "Add it under Setup, Settings (Assistant). It starts with sk-ant-." };
    return { text: m, todo: "" };
  }
  // The server only said a status code (it had no words of its own)
  function plainHttp(m) {
    const http = /Request failed \((\d+)\)/i.exec(m);
    if (!http) return null;
    if (http[1] === "403") return { text: "Your login is not allowed to do this.", todo: "" };
    if (http[1] === "404") return { text: "PLC-Ki could not find that.", todo: "Reload the page. If it stays, it may have been removed." };
    if (+http[1] >= 500) return { text: "PLC-Ki hit a problem doing this.", todo: "Try again. If it keeps happening, the PLC-Ki window on the PC shows what went wrong." };
    return { text: "PLC-Ki did not accept this.", todo: "Reload the page and try again." };
  }
  // plain()'s rules are about reaching the PLC, the TIA helper or Ki. Other messages ("The new password needs at least
  // 8 characters.", "The PLC refused the value…") are already written for people and must not be rewritten by them.
  const ABOUT_A_LINK = /failed to fetch|networkerror|load failed|timed out|timeout|within \d+ s|unreachable|no route|host is down|no such host|not known|name or service|connection|connect|tia helper|5090|importer|token|401|already been opened|cannot be accessed|api key|anthropic|legitimation/i;
  // The program export: Hardware, Tags, Documents and Program all read it, and say the same when it is missing
  function plainProgram(m) {
    if (/No program export is set|No program loaded/i.test(m)) return { text: "No PLC program is loaded yet.", todo: "Import it under Setup, Import a program.", op: "Ask an administrator to import the PLC program into PLC-Ki." };
    if (/Cannot read the program export/i.test(m)) return { text: "PLC-Ki cannot read the program file.", todo: "Import the program again under Setup, Import a program.", op: "Ask an administrator to import the PLC program again." };
    if (/program export has no PLC/i.test(m)) return { text: "The imported program has no PLC in it.", todo: "Import the program again and choose the PLC.", op: "Ask an administrator to import the PLC program again." };
    if (/is not in the program export/i.test(m)) return { text: m.replace(/program export/i, "loaded program"), todo: "The program may have changed since it was imported. Import it again under Setup, Import a program.", op: "The program may have changed. Ask an administrator to import it again." };
    return null;
  }
  /** plain() for the person looking: operators cannot open Setup, so they read who to ask instead. */
  function plainFor(message, where) {
    const m = String((message && message.message) || message || "");
    const p = plainProgram(m) || plainHttp(m) || ((where && where.plc) || ABOUT_A_LINK.test(m) ? plain(m, where) : { text: m, todo: "" });
    if (!can("settings") && p.op != null && /Setup/.test(p.todo)) p.todo = p.op;
    return p;
  }

  /** What the PLC connection state means, for the pill sheet and empty pages. Operators cannot open Setup, so they read who to ask. */
  function connInfo() {
    const i = connInfoAll();
    if (can("settings")) return i;
    if (i.todo && /Setup|Connections|Link/.test(i.todo)) i.todo = i.op || "Ask an administrator of PLC-Ki to check the connection to the PLC.";
    return i;
  }
  function connInfoAll() {
    const c = state.status && state.status.connection;
    const stale = Date.now() - state.lastOk > 5000;
    if (!state.status || stale) return { level: "bad", title: "PLC-Ki is not answering.", text: "This page cannot reach PLC-Ki.", todo: "Check that the PLC-Ki window on the PC is still open. If it was closed, double-click Start-PLC-Ki in the PLC-Ki folder, then reload this page." };
    if (c.state === "connected") return { level: "ok", title: `Reading the PLC at ${c.address}.`, text: "Values are live, read once a second.", todo: "" };
    if (c.state === "snapshot") return { level: "info", title: "Showing test values, not a PLC.", text: "PLC-Ki reads values from a test file.", todo: "To read a real PLC, choose Live PLC under Setup, Connections (More PLC options).", op: "Ask an administrator to switch PLC-Ki to the real PLC." };
    if (c.state === "notset") return { level: "warn", title: "No PLC is set yet.", text: "PLC-Ki does not know which PLC to read, so there are no live values.", todo: "Enter the PLC's address under Setup, Connections.", op: "Ask an administrator to set up the connection to the PLC." };
    if (c.state === "connecting") return { level: "warn", title: `Connecting to ${c.address}…`, text: "This takes a few seconds.", todo: "" };
    if (c.state === "waiting") return { level: "warn", title: "Waiting for PLC-Ki Link.", text: "The PLC is read by PLC-Ki Link on your laptop, and it is not connected.", todo: "On the laptop, start PLC-Ki Link (Setup, Connections shows the command).", op: "Ask an administrator to start PLC-Ki Link on the laptop that is connected to the PLC." };
    const p = plain(c.error, { plc: c.address });
    return { level: "bad", title: `No connection to the PLC at ${c.address}.`, text: p.text === c.error ? "" : p.text, todo: p.todo, op: p.op, detail: c.error };
  }

  function connSheet() {
    const i = connInfo();
    const admin = can("settings");
    const a = access();
    sheet(`<span class="pill ${i.level}" style="align-self:flex-start;justify-self:start"><span class="dot"></span>${{ ok: "Connected", bad: "Not connected", warn: "Waiting", info: "Test values" }[i.level]}</span>
      <h2>${esc(i.title)}</h2>${i.text ? `<p class="m-0 muted">${esc(i.text)}</p>` : ""}
      ${a.text ? `<p class="m-0 ${a.level === "real" ? "notice gold" : "muted"}">${esc(a.text)}</p>` : ""}
      ${i.todo ? `<div class="notice ${i.level === "bad" ? "red" : "gold"}">${icon("info")}<div><b>What to do</b><br>${esc(i.todo)}</div></div>` : ""}
      ${i.detail ? `<details class="more"><summary>Technical detail</summary><p class="mt-2 mb-0 faint small">${esc(i.detail)}</p></details>` : ""}
      <div class="row end">${admin && i.level !== "ok" ? `<a class="btn b" href="#/setup/connections" data-close>Open Connections</a>` : ""}<button class="btn" data-close>Done</button></div>`);
  }

  /** A command to type, with where to type it ("On the VM") and a Copy button. One line where possible. */
  function cmd(where, text, note = "") {
    const id = "c" + Math.random().toString(36).slice(2, 8);
    return `<div class="cmd"><div class="cmd-head"><span class="where where-${esc(where.split(" ").pop().toLowerCase())}">${esc(where)}</span>${note ? `<span class="faint small">${note}</span>` : ""}<span class="spacer"></span>
      <button type="button" class="btn plain small" data-copy="${id}">Copy</button></div><pre id="${id}">${esc(text)}</pre></div>`;
  }
  document.addEventListener("click", async (e) => {
    const b = e.target.closest("[data-copy]");
    if (!b) return;
    const el = document.getElementById(b.dataset.copy);
    try { await navigator.clipboard.writeText(el.textContent); b.textContent = "Copied"; setTimeout(() => { b.textContent = "Copy"; }, 1600); }
    catch { const r = document.createRange(); r.selectNodeContents(el); getSelection().removeAllRanges(); getSelection().addRange(r); toast("Selected. Press Ctrl+C to copy."); }
  });

  // ---- Help: what the current page shows and how to read it ----
  const HELP = {
    documents: ["Documents", "Device manuals, fault lists and electrical drawings. Ask and the Active alarm page look things up in them.", [
      "Search: type words, a fault code as the device shows it (F07801, SRVO-062, 0x8C10), a terminal (X2:14) or an address (%I12.3). Fault codes come first, then the pages that match. A page opens in the browser at that page.",
      "For this machine: documents only this machine uses, such as its EPLAN drawing. For every machine: manuals you use everywhere, such as the SINAMICS list manual.",
      "Administrators add files by dropping them on the page. PDFs need text in them: a scanned paper page has none. A fault list can also be a CSV file with code, text, cause and remedy columns.",
      "Built in: general Siemens, FANUC and IO-Link knowledge. When a manual knows the same code, the manual comes first."]],
    alarm: ["Active alarm", "The start page. It shows the alarm that matters most right now and how to get the machine going again.", [
      "With several alarms, the first chip is the first-out: the alarm that came first, as PLC-Ki saw them come. Fix that one first; the others often follow from it.",
      "Fix it: work down the list and tap each step when done. The ticks are only on this screen.",
      "Where in the machine: the device behind the alarm and the chain Requested › Interlocks › Output › Feedback. The first red link is why it is not working.",
      "Where in the software: from the main cycle (OB1) down to the bit that sets the alarm. Show where it is set opens that network as rungs with live values.",
      "The seconds before it came: every change of the machine's tags in the 10 s before the alarm, recorded much finer than the normal reads. Short pulses (amber) are signals the once-a-second reads would miss.",
      "History of this alarm: how often it came, how long it stayed, and the cause and fix technicians noted last time. When you have found the cause, tap Note cause and fix so the next person sees it.",
      "Ask about this alarm: Ki looks at the live values, the last minutes, History and the program, and says why it is on and what to check."]],
    plant: ["Plant", "What the machine is doing now. The top shows the step, what it waits for and the key values; the tabs below hold the rest.", [
      "Overview: Needs attention lists active alarms and blocked or faulted devices first; tap one to go to it. Below it the sequence: the blue step is where it is now, red means halted, ✓ is met, ✗ is missing.",
      "The machine at a glance: every device as a symbol. Green runs, grey is off, a dashed amber edge means the program asks for it but an interlock blocks it, red is a fault. Tap one to open it on Devices.",
      "Devices: the list on the left (needs attention first), the picked device on the right with the chain Requested › Interlocks › Output › Feedback. The first red link is why it is not running.",
      "Values over time: the measured values of the last minutes, with the step band and alarm marks under them. Move along a chart to read every value at that moment.",
      "Machine settings: the values an operator or engineer sets on the HMI panel or in the recipe, with what they are now. ✗ means the machine cannot work with that value.",
      "Data exchange: signals to and from MES, a filler and so on, with their live values."]],
    sequence: ["Step chart", "One sequence as a chain of steps, read like the GRAPH editor, with how long each step took.", [
      "Step timeline: which step ran when in the last minutes, and the last and average time of each step. A step that takes longer than usual is where the machine waits.",
      "Step chart: each box is a step, the bar under it is the transition to the next one with its conditions. ↷ marks a jump to another step; \"from 12\" above a step says which steps jump to it.",
      "The blue step is where it is now. Its conditions are live: ✓ is met, ✗ is missing. Tap any step for what it does."]],
    alarms: ["Alarms", "Every alarm the program can raise, active or not, with its meaning, where it is set, likely causes and the fix.", [
      "Search by number, text or device. Active alarms are at the top.",
      "Click an alarm to open its causes and fix. Red tags are faults (they stop the machine), gold tags are warnings.",
      "Past alarms, what fixed them and the seconds before each one are in History."]],
    history: ["History", "The one place for past alarms: every alarm PLC-Ki saw on this machine, what it was doing then, and what the technicians found and did.", [
      "The time window at the top (24 h, 7 days, 30 days, 1 year) applies to Log and Ranking.",
      "Log: every alarm that came, newest first. Waiting for a note lists alarms of the last 12 hours that nobody wrote anything for yet. PLC and connection lists when the CPU stopped or the link to the PLC dropped.",
      "What fixed it? After an alarm goes, write down the cause and the fix in one line. The next time it comes, the Active alarm page and Ask show it first: \"last time it was a dirty sensor\".",
      "Ranking: the alarms that came most often, or that were on longest in total. Tap one for its own history.",
      "Recordings: the seconds before each alarm came, from every change the PLC sends (usually sampled every 100 ms). Amber marks a short pulse the normal reads would miss; a Blip is an alarm that went again before a normal read saw it. The last 100 are kept.",
      "Download for Excel: the log as a file for the maintenance team. The history is kept on this PC in the PLC-Ki data folder and survives restarts."]],
    program: ["Program", "Every block of the PLC program with live values, as rungs or as text.", [
      "Pick a block on the left. At the top, \"Why outputs are off right now\" names each output that is off and the signal that stops it.",
      "Rungs: each network drawn as a rung. Green passes power, grey does not, a dashed ? has no live value. Read from left to right: the amber contact is the first one that stops the output.",
      "Click a contact or a signal to see what sets it: the network in this block, the block that writes it, or the block that calls this one for an input. Click an output to see where it is used.",
      "Text: the code as written in TIA (LAD and GRAPH as readable text with the same meaning), with the live value after each signal.",
      "LAD, FBD and GRAPH blocks open as rungs, SCL blocks as text. Switch with Rungs / Text. Coming from an alarm, the network that sets it is marked.",
      "Values update every two seconds."]],
    tags: ["Tags", "The PLC tag tables with addresses, and every data block's values, live.", [
      "Tag tables: every tag with its address (%I, %Q, %M), type and comment. Search by name, address or comment.",
      "Data blocks: pick one on the left to see all its values. Optimized blocks have no addresses; values are read by name.",
      "Up to 300 rows are read live at a time, about once a second."]],
    plc: ["PLC", "The PLC in three tabs: the CPU, its racks with every input and output, and what it reports.", [
      "CPU: RUN, STOP or STARTUP as the CPU reports it, with the position of the mode switch where the CPU tells it. The picture is drawn from the order number; page through its display with the keys under it.",
      "Start and Stop work only when PLC-Ki reads the PLCSIM CPU of the TIA helper, and only for administrators. You confirm first. A real PLC is never started or stopped from PLC-Ki.",
      "ERROR and MAINT come from PLCSIM where it tells them; on a real CPU they are not read and stay dark here.",
      "Racks and I/O: pick a device at the top (PLC, I/O stations, drives, robots), then tap a module for its channels. Channels are matched to the PLC tags by address, so you see the tag, its comment and the live value.",
      "If only the CPU shows, the program export has no hardware yet: Read the hardware from TIA (administrators) takes a minute or two and changes nothing else.",
      "Diagnostics: what the CPU reports for PROFINET devices, modules, channels and drives, read live, with what it means and what to check."]],
    reports: ["Reports", "Shift report with OEE, and a summary for maintenance, from what PLC-Ki records while it watches the machine.", [
      "Shift report: OEE and its three parts, a bar of where the time went (running, waiting, down by a fault, no PLC connection), parts made, the biggest losses and the alarms of that shift.",
      "Summary: down time, average repair time (MTTR), running time between faults (MTBF), why the machine was not running, and every shift's OEE, over 24 h, 7 days, 30 days or 1 year. Which alarms cost the most time is in History, Ranking.",
      "Download for Excel saves a CSV file; Print or PDF prints the page.",
      "PLC-Ki records only while it runs and reads the PLC. Time it was off or without connection is shown and left out of OEE.",
      "Report setup (administrators): shift times, planned stops, the part and reject counters and the ideal cycle time. Without a counter every finished sequence cycle counts as one part."]],
    "setup/start": ["Get started", "Sets PLC-Ki up one question at a time. You can come back here any time.", [
      "Each step is saved when you press Continue. Skip what you don't need now: TIA Portal is only needed to import a program or build blocks.",
      "At the end, Apply restarts PLC-Ki with the new settings (a few seconds)."]],
    "setup/connections": ["Connections", "Where PLC-Ki, TIA Portal and the PLC are, and whether each link works.", [
      "The drawing turns each line green when it works and red when it does not. Grey means not checked yet.",
      "Test the address checks that something answers on the PLC's S7 port. It sends nothing to the PLC.",
      "Save, then Apply: PLC-Ki uses new connection settings after a quick restart."]],
    "setup/program": ["Import a program", "Teaches PLC-Ki a machine from its TIA program.", [
      "1. Drop the TIA archive (.zap20). 2. TIA exports it on the TIA PC (needs the TIA helper). 3. Ki builds the model: a few minutes, about a dollar. 4. Check the numbers and press Use this program.",
      "Import again when the program in the PLC changes, so the explanations match."]],
    "setup/generator": ["Block generator", "Say what you need; Ki, PLC-Ki's block writer, writes the blocks, TIA imports and compiles them, PLCSIM runs them.", [
      "New blocks: say what you need, pick the TIA project and press Generate. The package name is made from your words if you leave it empty.",
      "Made before: pick a package on the left to see its versions, what Ki changed, Ki's notes and the files. \"Change it\" makes the next version (v002, v003 …); older versions stay.",
      "When a job ends, \"What Ki did\" lists the blocks and tags it made, changed or removed against the version before.",
      "Ki runs on Claude, with the API key from Settings.",
      "Use a copy of the TIA project, not the plant's master.",
      "A real PLC needs two confirmations: you type the version here, and someone types the PLC address in the TIA helper window. The CPU must be in STOP."]],
    station: ["Stations", "One page per station, made for a phone: is it OK, its alarms with the fix, its sequence step and its devices.", [
      "Scan the QR label on the machine to open its station here. Administrators print the labels in Setup › QR labels.",
      "The stations come from the program: each device and sequence belongs to the station (instance) named in its location. An alarm belongs to the station of its device.",
      "Tap a device for its values and interlocks. Open the alarm for the full fix list and where it is set in the program.",
      "Values update every second."]],
    "setup/qr": ["QR labels", "Print a QR label per station or device. A phone that scans it opens that station's live page.", [
      "Pick the address phones use: the PC's address on the plant network, not localhost. Open it on a phone once to check it works.",
      "Phones only reach PLC-Ki when Settings › Other computers may open PLC-Ki is on.",
      "Print on sticker paper or laminate the labels. They keep working after a new program import as long as the station keeps its name."]],
    "setup/settings": ["Settings", "The machine model, the Claude key, who may open PLC-Ki, and logins.", [
      "Operators see every machine page; administrators also see Setup.",
      "Other computers may open PLC-Ki: off means only this PC."]],
    notfound: ["Page not found", "The address in the browser does not match a page of PLC-Ki.", [
      "Pick a page in the menu, or use one of the links on the page.",
      "An old bookmark or a mistyped address leads here. Nothing is wrong with the machine."]],
  };
  // ---- Page not found: an unknown address says so, with a way out, instead of quietly showing Active alarm ----
  /** The page an unknown address most likely meant, only when it is obvious ("generator" → Block generator). */
  function closestPage(typed) {
    const admin = can("settings");
    const t = String(typed || "").toLowerCase();
    const want = t.split("/").pop();
    const names = Object.keys(views).filter((n) => n !== "notfound" && HELP[n] && (admin || !n.startsWith("setup/")));
    const last = (n) => n.split("/").pop();
    if (want.length >= 3) {
      const hit = names.find((n) => last(n) === want)
        || names.find((n) => last(n).startsWith(want) || want.startsWith(last(n)))
        || names.find((n) => HELP[n][0].toLowerCase().includes(want));
      if (hit) return hit;
    }
    return admin && t.startsWith("setup/") ? "setup/connections" : null;
  }
  view("notfound", {
    render() {
      const typed = location.hash.replace(/^#\/?/, "");
      const near = closestPage(typed.split("/").slice(0, typed.startsWith("setup/") ? 2 : 1).join("/"));
      const links = [
        near ? `<a class="btn" href="#/${near}">Open ${esc(HELP[near][0])}</a>` : "",
        near === "alarm" ? "" : `<a class="btn${near ? " gray" : ""}" href="#/alarm">Active alarm</a>`,
        near === "plant" ? "" : `<a class="btn gray" href="#/plant">Plant</a>`,
      ].join("");
      return hero({ title: "Page not found", actions: links,
        sub: `There is no page at <code>#/${esc(typed)}</code>.${near ? ` Did you mean ${esc(HELP[near][0])}?` : " It may be an old bookmark or a typo."}` });
    },
  });

  function helpSheet() {
    const name = current ? current.name : "alarm";
    const [title, intro, tips] = HELP[name] || HELP.alarm;
    const admin = can("settings");
    const a = access(name);
    const h = sheet(`<span class="eyebrow">Help</span><h2>${esc(title)}</h2><p class="m-0 muted">${esc(intro)}</p>
      <ul class="plainlist">${tips.map((t) => `<li>${esc(t)}</li>`).join("")}</ul>
      ${a.text ? `<p class="m-0 small">${esc(a.text)}</p>` : ""}
      <p class="m-0 faint small">The light at the top right shows the PLC connection; click it for what it means. Ask answers questions about the machine on every page.</p>
      <p class="m-0 faint small"><button type="button" class="linkbtn" data-screen>Alarm sound and screen</button>: beep or show a notice when an alarm comes, keep a line screen on.</p>
      <p class="m-0 faint small keys"><kbd>Ctrl K</kbd> search · <kbd>?</kbd> this help · <kbd>/</kbd> ask Ki · <kbd>Esc</kbd> close</p>
      <div class="row end">${admin && name !== "setup/start" ? `<a class="btn b" href="#/setup/start" data-close>Get started</a>` : ""}<button class="btn" data-close>Done</button></div>`);
    h.el.querySelector("[data-screen]").onclick = () => { h.close(); screenSheet(); };
  }

  /**
   * Phone menu: the bottom bar keeps the four pages used most; "More" opens a sheet with the other pages
   * (the ones this login may see) and the account settings that the side menu shows on larger screens.
   */
  function moreSheet() {
    const label = (a) => { const l = a.querySelector(".tl-long") || a.querySelector(".tl"); return l ? l.textContent.trim() : a.title; };
    const pages = [...document.querySelectorAll("#nav a.more-item")].filter((a) => !a.classList.contains("hidden"));
    const row = (inner, attrs, on) => attrs.startsWith("href")
      ? `<li><a class="mrow${on ? " on" : ""}" ${attrs}${on ? ' aria-current="page"' : ""}>${inner}<span class="mrow-go" data-icon="chevron"></span></a></li>`
      : `<li><button type="button" class="mrow" ${attrs}>${inner}</button></li>`;
    const item = (ic, text, sub) => `<span class="mrow-i" data-icon="${ic}"></span><span class="mrow-t">${esc(text)}${sub ? `<small>${esc(sub)}</small>` : ""}</span>`;
    const pageRow = (a) => row(item(a.querySelector(".ti").dataset.icon, label(a), a.title.includes(":") ? a.title.split(":")[1].trim().replace(/^./, (c) => c.toUpperCase()) : ""),
      `href="${a.getAttribute("href")}" data-close`, a.classList.contains("active"));
    // The same groups as the side menu (Find out, Over time, Admin), each with a small heading
    const groups = [...new Set(pages.map((a) => a.dataset.group || ""))];
    const pageLists = groups.map((g) => `${g ? `<h3 class="more-group">${esc(g)}</h3>` : ""}<ul class="mlist" aria-label="${esc(g || "Pages")}">${pages.filter((a) => (a.dataset.group || "") === g).map(pageRow).join("")}</ul>`).join("");
    const acct = [
      row(item("sound", "Alarm sound and screen"), 'data-m="screen"'),
      row(item("moon", darkNow() ? "Light screen" : "Dark screen"), 'data-m="theme"'),
      state.user ? row(item("key", "Change my password"), 'data-m="pw"') : "",
      row(item("logout", state.user ? "Log out" : "Log in"), 'data-m="logout"'),
    ].join("");
    const who = state.user ? `<p class="faint small more-who">${esc(state.user.name)} · ${roleTitle(state.user.role).toLowerCase()}</p>` : "";
    const s = sheet(`<div class="more-head"><h2>More</h2><button class="btn plain small" type="button" data-close>Done</button></div>
      ${pageLists}
      ${who}<ul class="mlist" aria-label="Account">${acct}</ul>`);
    s.el.classList.add("more-sheet");
    s.el.addEventListener("click", (e) => {
      const b = e.target.closest("[data-m]");
      if (!b) return;
      const m = b.dataset.m;
      s.close();
      if (m === "theme") setTheme(darkNow() ? "light" : "dark");
      else { const t = document.querySelector(`.rail-foot [data-act="${m}"]`); if (t && t.onclick) t.onclick(); }
    });
  }

  // ---- Hold on PLCSIM: the inputs PLC-Ki holds show as a strip on every page (GET /api/held: PLC-Ki's own list,
  // no PLCSIM call). It is not the CPU's force list: forces set in TIA Portal are not in it.
  let held = null;
  async function loadHeld() {
    if (!state.user) return;
    try { held = await api("GET", "/api/held"); } catch { return; }
    showHeld();
  }
  function showHeld() {
    const bar = $("held-bar");
    if (!bar) return;
    const items = (held && held.items) || [];
    bar.classList.toggle("hidden", !items.length);
    if (!items.length) { bar.innerHTML = ""; return; }
    const admin = can("plcsim");     // who may release (technicians too: holds are PLCSIM only)
    const list = items.slice(0, 3).map((h) => `<b class="mono">${esc(String(h.symbol).replace(/^"|"$/g, ""))}</b> = ${esc(h.value)}`).join(", ")
      + (items.length > 3 ? `, and ${items.length - 3} more` : "");
    bar.innerHTML = `<div class="wrap"><div class="notice gold held-strip" role="status">${icon("warn")}
      <div><b>${items.length === 1 ? "1 input held" : items.length + " inputs held"} on PLCSIM</b>: ${list}. ${items.length === 1 ? "It stays" : "They stay"} until released.</div>
      ${admin ? `<div class="held-acts"><a class="btn gray small" href="#/tags">Show</a><button type="button" class="btn small" data-release-all>Release all</button></div>` : ""}</div></div>`;
    const b = bar.querySelector("[data-release-all]");
    if (b) b.onclick = releaseAll;
  }
  // ---- Shift handover (review 3, item 47): the note the last shift left, as one entry in the notice strip on every page
  // during the first hours of the next shift (GET /api/handover/now), until "Got it" in this browser. Checked again every minute, so a panel PC
  // whose page stays open across the shift change shows it too.
  let handover = null;
  const hoKey = (n) => `${n.day}|${n.shiftIndex}|${n.editedAt || n.at}`;
  const hoSeen = () => { try { return localStorage.getItem("pk.handover.seen") || ""; } catch { return ""; } };
  async function loadHandover() {
    if (document.hidden && handover) return;
    try { handover = (await api("GET", "/api/handover/now")).note; } catch { return; }
    showHandover();
  }
  /** The note to show now, or null: not seen in this browser, and not on the report of the shift that wrote it. */
  function handoverShown() {
    const n = handover;
    return n && hoKey(n) !== hoSeen() && location.hash !== `#/reports/shift/${n.day}/${n.shiftIndex}` ? n : null;
  }
  function handoverHtml(n) {
    const at = new Date(n.editedAt || n.at);
    const text = n.text.length > 220 ? n.text.slice(0, 219).trimEnd() + "…" : n.text;
    return `<div class="notice notice-strip ho-strip" role="status" data-kind="handover">${icon("chat")}
      <div><b>Handover from the ${esc(n.shift)} shift</b> <span class="small">(${esc(n.editedBy || n.by)}, ${pad(at.getHours())}:${pad(at.getMinutes())})</span>
        <div class="ho-text">${esc(text)}</div></div>
      <a class="btn gray small" href="#/reports/shift/${esc(n.day)}/${n.shiftIndex}">Read</a><button type="button" class="btn small" data-ho-ok>Got it</button></div>`;
  }
  function handoverSeen() {
    if (handover) try { localStorage.setItem("pk.handover.seen", hoKey(handover)); } catch { /* shown again on the next page load */ }
    handover = null;
    renderNotices();
  }
  const showHandover = () => renderNotices();

  /** Puts every held input back to the value it had before its hold (administrators). */
  async function releaseAll() {
    const n = ((held && held.items) || []).length;
    const ok = await confirmSheet({ title: n === 1 ? "Release the held input?" : `Release all ${n} held inputs?`,
      text: n === 1 ? "PLC-Ki puts the input back to the value it had before the hold." : "PLC-Ki puts each input back to the value it had before the hold.", ok: n === 1 ? "Release" : "Release all" });
    if (!ok) return;
    try {
      const r = await api("POST", "/api/admin/hold/release", { all: true });
      const bad = r.results.filter((x) => !x.ok);
      toast(bad.length ? `${bad.length} could not be released: ${bad[0].error}` : "Released. The inputs have their earlier values again.", bad.length ? "error" : undefined);
    } catch (e) { toast(e.message, "error"); }
    await loadHeld();
    window.dispatchEvent(new Event("pk:held-changed"));
  }

  async function start() {
    fillIcons();
    try {
      const st = await api("GET", "/api/auth/state");
      state.user = st.user;
    } catch { /* public view */ }
    const admin = can("settings");
    document.querySelectorAll(".admin-only").forEach((el) => el.classList.toggle("hidden", !admin));
    // Public view without a login: Ask needs a login (it spends Claude credit), so its buttons are hidden
    document.documentElement.classList.toggle("anon", !state.user);
    if (can("plcsim")) setInterval(loadChanges, 60000);   // "changes allowed" can change without a restart
    const who = state.user ? state.user.name + (admin ? " · administrator" : " · operator") : "Not logged in";
    document.querySelectorAll(".who").forEach((el) => { el.textContent = who; });
    document.querySelectorAll(".act-logout").forEach((el) => { el.textContent = state.user ? "Log out" : "Log in"; });
    const acts = {
      logout: async () => { if (state.user) await fetch("/api/auth/logout", { method: "POST" }); location.href = "/login.html"; },
      screen: () => screenSheet(),
      pw: () => passwordSheet(),
    };
    document.querySelectorAll("[data-act]").forEach((b) => {
      b.onclick = acts[b.dataset.act];
      if (b.dataset.act === "pw") b.classList.toggle("hidden", !state.user);
    });
    $("theme-btn").onclick = () => setTheme(darkNow() ? "light" : "dark");
    $("nav-more").onclick = moreSheet;
    showTheme();
    const tick = () => { const c = $("clock"); if (c) c.textContent = clock(Date.now()); };
    tick(); setInterval(tick, 1000);
    try { state.model = await api("GET", "/api/model"); } catch { /* shown on pages */ }
    try { state.info = await api("GET", "/api/info"); $("machine").textContent = state.info.machine; machineLine(); } catch { /* */ }
    loadProgram().then(machineLine);   // the top bar names the CPU and the program file
    $("help-open").onclick = helpSheet;
    if (screenOpt.awake) keepAwake(true);
    $("conn").setAttribute("role", "button");
    $("conn").tabIndex = 0;
    $("conn").title = "What does this mean?";
    $("conn").onclick = connSheet;
    const cpuPill = $("cpu-pill");
    if (cpuPill) {
      cpuPill.setAttribute("role", "link");
      cpuPill.tabIndex = 0;
      cpuPill.onclick = () => go("#/plc");
      cpuPill.onkeydown = (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); go("#/plc"); } };
    }
    $("conn").onkeydown = (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); connSheet(); } };
    // The badge says in a word what may change; a tap shows the full sentence (a phone has no tooltip)
    $("ro-badge").setAttribute("role", "button");
    $("ro-badge").tabIndex = 0;
    $("ro-badge").onclick = connSheet;
    $("ro-badge").onkeydown = (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); connSheet(); } };
    window.addEventListener("hashchange", onHash);
    // The back chip sits above the page, so every page has it without drawing it
    const back = document.createElement("div");
    back.id = "back-bar";
    back.className = "back-bar hidden";
    back.innerHTML = `<div class="wrap"><a class="back-chip" href="#/"><span aria-hidden="true">←</span><span class="back-l"></span></a></div>`;
    $("page").before(back);
    // Inputs held on PLCSIM: a strip above every page while there are any
    const heldBar = document.createElement("div");
    heldBar.id = "held-bar";
    heldBar.className = "held-bar hidden";
    back.before(heldBar);
    // Test switches on, or the PLC's blocks differ from the import: a strip of its own, so a STOP or the test-values
    // banner never hides it
    const noticeBar = document.createElement("div");
    noticeBar.id = "notice-bar";
    noticeBar.className = "notice-bar hidden";
    heldBar.before(noticeBar);
    // Got it on the handover entry (the strip is redrawn with each status, so the click is caught here)
    noticeBar.addEventListener("click", (e) => { if (e.target.closest("[data-ho-ok]")) handoverSeen(); });
    back.querySelector("a").onclick = (e) => { if (e.button || e.ctrlKey || e.metaKey || e.shiftKey) return; e.preventDefault(); history.back(); };
    document.addEventListener("click", (e) => { if (e.target.closest && e.target.closest("#nav a, .rail-brand, a.mrow[href]")) fromMenu = true; }, true);
    const bar = document.createElement("div");
    bar.id = "dirty-bar";
    bar.className = "dirty-bar";
    bar.setAttribute("role", "region");
    bar.setAttribute("aria-label", "Unsaved changes");
    bar.innerHTML = `<span class="dot"></span><span>Unsaved changes</span><button type="button" class="btn gray small" data-discard>Discard</button><button type="button" class="btn small" data-save-now>Save</button>`;
    document.body.append(bar);
    bar.querySelector("[data-discard]").onclick = () => { setDirty(null); route(); };
    bar.querySelector("[data-save-now]").onclick = () => dirty && dirty.save();
    window.addEventListener("beforeunload", (e) => { if (dirty) { e.preventDefault(); e.returnValue = ""; } });
    // Tabs (item 37): Left and Right move to the next tab, Home and End to the first and last; Enter or Space opens it
    document.addEventListener("keydown", (e) => {
      const tab = e.target.closest && e.target.closest('[role="tab"]');
      if (!tab || e.ctrlKey || e.metaKey || e.altKey) return;
      if (e.key === " " && tab.tagName === "A") { e.preventDefault(); tab.click(); return; }
      if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(e.key)) return;
      const list = [...tab.closest('[role="tablist"]').querySelectorAll('[role="tab"]')];
      let i = list.indexOf(tab);
      i = e.key === "Home" ? 0 : e.key === "End" ? list.length - 1 : (i + (e.key === "ArrowRight" ? 1 : -1) + list.length) % list.length;
      e.preventDefault();
      list.forEach((t, k) => { t.tabIndex = k === i ? 0 : -1; });
      list[i].focus();
      list[i].scrollIntoView({ block: "nearest", inline: "nearest" });
    });
    document.addEventListener("keydown", (e) => {
      if (e.ctrlKey || e.metaKey || e.altKey || document.querySelector(".sheet-wrap")) return;
      const t = e.target;
      if (t.closest && t.closest("input,textarea,select,[contenteditable]")) return;
      if (e.key === "?") { e.preventDefault(); helpSheet(); }
      else if (e.key === "/" && window.PKAsk) { e.preventDefault(); window.PKAsk.open(); }
    });
    window.addEventListener("scroll", () => $("topnav").classList.toggle("scrolled", window.scrollY > 4), { passive: true });
    const navh = () => {
      document.documentElement.style.setProperty("--navh", $("topnav").offsetHeight + "px");
      document.documentElement.style.setProperty("--bannerh", $("banner").offsetHeight + "px");   // 0 when hidden
    };
    navh();
    if ("ResizeObserver" in window) { const ro = new ResizeObserver(navh); ro.observe($("topnav")); ro.observe($("banner")); }
    // An administrator who has not finished setup gets Get started, but not while an alarm is on: then the
    // alarm comes first (Active alarm still shows the Get started card).
    const alarmOn = async () => {
      try { const s = await api("GET", "/api/status"); state.status = s; state.lastOk = Date.now(); return !!(s.activeAlarms || []).length; }
      catch { return false; }
    };
    if (admin && !location.hash && typeof PKStart !== "undefined" && (await PKStart.firstRun()) && !(await alarmOn())) history.replaceState(null, "", "#/setup/start");
    route();
    pollStatus();
    pollValues();
    pollJobs();
    if (state.user) { loadHeld(); setInterval(loadHeld, 15000); }
    loadHandover(); setInterval(loadHandover, 60000);
  }

  return { AI, ui, BP, can, roleTitle, screenSize, dirty: setDirty, get isDirty() { return !!dirty; }, plain, connInfo, connSheet, helpSheet, screenSheet, passwordSheet, setHash, cmd, sec, hero, on, $, esc, icon, fillIcons, kindIcon, api, state, view, go, draw, route, toast, copyText, sheet, confirmSheet, cond, md, ago, clock, date, when,
    loadProgram, blockOf, valueBySymbol, norm, start, cpuStop, cpuSince, loadHeld, releaseAll, loadHandover, get current() { return current; } };
})();
