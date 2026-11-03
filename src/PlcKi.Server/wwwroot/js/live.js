// Live views: Active alarm (the start page), Plant, Alarms. Built from /api/status and the machine model.
"use strict";

(() => {
  const { esc, icon, state, cond, sec, hero } = PK;

  const firstSeen = {};          // alarm id -> when this page first saw it (PLC-Ki keeps no alarm log yet)
  const fixDone = {};            // alarm id -> Set of fix steps ticked off on this screen
  let selectedAlarm = null;
  let selectedEq = null;

  const sorted = (alarms) => [...alarms].sort((a, b) => (a.severity === "WARNING") - (b.severity === "WARNING"));
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
      block: (loc.match(/^\s*([A-Za-z_]\w*)/) || [])[1],
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
      ${chain}${vals}
      ${full && e.permits.length ? `<div><h3 style="margin-top:6px">Interlocks</h3><ul class="conds">${e.permits.map(cond).join("")}</ul></div>` : ""}
      ${full && e.blocking.length && e.requested ? `<div class="notice red">${icon("warn")}<div><b>Requested but blocked by:</b><ul class="conds" style="margin-top:6px">${e.blocking.map(cond).join("")}</ul></div></div>` : ""}
      ${full && e.explanation ? `<p class="muted small" style="margin:0">${esc(e.explanation)}</p>` : ""}`;
  }

  function stepStrip(seq) {
    const def = state.model && state.model.sequences.find((d) => d.id === seq.id);
    if (!def) return "";
    const curIdx = def.steps.findIndex((st) => st.no === seq.step);
    return `<div class="stepstrip">${def.steps.map((st, i) =>
      `<div class="st ${st.no === seq.step ? "cur" + (seq.halted ? " halted" : "") : i < curIdx ? "done" : ""}"><b>${st.no}</b>${esc(st.name)}</div>`).join("")}</div>`;
  }

  function seqState(q) {
    return q.halted ? ["Halted", "bad"] : q.next.some((n) => n.ready === false) ? ["Waiting", "warn"] : ["Running", "ok"];
  }

  function bindAsk(page) {
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
      const alarms = sorted(s.activeAlarms);
      for (const a of alarms) firstSeen[a.id] ||= Date.now();
      for (const id of Object.keys(firstSeen)) if (!alarms.some((a) => a.id === id)) delete firstSeen[id];
      const seq = s.sequences.find((q) => q.halted) || s.sequences[0];
      if (offline()) return guide() + offlinePage();

      if (!alarms.length) {
        return guide() + hero({
          eyebrow: `<span class="pill ok"><span class="dot"></span>No alarm</span>`,
          title: "No alarm is active.",
          sub: esc(s.summary[0] || ""),
          actions: `<a class="btn" href="#/plant">See the plant</a><button class="btn b" data-ask="What is the machine doing right now?">Ask</button>`,
        }) + (seq ? sec("alt", `<h2>${esc(seq.name)}</h2><p class="sub">${esc(seq.explanation)}</p>${stepStrip(seq)}`) : "");
      }

      if (!alarms.some((a) => a.id === selectedAlarm)) selectedAlarm = alarms[0].id;
      const a = alarms.find((x) => x.id === selectedAlarm);
      const warn = a.severity === "WARNING";
      const eqs = relatedEquipment(a);
      const done = fixDone[a.id] || new Set();
      const since = firstSeen[a.id];
      const loc = parseLoc(a.location);
      const path = callPath(loc.block);

      const chips = alarms.length > 1 ? `<div class="alarm-chips">${alarms.map((x, i) =>
        `<button class="alarm-chip ${x.severity === "WARNING" ? "warning" : ""} ${x.id === a.id ? "sel" : ""}" data-alarm="${esc(x.id)}">
          <span class="d"></span>${i === 0 ? "First · " : ""}${esc(x.id)} ${esc(x.text.length > 40 ? x.text.slice(0, 38) + "…" : x.text)}</button>`).join("")}</div>` : "";

      const kpis = [
        ...eqs.slice(0, 2).map((e) => [e.state, e.name, e.level === "ok" ? "ok" : e.level === "idle" ? "" : "bad"]),
        seq ? [`Step ${seq.step ?? "?"}`, `${seq.name}: ${seq.stepName}${seq.halted ? ", halted" : ""}`, seq.halted ? "bad" : ""] : null,
        seq && seq.timeInStep ? [seq.timeInStep, "in this step"] : null,
        [since && Date.now() - since > 3000 ? PK.ago(since).replace(" ago", "") : "now", "on this screen"],
      ].filter(Boolean).slice(0, 4);

      return guide() + sec("hero", `${chips}
          <div class="eyebrow"><span class="pill ${warn ? "warn" : "bad"}"><span class="dot"></span>${esc(a.severity)} · ${esc(a.id)}</span></div>
          <h1>${esc(a.text)}</h1>
          ${a.condition ? `<p class="sub">${esc(a.condition)}</p>` : ""}
          <div class="actions"><button class="btn" data-ask="Why is alarm ${esc(a.id)} on, and how do I fix it?">Ask about this alarm</button>
            ${loc.block ? `<a class="btn b" href="#/logic/${[loc.block, loc.instance, loc.output].filter(Boolean).map(encodeURIComponent).join("/")}">Show where it is set</a>` : ""}</div>
          <div class="kpis" style="margin-top:34px">${kpis.map(([v, l, c]) => `<div class="k ${c || ""}"><b>${esc(v)}</b><span>${esc(l)}</span></div>`).join("")}</div>`)

        + sec("alt", `<h2>Fix it</h2><p class="sub">Work down the list. Tap a step when it is done.</p>
          <div class="grid two">
            <div class="card">${a.fix.length ? `<ol class="fix">${a.fix.map((f, i) => `<li class="${done.has(i) ? "done" : ""}" data-fix="${i}">${esc(f)}</li>`).join("")}</ol>` : `<p class="muted">No fix written for this alarm yet. Ask about it.</p>`}</div>
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
          ${seq ? `<div class="card"><h3>${esc(seq.name)}</h3>${stepStrip(seq)}
            ${seq.next.length ? `<p class="muted small" style="margin:12px 0 8px">To go on it needs</p><ul class="conds">${seq.next.flatMap((n) => n.conditions).map(cond).join("")}</ul>` : ""}</div>` : ""}</div>`);
    },
    after(page) {
      page.querySelectorAll("[data-alarm]").forEach((b) => b.onclick = () => { selectedAlarm = b.dataset.alarm; PK.draw(); });
      page.querySelectorAll("[data-fix]").forEach((li) => li.onclick = () => {
        const set = (fixDone[selectedAlarm] ||= new Set());
        const i = Number(li.dataset.fix);
        set.has(i) ? set.delete(i) : set.add(i);
        PK.draw();
      });
      bindAsk(page);
    },
  });

  // ---------- Plant ----------
  PK.view("plant", {
    live: true,
    render() {
      const s = state.status;
      if (!s) return waitingPage();
      if (offline()) return guide() + offlinePage();
      const main = s.sequences[0];
      const [stText, stCls] = main ? seqState(main) : ["", ""];
      const top = hero({
        eyebrow: main ? `<span class="pill ${stCls}"><span class="dot"></span>${stText}</span>${main.timeInStep ? `<span class="faint">for ${esc(main.timeInStep)}</span>` : ""}` : "",
        title: main ? `Step ${main.step ?? "?"}: ${esc(main.stepName)}` : esc(s.machine),
        sub: esc(main ? main.explanation : s.summary.join(" ")),
        actions: `<button class="btn" data-ask="What is the machine doing right now?">Ask about it</button>${s.activeAlarms.length ? `<a class="btn b" href="#/alarm">${s.activeAlarms.length} active alarm${s.activeAlarms.length > 1 ? "s" : ""}</a>` : ""}`,
        extra: s.overview.length ? `<div class="kpis" style="margin-top:34px">${s.overview.slice(0, 8).map((v) => `<div class="k"><b>${esc(v.text)}</b><span>${esc(v.label)}</span></div>`).join("")}</div>` : "",
      });

      const seqs = s.sequences.map((q) => `<div class="card stack">
          <div class="row"><h3 style="margin:0">${esc(q.name)}</h3><span class="pill ${seqState(q)[1]}">${seqState(q)[0]}</span></div>
          ${stepStrip(q)}
          ${q.does ? `<p style="margin:0">${esc(q.does)}</p>` : ""}
          ${q.halted && q.haltReasons.length ? `<div class="notice red">${icon("warn")}<div><b>Halted, outputs are off:</b> ${q.haltReasons.map(esc).join("; ")}</div></div>` : ""}
          ${q.next.map((n) => `<div><p class="muted small" style="margin:0 0 8px">To go to step ${n.to} "${esc(n.toName)}" it needs</p><ul class="conds">${n.conditions.map(cond).join("")}</ul></div>`).join("")}
          ${q.limit ? `<p class="faint" style="margin:0">${esc(q.limit)}</p>` : ""}
        </div>`).join("");

      const sel = s.equipment.find((e) => e.id === selectedEq);
      return guide() + top
        + (seqs ? sec("alt", `<h2>Sequence</h2><p class="sub">Each step, where it is now, and what it waits for.</p>${seqs}`) : "")
        + sec("", `<h2>Equipment</h2><p class="sub">Every device, and the chain from request to feedback. The first red link is why it is not running.</p>
            ${sel ? `<div class="card stack" style="margin-bottom:18px">${eqCard(sel, true)}<div class="actions"><button class="btn small" data-ask="Why is ${esc(sel.id)} ${esc(sel.state)}?">Ask why</button><button class="btn b small" data-close-eq>Close</button></div></div>` : ""}
            <div class="grid auto">${s.equipment.map((e) => `<div class="eq ${e.id === selectedEq ? "sel" : ""}" data-eq="${esc(e.id)}">${eqCard(e, false)}</div>`).join("")}</div>`)
        + (s.exchange.length ? sec("dark", `<h2>Data exchange</h2><p class="sub">Signals to and from other systems.</p>
            <div style="overflow-x:auto"><table class="tbl"><tr><th>Partner</th><th>Direction</th><th>Signal</th><th>Value</th><th>Meaning</th></tr>
            ${s.exchange.map((x) => `<tr><td><b>${esc(x.partner)}</b></td><td>${x.direction === "in" ? "from" : "to"} ${esc(x.partner)}</td><td>${esc(x.label)}</td><td class="val">${esc(x.value)}</td><td class="muted">${esc(x.text || "")}</td></tr>`).join("")}
            </table></div>`) : "")
        + (Object.keys(s.tagErrors || {}).length ? sec("tight", `<details class="more"><summary>${Object.keys(s.tagErrors).length} signals could not be read</summary><ul class="small">${Object.entries(s.tagErrors).map(([k, v]) => `<li><code>${esc(k)}</code>: ${esc(v)}</li>`).join("")}</ul></details>`) : "");
    },
    after(page) {
      page.querySelectorAll("[data-eq]").forEach((d) => d.onclick = () => { selectedEq = selectedEq === d.dataset.eq ? null : d.dataset.eq; PK.draw(); });
      const c = page.querySelector("[data-close-eq]");
      if (c) c.onclick = () => { selectedEq = null; PK.draw(); };
      bindAsk(page);
    },
  });

  // ---------- Alarms ----------
  let alarmFilter = "";
  let openAlarm = null;
  PK.view("alarms", {
    live: true,
    render() {
      const s = state.status, m = state.model;
      if (!s || !m) return waitingPage();
      const active = new Set(s.activeAlarms.map((a) => a.id));
      const q = alarmFilter.toLowerCase();
      const all = m.alarms.filter((a) => !q || (a.id + " " + a.text + " " + (a.location || "")).toLowerCase().includes(q))
        .sort((a, b) => (active.has(b.id) - active.has(a.id)) || a.id.localeCompare(b.id, undefined, { numeric: true }));
      const faults = m.alarms.filter((a) => a.severity !== "WARNING").length;
      const rows = all.map((a) => {
        const on = active.has(a.id), open = openAlarm === a.id;
        return `<li class="click" data-open="${esc(a.id)}" style="flex-wrap:wrap">
          <span class="tag ${a.severity === "WARNING" ? "gold" : "red"}">${esc(a.id)}</span>
          <span style="flex:1;min-width:200px"><b style="font-weight:600">${esc(a.text)}</b>${a.location ? `<div class="faint">${esc(a.location)}</div>` : ""}</span>
          ${on ? `<span class="pill ${a.severity === "WARNING" ? "warn" : "bad"}"><span class="dot"></span>Active</span>` : ""}<span class="chev">${open ? "⌄" : "›"}</span>
          ${open ? `<div style="flex-basis:100%;padding:10px 0 4px" class="grid two">
              <div>${a.condition ? `<h3>Meaning</h3><p style="margin:0 0 12px" class="muted">${esc(a.condition)}</p>` : ""}
                ${a.causes.length ? `<h3>Likely causes</h3><ol class="causes small">${a.causes.map((c, i) => `<li><b>${i + 1}</b><span>${esc(c)}</span></li>`).join("")}</ol>` : ""}</div>
              <div>${a.fix.length ? `<h3>How to fix</h3><ol class="fix small">${a.fix.map((c) => `<li style="font-size:15px;cursor:default">${esc(c)}</li>`).join("")}</ol>` : ""}
                <div class="actions" style="margin-top:14px">${on ? `<a class="btn small" href="#/alarm">Open on Active alarm</a>` : ""}
                <button class="btn b small" data-ask="Explain alarm ${esc(a.id)}: when does it come, why, and how is it fixed?">Ask</button></div></div>
            </div>` : ""}
        </li>`;
      }).join("");
      return hero({
        title: "Every alarm, explained.",
        sub: "Each alarm the program can raise, with its meaning, where it is set, the likely causes and the fix.",
        extra: `<div class="kpis" style="margin-top:12px"><div class="k ${s.activeAlarms.length ? "bad" : "ok"}"><b>${s.activeAlarms.length}</b><span>active now</span></div>
          <div class="k"><b>${m.alarms.length}</b><span>alarms in the program</span></div><div class="k"><b>${faults}</b><span>faults (stop the machine)</span></div>
          <div class="k"><b>${m.alarms.length - faults}</b><span>warnings</span></div></div>`,
      }) + sec("alt", `<input type="search" id="alarm-search" placeholder="Search by number, text or device" value="${esc(alarmFilter)}" style="margin-bottom:16px;max-width:460px">
          <ul class="list">${rows || `<li class="muted">No alarm matches.</li>`}</ul>`);
    },
    after(page) {
      const inp = page.querySelector("#alarm-search");
      inp.oninput = () => { alarmFilter = inp.value; PK.draw(); const i = PK.$("alarm-search"); i.focus(); i.setSelectionRange(i.value.length, i.value.length); };
      page.querySelectorAll("[data-open]").forEach((li) => li.onclick = (e) => {
        if (e.target.closest("a,button")) return;
        openAlarm = openAlarm === li.dataset.open ? null : li.dataset.open;
        PK.draw();
      });
      bindAsk(page);
    },
  });

  window.PKLive = { parseLoc, bindAsk };
})();
