// P11 (AU-06) — two resellers (IT companies) on one server, each with a customer that really backs up. Reseller A's
// administrator signs in to the admin site in a real browser and sees only its own customer — on "My customers", on the
// Tasks page and when it opens the other customer's set by its address; what the browser could send for the other
// reseller's customer (read the set, change it, start a backup) is refused by the server and changes nothing there.
// Its own customer it can work with (open the set, "Back up now" starts a real backup).
// PRECONDITION (FIXTURE): the resellers, their administrators and their customers are made through the server's
// administrator API: the admin site has a Resellers page (src/Server/Web/app.js PAGES.resellers) but no menu entry
// leads to it, and new customers sign up from the client software.
// Oracles outside the page: the server's answers to the reseller's own session, the other customer's Profile.xml on
// disk (SHA-256 before and after), the server's run records.
import { test, expect } from '../lib/fixtures';
import { Page } from '@playwright/test';
import { goldenDataset, CUSTOMER_PASSWORD, World } from '../lib/world';
import { openSet, backUpNow } from '../lib/ui';
import { trySignIn, serviceResults } from './p-helpers';
import { rail, until, find } from './q-helpers';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';

const sha = (f: string) => crypto.createHash('sha256').update(fs.readFileSync(f)).digest('hex');
async function ok(world: World, method: string, p: string, body?: Record<string, string | number>) { const r = await world.adminApi(method, p, body); expect(r.status, method + ' ' + p + ': ' + r.text).toBe(200); return r.text; }

test('P11 AU-06 a reseller\'s administrator sees and changes only its own customers in the browser; the other reseller\'s customer stays untouched', async ({ browser, world, evidence }) => {
  evidence.step('PRECONDITION: two resellers, an administrator each, a customer each (server API)');
  for (const [id, name] of [['resa', 'Reseller A'], ['resb', 'Reseller B']]) {
    await ok(world, 'POST', 'vendors', { id, name, maxUsers: 0, maxQuotaGB: 0 });
    await ok(world, 'POST', 'vendors/' + id + '/admins', { login: id + '-admin', password: 'Vendor-Pass-' + id });
  }
  await ok(world, 'POST', 'users', { login: 'acme-a', password: CUSTOMER_PASSWORD, alias: 'Acme (A)', quotaGB: 5, email: 'a@example.invalid', vendor: 'resa' });
  await ok(world, 'POST', 'users', { login: 'beta-b', password: CUSTOMER_PASSWORD, alias: 'Beta (B)', quotaGB: 5, email: 'b@example.invalid', vendor: 'resb' });
  const far = new Date(Date.now() + 6 * 3600000);
  const mk = (login: string, set: string) => {
    const ag = world.agent(login, login.toUpperCase() + '-PC'); ag.register();
    const src = path.join(world.dir, 'data-' + login); goldenDataset(src);
    const id = ag.addSet(set, [src], ['--hour', String(far.getHours()), '--minute', '0']);
    const b = ag.backup(id); expect(b.out, b.out).toMatch(/^BS_STOP_SUCCESS /m);
    return { ag, id };
  };
  const A = mk('acme-a', 'Acme files'), B = mk('beta-b', 'Beta secrets');
  const profileB = find(world.usersDir, /(^|\/)beta-b\/db\/Profile\.xml$/)[0];
  expect(profileB, 'Profile.xml of beta-b on the server').toBeTruthy();
  const shaB = sha(profileB), runsB = (await world.runs('beta-b')).length;

  evidence.step('Reseller A\'s administrator signs in to the admin site');
  const ctx = await browser.newContext({ locale: 'en-US', viewport: { width: 1366, height: 860 } });
  const page: Page = await ctx.newPage(); evidence.watch(page);
  const si = await trySignIn(page, world.url, 'resa-admin', 'Vendor-Pass-resa', '');
  expect(si.status, si.shown).toBe(200);
  await expect(page.locator('.rail button[data-k="cust"]')).toHaveText(/My customers/);
  await expect(page.locator('.rail button[data-k="admins"]'), 'no server settings for a reseller').toHaveCount(0);

  evidence.step('My customers: only its own');
  await rail(page, 'cust');
  await expect(page.getByRole('row').filter({ hasText: 'acme-a' })).toHaveCount(1);
  await expect(page.locator('main')).not.toContainText('beta-b');
  await expect(page.locator('main')).not.toContainText('Beta (B)');
  evidence.step('Tasks: only its own customer\'s runs');
  await rail(page, 'tasks');
  await page.waitForLoadState('networkidle');
  await expect(page.getByRole('row').filter({ hasText: 'Acme files' }).first()).toBeVisible();
  await expect(page.locator('main')).not.toContainText('Beta secrets');

  evidence.step('the other reseller\'s customer opened by its address (as a link or a guessed URL would): nothing of it is shown');
  await page.evaluate(([setId]) => sessionStorage.setItem('obNav', JSON.stringify({ page: 'customer', login: 'beta-b', ctab: 'sets', set: setId, stab: 'general' })), [B.id]);
  await page.reload();
  await expect(page.locator('.rail button[data-k]').first()).toBeVisible();
  await page.waitForLoadState('networkidle');
  await expect(page.locator('main')).not.toContainText('Beta secrets');
  await expect(page.locator('main')).not.toContainText(path.join(world.dir, 'data-beta-b'));

  evidence.step('what the browser could send for the other customer is refused by the server');
  const ses = (await page.evaluate(() => sessionStorage.getItem('obAdmin')))!;
  const call = async (method: string, p: string, body?: string) => (await fetch(world.url + '/api/admin/' + p, { method, headers: { 'Content-Type': 'application/xml', 'X-Session': ses }, body })).status;
  expect(await call('GET', 'users/acme-a/sets/' + A.id), 'its own customer\'s set').toBe(200);
  const list = await (await fetch(world.url + '/api/admin/users', { headers: { 'X-Session': ses } })).text();
  expect(list).toContain('acme-a'); expect(list).not.toContain('beta-b');
  expect(await call('GET', 'users/beta-b/sets/' + B.id), 'read the other customer\'s set').toBe(403);
  expect(await call('GET', 'users/beta-b/computers'), 'the other customer\'s computers').toBe(403);
  const forged = '<m><f n="set">&lt;BACKUP_SET ID="' + B.id + '" NAME="taken over"/&gt;</f></m>';
  expect(await call('POST', 'users/beta-b/sets/' + B.id, forged), 'change the other customer\'s set').toBe(403);
  expect(await call('POST', 'users/beta-b/sets/' + B.id + '/run', '<m/>'), 'start its backup').toBe(403);
  expect(await call('POST', 'users/beta-b/delete', '<m/>'), 'delete the other customer').toBe(403);
  expect(await call('GET', 'vendors'), 'the resellers list').toBe(403);

  evidence.step('ORACLE (server disk and records): nothing of the other customer changed, no run started');
  await new Promise((r) => setTimeout(r, 2000));
  expect(sha(profileB), 'Profile.xml of beta-b').toBe(shaB);
  expect((await world.runs('beta-b')).length).toBe(runsB);
  expect(fs.existsSync(profileB)).toBe(true);

  evidence.step('its own customer: the set opens, Back up now starts a real backup on the customer\'s computer');
  A.ag.startService();
  await openSet(page, 'acme-a', 'Acme files');
  await backUpNow(page);
  const r = await until('the run of acme-a', () => serviceResults(A.ag.log, 'Acme files')[0], 150000, 2000);
  expect(r.result).toBe('BS_STOP_SUCCESS');
  A.ag.stopService();

  evidence.step('Reseller B\'s administrator: only beta-b (the other way round)');
  const p2 = await (await browser.newContext({ locale: 'en-US' })).newPage(); evidence.watch(p2);
  expect((await trySignIn(p2, world.url, 'resb-admin', 'Vendor-Pass-resb', '')).status).toBe(200);
  await rail(p2, 'cust');
  await expect(p2.getByRole('row').filter({ hasText: 'beta-b' })).toHaveCount(1);
  await expect(p2.locator('main')).not.toContainText('acme-a');
});
