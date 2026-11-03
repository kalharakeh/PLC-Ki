// "Ask about the machine": a panel that slides in over any page. Claude answers from the live values, the
// recent history and the program model (/api/ask); the server keeps the conversation, the page only its id.
"use strict";

(() => {
  const { $, esc } = PK;
  const log = $("ask-log"), form = $("ask-form"), input = $("ask-input"), send = $("ask-send"), chips = $("ask-examples"), newBtn = $("ask-new");
  let conversationId = null;
  let busy = false;

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

  async function ask(question) {
    question = question.trim();
    if (!question || busy) return;
    add("q", esc(question));
    input.value = "";
    setBusy(true);
    const pending = add("a pending", "Looking at the machine");
    try {
      const r = await fetch("/api/ask", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ conversationId, question }) });
      const data = await r.json().catch(() => ({}));
      pending.remove();
      if (!r.ok) { add("err", esc(data.error || `Request failed (${r.status}).`)); return; }
      conversationId = data.conversationId;
      newBtn.classList.remove("hidden");
      const looked = [...new Set((data.steps || []).map((s) => s.label))];
      const meta = [looked.length ? "Looked at: " + looked.join(" · ") : "Answered from the live summary", `${data.seconds} s`].join(" · ");
      add("a", PK.md(data.answer) + (data.note ? `<div class="meta">${esc(data.note)}</div>` : "") + `<div class="meta">${esc(meta)}</div>`);
    } catch {
      pending.remove();
      add("err", "Cannot reach PLC-Ki. Is it still running?");
    } finally {
      setBusy(false);
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
      alarm: alarm ? [`Why is alarm ${alarm.id} on, and how do I fix it?`, "What should I check first?", "What happened just before the alarm?"]
        : ["What is the machine doing right now?", "Is anything close to raising an alarm?", "What happened in the last 5 minutes?"],
      plant: ["Why is the sequence not going on?", "Which devices are not running, and why?", "What happened in the last 5 minutes?"],
      alarms: [alarm ? `Explain alarm ${alarm.id} in plain words.` : "Which alarms stop the machine?", "Which alarms come from the same device?", "How do I reset an alarm?"],
      logic: block ? [`Explain ${block} in plain words.`, `What stops the output in ${block} right now?`, `Where is ${block} called from?`] : ["Which block runs the sequence?", "Where are the interlocks programmed?", "What is the machine doing right now?"],
      code: block ? [`Explain ${block} in plain words.`, `What are the inputs and outputs of ${block}?`, `Where is ${block} called from?`] : ["Which block runs the sequence?", "Where are the interlocks programmed?", "What is the machine doing right now?"],
    }[cur.name] || ["What is the machine doing right now?", "Why is the alarm on and how do I fix it?", "What happened in the last 5 minutes?"];
    chips.innerHTML = list.map((q) => `<button type="button">${PK.esc(q)}</button>`).join("");
    chips.querySelectorAll("button").forEach((c) => { c.disabled = busy; });
  }

  function open(question) {
    suggestions();
    $("ask").classList.add("open");
    $("scrim").classList.add("on");
    if (question) ask(question); else setTimeout(() => input.focus(), 250);
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
  newBtn.addEventListener("click", () => {
    if (busy) return;
    if (conversationId) fetch(`/api/ask/${encodeURIComponent(conversationId)}`, { method: "DELETE" }).catch(() => {});
    conversationId = null;
    log.innerHTML = "";
    newBtn.classList.add("hidden");
    input.focus();
  });

  fetch("/api/ask").then((r) => r.json()).then((s) => {
    if (s.enabled) return;
    $("ask-off").textContent = (s.reason || "The assistant is not available.") + " An administrator turns it on under Setup, Get started (step Ask).";
    $("ask-off").classList.remove("hidden");
    form.classList.add("hidden");
    chips.classList.add("hidden");
  }).catch(() => {});

  window.PKAsk = { open, close };
})();
