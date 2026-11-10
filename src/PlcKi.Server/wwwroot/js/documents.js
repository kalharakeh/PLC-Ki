// Documents: device manuals, electrical drawings and fault lists, for this machine or for every machine.
// Ask and the Active alarm page look things up in them. Technicians and administrators add them; removing is for administrators.
"use strict";

const PKDocs = (() => {
  const { $, esc, icon, api, state, toast } = PK;

  const KINDS = [["manual", PK.T("Manual")], ["drawing", PK.T("Drawing")], ["faults", PK.T("Fault list")], ["other", PK.T("Other")]];
  const kindName = (k) => PK.t((KINDS.find((x) => x[0] === k) || KINDS[3])[1]);
  const isAdmin = () => PK.can("docs");     // may add documents and change their details
  const fileUrl = (id, page) => `/api/documents/${encodeURIComponent(id)}/file${page ? "#page=" + page : ""}`;
  const stem = (f) => f.replace(/\.[^.]+$/, "");

  let lib = null, query = "", results = null, searching = false, timer = null, searchSeq = 0;

  async function load() {
    try { lib = await api("GET", "/api/documents"); } catch (e) { lib = { error: e.message, items: [] }; }
    clearTimeout(timer);
    if (lib.busy || (lib.items || []).some((d) => d.state === "reading")) timer = setTimeout(async () => { await load(); PK.draw(); }, 2000);
  }

  async function search(q) {
    query = q;
    const seq = ++searchSeq;
    if (!q.trim()) { results = null; searching = false; PK.draw(); return; }
    searching = true;
    try { const r = await api("GET", "/api/documents/search?q=" + encodeURIComponent(q)); if (seq === searchSeq) results = r; }
    catch (e) { if (seq === searchSeq) results = { error: e.message }; }
    if (seq === searchSeq) { searching = false; PK.draw(); }
  }

  // ---------- pieces ----------
  function faultCard(f) {
    const open = f.documentId ? `<a class="btn b small" href="${fileUrl(f.documentId, f.page)}" target="_blank" rel="noopener">${PK.t("Open page {page}", { page: f.page })}</a>` : "";
    const list = (title, xs) => xs && xs.length ? `<h3>${title}</h3><ol class="causes">${xs.map((c, i) => `<li><b>${i + 1}</b><span>${esc(c)}</span></li>`).join("")}</ol>` : "";
    return `<div class="card doc-fault">
        <div class="row" style="gap:8px;align-items:baseline;flex-wrap:nowrap"><span class="no-shrink tag ${f.generalKnowledge ? "" : "blue"}">${esc(f.code)}</span><b class="grow">${esc(f.title)}</b></div>
        ${f.meaning ? `<p class="m-0">${esc(f.meaning)}</p>` : ""}
        ${list(PK.t("Cause"), f.causes)}${list(f.generalKnowledge ? PK.t("What to check") : "", f.generalKnowledge ? f.checks : [])}${list(PK.t("Remedy"), f.fixes)}
        ${!f.causes.length && !f.fixes.length && f.details ? `<p class="small doc-details">${esc(f.details)}</p>` : ""}
        ${f.safety ? `<div class="notice gold small">${icon("warn")}<span>${esc(f.safety)}</span></div>` : ""}
        <div class="row"><span class="grow faint small">${f.generalKnowledge ? PK.t("General knowledge, built into PLC-Ki. Confirm on the device.") : PK.t("From {source}", { source: esc(f.source) })}</span>${open}</div>
      </div>`;
  }

  function pageHit(h) {
    return `<a class="doc-hit" href="${fileUrl(h.id, h.page)}" target="_blank" rel="noopener">
        <span class="ico">${icon(h.kind === "drawing" ? "drawing" : "book")}</span>
        <span class="doc-hit-t"><b>${esc(stem(h.file))}</b> <span class="faint">· ${PK.t("page {page}", { page: h.page })}${h.device ? " · " + esc(h.device) : ""}</span>
        <span class="doc-snip">${esc(h.text)}</span></span><span class="faint" aria-hidden="true">›</span></a>`;
  }

  function docRow(d) {
    const st = d.state === "reading" ? `<span class="tag gold">${PK.t("Reading…")}</span>`
      : d.state === "notext" ? `<span class="tag gold">${PK.t("No text")}</span>`
      : d.state === "failed" ? `<span class="tag red">${PK.t("Not read")}</span>` : "";
    const facts = [d.device ? esc(d.device) : null, kindName(d.kind), d.pages ? PK.t("{n} page|{n} pages", { n: d.pages }) : null,
      d.faultCodes ? PK.t("{n} fault code|{n} fault codes", { n: d.faultCodes }) : null].filter(Boolean).join(" · ");
    return `<li class="doc-row">
        <span class="ico">${icon(d.kind === "drawing" ? "drawing" : d.kind === "faults" ? "list" : "book")}</span>
        <div class="doc-row-t"><b>${esc(stem(d.file))}</b> ${st}<div class="faint small">${facts}</div>
          ${d.note ? `<div class="mt-1 small muted">${esc(d.note)}</div>` : ""}</div>
        <div class="row" style="gap:6px;flex-wrap:nowrap">
          <a class="btn b small" href="${fileUrl(d.id)}" target="_blank" rel="noopener">${PK.t("Open")}</a>
          ${isAdmin() ? `<button class="btn gray small" type="button" data-edit="${esc(d.id)}" aria-label="${esc(PK.t("Change {name}", { name: d.file }))}">${PK.t("Change")}</button>` : ""}
        </div></li>`;
  }

  function group(title, sub, items, empty) {
    return `<div class="card"><h3 class="mt-0">${title}</h3><p class="muted small" style="margin:-4px 0 10px">${sub}</p>
      ${items.length ? `<ul class="doc-list">${items.map(docRow).join("")}</ul>` : `<p class="m-0 muted">${empty}</p>`}</div>`;
  }

  function resultsHtml() {
    if (!query.trim()) return "";
    if (searching && !results) return `<p class="muted">${PK.t("Searching…")}</p>`;
    if (!results) return "";
    if (results.error) return PK.ui.error(results.error);
    const f = results.faults || [], p = results.pages || [];
    if (!f.length && !p.length) return `<p class="muted">${PK.t("Nothing found for “{query}”.", { query: esc(query) })}${lib && lib.items.length ? " " + PK.t("Try other words, or the code as the device shows it.") : " " + PK.t("No documents are added yet.")}</p>`;
    return `${f.length ? `<h3>${PK.t("Fault code")}</h3><div class="grid ${f.length > 1 ? "two" : ""}">${f.map(faultCard).join("")}</div>` : ""}
      ${p.length ? `<h3 style="margin-top:${f.length ? 26 : 0}px">${PK.t("Pages")}</h3><div class="card doc-hits">${p.map(pageHit).join("")}</div>` : ""}`;
  }

  function pageHtml() {
    if (!lib) return PK.hero({ title: PK.t("Documents") }) + PK.sec("", PK.ui.loading("cards", { rows: 3 }));
    if (lib.error) return PK.hero({ title: PK.t("Documents") }) + PK.sec("", PK.ui.error(lib.error, { level: "warn" }));
    const mine = lib.items.filter((d) => d.scope === "machine"), shared = lib.items.filter((d) => d.scope === "shared");
    const builtIn = (lib.builtIn || []).reduce((n, k) => n + k.entries, 0);
    const admin = isAdmin();
    return PK.hero({
      eyebrow: PK.t("Documents"),
      title: PK.t("Manuals and drawings"),
      sub: PK.t("Device manuals, fault lists and electrical drawings for {machine}. Ask and the alarm page look up fault codes, wiring and settings in them and name the page.", { machine: esc(lib.machine) }),
      extra: `<form class="doc-search" id="doc-search" role="search">
          <span class="ico">${icon("search")}</span>
          <input type="search" id="doc-q" placeholder="${esc(PK.t("Search the manuals, or type a fault code (F07801, SRVO-062)"))}" aria-label="${esc(PK.t("Search the documents"))}" value="${esc(query)}" autocomplete="off">
          <button class="btn" type="submit">${PK.t("Search")}</button></form>`,
    })
      + (query.trim() ? PK.sec("", resultsHtml()) : "")
      + PK.sec("alt", `
        ${admin ? `<div class="dropzone" id="doc-drop" tabindex="0" role="button" aria-label="${esc(PK.t("Add documents"))}">${icon("import")}
            <div class="semibold mt-2">${PK.t("Drop manuals, drawings or fault lists here, or click to choose")}</div>
            <div class="mt-1 small">${PK.t("PDF, or a fault list as CSV (code; text; cause; remedy). Several at once is fine.")}</div>
            <input type="file" id="doc-file" accept=".pdf,.csv,.tsv,.txt,.md" multiple class="hidden"></div>` : ""}
        <div class="grid two" style="margin-top:${admin ? 22 : 0}px">
          ${group(PK.t("For this machine"), PK.t("Only for {machine}: its drawings, the leak detector or robot manual of this station.", { machine: esc(lib.machine) }), mine,
            admin ? PK.t("None yet. Drop the machine's EPLAN drawing or a device manual above.") : PK.t("None yet. A technician or an administrator can add them here."))}
          ${group(PK.t("For every machine"), PK.t("Shared by all machines on this PLC-Ki, such as the SINAMICS list manual."), shared,
            admin ? PK.t("None yet. Choose “Every machine” when adding a manual you use everywhere.") : PK.t("None yet."))}
        </div>
        <div class="mt-5 card"><div class="row" style="align-items:flex-start">
          <span class="ico doc-bi">${icon("shield")}</span>
          <div class="grow"><b>${PK.t("Built in")}</b><div class="muted small">${PK.t("{n} entries of general knowledge: Siemens CPU, PROFINET, module, safety and SINAMICS drive messages, FANUC robot alarms and IO-Link events. Your own manuals come first when both know a code.", { n: builtIn })}</div></div></div>
          ${PK.can("settings") ? `<details class="doc-folders"><summary>${PK.t("Copy files in instead")}</summary>
            <p class="small muted">${PK.t("PLC-Ki also reads files you copy into these folders on this PC. Press Look for new files after copying.")}</p>
            <p class="small"><b>${PK.t("This machine")}</b><br><code>${esc(lib.folders.machine)}</code></p>
            <p class="small"><b>${PK.t("Every machine")}</b><br><code>${esc(lib.folders.shared)}</code></p>
            <button class="btn gray small" type="button" id="doc-scan">${PK.t("Look for new files")}</button></details>` : ""}
        </div>`);
  }

  // ---------- adding, changing ----------
  const guessKind = (name) => /\.(csv|tsv)$/i.test(name) ? "faults" : /eplan|schaltplan|schema|wiring|circuit|drawing|electrical|stromlauf/i.test(name) ? "drawing" : "manual";

  function settingsForm({ title, kind, scope, device, scopeFixed, okText, extra = "" }) {
    return `<h2>${esc(title)}</h2>
      ${scopeFixed ? "" : `<p class="mt-0 mb-2 muted small">${PK.t("For")}</p>${PK.ui.choice(PK.t("For"), [["machine", PK.T("This machine")], ["shared", PK.T("Every machine")]].map(([k, l]) => ({ label: PK.t(l), on: scope === k, data: { "data-v": k } })), { data: { "data-g": "scope" } })}`}
      <p class="mt-4 mb-2 muted small">${PK.t("What it is")}</p>${PK.ui.choice(PK.t("What it is"), KINDS.map(([k, l]) => ({ label: PK.t(l), on: k === kind, data: { "data-v": k } })), { data: { "data-g": "kind" } })}
      <label class="mt-4 field">${PK.t("Device or maker (helps find the right manual)")}
        <input type="text" id="doc-device" placeholder="${esc(PK.t("e.g. SINAMICS G120, FANUC R-30iB, Inficon LDS3000"))}" value="${esc(device || "")}" maxlength="120"></label>
      ${extra}
      <div class="mt-4 row end"><button class="btn gray" data-close>${PK.t("Cancel")}</button><button class="btn" data-ok>${esc(okText)}</button></div>`;
  }
  function bindSegments(el) {
    el.querySelectorAll("[data-g]").forEach((g) => g.querySelectorAll("button").forEach((b) => b.onclick = () => {
      g.querySelectorAll("button").forEach((x) => { x.classList.toggle("on", x === b); x.setAttribute("aria-pressed", String(x === b)); });
    }));
  }
  const segValue = (el, g) => { const b = el.querySelector(`[data-g="${g}"] button.on`); return b ? b.dataset.v : null; };

  function addFiles(files) {
    files = [...files];
    if (!files.length) return;
    const names = files.map((f) => f.name);
    const s = PK.sheet(settingsForm({
      title: files.length === 1 ? PK.t("Add {name}", { name: names[0] }) : PK.t("Add {n} documents", { n: files.length }),
      kind: guessKind(names[0]), scope: "machine", okText: PK.t("Add"),
      extra: files.length > 1 ? `<p class="mt-3 mb-0 faint small">${names.map(esc).join(" · ")}</p>` : "",
    }));
    bindSegments(s.el);
    s.el.querySelector("[data-ok]").onclick = async () => {
      const opts = { scope: segValue(s.el, "scope"), kind: segValue(s.el, "kind"), device: s.el.querySelector("#doc-device").value.trim() };
      s.close();
      await upload(files, opts);
    };
  }

  async function upload(files, opts) {
    const s = PK.sheet(`<h2 id="up-h">${PK.t("Adding…")}</h2><div class="progress"><div style="width:0%" id="up-bar"></div></div><p class="muted small" id="up-text"></p>`, { dismiss: false });
    let ok = 0;
    for (let i = 0; i < files.length; i++) {
      const f = files[i];
      $("up-h").textContent = files.length > 1 ? PK.t("Adding {i} of {n}: {name}", { i: i + 1, n: files.length, name: f.name }) : PK.t("Adding {name}", { name: f.name });
      try {
        await new Promise((resolve, reject) => {
          const x = new XMLHttpRequest();
          const q = new URLSearchParams({ scope: opts.scope || "machine", kind: opts.kind || "", device: opts.device || "" });
          x.open("PUT", `/api/admin/documents/upload/${encodeURIComponent(f.name)}?${q}`);
          x.upload.onprogress = (e) => { if (e.lengthComputable) { const p = Math.round(e.loaded / e.total * 100); $("up-bar").style.width = p + "%"; $("up-text").textContent = p + " %"; } };
          x.onload = () => { let d = null; try { d = JSON.parse(x.responseText); } catch { /* */ } x.status < 300 ? resolve(d) : reject(new Error((d && d.error) || PK.t("Could not add {name} ({status}).", { name: f.name, status: x.status }))); };
          x.onerror = () => reject(new Error(PK.t("Could not add {name}.", { name: f.name })));
          x.send(f);
        });
        ok++;
      } catch (e) { toast(e.message, "error"); }
    }
    s.close();
    if (ok) toast(ok === 1 ? PK.t("Added. PLC-Ki reads it now; a big manual takes a minute.") : PK.t("{n} added. PLC-Ki reads them now.", { n: ok }));
    await load();
    PK.draw();
  }

  function change(id) {
    const d = lib.items.find((x) => x.id === id);
    if (!d) return;
    const s = PK.sheet(settingsForm({ title: stem(d.file), kind: d.kind, device: d.device, scopeFixed: true, okText: PK.t("Save"),
      extra: PK.can("settings") ? `<p class="mt-3 mb-0 faint small">${d.scope === "shared" ? PK.t("For every machine") : PK.t("For this machine")}. ${PK.t("To move it, remove it and add it again.")}</p>
        <div class="mt-4 row"><button class="btn red small" type="button" data-remove>${PK.t("Remove from PLC-Ki")}</button></div>`
        : `<p class="mt-3 mb-0 faint small">${d.scope === "shared" ? PK.t("For every machine") : PK.t("For this machine")}. ${PK.t("Only an administrator can remove it.")}</p>` }));
    bindSegments(s.el);
    s.el.querySelector("[data-ok]").onclick = async () => {
      try { await api("PATCH", "/api/admin/documents/" + encodeURIComponent(id), { kind: segValue(s.el, "kind"), device: s.el.querySelector("#doc-device").value }); s.close(); toast(PK.t("Saved.")); }
      catch (e) { toast(e.message, "error"); return; }
      await load(); PK.draw();
    };
    const remove = s.el.querySelector("[data-remove]");
    if (remove) remove.onclick = async () => {
      s.close();
      if (!(await PK.confirmSheet({ title: PK.t("Remove {name}?", { name: stem(d.file) }), text: PK.t("The file is deleted from PLC-Ki. Ask and the alarm page no longer use it."), ok: PK.t("Remove"), danger: true }))) return;
      try { await api("DELETE", "/api/admin/documents/" + encodeURIComponent(id)); toast(PK.t("Removed.")); } catch (e) { toast(e.message, "error"); }
      await load(); PK.draw();
    };
  }

  PK.view("documents", {
    live: false,
    async mount(page, params) {
      if (params && params[0] === "search" && params[1]) query = params[1];
      await load();
      PK.draw();
      if (query) search(query);
    },
    leave() { clearTimeout(timer); timer = null; },
    render: pageHtml,
    after(page) {
      const form = page.querySelector("#doc-search"), q = page.querySelector("#doc-q");
      if (form) form.onsubmit = (e) => { e.preventDefault(); search(q.value); };
      if (q) q.addEventListener("search", () => { if (!q.value) search(""); });
      page.querySelectorAll("[data-edit]").forEach((b) => b.onclick = () => change(b.dataset.edit));
      const drop = page.querySelector("#doc-drop"), file = page.querySelector("#doc-file");
      if (drop) {
        drop.onclick = () => file.click();
        drop.onkeydown = (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); file.click(); } };
        file.onclick = (e) => e.stopPropagation();
        file.onchange = () => { addFiles(file.files); file.value = ""; };
        drop.ondragover = (e) => { e.preventDefault(); drop.classList.add("over"); };
        drop.ondragleave = () => drop.classList.remove("over");
        drop.ondrop = (e) => { e.preventDefault(); drop.classList.remove("over"); addFiles(e.dataTransfer.files); };
      }
      const scan = page.querySelector("#doc-scan");
      if (scan) scan.onclick = async () => { try { await api("POST", "/api/admin/documents/scan"); } catch (e) { toast(e.message, "error"); } await load(); PK.draw(); toast(PK.t("Looked for new files.")); };
    },
  });

  // ---------- Active alarm page: "From the manuals" ----------
  const related = new Map();      // query -> result (or "loading")
  /** A section for the alarm page with fault codes and manual pages that fit this alarm; "" when there is nothing. */
  function alarmSection(a, eqs) {
    const tags = (eqs || []).slice(0, 3).map((e) => e.id).filter(Boolean).join(",");
    const words = a.text + "|" + tags;
    const r = related.get(words);
    if (r === undefined) {
      related.set(words, "loading");
      api("GET", "/api/documents/related?q=" + encodeURIComponent(a.text) + "&tags=" + encodeURIComponent(tags))
        .then((x) => { related.set(words, x); if (x.faults.length || x.pages.length) PK.draw(); })
        .catch(() => related.set(words, { faults: [], pages: [], documents: 0 }));
      return "";
    }
    if (r === "loading" || (!r.faults.length && !r.pages.length)) return "";
    return PK.sec("", `<h2>${PK.t("From the manuals")}</h2><p class="sub">${tags ? PK.t("Where {tags} appears in the drawings and manuals.", { tags: esc(tags.split(",").join(", ")) }) : PK.t("Fault codes and pages in the documents that fit this alarm.")} ${PK.t("Open a page to read it in full.")}</p>
      ${r.faults.length ? `<div class="grid ${r.faults.length > 1 ? "two" : ""}">${r.faults.map(faultCard).join("")}</div>` : ""}
      ${r.pages.length ? `<div class="card doc-hits" style="margin-top:${r.faults.length ? 18 : 0}px">${r.pages.map(pageHit).join("")}</div>` : ""}
      <div class="mt-4 row"><a class="btn b small" href="#/documents/search/${encodeURIComponent(a.text)}">${PK.t("Search all documents")}</a></div>`);
  }

  return { alarmSection, faultCard };
})();
