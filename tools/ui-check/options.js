'use strict';
// OPT-020: the option robot. Finds every editor of the admin site by itself (every page and tab with "Save and exit"),
// and for every field in it — box, list, switch, number, text — changes the value, saves, opens the page again and
// checks that the new value is there; then puts the old value back. A field that does not keep its value is a broken
// option. Writes OUT/options.json (every option found, per screen) and OUT/options.html; exit 1 on problems.
// The rest of the way (server → computer → what the backup does) is proven by tests/Tests/OptionsTests.cs.
const fs = require('fs'), path = require('path');
const { chromium } = require('playwright');
const OUT = process.env.OUT || 'ui-report', U = process.env.ADMIN_URL, LOGIN = process.env.CUSTOMER || 'dana-office';
// fields that would lock the robot out of the site it is testing — proven by tests/Tests/SecurityTests.cs instead
const SKIP = [/security .*(address|IP|range|כתוב)/i];

function totp(secret) {
  const A = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'; let bits = ''; for (const ch of secret.replace(/=+$/, '')) bits += A.indexOf(ch).toString(2).padStart(5, '0');
  const key = Buffer.from(bits.match(/.{8}/g).map((b) => parseInt(b, 2)));
  const msg = Buffer.alloc(8); msg.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30000)));
  const hm = require('crypto').createHmac('sha1', key).update(msg).digest(); const o = hm[19] & 15;
  return String(((hm.readUInt32BE(o) & 0x7fffffff) % 1000000)).padStart(6, '0');
}

// ---- inside the page: the fields of the editor, each with a name a person would recognise
function fields() {
  const root = document.querySelector('main.pg .card .cb') || document.querySelector('main.pg');
  const vis = (e) => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0 && !e.closest('[hidden]') && getComputedStyle(e).visibility !== 'hidden'; };
  const all = Array.from(root.querySelectorAll('input, select, textarea')).filter((e) => !/^(hidden|file|search|button|submit)$/.test(e.type) && !e.disabled && !e.readOnly && (vis(e) || (e.type === 'checkbox' && vis(e.parentElement))));
  // not settings: a choice of what is shown (data-view), and a box typed into for the button beside it ("Add", "Send a test")
  const entry = (e) => e.type !== 'checkbox' && e.type !== 'radio' && e.parentElement && Array.from(e.parentElement.children).some((x) => x.tagName === 'BUTTON' && !/danger/.test(x.className));
  const seen = {};
  return all.map((e, i) => {
    const row = e.closest('.fr'); const lb = row ? (row.querySelector('.lb b') || {}).textContent : '';
    const near = e.getAttribute('aria-label') || (e.closest('.inline') ? e.closest('.inline').textContent.trim().slice(0, 50) : '') || e.placeholder || '';
    const label = ((lb || '') + (near && near !== lb ? ' · ' + near : '')).trim() || e.tagName.toLowerCase() + '#' + i;
    seen[label] = (seen[label] || 0) + 1;
    const value = e.type === 'checkbox' || e.type === 'radio' ? (e.checked ? 'on' : 'off') : e.value;
    const opts = e.tagName === 'SELECT' ? Array.from(e.options).map((o) => o.value) : null;
    if (e.dataset.view || entry(e)) return { i, key: label + (seen[label] > 1 ? ' [' + seen[label] + ']' : ''), skip: e.dataset.view ? 'shows a choice, not a setting' : 'typed for the button beside it' };
    return { i, key: label + (seen[label] > 1 ? ' [' + seen[label] + ']' : ''), tag: e.tagName.toLowerCase(), type: e.type, value, opts, min: e.min, max: e.max, ph: e.placeholder, ltr: e.classList.contains('ltr') };
  });
}

// a valid value other than the current one
function other(f) {
  if (f.type === 'checkbox') return f.value === 'on' ? 'off' : 'on';
  if (f.type === 'radio') return f.value === 'on' ? null : 'on';
  if (f.tag === 'select') { const o = f.opts.filter((v) => v !== f.value && v !== ''); return o.length ? o[o.length > 1 ? 1 : 0] : null; }
  if (f.type === 'number') { const n = Number(f.value || 0); let v = n + 1; if (f.max !== '' && v > Number(f.max)) v = n - 1; if (f.min !== '' && v < Number(f.min)) return null; return String(v); }
  if (f.type === 'time') return f.value === '03:45' ? '04:15' : '03:45';
  if (f.type === 'email') return 'robot-' + Date.now() % 100000 + '@example.invalid';
  if (f.type === 'color') return f.value.toLowerCase() === '#123456' ? '#654321' : '#123456';
  if (f.value) return f.value + (f.ltr ? '-x' : ' x');
  return f.ph && !/\s/.test(f.ph) ? f.ph : f.ltr ? 'robot-value' : 'ערך בדיקה';
}

(async () => {
  fs.mkdirSync(OUT, { recursive: true });
  const b = await chromium.launch({ executablePath: process.env.CHROMIUM || (fs.existsSync('/opt/pw-browsers/chromium') ? '/opt/pw-browsers/chromium' : undefined) });
  const ctx = await b.newContext({ viewport: { width: 1280, height: 900 } });
  await ctx.route(/^https?:\/\/(?!localhost|127\.0\.0\.1)/, (r) => r.abort());
  const p = await ctx.newPage(); const errors = [];
  p.on('pageerror', (e) => errors.push(e.message));
  p.on('dialog', (d) => d.dismiss().catch(() => {}));
  await p.goto(U + '/admin/'); await p.waitForSelector('form.login');
  await p.locator('form.login input').nth(0).fill('admin'); await p.locator('form.login input[type=password]').fill('Admin-Pass-123');
  await p.locator('form.login input').nth(2).fill(totp(process.env.ADMIN_TOTP || '')); await p.locator('form.login button[type=submit]').click();
  await p.waitForSelector('.rail button');
  const api = (u) => p.evaluate(async (u) => { const r = await fetch('/api/admin/' + u, { headers: { 'X-Session': sessionStorage.getItem('obAdmin') } }); const x = new DOMParser().parseFromString(await r.text(), 'application/xml'); const f = (el) => { const o = {}; for (const c of el.children) { if (c.tagName === 'f') o[c.getAttribute('n')] = c.textContent; else if (c.tagName === 'l') o[c.getAttribute('n')] = Array.from(c.children).map(f); } return o; }; return f(x.documentElement); }, u);
  const users = (await api('users')).users || []; const sets = ((users.find((u) => u.login === LOGIN) || {}).sets) || [];
  const set = (sets.find((s) => !/LOCAL/.test(s.dest || '')) || sets[0] || {}).id;

  // every screen: the rail's pages, the customer's tabs, the set's tabs — each is an editor if it has "Save and exit"
  const rail = await p.$$eval('.rail button[data-k]', (l) => l.map((e) => e.dataset.k));
  const screens = rail.filter((k) => !/^(dash|cust)$/.test(k)).map((k) => ({ name: k, nav: { page: k } }))
    .concat(['overview', 'contacts', 'quota', 'sec'].map((k) => ({ name: 'customer ' + k, nav: { page: 'customer', login: LOGIN, ctab: k } })))
    .concat(set ? ['general', 'src', 'sched', 'method', 'dest', 'ret', 'filter', 'enc', 'perf', 'cmd'].map((k) => ({ name: 'set ' + k, nav: { page: 'customer', login: LOGIN, ctab: 'sets', set, stab: k } })) : []);
  const open = async (nav) => {
    await p.evaluate((n) => sessionStorage.setItem('obNav', JSON.stringify(n)), nav);
    await p.goto(U + '/admin/'); await p.waitForSelector('main.pg .head', { timeout: 15000 }); await p.waitForTimeout(500);
  };
  const setField = async (i, v) => {
    const e = p.locator('main.pg .card .cb input:not([type=hidden]):not([type=file]):not([type=search]):not([type=button]):not([type=submit]), main.pg .card .cb select, main.pg .card .cb textarea').filter({ hasNot: p.locator('[disabled]') });
    const all = await p.evaluate(fields); const f = all[i];
    const h = (await p.evaluateHandle((i) => { const root = document.querySelector('main.pg .card .cb') || document.querySelector('main.pg'); const vis = (e) => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0 && !e.closest('[hidden]') && getComputedStyle(e).visibility !== 'hidden'; }; return Array.from(root.querySelectorAll('input, select, textarea')).filter((e) => !/^(hidden|file|search|button|submit)$/.test(e.type) && !e.disabled && !e.readOnly && (vis(e) || (e.type === 'checkbox' && vis(e.parentElement))))[i]; }, i)).asElement();
    if (!h) return false; void e;
    if (f.type === 'checkbox' || f.type === 'radio') { if ((f.value === 'on') !== (v === 'on')) await h.evaluate((x) => { x.click(); }); }
    else if (f.tag === 'select') await h.selectOption(v);
    else await h.evaluate((x, v) => { x.value = v; x.dispatchEvent(new Event('input', { bubbles: true })); x.dispatchEvent(new Event('change', { bubbles: true })); }, v);
    await p.waitForTimeout(150);
    return true;
  };
  const save = async () => {
    const btn = p.locator('main.pg .savebar .btn.pri').first();
    if (!(await btn.count())) return 'no save button';
    await btn.click(); await p.waitForTimeout(900);
    const err = await p.evaluate(() => { const t = document.querySelector('#toast'); return t && !t.hidden && t.className.includes('err') ? t.textContent : (document.querySelector('dialog[open]') ? 'a window opened: ' + document.querySelector('dialog[open]').textContent.slice(0, 80) : null); });
    await p.evaluate(() => document.querySelectorAll('dialog').forEach((d) => { try { d.close(); } catch (e) { } d.remove(); }));
    return err;
  };

  const report = []; const problems = [];
  for (const s of screens) {
    await open(s.nav);
    const hasSave = await p.locator('main.pg .savebar .btn.pri').count();
    const list = await p.evaluate(fields);
    if (!hasSave) { if (list.length && /^set |^customer (contacts|quota|sec)/.test(s.name)) problems.push({ screen: s.name, key: '-', problem: 'fields without "Save and exit"' }); continue; }
    console.log(s.name + ': ' + list.length + ' fields');
    for (const f0 of list) {
      if (f0.skip) { report.push({ screen: s.name, key: f0.key, result: 'not a setting: ' + f0.skip }); continue; }
      if (SKIP.some((r) => r.test(s.name + ' ' + f0.key))) { report.push({ screen: s.name, key: f0.key, result: 'skipped (would lock the robot out)' }); continue; }
      await open(s.nav);
      const now = (await p.evaluate(fields)).find((x) => x.key === f0.key);
      if (!now) { report.push({ screen: s.name, key: f0.key, result: 'appears only with another choice' }); continue; }
      const v = other(now);
      if (v == null) { report.push({ screen: s.name, key: f0.key, result: 'no other value to try' }); continue; }
      errors.length = 0;
      await setField(now.i, v);
      const err = await save();
      if (err) { report.push({ screen: s.name, key: f0.key, result: 'refused: ' + err.trim().slice(0, 120), value: v }); continue; }   // a value the server refuses on purpose (checked: it says why)
      await open(s.nav);
      const after = (await p.evaluate(fields)).find((x) => x.key === f0.key);
      const kept = after && after.value === v;
      report.push({ screen: s.name, key: f0.key, from: now.value, to: v, result: kept ? 'kept' : 'LOST', now: after ? after.value : '(gone)' });
      if (!kept) problems.push({ screen: s.name, key: f0.key, problem: 'changed to "' + v + '", saved, opened again: "' + (after ? after.value : '(the field is gone)') + '"' });
      if (errors.length) problems.push({ screen: s.name, key: f0.key, problem: 'script error: ' + errors[0] });
      // the old value back
      if (after) { const i = (await p.evaluate(fields)).find((x) => x.key === f0.key).i; await setField(i, now.value); const e2 = await save(); if (e2) problems.push({ screen: s.name, key: f0.key, problem: 'could not put the old value back: ' + e2 }); }
    }
  }
  await b.close();
  const kept = report.filter((r) => r.result === 'kept').length;
  fs.writeFileSync(path.join(OUT, 'options.json'), JSON.stringify({ report, problems }, null, 1));
  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
  fs.writeFileSync(path.join(OUT, 'options.html'), '<!doctype html><meta charset="utf-8"><title>Options</title><style>body{font:14px system-ui;margin:20px}td,th{border-bottom:1px solid #ddd;padding:4px 6px;text-align:start}.LOST,.bad{color:#b91c1c;font-weight:700}</style>'
    + '<h1>Every option, changed, saved and read back</h1><p>' + report.length + ' fields · ' + kept + ' kept · ' + problems.length + ' problems</p>'
    + (problems.length ? '<h2 class="bad">Problems</h2><table>' + problems.map((x) => '<tr><td>' + esc(x.screen) + '</td><td>' + esc(x.key) + '</td><td>' + esc(x.problem) + '</td></tr>').join('') + '</table>' : '')
    + '<h2>All fields</h2><table><tr><th>Screen</th><th>Field</th><th>From</th><th>To</th><th>Result</th></tr>' + report.map((r) => '<tr><td>' + esc(r.screen) + '</td><td>' + esc(r.key) + '</td><td>' + esc(r.from) + '</td><td>' + esc(r.to) + '</td><td class="' + esc(r.result) + '">' + esc(r.result) + '</td></tr>').join('') + '</table>');
  console.log(report.length + ' fields, ' + kept + ' kept, ' + problems.length + ' problems → ' + path.join(OUT, 'options.html'));
  for (const x of problems) console.log('  ✗ ' + x.screen + ' · ' + x.key + ': ' + x.problem);
  process.exit(problems.length ? 1 : 0);
})();
