// Live views: Active alarm (the start page), Plant, Alarms. Built from /api/status and the machine model.
"use strict";

(() => {
  const { esc, icon, state, cond, sec, hero } = PK;

  const fixDone = {};            // alarm id -> Set of fix steps ticked off on this screen
  let selectedAlarm = null;
  let selectedEq = null;

  /** FAULT / WARNING as people read them; the logic stays on the English words. */
  const sevWord = (a) => (a.severity === "WARNING" ? PK.t("WARNING") : a.severity === "FAULT" ? PK.t("FAULT") : a.severity);
  /** The only state word the server writes itself is "unknown"; the others are the model's own texts and stay as they are. */
  const stT = (text) => (text === "unknown" ? PK.t("unknown") : text);
  /** A message the server wrote, with the words it puts around the program's texts ("(now Level 15 %)", "[unknown]") in German too. */
  const srvT = (text) => {
    const out = PK.srv(text);
    return out === text ? out : out.replace(/\(now /g, "(jetzt ").replace(/, now /g, ", jetzt ").replace(/ \[unknown\]/g, " [unbekannt]").replace(/ \(no live value to check\)/g, " (kein Livewert zum Prüfen)");
  };
  /**
   * What the server wrote about a device or a sequence in a sentence or two ("Agitator M201: Off. Not requested in the
   * current step."): sentence by sentence (the rules in the dictionary know the sentences the server writes; the
   * program's own words inside them stay as they are).
   */
  const sentences = (text) => {
    const t = String(text || "");
    return PK.lang !== "de" ? t : t.replace(/\.\s+(?=[A-Z])/g, ".\n").split("\n").map(srvT).join(" ");
  };
  /** A device's explanation: the first sentence is "<name>: <state>." and the state is translated on its own. */
  function eqExpl(e) {
    const text = String(e.explanation || "");
    const head = `${e.name}: ${e.state}.`;
    if (PK.lang === "de" && text.startsWith(head)) return `${e.name}: ${stT(e.state)}.` + (text.length > head.length ? " " + sentences(text.slice(head.length).trim()) : "");
    return sentences(text);
  }

  /** Devices the program asks for whose output stays off, with what they need (from the server). */
  const blockedOf = (s) => (s.equipment || []).filter((e) => e.blockedBy);
  /** "is blocked: it needs tank level above 20 %, now Level 15 %." (the server writes "needs ..."; the rest of the sentence is here) */
  const blockedTail = (e) => {
    const key = /^needs /.test(e.blockedBy) ? PK.T("is blocked: it {why}.") : PK.T("is blocked: {why}.");
    return PK.t(key, { why: srvT(e.blockedBy) });
  };
  /** "Agitator M201 is blocked: it needs tank level above 20 %, now Level 15 %." */
  const blockedLine = (e) => `${e.name} ${blockedTail(e)}`;
  /** "Blocked: it needs ..." for a list line. */
  const blockedShort = (e) => PK.t(/^needs /.test(e.blockedBy) ? PK.T("Blocked: it {why}") : PK.T("Blocked: {why}"), { why: srvT(e.blockedBy) });
  function blockedNotice(list, max = 3) {
    if (!list.length) return "";
    return `<div class="notice gold blocked-list">${icon("warn")}<div>${list.slice(0, max).map((e) =>
      `<div><b>${esc(e.name)}</b> ${esc(blockedTail(e))}</div>`).join("")}
      ${list.length > max ? `<div class="small">${PK.t("And {n} more on the Plant page.", { n: list.length - max })}</div>` : ""}</div></div>`;
  }
  const opened = Date.now();
  /** No status from PLC-Ki (yet): say so, and what to do if it stays that way. */
  function waitingPage() {
    if (Date.now() - opened < 6000) return hero({ title: PK.t("Connecting to PLC-Ki…"), sub: PK.t("One moment.") });
    const i = PK.connInfo();
    return hero({ eyebrow: `<span class="pill bad"><span class="dot"></span>${PK.t("Not reachable")}</span>`, title: esc(i.title), sub: esc(i.todo),
      actions: `<button class="btn" onclick="location.reload()">${PK.t("Try again")}</button>` });
  }
  const guide = () => (typeof PKStart !== "undefined" ? PKStart.card() : "");

  /** The PLC is not read (not connected, or waiting for Link): explain instead of showing old values. */
  function offlinePage() {
    const i = PK.connInfo();
    const admin = PK.can("settings");
    return hero({
      cls: "hero offline-page",
      eyebrow: `<span class="pill ${i.level}"><span class="dot"></span>${i.level === "warn" ? PK.t("Waiting") : PK.t("Not connected")}</span>`,
      title: PK.t("No live values from the PLC."),
      sub: esc(i.text || i.title),
      actions: admin ? `<a class="btn" href="#/setup/start/plc">${PK.t("Fix the connection")}</a><button class="btn b" data-conn-help>${PK.t("What does this mean?")}</button>`
        : `<button class="btn" data-conn-help>${PK.t("What does this mean?")}</button>`,
      extra: i.todo ? `<div class="notice gold" style="margin-top:26px;max-width:720px">${icon("info")}<div><b>${PK.t("What to do")}</b><br>${esc(i.todo)}${admin ? "" : "<br>" + PK.t("If it stays like this, tell your administrator.")}</div></div>` : "",
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
  const laterText = (ms) => (ms < 1000 ? PK.t("in the same second") : PK.t("{n} s later", { n: Math.round(ms / 1000) }));
  /** "Came 1 s after A01. Probably a result of it: fix A01 first." */
  const followText = (id, ms, text) => {
    const what = `${id}${text ? ` (${text})` : ""}`;
    return ms < 1000 ? PK.t("Came in the same second as {what}. Probably a result of it: fix {id} first.", { what, id })
      : PK.t("Came {n} s after {what}. Probably a result of it: fix {id} first.", { n: Math.round(ms / 1000), what, id });
  };

  // ---------- Share an alarm (review 48) ----------
  let share = null;               // what the Share button on the page sends: { title, text, url }
  /** Plain text for a colleague: the alarm, when it came, the step, the device's values, what came after it, and the link. */
  function shareText(a, s, seq, eqs, failed, after, follow) {
    const url = `${location.origin}${location.pathname}#/alarm/${encodeURIComponent(a.id)}`;
    const local = /^(localhost|127\.0\.0\.1|\[::1\])$|\.localhost$/i.test(location.hostname);
    const lines = [
      `PLC-Ki · ${s.machine}`,
      a.severity === "WARNING" ? PK.t("Warning {id}: {text}", { id: a.id, text: a.text }) : PK.t("Fault {id}: {text}", { id: a.id, text: a.text }),
      a.since ? (a.atStart ? PK.t("Already on when PLC-Ki started watching ({when})", { when: PK.when(a.since) }) : PK.t("Came at {when}", { when: PK.when(a.since) })) : null,
      follow ? followText(follow.root.id, follow.ms, follow.root.text) : null,
      seq ? `${PK.t("Step {n}", { n: seq.step ?? "?" })}${seq.stepName ? " " + seq.stepName : ""} (${seq.name})${seq.halted ? PK.t(", halted") : ""}` : null,
      ...eqs.slice(0, 2).flatMap((e) => [`${e.name}: ${stT(e.state)}`, ...e.values.slice(0, 4).map((v) => `  ${v.label}: ${v.text}`)]),
      ...failed.map((c) => `${PK.t("Failed check: {text}", { text: c.text })}${c.values && c.values.length ? " " + PK.t("(now {values})", { values: c.values.map((v) => v.label + " " + v.text).join(", ") }) : ""}`),
      after.length ? PK.t("After it: {list}", { list: after.map((f) => `${f.alarm.id} ${f.alarm.text} (${laterText(f.ms)})`).join("; ") }) : null,
    ].filter((l) => l != null);
    const link = local ? `${url} ${PK.t("(this address only works on the PC it was copied on)")}` : url;
    return { title: `${a.id}: ${a.text}`, text: lines.join("\n"), url, full: lines.join("\n") + "\n\n" + link };
  }
  /** Send on a phone (the phone's own share sheet), else copy; when the browser allows neither, show the text to copy by hand. */
  async function shareAlarm() {
    if (!share) return;
    if (navigator.share && matchMedia("(pointer: coarse)").matches) {
      try { await navigator.share({ title: share.title, text: share.text, url: share.url }); return; }
      catch (e) { if (e && e.name === "AbortError") return; /* else copy instead */ }
    }
    if (await PK.copyText(share.full)) { PK.toast(PK.t("Copied: the alarm, the step, the values and the link. Paste it in a message.")); return; }
    const sh = PK.sheet(`<h2>${PK.t("Copy this for a colleague")}</h2><p class="muted">${PK.t("This browser does not let PLC-Ki copy. Select the text and copy it.")}</p>
      <textarea class="share-text" readonly rows="10">${esc(share.full)}</textarea><div class="row end"><button class="btn" data-close>${PK.t("Done")}</button></div>`);
    const ta = sh.el.querySelector("textarea");
    ta.focus(); ta.select();
  }
  let linkAlarm = null;           // the alarm named in the address (#/alarm/A01), picked once when the page opens on it
  /** A shared link to an alarm that has gone since: say so instead of silently showing another one. */
  const goneNotice = (id) => `<div class="notice gold aa-gone">${icon("info")}<div>${PK.t("Alarm {id} from the link is not on any more.", { id: `<b>${esc(id)}</b>` })} <a href="#/alarms/${encodeURIComponent(id)}">${PK.t("What it means and when it was on ›")}</a></div></div>`;

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
    return `<a class="btn white" href="#/program/${rest}">${PK.t("Show in the program")}</a>`;
  }

  function eqCard(e, full) {
    const look = PK.devLook(e);
    const lvl = look.alarms.length ? look.lvl : e.level;      // an alarm on the device colours it; else its own state
    const chain = PK.devStrip(e);
    // An alarm on this device: name it and open it
    const alarms = look.alarms.map((a) => `<a class="eq-alarm ${a.severity === "WARNING" ? "w" : ""}" href="#/alarms/${encodeURIComponent(a.id)}">${icon("bell")}<span><b>${a.severity === "WARNING" ? PK.t("Warning") : PK.t("Alarm")} ${esc(a.id)}:</b> ${esc(a.text)}</span><span class="chev">›</span></a>`).join("");
    const vals = e.values.length ? `<div class="kv">${e.values.map((v) => `<span>${esc(v.label)}</span><span class="val">${esc(v.text)}</span>`).join("")}</div>` : "";
    return `<div class="eq-top"><span class="ico">${icon(PK.kindIcon(e.kind))}</span>
        <div class="grow"><div class="eq-name">${esc(e.name)}</div><div class="eq-id">${esc(e.id)}${e.kind ? " · " + esc(e.kind) : ""}</div></div>
        <span class="lv lv-${esc(lvl)}">${esc(look.alarms.length ? (lvl === "fault" ? PK.t("fault") : PK.t("warning")) : stT(e.state))}</span></div>
      ${alarms}
      ${chain}
      ${e.blockedBy ? `<div class="eq-why">${icon("warn")}<span>${esc((w => w.charAt(0).toUpperCase() + w.slice(1))(srvT(e.blockedBy)))}</span></div>` : ""}
      ${vals}
      ${full ? measureHtml(deviceSignals(e)) : ""}
      ${full && e.permits.length ? `<div><h3 class="mt-2">${PK.t("Interlocks")}</h3><ul class="conds">${e.permits.map(cond).join("")}</ul></div>` : ""}
      ${full && e.explanation && !e.blockedBy ? `<p class="m-0 muted small">${esc(eqExpl(e))}</p>` : ""}
      ${full ? `<div class="row">${logicLink(e)}${modelFix("equipment:" + e.id)}</div>` : `<span class="eq-more" aria-hidden="true">${PK.t("Details ›")}</span>`}`;
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
        ${c ? `<a class="ms-w" href="${esc(c.href)}">${esc(c.station)}${c.slot != null ? PK.t(", slot {n}", { n: esc(c.slot) }) : ""}${c.module ? " · " + esc(c.module) : ""}${c.channel != null ? " " + PK.t("· channel {n}", { n: esc(c.channel) }) : ""} ›</a>` : `<span class="ms-w faint small">${PK.t("Module not known")}</span>`}</li>`);
    }
    if (!rows.length) return "";
    const inner = `<h3 class="${card ? "m-0" : "mt-2"}">${PK.t("Where to measure")}</h3><ul class="ms-list">${rows.join("")}</ul>
      ${noModule ? `<p class="m-0 faint small">${PK.t("The module and channel show once the hardware is read from TIA (PLC page, Racks and I/O).")}</p>` : ""}`;
    return card ? `<div class="card aa-ms">${inner}</div>` : `<div class="ms">${inner}</div>`;
  }

  /** "Show in the program" for a device: its block and instance from the model's location ("FB_VFD, instance "Station".M201"). */
  function logicLink(e) {
    const l = parseLoc(e.location);
    return l.block ? `<div><a class="btn b small" href="#/program/${[l.block, l.instance].filter(Boolean).map(encodeURIComponent).join("/")}">${PK.t("Show in the program")}</a></div>` : "";
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
      : `<div class="st">${seq.step != null ? `<b>${seq.step}</b>` : ""}${esc(seq.stepName || PK.t("Step not known"))}</div>`;
    return `<div class="stepnow">
        <span class="sn-l">${PK.t("Now")}</span>${now}
        ${next.length ? `<span class="sn-l">${PK.t("Next")}</span>${next.map((n) => `<div class="st nx ${n.ready === false ? "wait" : n.ready ? "ready" : ""}"><b>${n.to}</b>${esc(n.toName)}</div>`).join("")}` : ""}
      </div>
      <details class="allsteps"><summary>${PK.t("All {n} steps", { n: def.steps.length })}${cur ? " · " + PK.t("this is step {i} of {n}", { i: curIdx + 1, n: def.steps.length }) : ""}</summary>
        <ol class="steplist">${def.steps.map((st, i) => `<li class="${i === curIdx ? curCls : ""}"${i === curIdx ? ' aria-current="step"' : ""}><b>${st.no}</b><span>${esc(st.name)}</span></li>`).join("")}</ol>
      </details>`;
  }
  /** The way to the full step chart and step timeline of a sequence. */
  const chartLink = (seq) => `<div><a class="btn b small" href="#/plant/sequence/${encodeURIComponent(seq.id)}">${PK.t("Step chart and timeline ›")}</a></div>`;
  /** Settings (HMI values) the model says keep the machine from working, e.g. a 0 % tolerance. */
  function settingsNotice(s) {
    const bad = (s.settings || []).filter((x) => x.valid === false);
    if (!bad.length) return "";
    return `<div class="mt-4 notice gold">${icon("warn")}<div><b>${PK.t("A machine setting stops it from working|{n} machine settings stop it from working", { n: bad.length })}</b>
      ${bad.map((x) => `<br>${PK.t("{label} is {value}: {problem}.", { label: esc(x.label), value: `<b>${esc(x.value)}</b>`, problem: esc(x.problem || PK.t("the machine cannot work with this value")) })}`).join("")}
      <br><span class="small">${PK.t("Set it on the HMI (or in the recipe).")} <a href="#/plant/settings">${PK.t("All settings")}</a></span></div></div>`;
  }
  /** "This is wrong" for administrators: correct the model entry with Ki (Setup > Program has the live check). */
  const modelFix = (target) => PK.can("settings") && typeof PKModel !== "undefined"
    ? `<button type="button" class="btn gray small" data-modelfix="${esc(target)}">${PK.t("This is wrong")}</button>` : "";
  /** Opening the full step list shows the current step in the middle of it, not step 1. */
  function bindSteps(page) {
    page.querySelectorAll("details.allsteps").forEach((d) => d.ontoggle = () => {
      const list = d.querySelector(".steplist"), cur = list && list.querySelector("li.cur");
      if (d.open && cur) list.scrollTop = cur.offsetTop - list.clientHeight / 2 + cur.offsetHeight / 2;
    });
  }

  function seqState(q) {
    if (PK.cpuStop()) return [PK.t("Not running"), "bad"];
    return q.halted ? [PK.t("Halted"), "bad"] : q.next.some((n) => n.ready === false) ? [PK.t("Waiting"), "warn"] : [PK.t("Running"), "ok"];
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
      sw ? PK.t("The mode switch on the CPU is at {sw}.", { sw }) + (/STOP/.test(sw) ? " " + PK.t("Someone set it there: find out why before it goes back to RUN.") : "")
        : PK.t("Look at the mode switch on the CPU: RUN or STOP."),
      PK.t("Open the CPU's diagnostic buffer in TIA Portal (Online & diagnostics). The newest entry says why it stopped."),
      PK.t("Ask who stopped it: a download or a STOP from TIA or the CPU display, or a fault in the program (cycle time, a missing block, an access error)."),
      PK.t("Start it again only when you know why it stopped and the machine is safe to run."),
    ];
    const reported = msgs === null ? ""
      : msgs.length ? `<div class="card"><h3>${PK.t("What the CPU reports")}</h3><ul class="list">${msgs.map((m) => `<li style="flex-wrap:wrap"><span style="flex:1;min-width:200px"><b class="semibold">${esc(m.text)}</b>${m.info ? `<div class="faint small">${esc(m.info)}</div>` : ""}</span>${m.since ? `<span class="faint small">${esc(PK.when(m.since))}</span>` : ""}</li>`).join("")}</ul></div>`
      : `<div class="card"><h3>${PK.t("What the CPU reports")}</h3><p class="m-0 muted">${PK.t("No message from the CPU right now. Its diagnostic buffer in TIA says why it stopped.")}</p></div>`;
    return hero({
      cls: "hero cpu-stop-page",
      eyebrow: `<span class="pill bad"><span class="dot"></span>${PK.t("PLC in {word}", { word: esc(word) })}${alarms.length ? " · " + PK.t("{n} alarm on|{n} alarms on", { n: alarms.length }) : ""}</span>`,
      title: PK.t("The PLC is in {word}: the program is not running.", { word: esc(word) }),
      sub: esc(PK.t("In {word} {since}. What you see on these pages is its last state, not a running machine.", { word, since: PK.cpuSince(c) })),
      actions: `<a class="btn" href="#/plc">${PK.t("Open the PLC page")}</a><button class="btn b" data-ask="${esc(PK.t("The PLC is in {word}. What could have stopped it, and what do I check?", { word }))}">${PK.t("Ask")}</button>`,
      extra: `<div class="grid two" style="margin-top:26px;align-items:start">
          <div class="card"><h3>${PK.t("What to check")}</h3><ol class="fix">${todo.map((t) => `<li style="cursor:default">${esc(t)}</li>`).join("")}</ol></div>
          ${reported}</div>`,
    })
    + (alarms.length ? sec("alt", `<h2>${PK.t("Alarms that are on")}</h2><p class="sub">${PK.t("As they were when the CPU stopped. Fix the STOP first; some may go when the program runs again.")}</p>
        <ul class="list">${alarms.map((a) => `<li><span class="tag ${a.severity === "WARNING" ? "gold" : "red"}">${esc(a.id)}</span>
          <span class="grow min0"><a href="#/alarms/${encodeURIComponent(a.id)}">${esc(a.text)}</a></span></li>`).join("")}</ul>`) : "")
    + (seq ? sec(alarms.length ? "" : "alt", `<h2>${esc(seq.name)}</h2><p class="sub">${esc(sentences(seq.explanation))}</p>${stepStrip(seq)}`) : "");
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
          eyebrow: n ? `<span class="pill warn"><span class="dot"></span>${PK.t("No alarm · {n} device blocked|No alarm · {n} devices blocked", { n })}</span>` : `<span class="pill ok"><span class="dot"></span>${PK.t("No alarm")}</span>`,
          title: n ? PK.t("No alarm, but {n} device is blocked.|No alarm, but {n} devices are blocked.", { n }) : PK.t("No alarm is active."),
          sub: esc(n ? blockedLine(root) : (seq ? sentences(seq.explanation) : sentences(s.summary[0] || ""))),
          actions: n ? `<button class="btn" data-ask="${esc(PK.t("Why is {name} blocked, and what do I check?", { name: root.name }))}">${PK.t("Ask why")}</button><a class="btn b" href="#/plant">${PK.t("See the plant")}</a>`
            : `<a class="btn" href="#/plant">${PK.t("See the plant")}</a><button class="btn b" data-ask="${esc(PK.t("What is the machine doing right now?"))}">${PK.t("Ask")}</button>`,
          extra: n ? `<div class="chains">${chains.map(PK.chainHtml).join("")}</div>` : "",
        }) + (settingsNotice(s) ? sec("", settingsNotice(s)) : "")
        + (window.PKMemory ? PKMemory.unnotedBlock() : "")
        + (n ? sec("alt", `<h2>${PK.t("Blocked now")}</h2><p class="sub">${PK.t("The program asks for this device, but its output stays off.|The program asks for these devices, but their output stays off.", { n })} ${n > 1 ? PK.t("The cause comes first.") : ""}</p>
            <div class="stack blocked-now">${order.map((e) => `<div class="aa-dev"><div class="row"><h3 class="m-0">${esc(e.name)}</h3><span class="spacer"></span><a class="small" href="#/plant/devices/${encodeURIComponent(e.id)}">${PK.t("Details ›")}</a></div>
              ${PK.devStrip(e)}</div>`).join("")}</div>`) : "")
        + (seq ? sec(n ? "" : "alt", `<h2>${esc(seq.name)}</h2>${n ? `<p class="sub">${esc(sentences(seq.explanation))}</p>` : ""}${stepStrip(seq)}${chartLink(seq)}
            ${seq.next.length ? `<p class="mt-4 mb-2 muted small">${PK.t("To go on it needs")}</p><ul class="conds">${seq.next.flatMap((x) => x.conditions).map((c) => PK.condHeld(c, s)).join("")}</ul>` : ""}`) : "")
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
        <button class="alarm-chip ${[x, ...g.after.map((f) => f.alarm)].every((y) => y.severity === "WARNING") ? "warning" : ""} ${x.id === a.id ? "sel" : ""}" data-alarm="${esc(x.id)}"${g.after.length ? ` title="${esc(g.after.map((f) => PK.t("{id} came {when}", { id: f.alarm.id, when: laterText(f.ms) })).join(", "))}"` : ""}>
          <span class="d"></span>${i === 0 && firstKnown && groups.length > 1 ? PK.t("First") + " · " : ""}${esc(x.id)} ${esc(x.text.length > 40 ? x.text.slice(0, 38) + "…" : x.text)}${g.after.length ? `<span class="chip-n">${PK.t("+{n} after it", { n: g.after.length })}</span>` : ""}</button>`; }).join("")}</div>
          ${atStart > 1 ? `<p class="faint small" style="margin:-12px 0 18px">${PK.t("{n} alarms were already on when PLC-Ki started watching, so which of those came first is not known.", { n: atStart })}</p>` : ""}` : "";

      // Four short facts under the alarm: the device's state, the step, how long in it, when the alarm came
      const facts = [
        ...eqs.slice(0, 1).map((e) => [stT(e.state), e.name, PK.devLook(e).lvl === "fault" ? "bad" : e.level === "ok" ? "ok" : ""]),
        seq ? [seq.halted ? PK.t("Step {n} halted", { n: seq.step ?? "?" }) : PK.t("Step {n}", { n: seq.step ?? "?" }), `${seq.name}: ${seq.stepName}`, seq.halted ? "bad" : ""] : null,
        seq && seq.timeInStep ? [seq.timeInStep, PK.t("in this step")] : null,
        a.since ? [PK.when(a.since), a.atStart ? PK.t("already on when PLC-Ki started watching") : PK.t("came at")] : null,
      ].filter(Boolean).slice(0, 4);
      const V = window.PKVis;
      const rung = rungHtml(a, where, loc);
      const rec = !!(window.PKCapture && PKCapture.atAlarm(a, loc.instance));
      const fixList = a.fix.length ? `<ol class="fix">${a.fix.map((f, i) => `<li class="${done.has(i) ? "done" : ""}"><button type="button" class="fix-b" data-fix="${i}" aria-pressed="${done.has(i)}">${esc(f)}</button></li>`).join("")}</ol>`
        : `<p class="muted">${PK.t("No fix written for this alarm yet. Ask about it.")}</p>`;

      // Left: the alarm, then straight to the fix. Right: where it is, in the machine and in the program.
      // On a phone the right column follows the left, so the fix is on the first screen.
      const main = `<div class="eyebrow"><span class="pill ${warn ? "warn" : "bad"}"><span class="dot"></span>${esc(sevWord(a))} · ${esc(a.id)}</span></div>
          <h1>${esc(a.text)}</h1>
          ${follow ? `<div class="notice aa-follow">${icon("info")}<div>${esc(followText(follow.root.id, follow.ms))} <button type="button" class="btn plain small" data-alarm="${esc(follow.root.id)}">${PK.t("Open {id} ›", { id: esc(follow.root.id) })}</button></div></div>` : ""}
          ${a.condition ? `<p class="sub">${esc(a.condition)}</p>` : ""}
          ${failed.length ? `<p class="sub"><b>${PK.t("Failed check:")}</b> ${failed.map((c) => esc(c.text) + (c.values && c.values.length ? " " + PK.t("(now {values})", { values: c.values.map((v) => esc(v.label) + " " + esc(v.text)).join(", ") }) : "")).join("; ")}</p>` : ""}
          ${settingsNotice(s)}
          ${PK.ui.tiles(facts.map(([value, label, state]) => ({ value, label, state })), { small: true })}
          <div class="actions"><button class="btn" data-ask="${esc(PK.t("Why is alarm {id} on, and how do I fix it?", { id: a.id }))}">${PK.t("Ask about this alarm")}</button>
            ${where.block ? `<a class="btn b" href="${esc(where.href)}">${PK.t("Show where it is set")}</a>` : ""}
            <button type="button" class="btn b" data-share title="${esc(PK.t("The alarm, the step, the values and a link, as plain text for a colleague"))}">${navigator.share && matchMedia("(pointer: coarse)").matches ? PK.t("Send") : PK.t("Copy link")}</button></div>
          <div class="aa-fix">
            <div class="card"><h3>${PK.t("Fix it")}</h3>${fixList}<p class="mt-3 mb-0 faint small">${PK.t("Tap a step when it is done. The ticks are only on this screen.")}</p></div>
            <div class="card"><h3>${PK.t("Likely causes")}</h3>${a.causes.length ? `<ol class="causes">${a.causes.map((c, i) => `<li><b>${i + 1}</b><span>${esc(c)}</span></li>`).join("")}</ol>` : `<p class="muted">${PK.t("None written yet.")}</p>`}</div>
          </div>
          ${after.length ? `<div class="card aa-after"><h3>${PK.t("Came after it")}</h3><ul class="list">${after.map((f) => `<li>
              <span class="tag ${f.alarm.severity === "WARNING" ? "gold" : "red"}">${esc(f.alarm.id)}</span><span class="grow min0">${esc(f.alarm.text)}<span class="faint small">${esc(PK.t("Came {when}", { when: laterText(f.ms) }))}</span></span>
              <button type="button" class="btn gray small" data-alarm="${esc(f.alarm.id)}">${PK.t("Open")}</button></li>`).join("")}</ul>
            <p class="m-0 faint small">${PK.t("Probably a result of this alarm: fix it first, then see whether that one clears too.|Probably a result of this alarm: fix it first, then see whether they clear too.", { n: after.length })}</p></div>` : ""}
          ${window.PKMemory ? PKMemory.lastLine(a.id) : ""}
          ${eqs.map((e) => `<div class="aa-dev"><div class="row"><h3 class="m-0">${esc(e.name)}</h3><span class="spacer"></span><a class="small" href="#/plant/devices/${encodeURIComponent(e.id)}">${PK.t("Details ›")}</a></div>${PK.devStrip(e)}</div>`).join("")}`;

      const vals = eqs.flatMap((e) => e.values);
      const side = `${eqs.length && V ? `<div class="card aa-pic"><div class="row"><h3 class="m-0">${PK.t("Where in the machine")}</h3><span class="spacer"></span><a class="small" href="#/plant">${PK.t("Plant ›")}</a></div>
            ${V.plantMap(V.around(eqs.map((e) => e.id)), null, eqs.map((e) => e.id))}
            ${vals.length ? `<div class="kv">${vals.map((v) => `<span>${esc(v.label)}</span><span class="val">${esc(v.text)}</span>`).join("")}</div>` : ""}</div>` : ""}
          ${measureHtml([...failed.flatMap((c) => (c.values || []).map((v) => v.id)), ...eqs.flatMap(deviceSignals)], { card: true })}
          <div class="card aa-sw"><h3>${PK.t("Where it is set")}</h3>
            ${where.block ? `<div class="path">${(path || [{ block: where.block }]).map((p) => `<span>${esc(p.block)}${p.instance ? " " + esc(p.instance) : ""}</span>`).join("<i>›</i>")}${where.network ? `<i>›</i><span>${PK.t("Network {n}", { n: esc(where.network) })}</span>` : ""}${where.symbol ? `<i>›</i><span>${esc(where.symbol)}</span>` : ""}</div>` : `<p class="m-0 muted small">${esc(a.location || PK.t("No place in the program is known for this alarm."))}</p>`}
            ${rung ? `<div class="aa-rung">${rung}</div><p class="m-0 faint small">${rec ? PK.t("Values just before the alarm came, from the recording. ? means not recorded.") : PK.t("Values now. They can look normal again after a halt.")}</p>` : ""}
            <div class="gap-2 row">${where.block ? `<a class="btn b small" href="${esc(where.href)}">${PK.t("Open in the program")}</a>` : links(loc)}${modelFix("alarm:" + a.id)}</div></div>`;

      share = shareText(a, s, seq, eqs, failed, after, follow);
      return sec(`hero aa ${warn ? "aa-warn" : ""}`, `${gone}${chips}<div class="aa-grid"><div class="aa-main">${main}</div><aside class="aa-side">${side}</aside></div>`)
        + (window.PKCapture ? PKCapture.alarmSection(a) : "")
        + (checks.length ? sec("", `<h2>${PK.t("What the program checks")}</h2><p class="sub">${a.checksAtAlarm ? PK.t("The comparisons behind this alarm, with the values from the last read before it came. ✗ is the one that failed.") : PK.t("The comparisons behind this alarm, with the values now. ✗ is the one that failed.")}</p>
            <div class="card"><ul class="conds">${checks.map(cond).join("")}</ul></div>`) : "")
        + (typeof PKDocs !== "undefined" ? PKDocs.alarmSection(a, eqs) : "")
        + sec("alt", `<h2>${PK.t("What is going on")}</h2>
          <div class="grid two"><div>${s.summary.map((l) => `<p class="mb-3 sub">${esc(sentences(l))}</p>`).join("")}</div>
          ${seq ? `<div class="card"><h3>${esc(seq.name)}</h3>${stepStrip(seq)}${chartLink(seq)}
            ${seq.next.length ? `<p class="mt-3 mb-2 muted small">${PK.t("To go on it needs")}</p><ul class="conds">${seq.next.flatMap((n) => n.conditions).map((c) => PK.condHeld(c, s)).join("")}</ul>` : ""}</div>` : ""}</div>`)
        + (window.PKMemory ? sec("", `<details class="more" data-hist ${histOpen ? "open" : ""}><summary><b>${PK.t("History of this alarm")}</b> <span class="muted small">${PK.t("every time PLC-Ki saw it, and what was found")}</span></summary>
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
  // ---- Timeline strip on Plant › Overview (review 3, item 40): this shift, 24 h or 7 days, from /api/timeline ----
  // The machine's states as one bar, marks for alarms, halts, STOP, connection lost and values changed, and the latest lines.
  const TL = { win: "shift", data: null, at: 0, busy: false, err: null, sel: -1 };
  const readTlWin = () => { TL.win = PK.prefs.get("timeline", "shift"); };
  readTlWin();
  document.addEventListener("pk:prefs", readTlWin);
  async function loadTimeline() {
    if (TL.busy) return;
    TL.busy = true;
    const win = TL.win;
    try { const d = await PK.api("GET", "/api/timeline?window=" + win); if (win === TL.win) { TL.data = d; TL.err = null; } }
    catch (e) { TL.err = e.message; }
    TL.busy = false; TL.at = Date.now();
    if (win !== TL.win) return loadTimeline();     // the window changed while this one loaded
    PK.draw();
  }
  function timelineCard() {
    if (!TL.busy && Date.now() - TL.at > 30000) setTimeout(loadTimeline, 0);     // every 30 s while the page is open
    const d = TL.data;
    const pick = PK.ui.choice(PK.t("Time"), [["shift", PK.t("This shift")], ["24h", PK.t("24 h")], ["7d", PK.t("7 days")]]
      .map(([k, l]) => ({ label: l, on: TL.win === k, data: { "data-tlwin": k } })));
    const head = `<div class="tl-head"><h3>${PK.t("Timeline")}</h3>${d ? `<span class="faint small">${esc(PK.srv(d.label))}</span>` : ""}<span class="spacer"></span>${pick}</div>`;
    if (!d) return `<div class="card stack">${head}${TL.err ? PK.ui.error(TL.err) : PK.ui.loading("chart", { label: PK.t("Loading the timeline") })}</div>`;
    const m = d.marks[TL.sel];
    const sel = m ? `<p class="tl-sel"><time>${esc(PK.when(m.time))}</time> ${esc(PK.srv(m.text))}${m.shift ? ` <a class="nowrap" href="#/reports/shift/${m.shift.day}/${m.shift.index}/@${new Date(m.time).getTime()}">${PK.t("In the shift log ›")}</a>` : ""}</p>` : "";
    const logHref = d.shift ? `#/reports/shift/${d.shift.day}/${d.shift.index}` : "#/reports/shift";
    const recent = d.recent.length ? PK.ui.events(d.recent, { cls: "tl-recent" })
      : `<p class="m-0 muted small">${PK.t("Nothing has happened yet in the last 24 hours.")}</p>`;
    const legend = d.marks.length ? `<div class="legend small">${[...new Set(d.marks.map((x) => x.mark))].map((k) => `<span class="lg-${esc(k)}"><span class="tl-dot tl-${esc(k)}"></span> ${esc(PK.t(PK.ui.marks[k] || k))}</span>`).join("")}</div>` : "";
    return `<div class="card stack" data-timeline>${head}
      ${PK.ui.timeline({ spans: d.spans, from: d.from, to: d.to, marks: d.marks, sel: TL.sel, label: PK.t("Machine state, {label}", { label: PK.srv(d.label) }) })}${sel}${legend}
      <div><h4 class="mt-3 mb-0 small muted">${PK.t("Latest")}</h4>${recent}</div>
      <div class="row"><span class="spacer"></span><a class="small" href="${logHref}">${PK.t("Shift log ›")}</a></div></div>`;
  }

  const PLANT_TABS = [["overview", PK.T("Overview")], ["devices", PK.T("Devices")], ["sequence", PK.T("Sequence")], ["stations", PK.T("Stations")], ["values", PK.T("Values over time")], ["settings", PK.T("Machine settings")], ["exchange", PK.T("Data exchange")]];
  // The HMI panel's operator settings and buttons (from the program export), loaded once for the Settings tab
  const H = { data: null, error: null, loading: false, q: "", shown: [] };
  async function loadHmi() {
    if (H.data || H.error || H.loading) return;
    H.loading = true;
    try {
      const r = await PK.api("GET", "/api/program/hmi");
      if (r.available) H.data = r; else H.error = r.error || PK.T("No HMI in the program export.");
    } catch (e) { H.error = e.message; }
    H.loading = false;
    PK.draw();
  }
  window.addEventListener("pk:program-changed", () => { H.data = null; H.error = null; });
  const hmiLive = (f) => (window.PKTags ? PKTags.live({ symbol: f.symbol, type: f.type }) : "");

  /** Every value an operator sets on the HMI panel, by screen, with its live value; then the buttons. */
  function hmiSettingsHtml(s) {
    if (H.loading || (!H.data && !H.error)) return `<p class="faint small">${PK.t("Reading the HMI panel…")}</p>`;
    if (!H.data) return `<p class="faint small">${esc(PK.srv(H.error))}</p>`;
    const d = H.data;
    const q = H.q.trim().toLowerCase();
    const hit = (f) => !q || [f.label, f.screen, f.symbol, f.comment].some((x) => String(x || "").toLowerCase().includes(q));
    const rows = d.settings.filter(hit);
    const take = rows.slice(0, 300);
    H.shown = take.filter((f) => f.type && !f.modelTag).concat(d.commands.filter(hit).slice(0, 100).filter((f) => f.type && !f.modelTag));
    const modelVal = (f) => { const m = f.modelTag && (s.settings || []).find((x) => x.id === f.modelTag); return m ? `<span class="m-0 chip">${esc(m.value)}</span>` : null; };
    const byScreen = new Map();
    for (const f of take) { const k = (f.screenKind === "Popup" ? PK.t("Pop-up {name}", { name: f.screen }) : PK.t("Screen {name}", { name: f.screen })) + (f.screenNumber != null ? " " + PK.t("(no. {n})", { n: f.screenNumber }) : ""); if (!byScreen.has(k)) byScreen.set(k, []); byScreen.get(k).push(f); }
    const panels = d.panels.map((p) => `${esc(p.name)}${p.type ? ` · ${esc(p.type)}` : ""}`).join(", ");
    const cmds = d.commands.filter(hit);
    return `<div class="card stack">
        <div><b>${PK.t("On the HMI panel")}</b> <span class="faint small">${panels} · ${PK.t("{n} values the operator can change", { n: d.settings.length })}${d.commands.length ? ", " + PK.t("{n} buttons", { n: d.commands.length }) : ""}</span></div>
        <input class="input" type="search" placeholder="${esc(PK.t("Find a setting, screen or tag"))}" value="${esc(H.q)}" id="hmi-q" aria-label="${esc(PK.t("Find a setting on the HMI"))}">
        ${take.length ? `<table class="tbl hmi-tbl"><tr><th>${PK.t("On the panel")}</th><th>${PK.t("Now")}</th><th>${PK.t("PLC value")}</th></tr>
          ${[...byScreen].map(([screen, fs]) => `<tr class="grp"><td colspan="3"><b>${esc(screen)}</b></td></tr>`
            + fs.map((f) => `<tr><td>${esc(f.label || PK.t("(no text)"))}${f.unit ? ` <span class="faint">[${esc(f.unit)}]</span>` : ""}${f.authorization ? ` <span class="faint small" title="${esc(PK.t("User group needed on the panel"))}">· ${esc(f.authorization)}</span>` : ""}</td>
              <td>${modelVal(f) || (f.type ? hmiLive(f) : `<span class="faint small">${PK.t("not read")}</span>`)}</td>
              <td class="mono small">${esc(f.symbol)}${f.comment ? `<br><span class="muted" style="font-family:inherit">${esc(f.comment)}</span>` : ""}</td></tr>`).join("")).join("")}
          </table>` : `<p class="muted">${q ? PK.t("No setting matches.") : PK.t("The panel has no input fields linked to the PLC.")}</p>`}
        ${rows.length > take.length ? `<p class="faint small">${PK.t("Showing {a} of {b}. Search to narrow it down.", { a: take.length, b: rows.length })}</p>` : ""}
        ${cmds.length ? `<details class="more"><summary>${PK.t("Buttons and switches that change PLC values ({n})", { n: cmds.length })}</summary>
          <table class="tbl hmi-tbl"><tr><th>${PK.t("On the panel")}</th><th>${PK.t("Does")}</th><th>${PK.t("PLC value")}</th><th>${PK.t("Now")}</th></tr>
          ${cmds.slice(0, 100).map((f) => `<tr><td>${esc(f.where)}</td><td class="small">${esc(f.action || "")}</td><td class="mono small">${esc(f.symbol)}</td><td>${f.type ? hmiLive(f) : ""}</td></tr>`).join("")}
          </table></details>` : ""}
        <p class="faint small">${PK.t("Read from the HMI project in the TIA archive. PLC-Ki only reads these values; change them on the panel.")}</p>
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
      const heldText = (c) => { const d = c.heldBy && s.equipment.find((e) => e.id === c.heldBy); return d ? (d.blockedBy ? PK.t("{text} (can't come while {name} is blocked)", { text: c.text, name: d.name }) : PK.t("{text} (can't come while {name} is in fault)", { text: c.text, name: d.name })) : c.text; };
      const waitsFor = main ? main.next.flatMap((n) => n.conditions).filter((c) => c.met === false).map(heldText) : [];
      const badSettings = (s.settings || []).filter((x) => x.valid === false);
      const counts = { devices: attention.length, exchange: s.exchange.length, settings: badSettings.length };
      // CPU not in RUN: the step is only where the program was when it stopped, and nothing waits for anything
      const stop = PK.cpuStop(s);
      const stopWord = stop ? stop.word || "STOP" : "";

      const head = hero({
        eyebrow: main ? `<span class="pill ${stCls}"><span class="dot"></span>${stText}</span>${main.timeInStep && !stop ? `<span class="faint">${PK.t("for {time}", { time: esc(main.timeInStep) })}</span>` : ""}<span class="faint">· ${esc(main.name)}</span>` : "",
        title: main ? (stop ? PK.t("Last step {n}: {name}", { n: main.step ?? "?", name: esc(main.stepName) }) : PK.t("Step {n}: {name}", { n: main.step ?? "?", name: esc(main.stepName) })) : esc(s.machine),
        sub: stop ? esc(PK.t("The CPU is in {word} {since}: the program is not running. The values are its last state.", { word: stopWord, since: PK.cpuSince(stop) }))
          : main ? (main.halted ? esc(PK.t("Halted: {why}", { why: main.haltReasons.map(srvT).join("; ") || PK.t("the outputs are off.") })) : waitsFor.length ? esc(PK.t("Waiting for: {list}", { list: waitsFor.join("; ") })) : esc(main.does || "")) : esc(sentences(s.summary[0] || "")),
        actions: stop ? `<a class="btn" href="#/alarm">${PK.t("What to check")}</a><a class="btn b" href="#/plc">${PK.t("PLC page")}</a>` : `<button class="btn" data-ask="${esc(ask0 ? PK.t("Why is alarm {id} on for {name}, and what do I check?", { id: ask0.id, name: attention[0].name }) : attention.length ? PK.t("Why is {name} {state}, and what do I check?", { name: attention[0].name, state: attention[0].blockedBy ? PK.t("blocked") : stT(attention[0].state) }) : PK.t("What is the machine doing right now?"))}">${PK.t("Ask about it")}</button>${s.activeAlarms.length ? `<a class="btn b" href="#/alarm">${PK.t("{n} active alarm|{n} active alarms", { n: s.activeAlarms.length })}</a>` : ""}`,
        // Measured values as bars with the interlock limit marked; switches and counters stay as numbers
        extra: (s.overview.length ? (V ? V.valueBars(s.overview.slice(0, 8)) : PK.ui.tiles(s.overview.slice(0, 8).map((v) => ({ value: v.text, label: v.label })), { cls: "plant-kpis" })) : "")
          + PK.ui.tabs(PK.t("Plant"), PLANT_TABS.filter(([k]) => (k !== "exchange" || s.exchange.length) && (k !== "settings" || (s.settings || []).length || (H.data && H.data.settings.length)) && (k !== "stations" || stationList.stations.length > 1) && (k !== "sequence" || (state.model && state.model.sequences.length))).map(([k, l]) =>
            ({ label: PK.t(l), on: k === tab, href: "#/plant" + (k === "overview" ? "" : "/" + k), count: k === "devices" || k === "settings" ? counts[k] : 0 }))),
      });

      loadHmi();
      if (window.PKTags) PKTags.watch("plant", () => H.shown);   // live values of the panel's settings
      if (tab !== "settings") H.shown = [];
      let body = "";
      if (tab === "overview") {
        const alarmRows = s.activeAlarms.map((x) => `<li><a class="al ${x.severity === "WARNING" ? "w" : ""}" href="#/alarm">
              <span class="ico">${icon("bell")}</span>
              <span class="nd-t"><b>${esc(x.id)} · ${esc(x.text)}</b><span>${esc(x.condition || x.location || "")}</span></span>
              <span class="lv lv-${x.severity === "WARNING" ? "warn" : "fault"}">${esc(x.severity === "WARNING" ? PK.t("warning") : PK.t("fault"))}</span><span class="chev">›</span></a></li>`).join("");
        // A device whose problem is an alarm already listed above is not listed again; a blocked one still is
        const devRows = attention.filter((e) => e.blockedBy || !PK.devLook(e).alarms.length);
        const stopRow = stop ? `<li><a class="al" href="#/alarm"><span class="ico">${icon("warn")}</span>
              <span class="nd-t"><b>${PK.t("PLC in {word}", { word: esc(stopWord) })}</b><span>${PK.t("The program is not running. What to check is on Active alarm.")}</span></span>
              <span class="lv lv-fault">${esc(stopWord.toLowerCase())}</span><span class="chev">›</span></a></li>` : "";
        const nNeeds = devRows.length + s.activeAlarms.length + badSettings.length + (stop ? 1 : 0);
        const settingRows = badSettings.map((x) => `<li><a href="#/plant/settings">
              <span class="ico">${icon("gear")}</span>
              <span class="nd-t"><b>${PK.t("Setting {label} is {value}", { label: esc(x.label), value: esc(x.value) })}</b><span>${esc(x.problem || PK.t("the machine cannot work with this value"))}</span></span>
              <span class="lv lv-warn">${PK.t("setting")}</span><span class="chev">›</span></a></li>`).join("");
        const needs = nNeeds
          ? `<ul class="needs">${stopRow}${alarmRows}${settingRows}${devRows.map((e) => `<li><a href="#/plant/devices/${encodeURIComponent(e.id)}">
              <span class="ico">${icon(PK.kindIcon(e.kind))}</span>
              <span class="nd-t"><b>${esc(e.name)}</b><span>${esc(e.blockedBy ? blockedShort(e) : e.explanation ? eqExpl(e) : stT(e.state))}</span></span>
              <span class="lv lv-${esc(e.blockedBy ? "warn" : e.level)}">${esc(e.blockedBy ? PK.t("blocked") : stT(e.state))}</span><span class="chev">›</span></a></li>`).join("")}</ul>`
          : `<p class="ok-line">${icon("check")}<span>${PK.t("No alarm, and every device is doing what the program asks.")}</span></p>`;
        const seqCards = s.sequences.map((q) => `<div class="card stack">
            <div class="row"><h3 class="m-0">${esc(q.name)}</h3><span class="pill ${seqState(q)[1]}">${seqState(q)[0]}</span><span class="spacer"></span>
              <a class="small" href="#/plant/sequence/${encodeURIComponent(q.id)}">${PK.t("Step chart and timeline ›")}</a></div>
            ${stepStrip(q)}
            ${stop ? `<div class="notice red">${icon("warn")}<div>${esc(sentences(q.explanation))}</div></div>`
              : q.does ? `<p class="m-0"><span class="muted">${PK.t("This step should:")}</span> ${esc(q.does)}</p>` : ""}
            ${q.halted && q.haltReasons.length && !stop ? `<div class="notice red">${icon("warn")}<div><b>${PK.t("Halted, outputs are off:")}</b> ${q.haltReasons.map((x) => esc(srvT(x))).join("; ")}</div></div>` : ""}
            ${q.next.map((n) => `<div><p class="mt-0 mb-2 muted small">${PK.t(`To go to step {n} "{name}" it needs`, { n: n.to, name: esc(n.toName) })}</p><ul class="conds">${n.conditions.map((c) => PK.condHeld(c, s)).join("")}</ul></div>`).join("")}
            ${q.limit ? `<p class="m-0 faint">${esc(q.limit)}</p>` : ""}
          </div>`).join("");
        body = sec("", `<div class="plant-cols">
            <div class="stack">
              <div class="card stack"><div class="row"><h3 class="m-0">${PK.t("Needs attention")}</h3>${nNeeds ? `<span class="count ${stop || s.activeAlarms.some((x) => x.severity !== "WARNING") ? "" : "warn"}">${nNeeds}</span>` : ""}<span class="spacer"></span>
                <a class="small" href="#/plant/devices">${PK.t("All devices ›")}</a></div>${needs}</div>
              ${timelineCard()}
              ${seqCards}
            </div>
            <div class="stack">
              ${V && s.equipment.length ? `<div class="card stack"><h3 class="m-0">${PK.t("The machine at a glance")}</h3>${V.plantMap(s.equipment, null)}</div>` : ""}
              ${V ? `<a class="card plink" href="#/plant/values"><span class="ico">${icon("clock")}</span><span><b>${PK.t("Values over time")}</b><span class="muted small">${PK.t("The last minutes of every measured value, with steps and alarms.")}</span></span><span class="chev">›</span></a>` : ""}
              ${s.exchange.length ? `<a class="card plink" href="#/plant/exchange"><span class="ico">${icon("link")}</span><span><b>${PK.t("Data exchange")}</b><span class="muted small">${PK.t("{n} signals to and from {partners}.", { n: s.exchange.length, partners: esc([...new Set(s.exchange.map((x) => x.partner))].join(", ")) })}</span></span><span class="chev">›</span></a>` : ""}
            </div>
          </div>`);
      } else if (tab === "devices") {
        const q = eqQuery.trim().toLowerCase();
        const sorted = [...s.equipment].sort((x, y) => rank(x) - rank(y));
        const shown = sorted.filter((e) => (!eqOnlyProblems || problemOf(e)) && (!q || (e.id + " " + e.name + " " + (e.kind || "") + " " + e.state).toLowerCase().includes(q)));
        const sel = s.equipment.find((e) => e.id === selectedEq) || shown[0] || sorted[0];
        const tools = `<div class="dev-tools">${s.equipment.length > 6 ? `<input type="search" id="eq-search" placeholder="${esc(PK.t("Find a device"))}" value="${esc(eqQuery)}" aria-label="${esc(PK.t("Find a device"))}">` : ""}
            ${PK.ui.choice(PK.t("Show"), [{ label: PK.t("All {n}", { n: s.equipment.length }), on: !eqOnlyProblems, data: { "data-eqf": "all" } }, { label: PK.t("Needs attention {n}", { n: attention.length }), on: eqOnlyProblems, data: { "data-eqf": "problems" } }])}</div>`;
        const list = shown.length ? `<ul class="devlist">${shown.map((e) => `<li><button type="button" class="${sel && e.id === sel.id ? "on" : ""}" data-pick="${esc(e.id)}" aria-pressed="${!!sel && e.id === sel.id}">
            <span class="ico">${icon(PK.kindIcon(e.kind))}</span><span class="dv-t"><b>${esc(e.name)}</b><span>${esc(e.id)}</span></span>
            ${devPill(e)}</button></li>`).join("")}</ul>`
          : PK.ui.empty({ icon: eqOnlyProblems && !q ? "check" : "search", title: eqOnlyProblems && !q ? PK.t("Every device is fine.") : PK.t("No device matches."), action: `<button class="btn b small" data-eq-reset>${PK.t("Show all devices")}</button>` });
        body = sec("", `<div class="dev-cols"><div class="stack">${tools}${list}</div>
            <div data-dev>${sel ? `<div class="card stack eq-open">${eqCard(sel, true)}<div class="actions"><button class="btn small" data-ask="${esc(PK.devLook(sel).alarms[0] ? PK.t("Why is alarm {id} on for {name}?", { id: PK.devLook(sel).alarms[0].id, name: sel.id }) : PK.t("Why is {name} {state}?", { name: sel.id, state: stT(sel.state) }))}">${PK.t("Ask why")}</button></div></div>` : ""}</div></div>`);
      } else if (tab === "stations") {
        // The stations inside Plant; each card opens that station's phone page (the one its QR label opens)
        body = sec("", PKStations.grid(stationList.stations, stationList.machineAlarms));
      } else if (tab === "sequence") {
        body = sec("", V ? V.sequenceBody(params[1], params[2]) : `<p class="muted">${PK.t("The step chart is not available.")}</p>`);
      } else if (tab === "values") {
        body = sec("", V ? V.trends() : `<p class="muted">${PK.t("Charts are not available.")}</p>`);
      } else if (tab === "settings") {
        const where = (x) => x.hmi || (H.data && H.data.bySetting[x.id]) || "";
        body = sec("", ((s.settings || []).length ? `<p class="sub">${PK.t("Values an operator or engineer sets on the HMI or in the recipe that the machine needs. ✗ means the machine cannot work with the value it has now.")}</p>
          <div class="card"><table class="tbl"><tr><th></th><th>${PK.t("Setting")}</th><th>${PK.t("Value")}</th><th>${PK.t("What it does")}</th></tr>
            ${(s.settings || []).map((x) => `<tr><td>${x.valid === false ? `<span class="mark no" aria-label="${esc(PK.t("not OK"))}">✗</span>` : x.valid === true ? `<span class="mark yes" aria-label="${esc(PK.t("OK"))}">✓</span>` : ""}</td>
              <td><b>${esc(x.label)}</b>${where(x) ? `<br><span class="faint small">${PK.t("On the panel: {where}", { where: esc(where(x)) })}</span>` : ""}</td><td class="val ${x.valid === false ? "setting-bad" : ""}">${esc(x.value)}</td>
              <td class="muted">${esc(x.text || "")}${x.valid === false && x.problem ? `<br><span class="setting-bad">${esc(x.problem)}${where(x) ? ". " + PK.t("Change it on the panel: {where}", { where: esc(where(x)) }) : ""}</span>` : ""}</td></tr>`).join("")}
          </table></div>` : `<p class="sub">${PK.t("Values an operator sets on the HMI panel, with what they are now.")}</p>`)
          + `<div class="mt-4">${hmiSettingsHtml(s)}</div>`);
      } else {
        body = sec("", `<div class="card"><table class="tbl xchg"><tr><th>${PK.t("With")}</th><th>${PK.t("Signal")}</th><th>${PK.t("Value")}</th><th>${PK.t("Meaning")}</th></tr>
            ${s.exchange.map((x) => `<tr><td class="with"><b title="${x.direction === "in" ? PK.t("from {partner}", { partner: esc(x.partner) }) : PK.t("to {partner}", { partner: esc(x.partner) })}">${x.direction === "in" ? PK.t("← from {partner}", { partner: esc(x.partner) }) : PK.t("→ to {partner}", { partner: esc(x.partner) })}</b></td>
              <td class="sig">${esc(x.label)}</td><td class="val">${esc(x.value)}</td><td class="muted mean">${esc(x.text || "")}</td></tr>`).join("")}
            </table></div>`);
      }
      return head + body
        + (Object.keys(s.tagErrors || {}).length ? sec("tight", `<details class="more"><summary>${PK.t("{n} signals could not be read", { n: Object.keys(s.tagErrors).length })}</summary><ul class="small">${Object.entries(s.tagErrors).map(([k, v]) => `<li><code>${esc(k)}</code>: ${esc(PK.srv(v))}</li>`).join("")}</ul></details>`) : "")
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
      // Timeline: the time window (remembered in this browser) and a mark picked to read its line
      page.querySelectorAll("[data-tlwin]").forEach((b) => b.onclick = () => {
        TL.win = b.dataset.tlwin; TL.data = null; TL.sel = -1; TL.at = 0;
        PK.prefs.set("timeline", TL.win);
        loadTimeline();
      });
      page.querySelectorAll("[data-tlmk]").forEach((b) => b.onclick = () => { const i = +b.dataset.tlmk; TL.sel = TL.sel === i ? -1 : i; PK.draw(); });
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
          ${on ? `<span class="pill ${a.severity === "WARNING" ? "warn" : "bad"}"><span class="dot"></span>${PK.t("Active")}</span>` : ""}<span class="chev">${open ? "⌄" : "›"}</span>
          ${open ? `<div style="flex-basis:100%;padding:10px 0 4px" class="grid two">
              <div>${a.condition ? `<h3>${PK.t("Meaning")}</h3><p class="mt-0 mb-3 muted">${esc(a.condition)}</p>` : ""}
                ${a.causes.length ? `<h3>${PK.t("Likely causes")}</h3><ol class="causes small">${a.causes.map((c, i) => `<li><b>${i + 1}</b><span>${esc(c)}</span></li>`).join("")}</ol>` : ""}</div>
              <div>${a.fix.length ? `<h3>${PK.t("How to fix")}</h3><ol class="fix small">${a.fix.map((c) => `<li style="font-size:15px;cursor:default">${esc(c)}</li>`).join("")}</ol>` : ""}
                <div class="mt-4 actions">${on ? `<a class="btn small" href="#/alarm">${PK.t("Open on Active alarm")}</a>` : ""}
                ${(() => { const w = alarmWhere(a); return w.block ? `<a class="btn b small" href="${esc(w.href)}">${PK.t("Show where it is set")}</a>` : ""; })()}
                <button class="btn b small" data-ask="${esc(PK.t("Explain alarm {id}: when does it come, why, and how is it fixed?", { id: a.id }))}">${PK.t("Ask")}</button></div></div>
            </div>${window.PKMemory ? `<div class="mem-in-row"><h3>${PK.t("History of this alarm")}</h3>${PKMemory.alarmBlock(a.id, { compact: true })}</div>` : ""}` : ""}
        </li>`;
      }).join("");
      // The counts live in the filter buttons: one row to read and to click, no second row of the same numbers
      return hero({
        title: PK.t("Alarms"),
        sub: PK.t("Every alarm the program can raise: what it means, where it is set, the likely causes and the fix."),
      }) + sec("alt", `${PK.ui.toolbar({ search: { id: "alarm-search", placeholder: PK.t("Search by number, text or device"), label: PK.t("Search alarms"), value: alarmFilter },
          left: PK.ui.choice(PK.t("Show"), [["all", PK.t("All"), m.alarms.length, PK.t("Every alarm in the program")], ["active", PK.t("Active"), s.activeAlarms.length, PK.t("On right now")], ["faults", PK.t("Faults"), faults, PK.t("Faults stop the machine")], ["warnings", PK.t("Warnings"), m.alarms.length - faults, PK.t("Warnings do not stop the machine")]]
            .map(([k, l, n, t]) => ({ label: `${l} <b>${n}</b>`, on: alarmKind === k, hot: k === "active" && n, title: t, data: { "data-akind": k } }))) })}
          ${PK.cpuStop(s) ? `<div class="mb-4 notice red">${icon("warn")}<div><b>${PK.t("The PLC is in {word}: the program is not running.", { word: esc(PK.cpuStop(s).word || "STOP") })}</b> ${PK.t("The alarms below are as they were when it stopped.")} <a href="#/alarm">${PK.t("What to check")}</a></div></div>` : ""}
          ${PK.deviceDiagnostics ? `<div class="diag-zone">${PK.deviceDiagnostics()}</div>` : ""}
          <a class="card plink alarms-past" href="#/history"><span class="ico">${icon("clock")}</span><span><b>${PK.t("Past alarms")}</b><span class="muted small">${PK.t("What came and went, what fixed it, and the seconds before each alarm: in History.")}</span></span><span class="chev">›</span></a>
          ${rows ? `<ul class="list">${rows}</ul>` : PK.ui.empty({ ...(alarmKind === "active" && !q
            ? { title: PK.t("No alarm is active."), text: PK.t("The machine reports no fault or warning right now.") }
            : { icon: "search", title: PK.t("No alarm matches."), text: PK.t("Search by the alarm number (like {id}), a word from the text, or a device tag.", { id: esc((m.alarms[0] || {}).id || "A101") }) }),
            action: q || alarmKind !== "all" ? `<button class="btn b small" data-areset>${PK.t("Show all alarms")}</button>` : "" })}`);
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
