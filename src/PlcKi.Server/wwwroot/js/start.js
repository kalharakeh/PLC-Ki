// Get started (administrators): a step-by-step setup that asks one thing per page, and a "Getting started" card on
// the machine pages that shows what is done and what comes next, until PLC-Ki is fully set up.
"use strict";

const PKStart = (() => {
  const { esc, icon, api, state, toast, cmd } = PK;
  const SEEN = "plcki.getStarted";
  const HIDE = "plcki.hideGuide";
  const store = (s) => ({
    get: (k) => { try { return s.getItem(k); } catch { return null; } },
    set: (k, v) => { try { s.setItem(k, v); } catch { /* private window */ } },
  });
  const local = store(window.localStorage), session = store(window.sessionStorage);
  const DEMO = /demo-mixing-station\.json$/i;
  const VM_DIR = "C:\\PLCKi\\PLC-Ki";
  const SETUP_URL = "https://github.com/kalharakeh/PLC-Ki/actions/workflows/ci.yml?query=branch%3Amain";
  const helperExe = `${VM_DIR}\\tools\\TiaImporter\\bin\\Release\\net48\\TiaImporter.exe`;

  // ---------- what is set up ----------
  let ready = null, readyAt = 0, loading = false;
  async function readiness(force) {
    if (loading || (!force && ready && Date.now() - readyAt < 30000)) return ready;
    loading = true;
    try {
      const d = await PKSetup.loadSettings();
      let ask = null, tia = null;
      try { ask = await api("GET", "/api/ask"); } catch { /* shown as unknown */ }
      try { tia = await api("GET", "/api/admin/tia/status"); } catch (e) { tia = { ok: false, message: e.message }; }
      ready = { settings: d.settings, info: d.info, ask, tia };
      readyAt = Date.now();
    } catch { /* not an administrator, or PLC-Ki down */ }
    loading = false;
    return ready;
  }

  /** The list behind the card and the last wizard page. */
  function items(r) {
    const s = r.settings, info = r.info;
    const c = state.status && state.status.connection;
    const preset = PKSetup.presets[PKSetup.presetOf(s)];
    const list = [];
    if (info.restartNeeded) list.push({ key: "apply", title: "Apply the saved settings", done: false, text: "PLC-Ki uses them after a quick restart (a few seconds).", btn: "Apply now", action: "restart" });
    list.push({ key: "setup", title: "Pick your setup", done: !!s.setupChosen, text: s.setupChosen ? preset.title : "Same PC as TIA, a PC with a TIA VM, or a server with your laptop.", href: "#/setup/start", btn: "Start" });
    const live = c && c.state === "connected";
    list.push({ key: "plc", title: "Connect the PLC", done: live, text: live ? `Reading ${c.address}, live.` : PK.connInfo().title, href: "#/setup/start/plc", btn: "Connect" });
    const demo = DEMO.test(s.model || "");
    list.push({ key: "program", title: "Teach it your machine", done: !demo, text: demo ? "PLC-Ki shows the demo mixing station until you import your TIA program." : `Watching ${state.info ? state.info.machine : "your program"}.`, href: "#/setup/program", btn: "Import" });
    const keyOk = info.apiKey !== "none";
    list.push({ key: "ask", title: "Turn on Ask", done: keyOk, text: keyOk ? "Claude can answer questions and read programs." : "Needs a Claude API key. Without it, Ask and program import stay off.", href: "#/setup/start/assistant", btn: "Add key" });
    const t = r.tia || {};
    list.push({ key: "tia", title: "TIA helper", optional: true, done: !!t.ok, text: t.ok ? `Ready on ${t.machine || "the TIA PC"}.` : "Only for importing a program and the block generator. " + PK.plain(t.message).text, href: "#/setup/start/tia", btn: "Set up" });
    return list;
  }

  function checklist(list, compact) {
    return `<ul class="todo">${list.map((i) => `<li class="${i.done ? "done" : ""}">
      <span class="tick">${i.done ? icon("check") : ""}</span>
      <div class="t"><b>${esc(i.title)}${i.optional ? ` <span class="tag">optional</span>` : ""}</b><span>${esc(i.text)}</span></div>
      ${i.done ? "" : i.action === "restart" ? `<button class="btn small gold" data-restart>${esc(i.btn)}</button>`
        : `<a class="btn small ${compact ? "b" : ""}" href="${i.href}">${esc(i.btn)}</a>`}</li>`).join("")}</ul>`;
  }

  /** The "Getting started" card at the top of the machine pages, for administrators, until everything needed is done. */
  function card() {
    if (!state.user || state.user.role !== "admin" || session.get(HIDE) === "1") return "";
    if (!ready) { readiness().then((r) => r && PK.draw()); return ""; }
    const list = items(ready);
    const need = list.filter((i) => !i.optional);
    const done = need.filter((i) => i.done).length;
    if (done === need.length) return "";
    const next = list.find((i) => !i.done && !i.optional) || list.find((i) => !i.done);
    // A slim bar below the page's own content: the machine comes first, set-up second
    return `<section class="guide-sec"><div class="wrap"><div class="guide-bar" role="region" aria-label="Getting started">
      <div class="gb-text"><span class="gb-count">Getting started · ${done} of ${need.length} done</span>
        <span class="gb-next"><b>Next: ${esc(next.title)}.</b> ${esc(next.text)}</span></div>
      <div class="gb-actions">${next.action === "restart" ? `<button class="btn small gold" data-restart>${esc(next.btn)}</button>` : `<a class="btn small" href="${next.href}">${esc(next.btn)}</a>`}
        <a class="btn plain small" href="#/setup/start/done">See all</a><button class="btn plain small" data-hide-guide>Hide</button></div>
      <div class="bar"><div style="width:${Math.round(done / need.length * 100)}%"></div></div></div></div></section>`;
  }
  function bindCard(page) {
    const h = page.querySelector("[data-hide-guide]");
    if (h) h.onclick = () => { session.set(HIDE, "1"); PK.draw(); };
    page.querySelectorAll(".guide-bar [data-restart]").forEach((b) => b.onclick = PKSetup.restart);
  }

  // ---------- the wizard ----------
  const STEPS = [
    ["setup", "Your setup"], ["plc", "The PLC"], ["tia", "TIA Portal"], ["assistant", "Ask"], ["program", "Your machine"], ["done", "Done"],
  ];
  let w = null;          // edited settings
  let plcTest = null, tiaCheck = null, link = null, linkKey = null, timer = null, busy = false;

  /** A small drawing of each setup: which computer does what. */
  function mini(kind) {
    const b = (x, y, wd, t, sub, hl) => `<rect x="${x}" y="${y}" width="${wd}" height="44" rx="10" fill="var(--panel)" stroke="${hl ? "var(--accent)" : "var(--line-strong)"}" stroke-width="${hl ? 1.6 : 1}"/>
      <text x="${x + wd / 2}" y="${y + 19}" font-size="11.5" font-weight="600" text-anchor="middle" fill="var(--ink)">${t}</text>
      <text x="${x + wd / 2}" y="${y + 34}" font-size="10" text-anchor="middle" fill="var(--muted)">${sub}</text>`;
    const l = (x1, x2, y) => `<line x1="${x1}" y1="${y}" x2="${x2}" y2="${y}" stroke="var(--accent)" stroke-width="1.6" stroke-dasharray="4 4"/>`;
    const svg = {
      same: `<rect x="4" y="8" width="168" height="76" rx="14" fill="none" stroke="var(--line-strong)" stroke-dasharray="3 5"/><text x="14" y="24" font-size="10" fill="var(--muted)">one PC</text>
        ${b(12, 32, 72, "PLC-Ki", "browser", true)}${b(92, 32, 72, "TIA", "+ helper")}${l(172, 206, 54)}${b(206, 32, 66, "PLC", "S7")}`,
      vm: `<rect x="4" y="8" width="176" height="76" rx="14" fill="none" stroke="var(--line-strong)" stroke-dasharray="3 5"/><text x="14" y="24" font-size="10" fill="var(--muted)">your PC</text>
        ${b(12, 32, 72, "PLC-Ki", "browser", true)}${b(96, 32, 76, "VM", "TIA + helper")}${l(180, 206, 54)}${b(206, 32, 66, "PLC", "S7")}`,
      server: `${b(4, 32, 70, "Server", "PLC-Ki", true)}${l(74, 104, 54)}<rect x="104" y="8" width="94" height="76" rx="14" fill="none" stroke="var(--line-strong)" stroke-dasharray="3 5"/>
        <text x="114" y="24" font-size="10" fill="var(--muted)">laptop</text>${b(112, 32, 78, "Link", "+ TIA")}${l(198, 206, 54)}${b(206, 32, 66, "PLC", "S7")}`,
    }[kind];
    return `<svg viewBox="0 0 276 92" class="mini" aria-hidden="true" font-family="inherit">${svg}</svg>`;
  }

  const daily = {
    same: { where: [["This PC", "PLC-Ki, TIA Portal, and the cable to the PLC."]],
      every: ["Start PLC-Ki (double-click Start-PLC-Ki). That is all.", "PLC-Ki starts the TIA helper by itself when you import a program or build blocks."] },
    vm: { where: [["Your PC", "PLC-Ki. It reads the PLC directly."], ["The VM", "TIA Portal and the TIA helper."]],
      every: ["Start PLC-Ki on the PC (double-click Start-PLC-Ki).", "Live values, alarms and Ask work with the VM off.", "The VM is only needed to import a program or for the block generator. After a one-time setup, the TIA helper starts by itself when you log into the VM."] },
    server: { where: [["The server", "PLC-Ki, open from any browser."], ["Your laptop", "PLC-Ki Link and TIA Portal. It reaches the PLC."]],
      every: ["PLC-Ki runs on the server by itself.", "On the laptop, start PLC-Ki Link. It reads the PLC and sends the values to the server, and does the TIA work."] },
  };

  const preset = () => PKSetup.presetOf(w);
  const field = (id, label, value, opts = {}) => `<label class="field">${label}${opts.hint ? `<span class="hint">${opts.hint}</span>` : ""}
    <input id="${id}" value="${esc(value || "")}" ${opts.type ? `type="${opts.type}"` : ""} placeholder="${esc(opts.placeholder || "")}" autocomplete="${opts.type === "password" ? "new-password" : "off"}" spellcheck="false"></label>`;
  const result = (r) => !r ? "" : r.busy ? `<span class="pill"><span class="dot"></span>Checking…</span>`
    : r.ok ? `<div class="notice green">${icon("check")}<div>${esc(r.text)}</div></div>`
    : `<div class="notice red">${icon("warn")}<div><b>${esc(r.text)}</b>${r.todo ? `<br>${esc(r.todo)}` : ""}</div></div>`;

  function stepSetup() {
    const p = preset();
    const d = daily[p];
    return {
      title: "Which setup is yours?",
      sub: "PLC-Ki needs the PLC for live values, and TIA Portal only to import a program or build blocks. Pick the one that looks like yours. You can change it later.",
      body: `<div class="setups">${Object.entries(PKSetup.presets).map(([k, x]) => `<button type="button" class="setup-opt ${p === k ? "on" : ""}" data-preset="${k}">
          ${mini(k)}<b>${esc(x.title)}</b><small>${esc(x.text)}</small></button>`).join("")}</div>
        <div class="card explain">
          <div class="grid two">
            <div><h3>What runs where</h3><dl class="where-list">${d.where.map(([a, b]) => `<dt>${esc(a)}</dt><dd>${esc(b)}</dd>`).join("")}</dl></div>
            <div><h3>Every day</h3><ul class="plainlist">${d.every.map((x) => `<li>${esc(x)}</li>`).join("")}</ul></div>
          </div>
          ${p === "same" ? "" : `<p class="faint small" style="margin:16px 0 0">Tip: installed with <a href="${SETUP_URL}" target="_blank" rel="noopener">PLC-Ki-Setup.exe</a>
            (GitHub, Actions, latest run, Artifacts), PLC-Ki runs by itself in the background and starts with Windows, so there is no window to keep open.</p>`}</div>
        ${p === "same" && PKSetup.info.service ? `<div class="notice gold">${icon("info")}<div>PLC-Ki runs as a Windows service on this PC, so it cannot open the TIA helper window itself.
          Set the helper up as for a VM (double-click Setup-TIA-helper on this PC) and choose <b>PC with a TIA VM</b> with the address <code>http://localhost:5090</code>.</div></div>` : ""}`,
    };
  }

  function stepPlc() {
    const viaLink = w.plcVia === "link";
    const c = state.status && state.status.connection;
    const liveNow = c && c.state === "connected" && c.address === w.address;
    let linkPart = "";
    if (viaLink) {
      const l = link;
      const key = linkKey || "<the key>";
      const cmdText = `cd /d %USERPROFILE%\\Downloads\\PLC-Ki && dotnet run --project src\\PlcKi.Server -- link --server ${location.origin} --key ${key}${w.tiaMode === "link" ? ` --tia "${helperExe}"` : ""}`;
      linkPart = `<div class="card stack">
        <h3>PLC-Ki Link on your laptop</h3>
        <p class="muted" style="margin:0">The laptop reads the PLC and sends the values here. It only connects out to this server, so the plant network needs no changes.</p>
        <ol class="howto">
          <li><b>Make a key.</b> It lets your laptop in. It is shown once.
            <div class="row" style="margin-top:8px"><button class="btn small" data-link-key>${l && l.hasKey ? "Make a new key" : "Make a key"}</button>
            ${linkKey ? `<span class="pill ok"><span class="dot"></span>Key made. It is in the command below.</span>` : l && l.hasKey ? `<span class="faint small">A key exists. Make a new one if you lost it.</span>` : ""}</div></li>
          <li><b>On the laptop, open Command Prompt and paste this one line.</b> Leave the window open.${cmd("On the laptop", cmdText)}</li>
          <li><b>Wait here.</b> This turns green within a few seconds:
            <div style="margin-top:8px">${!l ? `<span class="pill">Checking…</span>` : l.connected ? `<span class="pill ok"><span class="dot"></span>Link connected from ${esc(l.machine || "the laptop")}${l.plc && l.plc.connected ? ", reading the PLC" : ""}</span>` : `<span class="pill bad"><span class="dot"></span>Link not connected yet</span>`}</div></li>
        </ol></div>`;
    }
    return {
      title: "Where is the PLC?",
      sub: viaLink ? "Your laptop reads the PLC at this address and sends the values to PLC-Ki." : "PLC-Ki reads the PLC over the network, read-only. It never writes values or changes the CPU state.",
      body: `<div class="card stack">
          <div class="grid two">${field("w-address", "PLC address (IP)", w.address, { placeholder: "e.g. 192.168.0.1" })}
            <div class="row" style="align-self:end;margin-bottom:2px">${viaLink ? "" : `<button class="btn b" data-test>Test</button>`}
              ${liveNow ? `<span class="pill ok"><span class="dot"></span>Live now</span>` : ""}</div></div>
          ${result(plcTest)}
          <details class="more"><summary>Where do I find the address?</summary><p class="muted small" style="margin:8px 0 0">In TIA Portal: <b>Devices &amp; networks</b>, click the CPU, then
            <b>Properties › PROFINET interface › Ethernet addresses</b>. For PLCSIM Advanced it is the address of the virtual PLC (in your test setup 192.168.10.10).
            ${viaLink ? "" : "This PC must be in the same network: <code>ping</code> to the address should answer."}</p></details>
          <details class="more"><summary>The CPU has a password</summary><div style="margin-top:8px">${viaLink
            ? `<p class="muted small" style="margin:0">With Link the password stays on the laptop: add <code>--plc-password &lt;password&gt;</code> to the Link command.</p>`
            : field("w-plcPassword", "CPU access password", "", { type: "password", hint: PKSetup.info.plcPassword === "none" ? "Only if the CPU has access protection." : "A password is saved. Leave empty to keep it." })}</div></details>
        </div>${linkPart}`,
    };
  }

  function stepTia() {
    const m = w.tiaMode;
    const needApply = PKSetup.info.restartNeeded;
    const check = `<div class="row"><button class="btn b" data-check-tia>Check the TIA helper</button></div>
      ${needApply && tiaCheck && !tiaCheck.ok ? `<div class="notice gold">${icon("info")}<div style="flex:1">Your new settings are saved but not used yet. Apply them, then check again.</div><button class="btn gold small" data-restart>Apply now</button></div>` : ""}
      ${result(tiaCheck)}`;
    let body;
    if (m === "local") {
      body = `<div class="card stack"><h3>TIA Portal is on this PC</h3>
        <p class="muted" style="margin:0">PLC-Ki starts the TIA helper by itself, in its own window, when it needs TIA. Tell it where the helper is.</p>
        ${field("w-tiaImporterPath", "TiaImporter.exe on this PC", w.tiaImporterPath, { placeholder: helperExe })}
        ${field("w-tiaPlcsimIp", "PLCSIM address (for test downloads)", w.tiaPlcsimIp, { placeholder: "e.g. 192.168.10.10, empty = no PLCSIM downloads" })}
        ${check}</div>`;
    } else if (m === "remote") {
      body = `<div class="card stack"><span class="where where-vm">On the VM · once</span>
          <h3>Set up the TIA helper on the VM</h3>
          <ol class="howto">
            <li><b>Get the newest PLC-Ki on the VM.</b> Open Command Prompt on the VM and paste this one line:${cmd("On the VM", `cd /d ${VM_DIR} && git pull`)}</li>
            <li><b>Double-click <code>Setup-TIA-helper</code></b> in File Explorer, in <code>${VM_DIR}\\tools\\TiaImporter</code>. Say Yes when Windows asks.
              A small window asks for a token (a password you make up, at least 8 characters) and the PLCSIM address, then starts the helper.</li>
            <li><b>That's it.</b> From now on the helper starts by itself when you log into the VM, in a window called <b>PLC-Ki TIA helper</b>. Leave it open.
              After a PLC-Ki update, double-click <code>Update-TIA-helper</code> in the same folder: it gets the newest version, builds it and starts it again.</li>
          </ol></div>
        <div class="card stack"><span class="where where-pc">Here · on the PC</span>
          <h3>Tell PLC-Ki where the VM is</h3>
          <div class="grid two">${field("w-tiaUrl", "VM address", w.tiaUrl, { placeholder: "http://192.168.10.3:5090", hint: "The VM's IP address, port 5090." })}
            ${field("w-tiaToken", "Token", "", { type: "password", placeholder: PKSetup.info.tiaToken === "set" ? "•••••••• (saved)" : "the same token as on the VM", hint: PKSetup.info.tiaToken === "set" ? "Saved. Leave empty to keep it." : "Shown in the TIA helper window on the VM." })}</div>
          ${field("w-tiaProject", "TIA project for generated blocks (optional)", w.tiaProject, { placeholder: "C:\\Users\\…\\Documents\\Automation\\…\\Project.ap20", hint: "A path on the VM. Use a copy, not the plant's master project." })}
          ${check}</div>`;
    } else {
      body = `<div class="card stack"><h3>TIA Portal on your laptop</h3>
        <p class="muted" style="margin:0">PLC-Ki Link carries the TIA work to the laptop. The Link command on the previous page already starts the TIA helper there (the part with <code>--tia</code>).</p>
        ${field("w-tiaProject", "TIA project for generated blocks (optional)", w.tiaProject, { placeholder: "C:\\Users\\…\\Documents\\Automation\\…\\Project.ap20", hint: "A path on the laptop." })}
        ${check}</div>`;
    }
    return {
      title: "TIA Portal",
      sub: "Only needed to import a program from a TIA archive and for the block generator. Live values, alarms and Ask work without it, so you can skip this and come back later.",
      body, skip: true,
    };
  }

  function stepAssistant() {
    const k = PKSetup.info.apiKey;
    return {
      title: "Turn on Ask.",
      sub: "Claude answers questions about the machine (\"why is the alarm on?\") and reads your program once, at import, to write the plain-English explanations. Both need a Claude API key.",
      body: `<div class="card stack">
        ${k !== "none" ? `<div class="notice green">${icon("check")}<div>${k === "environment" ? "A key is set on this PC (ANTHROPIC_API_KEY). You can leave this empty." : "A key is saved. Leave empty to keep it."}</div></div>` : ""}
        ${field("w-apiKey", "Claude API key", "", { type: "password", placeholder: "sk-ant-…", hint: "From console.anthropic.com, API keys. Stored encrypted on this PC." })}
        <p class="faint small" style="margin:0">Questions cost a few cents each. Importing a program costs a few dollars, once.</p></div>`,
      skip: true,
    };
  }

  function stepProgram() {
    const demo = DEMO.test(w.model || "");
    return {
      title: demo ? "Teach it your machine." : "Your machine.",
      sub: demo ? "Right now PLC-Ki shows a demo mixing station. Give it your TIA archive (.zap20): TIA exports the program, Claude writes what each step, device and alarm means, and you check it."
        : "PLC-Ki watches the program you imported. Import again when the program changes.",
      body: `<div class="card stack"><div class="row"><span class="ico">${icon(demo ? "file" : "check")}</span><div style="flex:1"><b>${esc(state.info ? state.info.machine : "")}</b>
          <div class="faint small">${demo ? "Demo machine" : esc((w.model || "").split(/[\\/]/).pop())}</div></div>
          <a class="btn ${demo ? "" : "b"}" href="#/setup/program" data-seen>${demo ? "Import my program" : "Import again"}</a></div>
        ${demo ? `<p class="faint small" style="margin:0">Needs the TIA helper (previous steps) and the Claude key. You can also keep the demo for now.</p>` : ""}</div>`,
      skip: demo, skipText: "Keep the demo for now",
    };
  }

  function stepDone() {
    const list = ready ? items(ready).filter((i) => i.key !== "setup") : [];
    const apply = PKSetup.info.restartNeeded;
    const left = list.filter((i) => !i.done && !i.optional && i.key !== "apply").length;
    return {
      title: apply ? "Almost there." : !ready ? "Checking…" : left === 0 ? "You're set up." : left === 1 ? "One thing left." : `${left} things left.`,
      sub: apply ? "Apply the settings and PLC-Ki restarts with them. The page comes back by itself in a few seconds."
        : left === 0 ? "Here is what's ready. Optional items can be done any time from Setup."
        : "Here is what's ready and what is still open. Each one can be done any time from Setup.",
      body: `<div class="card">${ready ? checklist(list.filter((i) => i.key !== "apply")) : `<p class="muted">Checking…</p>`}</div>`,
      final: apply ? `<button class="btn big" data-restart>Apply and start</button>` : `<a class="btn big" href="#/alarm" data-seen>Go to the machine</a>`,
    };
  }

  const builders = { setup: stepSetup, plc: stepPlc, tia: stepTia, assistant: stepAssistant, program: stepProgram, done: stepDone };

  function stepIndex() { const k = (PK.current && PK.current.params[0]) || "setup"; return Math.max(0, STEPS.findIndex(([x]) => x === k)); }

  function render() {
    if (!w) return PK.hero({ title: "Loading…" });
    const i = stepIndex();
    const [key] = STEPS[i];
    const st = builders[key]();
    const dots = `<ol class="wiz-dots">${STEPS.map(([k, l], n) => `<li class="${n < i ? "done" : n === i ? "now" : ""}"><a href="#/setup/start/${k}"><span>${n + 1}</span>${esc(l)}</a></li>`).join("")}</ol>`;
    const nav = key === "done" ? `<div class="wiz-nav"><a class="btn b" href="#/setup/start/${STEPS[i - 1][0]}">Back</a><span class="spacer"></span>${st.final}</div>`
      : `<div class="wiz-nav">${i > 0 ? `<a class="btn b" href="#/setup/start/${STEPS[i - 1][0]}">Back</a>` : ""}<span class="spacer"></span>
          ${st.skip ? `<button class="btn plain" data-skip>${esc(st.skipText || "Skip for now")}</button>` : ""}
          <button class="btn big" data-next ${busy ? "disabled" : ""}>${busy ? "Saving…" : "Continue"}</button></div>`;
    return PK.sec("hero wiz", `${PKSetup.setupNav("start")}${dots}
        <div class="eyebrow">Step ${i + 1} of ${STEPS.length}</div><h1>${st.title}</h1><p class="sub">${st.sub}</p>`)
      + PK.sec("alt wiz-body", `${st.body}${nav}`);
  }

  /** Reads the fields of the current step into w; returns the changes to save. */
  function read(page) {
    const ch = {};
    const v = (id) => { const el = page.querySelector("#w-" + id); return el ? el.value.trim() : undefined; };
    for (const k of ["address", "tiaUrl", "tiaImporterPath", "tiaPlcsimIp", "tiaProject"]) { const x = v(k); if (x !== undefined) { w[k] = x; ch[k] = x; } }
    for (const k of ["plcPassword", "tiaToken", "apiKey"]) { const x = v(k); if (x) { w["_" + k] = x; } }
    return ch;
  }

  async function save(page) {
    read(page);
    const { _plcPassword, _tiaToken, _apiKey, ...rest } = w;
    await PKSetup.saveSettings({ ...rest, setupChosen: true, plcPassword: _plcPassword || null, tiaToken: _tiaToken || null, apiKey: _apiKey || null });
    delete w._plcPassword; delete w._tiaToken; delete w._apiKey;
    readyAt = 0;
  }

  async function checkTia(page) {
    tiaCheck = { busy: true }; PK.draw();
    try {
      await save(page);
      if (w.tiaMode === "local") { try { await api("POST", "/api/admin/tia/start-helper"); } catch { /* status says why */ } }
      const t = await api("GET", "/api/admin/tia/status");
      const p = PK.plain(t.message);
      tiaCheck = t.ok ? { ok: true, text: `The TIA helper answers on ${t.machine || "the TIA PC"}.${t.plcsimIp ? " PLCSIM downloads go to " + t.plcsimIp + "." : ""}` }
        : { ok: false, text: p.text, todo: p.todo };
    } catch (e) { const p = PK.plain(e.message); tiaCheck = { ok: false, text: p.text, todo: p.todo }; }
    PK.draw();
  }

  PK.view("setup/start", {
    live: true,
    async mount() {
      w = null; plcTest = null; tiaCheck = null; linkKey = null; busy = false;
      try { await PKSetup.loadSettings(); w = { ...PKSetup.settings }; } catch (e) { toast(e.message, "error"); return; }
      clearInterval(timer);
      timer = setInterval(async () => {
        if (w && w.plcVia === "link") { try { link = await api("GET", "/api/admin/link/"); PK.draw(); } catch { /* next time */ } }
      }, 3000);
      readiness(true).then(PK.draw);
      PK.draw();
    },
    leave() { clearInterval(timer); timer = null; },
    render,
    after(page) {
      PKSetup.bindRestart(page);
      const i = stepIndex();
      const key = STEPS[i][0];
      const nextKey = STEPS[Math.min(i + 1, STEPS.length - 1)][0];
      page.querySelectorAll("input").forEach((el) => PK.on(el, "change", () => read(page)));
      page.querySelectorAll("[data-preset]").forEach((b) => b.onclick = () => {
        const p = PKSetup.presets[b.dataset.preset];
        w.tiaMode = p.tiaMode; w.plcVia = p.plcVia; PK.draw();
      });
      page.querySelectorAll("[data-seen]").forEach((a) => PK.on(a, "click", () => local.set(SEEN, "1")));
      const t = page.querySelector("[data-test]");
      if (t) t.onclick = async () => {
        read(page);
        plcTest = { busy: true }; PK.draw();
        try {
          const r = await api("POST", "/api/admin/test-plc", { address: w.address });
          const p = PK.plain(r.message, { plc: w.address });
          plcTest = r.ok ? { ok: true, text: `The PLC answers at ${w.address}. Continue to save it.` } : { ok: false, text: p.text, todo: p.todo };
        } catch (e) { plcTest = { ok: false, text: e.message }; }
        PK.draw();
      };
      const ct = page.querySelector("[data-check-tia]");
      if (ct) ct.onclick = () => checkTia(page);
      const lk = page.querySelector("[data-link-key]");
      if (lk) lk.onclick = async () => {
        if (link && link.hasKey && !(await PK.confirmSheet({ title: "Make a new key?", text: "Link on the laptop stops working until it is started with the new key.", ok: "Make a new key" }))) return;
        try { await save(page); linkKey = (await api("POST", "/api/admin/link/key")).key; link = await api("GET", "/api/admin/link/"); } catch (e) { toast(e.message, "error"); }
        PK.draw();
      };
      const sk = page.querySelector("[data-skip]");
      if (sk) sk.onclick = () => PK.go("#/setup/start/" + nextKey);
      const nx = page.querySelector("[data-next]");
      if (nx) nx.onclick = async () => {
        busy = true; PK.draw();
        try {
          await save(page);
          if (key === "setup") local.set(SEEN, "1");
          busy = false;
          PK.go("#/setup/start/" + nextKey);
          if (nextKey === "done") readiness(true).then(PK.draw);
        } catch (e) { busy = false; const p = PK.plain(e.message); toast(p.todo ? p.text + " " + p.todo : p.text, "error"); PK.draw(); }
      };
      if (key === "done") page.querySelectorAll("[data-restart]").forEach((b) => PK.on(b, "click", () => local.set(SEEN, "1")));
    },
  });

  return {
    card, bindCard, readiness,
    /** First visit of an administrator: open Get started once. */
    async firstRun() {
      if (local.get(SEEN) === "1") return false;
      const r = await readiness();
      return !(r && r.settings && r.settings.setupChosen);
    },
  };
})();
