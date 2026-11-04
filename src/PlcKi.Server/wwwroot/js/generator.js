// Block generator (administrators): say what you need, Claude writes the blocks, the TIA PC imports and compiles them,
// optionally downloads to PLCSIM and watches them run. A real-PLC download needs two people-confirmations: the version
// typed here, and the PLC address typed in the TiaImporter window on the TIA PC.
"use strict";

(() => {
  const { esc, icon, api, state, toast } = PK;

  const g = {
    packages: null, projects: null, projectsError: null, tia: null, settings: null,
    open: null, detail: null, file: null, tab: "versions",   // the package picked on the left, and which part of it shows
    form: { name: "", request: "", project: "", plc: "", edit: "", useContext: true, sclOnly: false, download: "none", plcAddress: "", watch: 30, rounds: 3, effort: "high" },
    job: null, unfollow: null, sheet: null, sheetFor: null,
  };

  const stateCls = (st) => /error/i.test(st || "") ? "bad" : /warn/i.test(st || "") ? "warn" : "ok";
  /** Project names repeat across folders: add the folder when they do. */
  const projectLabel = (p, all) => all.filter((x) => x.name === p.name).length > 1
    ? `${p.name}  (${p.path.split(/[\\/]/).slice(-3, -1).join("\\")})` : p.name;
  async function textOf(url) {
    const r = await fetch(url, { cache: "no-store" });
    if (!r.ok) throw new Error(r.status === 404 ? "File not found." : `Request failed (${r.status}).`);
    return r.text();
  }
  async function loadPackages() {
    try { g.packages = await api("GET", "/api/admin/generator/packages"); } catch (e) { g.packages = []; toast(e.message, "error"); }
    PK.draw();
  }
  /** Picks a package on the left: the right side shows its versions, and "Change it" works on this package. */
  async function openPackage(name, tab = "versions") {
    if (g.open !== name) { g.form.request = ""; g.form.edit = ""; }
    g.open = name; g.form.name = name; g.tab = tab; g.detail = null; g.file = null;
    PK.draw();
    showWork();
    try {
      g.detail = await api("GET", "/api/admin/generator/packages/" + encodeURIComponent(name));
      if (g.detail.plc && !g.form.plc) g.form.plc = g.detail.plc;
      const last = g.detail.versions.find((v) => v.report && v.report.project);
      if (last && !g.form.project) g.form.project = last.report.project;   // change it in the project it went into last
    } catch (e) { toast(e.message, "error"); }
    PK.draw();
    showWork();   // again: the page is taller now that the versions are in
  }
  /** Starts a new package: the right side shows the empty form. */
  function newPackage() {
    g.open = null; g.detail = null; g.file = null; g.form.name = ""; g.form.request = ""; g.form.edit = "";
    PK.draw();
    showWork();
  }
  /** On a narrow screen the list sits above the work area: bring the work area into view (after draw() restores the scroll). */
  function showWork() {
    if (innerWidth > 1000) return;
    setTimeout(() => { const el = document.querySelector("[data-work]"); if (el) window.scrollTo({ top: el.getBoundingClientRect().top + scrollY - 70, behavior: "instant" }); }, 0);
  }
  /** A package name from the request when none is typed: "An FB for a pump with start/stop" gives "fb-pump-start". */
  const STOP = new Set("a an the for with and or of to in on at that which i we need needs want please make create write add new block blocks it is be from by as".split(" "));
  function nameFrom(request) {
    const words = (request || "").toLowerCase().normalize("NFKD").replace(/[^a-z0-9]+/g, " ").split(" ").filter((w) => w && !STOP.has(w));
    let base = words.slice(0, 3).join("-").slice(0, 40) || "blocks";
    const taken = new Set((g.packages || []).map((p) => p.name.toLowerCase()));
    let name = base, n = 2;
    while (taken.has(name)) name = `${base}-${n++}`;
    return name;
  }
  async function showFile(version, file) {
    try {
      g.file = { version, file, text: await textOf(`/api/admin/generator/packages/${encodeURIComponent(g.open)}/${version}/${encodeURIComponent(file)}`) };
    } catch (e) { toast(e.message, "error"); }
    PK.draw();
  }

  // ---------- following the running job ----------
  function followJob(job) {
    if (g.unfollow) g.unfollow();
    g.job = job;
    g.unfollow = PKSetup.follow(job, (j) => {
      waitingSheets(j);
      PK.draw();
      if (j.state !== "running") {
        closeSheet();
        loadPackages();
        if (j.outputs && j.outputs.package) openPackage(j.outputs.package);
      }
    });
  }

  function closeSheet() { if (g.sheet) { g.sheet.close(); g.sheet = null; g.sheetFor = null; } }

  /** The job asks a person to confirm: show it as a sheet, once per question. */
  function waitingSheets(j) {
    const w = j.state === "running" ? j.waiting : null;
    if (!w) { closeSheet(); return; }
    if (g.sheetFor === w) return;
    closeSheet();
    g.sheetFor = w;
    const [what, arg] = w.split(" ");
    if (what === "confirm-version") confirmVersionSheet(j, arg);
    else if (what === "confirm-at-tia") confirmAtTiaSheet(j, Number(arg) || 120);
  }

  function confirmVersionSheet(j, version) {
    const pkg = j.outputs && j.outputs.package;
    g.sheet = PK.sheet(`<span class="eyebrow" style="color:var(--fault)">Real PLC · step 1 of 2</span>
      <h2>Did you review ${esc(version)}?</h2>
      <p class="muted" style="margin:0">It compiles in TIA. Before it goes to a real PLC, read what Claude wrote and look at the blocks in TIA Portal.
        Then type <b>${esc(version)}</b> to go on. Anything else stops here and nothing is downloaded.</p>
      <details class="more"><summary>What Claude wrote (REVIEW.md)</summary><div class="md" data-review>Loading…</div></details>
      <input id="cv-text" autocomplete="off" spellcheck="false" placeholder="Type ${esc(version)}" style="font-family:var(--mono);font-size:18px">
      <div class="row end"><button class="btn gray" data-no>Stop, don't download</button><button class="btn red" data-yes disabled>Download to the real PLC</button></div>`, { dismiss: false });
    const el = g.sheet.el, input = el.querySelector("#cv-text"), yes = el.querySelector("[data-yes]");
    input.focus();
    input.oninput = () => { yes.disabled = input.value.trim() !== version; };
    const answer = async (text) => {
      yes.disabled = true;
      try { await api("POST", `/api/admin/jobs/${j.id}/input`, { text }); } catch (e) { toast(e.message, "error"); }
    };
    yes.onclick = () => answer(input.value.trim());
    input.onkeydown = (e) => { if (e.key === "Enter" && input.value.trim() === version) answer(version); };
    el.querySelector("[data-no]").onclick = () => answer("no");
    if (pkg) textOf(`/api/admin/generator/packages/${encodeURIComponent(pkg)}/${version}/REVIEW.md`)
      .then((t) => { const r = el.querySelector("[data-review]"); if (r) r.innerHTML = PK.md(t); })
      .catch(() => { const r = el.querySelector("[data-review]"); if (r) r.textContent = "Not found. Check the package below."; });
  }

  function confirmAtTiaSheet(j, seconds) {
    const since = j.waitingSince ? new Date(j.waitingSince).getTime() : Date.now();
    g.sheet = PK.sheet(`<span class="eyebrow" style="color:var(--fault)">Real PLC · step 2 of 2</span>
      <h2>Now at the TIA PC.</h2>
      <p class="muted" style="margin:0">Someone at the TIA PC types the PLC's address in the <b>PLC-Ki TIA helper</b> window. Only addresses on that PC's allow-list are accepted,
        and the CPU must be in STOP. If nobody confirms in time, nothing is downloaded.</p>
      <div class="countdown" data-left>2:00</div>
      <div class="progress"><div data-bar style="width:100%;background:var(--fault)"></div></div>
      <p class="faint small" style="margin:0">This closes by itself when the TIA PC answers.</p>`, { dismiss: false });
    const el = g.sheet.el, left = el.querySelector("[data-left]"), bar = el.querySelector("[data-bar]");
    const tick = () => {
      if (!el.isConnected) return;
      const rest = Math.max(0, seconds - (Date.now() - since) / 1000);
      left.textContent = `${Math.floor(rest / 60)}:${String(Math.floor(rest % 60)).padStart(2, "0")}`;
      bar.style.width = (rest / seconds * 100) + "%";
      if (rest > 0) setTimeout(tick, 500); else left.textContent = "Time is up";
    };
    tick();
  }

  // ---------- page ----------
  const stageNames = { generate: "Write", import: "Import & compile", download: "Download", watch: "Watch", done: "Done" };
  function timeline(j) {
    const plan = ["generate", "import"];
    const title = j.title || "";
    if (/PLCSIM/.test(title)) plan.push("download", "watch");
    else if (/real PLC/.test(title)) plan.push("download");
    plan.push("done");
    const [stage, arg] = (j.stage || "").split(" ");
    const at = stage === "fix" ? 0 : Math.max(0, plan.indexOf(stage));
    const ended = j.state !== "running";
    const ok = j.state === "done";
    const pills = plan.map((s, i) => {
      const cls = (ended && ok) || i < at ? "done" : i === at ? (ended ? "bad" : "now") : "";
      const label = s === "import" && stage === "import" && arg ? `Import & compile ${arg}` : stageNames[s];
      return `<span class="tl ${cls}">${esc(label)}</span>`;
    }).join("<i>›</i>");
    const fix = stage === "fix" ? `<span class="tl now">Fixing, round ${esc(arg || "")}</span>` : "";
    const waiting = j.state === "running" && j.waiting
      ? `<div class="notice red" style="margin-top:12px">${icon("warn")}<div style="flex:1"><b>Waiting for a person.</b> ${j.waiting.startsWith("confirm-version") ? "Confirm the version you reviewed." : "Confirm the PLC address at the TIA PC."}</div>
          <button class="btn red small" data-reopen>Open</button></div>` : "";
    return `<div class="timeline" style="margin-top:12px">${pills}${fix ? "<i>›</i>" + fix : ""}</div>${waiting}`;
  }

  function choice(name, current, options) {
    return `<div class="choice" role="group">${options.map((o) => `<button type="button" data-${name}="${o.value}" class="${current === o.value ? "on" : ""} ${o.danger ? "danger" : ""}" aria-pressed="${current === o.value}">
      <span class="ico">${icon(o.icon)}</span><b>${esc(o.label)}</b><small>${esc(o.text)}</small></button>`).join("")}</div>`;
  }

  /** One numbered step of the form. */
  const gstep = (n, title, body, hint = "") => `<div class="gstep"><div class="gstep-n">${n}</div><div class="gstep-b"><h4>${title}</h4>${hint ? `<p class="hint" style="margin:0 0 10px">${hint}</p>` : ""}${body}</div></div>`;

  /** The form: new blocks, or a change to the picked package. */
  function formHtml() {
    const f = g.form;
    const change = !!g.open;
    const running = g.job && g.job.state === "running";
    const projects = g.projects || [];
    const known = projects.some((p) => p.path === f.project);
    const projectField = g.projects
      ? `<select id="g-project" aria-label="TIA project"><option value="">Choose a project</option>${projects.map((p) => `<option value="${esc(p.path)}" ${p.path === f.project ? "selected" : ""}>${esc(projectLabel(p, projects))}</option>`).join("")}
          ${f.project && !known ? `<option value="${esc(f.project)}" selected>${esc(f.project)}</option>` : ""}</select>
         <span class="hint">Projects on the TIA PC${g.tia && g.tia.projectsFolder ? " under " + esc(g.tia.projectsFolder) : ""}. Use a copy, not the plant's master project.</span>`
      : `<input id="g-project" value="${esc(f.project)}" placeholder="C:\\Users\\…\\MyProject.ap20" aria-label="TIA project"><span class="hint">${g.projectsError ? "Type the full path: the TIA PC did not list its projects." : "Looking for projects on the TIA PC…"}</span>`;
    const realAllowed = g.tia && g.tia.realAllowed && g.tia.realAllowed.length;
    const dl = f.download === "plcsim"
      ? `<div class="grid two" style="margin-top:12px"><label class="field">PLCSIM address<input id="g-plcAddress" value="${esc(f.plcAddress)}" placeholder="${esc((g.tia && g.tia.plcsimIp) || "e.g. 192.168.0.10")}"><span class="hint">Empty: the TIA PC's PLCSIM address.</span></label>
          <label class="field">Watch for (seconds)<input id="g-watch" type="number" min="5" max="600" value="${f.watch}"><span class="hint">PLC-Ki reads the new blocks while they run.</span></label></div>`
      : f.download === "real"
        ? `<div class="notice red" style="margin-top:12px">${icon("shield")}<div><b>A real PLC needs two people to confirm.</b> When it compiles, you type the version here after reviewing it.
            Then someone at the TIA PC types the PLC address in the PLC-Ki TIA helper window (2 minutes). Only addresses on that PC's allow-list work, the CPU must be in STOP,
            it is tried once (no fixing loop), and every step is logged. The CPU stays in STOP afterwards.
            ${g.tia ? (realAllowed ? `<br>Allowed on the TIA PC: <b>${esc(g.tia.realAllowed.join(", "))}</b>.` : `<br><b>No address is allowed on the TIA PC yet.</b> At the TIA PC, run Setup-TIA-helper again and enter the PLC's address as allowed for real downloads.`) : ""}</div></div>`
        : "";
    const suggested = nameFrom(f.request);
    const steps = [
      gstep(1, change ? "What should change?" : "What do you need?",
        `<textarea id="g-request" rows="4" aria-label="${change ? "What should change" : "What do you need"}" placeholder="${change ? "For example: add a dry-run stop when the tank level is below 10 %." : "For example: an FB for a pump with start/stop, run feedback, a 5 s feedback alarm and run hours."}">${esc(f.request)}</textarea>
         <span class="hint">${change ? "Claude changes the latest version and saves it as the next one. Leave it empty to import the latest version again." : "Plain words are fine. Name signals and times if you know them."}</span>`),
      gstep(2, "Where should it go?",
        `<div class="grid ${change ? "" : "two"}">
          ${change ? "" : `<label class="field">Package name<input id="g-name" value="${esc(f.name)}" placeholder="${esc(suggested)}" autocomplete="off" spellcheck="false"><span class="hint">Optional. Empty: <b data-suggest>${esc(suggested)}</b>, made from your words.</span></label>`}
          <label class="field">TIA project${projectField}</label>
        </div>
        <div class="row" style="margin-top:12px"><div style="flex:1"><b style="font-weight:600">Fit into the current program</b><div class="muted small">Claude sees the imported program: its types, tags and blocks.${g.settings && g.settings.export ? "" : " (No program imported yet.)"}</div></div>
          <label class="switch"><input type="checkbox" id="g-useContext" ${f.useContext ? "checked" : ""}><span></span></label></div>`),
      gstep(3, "After it compiles", choice("dl", f.download, [
        { value: "none", icon: "tia", label: "Stop in TIA", text: "Import and compile only. You download yourself." },
        { value: "plcsim", icon: "vm", label: "Run in PLCSIM", text: "Download to PLCSIM, watch it run, fix what fails." },
        { value: "real", icon: "warn", label: "Real PLC: changes a machine", text: "Loads the blocks into a real CPU once, after two people confirm. The CPU must be in STOP.", danger: true },
      ]) + dl),
    ];
    return `<div class="card stack gen-form">
      ${steps.join("")}
      <details class="more"><summary>More options</summary><div class="stack" style="margin-top:12px">
        <div class="grid two">
          <label class="field">PLC in the project<input id="g-plc" value="${esc(f.plc)}" placeholder="Empty: the only PLC"></label>
          <label class="field">Change existing blocks<input id="g-edit" value="${esc(f.edit)}" placeholder="FB_Mixer, FC_Valves"><span class="hint">Only these may be overwritten. Needs "Fit into the current program".</span></label>
          <label class="field">Fixing rounds<input id="g-rounds" type="number" min="0" max="6" value="${f.rounds}"><span class="hint">How often Claude fixes compile or run problems on its own.</span></label>
          <label class="field">How hard Claude thinks<select id="g-effort">${["low", "medium", "high", "max"].map((x) => `<option ${f.effort === x ? "selected" : ""}>${x}</option>`).join("")}</select></label>
        </div>
        <div class="row"><div style="flex:1"><b style="font-weight:600">SCL only</b><div class="muted small">No LAD or GRAPH, only SCL blocks.</div></div>
          <label class="switch"><input type="checkbox" id="g-sclOnly" ${f.sclOnly ? "checked" : ""}><span></span></label></div>
      </div></details>
      <div class="gen-go"><button class="btn big ${f.download === "real" ? "red" : ""}" data-run ${running ? "disabled" : ""}>${icon("play")}${running ? "A job is running" : change ? (f.download === "real" ? `Change ${esc(g.open)} (real PLC)` : `Change ${esc(g.open)}`) : f.download === "real" ? "Generate (real PLC)" : "Generate"}</button>
        <span class="faint small">${running ? "Wait for the job above to finish." : change ? "Saved as the next version; the old ones stay." : "Becomes a package with versions: v001, v002 and so on."}</span></div>
    </div>`;
  }

  /** Left: start something new, or pick a package made before. */
  function listHtml() {
    const pk = g.packages;
    const items = !pk ? `<li class="muted small">Loading…</li>`
      : !pk.length ? `<li class="muted small gen-empty">Nothing generated yet. What you start appears here.</li>`
      : pk.map((p) => `<li><button type="button" class="gen-item ${g.open === p.name ? "on" : ""}" data-open="${esc(p.name)}" aria-pressed="${g.open === p.name}">
          <span class="gi-top"><b>${esc(p.name)}</b>${p.state ? `<span class="pill ${stateCls(p.state)}">${esc(p.state)}</span>` : ""}</span>
          <span class="gi-req">${esc(p.request || "")}</span>
          <span class="gi-meta">${esc(p.latest || "")}${p.versions > 1 ? ` · ${p.versions} versions` : ""}${p.time ? " · " + esc(PK.date(p.time)) : ""}</span>
        </button></li>`).join("");
    return `<nav class="gen-list" aria-label="Packages">
      <button type="button" class="gen-new ${g.open ? "" : "on"}" data-new aria-pressed="${!g.open}">${icon("plus")}<span>New blocks</span></button>
      <div class="gen-list-h">Made before</div>
      <ul>${items}</ul>
    </nav>`;
  }

  function reportHtml(r) {
    if (!r) return `<p class="muted small" style="margin:0">Not imported into TIA yet.</p>`;
    const ok = r.fileErrors.length === 0 && r.errorCount === 0 && r.compileState && !/error/i.test(r.compileState);
    return `<div class="kv">
        <span>Compile</span><b><span class="pill ${ok ? stateCls(r.compileState) : "bad"}">${esc(r.compileState || "not compiled")}</span> ${r.errorCount} errors, ${r.warningCount} warnings</b>
        <span>Project</span><b class="mono">${esc(r.project)}</b>
        ${r.download ? `<span>Download</span><b>${esc(r.download)}</b>` : ""}
      </div>
      ${r.fileErrors.length ? `<ul class="small">${r.fileErrors.map((e) => `<li><b>${esc(e.file || "")}</b> ${esc(e.error || "")}</li>`).join("")}</ul>` : ""}
      ${r.messages.length ? `<details class="more"><summary>Compiler messages (${r.messages.length})</summary><ul class="small">${r.messages.map((m) => `<li>${esc([m.state, m.path, m.description].filter(Boolean).join(" · "))}</li>`).join("")}</ul></details>` : ""}
      ${r.runtime.length ? `<div class="notice gold" style="margin-top:10px">${icon("warn")}<div><b>While it ran in PLCSIM</b><ul class="small" style="margin:6px 0 0">${r.runtime.map((x) => `<li>${esc(x)}</li>`).join("")}</ul></div></div>` : ""}`;
  }

  /** The versions of the picked package, newest first. */
  function versionsHtml(d) {
    const kindText = { new: "First version", change: "Changed on request", fix: "Fixed after TIA's report" };
    return `<div class="stack">${d.versions.map((v, i) => `<details class="card more gen-ver" ${i === 0 ? "open" : ""}>
        <summary><b>${esc(v.folder)}</b> <span class="muted">· ${esc(kindText[v.kind] || v.kind)} · ${PK.date(v.time)}</span>
          ${v.report ? `<span class="pill ${v.report.errorCount || v.report.fileErrors.length ? "bad" : stateCls(v.report.compileState)}" style="margin-left:8px">${esc(v.report.compileState || "?")}</span>` : ""}</summary>
        <div class="grid two" style="margin-top:14px;align-items:start">
          <div class="stack">
            ${v.request ? `<div><span class="eyebrow">Asked for</span><p style="margin:4px 0 0">${esc(v.request)}</p></div>` : ""}
            <div><span class="eyebrow">In TIA</span><div style="margin-top:6px">${reportHtml(v.report)}</div></div>
            ${v.problems && v.problems.length ? `<div class="notice gold">${icon("warn")}<div><b>The offline check still found</b><ul class="small" style="margin:6px 0 0">${v.problems.map((p) => `<li>${esc(p)}</li>`).join("")}</ul></div></div>` : ""}
            <div><span class="eyebrow">Files</span><div class="row" style="margin-top:6px">${v.files.map((f) => `<button class="tag ${g.file && g.file.version === v.folder && g.file.file === f ? "on" : ""}" data-file="${esc(f)}" data-ver="${esc(v.folder)}">${esc(f)}${v.added.includes(f) ? " · new" : v.changed.includes(f) ? " · changed" : ""}</button>`).join("")}</div></div>
            ${v.replaces && v.replaces.length ? `<p class="faint small" style="margin:0">May overwrite: ${esc(v.replaces.join(", "))}</p>` : ""}
          </div>
          <div>${v.review ? `<span class="eyebrow">Claude's notes</span><div class="md" style="margin-top:6px">${PK.md(v.review)}</div>` : ""}</div>
        </div>
        ${g.file && g.file.version === v.folder ? `<div class="code" style="margin-top:14px;max-height:480px;overflow:auto"><pre style="margin:0;padding:14px 16px">${esc(g.file.text)}</pre></div>` : ""}
      </details>`).join("")}</div>`;
  }

  /** Right: the form for new blocks, or the picked package with its versions and "Change it". */
  function workHtml() {
    if (!g.open) return `<div class="gen-head"><div><span class="eyebrow">New package</span><h2>New blocks</h2>
        <p class="muted small" style="margin:0">Say what you need. Claude writes the blocks, TIA Portal imports and compiles them, and, if you choose, PLCSIM runs them.</p></div></div>${formHtml()}`;
    const d = g.detail;
    const p = (g.packages || []).find((x) => x.name === g.open) || {};
    const meta = d ? [`${d.versions.length} version${d.versions.length === 1 ? "" : "s"}`, d.plc ? "PLC " + d.plc : "", d.context ? "fitted into " + d.context.split(/[\\/]/).pop() : ""].filter(Boolean).join(" · ") : "Loading…";
    const tabs = `<div class="segmented" role="tablist" aria-label="Package">
        <button type="button" role="tab" data-tab="versions" class="${g.tab === "versions" ? "on" : ""}" aria-selected="${g.tab === "versions"}">Versions</button>
        <button type="button" role="tab" data-tab="change" class="${g.tab === "change" ? "on" : ""}" aria-selected="${g.tab === "change"}">Change it</button></div>`;
    return `<div class="gen-head"><div style="min-width:0"><span class="eyebrow">Package${p.state ? ` <span class="pill ${stateCls(p.state)}">${esc(p.state)}</span>` : ""}</span><h2>${esc(g.open)}</h2>
        <p class="muted small" style="margin:0">${esc(meta)}</p></div>${tabs}</div>
      ${g.tab === "change" ? formHtml() : d ? versionsHtml(d) : `<div class="card"><p class="muted" style="margin:0">Loading…</p></div>`}`;
  }

  PK.view("setup/generator", {
    async mount() {
      PK.draw();
      loadPackages();
      try {
        g.settings = (await PKSetup.loadSettings()).settings;
        if (!g.form.project && g.settings.tiaProject) g.form.project = g.settings.tiaProject;
      } catch { /* shown elsewhere */ }
      api("GET", "/api/admin/tia/status").then((s) => { g.tia = s; PK.draw(); }).catch(() => { });
      api("GET", "/api/admin/tia/projects").then((p) => { g.projects = p.projects || []; PK.draw(); })
        .catch((e) => { g.projects = null; g.projectsError = e.message; PK.draw(); });
      try {
        const jobs = await api("GET", "/api/admin/jobs");
        const j = jobs.find((x) => x.kind === "develop" && x.state === "running") || (g.job ? null : jobs.find((x) => x.kind === "develop"));
        if (j && (!g.job || g.job.id !== j.id)) followJob(j);
      } catch { /* */ }
      PK.draw();
    },
    onJobs(jobs) {
      const j = jobs.find((x) => x.kind === "develop" && x.state === "running");
      if (j && (!g.job || g.job.id !== j.id)) followJob(j);
    },
    leave() { closeSheet(); },
    render() {
      const t = g.tia;
      const tiaChip = t ? `<span class="pill ${t.ok ? "ok" : "warn"}"><span class="dot"></span>TIA Portal ${t.ok ? "ready" + (t.machine ? " on " + esc(t.machine) : "") : "not reachable"}</span>` : `<span class="pill">TIA Portal …</span>`;
      const chips = `<div class="row gen-status">${tiaChip}
          <span class="pill">PLCSIM ${t && t.plcsimIp ? esc(t.plcsimIp) : "not set"}</span>
          <span class="pill ${t && t.realAllowed && t.realAllowed.length ? "warn" : ""}">Real PLCs allowed: ${t && t.realAllowed ? t.realAllowed.length : "none"}</span></div>`;
      const tiaNote = t && !t.ok ? `<div class="notice gold" style="margin-top:12px">${icon("info")}<div style="flex:1"><b>TIA Portal is not reachable.</b> ${esc(t.message || "")}</div><a class="btn gold small" href="#/setup/connections">Connections</a></div>` : "";
      return PK.sec("hero compact", `${PKSetup.setupNav("generator")}<h1>Block generator</h1>
          <p class="sub">Claude writes PLC blocks from plain words, TIA Portal compiles them, and problems go back to Claude until it is clean. Nothing reaches a real PLC without two people confirming.</p>
          ${chips}${tiaNote}`)
        + (g.job ? PK.sec("tight", `<span class="eyebrow">Latest job</span>${PKSetup.jobCard(g.job, timeline(g.job))}`) : "")
        + PK.sec("", `<div class="gen-layout">${listHtml()}<div class="gen-work" data-work>${workHtml()}</div></div>`);
    },
    after(page) {
      PKSetup.bindJob(page);
      const f = g.form;
      const v = (id) => page.querySelector("#g-" + id);
      const read = () => {
        for (const k of ["name", "request", "project", "plc", "edit", "effort", "plcAddress"]) if (v(k)) f[k] = v(k).value;
        for (const k of ["watch", "rounds"]) if (v(k)) f[k] = Number(v(k).value);
        for (const k of ["useContext", "sclOnly"]) if (v(k)) f[k] = v(k).checked;
      };
      page.querySelectorAll("textarea,input,select").forEach((x) => { if (x.id && x.id.startsWith("g-")) PK.on(x, "change", read); });
      page.querySelectorAll("[data-dl]").forEach((b) => b.onclick = () => { read(); f.download = b.dataset.dl; PK.draw(); });
      page.querySelectorAll("[data-open]").forEach((b) => b.onclick = () => { read(); openPackage(b.dataset.open); });
      const nw = page.querySelector("[data-new]");
      if (nw) nw.onclick = () => { read(); newPackage(); };
      page.querySelectorAll("[data-tab]").forEach((b) => b.onclick = () => { read(); g.tab = b.dataset.tab; PK.draw(); });
      const req = v("request"), sug = page.querySelector("[data-suggest]"), nm = v("name");
      if (req && sug) PK.on(req, "input", () => { const n = nameFrom(req.value); sug.textContent = n; if (nm) nm.placeholder = n; });
      page.querySelectorAll("[data-file]").forEach((b) => b.onclick = (e) => { e.preventDefault(); showFile(b.dataset.ver, b.dataset.file); });
      const reopen = page.querySelector("[data-reopen]");
      if (reopen) reopen.onclick = () => { g.sheetFor = null; waitingSheets(g.job); };
      const run = page.querySelector("[data-run]");
      if (run) run.onclick = async () => {
        read();
        const name = g.open || f.name.trim() || nameFrom(f.request);
        if (f.download === "real" && !(await PK.confirmSheet({ title: "Plan a real-PLC download?",
          text: "PLC-Ki still asks you to type the version after it compiles, and the TIA PC still asks for the PLC address. Nothing is downloaded before both.", ok: "Start", danger: true }))) return;
        try {
          const job = await api("POST", "/api/admin/generator/run", {
            package: name, request: f.request.trim() || null, project: f.project.trim(), plc: f.plc.trim() || null, edit: f.edit.trim() || null,
            useContext: f.useContext, sclOnly: f.sclOnly, download: f.download, plcAddress: f.plcAddress.trim() || null, watch: f.watch, rounds: f.rounds, effort: f.effort,
          });
          g.open = name; f.name = name; g.tab = "versions";
          followJob(job);
          PK.draw();
          window.scrollTo({ top: 0, behavior: "smooth" });
        } catch (e) { toast(e.message, "error"); }
      };
    },
  });
})();
