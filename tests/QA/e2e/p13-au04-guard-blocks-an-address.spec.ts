// P13 (AU-04) — the Guard, in a real browser, from another computer's address, end to end (AU-01 sign-in and lock is P09):
//  AU-04: guessing from one address (wrong sign-ins for a name that does not exist) gets that address blocked — every
//     request from it is refused, even the right password and code; the administrator's own address is not affected;
//     exactly one alert mail reaches the IT company's contact; the Security page shows the address with the reason;
//     "Unblock" lets it in again.
// The browser reaches the server through this computer's network address (not 127.0.0.1): the server trusts itself
// (SEC-090/110: from the server's own console an account never locks and needs no code), so the technician and the
// attacker come from the network card's address, as a computer in the office or on the internet does.
// Oracles outside the page: the HTTP status the server answers, the server's settings on disk (system.xml: FAIL_COUNT,
// LOCKED_UNTIL), the server's own answer to the old session, the mails received by the test's own mail server.
import { test, expect } from '../lib/fixtures';
import { Browser, Page } from '@playwright/test';
import { ADMIN, freshTotp, totp } from '../lib/world';
import { OpenWorld, SmtpSink, lanAddress, officeAddress, trySignIn, mailSettings } from './p-helpers';
import { until } from './q-helpers';
import * as fs from 'fs';
import * as path from 'path';

const CTX = { locale: 'en-US', viewport: { width: 1366, height: 860 } };
const TECH = { login: 'tech1', password: 'Tech-Pass-2468' };
// every page of the admin site's menu (NAV_SYSTEM in src/Server/Web/app.js)
const PAGES = ['dash', 'cust', 'allsets', 'tasks', 'live', 'tickets', 'logs', 'reports', 'restoretests', 'storage', 'license', 'defaults', 'policies', 'notify', 'admins', 'tset', 'security', 'time', 'integr', 'client', 'contract', 'brand'];
// what those pages read from the server (each must answer 200 with a live session, 401 with an ended one)
const API = ['me', 'dashboard', 'users', 'tasks?hours=24', 'live', 'staff', 'settings', 'guard', 'time', 'vendors'];

async function newPage(browser: Browser, evidence: { watch: (p: Page) => void }) { const c = await browser.newContext(CTX); const p = await c.newPage(); evidence.watch(p); return p; }
const staffAttr = (world: OpenWorld, login: string, attr: string) => {
  const x = fs.readFileSync(path.join(world.sys, 'conf', 'system.xml'), 'utf8');
  const el = [...x.matchAll(/<[A-Z_]+ [^>]*LOGIN_NAME="([^"]+)"[^>]*>/g)].find((m) => m[1].toLowerCase() === login.toLowerCase());
  if (!el) return undefined; const a = new RegExp(' ' + attr + '="([^"]*)"').exec(el[0]); return a ? a[1] : undefined;
};
const wrongCode = (secret: string) => { const now = [totp(secret), totp(secret, Date.now() - 30000), totp(secret, Date.now() + 30000)]; let c = 123456; while (now.includes(String(c))) c++; return String(c); };
async function signInLocal(page: Page, world: OpenWorld) {
  const r = await trySignIn(page, world.url, ADMIN.login, ADMIN.password, await freshTotp(ADMIN.totp, world.url));
  expect(r.status, 'the administrator signs in on the server itself: ' + r.shown).toBe(200);
  await expect(page.locator('.rail button[data-k]').first()).toBeVisible();
}
const api = async (base: string, p: string, session: string) => (await fetch(base + '/api/admin/' + p, { headers: { 'X-Session': session } })).status;

test('P13 AU-04 an address that guesses passwords is blocked for everything, the administrator\'s own address is not, one alert mail, unblocked on the site', async ({ browser, evidence }, info) => {
  const ip = lanAddress();
  test.skip(!ip, 'NOT TESTED: this computer has no network address besides 127.0.0.1');
  test.skip(!!ip && officeAddress(ip), 'NOT TESTED: this computer\'s address ' + ip + ' is an office (private) address, which the Guard never blocks by design');
  const world = new OpenWorld(); const sink = await new SmtpSink().start();
  try {
    await world.start();
    const lan = world.lanUrl()!;
    const admin = await newPage(browser, evidence), attacker = await newPage(browser, evidence);
    evidence.step('the administrator (on the server) sets the mail server and the IT company\'s contact, and reads the Guard rule');
    await signInLocal(admin, world);
    await mailSettings(admin, sink.port, 'it-admin@example.invalid');
    await admin.locator('.rail button[data-k="security"]').click();
    const fr = (label: string) => admin.locator('.form .fr').filter({ has: admin.locator('.lb b', { hasText: label }) }).first();
    const attacks = fr('Attacks from the internet');
    await expect(attacks.locator('label.tog input[type=checkbox]').first()).toBeChecked();
    const fails = Number(await attacks.locator('input[type=number]').nth(0).inputValue());
    evidence.step('the rule on the site: ' + fails + ' wrong passwords within the window block the address');
    expect(fails).toBeGreaterThanOrEqual(3);

    evidence.step('from ' + ip + ': ' + fails + ' wrong sign-ins for a name that does not exist');
    for (let i = 1; i <= fails; i++) {
      const r = await trySignIn(attacker, lan, 'intruder', 'guess-' + i, '000000');
      expect([i, r.status, r.shown], 'attempt ' + i).toEqual([i, 401, 'Wrong administrator details.']);
    }
    evidence.step('the address is blocked: the right administrator password and code are refused, with the reason');
    const rb = await trySignIn(attacker, lan, ADMIN.login, ADMIN.password, await freshTotp(ADMIN.totp, world.url));
    expect(rb.status).toBe(403);
    expect(rb.shown).toContain('blocked');
    await expect(attacker.locator('.rail')).toHaveCount(0);
    const pageStatus = (await attacker.goto(lan + '/admin'))!.status();
    expect(pageStatus, 'the site itself from that address').toBe(403);
    expect((await fetch(lan + '/api/admin/me')).status, 'any request from that address').toBe(403);

    evidence.step('the administrator\'s own address is not affected');
    expect((await fetch(world.url + '/admin')).status).toBe(200);
    await admin.locator('.rail button[data-k="cust"]').click();
    await expect(admin.locator('main h1').first()).toBeVisible();
    await admin.locator('.rail button[data-k="security"]').click();
    const blockedCard = admin.locator('section.card').filter({ has: admin.locator('h2', { hasText: 'Blocked addresses' }) });
    const row = blockedCard.getByRole('row').filter({ hasText: ip! });
    await expect(row).toHaveCount(1);
    await expect(row).toContainText(fails + ' wrong sign-ins in 10 minutes');

    evidence.step('ORACLE (mail server): the IT company\'s contact got exactly one alert about the address');
    const subject = 'An address was blocked: ' + ip;
    await until('the alert mail', () => sink.mails.some((m) => m.subject === subject), 60000, 1000);
    await new Promise((r) => setTimeout(r, 3000));
    const alerts = sink.mails.filter((m) => m.subject === subject);
    expect(alerts.length, 'mails: ' + JSON.stringify(sink.mails.map((m) => m.subject))).toBe(1);
    expect(alerts[0].to).toEqual(['it-admin@example.invalid']);

    evidence.step('Unblock on the site: the address signs in again');
    await row.getByRole('button', { name: 'Unblock' }).click();
    await expect(admin.locator('#toast').filter({ hasText: 'Unblocked' })).toBeVisible();
    await expect(blockedCard).toContainText('No address is blocked now.');
    expect((await fetch(lan + '/admin')).status).toBe(200);
    const ok = await trySignIn(attacker, lan, ADMIN.login, ADMIN.password, await freshTotp(ADMIN.totp, world.url));
    expect(ok.status, ok.shown).toBe(200);
    await expect(attacker.locator('.rail button[data-k]').first()).toBeVisible();
  } finally {
    sink.stop();
    await world.stop();
    if (info.status === info.expectedStatus && !process.env.QA_KEEP) fs.rmSync(world.dir, { recursive: true, force: true });
  }
});
