// "Ask about the machine": sends the question to /api/ask, where Claude answers from the live values
// and the machine model. The server keeps the conversation; the page only remembers its id.
"use strict";

(() => {
  const $ = (id) => document.getElementById(id);
  const log = $("ask-log");
  const form = $("ask-form");
  const input = $("ask-input");
  const send = $("ask-send");
  const chips = $("ask-examples");
  const newBtn = $("ask-new");
  let conversationId = null;
  let busy = false;

  const esc = (s) => String(s ?? "").replace(/[&<>"']/g,
    (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  // The answers use a tiny part of Markdown: **bold**, `code`, "- " and "1. " lists, blank lines between paragraphs.
  function render(text) {
    const inline = (s) => esc(s)
      .replace(/\*\*(.+?)\*\*/g, "<strong>$1</strong>")
      .replace(/`([^`]+)`/g, "<code>$1</code>");
    const out = [];
    let list = null;
    let para = [];
    const flushPara = () => { if (para.length) { out.push("<p>" + para.map(inline).join("<br>") + "</p>"); para = []; } };
    const flushList = () => { if (list) { out.push(`<${list.tag}>` + list.items.map((i) => `<li>${inline(i)}</li>`).join("") + `</${list.tag}>`); list = null; } };
    for (const raw of String(text).split(/\r?\n/)) {
      const line = raw.trimEnd();
      const bullet = line.match(/^\s*[-*•]\s+(.*)$/);
      const num = line.match(/^\s*\d+[.)]\s+(.*)$/);
      if (bullet || num) {
        flushPara();
        const tag = bullet ? "ul" : "ol";
        if (!list || list.tag !== tag) { flushList(); list = { tag, items: [] }; }
        list.items.push((bullet || num)[1]);
      } else if (line.trim() === "") {
        flushPara(); flushList();
      } else if (list && /^\s{2,}/.test(raw)) {
        list.items[list.items.length - 1] += " " + line.trim();   // continuation of a list item
      } else {
        flushList();
        para.push(line.replace(/^#+\s*/, ""));
      }
    }
    flushPara(); flushList();
    return out.join("");
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
    send.textContent = b ? "…" : "Ask";
  }

  async function ask(question) {
    question = question.trim();
    if (!question || busy) return;
    add("q", esc(question));
    input.value = "";
    setBusy(true);
    const pending = add("a pending", "Looking at the machine");
    try {
      const r = await fetch("/api/ask", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ conversationId, question }),
      });
      const data = await r.json().catch(() => ({}));
      pending.remove();
      if (!r.ok) {
        add("err", esc(data.error || `Request failed (${r.status}).`));
        return;
      }
      conversationId = data.conversationId;
      newBtn.classList.remove("hidden");
      const looked = (data.steps || []).map((s) => s.label);
      const meta = [
        looked.length ? "Looked at: " + [...new Set(looked)].join(" · ") : "Answered from the live summary",
        `${data.seconds} s`,
      ].join(" · ");
      add("a", render(data.answer)
        + (data.note ? `<div class="note">${esc(data.note)}</div>` : "")
        + `<div class="meta">${esc(meta)}</div>`);
    } catch (e) {
      pending.remove();
      add("err", "Cannot reach PLC-Ki. Is it still running?");
    } finally {
      setBusy(false);
      input.focus();
    }
  }

  form.addEventListener("submit", (e) => { e.preventDefault(); ask(input.value); });
  input.addEventListener("keydown", (e) => {
    if (e.key === "Enter" && !e.shiftKey && !e.isComposing) { e.preventDefault(); ask(input.value); }
  });
  chips.addEventListener("click", (e) => {
    const b = e.target.closest("button.chip");
    if (b) ask(b.textContent);
  });
  newBtn.addEventListener("click", () => {
    if (busy) return;
    if (conversationId) fetch(`/api/ask/${encodeURIComponent(conversationId)}`, { method: "DELETE" }).catch(() => {});
    conversationId = null;
    log.innerHTML = "";
    newBtn.classList.add("hidden");
    input.focus();
  });

  // Is the assistant available (API key set)?
  fetch("/api/ask").then((r) => r.json()).then((s) => {
    if (s.enabled) return;
    $("ask-off").textContent = s.reason || "The assistant is not available.";
    $("ask-off").classList.remove("hidden");
    form.classList.add("hidden");
    chips.classList.add("hidden");
  }).catch(() => {});
})();
