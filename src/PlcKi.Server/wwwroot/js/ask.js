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

  function open(question) {
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
    $("ask-off").textContent = s.reason || "The assistant is not available.";
    $("ask-off").classList.remove("hidden");
    form.classList.add("hidden");
    chips.classList.add("hidden");
  }).catch(() => {});

  window.PKAsk = { open, close };
})();
