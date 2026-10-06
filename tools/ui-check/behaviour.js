'use strict';
// UX-010 (owner: "find bugs of this kind all over the system by yourself"): the behaviour robot. Not pictures — it acts
// like a person and checks what every system in the world does:
//   B1 wrong sign-in: the error shows on the form, the password is emptied, the page stays usable (in Hebrew: a Hebrew error)
//   B2 typing is kept: on every page with a field it types, waits while the page refreshes itself, and checks the text is
//      still there (the bug that threw away what was typed in the client program)
//   B3 Hebrew screens without English sentences (a word list of names that stay English is allowed)
//   B4 no page throws a script error
//   B5 after an update the page reloads itself: the administrator stays signed in (SEC-130)
//   B6 the client-software page warns when the installation would connect to another address than this one (PKG-090)
// Writes OUT/behaviour.json; exit 1 when B1, B2 or B4 fail (B3 is a list to fix).
const fs = require('fs'), path = require('path');
const { chromium } = require('playwright');
const OUT = process.env.OUT || 'ui-report', U = process.env.ADMIN_URL;
const WAIT = +(process.env.WAIT_MS || 12000);

function totp(secret) {
  const A = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'; let bits = ''; for (const ch of secret.replace(/=+$/, '')) bits += A.indexOf(ch).toString(2).padStart(5, '0');
  const key = Buffer.from(bits.match(/.{8}/g).map((b) => parseInt(b, 2)));
  const msg = Buffer.alloc(8); msg.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30000)));
  const hm = require('crypto').createHmac('sha1', key).update(msg).digest(); const o = hm[19] & 15;
  return String(((hm.readUInt32BE(o) & 0x7fffffff) % 1000000)).padStart(6, '0');
}

// names and terms that stay in English inside Hebrew screens
const KEEP = /^(ITSguard|Server|Online|ITCare|Cloud|Backup|Robot|SQL|Windows|Linux|Mac|macOS|Google|Microsoft|Authenticator|GitHub|Hyper|VMware|ESXi|vCenter|Oracle|RMAN|Domino|HCL|Exchange|OneDrive|SharePoint|Teams|restic|Setup|Version|Your|data|always|safe|https|http|localhost|admin|dana|office|ZIP|NTP|SMTP|STARTTLS|Gmail|Outlook|TOTP|QR|API|PDF|CSV|Excel|Zabbix|Veeam|Ahsay|HaloPSA|ConnectWise|Autotask|Anthropic|Claude|Acme|Docs|Temp|Documents|Shared|Users|Program|Files|System|State|MySQL|PostgreSQL|MariaDB|MongoDB)$/i;

(async () => {
  const report = { b1: [], b2: [], b3: [], b4: [], b5: [], b6: [] };
  const browser = await chromium.launch({ executablePath: process.env.CHROMIUM || undefined });
  const ctx = await browser.newContext({ viewport: { width: 1366, height: 860 }, ignoreHTTPSErrors: true });
  await ctx.addInitScript(() => { try { localStorage.setItem('obLang', 'he'); } catch (e) { } });
  const p = await ctx.newPage();
  p.on('pageerror', (e) => report.b4.push({ where: p.url(), error: String(e.message || e).slice(0, 300) }));

  // ---- B1: wrong details
  await p.goto(U + '/admin'); await p.waitForSelector('form.login');
  await p.fill('form.login input[autocomplete=username]', 'admin');
  await p.fill('form.login input[type=password]', 'Wrong-Pass-999');
  await p.click('form.login button[type=submit]'); await p.waitForTimeout(1500);
  const b1 = await p.evaluate(() => {
    const err = document.querySelector('.loginerr');
    return { form: !!document.querySelector('form.login'), shown: !!err && !err.hidden && err.textContent.trim().length > 0, text: err ? err.textContent.trim() : '',
      passwordEmptied: document.querySelector('form.login input[type=password]').value === '', user: document.querySelector('form.login input[autocomplete=username]').value };
  });
  if (!b1.form) report.b1.push('the sign-in page disappeared after wrong details');
  if (!b1.shown) report.b1.push('no error shown on the form after wrong details');
  if (!b1.passwordEmptied) report.b1.push('the wrong password stays in the field');
  if (b1.user !== 'admin') report.b1.push('the user name was lost');
  if (b1.shown && !/[֐-׿]/.test(b1.text)) report.b1.push('the error is not in Hebrew: ' + b1.text);

  // ---- sign in for real
  await p.fill('form.login input[type=password]', process.env.ADMIN_PASSWORD || 'Admin-Pass-123');
  await p.fill('form.login input.otp', totp(process.env.ADMIN_TOTP));
  await p.click('form.login button[type=submit]');
  await p.waitForSelector('.rail button[data-k]', { timeout: 20000 });
  let fields = 0;
  const pages = await p.$$eval('.rail button[data-k]', (bs) => bs.map((b) => ({ k: b.dataset.k, name: b.textContent.trim() })));

  for (const pg of pages) {
    try {
      await p.click('.rail button[data-k=' + pg.k + ']'); await p.waitForTimeout(1500);
      // ---- B3: English sentences on a Hebrew screen
      const english = await p.evaluate((keep) => {
        const re = new RegExp(keep.slice(1, keep.lastIndexOf('/')), 'i'); const out = new Set();
        const w = document.createTreeWalker(document.querySelector('main') || document.body, NodeFilter.SHOW_TEXT);
        for (let n = w.nextNode(); n; n = w.nextNode()) {
          const el = n.parentElement; if (!el || el.closest('.ltr, code, pre, td, [dir=ltr], input, textarea, select, .mono')) continue;
          const r = el.getBoundingClientRect(); if (!r.width || !r.height) continue;
          const words = (n.textContent.match(/[A-Za-z]{4,}/g) || []).filter((x) => !re.test(x));
          if (words.length >= 2) out.add(n.textContent.trim().slice(0, 90));
        }
        return [...out].slice(0, 15);
      }, KEEP.toString());
      if (english.length) report.b3.push({ page: pg.name, text: english });
      // ---- B2: typing is kept in EVERY field of the page while the page lives (owner: "check it on all the fields")
      const n = await p.evaluate(() => {
        const vis = (e) => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
        const fs = [...document.querySelectorAll('main input[type=text], main input[type=email], main input[type=number], main input:not([type]), main textarea')].filter((e) => vis(e) && !e.readOnly && !e.disabled);
        fs.forEach((f, i) => f.setAttribute('data-robot', String(i)));
        return fs.length;
      });
      if (!n) continue;
      fields += n;
      const before = [];
      for (let i = 0; i < n; i++) {
        const box = p.locator('[data-robot="' + i + '"]').first();
        const v = await box.inputValue(); before.push(v);
        const typed = (await box.getAttribute('type')) === 'number' ? '7' : 'רובוט';
        await box.pressSequentially(typed, { delay: 15 }).catch(() => { });
      }
      await p.waitForTimeout(WAIT);
      const after = await p.evaluate((n) => [...Array(n).keys()].map((i) => { const f = document.querySelector('[data-robot="' + i + '"]'); return f ? f.value : null; }), n);
      for (let i = 0; i < n; i++) {
        if (after[i] === null) report.b2.push({ page: pg.name, field: i, problem: 'the field was drawn again — the typed text is gone' });
        else if (after[i] === before[i]) report.b2.push({ page: pg.name, field: i, problem: 'typing did not reach the field' });
      }
      for (let i = 0; i < n; i++) await p.locator('[data-robot="' + i + '"]').first().fill(before[i]).catch(() => { });
    } catch (e) { report.b4.push({ where: pg.name, error: String(e.message || e).slice(0, 200) }); }
  }

  // ---- B5: the reload after an update keeps the sign-in
  await p.reload(); await p.waitForTimeout(2500);
  if (await p.$('form.login')) report.b5.push('after a reload the sign-in page came back');
  else if (!(await p.$('.rail button[data-k]'))) report.b5.push('after a reload the menu is missing');
  // ---- B6: the address in the installation is checked against this page's address
  try {
    await p.click('.rail button[data-k=client]'); await p.waitForTimeout(1500);
    const urlBox = p.locator('main input.ltr').first();
    await urlBox.fill('https://198.51.100.7:8443'); await p.waitForTimeout(300);
    const shown = await p.evaluate(() => { const w = document.querySelector('.addrwarn'); return !!w && !w.hidden; });
    const local = /^(localhost|127\.)/.test(new URL(U).hostname);
    if (!local && !shown) report.b6.push('a different address shows no warning');
    if (local && shown) report.b6.push('on the server itself the warning must stay hidden');
    await urlBox.fill(U); await p.waitForTimeout(300);
    if (await p.evaluate(() => { const w = document.querySelector('.addrwarn'); return !!w && !w.hidden; })) report.b6.push('the same address still shows the warning');
  } catch (e) { report.b6.push('client page: ' + String(e.message || e).slice(0, 150)); }

  await browser.close();
  fs.mkdirSync(OUT, { recursive: true });
  fs.writeFileSync(path.join(OUT, 'behaviour.json'), JSON.stringify(report, null, 2));
  console.log('B1 wrong sign-in: ' + (report.b1.length ? report.b1.join('; ') : 'ok'));
  console.log('B2 typing kept: ' + (report.b2.length ? JSON.stringify(report.b2) : 'ok — ' + fields + ' fields on ' + pages.length + ' pages'));
  console.log('B3 English on Hebrew screens: ' + report.b3.length + ' page(s)' + (report.b3.length ? '\n' + report.b3.map((x) => '  ' + x.page + ': ' + x.text.join(' | ')).join('\n') : ''));
  console.log('B5 reload keeps the sign-in: ' + (report.b5.length ? report.b5.join('; ') : 'ok'));
  console.log('B6 client address check: ' + (report.b6.length ? report.b6.join('; ') : 'ok'));
  console.log('B4 script errors: ' + (report.b4.length ? JSON.stringify(report.b4) : 'none'));
  process.exit(report.b1.length || report.b2.length || report.b4.length || report.b5.length || report.b6.length ? 1 : 0);
})().catch((e) => { console.error(e); process.exit(2); });
