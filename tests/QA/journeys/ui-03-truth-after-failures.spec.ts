// UI-03 — the admin site tells the truth after a backup that did not finish: stopped by the technician, the server
// crashed under it, the computer died and never came back. Never green ("Succeeded"), never "running" for a dead run.
// Oracle (outside the page): the server's run records (/api/admin/tasks, /api/admin/live), the set's LAST_RESULT in
// Profile.xml on the server's disk, and the agent's own exit code / log.
import { test, expect, signIn } from '../lib/fixtures';
import { Page } from '@playwright/test';
import { goldenDataset, World } from '../lib/world';
import { openSet, openCustomer, backUpNow } from '../lib/ui';
import { diskSet } from '../lib/ui-oracle';
import * as fs from 'fs';
import * as path from 'path';

/** The Backup rows of a set on the tasks page, as text (status words included). */
async function taskRows(page: Page, setName: string) {
  await page.locator('.rail button[data-k="tasks"]').click();
  await page.waitForLoadState('networkidle');
  await expect(page.locator('main h1')).toHaveText(/Tasks/);
  const rows = page.getByRole('row').filter({ hasText: setName }).filter({ has: page.getByRole('cell', { name: 'Backup', exact: true }) });
  return rows.allInnerTexts();
}
/** Waits until the tasks page shows a run of the set with this status; fails at once if it ever shows "Succeeded". */
async function waitForTruth(page: Page, setName: string, status: 'Failed' | 'Stopped', minutes: number) {
  const until = Date.now() + minutes * 60000; let seen: string[] = [];
  while (Date.now() < until) {
    seen = await taskRows(page, setName);
    expect(seen.filter((r) => /Succeeded/.test(r)), 'a run that did not finish must never be shown as Succeeded').toEqual([]);
    if (seen.some((r) => r.includes(status))) return seen;
    await page.waitForTimeout(10000);
  }
  throw new Error('after ' + minutes + ' min the tasks page shows no "' + status + '" run of "' + setName + '"; rows: ' + JSON.stringify(seen));
}
async function liveRowCount(page: Page, setName: string) {
  await page.locator('.rail button[data-k="live"]').click();
  await page.waitForLoadState('networkidle');
  await expect(page.locator('main h1')).toHaveText('Active backups');
  return page.getByRole('row').filter({ hasText: setName }).count();
}
async function dashboardTruth(page: Page, setName: string) {
  await page.locator('.rail button[data-k="dash"]').click();
  await page.waitForLoadState('networkidle');
  await expect(page.getByText('No backup is running now.'), 'dashboard: nothing running').toBeVisible();
  return page;
}
const lastResult = (world: World, login: string, id: string) => diskSet(world, login, id).attrs.LAST_RESULT;

test('UI-03 stopped by the technician: "Stopped" (never Succeeded), not running anywhere on the site', async ({ admin: page, world, evidence }) => {
  world.addCustomer('qa-ui3s');
  const ag = world.agent('qa-ui3s', 'UI3S-PC'); ag.register();
  const src = path.join(world.dir, 'data'); goldenDataset(src);
  const id = ag.addSet('Stop me', [src]);
  await world.setBandwidth('qa-ui3s', id, 150);
  ag.startService();
  await openSet(page, 'qa-ui3s', 'Stop me');
  await backUpNow(page);
  evidence.step('the site shows it running');
  await expect.poll(async () => liveRowCount(page, 'Stop me'), { timeout: 150000, intervals: [5000] }).toBe(1);
  expect(await world.live()).toContain(id);

  evidence.step('■ Stop in the set');
  await openSet(page, 'qa-ui3s', 'Stop me');
  await page.locator('.head .acts').getByRole('button', { name: '■ Stop' }).click();
  await expect(page.locator('#toast')).toContainText('A stop request was sent');

  evidence.step('the tasks page: Stopped, never Succeeded');
  await waitForTruth(page, 'Stop me', 'Stopped', 4);
  evidence.step('the live page and the dashboard: nothing running');
  expect(await liveRowCount(page, 'Stop me')).toBe(0);
  await dashboardTruth(page, 'Stop me');
  evidence.step('the set list: no date of a backup that did not happen, nothing green');
  await openCustomer(page, 'qa-ui3s');
  const setRow = page.getByRole('row').filter({ hasText: 'Stop me' }).first();
  await expect(setRow.locator('.lastrun')).toHaveText('—');
  // the set never completed a backup ("Last backup —"); a green pill in its row (e.g. "Restore test: Passed" from a
  // sample test of the partial, stopped run) reads as "this set is fine". Soft, so the oracle checks below still run.
  await expect.soft(setRow.locator('.pill.ok'), 'a set whose only run was stopped shows a green pill: ' + (await setRow.locator('.pill.ok').allInnerTexts()).join(', ')).toHaveCount(0);

  evidence.step('ORACLE: the server records and the agent log');
  const runs = await world.runs('qa-ui3s');
  expect(runs.map((r) => r.status), 'server run records').toContain('stopped');
  expect(runs.filter((r) => r.kind === 'Backup' && r.status === 'ok'), 'no backup run recorded as succeeded').toEqual([]);
  expect(await world.live()).not.toContain(id);
  expect(lastResult(world, 'qa-ui3s', id)).toBe('BS_STOP_BY_USER');
  expect(diskSet(world, 'qa-ui3s', id).attrs.LAST_BACKUP_COMPLETE ?? '', 'no "last backup" time for a stopped run').toBe('');
  expect(fs.readFileSync(ag.log, 'utf8')).toMatch(/BS_STOP_BY_USER/);
});

test('UI-03 the server crashes under a backup and comes back: the site shows the run Failed in red, nothing running', async ({ page, world, evidence }) => {
  world.addCustomer('qa-ui3k');
  const ag = world.agent('qa-ui3k', 'UI3K-PC'); ag.register();
  const src = path.join(world.dir, 'data'); goldenDataset(src);
  const id = ag.addSet('Crash under me', [src]);
  await world.setBandwidth('qa-ui3k', id, 150);
  await signIn(page, world);

  evidence.step('a slow backup on the computer (its own process: nothing restarts it)');
  const p = ag.backupProcess(id);
  await expect.poll(async () => liveRowCount(page, 'Crash under me'), { timeout: 150000, intervals: [5000] }).toBe(1);
  evidence.step('KILL the server');
  world.killServer();
  const code = await new Promise<number>((r) => p.on('exit', (c) => r(c ?? -1)));
  expect(code, 'the computer must not report this run as successful').not.toBe(0);
  evidence.step('the server starts again; the technician opens the site');
  await world.startServer();
  await page.goto(world.url + '/admin');
  if (await page.locator('form.login').isVisible().catch(() => false)) await signIn(page, world);
  await expect(page.locator('.rail button[data-k]').first()).toBeVisible();

  evidence.step('nothing is shown as running');
  expect(await liveRowCount(page, 'Crash under me')).toBe(0);
  expect(await world.live()).not.toContain(id);
  await dashboardTruth(page, 'Crash under me');
  evidence.step('the tasks page shows the run Failed (after the server notices the dead run), never Succeeded');
  await waitForTruth(page, 'Crash under me', 'Failed', 9);
  evidence.step('the set list: red "Last run failed"');
  await openCustomer(page, 'qa-ui3k');
  const setRow = page.getByRole('row').filter({ hasText: 'Crash under me' }).first();
  await expect(setRow.locator('.lastrun .pill.bad')).toContainText('Last run failed');
  evidence.step('the dashboard counts it as failed and lists it under "Needs attention"');
  await page.locator('.rail button[data-k="dash"]').click();
  await page.waitForLoadState('networkidle');
  await expect(page.locator('.tile', { hasText: 'Failed' }).locator('.v')).not.toHaveText('0');
  await expect(page.locator('section.card', { hasText: 'Needs attention' }).getByRole('row').filter({ hasText: 'Crash under me' })).toContainText('Failed');

  evidence.step('ORACLE: the server records');
  const runs = await world.runs('qa-ui3k');
  expect(runs.map((r) => r.status)).toContain('bad');
  expect(runs.filter((r) => r.kind === 'Backup' && r.status === 'ok'), 'no backup run recorded as succeeded').toEqual([]);
  expect(lastResult(world, 'qa-ui3k', id)).toBe('BS_STOP_BY_SYSTEM_ERROR');
});

test('UI-03 the computer dies during a backup and never comes back: the "running" row goes away and the run is shown Failed', async ({ admin: page, world, evidence }) => {
  world.addCustomer('qa-ui3d');
  const ag = world.agent('qa-ui3d', 'UI3D-PC'); ag.register();
  const src = path.join(world.dir, 'data'); goldenDataset(src);
  const id = ag.addSet('Dead computer', [src]);
  await world.setBandwidth('qa-ui3d', id, 150);
  ag.startService();
  await openSet(page, 'qa-ui3d', 'Dead computer');
  await backUpNow(page);
  await expect.poll(async () => liveRowCount(page, 'Dead computer'), { timeout: 150000, intervals: [5000] }).toBe(1);

  evidence.step('KILL the backup program; the computer stays off');
  ag.killService();
  const killedAt = Date.now();
  evidence.step('the "running" row goes away (no ghost) — within 4 minutes');
  await expect.poll(async () => liveRowCount(page, 'Dead computer'), { timeout: 4 * 60000, intervals: [10000] }).toBe(0);
  evidence.step('ghost shown for ' + Math.round((Date.now() - killedAt) / 1000) + ' s');
  expect(await world.live()).not.toContain(id);
  await dashboardTruth(page, 'Dead computer');
  evidence.step('the tasks page shows the run Failed, never Succeeded');
  await waitForTruth(page, 'Dead computer', 'Failed', 8);
  await openCustomer(page, 'qa-ui3d');
  await expect(page.getByRole('row').filter({ hasText: 'Dead computer' }).first().locator('.lastrun .pill.bad')).toContainText('Last run failed');

  evidence.step('ORACLE: the server records');
  const runs = await world.runs('qa-ui3d');
  expect(runs.map((r) => r.status)).toContain('bad');
  expect(runs.filter((r) => r.kind === 'Backup' && r.status === 'ok'), 'no backup run recorded as succeeded').toEqual([]);
  expect(lastResult(world, 'qa-ui3d', id)).toBe('BS_STOP_BY_SYSTEM_ERROR');
  expect(diskSet(world, 'qa-ui3d', id).attrs.LAST_BACKUP_COMPLETE ?? '').toBe('');
});
