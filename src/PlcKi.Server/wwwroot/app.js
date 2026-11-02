// PLC-Ki live status page. Polls /api/status once per second; no external libraries,
// so it also works on a plant PC without internet.
"use strict";

const $ = (id) => document.getElementById(id);
let model = null;

function esc(s) {
  return String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

async function loadModel() {
  try {
    const r = await fetch("/api/model");
    if (r.ok) model = await r.json();
  } catch { /* retried on next poll */ }
}

function renderConnection(c, time) {
  const pill = $("conn");
  const map = {
    connected: ["connected to " + c.address, "pill-ok"],
    snapshot: ["test data: " + c.address, "pill-info"],
    connecting: ["connecting to " + c.address, "pill-wait"],
    disconnected: ["not connected", "pill-bad"],
  };
  const [text, cls] = map[c.state] || [c.state, "pill-wait"];
  pill.textContent = text;
  pill.className = "pill " + cls;
  pill.title = c.error || "";
  const t = c.lastGoodRead ? new Date(c.lastGoodRead) : null;
  $("updated").textContent = t ? "last read " + t.toLocaleTimeString() : "";
}

function renderSummary(lines) {
  $("summary").innerHTML = lines.map((l) => `<li>${esc(l)}</li>`).join("");
}

function renderOverview(values) {
  $("overview").innerHTML = values.map((v) =>
    `<div class="tile"><div class="label">${esc(v.label)}</div><div class="value">${esc(v.text)}</div></div>`).join("");
}

function condList(conds) {
  return `<ul class="conds">${conds.map((c) => {
    const [cls, sym] = c.met === true ? ["yes", "✓"] : c.met === false ? ["no", "✗"] : ["unk", "?"];
    const now = c.values.length ? `<span class="now">now ${c.values.map((v) => esc(v.label) + " " + esc(v.text)).join(", ")}</span>` : "";
    return `<li><span class="mark ${cls}" aria-label="${cls === "yes" ? "met" : cls === "no" ? "not met" : "unknown"}">${sym}</span><span>${esc(c.text)} ${now}</span></li>`;
  }).join("")}</ul>`;
}

function renderSequences(seqs) {
  $("sequences").innerHTML = seqs.map((s) => {
    const def = model?.sequences?.find((d) => d.id === s.id);
    const chain = def ? `<ol class="steps">${def.steps.map((st) =>
      `<li class="${st.no === s.step ? "current" + (s.halted ? " halted" : "") : ""}">${st.no} ${esc(st.name)}</li>`).join("")}</ol>` : "";
    const halt = s.halted ? `<div class="halt"><strong>Halted: outputs are off.</strong>${s.haltReasons.length ? `<ul>${s.haltReasons.map((r) => `<li>${esc(r)}</li>`).join("")}</ul>` : ""}</div>` : "";
    const next = s.next.map((n) =>
      `<h3>To go to step ${n.to} "${esc(n.toName)}" it needs</h3>${condList(n.conditions)}`).join("");
    return `
      <div class="seq">
        <div class="seq-head">
          <div class="seq-step">${esc(s.name)}: step ${s.step ?? "?"} "${esc(s.stepName)}"</div>
          <div class="muted">${s.timeInStep ? "in this step for " + esc(s.timeInStep) : ""}</div>
        </div>
        ${chain}
        ${s.does ? `<p>${esc(s.does)}</p>` : ""}
        ${halt}
        ${next}
        ${s.limit ? `<p class="muted small limit">Time limit: ${esc(s.limit)}</p>` : ""}
        ${s.block ? `<p class="muted small">Program: ${esc(s.block)}</p>` : ""}
      </div>`;
  }).join("");
}

function renderAlarms(alarms) {
  $("alarm-count").textContent = alarms.length ? `(${alarms.length})` : "";
  if (!alarms.length) {
    $("alarms").innerHTML = `<p class="none">No alarms are active.</p>`;
    return;
  }
  $("alarms").innerHTML = alarms.map((a) => `
    <article class="alarm ${a.severity === "WARNING" ? "warning" : ""}">
      <h3><span class="sev">${esc(a.severity)}</span> ${esc(a.id)} ${esc(a.text)}</h3>
      <dl>
        ${a.condition ? `<dt>Meaning</dt><dd>${esc(a.condition)}</dd>` : ""}
        ${a.location ? `<dt>Where</dt><dd>${esc(a.location)}</dd>` : ""}
        ${a.causes.length ? `<dt>Likely cause</dt><dd><ul>${a.causes.map((c) => `<li>${esc(c)}</li>`).join("")}</ul></dd>` : ""}
        ${a.fix.length ? `<dt>Fix</dt><dd><ol>${a.fix.map((c) => `<li>${esc(c)}</li>`).join("")}</ol></dd>` : ""}
      </dl>
    </article>`).join("");
}

function renderEquipment(items) {
  $("equipment").innerHTML = items.map((e) => {
    const req = e.requested === true ? "yes" : e.requested === false ? "no" : "?";
    const cmd = e.commanded === true ? "on" : e.commanded === false ? "off" : "?";
    const blocked = e.requested === true && e.commanded === false && e.blocking.length
      ? `<div class="blocked"><strong>Requested but blocked by:</strong><ul>${e.blocking.map((b) =>
          `<li>${esc(b.text)}${b.values.length ? ` <span class="now">now ${b.values.map((v) => esc(v.text)).join(", ")}</span>` : ""}</li>`).join("")}</ul></div>`
      : "";
    return `
      <div class="eq">
        <h3>${esc(e.name)}</h3>
        <span class="state lv-${esc(e.level)}">${esc(e.state)}</span>
        <div class="kv">
          <span>Requested by program</span><span>${req}</span>
          <span>Output to device</span><span>${cmd}</span>
          ${e.values.map((v) => `<span>${esc(v.label)}</span><span>${esc(v.text)}</span>`).join("")}
        </div>
        ${blocked}
        ${e.location ? `<div class="loc">${esc(e.location)}</div>` : ""}
      </div>`;
  }).join("");
}

function renderExchange(rows) {
  $("exchange").querySelector("tbody").innerHTML = rows.map((x) => `
    <tr>
      <td>${esc(x.partner)}</td>
      <td>${x.direction === "in" ? "← from " + esc(x.partner) : "→ to " + esc(x.partner)}</td>
      <td>${esc(x.label)}</td>
      <td class="val">${esc(x.value)}</td>
      <td>${esc(x.text || "")}</td>
    </tr>`).join("");
}

function renderTagErrors(errors) {
  const entries = Object.entries(errors || {});
  $("tag-errors-card").classList.toggle("hidden", entries.length === 0);
  $("tag-errors").innerHTML = entries.map(([id, why]) => `<li>${esc(id)}: ${esc(why)}</li>`).join("");
}

async function poll() {
  try {
    const r = await fetch("/api/status", { cache: "no-store" });
    if (r.ok) {
      const s = await r.json();
      if (!model) await loadModel();
      $("machine").textContent = s.machine;
      renderConnection(s.connection, s.time);
      renderSummary(s.summary);
      renderOverview(s.overview);
      renderSequences(s.sequences);
      renderAlarms(s.activeAlarms);
      renderEquipment(s.equipment);
      renderExchange(s.exchange);
      renderTagErrors(s.tagErrors);
    }
  } catch {
    $("conn").textContent = "server not reachable";
    $("conn").className = "pill pill-bad";
  } finally {
    setTimeout(poll, 1000);
  }
}

loadModel().then(poll);
