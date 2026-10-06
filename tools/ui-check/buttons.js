'use strict';
// UI-040: the button robot. Opens every screen of the admin site (every page, every tab of a customer and of a set —
// a files set and an SQL Server set), finds every button, link and tab on it by itself, and clicks each one on a freshly
// opened screen. A button must do something a person can see (a new screen, a window, a message, a changed page), must
// not raise a script error or a server error, and every window with fields must end with "Save and exit" and
// "Exit without saving" (or "Close"). "Are you sure?" windows are answered "no", so nothing is deleted.
// Writes OUT/buttons.json and OUT/buttons.html (every button found, per screen); exit 1 on problems.
const fs = require('fs'), path = require('path');
const { chromium } = require('playwright');
const OUT = process.env.OUT || 'ui-report', U = process.env.ADMIN_URL, LOGIN = process.env.CUSTOMER || 'dana-office';

function totp(secret) {
  const A = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'; let bits = ''; for (const ch of secret.replace(/=+$/, '')) bits += A.indexOf(ch).toString(2).padStart(5, '0');
  const key = Buffer.from(bits.match(/.{8}/g).map((b) => parseInt(b, 2)));
  const msg = Buffer.alloc(8); msg.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30000)));
  const hm = require('crypto').createHmac('sha1', key).update(msg).digest(); const o = hm[19] & 15;
  return String(((hm.readUInt32BE(o) & 0x7fffffff) % 1000000)).padStart(6, '0');
}

// ---- inside the page: the buttons of the screen (not the menu, not the top bar), each with a name a person would recognise
function buttons() {
  const root = document.querySelector('main.pg');
  const vis = (e) => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0 && !e.closest('[hidden]') && getComputedStyle(e).visibility !== 'hidden'; };
  const all = Array.from(root.querySelectorAll('button, a[href], [role=tab], tr.click, summary')).filter((e) => vis(e) && !e.disabled);
  // a long list of the same button (a folder tree, rows of a table) is tried on its first 3 — the rest are the same code
  const kind = (e) => e.tagName + '.' + (typeof e.className === 'string' ? e.className : '') + '@' + (e.parentElement ? e.parentElement.className : '');
  const kinds = {};
  const seen = {};
  return all.map((e, i) => {
    if (e.closest('.tabs') && (e.classList.contains('on') || e.getAttribute('aria-selected') === 'true')) return null;   // the tab already open
    const k = kind(e); kinds[k] = (kinds[k] || 0) + 1; if (kinds[k] > 3 && (e.closest('.ftree, tbody, .chips, .paths, .opt'))) return null;
    let label = (e.getAttribute('aria-label') || e.title || e.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 50) || e.tagName.toLowerCase();
    if (e.tagName === 'TR') label = 'row: ' + label.slice(0, 30);
    seen[label] = (seen[label] || 0) + 1;
    return { i, key: label + (seen[label] > 1 ? ' [' + seen[label] + ']' : ''), tag: e.tagName.toLowerCase(), save: !!e.closest('.savebar') };
  }).filter(Boolean);
}
function nth(i) {
  const root = document.querySelector('main.pg');
  const vis = (e) => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0 && !e.closest('[hidden]') && getComputedStyle(e).visibility !== 'hidden'; };
  return Array.from(root.querySelectorAll('button, a[href], [role=tab], tr.click, summary')).filter((e) => vis(e) && !e.disabled)[i];
}
// what a person sees: the screen, a window, a message
const state = () => { const d = document.querySelector('dialog[open]'); const t = document.querySelector('#toast'); return { html: (document.querySelector('#main, main.pg') || document.body).innerHTML.length + ':' + (document.querySelector('main.pg') || document.body).innerHTML.slice(0, 20000).split('').reduce((h, c) => (h * 31 + c.charCodeAt(0)) | 0, 0), nav: sessionStorage.getItem('obNav'), dialog: d ? d.textContent.slice(0, 80) : null, toast: t && !t.hidden ? t.textContent : null, toastErr: !!(t && !t.hidden && t.className.includes('err')) }; };
// a window with fields ends with save + exit (or only "Close" when it changes nothing)
const windowRule = () => {
  const d = document.querySelector('dialog[open]'); if (!d) return null;
  const fields = d.querySelectorAll('input:not([type=hidden]), select, textarea').length;
  const acts = d.querySelectorAll('.actions button').length, pri = d.querySelectorAll('.actions .btn.pri').length;
  if (fields > 0 && (acts < 2 || pri < 1)) return 'a window with fields has no "Save and exit" + "Exit without saving"';
  if (acts < 1) return 'a window with no way to close it';
  return null;
};

(async () => {
  fs.mkdirSync(OUT, { recursive: true });
  const b = await chromium.launch({ executablePath: process.env.CHROMIUM || (fs.existsSync('/opt/pw-browsers/chromium') ? '/opt/pw-browsers/chromium' : undefined) });
  const ctx = await b.newContext({ viewport: { width: 1280, height: 900 }, acceptDownloads: true });
  await ctx.route(/^https?:\/\/(?!localhost|127\.0\.0\.1)/, (r) => r.abort());
  const p = await ctx.newPage(); const errors = [], bad = []; let events = [];
  p.on('pageerror', (e) => errors.push(e.message));
  p.on('response', (r) => { try { const u = new URL(r.url()); if (u.pathname.startsWith('/api/') && r.status() >= 500) bad.push(r.status() + ' ' + r.request().method() + ' ' + u.pathname); } catch (e) { } });
  p.on('download', () => events.push('download'));
  p.on('request', (r) => { try { if (new URL(r.url()).pathname.startsWith('/api/')) events.push('asks the server'); } catch (e) { } });
  ctx.on('page', (np) => { events.push('new window'); np.close().catch(() => {}); });
  await p.goto(U + '/admin/'); await p.waitForSelector('form.login');
  await p.locator('form.login input').nth(0).fill('admin'); await p.locator('form.login input[type=password]').fill('Admin-Pass-123');
  await p.locator('form.login input').nth(2).fill(totp(process.env.ADMIN_TOTP || '')); await p.locator('form.login button[type=submit]').click();
  await p.waitForSelector('.rail button');
  const api = (u) => p.evaluate(async (u) => { const r = await fetch('/api/admin/' + u, { headers: { 'X-Session': sessionStorage.getItem('obAdmin') } }); const x = new DOMParser().parseFromString(await r.text(), 'application/xml'); const f = (el) => { const o = {}; for (const c of el.children) { if (c.tagName === 'f') o[c.getAttribute('n')] = c.textContent; else if (c.tagName === 'l') o[c.getAttribute('n')] = Array.from(c.children).map(f); } return o; }; return f(x.documentElement); }, u);
  const users = (await api('users')).users || []; const sets = ((users.find((u) => u.login === LOGIN) || {}).sets) || [];
  const fileSet = (sets.find((s) => s.type !== 'MSSQL') || {}).id, sqlSet = (sets.find((s) => s.type === 'MSSQL') || {}).id;
  const rail = await p.$$eval('.rail button[data-k]', (l) => l.map((e) => e.dataset.k));
  const STABS = ['general', 'src', 'sched', 'method', 'dest', 'ret', 'filter', 'enc', 'perf', 'cmd', 'rep', 'maint'];
  const screens = rail.map((k) => ({ name: k, nav: { page: k } }))
    .concat(['sets', 'overview', 'pcs', 'contacts', 'quota', 'sec', 'calls', 'reports', 'logs'].map((k) => ({ name: 'customer ' + k, nav: { page: 'customer', login: LOGIN, ctab: k } })))
    .concat(fileSet ? STABS.map((k) => ({ name: 'set ' + k, nav: { page: 'customer', login: LOGIN, ctab: 'sets', set: fileSet, stab: k } })) : [])
    .concat(sqlSet ? ['src', 'method'].map((k) => ({ name: 'sql set ' + k, nav: { page: 'customer', login: LOGIN, ctab: 'sets', set: sqlSet, stab: k } })) : []);
  const open = async (nav) => {
    await p.evaluate((n) => sessionStorage.setItem('obNav', JSON.stringify(n)), nav);
    await p.goto(U + '/admin/'); await p.waitForSelector('main.pg .head', { timeout: 15000 }); await p.waitForTimeout(400);
  };
  const closeWindows = () => p.evaluate(() => document.querySelectorAll('dialog').forEach((d) => { try { d.close(); } catch (e) { } d.remove(); }));

  const report = [], problems = [];
  for (const s of screens) {
    await open(s.nav);
    const list = await p.evaluate(buttons);
    console.log(s.name + ': ' + list.length + ' buttons');
    for (const b0 of list) {
      await open(s.nav);
      const cur = (await p.evaluate(buttons)).find((x) => x.key === b0.key);
      if (!cur) { report.push({ screen: s.name, key: b0.key, result: 'appears only after another click' }); continue; }
      errors.length = 0; bad.length = 0; events = [];
      const before = await p.evaluate(state);
      const h = (await p.evaluateHandle(nth, cur.i)).asElement();
      try { await h.click({ timeout: 5000 }); } catch (e) { problems.push({ screen: s.name, key: cur.key, problem: 'cannot be clicked: ' + e.message.split('\n')[0] }); continue; }
      await p.waitForTimeout(900);
      let after = await p.evaluate(state);
      const rule = await p.evaluate(windowRule);
      // "Are you sure?" → no; any other window → closed (its fields are not filled, nothing is saved)
      if (after.dialog) await closeWindows();
      const reacted = events.length || after.dialog || after.toast || after.nav !== before.nav || after.html !== before.html;
      const what = events[0] || (after.dialog ? 'window: ' + after.dialog.trim().slice(0, 40) : after.toast ? (after.toastErr ? 'message (refused): ' : 'message: ') + after.toast.slice(0, 50) : after.nav !== before.nav ? 'opens another screen' : after.html !== before.html ? 'the screen changed' : 'nothing');
      report.push({ screen: s.name, key: cur.key, result: what });
      if (!reacted) problems.push({ screen: s.name, key: cur.key, problem: 'nothing happens when it is clicked' });
      if (errors.length) problems.push({ screen: s.name, key: cur.key, problem: 'script error: ' + errors[0] });
      if (bad.length) problems.push({ screen: s.name, key: cur.key, problem: 'server error: ' + bad[0] });
      if (rule) problems.push({ screen: s.name, key: cur.key, problem: rule });
    }
  }
  await b.close();
  fs.writeFileSync(path.join(OUT, 'buttons.json'), JSON.stringify({ report, problems }, null, 1));
  const esc = (x) => String(x == null ? '' : x).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
  fs.writeFileSync(path.join(OUT, 'buttons.html'), '<!doctype html><meta charset="utf-8"><title>Buttons</title><style>body{font:14px system-ui;margin:20px}td,th{border-bottom:1px solid #ddd;padding:4px 6px;text-align:start}.bad{color:#b91c1c;font-weight:700}</style>'
    + '<h1>Every button, clicked</h1><p>' + report.length + ' buttons on ' + screens.length + ' screens · ' + problems.length + ' problems</p>'
    + (problems.length ? '<h2 class="bad">Problems</h2><table>' + problems.map((x) => '<tr><td>' + esc(x.screen) + '</td><td>' + esc(x.key) + '</td><td>' + esc(x.problem) + '</td></tr>').join('') + '</table>' : '')
    + '<h2>All buttons</h2><table><tr><th>Screen</th><th>Button</th><th>What it did</th></tr>' + report.map((r) => '<tr><td>' + esc(r.screen) + '</td><td>' + esc(r.key) + '</td><td>' + esc(r.result) + '</td></tr>').join('') + '</table>');
  console.log(report.length + ' buttons, ' + problems.length + ' problems → ' + path.join(OUT, 'buttons.html'));
  for (const x of problems) console.log('  ✗ ' + x.screen + ' · ' + x.key + ': ' + x.problem);
  process.exit(problems.length ? 1 : 0);
})();
