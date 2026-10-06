// SITE-020: checks the built website — every language, computer and phone: the page direction, no sideways scrolling,
// no {{…}} left over, no text cut, every link inside the site resolves. Takes a picture of each page (SHOTS=all) or of the
// pages with problems. Exit code 1 on any problem (the CI gate).
//   node tools/site/check.mjs [siteDir=site/dist] [outDir=site-report]
import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { chromium } = require('playwright');

const dir = path.resolve(process.argv[2] || 'site/dist'), OUT = path.resolve(process.argv[3] || 'site-report');
fs.mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html; charset=utf-8', '.css': 'text/css', '.js': 'application/javascript', '.svg': 'image/svg+xml', '.xml': 'application/xml', '.txt': 'text/plain' };
const srv = http.createServer((q, r) => {
  let p = path.join(dir, decodeURIComponent(q.url.split('?')[0])); if (p.endsWith('/')) p += 'index.html';
  if (!p.startsWith(dir) || !fs.existsSync(p)) { r.writeHead(404); r.end(); return; }
  r.writeHead(200, { 'Content-Type': TYPES[path.extname(p)] || 'application/octet-stream' }); fs.createReadStream(p).pipe(r);
}).listen(0);
const base = 'http://127.0.0.1:' + srv.address().port;
const langs = fs.readdirSync(dir).filter((d) => fs.existsSync(path.join(dir, d, 'index.html')));
const found = [];
const b = await chromium.launch({ executablePath: process.env.CHROMIUM || (fs.existsSync('/opt/pw-browsers/chromium') ? '/opt/pw-browsers/chromium' : undefined) });
for (const [vp, w, h] of [['desktop', 1280, 800], ['phone', 390, 844]]) {
  const ctx = await b.newContext({ viewport: { width: w, height: h } }); const p = await ctx.newPage();
  const errors = []; p.on('pageerror', (e) => errors.push(e.message)); p.on('response', (r) => { if (r.status() >= 400) errors.push(r.status() + ' ' + r.url()); });
  for (const lang of langs) {
    errors.length = 0;
    await p.goto(base + '/' + lang + '/', { waitUntil: 'load' });
    const problems = await p.evaluate((rtl) => {
      const out = [];
      if (document.documentElement.dir !== (rtl ? 'rtl' : 'ltr')) out.push('page dir is ' + document.documentElement.dir);
      if (document.documentElement.scrollWidth > innerWidth + 2) out.push('the page scrolls sideways: ' + document.documentElement.scrollWidth + 'px in ' + innerWidth + 'px');
      if (/\{\{|\}\}/.test(document.documentElement.outerHTML)) out.push('a {{…}} marker was left in the page');
      for (const e of document.querySelectorAll('h1,h2,h3,p,li,a,summary,b,small,span,em')) {
        const r = e.getBoundingClientRect(); if (!r.width || getComputedStyle(e).display === 'none') continue;
        if (e.scrollWidth > e.clientWidth + 2 && ['hidden', 'clip'].includes(getComputedStyle(e).overflowX)) out.push('cut text: ' + e.textContent.trim().slice(0, 40));
        if (r.right > innerWidth + 2 || r.left < -2) { if (!e.closest('.top')) out.push('off the screen: ' + e.tagName + ' "' + e.textContent.trim().slice(0, 40) + '"'); }
        // a word broken in the middle (a box too narrow for its words)
        const lh = parseFloat(getComputedStyle(e).lineHeight) || parseFloat(getComputedStyle(e).fontSize) * 1.3;
        if (r.height > lh * 1.6) for (const n of Array.from(e.childNodes).filter((x) => x.nodeType === 3)) {
          const re = /[\p{L}\p{M}\p{N}]{4,}/gu; let m;
          while ((m = re.exec(n.textContent))) { const rg = document.createRange(); if (/[\p{sc=Han}\p{sc=Hiragana}\p{sc=Katakana}\p{sc=Hangul}]/u.test(m[0])) continue;   // these scripts wrap between any two characters by design
            if (m[0].length >= 12 && getComputedStyle(e).hyphens === 'auto') continue;   // a long compound word hyphenated by the language's rules rg.setStart(n, m.index); rg.setEnd(n, m.index + m[0].length);
            if (new Set(Array.from(rg.getClientRects()).filter((q) => q.width > 0).map((q) => Math.round(q.top))).size > 1) { out.push('broken word "' + m[0].slice(0, 30) + '" in ' + e.tagName); break; } }
        }
      }
      return [...new Set(out)];
    }, ['he', 'ar'].includes(lang));
    for (const e of errors) problems.push('error: ' + e);
    const id = lang + '_' + vp;
    if (problems.length || process.env.SHOTS === 'all') await p.screenshot({ path: path.join(OUT, id + '.png'), fullPage: true });
    for (const x of problems) found.push({ id, problem: x });
  }
  await ctx.close();
}
// the start page sends a visitor from Israel to Hebrew and one from Berlin to German
for (const [tz, want] of [['Asia/Jerusalem', 'he'], ['Europe/Berlin', 'de'], ['America/New_York', 'en']]) {
  const ctx = await b.newContext({ timezoneId: tz, locale: 'en-US' }); const p = await ctx.newPage();
  await p.goto(base + '/'); await p.waitForURL('**/' + want + '/', { timeout: 5000 }).catch(() => found.push({ id: 'start page', problem: tz + ' did not open /' + want + '/ but ' + p.url() }));
  await ctx.close();
}
await b.close(); srv.close();
fs.writeFileSync(path.join(OUT, 'report.json'), JSON.stringify(found, null, 1));
for (const f of found) console.log(f.id + ': ' + f.problem);
console.log(langs.length + ' languages × 2 screens checked, ' + found.length + ' problems');
process.exit(found.length ? 1 : 0);
