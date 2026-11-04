/* Machine memory: every alarm occurrence PLC-Ki saw, what the machine was doing, and what the technicians noted
   as cause and fix. The History page, the "This alarm before" card on Active alarm and Alarms, and the note sheet. */
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
      <form class="stack" style="gap:14px" data-note-form>
        <label class="field">Cause <span class="hint">what was wrong at the machine</span>
          <input type="text" id="n-cause" maxlength="1000" value="${esc(n.cause || "")}" placeholder="e.g. dirty light barrier B23" autocomplete="off"></label>
        ${causeIdeas.length ? `<div class="chips" data-ideas="n-cause" aria-label="Causes noted before or listed for this alarm">${causeIdeas.map((c) => `<button type="button">${esc(c)}</button>`).join("")}</div>` : ""}
        <label class="field">Fix <span class="hint">what you did so it works again</span>
          <textarea id="n-fix" maxlength="1000" rows="2" placeholder="e.g. cleaned the lens, checked the alignment">${esc(n.fix || "")}</textarea></label>
        ${fixIdeas.length ? `<div class="chips" data-ideas="n-fix" aria-label="Fixes used before">${fixIdeas.map((c) => `<button type="button">${esc(c)}</button>`).join("")}</div>` : ""}
        <label class="field">Anything else <span class="hint">optional: parts used, who to call, what to watch</span>
          <textarea id="n-text" maxlength="1000" rows="2">${esc(n.text || "")}</textarea></label>
        <p class="muted small" id="n-err" role="alert" style="margin:0;color:var(--fault-ink)"></p>
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
   * "This alarm before" for one alarm: the plain line ("Happened 4 times... last time a dirty sensor"),
   * the causes noted most, and the last few occurrences. For Active alarm (full) and the Alarms list (compact).
   */
  function alarmBlock(id, { compact = false } = {}) {
    const d = cached(`/api/memory/alarms/${encodeURIComponent(id)}`);
    if (!d) return compact ? "" : `<p class="muted">Looking in the machine memory…</p>`;
    const h = d.summary, occ = remember(d.occurrences);
    const open = occ.find((o) => !o.went);
    const past = occ.filter((o) => o !== open);
    const nums = h.total > (open ? 1 : 0) ? `<div class="mem-nums">
        <span><b>${h.last7Days}</b> in 7 days</span><span><b>${h.last30Days}</b> in 30 days</span>
        ${h.averageDuration ? `<span><b>${esc(h.averageDuration)}</b> on average</span>` : ""}
        <span><b>${esc(h.downtime30Days)}</b> on in 30 days</span></div>` : "";
    const causes = h.causes.length ? `<h3 style="margin-top:16px">Causes noted before</h3><ol class="causes small">${h.causes.slice(0, compact ? 2 : 4).map((c, i) =>
      `<li><b>${c.count}×</b><span>${esc(c.cause)}${c.lastFix ? `<span class="muted"> · fix: ${esc(c.lastFix)}</span>` : ""}</span></li>`).join("")}</ol>` : "";
    const list = past.length ? `<ul class="mem-list">${past.slice(0, compact ? 3 : 5).map((o) => occLine(o)).join("")}</ul>` : "";
    return `<p class="mem-insight">${icon("clock")}<span>${esc(h.insight)}</span></p>${nums}
      ${open && !compact ? `<div class="row" style="margin:12px 0 4px;gap:10px;flex-wrap:wrap"><button type="button" class="btn small" data-note="${esc(open.id)}">${open.note ? "Edit the note on this one" : "Note cause and fix"}</button>
        <span class="muted small">${open.note ? "Noted by " + esc(open.note.by) : "When you have found it, write it down: PLC-Ki shows it the next time."}</span></div>` : ""}
      ${causes}${list ? `<h3 style="margin-top:16px">${compact ? "Last times" : "Last times it was on"}</h3>${list}` : ""}
      ${h.total > 0 ? `<a class="btn plain small" href="#/history/${encodeURIComponent(id)}" style="margin-top:8px">All ${h.total} in History</a>` : ""}`;
  }

  /** On Active alarm when nothing is on: the alarms that went lately and nobody noted what fixed them. */
  function unnotedBlock() {
    const d = cached("/api/memory?days=7", 15000);
    const list = d ? remember(d.unnoted) : [];
    if (!list.length) return "";
    return sec("", `<h2>What fixed it?</h2><p class="sub">${list.length === 1 ? "This alarm" : "These alarms"} went in the last hours and nobody wrote down why. A line now helps the next person at this machine.</p>
      <ul class="mem-list card">${list.map((o) => occLine(o, { showAlarm: true })).join("")}</ul>`);
  }

  // ---------- History page ----------
  const WINDOWS = [[1, "24 h"], [7, "7 days"], [30, "30 days"], [365, "1 year"]];
  let days = 7, q = "", onlyUnnoted = false, logMax = 50, alarmOnly = null;
  let timer = null, logData = null, logKey = "";

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

  PK.view("history", {
    live: true,
    mount() {
      alarmOnly = (PK.current && PK.current.params[0]) || null;
      days = alarmOnly ? 365 : 7;
      logData = null; logKey = "";
      loadLog(true);
      clearInterval(timer);
      timer = setInterval(() => loadLog(true), 15000);
    },
    leave() { clearInterval(timer); },
    render() {
      const want = (PK.current && PK.current.params[0]) || null;
      if (want !== alarmOnly) { alarmOnly = want; days = want ? 365 : 7; loadLog(true); }
      const d = cached(`/api/memory?days=${days}`);
      const def = alarmOnly && state.model ? state.model.alarms.find((a) => a.id === alarmOnly) : null;
      const seg = `<div class="segmented" role="group" aria-label="Time window">${WINDOWS.map(([n, l]) =>
        `<button type="button" class="${days === n ? "on" : ""}" data-days="${n}" aria-pressed="${days === n}">${l}</button>`).join("")}</div>`;
      const top = d ? `<div class="hero-kpis kpis" style="margin-top:28px">
          <div class="k"><b>${d.occurrences}</b><span>alarm${d.occurrences === 1 ? "" : "s"} in the last ${esc(WINDOWS.find((w) => w[0] === days)[1])}</span></div>
          <div class="k"><b>${esc(d.downtime)}</b><span>alarms were on (added up)</span></div>
          <div class="k ${d.occurrences && d.noted < d.occurrences ? "" : "ok"}"><b>${d.noted} of ${d.occurrences}</b><span>have a note on cause and fix</span></div>
          <div class="k"><b>${d.recorded}</b><span>kept in the machine memory</span></div></div>` : "";

      const head = hero({
        eyebrow: `<span class="pill info"><span class="dot"></span>Machine memory</span>`,
        title: def ? `${esc(def.id)} history` : "Alarm history",
        sub: def ? esc(def.text) : "Every alarm PLC-Ki saw on this machine, what it was doing then, and what the technicians found and did. Ask uses it too: \"did this happen before?\"",
        actions: `${seg}${def ? `<a class="btn b small" href="#/history">All alarms</a><a class="btn b small" href="#/alarms/${encodeURIComponent(def.id)}">Alarm details</a>` : ""}`,
        extra: def ? "" : top,
      });

      const loadErr = d && d.loadError ? sec("", `<div class="notice gold">${icon("warn")}<div>${esc(d.loadError)}</div></div>`) : "";

      const unnoted = !def && d && d.unnoted.length ? sec("", `<h2>Waiting for a note</h2><p class="sub">Went in the last 12 hours, no cause or fix written down yet.</p>
        <ul class="mem-list card">${remember(d.unnoted).map((o) => occLine(o, { showAlarm: true })).join("")}</ul>`) : "";

      const max = d && d.top.length ? Math.max(...d.top.map((r) => r.count)) : 1;
      const most = !def && d ? sec("", `<h2>Happens most</h2>${d.top.length ? `<ul class="list mem-top">${d.top.map((r) => `<li class="click" data-hist="${esc(r.alarmId)}" tabindex="0">
            ${sevTag(r.severity, r.alarmId)}
            <span class="mem-top-t"><b>${esc(r.text)}</b><span class="mem-bar" aria-hidden="true"><i style="width:${Math.max(4, Math.round(r.count / max * 100))}%" class="${r.severity === "WARNING" ? "w" : ""}"></i></span>
              <span class="muted small">${r.topCause ? "Most noted cause: " + esc(r.topCause) + " · " : ""}last ${esc(PK.when(r.last))}</span></span>
            <span class="mem-top-n"><b>${r.count}×</b><span class="muted small">${esc(r.downtime)} on</span></span></li>`).join("")}</ul>`
          : `<div class="nomatch">${icon("check")}<b>No alarm in this window.</b><span>PLC-Ki records every alarm that comes while it watches the machine.</span></div>`}`) : "";

      const summary = def ? sec("", `<div class="card">${alarmBlock(def.id)}</div>`) : "";

      const rows = logData ? logData.occurrences : null;
      const csv = new URLSearchParams({ days });
      if (alarmOnly) csv.set("alarm", alarmOnly);
      if (q) csv.set("q", q);
      if (onlyUnnoted) csv.set("unnoted", "true");
      const log = sec("", `<h2>${def ? "Every time it was on" : "Log"}</h2>
        <div class="toolbar"><input type="search" id="mem-q" placeholder="Search alarm, cause, fix, step or name" value="${esc(q)}" aria-label="Search the alarm history">
          <label class="check"><input type="checkbox" id="mem-unnoted" ${onlyUnnoted ? "checked" : ""}> Only without a note</label>
          <a class="btn b small" href="/api/memory/export.csv?${csv}" download>${icon("download")} Download for Excel</a></div>
        ${!rows ? `<p class="muted">Loading…</p>` : rows.length ? `<ul class="mem-list card">${rows.map((o) => occLine(o, { showAlarm: !def })).join("")}</ul>
          ${logData.total > rows.length ? `<button type="button" class="btn plain small" data-more style="margin-top:10px">Show more (${logData.total - rows.length} older)</button>` : ""}`
          : `<div class="nomatch">${icon("search")}<b>${q || onlyUnnoted ? "Nothing matches." : "Nothing recorded in this window yet."}</b><span>${q || onlyUnnoted ? "Try fewer words or a longer time window." : "Alarms are recorded from the moment PLC-Ki watches the machine."}</span></div>`}`);

      return head + loadErr + summary + unnoted + most + log;
    },
    after(page) {
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

  window.PKMemory = { alarmBlock, unnotedBlock, noteSheet, bind };
})();
