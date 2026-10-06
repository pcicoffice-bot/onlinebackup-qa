// UI-01 — every screen of the admin site opens without errors, WITH real data behind it (J1 covers the menu of an empty
// server): a customer with a computer, a set, a good backup and a failed one. Every customer tab, every set-editor tab,
// the run details and their logs. Then: reload on a deep screen, two tabs and sign-out, a forged session.
// Oracle: the browser's console and network (no error, no failed request), the server's own session check
// (/api/admin/me), and the server's run records for what the screens must show.
import { test, expect, signIn } from '../lib/fixtures';
import { ADMIN, totp, freshTotp } from '../lib/world';
import { openSet, openCustomer } from '../lib/ui';
import { problems } from '../lib/ui-oracle';
import * as fs from 'fs';
import * as path from 'path';

const RAW = /undefined|NaN|\[object Object\]|Exception|Error \d{3}/;

test('UI-01 with real data: every customer tab, every set tab, run details and logs open without a console error or a failed request', async ({ page, world, evidence }) => {
  evidence.step('a customer, a computer, a set, one good backup and one failed backup');
  world.addCustomer('qa-ui1');
  const ag = world.agent('qa-ui1', 'UI1-PC'); ag.register();
  const src = path.join(world.dir, 'data'); fs.mkdirSync(src, { recursive: true });
  for (let i = 0; i < 20; i++) fs.writeFileSync(path.join(src, 'f' + i + '.txt'), 'file ' + i);
  const id = ag.addSet('Daily files', [src]);
  expect(ag.backup(id).out).toMatch(/^BS_STOP_SUCCESS /m);
  fs.renameSync(src, src + '-away');
  expect(ag.backup(id).out).not.toMatch(/^BS_STOP_SUCCESS /m);
  fs.renameSync(src + '-away', src);
  const runs = await world.runs('qa-ui1');
  expect(runs.map((r) => r.status).sort(), 'precondition (server records): one good and one failed run').toEqual(['bad', 'ok']);

  evidence.step('sign in with the Enter key (no mouse)');
  await page.goto(world.url + '/admin');
  await page.locator('form.login input[autocomplete=username]').fill(ADMIN.login);
  await page.locator('form.login input[type=password]').fill(ADMIN.password);
  await page.locator('form.login input.otp').fill(await freshTotp(ADMIN.totp, world.url));
  await page.locator('form.login input.otp').press('Enter');
  await expect(page.locator('.rail button[data-k]').first()).toBeVisible();

  evidence.step('every menu page, with data');
  const pages = await page.$$eval('.rail button[data-k]', (bs) => bs.map((b) => (b as HTMLElement).dataset.k!));
  for (const k of pages) {
    evidence.step('menu: ' + k);
    await page.locator('.rail button[data-k="' + k + '"]').click();
    await page.waitForLoadState('networkidle');
    await expect(page.locator('main h1, main h2').first()).toBeVisible();
    await expect(page.locator('main .note.warn'), 'page ' + k + ' shows no load error').toHaveCount(0);
    await expect(page.locator('main')).not.toContainText(RAW);
  }

  evidence.step('the tasks page shows both runs as the server records them, and each opens with its log');
  await page.locator('.rail button[data-k="tasks"]').click();
  await page.waitForLoadState('networkidle');
  const rows = page.getByRole('row').filter({ hasText: 'Daily files' });
  await expect(rows).toHaveCount(2);
  await expect(rows.filter({ hasText: 'Succeeded' })).toHaveCount(1);
  await expect(rows.filter({ hasText: 'Failed' })).toHaveCount(1);
  for (const st of ['Succeeded', 'Failed']) {
    await rows.filter({ hasText: st }).first().click();
    await page.waitForLoadState('networkidle');
    await expect(page.locator('.logbox').last(), 'the ' + st + ' run shows its log').toBeVisible();
    await expect(page.locator('.logbox').last()).not.toBeEmpty();
  }

  evidence.step('every tab of the customer');
  await openCustomer(page, 'qa-ui1');
  const ctabs = await page.locator('main .tabs[role=tablist]').first().locator('button[data-k]').evaluateAll((bs) => bs.map((b) => (b as HTMLElement).dataset.k!));
  expect(ctabs.length).toBeGreaterThan(5);
  for (const k of ctabs) {
    evidence.step('customer tab: ' + k);
    await page.locator('main .tabs[role=tablist]').first().locator('button[data-k="' + k + '"]').click();
    await page.waitForLoadState('networkidle');
    await expect(page.locator('main .tabs[role=tablist]').first().locator('button.on')).toHaveAttribute('data-k', k);
    await expect(page.locator('main .note.warn'), 'customer tab ' + k + ' shows no load error').toHaveCount(0);
    await expect(page.locator('main')).not.toContainText(RAW);
  }

  evidence.step('every tab of the set editor');
  await openSet(page, 'qa-ui1', 'Daily files');
  const stabs = await page.locator('.tabs[role=tablist]').last().locator('button').evaluateAll((bs) => bs.map((b) => (b as HTMLElement).dataset.k!));
  expect(stabs).toEqual(['general', 'src', 'sched', 'method', 'dest', 'ret', 'filter', 'enc', 'perf', 'cmd', 'rep', 'maint']);
  for (const k of stabs) {
    evidence.step('set tab: ' + k);
    await page.locator('.tabs[role=tablist]').last().locator('button[data-k="' + k + '"]').click();
    await page.waitForLoadState('networkidle');
    await expect(page.locator('.tabs[role=tablist]').last().locator('button.on')).toHaveAttribute('data-k', k);
    await expect(page.locator('main p.bad'), 'set tab ' + k + ' shows no error').toHaveCount(0);
    await expect(page.locator('main')).not.toContainText(RAW);
  }
  evidence.step('the set\'s Reports tab: 2 backups, 50% succeeded — the same as the server records; the failed one opens with its log');
  await page.locator('.tabs[role=tablist]').last().locator('button[data-k="rep"]').click();
  await expect(page.locator('.kpi', { hasText: 'Backups — 60 days' }).locator('.v')).toHaveText('2');
  await expect(page.locator('.kpi', { hasText: 'Succeeded' }).locator('.v')).toHaveText('50%');
  await page.getByRole('row').filter({ hasText: 'Failed' }).first().click();
  await expect(page.locator('.logbox').last()).toBeVisible();

  evidence.step('reload on the deep screen → the same set and tab, still signed in');
  await page.locator('.tabs[role=tablist]').last().locator('button[data-k="perf"]').click();
  await page.reload();
  await expect(page.locator('form.login')).toHaveCount(0);
  await expect(page.locator('main h1').first()).toBeVisible();
  await expect(page.locator('main')).toContainText('Daily files');
  await expect(page.locator('main')).not.toContainText(RAW);

  expect(problems(evidence), 'no console error and no failed request on any screen').toEqual([]);
});

test('UI-01 sign-out in one tab ends the session for every tab (server check); a forged session shows the sign-in form', async ({ page, world, evidence, context }) => {
  world.addCustomer('qa-ui1b');
  evidence.step('sign in; a second tab of the same browser');
  await signIn(page, world);
  const token = await page.evaluate(() => sessionStorage.getItem('obAdmin') || localStorage.getItem('obAdminLocal'));
  expect((await fetch(world.url + '/api/admin/me', { headers: { 'X-Session': token! } })).status, 'the server knows the session').toBe(200);
  const tab2 = await context.newPage(); evidence.watch(tab2);
  await tab2.goto(world.url + '/admin');
  const tab2Signed = await tab2.locator('.rail button[data-k]').first().isVisible({ timeout: 10000 }).catch(() => false);
  if (!tab2Signed) await signIn(tab2, world);
  await tab2.locator('.rail button[data-k="cust"]').click();
  await expect(tab2.getByRole('row').filter({ hasText: 'qa-ui1b' })).toBeVisible();
  const token2 = await tab2.evaluate(() => sessionStorage.getItem('obAdmin') || localStorage.getItem('obAdminLocal'));

  evidence.step('sign out in tab 1');
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page.locator('form.login')).toBeVisible();
  await expect.poll(async () => (await fetch(world.url + '/api/admin/me', { headers: { 'X-Session': token! } })).status, { timeout: 5000 }).toBe(401);
  evidence.step('tab 2: ' + (token2 === token ? 'the same session' : 'its own session') + ' — the next click');
  await tab2.locator('.rail button[data-k="tasks"]').click();
  if (token2 === token) {
    await expect(tab2.locator('form.login'), 'tab 2 shared the ended session: it must go back to the sign-in form').toBeVisible();
    await expect(tab2.locator('.rail')).toHaveCount(0);
  } else {
    expect((await fetch(world.url + '/api/admin/me', { headers: { 'X-Session': token2! } })).status, 'tab 2 has its own session, still valid on the server').toBe(200);
    await expect(tab2.locator('main h1')).toHaveText(/Tasks/);
  }

  evidence.step('a forged session in the browser → the sign-in form, no data');
  const ctx3 = await page.context().browser()!.newContext(); const p3 = await ctx3.newPage(); evidence.watch(p3);
  await p3.goto(world.url + '/admin');
  await p3.evaluate(() => { sessionStorage.setItem('obAdmin', 'forged-' + 'x'.repeat(40)); });
  await p3.reload();
  await expect(p3.locator('form.login')).toBeVisible();
  await expect(p3.locator('.rail')).toHaveCount(0);
  await expect(p3.getByText('qa-ui1b')).toHaveCount(0);
  await ctx3.close();
  // the only refusals allowed are the server answering 401 to the dead / forged session (including the page's own
  // "sign out" call that it sends for a session the server already ended)
  const unexpected = problems(evidence).filter((m) => !/401 (GET|POST) .*\/api\/admin\/(me|tasks|users|dashboard|live|update|logout)(\?[^ ]*)?$/.test(m) && !/status of 401/.test(m));
  expect(unexpected, 'no other console error or failed request').toEqual([]);
});
