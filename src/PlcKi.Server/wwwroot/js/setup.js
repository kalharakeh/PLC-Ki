// Setup pages (administrators): Connections, Program import, Settings. Long work (TIA export, building the
// model with Claude) runs as a job on the server; the page shows its progress and log.
"use strict";

const PKSetup = (() => {
  // The three roles (review 55): [role, what it may do, short text for the role list]. The server enforces the same (Roles.cs).
  const ROLES = [
    ["operator", PK.T("Sees every machine page and writes alarm notes. Only reads the PLC."), PK.T("sees the machine pages")],
    ["technician", PK.T("Also adds documents and changes or holds values on PLCSIM. Cannot open Setup or change a real PLC."), PK.T("also documents and PLCSIM values")],
    ["admin", PK.T("Also opens Setup: connections, program, settings and logins, and may change allowed values on a real PLC."), PK.T("also Setup")],
  ];
  const ROLE_PLURAL = { operator: PK.T("Operators"), technician: PK.T("Technicians"), admin: PK.T("Administrators") };
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
  const RUN_LABELS = { source: PK.T("Values from"), address: PK.T("PLC address"), plcVia: PK.T("PLC read"), model: PK.T("Machine model"), export: PK.T("Program export"), port: PK.T("Port"), tiaMode: PK.T("TIA"), tiaUrl: PK.T("TIA PC"),
    username: PK.T("PLC user name"), askModel: PK.T("Ask model"), askMaxTokens: PK.T("Answer length"), askMaxToolRounds: PK.T("Lookups per question") };
  const short = (v) => (v === "" || v == null ? PK.t("not set") : String(v).split(/[\\/]/).pop());
  function runDiff() {
    const r = info && info.running;
    if (!r || !settings) return [];
    return Object.keys(RUN_LABELS).filter((k) => String(r[k] ?? "") !== String(settings[k] ?? "") && !(k === "port" && settings.port === r.port))
      .map((k) => PK.t("{label}: running {run}, saved {saved}", { label: PK.t(RUN_LABELS[k]), run: short(r[k]), saved: short(settings[k]) }));
  }
  /** "Running now: …" under a field whose saved value is not the one in use. */
  function runNote(key) {
    const r = info && info.running;
    return r && settings && String(r[key] ?? "") !== String(settings[key] ?? "") ? `<span class="hint run-now">${PK.t("Running now: {value}", { value: esc(short(r[key])) })}</span>` : "";
  }

  function restartBar(el) {
    if (!info || !info.restartNeeded) return "";
    const diff = runDiff();
    return `<div class="notice gold" style="align-items:center">${icon("info")}<div class="grow"><b>${PK.t("Saved, not in use yet.")}</b> ${PK.t("PLC-Ki runs with the settings it started with and uses the saved ones after a restart.")}
      ${diff.length ? `<br><span class="small">${diff.map(esc).join(" · ")}</span>` : ""}
      ${info.canRestart ? "" : ` ${PK.t("Stop PLC-Ki (Ctrl+C in its window) and start it again.")}`}</div>
      ${info.canRestart ? `<button class="btn gold small" data-restart>${PK.t("Apply now")}</button>` : ""}</div>`;
  }

  async function restart() {
    try { await api("POST", "/api/admin/restart"); } catch (e) { toast(e.message, "error"); return; }
    const s = PK.sheet(`<h2>${PK.t("Restarting PLC-Ki…")}</h2><p class="muted">${PK.t("The page comes back by itself in a few seconds.")}</p><div class="progress indet"><div></div></div>`, { dismiss: false });
    await new Promise((r) => setTimeout(r, 1500));
    for (let i = 0; i < 60; i++) {
      try { const r = await fetch("/api/auth/state", { cache: "no-store" }); if (r.ok) { location.reload(); return; } } catch { /* still down */ }
      await new Promise((r) => setTimeout(r, 1000));
    }
    s.close();
    toast(PK.t("PLC-Ki did not come back. Check its window."));
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

  const stateText = { running: [PK.T("Running"), "info"], done: [PK.T("Finished"), "ok"], problems: [PK.T("Finished with problems"), "warn"], failed: [PK.T("Failed"), "bad"], stopped: [PK.T("Stopped"), "bad"] };
  /** Why a job failed, in plain words, from its log: { text, todo }. */
  function jobProblem(job) {
    if (job.state === "stopped") return { text: PK.t("You stopped this job."), todo: PK.t("Start it again when you are ready. What was done so far is kept.") };
    const log = job.log || "";
    const has = (re) => re.test(log);
    if (has(/Siemens TIA Openness|not a member of|EngineeringSecurityException|UnauthorizedAccess/i))
      return { text: PK.t("Your Windows user on the TIA PC may not use TIA Openness."), todo: PK.t("On the VM: Computer Management, Local Users and Groups, Groups, \"Siemens TIA Openness\": add your user. Then log off and on again, and start the job again.") };
    if (has(/whitelist|allow access|access was denied by the user|firewall.*openness/i))
      return { text: PK.t("TIA Portal did not allow the TIA helper to use it."), todo: PK.t("On the VM: double-click Update-TIA-helper in C:\\PLCKi\\PLC-Ki\\tools\\TiaImporter once (it approves TiaImporter in TIA), or answer \"Yes to all\" when TIA asks.") };
    if (has(/refused the token|\b401\b.*(helper|5090|vm)|(helper|5090|vm).*\b401\b/i))
      return PK.plain("refused the token");
    if (has(/No answer from .*:\d+|Cannot reach the TIA helper|Link is not connected|connection refused.*5090|actively refused/i))
      return PK.plain("TIA helper timeout");
    if (has(/newer version|older version|upgrade the project|cannot be opened with this version|not compatible with/i))
      return { text: PK.t("The archive was made with another TIA Portal version."), todo: PK.t("PLC-Ki uses TIA Portal V20. Open the project in TIA V20 (it offers to upgrade it), archive it again, and import that.") };
    if (has(/licen[cs]e[^\n]*(no valid|missing|expired|not found|fail)|(no valid|missing|expired)[^\n]*licen[cs]e/i))
      return { text: PK.t("TIA Portal has no valid license on the TIA PC."), todo: PK.t("Check the STEP 7 Professional license in the Automation License Manager on the VM.") };
    if (has(/credit balance|billing|insufficient.*(credit|funds)/i))
      return { text: PK.t("The Claude account has no credit left."), todo: PK.t("Add credit at console.anthropic.com (Billing), then start the job again.") };
    if (has(/authentication_error|invalid x-api-key|ANTHROPIC_API_KEY|no (claude )?api key/i))
      return PK.plain("api key");
    if (has(/overloaded|rate_limit|\b529\b|\b429\b/i))
      return { text: PK.t("Claude is busy right now."), todo: PK.t("Wait a minute and start the job again.") };
    if (has(/out of memory|OutOfMemory|0xc000012d|not enough memory/i))
      return { text: PK.t("The TIA PC ran out of memory."), todo: PK.t("Close programs you don't need on the VM, or give the VM more memory (16 GB works well with TIA and PLCSIM).") };
    if (has(/know-how|protected block/i) && job.state === "problems")
      return { text: PK.t("Some blocks are know-how protected."), todo: PK.t("PLC-Ki cannot read protected blocks. Remove the protection in a copy of the project if those blocks matter.") };
    if (has(/fail-safe data in Openness/i))
      return { text: PK.t("It compiled, but this PLC has a safety program, and Siemens does not allow downloading it through a tool."), todo: PK.t("On the TIA PC: open the project in TIA Portal, select the PLC, Download to device (software, changes only), then close TIA again.") };
    if (has(/API usage limits|usage limit/i))
      return { text: PK.t("Your Claude API account reached its spending limit."), todo: PK.t("Raise the limit in the Anthropic Console (Settings, Limits), or wait until the date in the log. Importing again without a request still works.") };
    if (has(/PLCSIM|download/i) && has(/fail|error|refused|not reachable/i) && /download|PLCSIM/i.test(job.title || ""))
      return { text: PK.t("The download to PLCSIM did not work."), todo: PK.t("On the VM: check PLCSIM Advanced runs the virtual PLC (green), and its address is the PLCSIM address in Setup. Then start the job again.") };
    if (has(/compil\w*[^\n]*\b[1-9]\d* error/i) || has(/\berrors?: *[1-9]/i))
      return { text: PK.t("TIA found compile errors."), todo: PK.t("Open the log below for the lines TIA complained about. More fixing rounds (More options) let Ki fix them by itself.") };
    if (has(/already been opened by user|project\/library .* cannot be accessed/i))
      return { text: PK.t("TIA could not open the project: it is still open or locked on the TIA PC."), todo: PK.t("Close the project in TIA Portal on the TIA PC. If it is not open there, a hidden TIA is holding it: close the TIA helper window, run taskkill /im Siemens.Automation.Portal.exe /f on the TIA PC, wait 2 minutes, start the TIA helper again, then start the job again.") };
    // Stack frames ("at X.Y()") say nothing to a person: skip them, and cut "Unhandled exception. Type:" off the message
    const lines = log.split("\n").map((l) => l.replace(/^\s*\|\s?/, "").trim()).filter((l) => l && !l.startsWith(">") && !/^at |^--- End of/.test(l))
      .map((l) => l.replace(/^(Unhandled exception\.\s*)?(ERROR:\s*)?([\w.]+(Exception|Error):\s*)?/, ""));
    const last = lines.reverse().find((l) => /error|fail|cannot|could not|not found|refused|problem|answered/i.test(l)) || lines[0] || "";
    const p = PK.plain(last);
    return { text: p.todo ? p.text : job.state === "problems" ? PK.t("It finished, but with problems.") : PK.t("The job stopped with an error."), todo: p.todo || (last ? PK.t("The log says: {line}", { line: PK.srv(last.slice(0, 240)) }) : PK.t("Open the log below.")) };
  }

  /** How far a job is: { pct, phase, hint } from its log, or null when it cannot tell (then the bar just moves). */
  function jobProgress(job) {
    const log = job.log || "";
    const at = (re) => re.test(log);
    const secs = job.started ? (Date.now() - new Date(job.started).getTime()) / 1000 : 0;
    if (job.kind === "export") {
      let pct = 2, phase = PK.t("Sending the archive to the TIA PC");
      if (at(/TIA opens the archive/)) { pct = 10; phase = PK.t("Waiting for TIA on the TIA PC"); }
      if (at(/Starting TIA Portal/)) { pct = 15; phase = PK.t("Starting TIA Portal in the background"); }
      if (at(/Retrieving /)) { pct = 25; phase = PK.t("Opening the archive in TIA (the slowest part)"); }
      if (at(/PLC [^\n]+ \/ /)) { pct = 40; phase = PK.t("Exporting the PLC program"); }
      const m = [...log.matchAll(/blocks (\d+)\/(\d+) \((\d+)%\)/g)].pop();
      if (m) { pct = 40 + Math.round(Number(m[3]) * 0.45); phase = PK.t("Exporting blocks: {done} of {total} done", { done: m[1], total: m[2] }); }
      if (at(/types and tag tables/)) { pct = Math.max(pct, 86); phase = PK.t("Exporting data types and tag tables"); }
      if (at(/Done\. \d+ PLC/)) { pct = 92; phase = PK.t("Copying the export back to this PC"); }
      if (at(/Export saved/)) { pct = 100; phase = PK.t("Done"); }
      // between two log lines, creep forward a little so it never looks frozen
      return { pct, phase, hint: PK.t("Usually 2 to 10 minutes, depending on the program size.") };
    }
    if (job.kind === "import") {
      // Time grows with the program: about 5 minutes for the demo (80 kB of program text), 20 for a 650 kB plant
      const kb = Number((log.match(/Program digest: (\d+) kB/) || [])[1] || 100);
      const est = 180 + kb * 1.6;
      const written = Number(([...log.matchAll(/written (\d+) kB of the model/g)].pop() || [])[1] || 0);
      const pct = Math.min(95, Math.round(5 + secs / est * 85));
      const phase = at(/Compared with|Wrote /) ? PK.t("Checking the model")
        : at(/Correcting \d+ problem/) ? PK.t("Ki is fixing what the check found")
        : written > 0 ? PK.t("Ki is writing the model ({written} kB so far)", { written })
        : at(/reading the program/) ? PK.t("Ki is reading the program and thinking") : PK.t("Reading the program export");
      const mins = Math.max(3, Math.round(est / 60));
      return { pct, phase, hint: PK.t("About {mins} minutes for a program this size; it can take longer. While Ki writes, the kB count keeps growing.", { mins }) };
    }
    if (job.kind === "develop") {
      // The generator's own markers (##plcki stage ...) say where it is; between them, creep forward a little
      const [stage, arg] = (job.stage || "connect").split(" ");
      const since = (from, to, est) => Math.min(to, Math.round(from + secs / est * (to - from)));
      const steps = {
        connect: [3, PK.t("Connecting to the TIA PC")],
        generate: [since(3, 40, 240), PK.t("{ai} is writing the blocks", { ai: PK.AI })],
        fix: [40, PK.t("{ai} is fixing what TIA found (round {n})", { ai: PK.AI, n: arg || 1 })],
        import: [55, PK.t("TIA is importing and compiling {what}", { what: arg || PK.t("the blocks") })],
        download: [75, PK.t("Downloading {what} to {where}", { what: arg || PK.t("the blocks"), where: /real PLC/.test(job.title || "") ? PK.t("the real PLC") : "PLCSIM" })],
        watch: [85, PK.t("Watching the PLC run for {n} s (read-only)", { n: arg || PK.t("a few") })],
        done: [100, PK.t("Done")],
      };
      const [pct, phase] = steps[stage] || [10, PK.t("{ai} is working", { ai: PK.AI })];
      const hint = stage === "connect" && secs > 20
        ? PK.t("The TIA helper on the TIA PC has not answered yet. Check that its window is open and not busy with another job; after about 100 s PLC-Ki gives up and says why.")
        : /^Import /.test(job.title || "") ? PK.t("TIA imports and compiles the latest version as it is; what TIA finds goes back to {ai} for another round.", { ai: PK.AI })
        : PK.t("{ai} writes the blocks, TIA imports and compiles them, and what TIA finds goes back to {ai} for another round. Usually 3 to 10 minutes.", { ai: PK.AI });
      return { pct, phase, hint };
    }
    return null;
  }

  function progressHtml(job, lastLine) {
    const secs = job.started ? Math.max(0, Math.round((Date.now() - new Date(job.started).getTime()) / 1000)) : 0;
    const time = PK.t("{time} so far", { time: `${Math.floor(secs / 60)}:${String(secs % 60).padStart(2, "0")}` });
    const p = jobProgress(job);
    const raw = lastLine.replace(/^\s*\|\s?/, "").slice(0, 200);
    const line = esc(PK.srv(raw));
    if (!p) return `<div class="jobprog"><div class="top"><span class="phase">${PK.t("Working…")}</span><span class="time">${time}</span></div>
      <div class="progress indet"><div></div></div>${line ? `<p class="latest" title="${line}"><span>${PK.t("Latest")}</span>${line}</p>` : ""}</div>`;
    return `<div class="jobprog"><div class="top"><span class="pct">${p.pct}%</span><span class="phase">${esc(p.phase)}</span><span class="time">${time}</span></div>
      <div class="progress"><div style="width:${p.pct}%"></div></div>
      <p class="m-0 muted small">${esc(p.hint)}</p>
      ${/blocks \d+\//.test(raw) || !line ? "" : `<p class="latest" title="${line}"><span>${PK.t("Latest")}</span>${line}</p>`}</div>`;
  }

  /** A job: title, state, progress while it runs, the problem in plain words when it fails, and the log. actions: buttons for a finished job. */
  function jobCard(job, extra = "", actions = "") {
    const [t, cls] = stateText[job.state] || [job.state, "info"];
    const lines = (job.log || "").split("\n").filter((l) => l.trim() && !l.startsWith("> "));
    const lastLine = lines.slice(-1)[0] || PK.t("Starting…");
    return `<div class="card job">
      <div class="row"><b style="font-size:16px">${esc(PK.srv(job.title))}</b><span class="pill ${cls}"><span class="dot"></span>${PK.t(t)}</span><span class="spacer"></span>
        ${job.state === "running" ? `<button class="btn red-tinted small" data-stop="${esc(job.id)}">${icon("stop")}${PK.t("Stop")}</button>` : actions}</div>
      ${extra}
      ${job.state === "running" ? progressHtml(job, lastLine) : ""}
      ${["failed", "problems", "stopped"].includes(job.state) ? (() => { const p = jobProblem(job); return `<div class="mt-3 notice ${job.state === "failed" ? "red" : "gold"}">${icon("warn")}<div><b>${esc(PK.srv(p.text))}</b>${p.todo ? `<br>${esc(PK.srv(p.todo))}` : ""}</div></div>`; })() : ""}
      <details class="more" ${job.state === "failed" ? "open" : ""}><summary>${PK.t("Log")}</summary>
        <div class="joblog" data-log>${esc((job.log || "").split("\n").map((l) => PK.srv(l)).join("\n"))}</div></details>
    </div>`;
  }
  function bindJob(page) {
    page.querySelectorAll("[data-stop]").forEach((b) => b.onclick = async () => {
      if (await PK.confirmSheet({ title: PK.t("Stop this job?"), text: PK.t("What was done so far is kept. A TIA import that is already running on the TIA PC finishes there."), ok: PK.t("Stop"), danger: true }))
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
    same: { title: PK.T("Same PC as TIA"), text: PK.T("PLC-Ki, TIA Portal and the cable to the PLC are on this PC."), icon: "pc", tiaMode: "local", plcVia: "direct" },
    vm: { title: PK.T("PC with a TIA VM"), text: PK.T("PLC-Ki runs on this PC; TIA runs in a VM (or another PC) next to it."), icon: "vm", tiaMode: "remote", plcVia: "direct" },
    server: { title: PK.T("Server + my laptop"), text: PK.T("PLC-Ki runs on a server. Your laptop has TIA and reaches the PLC."), icon: "server", tiaMode: "link", plcVia: "link" },
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
    const plc = (x, y) => box(x, y, 210, 92, PK.t("PLC"), [{ t: c.address || PK.t("address not set"), mono: true }, { t: plcOk ? PK.t("read live") : plcOk === false ? PK.t("no answer") : "S7-1500 / S7-1200" }]);
    let svg;
    if (c.tiaMode === "local") {
      svg = `<rect x="30" y="30" width="430" height="200" rx="22" fill="none" stroke="var(--line-strong)" stroke-dasharray="4 6"/>
        <text x="50" y="56" font-size="12.5" fill="var(--muted)">${PK.t("This PC")}</text>
        ${box(50, 76, 180, 120, "PLC-Ki", [{ t: location.host, mono: true }, { t: PK.t("web app") }], true)}
        ${box(260, 76, 180, 120, "TIA Portal", [{ t: "+ TIA helper" }, { t: tiaOk ? PK.t("started") : PK.t("on this PC") }])}
        ${wire(230, 136, 260, 136, tiaOk, "")}
        ${wire(460, 130, 720, 130, plcOk, PK.t("Ethernet · S7"))}${plc(720, 84)}`;
    } else if (c.tiaMode === "remote") {
      svg = `${box(40, 74, 220, 112, "PLC-Ki", [{ t: location.host, mono: true }, { t: PK.t("this PC") }], true)}
        ${box(700, 20, 230, 100, "TIA Portal", [{ t: (c.tiaUrl || PK.t("address not set")).replace(/^https?:\/\//, ""), mono: true }, { t: PK.t("VM or PC with the TIA helper") }])}
        ${wire(260, 110, 700, 70, tiaOk, PK.t("TIA helper, port 5090"))}
        ${wire(260, 150, 700, 190, plcOk, "S7")}${plc(700, 144)}`;
    } else {
      svg = `${box(20, 74, 200, 112, "PLC-Ki", [{ t: PK.t("on the server"), mono: false }, { t: location.host, mono: true }], true)}
        ${box(410, 64, 230, 132, PK.t("Your laptop"), [{ t: linkStatus && linkStatus.connected ? "PLC-Ki Link · " + (linkStatus.machine || "") : "PLC-Ki Link" }, { t: linkStatus && linkStatus.tia ? PK.t("TIA Portal, helper ready") : "TIA Portal" }, { t: PK.t("your browser") }])}
        ${wire(410, 130, 220, 130, linkStatus ? linkStatus.connected : null, PK.t("connects out"), !(linkStatus && linkStatus.connected))}
        ${wire(640, 130, 740, 130, linkStatus && linkStatus.plc ? linkStatus.plc.connected : null, "S7")}${plc(740, 84)}`;
    }
    const st = (v) => v === true ? ["var(--ok)", PK.t("working")] : v === false ? ["var(--fault)", PK.t("no answer")] : ["var(--muted)", PK.t("not checked yet")];
    const rows = c.tiaMode === "link" || c.plcVia === "link"
      ? [[PK.t("PLC-Ki › your laptop (Link)"), linkStatus ? linkStatus.connected : null], [PK.t("Laptop › PLC {address}", { address: c.address || "" }), linkStatus && linkStatus.plc ? linkStatus.plc.connected : null]]
      : [[c.tiaMode === "remote" ? PK.t("PLC-Ki › TIA helper on the VM") : PK.t("PLC-Ki › TIA helper"), tiaOk], [PK.t("PLC-Ki › PLC {address}", { address: c.address || "" }), plcOk]];
    const list = `<ul class="linklist">${rows.map(([t, v]) => `<li><span class="sw" style="background:${st(v)[0]}"></span>${esc(t)}<small>${st(v)[1]}</small></li>`).join("")}</ul>`;
    return list + `<div class="schematic"><svg viewBox="0 0 960 250" role="img" aria-label="${PK.t("How PLC-Ki, TIA Portal and the PLC are connected")}" font-family="inherit">${svg}</svg></div>
      <div class="legend"><span><span class="sw" style="background:var(--ok)"></span>${PK.t("checked, working")}</span><span><span class="sw" style="background:var(--fault)"></span>${PK.t("no answer")}</span><span><span class="sw" style="background:var(--muted)"></span>${PK.t("not checked yet")}</span></div>`;
  }

  const setupNav = (here) => PK.ui.tabs(PK.t("Setup"), [["start", PK.t("Get started")], ["connections", PK.t("Connections")], ["program", PK.t("Import a program")], ["settings", PK.t("Settings")], ["qr", PK.t("QR labels")]]
    .map(([k, l]) => ({ label: l, on: k === here, href: "#/setup/" + k })));

  function choice(name, current, options) {
    return `<div class="choice" role="group">${options.map((o) => `<button type="button" data-${name}="${o.value}" class="${current === o.value ? "on" : ""}" aria-pressed="${current === o.value}" aria-label="${esc(o.label + ". " + o.text)}" ${o.disabled ? "disabled" : ""}>
      <span class="ico">${icon(o.icon)}</span><b>${esc(o.label)}</b><small>${esc(o.text)}</small>${o.soon ? `<span class="tag gold soon">${PK.t("Next update")}</span>` : ""}</button>`).join("")}</div>`;
  }

  function connectionsHtml() {
    const c = conn;
    const preset = c.setupChosen ? presetOf(c) : null;   // a fresh install has not picked one yet
    const tiaFields = c.tiaMode === "local" ? `
        <div class="grid two">
          <label class="field">${PK.t("TIA helper (TiaImporter.exe) on this PC")}<input id="tiaImporterPath" value="${esc(c.tiaImporterPath)}" placeholder="C:\\PLCKi\\PLC-Ki\\tools\\TiaImporter\\bin\\Release\\net48\\TiaImporter.exe"><span class="hint">${PK.t("PLC-Ki starts it in its own window and stops it when PLC-Ki stops.")}</span></label>
          <label class="field">${PK.t("TIA project for generated blocks")}<input id="tiaProject" value="${esc(c.tiaProject)}" placeholder="C:\\Users\\you\\Documents\\Automation\\Line1\\Line1.ap20"><span class="hint">${PK.t("Can also be chosen on the Block generator page.")}</span></label>
          <label class="field">${PK.t("PLCSIM address (downloads for testing)")}<input id="tiaPlcsimIp" value="${esc(c.tiaPlcsimIp)}" placeholder="${PK.t("e.g. 192.168.0.10, empty = no PLCSIM downloads")}"></label>
          <label class="field">${PK.t("Real PLCs that may be loaded")}<input id="tiaAllowReal" value="${esc(c.tiaAllowReal)}" placeholder="${PK.t("empty = never")}"><span class="hint">${PK.t("Each download is still confirmed by typing the address in the TiaImporter window, with the CPU in STOP.")}</span></label>
        </div>
        <div class="mt-3 row"><button class="btn b small" data-start-helper>${tiaStatus && tiaStatus.helperRunning ? PK.t("Restart the TIA helper") : PK.t("Start the TIA helper")}</button>
          <span class="faint small">${PK.t("It listens on port {port} on this PC (set in appsettings.json, {setting}).", { port: esc(info.tiaPort || 5090), setting: "<code>PlcKi:Tia:Port</code>" })}</span></div>`
      : c.tiaMode === "remote" ? `
        <div class="grid two">
          <label class="field">${PK.t("TIA PC address")}<input id="tiaUrl" value="${esc(c.tiaUrl)}" placeholder="http://192.168.0.20:5090"></label>
          <label class="field">${PK.t("Token")} <span class="hint">${info.tiaToken === "set" ? PK.t("Saved. Leave empty to keep it.") : PK.t("Shown in the PLC-Ki TIA helper window on the VM.")}</span><input id="tiaToken" type="password" autocomplete="new-password" placeholder="${info.tiaToken === "set" ? "••••••••" : PK.t("at least 8 characters")}"></label>
          <label class="field">${PK.t("TIA project for generated blocks")}<input id="tiaProject" value="${esc(c.tiaProject)}" placeholder="${PK.t("path on the TIA PC, e.g. {example}", { example: "C:\\Users\\Software\\Documents\\Automation\\…\\.ap20" })}"></label>
        </div>
        <details class="more" ${tiaStatus && !tiaStatus.ok ? "open" : ""}><summary>${PK.t("How to set up the TIA helper on the VM")}</summary>
          <ol class="mt-3 howto">
            <li><b>${PK.t("Once, put the newest PLC-Ki on the VM")}</b> ${PK.t("in {folder}: copy the PLC-Ki folder from this PC. If it was downloaded there with git, this line in Command Prompt updates it:", { folder: "<code>C:\\PLCKi\\PLC-Ki</code>" })}${PK.cmd(PK.t("On the VM"), "cd /d C:\\PLCKi\\PLC-Ki && git pull")}</li>
            <li><b>${PK.t("Once, double-click {name}", { name: "<code>Setup-TIA-helper</code>" })}</b> ${PK.t("in {folder}. It asks for the token and the PLCSIM address, opens the port, and starts the helper.", { folder: "<code>C:\\PLCKi\\PLC-Ki\\tools\\TiaImporter</code>" })}</li>
            <li><b>${PK.t("Every day: nothing.")}</b> ${PK.t("The helper starts by itself when you log into the VM, in a window called {window}. If that window was closed, double-click {start} in the same folder. After a PLC-Ki update, double-click {update}.", { window: "<b>PLC-Ki TIA helper</b>", start: "<code>Start-TIA-helper</code>", update: "<code>Update-TIA-helper</code>" })}</li>
          </ol>
        </details>`
      : `<div class="grid two">
          <label class="field">${PK.t("TIA project for generated blocks")}<input id="tiaProject" value="${esc(c.tiaProject)}" placeholder="${PK.t("path on the laptop, e.g. {example}", { example: "C:\\Users\\you\\Documents\\Automation\\…\\.ap20" })}"><span class="hint">${PK.t("Can also be chosen on the Block generator page.")}</span></label>
        </div>
        <p class="m-0 muted small">${PK.t("PLC-Ki Link starts the TIA helper on the laptop (see PLC-Ki Link below). PLCSIM and real-PLC downloads are allowed there, on the laptop's command.")}</p>`;

    const tiaPlain = tiaStatus && !tiaStatus.ok ? PK.plain(tiaStatus.message) : null;
    const tiaMsg = !tiaStatus ? `<span class="pill">${PK.t("Checking…")}</span>`
      : tiaStatus.notSet ? `<span class="pill"><span class="dot"></span>${PK.t("Not set yet")}</span><span class="muted small" style="flex-basis:100%">${esc(PK.srv(tiaStatus.message))} ${PK.t("Only needed to import a program or for the block generator.")}</span>`
      : `<span class="pill ${tiaStatus.ok ? "ok" : "bad"}"><span class="dot"></span>${esc(tiaStatus.ok ? PK.srv(tiaStatus.message || "") || PK.t("Reachable") : PK.srv(tiaPlain.text))}</span>
         ${tiaPlain && tiaPlain.todo ? `<span class="muted small" style="flex-basis:100%">${esc(PK.srv(tiaPlain.todo))}</span>` : ""}
         ${tiaStatus.ok ? `<span class="faint small">${PK.t("PLCSIM downloads: {plcsim} · real PLC downloads: {real}", { plcsim: tiaStatus.plcsimIp ? esc(tiaStatus.plcsimIp) : PK.t("off"), real: tiaStatus.realAllowed ? PK.t("allowed (confirmed at the TIA PC)") : PK.t("off") })}</span>` : ""}`;

    return PK.hero({ cls: "hero compact", before: setupNav("connections"), title: PK.t("Connections"),
        sub: `${PK.t("Where PLC-Ki finds the PLC (live values) and TIA Portal (imports and the block generator). Pick the setup that matches yours.")}
          ${PK.t("New here? {link} walks you through it.", { link: `<a href="#/setup/start">${PK.t("Get started")}</a>` })}`,
        extra: `${restartBar()}
        ${choice("preset", preset, Object.entries(presets).map(([k, p]) => ({ value: k, label: PK.t(p.title), text: PK.t(p.text), icon: p.icon, })))}
        <div class="mt-5">${diagram(c)}</div>` })

      + PK.sec("alt", `<div class="row" style="align-items:baseline"><h2>${PK.t("The PLC")}</h2><span class="pill warn">${(c.plcAllowWrites || "").trim() ? PK.t("Reads; changes allowed") : PK.t("Reads only")}</span></div>
        <p class="sub">${PK.t("PLC-Ki reads values with the S7 protocol TIA and HMIs use. Administrators can change a value on PLCSIM, safety ones after a warning; on a real PLC only memory and data values, when it is allowed below. F-blocks never.")}</p>
        <div class="card stack">
        ${choice("plcvia", c.plcVia, [
          { value: "direct", label: PK.t("From here"), text: PK.t("PLC-Ki reads the PLC over the network."), icon: "pc" },
          { value: "link", label: PK.t("Through my laptop"), text: PK.t("PLC-Ki Link on the laptop reads it and sends the values here."), icon: "laptop" },
        ]).replace('class="choice"', 'class="choice two"')}
        <div class="grid two">
          <label class="field">${PK.t("PLC address")}<input id="address" value="${esc(c.address)}" placeholder="${PK.t("e.g. 192.168.0.10")}" inputmode="decimal">${runNote("address")}</label>
          ${c.plcVia === "link" ? `<p class="muted small" style="align-self:end;margin:0 0 10px">${PK.t("The laptop reads this address. If the CPU has access protection, its password stays on the laptop ({flag}).", { flag: "<code>--plc-password</code>" })}</p>`
            : `<label class="field">${PK.t("Password")} <span class="hint">${info.plcPassword === "settings" ? PK.t("saved") : info.plcPassword === "environment" ? PK.t("from PLCKI_PLC_PASSWORD") : PK.t("only if the CPU has access protection")}</span><input id="plcPassword" type="password" autocomplete="new-password"></label>`}
        </div>
        ${c.plcVia === "link" ? "" : `<div class="row"><button class="btn b small" data-test-plc>${PK.t("Test the address")}</button>
          ${plcTest ? `<span class="pill ${plcTest.ok ? "ok" : "bad"}"><span class="dot"></span>${esc(PK.srv(plcTest.ok ? plcTest.message : PK.plain(plcTest.message, { plc: conn.address }).text))}</span>` : ""}</div>
          ${plcTest && !plcTest.ok && PK.plain(plcTest.message).todo ? `<p class="m-0 muted small">${esc(PK.srv(PK.plain(plcTest.message, { plc: conn.address }).todo))}</p>` : ""}`}
        <details class="mt-0 more"><summary>${PK.t("More PLC options")}</summary>
          <div class="mt-3 grid three">
            <label class="field">${PK.t("Security")}<select id="security">${["Auto", "Tls", "LegacyChallenge"].map((x) => `<option ${c.security === x ? "selected" : ""}>${x}</option>`).join("")}</select></label>
            ${c.plcVia === "link" ? "" : `<label class="field">${PK.t("PLC user name")} <span class="hint">${PK.t("only when the CPU has users set up in TIA (access control); empty = the password alone")}</span><input id="username" value="${esc(c.username || "")}" autocomplete="off" spellcheck="false">${runNote("username")}</label>`}
            <label class="field">${PK.t("Read the PLC every")} <span class="hint">${PK.t("milliseconds; 1000 = once a second")}</span><input id="pollMs" type="number" min="100" max="60000" step="100" value="${c.pollMs}"></label>
            <label class="field">${PK.t("Fast capture: the PLC sends every change, sampled every")} <span class="hint">${PK.t("milliseconds; 100 is a good start, 0 = off. Used for the seconds before each alarm.")}</span><input id="fastMs" type="number" min="0" max="10000" step="10" value="${c.fastMs ?? 100}"></label>
            <label class="field">${PK.t("Wait for an answer up to")} <span class="hint">${PK.t("milliseconds; 5000 = 5 seconds")}</span><input id="timeoutMs" type="number" min="500" max="60000" step="500" value="${c.timeoutMs}"></label>
            <label class="field">${PK.t("Values from")}<select id="source"><option value="s7" ${c.source === "s7" ? "selected" : ""}>${PK.t("Live PLC")}</option><option value="snapshot" ${c.source === "snapshot" ? "selected" : ""}>${PK.t("Test file (no PLC)")}</option></select></label>
            <label class="field">${PK.t("Test file")}<select id="snapshotFile">${(lists.snapshots || []).map((f) => `<option value="${esc(f.path)}" ${f.path === c.snapshotFile ? "selected" : ""}>${esc(f.label)}</option>`).join("")}</select></label>
            <label class="field" style="grid-column:1/-1">${PK.t("Real PLCs whose values may be changed")}<input id="plcAllowWrites" value="${esc(c.plcAllowWrites || "")}" placeholder="${PK.t("empty = never (PLCSIM is always allowed)")}"><span class="hint">${PK.t("Each change on a real PLC still needs a reason and the PLC address typed again. Inputs, outputs and values the safety program reads are never changed on a real PLC. Takes effect without a restart.")}</span></label>
          </div>
        </details></div>`)

      + PK.sec("", `<h2>TIA Portal</h2>
        <p class="sub">${PK.t("Used to import a program from an archive and by the block generator. A small program, the TIA helper, does the TIA work next to TIA.")}</p>
        <div class="card stack">
        ${choice("tiamode", c.tiaMode, [
          { value: "local", label: PK.t("This PC"), text: PK.t("TIA Portal is installed here."), icon: "pc" },
          { value: "remote", label: PK.t("Another PC or VM"), text: PK.t("A PC or VM next to this one runs TIA and the helper."), icon: "vm" },
          { value: "link", label: PK.t("My laptop"), text: PK.t("Through PLC-Ki Link."), icon: "laptop" },
        ])}
        <div>${tiaFields}</div>
        <div class="row" style="align-items:center">${tiaMsg}<button class="btn gray small" data-check-tia>${PK.t("Check again")}</button></div>
        </div>
        ${c.plcVia === "link" || c.tiaMode === "link" ? "" : saveRow()}`)
      + (c.plcVia === "link" || c.tiaMode === "link" ? linkHtml(c) : "");
  }

  const saveRow = () => `<div class="mt-6 actions"><button class="btn big" data-save>${PK.t("Save")}</button><span class="muted small" id="save-msg"></span></div>`;

  /** PLC-Ki Link: the laptop that reaches the PLC and has TIA connects out to this server. */
  function linkHtml(c) {
    const l = linkStatus;
    const ago = (t) => { if (!t) return PK.t("never"); const s = Math.round((Date.now() - new Date(t)) / 1000); return s < 5 ? PK.t("just now") : s < 120 ? PK.t("{n} s ago", { n: s }) : PK.t("{n} min ago", { n: Math.round(s / 60) }); };
    const state = !l ? `<span class="pill">${PK.t("Checking…")}</span>`
      : l.connected ? `<span class="pill ok"><span class="dot"></span>${PK.t("Connected from {name}", { name: esc(l.machine || PK.t("the laptop")) })}</span>`
      : `<span class="pill bad"><span class="dot"></span>${l.hasKey ? (l.lastSeen ? PK.t("Not connected, last seen {when}", { when: ago(l.lastSeen) }) : PK.t("Not connected")) : PK.t("No key yet")}</span>`;
    const plc = l && l.plc ? (l.plc.connected ? `<span class="pill ok"><span class="dot"></span>${PK.t("Reading PLC {address}", { address: esc(l.plc.address || "") })}</span>`
      : `<span class="pill bad"><span class="dot"></span>${PK.t("PLC: {error}", { error: esc(PK.srv(l.plc.error || "") || PK.t("no answer")) })}</span>`) : "";
    const tia = l && l.connected ? (l.tia ? `<span class="pill ok"><span class="dot"></span>${PK.t("TIA helper ready")}</span>`
      : `<span class="pill ${c.tiaMode === "link" ? "bad" : ""}"><span class="dot"></span>${esc(PK.srv(l.tiaMessage || "") || PK.t("No TIA helper"))}</span>`) : "";
    const key = linkKey || "<the key>";
    const server = location.origin;
    const cmd = `cd /d %USERPROFILE%\\Downloads\\PLC-Ki && dotnet run --project src\\PlcKi.Server -- link --server ${server} --key ${key}${c.tiaMode === "link" ? ` --tia "C:\\PLCKi\\PLC-Ki\\tools\\TiaImporter\\bin\\Release\\net48\\TiaImporter.exe"` : ""}`;
    return PK.sec("dark", `<span class="eyebrow">PLC-Ki Link</span><h2 class="mt-2">${PK.t("Your laptop, connected out.")}</h2>
      <p class="sub">${c.tiaMode === "link" ? PK.t("A small program on the laptop that reaches the PLC and has TIA.") : PK.t("A small program on the laptop that reaches the PLC.")} ${PK.t("It only connects out to this server, so no firewall or VPN changes on the plant side.")}
        ${c.tiaMode === "link" ? PK.t("It reads the PLC read-only and carries the TIA work to the laptop's TIA.") : PK.t("It reads the PLC read-only.")}</p>
      <div class="mt-2 row">${state}${plc}${tia}</div>
      <div class="mt-5 steps col">
        <div class="step ${l && l.hasKey ? "done" : "now"}"><span class="n">1</span><div><b>${PK.t("Save this setup, then make a key")}</b>
          <p class="small" style="margin:6px 0 10px;opacity:.8">${PK.t("The key lets the laptop in. It is shown once; a new key replaces the old one.")}</p>
          <div class="row">${saveRow().replace('style="margin-top:28px"', "")}<button class="btn white small" data-link-key>${l && l.hasKey ? PK.t("Make a new key") : PK.t("Make a key")}</button>
          ${linkKey ? `<span class="pill ok"><span class="dot"></span>${PK.t("New key below. Copy it now.")}</span>` : l && l.hasKey ? `<span class="small" style="opacity:.7">${PK.t("A key exists (made {when}).", { when: esc(PK.date(l.keyCreated)) })}</span>` : ""}</div></div></div>
        <div class="step ${l && l.connected ? "done" : l && l.hasKey ? "now" : ""}"><span class="n">2</span><div><b>${PK.t("On the laptop, start Link")}</b>
          <p class="small" style="margin:6px 0 10px;opacity:.8">${c.tiaMode === "link" ? PK.t("In a Command Prompt, in the PLC-Ki folder (a clone of the repository, as on your PC). Leave the window open; it opens the TIA helper's window too.") : PK.t("In a Command Prompt, in the PLC-Ki folder (a clone of the repository, as on your PC). Leave the window open.")}</p>
          ${PK.cmd(PK.t("On the laptop"), cmd)}
          <div class="mt-3 row">
            ${c.tiaMode === "link" ? `<span class="small" style="opacity:.8">${PK.t("Add {plcsim} for PLCSIM tests, {real} only if blocks may go to a real PLC.", { plcsim: "<code>--plcsim-ip &lt;address&gt;</code>", real: "<code>--allow-real &lt;address&gt;</code>" })}</span>` : ""}</div></div></div>
        <div class="step ${l && l.connected ? "done" : ""}"><span class="n">3</span><div><b>${PK.t("Back here, it turns green")}</b>
          <p class="small" style="margin:6px 0 0;opacity:.8">${PK.t("The status above shows the laptop, the PLC and TIA within a few seconds.")}</p></div></div>
      </div>`);
  }

  function readConn(page) {
    if (!conn) return;      // still loading
    const v = (id) => { const el = page.querySelector("#" + id); return el ? el.value : undefined; };
    for (const k of ["address", "security", "username", "source", "snapshotFile", "tiaUrl", "tiaImporterPath", "tiaPlcsimIp", "tiaAllowReal", "tiaProject", "plcAllowWrites"]) {
      const x = v(k); if (x !== undefined) conn[k] = x.trim();
    }
    for (const k of ["pollMs", "fastMs", "timeoutMs"]) { const x = v(k); if (x !== undefined) conn[k] = Number(x); }
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
    guide: "setup",
    leave() { clearInterval(linkTimer); linkTimer = null; },
    async mount() {
      conn = null; tiaStatus = null; plcTest = null; linkKey = null;
      clearInterval(linkTimer);
      linkTimer = setInterval(async () => { if (conn && (conn.plcVia === "link" || conn.tiaMode === "link")) { const was = linkStatus && linkStatus.connected; await loadLink(); PK.draw(); if (linkStatus && linkStatus.connected && !was) checkTia(); } }, 3000);
      loadLink();
      try { await loadSettings(); conn = { ...settings }; } catch (e) { PK.$("page").innerHTML = PK.sec("", PK.ui.error(e)); return; }
      PK.draw();
      checkTia();
    },
    render() { return conn ? connectionsHtml() : PK.hero({ cls: "hero compact", before: setupNav("connections"), title: PK.t("Connections") }) + PK.sec("", PK.ui.loading("cards", { rows: 3 })); },
    after(page) {
      bindRestart(page);
      const keep = () => { readConn(page); trackConn(); };
      page.querySelectorAll("input,select").forEach((el) => { PK.on(el, "change", keep); PK.on(el, "input", keep); });
      trackConn();
      page.querySelectorAll("[data-preset]").forEach((b) => b.onclick = () => { keep(); const p = presets[b.dataset.preset]; conn.tiaMode = p.tiaMode; conn.plcVia = p.plcVia; PK.draw(); });
      page.querySelectorAll("[data-tiamode]").forEach((b) => b.onclick = () => { keep(); conn.tiaMode = b.dataset.tiamode; PK.draw(); });
      page.querySelectorAll("[data-plcvia]").forEach((b) => b.onclick = () => { keep(); conn.plcVia = b.dataset.plcvia; PK.draw(); });
      const t = page.querySelector("[data-test-plc]");
      if (t) t.onclick = async () => { keep(); plcTest = { ok: false, message: PK.t("Testing…") }; PK.draw(); try { plcTest = await api("POST", "/api/admin/test-plc", { address: conn.address }); } catch (e) { plcTest = { ok: false, message: e.message }; } PK.draw(); };
      const lk = page.querySelector("[data-link-key]");
      if (lk) lk.onclick = async () => {
        if (linkStatus && linkStatus.hasKey && !(await PK.confirmSheet({ title: PK.t("Make a new key?"), text: PK.t("The laptop's Link stops working until it is started with the new key."), ok: PK.t("Make a new key") }))) return;
        try { linkKey = (await api("POST", "/api/admin/link/key")).key; await loadLink(); PK.draw(); } catch (e) { toast(e.message, "error"); }
      };
      const c = page.querySelector("[data-check-tia]");
      if (c) c.onclick = checkTia;
      const h = page.querySelector("[data-start-helper]");
      if (h) h.onclick = async () => {
        if (JSON.stringify({ ...conn, _plcPassword: 0, _tiaToken: 0 }) !== JSON.stringify({ ...settings, _plcPassword: 0, _tiaToken: 0 })) { toast(PK.t("Save first, then start the helper.")); return; }
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
          toast(info.restartNeeded ? PK.t("Saved. Apply to use the new connections.") : PK.t("Saved."));
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
    return `<div class="mb-5 card stack">
      <div class="row"><span class="ico">${icon("import")}</span><h3 class="m-0 grow">${PK.t("Changed the program in TIA? Refresh it")}</h3></div>
      <p class="m-0 muted">${PK.t("PLC-Ki shows the code, tags and addresses of the program it last read")}${pr ? `: <b>${esc(pr.project)}</b>${pr.exportedAt ? `, ${PK.t("read {when}", { when: PK.date(pr.exportedAt) })}` : ""}` : ""}.
        ${PK.t("After changes in TIA (new addresses, edited blocks), read the project again. About a minute; Ki is not used, so it costs nothing. Alarms and steps stay as they are.")}
        ${PK.t("If TIA Portal has the project open, it is read from there (TIA asks once to allow access).")}</p>
      ${fresh.error ? `<div class="notice gold">${icon("warn")}<div>${PK.t("Cannot list the TIA projects: {error} Check Connections.", { error: esc(PK.srv(fresh.error)) })}</div></div>`
        : `<div class="row"><label class="grow m-0 field">${PK.t("TIA project")}<select id="fresh-project">${opts.map((x) => `<option value="${esc(x.path)}" ${x.path === fresh.project ? "selected" : ""}>${esc(x.name)}  ·  ${esc(x.path)}</option>`).join("")}</select></label>
          <button class="btn" data-refresh ${running || !fresh.project ? "disabled" : ""}>${PK.t("Refresh from TIA")}</button></div>`}
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
    const s = PK.sheet(`<h2>${PK.t("Uploading {name}", { name: esc(file.name) })}</h2><div class="progress"><div style="width:0%" id="up-bar"></div></div><p class="muted small" id="up-text">0 %</p>`, { dismiss: false });
    try {
      const res = await new Promise((resolve, reject) => {
        const x = new XMLHttpRequest();
        x.open("PUT", "/api/admin/import/upload/" + encodeURIComponent(file.name));
        x.upload.onprogress = (e) => { if (e.lengthComputable) { const p = Math.round(e.loaded / e.total * 100); $("up-bar").style.width = p + "%"; $("up-text").textContent = p + " %"; } };
        x.onload = () => { let d = null; try { d = JSON.parse(x.responseText); } catch { /* */ } x.status < 300 ? resolve(d) : reject(new Error((d && d.error) || PK.t("Upload failed ({status}).", { status: x.status }))); };
        x.onerror = () => reject(new Error(PK.t("Upload failed.")));
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
        <span class="grow"><b class="semibold">${esc(x.name)}</b><div class="faint">${(x.size / 1048576).toFixed(1)} MB · ${PK.date(x.time)}</div></span><span class="chev">›</span></li>`;
    const earlier = f && (f.archives.length || f.exports.length) ? `<details class="more" ${ch ? "" : "open"}><summary>${PK.t("Earlier files ({n})", { n: f.archives.length + f.exports.length })}</summary>
        <ul class="mt-3 list">${f.exports.map(fileRow("export")).join("")}${f.archives.map(fileRow("archive")).join("")}</ul></details>` : "";

    const exp = imp.exportInfo;
    const plcPick = exp && exp.plcs.length ? `<div class="mt-0 mb-4 tiles">${exp.plcs.map((p) => `<div class="tile pick ${imp.plc === p.name ? "on" : ""}" data-plc="${esc(p.name)}">
        <div class="label">${esc(p.cpu || PK.t("PLC"))}</div><div class="value">${esc(p.name)}</div><div class="faint">${PK.t("{blocks} blocks · {tags} tags · {languages}", { blocks: p.blocks, tags: p.tags, languages: (p.languages || []).join(", ") })}</div></div>`).join("")}</div>` : "";

    const mi = imp.modelInfo;
    const review = mi ? (mi.ok ? `${PK.ui.tiles([[PK.t("Tags"), mi.tags], [PK.t("Steps"), mi.steps], [PK.t("Devices"), mi.equipment], [PK.t("Alarms"), mi.alarms]].map(([label, value]) => ({ value, label })))}
        <p class="mt-3 mb-0"><b>${esc(mi.name)}</b>${mi.description ? " · " + esc(mi.description) : ""}</p>
        ${imp.job && imp.job.state === "problems" ? `<div class="mt-3 notice gold">${icon("warn")}<div>${PK.t("Ki left some problems (see the log). PLC-Ki will not start with this model until they are fixed. Build it again, or ask for help with the log.")}</div></div>` : ""}
        <div class="mt-4 row"><button class="btn" data-use ${imp.job && imp.job.state === "problems" ? "disabled" : ""}>${PK.t("Use this program")}</button></div>`
      : PK.ui.error(mi.error)) : "";

    const step = (n, title, text, body) => `<div class="step ${stepCls(n)}"><div class="n">${stepCls(n) === "done" ? "✓" : n}</div><h3>${title}</h3><p class="mb-4">${text}</p>${body}</div>`;
    return PK.hero({ cls: "hero compact", before: setupNav("program"), title: PK.t("Import a program"),
        sub: PK.t("Give PLC-Ki the TIA archive. It reads every block, and Ki writes the plain-English model of steps, devices, alarms, causes and fixes, once."),
        extra: `${restartBar()}
        ${PK.ui.tiles([
          { value: state.info ? state.info.machine : "", label: PK.t("watched now") },
          { value: state.model ? state.model.alarms.length : "–", label: PK.t("alarms explained") },
          { value: state.model ? state.model.equipment.length : "–", label: PK.t("devices") },
          { html: `<span class="k-file">${esc((cur.export || PK.t("none")).split(/[\\/]/).pop())}</span>`, label: PK.t("program file") }])}` })
      + PK.sec("alt", `${PKModel.html()}${refreshHtml()}<div class="steps col">
        ${step(1, PK.t("Choose the program"), PK.t("A TIA archive (.zap20), a program export (.zip) made earlier, or a Beckhoff TwinCAT 3 project (.tnzip, or the project folder zipped)."), `
          <div class="dropzone" id="drop">${icon("import", "")}<div class="semibold mt-2">${PK.t("Drop the file here, or click to choose it")}</div>
            <input type="file" id="file" accept=".zap20,.zap19,.zap18,.zap17,.zap16,.zip,.tnzip" class="hidden"></div>
          ${ch ? `<p class="mt-3 mb-0">${PK.t("Chosen:")} <b>${esc(ch.name)}</b> <span class="tag ${isArchive ? "gold" : "blue"}">${isArchive ? PK.t("TIA archive") : PK.t("program export")}</span></p>` : ""}
          ${earlier}`)}
        ${step(2, PK.t("Export it with TIA Portal"), PK.t("Only for archives. TIA opens it in the background on the TIA PC (see Connections) and exports every block. The archive is not changed."),
          (isArchive ? `<button class="btn" data-export ${jobRunning ? "disabled" : ""}>${PK.t("Export with TIA")}</button>` : `<span class="faint">${ch ? PK.t("Not needed: this is already an export.") : PK.t("Waiting for a file.")}</span>`)
          + (imp.job && imp.job.kind === "export" ? jobCard(imp.job) : ""))}
        ${step(3, PK.t("Build the machine model"), PK.t("Ki reads the program and writes what each step, device and alarm means. A few minutes, usually one to a few dollars of API use."),
          ch && !isArchive ? `${plcPick}
            <div class="row"><span class="small muted">${PK.t("Effort")}</span>${PK.ui.choice(PK.t("Effort"), [PK.T("medium"), PK.T("high"), PK.T("max")].map((e) => ({ label: PK.t(e), on: imp.effort === e, data: { "data-effort": e } })))}
              <label class="check small"><input type="checkbox" id="compare" ${imp.compare ? "checked" : ""}>${PK.t("Compare with the current model")}</label></div>
            <div class="mt-4 row"><button class="btn" data-build ${jobRunning || (exp && exp.plcs.length > 1 && !imp.plc) ? "disabled" : ""}>${PK.t("Build the model")}</button>
              ${exp && exp.plcs.length > 1 && !imp.plc ? `<span class="faint">${PK.t("Choose the PLC first.")}</span>` : ""}</div>` + (imp.job && imp.job.kind === "import" ? jobCard(imp.job) : "") : `<span class="faint">${PK.t("Needs a program export.")}</span>`)}
        ${step(4, PK.t("Check it and use it"), PK.t("What Ki found. Use it, and PLC-Ki restarts with this machine."), review || `<span class="faint">${PK.t("The result shows here.")}</span>`)}
        </div>
        ${imp.job && !["export", "import", "refresh"].includes(imp.job.kind) ? `<div class="mt-5">${jobCard(imp.job)}</div>` : ""}`);
  }

  PK.view("setup/program", {
    live: false,
    guide: "setup",
    async mount() {
      await refreshFiles().catch((e) => toast(e.message, "error"));
      loadRefresh();
      PKModel.start();
      const run = (state.jobs || []).find((j) => j.state === "running" && ["export", "import", "refresh"].includes(j.kind));
      if (run && !imp.job) startJob(run);
      PK.draw();
    },
    leave() { PKModel.stop(); if (imp.unfollow) { imp.unfollow(); imp.unfollow = null; } },
    render() { return imp.files ? programHtml() : PK.hero({ cls: "hero compact", before: setupNav("program"), title: PK.t("Import a program") }) + PK.sec("", PK.ui.loading("cards", { rows: 3 })); },
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
            state.program = null; state.programError = null;   // Program and Tags load the new program
            window.dispatchEvent(new Event("pk:program-changed"));
            await refreshFiles(); await loadRefresh();
            toast(PK.t("Refreshed. Program and Tags show the program as it is in TIA now."));
          });
        } catch (e) { toast(e.message, "error"); }
      };
      const ex = page.querySelector("[data-export]");
      if (ex) ex.onclick = async () => {
        try {
          const job = await api("POST", "/api/admin/import/export", { archive: imp.chosen.path });
          startJob(job, async (j) => { if (j.state === "done") { await refreshFiles(); await choose("export", j.outputs.export); toast(PK.t("Exported. Now build the model.")); } });
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
          toast(PK.t("Saved. Apply to start watching this program."));
          PK.draw();
        } catch (e) { toast(e.message, "error"); }
      };
    },
  });

  // ---------- Storage (inside Settings) ----------
  // What PLC-Ki keeps, how much, the limits that stop it growing forever, export and clear (/api/admin/storage).
  let storage = null;
  async function loadStorage() {
    try { storage = await api("GET", "/api/admin/storage"); } catch (e) { storage = { error: e.message }; }
  }
  const size = (b) => b == null ? "" : b >= 1073741824 ? (b / 1073741824).toFixed(1) + " GB" : b >= 1048576 ? (b / 1048576).toFixed(1) + " MB" : b >= 1024 ? Math.round(b / 1024) + " KB" : PK.t("{n} bytes", { n: b });
  const EXPORT = {
    changes: ["/api/admin/storage/changes.csv", "CSV"],
    recordings: ["/api/admin/storage/recordings.zip", "Zip"],
    memory: ["/api/memory/export.csv?days=3650", "CSV"],
    events: ["/api/events.csv?days=3660&kind=alarm,halt,blocked,state,cpu,link,write,monitor", "CSV"],
    steps: ["/api/events.csv?days=3660&kind=step,output", "CSV"],
  };
  const LIMITS = [
    ["productionDays", PK.T("Production log"), PK.T("days"), PK.T("400 days lets a report compare with the same weeks last year.")],
    ["eventDays", PK.T("Machine events"), PK.T("days"), PK.T("Alarms, halts, connection drops, values changed. As long as the production log, so every shift report has its shift log.")],
    ["stepEventDays", PK.T("Steps and outputs"), PK.T("days"), PK.T("About 20 per cycle, 1 to 5 MB a day, so kept for a shorter time.")],
    ["valueFullDays", PK.T("Values, every read"), PK.T("days"), PK.T("Big: about 16 MB a day on the demo, several times that on a real machine. Charts of up to 2 hours use it.")],
    ["valueDays", PK.T("Values, a minute at a time"), PK.T("days"), PK.T("Lowest, highest and last value of each minute: 1 to 5 MB a day, so a week of trends stays small.")],
    ["changeLogDays", PK.T("Change log"), PK.T("days"), PK.T("Who changed which value. Older files go first.")],
    ["changeLogMb", PK.T("Change log, at most"), "MB", PK.T("Whichever comes first: the days or the size.")],
    ["recordings", PK.T("Alarm recordings"), PK.T("per machine"), PK.T("The newest ones are kept.")],
    ["alarmOccurrences", PK.T("Alarm memory"), PK.T("occurrences"), PK.T("Ones with a technician's note are never removed.")],
  ];
  const isoDay = (d) => new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 10);
  function storageHtml() {
    if (!storage) return "";
    if (storage.error) return PK.sec("", `${PK.ui.error(storage.error)}${info ? `<p class="mt-4 mb-0 faint">${PK.t("Data folder:")} ${esc(info.dataDir)}</p>` : ""}`);
    const d = storage.disk, l = (draft && draft.limits) || storage.limits;
    const today = isoDay(new Date()), monthAgo = isoDay(new Date(Date.now() - 30 * 864e5));
    const rows = storage.rows.map((r) => {
      const exp = r.kind === "production" && r.count ? `<button class="btn plain small" data-prod-csv>CSV</button>`
        : EXPORT[r.kind] && r.count ? `<a class="btn plain small" href="${EXPORT[r.kind][0]}" download>${EXPORT[r.kind][1]}</a>` : "";
      const clr = r.clear ? `<button class="fg-fault btn plain small" data-clear="${esc(r.kind)}">${PK.t("Clear")}</button>` : "";
      return `<tr><td><b class="semibold">${esc(PK.srv(r.title))}</b><div class="muted small">${esc(PK.srv(r.what))}</div></td>
        <td class="small">${r.where === "memory" ? PK.t("Memory") : `${PK.t("Disk")}<div class="faint mono" style="overflow-wrap:anywhere">${esc(r.folder || "")}</div>`}</td>
        <td class="nowrap small">${esc(PK.srv(r.countText))}${r.bytes != null && r.where === "disk" ? `<div class="faint">${size(r.bytes)}</div>` : ""}</td>
        <td class="nowrap small">${r.oldest ? esc(PK.date(r.oldest, !["production", "events", "steps", "values"].includes(r.kind))) : "–"}</td>
        <td class="small">${esc(PK.srv(r.limit))}</td>
        <td class="nowrap">${exp}${clr}</td></tr>`;
    }).join("");
    const when = storage.lastPrune ? esc(PK.date(storage.lastPrune)) : "";
    const last = storage.lastPrune
      ? (storage.lastRemoved.length ? PK.t("Limits last applied {when}: {what} removed.", { when, what: storage.lastRemoved.map((x) => esc(PK.srv(x.text))).join("; ") }) : PK.t("Limits last applied {when}: nothing was over them.", { when }))
      : PK.t("Limits are applied when PLC-Ki starts and once a day.");
    return PK.sec("", `${d.warning ? `<div class="mb-4 notice red">${icon("info")}<div>${esc(PK.srv(d.warning))}</div></div>` : ""}
      <p class="mt-0 mb-3 small">${PK.t("Data folder:")} <span class="mono">${esc(storage.dataDir)}</span>${d.total ? ` · ${PK.t("{free} free of {total} ({pct} %)", { free: size(d.free), total: size(d.total), pct: Math.round(d.free / d.total * 100) })}` : ""}</p>
      <div style="overflow-x:auto"><table class="tbl"><tr><th>${PK.t("What")}</th><th>${PK.t("Where")}</th><th>${PK.t("How much")}</th><th>${PK.t("Oldest")}</th><th>${PK.t("Kept")}</th><th></th></tr>${rows}</table></div>
      <div class="row" data-prod-range hidden style="gap:10px;margin-top:10px;flex-wrap:wrap;align-items:end">
        <label class="field" style="max-width:180px">${PK.t("Production log from")}<input type="date" id="pr-from" value="${monthAgo}"></label>
        <label class="field" style="max-width:180px">${PK.t("to")}<input type="date" id="pr-to" value="${today}"></label>
        <a class="btn small" data-prod-dl href="#" download>${PK.t("Download CSV")}</a></div>
      <h3 class="mt-5">${PK.t("Limits")}</h3>
      <div class="grid three">${LIMITS.map(([k, t, unit, hint]) => `<label class="field">${PK.t(t)} <span class="hint">${PK.t(unit)}</span>
        <input id="lim-${k}" data-d="limits.${k}" type="number" min="1" value="${l[k]}"><span class="hint">${PK.t(hint)}</span></label>`).join("")}</div>
      <p class="mt-3 mb-0 muted small">${last} ${PK.t("Notes, documents, logins, settings and keys are never removed automatically.")}</p>`);
  }
  function bindStorage(page) {
    if (!storage || storage.error) return;
    const prod = page.querySelector("[data-prod-csv]"), range = page.querySelector("[data-prod-range]"), dl = page.querySelector("[data-prod-dl]");
    const href = () => `/api/admin/storage/production.csv?from=${encodeURIComponent(page.querySelector("#pr-from").value)}&to=${encodeURIComponent(page.querySelector("#pr-to").value)}`;
    if (dl) {
      dl.href = href();
      page.querySelectorAll("#pr-from,#pr-to").forEach((el) => { el.onchange = () => { dl.href = href(); }; });
    }
    if (prod) prod.onclick = () => { range.hidden = !range.hidden; };
    page.querySelectorAll("[data-clear]").forEach((b) => b.onclick = async () => {
      const r = storage.rows.find((x) => x.kind === b.dataset.clear);
      if (!r || !(await PK.confirmSheet({ title: PK.t("Clear the {what}?", { what: PK.lang === "de" ? PK.srv(r.title) : r.title.toLowerCase() }),
        text: `<p class="mt-0">${PK.t("This deletes {what}", { what: esc(PK.srv(r.clear)) })}</p><p><b>${PK.t("It cannot be undone.")}</b> ${PK.t("Export it first if you may need it. The change log notes that you cleared it.")}</p>`,
        ok: PK.t("Delete"), danger: true }))) return;
      b.disabled = true;
      try { const x = await api("POST", "/api/admin/storage/clear", { kind: r.kind }); toast(PK.t("Cleared: {what}.", { what: PK.srv(x.text) })); }
      catch (e) { toast(e.message, "error"); }
      await loadStorage(); PK.draw();
    });
  }

  // ---------- Settings ----------
  let users = null, askUse = null;
  const LENGTHS = [[4000, PK.T("Short")], [8000, PK.T("Normal")], [16000, PK.T("Long")]];
  const money = (x) => "$" + (Number(x) || 0).toFixed(2);
  /** "This month: 12 questions, about $3.40 of $50." for the Assistant card (admins only). */
  function askUseHtml(limit) {
    if (!askUse) return "";
    const m = askUse.month || {};
    const pct = limit > 0 ? Math.min(100, (m.cost || 0) / limit * 100) : 0;
    return `<div class="ask-use"><div class="small"><b class="semibold">${PK.t("This month:")}</b> ${PK.t("{n} question|{n} questions", { n: m.questions || 0 })}, ${PK.t("about {cost}", { cost: money(m.cost) })}${limit > 0 ? ` ${PK.t("of {limit}", { limit: money(limit) })}` : ""}.${pct >= 100 ? ` <b class="ink-fault">${PK.t("Ask is off until next month or a higher limit.")}</b>` : ""}</div>
      ${limit > 0 ? `<div class="ask-bar ${pct >= 100 ? "full" : pct >= 80 ? "near" : ""}" role="img" aria-label="${PK.t("{pct}% of the monthly limit", { pct: Math.round(pct) })}"><span style="width:${pct.toFixed(1)}%"></span></div>` : ""}
      <div class="faint small">${PK.t("An estimate from the words (tokens) Ask sent and got, at Anthropic's list prices. The bill comes from Anthropic.")}</div></div>`;
  }
  // Four tabs, each with one way to save: the unsaved-changes bar (review 4, item 8). The page is drawn from a draft, so a
  // redraw (a login added, a storage row cleared) keeps what was typed and not saved yet. Leaving the tab with changes asks first.
  const SET_TABS = [["machine", PK.T("Machine and Ask")], ["storage", PK.T("Storage")], ["logins", PK.T("Logins and access")], ["language", PK.T("Language and formats")]];
  const SET_SUB = {
    machine: PK.T("The machine model, and the assistant that answers questions about the machine."),
    storage: PK.T("What PLC-Ki keeps on this PC, how much, and for how long."),
    logins: PK.T("Who may open PLC-Ki, from where, and where it opens for each role."),
    language: PK.T("How the pages are written for everyone. Each person can choose their own under My settings."),
  };
  const setTab = (params) => SET_TABS.some(([k]) => k === (params || [])[0]) ? params[0] : "machine";
  const MACHINE_KEYS = ["model", "historyMinutes", "askEnabled", "askEffort", "askModel", "askMaxTokens", "askMaxToolRounds", "askMonthlyLimitUsd"];
  const ACCESS_KEYS = ["allowRemote", "publicView", "port"];
  let draft = null, mountSeq = 0, saving = false;
  const pick = (o, keys) => Object.fromEntries(keys.map((k) => [k, o[k]]));
  const plantLang = () => (state.info && state.info.language) || "en";
  const savedStart = () => Object.fromEntries(ROLES.map(([r]) => [r, (lists.startPages || {})[r] || ""]));
  function freshDraft() {
    return { s: pick(settings, [...MACHINE_KEYS, ...ACCESS_KEYS]), apiKey: "", limits: storage && storage.limits ? { ...storage.limits } : null,
      start: savedStart(), language: plantLang(), format: PK.plantFormat() };
  }
  const differs = (a, b, keys) => keys.some((k) => String(a[k]) !== String(b[k]));
  /** What on this tab differs from what is saved. */
  function changes(tab) {
    if (!draft || !settings) return {};
    if (tab === "machine") return { settings: differs(draft.s, settings, MACHINE_KEYS) || !!draft.apiKey.trim() };
    if (tab === "storage") return { limits: !!(draft.limits && storage && storage.limits) && differs(draft.limits, storage.limits, LIMITS.map(([k]) => k)) };
    if (tab === "logins") return { settings: differs(draft.s, settings, ACCESS_KEYS), start: differs(draft.start, savedStart(), ROLES.map(([r]) => r)) };
    if (tab === "language") return { language: draft.language !== plantLang(), format: draft.format !== PK.plantFormat() };
    return {};
  }
  const anyChange = (tab) => Object.values(changes(tab)).some(Boolean);
  function track(tab) { PK.dirty(anyChange(tab) ? () => saveTab(tab) : null); }

  async function saveTab(tab) {
    if (saving) return;
    saving = true;
    const ch = changes(tab);
    let ok = true, note = PK.t("Saved.");
    try {
      if (tab === "storage" && ch.limits) {
        const limits = Object.fromEntries(LIMITS.map(([k]) => [k, Number(draft.limits[k])]));
        // Tighter limits remove data now: say exactly what, and ask first
        const dry = await api("PUT", "/api/admin/storage/limits", { limits, dryRun: true });
        if (dry.removed.length && !(await PK.confirmSheet({ title: PK.t("Save these limits?"),
          text: `<p class="mt-0">${PK.t("They remove now:")}</p><ul>${dry.removed.map((x) => `<li>${esc(PK.srv(x.text))}${x.bytes ? ` (${size(x.bytes)})` : ""}</li>`).join("")}</ul><p><b>${PK.t("This cannot be undone.")}</b></p>`,
          ok: PK.t("Save and remove"), danger: true }))) { saving = false; return; }     // still unsaved: the bar stays
        const done = await api("PUT", "/api/admin/storage/limits", { limits, dryRun: false });
        if (done.removed.length) note = PK.t("Saved. Removed: {what}.", { what: done.removed.map((x) => PK.srv(x.text)).join("; ") });
        await loadSettings();
        await loadStorage();
        draft.limits = storage.limits ? { ...storage.limits } : draft.limits;
      }
      if ((tab === "machine" || tab === "logins") && ch.settings) {
        const keys = tab === "machine" ? MACHINE_KEYS : ACCESS_KEYS;
        await saveSettings({ ...pick(draft.s, keys), ...(tab === "machine" ? { apiKey: draft.apiKey.trim() || null } : {}) });
        Object.assign(draft.s, pick(settings, keys));
        draft.apiKey = "";
        if (tab === "machine") { try { askUse = await api("GET", "/api/admin/ask/usage"); } catch { /* keeps the last */ } }
        if (info.restartNeeded) note = PK.t("Saved. Apply to use the new settings.");
      }
      if (tab === "logins" && ch.start) {
        try { lists.startPages = await api("PUT", "/api/admin/start-pages", { ...draft.start }); if (!ch.settings) note = PK.t("Start pages saved. They are used from the next login."); }
        catch (e) { ok = false; toast(e.message, "error"); }
      }
      if (tab === "language") {
        if (ch.format) { const r = await api("PUT", "/api/admin/format", { format: draft.format }); if (state.info) { state.info.format = r.format; state.info.formatChosen = true; } }
        if (ch.language) await api("PUT", "/api/admin/language", { language: draft.language });
        if (ch.language && state.info) state.info.language = draft.language;
        note = PK.t("Saved for this plant.");
        // The pages are written again in the new language, unless this person chose their own
        if (ch.language && !PK.prefs.get("language", "")) { PK.dirty(null); toast(note); setTimeout(() => location.reload(), 600); saving = false; return; }
      }
      if (ok) toast(note);
    } catch (e) { toast(e.message, "error"); }
    saving = false;
    track(tab);         // what is still different keeps the bar up
    PK.draw();
  }

  PK.view("setup/settings", {
    guide: "setup",
    async mount(page, params) {
      const tab = setTab(params), seq = ++mountSeq;
      // Back on a tab already seen: draw it at once, then refresh
      if (settings && lists && users) { draft = freshDraft(); PK.draw(); } else draft = null;
      try { await loadSettings(); users = await api("GET", "/api/admin/users"); } catch (e) { toast(e.message, "error"); }
      if (tab === "machine") { try { askUse = await api("GET", "/api/admin/ask/usage"); } catch { askUse = null; } }
      if (tab === "storage") await loadStorage();
      if (seq !== mountSeq) return;           // another tab opened meanwhile
      if (!PK.isDirty && settings) draft = freshDraft();
      PK.draw();
    },
    render(params) {
      const tab = setTab(params);
      const nav = PK.ui.tabs(PK.t("Settings"), SET_TABS.map(([k, l]) => ({ label: PK.t(l), on: k === tab, href: "#/setup/settings" + (k === "machine" ? "" : "/" + k) })));
      if (!settings || !draft) return PK.hero({ cls: "hero compact", before: setupNav("settings"), title: PK.t("Settings"), extra: nav }) + PK.sec("", PK.ui.loading("cards", { rows: 3 }));
      const s = settings, d = draft.s;
      const keyText = { settings: PK.t("A key is saved here."), environment: PK.t("A key is already set on this PC (outside PLC-Ki). A key entered here replaces it."), none: PK.t("No key yet: the Ask box stays off until you add one.") }[info.apiKey];
      const sw = (id, on, title, text) => `<div class="row"><div class="grow"><b class="semibold">${title}</b><div class="muted small">${text}</div></div><label class="switch"><input type="checkbox" id="${id}" data-d="s.${id}" ${on ? "checked" : ""}><span></span></label></div>`;
      const head = PK.hero({ cls: "hero compact", before: setupNav("settings"), title: PK.t("Settings"), sub: PK.t(SET_SUB[tab]), extra: `${restartBar()}${nav}` });
      let body = "";
      if (tab === "machine") body = PK.sec("alt", `<div class="grid two">
          <div class="card stack"><h3>${PK.t("Machine")}</h3>
            <label class="field">${PK.t("Machine model")}<select id="model" data-d="s.model">${(lists.models || []).map((f) => `<option value="${esc(f.path)}" ${f.path === d.model ? "selected" : ""}>${esc(f.label)}${f.uploaded ? ` ${PK.t("(imported)")}` : ""}</option>`).join("")}</select><span class="hint">${PK.t("New models come from the Program page. Its program export is picked with it.")}</span>${runNote("model")}</label>
            <label class="field">${PK.t("History kept (minutes)")}<input id="historyMinutes" data-d="s.historyMinutes" type="number" value="${esc(d.historyMinutes)}"><span class="hint">${PK.t("For questions like \"what happened before the alarm?\"")}</span></label></div>
          <div class="card stack"><h3>${PK.t("Assistant")}</h3>
            ${sw("askEnabled", d.askEnabled, PK.t("Ask box"), PK.t("Ki answers questions about the machine."))}
            <label class="field">${PK.t("Claude API key")} <span class="hint">${esc(keyText)}</span><input id="apiKey" data-d="apiKey" type="password" autocomplete="new-password" placeholder="sk-ant-…" value="${esc(draft.apiKey)}"></label>
            <label class="field">${PK.t("Model")}<select id="askModel" data-d="s.askModel">${(askUse ? askUse.models : [{ id: s.askModel, name: s.askModel, note: "" }]).concat(askUse && !askUse.models.some((m) => m.id === s.askModel) ? [{ id: s.askModel, name: s.askModel, note: PK.t("set in the config file") }] : [])
              .map((m) => `<option value="${esc(m.id)}" ${m.id === d.askModel ? "selected" : ""}>${esc(m.name)}${m.note ? " · " + esc(m.note) : ""}</option>`).join("")}</select>${runNote("askModel")}</label>
            <label class="field">${PK.t("How hard Ki thinks")}<select id="askEffort" data-d="s.askEffort">${[PK.T("low"), PK.T("medium"), PK.T("high"), PK.T("max")].map((x) => `<option value="${x}" ${d.askEffort === x ? "selected" : ""}>${PK.t(x)}</option>`).join("")}</select><span class="hint">${PK.t("Higher gives better answers to hard questions, but slower and dearer.")}</span></label>
            <div class="grid two"><label class="field">${PK.t("Answer length")}<select id="askMaxTokens" data-d="s.askMaxTokens">${LENGTHS.concat(LENGTHS.some(([n]) => n === s.askMaxTokens) ? [] : [[s.askMaxTokens, PK.t("{n} tokens", { n: s.askMaxTokens })]])
              .map(([n, t]) => `<option value="${n}" ${String(n) === String(d.askMaxTokens) ? "selected" : ""}>${esc(PK.t(t))}</option>`).join("")}</select>${runNote("askMaxTokens")}</label>
            <label class="field">${PK.t("Lookups per question")}<input id="askMaxToolRounds" data-d="s.askMaxToolRounds" type="number" min="2" max="30" value="${esc(d.askMaxToolRounds)}"><span class="hint">${PK.t("2 to 30. More lets Ki look further, at more cost.")}</span>${runNote("askMaxToolRounds")}</label></div>
            <label class="field" style="max-width:240px">${PK.t("Monthly limit in dollars")}<input id="askLimit" data-d="s.askMonthlyLimitUsd" type="number" min="0" step="1" inputmode="decimal" value="${Number(d.askMonthlyLimitUsd) > 0 ? esc(d.askMonthlyLimitUsd) : ""}" placeholder="${PK.t("No limit")}"><span class="hint">${PK.t("When it is reached, Ask stops until next month. Works at once.")}</span></label>
            ${askUseHtml(s.askMonthlyLimitUsd)}</div>
        </div>`);
      else if (tab === "storage") body = storage ? storageHtml() : PK.sec("", PK.ui.loading("cards", { rows: 2 }));
      else if (tab === "logins") body = PK.sec("alt", `<div class="grid two">
          <div class="card stack"><h3>${PK.t("Access")}</h3>
            ${sw("allowRemote", d.allowRemote, PK.t("Other computers may open PLC-Ki"), PK.t("Off: only this PC. On: anyone on the network who has a login."))}
            ${sw("publicView", d.publicView, PK.t("Viewing without login"), PK.t("For a screen on the line. Setup still needs an administrator."))}
            <label class="field" style="max-width:200px">${PK.t("Port")}<input id="port" data-d="s.port" type="number" value="${esc(d.port)}">${runNote("port")}</label></div>
          <div class="card stack"><h3>${PK.t("Start page for each role")}</h3>
            <p class="m-0 muted small">${PK.t("Where PLC-Ki opens after logging in, when no page is in the address. Each person can choose their own under My settings.")}</p>
            ${ROLES.map(([r]) => { const now = draft.start[r] || "";
              return `<label class="field">${esc(PK.t(ROLE_PLURAL[r]))}<select data-start-role="${r}" data-d="start.${r}">${PK.startChoices([now, (lists.startPages || {})[r] || ""]).map(([h, l]) => `<option value="${esc(h)}" ${(now || "#/alarm") === h ? "selected" : ""}>${esc(l)}</option>`).join("")}</select></label>`; }).join("")}</div>
        </div>`)
        + PK.sec("", `<h2>${PK.t("Logins")}</h2><p class="sub">${PK.t("Operators see every machine page. Technicians also add documents and change values on PLCSIM. Administrators also see Setup.")}</p>
          <ul class="list logins">${(users || []).map((u) => `<li><span class="ico">${icon("user")}</span>
            <span class="grow"><b class="semibold">${esc(u.name)}</b>${state.user && u.name === state.user.name ? ` ${PK.t("(you)")}` : ""}<div class="faint">${esc(PK.roleTitle(u.role))}</div></span>
            <span class="li-acts">${state.user && u.name !== state.user.name ? `<button class="btn plain small" data-role="${esc(u.name)}" data-now="${esc(u.role)}">${PK.t("Change role")}</button>
              <button class="btn plain small" data-pw="${esc(u.name)}">${PK.t("New password")}</button><button class="fg-fault btn plain small" data-del="${esc(u.name)}">${PK.t("Delete")}</button>` : `<button class="btn plain small" data-pw-own>${PK.t("Change my password")}</button>`}</span></li>`).join("")}</ul>
          <h3 class="mt-5">${PK.t("Add a login")}</h3>
          <div class="grid three"><label class="field">${PK.t("Name")}<input id="nu-name" autocomplete="off"></label>
            <label class="field">${PK.t("Password")} <span class="hint">${PK.t("at least 8 characters")}</span><input id="nu-pw" type="password" autocomplete="new-password"></label>
            <label class="field">${PK.t("Role")}<select id="nu-role">${ROLES.map(([r, , short]) => `<option value="${r}">${esc(PK.roleTitle(r))}: ${esc(PK.t(short))}</option>`).join("")}</select></label></div>
          <div class="mt-3 row end"><button class="btn" data-add-user>${PK.t("Add login")}</button></div>`);
      else body = PK.sec("alt", `<div class="grid two">
          <div class="card stack"><h3>${PK.t("Language")}</h3>
            <p class="m-0 muted small">${PK.t("The language of the pages for everyone. Each person can choose their own under My settings. Alarm texts, step names and tag comments stay as the PLC program has them.")}</p>
            <select id="plant-language" data-d="language" aria-label="${PK.t("Language for this plant")}" style="max-width:100%">${[["en", "English"], ["de", "Deutsch"]].map(([k, l]) =>
              `<option value="${k}" ${draft.language === k ? "selected" : ""}>${l}</option>`).join("")}</select></div>
          <div class="card stack"><h3>${PK.t("Dates and numbers")}</h3>
            <p class="m-0 muted small">${PK.t("How times, dates and decimals are written for everyone. Each person can choose their own under My settings.")}</p>
            <select id="plant-format" data-d="format" aria-label="${PK.t("Dates and numbers for this plant")}" style="max-width:100%">${Object.entries(PK.FORMATS).map(([k, f]) =>
              `<option value="${k}" ${draft.format === k ? "selected" : ""}>${esc(f.label)}</option>`).join("")}</select></div>
        </div>`);
      return head + body;
    },
    after(page, params) {
      const tab = setTab(params);
      bindRestart(page);
      if (!draft) return;
      if (tab === "storage") bindStorage(page);
      // Every setting writes into the draft; the bar shows while the tab differs from what is saved
      page.querySelectorAll("[data-d]").forEach((el) => {
        const keep = () => {
          const [a, b] = el.dataset.d.split(".");
          let x = el.type === "checkbox" ? el.checked : el.value;
          if (el.type === "number") x = Number(x) || 0;
          if (a === "start" && x === "#/alarm") x = "";
          if (b) draft[a][b] = x; else draft[a] = x;
          track(tab);
        };
        PK.on(el, "input", keep); PK.on(el, "change", keep);
      });
      track(tab);
      if (tab !== "logins") return;
      const v = (id) => page.querySelector("#" + id);
      const act = async (fn, ok) => { try { await fn(); toast(ok); users = await api("GET", "/api/admin/users"); PK.draw(); } catch (e) { toast(e.message, "error"); } };
      page.querySelectorAll("[data-role]").forEach((b) => b.onclick = () => {
        const name = b.dataset.role, now = b.dataset.now;
        const s = PK.sheet(`<span class="eyebrow">${PK.t("Login")}</span><h2>${PK.t("Role of {name}", { name: esc(name) })}</h2>
          <p class="m-0 muted">${PK.t("They are logged out on their devices and log in again with the new role.")}</p>
          <div class="stack gap-2">${ROLES.map(([r, text]) => `<button type="button" class="opt-row role-pick ${r === now ? "on" : ""}" data-to="${r}" ${r === now ? 'aria-current="true"' : ""}>
            <span><b>${esc(PK.roleTitle(r))}</b>${r === now ? ` <span class="faint">${PK.t("(now)")}</span>` : ""}<br><span class="muted small">${esc(PK.t(text))}</span></span></button>`).join("")}</div>
          <div class="row end"><button class="btn gray" type="button" data-close>${PK.t("Cancel")}</button></div>`);
        s.el.querySelectorAll("[data-to]").forEach((o) => o.onclick = () => {
          s.close();
          if (o.dataset.to !== now) act(() => api("PUT", `/api/admin/users/${encodeURIComponent(name)}/role`, { role: o.dataset.to }), PK.t("{name} is now {role}.", { name, role: PK.lang === "de" ? PK.roleTitle(o.dataset.to) : PK.roleTitle(o.dataset.to).toLowerCase() }));
        });
      });
      const own = page.querySelector("[data-pw-own]");
      if (own) own.onclick = () => PK.passwordSheet();
      page.querySelectorAll("[data-del]").forEach((b) => b.onclick = async () => {
        if (await PK.confirmSheet({ title: PK.t("Delete {name}?", { name: b.dataset.del }), text: PK.t("They can no longer log in."), ok: PK.t("Delete"), danger: true }))
          act(() => api("DELETE", `/api/admin/users/${encodeURIComponent(b.dataset.del)}`), PK.t("Deleted."));
      });
      page.querySelectorAll("[data-pw]").forEach((b) => b.onclick = () => PK.passwordSheet(b.dataset.pw));
      page.querySelector("[data-add-user]").onclick = () => act(() => api("POST", "/api/admin/users", { name: v("nu-name").value, password: v("nu-pw").value, role: v("nu-role").value }), PK.t("Login added."));
    },
  });

  return { runDiff, setupNav, follow, jobCard, jobProblem, bindJob, restartBar, bindRestart, loadSettings, saveSettings, restart, presets, presetOf,
    get settings() { return settings; }, get info() { return info; } };
})();
