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

  /** The live value plus, for administrators, the button that changes it. */
  const now = (t) => `<span class="row" style="gap:4px;flex-wrap:nowrap">${live(t)}${window.PKWrite ? PKWrite.button(t) : ""}</span>`;
  /** The value shown for a symbol, as the change sheet's "now". */
  const current = (sym) => { const v = W.values[sym]; if (!v || v.error) return null; return v.value === "True" ? true : v.value === "False" ? false : v.value; };

  function live(t) {
    const v = W.values[t.symbol];
    if (!v) return `<span class="faint">…</span>`;
    if (v.error) return `<span class="faint small" style="white-space:nowrap" title="${esc(v.error)}">not read</span>`;
    if (v.value === "True" || v.value === "False") v.value = v.value === "True";
    if (v.value === true || v.value === false) return `<span class="chip ${v.value ? "t" : "f"}" style="margin:0">${v.value ? "TRUE" : "FALSE"}</span>`;
    if (v.value == null) return `<span class="faint">…</span>`;
    const n = typeof v.value === "number" ? (Number.isInteger(v.value) ? String(v.value) : v.value.toFixed(3).replace(/0+$/, "").replace(/\.$/, "")) : String(v.value);
    return `<span class="chip" style="margin:0">${esc(t.type === "Time" && typeof v.value === "number" ? n + " ms" : n)}</span>`;
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
      return `<div class="card"><h3 style="margin:0 0 6px">${esc(tb.name)} <span class="faint small">${tags.length} tag${tags.length > 1 ? "s" : ""}</span></h3>
        <table class="tbl tags-tbl"><tr><th>Name</th><th>Address</th><th>Type</th><th>Now</th><th>Comment</th></tr>
        ${take.map((t) => `<tr><td class="mono">${esc(t.name)}</td><td class="mono">${esc(t.address || "")}</td><td class="mono muted">${esc(t.datatype)}</td><td>${now(t)}</td><td class="muted">${esc(t.comment || "")}</td></tr>`).join("")}
        </table></div>`;
    }).join("");
    const none = query.trim() ? `No tag matches “${esc(query)}”.` : "This program export has no PLC tag tables.";
    return (html || `<div class="card"><p class="muted" style="margin:0">${none}</p></div>`)
      + (more ? `<p class="faint small">${more} more tag${more > 1 ? "s" : ""} not shown. Search to narrow the list.</p>` : "");
  }

  function dbsHtml(q) {
    const list = data.dbs.filter((d) => match(q, d.name, d.title, d.instanceOf, d.number));
    const nav = list.map((d) => `<a href="#/tags/db/${encodeURIComponent(d.name)}" class="${d.name === db ? "on" : ""}">${esc(d.name)}<span class="tag">${d.number != null ? "DB" + d.number : ""}</span></a>`).join("")
      || `<p class="faint small" style="padding:8px 10px">${q ? "No data block matches." : "This program export has no data blocks."}</p>`;
    let right;
    shown = [];
    if (!db) right = `<div class="card"><p class="muted" style="margin:0">Pick a data block on the left to see its values live.</p></div>`;
    else if (dbError) right = `<div class="notice red">${icon("warn")}<div>${esc(dbError)}</div></div>`;
    else if (!dbData) right = `<div class="card"><p class="muted" style="margin:0">Loading…</p></div>`;
    else {
      const vq = valQuery.trim().toLowerCase();
      const vals = dbData.values.filter((t) => match(vq, t.symbol, t.comment, t.datatype));
      const take = vals.slice(0, MAX_ROWS);
      shown = take;
      const short = (s) => s.startsWith(`"${dbData.db}".`) ? s.slice(dbData.db.length + 3) : s;
      right = `<div class="card">
        <div class="row"><h2 style="margin:0;font-size:24px">${esc(dbData.db)}</h2><span class="tag">${dbData.number != null ? "DB " + dbData.number : ""}</span>
          ${dbData.instanceOf ? `<span class="faint small">instance of <a href="#/program/${encodeURIComponent(dbData.instanceOf)}">${esc(dbData.instanceOf)}</a></span>` : ""}
          <span class="spacer"></span><a class="btn plain small" href="#/program/${encodeURIComponent(dbData.db)}">Show in the program ›</a></div>
        ${dbData.title || dbData.comment ? `<p class="muted" style="margin:8px 0 0">${esc([dbData.title, dbData.comment].filter(Boolean).join(" · "))}</p>` : ""}
        <div class="row" style="margin-top:10px;gap:12px"><input type="search" id="val-q" placeholder="Find a value" aria-label="Find a value" value="${esc(valQuery)}" style="max-width:300px">
          <span class="faint small">Optimized data blocks have no fixed addresses: values are read by name.</span></div>
        <table class="tbl tags-tbl" style="margin-top:10px"><tr><th>Value</th><th>Type</th><th>Now</th><th>Comment</th></tr>
        ${take.map((t) => `<tr><td class="mono">${esc(short(t.symbol))}</td><td class="mono muted">${esc(t.datatype)}</td><td>${now(t)}</td><td class="muted">${esc(t.comment || "")}</td></tr>`).join("")}
        </table>
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
      <table class="tbl tags-tbl" style="margin-top:8px"><tr><th>Tag</th><th>Address</th><th>Type</th><th>Now</th><th>Comment</th></tr>
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
      if (params[0] === "db" && params[1]) { tab = "dbs"; openDb(params[1]); }
      // Draw now: when the tags were already read (an earlier visit, or the Program page), load() has nothing to wait for
      PK.draw();
    },
    leave() { shown = []; },
    render(params) {
      if (params[0] === "db" && params[1] && params[1] !== db) { tab = "dbs"; query = ""; openDb(params[1]); }
      const head = PK.hero({ cls: "hero compact", title: "Tags",
        sub: "Every tag of the PLC tag tables with its address, and every value of the data blocks, read live from the PLC."
          + (PK.state.user && PK.state.user.role === "admin" ? " Change a value with the pen: on PLCSIM (safety values after a warning), on a real PLC only memory and data values, when allowed and confirmed." : "") });
      if (dataError) return head + PK.sec("", `<div class="notice gold">${icon("warn")}<div>${esc(dataError)}</div></div>`);
      if (!data) return head + PK.sec("", `<p class="muted">Loading…</p>`);
      const q = query.trim().toLowerCase();
      const nTags = data.tables.reduce((n, t) => n + t.tags.length, 0);
      // First time the data is shown: open the first tab that has something in it
      if (!tabChosen) { tabChosen = true; if (!nTags && data.dbs.length) tab = "dbs"; }
      const status = W.connection && W.connection !== "connected"
        ? `<div class="notice gold" style="margin-bottom:14px">${icon("warn")}<div>${W.connection === "snapshot" ? "PLC-Ki runs on test values, so tags outside the machine model show no value." : "PLC-Ki is not reading the PLC right now, so there are no live values."}</div></div>` : "";
      const bar = `<div class="row" style="gap:12px;margin-bottom:16px;flex-wrap:wrap">
          <div class="segmented" role="group" aria-label="Show">
            <button type="button" data-tab="tables" class="${tab === "tables" ? "on" : ""}">Tag tables · ${nTags}</button>
            <button type="button" data-tab="dbs" class="${tab === "dbs" ? "on" : ""}">Data blocks · ${data.dbs.length}</button></div>
          <input type="search" id="tag-q" placeholder="${tab === "tables" ? "Find a tag, address or comment" : "Find a data block"}" aria-label="Find" value="${esc(query)}" style="max-width:340px">
          <span class="spacer"></span><span class="faint small">${W.at ? "Read " + PK.clock(W.at) : ""}</span></div>`;
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
