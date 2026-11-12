// Get started (administrators): a step-by-step setup that asks one thing per page, and a "Getting started" card on
// the machine pages that shows what is done and what comes next, until PLC-Ki is fully set up.
"use strict";

const PKStart = (() => {
  const { esc, icon, api, state, toast, cmd } = PK;
  // "Get started" was seen: kept per login (PK.prefs "getStarted")
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
      let ask = null, tia = null, disk = null;
      try { ask = await api("GET", "/api/ask"); } catch { /* shown as unknown */ }
      try { disk = await api("GET", "/api/admin/storage/disk"); } catch { /* not shown */ }
      try { tia = await api("GET", "/api/admin/tia/status"); } catch (e) { tia = { ok: false, message: e.message }; }
      ready = { settings: d.settings, info: d.info, ask, tia, disk };
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
    if (info.restartNeeded) list.push({ key: "apply", title: PK.t("Apply the saved settings"), done: false, text: PK.t("PLC-Ki uses them after a quick restart (a few seconds)."), btn: PK.t("Apply now"), action: "restart" });
    list.push({ key: "setup", title: PK.t("Pick your setup"), done: !!s.setupChosen, text: s.setupChosen ? PK.t(preset.title) : PK.t("Same PC as TIA, a PC with a TIA VM, or a server with your laptop."), href: "#/setup/start", btn: PK.t("Start") });
    const live = c && c.state === "connected";
    list.push({ key: "plc", title: PK.t("Connect the PLC"), done: live, text: live ? PK.t("Reading {address}, live.", { address: c.address }) : PK.srv(PK.connInfo().title), href: "#/setup/start/plc", btn: PK.t("Connect") });
    const demo = DEMO.test(s.model || "");
    list.push({ key: "program", title: PK.t("Teach it your machine"), done: !demo, text: demo ? PK.t("PLC-Ki shows the demo mixing station until you import your TIA program.") : PK.t("Watching {name}.", { name: state.info ? state.info.machine : PK.t("your program") }), href: "#/setup/program", btn: PK.t("Import") });
    const keyOk = info.apiKey !== "none";
    list.push({ key: "ask", title: PK.t("Turn on Ask"), done: keyOk, text: keyOk ? PK.t("Ki can answer questions and read programs.") : PK.t("Needs a Claude API key. Without it, Ask and program import stay off."), href: "#/setup/start/assistant", btn: PK.t("Add key") });
    const t = r.tia || {};
    list.push({ key: "tia", title: PK.t("TIA helper"), optional: true, done: !!t.ok, text: t.ok ? PK.t("Ready on {name}.", { name: t.machine || PK.t("the TIA PC") }) : PK.t("Only for importing a program and the block generator.") + " " + PK.srv(PK.plain(t.message).text), href: "#/setup/start/tia", btn: PK.t("Set up") });
    return list;
  }

  function checklist(list, compact) {
    return `<ul class="todo">${list.map((i) => `<li class="${i.done ? "done" : ""}">
      <span class="tick">${i.done ? icon("check") : ""}</span>
      <div class="t"><b>${esc(i.title)}${i.optional ? ` <span class="tag">${PK.t("optional")}</span>` : ""}</b><span>${esc(i.text)}</span></div>
      ${i.done ? "" : i.action === "restart" ? `<button class="btn small gold" data-restart>${esc(i.btn)}</button>`
        : `<a class="btn small ${compact ? "b" : ""}" href="${i.href}">${esc(i.btn)}</a>`}</li>`).join("")}</ul>`;
  }

  /** The "Getting started" card at the top of the machine pages, for administrators, until everything needed is done. */
  function card() {
    if (!PK.can("settings")) return "";
    if (!ready) { readiness().then((r) => r && PK.draw()); return ""; }
    // The disk nearly full: shown to administrators even when everything is set up (Setup › Settings › Storage)
    const disk = ready.disk && ready.disk.low ? `<section class="guide-sec"><div class="wrap"><div class="notice red" role="alert">${icon("info")}
      <div class="grow">${esc(PK.srv(ready.disk.warning))}</div><a class="btn small" href="#/setup/settings/storage">${PK.t("Open Storage")}</a></div></div></section>` : "";
    if (session.get(HIDE) === "1") return disk;
    const list = items(ready);
    const need = list.filter((i) => !i.optional);
    const done = need.filter((i) => i.done).length;
    if (done === need.length) return disk;
    const next = list.find((i) => !i.done && !i.optional) || list.find((i) => !i.done);
    // A slim bar below the page's own content: the machine comes first, set-up second
    return disk + `<section class="guide-sec"><div class="wrap"><div class="guide-bar" role="region" aria-label="${PK.t("Getting started")}">
      <div class="gb-text"><span class="gb-count">${PK.t("Getting started · {done} of {total} done", { done, total: need.length })}</span>
        <span class="gb-next"><b>${PK.t("Next: {title}.", { title: esc(next.title) })}</b> ${esc(next.text)}</span></div>
      <div class="gb-actions">${next.action === "restart" ? `<button class="btn small gold" data-restart>${esc(next.btn)}</button>` : `<a class="btn small" href="${next.href}">${esc(next.btn)}</a>`}
        <a class="btn plain small" href="#/setup/start/done">${PK.t("See all")}</a><button class="btn plain small" data-hide-guide>${PK.t("Hide")}</button></div>
      <div class="bar"><div style="width:${Math.round(done / need.length * 100)}%"></div></div></div></div></section>`;
  }
  function bindCard(page) {
    const h = page.querySelector("[data-hide-guide]");
    if (h) h.onclick = () => { session.set(HIDE, "1"); PK.draw(); };
    page.querySelectorAll(".guide-bar [data-restart]").forEach((b) => b.onclick = PKSetup.restart);
  }

  // ---------- the wizard ----------
  const STEPS = [
    ["setup", PK.T("Your setup")], ["plc", PK.T("The PLC")], ["tia", PK.T("TIA Portal")], ["assistant", PK.T("Ask")], ["program", PK.T("Your machine")], ["done", PK.T("Done")],
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
      same: `<rect x="4" y="8" width="168" height="76" rx="14" fill="none" stroke="var(--line-strong)" stroke-dasharray="3 5"/><text x="14" y="24" font-size="10" fill="var(--muted)">${PK.t("one PC")}</text>
        ${b(12, 32, 72, "PLC-Ki", PK.t("browser"), true)}${b(92, 32, 72, "TIA", PK.t("+ helper"))}${l(172, 206, 54)}${b(206, 32, 66, PK.t("PLC"), "S7")}`,
      vm: `<rect x="4" y="8" width="176" height="76" rx="14" fill="none" stroke="var(--line-strong)" stroke-dasharray="3 5"/><text x="14" y="24" font-size="10" fill="var(--muted)">${PK.t("your PC")}</text>
        ${b(12, 32, 72, "PLC-Ki", PK.t("browser"), true)}${b(96, 32, 76, "VM", PK.t("TIA + helper"))}${l(180, 206, 54)}${b(206, 32, 66, PK.t("PLC"), "S7")}`,
      server: `${b(4, 32, 70, "Server", "PLC-Ki", true)}${l(74, 104, 54)}<rect x="104" y="8" width="94" height="76" rx="14" fill="none" stroke="var(--line-strong)" stroke-dasharray="3 5"/>
        <text x="114" y="24" font-size="10" fill="var(--muted)">${PK.t("laptop")}</text>${b(112, 32, 78, "Link", "+ TIA")}${l(198, 206, 54)}${b(206, 32, 66, PK.t("PLC"), "S7")}`,
    }[kind];
    return `<svg viewBox="0 0 276 92" class="mini" aria-hidden="true" font-family="inherit">${svg}</svg>`;
  }

  const daily = {
    same: { where: [[PK.T("This PC"), PK.T("PLC-Ki, TIA Portal, and the cable to the PLC.")]],
      every: [PK.T("Start PLC-Ki (double-click Start-PLC-Ki). That is all."), PK.T("PLC-Ki starts the TIA helper by itself when you import a program or build blocks.")] },
    vm: { where: [[PK.T("Your PC"), PK.T("PLC-Ki. It reads the PLC directly.")], [PK.T("The VM"), PK.T("TIA Portal and the TIA helper.")]],
      every: [PK.T("Start PLC-Ki on the PC (double-click Start-PLC-Ki)."), PK.T("Live values, alarms and Ask work with the VM off."), PK.T("The VM is only needed to import a program or for the block generator. After a one-time setup, the TIA helper starts by itself when you log into the VM.")] },
    server: { where: [[PK.T("The server"), PK.T("PLC-Ki, open from any browser.")], [PK.T("Your laptop"), PK.T("PLC-Ki Link and TIA Portal. It reaches the PLC.")]],
      every: [PK.T("PLC-Ki runs on the server by itself."), PK.T("On the laptop, start PLC-Ki Link. It reads the PLC and sends the values to the server, and does the TIA work.")] },
  };

  const preset = () => PKSetup.presetOf(w);
  const field = (id, label, value, opts = {}) => `<label class="field">${label}${opts.hint ? `<span class="hint">${opts.hint}</span>` : ""}
    <input id="${id}" value="${esc(value || "")}" ${opts.type ? `type="${opts.type}"` : ""} placeholder="${esc(opts.placeholder || "")}" autocomplete="${opts.type === "password" ? "new-password" : "off"}" spellcheck="false"></label>`;
  const result = (r) => !r ? "" : r.busy ? `<span class="pill"><span class="dot"></span>${PK.t("Checking…")}</span>`
    : r.ok ? `<div class="notice green">${icon("check")}<div>${esc(r.text)}</div></div>`
    : `<div class="notice red">${icon("warn")}<div><b>${esc(r.text)}</b>${r.todo ? `<br>${esc(r.todo)}` : ""}</div></div>`;

  function stepSetup() {
    const p = preset();
    const d = daily[p];
    return {
      title: PK.t("Which setup is yours?"),
      sub: PK.t("PLC-Ki needs the PLC for live values, and TIA Portal only to import a program or build blocks. Pick the one that looks like yours. You can change it later."),
      body: `<div class="setups">${Object.entries(PKSetup.presets).map(([k, x]) => `<button type="button" class="setup-opt ${p === k ? "on" : ""}" data-preset="${k}" aria-pressed="${p === k}" aria-label="${esc(PK.t(x.title) + ". " + PK.t(x.text))}">
          ${mini(k)}<b>${esc(PK.t(x.title))}</b><small>${esc(PK.t(x.text))}</small></button>`).join("")}</div>
        <div class="card explain">
          <div class="grid two">
            <div><h3>${PK.t("What runs where")}</h3><dl class="where-list">${d.where.map(([a, b]) => `<dt>${esc(PK.t(a))}</dt><dd>${esc(PK.t(b))}</dd>`).join("")}</dl></div>
            <div><h3>${PK.t("Every day")}</h3><ul class="plainlist">${d.every.map((x) => `<li>${esc(PK.t(x))}</li>`).join("")}</ul></div>
          </div>
          ${p === "same" ? "" : `<p class="mt-4 mb-0 faint small">${PK.t("Tip: installed with {link} (the installer), PLC-Ki runs by itself in the background and starts with Windows, so there is no window to keep open.", { link: `<a href="${SETUP_URL}" target="_blank" rel="noopener">PLC-Ki-Setup.exe</a>` })}</p>`}</div>
        ${p === "same" && PKSetup.info.service ? `<div class="notice gold">${icon("info")}<div>${PK.t("PLC-Ki runs as a Windows service on this PC, so it cannot open the TIA helper window itself.")}
          ${PK.t("Set the helper up as for a VM (double-click Setup-TIA-helper on this PC) and choose {preset} with the address {address}.", { preset: `<b>${PK.t("PC with a TIA VM")}</b>`, address: "<code>http://localhost:5090</code>" })}</div></div>` : ""}`,
    };
  }

  function stepPlc() {
    const viaLink = w.plcVia === "link";
    const c = state.status && state.status.connection;
    const liveNow = c && c.state === "connected" && c.address === w.address;
    let linkPart = "";
    if (viaLink) {
      const l = link;
      const key = linkKey || PK.t("<the key>");
      const cmdText = `cd /d %USERPROFILE%\\Downloads\\PLC-Ki && dotnet run --project src\\PlcKi.Server -- link --server ${location.origin} --key ${key}${w.tiaMode === "link" ? ` --tia "${helperExe}"` : ""}`;
      linkPart = `<div class="card stack">
        <h3>${PK.t("PLC-Ki Link on your laptop")}</h3>
        <p class="m-0 muted">${PK.t("The laptop reads the PLC and sends the values here. It only connects out to this server, so the plant network needs no changes.")}</p>
        <ol class="howto">
          <li><b>${PK.t("Make a key.")}</b> ${PK.t("It lets your laptop in. It is shown once.")}
            <div class="mt-2 row"><button class="btn small" data-link-key>${l && l.hasKey ? PK.t("Make a new key") : PK.t("Make a key")}</button>
            ${linkKey ? `<span class="pill ok"><span class="dot"></span>${PK.t("Key made. It is in the command below.")}</span>` : l && l.hasKey ? `<span class="faint small">${PK.t("A key exists. Make a new one if you lost it.")}</span>` : ""}</div></li>
          <li><b>${PK.t("On the laptop, open Command Prompt and paste this one line.")}</b> ${PK.t("Leave the window open.")}${cmd(PK.t("On the laptop"), cmdText)}</li>
          <li><b>${PK.t("Wait here.")}</b> ${PK.t("This turns green within a few seconds:")}
            <div class="mt-2">${!l ? `<span class="pill">${PK.t("Checking…")}</span>` : l.connected ? `<span class="pill ok"><span class="dot"></span>${PK.t("Link connected from {name}", { name: esc(l.machine || PK.t("the laptop")) })}${l.plc && l.plc.connected ? ", " + PK.t("reading the PLC") : ""}</span>` : `<span class="pill bad"><span class="dot"></span>${PK.t("Link not connected yet")}</span>`}</div></li>
        </ol></div>`;
    }
    return {
      title: PK.t("Where is the PLC?"),
      sub: viaLink ? PK.t("Your laptop reads the PLC at this address and sends the values to PLC-Ki.") : PK.t("PLC-Ki reads the PLC over the network. It changes values on a real PLC only if you allow that later, in Setup."),
      body: `<div class="card stack">
          <div class="grid two">${field("w-address", PK.t("PLC address (IP)"), w.address, { placeholder: PK.t("e.g. 192.168.0.1") })}
            <div class="row" style="align-self:end;margin-bottom:2px">${viaLink ? "" : `<button class="btn b" data-test>${PK.t("Test")}</button>`}
              ${liveNow ? `<span class="pill ok"><span class="dot"></span>${PK.t("Live now")}</span>` : ""}</div></div>
          ${result(plcTest)}
          <details class="more"><summary>${PK.t("Where do I find the address?")}</summary><p class="mt-2 mb-0 muted small">${PK.t("In TIA Portal: {devices}, click the CPU, then {props}.", { devices: `<b>${esc(PK.t("Devices & networks"))}</b>`, props: `<b>${esc(PK.t("Properties › PROFINET interface › Ethernet addresses"))}</b>` })}
            ${PK.t("For PLCSIM Advanced it is the address of the virtual PLC, as shown in PLCSIM Advanced.")}
            ${viaLink ? "" : PK.t("This PC must be in the same network: {ping} to the address should answer.", { ping: "<code>ping</code>" })}</p></details>
          <details class="more"><summary>${PK.t("The CPU has a password")}</summary><div class="mt-2">${viaLink
            ? `<p class="m-0 muted small">${PK.t("With Link the password stays on the laptop: add {option} to the Link command.", { option: "<code>--plc-password &lt;password&gt;</code>" })}</p>`
            : field("w-plcPassword", PK.t("CPU access password"), "", { type: "password", hint: PKSetup.info.plcPassword === "none" ? PK.t("Only if the CPU has access protection.") : PK.t("A password is saved. Leave empty to keep it.") })}</div></details>
        </div>${linkPart}`,
    };
  }

  function stepTia() {
    const m = w.tiaMode;
    const needApply = PKSetup.info.restartNeeded;
    const check = `<div class="row"><button class="btn b" data-check-tia>${PK.t("Check the TIA helper")}</button></div>
      ${needApply && tiaCheck && !tiaCheck.ok ? `<div class="notice gold">${icon("info")}<div class="grow">${PK.t("Your new settings are saved but not used yet. Apply them, then check again.")}</div><button class="btn gold small" data-restart>${PK.t("Apply now")}</button></div>` : ""}
      ${result(tiaCheck)}`;
    let body;
    if (m === "local") {
      body = `<div class="card stack"><h3>${PK.t("TIA Portal is on this PC")}</h3>
        <p class="m-0 muted">${PK.t("PLC-Ki starts the TIA helper by itself, in its own window, when it needs TIA. Tell it where the helper is.")}</p>
        ${field("w-tiaImporterPath", PK.t("TIA helper (TiaImporter.exe) on this PC"), w.tiaImporterPath, { placeholder: helperExe })}
        ${field("w-tiaPlcsimIp", PK.t("PLCSIM address (for test downloads)"), w.tiaPlcsimIp, { placeholder: PK.t("e.g. 192.168.0.10, empty = no PLCSIM downloads") })}
        ${check}</div>`;
    } else if (m === "remote") {
      body = `<div class="card stack"><span class="where where-vm">${PK.t("On the VM · once")}</span>
          <h3>${PK.t("Set up the TIA helper on the VM")}</h3>
          <ol class="howto">
            <li><b>${PK.t("Put the newest PLC-Ki on the VM")}</b>, ${PK.t("in {dir}: copy the PLC-Ki folder from this PC. If it is already there and was downloaded with git, this one line in Command Prompt updates it:", { dir: `<code>${VM_DIR}</code>` })}${cmd(PK.t("On the VM"), `cd /d ${VM_DIR} && git pull`)}</li>
            <li><b>${PK.t("Double-click {name}", { name: "<code>Setup-TIA-helper</code>" })}</b> ${PK.t("in File Explorer, in {dir}. Say Yes when Windows asks.", { dir: `<code>${VM_DIR}\\tools\\TiaImporter</code>` })}
              ${PK.t("A small window asks for a token (a password you make up, at least 8 characters) and the PLCSIM address, then starts the helper.")}</li>
            <li><b>${PK.t("That's it.")}</b> ${PK.t("From now on the helper starts by itself when you log into the VM, in a window called {name}. Leave it open.", { name: "<b>PLC-Ki TIA helper</b>" })}
              ${PK.t("After a PLC-Ki update, double-click {name} in the same folder: it gets the newest version, builds it and starts it again.", { name: "<code>Update-TIA-helper</code>" })}</li>
          </ol></div>
        <div class="card stack"><span class="where where-pc">${PK.t("Here · on the PC")}</span>
          <h3>${PK.t("Tell PLC-Ki where the VM is")}</h3>
          <div class="grid two">${field("w-tiaUrl", PK.t("VM address"), w.tiaUrl, { placeholder: "http://192.168.0.20:5090", hint: PK.t("The VM's IP address, port 5090.") })}
            ${field("w-tiaToken", PK.t("Token"), "", { type: "password", placeholder: PKSetup.info.tiaToken === "set" ? PK.t("•••••••• (saved)") : PK.t("the same token as on the VM"), hint: PKSetup.info.tiaToken === "set" ? PK.t("Saved. Leave empty to keep it.") : PK.t("Shown in the TIA helper window on the VM.") })}</div>
          ${field("w-tiaProject", PK.t("TIA project for generated blocks (optional)"), w.tiaProject, { placeholder: "C:\\Users\\…\\Documents\\Automation\\…\\Project.ap20", hint: PK.t("A path on the VM. Use a copy, not the plant's master project.") })}
          ${check}</div>`;
    } else {
      body = `<div class="card stack"><h3>${PK.t("TIA Portal on your laptop")}</h3>
        <p class="m-0 muted">${PK.t("PLC-Ki Link carries the TIA work to the laptop. The Link command on the previous page already starts the TIA helper there (the part with {option}).", { option: "<code>--tia</code>" })}</p>
        ${field("w-tiaProject", PK.t("TIA project for generated blocks (optional)"), w.tiaProject, { placeholder: "C:\\Users\\…\\Documents\\Automation\\…\\Project.ap20", hint: PK.t("A path on the laptop.") })}
        ${check}</div>`;
    }
    return {
      title: PK.t("TIA Portal"),
      sub: PK.t("Only needed to import a program from a TIA archive and for the block generator. Live values, alarms and Ask work without it, so you can skip this and come back later."),
      body, skip: true,
    };
  }

  function stepAssistant() {
    const k = PKSetup.info.apiKey;
    return {
      title: PK.t("Turn on Ask."),
      sub: PK.t("Ki answers questions about the machine (\"why is the alarm on?\") and reads your program once, at import, to write the plain-English explanations. Both need a Claude API key."),
      body: `<div class="card stack">
        ${k !== "none" ? `<div class="notice green">${icon("check")}<div>${k === "environment" ? PK.t("A key is already set on this PC (outside PLC-Ki). You can leave this empty.") : PK.t("A key is saved. Leave empty to keep it.")}</div></div>` : ""}
        ${field("w-apiKey", PK.t("Claude API key"), "", { type: "password", placeholder: "sk-ant-…", hint: PK.t("From console.anthropic.com, API keys. Stored encrypted on this PC.") })}
        <p class="m-0 faint small">${PK.t("Questions cost a few cents each. Importing a program costs a few dollars, once.")}</p></div>`,
      skip: true,
    };
  }

  function stepProgram() {
    const demo = DEMO.test(w.model || "");
    return {
      title: demo ? PK.t("Teach it your machine.") : PK.t("Your machine."),
      sub: demo ? PK.t("Right now PLC-Ki shows a demo mixing station. Give it your TIA archive (.zap20): TIA exports the program, Ki writes what each step, device and alarm means, and you check it.")
        : PK.t("PLC-Ki watches the program you imported. Import again when the program changes."),
      body: `<div class="card stack"><div class="row"><span class="ico">${icon(demo ? "file" : "check")}</span><div class="grow"><b>${esc(state.info ? state.info.machine : "")}</b>
          <div class="faint small">${demo ? PK.t("Demo machine") : esc((w.model || "").split(/[\\/]/).pop())}</div></div>
          <a class="btn ${demo ? "" : "b"}" href="#/setup/program" data-seen>${demo ? PK.t("Import my program") : PK.t("Import again")}</a></div>
        ${demo ? `<p class="m-0 faint small">${PK.t("Needs the TIA helper (previous steps) and the Claude key. You can also keep the demo for now.")}</p>` : ""}</div>`,
      skip: demo, skipText: PK.t("Keep the demo for now"),
    };
  }

  function stepDone() {
    const list = ready ? items(ready).filter((i) => i.key !== "setup") : [];
    const apply = PKSetup.info.restartNeeded;
    const left = list.filter((i) => !i.done && !i.optional && i.key !== "apply").length;
    return {
      title: apply ? PK.t("Almost there.") : !ready ? PK.t("Checking…") : left === 0 ? PK.t("You're set up.") : left === 1 ? PK.t("One thing left.") : PK.t("{n} things left.", { n: left }),
      sub: apply ? PK.t("Apply the settings and PLC-Ki restarts with them. The page comes back by itself in a few seconds.")
        : left === 0 ? PK.t("Here is what's ready. Optional items can be done any time from Setup.")
        : PK.t("Here is what's ready and what is still open. Each one can be done any time from Setup."),
      body: `<div class="card">${ready ? checklist(list.filter((i) => i.key !== "apply")) : `<p class="muted">${PK.t("Checking…")}</p>`}</div>`,
      final: apply ? `<button class="btn big" data-restart>${PK.t("Apply and start")}</button>` : `<a class="btn big" href="#/alarm" data-seen>${PK.t("Go to the machine")}</a>`,
    };
  }

  const builders = { setup: stepSetup, plc: stepPlc, tia: stepTia, assistant: stepAssistant, program: stepProgram, done: stepDone };

  function stepIndex() { const k = (PK.current && PK.current.params[0]) || "setup"; return Math.max(0, STEPS.findIndex(([x]) => x === k)); }

  function render() {
    if (!w) return PK.hero({ title: PK.t("Get started") }) + PK.sec("", PK.ui.loading("cards", { rows: 3 }));
    const i = stepIndex();
    const [key] = STEPS[i];
    const st = builders[key]();
    const dots = `<ol class="wiz-dots">${STEPS.map(([k, l], n) => `<li class="${n < i ? "done" : n === i ? "now" : ""}"><a href="#/setup/start/${k}"><span>${n + 1}</span>${esc(PK.t(l))}</a></li>`).join("")}</ol>`;
    const nav = key === "done" ? `<div class="wiz-nav"><a class="btn b" href="#/setup/start/${STEPS[i - 1][0]}">${PK.t("Back")}</a><span class="spacer"></span>${st.final}</div>`
      : `<div class="wiz-nav">${i > 0 ? `<a class="btn b" href="#/setup/start/${STEPS[i - 1][0]}">${PK.t("Back")}</a>` : ""}<span class="spacer"></span>
          ${st.skip ? `<button class="btn plain" data-skip>${esc(st.skipText || PK.t("Skip for now"))}</button>` : ""}
          <button class="btn big" data-next ${busy ? "disabled" : ""}>${busy ? PK.t("Saving…") : PK.t("Continue")}</button></div>`;
    return PK.hero({ cls: "hero wiz", before: PKSetup.setupNav("start") + dots, eyebrow: PK.t("Step {n} of {total}", { n: i + 1, total: STEPS.length }), title: st.title, sub: st.sub })
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
      tiaCheck = t.ok ? { ok: true, text: PK.t("The TIA helper answers on {name}.", { name: t.machine || PK.t("the TIA PC") }) + (t.plcsimIp ? " " + PK.t("PLCSIM downloads go to {ip}.", { ip: t.plcsimIp }) : "") }
        : { ok: false, text: PK.srv(p.text), todo: PK.srv(p.todo) };
    } catch (e) { const p = PK.plain(e.message); tiaCheck = { ok: false, text: PK.srv(p.text), todo: PK.srv(p.todo) }; }
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
      page.querySelectorAll("[data-seen]").forEach((a) => PK.on(a, "click", () => PK.prefs.set("getStarted", "1")));
      const t = page.querySelector("[data-test]");
      if (t) t.onclick = async () => {
        read(page);
        plcTest = { busy: true }; PK.draw();
        try {
          const r = await api("POST", "/api/admin/test-plc", { address: w.address });
          const p = PK.plain(r.message, { plc: w.address });
          plcTest = r.ok ? { ok: true, text: PK.t("The PLC answers at {address}. Continue to save it.", { address: w.address }) } : { ok: false, text: PK.srv(p.text), todo: PK.srv(p.todo) };
        } catch (e) { plcTest = { ok: false, text: PK.srv(e.message) }; }
        PK.draw();
      };
      const ct = page.querySelector("[data-check-tia]");
      if (ct) ct.onclick = () => checkTia(page);
      const lk = page.querySelector("[data-link-key]");
      if (lk) lk.onclick = async () => {
        if (link && link.hasKey && !(await PK.confirmSheet({ title: PK.t("Make a new key?"), text: PK.t("Link on the laptop stops working until it is started with the new key."), ok: PK.t("Make a new key") }))) return;
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
          if (key === "setup") PK.prefs.set("getStarted", "1");
          busy = false;
          PK.go("#/setup/start/" + nextKey);
          if (nextKey === "done") readiness(true).then(PK.draw);
        } catch (e) { busy = false; toast(e, "error"); PK.draw(); }
      };
      if (key === "done") page.querySelectorAll("[data-restart]").forEach((b) => PK.on(b, "click", () => PK.prefs.set("getStarted", "1")));
    },
  });

  return {
    card, bindCard, readiness,
    /** First visit of an administrator: open Get started once. */
    async firstRun() {
      if (PK.prefs.get("getStarted") === "1") return false;
      const r = await readiness();
      return !(r && r.settings && r.settings.setupChosen);
    },
  };
})();
