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
    expand: '<path d="M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5"/>',
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
    sliders: '<path d="M4 6h9M17 6h3M4 12h3M11 12h9M4 18h11M19 18h1"/><circle cx="15" cy="6" r="2"/><circle cx="9" cy="12" r="2"/><circle cx="17" cy="18" r="2"/>',
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
    if (r.status === 401) { location.href = "/login.html?next=" + encodeURIComponent(location.pathname + location.hash); throw new Error(PK.t("Please log in.")); }
    const text = await r.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch { data = text; }
    if (!r.ok) throw new Error((data && data.error) || PK.t("Request failed ({status}).", { status: r.status }));
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
  /** PLC-Ki reads test values from a file (Source=snapshot), not a PLC: nothing on the page can be changed. */
  const testValues = () => { const c = state.status && state.status.connection; return !!c && c.state === "snapshot"; };
  const ROLE_TITLE = { admin: I18N.T("Administrator"), technician: I18N.T("Technician"), operator: I18N.T("Operator") };
  const roleTitle = (role) => PK.t(ROLE_TITLE[role] || "Operator");
  /** The role as a word inside a sentence ("anna · operator"): lower case in English; German nouns keep their capital. */
  const roleWord = (role) => (I18N.lang === "de" ? roleTitle(role) : roleTitle(role).toLowerCase());
  const AS_FOR = { admin: I18N.T("As for administrators: {page}"), technician: I18N.T("As for technicians: {page}"), operator: I18N.T("As for operators: {page}") };

  // ---- Choices per login (review 52): saved on the server, so they follow the person to another PC or a phone.
  // Logged out (public view) they stay in this browser under the old keys, as before. A login's choices are also
  // cached in this browser ("plcki.prefs.<name>"), apart from the logged-out copy, so one person never gets another's.
  const LEGACY = { theme: "plcki.theme", screen: "plcki.screen", trendTags: "plcki.trendTags", timeline: "pk.timeline",
    netFilter: "plcki.netFilter", getStarted: "plcki.getStarted", handoverSeen: "pk.handover.seen" };
  const JSON_PREFS = new Set(["screen", "trendTags", "alarmSound", "format"]);
  const ls = {
    get: (k) => { try { return localStorage.getItem(k); } catch { return null; } },
    set: (k, v) => { try { if (v == null) localStorage.removeItem(k); else localStorage.setItem(k, v); } catch { /* private window */ } },
  };
  let mine = null;            // this login's choices; null while logged out
  let prefsOut = {}, prefsTimer = null;
  const prefsCache = () => "plcki.prefs." + state.user.name.toLowerCase();
  function readLocal(key) {
    const raw = ls.get(LEGACY[key] || "plcki." + key);
    if (raw == null) return undefined;
    if (!JSON_PREFS.has(key)) return raw;
    try { return JSON.parse(raw); } catch { return undefined; }
  }
  const writeLocal = (key, v) => ls.set(LEGACY[key] || "plcki." + key, v == null ? null : JSON_PREFS.has(key) ? JSON.stringify(v) : String(v));
  function sendPrefs(keepalive) {
    clearTimeout(prefsTimer); prefsTimer = null;
    const body = prefsOut; prefsOut = {};
    if (!Object.keys(body).length) return;
    if (keepalive) { fetch("/api/me/prefs", { method: "PUT", keepalive: true, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) }).catch(() => {}); return; }
    api("PUT", "/api/me/prefs", body).catch(() => { prefsOut = { ...body, ...prefsOut }; });   // tried again with the next change
  }
  const prefs = {
    /** This login's choice, or this browser's when logged out; dflt when there is none. */
    get(key, dflt) { const v = mine ? mine[key] : readLocal(key); return v === undefined || v === null ? dflt : v; },
    /** null removes the choice (back to the default). */
    set(key, v) {
      if (v === undefined) v = null;
      if (!mine) { writeLocal(key, v); return; }
      if (v === null) delete mine[key]; else mine[key] = v;
      ls.set(prefsCache(), JSON.stringify(mine));
      prefsOut[key] = v;
      clearTimeout(prefsTimer); prefsTimer = setTimeout(() => sendPrefs(false), 500);
    },
    /** Sends what is waiting now (before a reload) and waits for the answer. */
    async flush() {
      clearTimeout(prefsTimer); prefsTimer = null;
      const body = prefsOut; prefsOut = {};
      if (mine && Object.keys(body).length) { try { await api("PUT", "/api/me/prefs", body); } catch { /* the browser's copy still has it */ } }
    },
    /** True when the choices are kept for the login (and follow the person). */
    get perLogin() { return !!mine; },
  };
  window.addEventListener("pagehide", () => { if (prefsTimer) sendPrefs(true); });
  async function loadPrefs() {
    if (!state.user) return;
    let cached = null;
    try { cached = JSON.parse(ls.get(prefsCache()) || "null"); } catch { /* none */ }
    let server = null;
    try { server = await api("GET", "/api/me/prefs"); } catch { /* PLC-Ki not answering: this browser's copy */ }
    mine = server || cached || {};
    // The first login on this browser after the update takes the browser's old choices along, once. Later logins
    // (another person on a shared PC) start from their own, never from what is in this browser.
    if (server && !ls.get("plcki.prefsMoved")) {
      if (!Object.keys(server).length) {
        const up = {};
        for (const k of Object.keys(LEGACY)) { const v = readLocal(k); if (v !== undefined) up[k] = v; }
        if (Object.keys(up).length) { try { mine = await api("PUT", "/api/me/prefs", up); } catch { mine = up; } }
      }
      ls.set("plcki.prefsMoved", "1");
    }
    ls.set(prefsCache(), JSON.stringify(mine));
    document.dispatchEvent(new Event("pk:prefs"));     // pages that read a choice at load read it again
  }
  /** Where this login starts: its own choice, else its role's (Setup, Settings), else Active alarm. */
  const PAGE = /^#\/[A-Za-z0-9/_.%~-]{0,120}$/;
  const startPage = () => [prefs.get("startPage", ""), state.roleStart].find((p) => typeof p === "string" && PAGE.test(p)) || "#/alarm";
  /** What a start page can be: the main pages (not Setup) and each station. [[address, label]] */
  function startChoices(also) {
    const out = [...document.querySelectorAll("#nav a[data-route]")].filter((a) => !a.classList.contains("admin-only"))
      .map((a) => [a.getAttribute("href"), (a.title || a.textContent).split(":")[0].trim()]);
    const st = typeof PKStations !== "undefined" && state.status ? PKStations.list(state.status).stations : [];
    st.filter((x) => x.key).forEach((x) => out.push([PKStations.href(x.key), PK.t("Station: {name}", { name: x.name })]));
    // The line screen (TV, review 44), for a wall PC that opens straight into it: the whole machine, then each station
    out.push(["#/tv", PK.t("Line screen (TV)")]);
    st.filter((x) => x.key).forEach((x) => out.push(["#/tv/" + encodeURIComponent(x.key), PK.t("Line screen (TV): {name}", { name: x.name })]));
    for (const h of also || []) if (h && !out.some(([v]) => v === h)) out.push([h, h.replace(/^#\//, "")]);    // a saved page not listed now
    return out;
  }
  const startLabel = (h) => (startChoices([h]).find(([v]) => v === h) || [h, h])[1];
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
    loading(shape = "lines", { rows = 4, label = PK.t("Loading") } = {}) {
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
        ${p.text !== raw ? `<details class="pk-detail"><summary>${PK.t("Details")}</summary><code>${esc(raw)}</code></details>` : ""}${action ? `<div class="row mt-2">${action}</div>` : ""}</div></div>`;
    },
    empty({ icon: ic = "check", title, text = "", action = "", cls = "" }) {
      return `<div class="empty ${cls}">${icon(ic)}<b>${title}</b>${text ? `<span>${text}</span>` : ""}${action}</div>`;
    },
    /** The machine's states, the same words and colours on every page: [name, colour]. */
    // Getters, so every page that reads a name gets it in the language in use
    states: {
      get running() { return [PK.t("Running"), "var(--ok)"]; },
      get idle() { return [PK.t("Waiting or stopped"), "var(--steel)"]; },
      get down() { return [PK.t("Down (fault)"), "var(--fault)"]; },
      get nolink() { return [PK.t("No PLC connection"), "var(--warn)"]; },
      get off() { return [PK.t("Not watched"), "var(--line-strong)"]; },
    },
    /** What a mark on the timeline stands for (EventLog.Mark on the server). Colour only where it is a state. */
    marks: { get alarm() { return PK.t("Alarm came"); }, get warn() { return PK.t("Warning came"); }, get halt() { return PK.t("Halted"); },
      get stop() { return PK.t("PLC in STOP"); }, get link() { return PK.t("PLC connection lost"); }, get write() { return PK.t("Value changed"); } },
    /**
     * The machine's state over a time as one coloured bar (running, waiting, down, no PLC connection, not watched), time
     * under it, and marks above it for what happened: [{ time, mark, text }]. Each mark is a button with data-tlmk="i";
     * sel is the index shown as picked. Hours under a day or so; days (Mon 9) over longer times.
     */
    timeline({ spans, from, to, marks = [], sel = -1, label = PK.t("Machine state over time") }) {
      const a = new Date(from).getTime(), b = new Date(to).getTime(), len = Math.max(1, b - a);
      const at = (t) => ((new Date(t).getTime() - a) / len * 100);
      const days = len > 36 * 3600000;
      const dayName = (t) => { const d = new Date(t); return `${WEEKDAYS[d.getDay()]} ${d.getDate()}`; };
      const segs = spans.map((s) => {
        const [name, col] = ui.states[s.state] || [s.state, "var(--muted)"];
        const w = (new Date(s.to).getTime() - new Date(s.from).getTime()) / len * 100;
        const tip = `${days ? dayName(s.from) + " " : ""}${hm(s.from)}–${hm(s.to)} ${name}${s.why ? ": " + PK.srv(s.why) : ""}`;
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
      let endLabel = endIsNow ? PK.t("now") : hm(b);
      if (!endIsNow && nowT > a && nowT < b) {
        if (at(nowT) > 80) endLabel = "";      // "now" close to the end: the end time would sit on it
        const x = at(nowT);
        for (let i = ticks.length - 1; i >= 0; i--) if (Math.abs(+ticks[i].match(/left:([\d.]+)%/)[1] - x) < 6) ticks.splice(i, 1);   // no overlap
        ticks.push(`<span class="rp-tick rp-now" style="left:${x.toFixed(2)}%">${PK.t("now")}</span>`);
      }
      const mk = marks.map((m, i) => `<button type="button" class="tl-mk tl-${esc(m.mark)}${i === sel ? " on" : ""}" style="left:${at(m.time).toFixed(3)}%" data-tlmk="${i}"
        title="${esc(`${days ? dayName(m.time) + " " : ""}${clock(m.time)} ${PK.srv(m.text)}`)}" aria-label="${esc(`${clock(m.time)} ${ui.marks[m.mark] || m.mark}: ${PK.srv(m.text)}`)}"></button>`).join("");
      return `<div class="tl">${marks.length ? `<div class="tl-marks">${mk}</div>` : ""}<div class="rp-bar" role="img" aria-label="${esc(label)}">${segs}</div>
        <div class="rp-ticks"><span>${days ? dayName(a) : hm(a)}</span>${ticks.join("")}${endLabel ? `<span class="rp-end">${endLabel}</span>` : ""}</div></div>`;
    },
    /** Each kind of machine event in plain words (the same names as the shift log's spreadsheet). */
    kinds: { get alarm() { return PK.t("Alarm"); }, get step() { return PK.t("Step"); }, get halt() { return PK.t("Halt"); }, get blocked() { return PK.t("Blocked"); },
      get output() { return PK.t("Output"); }, get state() { return PK.t("Device state"); }, get cpu() { return PK.t("PLC mode"); },
      get link() { return PK.t("PLC connection"); }, get write() { return PK.t("Value changed"); }, monitor: "PLC-Ki" },
    /** Machine events as lines: time, kind, what happened (an alarm id links to its page). day: show the day too.
     *  hit: a time (ms); lines of that moment are marked. */
    events(list, { cls = "", day = false, hit = null } = {}) {
      const t = (x) => day ? date(x) : clock(x);
      const text = (e) => { const tx = PK.srv(e.text); return e.alarm && tx.startsWith(e.alarm)
        ? `<a href="#/alarms/${encodeURIComponent(e.alarm)}">${esc(e.alarm)}</a>${esc(tx.slice(e.alarm.length))}` : esc(tx); };
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
    // A bookmark for one station's line screen may be written #/tv?station=<name>; the page itself is #/tv/<name>
    [/^#\/tv\/?\?(?:.*&)?station=([^&]*).*$/, "#/tv/$1"],
  ];
  const movedTo = (hash) => { for (const [re, to] of MOVED) if (re.test(hash)) return hash.replace(re, to); return null; };

  function go(hash) {
    if (leaveSheetTo(hash)) return;    // a Search result: the sheet's Back step goes first, then the page
    if (location.hash !== hash) location.hash = hash; else route();
  }

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
      const leave = await confirmSheet({ title: PK.t("Leave without saving?"), text: PK.t("Your changes on this page are not saved yet."), ok: PK.t("Leave without saving"), cancel: PK.t("Stay"), danger: true });
      if (!leave) return;
      setDirty(null);
      location.hash = target;
      return;
    }
    route();
  }

  function route() {
    setDirty(null);
    closeSheets();          // a sheet belongs to the page it was opened on (More, Help or Search left open after Back)
    const moved = movedTo(location.hash);
    if (moved) history.replaceState(history.state, "", moved);
    const leftHash = shownHash;
    shownHash = location.hash;
    let { name, params } = parseRoute();
    let v = views[name];
    // Setup is for administrators: an old bookmark or a shared link says so, instead of waiting for answers the server refuses
    if (name.startsWith("setup/") && !can("settings")) { params = []; name = "noaccess"; v = views.noaccess; }
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
    // A new page opens at the top. Instant, not the smooth scroll of html: draw() keeps the scroll position it finds,
    // and during a smooth scroll that is still the old page's position (a link low on Active alarm opened Sequence near its end).
    page.scrollTop = 0;
    window.scrollTo({ top: 0, behavior: "instant" });
    quiet(2000);            // a new page: its first values are not "changes"
    tabsIntoView = true;    // and its open tab is shown, also at the end of a long row on a phone
    if (v.mount) v.mount(page, params);
    else draw();
    page.scrollTop = 0;
    window.scrollTo({ top: 0, behavior: "instant" });
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
    const reads = (text) => ({ badge: PK.t("Reads only"), short: PK.t("Reads only"), level: "", text });
    if (!state.user) return reads(PK.t("Without a login, PLC-Ki only reads the PLC."));
    if (!can("plcsim")) return reads(PK.t("With an operator login, PLC-Ki only reads the PLC."));
    const ch = state.changes;
    if (!ch) return { badge: null, short: null, level: "", text: "" };
    const plc = ch.plc || PK.t("the PLC");
    const valuePage = name === "tags" || name === "program";
    let a;
    if (name === "setup/generator") a = { badge: PK.t("Loads blocks here"), short: PK.t("Blocks"), level: "except",
      text: PK.t("The block generator on this page loads blocks into PLCSIM, or into a real PLC after two people confirm.") };
    else if (valuePage && ch.values === "plcsim") a = { badge: PK.t("Can change values here"), short: PK.t("Can change"), level: "except",
      text: PK.t("On this page you can change a value with the pen, on PLCSIM: inputs, outputs, memory and data values, safety ones after a warning, never F-blocks. Every change is logged.") };
    else if (valuePage && ch.values === "real") a = { badge: PK.t("Can change values here"), short: PK.t("Can change"), level: "except",
      text: PK.t("On this page you can change memory and data values on the real PLC {plc} with the pen, after typing a reason and the PLC address; never inputs, outputs or values the safety program reads. Every change is logged.", { plc }) };
    else if (name === "plc" && ch.startStop && !["racks", "diagnostics"].includes((current && current.params[0]) || "")) a = { badge: PK.t("Can start and stop PLCSIM"), short: PK.t("Start/Stop"), level: "except",
      text: PK.t("Start and Stop on this page switch the PLCSIM CPU, after you confirm. A real PLC is never started or stopped.") };
    else a = reads(PK.t("This page only reads the PLC.")
      + (ch.values === "plcsim" ? " " + PK.t("On Tags and Program, you can change values on PLCSIM.")
        : ch.values === "real" ? " " + PK.t("On Tags and Program, administrators can change values on the real PLC {plc}.", { plc })
        : !can("real") && ch.plc ? " " + PK.t("A technician login changes values on PLCSIM only.") : ""));
    // A real PLC that may be changed is said on every page, in amber
    if (ch.values === "real") { a.badge = PK.t("Changes allowed on the real PLC {plc}", { plc }); a.short = PK.t("Real PLC"); a.level = "real"; }
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
    const page = PK.t((HELP[name] || [""])[0] || name);
    const p = (params || []).filter((x) => !/^(as-(rungs|text)|network-\d+|line-\d+|alarm-.+|@.*)$/.test(x));
    let label = page;
    if (name === "alarm") { const al = (state.status && state.status.activeAlarms) || []; if (al.length) label = al[0].id; }
    else if (name === "plant" && p[0] === "devices" && p[1]) label = p[1];
    else if (name === "tags" && p[0] === "db" && p[1]) label = /^DB/i.test(p[1]) ? p[1] : "DB " + p[1];     // "Back to DB Station", the block, not a page
    else if (name === "plc" && p[0] === "racks" && p[1]) label = p[1];
    else if (name === "history" && p[0] && !["ranking", "recordings"].includes(p[0])) label = PK.t("{name} history", { name: p[0] });
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
    a.querySelector(".back-l").textContent = PK.t("Back to {label}", { label: b.label });
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
    catch (e) { html = sec("", `<div class="notice red" role="alert">${icon("warn")}<div><b>${PK.t("This page hit a problem.")}</b> ${PK.t("Reload the page. If it happens again, tell an administrator what you clicked.")}
      <details class="pk-detail"><summary>${PK.t("Details")}</summary><code>${esc(e.message)}</code></details></div></div>`); console.error(e); }
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
    window.scrollTo({ top: y, behavior: "instant" });
  }

  // ---- live polling ----
  /** "updated 1 s ago" beside the connection pill (item 39): a frozen page counts up, a live one stays at a second or two. */
  function renderAge() {
    const el = $("conn-age");
    if (!el) return;
    const c = state.status && state.status.connection;
    if (!state.lastOk || !c || !["connected", "snapshot"].includes(c.state)) { el.textContent = ""; return; }
    const s = Math.max(1, Math.round((Date.now() - state.lastOk) / 1000));
    const text = s < 60 ? PK.t("updated {s} s ago", { s }) : PK.t("updated {ago}", { ago: ago(state.lastOk) });
    if (el.textContent !== text) el.textContent = text;
    el.classList.toggle("stale", s > 5);
  }
  setInterval(renderAge, 1000);

  function renderConn() {
    const pill = $("conn");
    const c = state.status && state.status.connection;
    const stale = Date.now() - state.lastOk > 5000;
    let text, cls;
    if (!state.status || stale) { text = PK.t("PLC-Ki not reachable"); cls = "bad"; }
    else {
      const map = {
        connected: [PK.t("Live") + "|" + c.address, "ok"],
        snapshot: [PK.t("Test values") + "|" + c.address.split(/[\\/]/).pop(), "warn"],
        connecting: [PK.t("Connecting") + "|" + c.address, "warn"],
        disconnected: [PK.t("Not connected") + "|" + c.address, "bad"],
        waiting: [PK.t("Waiting for Link") + "|", "warn"],
        notset: [PK.t("No PLC set yet") + "|", "warn"],
      };
      [text, cls] = map[c.state] || [c.state, "warn"];
    }
    pill.className = "pill " + cls;
    const [main, extra] = String(text).split("|");
    // Short in the bar; the address or test file is in the tooltip (a long file name wrapped into the menu).
    // A mode read that failed is only said here, quietly: it is not a STOP.
    const cpu = !stale && state.status && state.status.cpu;
    pill.title = (extra ? main + ": " + extra + ". " : "") + (cpu && cpu.error ? PK.t("PLC mode unknown.") + " " : "") + PK.t("What does this mean?");
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
      const first = items.slice(0, 4).map((i) => `<span class="mono">${esc(i)}</span>`).join(", ");
      const shown = items.length > 4 ? PK.t("{list}, and {n} more", { list: first, n: items.length - 4 }) : first;
      const link = n.link && (n.kind !== "program" || can("settings"))
        ? `<a class="btn gray small" href="${esc(n.link)}">${n.kind === "program" ? PK.t("Import again") : PK.t("Show")}</a>` : "";
      const who = n.kind === "program" && !admin ? " " + PK.t("Tell an administrator.") : "";
      return `<div class="notice gold notice-strip" role="status" data-kind="${esc(n.kind)}">${icon(n.kind === "program" ? "file" : "warn")}
        <div><b>${esc(PK.srv(n.text))}</b>${who}${shown ? `<div class="small">${shown}</div>` : ""}</div>${link}</div>`;
    }).join("");
    const all = (ho ? handoverHtml(ho) : "") + html;
    const full = all ? `<div class="wrap">${all}</div>` : "";
    if (bar.dataset.html !== full) { bar.innerHTML = full; bar.dataset.html = full; }
    bar.classList.toggle("hidden", !list.length && !ho);
  }

  /** Under the machine name in the top bar: the CPU and the program it was read from, as far as known. */
  function machineLine() {
    const i = state.info || {}, p = state.program || {};
    const plc = p.cpu ? p.cpu : i.plc && i.plc.address ? PK.t("PLC {address}", { address: i.plc.address }) : "";
    const prog = p.archive || p.project || (i.model ? String(i.model).split(/[\\/]/).pop() : "");
    const t = [plc, prog && PK.t("program {name}", { name: prog })].filter(Boolean).join(" · ");
    const el = $("machine-sub");
    if (el && el.textContent !== t) { el.textContent = t; el.title = t; }
  }

  /** The CPU's mode when it is really not in RUN (STOP, startup, hold, defect), else null. A failed mode read is never STOP. */
  const cpuStop = (s = state.status) => (s && s.cpu && s.cpu.stopped ? s.cpu : null);
  /** "STOP since 09:12", "STOP since at least 09:12" (already so when PLC-Ki started watching), with the day when not today. */
  function cpuSince(c) {
    if (!c || !c.since) return "";
    const d = new Date(c.since), today = d.toDateString() === new Date().toDateString();
    const at = today ? hm(d) : date(c.since);
    return c.atStart ? PK.t("since at least {time}", { time: at }) : PK.t("since {time}", { time: at });
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
    p.title = PK.t("The PLC is in {word} {since}: the program is not running. Open the PLC page.", { word: c.word || "STOP", since: cpuSince(c) });
  }

  /** Says loudly when the values on the page are not live: PLC-Ki not answering, PLC not read, PLC in STOP, or test values. */
  function renderBanner(stale) {
    const c = state.status && state.status.connection;
    const stop = cpuStop();
    let text = "", kind = "", link = "";
    if (state.lastOk && stale) { text = PK.t("PLC-Ki is not answering. Values frozen since {time}.", { time: clock(state.lastOk) }); kind = "frozen"; }
    else if (c && (c.state === "disconnected" || c.state === "connecting" || c.state === "waiting") && c.lastGoodRead) {
      text = PK.t("No connection to the PLC. Values are from the last good read at {time}.", { time: clock(c.lastGoodRead) }); kind = "frozen";
    }
    else if (stop) {
      text = PK.t("PLC in {word} {since}. The program is not running; what you see is its last state.", { word: stop.word || "STOP", since: cpuSince(stop) });
      kind = "stop"; link = `<a href="#/plc">${PK.t("PLC page")}</a>`;
    }
    else if (c && c.state === "snapshot") { text = PK.t("Test values from a file, not the real machine."); kind = "test"; }
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
    const page = current ? PK.t((HELP[current.name] || [""])[0]) : "";
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
  // Sound, notices and spacing follow the login; "keep this screen on" belongs to the device, so it stays in this browser.
  // A screen set to stay on before the update keeps it: until "plcki.awake" is written, it is read from the old "plcki.screen".
  const awakeHere = () => {
    const a = ls.get("plcki.awake");
    if (a != null) return a === "1";
    try { return !!(JSON.parse(ls.get("plcki.screen") || "{}") || {}).awake; } catch { return false; }
  };
  const screenOpt = {};
  const readScreenOpt = () => {
    for (const k of Object.keys(screenOpt)) delete screenOpt[k];
    Object.assign(screenOpt, prefs.get("screen", {}));
    screenOpt.awake = awakeHere();
  };
  readScreenOpt();
  const saveScreenOpt = () => {
    const { awake, ...rest } = screenOpt;
    ls.set("plcki.awake", awake ? "1" : "0");
    prefs.set("screen", rest);
  };

  // ---- Spacing (item 36): Compact for control-room PCs, Touch for tablets and gloves. "auto" is Touch on a touch screen. ----
  const coarse = window.matchMedia ? matchMedia("(pointer: coarse)") : { matches: false, addEventListener() {} };
  const densityChoice = () => screenOpt.density || "auto";
  function applyDensity() {
    const c = densityChoice();
    document.documentElement.dataset.density = c === "auto" ? (coarse.matches ? "touch" : "normal") : c;
  }
  applyDensity();
  coarse.addEventListener("change", applyDensity);
  // Which alarms beep or show a notice (review 56), kept per login: per severity and per station.
  //   { fault: "all" | "notice" | "none", warning: same, quiet: [station keys] }
  //   all = beep and notice (each as switched on), notice = a notice only, none = nothing. Default: all, as before.
  const soundRule = () => ({ fault: "all", warning: "all", quiet: [], ...prefs.get("alarmSound", {}) });
  const isWarning = (a) => String(a.severity || "").toUpperCase() === "WARNING";
  /** FAULT and WARNING as the server sends them, in the language in use (only for showing them). */
  const sevWord = (s) => { const u = String(s || "").toUpperCase(); return u === "FAULT" ? PK.t("FAULT") : u === "WARNING" ? PK.t("WARNING") : s; };
  /** "all", "notice" or null (nothing) for one alarm. */
  function alarmWants(a, rule = soundRule()) {
    const level = isWarning(a) ? rule.warning : rule.fault;
    if (level === "none") return null;
    if ((rule.quiet || []).length && typeof PKStations !== "undefined" && state.status) {
      const at = PKStations.list(state.status).stations.filter((s) => s.alarms.some((x) => x.id === a.id));
      if (at.length && at.every((s) => rule.quiet.includes(s.key))) return null;     // only at quiet stations
    }
    return level === "notice" ? "notice" : "all";
  }
  let seenAlarms = null;
  function alertNew(al) {
    if (!state.status || Date.now() - state.lastOk > 5000) return;
    const ids = new Set(al.map((a) => a.id));
    const news = seenAlarms ? al.filter((a) => !seenAlarms.has(a.id)) : [];
    seenAlarms = ids;
    const rule = soundRule();
    const fresh = news.filter((a) => alarmWants(a, rule));
    if (!fresh.length) return;
    const beeps = fresh.filter((a) => alarmWants(a, rule) === "all");
    if (screenOpt.sound && beeps.length) beep(beeps.some((a) => !isWarning(a)));
    if (screenOpt.notify && "Notification" in window && Notification.permission === "granted") {
      const a = fresh[0];
      try { const n = new Notification(`${sevWord(a.severity)} ${a.id}: ${a.text}`, { body: state.status.machine || "PLC-Ki", tag: "plcki-" + a.id }); n.onclick = () => { window.focus(); if (!tvOn) go("#/alarm"); }; } catch { /* not allowed here */ }
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
    const level = soundRule().fault;      // STOP counts as a fault
    if (level === "none") return;
    if (screenOpt.sound && level === "all") beep(true);
    if (screenOpt.notify && "Notification" in window && Notification.permission === "granted") {
      try {
        const n = new Notification(PK.t("The PLC is in {word}", { word: c.word || "STOP" }), { body: PK.t("{machine}: the program is not running.", { machine: state.status.machine || "PLC-Ki" }), tag: "plcki-cpu" });
        n.onclick = () => { window.focus(); if (!tvOn) go("#/alarm"); };
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
  document.addEventListener("visibilitychange", () => { if ((screenOpt.awake || tvOn) && document.visibilityState === "visible") keepAwake(true); });

  // ---- Line screen (TV, review 44): no menu or top bar, dark unless Light was chosen, the screen kept on ----
  // Nothing is saved: the theme is only painted (not written to the login's choices or "plcki.paint"), and leaving
  // puts back what was there. The page itself is tv.js.
  let tvOn = false;
  function tvMode(on) {
    on = !!on;
    if (tvOn === on) return;
    tvOn = on;
    document.documentElement.classList.toggle("tv", on);
    const chosen = prefs.get("theme", "auto");
    if (on) { if (chosen !== "light") document.documentElement.dataset.theme = "dark"; keepAwake(true); }
    else {
      if (chosen === "auto") delete document.documentElement.dataset.theme; else document.documentElement.dataset.theme = chosen;
      showTheme();
      if (!screenOpt.awake) keepAwake(false);
    }
  }

  /**
   * Set a password: one's own (asks for the current one) or, for an administrator, someone else's.
   * Asks twice so a typing mistake does not lock anyone out.
   */
  function passwordSheet(forName) {
    const own = !forName;
    const s = sheet(`<h2>${own ? PK.t("Change my password") : PK.t("New password for {name}", { name: esc(forName) })}</h2>
      <form class="gap-3 stack" data-pw-form>
        ${own ? `<label class="field">${PK.t("Current password")}<input type="password" id="pw-cur" autocomplete="current-password" required></label>` : ""}
        <label class="field">${PK.t("New password")} <span class="hint">${PK.t("at least 8 characters")}</span><input type="password" id="pw-new" autocomplete="new-password" minlength="8" required ${own ? "" : "autofocus"}></label>
        <label class="field">${PK.t("New password again")}<input type="password" id="pw-rep" autocomplete="new-password" required></label>
        <p class="m-0 ink-fault muted small" id="pw-err" role="alert"></p>
        <div class="row end"><button type="button" class="btn gray" data-close>${PK.t("Cancel")}</button><button type="submit" class="btn">${PK.t("Set password")}</button></div>
      </form>`);
    const f = s.el.querySelector("[data-pw-form]");
    const err = (t) => { s.el.querySelector("#pw-err").textContent = t; };
    if (own) setTimeout(() => s.el.querySelector("#pw-cur").focus(), 50);
    f.onsubmit = async (e) => {
      e.preventDefault();
      const pw = f.querySelector("#pw-new").value, rep = f.querySelector("#pw-rep").value;
      if (pw.length < 8) return err(PK.t("The new password needs at least 8 characters."));
      if (pw !== rep) return err(PK.t("The two new passwords are not the same. Type them again."));
      try {
        if (own) await api("POST", "/api/auth/password", { current: f.querySelector("#pw-cur").value, password: pw });
        else await api("PUT", `/api/admin/users/${encodeURIComponent(forName)}/password`, { password: pw });
        s.close();
        toast(own ? PK.t("Your password is changed.") : PK.t("Password for {name} is changed.", { name: forName }));
      } catch (x) { err(PK.srv(x.message)); }
    };
  }

  // ---- Light or dark: follows this PC unless chosen here (a bright wall screen may want dark) ----
  const sysDark = window.matchMedia ? window.matchMedia("(prefers-color-scheme: dark)") : { matches: false, addEventListener() {} };
  const themeChoice = () => document.documentElement.dataset.theme || "auto";
  const darkNow = () => themeChoice() === "dark" || (themeChoice() === "auto" && sysDark.matches);
  function applyTheme(t) {
    if (t === "auto") delete document.documentElement.dataset.theme; else document.documentElement.dataset.theme = t;
    ls.set("plcki.paint", t === "auto" ? null : t);     // the first paint of the next page load (index.html), before the login is known
    showTheme();
  }
  function setTheme(t) {
    prefs.set("theme", t === "auto" ? null : t);
    applyTheme(t);
  }
  function showTheme() {
    const b = $("theme-btn");
    if (!b) return;
    const dark = darkNow();
    b.innerHTML = icon(dark ? "sun" : "moon");
    b.title = dark ? PK.t("Switch to a light screen") : PK.t("Switch to a dark screen");
    b.setAttribute("aria-label", b.title);
  }
  if (sysDark.addEventListener) sysDark.addEventListener("change", showTheme);

  /** "Which alarms" in the sound sheet: faults and warnings each beep, notice only or nothing; stations kept quiet. */
  function soundRulesHtml() {
    const r = soundRule();
    const pick = (key, label) => `<label class="field small">${label}<select data-sound="${key}">${[["all", PK.t("Beep")], ["notice", PK.t("Notice only")], ["none", PK.t("Nothing")]]
      .map(([v, l]) => `<option value="${v}" ${r[key] === v ? "selected" : ""}>${l}</option>`).join("")}</select></label>`;
    const stations = typeof PKStations !== "undefined" && state.status ? PKStations.list(state.status).stations.filter((s) => s.key) : [];
    const quiet = stations.length > 1 ? `<details class="quiet-st"${(r.quiet || []).length ? " open" : ""}><summary>${PK.t("Quiet stations")}</summary>
      <p class="muted small m-0">${PK.t("No beep or notice for alarms that only concern these stations. Alarms of the whole machine always count.")}</p>
      ${stations.map((s) => `<label class="row small"><input type="checkbox" data-quiet="${esc(s.key)}" ${(r.quiet || []).includes(s.key) ? "checked" : ""}> ${esc(s.name)}</label>`).join("")}</details>` : "";
    return `<div class="opt-row" style="cursor:default;flex-wrap:wrap"><span class="grow"><b>${PK.t("Which alarms")}</b><br><span class="muted small">${PK.t("For example warnings silent and faults beep. Beep also shows a notice when notices are on. A CPU STOP counts as a fault.")}</span></span>
      <div class="sound-rules">${pick("fault", PK.t("Faults"))}${pick("warning", PK.t("Warnings"))}</div>${quiet}</div>`;
  }

  /** A language in its own words, the same in both languages. */
  const langName = (k) => (k === "de" ? "Deutsch" : "English");

  function screenSheet() {
    const secure = window.isSecureContext;
    const canNotify = secure && "Notification" in window;
    const canWake = secure && "wakeLock" in navigator;
    const row = (key, title, text, ok, why) => `<label class="opt-row ${ok ? "" : "off"}"><input type="checkbox" data-opt="${key}" ${screenOpt[key] && ok ? "checked" : ""} ${ok ? "" : "disabled"}>
      <span><b>${esc(title)}</b><br><span class="muted small">${esc(ok ? text : why)}</span></span></label>`;
    const notHere = PK.t("Not possible on this connection: the browser allows it only on the PLC-Ki PC itself (localhost) or over https.");
    const tvStations = typeof PKStations !== "undefined" && state.status ? PKStations.list(state.status).stations.filter((x) => x.key) : [];
    const group = (t) => `<h3 class="more-group">${t}</h3>`;
    const s = sheet(`<h2>${PK.t("My settings")}</h2>
      <p class="m-0 muted">${prefs.perLogin ? PK.t("Saved for {name}, on every device you log in on. \"Keep this screen on\" is for this device only.", { name: esc(state.user.name) }) : PK.t("Only for this browser on this device.")} ${PK.t("Nothing here changes the machine.")}</p>
      <div class="gap-3 stack">
        ${group(PK.t("Alarm sound"))}
        ${row("sound", PK.t("Beep when a new alarm comes"), PK.t("A short tone: three beeps for a fault, one for a warning. Leave this page open in a tab."), true, "")}
        ${row("notify", PK.t("Show a notice when a new alarm comes"), PK.t("A message from the browser, also when PLC-Ki is in a background tab."), canNotify, notHere)}
        ${soundRulesHtml()}
        ${group(PK.t("This screen"))}
        ${row("awake", PK.t("Keep this screen on"), PK.t("For a screen on the line: the display does not go dark while this page is open."), canWake, notHere)}
        <div class="opt-row" style="cursor:default;flex-wrap:wrap"><span class="grow"><b>${PK.t("Line screen (TV)")}</b><br><span class="muted small">${PK.t("For a TV on the line: no menu, the state in big letters and the machine picture. It turns to the alarm when one comes and keeps the screen on. Esc leaves it.")}</span></span>
          <span class="row end" style="flex-basis:100%">${tvStations.length ? `<select id="tv-pick" class="start-sel" style="flex:1 1 12em;width:auto" aria-label="${PK.t("Line screen for")}"><option value="#/tv">${PK.t("Whole machine")}</option>${tvStations.map((x) => `<option value="#/tv/${esc(encodeURIComponent(x.key))}">${esc(x.name)}</option>`).join("")}</select>` : ""}
          <button type="button" class="btn" data-tv-open>${PK.t("Line screen (TV)")}</button></span></div>
        <div class="opt-row" style="cursor:default;flex-wrap:wrap"><span class="grow"><b>${PK.t("Screen colours")}</b><br><span class="muted small">${PK.t("Dark suits a wall screen in a bright hall.")}</span></span>
          ${ui.choice(PK.t("Screen colours"), [["auto", PK.t("Like this PC")], ["light", PK.t("Light")], ["dark", PK.t("Dark")]].map(([k, l]) => ({ label: l, on: themeChoice() === k, data: { "data-theme-set": k } })))}</div>
        <div class="opt-row" style="cursor:default;flex-wrap:wrap"><span class="grow"><b>${PK.t("Spacing")}</b><br><span class="muted small">${PK.t("Compact fits more on a control-room PC. Touch makes buttons bigger for a tablet or gloves.")}</span></span>
          ${ui.choice(PK.t("Spacing"), [["auto", PK.t("Like this device")], ["compact", PK.t("Compact")], ["touch", PK.t("Touch")]].map(([k, l]) => ({ label: l, on: densityChoice() === k, data: { "data-density-set": k } })))}</div>
        ${group(PK.t("Language and start page"))}
        <label class="opt-row" style="cursor:default;flex-wrap:wrap"><span class="grow"><b>${PK.t("Language")}</b><br><span class="muted small">${PK.t("The language of the menus, buttons and messages. Alarm texts, step names and tag comments stay as the PLC program has them.")}</span></span>
          <select id="lang-pick" class="start-sel" aria-label="${PK.t("Language")}"><option value="">${PK.t("As for this plant: {language}", { language: langName(state.info && state.info.language) })}</option>
            ${["en", "de"].map((k) => `<option value="${k}" ${prefs.get("language", "") === k ? "selected" : ""}>${langName(k)}</option>`).join("")}</select></label>
        <label class="opt-row" style="cursor:default;flex-wrap:wrap"><span class="grow"><b>${PK.t("Dates and numbers")}</b><br><span class="muted small">${PK.t("How times, dates and decimals are written on every page.")}</span></span>
          <select id="fmt-pick" class="start-sel" aria-label="${PK.t("Dates and numbers")}"><option value="">${PK.t("As for this plant: {format}", { format: esc(FORMATS[plantFormat()].label) })}</option>
            ${Object.entries(FORMATS).map(([k, f]) => `<option value="${k}" ${prefs.get("format", "") === k ? "selected" : ""}>${esc(f.label)}</option>`).join("")}</select></label>
        ${state.user ? `<label class="opt-row" style="cursor:default;flex-wrap:wrap"><span class="grow"><b>${PK.t("Start page")}</b><br><span class="muted small">${PK.t("Where PLC-Ki opens for you when no page is in the address. A link or a bookmark still opens its own page.")}</span></span>
          <select id="start-page" class="start-sel" aria-label="${PK.t("Start page")}"><option value="">${esc(PK.t(AS_FOR[state.user.role] || "As for operators: {page}", { page: startLabel(state.roleStart || "#/alarm") }))}</option>
            ${startChoices([prefs.get("startPage", "")]).map(([h, l]) => `<option value="${esc(h)}" ${prefs.get("startPage", "") === h ? "selected" : ""}>${esc(l)}</option>`).join("")}</select></label>` : ""}
      </div>
      <div class="row end"><button class="btn plain" type="button" data-test-beep>${PK.t("Test the beep")}</button><button class="btn" data-close>${PK.t("Done")}</button></div>`);
    s.el.addEventListener("change", async (e) => {
      const box = e.target.closest("[data-opt]"); if (!box) return;
      const key = box.dataset.opt;
      if (key === "notify" && box.checked && Notification.permission !== "granted") {
        const p = await Notification.requestPermission().catch(() => "denied");
        if (p !== "granted") { box.checked = false; toast(PK.t("The browser did not allow notices. Allow them in the browser's site settings."), "error"); return; }
      }
      screenOpt[key] = box.checked; saveScreenOpt();
      if (key === "awake") keepAwake(box.checked);
      if (key === "sound" && box.checked) beep(false);
    });
    s.el.querySelector("[data-test-beep]").onclick = () => beep(true);
    s.el.querySelector("[data-tv-open]").onclick = () => { const p = s.el.querySelector("#tv-pick"); s.close(); go(p ? p.value : "#/tv"); };
    const lp = s.el.querySelector("#lang-pick");
    // The pages are drawn again in the new language: a reload is the simple way, after the choice reached the server
    lp.onchange = async () => { prefs.set("language", lp.value || null); await prefs.flush(); location.reload(); };
    const fp = s.el.querySelector("#fmt-pick");
    fp.onchange = () => { prefs.set("format", fp.value || null); draw(); toast(PK.t("Saved: {date}.", { date: date(Date.now()) })); };
    s.el.querySelectorAll("[data-sound]").forEach((sel) => sel.onchange = () => { const r = soundRule(); r[sel.dataset.sound] = sel.value; prefs.set("alarmSound", r); });
    s.el.querySelectorAll("[data-quiet]").forEach((box) => box.onchange = () => {
      const r = soundRule();
      const q = new Set(r.quiet || []);
      if (box.checked) q.add(box.dataset.quiet); else q.delete(box.dataset.quiet);
      r.quiet = [...q];
      prefs.set("alarmSound", r);
    });
    const sp = s.el.querySelector("#start-page");
    if (sp) sp.onchange = () => { prefs.set("startPage", sp.value || null); toast(PK.t("Start page saved.")); };
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
      if (r.ok) { state.status = localTexts(await r.json()); state.lastOk = Date.now(); }
      const run = r.headers.get("X-PlcKi-Run");
      if (run && state.run && run !== state.run) {
        // PLC-Ki was restarted (maybe with another model or program): start this page fresh
        if (!dirty) { location.reload(); return; }
        toast(PK.t("PLC-Ki was restarted. Save, then reload the page to see the current program."), "error");
      }
      if (run) state.run = run;
    } catch { /* shown as not reachable */ }
    renderConn();
    if (current && current.view.live) draw();
    setTimeout(pollStatus, 1000);
  }

  async function pollValues() {
    if (current && current.view.needsValues) {
      try { state.values = localTexts(await api("GET", "/api/values")); } catch { /* next time */ }
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
          const title = PK.srv(run.title);
          const text = run.waiting ? PK.t("Needs you: {title}", { title }) : title;
          pill.innerHTML = `<span class="dot"></span><span class="jp-t">${esc(text)}</span><span class="jp-s">${run.waiting ? PK.t("Needs you") : PK.t("Job")}</span>`;
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
    if (kind !== "error" && typeof text === "string") text = PK.srv(text);    // a message the server wrote
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

  // ---- Sheets and the Back button ----
  // A sheet opened with { back: true } (More, Help, Search) gets a history entry of its own, so the phone's or the
  // browser's Back closes the sheet first and leaves the page as it is. A link inside it goes back over that entry
  // first, then to the link, so the history holds no step that only reopens nothing.
  const openSheets = [];       // newest last: { close, dismiss, mark }
  let afterSheetBack = null;   // where to go once Back has taken a sheet's entry away
  const sheetEntry = (mark) => !!mark && !!history.state && history.state.pkSheet === mark;
  window.addEventListener("popstate", () => {
    const now = history.state && history.state.pkSheet;
    openSheets.filter((s) => s.mark && s.mark !== now).reverse().forEach((s) => s.close("pop"));
    if (afterSheetBack != null) { const h = afterSheetBack; afterSheetBack = null; go(h); }
  });
  /** route(): the sheets of the page that is left close with it (not the ones that wait for a job). */
  function closeSheets() { openSheets.filter((s) => s.dismiss).reverse().forEach((s) => s.close("route")); }
  /** Going to a page from a sheet that has its own Back step: take that step first, then go. True when it did. */
  function leaveSheetTo(hash) {
    const s = [...openSheets].reverse().find((x) => sheetEntry(x.mark));
    if (!s) return false;
    afterSheetBack = hash;
    s.close();
    return true;
  }

  /**
   * A modal sheet; returns {el, close}. Esc, a click outside it and any [data-close] button close it,
   * unless dismiss is false (sheets that wait for a job, such as a real-PLC confirmation or a restart).
   * back: true gives it a history entry, so Back closes it (see above).
   */
  function sheet(html, { dismiss = true, onClose, back: withBack = false } = {}) {
    const mark = withBack && dismiss ? "s" + Math.random().toString(36).slice(2, 8) : null;
    if (mark) history.pushState({ ...(history.state || {}), pkSheet: mark }, "");
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
    const entry = { dismiss, mark };
    const close = (how) => {
      if (closed) return;
      closed = true;
      openSheets.splice(openSheets.indexOf(entry), 1);
      wrap.classList.add("out");
      setTimeout(() => wrap.remove(), matchMedia("(prefers-reduced-motion: reduce)").matches ? 0 : 160);
      if (back && back.isConnected && back.focus) back.focus({ preventScroll: true });
      // Closed with Done, Esc or a click outside: take its history entry away again (Back already did, a new page left it behind)
      if (how !== "pop" && how !== "route" && sheetEntry(mark)) history.back();
      if (onClose) onClose();
    };
    entry.close = close;
    openSheets.push(entry);
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
    el.addEventListener("click", (e) => {
      // A page link in a sheet with its own Back step: that step first, then the page (a new tab or window as usual)
      const a = mark && e.target.closest('a[href^="#"]');
      if (a && !e.defaultPrevented && !e.button && !e.ctrlKey && !e.metaKey && !e.shiftKey && !e.altKey && !a.target && sheetEntry(mark)) {
        e.preventDefault();
        afterSheetBack = a.getAttribute("href");
        close();
        return;
      }
      if (e.target.closest("[data-close]")) close();
    });
    requestAnimationFrame(() => { if (!el.contains(document.activeElement)) (el.querySelector("[autofocus]") || el.querySelector(".row.end .btn:last-child") || el).focus({ preventScroll: true }); });
    return { el, close };
  }

  function confirmSheet({ title, text, ok = PK.t("OK"), cancel = PK.t("Cancel"), danger = false }) {
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
    const now = c.values && c.values.length ? `<span class="now">${PK.t("now")} ${c.values.map((v) => esc(v.label) + " <span class=val>" + esc(v.text) + "</span>").join(", ")}</span>` : "";
    return `<li><span class="mark ${cls}" aria-label="${cls === "yes" ? PK.t("met") : cls === "no" ? PK.t("not met") : PK.t("unknown")}">${sym}</span><span>${esc(c.text)} ${now}</span></li>`;
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

  // ---- Dates, times and numbers: one format everywhere (review 57). The plant picks one (Setup, Settings), each
  // login may pick its own (My settings). "text" is the default: "3 Nov 2026, 14:02", no ambiguous 03/11.
  const MONTHS_EN = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
  const MONTHS_DE = ["Jan", "Feb", "Mär", "Apr", "Mai", "Jun", "Jul", "Aug", "Sep", "Okt", "Nov", "Dez"];
  const WEEKDAYS_EN = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
  const WEEKDAYS_DE = ["So", "Mo", "Di", "Mi", "Do", "Fr", "Sa"];
  const MONTHS = new Proxy([], { get: (_, i) => (I18N.lang === "de" ? MONTHS_DE : MONTHS_EN)[i] });
  const WEEKDAYS = new Proxy([], { get: (_, i) => (I18N.lang === "de" ? WEEKDAYS_DE : WEEKDAYS_EN)[i] });
  const pad = (n) => String(n).padStart(2, "0");
  const FORMATS = {
    text: { h12: false, order: "text", comma: false, get label() { return I18N.lang === "de" ? "3. Nov 2026, 14:02 · 12.5" : "3 Nov 2026, 14:02 · 12.5"; } },
    dot: { h12: false, order: "dot", comma: true, label: "03.11.2026, 14:02 · 12,5" },
    iso: { h12: false, order: "iso", comma: false, label: "2026-11-03 14:02 · 12.5" },
    us: { h12: true, order: "us", comma: false, label: "11/03/2026, 2:02 pm · 12.5" },
  };
  /** The format in use: this login's choice, else the plant's. While nobody chose, German pages use the German
   *  way (03.11.2026, 24 h, decimal comma) and English pages "3 Nov 2026, 14:02" (review 49). */
  const plantFormat = () => {
    const i = state.info || {};
    const k = (i.formatChosen === false && I18N.lang === "de" ? "dot" : i.format) || "text";
    return FORMATS[k] ? k : "text";
  };
  const fmtKey = () => { const k = prefs.get("format", ""); return FORMATS[k] ? k : plantFormat(); };
  const fmt = () => FORMATS[fmtKey()];
  /** "14:02", or "2:02 pm" with the 12-hour clock */
  const hm = (t, secs) => {
    const d = new Date(t);
    if (isNaN(d)) return "";
    const s = secs ? ":" + pad(d.getSeconds()) : "";
    if (!fmt().h12) return `${pad(d.getHours())}:${pad(d.getMinutes())}${s}`;
    const h = d.getHours() % 12 || 12;
    return `${h}:${pad(d.getMinutes())}${s} ${d.getHours() < 12 ? "am" : "pm"}`;
  };
  /** "14:02:13" */
  const clock = (t) => hm(t, true);
  /** The day without the year: "3 Nov", "03.11.", "11-03", "11/03" */
  const dayMonth = (t) => {
    const d = new Date(t), o = fmt().order;
    return o === "dot" ? `${pad(d.getDate())}.${pad(d.getMonth() + 1)}.` : o === "iso" ? `${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
      : o === "us" ? `${pad(d.getMonth() + 1)}/${pad(d.getDate())}` : `${d.getDate()}${I18N.lang === "de" ? "." : ""} ${MONTHS[d.getMonth()]}`;
  };
  /** "Tue 3 Nov" */
  const weekdayDay = (t) => `${WEEKDAYS[new Date(t).getDay()]} ${dayMonth(t)}`;
  const joint = () => (fmt().order === "iso" ? " " : ", ");
  /** "3 Nov 2026" or, with time, "3 Nov 2026, 14:02" (in the format in use) */
  const date = (t, withTime = true) => {
    const d = new Date(t);
    if (isNaN(d)) return "";
    const o = fmt().order, y = d.getFullYear(), m = pad(d.getMonth() + 1), day = pad(d.getDate());
    const ds = o === "dot" ? `${day}.${m}.${y}` : o === "iso" ? `${y}-${m}-${day}` : o === "us" ? `${m}/${day}/${y}` : `${d.getDate()}${I18N.lang === "de" ? "." : ""} ${MONTHS[d.getMonth()]} ${y}`;
    return ds + (withTime ? joint() + hm(d) : "");
  };
  /** "14:02:13" today, "3 Nov, 14:02" before */
  const when = (t) => {
    const d = new Date(t), now = new Date();
    return d.toDateString() === now.toDateString() ? clock(d) : dayMonth(d) + joint() + hm(d);
  };
  /** A number for people: 12.5 or 12,5. digits: fixed decimals (optional). */
  const num = (x, digits) => {
    if (x == null || x === "" || !isFinite(x)) return "–";
    const s = digits == null ? String(Number(x)) : Number(x).toFixed(digits);
    return fmt().comma ? s.replace(".", ",") : s;
  };
  /**
   * A value text from the server ("12.5 bar") in the format in use ("12,5 bar"). Only a whole text that is one
   * decimal number with an optional unit is changed; addresses (%I4.2), IPs, versions and sentences stay as they are.
   */
  const VALUE_TEXT = /^(-?\d+)\.(\d+)((?: ?[^\d.,:\s][^\d.,:]{0,11})?)$/;
  const numText = (s) => (typeof s === "string" && fmt().comma ? s.replace(VALUE_TEXT, "$1,$2$3") : s);
  /** Every "text" of a value from /api/values or /api/status, in place. */
  function localTexts(o, depth = 0) {
    if (!fmt().comma || !o || typeof o !== "object" || depth > 12) return o;
    if (Array.isArray(o)) { o.forEach((x) => localTexts(x, depth + 1)); return o; }
    const textual = typeof o.type === "string" && /string|char/i.test(o.type);     // a String tag's text is not a number
    for (const k of Object.keys(o)) {
      const v = o[k];
      if (typeof v === "string") { if (k === "text" && !textual) o[k] = numText(v); }
      else if (v && typeof v === "object") localTexts(v, depth + 1);
    }
    return o;
  }

  const ago = (t) => {
    const s = Math.round((Date.now() - new Date(t).getTime()) / 1000);
    if (s < 60) return PK.t("{n} s ago", { n: s });
    if (s < 3600) return PK.t("{n} min ago", { n: Math.round(s / 60) });
    if (s < 86400) return PK.t("{n} h ago", { n: Math.round(s / 3600) });
    return date(t, false);
  };

  // ---- plain language: what a status or error means and what to do ----
  /** Turns a technical error into { text, todo } a person can act on. */
  function plain(message, where = {}) {
    const m = String(message || "");
    const plc = where.plc || (state.status && state.status.connection && state.status.connection.address) || PK.t("the PLC");
    // "the PLC" / "die SPS" starts a sentence below: German needs the capital there
    const Plc = plc === PK.t("the PLC") && I18N.lang === "de" ? plc.charAt(0).toUpperCase() + plc.slice(1) : plc;
    if (/failed to fetch|networkerror|load failed/i.test(m)) return { text: PK.t("PLC-Ki itself is not answering."), todo: PK.t("Check that the PLC-Ki window on the PC is still open. Start it again with Start-PLC-Ki in the PLC-Ki folder.") };
    if (/timed out|timeout|within \d+ s|no answer/i.test(m) && /5090|tia|helper|importer/i.test(m)) return { text: PK.t("The TIA helper did not answer."), todo: PK.t("On the VM (or TIA PC): check the window \"PLC-Ki TIA helper\" is open. If not, double-click Start-TIA-helper in C:\\PLCKi\\PLC-Ki\\tools\\TiaImporter.") };
    if (/refused the token|401/i.test(m)) return { text: PK.t("The TIA helper does not accept the token."), todo: PK.t("Type the same token here as the one shown in the TIA helper window on the VM.") };
    if (/timed out|timeout|no answer|unreachable|no route|host is down/i.test(m)) return { text: PK.t("{plc} does not answer.", { plc: Plc }), todo: PK.t("Check the PLC is on and the cable or virtual switch is connected. On the PC, ping {plc} should answer. For PLCSIM: is the virtual PLC started in PLCSIM Advanced?", { plc }) };
    if (/no such host|name or service not known|nodename nor servname|not known on this network|host not found/i.test(m)) return { text: PK.t("{plc} is not known on the network.", { plc: Plc }), todo: PK.t("Check the spelling. Use the PLC's IP address from TIA (Device configuration › PROFINET interface), for example 192.168.0.10.") };
    if (/is not an IP address/i.test(m)) return { text: PK.srv(m), todo: "" };
    if (/refused/i.test(m)) return { text: PK.t("Something is at {plc}, but it does not accept the connection.", { plc: Plc }), todo: PK.t("Check it is really the PLC's address, and that PUT/GET or the access level allows reading (TIA: CPU properties, Protection & Security).") };
    if (/already been opened|project\/library .* cannot be accessed/i.test(m)) return { text: PK.t("TIA could not open the project: it is open in TIA Portal."), todo: PK.t("Close the project in TIA Portal on the TIA PC (or wait 2 minutes after a crash), then start the job again. Nothing was imported.") };
    if (/password|access.?(level|protection|denied|rights)|legitimation|protection/i.test(m)) return { text: PK.t("The PLC asks for a password."), todo: PK.t("Enter the CPU's access password under Setup, Connections (More PLC options)."), op: PK.t("Ask an administrator to enter the PLC's access password in PLC-Ki.") };
    if (/api key|anthropic/i.test(m)) return { text: PK.t("No Claude API key is set."), todo: PK.t("Add it under Setup, Settings (Assistant). It starts with sk-ant-.") };
    return { text: m, todo: "" };
  }
  // The server only said a status code (it had no words of its own)
  function plainHttp(m) {
    const http = /(?:Request failed|Anfrage fehlgeschlagen) \((\d+)\)/i.exec(m);    // api() writes it in the language in use
    if (!http) return null;
    if (http[1] === "403") return { text: PK.t("Your login is not allowed to do this."), todo: "" };
    if (http[1] === "404") return { text: PK.t("PLC-Ki could not find that."), todo: PK.t("Reload the page. If it stays, it may have been removed.") };
    if (+http[1] >= 500) return { text: PK.t("PLC-Ki hit a problem doing this."), todo: PK.t("Try again. If it keeps happening, the PLC-Ki window on the PC shows what went wrong.") };
    return { text: PK.t("PLC-Ki did not accept this."), todo: PK.t("Reload the page and try again.") };
  }
  // plain()'s rules are about reaching the PLC, the TIA helper or Ki. Other messages ("The new password needs at least
  // 8 characters.", "The PLC refused the value…") are already written for people and must not be rewritten by them.
  const ABOUT_A_LINK = /failed to fetch|networkerror|load failed|timed out|timeout|within \d+ s|unreachable|no route|host is down|no such host|not known|name or service|connection|connect|tia helper|5090|importer|token|401|already been opened|cannot be accessed|api key|anthropic|legitimation/i;
  // The program export: Hardware, Tags, Documents and Program all read it, and say the same when it is missing
  function plainProgram(m) {
    if (/No program export is set|No program loaded/i.test(m)) return { text: PK.t("No PLC program is loaded yet."), todo: PK.t("Import it under Setup, Import a program."), op: PK.t("Ask an administrator to import the PLC program into PLC-Ki.") };
    if (/Cannot read the program export/i.test(m)) return { text: PK.t("PLC-Ki cannot read the program file."), todo: PK.t("Import the program again under Setup, Import a program."), op: PK.t("Ask an administrator to import the PLC program again.") };
    if (/program export has no PLC/i.test(m)) return { text: PK.t("The imported program has no PLC in it."), todo: PK.t("Import the program again and choose the PLC."), op: PK.t("Ask an administrator to import the PLC program again.") };
    if (/is not in the program export/i.test(m)) return { text: PK.srv(m.replace(/program export/i, "loaded program")), todo: PK.t("The program may have changed since it was imported. Import it again under Setup, Import a program."), op: PK.t("The program may have changed. Ask an administrator to import it again.") };
    return null;
  }
  /** plain() for the person looking: operators cannot open Setup, so they read who to ask instead. */
  function plainFor(message, where) {
    const m = String((message && message.message) || message || "");
    const p = plainProgram(m) || plainHttp(m) || ((where && where.plc) || ABOUT_A_LINK.test(m) ? plain(m, where) : { text: PK.srv(m), todo: "" });
    if (!can("settings") && p.op != null && /Setup|Einrichtung/.test(p.todo)) p.todo = p.op;
    return p;
  }

  /** What the PLC connection state means, for the pill sheet and empty pages. Operators cannot open Setup, so they read who to ask. */
  function connInfo() {
    const i = connInfoAll();
    if (can("settings")) return i;
    if (i.todo && /Setup|Einrichtung|Connections|Verbindungen|Link/.test(i.todo)) i.todo = i.op || PK.t("Ask an administrator of PLC-Ki to check the connection to the PLC.");
    return i;
  }
  function connInfoAll() {
    const c = state.status && state.status.connection;
    const stale = Date.now() - state.lastOk > 5000;
    if (!state.status || stale) return { level: "bad", title: PK.t("PLC-Ki is not answering."), text: PK.t("This page cannot reach PLC-Ki."), todo: PK.t("Check that the PLC-Ki window on the PC is still open. If it was closed, double-click Start-PLC-Ki in the PLC-Ki folder, then reload this page.") };
    if (c.state === "connected") return { level: "ok", title: PK.t("Reading the PLC at {address}.", { address: c.address }), text: PK.t("Values are live, read once a second."), todo: "" };
    if (c.state === "snapshot") return { level: "info", title: PK.t("Showing test values, not a PLC."), text: PK.t("PLC-Ki reads values from a test file."), todo: PK.t("To read a real PLC, choose Live PLC under Setup, Connections (More PLC options)."), op: PK.t("Ask an administrator to switch PLC-Ki to the real PLC.") };
    if (c.state === "notset") return { level: "warn", title: PK.t("No PLC is set yet."), text: PK.t("PLC-Ki does not know which PLC to read, so there are no live values."), todo: PK.t("Enter the PLC's address under Setup, Connections."), op: PK.t("Ask an administrator to set up the connection to the PLC.") };
    if (c.state === "connecting") return { level: "warn", title: PK.t("Connecting to {address}…", { address: c.address }), text: PK.t("This takes a few seconds."), todo: "" };
    if (c.state === "waiting") return { level: "warn", title: PK.t("Waiting for PLC-Ki Link."), text: PK.t("The PLC is read by PLC-Ki Link on your laptop, and it is not connected."), todo: PK.t("On the laptop, start PLC-Ki Link (Setup, Connections shows the command)."), op: PK.t("Ask an administrator to start PLC-Ki Link on the laptop that is connected to the PLC.") };
    const p = plain(c.error, { plc: c.address });
    return { level: "bad", title: PK.t("No connection to the PLC at {address}.", { address: c.address }), text: p.text === c.error ? "" : p.text, todo: p.todo, op: p.op, detail: c.error };
  }

  function connSheet() {
    const i = connInfo();
    const admin = can("settings");
    const a = access();
    sheet(`<span class="pill ${i.level}" style="align-self:flex-start;justify-self:start"><span class="dot"></span>${{ ok: PK.t("Connected"), bad: PK.t("Not connected"), warn: PK.t("Waiting"), info: PK.t("Test values") }[i.level]}</span>
      <h2>${esc(i.title)}</h2>${i.text ? `<p class="m-0 muted">${esc(i.text)}</p>` : ""}
      ${a.text ? `<p class="m-0 ${a.level === "real" ? "notice gold" : "muted"}">${esc(a.text)}</p>` : ""}
      ${i.todo ? `<div class="notice ${i.level === "bad" ? "red" : "gold"}">${icon("info")}<div><b>${PK.t("What to do")}</b><br>${esc(i.todo)}</div></div>` : ""}
      ${i.detail ? `<details class="more"><summary>${PK.t("Technical detail")}</summary><p class="mt-2 mb-0 faint small">${esc(i.detail)}</p></details>` : ""}
      <div class="row end">${admin && i.level !== "ok" ? `<a class="btn b" href="#/setup/connections" data-close>${PK.t("Open Connections")}</a>` : ""}<button class="btn" data-close>${PK.t("Done")}</button></div>`);
  }

  /** A command to type, with where to type it ("On the VM") and a Copy button. One line where possible. */
  function cmd(where, text, note = "") {
    const id = "c" + Math.random().toString(36).slice(2, 8);
    return `<div class="cmd"><div class="cmd-head"><span class="where where-${esc(where.split(" ").pop().toLowerCase())}">${esc(PK.t(where))}</span>${note ? `<span class="faint small">${note}</span>` : ""}<span class="spacer"></span>
      <button type="button" class="btn plain small" data-copy="${id}">${PK.t("Copy")}</button></div><pre id="${id}">${esc(text)}</pre></div>`;
  }
  document.addEventListener("click", async (e) => {
    const b = e.target.closest("[data-copy]");
    if (!b) return;
    const el = document.getElementById(b.dataset.copy);
    try { await navigator.clipboard.writeText(el.textContent); b.textContent = PK.t("Copied"); setTimeout(() => { b.textContent = PK.t("Copy"); }, 1600); }
    catch { const r = document.createRange(); r.selectNodeContents(el); getSelection().removeAllRanges(); getSelection().addRange(r); toast(PK.t("Selected. Press Ctrl+C to copy.")); }
  });

  // ---- Help: what the current page shows and how to read it ----
  const HELP = {
    documents: [I18N.T("Documents"), I18N.T("Device manuals, fault lists and electrical drawings. Ask and the Active alarm page look things up in them."), [
      I18N.T("Search: type words, a fault code as the device shows it (F07801, SRVO-062, 0x8C10), a terminal (X2:14) or an address (%I12.3). Fault codes come first, then the pages that match. A page opens in the browser at that page."),
      I18N.T("For this machine: documents only this machine uses, such as its EPLAN drawing. For every machine: manuals you use everywhere, such as the SINAMICS list manual."),
      I18N.T("Administrators add files by dropping them on the page. PDFs need text in them: a scanned paper page has none. A fault list can also be a CSV file with code, text, cause and remedy columns."),
      I18N.T("Built in: general Siemens, FANUC and IO-Link knowledge. When a manual knows the same code, the manual comes first.")]],
    alarm: [I18N.T("Active alarm"), I18N.T("The start page. It shows the alarm that matters most right now and how to get the machine going again."), [
      I18N.T("With several alarms, the first chip is the first-out: the alarm that came first, as PLC-Ki saw them come. Fix that one first; the others often follow from it."),
      I18N.T("Fix it: work down the list and tap each step when done. The ticks are only on this screen."),
      I18N.T("Where in the machine: the device behind the alarm and the chain Requested › Interlocks › Output › Feedback. The first red link is why it is not working."),
      I18N.T("Where in the software: from the main cycle (OB1) down to the bit that sets the alarm. Show where it is set opens that network as rungs with live values."),
      I18N.T("The seconds before it came: every change of the machine's tags in the 10 s before the alarm, recorded much finer than the normal reads. Short pulses (amber) are signals the once-a-second reads would miss."),
      I18N.T("History of this alarm: how often it came, how long it stayed, and the cause and fix technicians noted last time. When you have found the cause, tap Note cause and fix so the next person sees it."),
      I18N.T("Ask about this alarm: Ki looks at the live values, the last minutes, History and the program, and says why it is on and what to check.")]],
    plant: [I18N.T("Plant"), I18N.T("What the machine is doing now. The top shows the step, what it waits for and the key values; the tabs below hold the rest."), [
      I18N.T("Overview: Needs attention lists active alarms and blocked or faulted devices first; tap one to go to it. Below it the sequence: the blue step is where it is now, red means halted, ✓ is met, ✗ is missing."),
      I18N.T("The machine at a glance: every device as a symbol. Green runs, grey is off, a dashed amber edge means the program asks for it but an interlock blocks it, red is a fault. Tap one to open it on Devices."),
      I18N.T("Devices: the list on the left (needs attention first), the picked device on the right with the chain Requested › Interlocks › Output › Feedback. The first red link is why it is not running."),
      I18N.T("Values over time: the measured values of the last minutes, with the step band and alarm marks under them. Move along a chart to read every value at that moment."),
      I18N.T("Machine settings: the values an operator or engineer sets on the HMI panel or in the recipe, with what they are now. ✗ means the machine cannot work with that value."),
      I18N.T("Data exchange: signals to and from MES, a filler and so on, with their live values.")]],
    sequence: [I18N.T("Step chart"), I18N.T("One sequence as a chain of steps, read like the GRAPH editor, with how long each step took."), [
      I18N.T("Step timeline: which step ran when in the last minutes, and the last and average time of each step. A step that takes longer than usual is where the machine waits."),
      I18N.T("Step chart: each box is a step, the bar under it is the transition to the next one with its conditions. ↷ marks a jump to another step; \"from 12\" above a step says which steps jump to it."),
      I18N.T("The blue step is where it is now. Its conditions are live: ✓ is met, ✗ is missing. Tap any step for what it does.")]],
    alarms: [I18N.T("Alarms"), I18N.T("Every alarm the program can raise, active or not, with its meaning, where it is set, likely causes and the fix."), [
      I18N.T("Search by number, text or device. Active alarms are at the top."),
      I18N.T("Click an alarm to open its causes and fix. Red tags are faults (they stop the machine), gold tags are warnings."),
      I18N.T("Past alarms, what fixed them and the seconds before each one are in History.")]],
    history: [I18N.T("History"), I18N.T("The one place for past alarms: every alarm PLC-Ki saw on this machine, what it was doing then, and what the technicians found and did."), [
      I18N.T("The time window at the top (24 h, 7 days, 30 days, 1 year) applies to Log and Ranking."),
      I18N.T("Log: every alarm that came, newest first. Waiting for a note lists alarms of the last 12 hours that nobody wrote anything for yet. PLC and connection lists when the CPU stopped or the link to the PLC dropped."),
      I18N.T("What fixed it? After an alarm goes, write down the cause and the fix in one line. The next time it comes, the Active alarm page and Ask show it first: \"last time it was a dirty sensor\"."),
      I18N.T("Ranking: the alarms that came most often, or that were on longest in total. Tap one for its own history."),
      I18N.T("Recordings: the seconds before each alarm came, from every change the PLC sends (usually sampled every 100 ms). Amber marks a short pulse the normal reads would miss; a Blip is an alarm that went again before a normal read saw it. The last 100 are kept."),
      I18N.T("Download for Excel: the log as a file for the maintenance team. The history is kept on this PC in the PLC-Ki data folder and survives restarts.")]],
    program: [I18N.T("Program"), I18N.T("Every block of the PLC program with live values, as rungs or as text."), [
      I18N.T("Pick a block on the left. At the top, \"Why outputs are off right now\" names each output that is off and the signal that stops it."),
      I18N.T("Rungs: each network drawn as a rung. Green passes power, grey does not, a dashed ? has no live value. Read from left to right: the amber contact is the first one that stops the output."),
      I18N.T("Click a contact or a signal to see what sets it: the network in this block, the block that writes it, or the block that calls this one for an input. Click an output to see where it is used."),
      I18N.T("Text: the code as written in TIA (LAD and GRAPH as readable text with the same meaning), with the live value after each signal."),
      I18N.T("LAD, FBD and GRAPH blocks open as rungs, SCL blocks as text. Switch with Rungs / Text. Coming from an alarm, the network that sets it is marked."),
      I18N.T("Values update every two seconds.")]],
    tags: [I18N.T("Tags"), I18N.T("The PLC tag tables with addresses, and every data block's values, live."), [
      I18N.T("Tag tables: every tag with its address (%I, %Q, %M), type and comment. Search by name, address or comment."),
      I18N.T("Data blocks: pick one on the left to see all its values. Optimized blocks have no addresses; values are read by name."),
      I18N.T("Up to 300 rows are read live at a time, about once a second.")]],
    plc: [I18N.T("PLC"), I18N.T("The PLC in three tabs: the CPU, its racks with every input and output, and what it reports."), [
      I18N.T("CPU: RUN, STOP or STARTUP as the CPU reports it, with the position of the mode switch where the CPU tells it. The picture is drawn from the order number; page through its display with the keys under it."),
      I18N.T("Start and Stop work only when PLC-Ki reads the PLCSIM CPU of the TIA helper, and only for administrators. You confirm first. A real PLC is never started or stopped from PLC-Ki."),
      I18N.T("ERROR and MAINT come from PLCSIM where it tells them; on a real CPU they are not read and stay dark here."),
      I18N.T("Racks and I/O: pick a device at the top (PLC, I/O stations, drives, robots), then tap a module for its channels. Channels are matched to the PLC tags by address, so you see the tag, its comment and the live value."),
      I18N.T("If only the CPU shows, the program export has no hardware yet: Read the hardware from TIA (administrators) takes a minute or two and changes nothing else."),
      I18N.T("Diagnostics: what the CPU reports for PROFINET devices, modules, channels and drives, read live, with what it means and what to check.")]],
    reports: [I18N.T("Reports"), I18N.T("Shift report with OEE, and a summary for maintenance, from what PLC-Ki records while it watches the machine."), [
      I18N.T("Shift report: OEE and its three parts, a bar of where the time went (running, waiting, down by a fault, no PLC connection), parts made, the biggest losses and the alarms of that shift."),
      I18N.T("Summary: down time, average repair time (MTTR), running time between faults (MTBF), why the machine was not running, and every shift's OEE, over 24 h, 7 days, 30 days or 1 year. Which alarms cost the most time is in History, Ranking."),
      I18N.T("Download for Excel saves a CSV file; Print or PDF prints the page."),
      I18N.T("PLC-Ki records only while it runs and reads the PLC. Time it was off or without connection is shown and left out of OEE."),
      I18N.T("Report setup (administrators): shift times, planned stops, the part and reject counters and the ideal cycle time. Without a counter every finished sequence cycle counts as one part.")]],
    "setup/start": [I18N.T("Get started"), I18N.T("Sets PLC-Ki up one question at a time. You can come back here any time."), [
      I18N.T("Each step is saved when you press Continue. Skip what you don't need now: TIA Portal is only needed to import a program or build blocks."),
      I18N.T("At the end, Apply restarts PLC-Ki with the new settings (a few seconds).")]],
    "setup/connections": [I18N.T("Connections"), I18N.T("Where PLC-Ki, TIA Portal and the PLC are, and whether each link works."), [
      I18N.T("The drawing turns each line green when it works and red when it does not. Grey means not checked yet."),
      I18N.T("Test the address checks that something answers on the PLC's S7 port. It sends nothing to the PLC."),
      I18N.T("Save, then Apply: PLC-Ki uses new connection settings after a quick restart.")]],
    "setup/program": [I18N.T("Import a program"), I18N.T("Teaches PLC-Ki a machine from its TIA program."), [
      I18N.T("1. Drop the TIA archive (.zap20). 2. TIA exports it on the TIA PC (needs the TIA helper). 3. Ki builds the model: a few minutes, about a dollar. 4. Check the numbers and press Use this program."),
      I18N.T("Import again when the program in the PLC changes, so the explanations match.")]],
    "setup/generator": [I18N.T("Block generator"), I18N.T("Say what you need; Ki, PLC-Ki's block writer, writes the blocks, TIA imports and compiles them, PLCSIM runs them."), [
      I18N.T("New blocks: say what you need, pick the TIA project and press Generate. The package name is made from your words if you leave it empty."),
      I18N.T("Made before: pick a package on the left to see its versions, what Ki changed, Ki's notes and the files. \"Change it\" makes the next version (v002, v003 …); older versions stay."),
      I18N.T("When a job ends, \"What Ki did\" lists the blocks and tags it made, changed or removed against the version before."),
      I18N.T("Ki runs on Claude, with the API key from Settings."),
      I18N.T("Use a copy of the TIA project, not the plant's master."),
      I18N.T("A real PLC needs two confirmations: you type the version here, and someone types the PLC address in the TIA helper window. The CPU must be in STOP.")]],
    station: [I18N.T("Stations"), I18N.T("One page per station, made for a phone: is it OK, its alarms with the fix, its sequence step and its devices."), [
      I18N.T("Scan the QR label on the machine to open its station here. Administrators print the labels in Setup › QR labels."),
      I18N.T("The stations come from the program: each device and sequence belongs to the station (instance) named in its location. An alarm belongs to the station of its device."),
      I18N.T("Tap a device for its values and interlocks. Open the alarm for the full fix list and where it is set in the program."),
      I18N.T("Values update every second.")]],
    tv: [I18N.T("Line screen (TV)"), I18N.T("A page for a TV on the line, readable from a few metres: the state, the step and the machine picture. It turns to the alarm when one comes."), [
      I18N.T("Open it from My settings, or bookmark #/tv (the whole machine) or #/tv/<station> (one station). It can also be a start page."),
      I18N.T("When an alarm comes, the screen shows that alarm large: its text, the station and since when. It goes back to the picture when no alarm is on."),
      I18N.T("The screen stays on and is dark unless Light was chosen. Everything moves a few pixels every few minutes so nothing burns into the TV."),
      I18N.T("It only reads. No login is needed when viewing without login is on. Press Esc or the × in the corner to leave.")]],
    "setup/qr": [I18N.T("QR labels"), I18N.T("Print a QR label per station or device. A phone that scans it opens that station's live page."), [
      I18N.T("Pick the address phones use: the PC's address on the plant network, not localhost. Open it on a phone once to check it works."),
      I18N.T("Phones only reach PLC-Ki when Other computers may open PLC-Ki is on, in Settings › Logins and access."),
      I18N.T("Print on sticker paper or laminate the labels. They keep working after a new program import as long as the station keeps its name.")]],
    "setup/settings": [I18N.T("Settings"), I18N.T("The machine model, the Claude key, who may open PLC-Ki, and logins."), [
      I18N.T("Four tabs: Machine and Ask, Storage, Logins and access, Language and formats. Change what you need, then Save in the bar at the bottom."),
      I18N.T("Operators see every machine page; administrators also see Setup."),
      I18N.T("Other computers may open PLC-Ki: off means only this PC.")]],
    notfound: [I18N.T("Page not found"), I18N.T("The address in the browser does not match a page of PLC-Ki."), [
      I18N.T("Pick a page in the menu, or use one of the links on the page."),
      I18N.T("An old bookmark or a mistyped address leads here. Nothing is wrong with the machine.")]],
    noaccess: [I18N.T("Setup is for administrators"), I18N.T("This address is a Setup page, and only administrators can open Setup."), [
      I18N.T("Every machine page is in the menu."),
      I18N.T("If something in Setup needs changing, ask an administrator.")]],
  };
  // ---- Page not found: an unknown address says so, with a way out, instead of quietly showing Active alarm ----
  /** The page an unknown address most likely meant, only when it is obvious ("generator" → Block generator). */
  function closestPage(typed) {
    const admin = can("settings");
    const t = String(typed || "").toLowerCase();
    const want = t.split("/").pop();
    const names = Object.keys(views).filter((n) => n !== "notfound" && n !== "noaccess" && HELP[n] && (admin || !n.startsWith("setup/")));
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
        near ? `<a class="btn" href="#/${near}">${PK.t("Open {page}", { page: esc(PK.t(HELP[near][0])) })}</a>` : "",
        near === "alarm" ? "" : `<a class="btn${near ? " gray" : ""}" href="#/alarm">${PK.t("Active alarm")}</a>`,
        near === "plant" ? "" : `<a class="btn gray" href="#/plant">${PK.t("Plant")}</a>`,
      ].join("");
      return hero({ title: PK.t("Page not found"), actions: links,
        sub: `${PK.t("There is no page at {address}.", { address: `<code>#/${esc(typed)}</code>` })}${near ? " " + PK.t("Did you mean {page}?", { page: esc(PK.t(HELP[near][0])) }) : " " + PK.t("It may be an old bookmark or a typo.")}` });
    },
  });
  // ---- Setup for a technician or an operator (an old bookmark, a link from a colleague): say so, with the way to their start page.
  // route() sends every setup/* address here for a login that may not change settings; the server refuses those answers anyway.
  view("noaccess", {
    transient: true,      // the back chip skips it: it only says where to go
    render() {
      const start = /^#\/setup(\/|$)/.test(startPage()) ? "#/alarm" : startPage();
      return hero({ title: PK.t("Setup is for administrators"),
        sub: PK.t("This login sees the machine pages, not Setup. If something in Setup needs changing, ask an administrator."),
        actions: `<a class="btn" href="${esc(start)}">${PK.t("Open {page}", { page: esc(startLabel(start)) })}</a>` });
    },
  });

  function helpSheet() {
    const name = current ? current.name : "alarm";
    const [title, intro, tips] = HELP[name] || HELP.alarm;
    const admin = can("settings");
    const a = access(name);
    const h = sheet(`<span class="eyebrow">${PK.t("Help")}</span><h2>${esc(PK.t(title))}</h2><p class="m-0 muted">${esc(PK.t(intro))}</p>
      <ul class="plainlist">${tips.map((t) => `<li>${esc(PK.t(t))}</li>`).join("")}</ul>
      ${a.text ? `<p class="m-0 small">${esc(a.text)}</p>` : ""}
      <p class="m-0 faint small">${PK.t("The light at the top right shows the PLC connection; click it for what it means. Ask answers questions about the machine on every page.")}</p>
      <p class="m-0 faint small"><button type="button" class="linkbtn" data-screen>${PK.t("My settings")}</button>: ${PK.t("the alarm sound, this screen, your language and start page.")}</p>
      <p class="m-0 faint small keys"><kbd>${PK.t("Ctrl K")}</kbd> ${PK.t("search")} · <kbd>?</kbd> ${PK.t("this help")} · <kbd>/</kbd> ${PK.t("ask Ki")} · <kbd>Esc</kbd> ${PK.t("close")}</p>
      <div class="row end">${admin && name !== "setup/start" ? `<a class="btn b" href="#/setup/start" data-close>${PK.t("Get started")}</a>` : ""}<button class="btn" data-close>${PK.t("Done")}</button></div>`, { back: true });
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
    const pageLists = groups.map((g) => `${g ? `<h3 class="more-group">${esc(PK.t(g))}</h3>` : ""}<ul class="mlist" aria-label="${esc(g ? PK.t(g) : PK.t("Pages"))}">${pages.filter((a) => (a.dataset.group || "") === g).map(pageRow).join("")}</ul>`).join("");
    const acct = [
      row(item("sliders", PK.t("My settings")), 'data-m="screen"'),
      row(item("moon", darkNow() ? PK.t("Light screen") : PK.t("Dark screen")), 'data-m="theme"'),
      state.user ? row(item("key", PK.t("Change my password")), 'data-m="pw"') : "",
      row(item("logout", state.user ? PK.t("Log out") : PK.t("Log in")), 'data-m="logout"'),
    ].join("");
    const who = state.user ? `<p class="faint small more-who">${esc(state.user.name)} · ${roleWord(state.user.role)}</p>` : "";
    const s = sheet(`<div class="more-head"><h2>${PK.t("More")}</h2><button class="btn plain small" type="button" data-close>${PK.t("Done")}</button></div>
      ${pageLists}
      ${who}<ul class="mlist" aria-label="${PK.t("Account")}">${acct}</ul>`, { back: true });
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
      ;
    const listAll = items.length > 3 ? PK.t("{list}, and {n} more", { list, n: items.length - 3 }) : list;
    bar.innerHTML = `<div class="wrap"><div class="notice gold held-strip" role="status">${icon("warn")}
      <div><b>${PK.t("{n} input held on PLCSIM|{n} inputs held on PLCSIM", { n: items.length })}</b>: ${listAll}. ${items.length === 1 ? PK.t("It stays until released.") : PK.t("They stay until released.")}</div>
      ${admin ? `<div class="held-acts"><a class="btn gray small" href="#/tags">${PK.t("Show")}</a><button type="button" class="btn small" data-release-all>${PK.t("Release all")}</button></div>` : ""}</div></div>`;
    const b = bar.querySelector("[data-release-all]");
    if (b) b.onclick = releaseAll;
  }
  // ---- Shift handover (review 3, item 47): the note the last shift left, as one entry in the notice strip on every page
  // during the first hours of the next shift (GET /api/handover/now), until "Got it" in this browser. Checked again every minute, so a panel PC
  // whose page stays open across the shift change shows it too.
  let handover = null;
  const hoKey = (n) => `${n.day}|${n.shiftIndex}|${n.editedAt || n.at}`;
  const hoSeen = () => prefs.get("handoverSeen", "");
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
      <div><b>${PK.t("Handover from the {shift} shift", { shift: esc(n.shift) })}</b> <span class="small">(${esc(n.editedBy || n.by)}, ${hm(at)})</span>
        <div class="ho-text">${esc(text)}</div></div>
      <a class="btn gray small" href="#/reports/shift/${esc(n.day)}/${n.shiftIndex}">${PK.t("Read")}</a><button type="button" class="btn small" data-ho-ok>${PK.t("Got it")}</button></div>`;
  }
  function handoverSeen() {
    if (handover) prefs.set("handoverSeen", hoKey(handover));
    handover = null;
    renderNotices();
  }
  const showHandover = () => renderNotices();

  /** Puts every held input back to the value it had before its hold (administrators). */
  async function releaseAll() {
    const n = ((held && held.items) || []).length;
    const ok = await confirmSheet({ title: n === 1 ? PK.t("Release the held input?") : PK.t("Release all {n} held inputs?", { n }),
      text: n === 1 ? PK.t("PLC-Ki puts the input back to the value it had before the hold.") : PK.t("PLC-Ki puts each input back to the value it had before the hold."), ok: n === 1 ? PK.t("Release") : PK.t("Release all") });
    if (!ok) return;
    try {
      const r = await api("POST", "/api/admin/hold/release", { all: true });
      const bad = r.results.filter((x) => !x.ok);
      toast(bad.length ? PK.t("{n} could not be released: {error}", { n: bad.length, error: PK.srv(bad[0].error) }) : PK.t("Released. The inputs have their earlier values again."), bad.length ? "error" : undefined);
    } catch (e) { toast(e.message, "error"); }
    await loadHeld();
    window.dispatchEvent(new Event("pk:held-changed"));
  }

  async function start() {
    let st0Language = "";
    fillIcons();
    try {
      const st = await api("GET", "/api/auth/state");
      state.user = st.user;
      state.roleStart = st.roleStart || "";
      st0Language = st.language || "";
    } catch { /* public view */ }
    await loadPrefs();
    // The language (review 49): this login's choice (this browser's when logged out), else the plant's, else English.
    try { state.info = await api("GET", "/api/info"); } catch { /* PLC-Ki not answering: English */ }
    I18N.setLang(prefs.get("language", "") || (state.info && state.info.language) || (state.user ? "" : st0Language) || "en");
    I18N.dom(document.body);
    document.dispatchEvent(new Event("pk:lang"));     // what a script drew before the language was known draws itself again
    // "Ask Ki" reads "Ki fragen" in German; the narrow bar keeps just "Ki"
    if (I18N.lang === "de") $("ask-open").innerHTML = `Ki<span class="ask-ki"> fragen</span><kbd>/</kbd>`;
    readScreenOpt();
    applyDensity();
    applyTheme(prefs.get("theme", "auto"));
    const admin = can("settings");
    document.querySelectorAll(".admin-only").forEach((el) => el.classList.toggle("hidden", !admin));
    // Public view without a login: Ask needs a login (it spends Claude credit), so its buttons are hidden
    document.documentElement.classList.toggle("anon", !state.user);
    if (can("plcsim")) setInterval(loadChanges, 60000);   // "changes allowed" can change without a restart
    const who = state.user ? state.user.name + " · " + roleWord(state.user.role) : PK.t("Not logged in");
    document.querySelectorAll(".who").forEach((el) => { el.textContent = who; });
    document.querySelectorAll(".act-logout").forEach((el) => { el.textContent = state.user ? PK.t("Log out") : PK.t("Log in"); });
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
    if (state.info) { $("machine").textContent = state.info.machine; machineLine(); }
    loadProgram().then(machineLine);   // the top bar names the CPU and the program file
    $("help-open").onclick = helpSheet;
    if (screenOpt.awake) keepAwake(true);
    $("conn").setAttribute("role", "button");
    $("conn").tabIndex = 0;
    $("conn").title = PK.t("What does this mean?");
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
    bar.setAttribute("aria-label", PK.t("Unsaved changes"));
    bar.innerHTML = `<span class="dot"></span><span>${PK.t("Unsaved changes")}</span><button type="button" class="btn gray small" data-discard>${PK.t("Discard")}</button><button type="button" class="btn small" data-save-now>${PK.t("Save")}</button>`;
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
    // Start page (review 53): only when PLC-Ki opens without a page in the address; a link or bookmark wins
    if (!location.hash || location.hash === "#" || location.hash === "#/") {
      const startAt = startPage();
      if (startAt !== "#/alarm") history.replaceState(null, "", startAt);
    }
    // Reloaded while More, Help or Search was open: its history entry has no sheet now, so step back over it
    // (else the first Back would do nothing). The same page, so no hashchange.
    if (history.state && history.state.pkSheet) history.back();
    route();
    pollStatus();
    pollValues();
    pollJobs();
    if (state.user) { loadHeld(); setInterval(loadHeld, 15000); }
    loadHandover(); setInterval(loadHandover, 60000);
  }

  return { AI, ui, t: I18N.t, T: I18N.T, srv: I18N.srv, get lang() { return I18N.lang; }, BP, can, testValues, roleTitle, prefs, startPage, startChoices, startLabel, hm, num, numText, dayMonth, weekdayDay, FORMATS, fmtKey, plantFormat, alarmWants, screenSize, dirty: setDirty, get isDirty() { return !!dirty; }, plain, connInfo, connSheet, helpSheet, screenSheet, passwordSheet, setHash, cmd, sec, hero, on, $, esc, icon, fillIcons, kindIcon, api, state, view, go, draw, route, toast, copyText, sheet, confirmSheet, cond, md, ago, clock, date, when,
    loadProgram, blockOf, valueBySymbol, norm, start, cpuStop, cpuSince, loadHeld, releaseAll, loadHandover, tvMode, get current() { return current; } };
})();
