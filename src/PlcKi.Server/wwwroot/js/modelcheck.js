// How well the machine model fits the live machine (Setup > Program), and correcting one model entry with Ki.
// The check only watches what PLC-Ki already reads; saving a corrected entry writes the model file and restarts PLC-Ki.
const PKModel = (() => {
  const { api, esc, icon, toast, AI } = PK;
  const mc = { report: null, error: null, timer: null };

  async function load() {
    try { mc.report = await api("GET", "/api/model-check"); mc.error = null; }
    catch (e) { mc.error = e.message; }
    PK.draw();
  }
  function start() { stop(); load(); mc.timer = setInterval(load, 10000); }
  function stop() { if (mc.timer) clearInterval(mc.timer); mc.timer = null; }

  const mins = (ms) => {
    const s = Math.round(ms / 1000);
    return s < 90 ? `${s} s` : s < 5400 ? `${Math.round(s / 60)} min` : `${(s / 3600).toFixed(1)} h`;
  };
  // "00:03:12.5" (TimeSpan) -> ms
  const spanMs = (t) => {
    if (typeof t === "number") return t;
    const m = /^(?:(\d+)\.)?(\d+):(\d+):(\d+(?:\.\d+)?)$/.exec(t || "");
    return m ? (((+(m[1] || 0) * 24 + +m[2]) * 60 + +m[3]) * 60 + +m[4]) * 1000 : 0;
  };
  const targetName = (t) => {
    const [kind, id] = [t.slice(0, t.indexOf(":")), t.slice(t.indexOf(":") + 1)];
    return { equipment: PK.t("Device"), sequence: PK.t("Sequence"), alarm: PK.t("Alarm"), setting: PK.t("Setting"), tag: PK.t("Tag") }[kind] + " " + id;
  };

  /** The card on Setup > Program. */
  function html() {
    const r = mc.report;
    if (mc.error) return `<div class="mb-5 card"><p class="m-0 muted">${PK.t("Model check: {error}", { error: esc(PK.srv(mc.error)) })}</p></div>`;
    if (!r) return "";
    const watched = spanMs(r.watched);
    const wrong = r.findings.filter((f) => f.severity === "wrong").length;
    const head = !r.reads ? PK.t("Waiting for live values from the PLC.")
      : r.findings.length === 0 ? PK.t("Nothing found in {time} of watching ({count} step changes).", { time: mins(watched), count: r.stepChanges })
      : wrong ? PK.t("{n} thing the model does not explain, {wrong} clearly wrong, in {time} of watching.|{n} things the model does not explain, {wrong} clearly wrong, in {time} of watching.", { n: r.findings.length, wrong, time: mins(watched) })
      : PK.t("{n} thing the model does not explain, in {time} of watching.|{n} things the model does not explain, in {time} of watching.", { n: r.findings.length, time: mins(watched) });
    const list = r.findings.map((f, i) => `<li class="mc-item">
        <span class="pill ${f.severity === "wrong" ? "bad" : "warn"}"><span class="dot"></span>${f.severity === "wrong" ? PK.t("Wrong") : PK.t("Check")}</span>
        <div class="grow min0"><b class="semibold">${esc(PK.srv(f.title))}</b>
          <div class="muted small">${esc(PK.srv(f.detail))}</div>
          <div class="faint small">${esc(PK.t("{target} · seen {count}× · last {when}", { target: targetName(f.target), count: f.count, when: PK.ago ? PK.ago(f.lastSeen) : f.lastSeen }))}</div></div>
        ${f.target.startsWith("tag:") ? "" : `<button class="btn small" data-mcfix="${i}">${PK.t("Fix this")}</button>`}</li>`).join("");
    return `<div class="mb-5 card stack">
      <div class="row"><span class="ico">${icon("check")}</span><h3 class="m-0 grow">${PK.t("Does the model match the machine?")}</h3>
        <button class="btn gray small" data-mcreset ${r.reads ? "" : "disabled"}>${PK.t("Start over")}</button></div>
      <p class="m-0 muted">${PK.t("PLC-Ki compares the model with what the machine does while it runs: outputs that switch on although the model says an interlock is missing, devices shown blocked while the machine keeps going, steps and step changes the model does not know. Let the machine run a few cycles, then look here.")}</p>
      <p class="m-0"><b>${esc(head)}</b></p>
      ${r.seen && r.seen.length ? `<p class="m-0 faint small">${r.seen.map((x) => esc(PK.srv(x))).join(" · ")}</p>` : ""}
      ${list ? `<ul class="list mc-list">${list}</ul>` : ""}
    </div>`;
  }

  function bind(page) {
    page.querySelectorAll("[data-mcfix]").forEach((b) => b.onclick = () => {
      const f = mc.report.findings[+b.dataset.mcfix];
      fixSheet(f.target, f);
    });
    const rs = page.querySelector("[data-mcreset]");
    if (rs) rs.onclick = async () => { try { await api("POST", "/api/admin/model/check/reset"); await load(); } catch (e) { toast(e.message, "error"); } };
  }

  /** Correct one entry: what is wrong (optional), Ki's proposal, the JSON, Save (restarts PLC-Ki). */
  async function fixSheet(target, finding) {
    let entry;
    try { entry = await api("GET", "/api/admin/model/entry?target=" + encodeURIComponent(target)); }
    catch (e) { toast(e.message, "error"); return; }
    let newTags = "[]";
    const s = PK.sheet(`<h2>${PK.t("Correct {what}", { what: esc(targetName(target)) })}</h2>
      ${finding ? `<div class="notice gold">${icon("warn")}<div><b>${esc(PK.srv(finding.title))}</b><br><span class="small">${esc(PK.srv(finding.detail))}</span></div></div>` : ""}
      ${entry.findings && entry.findings.length > (finding ? 1 : 0) ? `<p class="m-0 small muted">${PK.t("Also seen for this entry: {list}", { list: entry.findings.filter((x) => !finding || x.title !== finding.title).map((x) => esc(PK.srv(x.title))).join("; ") })}</p>` : ""}
      <label class="field">${PK.t("What is wrong? (optional)")}<textarea id="mf-note" rows="2" placeholder="${PK.t("For example: the robot is not blocked, it just finished its job.")}"></textarea>
        <span class="hint">${PK.t("{ai} reads the program part this entry is about and what the machine did, and proposes a corrected entry. Nothing is saved until you press Save.", { ai: AI })}</span></label>
      <div class="row"><button class="btn" data-mfask ${entry.canAsk ? "" : "disabled"}>${icon("spark")}<span>${PK.t("Ask {ai} to correct it", { ai: AI })}</span></button>
        ${entry.canAsk ? "" : `<span class="faint small">${PK.t("{ai} needs a Claude key (Setup, Settings).", { ai: AI })}</span>`}<span class="faint small" id="mf-wait"></span></div>
      <div id="mf-why"></div>
      <details class="more" ${entry.canAsk ? "" : "open"}><summary>${PK.t("The entry in the model (JSON)")}</summary>
        <textarea id="mf-json" rows="14" spellcheck="false" style="font-family:var(--mono);font-size:var(--fs-1);width:100%;margin-top:10px">${esc(entry.json)}</textarea></details>
      <p class="m-0 faint small">${PK.t("Saving keeps a copy of the current model next to it, then restarts PLC-Ki so the pages use the corrected model.")}</p>
      <div class="row end"><button class="btn gray" data-close>${PK.t("Cancel")}</button><button class="btn" data-mfsave>${PK.t("Save and restart")}</button></div>`);
    const $ = (sel) => s.el.querySelector(sel);
    $("[data-mfask]").onclick = async () => {
      const b = $("[data-mfask]");
      b.disabled = true; $("#mf-wait").textContent = PK.t("{ai} is reading the program… (up to a minute)", { ai: AI });
      try {
        const r = await api("POST", "/api/admin/model/entry/ask", { target, note: $("#mf-note").value.trim() || null, json: null });
        $("#mf-json").value = r.json;
        newTags = r.newTags || "[]";
        const nt = JSON.parse(newTags);
        $("#mf-why").innerHTML = `<div class="notice ${r.problems && r.problems.length ? "gold" : "blue"}">${icon("info")}<div>${esc(PK.srv(r.why || "") || PK.t("Proposal ready."))}
          ${nt.length ? `<br><span class="small">${PK.t("Adds {n} tag|Adds {n} tags", { n: nt.length })}: ${nt.map((t) => esc(t.id)).join(", ")}</span>` : ""}
          ${r.problems && r.problems.length ? `<br><span class="small"><b>${PK.t("Still not right:")}</b> ${r.problems.map((x) => esc(PK.srv(x))).join("; ")}</span>` : ""}</div></div>`;
        s.el.querySelector("details.more").open = true;
        $("#mf-wait").textContent = "";
      } catch (e) { $("#mf-wait").textContent = ""; toast(e.message, "error"); }
      b.disabled = false;
    };
    $("[data-mfsave]").onclick = async () => {
      try {
        await api("POST", "/api/admin/model/entry", { target, json: $("#mf-json").value, newTags });
        s.close();
        toast(PK.t("Saved. Restarting PLC-Ki…"));
        PKSetup.restart();
      } catch (e) { toast(e.message, "error"); }
    };
  }

  return { html, bind, start, stop, load, fixSheet };
})();
