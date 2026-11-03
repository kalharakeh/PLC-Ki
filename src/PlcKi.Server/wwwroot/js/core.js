// PLC-Ki app core: routing, live data, small helpers. Plain JavaScript, no libraries, so a plant PC
// without internet shows the same page. Views register with PK.view(name, {...}) in the other files.
"use strict";

const PK = (() => {
  const $ = (id) => document.getElementById(id);
  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  // ---- icons (SF Symbols-like line icons) ----
  const P = {
    bell: '<path d="M6 16V11a6 6 0 1 1 12 0v5l2 2H4l2-2Z"/><path d="M10 20a2 2 0 0 0 4 0"/>',
    plant: '<path d="M3 21V10l5 3V10l5 3V6h4l1 15"/><path d="M3 21h18"/><path d="M7 17h2M12 17h2"/>',
    list: '<path d="M9 6h11M9 12h11M9 18h11"/><circle cx="4.5" cy="6" r="1.2"/><circle cx="4.5" cy="12" r="1.2"/><circle cx="4.5" cy="18" r="1.2"/>',
    rung: '<path d="M3 4v16M21 4v16M3 12h4M17 12h4"/><path d="M8 8v8M11 8v8M15 9a3 3 0 0 1 0 6"/><path d="M11 12h2"/>',
    code: '<path d="m8 7-5 5 5 5M16 7l5 5-5 5M14 4l-4 16"/>',
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
    cpu: '<rect x="5" y="5" width="14" height="14" rx="2"/><rect x="9" y="9" width="6" height="6"/><path d="M9 2v3M15 2v3M9 19v3M15 19v3M2 9h3M2 15h3M19 9h3M19 15h3"/>',
    tia: '<path d="M4 6h16M4 12h10M4 18h7"/><path d="m16 15 3 3 3-6"/>',
    play: '<path d="M7 4v16l13-8L7 4Z"/>',
    stop: '<rect x="6" y="6" width="12" height="12" rx="2"/>',
    download: '<path d="M12 4v11M7 10l5 5 5-5M4 20h16"/>',
    file: '<path d="M14 3H6v18h12V7l-4-4Z"/><path d="M14 3v4h4"/>',
    eye: '<path d="M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12Z"/><circle cx="12" cy="12" r="3"/>',
    clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
    shield: '<path d="M12 3 4 6v6c0 5 3.5 8 8 9 4.5-1 8-4 8-9V6l-8-3Z"/>',
    user: '<circle cx="12" cy="8" r="4"/><path d="M4 21c1-4 4-6 8-6s7 2 8 6"/>',
  };
  const icon = (name, cls = "") =>
    `<svg class="${cls}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${P[name] || P.device}</svg>`;
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
  const views = {};
  let current = null;       // { name, view, params }
  let lastHtml = "";
  let firstDraw = true;
  const io = "IntersectionObserver" in window
    ? new IntersectionObserver((es) => es.forEach((e) => { if (e.isIntersecting) { e.target.classList.add("in"); io.unobserve(e.target); } }), { rootMargin: "0px 0px -8% 0px" })
    : null;

  /** Sections rise in as they scroll into view, once per page (not on every live update). */
  function reveal(root) {
    if (!firstDraw || !io || matchMedia("(prefers-reduced-motion: reduce)").matches) return;
    if (root.querySelectorAll(":scope > section").length < 2) return;   // still loading
    firstDraw = false;
    root.querySelectorAll(":scope > section").forEach((sec, i) => {
      sec.classList.add("reveal");
      if (i === 0) requestAnimationFrame(() => sec.classList.add("in")); else io.observe(sec);
    });
  }

  // Page building blocks in the house style: full-width sections with a centred column
  const sec = (cls, html) => `<section class="${cls}"><div class="wrap">${html}</div></section>`;
  const hero = ({ eyebrow = "", title, sub = "", actions = "", cls = "hero", extra = "" }) =>
    sec(cls, `${eyebrow ? `<div class="eyebrow">${eyebrow}</div>` : ""}<h1>${title}</h1>${sub ? `<p class="sub">${sub}</p>` : ""}${actions ? `<div class="actions">${actions}</div>` : ""}${extra}`);

  function view(name, def) { views[name] = def; }

  function parseRoute() {
    const h = location.hash.replace(/^#\/?/, "");
    const parts = h.split("/").filter(Boolean).map(decodeURIComponent);
    if (parts[0] === "setup") return { name: "setup/" + (parts[1] || "connections"), params: parts.slice(2) };
    return { name: parts[0] || "alarm", params: parts.slice(1) };
  }

  function go(hash) { if (location.hash !== hash) location.hash = hash; else route(); }

  function route() {
    const { name, params } = parseRoute();
    const v = views[name] || views.alarm;
    document.querySelectorAll("#nav a").forEach((a) => a.classList.toggle("active", a.dataset.route === name || (a.dataset.route === "setup" && name.startsWith("setup/"))));
    const page = $("page");
    if (current && current.view.leave) current.view.leave();
    current = { name, view: v, params };
    lastHtml = "";
    page.innerHTML = "";
    firstDraw = true;
    if (v.mount) v.mount(page, params);
    else draw();
    page.scrollTop = 0;
    window.scrollTo(0, 0);
  }

  // Live views return their HTML from render(); it is only put on the page when it changed
  function draw() {
    if (!current || !current.view.render) return;
    let html;
    try { html = current.view.render(current.params); }
    catch (e) { html = `<div class="notice red">${icon("warn")}<div>This page hit a problem: ${esc(e.message)}</div></div>`; console.error(e); }
    if (html === lastHtml) return;
    const page = $("page");
    const y = window.scrollY;
    page.innerHTML = html;
    lastHtml = html;
    fillIcons(page);
    reveal(page);
    if (current.view.after) current.view.after(page, current.params);
    window.scrollTo(0, y);
  }

  // ---- live polling ----
  function renderConn() {
    const pill = $("conn");
    const c = state.status && state.status.connection;
    const stale = Date.now() - state.lastOk > 5000;
    let text, cls;
    if (!state.status || stale) { text = "PLC-Ki not reachable"; cls = "bad"; }
    else {
      const map = {
        connected: ["Live · " + c.address, "ok"],
        snapshot: ["Test values · " + c.address.split(/[\\/]/).pop(), "info"],
        connecting: ["Connecting to " + c.address, "warn"],
        disconnected: ["Not connected · " + c.address, "bad"],
        waiting: ["Waiting for PLC-Ki Link", "warn"],
      };
      [text, cls] = map[c.state] || [c.state, "warn"];
    }
    pill.className = "pill " + cls;
    pill.title = (c && c.error) || "";
    pill.innerHTML = `<span class="dot"></span><span>${esc(text)}</span>`;
    const alarms = (state.status && state.status.activeAlarms) || [];
    const b = $("nav-alarm-badge");
    b.textContent = alarms.length;
    b.classList.toggle("hidden", alarms.length === 0);
    b.classList.toggle("gold", alarms.length > 0 && alarms.every((a) => a.severity === "WARNING"));
    if (state.status) $("machine").textContent = state.status.machine;
  }

  async function pollStatus() {
    try {
      const r = await fetch("/api/status", { cache: "no-store" });
      if (r.status === 401) { location.href = "/login.html?next=" + encodeURIComponent("/" + location.hash); return; }
      if (r.ok) { state.status = await r.json(); state.lastOk = Date.now(); }
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

  async function pollJobs() {
    if (state.user && state.user.role === "admin") {
      try {
        state.jobs = await api("GET", "/api/admin/jobs");
        const run = state.jobs.find((j) => j.state === "running");
        const pill = $("job-pill");
        pill.classList.toggle("hidden", !run);
        if (run) {
          pill.innerHTML = `<span class="dot"></span>${esc(run.waiting ? "Needs you: " + run.title : run.title)}`;
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

  // ---- values by symbol, for live chips in Code and Logic ----
  const norm = (s) => String(s || "").replace(/\s+/g, "").toLowerCase();
  function valueBySymbol() {
    const map = new Map();
    for (const t of (state.values && state.values.tags) || []) map.set(norm(t.symbol), t);
    return map;
  }

  // ---- small UI helpers ----
  let toastTimer;
  function toast(text) {
    const t = $("toast");
    t.textContent = text;
    t.classList.add("on");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => t.classList.remove("on"), 3200);
  }

  /** A modal sheet; returns {el, close}. */
  function sheet(html) {
    const wrap = document.createElement("div");
    wrap.className = "sheet-wrap";
    wrap.innerHTML = `<div class="sheet" role="dialog" aria-modal="true">${html}</div>`;
    document.body.append(wrap);
    fillIcons(wrap);
    const close = () => wrap.remove();
    return { el: wrap.firstElementChild, close };
  }

  function confirmSheet({ title, text, ok = "OK", danger = false }) {
    return new Promise((resolve) => {
      const s = sheet(`<h2>${esc(title)}</h2><div class="muted">${text}</div>
        <div class="row end"><button class="btn gray" data-r="0">Cancel</button><button class="btn ${danger ? "red" : ""}" data-r="1">${esc(ok)}</button></div>`);
      s.el.addEventListener("click", (e) => {
        const b = e.target.closest("[data-r]");
        if (b) { s.close(); resolve(b.dataset.r === "1"); }
      });
    });
  }

  function cond(c) {
    const [cls, sym] = c.met === true ? ["yes", "✓"] : c.met === false ? ["no", "✗"] : ["unk", "?"];
    const now = c.values && c.values.length ? `<span class="now">now ${c.values.map((v) => esc(v.label) + " <span class=val>" + esc(v.text) + "</span>").join(", ")}</span>` : "";
    return `<li><span class="mark ${cls}" aria-label="${cls === "yes" ? "met" : cls === "no" ? "not met" : "unknown"}">${sym}</span><span>${esc(c.text)} ${now}</span></li>`;
  }

  function md(text) {
    // The small part of Markdown that REVIEW.md and Claude's answers use
    const inline = (s) => esc(s).replace(/\*\*(.+?)\*\*/g, "<strong>$1</strong>").replace(/`([^`]+)`/g, "<code>$1</code>");
    const out = []; let list = null; let para = [];
    const fp = () => { if (para.length) { out.push("<p>" + para.map(inline).join("<br>") + "</p>"); para = []; } };
    const fl = () => { if (list) { out.push(`<${list.t}>` + list.items.map((i) => `<li>${inline(i)}</li>`).join("") + `</${list.t}>`); list = null; } };
    for (const raw of String(text || "").split(/\r?\n/)) {
      const line = raw.trimEnd();
      const h = line.match(/^(#{1,4})\s+(.*)$/);
      const b = line.match(/^\s*[-*•]\s+(.*)$/);
      const n = line.match(/^\s*\d+[.)]\s+(.*)$/);
      if (h) { fp(); fl(); out.push(`<h3>${inline(h[2])}</h3>`); }
      else if (b || n) { fp(); const t = b ? "ul" : "ol"; if (!list || list.t !== t) { fl(); list = { t, items: [] }; } list.items.push((b || n)[1]); }
      else if (!line.trim()) { fp(); fl(); }
      else if (list && /^\s{2,}/.test(raw)) list.items[list.items.length - 1] += " " + line.trim();
      else { fl(); para.push(line); }
    }
    fp(); fl();
    return out.join("");
  }

  const ago = (t) => {
    const s = Math.round((Date.now() - new Date(t).getTime()) / 1000);
    if (s < 60) return s + " s ago";
    if (s < 3600) return Math.round(s / 60) + " min ago";
    if (s < 86400) return Math.round(s / 3600) + " h ago";
    return new Date(t).toLocaleDateString();
  };

  async function start() {
    fillIcons();
    try {
      const st = await api("GET", "/api/auth/state");
      state.user = st.user;
    } catch { /* public view */ }
    const admin = state.user && state.user.role === "admin";
    document.querySelectorAll(".admin-only").forEach((el) => el.classList.toggle("hidden", !admin));
    $("who").textContent = state.user ? state.user.name + (admin ? " · administrator" : " · operator") : "Not logged in";
    $("logout").textContent = state.user ? "Log out" : "Log in";
    $("logout").onclick = async () => {
      if (state.user) await fetch("/api/auth/logout", { method: "POST" });
      location.href = "/login.html";
    };
    try { state.model = await api("GET", "/api/model"); } catch { /* shown on pages */ }
    try { state.info = await api("GET", "/api/info"); $("machine").textContent = state.info.machine; } catch { /* */ }
    window.addEventListener("hashchange", route);
    window.addEventListener("scroll", () => $("topnav").classList.toggle("scrolled", window.scrollY > 4), { passive: true });
    route();
    pollStatus();
    pollValues();
    pollJobs();
  }

  return { sec, hero, reveal: (el) => reveal(el), $, esc, icon, fillIcons, kindIcon, api, state, view, go, draw, route, toast, sheet, confirmSheet, cond, md, ago,
    loadProgram, valueBySymbol, norm, start, get current() { return current; } };
})();
