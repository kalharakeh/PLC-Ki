// "Ask about the machine": a panel that slides in over any page. Claude answers from the live values, the
// recent history and the program model (/api/ask); the server keeps the conversation, the page only its id.
"use strict";

(() => {
  const { $, esc } = PK;
  const log = $("ask-log"), form = $("ask-form"), input = $("ask-input"), send = $("ask-send"), chips = $("ask-examples"), newBtn = $("ask-new");
  let conversationId = null;
  let busy = false;
  let abort = null;
  const KEY = "plcki.ask";

  // The conversation lives on the server; this tab keeps its id and what was shown, so a reload keeps it.
  function save() {
    try { sessionStorage.setItem(KEY, JSON.stringify({ id: conversationId, html: [...log.children].slice(1).filter((m) => !m.classList.contains("pending")).map((m) => m.outerHTML).join("") })); } catch { /* private window */ }
  }
  function restore() {
    try {
      const s = JSON.parse(sessionStorage.getItem(KEY) || "null");
      if (!s || !s.html) return;
      conversationId = s.id;
      log.insertAdjacentHTML("beforeend", s.html);
      newBtn.classList.remove("hidden");
    } catch { /* nothing kept */ }
  }

  function add(cls, html) {
    const div = document.createElement("div");
    div.className = "msg " + cls;
    div.innerHTML = html;
    log.appendChild(div);
    div.scrollIntoView({ block: "nearest", behavior: "smooth" });
    return div;
  }

  function setBusy(b) {
    busy = b;
    send.disabled = b;
    chips.querySelectorAll("button").forEach((c) => { c.disabled = b; });
  }

  /** "Looked at" items, as links where PLC-Ki has a page for them. */
  function lookedAt(steps) {
    const seen = new Set();
    return steps.map((s) => s.label).filter((l) => !seen.has(l) && seen.add(l)).map((l) => {
      let href = null;
      const m = /^alarm (\S+)/.exec(l);
      if (m) href = "#/alarms/" + encodeURIComponent(m[1]);
      else if (/^(device|sequence|live status)/.test(l)) href = "#/plant";
      else if (/^recent events/.test(l)) href = "#/alarms";
      return href ? `<a href="${href}">${esc(PK.srv(l))}</a>` : esc(PK.srv(l));
    }).join(" · ");
  }

  async function ask(question) {
    question = question.trim();
    if (!question || busy) return;
    if (PK.state.ask && !PK.state.ask.enabled) return;
    add("q", esc(question));
    input.value = "";
    setBusy(true);
    const t0 = Date.now();
    const pending = add("a pending", `<span class="pend-text">${PK.t("Looking at the machine")}</span> <span class="faint small pend-time"></span> <button type="button" class="linkbtn small" data-cancel>${PK.t("Cancel")}</button>`);
    const timer = setInterval(() => { pending.querySelector(".pend-time").textContent = Math.round((Date.now() - t0) / 1000) + " s"; }, 1000);
    abort = new AbortController();
    pending.querySelector("[data-cancel]").onclick = () => abort && abort.abort();
    try {
      const r = await fetch("/api/ask", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ conversationId, question, language: PK.lang === "de" ? "de" : null }), signal: abort.signal });
      const data = await r.json().catch(() => ({}));
      pending.remove();
      if (!r.ok) { add("err", esc(data.error ? PK.srv(data.error) : PK.t("Request failed ({status}).", { status: r.status }))); return; }
      conversationId = data.conversationId;
      newBtn.classList.remove("hidden");
      const steps = data.steps || [];
      const meta = (steps.length ? PK.t("Looked at") + ": " + lookedAt(steps) : PK.t("Answered from the live summary")) + ` · ${esc(String(data.seconds))} s`;
      add("a", `<div class="ans">${PK.md(I18N.fixed(data.answer))}</div>` + (data.note ? `<div class="meta">${esc(PK.srv(data.note))}</div>` : "")
        + `<div class="meta">${meta} · <button type="button" class="linkbtn" data-copy-ans>${PK.t("Copy")}</button></div>`);
    } catch (e) {
      pending.remove();
      add("err", e && e.name === "AbortError" ? PK.t("Stopped. Ask again when you are ready.") : PK.t("Cannot reach PLC-Ki. Is it still running?"));
    } finally {
      clearInterval(timer);
      abort = null;
      setBusy(false);
      save();
      input.focus();
    }
  }

  /** Example questions that fit the page and the alarm on screen, so the first question is one tap. */
  function suggestions() {
    const cur = PK.current || { name: "alarm", params: [] };
    const st = PK.state.status;
    const alarm = st && st.activeAlarms && st.activeAlarms[0];
    const block = cur.params && cur.params[0];
    const list = {
      alarm: alarm ? [PK.t("Why is alarm {id} on, and how do I fix it?", { id: alarm.id }), PK.t("What should I check first?"), PK.t("What happened just before the alarm?")]
        : [PK.t("What is the machine doing right now?"), PK.t("Is anything close to raising an alarm?"), PK.t("What happened in the last 5 minutes?")],
      plant: [PK.t("Why is the sequence not going on?"), PK.t("Which devices are not running, and why?"), PK.t("What happened in the last 5 minutes?")],
      alarms: [alarm ? PK.t("Explain alarm {id} in plain words.", { id: alarm.id }) : PK.t("Which alarms stop the machine?"), PK.t("Which alarms come from the same device?"), PK.t("How do I reset an alarm?")],
      program: block ? [PK.t("Explain {block} in plain words.", { block }), PK.t("What stops the output in {block} right now?", { block }), PK.t("Where is {block} called from?", { block })] : [PK.t("Which block runs the sequence?"), PK.t("Where are the interlocks programmed?"), PK.t("What is the machine doing right now?")],
    }[cur.name] || [PK.t("What is the machine doing right now?"), PK.t("Why is the alarm on and how do I fix it?"), PK.t("What happened in the last 5 minutes?")];
    chips.innerHTML = list.map((q) => `<button type="button">${PK.esc(q)}</button>`).join("");
    chips.querySelectorAll("button").forEach((c) => { c.disabled = busy; });
  }

  function open(question) {
    // Public view: asking spends Claude credit, so it needs a login (the server refuses it too)
    if (!PK.state.user) { PK.toast(PK.t("Log in to ask about the machine.")); return; }
    if (PK.state.ask) showOff(PK.state.ask);   // the role is known by now
    suggestions();
    $("ask").classList.add("open");
    $("scrim").classList.add("on");
    // Ask off: no question box to type in, so focus goes to the one thing to do here ("Add the key", or Done)
    const off = PK.state.ask && !PK.state.ask.enabled;
    if (off) setTimeout(() => ($("ask-off").querySelector("[data-add-key]") || $("ask-close")).focus(), 250);
    else if (question) ask(question); else setTimeout(() => input.focus(), 250);
  }
  function close() {
    $("ask").classList.remove("open");
    $("scrim").classList.remove("on");
  }

  $("ask-open").onclick = () => open();
  $("ask-close").onclick = close;
  $("scrim").onclick = close;
  document.addEventListener("keydown", (e) => { if (e.key === "Escape") close(); });
  form.addEventListener("submit", (e) => { e.preventDefault(); ask(input.value); });
  input.addEventListener("keydown", (e) => { if (e.key === "Enter" && !e.shiftKey && !e.isComposing) { e.preventDefault(); ask(input.value); } });
  chips.addEventListener("click", (e) => { const b = e.target.closest("button"); if (b) ask(b.textContent); });
  // A link to a page of PLC-Ki in an answer: close the panel so the page shows.
  log.addEventListener("click", (e) => { const a = e.target.closest("a[href^='#/']"); if (a) close(); });
  log.addEventListener("click", async (e) => {
    const c = e.target.closest("[data-copy-ans]");
    if (!c) return;
    const text = c.closest(".msg").querySelector(".ans").innerText;
    try { await navigator.clipboard.writeText(text); c.textContent = PK.t("Copied"); setTimeout(() => { c.textContent = PK.t("Copy"); }, 1600); }
    catch { const r = document.createRange(); r.selectNodeContents(c.closest(".msg").querySelector(".ans")); getSelection().removeAllRanges(); getSelection().addRange(r); PK.toast(PK.t("Selected. Press Ctrl+C to copy.")); }
  });
  newBtn.addEventListener("click", async () => {
    if (busy) return;
    if (log.children.length > 1 && !(await PK.confirmSheet({ title: PK.t("Start a new conversation?"), text: PK.t("The questions and answers above are cleared."), ok: PK.t("Start new") }))) return;
    if (conversationId) fetch(`/api/ask/${encodeURIComponent(conversationId)}`, { method: "DELETE" }).catch(() => {});
    conversationId = null;
    while (log.children.length > 1) log.lastElementChild.remove();
    newBtn.classList.add("hidden");
    save();
    input.focus();
  });

  /** Ask without a Claude key: say so once, with the one fix, and hide the Ask buttons on the pages. */
  function showOff(s) {
    PK.state.ask = s;
    document.documentElement.classList.toggle("ask-off", !s.enabled);
    if (s.enabled) return;
    const admin = PK.can("settings");
    const off = $("ask-off");
    const fix = s.code === "nokey"
      ? (admin ? PK.t("Add the key under Setup, Settings (Assistant) and save it, then press Apply now. Ask works after that.") : PK.t("Ask an administrator to add a Claude key to PLC-Ki."))
      : (admin ? "" : PK.t("Ask an administrator if you need it."));
    off.innerHTML = `<div><b>${PK.t("Ask is off.")}</b> ${esc(s.reason ? PK.srv(s.reason) : PK.t("The assistant is not available."))} ${esc(fix)}</div>`
      + (admin && s.code === "nokey" ? `<div class="mt-2 row"><a class="btn small" href="#/setup/settings" data-add-key>${PK.t("Add the key")}</a></div>` : "");
    off.classList.remove("hidden");
    const k = off.querySelector("[data-add-key]"); if (k) k.onclick = () => close();
    form.classList.add("hidden");
    chips.classList.add("hidden");
  }
  fetch("/api/ask").then((r) => r.json()).then(showOff).catch(() => {});
  document.addEventListener("pk:lang", () => { if (PK.state.ask) showOff(PK.state.ask); });
  restore();

  window.PKAsk = { open, close };
})();
