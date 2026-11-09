// Device drawings for the PLC page (CPU, Racks and I/O): each module drawn from its order number (family, kind, width),
// as a clean front view in millimetres, so a CPU 1515F looks like a wide S7-1500 CPU with a display and a DI 32
// has its 32 channel LEDs. Original drawings in SVG, no maker logos. Colours are the devices' own, the same in
// light and dark mode.
"use strict";

const PKDev = (() => {
  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const C = {
    anth: "#30353a", anthL: "#3b4147", anthD: "#22262a", edge: "#141618", text: "#eef1f3", sub: "#9aa4ad",
    light: "#e4e5e0", lightD: "#cfd1cb", lightEdge: "#a9aca6", term: "#3e4448",
    yellow: "#f1c40f", yellowD: "#c99e06",
    ledG: "#3fe06a", ledY: "#ffc21a", ledR: "#ff4b3e", ledOff: "#4a5157", ledOffL: "#b8bbb5",
    screen: "#0b1016", rail: "#c3c8cd", railD: "#9ea5ac",
  };
  /** Module height in mm by family (front view). */
  const HEIGHT = { "s7-1500": 147, "et200mp": 147, "s7-1200": 100, "et200sp": 117, other: 96 };

  let uid = 0;

  /** An LED: state "on" | "off" | "flash" | "unknown"; colour g/y/r. */
  function led(x, y, w, h, color, state, label, k, labelLeft) {
    const fill = state === "on" || state === "flash" ? (color === "r" ? C.ledR : color === "y" ? C.ledY : C.ledG) : C.ledOff;
    const glow = state === "on" || state === "flash" ? ` filter="url(#pkd-glow)"` : "";
    const cls = state === "flash" ? ` class="pkd-flash"` : state === "unknown" ? ` opacity="0.55"` : "";
    const lab = label ? `<text x="${labelLeft ? x - k * 1.2 : x + w + k * 1.2}" y="${y + h * 0.85}" font-size="${k * 2.1}" fill="${C.sub}" text-anchor="${labelLeft ? "end" : "start"}">${esc(label)}</text>` : "";
    return `<rect x="${x}" y="${y}" width="${w}" height="${h}" rx="${h / 2.4}" fill="${fill}"${glow}${cls}/>${lab}`;
  }

  /** Splits "DI 32x24VDC HF" into lines that fit about `chars` characters. */
  function lines(text, chars) {
    const words = String(text || "").split(/\s+/).filter(Boolean);
    const out = [];
    let cur = "";
    for (const w of words) {
      if (cur && (cur + " " + w).length > chars) { out.push(cur); cur = w; }
      else cur = cur ? cur + " " + w : w;
    }
    if (cur) out.push(cur);
    return out.slice(0, 3);
  }

  const chanOf = (m, io) => (m.channels || []).filter((c) => c.io === io);
  const value = (c, values) => (c && c.symbol && values) ? values[c.symbol] : undefined;
  const ledState = (v, running) => {
    if (v === undefined || v === null || v.error || v.value === undefined || v.value === null) return "unknown";
    const b = v.value === true || v.value === "True" || v.value === 1;
    return b && running !== false ? "on" : "off";
  };

  // ---------------- S7-1500 / ET 200MP ----------------
  function s1500(m, o) {
    const k = o.k, W = (m.widthMm || 35) * k, H = HEIGHT["s7-1500"] * k;
    const f = m.safety && m.kind !== "cpu";
    let s = `<rect x="0" y="0" width="${W}" height="${H}" rx="${k * 1.6}" fill="${C.anth}" stroke="${C.edge}" stroke-width="${k * 0.4}"/>`;
    // top vent and the front frame
    s += `<rect x="${k * 0.6}" y="${k * 0.6}" width="${W - k * 1.2}" height="${k * 7}" rx="${k}" fill="${C.anthD}"/>`;
    for (let x = k * 3; x < W - k * 3; x += k * 2.4) s += `<rect x="${x}" y="${k * 2.4}" width="${k * 1.1}" height="${k * 3.4}" rx="${k * 0.4}" fill="#16191c"/>`;
    if (f) s += `<rect x="${k * 0.6}" y="${k * 8}" width="${W - k * 1.2}" height="${k * 2.2}" fill="${C.yellow}"/>`;
    if (m.kind === "cpu") s += cpu1500Front(m, o, W, H);
    else if (m.kind === "ps" || m.kind === "pm") s += ps1500Front(m, o, W, H);
    else s += io1500Front(m, o, W, H, f);
    // order number at the foot, as printed on the device
    if (m.orderNumber) s += `<text x="${W / 2}" y="${H - k * 3.4}" font-size="${Math.min(k * 2.2, (W - k * 2) / 11)}" fill="${C.sub}" text-anchor="middle" class="pkd-mono">${esc(m.orderNumber)}</text>`;
    return { w: W, h: H, svg: s };
  }

  function title1500(m, W, k, y) {
    const t = m.title || m.name || "";
    const chars = Math.max(8, Math.floor(W / (k * 1.95)));
    return lines(t, chars).map((l, i) => `<text x="${k * 2.4}" y="${y + i * k * 3.6}" font-size="${k * 2.9}" font-weight="600" fill="${C.text}">${esc(l)}</text>`).join("");
  }

  function io1500Front(m, o, W, H, f) {
    const k = o.k;
    let s = title1500(m, W, k, k * 15);
    const run = o.running;
    // module status LEDs: RUN and ERROR (and PWR on digital outputs)
    const ly = k * 23.5;
    s += led(k * 2.4, ly, k * 3.4, k * 1.7, "g", run === true ? "on" : run === false ? "flash" : "unknown", "RUN", k);
    s += led(W / 2 + k * 0.6, ly, k * 3.4, k * 1.7, "r", "off", "ERR", k);
    // channel LEDs in one or two columns, as on the front of the module
    const chans = (m.channels || []);
    const n = chans.length;
    if (n) {
      const cols = n > 16 ? 2 : 1;
      const per = Math.ceil(n / cols);
      const top = k * 29, row = Math.min(k * 3.2, (H * 0.5) / per);
      const colW = (W - k * 4) / cols;
      s += `<rect x="${k * 1.6}" y="${top - k * 1.6}" width="${W - k * 3.2}" height="${per * row + k * 2.2}" rx="${k}" fill="${C.anthD}"/>`;
      chans.forEach((c, i) => {
        const col = Math.floor(i / per), r = i % per;
        const x = k * 2.4 + col * colW, y = top + r * row;
        const st = ledState(value(c, o.values), o.running);
        const analog = c.type === "analog";
        s += `<g data-ch="${esc(c.io + c.n)}"><title>${esc(`${c.address}${c.tag ? " · " + c.tag : ""}`)}</title>`
          + led(x, y, k * 2.6, k * 1.5, analog ? "g" : "g", analog ? (st === "unknown" ? "unknown" : "on") : st, "", k)
          + `<text x="${x + k * 3.6}" y="${y + k * 1.4}" font-size="${k * 1.9}" fill="${C.sub}">${c.n}</text></g>`;
      });
    }
    // front flap with its label area and the opening grip
    const fy = n ? k * 29 + Math.ceil(n / (n > 16 ? 2 : 1)) * Math.min(k * 3.2, (H * 0.5) / Math.ceil(n / (n > 16 ? 2 : 1))) + k * 4 : k * 30;
    const fh = H - fy - k * 9;
    if (fh > k * 10) {
      s += `<rect x="${k * 1.2}" y="${fy}" width="${W - k * 2.4}" height="${fh}" rx="${k * 1.2}" fill="${C.anthL}" stroke="#4a5056" stroke-width="${k * 0.3}"/>`;
      s += `<rect x="${W / 2 - k * 4}" y="${fy + k * 1.4}" width="${k * 8}" height="${k * 1.2}" rx="${k * 0.6}" fill="#262a2e"/>`;
      if (f) s += `<text x="${W / 2}" y="${fy + fh / 2}" font-size="${k * 5}" font-weight="700" fill="${C.yellow}" text-anchor="middle">F</text>`;
      else if (m.addresses && m.addresses.length) s += `<text x="${W / 2}" y="${fy + fh / 2}" font-size="${k * 2.2}" fill="${C.sub}" text-anchor="middle">${esc(m.addresses[0].text)}</text>`;
    }
    return s;
  }

  function ps1500Front(m, o, W, H) {
    const k = o.k;
    let s = title1500(m, W, k, k * 15);
    const ly = k * 26;
    s += led(W - k * 6.5, ly, k * 3.4, k * 1.7, "g", o.powered === false ? "off" : "on", "RUN", k, true);
    s += led(W - k * 6.5, ly + k * 3.6, k * 3.4, k * 1.7, "r", "off", "ERROR", k, true);
    s += led(W - k * 6.5, ly + k * 7.2, k * 3.4, k * 1.7, "y", "off", "MAINT", k, true);
    // flap and the on/off switch at the foot
    s += `<rect x="${k * 1.2}" y="${k * 40}" width="${W - k * 2.4}" height="${H - k * 52}" rx="${k * 1.2}" fill="${C.anthL}" stroke="#4a5056" stroke-width="${k * 0.3}"/>`;
    s += `<rect x="${W / 2 - k * 4}" y="${H - k * 26}" width="${k * 8}" height="${k * 12}" rx="${k}" fill="#1b1e21"/>`;
    s += `<rect x="${W / 2 - k * 2.6}" y="${H - k * 24.6}" width="${k * 5.2}" height="${k * 5}" rx="${k * 0.6}" fill="#5b636a"/>`;
    s += `<text x="${W / 2}" y="${H - k * 28}" font-size="${k * 2}" fill="${C.sub}" text-anchor="middle">I / O</text>`;
    return s;
  }

  /** The CPU front: name, the three LEDs, the display with its keys, the flap over card and ports. */
  function cpu1500Front(m, o, W, H) {
    const k = o.k;
    let s = title1500({ ...m, title: m.title || m.name }, W - k * 14, k, k * 14);
    const L = o.leds || {};
    const lx = W - k * 5.2, ly = k * 9.5;
    s += led(lx, ly, k * 3.4, k * 1.7, L.runColor || "g", L.run || "unknown", "RUN/STOP", k, true);
    s += led(lx, ly + k * 3.4, k * 3.4, k * 1.7, "r", L.error || "off", "ERROR", k, true);
    s += led(lx, ly + k * 6.8, k * 3.4, k * 1.7, "y", L.maint || "off", "MAINT", k, true);
    // display and keys
    const dx = k * 3.5, dy = k * 21, dw = W - k * 7, dh = Math.min(dw * 0.72, k * 40);
    s += `<rect x="${dx - k * 1.2}" y="${dy - k * 1.2}" width="${dw + k * 2.4}" height="${dh + k * 2.4}" rx="${k * 1.6}" fill="#15181b"/>`;
    s += `<g class="pkd-screen">${o.screen ? o.screen(dx, dy, dw, dh) : screenIdle(dx, dy, dw, dh, m)}</g>`;
    const ky = dy + dh + k * 5.5, kr = Math.min(k * 2.3, dw / 16);
    const keys = [["◀", "left"], ["▲", "up"], ["▼", "down"], ["▶", "right"]];
    const gap = dw / 6.2;
    keys.forEach(([t, a], i) => {
      const cx = dx + gap * (i + 0.7);
      s += `<g class="pkd-key" data-key="${a}" role="button" aria-label="Display key ${a}" tabindex="${o.keys ? 0 : -1}"><circle cx="${cx}" cy="${ky}" r="${kr}" fill="#454c53" stroke="#1b1e21" stroke-width="${k * 0.3}"/><text x="${cx}" y="${ky + kr * 0.42}" font-size="${kr * 1.15}" fill="#d9dee2" text-anchor="middle">${t}</text></g>`;
    });
    [["ESC", "esc"], ["OK", "ok"]].forEach(([t, a], i) => {
      const cx = dx + gap * (4.55 + i * 1.05);
      s += `<g class="pkd-key" data-key="${a}" role="button" aria-label="Display key ${t}" tabindex="${o.keys ? 0 : -1}"><rect x="${cx - kr * 1.25}" y="${ky - kr * 0.8}" width="${kr * 2.5}" height="${kr * 1.6}" rx="${kr * 0.4}" fill="#454c53" stroke="#1b1e21" stroke-width="${k * 0.3}"/><text x="${cx}" y="${ky + kr * 0.35}" font-size="${kr * 0.78}" font-weight="700" fill="#d9dee2" text-anchor="middle">${t}</text></g>`;
    });
    // the lower flap: memory card slot and PROFINET ports behind it
    const fy = ky + kr + k * 5, fh = H - fy - k * 8;
    if (fh > k * 12) {
      s += `<rect x="${k * 1.2}" y="${fy}" width="${W - k * 2.4}" height="${fh}" rx="${k * 1.2}" fill="${C.anthL}" stroke="#4a5056" stroke-width="${k * 0.3}"/>`;
      s += `<rect x="${W / 2 - k * 4}" y="${fy + k * 1.4}" width="${k * 8}" height="${k * 1.2}" rx="${k * 0.6}" fill="#262a2e"/>`;
      const ports = (m.ports && m.ports.length ? m.ports : ["X1 P1", "X1 P2"]).filter((p) => /^PROFINET|^X\d|^Port/i.test(p));
      const plabels = ports.length ? ports.map((p) => p.replace(/PROFINET interface_?/i, "X").replace(/Port_?/i, "P")) : ["X1"];
      const pw = Math.min(k * 9, (W - k * 8) / Math.max(2, plabels.length));
      plabels.slice(0, 4).forEach((p, i) => {
        const px = k * 4 + i * (pw + k * 1.2), py = fy + fh - k * 13;
        s += `<rect x="${px}" y="${py}" width="${pw}" height="${k * 8}" rx="${k * 0.6}" fill="#1c1f22"/><rect x="${px + pw * 0.2}" y="${py + k * 1.2}" width="${pw * 0.6}" height="${k * 4.5}" rx="${k * 0.4}" fill="#4c545b"/>`
          + `<text x="${px + pw / 2}" y="${py - k * 1.2}" font-size="${k * 1.9}" fill="${C.sub}" text-anchor="middle">${esc(p)}</text>`;
      });
      if (m.safety) s += `<text x="${W - k * 4}" y="${fy + k * 9}" font-size="${k * 3.4}" font-weight="700" fill="${C.yellow}" text-anchor="end">F</text>`;
    }
    return s;
  }

  function screenIdle(x, y, w, h, m) {
    const f = h / 9;
    return `<rect x="${x}" y="${y}" width="${w}" height="${h}" rx="${f * 0.3}" fill="${C.screen}"/>
      <text x="${x + w / 2}" y="${y + h / 2}" font-size="${f}" fill="#6f7b86" text-anchor="middle">${esc(m.title || "CPU")}</text>`;
  }

  // ---------------- S7-1200 ----------------
  function s1200(m, o) {
    const k = o.k, W = (m.widthMm || 45) * k, H = HEIGHT["s7-1200"] * k;
    const f = m.safety && m.kind !== "cpu";
    let s = `<rect x="0" y="0" width="${W}" height="${H}" rx="${k * 1.4}" fill="${f ? C.yellow : C.light}" stroke="${C.lightEdge}" stroke-width="${k * 0.4}"/>`;
    // terminal covers top and bottom, with the screw heads
    const tc = k * 15;
    for (const ty of [0, H - tc]) {
      s += `<rect x="${k * 0.8}" y="${ty + k * 0.8}" width="${W - k * 1.6}" height="${tc - k * 1.6}" rx="${k}" fill="${C.term}"/>`;
      for (let x = k * 4; x < W - k * 3; x += k * 5) s += `<circle cx="${x}" cy="${ty + tc / 2}" r="${k * 1.3}" fill="#5f676d"/>`;
    }
    const ins = chanOf(m, "Input"), outs = chanOf(m, "Output");
    // I/O LED rows under the top cover and above the bottom cover
    const row = (list, y) => list.slice(0, 24).map((c, i) => {
      const x = k * (m.kind === "cpu" ? 22 : 4) + i * k * 3.1;
      if (x > W - k * 4) return "";
      const st = ledState(value(c, o.values), o.running);
      return `<g><title>${esc(c.address + (c.tag ? " · " + c.tag : ""))}</title>${led(x, y, k * 2.2, k * 1.4, "g", st === "on" ? "on" : st === "unknown" ? "unknown" : "off", "", k).replace(C.ledOff, C.ledOffL)}</g>`;
    }).join("");
    s += row(ins, tc + k * 2.5) + row(outs, H - tc - k * 4);
    if (m.kind === "cpu") {
      const L = o.leds || {};
      s += led(k * 4, tc + k * 8, k * 3, k * 1.6, L.runColor || "g", L.run || "unknown", "RUN/STOP", k).replace(/fill="#9aa4ad"/, `fill="#5a6168"`);
      s += led(k * 4, tc + k * 12, k * 3, k * 1.6, "r", L.error || "off", "ERROR", k).replace(/fill="#9aa4ad"/, `fill="#5a6168"`);
      s += led(k * 4, tc + k * 16, k * 3, k * 1.6, "y", L.maint || "off", "MAINT", k).replace(/fill="#9aa4ad"/, `fill="#5a6168"`);
      // PROFINET port at the foot, left
      s += `<rect x="${k * 4}" y="${H - tc - k * 15}" width="${k * 11}" height="${k * 9}" rx="${k * 0.8}" fill="#2c3135"/><rect x="${k * 6.5}" y="${H - tc - k * 13.6}" width="${k * 6}" height="${k * 5}" rx="${k * 0.4}" fill="#596168"/>`;
    }
    const t = lines(m.title || m.name, Math.floor(W / (k * 2.1)));
    t.forEach((l, i) => { s += `<text x="${m.kind === "cpu" ? k * 22 : k * 3}" y="${H / 2 - k * 2 + i * k * 4}" font-size="${k * 3}" font-weight="600" fill="#2b3035">${esc(l)}</text>`; });
    if (m.orderNumber) s += `<text x="${m.kind === "cpu" ? k * 22 : k * 3}" y="${H / 2 + k * (t.length * 4 + 1)}" font-size="${k * 2}" fill="#6b7177" class="pkd-mono">${esc(m.orderNumber)}</text>`;
    return { w: W, h: H, svg: s };
  }

  // ---------------- ET 200SP ----------------
  function sp(m, o) {
    const k = o.k, W = (m.widthMm || 15) * k, H = HEIGHT.et200sp * k;
    if (m.kind === "im") return spIm(m, o, W, H);
    const bu = k * 44, top = H - bu;
    const f = m.safety;
    // BaseUnit with push-in terminals
    let s = `<rect x="0" y="${top}" width="${W}" height="${bu}" rx="${k}" fill="${C.light}" stroke="${C.lightEdge}" stroke-width="${k * 0.3}"/>`;
    for (let r = 0; r < 8; r++) for (let c = 0; c < 2; c++)
      s += `<rect x="${k * (2.2 + c * 6)}" y="${top + k * (3 + r * 4.9)}" width="${k * 4.4}" height="${k * 3.2}" rx="${k * 0.5}" fill="${C.lightD}" stroke="#b3b6b0" stroke-width="${k * 0.2}"/>`;
    // the module on top of it
    const short = m.kind === "bus";
    const my = short ? top - k * 46 : k * 2;
    s += `<rect x="${k * 0.4}" y="${my}" width="${W - k * 0.8}" height="${top - my + k * 3}" rx="${k}" fill="${f ? C.yellow : C.anth}" stroke="${f ? C.yellowD : C.edge}" stroke-width="${k * 0.3}"/>`;
    if (!short) {
      s += led(k * 2.2, my + k * 3, k * 3.2, k * 1.5, "g", o.running === true ? "on" : "unknown", "", k);
      const chans = m.channels || [];
      const per = chans.length > 8 ? Math.ceil(chans.length / 2) : chans.length;
      chans.forEach((c, i) => {
        const col = per ? Math.floor(i / per) : 0, r = per ? i % per : 0;
        const st = ledState(value(c, o.values), o.running);
        s += `<g><title>${esc(c.address + (c.tag ? " · " + c.tag : ""))}</title>${led(k * (2.2 + col * 6), my + k * (8 + r * 3.1), k * 3.2, k * 1.5, "g", c.type === "analog" ? (st === "unknown" ? "unknown" : "on") : st, "", k)}</g>`;
      });
    }
    // the type, written along the module as on the real label
    const t = (m.title || m.name || "").slice(0, 28);
    s += `<text transform="translate(${W / 2 + k * 1.1} ${top - k * 3}) rotate(-90)" font-size="${k * 2.6}" font-weight="600" fill="${f ? "#3b3000" : C.text}">${esc(t)}</text>`;
    return { w: W, h: H, svg: s };
  }

  function spIm(m, o, W, H) {
    const k = o.k;
    let s = `<rect x="0" y="${k * 4}" width="${W}" height="${H - k * 4}" rx="${k * 1.4}" fill="${C.anth}" stroke="${C.edge}" stroke-width="${k * 0.4}"/>`;
    // bus adapter with two RJ45 ports on top
    s += `<rect x="${k * 4}" y="0" width="${W - k * 8}" height="${k * 22}" rx="${k}" fill="#d9dad6" stroke="${C.lightEdge}" stroke-width="${k * 0.3}"/>`;
    for (let i = 0; i < 2; i++) s += `<rect x="${k * (7 + i * 19)}" y="${k * 4}" width="${k * 14}" height="${k * 11}" rx="${k * 0.8}" fill="#2a2e32"/><rect x="${k * (10 + i * 19)}" y="${k * 6}" width="${k * 8}" height="${k * 6}" rx="${k * 0.5}" fill="#5b636a"/><text x="${k * (14 + i * 19)}" y="${k * 19.5}" font-size="${k * 2.2}" fill="#4b5157" text-anchor="middle">P${i + 1}</text>`;
    const L = o.leds || {};
    const ly = k * 30;
    s += led(k * 4, ly, k * 3.4, k * 1.7, "g", o.running === true ? "on" : "unknown", "RN", k);
    s += led(k * 4, ly + k * 4, k * 3.4, k * 1.7, "r", "off", "ER", k);
    s += led(k * 4, ly + k * 8, k * 3.4, k * 1.7, "y", "off", "MT", k);
    s += led(k * 4, ly + k * 12, k * 3.4, k * 1.7, "g", o.running === true ? "on" : "unknown", "PWR", k);
    lines(m.title || m.name, Math.floor(W / (k * 2))).forEach((l, i) => { s += `<text x="${k * 3}" y="${k * 54 + i * k * 3.8}" font-size="${k * 2.8}" font-weight="600" fill="${C.text}">${esc(l)}</text>`; });
    s += `<rect x="${k * 2}" y="${k * 64}" width="${W - k * 4}" height="${H - k * 74}" rx="${k}" fill="${C.anthL}"/>`;
    for (let r = 0; r < 4; r++) s += `<rect x="${k * 6}" y="${k * (70 + r * 6)}" width="${k * 5}" height="${k * 3.4}" rx="${k * 0.5}" fill="#59616a"/>`;
    if (m.orderNumber) s += `<text x="${W / 2}" y="${H - k * 3.5}" font-size="${k * 2}" fill="${C.sub}" text-anchor="middle" class="pkd-mono">${esc(m.orderNumber)}</text>`;
    void L;
    return { w: W, h: H, svg: s };
  }

  // ---------------- anything else: drive, robot, camera ... ----------------
  function generic(m, o) {
    const k = o.k, W = Math.max(60, m.widthMm || 70) * k, H = HEIGHT.other * k;
    let s = `<rect x="0" y="0" width="${W}" height="${H}" rx="${k * 3}" fill="#e9ebee" stroke="#a5abb1" stroke-width="${k * 0.4}"/>`;
    s += `<rect x="${k * 3}" y="${k * 3}" width="${W - k * 6}" height="${k * 30}" rx="${k * 2}" fill="${C.anth}"/>`;
    s += led(k * 7, k * 8, k * 3.4, k * 1.7, "g", o.running === true ? "on" : "unknown", "", k);
    s += `<text x="${k * 7}" y="${k * 22}" font-size="${k * 4}" font-weight="600" fill="${C.text}">${esc((m.title || m.name || "").slice(0, Math.floor(W / (k * 2.6))))}</text>`;
    lines(m.name, Math.floor(W / (k * 2.2))).forEach((l, i) => { s += `<text x="${k * 5}" y="${k * 44 + i * k * 4.4}" font-size="${k * 3.2}" fill="#2b3035">${esc(l)}</text>`; });
    if (m.orderNumber) s += `<text x="${k * 5}" y="${H - k * 6}" font-size="${k * 2.4}" fill="#6b7177" class="pkd-mono">${esc(m.orderNumber)}</text>`;
    return { w: W, h: H, svg: s };
  }

  /** One module: {w, h, svg} in px. o = {k, values, running, leds, screen, keys}. */
  function module(m, o) {
    const fam = m.family || "other";
    if (fam === "s7-1500" || fam === "et200mp") {
      if (m.kind === "im") return s1500({ ...m, kind: "ps" }, o);    // ET 200MP head: a narrow module with LEDs
      return s1500(m, o);
    }
    if (fam === "s7-1200") return s1200(m, o);
    if (fam === "et200sp") return sp(m, o);
    return generic(m, o);
  }

  const defs = () => `<defs><filter id="pkd-glow" x="-80%" y="-80%" width="260%" height="260%"><feGaussianBlur stdDeviation="1.2" result="b"/><feMerge><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge></filter></defs>`;

  /**
   * A rack: the modules side by side on their rail, slot numbers above. o.selected = index of the picked module.
   * Returns the whole <svg>.
   */
  function rack(station, o) {
    const k = o.k;
    const mods = (station.modules || []).filter((m) => (m.widthMm || 0) > 0 || m.family === "other");
    const drawn = mods.map((m) => module(m, { ...o, values: o.values }));
    const gap = station.family === "other" ? k * 8 : k * 0.8;
    const top = k * 9;
    let x = k * 6, inner = "";
    const H = Math.max(...drawn.map((d) => d.h), k * 40);
    drawn.forEach((d, i) => {
      const m = mods[i], sel = o.selected === i;
      inner += `<g class="pkd-mod${sel ? " on" : ""}" data-mod="${i}" transform="translate(${x} ${top + (H - d.h)})" tabindex="0" role="button" aria-label="${esc(`Slot ${m.slot ?? "?"}: ${m.title || m.name}`)}">
        <rect class="pkd-hit" x="${-k * 1.2}" y="${-k * 1.2}" width="${d.w + k * 2.4}" height="${d.h + k * 2.4}" rx="${k * 2}" fill="none"/>${d.svg}</g>`;
      inner += `<text x="${x + d.w / 2}" y="${top - k * 3}" font-size="${k * 3}" text-anchor="middle" class="pkd-slot">${m.slot ?? ""}</text>`;
      x += d.w + gap;
    });
    const W = x + k * 6;
    // the rail behind the modules
    let rail = "";
    const fam = station.family;
    if (fam === "s7-1500" || fam === "et200mp")
      rail = `<rect x="${k * 2}" y="${top + H * 0.06}" width="${W - k * 4}" height="${H * 0.88}" rx="${k}" fill="${C.rail}" stroke="${C.railD}" stroke-width="${k * 0.4}"/><rect x="${k * 2}" y="${top + H * 0.5 - k}" width="${W - k * 4}" height="${k * 2}" fill="${C.railD}" opacity="0.5"/>`;
    else if (fam === "s7-1200" || fam === "et200sp")
      rail = `<rect x="${k * 2}" y="${top + H * 0.6 - k * 17.5}" width="${W - k * 4}" height="${k * 35}" fill="${C.rail}" stroke="${C.railD}" stroke-width="${k * 0.4}"/><rect x="${k * 2}" y="${top + H * 0.6 - k * 13}" width="${W - k * 4}" height="${k * 26}" fill="${C.railD}" opacity="0.45"/>`;
    // Scales up to fill the card (at most 1.6x), never below its drawn size (then the card scrolls sideways)
    return `<svg class="pkd-rack" viewBox="0 0 ${W} ${top + H + k * 4}" style="width:100%;height:auto;min-width:${Math.round(W)}px;max-width:${Math.round(W * 1.6)}px" role="group" aria-label="${esc(station.name)} rack">${defs()}${rail}${inner}</svg>`;
  }

  /** One device on its own (the PLC page's big CPU). */
  function single(m, o) {
    const d = module(m, o);
    const pad = o.k * 2;
    return `<svg class="pkd-single" viewBox="${-pad} ${-pad} ${d.w + pad * 2} ${d.h + pad * 2}" width="${d.w + pad * 2}" height="${d.h + pad * 2}" role="img" aria-label="${esc(m.title || m.name)}">${defs()}${d.svg}</svg>`;
  }

  return { module, rack, single, C, HEIGHT, lines, esc, uid: () => ++uid };
})();
