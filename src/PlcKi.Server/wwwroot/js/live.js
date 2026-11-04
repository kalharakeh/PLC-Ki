// Live views: Active alarm (the start page), Plant, Alarms. Built from /api/status and the machine model.
"use strict";

(() => {
  const { esc, icon, state, cond, sec, hero } = PK;

  const fixDone = {};            // alarm id -> Set of fix steps ticked off on this screen
  let selectedAlarm = null;
  let selectedEq = null;

  /** Devices the program asks for whose output stays off, with what they need (from the server). */
  const blockedOf = (s) => (s.equipment || []).filter((e) => e.blockedBy);
  /** "Agitator M201 is blocked: it needs tank level above 20 %, now Level 15 %." */
  const blockedLine = (e) => `${e.name} is blocked: ${/^needs /.test(e.blockedBy) ? "it " : ""}${e.blockedBy}.`;
  function blockedNotice(list, max = 3) {
    if (!list.length) return "";
    return `<div class="notice gold blocked-list">${icon("warn")}<div>${list.slice(0, max).map((e) =>
      `<div><b>${esc(e.name)}</b> is blocked: ${esc(/^needs /.test(e.blockedBy) ? "it " + e.blockedBy : e.blockedBy)}.</div>`).join("")}
      ${list.length > max ? `<div class="small">And ${list.length - max} more on the Plant page.</div>` : ""}</div></div>`;
  }
  const opened = Date.now();
  /** No status from PLC-Ki (yet): say so, and what to do if it stays that way. */
  function waitingPage() {
    if (Date.now() - opened < 6000) return hero({ title: "Connecting to PLC-Ki…", sub: "One moment." });
    const i = PK.connInfo();
    return hero({ eyebrow: `<span class="pill bad"><span class="dot"></span>Not reachable</span>`, title: esc(i.title), sub: esc(i.todo),
      actions: `<button class="btn" onclick="location.reload()">Try again</button>` });
  }
  const guide = () => (typeof PKStart !== "undefined" ? PKStart.card() : "");

  /** The PLC is not read (not connected, or waiting for Link): explain instead of showing old values. */
  function offlinePage() {
    const i = PK.connInfo();
    const admin = state.user && state.user.role === "admin";
    return hero({
      cls: "hero offline-page",
      eyebrow: `<span class="pill ${i.level}"><span class="dot"></span>${i.level === "warn" ? "Waiting" : "Not connected"}</span>`,
      title: "No live values from the PLC.",
      sub: esc(i.text || i.title),
      actions: admin ? `<a class="btn" href="#/setup/start/plc">Fix the connection</a><button class="btn b" data-conn-help>What does this mean?</button>`
        : `<button class="btn" data-conn-help>What does this mean?</button>`,
      extra: i.todo ? `<div class="notice gold" style="margin-top:26px;max-width:720px">${icon("info")}<div><b>What to do</b><br>${esc(i.todo)}${admin ? "" : "<br>If it stays like this, tell your administrator."}</div></div>` : "",
    });
  }
  const offline = () => { const c = state.status && state.status.connection; return c && (c.state === "disconnected" || c.state === "waiting"); };

  /** 'FB_Valve, instance "Station".XV101 (output FaultNotOpen)' -> { block, instance, output } */
  function parseLoc(loc) {
    if (!loc) return {};
    return {
      block: PK.blockOf(loc),
      instance: (loc.match(/instance\s+("[^"]+"(?:\.[\w#]+)*)/) || [])[1],
      output: (loc.match(/\((?:output|bit|tag|member)\s+([\w.]+)\)/i) || [])[1],
    };
  }

  /**
   * Where an alarm is set, for "Show where it is set": the block and network that write its bit, and the bit itself.
   * A location can name several places ("FB x, transition y; latched in FC z, network 7"): the last part is where the
   * alarm bit is written. Returns { block, network, symbol, href }.
   */
  function alarmWhere(a) {
    const parts = String(a.location || "").split(";").map((x) => x.trim()).filter(Boolean);
    const part = parts.length ? parts[parts.length - 1] : "";
    const block = PK.blockOf(part) || PK.blockOf(a.location);
    if (!block) return {};
    const network = (part.match(/\bnetwork\s+(\d+)/i) || [])[1] || null;
    const def = state.model && (state.model.alarms || []).find((x) => x.id === a.id);
    const tag = def && def.tag && (state.model.tags || []).find((t) => t.id === def.tag);
    const symbol = tag && tag.symbol && tag.symbol.startsWith('"') ? tag.symbol : null;
    const href = "#/program/" + [block, network ? "network-" + network : null, symbol ? "@" + symbol : null, "as-rungs"].filter(Boolean).map(encodeURIComponent).join("/");
    return { block, network, symbol, href };
  }

  function relatedEquipment(alarm) {
    const loc = parseLoc(alarm.location);
    return ((state.status && state.status.equipment) || []).filter((e) => {
      const el = parseLoc(e.location);
      if (loc.instance && el.instance && loc.instance === el.instance) return true;
      return new RegExp("\\b" + e.id + "\\b").test(alarm.text + " " + (alarm.location || ""));
    });
  }

  /** OB -> ... -> block, from the call tree of the program export */
  function callPath(block) {
    const p = state.program;
    if (!p || !block) return null;
    const byName = Object.fromEntries(p.blocks.map((b) => [b.name, b]));
    const seen = new Set();
    function dfs(b, path) {
      if (b.name === block) return path;
      if (seen.has(b.name)) return null;
      seen.add(b.name);
      for (const c of b.calls || []) {
        const next = byName[c.block];
        const r = next && dfs(next, [...path, { block: c.block, instance: c.instance }]);
        if (r) return r;
      }
      return null;
    }
    for (const r of p.blocks.filter((b) => b.kind === "OB")) {
      seen.clear();
      const res = dfs(r, [{ block: r.name }]);
      if (res) return res;
    }
    return null;
  }

  function links(loc) {
    const prog = state.program && state.program.blocks.find((b) => b.name === loc.block);
    if (!prog) return "";
    const rest = [loc.block, loc.instance, loc.output].filter(Boolean).map(encodeURIComponent).join("/");
    return `<a class="btn white" href="#/program/${rest}">Show in the program</a>`;
  }

  function eqCard(e, full) {
    const permOk = e.permits.every((p) => p.met !== false);
    const chain = `<div class="chain">
      <span class="lnk ${e.requested === true ? "" : "off"}">Requested ${e.requested === true ? "yes" : e.requested === false ? "no" : "?"}</span><i>›</i>
      <span class="lnk ${permOk ? "" : "no"}">Interlocks ${permOk ? "OK" : "blocking"}</span><i>›</i>
      <span class="lnk ${e.commanded === true ? "" : "off"}">Output ${e.commanded === true ? "on" : e.commanded === false ? "off" : "?"}</span><i>›</i>
      <span class="lnk ${e.level === "ok" ? "" : e.level === "idle" ? "off" : "no"}">${esc(e.state)}</span></div>`;
    const vals = e.values.length ? `<div class="kv">${e.values.map((v) => `<span>${esc(v.label)}</span><span class="val">${esc(v.text)}</span>`).join("")}</div>` : "";
    return `<div class="eq-top"><span class="ico">${icon(PK.kindIcon(e.kind))}</span>
        <div style="flex:1"><div class="eq-name">${esc(e.name)}</div><div class="eq-id">${esc(e.id)}${e.kind ? " · " + esc(e.kind) : ""}</div></div>
        <span class="lv lv-${esc(e.level)}">${esc(e.state)}</span></div>
      ${chain}
      ${e.blockedBy ? `<div class="eq-why">${icon("warn")}<span>${esc(e.blockedBy.charAt(0).toUpperCase() + e.blockedBy.slice(1))}</span></div>` : ""}
      ${vals}
      ${full && e.permits.length ? `<div><h3 style="margin-top:6px">Interlocks</h3><ul class="conds">${e.permits.map(cond).join("")}</ul></div>` : ""}
      ${full && e.explanation && !e.blockedBy ? `<p class="muted small" style="margin:0">${esc(e.explanation)}</p>` : ""}
      ${full ? `<div class="row">${logicLink(e)}${modelFix("equipment:" + e.id)}</div>` : `<span class="eq-more" aria-hidden="true">Details ›</span>`}`;
  }
  /** "Show in the program" for a device: its block and instance from the model's location ("FB_VFD, instance "Station".M201"). */
  function logicLink(e) {
    const l = parseLoc(e.location);
    return l.block ? `<div><a class="btn b small" href="#/program/${[l.block, l.instance].filter(Boolean).map(encodeURIComponent).join("/")}">Show in the program</a></div>` : "";
  }

  /** The steps of a sequence. A short one shows every step as a pill (they wrap). A long one (GRAPH chains
   *  often have 50+ steps) shows the step it is in and where it can go next, with the whole list folded away. */
  function stepStrip(seq) {
    const def = state.model && state.model.sequences.find((d) => d.id === seq.id);
    if (!def || !def.steps.length) return "";
    const curIdx = def.steps.findIndex((st) => st.no === seq.step);
    const cur = curIdx >= 0 ? def.steps[curIdx] : null;
    const curCls = "cur" + (seq.halted ? " halted" : "");
    if (def.steps.length <= 8) {
      return `<div class="stepstrip">${def.steps.map((st, i) =>
        `<div class="st ${i === curIdx ? curCls : i < curIdx ? "done" : ""}"><b>${st.no}</b>${esc(st.name)}</div>`).join("")}</div>`;
    }
    const seen = new Set();
    const next = seq.next.filter((n) => n.to !== seq.step && !seen.has(n.to) && seen.add(n.to));
    const now = cur ? `<div class="st ${curCls}"><b>${cur.no}</b>${esc(cur.name)}</div>`
      : `<div class="st">${seq.step != null ? `<b>${seq.step}</b>` : ""}${esc(seq.stepName || "Step not known")}</div>`;
    return `<div class="stepnow">
        <span class="sn-l">Now</span>${now}
        ${next.length ? `<span class="sn-l">Next</span>${next.map((n) => `<div class="st nx ${n.ready === false ? "wait" : n.ready ? "ready" : ""}"><b>${n.to}</b>${esc(n.toName)}</div>`).join("")}` : ""}
      </div>
      <details class="allsteps"><summary>All ${def.steps.length} steps${cur ? ` · this is step ${curIdx + 1} of ${def.steps.length}` : ""}</summary>
        <ol class="steplist">${def.steps.map((st, i) => `<li class="${i === curIdx ? curCls : ""}"${i === curIdx ? ' aria-current="step"' : ""}><b>${st.no}</b><span>${esc(st.name)}</span></li>`).join("")}</ol>
      </details>`;
  }
  /** The way to the full step chart and step timeline of a sequence. */
  const chartLink = (seq) => `<div><a class="btn b small" href="#/sequence/${encodeURIComponent(seq.id)}">Step chart and timeline ›</a></div>`;
  /** Settings (HMI values) the model says keep the machine from working, e.g. a 0 % tolerance. */
  function settingsNotice(s) {
    const bad = (s.settings || []).filter((x) => x.valid === false);
    if (!bad.length) return "";
    return `<div class="notice gold" style="margin-top:18px">${icon("warn")}<div><b>${bad.length === 1 ? "A machine setting stops it from working" : bad.length + " machine settings stop it from working"}</b>
      ${bad.map((x) => `<br>${esc(x.label)} is <b>${esc(x.value)}</b>: ${esc(x.problem || "the machine cannot work with this value")}.`).join("")}
      <br><span class="small">Set it on the HMI (or in the recipe). <a href="#/plant/settings">All settings</a></span></div></div>`;
  }
  /** "This is wrong" for administrators: correct the model entry with Ki (Setup > Program has the live check). */
  const modelFix = (target) => state.user && state.user.role === "admin" && typeof PKModel !== "undefined"
    ? `<button type="button" class="btn gray small" data-modelfix="${esc(target)}">This is wrong</button>` : "";
  /** Opening the full step list shows the current step in the middle of it, not step 1. */
  function bindSteps(page) {
    page.querySelectorAll("details.allsteps").forEach((d) => d.ontoggle = () => {
      const list = d.querySelector(".steplist"), cur = list && list.querySelector("li.cur");
      if (d.open && cur) list.scrollTop = cur.offsetTop - list.clientHeight / 2 + cur.offsetHeight / 2;
    });
  }

  function seqState(q) {
    return q.halted ? ["Halted", "bad"] : q.next.some((n) => n.ready === false) ? ["Waiting", "warn"] : ["Running", "ok"];
  }

  function bindAsk(page) {
    bindSteps(page);
    if (typeof PKStart !== "undefined") PKStart.bindCard(page);
    page.querySelectorAll("[data-conn-help]").forEach((b) => b.onclick = PK.connSheet);
    page.querySelectorAll("[data-ask]").forEach((b) => b.onclick = () => window.PKAsk && window.PKAsk.open(b.dataset.ask));
    page.querySelectorAll("[data-modelfix]").forEach((b) => b.onclick = () => PKModel.fixSheet(b.dataset.modelfix));
  }

  // ---------- Active alarm ----------
  PK.view("alarm", {
    live: true,
    mount() { PK.loadProgram().then(PK.draw); PK.draw(); },
    render() {
      const s = state.status;
      if (!s) return waitingPage();
      const alarms = s.activeAlarms;      // first-out first: the server orders them by when they came
      const seq = s.sequences.find((q) => q.halted) || s.sequences[0];
      if (offline()) return offlinePage() + guide();

      if (!alarms.length) {
        const blocked = blockedOf(s);
        const n = blocked.length;
        return hero({
          eyebrow: n ? `<span class="pill warn"><span class="dot"></span>No alarm · ${n} device${n > 1 ? "s" : ""} blocked</span>` : `<span class="pill ok"><span class="dot"></span>No alarm</span>`,
          title: n ? `No alarm, but ${n === 1 ? "1 device is" : n + " devices are"} blocked.` : "No alarm is active.",
          sub: esc(n ? blockedLine(blocked[0]) : (seq ? seq.explanation : s.summary[0] || "")),
          actions: n ? `<button class="btn" data-ask="Why is ${esc(blocked[0].name)} blocked, and what do I check?">Ask why</button><a class="btn b" href="#/plant">See the plant</a>`
            : `<a class="btn" href="#/plant">See the plant</a><button class="btn b" data-ask="What is the machine doing right now?">Ask</button>`,
        }) + (settingsNotice(s) ? sec("", settingsNotice(s)) : "")
        + (window.PKMemory ? PKMemory.unnotedBlock() : "")
        + (n ? sec("alt", `<h2>Blocked now</h2><p class="sub">The program asks for ${n === 1 ? "this device" : "these devices"}, but ${n === 1 ? "its" : "their"} output stays off.</p>
            <div class="grid ${n > 1 ? "two" : ""}">${blocked.map((e) => `<div class="card stack">${eqCard(e, true)}</div>`).join("")}</div>`) : "")
        + (seq ? sec(n ? "" : "alt", `<h2>${esc(seq.name)}</h2>${n ? `<p class="sub">${esc(seq.explanation)}</p>` : ""}${stepStrip(seq)}${chartLink(seq)}
            ${seq.next.length ? `<p class="muted small" style="margin:16px 0 8px">To go on it needs</p><ul class="conds">${seq.next.flatMap((x) => x.conditions).map(cond).join("")}</ul>` : ""}`) : "")
        + guide();
      }

      if (!alarms.some((a) => a.id === selectedAlarm)) selectedAlarm = alarms[0].id;
      const a = alarms.find((x) => x.id === selectedAlarm);
      const warn = a.severity === "WARNING";
      const eqs = relatedEquipment(a);
      const done = fixDone[a.id] || new Set();
      const atStart = alarms.filter((x) => x.atStart).length;
      const firstKnown = alarms.length > 1 && (!alarms[0].atStart || atStart === 1);
      const loc = parseLoc(a.location);
      const where = alarmWhere(a);
      const path = callPath(where.block || loc.block);
      const checks = a.checks || [];
      const failed = checks.filter((c) => c.met === false);

      const chips = alarms.length > 1 ? `<div class="alarm-chips">${alarms.map((x, i) =>
        `<button class="alarm-chip ${x.severity === "WARNING" ? "warning" : ""} ${x.id === a.id ? "sel" : ""}" data-alarm="${esc(x.id)}">
          <span class="d"></span>${i === 0 && firstKnown ? "First · " : ""}${esc(x.id)} ${esc(x.text.length > 40 ? x.text.slice(0, 38) + "…" : x.text)}</button>`).join("")}</div>
          ${atStart > 1 ? `<p class="faint small" style="margin:-12px 0 18px">${atStart} alarms were already on when PLC-Ki started watching, so which of those came first is not known.</p>` : ""}` : "";

      const kpis = [
        ...eqs.slice(0, 2).map((e) => [e.state, e.name, e.level === "ok" ? "ok" : e.level === "idle" ? "" : "bad"]),
        seq ? [`Step ${seq.step ?? "?"}`, `${seq.name}: ${seq.stepName}${seq.halted ? ", halted" : ""}`, seq.halted ? "bad" : ""] : null,
        seq && seq.timeInStep ? [seq.timeInStep, "in this step"] : null,
        a.since ? [PK.when(a.since), a.atStart ? "already on when PLC-Ki started watching" : "came at"] : null,
      ].filter(Boolean).slice(0, 4);

      return sec("hero", `${chips}
          <div class="eyebrow"><span class="pill ${warn ? "warn" : "bad"}"><span class="dot"></span>${esc(a.severity)} · ${esc(a.id)}</span></div>
          <h1>${esc(a.text)}</h1>
          ${a.condition ? `<p class="sub">${esc(a.condition)}</p>` : ""}
          ${failed.length ? `<p class="sub"><b>Failed check:</b> ${failed.map((c) => esc(c.text) + (c.values && c.values.length ? ` (now ${c.values.map((v) => esc(v.label) + " " + esc(v.text)).join(", ")})` : "")).join("; ")}</p>` : ""}
          ${settingsNotice(s)}
          <div class="actions"><button class="btn" data-ask="Why is alarm ${esc(a.id)} on, and how do I fix it?">Ask about this alarm</button>
            ${where.block ? `<a class="btn b" href="${esc(where.href)}">Show where it is set</a>` : ""}</div>
          <div class="kpis" style="margin-top:34px">${kpis.map(([v, l, c]) => `<div class="k ${c || ""}"><b>${esc(v)}</b><span>${esc(l)}</span></div>`).join("")}</div>`)

        + (window.PKMemory ? sec("", `<h2>This alarm before</h2><p class="sub">From the machine memory: every time PLC-Ki saw this alarm, and what the technicians found.</p>
            <div class="card">${PKMemory.alarmBlock(a.id)}</div>`) : "")

        + (checks.length ? sec("", `<h2>What the program checks</h2><p class="sub">The comparisons behind this alarm, ${a.checksAtAlarm ? "with the values from the last read before it came" : "with the values now"}. ✗ is the one that failed.</p>
            <div class="card"><ul class="conds">${checks.map(cond).join("")}</ul></div>`) : "")
        + sec("alt", `<h2>Fix it</h2><p class="sub">Work down the list. Tap a step when it is done. The ticks are only on this screen.</p>
          <div class="grid two">
            <div class="card">${a.fix.length ? `<ol class="fix">${a.fix.map((f, i) => `<li class="${done.has(i) ? "done" : ""}"><button type="button" class="fix-b" data-fix="${i}" aria-pressed="${done.has(i)}">${esc(f)}</button></li>`).join("")}</ol>` : `<p class="muted">No fix written for this alarm yet. Ask about it.</p>`}</div>
            <div class="card"><h3>Likely causes</h3>${a.causes.length ? `<ol class="causes">${a.causes.map((c, i) => `<li><b>${i + 1}</b><span>${esc(c)}</span></li>`).join("")}</ol>` : `<p class="muted">None written yet.</p>`}</div>
          </div>`)

        + (typeof PKDocs !== "undefined" ? PKDocs.alarmSection(a, eqs) : "")

        + sec("", `<h2>Where in the machine</h2><p class="sub">${eqs.length ? "The device behind the alarm, and why it is or is not doing its job." : esc(a.location || "No device is linked to this alarm.")}</p>
          ${eqs.length ? `<div class="grid ${eqs.length > 1 ? "two" : ""}">${eqs.map((e) => `<div class="card stack">${eqCard(e, true)}</div>`).join("")}</div>` : ""}`)

        + sec("dark", `<h2>Where in the software</h2><p class="sub">Where the program sets this alarm, from the main cycle down to the bit.</p>
          ${where.block ? `<div class="path">${(path || [{ block: where.block }]).map((p) => `<span>${esc(p.block)}${p.instance ? " " + esc(p.instance) : ""}</span>`).join("<i>›</i>")}${where.network ? `<i>›</i><span>Network ${esc(where.network)}</span>` : ""}${where.symbol ? `<i>›</i><span>${esc(where.symbol)}</span>` : ""}</div>` : ""}
          <p class="muted small" style="margin:14px 0 22px">${esc(a.location || "")}</p>
          <div class="actions">${where.block ? `<a class="btn white" href="${esc(where.href)}">Show where it is set</a>` : links(loc)}${modelFix("alarm:" + a.id)}</div>`)

        + sec("alt", `<h2>What is going on</h2>
          <div class="grid two"><div>${s.summary.map((l) => `<p class="sub" style="margin-bottom:12px">${esc(l)}</p>`).join("")}</div>
          ${seq ? `<div class="card"><h3>${esc(seq.name)}</h3>${stepStrip(seq)}${chartLink(seq)}
            ${seq.next.length ? `<p class="muted small" style="margin:12px 0 8px">To go on it needs</p><ul class="conds">${seq.next.flatMap((n) => n.conditions).map(cond).join("")}</ul>` : ""}</div>` : ""}</div>`) + guide();
    },
    after(page) {
      page.querySelectorAll("[data-alarm]").forEach((b) => b.onclick = () => { selectedAlarm = b.dataset.alarm; PK.draw(); });
      page.querySelectorAll("[data-fix]").forEach((b) => b.onclick = () => {
        const set = (fixDone[selectedAlarm] ||= new Set());
        const i = Number(b.dataset.fix);
        set.has(i) ? set.delete(i) : set.add(i);
        PK.draw();
      });
      bindAsk(page);
      if (window.PKMemory) PKMemory.bind(page);
    },
  });

  // ---------- Plant ----------
  let eqQuery = "";
  let eqOnlyProblems = false;
  // ---------- Plant ----------
  // One short header (the step, what it waits for, the key values), then four tabs so the page stays one screen:
  // Overview (what needs attention, the sequence, the machine at a glance), Devices (list and the picked one),
  // Values over time, and Data exchange. The tab is in the address: #/plant/devices/M201, #/plant/values.
  const PLANT_TABS = [["overview", "Overview"], ["devices", "Devices"], ["values", "Values over time"], ["settings", "Settings"], ["exchange", "Data exchange"]];
  // The HMI panel's operator settings and buttons (from the program export), loaded once for the Settings tab
  const H = { data: null, error: null, loading: false, q: "", shown: [] };
  async function loadHmi() {
    if (H.data || H.error || H.loading) return;
    H.loading = true;
    try {
      const r = await PK.api("GET", "/api/program/hmi");
      if (r.available) H.data = r; else H.error = r.error || "No HMI in the program export.";
    } catch (e) { H.error = e.message; }
    H.loading = false;
    PK.draw();
  }
  window.addEventListener("pk:program-changed", () => { H.data = null; H.error = null; });
  const hmiLive = (f) => (window.PKTags ? PKTags.live({ symbol: f.symbol, type: f.type }) : "");

  /** Every value an operator sets on the HMI panel, by screen, with its live value; then the buttons. */
  function hmiSettingsHtml(s) {
    if (H.loading || (!H.data && !H.error)) return `<p class="faint small">Reading the HMI panel…</p>`;
    if (!H.data) return `<p class="faint small">${esc(H.error)}</p>`;
    const d = H.data;
    const q = H.q.trim().toLowerCase();
    const hit = (f) => !q || [f.label, f.screen, f.symbol, f.comment].some((x) => String(x || "").toLowerCase().includes(q));
    const rows = d.settings.filter(hit);
    const take = rows.slice(0, 300);
    H.shown = take.filter((f) => f.type && !f.modelTag).concat(d.commands.filter(hit).slice(0, 100).filter((f) => f.type && !f.modelTag));
    const modelVal = (f) => { const m = f.modelTag && (s.settings || []).find((x) => x.id === f.modelTag); return m ? `<span class="chip" style="margin:0">${esc(m.value)}</span>` : null; };
    const byScreen = new Map();
    for (const f of take) { const k = (f.screenKind === "Popup" ? "Pop-up " : "Screen ") + f.screen + (f.screenNumber != null ? ` (no. ${f.screenNumber})` : ""); if (!byScreen.has(k)) byScreen.set(k, []); byScreen.get(k).push(f); }
    const panels = d.panels.map((p) => `${esc(p.name)}${p.type ? ` · ${esc(p.type)}` : ""}`).join(", ");
    const cmds = d.commands.filter(hit);
    return `<div class="card stack">
        <div><b>On the HMI panel</b> <span class="faint small">${panels} · ${d.settings.length} values the operator can change${d.commands.length ? `, ${d.commands.length} buttons` : ""}</span></div>
        <input class="input" type="search" placeholder="Find a setting, screen or tag" value="${esc(H.q)}" id="hmi-q" aria-label="Find a setting on the HMI">
        ${take.length ? `<table class="tbl hmi-tbl"><tr><th>On the panel</th><th>Now</th><th>PLC value</th></tr>
          ${[...byScreen].map(([screen, fs]) => `<tr class="grp"><td colspan="3"><b>${esc(screen)}</b></td></tr>`
            + fs.map((f) => `<tr><td>${esc(f.label || "(no text)")}${f.unit ? ` <span class="faint">[${esc(f.unit)}]</span>` : ""}${f.authorization ? ` <span class="faint small" title="User group needed on the panel">· ${esc(f.authorization)}</span>` : ""}</td>
              <td>${modelVal(f) || (f.type ? hmiLive(f) : `<span class="faint small">not read</span>`)}</td>
              <td class="mono small">${esc(f.symbol)}${f.comment ? `<br><span class="muted" style="font-family:inherit">${esc(f.comment)}</span>` : ""}</td></tr>`).join("")).join("")}
          </table>` : `<p class="muted">${q ? "No setting matches." : "The panel has no input fields linked to the PLC."}</p>`}
        ${rows.length > take.length ? `<p class="faint small">Showing ${take.length} of ${rows.length}. Search to narrow it down.</p>` : ""}
        ${cmds.length ? `<details class="more"><summary>Buttons and switches that change PLC values (${cmds.length})</summary>
          <table class="tbl hmi-tbl"><tr><th>On the panel</th><th>Does</th><th>PLC value</th><th>Now</th></tr>
          ${cmds.slice(0, 100).map((f) => `<tr><td>${esc(f.where)}</td><td class="small">${esc(f.action || "")}</td><td class="mono small">${esc(f.symbol)}</td><td>${f.type ? hmiLive(f) : ""}</td></tr>`).join("")}
          </table></details>` : ""}
        <p class="faint small">Read from the HMI project in the TIA archive. PLC-Ki only reads these values; change them on the panel.</p>
      </div>`;
  }

  const problemOf = (e) => !!e.blockedBy || (e.level !== "ok" && e.level !== "idle");
  const rank = (e) => (e.level === "fault" || e.level === "bad" ? 0 : e.blockedBy ? 1 : e.level === "warn" ? 2 : 3);

  PK.view("plant", {
    live: true,
    render() {
      const s = state.status;
      if (!s) return waitingPage();
      if (offline()) return offlinePage() + guide();
      const params = (PK.current && PK.current.params) || [];
      const tab = PLANT_TABS.some(([k]) => k === params[0]) ? params[0] : "overview";
      if (tab === "devices" && params[1]) selectedEq = params[1];
      const V = window.PKVis;
      const main = s.sequences[0];
      const [stText, stCls] = main ? seqState(main) : ["", ""];
      const attention = s.equipment.filter(problemOf).sort((x, y) => rank(x) - rank(y));
      const waitsFor = main ? main.next.flatMap((n) => n.conditions).filter((c) => c.met === false).map((c) => c.text) : [];
      const badSettings = (s.settings || []).filter((x) => x.valid === false);
      const counts = { devices: attention.length, exchange: s.exchange.length, settings: badSettings.length };

      const head = hero({
        eyebrow: main ? `<span class="pill ${stCls}"><span class="dot"></span>${stText}</span>${main.timeInStep ? `<span class="faint">for ${esc(main.timeInStep)}</span>` : ""}<span class="faint">· ${esc(main.name)}</span>` : "",
        title: main ? `Step ${main.step ?? "?"}: ${esc(main.stepName)}` : esc(s.machine),
        sub: main ? (main.halted ? esc("Halted: " + (main.haltReasons.join("; ") || "the outputs are off.")) : waitsFor.length ? esc("Waiting for: " + waitsFor.join("; ")) : esc(main.does || "")) : esc(s.summary[0] || ""),
        actions: `<button class="btn" data-ask="${attention.length ? `Why is ${esc(attention[0].name)} ${esc(attention[0].blockedBy ? "blocked" : attention[0].state)}, and what do I check?` : "What is the machine doing right now?"}">Ask about it</button>${s.activeAlarms.length ? `<a class="btn b" href="#/alarm">${s.activeAlarms.length} active alarm${s.activeAlarms.length > 1 ? "s" : ""}</a>` : ""}`,
        extra: (s.overview.length ? `<div class="kpis plant-kpis">${s.overview.slice(0, 8).map((v) => `<div class="k"><b>${esc(v.text)}</b><span>${esc(v.label)}</span></div>`).join("")}</div>` : "")
          + `<nav class="ptabs" aria-label="Plant">${PLANT_TABS.filter(([k]) => (k !== "exchange" || s.exchange.length) && (k !== "settings" || (s.settings || []).length || (H.data && H.data.settings.length))).map(([k, l]) =>
            `<a href="#/plant${k === "overview" ? "" : "/" + k}" class="${k === tab ? "on" : ""}" ${k === tab ? 'aria-current="page"' : ""}>${l}${(k === "devices" || k === "settings") && counts[k] ? ` <span class="count warn">${counts[k]}</span>` : ""}</a>`).join("")}<a href="#/station">Stations</a></nav>`,
      });

      loadHmi();
      if (window.PKTags) PKTags.watch("plant", () => H.shown);   // live values of the panel's settings
      if (tab !== "settings") H.shown = [];
      let body = "";
      if (tab === "overview") {
        const alarmRows = s.activeAlarms.map((x) => `<li><a class="al ${x.severity === "WARNING" ? "w" : ""}" href="#/alarm">
              <span class="ico">${icon("bell")}</span>
              <span class="nd-t"><b>${esc(x.id)} · ${esc(x.text)}</b><span>${esc(x.condition || x.location || "")}</span></span>
              <span class="lv lv-${x.severity === "WARNING" ? "warn" : "fault"}">${esc(x.severity === "WARNING" ? "warning" : "fault")}</span><span class="chev">›</span></a></li>`).join("");
        const nNeeds = attention.length + s.activeAlarms.length + badSettings.length;
        const settingRows = badSettings.map((x) => `<li><a href="#/plant/settings">
              <span class="ico">${icon("gear")}</span>
              <span class="nd-t"><b>Setting ${esc(x.label)} is ${esc(x.value)}</b><span>${esc(x.problem || "the machine cannot work with this value")}</span></span>
              <span class="lv lv-warn">setting</span><span class="chev">›</span></a></li>`).join("");
        const needs = nNeeds
          ? `<ul class="needs">${alarmRows}${settingRows}${attention.map((e) => `<li><a href="#/plant/devices/${encodeURIComponent(e.id)}">
              <span class="ico">${icon(PK.kindIcon(e.kind))}</span>
              <span class="nd-t"><b>${esc(e.name)}</b><span>${esc(e.blockedBy ? (/^needs /.test(e.blockedBy) ? "Blocked: it " + e.blockedBy : "Blocked: " + e.blockedBy) : e.explanation || e.state)}</span></span>
              <span class="lv lv-${esc(e.blockedBy ? "warn" : e.level)}">${esc(e.blockedBy ? "blocked" : e.state)}</span><span class="chev">›</span></a></li>`).join("")}</ul>`
          : `<p class="ok-line">${icon("check")}<span>No alarm, and every device is doing what the program asks.</span></p>`;
        const seqCards = s.sequences.map((q) => `<div class="card stack">
            <div class="row"><h3 style="margin:0">${esc(q.name)}</h3><span class="pill ${seqState(q)[1]}">${seqState(q)[0]}</span><span class="spacer"></span>
              <a class="small" href="#/sequence/${encodeURIComponent(q.id)}">Step chart and timeline ›</a></div>
            ${stepStrip(q)}
            ${q.does ? `<p style="margin:0"><span class="muted">This step should:</span> ${esc(q.does)}</p>` : ""}
            ${q.halted && q.haltReasons.length ? `<div class="notice red">${icon("warn")}<div><b>Halted, outputs are off:</b> ${q.haltReasons.map(esc).join("; ")}</div></div>` : ""}
            ${q.next.map((n) => `<div><p class="muted small" style="margin:0 0 6px">To go to step ${n.to} "${esc(n.toName)}" it needs</p><ul class="conds">${n.conditions.map(cond).join("")}</ul></div>`).join("")}
            ${q.limit ? `<p class="faint" style="margin:0">${esc(q.limit)}</p>` : ""}
          </div>`).join("");
        body = sec("", `<div class="plant-cols">
            <div class="stack">
              <div class="card stack"><div class="row"><h3 style="margin:0">Needs attention</h3>${nNeeds ? `<span class="count ${s.activeAlarms.some((x) => x.severity !== "WARNING") ? "" : "warn"}">${nNeeds}</span>` : ""}<span class="spacer"></span>
                <a class="small" href="#/plant/devices">All devices ›</a></div>${needs}</div>
              ${seqCards}
            </div>
            <div class="stack">
              ${V && s.equipment.length ? `<div class="card stack"><h3 style="margin:0">The machine at a glance</h3>${V.plantMap(s.equipment, null)}</div>` : ""}
              ${V ? `<a class="card plink" href="#/plant/values"><span class="ico">${icon("clock")}</span><span><b>Values over time</b><span class="muted small">The last minutes of every measured value, with steps and alarms.</span></span><span class="chev">›</span></a>` : ""}
              ${s.exchange.length ? `<a class="card plink" href="#/plant/exchange"><span class="ico">${icon("link")}</span><span><b>Data exchange</b><span class="muted small">${s.exchange.length} signals to and from ${esc([...new Set(s.exchange.map((x) => x.partner))].join(", "))}.</span></span><span class="chev">›</span></a>` : ""}
            </div>
          </div>`);
      } else if (tab === "devices") {
        const q = eqQuery.trim().toLowerCase();
        const sorted = [...s.equipment].sort((x, y) => rank(x) - rank(y));
        const shown = sorted.filter((e) => (!eqOnlyProblems || problemOf(e)) && (!q || (e.id + " " + e.name + " " + (e.kind || "") + " " + e.state).toLowerCase().includes(q)));
        const sel = s.equipment.find((e) => e.id === selectedEq) || shown[0] || sorted[0];
        const tools = `<div class="dev-tools">${s.equipment.length > 6 ? `<input type="search" id="eq-search" placeholder="Find a device" value="${esc(eqQuery)}" aria-label="Find a device">` : ""}
            <div class="segmented" role="group" aria-label="Show">
              <button type="button" class="${eqOnlyProblems ? "" : "on"}" data-eqf="all" aria-pressed="${!eqOnlyProblems}">All ${s.equipment.length}</button>
              <button type="button" class="${eqOnlyProblems ? "on" : ""}" data-eqf="problems" aria-pressed="${eqOnlyProblems}">Needs attention ${attention.length}</button></div></div>`;
        const list = shown.length ? `<ul class="devlist">${shown.map((e) => `<li><button type="button" class="${sel && e.id === sel.id ? "on" : ""}" data-pick="${esc(e.id)}" aria-pressed="${!!sel && e.id === sel.id}">
            <span class="ico">${icon(PK.kindIcon(e.kind))}</span><span class="dv-t"><b>${esc(e.name)}</b><span>${esc(e.id)}</span></span>
            <span class="lv lv-${esc(e.blockedBy ? "warn" : e.level)}">${esc(e.blockedBy ? "blocked" : e.state)}</span></button></li>`).join("")}</ul>`
          : `<div class="nomatch">${eqOnlyProblems && !q ? `${icon("check")}<b>Every device is fine.</b>` : `${icon("search")}<b>No device matches.</b>`}<button class="btn b small" data-eq-reset>Show all devices</button></div>`;
        body = sec("", `<div class="dev-cols"><div class="stack">${tools}${list}</div>
            <div data-dev>${sel ? `<div class="card stack eq-open">${eqCard(sel, true)}<div class="actions"><button class="btn small" data-ask="Why is ${esc(sel.id)} ${esc(sel.state)}?">Ask why</button></div></div>` : ""}</div></div>`);
      } else if (tab === "values") {
        body = sec("", V ? V.trends() : `<p class="muted">Charts are not available.</p>`);
      } else if (tab === "settings") {
        const where = (x) => x.hmi || (H.data && H.data.bySetting[x.id]) || "";
        body = sec("", ((s.settings || []).length ? `<p class="sub">Values an operator or engineer sets on the HMI or in the recipe that the machine needs. ✗ means the machine cannot work with the value it has now.</p>
          <div class="card"><table class="tbl"><tr><th></th><th>Setting</th><th>Value</th><th>What it does</th></tr>
            ${(s.settings || []).map((x) => `<tr><td>${x.valid === false ? `<span class="mark no" aria-label="not OK">✗</span>` : x.valid === true ? `<span class="mark yes" aria-label="OK">✓</span>` : ""}</td>
              <td><b>${esc(x.label)}</b>${where(x) ? `<br><span class="faint small">On the panel: ${esc(where(x))}</span>` : ""}</td><td class="val ${x.valid === false ? "setting-bad" : ""}">${esc(x.value)}</td>
              <td class="muted">${esc(x.text || "")}${x.valid === false && x.problem ? `<br><span class="setting-bad">${esc(x.problem)}${where(x) ? `. Change it on the panel: ${esc(where(x))}` : ""}</span>` : ""}</td></tr>`).join("")}
          </table></div>` : `<p class="sub">Values an operator sets on the HMI panel, with what they are now.</p>`)
          + `<div style="margin-top:18px">${hmiSettingsHtml(s)}</div>`);
      } else {
        body = sec("", `<div class="card"><table class="tbl xchg"><tr><th>With</th><th>Signal</th><th>Value</th><th>Meaning</th></tr>
            ${s.exchange.map((x) => `<tr><td class="with"><b title="${x.direction === "in" ? "from" : "to"} ${esc(x.partner)}">${x.direction === "in" ? "← from" : "→ to"} ${esc(x.partner)}</b></td>
              <td class="sig">${esc(x.label)}</td><td class="val">${esc(x.value)}</td><td class="muted mean">${esc(x.text || "")}</td></tr>`).join("")}
            </table></div>`);
      }
      return head + body
        + (Object.keys(s.tagErrors || {}).length ? sec("tight", `<details class="more"><summary>${Object.keys(s.tagErrors).length} signals could not be read</summary><ul class="small">${Object.entries(s.tagErrors).map(([k, v]) => `<li><code>${esc(k)}</code>: ${esc(v)}</li>`).join("")}</ul></details>`) : "")
        + guide();
    },
    after(page) {
      // The machine at a glance: a device opens on the Devices tab
      page.querySelectorAll(".pmap [data-eq]").forEach((d) => {
        d.onclick = () => PK.go("#/plant/devices/" + encodeURIComponent(d.dataset.eq));
        d.onkeydown = (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); d.click(); } };
      });
      page.querySelectorAll("[data-pick]").forEach((b) => b.onclick = () => {
        selectedEq = b.dataset.pick;
        PK.setHash("#/plant/devices/" + encodeURIComponent(selectedEq));
        PK.draw();
        if (innerWidth <= 1000) setTimeout(() => { const el = page.querySelector("[data-dev]"); if (el) window.scrollTo({ top: el.getBoundingClientRect().top + scrollY - 70, behavior: "instant" }); }, 0);
      });
      const hq = page.querySelector("#hmi-q");
      if (hq) hq.oninput = () => { H.q = hq.value; PK.draw(); };
      const es = page.querySelector("#eq-search");
      if (es) es.oninput = () => { eqQuery = es.value; PK.draw(); };
      page.querySelectorAll("[data-eqf]").forEach((b) => b.onclick = () => { eqOnlyProblems = b.dataset.eqf === "problems"; PK.draw(); });
      const r = page.querySelector("[data-eq-reset]");
      if (r) r.onclick = () => { eqQuery = ""; eqOnlyProblems = false; PK.draw(); };
      if (window.PKVis) window.PKVis.bindTrends(page);
      bindAsk(page);
    },
  });

  // ---------- Alarms ----------
  let alarmFilter = "";
  let alarmKind = "all";        // all | active | faults | warnings
  let openAlarm = null;
  let deepLink = null;          // #/alarms/A02: opened once, then scrolled to
  let scrollToAlarm = null;
  let events = null, eventsAt = 0;
  let recentOpen = false, recentAll = false;   // the "Recent alarms" strip above the list
  async function loadEvents() {
    eventsAt = Date.now();
    try { events = await PK.api("GET", "/api/events?kind=alarm&max=30"); } catch { events = events || []; }
    PK.draw();
  }
  PK.view("alarms", {
    live: true,
    leave() { deepLink = null; },
    render() {
      const s = state.status, m = state.model;
      if (!s || !m) return waitingPage();
      const want = PK.current && PK.current.params[0];
      if (want && want !== deepLink) { deepLink = want; openAlarm = want; alarmKind = "all"; alarmFilter = ""; scrollToAlarm = want; }
      if (Date.now() - eventsAt > 5000) loadEvents();
      const active = new Set(s.activeAlarms.map((a) => a.id));
      const q = alarmFilter.toLowerCase();
      const kindOk = { all: () => true, active: (a) => active.has(a.id), faults: (a) => a.severity !== "WARNING", warnings: (a) => a.severity === "WARNING" }[alarmKind] || (() => true);
      const all = m.alarms.filter((a) => kindOk(a) && (!q || (a.id + " " + a.text + " " + (a.location || "")).toLowerCase().includes(q)))
        .sort((a, b) => (active.has(b.id) - active.has(a.id)) || a.id.localeCompare(b.id, undefined, { numeric: true }));
      const faults = m.alarms.filter((a) => a.severity !== "WARNING").length;
      const rows = all.map((a) => {
        const on = active.has(a.id), open = openAlarm === a.id;
        return `<li class="click" data-open="${esc(a.id)}" id="alarm-${esc(a.id)}" style="flex-wrap:wrap" tabindex="0" aria-expanded="${open}">
          <span class="tag ${a.severity === "WARNING" ? "gold" : "red"}">${esc(a.id)}</span>
          <span style="flex:1;min-width:200px"><b style="font-weight:600">${esc(a.text)}</b>${a.location ? `<div class="faint">${esc(a.location)}</div>` : ""}</span>
          ${on ? `<span class="pill ${a.severity === "WARNING" ? "warn" : "bad"}"><span class="dot"></span>Active</span>` : ""}<span class="chev">${open ? "⌄" : "›"}</span>
          ${open ? `<div style="flex-basis:100%;padding:10px 0 4px" class="grid two">
              <div>${a.condition ? `<h3>Meaning</h3><p style="margin:0 0 12px" class="muted">${esc(a.condition)}</p>` : ""}
                ${a.causes.length ? `<h3>Likely causes</h3><ol class="causes small">${a.causes.map((c, i) => `<li><b>${i + 1}</b><span>${esc(c)}</span></li>`).join("")}</ol>` : ""}</div>
              <div>${a.fix.length ? `<h3>How to fix</h3><ol class="fix small">${a.fix.map((c) => `<li style="font-size:15px;cursor:default">${esc(c)}</li>`).join("")}</ol>` : ""}
                <div class="actions" style="margin-top:14px">${on ? `<a class="btn small" href="#/alarm">Open on Active alarm</a>` : ""}
                ${(() => { const w = alarmWhere(a); return w.block ? `<a class="btn b small" href="${esc(w.href)}">Show where it is set</a>` : ""; })()}
                <button class="btn b small" data-ask="Explain alarm ${esc(a.id)}: when does it come, why, and how is it fixed?">Ask</button></div></div>
            </div>${window.PKMemory ? `<div class="mem-in-row"><h3>This alarm before</h3>${PKMemory.alarmBlock(a.id, { compact: true })}</div>` : ""}` : ""}
        </li>`;
      }).join("");
      const recent = !events ? `<p class="muted" style="margin:0">Loading…</p>` : events.length ? `<ul class="list events">${events.slice(0, recentAll ? events.length : 4).map((e) => {
            const mm = /^(\S+) (came|went|was already active)/.exec(e.text);
            const id = mm && mm[1], kind = mm ? (mm[2] === "went" ? "went" : mm[2] === "came" ? "came" : "start") : "";
            return `<li><span class="ev-time">${esc(PK.date(e.time, true))}</span><span class="ev-kind ${kind}">${{ came: "Came", went: "Went", start: "Already on" }[kind] || ""}</span>
              <span style="flex:1;min-width:180px">${id ? `<a href="#/alarms/${encodeURIComponent(id)}">${esc(id)}</a> ` : ""}${esc(id ? e.text.replace(/^\S+ (came|went|was already active when watching started):?\s*/, "") : e.text)}</span></li>`;
          }).join("")}</ul>${events.length > 4 ? `<button type="button" class="btn plain small" data-recent style="margin-top:8px">${recentAll ? "Show fewer" : `Show all ${events.length}`}</button>` : ""}`
        : `<p class="muted" style="margin:0">No alarm came or went since PLC-Ki started watching.</p>`;
      // The counts live in the filter buttons: one row to read and to click, no second row of the same numbers
      return hero({
        title: "Alarms",
        sub: "Every alarm the program can raise: what it means, where it is set, the likely causes and the fix.",
      }) + sec("alt", `<div class="toolbar"><input type="search" id="alarm-search" placeholder="Search by number, text or device" value="${esc(alarmFilter)}" aria-label="Search alarms">
          <div class="segmented" role="group" aria-label="Show">${[["all", "All", m.alarms.length, "Every alarm in the program"], ["active", "Active", s.activeAlarms.length, "On right now"], ["faults", "Faults", faults, "Faults stop the machine"], ["warnings", "Warnings", m.alarms.length - faults, "Warnings do not stop the machine"]]
            .map(([k, l, n, t]) => `<button type="button" class="${alarmKind === k ? "on" : ""} ${k === "active" && n ? "hot" : ""}" data-akind="${k}" aria-pressed="${alarmKind === k}" title="${t}">${l} <b>${n}</b></button>`).join("")}</div></div>
          ${PK.deviceDiagnostics ? `<div class="diag-zone">${PK.deviceDiagnostics()}</div>` : ""}
          <details class="more recent" ${recentOpen ? "open" : ""}><summary><b>Recent alarms</b> <span class="muted small">${events ? `${events.length} since PLC-Ki started watching` : ""}</span></summary>
            <div style="margin-top:10px">${recent}</div></details>
          ${rows ? `<ul class="list">${rows}</ul>` : `<div class="nomatch">${alarmKind === "active" && !q ? `${icon("check")}<b>No alarm is active.</b><span>The machine reports no fault or warning right now.</span>`
            : `${icon("search")}<b>No alarm matches.</b><span>Search by the alarm number (like ${esc((m.alarms[0] || {}).id || "A101")}), a word from the text, or a device tag.</span>`}
            ${q || alarmKind !== "all" ? `<button class="btn b small" data-areset>Show all alarms</button>` : ""}</div>`}`);
    },
    after(page) {
      if (PK.wireDeviceDiagnostics) PK.wireDeviceDiagnostics(page);
      const inp = page.querySelector("#alarm-search");
      if (!inp) return;
      inp.oninput = () => { alarmFilter = inp.value; PK.draw(); };
      page.querySelectorAll("[data-akind]").forEach((b) => b.onclick = () => { alarmKind = b.dataset.akind; PK.draw(); });
      const rc = page.querySelector("details.recent");
      if (rc) rc.ontoggle = () => { recentOpen = rc.open; };
      const ra = page.querySelector("[data-recent]");
      if (ra) ra.onclick = () => { recentAll = !recentAll; PK.draw(); };
      const ar = page.querySelector("[data-areset]");
      if (ar) ar.onclick = () => { alarmFilter = ""; alarmKind = "all"; PK.draw(); };
      page.querySelectorAll("[data-open]").forEach((li) => {
        li.onclick = (e) => {
          if (e.target.closest("a,button,.mem-in-row")) return;
          openAlarm = openAlarm === li.dataset.open ? null : li.dataset.open;
          PK.setHash(openAlarm ? "#/alarms/" + encodeURIComponent(openAlarm) : "#/alarms");
          deepLink = openAlarm;
          PK.draw();
        };
        li.onkeydown = (e) => { if ((e.key === "Enter" || e.key === " ") && e.target === li) { e.preventDefault(); li.click(); } };
      });
      bindAsk(page);
      if (window.PKMemory) PKMemory.bind(page);
      if (scrollToAlarm) {
        // Wait until the opened alarm is on the page (the first draws can still be "Loading"), then bring it into view
        const id = "alarm-" + scrollToAlarm;
        scrollToAlarm = null;
        let tries = 0;
        const go = () => { const el = document.getElementById(id); if (el) el.scrollIntoView({ block: "start" }); else if (tries++ < 30) setTimeout(go, 100); };
        setTimeout(go, 50);
      }
    },
  });

  window.PKLive = { parseLoc, bindAsk, stepStrip, seqState };
})();
