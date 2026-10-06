'use strict';
// UI-020: the screen robot. Walks every screen of the product in every language (and two pseudo languages that make
// every text ~40% longer, one of them right to left) on a computer and a phone, and checks what a person would notice:
// the page's direction, text aligned to the wrong side, fields that do not line up with their labels, elements on top of
// each other, cut or overflowing text, sideways scrolling, "null" / "undefined" / "{0}" on the screen, untranslated
// texts, script errors. Writes OUT/index.html (every problem with a screenshot) and OUT/report.json; exit 1 on problems.
const fs = require('fs'), path = require('path');
const { chromium } = require('playwright');
const OUT = process.env.OUT || 'ui-report';
const LANGS = (process.env.LANGS || 'en he ar de es pt fr it nl pl tr ja zh qps qps-rtl').split(/\s+/).filter(Boolean);
// UI-030: real devices — a phone is a touch screen with the browser's phone layout (the page's viewport tag counts), not just a narrow window
const DEVICE_LIST = {
  desktop: { viewport: { width: 1280, height: 800 } },
  phone: { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2, userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1' },
  'phone-small': { viewport: { width: 360, height: 640 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2, userAgent: 'Mozilla/5.0 (Linux; Android 13; SM-A135F) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Mobile Safari/537.36' },
  'phone-landscape': { viewport: { width: 844, height: 390 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2, userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1' },
  tablet: { viewport: { width: 820, height: 1180 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2, userAgent: 'Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1' },
};
const VIEWPORTS = (process.env.DEVICES || 'desktop phone').split(/\s+/).filter(Boolean).map((d) => { if (!DEVICE_LIST[d]) throw new Error('unknown device ' + d); return [d, DEVICE_LIST[d]]; });
const U = { admin: process.env.ADMIN_URL, client: process.env.CLIENT_URL, portal: process.env.PORTAL_URL, setup: process.env.SETUP_URL, csetup: process.env.CSETUP_URL };
const RTL = ['he', 'ar', 'qps-rtl'];

// pseudo languages: every English text longer, with accents, placeholders kept — layout problems show without knowing a language
const pseudo = (s) => '[' + s.replace(/\{\d\}|<[^>]+>|./g, (c) => c.length > 1 ? c : ({ a: 'á', e: 'é', i: 'í', o: 'ó', u: 'ú', A: 'Å', E: 'É', O: 'Ö', c: 'ç', n: 'ñ' })[c] || c) + ' ~~~~' + '~'.repeat(Math.ceil(s.length * 0.2)) + ']';
async function dictionary(lang) {
  if (lang === 'en') return {};
  if (lang.startsWith('qps')) {
    const he = await (await fetch(U.admin + '/i18n/he.json')).json();
    const d = {}; for (const k of Object.keys(he.strings)) d[k] = pseudo(k); return d;
  }
  return (await (await fetch(U.admin + '/i18n/' + lang + '.json')).json()).strings;
}

// ---- the checks, inside the page ----
function inspect(args) {
  const rtl = args.rtl, translatedKeys = args.translatedKeys ? new Set(args.translatedKeys) : null;
  const problems = [];
  // an element scrolled out of a scrolling box (a tree, a list) is not on the screen
  const clipped = (e, r) => { for (let a = e.parentElement; a && a !== document.body; a = a.parentElement) { const st = getComputedStyle(a); if (/(auto|scroll|hidden)/.test(st.overflowY + st.overflowX)) { const b = a.getBoundingClientRect(); if (r.bottom <= b.top + 1 || r.top >= b.bottom - 1 || r.right <= b.left + 1 || r.left >= b.right - 1) return true; } } return false; };
  const visible = (e) => { const r = e.getBoundingClientRect(); const s = getComputedStyle(e); return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none' && !e.closest('[hidden]') && !clipped(e, r); };
  const island = (e) => !!e.closest('.ltr, .num, .mono, pre, code, [dir=ltr], input, textarea, select, img, svg');
  const name = (e) => (e.tagName.toLowerCase() + (e.id ? '#' + e.id : '') + (e.className && typeof e.className === 'string' ? '.' + e.className.trim().split(/\s+/).join('.') : '') + ' "' + (e.textContent || e.value || '').trim().slice(0, 40) + '"');
  // 1. direction of the page
  if ((document.documentElement.dir || 'ltr') !== (rtl ? 'rtl' : 'ltr')) problems.push(['direction', 'page dir is "' + document.documentElement.dir + '"']);
  // 2. sideways scrolling
  // on a phone the browser widens its window to fit what sticks out (and zooms the whole page out): measure against the screen
  const SW = args.width || window.innerWidth;
  if (document.documentElement.scrollWidth > SW + 2) { const W = SW, rtl = getComputedStyle(document.documentElement).direction === 'rtl';
    // the innermost element that reaches out of the screen — what to fix
    const out = Array.from(document.body.querySelectorAll('*')).filter((e) => { const r = e.getBoundingClientRect(); return r.width > 0 && (rtl ? r.left < -2 : r.right > W + 2) && !clipped(e, r); });
    const spill = Array.from(document.body.querySelectorAll('main *, #main *')).filter((e) => e.scrollWidth > e.clientWidth + 2 && e.clientWidth > 0 && getComputedStyle(e).overflowX === 'visible');
    const deep = spill.filter((e) => !spill.some((o) => o !== e && e.contains(o)));
    const leaves = out.filter((e) => !out.some((o) => o !== e && e.contains(o))); const leaf = deep[0] || leaves.find((e) => e.closest('main, #main')) || leaves[0];
    problems.push(['overflow', 'the page scrolls sideways: ' + document.documentElement.scrollWidth + 'px wide in ' + W + 'px' + (leaf ? ' — ' + leaf.tagName.toLowerCase() + (leaf.className && typeof leaf.className === 'string' ? '.' + leaf.className.split(' ').join('.') : '') + ' "' + (leaf.textContent || '').trim().slice(0, 40) + '"' : '')]); }
  // with a modal window open, only the window counts (the page behind it is covered on purpose)
  const modal = document.querySelector('dialog[open]');
  const all = Array.from((modal || document.body).querySelectorAll('*')).filter(visible);
  const scrolls = (e) => { for (let a = e.parentElement; a && a !== document.body; a = a.parentElement) { const o = getComputedStyle(a).overflowX; if (o === 'auto' || o === 'scroll') return true; } return false; };
  const texts = all.filter((e) => Array.from(e.childNodes).some((n) => n.nodeType === 3 && n.textContent.trim().length > 1));
  for (const e of texts) {
    const own = Array.from(e.childNodes).filter((n) => n.nodeType === 3).map((n) => n.textContent).join(' ').trim();
    // 3. leftovers of templates and code
    if (/\bnull\b|\bundefined\b|\{\d\}|\[object |NaN/.test(own) && !island(e)) problems.push(['text', name(e) + ' shows "' + own.slice(0, 60) + '"']);
    // 4. untranslated (the English text of a key that has a translation)
    if (translatedKeys && translatedKeys.has(own) && !island(e)) problems.push(['untranslated', name(e)]);
    // 5. aligned to the wrong side
    const ta = getComputedStyle(e).textAlign;
    if (!island(e) && ((rtl && ta === 'left') || (!rtl && ta === 'right'))) problems.push(['alignment', name(e) + ' is text-align: ' + ta]);
    // 6b. squeezed text: a title pressed into a narrow column (one or two letters per line)
    { const r = e.getBoundingClientRect(), lh = parseFloat(getComputedStyle(e).lineHeight) || parseFloat(getComputedStyle(e).fontSize) * 1.3;
      if (own.length > 6 && r.width < 60 && r.height > lh * 3.5) problems.push(['squeezed', name(e) + ' is squeezed into ' + Math.round(r.width) + 'px']); }
    // 6c. a word broken in the middle (a column too narrow for its words — unreadable on a phone)
    { const r0 = e.getBoundingClientRect(), lh0 = parseFloat(getComputedStyle(e).lineHeight) || parseFloat(getComputedStyle(e).fontSize) * 1.3;
      if (r0.height > lh0 * 1.6 && !e.closest('pre, code, .mono, input, textarea, select, svg')) {   // identifiers (e-mails, IDs, paths) may wrap anywhere
        let broken = null;
        for (const n of Array.from(e.childNodes).filter((x) => x.nodeType === 3)) {
          const txt = n.textContent; const re = /[\p{L}\p{M}\p{N}]{4,}/gu; let m;
          while (!broken && (m = re.exec(txt))) {
            if (/[\/\\]/.test(txt[m.index - 1] || '')) continue;   // a part of a path: a path may wrap at any place
            if (/[\p{sc=Han}\p{sc=Hiragana}\p{sc=Katakana}\p{sc=Hangul}]/u.test(m[0])) continue;   // these scripts wrap between any two characters by design
            if (m[0].length >= 12 && getComputedStyle(e).hyphens === 'auto') continue;
            if (m[0].length >= 16 && /\d/.test(m[0]) && /^[\p{L}\p{N}]+$/u.test(m[0]) && !/^\p{L}+$/u.test(m[0])) continue;   // an ID (snapshot, hash) is not a word   // a long compound word hyphenated by the language's rules
            const rg = document.createRange(); rg.setStart(n, m.index); rg.setEnd(n, m.index + m[0].length);
            const tops = new Set(Array.from(rg.getClientRects()).filter((q) => q.width > 0).map((q) => Math.round(q.top)));
            if (tops.size > 1) broken = m[0];
          }
          if (broken) break;
        }
        if (broken) problems.push(['broken-word', name(e) + ' breaks the word "' + broken.slice(0, 30) + '" across lines (' + Math.round(r0.width) + 'px wide)']);
      } }
    // 6. cut text
    if ((e.scrollWidth > e.clientWidth + 2) && ['hidden', 'clip'].includes(getComputedStyle(e).overflowX) && e.clientWidth > 0) problems.push(['cut', name(e) + ' text is cut (' + e.scrollWidth + ' > ' + e.clientWidth + ')']);
  }
  // 7. labels and their fields on the same side
  for (const l of all.filter((e) => e.tagName === 'LABEL')) {
    const f = l.nextElementSibling;
    if (!f || !/^(INPUT|SELECT|TEXTAREA)$/.test(f.tagName) || !visible(f) || f.type === 'checkbox' || f.type === 'radio') continue;
    const a = l.getBoundingClientRect(), b = f.getBoundingClientRect();
    if (b.top < a.bottom - 2) continue;   // side by side, not label above field
    const off = rtl ? Math.abs(a.right - b.right) : Math.abs(a.left - b.left);
    if (off > 6) problems.push(['field', name(l) + ' and its field do not line up (' + Math.round(off) + 'px)']);
  }
  // 8. elements on top of each other (controls and text blocks that are not inside one another)
  // the part of an element that is on the screen: a scrolling box hides what is scrolled out of it
  const seen = (e) => { const r = e.getBoundingClientRect(); let l = r.left, t = r.top, rr = r.right, bb = r.bottom;
    for (let a = e.parentElement; a && a !== document.body; a = a.parentElement) { const st = getComputedStyle(a); if (/(auto|scroll|hidden)/.test(st.overflowY + st.overflowX)) { const q = a.getBoundingClientRect(); l = Math.max(l, q.left); t = Math.max(t, q.top); rr = Math.min(rr, q.right); bb = Math.min(bb, q.bottom); } }
    return { left: l, top: t, right: rr, bottom: bb }; };
  const solid = all.filter((e) => /^(BUTTON|INPUT|SELECT|TEXTAREA|LABEL|A|H1|H2|H3)$/.test(e.tagName) && !e.closest('dialog:not([open])'));
  for (let i = 0; i < solid.length; i++) for (let j = i + 1; j < solid.length; j++) {
    const x = solid[i], y = solid[j];
    if (x.contains(y) || y.contains(x)) continue;
    const a = seen(x), b = seen(y);
    const w = Math.min(a.right, b.right) - Math.max(a.left, b.left), h = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
    if (w > 4 && h > 4) problems.push(['overlap', name(x) + ' overlaps ' + name(y)]);
  }
  // 9. content wider than its box (buttons, cells) on a phone
  for (const e of all.filter((e) => /^(BUTTON|TD|TH|LABEL)$/.test(e.tagName))) {
    const r = e.getBoundingClientRect();
    if ((r.right > window.innerWidth + 2 || r.left < -2) && !scrolls(e)) problems.push(['offscreen', name(e) + ' is outside the screen']);
  }
  if (args.touch) {
    // 10. a finger needs a target of at least 24 x 24 px (WCAG 2.2, 2.5.8) — a link inside a sentence is excused
    for (const e of all.filter((e) => e.matches('button, a[href], select, input:not([type=hidden]), [role=button], [role=tab], summary'))) {
      let t = e; if ((e.type === 'checkbox' || e.type === 'radio') && e.closest('label')) t = e.closest('label');
      const r = t.getBoundingClientRect();
      if (e.tagName === 'A' && e.parentElement && /^(P|LI|SPAN|TD)$/.test(e.parentElement.tagName) && e.parentElement.textContent.trim().length > e.textContent.trim().length + 10) continue;
      if (r.width < 24 || r.height < 24) problems.push(['tap-target', name(e) + ' is ' + Math.round(r.width) + ' x ' + Math.round(r.height) + 'px — too small for a finger']);
    }
    // 11. an iPhone zooms into a field whose letters are smaller than 16px, and the page stays zoomed
    for (const e of all.filter((e) => e.matches('input:not([type=checkbox]):not([type=radio]):not([type=range]):not([type=hidden]):not([type=color]), select, textarea')))
      if (parseFloat(getComputedStyle(e).fontSize) < 16) problems.push(['ios-zoom', name(e) + ' has ' + getComputedStyle(e).fontSize + ' letters — the phone zooms in on it']);
    // 12. letters too small to read on a phone
    for (const e of texts) if (parseFloat(getComputedStyle(e).fontSize) < 11) problems.push(['tiny-text', name(e) + ' has ' + getComputedStyle(e).fontSize + ' letters']);
  }
  return problems;
}

// ---- the screens ----
const closeDialogs = (p) => p.evaluate(() => document.querySelectorAll('dialog').forEach((d) => { try { d.close(); } catch (e) { } d.remove(); }));
// TECH-010: the administrator's two-step code (RFC 6238, from the test secret of run.sh)
function totp(secret) {
  const A = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'; let bits = ''; for (const ch of secret.replace(/=+$/, '')) bits += A.indexOf(ch).toString(2).padStart(5, '0');
  const key = Buffer.from(bits.match(/.{8}/g).map((b) => parseInt(b, 2)));
  const msg = Buffer.alloc(8); msg.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30000)));
  const hm = require('crypto').createHmac('sha1', key).update(msg).digest(); const o = hm[19] & 15;
  return String(((hm.readUInt32BE(o) & 0x7fffffff) % 1000000)).padStart(6, '0');
}
async function click(p, sel) { await p.locator(sel).first().click(); await p.waitForTimeout(500); }
async function rail(p, k) { await click(p, '.rail button[data-k=' + k + ']'); await p.waitForSelector('main.pg .head', { timeout: 10000 }); }
async function fill(p, sel, v) { await p.locator(sel).first().fill(v); }
const SCREENS = [
  ['admin', 'sign-in', async (p) => { await p.evaluate(() => { try { sessionStorage.clear(); } catch (e) { } }); await p.goto(U.admin + '/admin/'); await p.waitForSelector('form.login'); }],
  ['admin', 'dashboard', async (p) => { await fill(p, 'form.login input >> nth=0', 'admin'); await fill(p, 'form.login input[type=password]', 'Admin-Pass-123'); await fill(p, 'form.login input >> nth=2', totp(process.env.ADMIN_TOTP || '')); await click(p, 'form.login button[type=submit]'); await p.waitForSelector('.rail button'); await p.waitForSelector('.board'); }],
  ['admin', 'customers', async (p) => { await rail(p, 'cust'); await p.waitForSelector('table'); }],
  ['admin', 'customer', async (p) => { await click(p, 'tbody tr.click'); await p.waitForSelector('.tabs button[data-k=sets]'); }],
  ...['overview', 'pcs', 'contacts', 'quota', 'sec', 'calls', 'reports', 'logs'].map((k) => ['admin', 'customer ' + k, async (p) => { await click(p, '.tabs button[data-k=' + k + ']'); await p.waitForTimeout(400); }]),
  ['admin', 'set', async (p) => { await click(p, '.tabs button[data-k=sets]'); await click(p, 'tbody tr.click'); await p.waitForSelector('.savebar'); }],
  ...['src', 'sched', 'method', 'dest', 'ret', 'filter', 'enc', 'perf', 'cmd', 'rep', 'maint'].map((k) => ['admin', 'set ' + k, async (p) => { await click(p, '.tabs button[data-k=' + k + ']'); }]),
  // an SQL Server set: its own "what to back up" (server, sa, databases) and method (full / differential / logs)
  ['admin', 'set sql', async (p) => { await p.locator('.crumbs button').last().click(); await p.waitForTimeout(500); await click(p, 'tbody tr.click >> text=SQL Server'); await p.waitForSelector('.savebar'); await click(p, '.tabs button[data-k=src]'); }],
  ['admin', 'set sql method', async (p) => { await click(p, '.tabs button[data-k=method]'); }],
  ...['allsets', 'tasks', 'live', 'tickets', 'ai', 'logs', 'reports', 'restoretests', 'storage', 'license', 'defaults', 'policies', 'notify', 'admins', 'tset', 'security', 'time', 'integr', 'contract', 'brand'].map((k) => ['admin', k, async (p) => { await rail(p, k); await p.waitForSelector('main.pg .head'); }]),
  ['admin', 'new call', async (p) => { await rail(p, 'tickets'); await click(p, 'main.pg .head .acts .btn.pri'); await p.waitForSelector('.savebar'); }],
  ['admin', 'new administrator', async (p) => { await rail(p, 'admins'); await click(p, 'main.pg .head .acts .btn.pri'); await p.waitForSelector('.savebar'); }],
  ['restore', 'sign-in', async (p) => { await p.goto(U.admin + '/restore'); await p.waitForSelector('input[type=password]'); }],
  ['restore', 'backups', async (p) => { await fill(p, 'main input >> nth=0', 'dana-office'); await fill(p, 'main input[type=password]', 'Customer-Pass-1'); await click(p, 'main button.primary'); await p.waitForSelector('.row button'); }],
  ['restore', 'password', async (p) => { await click(p, '.row button'); await p.waitForSelector('input[type=password]'); }],
  ['restore', 'points', async (p) => { await fill(p, 'input[type=password]', 'Customer-Pass-1'); await click(p, 'button.primary'); await p.waitForSelector('form.search'); }],
  ['restore', 'search', async (p) => { await fill(p, 'form.search input', 'budget'); await click(p, 'form.search button'); await p.waitForSelector('table.found', { timeout: 20000 }); }],
  ['client', 'status', async (p) => { await p.goto(U.client); await p.waitForSelector('nav button'); await p.waitForTimeout(800); }],
  ['client', 'restore', async (p) => { await click(p, 'nav button >> nth=1'); await p.waitForTimeout(500); }],
  ['client', 'sign-in', async (p) => { await p.waitForSelector('dialog[open]', { timeout: 5000 }); }],
  ['client', 'new backup', async (p) => { await closeDialogs(p); await click(p, 'nav button >> nth=2'); await closeDialogs(p); }],
  ['client', 'security', async (p) => { await closeDialogs(p); await click(p, 'nav button >> nth=3'); }],
  ['client', 'help', async (p) => { await closeDialogs(p); await click(p, 'nav button >> nth=4'); await p.waitForTimeout(1000); }],
  ['client', 'change backup', async (p) => { await closeDialogs(p); await click(p, 'nav button >> nth=0'); await p.waitForSelector('main .card button.sec'); await click(p, 'main .card button.sec'); await p.waitForTimeout(1200); }],
  ['portal', 'welcome', async (p) => { await p.goto(U.portal + '/portal'); await p.waitForSelector('.hero'); }],
  ['portal', 'sign-in', async (p) => { await click(p, '.tabs button >> nth=1'); }],
  ['portal', 'partner', async (p, t, lang, vp) => {
    await click(p, '.tabs button >> nth=0');
    const f = p.locator('.auth form input');
    const v = ['Robot IT', 'Robot', 'robot-' + lang + '-' + vp + '-' + Date.now() + '@example.invalid', '03-5550000', 'Israel', 'Robot-Pass-2026'];
    for (let i = 0; i < v.length; i++) await f.nth(i).fill(v[i]);
    await p.check('#terms'); await click(p, '.auth button.primary'); await p.waitForSelector('.preview');
  }],
  ['portal', 'owner', async (p) => {
    await p.evaluate(() => sessionStorage.clear()); await p.goto(U.portal + '/portal'); await p.waitForSelector('.hero'); await click(p, '.tabs button >> nth=1');
    const f = p.locator('.auth form input'); await f.nth(0).fill('owner'); await f.nth(1).fill('Owner-Pass-2026'); await click(p, '.auth button.primary'); await p.waitForSelector('table');
  }],
  ['setup', 'welcome', async (p) => { await p.goto(U.setup); await p.waitForSelector('#card button.primary'); }],
  ['setup', 'company', async (p) => { await click(p, '#card button.primary'); await p.waitForSelector('#product'); }],
  ['setup', 'storage', async (p) => { await fill(p, '#product', 'Robot Backup'); await fill(p, '#company', 'Robot IT'); await click(p, '#card button.primary'); await p.waitForTimeout(500); }],
  // SETUP-C10: the client software's installation wizard — welcome, the server (checked), the agreement when there is one, the account
  ['csetup', 'welcome', async (p) => { await p.goto(U.csetup); await p.waitForSelector('#card button.primary'); }],
  ['csetup', 'server', async (p) => { await click(p, '#card button.primary'); await p.waitForSelector('#server'); }],
  ['csetup', 'server checked', async (p) => { await click(p, '#check'); await p.waitForSelector('#card .check'); }],
  ['csetup', 'account', async (p) => {
    await click(p, '#card button.primary');
    if (await p.locator('#card .contract').count()) { await p.locator('#card label.opt input').check(); await click(p, '#card button.primary'); }
    await click(p, '#card .choice button'); await p.waitForSelector('#company');
  }],
];

(async () => {
  fs.mkdirSync(OUT, { recursive: true });
  const REC = {}; let SEEDED = false;
  const b = await chromium.launch({ executablePath: process.env.CHROMIUM || (fs.existsSync('/opt/pw-browsers/chromium') ? '/opt/pw-browsers/chromium' : undefined) });
  const found = []; let shots = 0, screens = 0;
  for (const lang of LANGS) {
    const dict = await dictionary(lang);
    const t = (s) => dict[s] ?? s;
    const translatedKeys = lang === 'en' || lang.startsWith('qps') ? null : Object.entries(dict).filter(([k, v]) => v && v !== k && k.length > 3).map(([k]) => k);
    const real = lang === 'qps' ? 'de' : lang === 'qps-rtl' ? 'he' : lang;
    for (const [vp, dev] of VIEWPORTS) {
      const ctx = await b.newContext(dev);
      // only the product's own pages: an outside font or script that cannot load must not hold the screenshots
      await ctx.route(/^https?:\/\/(?!localhost|127\.0\.0\.1)/, (r) => r.abort());
      await ctx.addInitScript((l) => { try { localStorage.setItem('obLang', l); } catch (e) { } }, real);
      if (lang.startsWith('qps')) await ctx.route('**/i18n/' + real + '.json', (r) => r.fulfill({ contentType: 'application/json', body: JSON.stringify({ name: lang, dir: RTL.includes(lang) ? 'rtl' : 'ltr', strings: dict }) }));
      const p = await ctx.newPage(); const errors = [];
      // RECORD=<file>: keep every API answer (GET) — the data of the clickable demo of the admin site
      if (process.env.RECORD) p.on('response', async (r) => { try { const u = new URL(r.url()); if (r.request().method() === 'GET' && u.pathname.startsWith('/api/') && r.status() === 200) REC[u.pathname + u.search] = await r.text(); } catch (e) { } });
      p.on('pageerror', (e) => errors.push(e.message));
      p.on('console', (m) => { if (m.type() === 'error' && !/favicon|404|401/.test(m.text())) errors.push(m.text()); });
      for (const [app, screen, go] of SCREENS) {
        if (!U[app]) continue;
        const id = [app, screen, lang, vp].join(' / ');
        if (process.env.ONLY && !process.env.ONLY.split('|').some((o) => id.includes(o))) continue;   // ONLY=a|b: screens whose name has one of them
        errors.length = 0;
        const t0 = Date.now(); if (process.env.TRACE) console.log('screen: ' + id);
        try { await go(p, t, lang, vp); } catch (e) { found.push({ id, check: 'navigation', detail: e.message.split('\n')[0] }); continue; }
        if (process.env.RECORD && screen === 'dashboard' && !SEEDED) {
          SEEDED = true;   // the demo: a few more customers (signed up from the client) and service calls of every kind
          await p.evaluate(async () => {
            const x = (o) => '<m>' + Object.entries(o).map(([k, v]) => '<f n="' + k + '">' + String(v).replace(/&/g, '&amp;').replace(/</g, '&lt;') + '</f>').join('') + '</m>';
            const post = (u, o, adm) => fetch(u, { method: 'POST', headers: Object.assign({ 'Content-Type': 'application/xml' }, adm ? { 'X-Session': sessionStorage.getItem('obAdmin') } : {}), body: x(o) });
            const C = [['North Law', 'north-law'], ['River Dental Clinic', 'river-dental'], ['Stone CPA', 'stone-cpa'], ['Nof Architects', 'sky-arch'], ['Alpha Labs', 'alpha-labs']];
            for (const [company, login] of C) await post('/api/signup', { company, email: 'it@' + login + '.example', login, password: 'Demo-Pass-1' });
            const T = [['river-dental', 'The backup failed 2 nights in a row', 'Urgent', 'New', 'auto'], ['stone-cpa', 'Restore a deleted Excel file from last week', 'High', 'InProgress', 'phone'],
              ['dana-office', 'Add the Scans folder to the backup', 'Normal', 'Waiting', 'client'], ['sky-arch', 'Quota almost full (92%)', 'Normal', 'New', 'auto'], ['alpha-labs', 'Monthly restore test passed — report to the customer', 'Low', 'Resolved', 'email'], ['north-law', 'New server — move the backup sets', 'High', 'InProgress', 'phone']];
            for (const [login, subject, priority, status, channel] of T) await post('/api/admin/tickets', { login, subject, priority, status, channel, description: subject }, true);
            // every set's own pages (settings, reports, computers) — the demo opens any of them, the SQL Server set too
            const H = { 'X-Session': sessionStorage.getItem('obAdmin') };
            const xml = new DOMParser().parseFromString(await (await fetch('/api/admin/users', { headers: H })).text(), 'application/xml');
            for (const u of xml.querySelectorAll('l[n=users] > i')) {
              const login = u.querySelector(':scope > f[n=login]').textContent;
              for (const k of ['computers', 'folders']) await fetch('/api/admin/users/' + login + '/' + k, { headers: H });
              for (const st of u.querySelectorAll('l[n=sets] > i > f[n=id]')) for (const k of ['', '/runs']) await fetch('/api/admin/users/' + login + '/sets/' + st.textContent + k, { headers: H });
            }
          });
          await p.reload(); await p.waitForSelector('.board'); errors.length = 0;   // the seeding's own answers are not the product's
        }
        await p.waitForTimeout(250); screens++;
        let problems = await p.evaluate(inspect, { rtl: RTL.includes(lang), translatedKeys, touch: !!dev.hasTouch, width: dev.viewport.width }).catch((e) => [['robot', e.message]]);
        if (Date.now() - t0 > 8000) console.log('slow: ' + id + ' ' + Math.round((Date.now() - t0) / 1000) + ' s');
        problems = problems.concat(errors.map((e) => ['script', e]));
        if (problems.length || process.env.SHOTS === 'all') {
          const file = id.replace(/[^a-z0-9-]+/gi, '_') + '.png';
          await p.screenshot({ path: path.join(OUT, file), fullPage: true }).catch(() => {}); shots++;
          const seen = new Set();
          for (const [check, detail] of problems) { const k = check + detail; if (seen.has(k)) continue; seen.add(k); found.push({ id, check, detail, file }); }
        }
      }
      await ctx.close();
    }
    if (process.env.RECORD) fs.writeFileSync(process.env.RECORD, JSON.stringify(REC));
    console.log(lang + ': ' + found.filter((f) => f.id.includes(' / ' + lang + ' / ')).length + ' problems');
  }
  await b.close();
  fs.writeFileSync(path.join(OUT, 'report.json'), JSON.stringify(found, null, 1));
  const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
  const byCheck = {}; for (const f of found) byCheck[f.check] = (byCheck[f.check] || 0) + 1;
  fs.writeFileSync(path.join(OUT, 'index.html'), '<!doctype html><meta charset="utf-8"><title>Screen check</title><style>body{font:14px system-ui;margin:20px}td,th{border-bottom:1px solid #ddd;padding:4px 6px;text-align:start;vertical-align:top}img{max-width:260px;border:1px solid #ccc}</style>'
    + '<h1>Screen check</h1><p>' + screens + ' screens · ' + LANGS.length + ' languages · ' + found.length + ' problems</p><p>' + Object.entries(byCheck).map(([k, v]) => esc(k) + ': ' + v).join(' · ') + '</p>'
    + '<table><tr><th>Screen</th><th>Check</th><th>Detail</th><th></th></tr>' + found.map((f) => '<tr><td>' + esc(f.id) + '</td><td>' + esc(f.check) + '</td><td>' + esc(f.detail) + '</td><td>' + (f.file ? '<a href="' + esc(f.file) + '"><img loading="lazy" src="' + esc(f.file) + '"></a>' : '') + '</td></tr>').join('') + '</table>');
  console.log(screens + ' screens checked, ' + found.length + ' problems → ' + path.join(OUT, 'index.html'));
  // in the CI log too (its report is a download): each kind of problem once, with how often and where
  const groups = {}; for (const f of found) { const k = f.check + ' | ' + f.detail.slice(0, 140); (groups[k] = groups[k] || []).push(f.id); }
  for (const [k, ids] of Object.entries(groups).sort((a, b) => b[1].length - a[1].length).slice(0, 80)) console.log('  ' + ids.length + '× ' + k + '  [' + ids.slice(0, 3).join('; ') + ']');
  process.exit(found.length ? 1 : 0);
})();
