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
    return (prog.hasLogic || prog.hasSource ? `<a class="btn white" href="#/logic/${rest}">Show the logic</a>` : "") +
      `<a class="btn b" href="#/code/${rest}">Show the code</a>`;
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
      ${full ? logicLink(e) : `<span class="eq-more" aria-hidden="true">Details ›</span>`}`;
  }
  /** "Show the logic" for a device: its block and instance from the model's location ("FB_VFD, instance "Station".M201"). */
  function logicLink(e) {
    const l = parseLoc(e.location);
    return l.block ? `<div><a class="btn b small" href="#/logic/${[l.block, l.instance].filter(Boolean).map(encodeURIComponent).join("/")}">Show the logic</a></div>` : "";
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
        })
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
      const path = callPath(loc.block);

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
          <div class="actions"><button class="btn" data-ask="Why is alarm ${esc(a.id)} on, and how do I fix it?">Ask about this alarm</button>
            ${loc.block ? `<a class="btn b" href="#/logic/${[loc.block, loc.instance, loc.output].filter(Boolean).map(encodeURIComponent).join("/")}">Show where it is set</a>` : ""}</div>
          <div class="kpis" style="margin-top:34px">${kpis.map(([v, l, c]) => `<div class="k ${c || ""}"><b>${esc(v)}</b><span>${esc(l)}</span></div>`).join("")}</div>`)

        + sec("alt", `<h2>Fix it</h2><p class="sub">Work down the list. Tap a step when it is done. The ticks are only on this screen.</p>
          <div class="grid two">
            <div class="card">${a.fix.length ? `<ol class="fix">${a.fix.map((f, i) => `<li class="${done.has(i) ? "done" : ""}"><button type="button" class="fix-b" data-fix="${i}" aria-pressed="${done.has(i)}">${esc(f)}</button></li>`).join("")}</ol>` : `<p class="muted">No fix written for this alarm yet. Ask about it.</p>`}</div>
            <div class="card"><h3>Likely causes</h3>${a.causes.length ? `<ol class="causes">${a.causes.map((c, i) => `<li><b>${i + 1}</b><span>${esc(c)}</span></li>`).join("")}</ol>` : `<p class="muted">None written yet.</p>`}</div>
          </div>`)

        + sec("", `<h2>Where in the machine</h2><p class="sub">${eqs.length ? "The device behind the alarm, and why it is or is not doing its job." : esc(a.location || "No device is linked to this alarm.")}</p>
          ${eqs.length ? `<div class="grid ${eqs.length > 1 ? "two" : ""}">${eqs.map((e) => `<div class="card stack">${eqCard(e, true)}</div>`).join("")}</div>` : ""}`)

        + sec("dark", `<h2>Where in the software</h2><p class="sub">Where the program sets this alarm, from the main cycle down to the bit.</p>
          ${loc.block ? `<div class="path">${(path || [{ block: loc.block }]).map((p) => `<span>${esc(p.block)}${p.instance ? " " + esc(p.instance) : ""}</span>`).join("<i>›</i>")}${loc.output ? `<i>›</i><span>${esc(loc.output)}</span>` : ""}</div>` : ""}
          <p class="muted small" style="margin:14px 0 22px">${esc(a.location || "")}</p>
          <div class="actions">${links(loc)}</div>`)

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
    },
  });

  // ---------- Plant ----------
  let eqQuery = "";
  let eqOnlyProblems = false;
  PK.view("plant", {
    live: true,
    render() {
      const s = state.status;
      if (!s) return waitingPage();
      if (offline()) return offlinePage() + guide();
      const main = s.sequences[0];
      const [stText, stCls] = main ? seqState(main) : ["", ""];
      const blocked = blockedOf(s);
      const top = hero({
        eyebrow: main ? `<span class="pill ${stCls}"><span class="dot"></span>${stText}</span>${main.timeInStep ? `<span class="faint">for ${esc(main.timeInStep)}</span>` : ""}` : "",
        title: main ? `Step ${main.step ?? "?"}: ${esc(main.stepName)}` : esc(s.machine),
        sub: esc(main ? main.explanation : s.summary.join(" ")),
        extra0: blocked.length ? blockedNotice(blocked) : "",
        actions: `<button class="btn" data-ask="${blocked.length ? `Why is ${esc(blocked[0].name)} blocked, and what do I check?` : "What is the machine doing right now?"}">Ask about it</button>${s.activeAlarms.length ? `<a class="btn b" href="#/alarm">${s.activeAlarms.length} active alarm${s.activeAlarms.length > 1 ? "s" : ""}</a>` : ""}`,
        extra: s.overview.length ? `<div class="kpis" style="margin-top:34px">${s.overview.slice(0, 8).map((v) => `<div class="k"><b>${esc(v.text)}</b><span>${esc(v.label)}</span></div>`).join("")}</div>` : "",
      });

      const seqs = s.sequences.map((q) => `<div class="card stack">
          <div class="row"><h3 style="margin:0">${esc(q.name)}</h3><span class="pill ${seqState(q)[1]}">${seqState(q)[0]}</span></div>
          ${stepStrip(q)}${chartLink(q)}
          ${q.does ? `<p style="margin:0"><span class="muted">This step should:</span> ${esc(q.does)}</p>` : ""}
          ${q.halted && q.haltReasons.length ? `<div class="notice red">${icon("warn")}<div><b>Halted, outputs are off:</b> ${q.haltReasons.map(esc).join("; ")}</div></div>` : ""}
          ${q.next.map((n) => `<div><p class="muted small" style="margin:0 0 8px">To go to step ${n.to} "${esc(n.toName)}" it needs</p><ul class="conds">${n.conditions.map(cond).join("")}</ul></div>`).join("")}
          ${q.limit ? `<p class="faint" style="margin:0">${esc(q.limit)}</p>` : ""}
        </div>`).join("");

      const sel = s.equipment.find((e) => e.id === selectedEq);
      const problem = (e) => !!e.blockedBy || (e.level !== "ok" && e.level !== "idle");
      const nProblems = s.equipment.filter(problem).length;
      const q = eqQuery.trim().toLowerCase();
      const shown = s.equipment.filter((e) => (!eqOnlyProblems || problem(e)) && (!q || (e.id + " " + e.name + " " + (e.kind || "") + " " + e.state).toLowerCase().includes(q)));
      const tools = s.equipment.length > 4 ? `<div class="toolbar">
          <input type="search" id="eq-search" placeholder="Find a device by name or tag" value="${esc(eqQuery)}" aria-label="Find a device">
          <div class="segmented" role="group" aria-label="Show">
            <button type="button" class="${eqOnlyProblems ? "" : "on"}" data-eqf="all" aria-pressed="${!eqOnlyProblems}">All ${s.equipment.length}</button>
            <button type="button" class="${eqOnlyProblems ? "on" : ""}" data-eqf="problems" aria-pressed="${eqOnlyProblems}">Needs attention ${nProblems}</button></div></div>` : "";
      const V = window.PKVis;
      return top
        + (V && s.equipment.length ? sec("", `<h2>The machine at a glance</h2><p class="sub">Every device as a symbol, in the colour of what it is doing now. Tap one for the details.</p>${V.plantMap(s.equipment, selectedEq)}`) : "")
        + (seqs ? sec("alt", `<h2>Sequence</h2><p class="sub">Each step, where it is now, and what it waits for.</p>${seqs}`) : "")
        + (V ? sec("", `<h2>Values over time</h2><p class="sub">The measured values, the step each sequence was in, and when alarms came.</p>${V.trends()}`) : "")
        + sec("", `<h2>Equipment</h2><p class="sub">Every device, and the chain from request to feedback. The first red link is why it is not running.</p>
            ${sel ? `<div class="card stack eq-open" style="margin-bottom:18px">${eqCard(sel, true)}<div class="actions"><button class="btn small" data-ask="Why is ${esc(sel.id)} ${esc(sel.state)}?">Ask why</button><button class="btn b small" data-close-eq>Close</button></div></div>` : ""}
            ${tools}
            ${shown.length ? `<div class="grid auto">${shown.map((e) => `<div class="eq ${e.id === selectedEq ? "sel" : ""}" data-eq="${esc(e.id)}" role="button" tabindex="0" aria-label="${esc(e.name)}, ${esc(e.state)}">${eqCard(e, false)}</div>`).join("")}</div>`
              : `<div class="nomatch">${eqOnlyProblems && !q ? `${icon("check")}<b>Every device is fine.</b><span>Nothing needs attention right now.</span>` : `${icon("search")}<b>No device matches.</b><span>Try part of the name or the tag, like ${esc((s.equipment[0] || {}).id || "XV101")}.</span>`}
                <button class="btn b small" data-eq-reset>Show all devices</button></div>`}`)
        + (s.exchange.length ? sec("dark", `<h2>Data exchange</h2><p class="sub">Signals to and from other systems.</p>
            <table class="tbl xchg"><tr><th>With</th><th>Signal</th><th>Value</th><th>Meaning</th></tr>
            ${s.exchange.map((x) => `<tr><td class="with"><b title="${x.direction === "in" ? "from" : "to"} ${esc(x.partner)}">${x.direction === "in" ? "← from" : "→ to"} ${esc(x.partner)}</b></td>
              <td class="sig">${esc(x.label)}</td><td class="val">${esc(x.value)}</td><td class="muted mean">${esc(x.text || "")}</td></tr>`).join("")}
            </table>`) : "")
        + (Object.keys(s.tagErrors || {}).length ? sec("tight", `<details class="more"><summary>${Object.keys(s.tagErrors).length} signals could not be read</summary><ul class="small">${Object.entries(s.tagErrors).map(([k, v]) => `<li><code>${esc(k)}</code>: ${esc(v)}</li>`).join("")}</ul></details>`) : "")
        + guide();
    },
    after(page) {
      page.querySelectorAll("[data-eq]").forEach((d) => {
        d.onclick = () => { selectedEq = selectedEq === d.dataset.eq ? null : d.dataset.eq; PK.draw(); if (selectedEq) PK.$("page").querySelector(".eq-open")?.scrollIntoView({ block: "nearest", behavior: "smooth" }); };
        d.onkeydown = (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); d.click(); } };
      });
      const es = page.querySelector("#eq-search");
      if (es) es.oninput = () => { eqQuery = es.value; PK.draw(); };
      page.querySelectorAll("[data-eqf]").forEach((b) => b.onclick = () => { eqOnlyProblems = b.dataset.eqf === "problems"; PK.draw(); });
      const r = page.querySelector("[data-eq-reset]");
      if (r) r.onclick = () => { eqQuery = ""; eqOnlyProblems = false; PK.draw(); };
      const c = page.querySelector("[data-close-eq]");
      if (c) c.onclick = () => { selectedEq = null; PK.draw(); };
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
                ${(() => { const l = parseLoc(a.location); return l.block ? `<a class="btn b small" href="#/logic/${[l.block, l.instance, l.output].filter(Boolean).map(encodeURIComponent).join("/")}">Show where it is set</a>` : ""; })()}
                <button class="btn b small" data-ask="Explain alarm ${esc(a.id)}: when does it come, why, and how is it fixed?">Ask</button></div></div>
            </div>` : ""}
        </li>`;
      }).join("");
      return hero({
        title: "Alarms",
        sub: "Every alarm the program can raise: what it means, where it is set, the likely causes and the fix.",
        extra: `<div class="kpis" style="margin-top:12px">${[["active", s.activeAlarms.length, "active now", s.activeAlarms.length ? "bad" : "ok"], ["all", m.alarms.length, "alarms in the program", ""],
            ["faults", faults, "faults (stop the machine)", ""], ["warnings", m.alarms.length - faults, "warnings", ""]]
            .map(([k, n, l, c]) => `<button type="button" class="k kbtn ${c} ${alarmKind === k ? "on" : ""}" data-akind="${k}" aria-pressed="${alarmKind === k}" aria-label="Show ${esc(l)}: ${n}"><b>${n}</b><span>${esc(l)}</span></button>`).join("")}</div>`,
      }) + sec("alt", `<div class="toolbar"><input type="search" id="alarm-search" placeholder="Search by number, text or device" value="${esc(alarmFilter)}" aria-label="Search alarms">
          <div class="segmented" role="group" aria-label="Show">${[["all", "All", m.alarms.length], ["active", "Active", s.activeAlarms.length], ["faults", "Faults", faults], ["warnings", "Warnings", m.alarms.length - faults]]
            .map(([k, l, n]) => `<button type="button" class="${alarmKind === k ? "on" : ""}" data-akind="${k}" aria-pressed="${alarmKind === k}">${l} ${n}</button>`).join("")}</div></div>
          ${rows ? `<ul class="list">${rows}</ul>` : `<div class="nomatch">${alarmKind === "active" && !q ? `${icon("check")}<b>No alarm is active.</b><span>The machine reports no fault or warning right now.</span>`
            : `${icon("search")}<b>No alarm matches.</b><span>Search by the alarm number (like ${esc((m.alarms[0] || {}).id || "A101")}), a word from the text, or a device tag.</span>`}
            ${q || alarmKind !== "all" ? `<button class="btn b small" data-areset>Show all alarms</button>` : ""}</div>`}`)
        + sec("", `<h2>Recent alarms</h2><p class="sub">When alarms came and went since PLC-Ki started watching. Newest first.</p>
          ${!events ? `<p class="muted">Loading…</p>` : events.length ? `<ul class="list events">${events.map((e) => {
            const mm = /^(\S+) (came|went|was already active)/.exec(e.text);
            const id = mm && mm[1], kind = mm ? (mm[2] === "went" ? "went" : mm[2] === "came" ? "came" : "start") : "";
            return `<li><span class="ev-time">${esc(PK.date(e.time, true))}</span><span class="ev-kind ${kind}">${{ came: "Came", went: "Went", start: "Already on" }[kind] || ""}</span>
              <span style="flex:1;min-width:180px">${id ? `<a href="#/alarms/${encodeURIComponent(id)}">${esc(id)}</a> ` : ""}${esc(id ? e.text.replace(/^\S+ (came|went|was already active when watching started):?\s*/, "") : e.text)}</span></li>`;
          }).join("")}</ul>` : `<p class="muted">No alarm came or went since PLC-Ki started watching.</p>`}`);
    },
    after(page) {
      const inp = page.querySelector("#alarm-search");
      if (!inp) return;
      inp.oninput = () => { alarmFilter = inp.value; PK.draw(); };
      page.querySelectorAll("[data-akind]").forEach((b) => b.onclick = () => { alarmKind = b.dataset.akind; PK.draw(); });
      const ar = page.querySelector("[data-areset]");
      if (ar) ar.onclick = () => { alarmFilter = ""; alarmKind = "all"; PK.draw(); };
      page.querySelectorAll("[data-open]").forEach((li) => {
        li.onclick = (e) => {
          if (e.target.closest("a,button")) return;
          openAlarm = openAlarm === li.dataset.open ? null : li.dataset.open;
          PK.setHash(openAlarm ? "#/alarms/" + encodeURIComponent(openAlarm) : "#/alarms");
          deepLink = openAlarm;
          PK.draw();
        };
        li.onkeydown = (e) => { if ((e.key === "Enter" || e.key === " ") && e.target === li) { e.preventDefault(); li.click(); } };
      });
      bindAsk(page);
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

  window.PKLive = { parseLoc, bindAsk };
})();
