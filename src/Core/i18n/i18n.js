'use strict';
// I18N-010: the browser side of the product's languages. English is the source; a dictionary per language
// (/i18n/<lang>.json) maps English text → translation. Templates ("… ({0})") translate texts with values, so messages
// from the server translate too. The page direction follows the language (Hebrew, Arabic: right to left).
(function (g) {
  const RTL = ['he', 'ar'];
  // I18N-050 (owner: "too many bugs — for now only Hebrew and English"); a remembered other language opens in English
  const NAMES = { en: 'English', he: 'עברית' };
  const I = { lang: 'en', dict: {}, tpl: [], names: NAMES, base: '/i18n/' };
  const norm = (l) => { l = String(l || '').toLowerCase().slice(0, 2); if (l === 'iw') l = 'he'; return NAMES[l] ? l : null; };
  const store = { get() { try { return localStorage.getItem('obLang'); } catch (e) { return null; } }, set(v) { try { localStorage.setItem('obLang', v); } catch (e) { } } };
  // I18N-020: the language follows the region the person signs in from. Order: the person's own choice (remembered) →
  // the region (the computer's time zone, e.g. Asia/Jerusalem → Hebrew; a region with two languages, like Belgium,
  // takes the browser's one of them) → the browser's languages → the product's language → English.
  const Z = { he: 'Asia/Jerusalem Asia/Tel_Aviv', ar: 'Asia/Riyadh Asia/Dubai Asia/Qatar Asia/Kuwait Asia/Bahrain Asia/Muscat Asia/Aden Asia/Baghdad Asia/Amman Asia/Beirut Asia/Damascus Asia/Gaza Asia/Hebron Africa/Cairo Africa/Algiers Africa/Tunis Africa/Casablanca Africa/Tripoli Africa/Khartoum',
    de: 'Europe/Berlin Europe/Busingen Europe/Vienna Europe/Zurich Europe/Vaduz', es: 'Europe/Madrid Atlantic/Canary Africa/Ceuta America/Mexico_City America/Monterrey America/Cancun America/Tijuana America/Bogota America/Lima America/Argentina/Buenos_Aires America/Buenos_Aires America/Santiago America/Caracas America/Montevideo America/Asuncion America/La_Paz America/Guayaquil America/Costa_Rica America/Panama America/Guatemala America/El_Salvador America/Tegucigalpa America/Managua America/Havana America/Santo_Domingo',
    pt: 'Europe/Lisbon Atlantic/Azores Atlantic/Madeira America/Sao_Paulo America/Bahia America/Fortaleza America/Recife America/Manaus America/Belem America/Cuiaba America/Porto_Velho Africa/Luanda Africa/Maputo', fr: 'Europe/Paris Europe/Monaco Europe/Luxembourg Africa/Dakar Africa/Abidjan America/Montreal',
    it: 'Europe/Rome Europe/Vatican Europe/San_Marino', nl: 'Europe/Amsterdam', pl: 'Europe/Warsaw', tr: 'Europe/Istanbul Asia/Istanbul', ja: 'Asia/Tokyo',
    zh: 'Asia/Shanghai Asia/Chongqing Asia/Harbin Asia/Urumqi Asia/Hong_Kong Asia/Macau Asia/Taipei', 'fr nl de': 'Europe/Brussels', 'fr en': 'America/Toronto' };
  I.region = (tz) => {
    try { tz = tz || Intl.DateTimeFormat().resolvedOptions().timeZone || ''; } catch (e) { tz = ''; }
    for (const [langs, zones] of Object.entries(Z)) if (zones.split(' ').includes(tz)) return langs.split(' ');
    return [];
  };
  // I18N-040 (owner: "people see Arabic and delete the software"): the product always opens in English; the language
  // the person chooses by hand is remembered. (The region rule above stays for whoever asks I.region.)
  I.pick = (fallback, tz, browser) => norm(store.get()) || 'en';
  I.pickByRegion = (fallback, tz, browser) => {
    const saved = norm(store.get()); if (saved) return saved;
    const nav = (browser || navigator.languages || [navigator.language]).map(norm).filter(Boolean);
    const region = I.region(tz);
    return (region.length > 1 ? region.find((l) => nav.includes(l)) || region[0] : region[0]) || nav[0] || norm(fallback) || 'en';
  };
  I.load = async (lang) => {
    I.lang = norm(lang) || 'en'; I.dict = {}; I.tpl = [];
    if (I.lang !== 'en') {
      try { const r = await fetch(I.base + I.lang + '.json', { cache: 'no-cache' }); if (r.ok) I.dict = (await r.json()).strings || {}; } catch (e) { }
      // a placeholder used twice is matched once and then by reference (one bad key must never stop the whole language)
      for (const k of Object.keys(I.dict)) if (k.includes('{0}')) {
        const seen = new Set();
        try { I.tpl.push([new RegExp('^' + k.replace(/[.*+?^${}()|[\]\\]/g, '\\$&').replace(/\\\{(\d)\\\}/g, (m, n) => (seen.has(n) ? '\\k<a' + n + '>' : (seen.add(n), '(?<a' + n + '>[\\s\\S]*?)'))) + '$'), I.dict[k]]); } catch (e) { }
      }
      I.tpl.sort((a, b) => b[0].source.length - a[0].source.length);
    }
    I.apply();
    return I.lang;
  };
  I.apply = () => { document.documentElement.lang = I.lang; document.documentElement.dir = RTL.includes(I.lang) ? 'rtl' : 'ltr'; };
  I.rtl = () => RTL.includes(I.lang);
  const fmt = (s, a) => String(s).replace(/\{(\d)\}/g, (m, i) => a[i] !== undefined && a[i] !== null ? a[i] : m);
  // t('Text {0}', value): a text of this program
  I.t = (s, ...a) => fmt(I.dict[s] ?? s, a);
  // tr(message): a finished English text (from the server): exact, else by template
  I.tr = (s) => {
    if (s === null || s === undefined || I.lang === 'en') return s;
    s = String(s);
    if (I.dict[s] !== undefined) return I.dict[s];
    for (const [re, v] of I.tpl) { const m = s.match(re); if (m) { const a = []; for (const [k, x] of Object.entries(m.groups || {})) a[+k.slice(1)] = I.dict[x] ?? x; return fmt(v, a); } }
    return s;
  };
  I.date = (d, withTime) => { try { const x = d instanceof Date ? d : new Date(d); return isNaN(x) ? String(d || '') : x.toLocaleString(I.lang, withTime === false ? { dateStyle: 'medium' } : { dateStyle: 'medium', timeStyle: 'short' }); } catch (e) { return String(d || ''); } };
  // a language menu: changing it reloads the page in that language
  I.selector = () => {
    const s = document.createElement('select'); s.setAttribute('aria-label', 'Language'); s.className = 'lang';
    for (const [k, v] of Object.entries(NAMES)) { const o = document.createElement('option'); o.value = k; o.textContent = v; if (k === I.lang) o.selected = true; s.append(o); }
    s.onchange = () => { store.set(s.value); location.reload(); };
    return s;
  };
  // DESIGN-060: every table with a header becomes cards on a phone (theme.css) — each cell gets its column's name
  const cards = () => { for (const tb of document.querySelectorAll('table')) {
    const hs = Array.from(tb.querySelectorAll(':scope > thead th')).map((x) => x.textContent.trim()); if (!hs.length) continue;
    tb.classList.add('cards');
    for (const r of tb.querySelectorAll(':scope > tbody > tr')) Array.from(r.children).forEach((c, i) => { if (c.getAttribute('data-label') !== (hs[i] || '')) c.setAttribute('data-label', hs[i] || ''); });
  } };
  if (typeof MutationObserver !== 'undefined') { const start = () => { cards(); new MutationObserver(cards).observe(document.body, { childList: true, subtree: true }); }; if (document.body) start(); else document.addEventListener('DOMContentLoaded', start); }
  g.I18N = I; g.t = I.t; g.tr = I.tr;
})(window);
