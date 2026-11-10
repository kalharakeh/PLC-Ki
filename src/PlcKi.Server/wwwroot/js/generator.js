// Block generator (administrators): say what you need, Ki (PLC-Ki's block writer, on Claude) writes the blocks, the TIA PC imports and compiles them,
// optionally downloads to PLCSIM and watches them run. A real-PLC download needs two people-confirmations: the version
// typed here, and the PLC address typed in the TIA helper window on the TIA PC.
"use strict";

(() => {
  const { esc, icon, api, state, toast, AI } = PK;

  const g = {
    packages: null, projects: null, projectsError: null, tia: null, settings: null,
    open: null, detail: null, file: null, tab: "versions",   // the package picked on the left, and which part of it shows
    form: { name: "", request: "", project: "", plc: "", edit: "", useContext: true, sclOnly: false, download: "none", plcAddress: "", watch: 30, rounds: 3, effort: "high" },
    job: null, jobHidden: null, unfollow: null, sheet: null, sheetFor: null,
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
  /** On a narrow screen the work area comes first: bring it into view (after draw() restores the scroll). */
  function showWork() {
    if (PK.screenSize() === "desktop") return;
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
    g.jobHidden = null;
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
    g.sheet = PK.sheet(`<span class="fg-fault eyebrow">${PK.t("Real PLC · step 1 of 2")}</span>
      <h2>${PK.t("Did you review {version}?", { version: esc(version) })}</h2>
      <p class="m-0 muted">${PK.t("It compiles in TIA. Before it goes to a real PLC, read what {ai} wrote and look at the blocks in TIA Portal.", { ai: AI })}
        ${PK.t("Then type {version} to go on. Anything else stops here and nothing is downloaded.", { version: `<b>${esc(version)}</b>` })}</p>
      <details class="more"><summary>${PK.t("{ai}'s notes (REVIEW.md)", { ai: AI })}</summary><div class="md" data-review>${PK.ui.loading("lines")}</div></details>
      <input id="cv-text" autocomplete="off" spellcheck="false" placeholder="${PK.t("Type {version}", { version: esc(version) })}" style="font-family:var(--mono);font-size:18px">
      <div class="row end"><button class="btn gray" data-no>${PK.t("Stop, don't download")}</button><button class="btn red" data-yes disabled>${PK.t("Download to the real PLC")}</button></div>`, { dismiss: false });
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
      .catch(() => { const r = el.querySelector("[data-review]"); if (r) r.textContent = PK.t("Not found. Check the package below."); });
  }

  function confirmAtTiaSheet(j, seconds) {
    const since = j.waitingSince ? new Date(j.waitingSince).getTime() : Date.now();
    g.sheet = PK.sheet(`<span class="fg-fault eyebrow">${PK.t("Real PLC · step 2 of 2")}</span>
      <h2>${PK.t("Now at the TIA PC.")}</h2>
      <p class="m-0 muted">${PK.t("Someone at the TIA PC types the PLC's address in the {window} window. Only addresses on that PC's allow-list are accepted, and the CPU must be in STOP. If nobody confirms in time, nothing is downloaded.", { window: "<b>PLC-Ki TIA helper</b>" })}</p>
      <div class="countdown" data-left>2:00</div>
      <div class="progress"><div data-bar style="width:100%;background:var(--fault)"></div></div>
      <p class="m-0 faint small">${PK.t("This closes by itself when the TIA PC answers.")}</p>`, { dismiss: false });
    const el = g.sheet.el, left = el.querySelector("[data-left]"), bar = el.querySelector("[data-bar]");
    const tick = () => {
      if (!el.isConnected) return;
      const rest = Math.max(0, seconds - (Date.now() - since) / 1000);
      left.textContent = `${Math.floor(rest / 60)}:${String(Math.floor(rest % 60)).padStart(2, "0")}`;
      bar.style.width = (rest / seconds * 100) + "%";
      if (rest > 0) setTimeout(tick, 500); else left.textContent = PK.t("Time is up");
    };
    tick();
  }

  // ---------- page ----------
  const stageNames = { generate: PK.T("Write"), import: PK.T("Import & compile"), download: PK.T("Download"), watch: PK.T("Watch"), done: PK.T("Done") };
  function timeline(j) {
    const title = j.title || "";
    const plan = /^Import /.test(title) ? ["import"] : ["generate", "import"];   // "Import ... again": no writing step
    if (/PLCSIM/.test(title)) plan.push("download", "watch");
    else if (/real PLC/.test(title)) plan.push("download");
    plan.push("done");
    const [stage, arg] = (j.stage || "").split(" ");
    const at = stage === "fix" ? 0 : Math.max(0, plan.indexOf(stage));
    const ended = j.state !== "running";
    const ok = j.state === "done";
    const pills = plan.map((s, i) => {
      const cls = (ended && ok) || i < at ? "done" : i === at ? (ended ? "bad" : "now") : "";
      const label = s === "import" && stage === "import" && arg ? PK.t("Import & compile {arg}", { arg }) : PK.t(stageNames[s]);
      return `<span class="tl ${cls}">${esc(label)}</span>`;
    }).join("<i>›</i>");
    const fix = stage === "fix" ? `<span class="tl now">${PK.t("Fixing, round {n}", { n: esc(arg || "") })}</span>` : "";
    const waiting = j.state === "running" && j.waiting
      ? `<div class="mt-3 notice red">${icon("warn")}<div class="grow"><b>${PK.t("Waiting for a person.")}</b> ${j.waiting.startsWith("confirm-version") ? PK.t("Confirm the version you reviewed.") : PK.t("Confirm the PLC address at the TIA PC.")}</div>
          <button class="btn red small" data-reopen>${PK.t("Open")}</button></div>` : "";
    return `<div class="mt-3 timeline">${pills}${fix ? "<i>›</i>" + fix : ""}</div>${waiting}`;
  }

  function choice(name, current, options) {
    return `<div class="choice" role="group">${options.map((o) => `<button type="button" data-${name}="${o.value}" class="${current === o.value ? "on" : ""} ${o.danger ? "danger" : ""}" aria-pressed="${current === o.value}">
      <span class="ico">${icon(o.icon)}</span><b>${esc(o.label)}</b><small>${esc(o.text)}</small></button>`).join("")}</div>`;
  }

  /** One numbered step of the form. */
  const gstep = (n, title, body, hint = "") => `<div class="gstep"><div class="gstep-n">${n}</div><div class="gstep-b"><h4>${title}</h4>${hint ? `<p class="mt-0 mb-3 hint">${hint}</p>` : ""}${body}</div></div>`;

  /** The Change-it button: an empty box imports the latest version again, without Ki. */
  const runLabel = (request) => {
    const real = g.form.download === "real", name = esc(g.open);
    return (request || "").trim() ? (real ? PK.t("Change {name} (real PLC)", { name }) : PK.t("Change {name}", { name }))
      : real ? PK.t("Import {name} again (real PLC)", { name }) : PK.t("Import {name} again", { name });
  };

  /** The form: new blocks, or a change to the picked package. */
  function formHtml() {
    const f = g.form;
    const change = !!g.open;
    const running = g.job && g.job.state === "running";
    const projects = g.projects || [];
    const known = projects.some((p) => p.path === f.project);
    const projectField = g.projects
      ? `<select id="g-project" aria-label="${PK.t("TIA project")}"><option value="">${PK.t("Choose a project")}</option>${projects.map((p) => `<option value="${esc(p.path)}" ${p.path === f.project ? "selected" : ""}>${esc(projectLabel(p, projects))}</option>`).join("")}
          ${f.project && !known ? `<option value="${esc(f.project)}" selected>${esc(f.project)}</option>` : ""}</select>
         <span class="hint">${g.tia && g.tia.projectsFolder ? PK.t("Projects on the TIA PC under {folder}. Use a copy, not the plant's master project.", { folder: esc(g.tia.projectsFolder) }) : PK.t("Projects on the TIA PC. Use a copy, not the plant's master project.")}</span>`
      : `<input id="g-project" value="${esc(f.project)}" placeholder="C:\\Users\\…\\MyProject.ap20" aria-label="${PK.t("TIA project")}"><span class="hint">${g.projectsError ? PK.t("Type the full path: the TIA PC did not list its projects.") : PK.t("Looking for projects on the TIA PC…")}</span>`;
    const realAllowed = g.tia && g.tia.realAllowed && g.tia.realAllowed.length;
    const dl = f.download === "plcsim"
      ? `<div class="mt-3 grid two"><label class="field">${PK.t("PLCSIM address")}<input id="g-plcAddress" value="${esc(f.plcAddress)}" placeholder="${esc((g.tia && g.tia.plcsimIp) || PK.t("e.g. 192.168.0.10"))}"><span class="hint">${PK.t("Empty: the TIA PC's PLCSIM address.")}</span></label>
          <label class="field">${PK.t("Watch for (seconds)")}<input id="g-watch" type="number" min="5" max="600" value="${f.watch}"><span class="hint">${PK.t("PLC-Ki reads the new blocks while they run.")}</span></label></div>`
      : f.download === "real"
        ? `<div class="mt-3 notice red">${icon("shield")}<div><b>${PK.t("A real PLC needs two people to confirm.")}</b> ${PK.t("When it compiles, you type the version here after reviewing it. Then someone at the TIA PC types the PLC address in the PLC-Ki TIA helper window (2 minutes). Only addresses on that PC's allow-list work, the CPU must be in STOP, it is tried once (no fixing loop), and every step is logged. The CPU stays in STOP afterwards.")}
            ${g.tia ? (realAllowed ? `<br>${PK.t("Allowed on the TIA PC: {list}.", { list: `<b>${esc(g.tia.realAllowed.join(", "))}</b>` })}` : `<br><b>${PK.t("No address is allowed on the TIA PC yet.")}</b> ${PK.t("At the TIA PC, run Setup-TIA-helper again and enter the PLC's address as allowed for real downloads.")}`) : ""}</div></div>`
        : "";
    const suggested = nameFrom(f.request);
    const steps = [
      gstep(1, change ? PK.t("What should change?") : PK.t("What do you need?"),
        `<textarea id="g-request" rows="4" aria-label="${change ? PK.t("What should change") : PK.t("What do you need")}" placeholder="${change ? PK.t("For example: add a dry-run stop when the tank level is below 10 %.") : PK.t("For example: an FB for a pump with start/stop, run feedback, a 5 s feedback alarm and run hours.")}">${esc(f.request)}</textarea>
         <span class="hint">${change ? PK.t("{ai} changes the latest version and saves it as the next one. Leave it empty to import the latest version again (no {ai}, no cost).", { ai: AI }) : PK.t("Plain words are fine. Name signals and times if you know them.")}</span>`),
      gstep(2, PK.t("Where should it go?"),
        `<div class="grid ${change ? "" : "two"}">
          ${change ? "" : `<label class="field">${PK.t("Package name")}<input id="g-name" value="${esc(f.name)}" placeholder="${esc(suggested)}" autocomplete="off" spellcheck="false"><span class="hint">${PK.t("Optional. Empty: {name}, made from your words.", { name: `<b data-suggest>${esc(suggested)}</b>` })}</span></label>`}
          <label class="field">${PK.t("TIA project")}${projectField}</label>
        </div>
        <div class="mt-3 row"><div class="grow"><b class="semibold">${PK.t("Fit into the current program")}</b><div class="muted small">${PK.t("{ai} sees the imported program: its types, tags and blocks.", { ai: AI })}${g.settings && g.settings.export ? "" : ` ${PK.t("(No program imported yet.)")}`}</div></div>
          <label class="switch"><input type="checkbox" id="g-useContext" ${f.useContext ? "checked" : ""}><span></span></label></div>`),
      gstep(3, PK.t("After it compiles"), choice("dl", f.download, [
        { value: "none", icon: "tia", label: PK.t("Stop in TIA"), text: PK.t("Import and compile only. You download yourself.") },
        { value: "plcsim", icon: "vm", label: PK.t("Run in PLCSIM"), text: PK.t("Download to PLCSIM, watch it run, fix what fails.") },
        { value: "real", icon: "warn", label: PK.t("Real PLC: changes a machine"), text: PK.t("Loads the blocks into a real CPU once, after two people confirm. The CPU must be in STOP."), danger: true },
      ]) + dl),
    ];
    return `<div class="card stack gen-form">
      ${steps.join("")}
      <details class="more"><summary>${PK.t("More options")}</summary><div class="mt-3 stack">
        <div class="grid two">
          <label class="field">${PK.t("PLC in the project")}<input id="g-plc" value="${esc(f.plc)}" placeholder="${PK.t("Empty: the only PLC")}"></label>
          <label class="field">${PK.t("Change existing blocks or tags")}<input id="g-edit" value="${esc(f.edit)}" placeholder="FB_Mixer, FC_Valves, XV101_ZSO"><span class="hint">${PK.t("Only these may be overwritten. Tags: address, type or comment, standard tags only (never the safety program's). Needs \"Fit into the current program\".")}</span></label>
          <label class="field">${PK.t("Fixing rounds")}<input id="g-rounds" type="number" min="0" max="6" value="${f.rounds}"><span class="hint">${PK.t("How often {ai} fixes compile or run problems on its own.", { ai: AI })}</span></label>
          <label class="field">${PK.t("How hard {ai} thinks", { ai: AI })}<select id="g-effort">${[PK.T("low"), PK.T("medium"), PK.T("high"), PK.T("max")].map((x) => `<option value="${x}" ${f.effort === x ? "selected" : ""}>${PK.t(x)}</option>`).join("")}</select></label>
        </div>
        <div class="row"><div class="grow"><b class="semibold">${PK.t("SCL only")}</b><div class="muted small">${PK.t("No LAD or GRAPH, only SCL blocks.")}</div></div>
          <label class="switch"><input type="checkbox" id="g-sclOnly" ${f.sclOnly ? "checked" : ""}><span></span></label></div>
      </div></details>
      <div class="gen-go"><button class="btn big ${f.download === "real" ? "red" : ""}" data-run ${running ? "disabled" : ""}>${icon("play")}<span data-runlabel>${running ? PK.t("A job is running") : change ? runLabel(f.request) : f.download === "real" ? PK.t("Generate (real PLC)") : PK.t("Generate")}</span></button>
        <span class="faint small">${running ? PK.t("Wait for the job above to finish.") : change ? PK.t("Saved as the next version; the old ones stay.") : PK.t("Becomes a package with versions: v001, v002 and so on.")}</span></div>
    </div>`;
  }

  const running = (name) => !!(g.job && g.job.state === "running" && g.job.outputs && g.job.outputs.package === name);

  /** Left: start something new, or pick a package made before. */
  function listHtml() {
    const pk = g.packages;
    const items = !pk ? `<li>${PK.ui.loading("list", { rows: 3 })}</li>`
      : !pk.length ? `<li>${PK.ui.empty({ icon: "file", title: PK.t("Nothing generated yet."), text: PK.t("What you start appears here."), cls: "small" })}</li>`
      : pk.map((p) => `<li><button type="button" class="gen-item ${g.open === p.name ? "on" : ""}" data-open="${esc(p.name)}" aria-pressed="${g.open === p.name}">
          <span class="gi-top"><b>${esc(p.name)}</b>${running(p.name) ? `<span class="pill info"><span class="dot"></span>${PK.t("Running")}</span>` : p.state ? `<span class="pill ${stateCls(p.state)}">${esc(PK.srv(p.state))}</span>` : ""}</span>
          <span class="gi-req">${esc(p.request || "")}</span>
          <span class="gi-meta">${esc(p.latest || "")}${p.versions > 1 ? ` · ${PK.t("{n} version|{n} versions", { n: p.versions })}` : ""}${p.time ? " · " + esc(PK.date(p.time)) : ""}</span>
        </button></li>`).join("");
    return `<nav class="gen-list" aria-label="${PK.t("Packages")}">
      <button type="button" class="gen-new ${g.open ? "" : "on"}" data-new aria-pressed="${!g.open}">${icon("plus")}<span>${PK.t("New blocks")}</span></button>
      <button type="button" class="gen-new" data-sim title="${PK.t("Fills in a request for a simulator made from this machine's model, so the program runs in PLCSIM by itself")}">${icon("vm")}<span>${PK.t("Machine simulator")}</span></button>
      <div class="gen-list-h">${PK.t("Made before")}</div>
      <ul>${items}</ul>
    </nav>`;
  }

  /** TIA's verdict as a short label and colour: "Not imported" when TIA could not open the project at all. */
  function reportState(r) {
    if (!r) return null;
    if (r.fileErrors.some((e) => e.file === "(import)")) return [PK.t("Not imported"), "bad"];
    if (r.fileErrors.length) return [PK.t("Files refused"), "bad"];
    if (!r.compileState) return [PK.t("Not compiled"), "warn"];
    return [r.compileState, r.errorCount ? "bad" : stateCls(r.compileState)];
  }
  /** What to do when TIA could not open the project (the same words as the job log). */
  const importHint = (e) => /already been opened|cannot be accessed|is locked|in use/i.test(e)
    ? PK.t("The project is open in TIA Portal, or a hidden TIA still holds it. Close it in TIA Portal on the TIA PC. If it is not open there: close the TIA helper window, run taskkill /im Siemens.Automation.Portal.exe /f, wait 2 minutes and start the TIA helper again. Then press Change it and leave the box empty to import this version again.")
    : /not found|does not exist|could not find/i.test(e) ? PK.t("The project was not found on the TIA PC. Check the TIA project path, then import again.")
    : PK.t("Check that TIA Portal and the TIA helper run on the TIA PC and the project opens there, then import again.");

  function reportHtml(r) {
    if (!r) return `<p class="m-0 muted small">${PK.t("Not imported into TIA yet.")}</p>`;
    const failed = r.fileErrors.find((e) => e.file === "(import)");
    if (failed) return `<div class="notice red">${icon("warn")}<div><b>${PK.t("TIA could not open the project, so nothing was imported or compiled.")}</b>
        <p class="mt-2 mb-0 small">${esc(importHint(failed.error))}</p>
        <details class="mt-2 more"><summary>${PK.t("What TIA said")}</summary><p class="mt-2 mb-0 small">${esc(failed.error)}</p></details></div></div>
      ${r.project ? `<p class="mt-2 mb-0 faint small">${PK.t("Project:")} <span class="mono">${esc(r.project)}</span></p>` : ""}`;
    const [label, cls] = reportState(r);
    return `<div class="kv">
        <span>${PK.t("Compile")}</span><b><span class="pill ${cls}">${esc(label)}</span> ${PK.t("{n} error|{n} errors", { n: r.errorCount })}, ${PK.t("{n} warning|{n} warnings", { n: r.warningCount })}</b>
        ${r.project ? `<span>${PK.t("Project")}</span><b class="mono">${esc(r.project)}</b>` : ""}
        ${r.download ? `<span>${PK.t("Download")}</span><b>${esc(PK.srv(r.download))}</b>` : ""}
      </div>
      ${r.fileErrors.length ? `<div class="mt-3 notice red">${icon("warn")}<div><b>${r.fileErrors.length === 1 ? PK.t("TIA refused a file") : PK.t("TIA refused {n} files", { n: r.fileErrors.length })}</b><ul class="mt-2 mb-0 small">${r.fileErrors.map((e) => `<li><b>${esc(e.file || "")}</b> ${esc(e.error || "")}</li>`).join("")}</ul></div></div>` : ""}
      ${r.messages.length ? `<details class="more"><summary>${PK.t("Compiler messages ({n})", { n: r.messages.length })}</summary><ul class="small">${r.messages.map((m) => `<li>${esc([m.state, m.path, m.description].filter(Boolean).join(" · "))}</li>`).join("")}</ul></details>` : ""}
      ${r.runtime.length ? `<div class="mt-3 notice gold">${icon("warn")}<div><b>${PK.t("While it ran in PLCSIM")}</b><ul class="mt-2 mb-0 small">${r.runtime.map((x) => `<li>${esc(x)}</li>`).join("")}</ul></div></div>` : ""}`;
  }

  /** "What Ki did": the blocks and tags a version made, changed or removed, in plain sentences, plus TIA's verdict. */
  function summaryHtml(v, title, withTia) {
    const s = v && v.summary;
    if (!s || !s.lines || !s.lines.length) return "";
    const r = v.report;
    const tia = withTia && r ? [r.fileErrors.some((e) => e.file === "(import)") ? PK.t("TIA could not open the project, so nothing was imported (see below).")
      : PK.t("TIA: {state}, {errors}, {warnings}.", { state: r.compileState || PK.t("not compiled"), errors: PK.t("{n} error|{n} errors", { n: r.errorCount }), warnings: PK.t("{n} warning|{n} warnings", { n: r.warningCount }) }),
      r.download ? PK.t("Download: {what}.", { what: PK.srv(r.download) }) : "", r.runtime && r.runtime.length ? PK.t("While it ran: {problems} (see {folder} below).", { problems: PK.t("{n} problem|{n} problems", { n: r.runtime.length }), folder: v.folder }) : ""].filter(Boolean) : [];
    return `<div class="gen-sum"><span class="eyebrow">${esc(title)}</span>
      <ul>${s.lines.map((l) => `<li data-k="${esc(l.kind)}">${esc(PK.srv(l.text))}</li>`).join("")}${tia.map((t) => `<li data-k="tia">${esc(t)}</li>`).join("")}</ul></div>`;
  }

  /** The job card: while it runs, and after it ends until hidden (older than a day: hidden by itself). */
  function jobHtml() {
    const j = g.job;
    if (!j || g.jobHidden === j.id) return "";
    if (j.state !== "running" && j.ended && Date.now() - new Date(j.ended).getTime() > 24 * 3600e3) return "";
    const pkg = j.outputs && j.outputs.package;
    const label = j.state === "running" ? PK.t("Running now") : PK.t("Last job");
    const open = pkg && pkg !== g.open ? `<button type="button" class="btn plain small" data-open="${esc(pkg)}">${PK.t("Open {name}", { name: esc(pkg) })}</button>` : "";
    return `<div class="gen-job"><span class="eyebrow">${label}</span>${PKSetup.jobCard(j, timeline(j) + jobSummary(j),
      `${open}<button type="button" class="btn gray small" data-hidejob>${PK.t("Hide")}</button>`)}</div>`;
  }

  /** The finished job's version: "What Ki did in v002", shown in the job card. */
  function jobSummary(j) {
    if (!j || j.state === "running" || !g.detail || !j.outputs || g.detail.name !== j.outputs.package) return "";
    const made = [...(j.log || "").matchAll(/Wrote (v\d{3})/g)].map((m) => m[1]).pop();
    const v = made && g.detail.versions.find((x) => x.folder === made);
    return v ? `<div class="mt-4">${summaryHtml(v, PK.t("What {ai} did in {folder}", { ai: AI, folder: v.folder }), true)}</div>` : "";
  }

  /** The versions of the picked package, newest first. */
  function versionsHtml(d) {
    const kindText = { new: PK.t("First version"), change: PK.t("Changed on request"), fix: PK.t("Fixed after TIA's report") };
    return `<div class="stack">${d.versions.map((v, i) => `<details class="card more gen-ver" ${i === 0 ? "open" : ""}>
        <summary><b>${esc(v.folder)}</b> <span class="muted">· ${esc(kindText[v.kind] || v.kind)} · ${PK.date(v.time)}</span>
          ${v.report ? (([l, c]) => `<span class="pill ${c}" style="margin-left:8px">${esc(l)}</span>`)(reportState(v.report)) : ""}</summary>
        <div class="grid two" style="margin-top:14px;align-items:start">
          <div class="stack">
            ${summaryHtml(v, PK.t("What {ai} did", { ai: AI }))}
            ${v.request ? `<div><span class="eyebrow">${PK.t("Asked for")}</span><p class="mt-1 mb-0">${esc(v.request)}</p></div>` : ""}
            <div><span class="eyebrow">${PK.t("In TIA")}</span><div class="mt-2">${reportHtml(v.report)}</div></div>
            ${v.problems && v.problems.length ? `<div class="notice gold">${icon("warn")}<div><b>${PK.t("The offline check still found")}</b><ul class="mt-2 mb-0 small">${v.problems.map((p) => `<li>${esc(p)}</li>`).join("")}</ul></div></div>` : ""}
            <div><span class="eyebrow">${PK.t("Files")}</span><div class="mt-2 row">${v.files.map((f) => `<button class="tag ${g.file && g.file.version === v.folder && g.file.file === f ? "on" : ""}" data-file="${esc(f)}" data-ver="${esc(v.folder)}">${esc(f)}${v.added.includes(f) ? ` · ${PK.t("new")}` : v.changed.includes(f) ? ` · ${PK.t("changed")}` : ""}</button>`).join("")}</div></div>
            ${v.replaces && v.replaces.length ? `<p class="m-0 faint small">${PK.t("May overwrite: {list}", { list: esc(v.replaces.join(", ")) })}</p>` : ""}
          </div>
          <div>${v.review ? `<span class="eyebrow">${PK.t("{ai}'s notes", { ai: AI })}</span><div class="mt-2 md">${PK.md(v.review)}</div>` : ""}</div>
        </div>
        ${g.file && g.file.version === v.folder ? `<div class="code" style="margin-top:14px;max-height:480px;overflow:auto"><pre style="margin:0;padding:14px 16px">${esc(g.file.text)}</pre></div>` : ""}
      </details>`).join("")}</div>`;
  }

  /** Right: the job (while it runs), then the form for new blocks, or the picked package with its versions and "Change it". */
  function workHtml() {
    return jobHtml() + packageHtml();
  }
  function packageHtml() {
    if (!g.open) return `<div class="gen-head"><div><span class="eyebrow">${PK.t("New package")}</span><h2>${PK.t("New blocks")}</h2>
        <p class="m-0 muted small">${PK.t("Describe the blocks in three steps. Each later change is saved as a new version; the old ones stay.")}</p></div></div>${formHtml()}`;
    const d = g.detail;
    const p = (g.packages || []).find((x) => x.name === g.open) || {};
    const meta = d ? [PK.t("{n} version|{n} versions", { n: d.versions.length }), d.plc ? PK.t("PLC {name}", { name: d.plc }) : "", d.context ? PK.t("fitted into {name}", { name: d.context.split(/[\\/]/).pop() }) : ""].filter(Boolean).join(" · ") : "";
    const tabs = PK.ui.tabs(PK.t("Package"), [["versions", PK.t("Versions")], ["change", PK.t("Change it")]].map(([k, l]) => ({ label: l, on: g.tab === k, data: { "data-tab": k } })));
    return `<div class="gen-head"><div class="min0"><span class="eyebrow">${PK.t("Package")}${p.state ? ` <span class="pill ${stateCls(p.state)}">${esc(PK.srv(p.state))}</span>` : ""}</span><h2>${esc(g.open)}</h2>
        <p class="m-0 muted small">${esc(meta)}</p></div>${tabs}</div>
      ${g.tab === "change" ? formHtml() : d ? versionsHtml(d) : `<div class="card">${PK.ui.loading("table", { rows: 3 })}</div>`}`;
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
      const tiaChip = t ? `<span class="pill ${t.ok ? "ok" : "warn"}"><span class="dot"></span>${t.ok ? (t.machine ? PK.t("TIA Portal ready on {machine}", { machine: esc(t.machine) }) : PK.t("TIA Portal ready")) : PK.t("TIA Portal not reachable")}</span>` : `<span class="pill">TIA Portal …</span>`;
      const chips = `<div class="row gen-status">${tiaChip}
          <span class="pill">PLCSIM ${t && t.plcsimIp ? esc(t.plcsimIp) : PK.t("not set")}</span>
          <span class="pill ${t && t.realAllowed && t.realAllowed.length ? "warn" : ""}">${PK.t("Real PLCs allowed: {count}", { count: t && t.realAllowed ? t.realAllowed.length : PK.t("none") })}</span></div>`;
      const tiaNote = t && !t.ok ? `<div class="mt-3 notice gold">${icon("info")}<div class="grow"><b>${PK.t("TIA Portal is not reachable.")}</b> ${esc(PK.srv(t.message || ""))}</div><a class="btn gold small" href="#/setup/connections">${PK.t("Connections")}</a></div>` : "";
      return PK.hero({ cls: "hero compact", title: PK.t("Block generator"),
          sub: PK.t("{ai}, PLC-Ki's block writer, turns plain words into PLC blocks. TIA Portal compiles them, and problems go back to {ai} until it is clean. Nothing reaches a real PLC without two people confirming.", { ai: AI }),
          extra: chips + tiaNote })
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
      const sim = page.querySelector("[data-sim]");
      if (sim) sim.onclick = async () => {
        read();
        try {
          const r = await api("GET", "/api/admin/generator/simulator");
          newPackage();
          Object.assign(f, { name: r.name, request: r.request, edit: r.edit || "", useContext: true, download: "plcsim", watch: 60 });
          PK.draw();
          toast(PK.srv(r.note));
          setTimeout(() => { const el = document.querySelector("#g-request"); if (el) { el.rows = 14; el.focus(); } }, 0);
        } catch (e) { toast(e.message, "error"); }
      };
      page.querySelectorAll("[data-tab]").forEach((b) => b.onclick = () => { read(); g.tab = b.dataset.tab; PK.draw(); });
      const req = v("request"), sug = page.querySelector("[data-suggest]"), nm = v("name");
      if (req && sug) PK.on(req, "input", () => { const n = nameFrom(req.value); sug.textContent = n; if (nm) nm.placeholder = n; });
      const rl = page.querySelector("[data-runlabel]");
      if (req && rl && g.open && !(g.job && g.job.state === "running")) PK.on(req, "input", () => { rl.innerHTML = runLabel(req.value); });
      page.querySelectorAll("[data-file]").forEach((b) => b.onclick = (e) => { e.preventDefault(); showFile(b.dataset.ver, b.dataset.file); });
      page.querySelectorAll("[data-hidejob]").forEach((b) => b.onclick = () => { g.jobHidden = g.job && g.job.id; PK.draw(); });
      const reopen = page.querySelector("[data-reopen]");
      if (reopen) reopen.onclick = () => { g.sheetFor = null; waitingSheets(g.job); };
      const run = page.querySelector("[data-run]");
      if (run) run.onclick = async () => {
        read();
        const name = g.open || f.name.trim() || nameFrom(f.request);
        if (f.download === "real" && !(await PK.confirmSheet({ title: PK.t("Plan a real-PLC download?"),
          text: PK.t("PLC-Ki still asks you to type the version after it compiles, and the TIA PC still asks for the PLC address. Nothing is downloaded before both."), ok: PK.t("Start"), danger: true }))) return;
        try {
          const job = await api("POST", "/api/admin/generator/run", {
            package: name, request: f.request.trim() || null, project: f.project.trim(), plc: f.plc.trim() || null, edit: f.edit.trim() || null,
            useContext: f.useContext, sclOnly: f.sclOnly, download: f.download, plcAddress: f.plcAddress.trim() || null, watch: f.watch, rounds: f.rounds, effort: f.effort,
          });
          g.open = name; f.name = name; g.tab = "versions";
          followJob(job);
          PK.draw();
          setTimeout(() => { const el = document.querySelector(".gen-job"); if (el) window.scrollTo({ top: el.getBoundingClientRect().top + scrollY - 70, behavior: "smooth" }); }, 0);
        } catch (e) { toast(e.message, "error"); }
      };
    },
  });
})();
