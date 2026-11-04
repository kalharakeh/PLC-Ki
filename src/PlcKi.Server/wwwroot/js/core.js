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
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>',
    moon: '<path d="M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5Z"/>',
    help: '<circle cx="12" cy="12" r="9"/><path d="M9.5 9.5a2.5 2.5 0 1 1 3.5 2.3c-.6.3-1 .9-1 1.6V14M12 17.5v.01"/>',
    sound: '<path d="M4 9v6h4l5 4V5L8 9H4Z"/><path d="M16.5 8.5a5 5 0 0 1 0 7M19 6a8.5 8.5 0 0 1 0 12"/>',
    key: '<circle cx="8" cy="15" r="4"/><path d="m11 12 9-9M17 6l3 3M15 8l2 2"/>',
    logout: '<path d="M15 4h4v16h-4"/><path d="M10 8l-4 4 4 4M6 12h10"/>',
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
  // Page building blocks: sections with a centred column on one calm background. The old "alt" and "dark"
  // band colours are dropped here, so every page reads as one work surface with cards (UX review 2).
  const sec = (cls, html) => `<section class="${cls.replace(/\b(alt|dark)\b/g, "").replace(/\s+/g, " ").trim()}"><div class="wrap">${html}</div></section>`;
  const hero = ({ eyebrow = "", title, sub = "", actions = "", cls = "hero", extra = "", extra0 = "" }) =>
    sec(cls, `${eyebrow ? `<div class="eyebrow">${eyebrow}</div>` : ""}<h1>${title}</h1>${sub ? `<p class="sub">${sub}</p>` : ""}${extra0}${actions ? `<div class="actions">${actions}</div>` : ""}${extra}`);

  function view(name, def) { views[name] = def; }

  function parseRoute() {
    const h = location.hash.replace(/^#\/?/, "");
    const parts = h.split("/").filter(Boolean).map(decodeURIComponent);
    if (parts[0] === "setup") return { name: "setup/" + (parts[1] || "connections"), params: parts.slice(2) };
    return { name: parts[0] || "alarm", params: parts.slice(1) };
  }

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
      history.replaceState(null, "", shownHash || location.pathname);
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
    shownHash = location.hash;
    const { name, params } = parseRoute();
    const v = views[name] || views.alarm;
    document.querySelectorAll("#nav a").forEach((a) => a.classList.toggle("active", a.dataset.route === name || (a.dataset.route === "setup" && name.startsWith("setup/") && name !== "setup/generator")));
    // The pages that can change a PLC say so where the rest say "read-only"
    const ro = $("ro-badge");
    const except = name === "setup/generator" || name === "plc";
    if (ro) {
      ro.textContent = except ? "Read-only, except here" : "Read-only";
      ro.classList.toggle("except", except);
      ro.title = name === "setup/generator" ? "The block generator can load blocks into PLCSIM, or into a real PLC after two people confirm. Every other page only reads."
        : name === "plc" ? "Start and Stop here switch the PLCSIM CPU only, after you confirm. A real PLC is never started or stopped."
        : "PLC-Ki reads from the PLC only: it never writes values or changes the CPU state.";
    }
    $("foot-text").textContent = name === "setup/generator"
      ? "PLC-Ki reads from the PLC only. The one exception is this page: the block generator loads blocks into PLCSIM, or into a real PLC after two people confirm."
      : name === "plc" ? "PLC-Ki reads from the PLC only. The one exception is on this page: Start and Stop switch the PLCSIM CPU, never a real PLC."
      : "PLC-Ki reads from the PLC only: it never writes values or changes the CPU state.";
    const page = $("page");
    if (current && current.view.leave) current.view.leave();
    current = { name, view: v, params };
    lastHtml = "";
    page.innerHTML = "";
    if (v.mount) v.mount(page, params);
    else draw();
    page.scrollTop = 0;
    window.scrollTo(0, 0);
    setTitle();
  }

  /** Change the address shown (e.g. #/alarms/A02 for an opened alarm) without leaving the page, so it can be shared. */
  function setHash(h) {
    if (location.hash === h) return;
    history.replaceState(null, "", h);
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
      if (o.nodeType !== 1) { if (o.nodeValue !== n.nodeValue) o.nodeValue = n.nodeValue; return; }
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
    catch (e) { html = `<div class="notice red">${icon("warn")}<div>This page hit a problem: ${esc(e.message)}</div></div>`; console.error(e); }
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
    // Short in the bar; the address or test file is in the tooltip (a long file name wrapped into the menu)
    pill.title = (extra ? main + ": " + extra + ". " : "") + "What does this mean?";
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
    setTitle();
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

  /** Says loudly when the values on the page are not live: PLC-Ki not answering, PLC not read, or test values. */
  function renderBanner(stale) {
    const c = state.status && state.status.connection;
    let text = "", kind = "";
    if (state.lastOk && stale) { text = `PLC-Ki is not answering. Values frozen since ${clock(state.lastOk)}.`; kind = "frozen"; }
    else if (c && (c.state === "disconnected" || c.state === "connecting" || c.state === "waiting") && c.lastGoodRead) {
      text = `No connection to the PLC. Values are from the last good read at ${clock(c.lastGoodRead)}.`; kind = "frozen";
    }
    else if (c && c.state === "snapshot") { text = "Test values from a file, not the real machine."; kind = "test"; }
    document.documentElement.classList.toggle("test-values", kind === "test");
    const b = $("banner");
    if (!b) return;
    if (b.textContent !== text) b.innerHTML = text ? `<span class="dot"></span><span>${esc(text)}</span>` : "";
    b.className = "banner " + kind + (text ? "" : " hidden");
    document.documentElement.classList.toggle("frozen", kind === "frozen");
  }

  /** The browser tab shows the page and the number of active alarms, e.g. "(2) Plant · PLC-Ki". */
  /** Tab title and icon: a screen in another tab or on the wall shows the alarm, e.g. "(1) FAULT A02 · PLC-Ki". */
  function setTitle() {
    const al = (state.status && state.status.activeAlarms) || [];
    const page = current ? (HELP[current.name] || [""])[0] : "";
    const t = al.length ? `(${al.length}) ${al[0].severity} ${al[0].id} · PLC-Ki` : `${page ? page + " · " : ""}PLC-Ki`;
    if (document.title !== t) document.title = t;
    const level = !al.length ? "ok" : al.some((a) => a.severity === "FAULT") ? "fault" : "warn";
    if (setTitle.level !== level) {
      setTitle.level = level;
      const fill = { ok: "#2a64c9", fault: "#d93025", warn: "#c27c0e" }[level];
      const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32"><rect width="32" height="32" rx="8" fill="${fill}"/><path d="M9 9v14M9 16h6M15 9v14M19 9v14M19 16l6-7M19 16l6 7" stroke="#fff" stroke-width="2.6" stroke-linecap="round" fill="none"/></svg>`;
      let link = document.querySelector("link[rel=icon]");
      if (!link) { link = document.createElement("link"); link.rel = "icon"; document.head.append(link); }
      link.href = "data:image/svg+xml," + encodeURIComponent(svg);
    }
    alertNew(al);
  }

  // ---- This screen: alert on new alarms, keep the screen on. Saved per browser. ----
  const screenOpt = (() => { try { return JSON.parse(localStorage.getItem("plcki.screen") || "{}"); } catch { return {}; } })();
  const saveScreenOpt = () => { try { localStorage.setItem("plcki.screen", JSON.stringify(screenOpt)); } catch { /* private window */ } };
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
      <form class="stack" style="gap:12px" data-pw-form>
        ${own ? `<label class="field">Current password<input type="password" id="pw-cur" autocomplete="current-password" required></label>` : ""}
        <label class="field">New password <span class="hint">at least 8 characters</span><input type="password" id="pw-new" autocomplete="new-password" minlength="8" required ${own ? "" : "autofocus"}></label>
        <label class="field">New password again<input type="password" id="pw-rep" autocomplete="new-password" required></label>
        <p class="muted small" id="pw-err" role="alert" style="margin:0;color:var(--fault-ink)"></p>
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
    const s = sheet(`<span class="eyebrow">This screen</span><h2>Alarms and screen</h2>
      <p class="muted" style="margin:0">Only for this browser on this device. Nothing here changes the machine.</p>
      <div class="stack" style="gap:12px">
        ${row("sound", "Beep when a new alarm comes", "A short tone: three beeps for a fault, one for a warning. Leave this page open in a tab.", true, "")}
        ${row("notify", "Show a notice when a new alarm comes", "A message from the browser, also when PLC-Ki is in a background tab.", canNotify, notHere)}
        ${row("awake", "Keep this screen on", "For a screen on the line: the display does not go dark while this page is open.", canWake, notHere)}
        <div class="opt-row" style="cursor:default"><span style="flex:1"><b>Screen colours</b><br><span class="muted small">Dark suits a wall screen in a bright hall.</span></span>
          <div class="segmented" role="group" aria-label="Screen colours">${[["auto", "Like this PC"], ["light", "Light"], ["dark", "Dark"]].map(([k, l]) =>
            `<button type="button" data-theme-set="${k}" class="${themeChoice() === k ? "on" : ""}" aria-pressed="${themeChoice() === k}">${l}</button>`).join("")}</div></div>
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
    if (state.user && state.user.role === "admin") {
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
  function toast(text, kind) {
    const t = $("toast");
    t.textContent = text;
    t.classList.toggle("err", kind === "error");
    t.classList.add("on");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => t.classList.remove("on"), kind === "error" ? 7000 : 3200);
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

  /** What the PLC connection state means, for the pill sheet and empty pages. Operators cannot open Setup, so they read who to ask. */
  function connInfo() {
    const i = connInfoAll();
    if (state.user && state.user.role === "admin") return i;
    if (i.todo && /Setup|Connections|Link/.test(i.todo)) i.todo = i.op || "Ask an administrator of PLC-Ki to check the connection to the PLC.";
    return i;
  }
  function connInfoAll() {
    const c = state.status && state.status.connection;
    const stale = Date.now() - state.lastOk > 5000;
    if (!state.status || stale) return { level: "bad", title: "PLC-Ki is not answering.", text: "This page cannot reach PLC-Ki.", todo: "Check that the PLC-Ki window on the PC is still open. If it was closed, double-click Start-PLC-Ki in the PLC-Ki folder, then reload this page." };
    if (c.state === "connected") return { level: "ok", title: `Reading the PLC at ${c.address}.`, text: "Values are live, read once a second. PLC-Ki only reads: it never writes values or changes the CPU state.", todo: "" };
    if (c.state === "snapshot") return { level: "info", title: "Showing test values, not a PLC.", text: "PLC-Ki reads values from a test file.", todo: "To read a real PLC, choose Live PLC under Setup, Connections (More PLC options).", op: "Ask an administrator to switch PLC-Ki to the real PLC." };
    if (c.state === "notset") return { level: "warn", title: "No PLC is set yet.", text: "PLC-Ki does not know which PLC to read, so there are no live values.", todo: "Enter the PLC's address under Setup, Connections.", op: "Ask an administrator to set up the connection to the PLC." };
    if (c.state === "connecting") return { level: "warn", title: `Connecting to ${c.address}…`, text: "This takes a few seconds.", todo: "" };
    if (c.state === "waiting") return { level: "warn", title: "Waiting for PLC-Ki Link.", text: "The PLC is read by PLC-Ki Link on your laptop, and it is not connected.", todo: "On the laptop, start PLC-Ki Link (Setup, Connections shows the command).", op: "Ask an administrator to start PLC-Ki Link on the laptop that is connected to the PLC." };
    const p = plain(c.error, { plc: c.address });
    return { level: "bad", title: `No connection to the PLC at ${c.address}.`, text: p.text === c.error ? "" : p.text, todo: p.todo, op: p.op, detail: c.error };
  }

  function connSheet() {
    const i = connInfo();
    const admin = state.user && state.user.role === "admin";
    sheet(`<span class="pill ${i.level}" style="align-self:flex-start;justify-self:start"><span class="dot"></span>${{ ok: "Connected", bad: "Not connected", warn: "Waiting", info: "Test values" }[i.level]}</span>
      <h2>${esc(i.title)}</h2>${i.text ? `<p class="muted" style="margin:0">${esc(i.text)}</p>` : ""}
      ${i.todo ? `<div class="notice ${i.level === "bad" ? "red" : "gold"}">${icon("info")}<div><b>What to do</b><br>${esc(i.todo)}</div></div>` : ""}
      ${i.detail ? `<details class="more"><summary>Technical detail</summary><p class="faint small" style="margin:8px 0 0">${esc(i.detail)}</p></details>` : ""}
      <div class="row end">${admin && i.level !== "ok" ? `<a class="btn b" href="#/setup/connections" data-close>Open Connections</a>` : ""}<button class="btn" data-close>OK</button></div>`);
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
      "With several alarms, the first chip is the first-out: the alarm that came first, as PLC-Ki saw them come. Fix that one first; the others often follow from it.",
      "Fix it: work down the list and tap each step when done. The ticks are only on this screen.",
      "Where in the machine: the device behind the alarm and the chain Requested › Interlocks › Output › Feedback. The first red link is why it is not working.",
      "Where in the software: from the main cycle (OB1) down to the bit that sets the alarm. Show where it is set opens that network as rungs with live values.",
      "Ask about this alarm: Claude looks at the live values, the last minutes and the program, and says why it is on and what to check."]],
    plant: ["Plant", "What the machine is doing now. The top shows the step, what it waits for and the key values; the tabs below hold the rest.", [
      "Overview: Needs attention lists active alarms and blocked or faulted devices first; tap one to go to it. Below it the sequence: the blue step is where it is now, red means halted, ✓ is met, ✗ is missing.",
      "The machine at a glance: every device as a symbol. Green runs, grey is off, a dashed amber edge means the program asks for it but an interlock blocks it, red is a fault. Tap one to open it on Devices.",
      "Devices: the list on the left (needs attention first), the picked device on the right with the chain Requested › Interlocks › Output › Feedback. The first red link is why it is not running.",
      "Values over time: the measured values of the last minutes, with the step band and alarm marks under them. Move along a chart to read every value at that moment.",
      "Data exchange: signals to and from MES, a filler and so on, with their live values."]],
    sequence: ["Step chart", "One sequence as a chain of steps, read like the GRAPH editor, with how long each step took.", [
      "Step timeline: which step ran when in the last minutes, and the last and average time of each step. A step that takes longer than usual is where the machine waits.",
      "Step chart: each box is a step, the bar under it is the transition to the next one with its conditions. ↷ marks a jump to another step; \"from 12\" above a step says which steps jump to it.",
      "The blue step is where it is now. Its conditions are live: ✓ is met, ✗ is missing. Tap any step for what it does."]],
    alarms: ["Alarms", "Every alarm the program can raise, active or not, with its meaning, where it is set, likely causes and the fix.", [
      "Search by number, text or device. Active alarms are at the top.",
      "Click an alarm to open its causes and fix. Red tags are faults (they stop the machine), gold tags are warnings."]],
    program: ["Program", "Every block of the PLC program with live values, as rungs or as text.", [
      "Pick a block on the left. At the top, \"Why outputs are off right now\" names each output that is off and the signal that stops it.",
      "Rungs: each network drawn as a rung. Green passes power, grey does not, a dashed ? has no live value. Read from left to right: the amber contact is the first one that stops the output.",
      "Text: the code as written in TIA (LAD and GRAPH as readable text with the same meaning), with the live value after each signal. Click a signal in quotes to see where else it is used.",
      "LAD, FBD and GRAPH blocks open as rungs, SCL blocks as text. Switch with Rungs / Text. Coming from an alarm, the network that sets it is marked.",
      "Values update every two seconds. PLC-Ki only reads: nothing here changes the PLC."]],
    tags: ["Tags", "The PLC tag tables with addresses, and every data block's values, live.", [
      "Tag tables: every tag with its address (%I, %Q, %M), type and comment. Search by name, address or comment.",
      "Data blocks: pick one on the left to see all its values. Optimized blocks have no addresses; values are read by name.",
      "Up to 300 rows are read live at a time, about once a second. PLC-Ki only reads: nothing here changes the PLC."]],
    plc: ["PLC monitor", "The CPU as it looks on the machine: its mode, its LEDs and a display with what PLC-Ki reads.", [
      "Operating mode: RUN, STOP or STARTUP as the CPU reports it, with the position of the mode switch where the CPU tells it.",
      "The picture is drawn from the CPU's order number. Use the keys under the display, or the buttons below it, to page through Overview, Diagnostics, Settings and Modules.",
      "Start and Stop work only when PLC-Ki reads the PLCSIM CPU of the TIA helper, and only for administrators. You confirm first. A real PLC is never started or stopped from PLC-Ki.",
      "ERROR and MAINT come from PLCSIM where it tells them; on a real CPU they are not read and stay dark here."]],
    hardware: ["Hardware", "Every rack and module of the TIA project, with each input and output, its tag and its live value.", [
      "Pick a device at the top (PLC, I/O stations, drives, robots), then tap a module in the rack for its channels.",
      "Each module is drawn from its order number: width, LEDs and channel count as on the real device. Devices from other makers show as a plain box.",
      "Channels are matched to the PLC tags by address (%I10.3, %IW256), so you see the tag name, its comment and the live value.",
      "If only the CPU shows, the program export has no hardware yet: Read the hardware from TIA (administrators) takes a minute or two and changes nothing else."]],
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
    "setup/generator": ["Block generator", "Say what you need; Ki, PLC-Ki's block writer, writes the blocks, TIA imports and compiles them, PLCSIM runs them.", [
      "New blocks: say what you need, pick the TIA project and press Generate. The package name is made from your words if you leave it empty.",
      "Made before: pick a package on the left to see its versions, what Ki changed, Ki's notes and the files. \"Change it\" makes the next version (v002, v003 …); older versions stay.",
      "When a job ends, \"What Ki did\" lists the blocks and tags it made, changed or removed against the version before.",
      "Ki runs on Claude, with the API key from Settings.",
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
    const h = sheet(`<span class="eyebrow">Help</span><h2>${esc(title)}</h2><p class="muted" style="margin:0">${esc(intro)}</p>
      <ul class="plainlist">${tips.map((t) => `<li>${esc(t)}</li>`).join("")}</ul>
      <p class="faint small" style="margin:0">The light at the top right shows the PLC connection; click it for what it means. Ask answers questions about the machine on every page.</p>
      <p class="faint small" style="margin:0"><button type="button" class="linkbtn" data-screen>Alarm sound and screen</button>: beep or show a notice when an alarm comes, keep a line screen on.</p>
      <p class="faint small keys" style="margin:0"><kbd>?</kbd> this help · <kbd>/</kbd> ask a question · <kbd>Esc</kbd> close</p>
      <div class="row end">${admin && name !== "setup/start" ? `<a class="btn b" href="#/setup/start" data-close>Get started</a>` : ""}<button class="btn" data-close>OK</button></div>`);
    h.el.querySelector("[data-screen]").onclick = () => { h.close(); screenSheet(); };
  }

  async function start() {
    fillIcons();
    try {
      const st = await api("GET", "/api/auth/state");
      state.user = st.user;
    } catch { /* public view */ }
    const admin = state.user && state.user.role === "admin";
    document.querySelectorAll(".admin-only").forEach((el) => el.classList.toggle("hidden", !admin));
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
    $("conn").onkeydown = (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); connSheet(); } };
    window.addEventListener("hashchange", onHash);
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
    if (admin && !location.hash && typeof PKStart !== "undefined" && (await PKStart.firstRun())) history.replaceState(null, "", "#/setup/start");
    route();
    pollStatus();
    pollValues();
    pollJobs();
  }

  return { AI, dirty: setDirty, get isDirty() { return !!dirty; }, plain, connInfo, connSheet, helpSheet, screenSheet, passwordSheet, setHash, cmd, sec, hero, on, $, esc, icon, fillIcons, kindIcon, api, state, view, go, draw, route, toast, sheet, confirmSheet, cond, md, ago, clock, date, when,
    loadProgram, blockOf, valueBySymbol, norm, start, get current() { return current; } };
})();
