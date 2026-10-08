// P09 (AU-01) — administrator sign-in in a real browser, from another computer's address, end to end (AU-04, the Guard, is P13):
//  a. AU-01: a new technician (added on the Administrators page) must set up two-step at the first sign-in; a wrong
//     password and a wrong code are refused with the reason; after the threshold set on the Security page the account is
//     locked — even the right password and code are refused, with the reason; the main administrator unlocks it on the
//     site; then sign-in works; after "Sign out" the old session is refused on every page of the site and by the server.
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

test('P09 AU-01 a technician from the network: two-step set-up, wrong password and wrong code refused, locked after the threshold, unlocked on the site, signed-out session refused on every page', async ({ browser, evidence }, info) => {
  const ip = lanAddress();
  test.skip(!ip, 'NOT TESTED: this computer has no network address besides 127.0.0.1 (the server trusts itself: no lock, no code)');
  const world = new OpenWorld();
  try {
    await world.start();
    const lan = world.lanUrl()!;
    const admin = await newPage(browser, evidence), tech = await newPage(browser, evidence);

    evidence.step('the main administrator (on the server) adds a technician on the Administrators page; reads the lock rule on the Security page');
    await signInLocal(admin, world);
    await admin.locator('.rail button[data-k="admins"]').click();
    await admin.getByRole('button', { name: '+ Administrator' }).click();
    const fr = (label: string) => admin.locator('.form .fr').filter({ has: admin.locator('.lb b', { hasText: label }) }).first();
    await fr('Full name').locator('input').fill('Technician One');
    await fr('User name').locator('input').fill(TECH.login);
    await fr('Password').locator('input').fill(TECH.password);
    await admin.getByRole('button', { name: 'Save and exit' }).click();
    await expect(admin.getByRole('row').filter({ hasText: TECH.login })).toContainText('Set up at the first sign-in');
    await admin.locator('.rail button[data-k="security"]').click();
    const lockRow = fr('Lock after wrong passwords');
    const attempts = Number(await lockRow.locator('input').nth(0).inputValue()), minutes = Number(await lockRow.locator('input').nth(1).inputValue());
    evidence.step('the rule on the site: locked after ' + attempts + ' wrong attempts, for ' + minutes + ' minutes');
    expect(attempts).toBeGreaterThanOrEqual(1); expect(attempts).toBeLessThanOrEqual(10); expect(minutes).toBeGreaterThanOrEqual(5);

    evidence.step('the technician, from ' + ip + ': the first sign-in opens only the two-step set-up');
    const first = await trySignIn(tech, lan, TECH.login, TECH.password, '');
    expect(first.status, first.shown).toBe(200);
    await expect(tech.locator('form.login h2')).toHaveText('Set up two-step verification');
    await expect(tech.locator('.rail')).toHaveCount(0);
    const secret = (await tech.locator('form.login p.mono').innerText()).trim();
    expect(secret).toMatch(/^[A-Z2-7]{16,}$/);
    await tech.locator('form.login input[autocomplete=one-time-code]').fill(await freshTotp(secret, world.url));
    await tech.getByRole('button', { name: 'Turn on' }).click();
    await expect(tech.locator('.rail button[data-k]').first()).toBeVisible();
    await tech.getByRole('button', { name: 'Sign out' }).click();
    await expect(tech.locator('form.login')).toBeVisible();

    evidence.step('a wrong password, then the right password with a wrong code: refused, with the reason, nothing opens');
    const r1 = await trySignIn(tech, lan, TECH.login, 'Not-The-Password-9', await freshTotp(secret, world.url));
    expect([r1.status, r1.shown]).toEqual([401, 'Wrong administrator details.']);
    await expect(tech.locator('.rail')).toHaveCount(0);
    expect(staffAttr(world, TECH.login, 'FAIL_COUNT'), 'ORACLE (server disk): the failure is counted').toBe('1');
    if (attempts > 1) {
      const r2 = await trySignIn(tech, lan, TECH.login, TECH.password, wrongCode(secret));
      expect([r2.status, r2.shown]).toEqual([401, 'Wrong administrator details.']);
      await expect(tech.locator('.rail')).toHaveCount(0);
      expect(staffAttr(world, TECH.login, 'FAIL_COUNT')).toBe('2');
    }
    for (let i = 3; i <= attempts; i++) {
      const r = await trySignIn(tech, lan, TECH.login, 'Not-The-Password-' + i, '');
      expect([r.status, r.shown]).toEqual([401, 'Wrong administrator details.']);
    }
    evidence.step('ORACLE (server disk): the account is locked for the time set on the site');
    const lockedUntil = Number(staffAttr(world, TECH.login, 'LOCKED_UNTIL'));
    expect(lockedUntil - Date.now(), 'LOCKED_UNTIL').toBeGreaterThan((minutes - 1) * 60000);
    expect(lockedUntil - Date.now()).toBeLessThan((minutes + 1) * 60000);

    evidence.step('locked: even the right password and code are refused, and the form says why');
    const rl = await trySignIn(tech, lan, TECH.login, TECH.password, await freshTotp(secret, world.url));
    expect(rl.status).toBe(423);
    expect(rl.shown).toContain('locked');
    await expect(tech.locator('.rail')).toHaveCount(0);

    evidence.step('the main administrator sees it locked on the Administrators page and unlocks it');
    await admin.locator('.rail button[data-k="admins"]').click();
    const techRow = admin.getByRole('row').filter({ hasText: TECH.login });
    await expect(techRow.locator('.pill.bad')).toHaveText('Locked');
    await techRow.click();
    await admin.getByRole('button', { name: '🔓 Unlock' }).click();
    await expect(admin.locator('#toast').filter({ hasText: 'Done' })).toBeVisible();
    await expect(admin.getByRole('row').filter({ hasText: TECH.login }).locator('.pill.bad')).toHaveCount(0);

    evidence.step('unlocked: the technician signs in');
    const ok = await trySignIn(tech, lan, TECH.login, TECH.password, await freshTotp(secret, world.url));
    expect(ok.status, ok.shown).toBe(200);
    await expect(tech.locator('.rail button[data-k]').first()).toBeVisible();
    const session = (await tech.evaluate(() => sessionStorage.getItem('obAdmin')))!;
    expect(session).toBeTruthy();
    for (const p of API) expect(await api(lan, p, session), 'with the live session: GET ' + p).toBe(200);

    evidence.step('Sign out; the old session is refused by the server on everything the pages read');
    await tech.getByRole('button', { name: 'Sign out' }).click();
    await expect(tech.locator('form.login')).toBeVisible();
    await until('the server ended the session', async () => (await api(lan, 'me', session)) === 401, 10000, 500);
    for (const p of API) expect(await api(lan, p, session), 'after sign-out: GET ' + p).toBe(401);
    evidence.step('the old session put back into the browser: every page of the menu shows the sign-in form, never its content');
    for (const k of PAGES) {
      await tech.evaluate(([s, pg]) => { sessionStorage.setItem('obAdmin', s); sessionStorage.setItem('obNav', JSON.stringify({ page: pg })); }, [session, k]);
      await tech.goto(lan + '/admin');
      await expect(tech.locator('form.login'), 'page ' + k + ' with the ended session').toBeVisible();
      await expect(tech.locator('.rail'), 'page ' + k + ': no menu').toHaveCount(0);
      await expect(tech.locator('main h1'), 'page ' + k + ': no page title').toHaveCount(0);
    }
  } finally {
    await world.stop();
    if (info.status === info.expectedStatus && !process.env.QA_KEEP) fs.rmSync(world.dir, { recursive: true, force: true });
  }
});
