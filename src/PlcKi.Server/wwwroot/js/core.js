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
    pill.title = "What does this mean?";
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
  function toast(text, kind) {
    const t = $("toast");
    t.textContent = text;
    t.classList.toggle("err", kind === "error");
    t.classList.add("on");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => t.classList.remove("on"), kind === "error" ? 7000 : 3200);
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

  // ---- plain language: what a status or error means and what to do ----
  /** Turns a technical error into { text, todo } a person can act on. */
  function plain(message, where = {}) {
    const m = String(message || "");
    const plc = where.plc || (state.status && state.status.connection && state.status.connection.address) || "the PLC";
    if (/failed to fetch|networkerror|load failed/i.test(m)) return { text: "PLC-Ki itself is not answering.", todo: "Check that the PLC-Ki window on the PC is still open. Start it again with Start-PLC-Ki in the PLC-Ki folder." };
    if (/timed out|timeout|within \d+ s|no answer/i.test(m) && /5090|tia|helper|importer/i.test(m)) return { text: "The TIA helper did not answer.", todo: "On the VM (or TIA PC): check the window \"PLC-Ki TIA helper\" is open. If not, double-click Start-TIA-helper in C:\\PLCKi\\PLC-Ki\\tools\\TiaImporter." };
    if (/refused the token|401/i.test(m)) return { text: "The TIA helper does not accept the token.", todo: "Type the same token here as the one shown in the TIA helper window on the VM." };
    if (/timed out|timeout|no answer|unreachable|no route|host is down/i.test(m)) return { text: `${plc} does not answer.`, todo: `Check the PLC is on and the cable or virtual switch is connected. On the PC, ping ${plc} should answer. For PLCSIM: is the virtual PLC started in PLCSIM Advanced?` };
    if (/refused/i.test(m)) return { text: `Something is at ${plc}, but it does not accept the connection.`, todo: "Check it is really the PLC's address, and that PUT/GET or the access level allows reading (TIA: CPU properties, Protection & Security)." };
    if (/password|access|legitimation|protection/i.test(m)) return { text: "The PLC asks for a password.", todo: "Enter the CPU's access password under Setup, Connections (More PLC options)." };
    if (/api key|anthropic/i.test(m)) return { text: "No Claude API key is set.", todo: "Add it under Setup, Settings (Assistant). It starts with sk-ant-." };
    return { text: m, todo: "" };
  }

  /** What the PLC connection state means, for the pill sheet and empty pages. */
  function connInfo() {
    const c = state.status && state.status.connection;
    const stale = Date.now() - state.lastOk > 5000;
    if (!state.status || stale) return { level: "bad", title: "PLC-Ki is not answering.", text: "This page cannot reach PLC-Ki.", todo: "Check that the PLC-Ki window on the PC is still open. If it was closed, double-click Start-PLC-Ki in the PLC-Ki folder, then reload this page." };
    if (c.state === "connected") return { level: "ok", title: `Reading the PLC at ${c.address}.`, text: "Values are live, read once a second. PLC-Ki only reads: it never writes values or changes the CPU state.", todo: "" };
    if (c.state === "snapshot") return { level: "info", title: "Showing test values, not a PLC.", text: "PLC-Ki reads values from a test file.", todo: "To read a real PLC, choose Live PLC under Setup, Connections (More PLC options)." };
    if (c.state === "connecting") return { level: "warn", title: `Connecting to ${c.address}…`, text: "This takes a few seconds.", todo: "" };
    if (c.state === "waiting") return { level: "warn", title: "Waiting for PLC-Ki Link.", text: "The PLC is read by PLC-Ki Link on your laptop, and it is not connected.", todo: "On the laptop, start PLC-Ki Link (Setup, Connections shows the command)." };
    const p = plain(c.error, { plc: c.address });
    return { level: "bad", title: `No connection to the PLC at ${c.address}.`, text: p.text === c.error ? "" : p.text, todo: p.todo, detail: c.error };
  }

  function connSheet() {
    const i = connInfo();
    const admin = state.user && state.user.role === "admin";
    const s = sheet(`<span class="pill ${i.level}" style="align-self:flex-start;justify-self:start"><span class="dot"></span>${{ ok: "Connected", bad: "Not connected", warn: "Waiting", info: "Test values" }[i.level]}</span>
      <h2>${esc(i.title)}</h2>${i.text ? `<p class="muted" style="margin:0">${esc(i.text)}</p>` : ""}
      ${i.todo ? `<div class="notice ${i.level === "bad" ? "red" : "gold"}">${icon("info")}<div><b>What to do</b><br>${esc(i.todo)}</div></div>` : ""}
      ${i.detail ? `<details class="more"><summary>Technical detail</summary><p class="faint small" style="margin:8px 0 0">${esc(i.detail)}</p></details>` : ""}
      <div class="row end">${admin && i.level !== "ok" ? `<a class="btn b" href="#/setup/connections" data-close>Open Connections</a>` : ""}<button class="btn" data-close>OK</button></div>`);
    s.el.querySelectorAll("[data-close]").forEach((b) => b.addEventListener("click", s.close));
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
    alarm: ["Active alarm", "The start page. It shows the alarm that matters most right now and how to get the machine going again.", [
      "With several alarms, the first chip is the first-out: the alarm that came first. Fix that one first; the others often follow from it.",
      "Fix it: work down the list and tap each step when done. The ticks are only on this screen.",
      "Where in the machine: the device behind the alarm and the chain Requested › Interlocks › Output › Feedback. The first red link is why it is not working.",
      "Where in the software: from the main cycle (OB1) down to the bit that sets the alarm. Show the logic opens that network with live values.",
      "Ask about this alarm: Claude looks at the live values, the last minutes and the program, and says why it is on and what to check."]],
    plant: ["Plant", "What the machine is doing now: the sequence, every device, and data to and from other systems.", [
      "Sequence: the blue step is where it is now, red means halted. Below it, what it needs to go to the next step: ✓ is met, ✗ is missing.",
      "Equipment: each device with the chain Requested › Interlocks › Output › Feedback. The first red link is why it is not running. Click a device for its interlocks.",
      "Data exchange: signals to and from MES, a filler and so on, with their live values."]],
    alarms: ["Alarms", "Every alarm the program can raise, active or not, with its meaning, where it is set, likely causes and the fix.", [
      "Search by number, text or device. Active alarms are at the top.",
      "Click an alarm to open its causes and fix. Red tags are faults (they stop the machine), gold tags are warnings."]],
    logic: ["Logic", "Each network of a block drawn as a rung, with live values.", [
      "Green passes, red blocks. Read from left to right: the first red contact is what stops the output.",
      "Pick a block on the left. Coming from an alarm, the network that sets it is marked.",
      "Values update every two seconds. PLC-Ki only reads: nothing here changes the PLC."]],
    code: ["Code", "Every block as written in TIA, with the live value after each signal.", [
      "Pick a block on the left. SCL shows as text; LAD and GRAPH as readable text with the same meaning.",
      "The chips after a signal are its live value. Values update every two seconds."]],
    "setup/start": ["Get started", "Sets PLC-Ki up one question at a time. You can come back here any time.", [
      "Each step is saved when you press Continue. Skip what you don't need now: TIA Portal is only needed to import a program or build blocks.",
      "At the end, Apply restarts PLC-Ki with the new settings (a few seconds)."]],
    "setup/connections": ["Connections", "Where PLC-Ki, TIA Portal and the PLC are, and whether each link works.", [
      "The drawing turns each line green when it works and red when it does not. Grey means not checked yet.",
      "Test the address checks that something answers on the PLC's S7 port. It sends nothing to the PLC.",
      "Save, then Apply: PLC-Ki uses new connection settings after a quick restart."]],
    "setup/program": ["Program", "Teaches PLC-Ki a machine from its TIA program.", [
      "1. Drop the TIA archive (.zap20). 2. TIA exports it on the TIA PC (needs the TIA helper). 3. Claude builds the model: a few minutes, about a dollar. 4. Check the numbers and press Use this program.",
      "Import again when the program in the PLC changes, so the explanations match."]],
    "setup/generator": ["Block generator", "Say what you need; Claude writes the blocks, TIA imports and compiles them, PLCSIM runs them.", [
      "Each job becomes a package with versions v001, v002 and so on. To change blocks, open a package and say what to change.",
      "Use a copy of the TIA project, not the plant's master.",
      "A real PLC needs two confirmations: you type the version here, and someone types the PLC address in the TIA helper window. The CPU must be in STOP."]],
    "setup/settings": ["Settings", "The machine model, the Claude key, who may open PLC-Ki, and logins.", [
      "Operators see every machine page; administrators also see Setup.",
      "Other computers may open PLC-Ki: off means only this PC."]],
  };
  function helpSheet() {
    const name = current ? current.name : "alarm";
    const [title, intro, tips] = HELP[name] || HELP.alarm;
    const admin = state.user && state.user.role === "admin";
    const s = sheet(`<span class="eyebrow">Help</span><h2>${esc(title)}</h2><p class="muted" style="margin:0">${esc(intro)}</p>
      <ul class="plainlist">${tips.map((t) => `<li>${esc(t)}</li>`).join("")}</ul>
      <p class="faint small" style="margin:0">The light at the top right shows the PLC connection; click it for what it means. Ask answers questions about the machine on every page.</p>
      <div class="row end">${admin && name !== "setup/start" ? `<a class="btn b" href="#/setup/start" data-close>Get started</a>` : ""}<button class="btn" data-close>OK</button></div>`);
    s.el.querySelectorAll("[data-close]").forEach((b) => b.addEventListener("click", s.close));
  }

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
    $("help-open").onclick = helpSheet;
    $("conn").setAttribute("role", "button");
    $("conn").tabIndex = 0;
    $("conn").title = "What does this mean?";
    $("conn").onclick = connSheet;
    $("conn").onkeydown = (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); connSheet(); } };
    window.addEventListener("hashchange", route);
    window.addEventListener("scroll", () => $("topnav").classList.toggle("scrolled", window.scrollY > 4), { passive: true });
    if (admin && !location.hash && typeof PKStart !== "undefined" && (await PKStart.firstRun())) history.replaceState(null, "", "#/setup/start");
    route();
    pollStatus();
    pollValues();
    pollJobs();
  }

  return { plain, connInfo, connSheet, helpSheet, cmd, sec, hero, reveal: (el) => reveal(el), $, esc, icon, fillIcons, kindIcon, api, state, view, go, draw, route, toast, sheet, confirmSheet, cond, md, ago,
    loadProgram, valueBySymbol, norm, start, get current() { return current; } };
})();
