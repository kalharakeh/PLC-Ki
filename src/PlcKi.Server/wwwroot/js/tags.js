// Tags page: the PLC tag tables (name, address, type, comment) and every data block's values, all with live
// values. The page tells the server which symbols it shows (POST /api/watch); the polling loop reads them next to
// the model tags while the page is open. Administrators can change a value (write.js); everyone else only reads.
"use strict";

(() => {
  const { esc, icon, state } = PK;
  const MAX_ROWS = 300;     // rows drawn (and read live) at once; the search narrows the rest
  let data = null, dataError = null, loading = false;
  let tab = "tables";       // "tables" | "dbs"
  let tabChosen = false;    // false until the first draw with data picks the starting tab
  let query = "";
  let db = null, dbData = null, dbError = null;   // the data block open on the right
  let shown = [];
  let valQuery = "";        // Data blocks: find a value inside the open block

  // ---- live values for symbols outside the machine model, shared with the Program page ----
  // Each page registers what it shows; one loop asks the server for the current page's symbols about every 1.5 s.
  const W = { values: {}, at: null, connection: null, providers: {} };
  setInterval(async () => {
    const name = PK.current && PK.current.name;
    const syms = (W.providers[name] && W.providers[name]()) || [];
    if (!syms.length || document.hidden) return;
    try {
      const r = await PK.api("POST", "/api/watch", { symbols: syms.map((t) => ({ symbol: t.symbol, type: t.type || null })) });
      Object.assign(W.values, r.values || {}); W.at = r.time; W.connection = r.connection;
      PK.draw();
    } catch { /* next time */ }
  }, 1500);

  window.addEventListener("pk:program-changed", () => { data = null; dataError = null; tabChosen = false; db = null; dbData = null; dbError = null; });

  async function load() {
    if (data || loading) return;
    loading = true;
    try {
      const r = await PK.api("GET", "/api/program/tags");
      if (r.available) data = r; else dataError = r.error || "No program loaded.";
    } catch (e) { dataError = e.message; }
    loading = false;
    PK.draw();
  }

  async function openDb(name) {
    if (db === name && (dbData || dbError)) return;
    db = name; dbData = null; dbError = null; valQuery = "";
    PK.draw();
    try {
      const r = await PK.api("GET", "/api/program/tags?db=" + encodeURIComponent(name));
      if (r.available) dbData = r; else dbError = r.error;
    } catch (e) { dbError = e.message; }
    PK.draw();
  }

  const match = (q, ...xs) => !q || xs.some((x) => String(x || "").toLowerCase().includes(q));

  /** The data blocks named in a text: "FB_Valve, instance \"Station\".XV101" gives ["Station"]. */
  const dbsIn = (text) => [...String(text || "").matchAll(/"([^"]+)"/g)].map((m) => m[1]);
  /**
   * Where Tags starts: the data block of the first active alarm (where the model says it comes from, then its own
   * tag), else the device picked on Plant, else nothing (the first block). { name, why } or null.
   */
  function problemDb() {
    const m = state.model, s = state.status;
    if (!m || !data) return null;
    const known = new Set(data.dbs.map((d) => d.name));
    const tagSym = (id) => { const t = id && m.tags.find((x) => x.id === id); return t ? t.symbol : ""; };
    for (const a of (s && s.activeAlarms) || []) {
      const def = m.alarms.find((x) => x.id === a.id) || a;
      const hit = [...dbsIn(def.location), ...dbsIn(tagSym(def.tag))].find((n) => known.has(n));
      if (hit) return { name: hit, why: `alarm ${a.id}` };
    }
    const devId = window.PKLive && PKLive.selectedDevice();
    const dev = devId && m.equipment.find((e) => e.id === devId);
    if (dev) {
      const hit = [...dbsIn(dev.location), ...dbsIn(tagSym(dev.command)), ...dbsIn(tagSym(dev.request))].find((n) => known.has(n));
      if (hit) return { name: hit, why: dev.name };
    }
    return null;
  }
  let startWhy = "";      // "alarm A02": why this data block was opened first, said above its values

  /** Values that are in the machine model (normalized symbol -> its label), marked in the data block table. */
  function modelSymbols() {
    const map = new Map();
    for (const t of (state.model && state.model.tags) || []) map.set(PK.norm(t.symbol), t.label || t.id);
    return map;
  }

  /** The live value plus, for administrators, the button that changes it. */
  const now = (t) => `<span class="row" style="gap:4px;flex-wrap:nowrap">${live(t)}${window.PKWrite ? PKWrite.button(t) : ""}</span>`;
  /** The value shown for a symbol, as the change sheet's "now". */
  const current = (sym) => { const v = W.values[sym]; if (!v || v.error) return null; return v.value === "True" ? true : v.value === "False" ? false : v.value; };

  function live(t) {
    const v = W.values[t.symbol];
    if (!v) return `<span class="faint">…</span>`;
    if (v.error) return `<span class="nowrap faint small" title="${esc(v.error)}">not read</span>`;
    if (v.value === "True" || v.value === "False") v.value = v.value === "True";
    if (v.value === true || v.value === false) return `<span class="m-0 chip ${v.value ? "t" : "f"}">${v.value ? "TRUE" : "FALSE"}</span>`;
    if (v.value == null) return `<span class="faint">…</span>`;
    const n = typeof v.value === "number" ? (Number.isInteger(v.value) ? String(v.value) : v.value.toFixed(3).replace(/0+$/, "").replace(/\.$/, "")) : String(v.value);
    return `<span class="m-0 chip">${esc(t.type === "Time" && typeof v.value === "number" ? n + " ms" : n)}</span>`;
  }

  function tablesHtml(q) {
    let rows = 0, more = 0;
    shown = [];
    const html = data.tables.map((tb) => {
      const tags = tb.tags.filter((t) => match(q, t.name, t.address, t.comment, t.datatype, tb.name));
      if (!tags.length) return "";
      const take = tags.slice(0, Math.max(0, MAX_ROWS - rows));
      more += tags.length - take.length; rows += take.length;
      shown.push(...take);
      if (!take.length) return "";
      return `<div class="card"><h3 class="mt-0 mb-2">${esc(tb.name)} <span class="faint small">${tags.length} tag${tags.length > 1 ? "s" : ""}</span></h3>
        <div class="tbl-wrap"><table class="tbl tags-tbl"><tr><th>Name</th><th>Address</th><th>Type</th><th>Now</th><th>Comment</th></tr>
        ${take.map((t) => `<tr><td class="mono">${esc(t.name)}</td><td class="mono">${esc(t.address || "")}</td><td class="mono muted">${esc(t.datatype)}</td><td>${now(t)}</td><td class="muted">${esc(t.comment || "")}</td></tr>`).join("")}
        </table></div></div>`;
    }).join("");
    const none = query.trim() ? `No tag matches “${esc(query)}”.` : "This program export has no PLC tag tables.";
    return (html || `<div class="card"><p class="m-0 muted">${none}</p></div>`)
      + (more ? `<p class="faint small">${more} more tag${more > 1 ? "s" : ""} not shown. Search to narrow the list.</p>` : "");
  }

  function dbsHtml(q) {
    const list = data.dbs.filter((d) => match(q, d.name, d.title, d.instanceOf, d.number));
    const nav = list.map((d) => `<a href="#/tags/db/${encodeURIComponent(d.name)}" class="${d.name === db ? "on" : ""}">${esc(d.name)}<span class="tag">${d.number != null ? "DB" + d.number : ""}</span></a>`).join("")
      || `<p class="faint small" style="padding:8px 10px">${q ? "No data block matches." : "This program export has no data blocks."}</p>`;
    let right;
    shown = [];
    if (!db) right = `<div class="card">${data.dbs.length ? PK.ui.loading("table") : PK.ui.empty({ icon: "file", title: "This program has no data blocks.", cls: "small" })}</div>`;
    else if (dbError) right = PK.ui.error(dbError);
    else if (!dbData) right = `<div class="card">${PK.ui.loading("table")}</div>`;
    else {
      const vq = valQuery.trim().toLowerCase();
      const vals = dbData.values.filter((t) => match(vq, t.symbol, t.comment, t.datatype));
      const take = vals.slice(0, MAX_ROWS);
      shown = take;
      const short = (s) => s.startsWith(`"${dbData.db}".`) ? s.slice(dbData.db.length + 3) : s;
      const inModel = modelSymbols();
      const nModel = dbData.values.filter((t) => inModel.has(PK.norm(t.symbol))).length;
      const mark = (t) => { const l = inModel.get(PK.norm(t.symbol)); return l ? ` <span class="tag blue in-model" title="In the machine model: ${esc(l)}">model</span>` : ""; };
      right = `<div class="card">
        <div class="row"><h2 style="margin:0;font-size:24px">${esc(dbData.db)}</h2><span class="tag">${dbData.number != null ? "DB " + dbData.number : ""}</span>
          ${dbData.instanceOf ? `<span class="faint small">instance of <a href="#/program/${encodeURIComponent(dbData.instanceOf)}">${esc(dbData.instanceOf)}</a></span>` : ""}
          <span class="spacer"></span><a class="btn plain small" href="#/program/${encodeURIComponent(dbData.db)}">Show in the program ›</a></div>
        ${startWhy && startWhy.db === dbData.db ? `<p class="mt-2 mb-0 small">${icon("bell")} Opened here because of ${esc(startWhy.why)}.</p>` : ""}
        ${dbData.title || dbData.comment ? `<p class="mt-2 mb-0 muted">${esc([dbData.title, dbData.comment].filter(Boolean).join(" · "))}</p>` : ""}
        <div class="mt-3 gap-3 row"><input type="search" id="val-q" placeholder="Find a value" aria-label="Find a value" value="${esc(valQuery)}" style="max-width:300px">
          <span class="faint small">Optimized data blocks have no fixed addresses: values are read by name.${nModel ? ` <span class="tag blue">model</span> marks the ${nModel} value${nModel === 1 ? "" : "s"} the machine model explains.` : ""}</span></div>
        <div class="tbl-wrap"><table class="mt-3 tbl tags-tbl"><tr><th>Value</th><th>Type</th><th>Now</th><th>Comment</th></tr>
        ${take.map((t) => `<tr><td class="mono">${esc(short(t.symbol))}${mark(t)}</td><td class="mono muted">${esc(t.datatype)}</td><td>${now(t)}</td><td class="muted">${esc(t.comment || "")}</td></tr>`).join("")}
        </table></div>
        ${vals.length > take.length ? `<p class="faint small">Showing the first ${take.length} of ${vals.length} values.</p>` : ""}
        ${!vals.length ? `<p class="muted">No readable values in this data block.</p>` : ""}</div>`;
    }
    return `<div class="split"><nav class="blocklist">${nav}</nav><div class="stack">${right}</div></div>`;
  }

  W.providers.tags = () => shown;

  /**
   * For the Program page: the global signals a block uses ("Tag", "DB".member), with the tag table's
   * address and type where it is a PLC tag, and their live values. Returns the card's HTML.
   */
  function blockTagsHtml(text) {
    load();
    const code = String(text || "").split(/\r?\n/).map((l) => l.replace(/\/\/.*$/, "")).join("\n").replace(/\(\*[\s\S]*?\*\)/g, "");
    const syms = [...new Set((code.match(/"[^"]+"(?:\.(?:[A-Za-z_]\w*|"[^"]+")|\[\d+\])*/g) || []))];
    const dbNames = new Set(((data && data.dbs) || []).map((d) => `"${d.name}"`));
    const byName = new Map();
    for (const tb of (data && data.tables) || []) for (const t of tb.tags) byName.set(t.symbol, { ...t, table: tb.name });
    const blocks = new Set(((state.program && state.program.blocks) || []).filter((b) => !/DB$/.test(b.kind)).map((b) => `"${b.name}"`));
    const rows = syms.filter((s) => !blocks.has(s) && !dbNames.has(s)).slice(0, 150).map((s) => byName.get(s) || { symbol: s, type: null });
    blockShown = rows;
    if (!rows.length) return "";
    return `<div class="card"><details class="more" style="border:0;margin:0;padding:0" open><summary>Tags in this block (${rows.length})</summary>
      <table class="mt-2 tbl tags-tbl"><tr><th>Tag</th><th>Address</th><th>Type</th><th>Now</th><th>Comment</th></tr>
      ${rows.map((t) => `<tr><td class="mono">${dbLink(t.symbol)}</td><td class="mono">${esc(t.address || "")}</td><td class="mono muted">${esc(t.datatype || "")}</td><td>${now(t)}</td><td class="muted">${esc(t.comment || "")}</td></tr>`).join("")}
      </table></details></div>`;
  }
  let blockShown = [];
  /** "DB".member links to that data block on the Tags page. */
  function dbLink(sym) {
    const m = /^"([^"]+)"[.[]/.exec(sym);
    const isDb = m && data && data.dbs.some((d) => d.name === m[1]);
    return isDb ? `<a href="#/tags/db/${encodeURIComponent(m[1])}">${esc(sym)}</a>` : esc(sym);
  }
  // The Program page also asks for the open block's own members ("Station".XV101.Out), up to the server's 300
  let programExtra = () => [];
  W.providers.program = () => {
    const extra = programExtra().slice(0, 300), have = new Set(extra.map((t) => t.symbol));
    return extra.concat(blockShown.filter((t) => !have.has(t.symbol)).slice(0, 300 - extra.length));
  };
  /** Other pages (Plant > Settings) show live values of symbols outside the model the same way. */
  const watch = (page, provider) => { W.providers[page] = provider; };
  /** The last answer for a symbol, exactly as it was asked: { value, error } or undefined. */
  const value = (sym) => W.values[sym];
  window.PKTags = { blockTagsHtml, watch, live, value, programSymbols: (fn) => { programExtra = fn; },
    wire: (root) => window.PKWrite && PKWrite.wire(root, current) };

  PK.view("tags", {
    live: false,
    mount(el, params) {
      load();
      startWhy = "";
      if (params[0] === "db" && params[1]) { tab = "dbs"; tabChosen = true; openDb(params[1]); }
      else tabChosen = false;   // #/tags: start where the problem is (render picks it once the tags are read)
      // Draw now: when the tags were already read (an earlier visit, or the Program page), load() has nothing to wait for
      PK.draw();
    },
    leave() { shown = []; },
    render(params) {
      if (params[0] === "db" && params[1] && params[1] !== db) { tab = "dbs"; query = ""; openDb(params[1]); }
      const head = PK.hero({ cls: "hero compact", title: "Tags",
        sub: "Every tag of the PLC tag tables with its address, and every value of the data blocks, read live from the PLC."
          + (PK.can("real") ? " Change a value with the pen: on PLCSIM (safety values after a warning), on a real PLC only memory and data values, when allowed and confirmed."
            : PK.can("plcsim") ? " Change a value with the pen on PLCSIM (safety values after a warning)." : "") });
      if (dataError) return head + PK.sec("", PK.ui.error(dataError, { level: "warn" }));
      if (!data) return head + PK.sec("", PK.ui.loading("table", { rows: 8 }));
      const q = query.trim().toLowerCase();
      const nTags = data.tables.reduce((n, t) => n + t.tags.length, 0);
      // First draw with data: open the data block of the current problem; else the first tab that has something in it
      if (!tabChosen) {
        tabChosen = true;
        const p = problemDb();
        if (p) { tab = "dbs"; startWhy = { db: p.name, why: p.why }; PK.setHash("#/tags/db/" + encodeURIComponent(p.name)); openDb(p.name); }
        else if (!nTags && data.dbs.length) tab = "dbs";
      }
      // The data blocks tab always shows one: the first, when none is picked
      if (tab === "dbs" && !db && data.dbs.length) openDb(data.dbs[0].name);
      const status = W.connection && W.connection !== "connected"
        ? `<div class="mb-4 notice gold">${icon("warn")}<div>${W.connection === "snapshot" ? "PLC-Ki runs on test values, so tags outside the machine model show no value." : "PLC-Ki is not reading the PLC right now, so there are no live values."}</div></div>` : "";
      const bar = PK.ui.toolbar({
          left: PK.ui.choice("Show", [{ label: `Tag tables · ${nTags}`, on: tab === "tables", data: { "data-tab": "tables" } }, { label: `Data blocks · ${data.dbs.length}`, on: tab === "dbs", data: { "data-tab": "dbs" } }]),
          search: { id: "tag-q", placeholder: tab === "tables" ? "Find a tag, address or comment" : "Find a data block", label: "Find", value: query },
          right: `<span class="faint small">${W.at ? "Read " + PK.clock(W.at) : ""}</span>` });
      return head + PK.sec("", status + bar + (tab === "tables" ? tablesHtml(q) : dbsHtml(q)) + (window.PKWrite ? PKWrite.logHtml() : ""));
    },
    after(page) {
      if (window.PKWrite) PKWrite.wire(page, current);
      page.querySelectorAll("[data-tab]").forEach((b) => b.onclick = () => { tabChosen = true; tab = b.dataset.tab; query = ""; PK.draw(); });
      const q = page.querySelector("#tag-q");
      if (q) q.oninput = () => { query = q.value; PK.draw(); };
      const vq = page.querySelector("#val-q");
      if (vq) vq.oninput = () => { valQuery = vq.value; PK.draw(); };
    },
  });
})();
