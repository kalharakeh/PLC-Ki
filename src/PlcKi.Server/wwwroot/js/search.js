// Search (Ctrl+K, or the search button in the top bar): one box for the whole machine. What PLC-Ki already has in
// the browser shows at once (alarms, devices, machine values, blocks); the server's answers follow as they come
// (data block values, where a word is in the program, documents). Results are grouped; arrow keys move, Enter opens.
"use strict";

(() => {
  const { esc, state, icon } = PK;
  const PER_GROUP = 6;
  const GROUPS = [
    ["alarms", PK.T("Alarms")], ["devices", PK.T("Devices")], ["model", PK.T("Machine values")], ["blocks", PK.T("Blocks")],
    ["values", PK.T("Data block values")], ["code", PK.T("In the program")], ["docs", PK.T("Documents")],
  ];
  let open = null;            // the sheet while it is shown
  let q = "", seq = 0, timer = null;
  let server = {};            // group -> results from the server for the current words
  let waiting = new Set();    // server groups still asked
  let active = 0;

  const has = (words, ...xs) => { const t = xs.map((x) => String(x || "").toLowerCase()).join(" "); return words.every((w) => t.includes(w)); };
  const dbOf = (sym) => (/^"([^"]+)"[.[]/.exec(String(sym || "")) || [])[1] || null;
  const tagsHref = (sym) => { const d = dbOf(sym); return d ? "#/tags/db/" + encodeURIComponent(d) : "#/tags"; };

  /** What the browser already has: the machine model, the live status and the program's block list. */
  function local(words) {
    const m = state.model, s = state.status;
    const out = { alarms: [], devices: [], model: [], blocks: [] };
    if (!words.length) return out;
    if (m) {
      const on = new Set(((s && s.activeAlarms) || []).map((a) => a.id));
      out.alarms = m.alarms.filter((a) => has(words, a.id, a.text, a.location))
        .sort((a, b) => on.has(b.id) - on.has(a.id))
        .map((a) => ({ icon: "bell", title: `${a.id} · ${a.text}`, sub: on.has(a.id) ? PK.t("Active now") : a.location || "", href: "#/alarms/" + encodeURIComponent(a.id), hot: on.has(a.id) }));
      out.devices = m.equipment.filter((e) => has(words, e.id, e.name, e.kind))
        .map((e) => { const live = s && s.equipment.find((x) => x.id === e.id);
          return { icon: PK.kindIcon(e.kind), title: e.name, sub: [e.id, live && live.state].filter(Boolean).join(" · "), href: "#/plant/devices/" + encodeURIComponent(e.id) }; });
      out.model = m.tags.filter((t) => has(words, t.id, t.label, t.symbol))
        .map((t) => ({ icon: "table", title: t.label || t.id, sub: t.symbol, href: tagsHref(t.symbol) }));
    }
    const blocks = (state.program && state.program.blocks) || [];
    out.blocks = blocks.filter((b) => b.name && !/DB$/.test(b.kind) && has(words, b.name, b.title, b.kind))
      .map((b) => ({ icon: "rung", title: b.name, sub: [b.kind, b.title].filter(Boolean).join(" · "), href: "#/program/" + encodeURIComponent(b.name) }));
    return out;
  }

  /** The server's answers, each as soon as it arrives. Only the latest words count. */
  function ask(text) {
    const my = ++seq;
    server = {};
    waiting = new Set(text.length >= 2 ? ["values", "code", "docs"] : []);
    if (!waiting.size) { draw(); return; }
    const done = (group, list) => { if (my !== seq) return; server[group] = list; waiting.delete(group); draw(); };
    const fail = (group) => () => done(group, []);
    PK.api("GET", "/api/program/tags/find?q=" + encodeURIComponent(text)).then((r) => done("values", (r.values || []).map((v) => ({
      icon: "table", title: v.symbol, mono: true,
      sub: [v.db ? PK.t("in {db}", { db: v.db }) : v.table ? PK.t("tag table {table}", { table: v.table }) : "", v.address, v.comment].filter(Boolean).join(" · "),
      href: v.db ? "#/tags/db/" + encodeURIComponent(v.db) : "#/tags",
    })))).catch(fail("values"));
    PK.api("GET", "/api/program/search?q=" + encodeURIComponent(text)).then((r) => done("code", (r.results || []).map((x) => ({
      icon: "code", title: x.block, sub: PK.t("{n} line|{n} lines", { n: x.count }) + (x.lines && x.lines[0] ? ": " + x.lines[0].text : ""),
      href: "#/program/" + encodeURIComponent(x.block), mono: false,
    })))).catch(fail("code"));
    PK.api("GET", "/api/documents/search?q=" + encodeURIComponent(text)).then((r) => done("docs", [
      ...(r.faults || []).map((f) => ({ icon: "warn", title: `${f.code} · ${f.title}`, sub: f.device || PK.t("Fault code"),
        href: f.documentId ? `/api/documents/${encodeURIComponent(f.documentId)}/file${f.page ? "#page=" + f.page : ""}` : "#/documents", external: !!f.documentId })),
      ...(r.pages || []).map((p) => ({ icon: "book", title: PK.t("{file}, page {page}", { file: p.file, page: p.page }), sub: p.text || "",
        href: `/api/documents/${encodeURIComponent(p.id)}/file#page=${p.page}`, external: true })),
    ])).catch(fail("docs"));
  }

  function results() {
    const words = q.toLowerCase().split(/\s+/).filter(Boolean);
    const all = { ...local(words), ...server };
    return GROUPS.map(([k, label]) => ({ k, label, list: (all[k] || []).slice(0, PER_GROUP), more: Math.max(0, (all[k] || []).length - PER_GROUP) }))
      .filter((g) => g.list.length);
  }

  function draw() {
    if (!open) return;
    const box = open.el.querySelector("[data-results]");
    const groups = results();
    const flat = groups.flatMap((g) => g.list);
    active = Math.min(active, Math.max(0, flat.length - 1));
    let i = 0;
    const row = (r) => {
      const n = i++;
      return `<li><a class="sr-row${n === active ? " on" : ""}" id="sr-${n}" role="option" aria-selected="${n === active}" data-n="${n}" href="${esc(r.href)}"${r.external ? ' target="_blank" rel="noopener"' : ""}>
        <span class="mrow-i" data-icon="${esc(r.icon)}"></span>
        <span class="mrow-t"><span class="sr-title${r.mono ? " mono" : ""}">${esc(r.title)}</span>${r.sub ? `<small>${esc(r.sub)}</small>` : ""}</span>
        ${r.hot ? `<span class="pill bad"><span class="dot"></span>${PK.t("on")}</span>` : ""}</a></li>`;
    };
    const busy = waiting.size ? `<p class="faint small sr-wait">${esc(PK.t("Looking in {places}…", { places: [...waiting].map((k) => ({ values: PK.t("the data blocks"), code: PK.t("the program"), docs: PK.t("the documents") })[k]).join(", ") }))}</p>` : "";
    box.innerHTML = !q.trim()
      ? `<p class="muted small sr-hint">${PK.t("Find an alarm (A02), a device (XV101), a value, a block, or words from a manual. Enter opens the first result.")}</p>`
      : groups.length
        ? groups.map((g) => `<h3 class="more-group">${esc(PK.t(g.label))}${g.more ? ` <span class="faint">· ${PK.t("{n} more", { n: g.more })}</span>` : ""}</h3><ul class="mlist" role="listbox" aria-label="${esc(PK.t(g.label))}">${g.list.map(row).join("")}</ul>`).join("") + busy
        : busy || `<p class="muted sr-hint">${PK.t("Nothing found for “{q}”. Try fewer letters, an alarm number or a tag name.", { q: esc(q) })}</p>`;
    PK.fillIcons(box);
    const input = open.el.querySelector("#sr-q");
    if (input) input.setAttribute("aria-activedescendant", flat.length ? "sr-" + active : "");
    open.flat = flat;
  }

  /** Marks the chosen row without drawing the list again (a redraw under the mouse could swallow a click). */
  function mark() {
    open.el.querySelectorAll("a.sr-row").forEach((a) => { const on = +a.dataset.n === active; a.classList.toggle("on", on); a.setAttribute("aria-selected", on); });
    const input = open.el.querySelector("#sr-q");
    if (input) input.setAttribute("aria-activedescendant", (open.flat || []).length ? "sr-" + active : "");
  }
  function move(d) {
    const n = (open.flat || []).length;
    if (!n) return;
    active = (active + d + n) % n;
    mark();
    const el = open.el.querySelector("#sr-" + active);
    if (el) el.scrollIntoView({ block: "nearest" });
  }

  function go() {
    const r = (open.flat || [])[active];
    if (!r) return;
    const s = open;
    if (r.external) window.open(r.href, "_blank", "noopener");
    else PK.go(r.href);
    s.close();
  }

  function show() {
    if (open) return;
    // The block list is read once; it is small and makes block names searchable without asking the server
    PK.loadProgram().then(() => { if (open) draw(); });
    active = 0;
    open = PK.sheet(`<div class="more-head"><h2>${PK.t("Search")}</h2><button class="btn plain small" type="button" data-close>${PK.t("Done")}</button></div>
      <div class="sr-box">${icon("search")}<input type="search" id="sr-q" placeholder="${esc(PK.t("Alarm, device, value, block or manual"))}" aria-label="${esc(PK.t("Search the machine"))}" role="combobox" aria-expanded="true" aria-controls="sr-results" autocomplete="off" value="${esc(q)}"></div>
      <div id="sr-results" data-results></div>`, { onClose: () => { open = null; }, back: true });
    open.el.classList.add("more-sheet", "search-sheet");
    const input = open.el.querySelector("#sr-q");
    input.oninput = () => {
      q = input.value; active = 0; draw();
      clearTimeout(timer);
      timer = setTimeout(() => ask(q.trim()), 200);
    };
    input.onkeydown = (e) => {
      if (e.key === "ArrowDown") { e.preventDefault(); move(1); }
      else if (e.key === "ArrowUp") { e.preventDefault(); move(-1); }
      else if (e.key === "Enter") { e.preventDefault(); go(); }
    };
    open.el.addEventListener("click", (e) => { if (e.target.closest("a.sr-row")) setTimeout(() => open && open.close(), 0); });
    open.el.addEventListener("mousemove", (e) => { const a = e.target.closest("a.sr-row"); if (a && +a.dataset.n !== active) { active = +a.dataset.n; mark(); } });
    if (q.trim()) ask(q.trim());
    draw();
    input.focus();
    input.select();
  }

  // Ctrl+K (Cmd+K on a Mac) opens it from anywhere, also while typing in a field
  document.addEventListener("keydown", (e) => {
    if ((e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && e.key.toLowerCase() === "k") {
      if (document.querySelector(".sheet-wrap") && !open) return;   // another dialog is open
      e.preventDefault();
      if (open) open.el.querySelector("#sr-q").focus(); else show();
    }
  });
  // Going to another page (Back, a link, a result) closes it
  window.addEventListener("hashchange", () => { if (open) open.close(); });
  const btn = document.getElementById("search-open");
  if (btn) btn.onclick = show;

  window.PKSearch = { open: show };
})();
