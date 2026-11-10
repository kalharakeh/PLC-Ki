// PLC-Ki language switch, English and German (review 49). Plain JavaScript, no library.
//
//   PK.t("Active alarm")                        the English text is the key; German comes from i18n-de.js, English when
//                                               there is no German entry yet
//   PK.t("Step {n} of {total}", { n, total })   {name} is replaced by the value
//   PK.t("{n} alarm|{n} alarms", { n })         "singular|plural" with n: one form for n = 1, the other for the rest
//   PK.T("Waiting")                             marks a text in a table (changes nothing); translate where it is shown
//                                               with PK.t(variable). The marker lets the tests find the text.
//   PK.srv(message)                             a message the server wrote (refusal, validation error): exact German
//                                               entry, else a rule from i18n-de.js, else the message as it came
//
// The English text must be one string literal in the call, never built with + or ${}, so the tests can read it.
// Texts that come from the PLC program or from imports (alarm texts, step names, tag comments, station and device
// names) and what people wrote (notes, documents) are not translated.
"use strict";

const I18N = (() => {
  let lang = "en";
  const de = {};            // English text -> German (i18n-de.js)
  const rules = [];         // [RegExp, German with $1…] for server messages that carry a name or a number
  const own = (o, k) => Object.prototype.hasOwnProperty.call(o, k);

  /** The text in the language in use, with {name} filled in and "one|many" chosen by vars.n. */
  function t(en, vars) {
    let s = lang === "de" && own(de, en) ? de[en] : en;
    if (vars && vars.n != null && s.includes("|")) { const f = s.split("|"); s = Number(vars.n) === 1 ? f[0] : f[1] ?? f[0]; }
    return vars ? s.replace(/\{(\w+)\}/g, (m, k) => (k in vars && vars[k] != null ? vars[k] : m)) : s;
  }
  const T = (s) => s;

  /** The whole text: an exact German entry, else the first rule that matches; null when there is none. */
  function whole(m, depth = 0) {
    if (own(de, m)) return de[m];
    for (const [re, to] of rules) if (re.test(m)) return m.replace(re, (...a) => to.replace(/\$(T?)(\d)/g, (_, tr, i) => (tr ? inner(a[+i], depth) : a[+i])));
    return null;
  }
  /** $T1 in a rule: the text caught by group 1, translated on its own (clauses joined with "), " are taken apart first). */
  function inner(text, depth) {
    if (depth > 3 || text == null) return text;
    const bits = String(text).split(/\), (?=[a-z])/);
    return bits.map((b, i) => { const s = i < bits.length - 1 ? b + ")" : b; const r = parts(s, depth + 1); return r != null ? r : s; }).join("; ");
  }
  /** Like whole(), and when a message was joined from several (clauses after "; ", sentences, "Heading: text"), each part on its own. */
  function parts(m, depth) {
    let r = whole(m, depth);
    if (r != null || depth > 4) return r;
    if (m.endsWith(".") && !m.endsWith("..") && (r = whole(m.slice(0, -1), depth)) != null) return r + ".";
    const cut = [], seps = [];
    let last = 0;
    for (const x of m.matchAll(/(; )|([.!?]) (?=[A-Z])/g)) {
      if (x[1]) { cut.push(m.slice(last, x.index)); seps.push("; "); } else { cut.push(m.slice(last, x.index + 1)); seps.push(" "); }
      last = x.index + x[0].length;
    }
    if (cut.length) {
      cut.push(m.slice(last));
      let any = false;
      const out = cut.map((p) => { const t = parts(p, depth + 1); if (t != null) any = true; return t != null ? t : p; });
      return any ? out.map((p, i) => p + (seps[i] || "")).join("") : null;
    }
    const i = m.indexOf(": ");
    if (i > 0) {
      const h = whole(m.slice(0, i), depth), tail = parts(m.slice(i + 2), depth + 1);
      if (h != null || tail != null) return (h != null ? h : m.slice(0, i)) + ": " + (tail != null ? tail : m.slice(i + 2));
    }
    return null;
  }
  /** A message the server wrote, in the language in use; what has no German entry stays as it came. */
  function srv(message) {
    const m = String(message == null ? "" : message);
    if (lang !== "de" || !m) return m;
    const r = parts(m, 0);
    return r != null ? r : m;
  }

  /** Translates the text and the title, aria-label, placeholder and alt of what is already in the page (the shell in index.html). */
  const ATTRS = ["title", "aria-label", "placeholder", "alt"];
  function dom(root = document.body) {
    if (lang !== "de") return;
    const walk = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    const texts = [];
    for (let n = walk.nextNode(); n; n = walk.nextNode()) texts.push(n);
    for (const n of texts) {
      const raw = n.nodeValue, key = raw.trim();
      if (key && own(de, key) && !n.parentElement.closest("script,style,[data-no-t]")) n.nodeValue = raw.replace(key, de[key]);
    }
    root.querySelectorAll("[" + ATTRS.join("],[") + "]").forEach((el) => {
      if (el.closest("[data-no-t]")) return;
      for (const a of ATTRS) { const v = el.getAttribute(a); if (v && own(de, v.trim())) el.setAttribute(a, de[v.trim()]); }
    });
  }

  function setLang(l) { lang = l === "de" ? "de" : "en"; document.documentElement.lang = lang; }

  return {
    t, T, srv, dom, setLang,
    get lang() { return lang; },
    /** i18n-de.js: I18N.add({ "English": "Deutsch", … }) */
    add(o) { Object.assign(de, o); },
    /** i18n-de.js: I18N.addRules([[/^English (\d+)$/, "Deutsch $1"], …]) */
    addRules(list) { rules.push(...list); },
    de,
  };
})();
