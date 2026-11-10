// Line screen (TV) mode (UX review 3, item 44; concept addition 8): a page for a TV on the line, read from about 5 m.
// No menu or top bar: the state in big letters, the step and the cycle, and the machine picture filling the rest.
// When an alarm comes the screen turns to that alarm, large; it goes back to the picture when no alarm is on.
//   #/tv             the whole machine
//   #/tv/<station>   one station (its key or its name; #/tv?station=<name> leads here too, see MOVED in core.js)
// It only reads: no login is needed in public view, and nothing here is saved (PK.tvMode paints the theme without
// keeping it). Esc or the small × in the corner leaves it.
"use strict";

(() => {
  const { esc, icon, state } = PK;
  const STALE_MS = 5000;                 // the same as the rest of the app: no answer for 5 s is "not answering"
  const T = { tick: null, extras: null, idle: null, keys: null, move: null, cycles: null, shift: null };

  const pad = (n) => String(n).padStart(2, "0");
  /** 83 → "1:23", 3725 → "1:02:05" */
  const dur = (sec) => {
    sec = Math.max(0, Math.round(sec));
    const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), s = sec % 60;
    return h ? `${h}:${pad(m)}:${pad(s)}` : `${m}:${pad(s)}`;
  };

  // Burn-in: the whole screen sits a few pixels off and moves to the next place every 3 minutes. Taken from the
  // clock, so it needs no timer of its own and every redraw agrees.
  const SHIFT = 6;
  const PLACES = [[0, 0], [1, 0], [1, 1], [0, 1], [-1, 1], [-1, 0], [-1, -1], [0, -1]];
  const shift = (t = Date.now()) => PLACES[Math.floor(t / 180000) % PLACES.length].map((v) => v * SHIFT);

  /**
   * What this screen is about: the whole machine, or one station. null when the station is not there.
   * A station's screen also counts the alarms of no station (such as an emergency stop): they concern every station.
   */
  function scopeOf(param, s) {
    const all = PKStations.list(s);
    const stationsOf = (a) => all.stations.filter((st) => st.alarms.some((x) => x.id === a.id)).map((st) => st.name);
    if (param == null || param === "") {
      const [level, word] = PKStations.levelOf({ alarms: s.activeAlarms, equipment: s.equipment, sequences: s.sequences });
      return { name: s.machine || PK.t("Machine"), station: null, alarms: s.activeAlarms, equipment: s.equipment, sequences: s.sequences, level, word, stationsOf };
    }
    const want = param === "-" ? "" : param, low = want.toLowerCase();
    const st = all.stations.find((x) => x.key === want) || all.stations.find((x) => x.key.toLowerCase() === low || x.name.toLowerCase() === low);
    if (!st) return null;
    const mine = new Set([...st.alarms, ...all.machineAlarms]);
    const alarms = s.activeAlarms.filter((a) => mine.has(a));      // in the server's order: the first that came first
    const [level, word] = PKStations.levelOf({ ...st, alarms });
    return { name: st.name, station: st, alarms, equipment: st.equipment, sequences: st.sequences, level, word, stationsOf };
  }

  /** The cycle and the parts of this shift, when PLC-Ki knows them (a machine without a cycle sequence or shifts has none). */
  async function loadExtras() {
    try { T.cycles = await PK.api("GET", "/api/trend/cycles"); } catch { T.cycles = null; }
    try { T.shift = await PK.api("GET", "/api/reports/shift"); } catch { T.shift = null; }
    PK.draw();
  }

  function cycleHtml(sc) {
    const c = T.cycles;
    // A station's screen shows the machine's cycle only when the cycle sequence runs in that station
    const ours = c && (!sc.station || sc.sequences.some((q) => q.name === c.sequence));
    const list = ours ? c.cycles || [] : [];
    const run = list.find((x) => x.running), last = list.find((x) => !x.running);
    const p = !sc.station && T.shift && T.shift.parts;
    const items = [
      run ? [dur((Date.now() - run.start) / 1000), PK.t("this cycle")] : null,
      last ? [dur(last.sec), PK.t("last cycle")] : null,
      p && (p.total || p.cycles) ? [String(p.total || p.cycles), p.total ? PK.t("parts this shift") : PK.t("cycles this shift")] : null,
    ].filter(Boolean);
    return items.length ? `<div class="tv-cycle">${items.map(([v, l]) => `<div><b>${esc(v)}</b><span>${esc(l)}</span></div>`).join("")}</div>` : "";
  }

  /** The step block: the sequence that is halted, else the first one. */
  function stepHtml(sc, { stopped = false } = {}) {
    const q = sc.sequences.find((x) => x.halted) || sc.sequences[0];
    if (!q) return `<div class="tv-step"></div>`;
    const s = state.status;
    const waits = q.next.flatMap((n) => n.conditions).filter((c) => c.met === false).map((c) => {
      const d = c.heldBy && s.equipment.find((e) => e.id === c.heldBy);
      return d ? (d.blockedBy ? PK.t("{text} ({name} is blocked)", { text: c.text, name: d.name }) : PK.t("{text} ({name} is in fault)", { text: c.text, name: d.name })) : c.text;
    });
    const line = stopped ? "" : q.halted ? PK.t("Halted: {why}", { why: q.haltReasons.map((x) => PK.srv(x)).join("; ") || PK.t("the outputs are off.") }) : waits.length ? PK.t("Waiting for: {what}", { what: waits.join("; ") }) : q.does || "";
    return `<div class="tv-step">
      <div class="tv-step-n">${esc(stopped ? PK.t("Last step") : PK.t("Step"))} ${esc(q.step ?? "?")}${q.timeInStep && !stopped ? `<span class="tv-for">${PK.t("for {time}", { time: esc(q.timeInStep) })}</span>` : ""}</div>
      <div class="tv-step-t">${esc(q.stepName || q.name)}</div>
      ${line ? `<div class="tv-step-w">${esc(line)}</div>` : ""}</div>`;
  }

  /** The machine picture (the Plant page's map), large, not clickable. ring: devices to mark. */
  function pictureHtml(equipment, ring, { dim = false } = {}) {
    const V = window.PKVis;
    if (!V || !equipment.length) return `<div class="tv-pic tv-none">${dim ? "" : PK.t("No devices in the model for this screen.")}</div>`;
    return `<div class="tv-pic ${dim ? "dim" : ""}" inert>${V.plantMap(equipment, null, ring)}</div>`;
  }

  /** The alarm, large: text, station, since when, first or not, what else is on, the first steps of the fix. */
  function alarmHtml(sc, a) {
    const warn = a.severity === "WARNING";
    const al = sc.alarms;
    const atStart = al.filter((x) => x.atStart).length;
    const first = al.length > 1 && al[0] === a && (!a.atStart || atStart === 1);     // as Active alarm says "First"
    const where = sc.stationsOf(a);
    const station = where.length ? where.join(", ") : PK.t("Whole machine");
    const since = a.atStart ? PK.t("On since PLC-Ki started watching") : a.since ? PK.t("Since {when} · {ago}", { when: PK.when(a.since), ago: PK.ago(a.since) }) : "";
    const others = al.filter((x) => x !== a);
    const fix = (a.fix || []).slice(0, 3);
    return `<div class="tv-al ${warn ? "warn" : "fault"}" role="alert">
      <div class="tv-al-top"><span class="tv-al-sev">${esc(warn ? PK.t("Warning") : PK.t("Fault"))} · ${esc(a.id)}</span>${first ? `<span class="tv-al-first">${PK.t("First alarm")}</span>` : ""}</div>
      <div class="tv-al-text" role="heading" aria-level="2">${esc(a.text)}</div>
      <div class="tv-al-facts"><span>${icon("plant")}${esc(station)}</span>${since ? `<span>${icon("clock")}${esc(since)}</span>` : ""}</div>
      ${a.condition ? `<p class="tv-al-cond">${esc(a.condition)}</p>` : ""}
      ${others.length ? `<div class="tv-al-more"><b>${PK.t("Also on:")}</b> ${others.slice(0, 4).map((x) => `<span class="tv-al-chip ${x.severity === "WARNING" ? "warn" : ""}">${esc(x.id)} ${esc(x.text.length > 44 ? x.text.slice(0, 42) + "…" : x.text)}</span>`).join("")}${others.length > 4 ? ` <span>${PK.t("and {n} more", { n: others.length - 4 })}</span>` : ""}</div>` : ""}
      ${fix.length ? `<div class="tv-al-fix"><b>${PK.t("What to do")}</b><ol>${fix.map((f) => `<li>${esc(f)}</li>`).join("")}</ol></div>` : ""}
    </div>`;
  }

  function stopHtml(stop) {
    return `<div class="tv-al fault" role="alert">
      <div class="tv-al-top"><span class="tv-al-sev">${PK.t("PLC")} · ${esc(stop.word || "STOP")}</span></div>
      <div class="tv-al-text" role="heading" aria-level="2">${PK.t("The PLC is in {word}", { word: esc(stop.word || "STOP") })}</div>
      <div class="tv-al-facts"><span>${icon("clock")}${esc((PK.cpuSince(stop) || "").replace(/^s/, "S"))}</span></div>
      <p class="tv-al-cond">${PK.t("The program is not running. What the screen shows is its last state.")}</p></div>`;
  }

  /** The whole screen: the band on top (state, step, cycle, clock), the body under it, the quiet controls in a corner. */
  function frame({ level, word, title, step = "", cycle = "", body, alarm = false }) {
    const now = Date.now();
    const [x, y] = shift(now);
    const c = state.status && state.status.connection;
    const test = c && c.state === "snapshot";
    const scr = PK.prefs.get("screen", {}) || {};
    const ua = navigator.userActivation;
    const needClick = scr.sound && ua && !ua.hasBeenActive;     // a browser plays no sound before the first click on the page
    const full = document.fullscreenEnabled && !document.fullscreenElement;
    return `<div class="tv ${alarm ? "tv-alarm" : ""}"><div class="tv-in" style="transform:translate(${x}px,${y}px)">
      <header class="tv-band lv-${esc(level)}">
        <div class="tv-state"><span class="tv-name">${esc(title)}</span><h1 class="tv-word">${esc(word)}</h1></div>
        ${step}${cycle}
        <div class="tv-clock"><b>${esc(PK.hm(now))}</b><span>${esc(PK.weekdayDay(now))}</span></div>
      </header>
      <div class="tv-body">${body}</div>
      <div class="tv-corner">
        ${test ? `<span class="tv-test" title="${PK.t("The values come from a test file, not from the machine")}">${PK.t("Test values")}</span>` : ""}
        ${needClick ? `<span class="tv-hint">${PK.t("Click once so alarms can beep")}</span>` : ""}
        ${full ? `<button type="button" class="tv-btn" data-tv-full title="${PK.t("Full screen")}" aria-label="${PK.t("Full screen")}">${icon("expand")}</button>` : ""}
        <button type="button" class="tv-btn" data-tv-exit title="${PK.t("Leave the line screen (Esc)")}" aria-label="${PK.t("Leave the line screen")}">${icon("x")}</button>
      </div></div></div>`;
  }

  function render(params) {
    const s = state.status;
    const param = params && params[0];
    if (!s) return frame({ level: "idle", word: PK.t("Connecting"), title: "PLC-Ki", body: `<div class="tv-msg">${PK.t("Waiting for the first values from PLC-Ki.")}</div>` });
    const sc = scopeOf(param, s);
    if (!sc) return frame({ level: "idle", word: PK.t("No such station"), title: s.machine || "PLC-Ki",
      body: `<div class="tv-msg">${PK.t("There is no station called “{name}” in this machine now. {link}", { name: esc(param), link: `<a href="#/tv">${PK.t("Show the whole machine")}</a>` })}</div>` });
    const title = sc.station ? `${s.machine || PK.t("Machine")} · ${sc.name}` : sc.name;
    const conn = s.connection || {};

    // Values that are not live are said on the screen itself: the banner and the top bar are hidden here
    if (Date.now() - state.lastOk > STALE_MS) {
      return frame({ level: "fault", word: PK.t("No data"), title, step: stepHtml(sc), body: `<div class="tv-split">
        <div class="tv-al fault" role="alert"><div class="tv-al-text" role="heading" aria-level="2">${PK.t("PLC-Ki is not answering")}</div><p class="tv-al-cond">${PK.t("The values are frozen since {time}. Check that the PLC-Ki PC is on and on the network.", { time: esc(PK.clock(state.lastOk)) })}</p></div>
        ${pictureHtml(sc.equipment, [], { dim: true })}</div>` });
    }
    if (["disconnected", "connecting", "waiting", "notset"].includes(conn.state)) {
      return frame({ level: "warn", word: PK.t("No PLC connection"), title, step: stepHtml(sc), body: `<div class="tv-split">
        <div class="tv-al warn" role="alert"><div class="tv-al-text" role="heading" aria-level="2">${PK.t("PLC-Ki cannot read the PLC")}</div><p class="tv-al-cond">${conn.lastGoodRead ? PK.t("The values are from the last good read at {time}.", { time: esc(PK.clock(conn.lastGoodRead)) }) : PK.t("There are no values yet.")}</p></div>
        ${pictureHtml(sc.equipment, [], { dim: true })}</div>` });
    }
    const stop = PK.cpuStop(s);
    if (stop) {
      return frame({ level: "fault", word: stop.word || "STOP", title, step: stepHtml(sc, { stopped: true }), alarm: true,
        body: `<div class="tv-split">${stopHtml(stop)}${pictureHtml(sc.equipment, [])}</div>` });
    }
    if (sc.alarms.length) {
      // The same alarm Active alarm opens on: the first that came (the server sends them in that order)
      const a = sc.alarms[0];
      const ring = sc.equipment.filter((e) => PK.deviceAlarms(e, s).includes(a)).map((e) => e.id);
      const shown = !sc.station && ring.length && window.PKVis ? PKVis.around(ring, 12) : sc.equipment;
      return frame({ level: sc.level, word: sc.word, title, step: stepHtml(sc), cycle: cycleHtml(sc), alarm: true,
        body: `<div class="tv-split">${alarmHtml(sc, a)}${pictureHtml(shown, ring)}</div>` });
    }
    return frame({ level: sc.level, word: sc.word, title, step: stepHtml(sc), cycle: cycleHtml(sc), body: pictureHtml(sc.equipment, []) });
  }

  /** Where the × goes: the page the screen was opened from, else the station's page or Active alarm. */
  function exit() {
    const back = PK.current && PK.current.back;
    const param = PK.current && PK.current.params[0];
    const sc = param && state.status ? scopeOf(param, state.status) : null;
    PK.go(back && back.hash && !/^#\/tv(?=[/?]|$)/.test(back.hash) ? back.hash : sc && sc.station ? PKStations.href(sc.station.key) : "#/alarm");
  }

  function stopTimers() {
    clearInterval(T.tick); clearInterval(T.extras); clearTimeout(T.idle);
    T.tick = T.extras = T.idle = null;
    if (T.keys) document.removeEventListener("keydown", T.keys);
    if (T.move) document.removeEventListener("pointermove", T.move);
    T.keys = T.move = null;
    document.documentElement.classList.remove("tv-moved");
  }

  PK.view("tv", {
    live: true,
    mount() {
      stopTimers();
      PK.tvMode(true);
      // Its own second: the clock and the cycle go on, and a status request that hangs shows "No data" instead of a frozen "Running"
      T.tick = setInterval(PK.draw, 1000);
      loadExtras();
      T.extras = setInterval(loadExtras, 30000);
      T.keys = (e) => {
        if (e.key !== "Escape" || e.defaultPrevented || document.querySelector(".sheet-wrap")) return;
        e.preventDefault(); exit();
      };
      document.addEventListener("keydown", T.keys);
      // The corner controls and the pointer show while the mouse moves, and fade 3 s after
      T.move = () => {
        document.documentElement.classList.add("tv-moved");
        clearTimeout(T.idle);
        T.idle = setTimeout(() => document.documentElement.classList.remove("tv-moved"), 3000);
      };
      document.addEventListener("pointermove", T.move);
      PK.draw();
    },
    render,
    after(page) {
      const x = page.querySelector("[data-tv-exit]");
      if (x) x.onclick = exit;
      const f = page.querySelector("[data-tv-full]");
      if (f) f.onclick = () => { document.documentElement.requestFullscreen().then(PK.draw).catch(() => {}); };
    },
    leave() {
      stopTimers();
      // #/tv to #/tv/<station> stays a line screen: only switch the frame back once another page is open
      setTimeout(() => {
        if (PK.current && PK.current.name === "tv") return;
        PK.tvMode(false);
        if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
      }, 0);
    },
  });
})();
