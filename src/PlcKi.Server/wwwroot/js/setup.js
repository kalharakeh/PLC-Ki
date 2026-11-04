// Setup pages (administrators): Connections, Program import, Settings. Long work (TIA export, building the
// model with Claude) runs as a job on the server; the page shows its progress and log.
"use strict";

const PKSetup = (() => {
  const { $, esc, icon, api, state, toast } = PK;

  // ---------- settings (shared by Connections and Settings) ----------
  let settings = null, info = null, lists = null;
  async function loadSettings() {
    const d = await api("GET", "/api/admin/settings");
    settings = d.settings; info = d.info; lists = d;
    return d;
  }
  async function saveSettings(changes) {
    const d = await api("PUT", "/api/admin/settings", { ...settings, plcPassword: null, apiKey: null, tiaToken: null, ...changes });
    settings = d.settings; info = d.info;
    return d;
  }

  /** Saved settings that differ from what this PLC-Ki is running with (for example changed by another copy). */
  const RUN_LABELS = { source: "Values from", address: "PLC address", plcVia: "PLC read", model: "Machine model", export: "Program export", port: "Port", tiaMode: "TIA", tiaUrl: "TIA PC" };
  const short = (v) => (v === "" || v == null ? "not set" : String(v).split(/[\\/]/).pop());
  function runDiff() {
    const r = info && info.running;
    if (!r || !settings) return [];
    return Object.keys(RUN_LABELS).filter((k) => String(r[k] ?? "") !== String(settings[k] ?? "") && !(k === "port" && settings.port === r.port))
      .map((k) => `${RUN_LABELS[k]}: running ${short(r[k])}, saved ${short(settings[k])}`);
  }
  /** "Running now: …" under a field whose saved value is not the one in use. */
  function runNote(key) {
    const r = info && info.running;
    return r && settings && String(r[key] ?? "") !== String(settings[key] ?? "") ? `<span class="hint run-now">Running now: ${esc(short(r[key]))}</span>` : "";
  }

  function restartBar(el) {
    if (!info || !info.restartNeeded) return "";
    const diff = runDiff();
    return `<div class="notice gold" style="align-items:center">${icon("info")}<div style="flex:1"><b>Saved, not in use yet.</b> PLC-Ki runs with the settings it started with and uses the saved ones after a restart.
      ${diff.length ? `<br><span class="small">${diff.map(esc).join(" · ")}</span>` : ""}
      ${info.canRestart ? "" : " Stop PLC-Ki (Ctrl+C in its window) and start it again."}</div>
      ${info.canRestart ? `<button class="btn gold small" data-restart>Apply now</button>` : ""}</div>`;
  }

  async function restart() {
    try { await api("POST", "/api/admin/restart"); } catch (e) { toast(e.message, "error"); return; }
    const s = PK.sheet(`<h2>Restarting PLC-Ki…</h2><p class="muted">The page comes back by itself in a few seconds.</p><div class="progress indet"><div></div></div>`, { dismiss: false });
    await new Promise((r) => setTimeout(r, 1500));
    for (let i = 0; i < 60; i++) {
      try { const r = await fetch("/api/auth/state", { cache: "no-store" }); if (r.ok) { location.reload(); return; } } catch { /* still down */ }
      await new Promise((r) => setTimeout(r, 1000));
    }
    s.close();
    toast("PLC-Ki did not come back. Check its window.");
  }

  function bindRestart(page) {
    page.querySelectorAll("[data-restart]").forEach((b) => b.onclick = restart);
  }

  // ---------- jobs ----------
  /** Follows a job: polls its log and state, calls onUpdate(job) until it ends. */
  function follow(job, onUpdate) {
    let stopped = false;
    let log = job.log || "";
    async function tick() {
      if (stopped) return;
      try {
        const j = await api("GET", `/api/admin/jobs/${job.id}?from=${log.length}`);
        log += j.log;
        j.log = log;
        Object.assign(job, j);
        onUpdate(job);
        if (j.state !== "running") return;
      } catch { /* retry */ }
      setTimeout(tick, 1200);
    }
    tick();
    return () => { stopped = true; };
  }

  const stateText = { running: ["Running", "info"], done: ["Finished", "ok"], problems: ["Finished with problems", "warn"], failed: ["Failed", "bad"], stopped: ["Stopped", "bad"] };
  /** Why a job failed, in plain words, from its log: { text, todo }. */
  function jobProblem(job) {
    if (job.state === "stopped") return { text: "You stopped this job.", todo: "Start it again when you are ready. What was done so far is kept." };
    const log = job.log || "";
    const has = (re) => re.test(log);
    if (has(/Siemens TIA Openness|not a member of|EngineeringSecurityException|UnauthorizedAccess/i))
      return { text: "Your Windows user on the TIA PC may not use TIA Openness.", todo: "On the VM: Computer Management, Local Users and Groups, Groups, \"Siemens TIA Openness\": add your user. Then log off and on again, and start the job again." };
    if (has(/whitelist|allow access|access was denied by the user|firewall.*openness/i))
      return { text: "TIA Portal did not allow TiaImporter to use it.", todo: "On the VM: double-click Update-TIA-helper in C:\\PLCKi\\PLC-Ki\\tools\\TiaImporter once (it approves TiaImporter in TIA), or answer \"Yes to all\" when TIA asks." };
    if (has(/refused the token|\b401\b.*(helper|5090|vm)|(helper|5090|vm).*\b401\b/i))
      return PK.plain("refused the token");
    if (has(/No answer from .*:\d+|Cannot reach the TIA helper|Link is not connected|connection refused.*5090|actively refused/i))
      return PK.plain("TIA helper timeout");
    if (has(/newer version|older version|upgrade the project|cannot be opened with this version|not compatible with/i))
      return { text: "The archive was made with another TIA Portal version.", todo: "PLC-Ki uses TIA Portal V20. Open the project in TIA V20 (it offers to upgrade it), archive it again, and import that." };
    if (has(/licen[cs]e[^\n]*(no valid|missing|expired|not found|fail)|(no valid|missing|expired)[^\n]*licen[cs]e/i))
      return { text: "TIA Portal has no valid license on the TIA PC.", todo: "Check the STEP 7 Professional license in the Automation License Manager on the VM." };
    if (has(/credit balance|billing|insufficient.*(credit|funds)/i))
      return { text: "The Claude account has no credit left.", todo: "Add credit at console.anthropic.com (Billing), then start the job again." };
    if (has(/authentication_error|invalid x-api-key|ANTHROPIC_API_KEY|no (claude )?api key/i))
      return PK.plain("api key");
    if (has(/overloaded|rate_limit|\b529\b|\b429\b/i))
      return { text: "Claude is busy right now.", todo: "Wait a minute and start the job again." };
    if (has(/out of memory|OutOfMemory|0xc000012d|not enough memory/i))
      return { text: "The TIA PC ran out of memory.", todo: "Close programs you don't need on the VM, or give the VM more memory (16 GB works well with TIA and PLCSIM)." };
    if (has(/know-how|protected block/i) && job.state === "problems")
      return { text: "Some blocks are know-how protected.", todo: "PLC-Ki cannot read protected blocks. Remove the protection in a copy of the project if those blocks matter." };
    if (has(/fail-safe data in Openness/i))
      return { text: "It compiled, but this PLC has a safety program, and Siemens does not allow downloading it through a tool.", todo: "On the TIA PC: open the project in TIA Portal, select the PLC, Download to device (software, changes only), then close TIA again." };
    if (has(/API usage limits|usage limit/i))
      return { text: "Your Claude API account reached its spending limit.", todo: "Raise the limit in the Anthropic Console (Settings, Limits), or wait until the date in the log. Importing again without a request still works." };
    if (has(/PLCSIM|download/i) && has(/fail|error|refused|not reachable/i) && /download|PLCSIM/i.test(job.title || ""))
      return { text: "The download to PLCSIM did not work.", todo: "On the VM: check PLCSIM Advanced runs the virtual PLC (green), and its address is the PLCSIM address in Setup. Then start the job again." };
    if (has(/compil\w*[^\n]*\b[1-9]\d* error/i) || has(/\berrors?: *[1-9]/i))
      return { text: "TIA found compile errors.", todo: "Open the log below for the lines TIA complained about. More fixing rounds (More options) let Ki fix them by itself." };
    if (has(/already been opened by user|project\/library .* cannot be accessed/i))
      return { text: "TIA could not open the project: it is still open or locked on the TIA PC.", todo: "Close the project in TIA Portal on the TIA PC. If it is not open there, a hidden TIA is holding it: close the TIA helper window, run taskkill /im Siemens.Automation.Portal.exe /f on the TIA PC, wait 2 minutes, start the TIA helper again, then start the job again." };
    // Stack frames ("at X.Y()") say nothing to a person: skip them, and cut "Unhandled exception. Type:" off the message
    const lines = log.split("\n").map((l) => l.replace(/^\s*\|\s?/, "").trim()).filter((l) => l && !l.startsWith(">") && !/^at |^--- End of/.test(l))
      .map((l) => l.replace(/^(Unhandled exception\.\s*)?(ERROR:\s*)?([\w.]+(Exception|Error):\s*)?/, ""));
    const last = lines.reverse().find((l) => /error|fail|cannot|could not|not found|refused|problem|answered/i.test(l)) || lines[0] || "";
    const p = PK.plain(last);
    return { text: p.todo ? p.text : job.state === "problems" ? "It finished, but with problems." : "The job stopped with an error.", todo: p.todo || (last ? "The log says: " + last.slice(0, 240) : "Open the log below.") };
  }

  /** How far a job is: { pct, phase, hint } from its log, or null when it cannot tell (then the bar just moves). */
  function jobProgress(job) {
    const log = job.log || "";
    const at = (re) => re.test(log);
    const secs = job.started ? (Date.now() - new Date(job.started).getTime()) / 1000 : 0;
    if (job.kind === "export") {
      let pct = 2, phase = "Sending the archive to the TIA PC";
      if (at(/TIA opens the archive/)) { pct = 10; phase = "Waiting for TIA on the TIA PC"; }
      if (at(/Starting TIA Portal/)) { pct = 15; phase = "Starting TIA Portal in the background"; }
      if (at(/Retrieving /)) { pct = 25; phase = "Opening the archive in TIA (the slowest part)"; }
      if (at(/PLC [^\n]+ \/ /)) { pct = 40; phase = "Exporting the PLC program"; }
      const m = [...log.matchAll(/blocks (\d+)\/(\d+) \((\d+)%\)/g)].pop();
      if (m) { pct = 40 + Math.round(Number(m[3]) * 0.45); phase = `Exporting blocks: ${m[1]} of ${m[2]} done`; }
      if (at(/types and tag tables/)) { pct = Math.max(pct, 86); phase = "Exporting data types and tag tables"; }
      if (at(/Done\. \d+ PLC/)) { pct = 92; phase = "Copying the export back to this PC"; }
      if (at(/Export saved/)) { pct = 100; phase = "Done"; }
      // between two log lines, creep forward a little so it never looks frozen
      return { pct, phase, hint: "Usually 2 to 10 minutes, depending on the program size." };
    }
    if (job.kind === "import") {
      // Time grows with the program: about 5 minutes for the demo (80 kB of program text), 20 for a 650 kB plant
      const kb = Number((log.match(/Program digest: (\d+) kB/) || [])[1] || 100);
      const est = 180 + kb * 1.6;
      const written = Number(([...log.matchAll(/written (\d+) kB of the model/g)].pop() || [])[1] || 0);
      const pct = Math.min(95, Math.round(5 + secs / est * 85));
      const phase = at(/Compared with|Wrote /) ? "Checking the model"
        : at(/Correcting \d+ problem/) ? "Claude is fixing what the check found"
        : written > 0 ? `Claude is writing the model (${written} kB so far)`
        : at(/reading the program/) ? "Claude is reading the program and thinking" : "Reading the program export";
      const mins = Math.max(3, Math.round(est / 60));
      return { pct, phase, hint: `About ${mins} minutes for a program this size; it can take longer. While Claude writes, the kB count keeps growing.` };
    }
    if (job.kind === "develop") {
      // The generator's own markers (##plcki stage ...) say where it is; between them, creep forward a little
      const [stage, arg] = (job.stage || "connect").split(" ");
      const since = (from, to, est) => Math.min(to, Math.round(from + secs / est * (to - from)));
      const steps = {
        connect: [3, "Connecting to the TIA PC"],
        generate: [since(3, 40, 240), `${PK.AI} is writing the blocks`],
        fix: [40, `${PK.AI} is fixing what TIA found (round ${arg || 1})`],
        import: [55, `TIA is importing and compiling ${arg || "the blocks"}`],
        download: [75, `Downloading ${arg || "the blocks"} to ${/real PLC/.test(job.title || "") ? "the real PLC" : "PLCSIM"}`],
        watch: [85, `Watching the PLC run for ${arg || "a few"} s (read-only)`],
        done: [100, "Done"],
      };
      const [pct, phase] = steps[stage] || [10, `${PK.AI} is working`];
      const hint = stage === "connect" && secs > 20
        ? "The TIA helper on the TIA PC has not answered yet. Check that its window is open and not busy with another job; after about 100 s PLC-Ki gives up and says why."
        : /^Import /.test(job.title || "") ? "TIA imports and compiles the latest version as it is; what TIA finds goes back to " + PK.AI + " for another round."
        : `${PK.AI} writes the blocks, TIA imports and compiles them, and what TIA finds goes back to ${PK.AI} for another round. Usually 3 to 10 minutes.`;
      return { pct, phase, hint };
    }
    return null;
  }

  function progressHtml(job, lastLine) {
    const secs = job.started ? Math.max(0, Math.round((Date.now() - new Date(job.started).getTime()) / 1000)) : 0;
    const time = `${Math.floor(secs / 60)}:${String(secs % 60).padStart(2, "0")} so far`;
    const p = jobProgress(job);
    const line = esc(lastLine.replace(/^\s*\|\s?/, "").slice(0, 200));
    if (!p) return `<div class="jobprog"><div class="top"><span class="phase">Working…</span><span class="time">${time}</span></div>
      <div class="progress indet"><div></div></div>${line ? `<p class="latest" title="${line}"><span>Latest</span>${line}</p>` : ""}</div>`;
    return `<div class="jobprog"><div class="top"><span class="pct">${p.pct}%</span><span class="phase">${esc(p.phase)}</span><span class="time">${time}</span></div>
      <div class="progress"><div style="width:${p.pct}%"></div></div>
      <p class="muted small" style="margin:0">${esc(p.hint)}</p>
      ${/blocks \d+\//.test(line) || !line ? "" : `<p class="latest" title="${line}"><span>Latest</span>${line}</p>`}</div>`;
  }

  /** A job: title, state, progress while it runs, the problem in plain words when it fails, and the log. actions: buttons for a finished job. */
  function jobCard(job, extra = "", actions = "") {
    const [t, cls] = stateText[job.state] || [job.state, "info"];
    const lines = (job.log || "").split("\n").filter((l) => l.trim() && !l.startsWith("> "));
    const lastLine = lines.slice(-1)[0] || "Starting…";
    return `<div class="card job">
      <div class="row"><b style="font-size:16px">${esc(job.title)}</b><span class="pill ${cls}"><span class="dot"></span>${t}</span><span class="spacer"></span>
        ${job.state === "running" ? `<button class="btn red-tinted small" data-stop="${esc(job.id)}">${icon("stop")}Stop</button>` : actions}</div>
      ${extra}
      ${job.state === "running" ? progressHtml(job, lastLine) : ""}
      ${["failed", "problems", "stopped"].includes(job.state) ? (() => { const p = jobProblem(job); return `<div class="notice ${job.state === "failed" ? "red" : "gold"}" style="margin-top:12px">${icon("warn")}<div><b>${esc(p.text)}</b>${p.todo ? `<br>${esc(p.todo)}` : ""}</div></div>`; })() : ""}
      <details class="more" ${job.state === "failed" ? "open" : ""}><summary>Log</summary>
        <div class="joblog" data-log>${esc(job.log || "")}</div></details>
    </div>`;
  }
  function bindJob(page) {
    page.querySelectorAll("[data-stop]").forEach((b) => b.onclick = async () => {
      if (await PK.confirmSheet({ title: "Stop this job?", text: "What was done so far is kept. A TIA import that is already running on the TIA PC finishes there.", ok: "Stop", danger: true }))
        api("POST", `/api/admin/jobs/${b.dataset.stop}/stop`).catch((e) => toast(e.message, "error"));
    });
    page.querySelectorAll("[data-log]").forEach((l) => { l.scrollTop = l.scrollHeight; });
  }

  // ---------- Connections ----------
  let tiaStatus = null;
  let plcTest = null;
  let linkStatus = null, linkKey = null, linkTimer = null;
  let conn = null;      // edited copy

  const presets = {
    same: { title: "Same PC as TIA", text: "PLC-Ki, TIA Portal and the cable to the PLC are on this PC.", icon: "pc", tiaMode: "local", plcVia: "direct" },
    vm: { title: "PC with a TIA VM", text: "PLC-Ki runs on this PC; TIA runs in a VM (or another PC) next to it.", icon: "vm", tiaMode: "remote", plcVia: "direct" },
    server: { title: "Server + my laptop", text: "PLC-Ki runs on a server. Your laptop has TIA and reaches the PLC.", icon: "server", tiaMode: "link", plcVia: "link" },
  };
  const presetOf = (c) => c.tiaMode === "local" ? "same" : c.tiaMode === "link" || c.plcVia === "link" ? "server" : "vm";

  /** The setup as a clean schematic: boxes for the computers, lines for the links, green / red once checked. */
  function diagram(c) {
    const tiaOk = tiaStatus && !tiaStatus.notSet ? tiaStatus.ok : null;
    const plcState = state.status && state.status.connection.state;
    const plcOk = plcState === "connected" ? true : plcState === "disconnected" ? false : null;
    const col = (v) => (v === true ? "var(--ok)" : v === false ? "var(--fault)" : "var(--muted)");
    const box = (x, y, w, h, title, lines, strong) => `<g>
        <rect x="${x}" y="${y}" width="${w}" height="${h}" rx="16" fill="var(--panel)" stroke="${strong ? "var(--accent)" : "var(--line-strong)"}" stroke-width="${strong ? 2 : 1.2}"/>
        <text x="${x + 18}" y="${y + 30}" font-size="15" font-weight="600" fill="var(--ink)">${esc(title)}</text>
        ${lines.map((l, i) => `<text x="${x + 18}" y="${y + 52 + i * 19}" font-size="12.5" fill="var(--muted)" ${l.mono ? 'font-family="ui-monospace,Menlo,Consolas,monospace"' : ""}>${esc(l.t)}</text>`).join("")}</g>`;
    const wire = (x1, y1, x2, y2, ok, label, dash) => {
      const mx = (x1 + x2) / 2, my = (y1 + y2) / 2;
      return `<g><line x1="${x1}" y1="${y1}" x2="${x2}" y2="${y2}" stroke="${col(ok)}" stroke-width="2.5" ${ok == null || dash ? 'stroke-dasharray="6 6"' : ""} stroke-linecap="round"/>
        <circle cx="${x1}" cy="${y1}" r="4" fill="${col(ok)}"/><circle cx="${x2}" cy="${y2}" r="4" fill="${col(ok)}"/>
        <rect x="${mx - label.length * 3.4 - 10}" y="${my - 13}" width="${label.length * 6.8 + 20}" height="24" rx="12" fill="var(--bg-alt)"/>
        <text x="${mx}" y="${my + 4}" font-size="12" text-anchor="middle" fill="var(--muted)">${esc(label)}</text></g>`;
    };
    const plc = (x, y) => box(x, y, 210, 92, "PLC", [{ t: c.address || "address not set", mono: true }, { t: plcOk ? "read live" : plcOk === false ? "no answer" : "S7-1500 / S7-1200" }]);
    let svg;
    if (c.tiaMode === "local") {
      svg = `<rect x="30" y="30" width="430" height="200" rx="22" fill="none" stroke="var(--line-strong)" stroke-dasharray="4 6"/>
        <text x="50" y="56" font-size="12.5" fill="var(--muted)">This PC</text>
        ${box(50, 76, 180, 120, "PLC-Ki", [{ t: location.host, mono: true }, { t: "web app" }], true)}
        ${box(260, 76, 180, 120, "TIA Portal", [{ t: "+ TIA helper" }, { t: tiaOk ? "started" : "on this PC" }])}
        ${wire(230, 136, 260, 136, tiaOk, "")}
        ${wire(460, 130, 720, 130, plcOk, "Ethernet · S7, read-only")}${plc(720, 84)}`;
    } else if (c.tiaMode === "remote") {
      svg = `${box(40, 74, 220, 112, "PLC-Ki", [{ t: location.host, mono: true }, { t: "this PC" }], true)}
        ${box(700, 20, 230, 100, "TIA Portal", [{ t: (c.tiaUrl || "address not set").replace(/^https?:\/\//, ""), mono: true }, { t: "VM or PC with the TIA helper" }])}
        ${wire(260, 110, 700, 70, tiaOk, "TIA helper, port 5090")}
        ${wire(260, 150, 700, 190, plcOk, "S7, read-only")}${plc(700, 144)}`;
    } else {
      svg = `${box(20, 74, 200, 112, "PLC-Ki", [{ t: "on the server", mono: false }, { t: location.host, mono: true }], true)}
        ${box(410, 64, 230, 132, "Your laptop", [{ t: linkStatus && linkStatus.connected ? "PLC-Ki Link · " + (linkStatus.machine || "") : "PLC-Ki Link" }, { t: linkStatus && linkStatus.tia ? "TIA Portal, helper ready" : "TIA Portal" }, { t: "your browser" }])}
        ${wire(410, 130, 220, 130, linkStatus ? linkStatus.connected : null, "connects out", !(linkStatus && linkStatus.connected))}
        ${wire(640, 130, 740, 130, linkStatus && linkStatus.plc ? linkStatus.plc.connected : null, "S7")}${plc(740, 84)}`;
    }
    const st = (v) => v === true ? ["var(--ok)", "working"] : v === false ? ["var(--fault)", "no answer"] : ["var(--muted)", "not checked yet"];
    const rows = c.tiaMode === "link" || c.plcVia === "link"
      ? [["PLC-Ki › your laptop (Link)", linkStatus ? linkStatus.connected : null], ["Laptop › PLC " + (c.address || ""), linkStatus && linkStatus.plc ? linkStatus.plc.connected : null]]
      : [["PLC-Ki › TIA helper" + (c.tiaMode === "remote" ? " on the VM" : ""), tiaOk], ["PLC-Ki › PLC " + (c.address || ""), plcOk]];
    const list = `<ul class="linklist">${rows.map(([t, v]) => `<li><span class="sw" style="background:${st(v)[0]}"></span>${esc(t)}<small>${st(v)[1]}</small></li>`).join("")}</ul>`;
    return list + `<div class="schematic"><svg viewBox="0 0 960 250" role="img" aria-label="How PLC-Ki, TIA Portal and the PLC are connected" font-family="inherit">${svg}</svg></div>
      <div class="legend"><span><span class="sw" style="background:var(--ok)"></span>checked, working</span><span><span class="sw" style="background:var(--fault)"></span>no answer</span><span><span class="sw" style="background:var(--muted)"></span>not checked yet</span></div>`;
  }

  const setupNav = (here) => `<div class="subnav">${[["start", "Get started"], ["connections", "Connections"], ["program", "Program"], ["settings", "Settings"]]
    .map(([k, l]) => `<a class="btn ${k === here ? "" : "b"} small" href="#/setup/${k}">${l}</a>`).join("")}</div>`;

  function choice(name, current, options) {
    return `<div class="choice" role="group">${options.map((o) => `<button type="button" data-${name}="${o.value}" class="${current === o.value ? "on" : ""}" aria-pressed="${current === o.value}" aria-label="${esc(o.label + ". " + o.text)}" ${o.disabled ? "disabled" : ""}>
      <span class="ico">${icon(o.icon)}</span><b>${esc(o.label)}</b><small>${esc(o.text)}</small>${o.soon ? `<span class="tag gold soon">Next update</span>` : ""}</button>`).join("")}</div>`;
  }

  function connectionsHtml() {
    const c = conn;
    const preset = c.setupChosen ? presetOf(c) : null;   // a fresh install has not picked one yet
    const tiaFields = c.tiaMode === "local" ? `
        <div class="grid two">
          <label class="field">TiaImporter.exe on this PC<input id="tiaImporterPath" value="${esc(c.tiaImporterPath)}" placeholder="C:\\PLCKi\\PLC-Ki\\tools\\TiaImporter\\bin\\Release\\net48\\TiaImporter.exe"><span class="hint">PLC-Ki starts it in its own window and stops it when PLC-Ki stops.</span></label>
          <label class="field">TIA project for generated blocks<input id="tiaProject" value="${esc(c.tiaProject)}" placeholder="C:\\Users\\you\\Documents\\Automation\\Line1\\Line1.ap20"><span class="hint">Can also be chosen on the Block generator page.</span></label>
          <label class="field">PLCSIM address (downloads for testing)<input id="tiaPlcsimIp" value="${esc(c.tiaPlcsimIp)}" placeholder="e.g. 192.168.0.10, empty = no PLCSIM downloads"></label>
          <label class="field">Real PLCs that may be loaded<input id="tiaAllowReal" value="${esc(c.tiaAllowReal)}" placeholder="empty = never"><span class="hint">Each download is still confirmed by typing the address in the TiaImporter window, with the CPU in STOP.</span></label>
        </div>
        <div class="row" style="margin-top:12px"><button class="btn b small" data-start-helper>${tiaStatus && tiaStatus.helperRunning ? "Restart" : "Start"} the TIA helper</button></div>`
      : c.tiaMode === "remote" ? `
        <div class="grid two">
          <label class="field">TIA PC address<input id="tiaUrl" value="${esc(c.tiaUrl)}" placeholder="http://192.168.0.20:5090"></label>
          <label class="field">Token <span class="hint">${info.tiaToken === "set" ? "Saved. Leave empty to keep it." : "Shown in the PLC-Ki TIA helper window on the VM."}</span><input id="tiaToken" type="password" autocomplete="new-password" placeholder="${info.tiaToken === "set" ? "••••••••" : "at least 8 characters"}"></label>
          <label class="field">TIA project for generated blocks<input id="tiaProject" value="${esc(c.tiaProject)}" placeholder="path on the TIA PC, e.g. C:\\Users\\Software\\Documents\\Automation\\…\\.ap20"></label>
        </div>
        <details class="more" ${tiaStatus && !tiaStatus.ok ? "open" : ""}><summary>How to set up the TIA helper on the VM</summary>
          <ol class="howto" style="margin-top:12px">
            <li><b>Once, put the newest PLC-Ki on the VM</b> in <code>C:\\PLCKi\\PLC-Ki</code>: copy the PLC-Ki folder from this PC. If it was downloaded there with git, this line in Command Prompt updates it:${PK.cmd("On the VM", "cd /d C:\\PLCKi\\PLC-Ki && git pull")}</li>
            <li><b>Once, double-click <code>Setup-TIA-helper</code></b> in <code>C:\\PLCKi\\PLC-Ki\\tools\\TiaImporter</code>. It asks for the token and the PLCSIM address,
              opens the port, and starts the helper.</li>
            <li><b>Every day: nothing.</b> The helper starts by itself when you log into the VM, in a window called <b>PLC-Ki TIA helper</b>.
              If that window was closed, double-click <code>Start-TIA-helper</code> in the same folder. After a PLC-Ki update, double-click <code>Update-TIA-helper</code>.</li>
          </ol>
        </details>`
      : `<div class="grid two">
          <label class="field">TIA project for generated blocks<input id="tiaProject" value="${esc(c.tiaProject)}" placeholder="path on the laptop, e.g. C:\\Users\\you\\Documents\\Automation\\…\\.ap20"><span class="hint">Can also be chosen on the Block generator page.</span></label>
        </div>
        <p class="muted small" style="margin:0">PLC-Ki Link starts the TIA helper on the laptop (see PLC-Ki Link below). PLCSIM and real-PLC downloads are allowed there, on the laptop's command.</p>`;

    const tiaPlain = tiaStatus && !tiaStatus.ok ? PK.plain(tiaStatus.message) : null;
    const tiaMsg = !tiaStatus ? `<span class="pill">Checking…</span>`
      : tiaStatus.notSet ? `<span class="pill"><span class="dot"></span>Not set yet</span><span class="muted small" style="flex-basis:100%">${esc(tiaStatus.message)} Only needed to import a program or for the block generator.</span>`
      : `<span class="pill ${tiaStatus.ok ? "ok" : "bad"}"><span class="dot"></span>${esc(tiaStatus.ok ? tiaStatus.message || "Reachable" : tiaPlain.text)}</span>
         ${tiaPlain && tiaPlain.todo ? `<span class="muted small" style="flex-basis:100%">${esc(tiaPlain.todo)}</span>` : ""}
         ${tiaStatus.ok ? `<span class="faint small">PLCSIM downloads: ${tiaStatus.plcsimIp ? esc(tiaStatus.plcsimIp) : "off"} · real PLC downloads: ${tiaStatus.realAllowed ? "allowed (confirmed at the TIA PC)" : "off"}</span>` : ""}`;

    return PK.sec("hero compact", `${setupNav("connections")}
        <h1>Connections</h1>
        <p class="sub">Where PLC-Ki finds the PLC (live values) and TIA Portal (imports and the block generator). Pick the setup that matches yours.
          New here? <a href="#/setup/start">Get started</a> walks you through it.</p>
        ${restartBar()}
        ${choice("preset", preset, Object.entries(presets).map(([k, p]) => ({ value: k, label: p.title, text: p.text, icon: p.icon, })))}
        <div style="margin-top:22px">${diagram(c)}</div>`)

      + PK.sec("alt", `<div class="row" style="align-items:baseline"><h2>The PLC</h2><span class="pill warn">Read-only</span></div>
        <p class="sub">PLC-Ki reads values with the S7 protocol TIA and HMIs use. It never writes to the PLC.</p>
        <div class="card stack">
        ${choice("plcvia", c.plcVia, [
          { value: "direct", label: "From here", text: "PLC-Ki reads the PLC over the network.", icon: "pc" },
          { value: "link", label: "Through my laptop", text: "PLC-Ki Link on the laptop reads it and sends the values here.", icon: "laptop" },
        ]).replace('class="choice"', 'class="choice two"')}
        <div class="grid two">
          <label class="field">PLC address<input id="address" value="${esc(c.address)}" placeholder="e.g. 192.168.0.10" inputmode="decimal">${runNote("address")}</label>
          ${c.plcVia === "link" ? `<p class="muted small" style="align-self:end;margin:0 0 10px">The laptop reads this address. If the CPU has access protection, its password stays on the laptop (<code>--plc-password</code>).</p>`
            : `<label class="field">Password <span class="hint">${info.plcPassword === "settings" ? "saved" : info.plcPassword === "environment" ? "from PLCKI_PLC_PASSWORD" : "only if the CPU has access protection"}</span><input id="plcPassword" type="password" autocomplete="new-password"></label>`}
        </div>
        ${c.plcVia === "link" ? "" : `<div class="row"><button class="btn b small" data-test-plc>Test the address</button>
          ${plcTest ? `<span class="pill ${plcTest.ok ? "ok" : "bad"}"><span class="dot"></span>${esc(plcTest.ok ? plcTest.message : PK.plain(plcTest.message, { plc: conn.address }).text)}</span>` : ""}</div>
          ${plcTest && !plcTest.ok && PK.plain(plcTest.message).todo ? `<p class="muted small" style="margin:0">${esc(PK.plain(plcTest.message, { plc: conn.address }).todo)}</p>` : ""}`}
        <details class="more" style="margin-top:0"><summary>More PLC options</summary>
          <div class="grid three" style="margin-top:12px">
            <label class="field">Security<select id="security">${["Auto", "Tls", "LegacyChallenge"].map((x) => `<option ${c.security === x ? "selected" : ""}>${x}</option>`).join("")}</select></label>
            <label class="field">Read the PLC every <span class="hint">milliseconds; 1000 = once a second</span><input id="pollMs" type="number" min="100" max="60000" step="100" value="${c.pollMs}"></label>
            <label class="field">Wait for an answer up to <span class="hint">milliseconds; 5000 = 5 seconds</span><input id="timeoutMs" type="number" min="500" max="60000" step="500" value="${c.timeoutMs}"></label>
            <label class="field">Values from<select id="source"><option value="s7" ${c.source === "s7" ? "selected" : ""}>Live PLC</option><option value="snapshot" ${c.source === "snapshot" ? "selected" : ""}>Test file (no PLC)</option></select></label>
            <label class="field">Test file<select id="snapshotFile">${(lists.snapshots || []).map((f) => `<option value="${esc(f.path)}" ${f.path === c.snapshotFile ? "selected" : ""}>${esc(f.label)}</option>`).join("")}</select></label>
          </div>
        </details></div>`)

      + PK.sec("", `<h2>TIA Portal</h2>
        <p class="sub">Used to import a program from an archive and by the block generator. A small helper, TiaImporter, does the TIA work next to TIA.</p>
        <div class="card stack">
        ${choice("tiamode", c.tiaMode, [
          { value: "local", label: "This PC", text: "TIA Portal is installed here.", icon: "pc" },
          { value: "remote", label: "Another PC or VM", text: "A PC or VM next to this one runs TIA and the helper.", icon: "vm" },
          { value: "link", label: "My laptop", text: "Through PLC-Ki Link.", icon: "laptop" },
        ])}
        <div>${tiaFields}</div>
        <div class="row" style="align-items:center">${tiaMsg}<button class="btn gray small" data-check-tia>Check again</button></div>
        </div>
        ${c.plcVia === "link" || c.tiaMode === "link" ? "" : saveRow}`)
      + (c.plcVia === "link" || c.tiaMode === "link" ? linkHtml(c) : "");
  }

  const saveRow = `<div class="actions" style="margin-top:28px"><button class="btn big" data-save>Save</button><span class="muted small" id="save-msg"></span></div>`;

  /** PLC-Ki Link: the laptop that reaches the PLC and has TIA connects out to this server. */
  function linkHtml(c) {
    const l = linkStatus;
    const ago = (t) => { if (!t) return "never"; const s = Math.round((Date.now() - new Date(t)) / 1000); return s < 5 ? "just now" : s < 120 ? s + " s ago" : Math.round(s / 60) + " min ago"; };
    const state = !l ? `<span class="pill">Checking…</span>`
      : l.connected ? `<span class="pill ok"><span class="dot"></span>Connected from ${esc(l.machine || "the laptop")}</span>`
      : `<span class="pill bad"><span class="dot"></span>${l.hasKey ? "Not connected" + (l.lastSeen ? ", last seen " + ago(l.lastSeen) : "") : "No key yet"}</span>`;
    const plc = l && l.plc ? (l.plc.connected ? `<span class="pill ok"><span class="dot"></span>Reading PLC ${esc(l.plc.address || "")}</span>`
      : `<span class="pill bad"><span class="dot"></span>PLC: ${esc(l.plc.error || "no answer")}</span>`) : "";
    const tia = l && l.connected ? (l.tia ? `<span class="pill ok"><span class="dot"></span>TIA helper ready</span>`
      : `<span class="pill ${c.tiaMode === "link" ? "bad" : ""}"><span class="dot"></span>${esc(l.tiaMessage || "No TIA helper")}</span>`) : "";
    const key = linkKey || "<the key>";
    const server = location.origin;
    const cmd = `cd /d %USERPROFILE%\\Downloads\\PLC-Ki && dotnet run --project src\\PlcKi.Server -- link --server ${server} --key ${key}${c.tiaMode === "link" ? ` --tia "C:\\PLCKi\\PLC-Ki\\tools\\TiaImporter\\bin\\Release\\net48\\TiaImporter.exe"` : ""}`;
    return PK.sec("dark", `<span class="eyebrow">PLC-Ki Link</span><h2 style="margin-top:6px">Your laptop, connected out.</h2>
      <p class="sub">A small program on the laptop that reaches the PLC${c.tiaMode === "link" ? " and has TIA" : ""}. It only connects out to this server, so no firewall or VPN changes on the plant side.
        It reads the PLC read-only${c.tiaMode === "link" ? " and carries the TIA work to the laptop's TIA" : ""}.</p>
      <div class="row" style="margin-top:6px">${state}${plc}${tia}</div>
      <div class="steps col" style="margin-top:26px">
        <div class="step ${l && l.hasKey ? "done" : "now"}"><span class="n">1</span><div><b>Save this setup, then make a key</b>
          <p class="small" style="margin:6px 0 10px;opacity:.8">The key lets the laptop in. It is shown once; a new key replaces the old one.</p>
          <div class="row">${saveRow.replace('style="margin-top:28px"', "")}<button class="btn white small" data-link-key>${l && l.hasKey ? "Make a new key" : "Make a key"}</button>
          ${linkKey ? `<span class="pill ok"><span class="dot"></span>New key below. Copy it now.</span>` : l && l.hasKey ? `<span class="small" style="opacity:.7">A key exists (made ${esc(PK.date(l.keyCreated))}).</span>` : ""}</div></div></div>
        <div class="step ${l && l.connected ? "done" : l && l.hasKey ? "now" : ""}"><span class="n">2</span><div><b>On the laptop, start Link</b>
          <p class="small" style="margin:6px 0 10px;opacity:.8">In a Command Prompt, in the PLC-Ki folder (a clone of the repository, as on your PC). Leave the window open${c.tiaMode === "link" ? "; it opens the TIA helper's window too" : ""}.</p>
          ${PK.cmd("On the laptop", cmd)}
          <div class="row" style="margin-top:10px">
            ${c.tiaMode === "link" ? `<span class="small" style="opacity:.8">Add <code>--plcsim-ip &lt;address&gt;</code> for PLCSIM tests, <code>--allow-real &lt;address&gt;</code> only if blocks may go to a real PLC.</span>` : ""}</div></div></div>
        <div class="step ${l && l.connected ? "done" : ""}"><span class="n">3</span><div><b>Back here, it turns green</b>
          <p class="small" style="margin:6px 0 0;opacity:.8">The status above shows the laptop, the PLC and TIA within a few seconds.</p></div></div>
      </div>`);
  }

  function readConn(page) {
    if (!conn) return;      // still loading
    const v = (id) => { const el = page.querySelector("#" + id); return el ? el.value : undefined; };
    for (const k of ["address", "security", "source", "snapshotFile", "tiaUrl", "tiaImporterPath", "tiaPlcsimIp", "tiaAllowReal", "tiaProject"]) {
      const x = v(k); if (x !== undefined) conn[k] = x.trim();
    }
    for (const k of ["pollMs", "timeoutMs"]) { const x = v(k); if (x !== undefined) conn[k] = Number(x); }
    conn._plcPassword = v("plcPassword") || conn._plcPassword || null;
    conn._tiaToken = v("tiaToken") || conn._tiaToken || null;
  }

  /** Shows the "Unsaved changes" bar while the form differs from what is saved. */
  function trackConn() {
    if (!conn || !settings) return;
    const a = JSON.stringify({ ...conn, _plcPassword: 0, _tiaToken: 0 }), b = JSON.stringify({ ...settings, _plcPassword: 0, _tiaToken: 0 });
    const changed = a !== b || !!conn._plcPassword || !!conn._tiaToken;
    if (changed !== PK.isDirty) PK.dirty(changed ? () => { const s = PK.$("page").querySelector("[data-save]"); if (s) s.click(); } : null);
  }

  async function checkTia() {
    tiaStatus = null; PK.draw();
    try { tiaStatus = await api("GET", "/api/admin/tia/status"); } catch (e) { tiaStatus = { ok: false, message: e.message }; }
    PK.draw();
  }

  async function loadLink() {
    try { linkStatus = await api("GET", "/api/admin/link/"); } catch { /* next time */ }
  }

  PK.view("setup/connections", {
    live: true,      // the diagram follows the PLC state
    leave() { clearInterval(linkTimer); linkTimer = null; },
    async mount() {
      conn = null; tiaStatus = null; plcTest = null; linkKey = null;
      clearInterval(linkTimer);
      linkTimer = setInterval(async () => { if (conn && (conn.plcVia === "link" || conn.tiaMode === "link")) { const was = linkStatus && linkStatus.connected; await loadLink(); PK.draw(); if (linkStatus && linkStatus.connected && !was) checkTia(); } }, 3000);
      loadLink();
      try { await loadSettings(); conn = { ...settings }; } catch (e) { PK.$("page").innerHTML = `<div class="notice red">${esc(e.message)}</div>`; return; }
      PK.draw();
      checkTia();
    },
    render() { return conn ? connectionsHtml() : PK.hero({ title: "Loading…" }); },
    after(page) {
      bindRestart(page);
      const keep = () => { readConn(page); trackConn(); };
      page.querySelectorAll("input,select").forEach((el) => { PK.on(el, "change", keep); PK.on(el, "input", keep); });
      trackConn();
      page.querySelectorAll("[data-preset]").forEach((b) => b.onclick = () => { keep(); const p = presets[b.dataset.preset]; conn.tiaMode = p.tiaMode; conn.plcVia = p.plcVia; PK.draw(); });
      page.querySelectorAll("[data-tiamode]").forEach((b) => b.onclick = () => { keep(); conn.tiaMode = b.dataset.tiamode; PK.draw(); });
      page.querySelectorAll("[data-plcvia]").forEach((b) => b.onclick = () => { keep(); conn.plcVia = b.dataset.plcvia; PK.draw(); });
      const t = page.querySelector("[data-test-plc]");
      if (t) t.onclick = async () => { keep(); plcTest = { ok: false, message: "Testing…" }; PK.draw(); try { plcTest = await api("POST", "/api/admin/test-plc", { address: conn.address }); } catch (e) { plcTest = { ok: false, message: e.message }; } PK.draw(); };
      const lk = page.querySelector("[data-link-key]");
      if (lk) lk.onclick = async () => {
        if (linkStatus && linkStatus.hasKey && !(await PK.confirmSheet({ title: "Make a new key?", text: "The laptop's Link stops working until it is started with the new key.", ok: "Make a new key" }))) return;
        try { linkKey = (await api("POST", "/api/admin/link/key")).key; await loadLink(); PK.draw(); } catch (e) { toast(e.message, "error"); }
      };
      const c = page.querySelector("[data-check-tia]");
      if (c) c.onclick = checkTia;
      const h = page.querySelector("[data-start-helper]");
      if (h) h.onclick = async () => {
        if (JSON.stringify({ ...conn, _plcPassword: 0, _tiaToken: 0 }) !== JSON.stringify({ ...settings, _plcPassword: 0, _tiaToken: 0 })) { toast("Save first, then start the helper."); return; }
        h.disabled = true;
        try { tiaStatus = await api("POST", "/api/admin/tia/start-helper"); } catch (e) { tiaStatus = { ok: false, message: e.message }; }
        PK.draw();
      };
      const s = page.querySelector("[data-save]");
      if (s) s.onclick = async () => {
        keep();
        s.disabled = true;
        try {
          const { _plcPassword, _tiaToken, ...rest } = conn;
          await saveSettings({ ...rest, setupChosen: true, plcPassword: _plcPassword, tiaToken: _tiaToken });
          conn = { ...settings };
          PK.dirty(null);
          toast(info.restartNeeded ? "Saved. Apply to use the new connections." : "Saved.");
          PK.draw();
          checkTia();
        } catch (e) { toast(e.message, "error"); }
        s.disabled = false;
      };
    },
  });

  // ---------- Program import ----------
  const imp = { files: null, chosen: null, exportInfo: null, plc: "", effort: "high", compare: true, job: null, unfollow: null, model: null, modelInfo: null };
  // "Refresh from TIA": the project on the TIA PC, read again as it is now
  const fresh = { projects: null, error: null, project: "", program: null };

  async function loadRefresh() {
    try { fresh.program = await api("GET", "/api/program"); } catch { fresh.program = null; }
    try {
      const d = await loadSettings();
      const p = await api("GET", "/api/admin/tia/projects");
      fresh.projects = p.projects || []; fresh.error = null;
      if (!fresh.project) {
        // the project the shown program came from, else the one the block generator used last
        const name = fresh.program && fresh.program.available ? (fresh.program.project || "").toLowerCase() : "";
        const same = fresh.projects.find((x) => x.name.toLowerCase() === name);
        fresh.project = same ? same.path : (d.settings.tiaProject || (fresh.projects[0] || {}).path || "");
      }
    } catch (e) { fresh.projects = null; fresh.error = e.message; }
    PK.draw();
  }

  function refreshHtml() {
    const pr = fresh.program && fresh.program.available ? fresh.program : null;
    const running = imp.job && imp.job.state === "running";
    const list = fresh.projects || [];
    const known = list.some((x) => x.path === fresh.project);
    const opts = (known || !fresh.project ? [] : [{ path: fresh.project, name: fresh.project.split(/[\\/]/).pop() }]).concat(list);
    return `<div class="card stack" style="margin-bottom:22px">
      <div class="row"><span class="ico">${icon("import")}</span><h3 style="margin:0;flex:1">Changed the program in TIA? Refresh it</h3></div>
      <p class="muted" style="margin:0">PLC-Ki shows the code, tags and addresses of the program it last read${pr ? `: <b>${esc(pr.project)}</b>${pr.exportedAt ? `, read ${PK.date(pr.exportedAt)}` : ""}` : ""}.
        After changes in TIA (new addresses, edited blocks), read the project again. About a minute; no Claude, no cost. Alarms and steps stay as they are.
        If TIA Portal has the project open, it is read from there (TIA asks once to allow access).</p>
      ${fresh.error ? `<div class="notice gold">${icon("warn")}<div>Cannot list the TIA projects: ${esc(fresh.error)} Check Connections.</div></div>`
        : `<div class="row"><label class="field" style="flex:1;margin:0">TIA project<select id="fresh-project">${opts.map((x) => `<option value="${esc(x.path)}" ${x.path === fresh.project ? "selected" : ""}>${esc(x.name)}  ·  ${esc(x.path)}</option>`).join("")}</select></label>
          <button class="btn" data-refresh ${running || !fresh.project ? "disabled" : ""}>Refresh from TIA</button></div>`}
      ${imp.job && imp.job.kind === "refresh" ? jobCard(imp.job) : ""}
    </div>`;
  }

  async function refreshFiles() {
    imp.files = await api("GET", "/api/admin/import/files");
    PK.draw();
  }

  async function choose(kind, path) {
    imp.chosen = { kind, path, name: path.split(/[\\/]/).pop() };
    imp.exportInfo = null; imp.model = null; imp.modelInfo = null;
    PK.draw();
    if (kind === "export") {
      try { imp.exportInfo = await api("GET", "/api/admin/import/export-info?file=" + encodeURIComponent(path)); imp.plc = imp.exportInfo.plcs.length === 1 ? imp.exportInfo.plcs[0].name : ""; }
      catch (e) { toast(e.message, "error"); }
      PK.draw();
    }
  }

  async function upload(file) {
    const s = PK.sheet(`<h2>Uploading ${esc(file.name)}</h2><div class="progress"><div style="width:0%" id="up-bar"></div></div><p class="muted small" id="up-text">0 %</p>`, { dismiss: false });
    try {
      const res = await new Promise((resolve, reject) => {
        const x = new XMLHttpRequest();
        x.open("PUT", "/api/admin/import/upload/" + encodeURIComponent(file.name));
        x.upload.onprogress = (e) => { if (e.lengthComputable) { const p = Math.round(e.loaded / e.total * 100); $("up-bar").style.width = p + "%"; $("up-text").textContent = p + " %"; } };
        x.onload = () => { let d = null; try { d = JSON.parse(x.responseText); } catch { /* */ } x.status < 300 ? resolve(d) : reject(new Error((d && d.error) || "Upload failed (" + x.status + ").")); };
        x.onerror = () => reject(new Error("Upload failed."));
        x.send(file);
      });
      s.close();
      await refreshFiles();
      await choose(res.kind, res.file);
    } catch (e) { s.close(); toast(e.message, "error"); }
  }

  function startJob(job, after) {
    const fresh = !imp.job || imp.job.id !== job.id;
    imp.job = job;
    if (fresh) setTimeout(() => { const c = document.querySelector(".step .card.job"); if (c) c.scrollIntoView({ behavior: "smooth", block: "center" }); }, 80);
    if (imp.unfollow) imp.unfollow();
    imp.unfollow = follow(job, async (j) => {
      PK.draw();
      if (j.state !== "running" && after) { const a = after; after = null; await a(j); }
    });
    PK.draw();
  }

  function programHtml() {
    const f = imp.files;
    const cur = f ? f.current : {};
    const ch = imp.chosen;
    const isArchive = ch && ch.kind === "archive";
    const jobRunning = imp.job && imp.job.state === "running";
    const stepCls = (n) => {
      const at = !ch ? 1 : isArchive ? 2 : imp.model ? 4 : 3;
      return n < at ? "done" : n === at ? "now" : "";
    };
    const fileRow = (kind) => (x) => `<li class="click ${ch && ch.path === x.path ? "sel" : ""}" data-choose="${kind}" data-path="${esc(x.path)}">
        <span class="ico">${icon(kind === "archive" ? "file" : "import")}</span>
        <span style="flex:1"><b style="font-weight:600">${esc(x.name)}</b><div class="faint">${(x.size / 1048576).toFixed(1)} MB · ${PK.date(x.time)}</div></span><span class="chev">›</span></li>`;
    const earlier = f && (f.archives.length || f.exports.length) ? `<details class="more" ${ch ? "" : "open"}><summary>Earlier files (${f.archives.length + f.exports.length})</summary>
        <ul class="list" style="margin-top:10px">${f.exports.map(fileRow("export")).join("")}${f.archives.map(fileRow("archive")).join("")}</ul></details>` : "";

    const exp = imp.exportInfo;
    const plcPick = exp && exp.plcs.length ? `<div class="tiles" style="margin:0 0 14px">${exp.plcs.map((p) => `<div class="tile pick ${imp.plc === p.name ? "on" : ""}" data-plc="${esc(p.name)}">
        <div class="label">${esc(p.cpu || "PLC")}</div><div class="value">${esc(p.name)}</div><div class="faint">${p.blocks} blocks · ${p.tags} tags · ${(p.languages || []).join(", ")}</div></div>`).join("")}</div>` : "";

    const mi = imp.modelInfo;
    const review = mi ? (mi.ok ? `<div class="kpis">${[["Tags", mi.tags], ["Steps", mi.steps], ["Devices", mi.equipment], ["Alarms", mi.alarms]]
        .map(([l, v]) => `<div class="k"><b>${v}</b><span>${l}</span></div>`).join("")}</div>
        <p style="margin:12px 0 0"><b>${esc(mi.name)}</b>${mi.description ? " · " + esc(mi.description) : ""}</p>
        ${imp.job && imp.job.state === "problems" ? `<div class="notice gold" style="margin-top:10px">${icon("warn")}<div>Claude left some problems (see the log). PLC-Ki will not start with this model until they are fixed. Build it again, or ask for help with the log.</div></div>` : ""}
        <div class="row" style="margin-top:14px"><button class="btn" data-use ${imp.job && imp.job.state === "problems" ? "disabled" : ""}>Use this program</button></div>`
      : `<div class="notice red">${icon("warn")}<div>${esc(mi.error)}</div></div>`) : "";

    const step = (n, title, text, body) => `<div class="step ${stepCls(n)}"><div class="n">${stepCls(n) === "done" ? "✓" : n}</div><h3>${title}</h3><p style="margin-bottom:14px">${text}</p>${body}</div>`;
    return PK.sec("hero compact", `${setupNav("program")}
        <h1>Import a program</h1>
        <p class="sub">Give PLC-Ki the TIA archive. It reads every block, and Claude writes the plain-English model of steps, devices, alarms, causes and fixes, once.</p>
        ${restartBar()}
        <div class="kpis" style="margin-top:30px"><div class="k"><b>${esc(state.info ? state.info.machine : "")}</b><span>watched now</span></div>
          <div class="k"><b>${state.model ? state.model.alarms.length : "–"}</b><span>alarms explained</span></div>
          <div class="k"><b>${state.model ? state.model.equipment.length : "–"}</b><span>devices</span></div>
          <div class="k"><b style="font-size:16px;font-weight:600;word-break:break-all">${esc((cur.export || "none").split(/[\\/]/).pop())}</b><span>program file</span></div></div>`)
      + PK.sec("alt", `${PKModel.html()}${refreshHtml()}<div class="steps col">
        ${step(1, "Choose the program", "A TIA archive (.zap20), or a program export (.zip) made earlier.", `
          <div class="dropzone" id="drop">${icon("import", "")}<div style="font-weight:600;margin-top:6px">Drop the file here, or click to choose it</div>
            <input type="file" id="file" accept=".zap20,.zap19,.zap18,.zap17,.zap16,.zip" class="hidden"></div>
          ${ch ? `<p style="margin:12px 0 0">Chosen: <b>${esc(ch.name)}</b> <span class="tag ${isArchive ? "gold" : "blue"}">${isArchive ? "TIA archive" : "program export"}</span></p>` : ""}
          ${earlier}`)}
        ${step(2, "Export it with TIA Portal", "Only for archives. TIA opens it in the background on the TIA PC (see Connections) and exports every block. The archive is not changed.",
          (isArchive ? `<button class="btn" data-export ${jobRunning ? "disabled" : ""}>Export with TIA</button>` : `<span class="faint">${ch ? "Not needed: this is already an export." : "Waiting for a file."}</span>`)
          + (imp.job && imp.job.kind === "export" ? jobCard(imp.job) : ""))}
        ${step(3, "Build the machine model", "Claude reads the program and writes what each step, device and alarm means. A few minutes, usually one to a few dollars of API use.",
          ch && !isArchive ? `${plcPick}
            <div class="row"><span class="small muted">Effort</span><div class="segmented">${["medium", "high", "max"].map((e) => `<button class="${imp.effort === e ? "on" : ""}" data-effort="${e}">${e}</button>`).join("")}</div>
              <label class="check small"><input type="checkbox" id="compare" ${imp.compare ? "checked" : ""}>Compare with the current model</label></div>
            <div class="row" style="margin-top:14px"><button class="btn" data-build ${jobRunning || (exp && exp.plcs.length > 1 && !imp.plc) ? "disabled" : ""}>Build the model</button>
              ${exp && exp.plcs.length > 1 && !imp.plc ? `<span class="faint">Choose the PLC first.</span>` : ""}</div>` + (imp.job && imp.job.kind === "import" ? jobCard(imp.job) : "") : `<span class="faint">Needs a program export.</span>`)}
        ${step(4, "Check it and use it", "What Claude found. Use it, and PLC-Ki restarts with this machine.", review || `<span class="faint">The result shows here.</span>`)}
        </div>
        ${imp.job && !["export", "import", "refresh"].includes(imp.job.kind) ? `<div style="margin-top:22px">${jobCard(imp.job)}</div>` : ""}`);
  }

  PK.view("setup/program", {
    live: false,
    async mount() {
      await refreshFiles().catch((e) => toast(e.message, "error"));
      loadRefresh();
      PKModel.start();
      const run = (state.jobs || []).find((j) => j.state === "running" && ["export", "import", "refresh"].includes(j.kind));
      if (run && !imp.job) startJob(run);
      PK.draw();
    },
    leave() { PKModel.stop(); if (imp.unfollow) { imp.unfollow(); imp.unfollow = null; } },
    render() { return imp.files ? programHtml() : PK.hero({ title: "Loading…" }); },
    after(page) {
      bindRestart(page);
      PKModel.bind(page);
      bindJob(page);
      const drop = page.querySelector("#drop"), file = page.querySelector("#file");
      drop.onclick = () => file.click();
      file.onchange = () => file.files[0] && upload(file.files[0]);
      drop.ondragover = (e) => { e.preventDefault(); drop.classList.add("over"); };
      drop.ondragleave = () => drop.classList.remove("over");
      drop.ondrop = (e) => { e.preventDefault(); drop.classList.remove("over"); if (e.dataTransfer.files[0]) upload(e.dataTransfer.files[0]); };
      page.querySelectorAll("[data-choose]").forEach((li) => li.onclick = () => choose(li.dataset.choose, li.dataset.path));
      page.querySelectorAll("[data-plc]").forEach((t) => t.onclick = () => { imp.plc = t.dataset.plc; PK.draw(); });
      page.querySelectorAll("[data-effort]").forEach((b) => b.onclick = () => { imp.effort = b.dataset.effort; PK.draw(); });
      const cmp = page.querySelector("#compare");
      if (cmp) cmp.onchange = () => { imp.compare = cmp.checked; };
      const fp = page.querySelector("#fresh-project");
      if (fp) fp.onchange = () => { fresh.project = fp.value; };
      const rf = page.querySelector("[data-refresh]");
      if (rf) rf.onclick = async () => {
        try {
          const job = await api("POST", "/api/admin/import/refresh", { project: fresh.project });
          startJob(job, async (j) => {
            if (j.state !== "done") return;
            state.program = null; state.programError = null;   // Code, Logic and Tags load the new program
            window.dispatchEvent(new Event("pk:program-changed"));
            await refreshFiles(); await loadRefresh();
            toast("Refreshed. Code, Logic and Tags show the program as it is in TIA now.");
          });
        } catch (e) { toast(e.message, "error"); }
      };
      const ex = page.querySelector("[data-export]");
      if (ex) ex.onclick = async () => {
        try {
          const job = await api("POST", "/api/admin/import/export", { archive: imp.chosen.path });
          startJob(job, async (j) => { if (j.state === "done") { await refreshFiles(); await choose("export", j.outputs.export); toast("Exported. Now build the model."); } });
        } catch (e) { toast(e.message, "error"); }
      };
      const bd = page.querySelector("[data-build]");
      if (bd) bd.onclick = async () => {
        try {
          const job = await api("POST", "/api/admin/import/build", { export: imp.chosen.path, plc: imp.plc || null, effort: imp.effort, compare: imp.compare });
          startJob(job, async (j) => {
            imp.model = j.outputs.model;
            try { imp.modelInfo = await api("GET", "/api/admin/import/model-info?file=" + encodeURIComponent(imp.model)); } catch (e) { imp.modelInfo = { ok: false, error: e.message }; }
            PK.draw();
          });
        } catch (e) { toast(e.message, "error"); }
      };
      const use = page.querySelector("[data-use]");
      if (use) use.onclick = async () => {
        try {
          await api("POST", "/api/admin/import/use", { model: imp.model, export: imp.chosen.path });
          info = (await loadSettings()).info;
          toast("Saved. Apply to start watching this program.");
          PK.draw();
        } catch (e) { toast(e.message, "error"); }
      };
    },
  });

  // ---------- Settings ----------
  let users = null;
  PK.view("setup/settings", {
    async mount() {
      try { await loadSettings(); users = await api("GET", "/api/admin/users"); } catch (e) { toast(e.message, "error"); }
      PK.draw();
    },
    render() {
      if (!settings) return PK.hero({ title: "Loading…" });
      const s = settings;
      const keyText = { settings: "A key is saved here.", environment: "A key is already set on this PC (outside PLC-Ki). A key entered here replaces it.", none: "No key yet: the Ask box stays off until you add one." }[info.apiKey];
      const sw = (id, on, title, text) => `<div class="row"><div style="flex:1"><b style="font-weight:600">${title}</b><div class="muted small">${text}</div></div><label class="switch"><input type="checkbox" id="${id}" ${on ? "checked" : ""}><span></span></label></div>`;
      return PK.sec("hero compact", `${setupNav("settings")}<h1>Settings</h1><p class="sub">The machine model, the assistant, and who can open PLC-Ki from where.</p>${restartBar()}`)
        + PK.sec("alt", `<div class="grid two" data-settings-form>
          <div class="card stack"><h3>Machine</h3>
            <label class="field">Machine model<select id="model">${(lists.models || []).map((f) => `<option value="${esc(f.path)}" ${f.path === s.model ? "selected" : ""}>${esc(f.label)}${f.uploaded ? " (imported)" : ""}</option>`).join("")}</select><span class="hint">New models come from the Program page. Its program export is picked with it.</span>${runNote("model")}</label>
            <label class="field">History kept (minutes)<input id="historyMinutes" type="number" value="${s.historyMinutes}"><span class="hint">For questions like "what happened before the alarm?"</span></label></div>
          <div class="card stack"><h3>Assistant</h3>
            ${sw("askEnabled", s.askEnabled, "Ask box", "Claude answers questions about the machine.")}
            <label class="field">Claude API key <span class="hint">${esc(keyText)}</span><input id="apiKey" type="password" autocomplete="new-password" placeholder="sk-ant-…"></label>
            <label class="field">How hard Claude thinks<select id="askEffort">${["low", "medium", "high", "max"].map((x) => `<option ${s.askEffort === x ? "selected" : ""}>${x}</option>`).join("")}</select></label></div>
          <div class="card stack"><h3>Access</h3>
            ${sw("allowRemote", s.allowRemote, "Other computers may open PLC-Ki", "Off: only this PC. On: anyone on the network who has a login.")}
            ${sw("publicView", s.publicView, "Viewing without login", "For a screen on the line. Setup still needs an administrator.")}
            <label class="field" style="max-width:200px">Port<input id="port" type="number" value="${s.port}">${runNote("port")}</label></div>
        </div>
        <div class="actions" style="margin-top:24px"><button class="btn big" data-save-settings>Save</button><span class="muted small">Saves Machine, Assistant and Access. Logins below save on their own.</span></div>`)
        + PK.sec("", `<h2>Logins</h2><p class="sub">Operators see every machine page. Administrators also see Setup.</p>
          <ul class="list logins">${(users || []).map((u) => `<li><span class="ico">${icon("user")}</span>
            <span style="flex:1"><b style="font-weight:600">${esc(u.name)}</b>${state.user && u.name === state.user.name ? " (you)" : ""}<div class="faint">${u.role === "admin" ? "Administrator" : "Operator"}</div></span>
            <span class="li-acts">${state.user && u.name !== state.user.name ? `<button class="btn plain small" data-role="${esc(u.name)}" data-to="${u.role === "admin" ? "operator" : "admin"}">Make ${u.role === "admin" ? "operator" : "administrator"}</button>
              <button class="btn plain small" data-pw="${esc(u.name)}">New password</button><button class="btn plain small" style="color:var(--fault)" data-del="${esc(u.name)}">Delete</button>` : `<button class="btn plain small" data-pw-own>Change my password</button>`}</span></li>`).join("")}</ul>
          <h3 style="margin-top:26px">Add a login</h3>
          <div class="grid three"><label class="field">Name<input id="nu-name" autocomplete="off"></label>
            <label class="field">Password <span class="hint">at least 8 characters</span><input id="nu-pw" type="password" autocomplete="new-password"></label>
            <label class="field">Role<select id="nu-role"><option value="operator">Operator: sees the machine pages</option><option value="admin">Administrator: also Setup</option></select></label></div>
          <div class="row end" style="margin-top:10px"><button class="btn" data-add-user>Add login</button></div>
          <p class="faint" style="margin:16px 0 0">Data folder: ${esc(info.dataDir)}</p>`);
    },
    after(page) {
      bindRestart(page);
      const v = (id) => page.querySelector("#" + id);
      const saveBtn = page.querySelector("[data-save-settings]");
      const form = page.querySelector("[data-settings-form]");
      const mark = () => PK.dirty(() => saveBtn.click());
      form.querySelectorAll("input,select").forEach((el) => { PK.on(el, "input", mark); PK.on(el, "change", mark); });
      saveBtn.onclick = async () => {
        saveBtn.disabled = true;
        try {
          await saveSettings({ model: v("model").value, historyMinutes: Number(v("historyMinutes").value), askEnabled: v("askEnabled").checked,
            apiKey: v("apiKey").value.trim() || null, askEffort: v("askEffort").value, allowRemote: v("allowRemote").checked, publicView: v("publicView").checked, port: Number(v("port").value) });
          PK.dirty(null);
          toast(info.restartNeeded ? "Saved. Apply to use the new settings." : "Saved."); PK.draw();
        } catch (e) { toast(e.message, "error"); }
        saveBtn.disabled = false;
      };
      const act = async (fn, ok) => { try { await fn(); toast(ok); users = await api("GET", "/api/admin/users"); PK.draw(); } catch (e) { toast(e.message, "error"); } };
      page.querySelectorAll("[data-role]").forEach((b) => b.onclick = async () => {
        const toAdmin = b.dataset.to === "admin";
        if (await PK.confirmSheet({ title: `Make ${b.dataset.role} ${toAdmin ? "an administrator" : "an operator"}?`,
          text: toAdmin ? "Administrators can open Setup: change the connections, the program, the settings and the logins." : "Operators see every machine page but cannot open Setup.",
          ok: toAdmin ? "Make administrator" : "Make operator", danger: toAdmin }))
          act(() => api("PUT", `/api/admin/users/${encodeURIComponent(b.dataset.role)}/role`, { role: b.dataset.to }), "Role changed.");
      });
      const own = page.querySelector("[data-pw-own]");
      if (own) own.onclick = () => PK.passwordSheet();
      page.querySelectorAll("[data-del]").forEach((b) => b.onclick = async () => {
        if (await PK.confirmSheet({ title: `Delete ${b.dataset.del}?`, text: "They can no longer log in.", ok: "Delete", danger: true }))
          act(() => api("DELETE", `/api/admin/users/${encodeURIComponent(b.dataset.del)}`), "Deleted.");
      });
      page.querySelectorAll("[data-pw]").forEach((b) => b.onclick = () => PK.passwordSheet(b.dataset.pw));
      page.querySelector("[data-add-user]").onclick = () => act(() => api("POST", "/api/admin/users", { name: v("nu-name").value, password: v("nu-pw").value, role: v("nu-role").value }), "Login added.");
    },
  });

  return { runDiff, setupNav, follow, jobCard, jobProblem, bindJob, restartBar, bindRestart, loadSettings, saveSettings, restart, presets, presetOf,
    get settings() { return settings; }, get info() { return info; } };
})();
