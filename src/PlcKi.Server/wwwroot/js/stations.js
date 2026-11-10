// Stations: the machine split into its stations, each with one phone-sized page (#/station/<name>), and the
// QR labels that open those pages when someone scans the code on the machine (Setup › QR labels).
// The stations come from the model itself: a device or sequence belongs to the station (instance) named in its
// location, "FB_Valve, instance "Station".XV101" → Station. An alarm belongs to the station of its device, or of
// the block it names. Nothing to set up; a new import brings new stations.
"use strict";

const PKStations = (() => {
  const { esc, icon, state, sec, hero } = PK;

  /** The station named in a model location, or "". */
  function keyOf(text) {
    text = String(text || "");
    let m = /instance\s+"([^"]+)"/.exec(text) || /"([^"]+)"/.exec(text);
    if (m) return m[1];
    m = /\b(?:FB|FC)\s*\d+(?:\.\d+)*\.?\s+([^,;("]+)/.exec(text);   // real plants: "FB 1.5. SequenceProgramRobot, …"
    if (m) return m[1].trim();
    m = /^\s*(?:FB|FC)_?([A-Za-z0-9_]+)/.exec(text);
    return m ? m[1] : "";
  }
  /** Every name in a location that could be a station: "FB_Station, "DB_IO".E301_OverTemp" → Station, DB_IO. */
  function keysOf(text) {
    text = String(text || "");
    const out = new Set();
    for (const part of text.split(";")) {
      const k = keyOf(part);
      if (k) out.add(k);
      for (const m of part.matchAll(/"([^"]+)"/g)) out.add(m[1]);
      const fb = /\b(?:FB|FC)\s*\d+(?:\.\d+)*\.?\s+([^,;("]+)/.exec(part) || /^\s*(?:FB|FC)_?([A-Za-z0-9_]+)/.exec(part);
      if (fb) out.add(fb[1].trim());
    }
    return [...out];
  }
  /** "DB_Laser_Station" → "Laser Station" for headings; the key stays as it is in the program. */
  const pretty = (k) => String(k).replace(/^(?:i?DB|Inst|IDB)_/i, "").replace(/_/g, " ").trim() || k;

  const order = { fault: 0, warn: 1, ok: 2 };
  function levelOf(st) {
    const stop = PK.cpuStop();
    if (stop) return ["fault", stop.word || "STOP"];
    if (st.alarms.some((a) => a.severity !== "WARNING") || st.equipment.some((e) => e.level === "fault" || e.level === "bad") || st.sequences.some((q) => q.halted))
      return ["fault", st.alarms.length ? PK.t("Alarm") : st.sequences.some((q) => q.halted) ? PK.t("Halted") : PK.t("Fault")];
    if (st.alarms.length || st.equipment.some((e) => e.blockedBy || e.level === "warn"))
      return ["warn", st.alarms.length ? PK.t("Warning") : st.equipment.some((e) => e.blockedBy) ? PK.t("Blocked") : PK.t("Check")];
    const runs = st.sequences.some((q) => !q.halted && q.step != null) || st.equipment.some((e) => e.level === "ok");
    return ["ok", runs ? PK.t("Running") : PK.t("No problems")];
  }

  /** The stations of the machine now, from /api/status: [{ key, name, sequences, equipment, alarms, level, word }]. */
  function list(s = state.status) {
    if (!s) return { stations: [], machineAlarms: [] };
    const map = new Map();
    const get = (k) => {
      if (!map.has(k)) map.set(k, { key: k, name: k ? pretty(k) : PK.t("Other devices"), sequences: [], equipment: [], alarms: [] });
      return map.get(k);
    };
    s.equipment.forEach((e) => get(keyOf(e.location)).equipment.push(e));
    // A sequence "called in FB_Station" belongs to that station when it has devices; else to its own instance
    s.sequences.forEach((q) => {
      const caller = /called in\s+(?:(?:FB|FC)_?)?"?([A-Za-z0-9_ .]+?)"?(?:[,;)]|$)/i.exec(q.block || "");
      const k = caller && map.has(caller[1].trim()) ? caller[1].trim() : keyOf(q.block);
      get(k).sequences.push(q);
    });
    // A whole machine in one block (no instance named anywhere) is one station called after the machine
    if (map.size === 1 && map.has("")) { const only = map.get(""); only.name = s.machine || PK.t("Machine"); }
    const machineAlarms = [];
    for (const a of s.activeAlarms) {
      const hits = new Set();
      // The station of the device the alarm is about (the same rule as the Plant page, PK.deviceAlarms)
      for (const st of map.values()) {
        if (st.equipment.some((e) => PK.deviceAlarms(e, s).includes(a))) hits.add(st);
      }
      if (!hits.size) keysOf(a.location).forEach((k) => { if (map.has(k)) hits.add(map.get(k)); });
      if (!hits.size && map.size === 1) hits.add([...map.values()][0]);
      if (hits.size) hits.forEach((st) => st.alarms.push(a)); else machineAlarms.push(a);
    }
    const stations = [...map.values()].map((st) => { const [level, word] = levelOf(st); return { ...st, level, word }; })
      .sort((a, b) => order[a.level] - order[b.level] || (a.key === "") - (b.key === "") || a.name.localeCompare(b.name, undefined, { numeric: true }));
    return { stations, machineAlarms };
  }

  const href = (key, device) => "#/station/" + encodeURIComponent(key || "-") + (device ? "/" + encodeURIComponent(device) : "");
  const fromParam = (p) => (p === "-" ? "" : p);
  const pill = (level, word) => `<span class="pill ${level === "fault" ? "bad" : level}"><span class="dot"></span>${esc(word)}</span>`;
  /** What the server wrote for a person (a blocked reason, an explanation), in the language in use, first letter large. */
  const cap = (t) => { t = PK.srv(t); return t.charAt(0).toUpperCase() + t.slice(1); };
  /** The severity as the server sends it ("FAULT", "WARNING"), in the language in use. */
  const sev = (x) => (x === "WARNING" ? PK.t("WARNING") : x === "FAULT" ? PK.t("FAULT") : x);

  function devRow(e, open) {
    const look = PK.devLook(e);
    const lvl = look.lvl === "blocked" ? "warn" : look.lvl;
    const sym = window.PKVis ? window.PKVis.symbolFor(e.kind, e.id) : icon(PK.kindIcon(e.kind));
    const body = `<div class="st-dev-b">
        ${PK.devStrip(e)}
        ${e.blockedBy ? `<p class="st-why">${icon("warn")}<span>${esc(cap(e.blockedBy))}</span></p>` : ""}
        ${e.values.length ? `<div class="kv">${e.values.map((v) => `<span>${esc(v.label)}</span><span class="val">${esc(v.text)}</span>`).join("")}</div>` : ""}
        ${e.permits.length ? `<ul class="conds">${e.permits.map(PK.cond).join("")}</ul>` : ""}
        ${!e.blockedBy && e.explanation ? `<p class="m-0 muted small">${esc(PK.srv(e.explanation))}</p>` : ""}
        <div class="row"><a class="btn b small" href="#/plant/devices/${encodeURIComponent(e.id)}">${PK.t("Open on the Plant page")}</a></div></div>`;
    return `<details class="st-dev lv-${esc(lvl)}" data-dev="${esc(e.id)}" ${open ? "open" : ""}>
      <summary><span class="pm-sym">${sym}</span><span class="st-dev-n"><b>${esc(e.name)}</b><small>${esc(e.id)}${e.kind ? " · " + esc(e.kind) : ""}</small></span>
        <span class="lv lv-${esc(lvl)}">${esc(look.word)}</span></summary>${body}</details>`;
  }

  function alarmCard(a) {
    const warn = a.severity === "WARNING";
    return `<div class="card st-alarm ${warn ? "warning" : ""}">
      <div class="gap-2 row"><span class="pill ${warn ? "warn" : "bad"}"><span class="dot"></span>${esc(sev(a.severity))} · ${esc(a.id)}</span>${a.since ? `<span class="faint small">${a.atStart ? PK.t("on since PLC-Ki started") : PK.t("since {when}", { when: esc(PK.when(a.since)) })}</span>` : ""}</div>
      <h3>${esc(a.text)}</h3>
      ${a.condition ? `<p class="m-0 muted small">${esc(a.condition)}</p>` : ""}
      ${a.fix.length ? `<details class="st-fix"><summary>${PK.t("How to fix it ({n} step)|How to fix it ({n} steps)", { n: a.fix.length })}</summary><ol class="plainlist">${a.fix.map((f) => `<li>${esc(f)}</li>`).join("")}</ol>
        ${a.causes.length ? `<p class="mt-2 mb-0 muted small"><b>${PK.t("Likely causes")}:</b> ${a.causes.map(esc).join("; ")}</p>` : ""}</details>` : ""}
      <div class="row"><a class="btn small" href="#/alarms/${encodeURIComponent(a.id)}">${PK.t("Open the alarm")}</a><button class="btn b small" data-ask="Why is alarm ${esc(a.id)} on, and how do I fix it?">${PK.t("Ask")}</button></div></div>`;
  }

  function seqCard(q) {
    const L = window.PKLive;
    const [txt, cls] = L ? L.seqState(q) : ["", ""];
    const waits = q.next.flatMap((n) => n.conditions).filter((c) => c.met === false);
    return `<div class="card stack">
      <div class="gap-2 row">${txt ? `<span class="pill ${cls}"><span class="dot"></span>${txt}</span>` : ""}<span class="faint small">${esc(q.name)}${q.timeInStep ? " · " + PK.t("{time} in this step", { time: esc(q.timeInStep) }) : ""}</span></div>
      <h3>${PK.t("Step {n}: {name}", { n: esc(q.step ?? "?"), name: esc(q.stepName) })}</h3>
      ${PK.cpuStop() ? `<p class="m-0 ink-fault small">${esc(PK.srv(q.explanation))}</p>`
        : q.halted && q.haltReasons.length ? `<p class="m-0 muted small">${PK.t("Halted: {why}", { why: esc(q.haltReasons.map((x) => PK.srv(x)).join("; ")) })}</p>` : q.does ? `<p class="m-0 muted small">${esc(q.does)}</p>` : ""}
      ${L ? L.stepStrip(q) : ""}
      ${waits.length ? `<p class="mt-2 mb-0 muted small">${PK.t("Waiting for")}</p><ul class="conds">${waits.map((c) => PK.condHeld(c)).join("")}</ul>` : ""}
      <div><a class="btn b small" href="#/plant/sequence/${encodeURIComponent(q.id)}">${PK.t("Step chart")} ›</a></div></div>`;
  }

  /** One station on a phone: is it OK, its alarms with the fix, its sequence step, its devices. */
  function stationPage(key, device) {
    const s = state.status;
    const { stations, machineAlarms } = list(s);
    const st = stations.find((x) => x.key === key);
    if (!st) {
      return hero({ eyebrow: `<span class="pill warn"><span class="dot"></span>${PK.t("Not found")}</span>`, title: PK.t("There is no station \"{name}\" in the program now.", { name: esc(pretty(key) || key) }),
        sub: PK.t("The label may be older than the last program import. Pick the station below; an administrator can print new labels in Setup › QR labels.") })
        + sec("", overviewGrid(stations));
    }
    const dev = device && st.equipment.find((e) => e.id === device);
    // Blocked devices that wait on each other, root first (Level → M201 → E301 → step 30), when one of them is here
    const ids = new Set(st.equipment.map((e) => e.id));
    const chains = PK.causeChains(s).filter((c) => c.devices.some((e) => ids.has(e.id)));
    const chainOrder = chains.flatMap((c) => c.devices.map((e) => e.id));
    const rankOf = (e) => { const i = chainOrder.indexOf(e.id); return i < 0 ? 1e6 : i; };
    const problems = st.equipment.filter((e) => { const l = PK.devLook(e, s).lvl; return l !== "ok" && l !== "idle"; })
      .sort((x, y) => (PK.devLook(x, s).lvl === "fault" ? -1 : 0) - (PK.devLook(y, s).lvl === "fault" ? -1 : 0) || rankOf(x) - rankOf(y));
    const rest = st.equipment.filter((e) => !problems.includes(e));
    const first = st.alarms[0];
    const rootDev = chains.length && chains[0].devices.find((e) => e.blockedBy);
    const stop = PK.cpuStop(s);
    const sub = stop ? PK.t("The PLC is in {word} {since}: the program is not running. The values are its last state.", { word: stop.word || "STOP", since: PK.cpuSince(stop) })
      : first ? first.text : rootDev ? (/^needs /.test(rootDev.blockedBy) ? PK.t("{name} is blocked: it {why}.", { name: rootDev.name, why: PK.srv(rootDev.blockedBy) }) : PK.t("{name} is blocked: {why}.", { name: rootDev.name, why: PK.srv(rootDev.blockedBy) }))
      : problems.length ? `${problems[0].name}: ${PK.srv(problems[0].blockedBy || problems[0].state)}.`
      : st.sequences[0] ? PK.t("Step {n}: {name}.", { n: st.sequences[0].step ?? "?", name: st.sequences[0].stepName }) : PK.t("No alarm and no device needs attention.");
    const others = stations.filter((x) => x !== st);
    return hero({
      cls: "hero st-hero",
      eyebrow: `${pill(st.level, st.word)}<span class="faint">${PK.t("Station")} · ${esc(s.machine || "")}</span>`,
      title: esc(st.name),
      sub: esc(sub),
      actions: `<button class="btn" data-ask="What is station ${esc(st.name)} doing right now, and does anything need attention?">${PK.t("Ask about this station")}</button>`,
      extra: PK.ui.tiles([
        { value: st.alarms.length, label: PK.t("alarm|alarms", { n: st.alarms.length }), state: st.alarms.length ? "bad" : "" },
        { value: problems.length, label: PK.t("needs attention|need attention", { n: problems.length }), state: problems.length ? "warn" : "" },
        { value: st.equipment.length, label: PK.t("device|devices", { n: st.equipment.length }) }])
        + (chains.length && !first ? `<div class="chains">${chains.map(PK.chainHtml).join("")}</div>` : ""),
    })
      + (dev ? sec("", `<h2>${esc(dev.name)}</h2><p class="sub">${PK.t("The device on this label.")}</p><div class="st-devs">${devRow(dev, true)}</div>`) : "")
      + (st.alarms.length ? sec("", `<h2>${PK.t("Alarms")}</h2><div class="stack">${st.alarms.map(alarmCard).join("")}</div>`) : "")
      + (st.sequences.length ? sec("", `<h2>${PK.t("Sequence")}</h2><div class="stack">${st.sequences.map(seqCard).join("")}</div>`) : "")
      + (st.equipment.length ? sec("", `<h2>${PK.t("Devices")}</h2><p class="sub">${problems.length ? PK.t("Needs attention first.") + " " : ""}${PK.t("Tap one for its values and interlocks.")}</p>
          <div class="st-devs">${[...problems, ...rest].filter((e) => e !== dev).map((e) => devRow(e, false)).join("")}</div>`) : "")
      + sec("", `${machineAlarms.length ? `<div class="mb-4 notice gold">${icon("warn")}<div>${PK.t("{n} more alarm on the machine, not tied to one station.|{n} more alarms on the machine, not tied to one station.", { n: machineAlarms.length })} <a href="#/alarm">${PK.t("See them")}</a></div></div>` : ""}
          ${others.length ? `<h2>${PK.t("Other stations")}</h2>${overviewGrid(others)}` : ""}<p class="mt-4 faint small">${PK.t("Live from the PLC, updated every second. PLC-Ki only reads: nothing here changes the machine.")}</p>`);
  }

  function overviewGrid(stations) {
    if (!stations.length) return `<p class="muted">${PK.t("No stations yet: import a program first.")}</p>`;
    return `<div class="st-grid">${stations.map((x) => `<a class="card st-card lv-${x.level}" href="${href(x.key)}">
      <span class="st-card-h"><b>${esc(x.name)}</b>${pill(x.level, x.word)}</span>
      <small class="muted">${x.alarms.length ? esc(x.alarms[0].id + " " + x.alarms[0].text) : x.sequences[0] ? esc(PK.t("Step {n}: {name}", { n: x.sequences[0].step ?? "?", name: x.sequences[0].stepName })) : PK.t("{n} device|{n} devices", { n: x.equipment.length })}</small></a>`).join("")}</div>`;
  }

  function offlineHero() {
    const i = PK.connInfo();
    return hero({ eyebrow: `<span class="pill ${i.level}"><span class="dot"></span>${PK.t("No live values")}</span>`, title: PK.t("No live values from the PLC."), sub: esc(PK.srv(i.text || i.title)),
      actions: `<button class="btn" data-conn-help>${PK.t("What does this mean?")}</button>` });
  }

  PK.view("station", {
    live: true,
    nav: "plant",
    render() {
      const s = state.status;
      if (!s) return hero({ title: PK.t("Connecting to PLC-Ki…"), sub: PK.t("One moment.") });
      const c = s.connection;
      if (c && (c.state === "disconnected" || c.state === "waiting")) return offlineHero();
      const p = (PK.current && PK.current.params) || [];
      if (p.length) return stationPage(fromParam(p[0]), p[1]);
      const { stations, machineAlarms } = list(s);
      const admin = PK.can("settings");
      return hero({ eyebrow: `<span class="faint">${esc(s.machine || "")}</span>`, title: PK.t("Stations"), sub: PK.t("Each station on one page, made for a phone. Scan the QR label on the machine to jump straight to it."),
        actions: admin ? `<a class="btn b" href="#/setup/qr">${PK.t("Print QR labels")}</a>` : "" })
        + sec("", grid(stations, machineAlarms));
    },
    after(page) {
      if (window.PKLive) window.PKLive.bindAsk(page);
      // Keep open device panels open across the one-second redraws
      page.querySelectorAll("details.st-dev, details.st-fix").forEach((d) => d.addEventListener("toggle", () => {
        const id = d.dataset.dev || d.closest(".st-alarm")?.querySelector(".pill")?.textContent;
        if (id) openSet[d.open ? "add" : "delete"](id);
      }));
      page.querySelectorAll("details.st-dev, details.st-fix").forEach((d) => {
        const id = d.dataset.dev || d.closest(".st-alarm")?.querySelector(".pill")?.textContent;
        if (id && openSet.has(id)) d.open = true;
      });
    },
  });
  const openSet = new Set();

  // ---------- Setup › QR labels ----------
  const QR_BASE = "plcki.qrBase";
  let phone = null, phoneErr = null, picked = null, size = "medium";
  const store = { get: (k) => { try { return localStorage.getItem(k); } catch { return null; } }, set: (k, v) => { try { localStorage.setItem(k, v); } catch { /* private window */ } } };

  function baseChoices() {
    const out = [];
    const port = phone ? phone.port : Number(location.port) || 80;
    const add = (url, text) => { if (!out.some((o) => o.url === url)) out.push({ url, text }); };
    if (!/^(localhost|127\.|\[::1\])/.test(location.hostname)) add(location.origin, PK.t("The address in this browser"));
    (phone ? phone.addresses : []).forEach((a) => add(`http://${a.ip}${port === 80 ? "" : ":" + port}`, a.card + (a.virtual ? " · " + PK.t("virtual, phones usually cannot reach it") : "")));
    if (phone && phone.hostName) add(`http://${phone.hostName}${port === 80 ? "" : ":" + port}`, PK.t("This PC's name (works when the network knows it)"));
    return out;
  }
  function base() {
    const saved = store.get(QR_BASE);
    if (saved) return saved;
    const c = baseChoices();
    return c.length ? c[0].url : location.origin;
  }

  function targets() {
    const s = state.status;
    const { stations } = list(s);
    const out = [{ id: "all", title: PK.t("All stations"), sub: s ? s.machine : "", hash: "#/station" }];
    for (const st of stations) {
      out.push({ id: "st:" + st.key, title: st.name, sub: PK.t("Station"), hash: href(st.key), station: st.key });
      for (const e of st.equipment) out.push({ id: "dev:" + st.key + ":" + e.id, title: `${e.id} ${e.name !== e.id ? e.name : ""}`.trim(), sub: st.name, hash: href(st.key, e.id), station: st.key, device: true });
    }
    return out;
  }

  function label(t, b) {
    const url = b.replace(/\/+$/, "") + "/" + t.hash;
    let code;
    try { code = PKQR.svg(url, { label: PK.t("QR code for {name}", { name: t.title }) }); } catch (e) { code = PK.ui.error(e); }
    return `<figure class="qr-label">${code}<figcaption><small>${esc(t.sub || "")}</small><b>${esc(t.title)}</b><span class="qr-hint">${PK.t("Scan for live status and alarms")}</span><span class="qr-url">${esc(url)}</span></figcaption></figure>`;
  }

  PK.view("setup/qr", {
    live: false,
    async mount() {
      try { phone = await PK.api("GET", "/api/admin/phone-address"); phoneErr = null; } catch (e) { phoneErr = e.message; }
      PK.draw();
    },
    render() {
      const s = state.status;
      if (!s) return hero({ title: PK.t("Station"), extra: PK.ui.loading("tiles", { rows: 3 }) }) + PK.sec("", PK.ui.loading("cards", { rows: 3 }));
      const all = targets();
      if (!picked) picked = new Set(all.filter((t) => !t.device).map((t) => t.id));
      const b = base();
      const choices = baseChoices();
      const chosen = all.filter((t) => picked.has(t.id));
      const nav = typeof PKSetup !== "undefined" ? PKSetup.setupNav("qr") : "";
      const groups = new Map();
      all.filter((t) => t.id !== "all").forEach((t) => groups.set(t.station, [...(groups.get(t.station) || []), t]));
      const box = (t) => `<label class="qr-pick"><input type="checkbox" data-pick="${esc(t.id)}" ${picked.has(t.id) ? "checked" : ""}><span>${esc(t.title)}</span></label>`;
      return PK.hero({ cls: "hero compact no-print", before: nav, title: PK.t("QR labels"), sub: PK.t("Print a label for each station (and, if you like, each device) and stick it on the machine. Scanning it with a phone opens that station's live status and alarms.") })
        + PK.sec("no-print", `<div class="grid two">
          <div class="card stack"><h3>${PK.t("1. The address phones use")}</h3>
            ${phone && !phone.listensToOthers ? `<div class="notice gold">${icon("warn")}<div><b>${PK.t("Phones cannot open PLC-Ki yet.")}</b> ${PK.t("It only answers this PC. Turn on {setting} in {link}, then Apply.", { setting: `<i>${PK.t("Other computers may open PLC-Ki")}</i>`, link: `<a href="#/setup/settings">${PK.t("Settings")}</a>` })}</div></div>` : ""}
            ${phoneErr ? `<div class="notice gold">${icon("warn")}<div>${esc(PK.srv(phoneErr))}</div></div>` : ""}
            <div class="qr-bases">${choices.map((c) => `<label class="qr-base"><input type="radio" name="qrbase" value="${esc(c.url)}" ${c.url === b ? "checked" : ""}><span><b>${esc(c.url)}</b><small>${esc(c.text)}</small></span></label>`).join("")}
              <label class="qr-base"><input type="radio" name="qrbase" value="" ${choices.some((c) => c.url === b) ? "" : "checked"}><span><b>${PK.t("Another address")}</b><input id="qr-own" type="text" inputmode="url" placeholder="http://plc-ki.plant.local:5080" value="${choices.some((c) => c.url === b) ? "" : esc(b)}"></span></label></div>
            <p class="m-0 faint small">${PK.t("The phone must be on a network that reaches this PC (plant Wi-Fi). Open the address on a phone once to check. People log in once on each phone, unless {setting} is on.", { setting: `<i>${PK.t("Viewing without login")}</i>` })}</p></div>
          <div class="card stack"><h3>${PK.t("2. Which labels")}</h3>
            <div class="gap-2 row"><button class="btn b small" data-sel="stations">${PK.t("Stations only")}</button><button class="btn b small" data-sel="all">${PK.t("Everything")}</button><button class="btn b small" data-sel="none">${PK.t("None")}</button></div>
            <div class="qr-picks">${box(all[0])}${[...groups].map(([k, list]) => `<div class="qr-grp">${list.map(box).join("")}</div>`).join("")}</div>
            <h3>${PK.t("3. Size")}</h3>
            ${PK.ui.choice(PK.t("Label size"), [["small", PK.t("Small, 4 across")], ["medium", PK.t("Medium, 3 across")], ["large", PK.t("Large, 2 across")]].map(([k, l]) => ({ label: l, on: size === k, data: { "data-size": k } })))}
          </div></div>
          <div class="mt-5 actions"><button class="btn big" data-print ${chosen.length ? "" : "disabled"}>${PK.t("Print {n} label|Print {n} labels", { n: chosen.length })}</button><span class="muted small">${PK.t("Print on sticker paper, or laminate. Labels that are older than a new program import still work while the station keeps its name.")}</span></div>`)
        + PK.sec("qr-sec", `<div class="qr-sheet qr-${size}">${chosen.map((t) => label(t, b)).join("")}</div>`);
    },
    after(page) {
      const redraw = () => PK.draw();
      page.querySelectorAll("[data-pick]").forEach((c) => c.onchange = () => { c.checked ? picked.add(c.dataset.pick) : picked.delete(c.dataset.pick); redraw(); });
      page.querySelectorAll("[data-sel]").forEach((b) => b.onclick = () => {
        const all = targets();
        picked = new Set(b.dataset.sel === "none" ? [] : all.filter((t) => b.dataset.sel === "all" || !t.device).map((t) => t.id));
        redraw();
      });
      page.querySelectorAll("[data-size]").forEach((b) => b.onclick = () => { size = b.dataset.size; redraw(); });
      page.querySelectorAll("input[name=qrbase]").forEach((r) => r.onchange = () => {
        const own = page.querySelector("#qr-own");
        const v = r.value || (own && own.value.trim());
        if (v) store.set(QR_BASE, v.replace(/\/+$/, ""));
        if (!r.value && own) own.focus(); else redraw();
      });
      const own = page.querySelector("#qr-own");
      if (own) own.onchange = () => {
        const v = own.value.trim().replace(/\/+$/, "");
        if (!v) return;
        store.set(QR_BASE, /^https?:\/\//i.test(v) ? v : "http://" + v);
        redraw();
      };
      const pr = page.querySelector("[data-print]");
      if (pr) pr.onclick = () => window.print();
    },
  });

  /** The station cards, with a note for alarms not tied to one station (also the Plant › Stations tab). */
  const grid = (stations, machineAlarms = []) => (machineAlarms.length ? `<div class="mb-4 notice gold">${icon("warn")}<div>${PK.t("{n} alarm not tied to one station.|{n} alarms not tied to one station.", { n: machineAlarms.length })} <a href="#/alarm">${PK.t("See them")}</a></div></div>` : "") + overviewGrid(stations);

  return { list, keyOf, href, grid, levelOf };
})();
