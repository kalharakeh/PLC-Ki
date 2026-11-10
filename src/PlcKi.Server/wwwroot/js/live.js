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
   * Where an alarm comes from, for "Show where it is set". A location names where the condition is made, sometimes
   * followed by where the alarm bit is latched: 'FB_Valve, instance "Station".XV101 (output FaultNotOpen), latched in
   * FB_AlarmHandler' or "FB x, transition y; latched in FC z, network 7". The link opens the cause with its output
   * highlighted; the Program page finds the block that writes the alarm bit and names both (alarm-<id>).
   * Returns { block, network, symbol (the output), href }.
   */
  function alarmWhere(a) {
    const text = String(a.location || "");
    const cause = text.split(/;|\blatched in\b/i)[0];
    // Only a latch named ("latched in FC z, network 7"): open that block, the page highlights the alarm bit there
    const part = PK.blockOf(cause) ? cause : text;
    const block = PK.blockOf(part);
    if (!block) return {};
    const loc = parseLoc(part);
    // "(output FaultNotOpen)", or a bare name: "(TimeoutStep10)", "(NoHeating, timer tHeat)"
    const output = loc.output || (part.match(/\(\s*([A-Za-z_]\w*)\s*[,)]/) || [])[1] || null;
    const network = (part.match(/\bnetwork\s+(\d+)/i) || [])[1] || null;
    const href = "#/program/" + [block, loc.instance, network ? "network-" + network : null, output ? "@" + output : null, "alarm-" + a.id]
      .filter(Boolean).map(encodeURIComponent).join("/");
    return { block, network, symbol: output, href };
  }

  /**
   * The rung that sets the alarm, drawn by the Program page (PKProgram.rungFor), with the values just before the alarm
   * came when there is a recording, else live. Drawn in the background every 2 s; render() shows the last drawing.
   * null while there is none (no program, no rungFor on this build, or the network was not found).
   */
  const R = { key: "", html: null, at: 0, busy: false, want: new Map() };
  function rungHtml(a, where, loc) {
    if (!where.block || !window.PKProgram || !PKProgram.rungFor) return null;
    const resolve = window.PKCapture ? PKCapture.atAlarm(a, loc.instance) : null;
    const key = `${a.id}|${resolve ? "rec" : "live"}`;
    if (key !== R.key) Object.assign(R, { key, html: null, at: 0, want: new Map() });
    if (!R.busy && Date.now() - R.at > 2000) {
      R.busy = true; R.at = Date.now();
      const want = new Map();
      PK.loadProgram()
        .then(() => PKProgram.rungFor({ block: where.block, instance: loc.instance, output: where.symbol, network: where.network, want }, resolve || undefined))
        .then((h) => {
          h = String(h || "").replace(/\sid="hl"/, "") || null;     // the page has no #hl of its own to jump to
          if (key !== R.key) return;
          R.want = want;
          if (h !== R.html) { R.html = h; PK.draw(); }
        })
        .catch(() => { /* drawn again in 2 s */ })
        .finally(() => { R.busy = false; });
    }
    return R.html;
  }
  let histOpen = false;           // "History of this alarm" stays open across the redraws

  // ---------- Follow-up alarms (review 42) ----------
  /** How soon after a first alarm another one has to come to be folded under it. */
  const FOLD_MS = 5000;
  /**
   * The active alarms in groups: each first alarm with the ones that came up to 5 s after it, in the server's order
   * (first-out first). Only when PLC-Ki saw the first one come: alarms already on when it started watching have no
   * known order, so they stand alone. Timing only, nothing in the model links two alarms, so the page says "probably".
   * Returns { groups: [{ root, after: [{ alarm, ms }] }], rootOf: Map(id -> { root, ms }) }.
   */
  function foldAlarms(alarms) {
    const groups = [], rootOf = new Map();
    for (const a of alarms) {
      const t = a.since && !a.atStart ? Date.parse(a.since) : NaN;
      const g = !isNaN(t) && groups.find((x) => !isNaN(x.t) && x.t <= t && t - x.t <= FOLD_MS);
      if (g) { g.after.push({ alarm: a, ms: t - g.t }); rootOf.set(a.id, { root: g.root, ms: t - g.t }); }
      else groups.push({ root: a, t, after: [] });
    }
    return { groups, rootOf };
  }
  const laterText = (ms) => (ms < 1000 ? "in the same second" : `${Math.round(ms / 1000)} s later`);
  /** "Came 1 s after A01. Probably a result of it: fix A01 first." */
  const followText = (id, ms, text) => `Came ${ms < 1000 ? "in the same second as" : Math.round(ms / 1000) + " s after"} ${id}${text ? ` (${text})` : ""}. Probably a result of it: fix ${id} first.`;

  // ---------- Share an alarm (review 48) ----------
  let share = null;               // what the Share button on the page sends: { title, text, url }
  /** Plain text for a colleague: the alarm, when it came, the step, the device's values, what came after it, and the link. */
  function shareText(a, s, seq, eqs, failed, after, follow) {
    const url = `${location.origin}${location.pathname}#/alarm/${encodeURIComponent(a.id)}`;
    const local = /^(localhost|127\.0\.0\.1|\[::1\])$|\.localhost$/i.test(location.hostname);
    const lines = [
      `PLC-Ki · ${s.machine}`,
      `${a.severity === "WARNING" ? "Warning" : "Fault"} ${a.id}: ${a.text}`,
      a.since ? (a.atStart ? `Already on when PLC-Ki started watching (${PK.when(a.since)})` : `Came at ${PK.when(a.since)}`) : null,
      follow ? followText(follow.root.id, follow.ms, follow.root.text) : null,
      seq ? `Step ${seq.step ?? "?"}${seq.stepName ? " " + seq.stepName : ""} (${seq.name})${seq.halted ? ", halted" : ""}` : null,
      ...eqs.slice(0, 2).flatMap((e) => [`${e.name}: ${e.state}`, ...e.values.slice(0, 4).map((v) => `  ${v.label}: ${v.text}`)]),
      ...failed.map((c) => `Failed check: ${c.text}${c.values && c.values.length ? ` (now ${c.values.map((v) => v.label + " " + v.text).join(", ")})` : ""}`),
      after.length ? `After it: ${after.map((f) => `${f.alarm.id} ${f.alarm.text} (${laterText(f.ms)})`).join("; ")}` : null,
    ].filter((l) => l != null);
    const link = local ? `${url} (this address only works on the PC it was copied on)` : url;
    return { title: `${a.id}: ${a.text}`, text: lines.join("\n"), url, full: lines.join("\n") + "\n\n" + link };
  }
  /** Send on a phone (the phone's own share sheet), else copy; when the browser allows neither, show the text to copy by hand. */
  async function shareAlarm() {
    if (!share) return;
    if (navigator.share && matchMedia("(pointer: coarse)").matches) {
      try { await navigator.share({ title: share.title, text: share.text, url: share.url }); return; }
      catch (e) { if (e && e.name === "AbortError") return; /* else copy instead */ }
    }
    if (await PK.copyText(share.full)) { PK.toast("Copied: the alarm, the step, the values and the link. Paste it in a message."); return; }
    const sh = PK.sheet(`<h2>Copy this for a colleague</h2><p class="muted">This browser does not let PLC-Ki copy. Select the text and copy it.</p>
      <textarea class="share-text" readonly rows="10">${esc(share.full)}</textarea><div class="row end"><button class="btn" data-close>Done</button></div>`);
    const ta = sh.el.querySelector("textarea");
    ta.focus(); ta.select();
  }
  let linkAlarm = null;           // the alarm named in the address (#/alarm/A01), picked once when the page opens on it
  /** A shared link to an alarm that has gone since: say so instead of silently showing another one. */
  const goneNotice = (id) => `<div class="notice gold aa-gone">${icon("info")}<div>Alarm <b>${esc(id)}</b> from the link is not on any more. <a href="#/alarms/${encodeURIComponent(id)}">What it means and when it was on ›</a></div></div>`;

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
    const look = PK.devLook(e);
    const lvl = look.alarms.length ? look.lvl : e.level;      // an alarm on the device colours it; else its own state
    const chain = PK.devStrip(e);
    // An alarm on this device: name it and open it
    const alarms = look.alarms.map((a) => `<a class="eq-alarm ${a.severity === "WARNING" ? "w" : ""}" href="#/alarms/${encodeURIComponent(a.id)}">${icon("bell")}<span><b>${a.severity === "WARNING" ? "Warning" : "Alarm"} ${esc(a.id)}:</b> ${esc(a.text)}</span><span class="chev">›</span></a>`).join("");
    const vals = e.values.length ? `<div class="kv">${e.values.map((v) => `<span>${esc(v.label)}</span><span class="val">${esc(v.text)}</span>`).join("")}</div>` : "";
    return `<div class="eq-top"><span class="ico">${icon(PK.kindIcon(e.kind))}</span>
        <div class="grow"><div class="eq-name">${esc(e.name)}</div><div class="eq-id">${esc(e.id)}${e.kind ? " · " + esc(e.kind) : ""}</div></div>
        <span class="lv lv-${esc(lvl)}">${esc(look.alarms.length ? (lvl === "fault" ? "fault" : "warning") : e.state)}</span></div>
      ${alarms}
      ${chain}
      ${e.blockedBy ? `<div class="eq-why">${icon("warn")}<span>${esc(e.blockedBy.charAt(0).toUpperCase() + e.blockedBy.slice(1))}</span></div>` : ""}
      ${vals}
      ${full ? measureHtml(deviceSignals(e)) : ""}
      ${full && e.permits.length ? `<div><h3 class="mt-2">Interlocks</h3><ul class="conds">${e.permits.map(cond).join("")}</ul></div>` : ""}
      ${full && e.explanation && !e.blockedBy ? `<p class="m-0 muted small">${esc(e.explanation)}</p>` : ""}
      ${full ? `<div class="row">${logicLink(e)}${modelFix("equipment:" + e.id)}</div>` : `<span class="eq-more" aria-hidden="true">Details ›</span>`}`;
  }
  // ---------- Where to measure (review 41) ----------
  // Each signal's input or output, rack, slot and channel, from the tag table and the hardware read from TIA (the PLC
  // page's Racks and I/O). Looked up once per signal in the background (PKProgram.whereToMeasure); the page redraws.
  const MS = { res: new Map(), busy: new Set() };
  for (const ev of ["pk:program-changed", "pk:hardware-changed"]) window.addEventListener(ev, () => MS.res.clear());
  function measureOf(symbol) {
    if (MS.res.has(symbol)) return MS.res.get(symbol);
    if (window.PKProgram && PKProgram.whereToMeasure && !MS.busy.has(symbol)) {
      MS.busy.add(symbol);
      PKProgram.whereToMeasure(symbol).then((r) => { MS.res.set(symbol, r || null); MS.busy.delete(symbol); PK.draw(); });
    }
    return undefined;
  }
  /** A device's own signals: its command (the output) and its values (open switch, speed, drive ready). */
  function deviceSignals(e) {
    const def = state.model && (state.model.equipment || []).find((d) => d.id === e.id);
    return def ? [def.command, ...(def.values || [])].filter(Boolean) : [];
  }
  /** "XV101 open switch · %I0.2 · PLC_1 slot 2, DI 32x24VDC HF, channel 2 ›" for each signal that reaches an input or output. */
  function measureHtml(ids, { card = false } = {}) {
    const tags = (state.model && state.model.tags) || [];
    const rows = [];
    let noModule = false;
    for (const id of [...new Set(ids)]) {
      const t = tags.find((x) => x.id === id);
      const r = t ? measureOf(t.symbol) : null;
      if (!r) continue;
      if (!r.ch) noModule = true;
      const c = r.ch;
      rows.push(`<li><span class="ms-l">${esc(t.label || t.id)}<span class="faint small">${esc(r.tag)}${r.comment ? " · " + esc(r.comment) : ""}</span></span>
        <span class="mono ms-a">${esc(r.address)}</span>
        ${c ? `<a class="ms-w" href="${esc(c.href)}">${esc(c.station)}${c.slot != null ? ", slot " + esc(c.slot) : ""}${c.module ? " · " + esc(c.module) : ""}${c.channel != null ? " · channel " + esc(c.channel) : ""} ›</a>` : `<span class="ms-w faint small">Module not known</span>`}</li>`);
    }
    if (!rows.length) return "";
    const inner = `<h3 class="${card ? "m-0" : "mt-2"}">Where to measure</h3><ul class="ms-list">${rows.join("")}</ul>
      ${noModule ? `<p class="m-0 faint small">The module and channel show once the hardware is read from TIA (PLC page, Racks and I/O).</p>` : ""}`;
    return card ? `<div class="card aa-ms">${inner}</div>` : `<div class="ms">${inner}</div>`;
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
  const chartLink = (seq) => `<div><a class="btn b small" href="#/plant/sequence/${encodeURIComponent(seq.id)}">Step chart and timeline ›</a></div>`;
  /** Settings (HMI values) the model says keep the machine from working, e.g. a 0 % tolerance. */
  function settingsNotice(s) {
    const bad = (s.settings || []).filter((x) => x.valid === false);
    if (!bad.length) return "";
    return `<div class="mt-4 notice gold">${icon("warn")}<div><b>${bad.length === 1 ? "A machine setting stops it from working" : bad.length + " machine settings stop it from working"}</b>
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
    if (PK.cpuStop()) return ["Not running", "bad"];
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
  /**
   * The CPU is not in RUN: that comes first, before any alarm, because nothing else explains a machine that does not
   * move. The CPU's own messages, what to check, then the alarms that are on (their last state) and the last step.
   * Nothing here can start the CPU: PLC-Ki never changes its mode.
   */
  function stopPage(s, c, seq) {
    const word = c.word || "STOP";
    const msgs = PK.cpuMessages ? PK.cpuMessages() : null;
    const alarms = s.activeAlarms;
    const sw = c.switch ? String(c.switch).toUpperCase() : "";
    const todo = [
      sw ? `The mode switch on the CPU is at ${sw}.` + (/STOP/.test(sw) ? " Someone set it there: find out why before it goes back to RUN." : "")
        : "Look at the mode switch on the CPU: RUN or STOP.",
      "Open the CPU's diagnostic buffer in TIA Portal (Online & diagnostics). The newest entry says why it stopped.",
      "Ask who stopped it: a download or a STOP from TIA or the CPU display, or a fault in the program (cycle time, a missing block, an access error).",
      "Start it again only when you know why it stopped and the machine is safe to run.",
    ];
    const reported = msgs === null ? ""
      : msgs.length ? `<div class="card"><h3>What the CPU reports</h3><ul class="list">${msgs.map((m) => `<li style="flex-wrap:wrap"><span style="flex:1;min-width:200px"><b class="semibold">${esc(m.text)}</b>${m.info ? `<div class="faint small">${esc(m.info)}</div>` : ""}</span>${m.since ? `<span class="faint small">${esc(PK.when(m.since))}</span>` : ""}</li>`).join("")}</ul></div>`
      : `<div class="card"><h3>What the CPU reports</h3><p class="m-0 muted">No message from the CPU right now. Its diagnostic buffer in TIA says why it stopped.</p></div>`;
    return hero({
      cls: "hero cpu-stop-page",
      eyebrow: `<span class="pill bad"><span class="dot"></span>PLC in ${esc(word)}${alarms.length ? ` · ${alarms.length} alarm${alarms.length > 1 ? "s" : ""} on` : ""}</span>`,
      title: `The PLC is in ${esc(word)}: the program is not running.`,
      sub: esc(`In ${word} ${PK.cpuSince(c)}. What you see on these pages is its last state, not a running machine.`),
      actions: `<a class="btn" href="#/plc">Open the PLC page</a><button class="btn b" data-ask="The PLC is in ${esc(word)}. What could have stopped it, and what do I check?">Ask</button>`,
      extra: `<div class="grid two" style="margin-top:26px;align-items:start">
          <div class="card"><h3>What to check</h3><ol class="fix">${todo.map((t) => `<li style="cursor:default">${esc(t)}</li>`).join("")}</ol></div>
          ${reported}</div>`,
    })
    + (alarms.length ? sec("alt", `<h2>Alarms that are on</h2><p class="sub">As they were when the CPU stopped. Fix the STOP first; some may go when the program runs again.</p>
        <ul class="list">${alarms.map((a) => `<li><span class="tag ${a.severity === "WARNING" ? "gold" : "red"}">${esc(a.id)}</span>
          <span class="grow min0"><a href="#/alarms/${encodeURIComponent(a.id)}">${esc(a.text)}</a></span></li>`).join("")}</ul>`) : "")
    + (seq ? sec(alarms.length ? "" : "alt", `<h2>${esc(seq.name)}</h2><p class="sub">${esc(seq.explanation)}</p>${stepStrip(seq)}`) : "");
  }

  PK.view("alarm", {
    live: true,
    mount() {
      // Block members on the rung that the model does not read ("Station".XV101.Out): read them while this page is open
      if (window.PKTags) PKTags.watch("alarm", () => [...R.want].map(([symbol, type]) => ({ symbol, type })));
      PK.loadProgram().then(PK.draw); PK.draw();
    },
    render() {
      const s = state.status;
      if (!s) return waitingPage();
      const alarms = s.activeAlarms;      // first-out first: the server orders them by when they came
      const seq = s.sequences.find((q) => q.halted) || s.sequences[0];
      if (offline()) return offlinePage() + guide();
      const stop = PK.cpuStop(s);
      if (stop) return stopPage(s, stop, seq) + guide();
      // #/alarm/A01 (a shared link) opens on that alarm; when it has gone, the page says so
      const want = PK.current && PK.current.params[0];
      if (want && want !== linkAlarm) { linkAlarm = want; selectedAlarm = want; }
      const gone = want && !alarms.some((x) => x.id === want) ? goneNotice(want) : "";
      share = null;

      if (!alarms.length) {
        const blocked = blockedOf(s);
        const n = blocked.length;
        // Root first: one chain per group of devices that wait on each other (Level → M201 → E301 → step 30)
        const chains = PK.causeChains(s);
        const root = (chains.length && chains[0].devices.find((e) => e.blockedBy)) || blocked[0];
        const order = chains.flatMap((c) => c.devices).filter((e, i, l) => e.blockedBy && l.indexOf(e) === i);
        return hero({
          before: gone,
          eyebrow: n ? `<span class="pill warn"><span class="dot"></span>No alarm · ${n} device${n > 1 ? "s" : ""} blocked</span>` : `<span class="pill ok"><span class="dot"></span>No alarm</span>`,
          title: n ? `No alarm, but ${n === 1 ? "1 device is" : n + " devices are"} blocked.` : "No alarm is active.",
          sub: esc(n ? blockedLine(root) : (seq ? seq.explanation : s.summary[0] || "")),
          actions: n ? `<button class="btn" data-ask="Why is ${esc(root.name)} blocked, and what do I check?">Ask why</button><a class="btn b" href="#/plant">See the plant</a>`
            : `<a class="btn" href="#/plant">See the plant</a><button class="btn b" data-ask="What is the machine doing right now?">Ask</button>`,
          extra: n ? `<div class="chains">${chains.map(PK.chainHtml).join("")}</div>` : "",
        }) + (settingsNotice(s) ? sec("", settingsNotice(s)) : "")
        + (window.PKMemory ? PKMemory.unnotedBlock() : "")
        + (n ? sec("alt", `<h2>Blocked now</h2><p class="sub">The program asks for ${n === 1 ? "this device" : "these devices"}, but ${n === 1 ? "its" : "their"} output stays off. ${n > 1 ? "The cause comes first." : ""}</p>
            <div class="stack blocked-now">${order.map((e) => `<div class="aa-dev"><div class="row"><h3 class="m-0">${esc(e.name)}</h3><span class="spacer"></span><a class="small" href="#/plant/devices/${encodeURIComponent(e.id)}">Details ›</a></div>
              ${PK.devStrip(e)}</div>`).join("")}</div>`) : "")
        + (seq ? sec(n ? "" : "alt", `<h2>${esc(seq.name)}</h2>${n ? `<p class="sub">${esc(seq.explanation)}</p>` : ""}${stepStrip(seq)}${chartLink(seq)}
            ${seq.next.length ? `<p class="mt-4 mb-2 muted small">To go on it needs</p><ul class="conds">${seq.next.flatMap((x) => x.conditions).map((c) => PK.condHeld(c, s)).join("")}</ul>` : ""}`) : "")
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
      // Follow-ups fold under the alarm they came right after: one chip per first alarm, with "+1 after it".
      // The chip is red when any alarm in it is a fault, so a fault after a warning is not hidden behind amber.
      const { groups, rootOf } = foldAlarms(alarms);
      const follow = rootOf.get(a.id) || null;
      const after = (groups.find((g) => g.root.id === a.id) || { after: [] }).after;

      const chips = alarms.length > 1 ? `<div class="alarm-chips">${groups.map((g, i) => { const x = g.root; return `
        <button class="alarm-chip ${[x, ...g.after.map((f) => f.alarm)].every((y) => y.severity === "WARNING") ? "warning" : ""} ${x.id === a.id ? "sel" : ""}" data-alarm="${esc(x.id)}"${g.after.length ? ` title="${esc(g.after.map((f) => f.alarm.id + " came " + laterText(f.ms)).join(", "))}"` : ""}>
          <span class="d"></span>${i === 0 && firstKnown && groups.length > 1 ? "First · " : ""}${esc(x.id)} ${esc(x.text.length > 40 ? x.text.slice(0, 38) + "…" : x.text)}${g.after.length ? `<span class="chip-n">+${g.after.length} after it</span>` : ""}</button>`; }).join("")}</div>
          ${atStart > 1 ? `<p class="faint small" style="margin:-12px 0 18px">${atStart} alarms were already on when PLC-Ki started watching, so which of those came first is not known.</p>` : ""}` : "";

      // Four short facts under the alarm: the device's state, the step, how long in it, when the alarm came
      const facts = [
        ...eqs.slice(0, 1).map((e) => [e.state, e.name, PK.devLook(e).lvl === "fault" ? "bad" : e.level === "ok" ? "ok" : ""]),
        seq ? [`Step ${seq.step ?? "?"}${seq.halted ? " halted" : ""}`, `${seq.name}: ${seq.stepName}`, seq.halted ? "bad" : ""] : null,
        seq && seq.timeInStep ? [seq.timeInStep, "in this step"] : null,
        a.since ? [PK.when(a.since), a.atStart ? "already on when PLC-Ki started watching" : "came at"] : null,
      ].filter(Boolean).slice(0, 4);
      const V = window.PKVis;
      const rung = rungHtml(a, where, loc);
      const rec = !!(window.PKCapture && PKCapture.atAlarm(a, loc.instance));
      const fixList = a.fix.length ? `<ol class="fix">${a.fix.map((f, i) => `<li class="${done.has(i) ? "done" : ""}"><button type="button" class="fix-b" data-fix="${i}" aria-pressed="${done.has(i)}">${esc(f)}</button></li>`).join("")}</ol>`
        : `<p class="muted">No fix written for this alarm yet. Ask about it.</p>`;

      // Left: the alarm, then straight to the fix. Right: where it is, in the machine and in the program.
      // On a phone the right column follows the left, so the fix is on the first screen.
      const main = `<div class="eyebrow"><span class="pill ${warn ? "warn" : "bad"}"><span class="dot"></span>${esc(a.severity)} · ${esc(a.id)}</span></div>
          <h1>${esc(a.text)}</h1>
          ${follow ? `<div class="notice aa-follow">${icon("info")}<div>${esc(followText(follow.root.id, follow.ms))} <button type="button" class="btn plain small" data-alarm="${esc(follow.root.id)}">Open ${esc(follow.root.id)} ›</button></div></div>` : ""}
          ${a.condition ? `<p class="sub">${esc(a.condition)}</p>` : ""}
          ${failed.length ? `<p class="sub"><b>Failed check:</b> ${failed.map((c) => esc(c.text) + (c.values && c.values.length ? ` (now ${c.values.map((v) => esc(v.label) + " " + esc(v.text)).join(", ")})` : "")).join("; ")}</p>` : ""}
          ${settingsNotice(s)}
          ${PK.ui.tiles(facts.map(([value, label, state]) => ({ value, label, state })), { small: true })}
          <div class="actions"><button class="btn" data-ask="Why is alarm ${esc(a.id)} on, and how do I fix it?">Ask about this alarm</button>
            ${where.block ? `<a class="btn b" href="${esc(where.href)}">Show where it is set</a>` : ""}
            <button type="button" class="btn b" data-share title="The alarm, the step, the values and a link, as plain text for a colleague">${navigator.share && matchMedia("(pointer: coarse)").matches ? "Send" : "Copy link"}</button></div>
          <div class="aa-fix">
            <div class="card"><h3>Fix it</h3>${fixList}<p class="mt-3 mb-0 faint small">Tap a step when it is done. The ticks are only on this screen.</p></div>
            <div class="card"><h3>Likely causes</h3>${a.causes.length ? `<ol class="causes">${a.causes.map((c, i) => `<li><b>${i + 1}</b><span>${esc(c)}</span></li>`).join("")}</ol>` : `<p class="muted">None written yet.</p>`}</div>
          </div>
          ${after.length ? `<div class="card aa-after"><h3>Came after it</h3><ul class="list">${after.map((f) => `<li>
              <span class="tag ${f.alarm.severity === "WARNING" ? "gold" : "red"}">${esc(f.alarm.id)}</span><span class="grow min0">${esc(f.alarm.text)}<span class="faint small">Came ${esc(laterText(f.ms))}</span></span>
              <button type="button" class="btn gray small" data-alarm="${esc(f.alarm.id)}">Open</button></li>`).join("")}</ul>
            <p class="m-0 faint small">Probably a result of this alarm: fix it first, then see whether ${after.length === 1 ? "that one clears" : "they clear"} too.</p></div>` : ""}
          ${window.PKMemory ? PKMemory.lastLine(a.id) : ""}
          ${eqs.map((e) => `<div class="aa-dev"><div class="row"><h3 class="m-0">${esc(e.name)}</h3><span class="spacer"></span><a class="small" href="#/plant/devices/${encodeURIComponent(e.id)}">Details ›</a></div>${PK.devStrip(e)}</div>`).join("")}`;

      const vals = eqs.flatMap((e) => e.values);
      const side = `${eqs.length && V ? `<div class="card aa-pic"><div class="row"><h3 class="m-0">Where in the machine</h3><span class="spacer"></span><a class="small" href="#/plant">Plant ›</a></div>
            ${V.plantMap(V.around(eqs.map((e) => e.id)), null, eqs.map((e) => e.id))}
            ${vals.length ? `<div class="kv">${vals.map((v) => `<span>${esc(v.label)}</span><span class="val">${esc(v.text)}</span>`).join("")}</div>` : ""}</div>` : ""}
          ${measureHtml([...failed.flatMap((c) => (c.values || []).map((v) => v.id)), ...eqs.flatMap(deviceSignals)], { card: true })}
          <div class="card aa-sw"><h3>Where it is set</h3>
            ${where.block ? `<div class="path">${(path || [{ block: where.block }]).map((p) => `<span>${esc(p.block)}${p.instance ? " " + esc(p.instance) : ""}</span>`).join("<i>›</i>")}${where.network ? `<i>›</i><span>Network ${esc(where.network)}</span>` : ""}${where.symbol ? `<i>›</i><span>${esc(where.symbol)}</span>` : ""}</div>` : `<p class="m-0 muted small">${esc(a.location || "No place in the program is known for this alarm.")}</p>`}
            ${rung ? `<div class="aa-rung">${rung}</div><p class="m-0 faint small">${rec ? "Values just before the alarm came, from the recording. ? means not recorded." : "Values now. They can look normal again after a halt."}</p>` : ""}
            <div class="gap-2 row">${where.block ? `<a class="btn b small" href="${esc(where.href)}">Open in the program</a>` : links(loc)}${modelFix("alarm:" + a.id)}</div></div>`;

      share = shareText(a, s, seq, eqs, failed, after, follow);
      return sec(`hero aa ${warn ? "aa-warn" : ""}`, `${gone}${chips}<div class="aa-grid"><div class="aa-main">${main}</div><aside class="aa-side">${side}</aside></div>`)
        + (window.PKCapture ? PKCapture.alarmSection(a) : "")
        + (checks.length ? sec("", `<h2>What the program checks</h2><p class="sub">The comparisons behind this alarm, ${a.checksAtAlarm ? "with the values from the last read before it came" : "with the values now"}. ✗ is the one that failed.</p>
            <div class="card"><ul class="conds">${checks.map(cond).join("")}</ul></div>`) : "")
        + (typeof PKDocs !== "undefined" ? PKDocs.alarmSection(a, eqs) : "")
        + sec("alt", `<h2>What is going on</h2>
          <div class="grid two"><div>${s.summary.map((l) => `<p class="mb-3 sub">${esc(l)}</p>`).join("")}</div>
          ${seq ? `<div class="card"><h3>${esc(seq.name)}</h3>${stepStrip(seq)}${chartLink(seq)}
            ${seq.next.length ? `<p class="mt-3 mb-2 muted small">To go on it needs</p><ul class="conds">${seq.next.flatMap((n) => n.conditions).map((c) => PK.condHeld(c, s)).join("")}</ul>` : ""}</div>` : ""}</div>`)
        + (window.PKMemory ? sec("", `<details class="more" data-hist ${histOpen ? "open" : ""}><summary><b>History of this alarm</b> <span class="muted small">every time PLC-Ki saw it, and what was found</span></summary>
            <div class="mt-3 card">${PKMemory.alarmBlock(a.id)}</div></details>`) : "")
        + guide();
    },
    leave() { linkAlarm = null; },
    after(page) {
      page.querySelectorAll("[data-alarm]").forEach((b) => b.onclick = () => { selectedAlarm = b.dataset.alarm; PK.draw(); });
      page.querySelectorAll("[data-share]").forEach((b) => b.onclick = shareAlarm);
      page.querySelectorAll("[data-fix]").forEach((b) => b.onclick = () => {
        const set = (fixDone[selectedAlarm] ||= new Set());
        const i = Number(b.dataset.fix);
        set.has(i) ? set.delete(i) : set.add(i);
        PK.draw();
      });
      if (window.PKCapture) PKCapture.bind(page);
      const h = page.querySelector("details[data-hist]");
      if (h) h.ontoggle = () => { histOpen = h.open; };
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
  const PLANT_TABS = [["overview", "Overview"], ["devices", "Devices"], ["sequence", "Sequence"], ["stations", "Stations"], ["values", "Values over time"], ["settings", "Machine settings"], ["exchange", "Data exchange"]];
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
    const modelVal = (f) => { const m = f.modelTag && (s.settings || []).find((x) => x.id === f.modelTag); return m ? `<span class="m-0 chip">${esc(m.value)}</span>` : null; };
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

  // A device needs attention when its own state is bad or blocked, or when an active alarm is about it (PK.devLook)
  const problemOf = (e) => { const l = PK.devLook(e).lvl; return l !== "ok" && l !== "idle"; };
  const rank = (e) => ({ fault: 0, blocked: 1, warn: 2 })[PK.devLook(e).lvl] ?? 3;
  /** The pill for a device in a list: blocked shows amber, an alarm shows its id. */
  const devPill = (e) => { const l = PK.devLook(e); return `<span class="lv lv-${esc(l.lvl === "blocked" ? "warn" : l.lvl)}">${esc(l.word)}</span>`; };
  /** The stations of the machine, when stations.js has loaded. */
  const stationsNow = (s) => (typeof PKStations !== "undefined" ? PKStations.list(s) : { stations: [], machineAlarms: [] });

  PK.view("plant", {
    live: true,
    // The Sequence tab shows settings such as {LevelA} with their value, so it needs all tag values
    get needsValues() { return !!(PK.current && PK.current.params && PK.current.params[0] === "sequence"); },
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
      const stationList = stationsNow(s);
      const ask0 = attention[0] && PK.devLook(attention[0]).alarms[0];
      // "Temperature reached 60 °C (can't come while Heater E301 is blocked)": a wait that cannot come by itself says so
      const heldText = (c) => { const d = c.heldBy && s.equipment.find((e) => e.id === c.heldBy); return d ? `${c.text} (can't come while ${d.name} is ${d.blockedBy ? "blocked" : "in fault"})` : c.text; };
      const waitsFor = main ? main.next.flatMap((n) => n.conditions).filter((c) => c.met === false).map(heldText) : [];
      const badSettings = (s.settings || []).filter((x) => x.valid === false);
      const counts = { devices: attention.length, exchange: s.exchange.length, settings: badSettings.length };
      // CPU not in RUN: the step is only where the program was when it stopped, and nothing waits for anything
      const stop = PK.cpuStop(s);
      const stopWord = stop ? stop.word || "STOP" : "";

      const head = hero({
        eyebrow: main ? `<span class="pill ${stCls}"><span class="dot"></span>${stText}</span>${main.timeInStep && !stop ? `<span class="faint">for ${esc(main.timeInStep)}</span>` : ""}<span class="faint">· ${esc(main.name)}</span>` : "",
        title: main ? `${stop ? "Last step" : "Step"} ${main.step ?? "?"}: ${esc(main.stepName)}` : esc(s.machine),
        sub: stop ? esc(`The CPU is in ${stopWord} ${PK.cpuSince(stop)}: the program is not running. The values are its last state.`)
          : main ? (main.halted ? esc("Halted: " + (main.haltReasons.join("; ") || "the outputs are off.")) : waitsFor.length ? esc("Waiting for: " + waitsFor.join("; ")) : esc(main.does || "")) : esc(s.summary[0] || ""),
        actions: stop ? `<a class="btn" href="#/alarm">What to check</a><a class="btn b" href="#/plc">PLC page</a>` : `<button class="btn" data-ask="${ask0 ? `Why is alarm ${esc(ask0.id)} on for ${esc(attention[0].name)}, and what do I check?` : attention.length ? `Why is ${esc(attention[0].name)} ${esc(attention[0].blockedBy ? "blocked" : attention[0].state)}, and what do I check?` : "What is the machine doing right now?"}">Ask about it</button>${s.activeAlarms.length ? `<a class="btn b" href="#/alarm">${s.activeAlarms.length} active alarm${s.activeAlarms.length > 1 ? "s" : ""}</a>` : ""}`,
        // Measured values as bars with the interlock limit marked; switches and counters stay as numbers
        extra: (s.overview.length ? (V ? V.valueBars(s.overview.slice(0, 8)) : PK.ui.tiles(s.overview.slice(0, 8).map((v) => ({ value: v.text, label: v.label })), { cls: "plant-kpis" })) : "")
          + PK.ui.tabs("Plant", PLANT_TABS.filter(([k]) => (k !== "exchange" || s.exchange.length) && (k !== "settings" || (s.settings || []).length || (H.data && H.data.settings.length)) && (k !== "stations" || stationList.stations.length > 1) && (k !== "sequence" || (state.model && state.model.sequences.length))).map(([k, l]) =>
            ({ label: l, on: k === tab, href: "#/plant" + (k === "overview" ? "" : "/" + k), count: k === "devices" || k === "settings" ? counts[k] : 0 }))),
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
        // A device whose problem is an alarm already listed above is not listed again; a blocked one still is
        const devRows = attention.filter((e) => e.blockedBy || !PK.devLook(e).alarms.length);
        const stopRow = stop ? `<li><a class="al" href="#/alarm"><span class="ico">${icon("warn")}</span>
              <span class="nd-t"><b>PLC in ${esc(stopWord)}</b><span>The program is not running. What to check is on Active alarm.</span></span>
              <span class="lv lv-fault">${esc(stopWord.toLowerCase())}</span><span class="chev">›</span></a></li>` : "";
        const nNeeds = devRows.length + s.activeAlarms.length + badSettings.length + (stop ? 1 : 0);
        const settingRows = badSettings.map((x) => `<li><a href="#/plant/settings">
              <span class="ico">${icon("gear")}</span>
              <span class="nd-t"><b>Setting ${esc(x.label)} is ${esc(x.value)}</b><span>${esc(x.problem || "the machine cannot work with this value")}</span></span>
              <span class="lv lv-warn">setting</span><span class="chev">›</span></a></li>`).join("");
        const needs = nNeeds
          ? `<ul class="needs">${stopRow}${alarmRows}${settingRows}${devRows.map((e) => `<li><a href="#/plant/devices/${encodeURIComponent(e.id)}">
              <span class="ico">${icon(PK.kindIcon(e.kind))}</span>
              <span class="nd-t"><b>${esc(e.name)}</b><span>${esc(e.blockedBy ? (/^needs /.test(e.blockedBy) ? "Blocked: it " + e.blockedBy : "Blocked: " + e.blockedBy) : e.explanation || e.state)}</span></span>
              <span class="lv lv-${esc(e.blockedBy ? "warn" : e.level)}">${esc(e.blockedBy ? "blocked" : e.state)}</span><span class="chev">›</span></a></li>`).join("")}</ul>`
          : `<p class="ok-line">${icon("check")}<span>No alarm, and every device is doing what the program asks.</span></p>`;
        const seqCards = s.sequences.map((q) => `<div class="card stack">
            <div class="row"><h3 class="m-0">${esc(q.name)}</h3><span class="pill ${seqState(q)[1]}">${seqState(q)[0]}</span><span class="spacer"></span>
              <a class="small" href="#/plant/sequence/${encodeURIComponent(q.id)}">Step chart and timeline ›</a></div>
            ${stepStrip(q)}
            ${stop ? `<div class="notice red">${icon("warn")}<div>${esc(q.explanation)}</div></div>`
              : q.does ? `<p class="m-0"><span class="muted">This step should:</span> ${esc(q.does)}</p>` : ""}
            ${q.halted && q.haltReasons.length && !stop ? `<div class="notice red">${icon("warn")}<div><b>Halted, outputs are off:</b> ${q.haltReasons.map(esc).join("; ")}</div></div>` : ""}
            ${q.next.map((n) => `<div><p class="mt-0 mb-2 muted small">To go to step ${n.to} "${esc(n.toName)}" it needs</p><ul class="conds">${n.conditions.map((c) => PK.condHeld(c, s)).join("")}</ul></div>`).join("")}
            ${q.limit ? `<p class="m-0 faint">${esc(q.limit)}</p>` : ""}
          </div>`).join("");
        body = sec("", `<div class="plant-cols">
            <div class="stack">
              <div class="card stack"><div class="row"><h3 class="m-0">Needs attention</h3>${nNeeds ? `<span class="count ${stop || s.activeAlarms.some((x) => x.severity !== "WARNING") ? "" : "warn"}">${nNeeds}</span>` : ""}<span class="spacer"></span>
                <a class="small" href="#/plant/devices">All devices ›</a></div>${needs}</div>
              ${seqCards}
            </div>
            <div class="stack">
              ${V && s.equipment.length ? `<div class="card stack"><h3 class="m-0">The machine at a glance</h3>${V.plantMap(s.equipment, null)}</div>` : ""}
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
            ${PK.ui.choice("Show", [{ label: `All ${s.equipment.length}`, on: !eqOnlyProblems, data: { "data-eqf": "all" } }, { label: `Needs attention ${attention.length}`, on: eqOnlyProblems, data: { "data-eqf": "problems" } }])}</div>`;
        const list = shown.length ? `<ul class="devlist">${shown.map((e) => `<li><button type="button" class="${sel && e.id === sel.id ? "on" : ""}" data-pick="${esc(e.id)}" aria-pressed="${!!sel && e.id === sel.id}">
            <span class="ico">${icon(PK.kindIcon(e.kind))}</span><span class="dv-t"><b>${esc(e.name)}</b><span>${esc(e.id)}</span></span>
            ${devPill(e)}</button></li>`).join("")}</ul>`
          : PK.ui.empty({ icon: eqOnlyProblems && !q ? "check" : "search", title: eqOnlyProblems && !q ? "Every device is fine." : "No device matches.", action: `<button class="btn b small" data-eq-reset>Show all devices</button>` });
        body = sec("", `<div class="dev-cols"><div class="stack">${tools}${list}</div>
            <div data-dev>${sel ? `<div class="card stack eq-open">${eqCard(sel, true)}<div class="actions"><button class="btn small" data-ask="${PK.devLook(sel).alarms[0] ? `Why is alarm ${esc(PK.devLook(sel).alarms[0].id)} on for ${esc(sel.id)}?` : `Why is ${esc(sel.id)} ${esc(sel.state)}?`}">Ask why</button></div></div>` : ""}</div></div>`);
      } else if (tab === "stations") {
        // The stations inside Plant; each card opens that station's phone page (the one its QR label opens)
        body = sec("", PKStations.grid(stationList.stations, stationList.machineAlarms));
      } else if (tab === "sequence") {
        body = sec("", V ? V.sequenceBody(params[1], params[2]) : `<p class="muted">The step chart is not available.</p>`);
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
          + `<div class="mt-4">${hmiSettingsHtml(s)}</div>`);
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
        if (PK.screenSize() !== "desktop") setTimeout(() => { const el = page.querySelector("[data-dev]"); if (el) window.scrollTo({ top: el.getBoundingClientRect().top + scrollY - 70, behavior: "instant" }); }, 0);
      });
      const hq = page.querySelector("#hmi-q");
      if (hq) hq.oninput = () => { H.q = hq.value; PK.draw(); };
      const es = page.querySelector("#eq-search");
      if (es) es.oninput = () => { eqQuery = es.value; PK.draw(); };
      page.querySelectorAll("[data-eqf]").forEach((b) => b.onclick = () => { eqOnlyProblems = b.dataset.eqf === "problems"; PK.draw(); });
      const r = page.querySelector("[data-eq-reset]");
      if (r) r.onclick = () => { eqQuery = ""; eqOnlyProblems = false; PK.draw(); };
      if (window.PKVis) window.PKVis.bindTrends(page);
      const pp = (PK.current && PK.current.params) || [];
      if (window.PKVis && pp[0] === "sequence") window.PKVis.bindSequence(page, pp[1]);
      bindAsk(page);
    },
  });

  // ---------- Alarms ----------
  let alarmFilter = "";
  let alarmKind = "all";        // all | active | faults | warnings
  let openAlarm = null;
  let deepLink = null;          // #/alarms/A02: opened once, then scrolled to
  let scrollToAlarm = null;
  PK.view("alarms", {
    live: true,
    leave() { deepLink = null; },
    render() {
      const s = state.status, m = state.model;
      if (!s || !m) return waitingPage();
      const want = PK.current && PK.current.params[0];
      if (want && want !== deepLink) { deepLink = want; openAlarm = want; alarmKind = "all"; alarmFilter = ""; scrollToAlarm = want; }
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
          <span style="flex:1;min-width:200px"><b class="semibold">${esc(a.text)}</b>${a.location ? `<div class="faint">${esc(a.location)}</div>` : ""}</span>
          ${on ? `<span class="pill ${a.severity === "WARNING" ? "warn" : "bad"}"><span class="dot"></span>Active</span>` : ""}<span class="chev">${open ? "⌄" : "›"}</span>
          ${open ? `<div style="flex-basis:100%;padding:10px 0 4px" class="grid two">
              <div>${a.condition ? `<h3>Meaning</h3><p class="mt-0 mb-3 muted">${esc(a.condition)}</p>` : ""}
                ${a.causes.length ? `<h3>Likely causes</h3><ol class="causes small">${a.causes.map((c, i) => `<li><b>${i + 1}</b><span>${esc(c)}</span></li>`).join("")}</ol>` : ""}</div>
              <div>${a.fix.length ? `<h3>How to fix</h3><ol class="fix small">${a.fix.map((c) => `<li style="font-size:15px;cursor:default">${esc(c)}</li>`).join("")}</ol>` : ""}
                <div class="mt-4 actions">${on ? `<a class="btn small" href="#/alarm">Open on Active alarm</a>` : ""}
                ${(() => { const w = alarmWhere(a); return w.block ? `<a class="btn b small" href="${esc(w.href)}">Show where it is set</a>` : ""; })()}
                <button class="btn b small" data-ask="Explain alarm ${esc(a.id)}: when does it come, why, and how is it fixed?">Ask</button></div></div>
            </div>${window.PKMemory ? `<div class="mem-in-row"><h3>History of this alarm</h3>${PKMemory.alarmBlock(a.id, { compact: true })}</div>` : ""}` : ""}
        </li>`;
      }).join("");
      // The counts live in the filter buttons: one row to read and to click, no second row of the same numbers
      return hero({
        title: "Alarms",
        sub: "Every alarm the program can raise: what it means, where it is set, the likely causes and the fix.",
      }) + sec("alt", `${PK.ui.toolbar({ search: { id: "alarm-search", placeholder: "Search by number, text or device", label: "Search alarms", value: alarmFilter },
          left: PK.ui.choice("Show", [["all", "All", m.alarms.length, "Every alarm in the program"], ["active", "Active", s.activeAlarms.length, "On right now"], ["faults", "Faults", faults, "Faults stop the machine"], ["warnings", "Warnings", m.alarms.length - faults, "Warnings do not stop the machine"]]
            .map(([k, l, n, t]) => ({ label: `${l} <b>${n}</b>`, on: alarmKind === k, hot: k === "active" && n, title: t, data: { "data-akind": k } }))) })}
          ${PK.cpuStop(s) ? `<div class="mb-4 notice red">${icon("warn")}<div><b>The PLC is in ${esc(PK.cpuStop(s).word || "STOP")}: the program is not running.</b> The alarms below are as they were when it stopped. <a href="#/alarm">What to check</a></div></div>` : ""}
          ${PK.deviceDiagnostics ? `<div class="diag-zone">${PK.deviceDiagnostics()}</div>` : ""}
          <a class="card plink alarms-past" href="#/history"><span class="ico">${icon("clock")}</span><span><b>Past alarms</b><span class="muted small">What came and went, what fixed it, and the seconds before each alarm: in History.</span></span><span class="chev">›</span></a>
          ${rows ? `<ul class="list">${rows}</ul>` : PK.ui.empty({ ...(alarmKind === "active" && !q
            ? { title: "No alarm is active.", text: "The machine reports no fault or warning right now." }
            : { icon: "search", title: "No alarm matches.", text: `Search by the alarm number (like ${esc((m.alarms[0] || {}).id || "A101")}), a word from the text, or a device tag.` }),
            action: q || alarmKind !== "all" ? `<button class="btn b small" data-areset>Show all alarms</button>` : "" })}`);
    },
    after(page) {
      if (PK.wireDeviceDiagnostics) PK.wireDeviceDiagnostics(page);
      const inp = page.querySelector("#alarm-search");
      if (!inp) return;
      inp.oninput = () => { alarmFilter = inp.value; PK.draw(); };
      page.querySelectorAll("[data-akind]").forEach((b) => b.onclick = () => { alarmKind = b.dataset.akind; PK.draw(); });
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

  window.PKLive = { parseLoc, alarmWhere, bindAsk, stepStrip, seqState, selectedDevice: () => selectedEq };
})();
