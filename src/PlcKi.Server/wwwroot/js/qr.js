// QR codes for the machine labels, made in the browser so a plant PC without internet prints the same.
// Byte mode, error correction level M (about 15 % of a dirty or scratched label can be lost), versions 1 to 10
// (up to 213 bytes, plenty for a PLC-Ki address). Follows ISO/IEC 18004, in the same steps as Nayuki's
// QR Code generator. Checked against the Python "qrcode" package and decoded with ZXing for all 8 masks.
"use strict";

const PKQR = (() => {
  // Level M, versions 1..10: error correction codewords per block, and number of blocks
  const EC_PER_BLOCK = [10, 16, 26, 18, 24, 16, 18, 22, 22, 26];
  const BLOCKS = [1, 1, 1, 2, 2, 4, 4, 4, 5, 5];
  const FORMAT_M = 0;   // the two "error correction level" bits for M

  function rawModules(ver) {
    let n = (16 * ver + 128) * ver + 64;
    if (ver >= 2) {
      const align = Math.floor(ver / 7) + 2;
      n -= (25 * align - 10) * align - 55;
      if (ver >= 7) n -= 36;
    }
    return n;
  }
  const dataCodewords = (ver) => Math.floor(rawModules(ver) / 8) - EC_PER_BLOCK[ver - 1] * BLOCKS[ver - 1];

  // ---- Reed-Solomon over GF(256), polynomial 0x11D ----
  function mul(x, y) {
    let z = 0;
    for (let i = 7; i >= 0; i--) {
      z = (z << 1) ^ ((z >>> 7) * 0x11d);
      z ^= ((y >>> i) & 1) * x;
    }
    return z & 0xff;
  }
  function divisor(degree) {
    const r = new Array(degree).fill(0);
    r[degree - 1] = 1;
    let root = 1;
    for (let i = 0; i < degree; i++) {
      for (let j = 0; j < r.length; j++) {
        r[j] = mul(r[j], root);
        if (j + 1 < r.length) r[j] ^= r[j + 1];
      }
      root = mul(root, 0x02);
    }
    return r;
  }
  function remainder(data, div) {
    const r = new Array(div.length).fill(0);
    for (const b of data) {
      const f = b ^ r.shift();
      r.push(0);
      for (let i = 0; i < r.length; i++) r[i] ^= mul(div[i], f);
    }
    return r;
  }

  function utf8(text) {
    return Array.from(new TextEncoder().encode(text));
  }

  /** Data plus error correction codewords, split in blocks and interleaved. */
  function codewords(bytes, ver) {
    const bits = [];
    const put = (val, len) => { for (let i = len - 1; i >= 0; i--) bits.push((val >>> i) & 1); };
    put(0b0100, 4);                               // byte mode
    put(bytes.length, ver < 10 ? 8 : 16);
    bytes.forEach((b) => put(b, 8));
    const cap = dataCodewords(ver) * 8;
    put(0, Math.min(4, cap - bits.length));       // terminator
    put(0, (8 - (bits.length % 8)) % 8);
    for (let pad = 0xec; bits.length < cap; pad ^= 0xec ^ 0x11) put(pad, 8);
    const data = [];
    for (let i = 0; i < bits.length; i += 8) data.push(bits.slice(i, i + 8).reduce((a, b) => (a << 1) | b, 0));

    const nb = BLOCKS[ver - 1], ec = EC_PER_BLOCK[ver - 1];
    const raw = Math.floor(rawModules(ver) / 8);
    const nShort = nb - (raw % nb), shortLen = Math.floor(raw / nb);
    const div = divisor(ec);
    const blocks = [];
    for (let i = 0, k = 0; i < nb; i++) {
      const d = data.slice(k, k + shortLen - ec + (i < nShort ? 0 : 1));
      k += d.length;
      const e = remainder(d, div);
      if (i < nShort) d.push(0);
      blocks.push(d.concat(e));
    }
    const out = [];
    for (let i = 0; i < blocks[0].length; i++) {
      blocks.forEach((b, j) => { if (i !== shortLen - ec || j >= nShort) out.push(b[i]); });
    }
    return out;
  }

  function alignPositions(ver) {
    if (ver === 1) return [];
    const n = Math.floor(ver / 7) + 2;
    const step = Math.ceil((ver * 4 + 4) / (n * 2 - 2)) * 2;
    const r = [6];
    for (let i = 0, pos = ver * 4 + 10; i < n - 1; i++, pos -= step) r.splice(1, 0, pos);
    return r;
  }

  const MASKS = [
    (x, y) => (x + y) % 2 === 0,
    (x, y) => y % 2 === 0,
    (x) => x % 3 === 0,
    (x, y) => (x + y) % 3 === 0,
    (x, y) => (Math.floor(x / 3) + Math.floor(y / 2)) % 2 === 0,
    (x, y) => ((x * y) % 2) + ((x * y) % 3) === 0,
    (x, y) => (((x * y) % 2) + ((x * y) % 3)) % 2 === 0,
    (x, y) => (((x + y) % 2) + ((x * y) % 3)) % 2 === 0,
  ];

  function build(ver, cw, mask) {
    const size = ver * 4 + 17;
    const m = Array.from({ length: size }, () => new Array(size).fill(false));
    const fn = Array.from({ length: size }, () => new Array(size).fill(false));
    const set = (x, y, dark) => { m[y][x] = dark; fn[y][x] = true; };

    for (let i = 0; i < size; i++) { set(6, i, i % 2 === 0); set(i, 6, i % 2 === 0); }
    for (const [cx, cy] of [[3, 3], [size - 4, 3], [3, size - 4]]) {
      for (let dy = -4; dy <= 4; dy++) for (let dx = -4; dx <= 4; dx++) {
        const x = cx + dx, y = cy + dy;
        if (x < 0 || y < 0 || x >= size || y >= size) continue;
        const d = Math.max(Math.abs(dx), Math.abs(dy));
        set(x, y, d !== 2 && d !== 4);
      }
    }
    const al = alignPositions(ver), last = al.length - 1;
    al.forEach((ax, i) => al.forEach((ay, j) => {
      if ((i === 0 && j === 0) || (i === 0 && j === last) || (i === last && j === 0)) return;
      for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) set(ax + dx, ay + dy, Math.max(Math.abs(dx), Math.abs(dy)) !== 1);
    }));

    // format bits (written again below with the real mask; here they reserve the modules)
    const format = (mk) => {
      const data = (FORMAT_M << 3) | mk;
      let rem = data;
      for (let i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >>> 9) * 0x537);
      const bits = ((data << 10) | rem) ^ 0x5412;
      const bit = (i) => ((bits >>> i) & 1) === 1;
      for (let i = 0; i <= 5; i++) set(8, i, bit(i));
      set(8, 7, bit(6)); set(8, 8, bit(7)); set(7, 8, bit(8));
      for (let i = 9; i < 15; i++) set(14 - i, 8, bit(i));
      for (let i = 0; i < 8; i++) set(size - 1 - i, 8, bit(i));
      for (let i = 8; i < 15; i++) set(8, size - 15 + i, bit(i));
      set(8, size - 8, true);
    };
    format(mask);
    if (ver >= 7) {
      let rem = ver;
      for (let i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >>> 11) * 0x1f25);
      const bits = (ver << 12) | rem;
      for (let i = 0; i < 18; i++) {
        const b = ((bits >>> i) & 1) === 1, a = size - 11 + (i % 3), c = Math.floor(i / 3);
        set(a, c, b); set(c, a, b);
      }
    }

    // data, in two-column zigzags from the bottom right
    let i = 0;
    for (let right = size - 1; right >= 1; right -= 2) {
      if (right === 6) right = 5;
      for (let v = 0; v < size; v++) for (let j = 0; j < 2; j++) {
        const x = right - j, up = ((right + 1) & 2) === 0, y = up ? size - 1 - v : v;
        if (!fn[y][x] && i < cw.length * 8) { m[y][x] = ((cw[i >>> 3] >>> (7 - (i & 7))) & 1) === 1; i++; }
      }
    }
    const f = MASKS[mask];
    for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) if (!fn[y][x] && f(x, y)) m[y][x] = !m[y][x];
    return m;
  }

  /** How hard the pattern is to scan (lower is better): long runs, 2x2 boxes, finder look-alikes, dark/light balance. */
  function penalty(m) {
    const size = m.length;
    let p = 0, dark = 0;
    const lines = [];
    for (let y = 0; y < size; y++) lines.push(m[y]);
    for (let x = 0; x < size; x++) lines.push(m.map((row) => row[x]));
    for (const line of lines) {
      let run = 1;
      for (let i = 1; i <= size; i++) {
        if (i < size && line[i] === line[i - 1]) run++;
        else { if (run >= 5) p += run - 2; run = 1; }
      }
      const s = line.map((b) => (b ? "1" : "0")).join("");
      for (const pat of ["10111010000", "00001011101"]) for (let k = s.indexOf(pat); k >= 0; k = s.indexOf(pat, k + 1)) p += 40;
    }
    for (let y = 0; y < size - 1; y++) for (let x = 0; x < size - 1; x++) {
      const c = m[y][x];
      if (c === m[y][x + 1] && c === m[y + 1][x] && c === m[y + 1][x + 1]) p += 3;
    }
    m.forEach((row) => row.forEach((b) => { if (b) dark++; }));
    const total = size * size;
    p += (Math.ceil(Math.abs(dark * 20 - total * 10) / total) - 1) * 10;
    return p;
  }

  /** The QR code for text, as a square array of rows (true = dark). Throws when the text is too long. */
  function matrix(text, mask) {
    const bytes = utf8(text);
    let ver = 1;
    while (ver <= 10 && 4 + (ver < 10 ? 8 : 16) + bytes.length * 8 > dataCodewords(ver) * 8) ver++;
    if (ver > 10) throw new Error("The address is too long for a QR label.");
    const cw = codewords(bytes, ver);
    if (mask !== undefined) return build(ver, cw, mask);
    let best = null, bestP = Infinity;
    for (let k = 0; k < 8; k++) {
      const mm = build(ver, cw, k), p = penalty(mm);
      if (p < bestP) { best = mm; bestP = p; }
    }
    return best;
  }

  /** An SVG of the code with a 4-module quiet zone. Crisp at any print size. */
  function svg(text, { label = "QR code" } = {}) {
    const m = matrix(text), n = m.length, q = 4, s = n + q * 2;
    let d = "";
    m.forEach((row, y) => row.forEach((b, x) => { if (b) d += `M${x + q} ${y + q}h1v1h-1z`; }));
    return `<svg class="qr" viewBox="0 0 ${s} ${s}" role="img" aria-label="${String(label).replace(/[&<>"]/g, "")}" shape-rendering="crispEdges"><rect width="${s}" height="${s}" fill="#fff"/><path d="${d}" fill="#000"/></svg>`;
  }

  return { matrix, svg };
})();
if (typeof module !== "undefined") module.exports = PKQR;
