/* History (the alarm memory): every alarm occurrence PLC-Ki saw, what the machine was doing, and what the technicians noted
   as cause and fix. The History page, the "History of this alarm" card on Active alarm and Alarms, and the note sheet. */
(function () {
  const { esc, sec, hero, icon, state } = PK;

  // ---------- small cache, so the live pages (redrawn every second) ask the server only now and then ----------
  const cache = new Map();     // url -> { at, data, busy }
  function cached(url, maxAge = 10000) {
    const c = cache.get(url) || {};
    if (!c.busy && (!c.at || Date.now() - c.at > maxAge)) {
      c.busy = true;
      cache.set(url, c);
      PK.api("GET", url).then((d) => { c.data = d; c.err = null; }).catch((e) => { c.err = e.message; })
        .finally(() => { c.at = Date.now(); c.busy = false; PK.draw(); });
    }
    return c.data;
  }
  const forget = () => cache.clear();

  const dur = (o) => o.went ? (o.duration || "") : "still on";
  const sevTag = (sev, id) => `<span class="tag ${sev === "WARNING" ? "gold" : "red"}">${esc(id)}</span>`;

  /** One occurrence as a line: when, how long, the step, and the note if there is one. */
  function occLine(o, { showAlarm = false, noteBtn = true } = {}) {
    const n = o.note;
    return `<li class="mem-occ">
      <div class="mem-when"><b>${esc(PK.when(o.came))}</b><span class="muted small">${o.went ? esc(dur(o)) + (o.endUnknown ? " or less" : "") : `<span class="pill bad small-pill"><span class="dot"></span>still on</span>`}${o.repeats ? ` · came back ${o.repeats}×` : ""}</span></div>
      <div class="mem-body">
        ${showAlarm ? `<div>${sevTag(o.severity, o.alarmId)} <a href="#/alarms/${encodeURIComponent(o.alarmId)}">${esc(o.text)}</a></div>` : ""}
        ${o.steps && o.steps.length ? `<div class="muted small">${o.atStart ? "Already on when PLC-Ki started watching · " : ""}${esc(o.steps.join(" · "))}</div>` : o.atStart ? `<div class="muted small">Already on when PLC-Ki started watching</div>` : ""}
        ${o.checks && o.checks.length ? `<div class="muted small">Failed: ${esc(o.checks.join("; "))}</div>` : ""}
        ${n ? `<div class="mem-note">${n.cause ? `<span><b>Cause</b> ${esc(n.cause)}</span>` : ""}${n.fix ? `<span><b>Fix</b> ${esc(n.fix)}</span>` : ""}${n.text ? `<span>${esc(n.text)}</span>` : ""}<span class="faint small">${esc(n.by)}, ${esc(PK.when(n.at))}</span></div>` : ""}
      </div>
      ${noteBtn ? `<button type="button" class="btn ${n ? "plain" : "b"} small" data-note="${esc(o.id)}">${n ? "Edit note" : o.went ? "What fixed it?" : "Add note"}</button>` : ""}
    </li>`;
  }

  // Occurrences seen on the pages, by id, so a Note button can open the sheet without asking again
  const seen = new Map();
  const remember = (list) => { (list || []).forEach((o) => seen.set(o.id, o)); return list || []; };

  /** The sheet to write down what caused one occurrence and what fixed it. */
  function noteSheet(o, onSaved) {
    if (!state.user) {
      PK.sheet(`<h2>Log in to add a note</h2><p class="muted">Notes carry the name of who wrote them, so the team knows who to ask. Log in first, then add the note.</p>
        <div class="row end"><button class="btn gray" data-close>Cancel</button><a class="btn" href="/login.html?next=${encodeURIComponent("/" + location.hash)}">Log in</a></div>`);
      return;
    }
    const def = (state.model && state.model.alarms || []).find((a) => a.id === o.alarmId) || { causes: [], fix: [] };
    const hist = cache.get(`/api/memory/alarms/${encodeURIComponent(o.alarmId)}`);
    const before = ((hist && hist.data && hist.data.summary.causes) || []).map((c) => c.cause);
    const causeIdeas = [...new Set([...before, ...def.causes])].slice(0, 8);
    const fixIdeas = [...new Set([...((hist && hist.data && hist.data.summary.causes) || []).map((c) => c.lastFix).filter(Boolean)])].slice(0, 4);
    const n = o.note || {};
    const s = PK.sheet(`<h2>${o.went ? "What caused it, what fixed it?" : "Note on this alarm"}</h2>
      <p class="muted" style="margin:-4px 0 14px">${sevTag(o.severity, o.alarmId)} ${esc(o.text)}<br>
        <span class="small">Came ${esc(PK.when(o.came))}${o.went ? `, went after ${esc(dur(o))}` : ", still on"}${o.steps && o.steps.length ? ` · ${esc(o.steps[0])}` : ""}</span></p>
      <form class="gap-4 stack" data-note-form>
        <label class="field">Cause <span class="hint">what was wrong at the machine</span>
          <input type="text" id="n-cause" maxlength="1000" value="${esc(n.cause || "")}" placeholder="e.g. dirty light barrier B23" autocomplete="off"></label>
        ${causeIdeas.length ? `<div class="chips" data-ideas="n-cause" aria-label="Causes noted before or listed for this alarm">${causeIdeas.map((c) => `<button type="button">${esc(c)}</button>`).join("")}</div>` : ""}
        <label class="field">Fix <span class="hint">what you did so it works again</span>
          <textarea id="n-fix" maxlength="1000" rows="2" placeholder="e.g. cleaned the lens, checked the alignment">${esc(n.fix || "")}</textarea></label>
        ${fixIdeas.length ? `<div class="chips" data-ideas="n-fix" aria-label="Fixes used before">${fixIdeas.map((c) => `<button type="button">${esc(c)}</button>`).join("")}</div>` : ""}
        <label class="field">Anything else <span class="hint">optional: parts used, who to call, what to watch</span>
          <textarea id="n-text" maxlength="1000" rows="2">${esc(n.text || "")}</textarea></label>
        <p class="m-0 ink-fault muted small" id="n-err" role="alert"></p>
        <div class="row end">${o.note ? `<button type="button" class="btn plain" data-clear>Remove note</button>` : ""}<button type="button" class="btn gray" data-close>Cancel</button><button type="submit" class="btn">Save note</button></div>
      </form>`);
    const f = s.el.querySelector("[data-note-form]");
    setTimeout(() => f.querySelector("#n-cause").focus(), 50);
    s.el.querySelectorAll("[data-ideas] button").forEach((b) => b.onclick = () => {
      const inp = f.querySelector("#" + b.parentElement.dataset.ideas);
      inp.value = b.textContent;
      inp.focus();
    });
    const save = async (body) => {
      try {
        const r = await PK.api("POST", `/api/memory/occurrences/${encodeURIComponent(o.id)}/note`, body);
        seen.set(r.id, r);
        forget();
        s.close();
        if (logData) loadLog(true);
        PK.toast(r.note ? "Note saved. PLC-Ki will show it the next time this alarm comes." : "Note removed.");
        if (onSaved) onSaved(r);
        PK.draw();
      } catch (x) { s.el.querySelector("#n-err").textContent = x.message; }
    };
    f.onsubmit = (e) => {
      e.preventDefault();
      const body = { cause: f.querySelector("#n-cause").value, fix: f.querySelector("#n-fix").value, text: f.querySelector("#n-text").value };
      if (!body.cause.trim() && !body.fix.trim() && !body.text.trim()) { s.el.querySelector("#n-err").textContent = "Write the cause, the fix, or a note first."; return; }
      save(body);
    };
    const clr = s.el.querySelector("[data-clear]");
    if (clr) clr.onclick = () => save({ cause: "", fix: "", text: "" });
  }

  /** Wires the Note buttons a page drew with occLine. */
  function bind(page) {
    page.querySelectorAll("[data-note]").forEach((b) => b.onclick = (e) => {
      e.stopPropagation();
      const o = seen.get(b.dataset.note);
      if (o) noteSheet(o);
    });
  }

  /**
   * "History of this alarm": the plain line ("Happened 4 times... last time a dirty sensor"),
   * the causes noted most, and the last few occurrences. For Active alarm (full) and the Alarms list (compact).
   */
  function alarmBlock(id, { compact = false } = {}) {
    const d = cached(`/api/memory/alarms/${encodeURIComponent(id)}`);
    if (!d) return compact ? "" : `<p class="muted">Looking in History…</p>`;
    const h = d.summary, occ = remember(d.occurrences);
    const open = occ.find((o) => !o.went);
    const past = occ.filter((o) => o !== open);
    const nums = h.total > (open ? 1 : 0) ? `<div class="mem-nums">
        <span><b>${h.last7Days}</b> in 7 days</span><span><b>${h.last30Days}</b> in 30 days</span>
        ${h.averageDuration ? `<span><b>${esc(h.averageDuration)}</b> on average</span>` : ""}
        <span><b>${esc(h.downtime30Days)}</b> on in 30 days</span></div>` : "";
    const causes = h.causes.length ? `<h3 class="mt-4">Causes noted before</h3><ol class="causes small">${h.causes.slice(0, compact ? 2 : 4).map((c, i) =>
      `<li><b>${c.count}×</b><span>${esc(c.cause)}${c.lastFix ? `<span class="muted"> · fix: ${esc(c.lastFix)}</span>` : ""}</span></li>`).join("")}</ol>` : "";
    const list = past.length ? `<ul class="mem-list">${past.slice(0, compact ? 3 : 5).map((o) => occLine(o)).join("")}</ul>` : "";
    return `<p class="mem-insight">${icon("clock")}<span>${esc(h.insight)}</span></p>${nums}
      ${open && !compact ? `<div class="row" style="margin:12px 0 4px;gap:10px;flex-wrap:wrap"><button type="button" class="btn small" data-note="${esc(open.id)}">${open.note ? "Edit the note on this one" : "Note cause and fix"}</button>
        <span class="muted small">${open.note ? "Noted by " + esc(open.note.by) : "When you have found it, write it down: PLC-Ki shows it the next time."}</span></div>` : ""}
      ${causes}${list ? `<h3 class="mt-4">${compact ? "Last times" : "Last times it was on"}</h3>${list}` : ""}
      ${h.total > 0 ? `<a class="mt-2 btn plain small" href="#/history/${encodeURIComponent(id)}">All ${h.total} in History</a>` : ""}`;
  }

  /**
   * One line for Active alarm, next to Fix it. With a note from an earlier time: "Last time: solenoid coil Y101 burnt ·
   * fix: …", the most useful sentence for the next technician. Without one: "First time" (or "Seen 3 times, nothing
   * noted"), with the button to note cause and fix. "" while the memory is loading.
   */
  function lastLine(id) {
    const d = cached(`/api/memory/alarms/${encodeURIComponent(id)}`);
    if (!d) return "";
    const occ = remember(d.occurrences).slice().sort((x, y) => Date.parse(y.came) - Date.parse(x.came));
    const open = occ.find((o) => !o.went);
    const before = occ.filter((o) => o !== open);
    const last = before.find((o) => o.note && (o.note.cause || o.note.fix || o.note.text));
    const btn = open ? `<button type="button" class="btn ${last ? "plain" : "b"} small" data-note="${esc(open.id)}">${open.note ? "Edit your note" : "Note cause and fix"}</button>` : "";
    const times = Math.max(before.length, d.summary.total - (open ? 1 : 0));
    const hist = times > 0 ? `<a class="small" href="#/history/${encodeURIComponent(id)}">${times === 1 ? "1 time" : times + " times"} before ›</a>` : "";
    if (last) {
      const n = last.note;
      const text = [n.cause, n.fix ? "fix: " + n.fix : "", n.text].filter(Boolean).join(" · ");
      return `<div class="lastline noted">${icon("clock")}<span><b>Last time:</b> ${esc(text)} <span class="faint">${esc(n.by)}, ${esc(PK.when(n.at))}</span></span>${hist}${btn}</div>`;
    }
    return `<div class="lastline">${icon("clock")}<span>${times > 0 ? `Seen before, nobody noted the cause` : "First time PLC-Ki sees this alarm"}</span>${hist}${btn}</div>`;
  }

  /** On Active alarm when nothing is on: the alarms that went lately and nobody noted what fixed them. */
  function unnotedBlock() {
    const d = cached("/api/memory?days=7", 15000);
    const list = d ? remember(d.unnoted) : [];
    if (!list.length) return "";
    return sec("", `<h2>What fixed it?</h2><p class="sub">${list.length === 1 ? "This alarm" : "These alarms"} went in the last hours and nobody wrote down why. A line now helps the next person at this machine.</p>
      <ul class="mem-list card">${list.map((o) => occLine(o, { showAlarm: true })).join("")}</ul>`);
  }

  // ---------- History page: the one home for past alarms ----------
  // Tabs: Log (#/history: every alarm that came, what was noted, PLC and connection events), Ranking
  // (#/history/ranking/count or /downtime) and Recordings (#/history/recordings[/<id>], from capture.js).
  // #/history/<alarm id> is one alarm's own history. One set of time windows for all of it.
  const WINDOWS = [[1, "24 h"], [7, "7 days"], [30, "30 days"], [365, "1 year"]];
  const TABS = [["log", "Log"], ["ranking", "Ranking"], ["recordings", "Recordings"]];
  let days = 7, q = "", onlyUnnoted = false, logMax = 50, alarmOnly = null, rankBy = "count";
  let timer = null, logData = null, logKey = "";
  let events = null, eventsAt = 0;
  let mounted = false;        // the first visit starts at 7 days; later visits keep the window that was picked

  function loadLog(force) {
    const key = `${days}|${q}|${onlyUnnoted}|${logMax}|${alarmOnly || ""}`;
    if (!force && key === logKey && logData) return;
    logKey = key;
    const p = new URLSearchParams({ days, max: logMax });
    if (q) p.set("q", q);
    if (onlyUnnoted) p.set("unnoted", "true");
    if (alarmOnly) p.set("alarm", alarmOnly);
    PK.api("GET", "/api/memory/occurrences?" + p).then((d) => { logData = d; remember(d.occurrences); PK.draw(); }).catch(() => { logData = { total: 0, occurrences: [], error: true }; PK.draw(); });
  }
  /** The PLC's own events (STOP, back in RUN) and the connection's (dropped, back), kept since PLC-Ki started. */
  function loadEvents() {
    eventsAt = Date.now();
    PK.api("GET", "/api/events?kind=cpu,link&max=50").then((e) => { events = e; PK.draw(); }).catch(() => { events = events || []; });
  }

  /** Where the address points: a tab, or one alarm's history. An alarm id is checked first, so a tab never hides one. */
  function where(params) {
    const p0 = params[0] || "";
    const isAlarm = p0 && state.model && state.model.alarms.some((a) => a.id === p0);
    if (!p0 || (!isAlarm && TABS.some(([k]) => k === p0))) return { tab: p0 || "log", alarm: null, sub: params[1] || null };
    return { tab: "log", alarm: p0, sub: null };
  }

  const windowSeg = () => PK.ui.choice("Time window", WINDOWS.map(([n, l]) => ({ label: l, on: days === n, data: { "data-days": n } })));
  const windowName = () => WINDOWS.find((w) => w[0] === days)[1];

  function rankingHtml(d) {
    const rows = d ? d.top : null;
    const size = (r) => (rankBy === "downtime" ? r.downtimeMinutes : r.count) || 0;
    const max = rows && rows.length ? Math.max(...rows.map(size), 0.01) : 1;
    const seg = PK.ui.choice("Rank by", [["count", "Most often"], ["downtime", "Longest on"]].map(([k, l]) => ({ label: l, on: rankBy === k, href: "#/history/ranking/" + k })));
    const list = !rows ? PK.ui.loading("list") : rows.length ? `<ul class="list mem-top">${rows.map((r) => `<li class="click" data-hist="${esc(r.alarmId)}" tabindex="0">
          ${sevTag(r.severity, r.alarmId)}
          <span class="mem-top-t"><b>${esc(r.text)}</b><span class="mem-bar" aria-hidden="true"><i style="width:${Math.max(4, Math.round(size(r) / max * 100))}%" class="${r.severity === "WARNING" ? "w" : ""}"></i></span>
            <span class="muted small">${r.topCause ? "Most noted cause: " + esc(r.topCause) + " · " : ""}last ${esc(PK.when(r.last))}</span></span>
          <span class="mem-top-n">${rankBy === "downtime" ? `<b>${esc(r.downtime)}</b><span class="muted small">${r.count}×</span>` : `<b>${r.count}×</b><span class="muted small">${esc(r.downtime)} on</span>`}</span></li>`).join("")}</ul>`
      : PK.ui.empty({ title: "No alarm in this window.", text: "PLC-Ki records every alarm that comes while it watches the machine." });
    return sec("", `<div class="toolbar">${seg}</div>
      <p class="sub">${rankBy === "downtime" ? "The alarms that were on longest in total" : "The alarms that came most often"}, in the last ${esc(windowName())}. Tap one for its own history.</p>${list}
      <p class="mt-4 faint small">The shift report and OEE are on <a href="#/reports">Reports</a>.</p>`);
  }

  function eventsHtml() {
    if (!events || !events.length) return "";
    return `<details class="mt-4 more"><summary><b>PLC and connection</b> <span class="muted small">${events.length} since PLC-Ki started</span></summary>
      <ul class="mt-3 list events">${events.map((e) => {
        const ok = /back in RUN|\bback\b|again/i.test(e.text);
        const word = e.kind === "cpu" ? (ok ? "RUN" : (/(?:to|in) ([A-Z]+)/.exec(e.text) || [])[1] || "STOP") : ok ? "Back" : "Lost";
        return `<li><span class="ev-time">${esc(PK.date(e.time, true))}</span><span class="ev-kind ${ok ? "run" : "stop"}">${esc(word)}</span>
          <span style="flex:1;min-width:180px">${e.kind === "cpu" ? `<a href="#/plc">${esc(e.text)}</a>` : esc(e.text)}</span></li>`;
      }).join("")}</ul></details>`;
  }

  PK.view("history", {
    live: true,
    mount() {
      const w = where((PK.current && PK.current.params) || []);
      // The time window stays when moving between the tabs; one alarm's own history starts at a year
      if (!mounted || (w.alarm != null) !== (alarmOnly != null)) days = w.alarm ? 365 : 7;
      mounted = true;
      alarmOnly = w.alarm;
      logData = null; logKey = "";
      loadLog(true);
      clearInterval(timer);
      timer = setInterval(() => loadLog(true), 15000);
    },
    leave() { clearInterval(timer); },
    render() {
      const w = where((PK.current && PK.current.params) || []);
      if (w.tab === "recordings" && w.sub && window.PKCapture) return PKCapture.page(w.sub);
      if (w.alarm !== alarmOnly) { alarmOnly = w.alarm; days = w.alarm ? 365 : 7; loadLog(true); }
      if (w.tab === "ranking") rankBy = w.sub === "downtime" ? "downtime" : "count";
      const d = cached(`/api/memory?days=${days}` + (w.tab === "ranking" ? `&by=${rankBy}` : ""));
      const def = alarmOnly && state.model ? state.model.alarms.find((a) => a.id === alarmOnly) : null;
      const top = d && !def && w.tab !== "recordings" ? PK.ui.tiles([
          { value: d.occurrences, label: `alarm${d.occurrences === 1 ? "" : "s"} in the last ${windowName()}` },
          { value: d.downtime, label: "alarms were on (added up)" },
          { value: `${d.noted} of ${d.occurrences}`, label: "have a note on cause and fix", state: d.occurrences && d.noted < d.occurrences ? "" : "ok" },
          { value: d.recorded, label: "kept on this PC" }]) : "";
      const nav = def ? "" : PK.ui.tabs("History", TABS.map(([k, l]) => ({ label: l, on: k === w.tab, href: "#/history" + (k === "log" ? "" : "/" + k) })));

      const head = hero({
        title: def ? `${esc(def.id)} history` : "History",
        sub: def ? esc(def.text) : "Every alarm PLC-Ki saw on this machine, what it was doing then, and what the technicians found and did. Ask uses it too: \"did this happen before?\"",
        actions: `${w.tab === "recordings" ? "" : windowSeg()}${def ? `<a class="btn b small" href="#/history">All alarms</a><a class="btn b small" href="#/alarms/${encodeURIComponent(def.id)}">Alarm details</a>` : ""}`,
        extra: top + nav,
      });

      if (w.tab === "recordings") return head + sec("", window.PKCapture ? PKCapture.listHtml() : `<p class="muted">Recordings are not available.</p>`);

      const loadErr = d && d.loadError ? sec("", `<div class="notice gold">${icon("warn")}<div>${esc(d.loadError)}</div></div>`) : "";
      if (w.tab === "ranking") return head + loadErr + rankingHtml(d);

      if (!def && Date.now() - eventsAt > 15000) loadEvents();
      const unnoted = !def && d && d.unnoted.length ? sec("", `<h2>Waiting for a note</h2><p class="sub">Went in the last 12 hours, no cause or fix written down yet.</p>
        <ul class="mem-list card">${remember(d.unnoted).map((o) => occLine(o, { showAlarm: true })).join("")}</ul>`) : "";
      const summary = def ? sec("", `<div class="card">${alarmBlock(def.id)}</div>`) : "";

      const rows = logData ? logData.occurrences : null;
      const csv = new URLSearchParams({ days });
      if (alarmOnly) csv.set("alarm", alarmOnly);
      if (q) csv.set("q", q);
      if (onlyUnnoted) csv.set("unnoted", "true");
      const log = sec("", `<h2>${def ? "Every time it was on" : "Every alarm that came"}</h2>
        <div class="toolbar"><input type="search" id="mem-q" placeholder="Search alarm, cause, fix, step or name" value="${esc(q)}" aria-label="Search History">
          <label class="check"><input type="checkbox" id="mem-unnoted" ${onlyUnnoted ? "checked" : ""}> Only without a note</label>
          <a class="btn b small" href="/api/memory/export.csv?${csv}" download>${icon("download")} Download for Excel</a></div>
        ${!rows ? `<div class="card">${PK.ui.loading("list")}</div>` : rows.length ? `<ul class="mem-list card">${rows.map((o) => occLine(o, { showAlarm: !def })).join("")}</ul>
          ${logData.total > rows.length ? `<button type="button" class="mt-3 btn plain small" data-more>Show more (${logData.total - rows.length} older)</button>` : ""}`
          : PK.ui.empty({ icon: "search", title: q || onlyUnnoted ? "Nothing matches." : "Nothing recorded in this window yet.", text: q || onlyUnnoted ? "Try fewer words or a longer time window." : "Alarms are recorded from the moment PLC-Ki watches the machine." })}
        ${def ? "" : eventsHtml()}`);

      return head + loadErr + summary + unnoted + log;
    },
    after(page) {
      if (window.PKCapture) PKCapture.wirePage(page);
      page.querySelectorAll("[data-days]").forEach((b) => b.onclick = () => { days = Number(b.dataset.days); logMax = 50; loadLog(); PK.draw(); });
      page.querySelectorAll("[data-hist]").forEach((li) => {
        li.onclick = () => PK.go("#/history/" + encodeURIComponent(li.dataset.hist));
        li.onkeydown = (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); li.click(); } };
      });
      const inp = page.querySelector("#mem-q");
      if (inp) {
        let t;
        inp.oninput = () => { clearTimeout(t); t = setTimeout(() => { q = inp.value.trim(); logMax = 50; loadLog(); }, 250); };
      }
      const un = page.querySelector("#mem-unnoted");
      if (un) un.onchange = () => { onlyUnnoted = un.checked; loadLog(); };
      const more = page.querySelector("[data-more]");
      if (more) more.onclick = () => { logMax += 100; loadLog(); };
      bind(page);
      if (window.PKLive) PKLive.bindAsk(page);
    },
  });

  window.PKMemory = { alarmBlock, lastLine, unnotedBlock, noteSheet, bind };
})();
