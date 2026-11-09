// PLC page, three tabs:
//   CPU (#/plc): the CPU as it looks on the machine: mode, LEDs, display; Start/Stop for the PLCSIM CPU only.
//   Racks and I/O (#/plc/racks/<station>/<slot>): every rack and module of the TIA project, drawn from its order
//     number, with each channel's address, tag and live value.
//   Diagnostics (#/plc/diagnostics): what the CPU reports for devices, modules and channels, and each drive.
// The old Hardware addresses (#/hardware/...) lead to Racks and I/O (core.js). Drawings are in devices.js.
"use strict";

(() => {
  const { esc, icon } = PK;

  // ---- shared data: the hardware (once, again after a hardware read) and the CPU state (every 2 s on these pages) ----
  let hw = null, hwError = null, hwLoading = false;
  let cpu = null, cpuError = null;
  async function loadHw(force) {
    if ((hw || hwError) && !force) return;
    if (hwLoading) return;
    hwLoading = true;
    try {
      const r = await PK.api("GET", "/api/hardware");
      if (r.available) { hw = r; hwError = null; } else { hw = null; hwError = r.error || "No hardware configuration."; }
    } catch (e) { hwError = e.message; }
    hwLoading = false;
    PK.draw();
  }
  async function loadCpu() {
    try { cpu = await PK.api("GET", "/api/cpu"); cpuError = null; } catch (e) { cpuError = e.message; }
    PK.draw();
  }
  setInterval(() => {
    const n = PK.current && PK.current.name;
    if (n === "plc" && !document.hidden) loadCpu();
  }, 2000);

  // ---- drive and PROFINET diagnostics: what the CPU reports per station/module, and each drive's state ----
  let diag = null, diagError = null;
  async function loadDiag() {
    try { diag = await PK.api("GET", "/api/diagnostics"); diagError = null; } catch (e) { diagError = e.message; }
    PK.draw();
  }
  setInterval(() => {
    const n = PK.current && PK.current.name;
    if ((n === "plc" || n === "alarms" || (n === "alarm" && PK.cpuStop())) && !document.hidden) loadDiag();
  }, 2500);
  const sevCls = { fault: "bad", warning: "warn", maintenance: "warn", info: "info", ok: "ok" };
  const sevText = { fault: "Fault", warning: "Warning", maintenance: "Maintenance", info: "Info", ok: "OK" };
  const kindText = { profinet: "PROFINET", channel: "Channel", module: "Module", drive: "Drive" };
  const driveCls = { Fault: "bad", Inhibited: "warn", Running: "ok", Ready: "info", Off: "", Unknown: "" };
  const driveText = { Fault: "Fault", Inhibited: "Switching on inhibited", Running: "Running", Ready: "Ready", Off: "Off", Unknown: "Not read" };

  function knowledgeBlock(k) {
    if (!k) return "";
    return `<details class="more" style="margin-top:8px"><summary><b>${esc(k.title)}</b> <span class="faint small">general Siemens knowledge</span></summary>
      <p class="muted small" style="margin:8px 0">${esc(k.meaning)}</p>
      ${k.checks && k.checks.length ? `<h3 style="margin:8px 0 4px">What to check</h3><ol class="small" style="margin:0;padding-left:20px">${k.checks.slice(0, 4).map((c) => `<li>${esc(c)}</li>`).join("")}</ol>` : ""}
      ${k.fixes && k.fixes.length ? `<h3 style="margin:10px 0 4px">How to fix</h3><ol class="small" style="margin:0;padding-left:20px">${k.fixes.slice(0, 4).map((c) => `<li>${esc(c)}</li>`).join("")}</ol>` : ""}
      ${k.safety ? `<p class="small" style="margin:8px 0 0"><b>Safety:</b> ${esc(k.safety)}</p>` : ""}</details>`;
  }

  function problemItem(p) {
    const where = p.station ? `<a href="#/plc/racks/${encodeURIComponent(p.station)}${p.slot != null ? "/" + encodeURIComponent(p.slot) : ""}">${esc(p.station)}${p.module ? " · " + esc(p.module) : p.slot != null ? " · slot " + esc(p.slot) : ""}</a>` : `<span class="faint">Device not found in the hardware</span>`;
    return `<li style="flex-wrap:wrap;align-items:flex-start">
      <span class="pill ${sevCls[p.severity] || ""}"><span class="dot"></span>${esc(sevText[p.severity] || p.severity)}</span>
      <span style="flex:1;min-width:220px"><b style="font-weight:600">${esc(p.text)}</b>
        <div class="small">${where} · <span class="faint">${esc(kindText[p.kind] || p.kind)}${p.codes && p.codes.length ? " · " + p.codes.map(esc).join(", ") : ""}${p.since ? " · since " + esc(PK.date(p.since, true)) : ""}</span></div>
        ${p.info ? `<div class="faint small" style="margin-top:4px">${esc(p.info)}</div>` : ""}
        ${knowledgeBlock(p.knowledge)}</span>
      <button class="btn b small" data-ask="${esc(`The PLC reports "${p.text}"${p.station ? " for " + p.station : ""}. What does it mean here, what is affected, and how do I fix it?`)}">Ask</button></li>`;
  }

  // The list of what the CPU reports, for the PLC page (Diagnostics, Racks and I/O) and the Alarms page
  function problemsCard(opts) {
    opts = opts || {};
    const d = diag;
    const head = `<h3>${opts.title || "Reported by the PLC"}</h3><p class="muted small" style="margin:0 0 10px">The CPU's own system diagnostics: PROFINET devices, modules, channels and drives. Read live, nothing to set up in the PLC.</p>`;
    if (!d) return `<div class="card">${head}<p class="muted">${esc(diagError || "Reading…")}</p></div>`;
    const live = d.connection === "connected" || d.connection === "snapshot";
    let body;
    if (!live) body = `<p class="muted" style="margin:0">PLC-Ki is not reading the PLC right now.</p>`;
    else if (!d.alarmsRead) body = `<p class="muted" style="margin:0">${esc(d.alarmsError || "This connection cannot read the CPU's diagnostics (PLC-Ki Link does not pass them on yet).")}</p>`;
    else if (!d.problems.length) body = `<div class="notice green">${icon("check")}<div>No device problem reported. ${d.cpuAlarms ? `The CPU has ${d.cpuAlarms} other alarm${d.cpuAlarms === 1 ? "" : "s"} from the program.` : ""}</div></div>`;
    else body = `<ul class="list">${d.problems.filter((p) => !opts.station || p.station === opts.station).map(problemItem).join("") || `<li class="muted">Nothing reported for this device.</li>`}</ul>`;
    return `<div class="card">${head}${body}${d.alarmsTime && live ? `<p class="faint small" style="margin:8px 0 0">Read ${esc(PK.clock(d.alarmsTime))}</p>` : ""}</div>`;
  }

  function driveCard(dr) {
    const cls = driveCls[dr.state] || "";
    const facts = [];
    if (dr.fault) facts.push(["Fault", dr.fault]);
    if (dr.warning) facts.push(["Warning", dr.warning]);
    if (dr.speedPercent != null) facts.push(["Speed", dr.speedPercent + " %"]);
    else if (dr.speed != null) facts.push(["Speed", String(Math.round(dr.speed * 10) / 10)]);
    if (dr.statusWord != null) facts.push(["Status word", "16#" + dr.statusWord.toString(16).toUpperCase().padStart(4, "0")]);
    if (dr.telegram) facts.push(["Telegram", dr.telegram]);
    if (dr.block) facts.push(["Drive block", dr.block]);
    const vals = dr.values || [];
    const table = vals.length ? `<details class="more" style="margin-top:8px"><summary><span class="small">Values read (${vals.length})</span></summary>
      <table class="tbl" style="margin-top:6px"><tr><th>Value</th><th>Tag</th><th>Now</th></tr>${vals.map((v) => `<tr><td>${esc(v.label)}</td><td class="mono small">${esc(v.from)}</td>
        <td>${v.error ? `<span class="faint small" title="${esc(v.error)}">not read</span>` : v.value === true || v.value === false ? `<span class="chip ${v.value ? "t" : "f"}" style="margin:0">${v.value ? "TRUE" : "FALSE"}</span>` : v.value == null ? "…" : `<span class="chip" style="margin:0">${esc(v.value)}</span>`}</td></tr>`).join("")}</table></details>` : "";
    return `<div class="card drive-card">
      <div class="row" style="gap:10px;align-items:baseline;flex-wrap:wrap"><h3 style="margin:0">${dr.station ? `<a href="#/plc/racks/${encodeURIComponent(dr.station)}">${esc(dr.name)}</a>` : esc(dr.name)}</h3>
        <span class="muted small">${esc([dr.title, dr.orderNumber, dr.ip].filter(Boolean).join(" · "))}</span><span class="spacer"></span>
        <span class="pill ${cls}"><span class="dot"></span>${esc(driveText[dr.state] || dr.state)}</span></div>
      ${dr.text ? `<p style="margin:8px 0 0">${esc(dr.text)}</p>` : ""}
      ${dr.problems && dr.problems.length ? `<ul class="small" style="margin:6px 0 0;padding-left:20px">${dr.problems.map((x) => `<li>${esc(x)}</li>`).join("")}</ul>` : ""}
      ${facts.length ? `<div class="hw-fields">${facts.map(([a, b]) => `<div><span>${esc(a)}</span><b>${esc(b)}</b></div>`).join("")}</div>` : ""}
      ${knowledgeBlock(dr.knowledge)}
      ${(dr.reported || []).length ? `<p class="small" style="margin:8px 0 0"><b>The CPU reports:</b> ${dr.reported.map((p) => esc(p.text)).join("; ")}</p>` : ""}
      ${table}
      ${(dr.notes || []).length ? `<p class="faint small" style="margin:8px 0 0">${dr.notes.map(esc).join(" ")}</p>` : ""}
      ${dr.state === "Fault" || dr.state === "Inhibited" ? `<div class="row" style="margin-top:10px"><button class="btn b small" data-ask="${esc(`Drive ${dr.name} ${dr.state === "Fault" ? "has a fault" : "has switching on inhibited"}${dr.fault ? " (" + dr.fault + ")" : ""}. Why, what does it stop, and how do I fix it?`)}">Ask</button></div>` : ""}
    </div>`;
  }

  function drivesSection(only) {
    const list = diag && diag.drives ? diag.drives.filter((d) => !only || d.station === only) : [];
    if (!list.length) return "";
    return `<div class="stack" style="margin-top:16px"><h3 style="margin:0">Drives</h3>${list.map(driveCard).join("")}</div>`;
  }

  /** For the Alarms page: device problems and drives in fault, or nothing when all is well. */
  PK.deviceDiagnostics = () => {
    if (!diag) { loadDiag(); return ""; }
    const bad = (diag.drives || []).filter((d) => d.state === "Fault" || d.state === "Inhibited");
    if (!diag.problems.length && !bad.length) return "";
    return `<div style="margin-bottom:16px">${problemsCard({ title: "Hardware and drives" })}${bad.map(driveCard).join("")}</div>`;
  };
  /** The CPU's own messages (newest first), for the Active alarm page while the CPU is in STOP. null while not read. */
  let diagAsked = 0;
  PK.cpuMessages = () => {
    // Asked from a render: at most one request every few seconds, also when it fails (the interval keeps it fresh)
    if (!diag) { if (Date.now() - diagAsked > 5000) { diagAsked = Date.now(); loadDiag(); } return null; }
    return diag.alarmsRead ? diag.messages || [] : null;
  };
  function wireAsk(el) {
    el.querySelectorAll(".diag-zone [data-ask]").forEach((b) => PK.on(b, "click", () => window.PKAsk && window.PKAsk.open(b.dataset.ask)));
  }
  PK.wireDeviceDiagnostics = wireAsk;

  // ---- live channel values: the page names the tags it shows, the server reads them next to the model tags ----
  const values = {};
  let watchAt = null;
  let watched = [];
  setInterval(async () => {
    if (!PK.current || PK.current.name !== "plc" || !watched.length || document.hidden) return;
    try {
      const r = await PK.api("POST", "/api/watch", { symbols: watched.map((c) => ({ symbol: c.symbol, type: c.modelType || null })) });
      for (const [k, v] of Object.entries(r.values || {})) {
        if (v && (v.value === "True" || v.value === "False")) v.value = v.value === "True";
        values[k] = v;
      }
      watchAt = r.time;
      PK.draw();
    } catch { /* next time */ }
  }, 1500);

  // ---- what the CPU says: mode text, colours, and the LEDs as the device would show them ----
  function modeOf(c) {
    if (!c) return { text: "…", cls: "", state: null, from: "" };
    const sim = c.plcsim && c.plcsim.applies && c.plcsim.found ? c.plcsim : null;
    const read = c.mode && !c.mode.error && c.mode.state && c.mode.state !== "Unknown" && c.mode.state !== "NotSupported" ? c.mode : null;
    const live = c.connection === "connected" || c.connection === "snapshot";
    const state = (sim && sim.state) || (read && live ? read.state : null);
    const from = sim ? "PLCSIM" : read && live ? (c.connection === "snapshot" ? "test values" : "the PLC") : "";
    const map = {
      Run: ["RUN", "ok"], Stop: ["STOP", "warn"], Startup: ["STARTUP", "info"], Booting: ["STARTING", "info"],
      Halt: ["HOLD", "warn"], Hold: ["HOLD", "warn"], Freeze: ["FROZEN", "warn"], Defective: ["DEFECT", "bad"],
      Off: ["OFF", "bad"], ShuttingDown: ["SHUTTING DOWN", "warn"],
    };
    const [text, cls] = map[state] || [state ? String(state).toUpperCase() : "NOT READ", state ? "warn" : ""];
    return { text, cls, state, from, sw: read && read.switch, time: read && read.time };
  }

  function ledsOf(c, mode) {
    const sim = c && c.plcsim && c.plcsim.found && c.plcsim.leds;
    const fromSim = (name) => {
      if (!sim) return null;
      const k = Object.keys(sim).find((x) => x.toLowerCase() === name);
      if (!k) return null;
      const v = String(sim[k]).toLowerCase();
      return v === "on" ? "on" : v.startsWith("flash") ? "flash" : v === "off" ? "off" : null;
    };
    const s = mode.state;
    let run = s === "Run" ? "on" : s === "Stop" || s === "Halt" || s === "Hold" ? "on" : s === "Startup" || s === "Booting" ? "flash" : s ? "off" : "unknown";
    let runColor = s === "Run" || s === "Startup" || s === "Booting" ? "g" : "y";
    const simRun = fromSim("run"), simStop = fromSim("stop");
    if (simRun === "on" || simRun === "flash") { run = simRun; runColor = "g"; }
    else if (simStop === "on" || simStop === "flash") { run = simStop; runColor = "y"; }
    const error = fromSim("error") || (s === "Defective" ? "on" : "off");
    const maint = fromSim("maint") || "off";
    return { run, runColor, error, maint, real: !!sim };
  }

  // ---- the CPU display: a few pages, like the real one ----
  const PAGES = ["Overview", "Diagnostics", "Settings", "Modules"];
  let page = 0;
  function screen(c, mode) {
    return (x, y, w, h) => {
      const f = h / 10.5, pad = f * 0.6;
      const bar = { ok: "#2fa84f", warn: "#d99a1e", bad: "#c9463a", info: "#2f7fd9" }[mode.cls] || "#59636d";
      let s = `<rect x="${x}" y="${y}" width="${w}" height="${h}" rx="${f * 0.3}" fill="#0b1016"/>`;
      s += `<rect x="${x}" y="${y}" width="${w}" height="${f * 1.7}" fill="${bar}"/>`;
      s += `<text x="${x + pad}" y="${y + f * 1.2}" font-size="${f}" font-weight="700" fill="#fff">${esc(mode.text)}</text>`;
      // The station name takes what the mode word leaves free; a longer name is cut with an ellipsis
      const room = w - pad * 3 - String(mode.text).length * f * 0.68, sf = f * 0.85;
      const maxCh = Math.max(0, Math.floor(room / (sf * 0.58)));
      let name = String((c && c.station) || "PLC");
      if (name.length > maxCh) name = maxCh > 1 ? name.slice(0, maxCh - 1).trimEnd() + "…" : "";
      s += `<text x="${x + w - pad}" y="${y + f * 1.2}" font-size="${sf}" fill="#fff" text-anchor="end">${esc(name)}</text>`;
      const rows = [];
      const cp = (c && c.cpu) || {};
      if (page === 0) {
        rows.push([cp.title || "CPU", ""], ["Order no.", cp.orderNumber || "—"], ["Firmware", cp.firmware || "—"]);
        rows.push(["Mode switch", mode.sw || "—"]);
        if (cp.safety) rows.push(["Fail-safe", "F-CPU"]);
      } else if (page === 1) {
        const n = c ? c.alarms : 0;
        rows.push(["Machine alarms", String(n ?? "—")]);
        const al = (PK.state.status && PK.state.status.activeAlarms) || [];
        al.slice(0, 3).forEach((a) => rows.push([a.id, a.text]));
        if (!al.length) rows.push(["", "No alarm active"]);
      } else if (page === 2) {
        const ips = (cp.ips && cp.ips.length ? cp.ips : (c && c.stationIps) || []);
        ips.slice(0, 3).forEach((ip, i) => rows.push([`X${i + 1} IP`, ip]));
        if (!ips.length) rows.push(["IP", (c && c.address) || "—"]);
        rows.push(["Read by", "PLC-Ki"]);
      } else {
        const st = hw && hw.stations && hw.stations.find((s2) => s2.name === (c && c.station));
        (st ? st.modules : []).slice(0, 6).forEach((m) => rows.push([`Slot ${m.slot ?? "?"}`, m.title || m.name]));
        if (!st) rows.push(["", "Hardware not read yet"]);
      }
      const top = y + f * 3.1, lh = f * 1.32, maxRows = Math.floor((h - f * 5.2) / lh);
      rows.slice(0, maxRows).forEach(([a, b], i) => {
        const yy = top + i * lh;
        if (a) s += `<text x="${x + pad}" y="${yy}" font-size="${f * 0.72}" fill="#8fa0b0">${esc(String(a).slice(0, 14))}</text>`;
        s += `<text x="${a ? x + w * 0.4 : x + pad}" y="${yy}" font-size="${f * 0.72}" fill="#e8eef3">${esc(String(b).slice(0, a ? 20 : 32))}</text>`;
      });
      // menu icons along the bottom, the current one lit
      const iw = w / PAGES.length;
      PAGES.forEach((p, i) => {
        const cx = x + iw * (i + 0.5), cy = y + h - f * 1.05;
        s += `<rect x="${cx - iw * 0.42}" y="${cy - f * 0.8}" width="${iw * 0.84}" height="${f * 1.45}" rx="${f * 0.3}" fill="${i === page ? "#2b3a48" : "transparent"}"/>`;
        s += `<text x="${cx}" y="${cy + f * 0.25}" font-size="${f * 0.58}" fill="${i === page ? "#fff" : "#7f8c97"}" text-anchor="middle">${esc(p.length > 9 ? p.slice(0, 5) + "." : p)}</text>`;
      });
      return s;
    };
  }

  // ---------------------------------------------------------------- PLC page
  function plcCpuModule() {
    const c = cpu || {};
    if (c.cpu) return c.cpu;
    const p = PK.state.program;
    return { title: "CPU", name: (p && p.plc) || "PLC", family: "s7-1500", kind: "cpu", widthMm: 70, display: true, orderNumber: p && p.cpu };
  }

  async function switchMode(mode) {
    const c = cpu || {};
    const ip = (c.plcsim && c.plcsim.plcsimIp) || c.address;
    const ok = await PK.confirmSheet(mode === "stop"
      ? { title: "Stop the PLCSIM CPU?", text: `The program in the simulated CPU at <b>${esc(ip)}</b> stops. Its outputs go to their substitute values (usually off) and PLC-Ki shows the machine standing still.<br><br>This is PLCSIM, not a real PLC.`, ok: "Stop the CPU", danger: true }
      : { title: "Start the PLCSIM CPU?", text: `The simulated CPU at <b>${esc(ip)}</b> goes to RUN and the program starts from its startup blocks.<br><br>This is PLCSIM, not a real PLC.`, ok: "Start the CPU" });
    if (!ok) return;
    busy = mode;
    PK.draw();
    try {
      await PK.api("POST", "/api/admin/cpu/mode", { mode });
      PK.toast(mode === "stop" ? "The PLCSIM CPU is in STOP." : "The PLCSIM CPU is in RUN.");
    } catch (e) { PK.toast(e.message, "error"); }
    busy = null;
    loadCpu();
  }
  let busy = null;

  // ---------------------------------------------------------------- CPU tab
  const cpuTab = {
    render() {
      const c = cpu;
      if (!c) return PK.sec("", `<p class="muted">${cpuError ? esc(cpuError) : "Reading the CPU…"}</p>`);
      const mode = modeOf(c);
      const leds = ledsOf(c, mode);
      const m = plcCpuModule();
      const k = window.innerWidth < 700 ? 2.9 : 4;
      const dev = PKDev.single({ ...m, family: m.family || "s7-1500", kind: "cpu" }, { k, leds, running: mode.state === "Run", screen: (m.display !== false && (m.family || "s7-1500") === "s7-1500") ? screen(c, mode) : null, keys: true });

      const admin = c.admin;
      const sim = c.plcsim || {};
      const can = c.canSwitch;
      const why = !admin ? "Only an administrator can start or stop the PLCSIM CPU."
        : !sim.applies ? (sim.why || "Start and Stop work on the PLCSIM CPU only.")
        : !sim.found ? (sim.error || "The PLCSIM CPU was not found.") : "";
      const modeCard = `<div class="card plc-mode">
        <div class="eyebrow">Operating mode</div>
        <div class="plc-mode-big ${mode.cls}" aria-live="polite"><span class="dot"></span>${esc(mode.text)}</div>
        <p class="muted small" style="margin:6px 0 0">${[mode.sw ? "Mode switch " + mode.sw : "", mode.from ? "from " + mode.from : "", c.address ? c.address : ""].filter(Boolean).map(esc).join(" · ") || "The CPU mode was not read yet."}</p>
        ${c.mode && c.mode.error ? `<p class="faint small" style="margin:4px 0 0">${esc(c.mode.error)}</p>` : ""}
        <div class="row" style="margin-top:16px;gap:10px">
          <button class="btn" type="button" data-mode="run" ${can && mode.state !== "Run" && !busy ? "" : "disabled"}>${icon("play")}${busy === "run" ? "Starting…" : "Start"}</button>
          <button class="btn red-tinted" type="button" data-mode="stop" ${can && mode.state !== "Stop" && !busy ? "" : "disabled"}>${icon("stop")}${busy === "stop" ? "Stopping…" : "Stop"}</button>
          ${sim.applies && sim.found ? `<span class="tag blue">PLCSIM</span>` : `<span class="tag">Read-only</span>`}
        </div>
        ${why ? `<p class="faint small" style="margin:10px 0 0">${esc(why)}</p>` : `<p class="faint small" style="margin:10px 0 0">Asks you first. Real PLCs are never started or stopped from PLC-Ki.</p>`}
      </div>`;

      const tiles = [
        [m.title || "CPU", "CPU"],
        [m.orderNumber || "—", "Order number"],
        [m.firmware || "—", "Firmware"],
        [((m.ips && m.ips.length ? m.ips : c.stationIps) || [c.address]).filter(Boolean).join(", ") || "—", "Addresses"],
        [c.station || "—", "Station"],
        [String(c.modules || 0), "Modules in the rack"],
        [String(c.alarms ?? 0), "Machine alarms active", c.faults ? "bad" : c.alarms ? "warn" : "ok"],
        [diag && diag.alarmsRead ? String(diag.faults) : "—", "Device faults reported", diag && diag.faults ? "bad" : diag && diag.alarmsRead ? "ok" : ""],
        [leds.real ? "From PLCSIM" : "From the mode", "LEDs"],
      ];
      const info = `<div class="card"><div class="kpis plc-kpis">${tiles.map(([v, l, cls]) => `<div class="k ${cls || ""}"><b>${esc(v)}</b><span>${esc(l)}</span></div>`).join("")}</div>
        ${leds.real ? "" : `<p class="faint small" style="margin:14px 0 0">RUN/STOP follows the mode the CPU reports. ERROR and MAINT are not read from this CPU, so they stay dark here even when the real LEDs are lit.</p>`}
        <div class="row" style="margin-top:14px"><a class="btn b small" href="#/plc/racks">${icon("rack")}Racks and I/O</a>${diag && diag.problems && diag.problems.length
          ? `<a class="btn small" href="#/plc/diagnostics">${icon("warn")}${diag.problems.length} reported in Diagnostics</a>` : ""}</div></div>`;

      const pageBtns = `<div class="segmented plc-pages" role="group" aria-label="Display page">${PAGES.map((p, i) => `<button type="button" data-page="${i}" class="${i === page ? "on" : ""}">${p}</button>`).join("")}</div>`;
      const left = `<div class="card plc-dev"><div class="plc-dev-pic">${dev}</div>${m.display !== false && (m.family || "s7-1500") === "s7-1500" ? pageBtns : ""}
        <p class="faint small" style="margin:10px 0 0;text-align:center">Drawn from the order number${m.orderNumber ? " " + esc(m.orderNumber) : ""}. The display mirrors what PLC-Ki reads; it is not a copy of the real screen.</p></div>`;
      const notice = c.connection && c.connection !== "connected" && c.connection !== "snapshot" && !(sim.applies && sim.found)
        ? `<div class="notice gold" style="margin-bottom:16px">${icon("warn")}<div>PLC-Ki is not reading the PLC right now, so the mode and LEDs are not live.</div></div>` : "";
      return PK.sec("", notice + `<div class="plc-grid">${left}<div class="stack">${modeCard}${info}</div></div>`);
    },
    after(el) {
      el.querySelectorAll("[data-mode]").forEach((b) => PK.on(b, "click", () => switchMode(b.dataset.mode)));
      wireAsk(el);
      el.querySelectorAll("[data-page]").forEach((b) => PK.on(b, "click", () => { page = +b.dataset.page; PK.draw(); }));
      el.querySelectorAll(".pkd-key").forEach((k) => {
        const act = () => {
          const a = k.dataset.key;
          if (a === "right" || a === "down" || a === "ok") page = (page + 1) % PAGES.length;
          else if (a === "left" || a === "up") page = (page + PAGES.length - 1) % PAGES.length;
          else if (a === "esc") page = 0;
          PK.draw();
        };
        PK.on(k, "click", act);
        PK.on(k, "keydown", (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); act(); } });
      });
    },
  };

  // ---------------------------------------------------------------- Racks and I/O: state and helpers
  let stationName = null, slotSel = null, job = null, jobMsg = "";
  const kindLabel = { plc: "PLC", io: "I/O station", other: "Device" };
  const famLabel = { "s7-1500": "S7-1500", "s7-1200": "S7-1200", et200sp: "ET 200SP", et200mp: "ET 200MP", other: "" };
  const kindName = {
    cpu: "CPU", ps: "System power supply", pm: "Load power supply", di: "Digital inputs", dq: "Digital outputs", dio: "Digital inputs and outputs",
    ai: "Analog inputs", aq: "Analog outputs", aio: "Analog inputs and outputs", "f-di": "Fail-safe digital inputs", "f-dq": "Fail-safe digital outputs",
    "f-pm": "Fail-safe power module", cp: "Communication processor", cm: "Communication module", tm: "Technology module", im: "Interface module", bus: "Server module", other: "Device",
  };

  async function readHardware() {
    try {
      const r = await PK.api("POST", "/api/admin/hardware/read", {});
      job = r.id; jobMsg = "TIA opens the archive and reads the hardware. This takes a minute or two.";
    } catch (e) { PK.toast(e.message, "error"); }
    PK.draw();
  }

  function channelValue(ch) {
    if (!ch.symbol) return `<span class="faint small">no tag</span>`;
    const v = values[ch.symbol];
    if (!v) return `<span class="faint">…</span>`;
    if (v.error) return `<span class="faint small" title="${esc(v.error)}">not read</span>`;
    if (v.value === true || v.value === false) return `<span class="chip ${v.value ? "t" : "f"}" style="margin:0">${v.value ? "TRUE" : "FALSE"}</span>`;
    if (v.value == null) return `<span class="faint">…</span>`;
    const n = typeof v.value === "number" && !Number.isInteger(v.value) ? v.value.toFixed(3).replace(/0+$/, "").replace(/\.$/, "") : String(v.value);
    return `<span class="chip" style="margin:0">${esc(n)}</span>`;
  }

  function stationBadge(name) {
    const sev = diag && diag.stations && diag.stations[name];
    if (!sev || sev === "ok") return "";
    return `<span class="sev-dot ${sevCls[sev]}" title="${esc(sevText[sev] || sev)}" aria-label="${esc(sevText[sev] || sev)}"></span>`;
  }

  function moduleProblems(m, st) {
    const list = diag && diag.problems ? diag.problems.filter((p) => p.station === st.name && p.slot != null && String(p.slot) === String(m.slot)) : [];
    if (!list.length) return "";
    const worst = list.some((p) => p.severity === "fault") ? "red" : "gold";
    return `<div class="notice ${worst}" style="margin-top:12px">${icon("warn")}<div>${list.map((p) => `<div><b>${esc(p.text)}</b>${p.codes.length ? " · " + p.codes.map(esc).join(", ") : ""}</div>`).join("")}</div></div>`;
  }

  function moduleCard(m, st) {
    const fields = [
      ["Slot", m.slot ?? "—"], ["Order number", m.orderNumber || "—"], ["Firmware", m.firmware || "—"],
      ["Addresses", (m.addresses || []).map((a) => a.text).join(", ") || "—"],
    ];
    if (m.ips && m.ips.length) fields.push(["IP address", m.ips.join(", ")]);
    if (st.pnName && (m.kind === "cpu" || m.kind === "im" || st.kind === "other")) fields.push(["PROFINET name", st.pnName]);
    const chans = m.channels || [];
    const tagged = chans.filter((c) => c.tag).length;
    const table = chans.length || chQuery ? channelList(m, st, chans, tagged)
      : `<p class="muted" style="margin:12px 0 0">${m.kind === "cpu" ? "The CPU itself has no I/O channels." : m.kind === "ps" || m.kind === "pm" ? "A power supply has no I/O channels." : "No I/O channels known for this module."}</p>`;
    return `<div class="card hw-detail">
      <div class="row" style="gap:10px;align-items:baseline;flex-wrap:wrap"><h2 style="margin:0;font-size:24px">${esc(m.title || m.name)}</h2>
        ${m.safety ? `<span class="tag gold">Fail-safe</span>` : ""}<span class="tag">${esc(kindName[m.kind] || m.kind)}</span><span class="spacer"></span>
        ${chans.length ? `<span class="faint small">${chans.length} channels · ${tagged} with a tag${watchAt ? " · read " + PK.clock(watchAt) : ""}</span>` : ""}</div>
      ${m.name && m.name !== m.title ? `<p class="muted" style="margin:4px 0 0">${esc(m.name)}${m.comment ? " · " + esc(m.comment) : ""}</p>` : m.comment ? `<p class="muted" style="margin:4px 0 0">${esc(m.comment)}</p>` : ""}
      <div class="hw-fields">${fields.map(([a, b]) => `<div><span>${esc(a)}</span><b>${esc(b)}</b></div>`).join("")}</div>
      ${moduleProblems(m, st)}
      ${table}</div>`;
  }

  // ---- the channel list: short by default (tagged channels, 16 rows), a filter, and a search over the whole rack ----
  const CH_ROWS = 16;
  let chFilter = null, chQuery = "", chAll = false, chFor = "";
  const isOn = (c) => { const v = values[c.symbol]; return !!(v && v.value === true); };

  function channelList(m, st, chans, tagged) {
    const key = st.name + "/" + m.slot;
    if (key !== chFor) { chFor = key; chAll = false; chFilter = null; }
    const filter = chFilter || (tagged > 0 && tagged < chans.length ? "tagged" : "all");
    const q = chQuery.trim().toLowerCase();
    const digital = chans.some((c) => c.type === "digital" && c.symbol);
    const onCount = chans.filter((c) => c.type === "digital" && isOn(c)).length;
    let rows, scope;
    if (q) {
      // Search: every channel of every module in this rack
      rows = st.modules.flatMap((mm) => (mm.channels || []).map((c) => ({ ...c, mod: mm })))
        .filter((c) => [c.address, c.tag, c.comment, c.mod.title, c.mod.name].some((x) => String(x || "").toLowerCase().includes(q)));
      scope = `${rows.length} match${rows.length === 1 ? "" : "es"} in ${esc(st.name)}`;
    } else {
      rows = chans.filter((c) => filter === "tagged" ? !!c.tag : filter === "on" ? c.type === "digital" && isOn(c) : true).map((c) => ({ ...c, mod: m }));
      scope = "";
    }
    const shown = chAll ? rows : rows.slice(0, CH_ROWS);
    const seg = (id, label) => `<button type="button" data-chf="${id}" class="${!q && filter === id ? "on" : ""}">${label}</button>`;
    const bar = `<div class="row hw-ch-bar">
      <div class="segmented" role="group" aria-label="Show channels">${seg("tagged", `With a tag · ${tagged}`)}${digital ? seg("on", `On now · ${onCount}`) : ""}${seg("all", `All · ${chans.length}`)}</div>
      <input type="search" id="ch-q" placeholder="Find a tag, address or comment in this rack" aria-label="Find a channel" value="${esc(chQuery)}">
      ${scope ? `<span class="faint small">${scope}</span>` : ""}</div>`;
    const modCol = !!q;
    const table = rows.length ? `<table class="tbl hw-ch"><tr><th>Ch</th>${modCol ? "<th>Module</th>" : ""}<th>Address</th><th>Tag</th><th>Now</th><th>Comment</th></tr>
      ${shown.map((c) => `<tr><td class="mono">${c.io === "Output" ? "Q" : "I"}${c.n}</td>${modCol ? `<td><a href="#/plc/racks/${encodeURIComponent(st.name)}/${encodeURIComponent(c.mod.slot ?? "")}" data-goslot="${esc(c.mod.slot ?? "")}">${esc(c.mod.slot ?? "")} · ${esc(c.mod.title || c.mod.name)}</a></td>` : ""}<td class="mono">${esc(c.address)}</td>
        <td class="mono">${c.tag ? `<a href="#/tags">${esc(c.tag)}</a>` : `<span class="faint">—</span>`}</td><td>${channelValue(c)}</td><td class="muted">${esc(c.comment || "")}</td></tr>`).join("")}</table>`
      : `<p class="muted" style="margin:10px 0 0">${q ? "No channel matches." : filter === "on" ? "No input or output of this module is on right now." : "No channel of this module has a tag."}</p>`;
    const more = rows.length > shown.length ? `<button type="button" class="btn plain small" data-chall>Show all ${rows.length}</button>`
      : chAll && rows.length > CH_ROWS ? `<button type="button" class="btn plain small" data-chall>Show fewer</button>` : "";
    return bar + table + more;
  }

  function networkCard() {
    const rows = hw.stations.map((s) => `<tr data-st="${esc(s.name)}" class="${s.name === stationName ? "on" : ""}"><td><a href="#/plc/racks/${encodeURIComponent(s.name)}">${esc(s.name)}</a></td>
      <td>${esc(kindLabel[s.kind] || s.kind)}${famLabel[s.family] ? " · " + famLabel[s.family] : ""}</td><td class="mono">${esc(s.ips.join(", ") || "—")}</td>
      <td class="mono muted">${esc(s.pnName || "")}</td><td class="muted">${esc(s.controlsIoSystem ? "controls " + s.controlsIoSystem : s.ioSystem || "")}</td>
      <td class="muted">${s.modules.length}</td><td>${(() => { const sev = diag && diag.stations && diag.stations[s.name]; return sev ? `<span class="pill ${sevCls[sev]}"><span class="dot"></span>${esc(sevText[sev])}</span>` : `<span class="faint">—</span>`; })()}</td></tr>`).join("");
    return `<div class="card"><h3>Network</h3><p class="muted small" style="margin:0 0 8px">Every device in the project with its address and the PROFINET system it is on.</p>
      <div class="tbl-wrap"><table class="tbl"><tr><th>Device</th><th>Kind</th><th>IP address</th><th>PROFINET name</th><th>IO system</th><th>Modules</th><th>Status</th></tr>${rows}</table></div></div>`;
  }

  // ---------------------------------------------------------------- Racks and I/O tab
  const racksTab = {
    onJobs(jobs) {
      if (!job) return;
      const j = jobs.find((x) => x.id === job);
      if (!j) return;
      if (j.state === "running") { jobMsg = j.stage || jobMsg; return; }
      job = null;
      if (j.state === "done") { jobMsg = ""; PK.toast("Hardware read from TIA."); loadHw(true); }
      else { jobMsg = "Reading the hardware did not work. Open Setup › Program for the log."; PK.draw(); }
    },
    render(params) {
      if (params[0] && params[0] !== stationName) { stationName = params[0]; slotSel = params[1] ?? null; }
      if (hwError) return PK.sec("", `<div class="notice gold">${icon("warn")}<div>${esc(hwError)}</div></div>`);
      if (!hw) return PK.sec("", `<p class="muted">Loading…</p>`);
      const admin = PK.state.user && PK.state.user.role === "admin";
      const st = hw.stations.find((s) => s.name === stationName) || hw.stations.find((s) => s.name === hw.plcStation) || hw.stations[0];
      if (!st) return PK.sec("", `<div class="notice gold">${icon("warn")}<div>The project has no devices.</div></div>`);
      stationName = st.name;
      const mods = st.modules.filter((m) => (m.widthMm || 0) > 0 || m.family === "other");
      let idx = slotSel != null ? mods.findIndex((m) => String(m.slot) === String(slotSel)) : -1;
      if (idx < 0) idx = Math.max(0, mods.findIndex((m) => (m.channels || []).length > 0));
      const sel = mods[idx];
      watched = st.modules.flatMap((m) => (m.channels || []).filter((c) => c.symbol)).slice(0, 300);

      const only = hw.source !== "hardware";
      const readBtn = admin ? `<button class="btn small" type="button" data-read ${job ? "disabled" : ""}>${icon("download")}${job ? "Reading…" : only ? "Read the hardware from TIA" : "Read again from TIA"}</button>` : "";
      const notice = only
        ? `<div class="notice gold" style="margin-bottom:16px">${icon("info")}<div style="flex:1">Only the CPU is known so far: this program export has no hardware configuration. ${admin ? "Read it from TIA: a minute or two on the TIA PC, the program and model stay as they are." : "Ask an administrator to read the hardware from TIA."}
            ${jobMsg ? `<div class="small" style="margin-top:6px">${esc(jobMsg)}</div>` : ""}<div class="row" style="margin-top:10px">${readBtn}</div></div></div>`
        : jobMsg ? `<div class="notice" style="margin-bottom:16px">${icon("info")}<div>${esc(jobMsg)}</div></div>` : "";

      const picker = hw.stations.length > 1 ? `<div class="hw-stations" role="tablist" aria-label="Devices">${hw.stations.map((s) => `<a role="tab" aria-selected="${s.name === st.name}" class="hw-st ${s.name === st.name ? "on" : ""}" href="#/plc/racks/${encodeURIComponent(s.name)}">
          <b>${stationBadge(s.name)}${esc(s.name)}</b><span>${esc([kindLabel[s.kind], famLabel[s.family]].filter(Boolean).join(" · "))}${s.ips[0] ? " · " + esc(s.ips[0]) : ""}</span></a>`).join("")}</div>` : "";

      const mode = modeOf(cpu);
      const running = st.kind === "plc" ? (mode.state ? mode.state === "Run" : undefined) : (cpu && cpu.connection === "connected" ? true : undefined);
      const k = window.innerWidth < 700 ? 1.7 : 2.3;
      const svg = PKDev.rack(st, { k, values, running, selected: idx, leds: ledsOf(cpu, mode) });
      const rackCard = `<div class="card hw-rack">
        <div class="row" style="gap:10px;flex-wrap:wrap;align-items:baseline"><h2 style="margin:0;font-size:22px">${esc(st.name)}</h2>
          <span class="muted small">${esc([st.typeName, st.orderNumber, st.ips.join(", ")].filter(Boolean).join(" · "))}</span><span class="spacer"></span>${only ? "" : readBtn}</div>
        <div class="rack-scroll" tabindex="0" aria-label="Rack drawing, scrolls sideways">${svg}</div>
        <p class="faint small" style="margin:6px 0 0">Tap a module for its channels. Channel LEDs: green is on, grey is off, dim is no live value (no tag, or not read). Pictures are drawn from the order number, not photos.</p></div>`;
      const detail = sel ? moduleCard(sel, st) : "";
      // The PLC's station shows everything the CPU reports and every drive; another station only its own
      const diagZone = `<div class="diag-zone">${problemsCard({ title: `Reported for ${esc(st.name)}`, station: st.name }) + drivesSection(st.name)}</div>`;
      const meta = `<p class="faint small" style="margin:14px 0 0">${esc(hw.project || "")}${hw.exportedAt ? " · read from TIA " + esc(PK.date(hw.exportedAt)) : ""}</p>`;
      return PK.sec("", notice + picker + rackCard + detail + diagZone + networkCard() + meta);
    },
    after(el) {
      el.querySelectorAll("[data-read]").forEach((b) => PK.on(b, "click", readHardware));
      wireAsk(el);
      el.querySelectorAll("[data-chf]").forEach((b) => PK.on(b, "click", () => { chFilter = b.dataset.chf; chQuery = ""; chAll = false; PK.draw(); }));
      el.querySelectorAll("[data-chall]").forEach((b) => PK.on(b, "click", () => { chAll = !chAll; PK.draw(); }));
      el.querySelectorAll("[data-goslot]").forEach((a) => PK.on(a, "click", () => { chQuery = ""; }));
      const cq = el.querySelector("#ch-q");
      if (cq) PK.on(cq, "input", () => { chQuery = cq.value; chAll = false; PK.draw(); });
      el.querySelectorAll(".pkd-mod").forEach((g) => {
        const pick = () => {
          const st = hw.stations.find((s) => s.name === stationName);
          const mods = st.modules.filter((m) => (m.widthMm || 0) > 0 || m.family === "other");
          const m = mods[+g.dataset.mod];
          slotSel = m.slot != null ? String(m.slot) : null;
          PK.setHash(`#/plc/racks/${encodeURIComponent(stationName)}${slotSel != null ? "/" + encodeURIComponent(slotSel) : ""}`);
          PK.draw();
        };
        PK.on(g, "click", pick);
        PK.on(g, "keydown", (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); pick(); } });
      });
    },
  };

  // ---------------------------------------------------------------- Diagnostics tab
  const diagTab = {
    render() {
      const drives = drivesSection(null);
      return PK.sec("", `<div class="diag-zone">${problemsCard()}${drives || (diag ? `<p class="faint small" style="margin:14px 0 0">No drive in the program is read by PLC-Ki.</p>` : "")}</div>`);
    },
    after(el) { wireAsk(el); },
  };

  // ---------------------------------------------------------------- the PLC page: one header, three tabs
  const TABS = [["cpu", "CPU", cpuTab, "The CPU as it looks on the machine: its mode, LEDs and display, read live. Start and Stop work on the PLCSIM CPU only."],
    ["racks", "Racks and I/O", racksTab, "Every rack and module of the TIA project, drawn from its order number, with each input and output, its tag and its live value."],
    ["diagnostics", "Diagnostics", diagTab, "What the CPU reports for PROFINET devices, modules, channels and drives. Read live from the CPU."]];
  const tabOf = (params) => TABS.find((t) => t[0] === (params || [])[0]) || TABS[0];
  /** What follows the tab in the address: #/plc/racks/<station>/<slot> gives [station, slot]. */
  const rest = (params) => (TABS.some((t) => t[0] === (params || [])[0]) ? params.slice(1) : []);

  PK.view("plc", {
    live: true,
    mount(el, params) {
      loadHw(); loadCpu(); loadDiag(); PK.loadProgram().then(() => PK.draw());
      if (tabOf(params)[0] === "racks") { const r = rest(params); stationName = r[0] || null; slotSel = r[1] != null ? r[1] : null; }
      PK.draw();
    },
    leave() { watched = []; },
    onJobs(jobs) { racksTab.onJobs(jobs); },
    render(params) {
      const [k, , tab, sub] = tabOf(params);
      if (k !== "racks") watched = [];
      const nProblems = diag && diag.problems ? diag.problems.length : 0;
      const nav = `<nav class="ptabs" aria-label="PLC">${TABS.map(([id, label]) => `<a href="#/plc${id === "cpu" ? "" : "/" + id}" class="${id === k ? "on" : ""}" ${id === k ? 'aria-current="page"' : ""}>${label}${id === "diagnostics" && nProblems ? ` <span class="count warn">${nProblems}</span>` : ""}</a>`).join("")}</nav>`;
      const head = PK.hero({ cls: "hero compact", title: "PLC", sub, extra: nav });
      return head + tab.render(rest(params));
    },
    after(el, params) { const t = tabOf(params)[2]; if (t.after) t.after(el, rest(params)); },
  });
})();