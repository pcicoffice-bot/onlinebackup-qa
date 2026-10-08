// P06 (SH-01, SH-02) — the truth on the admin site after a success, a failure and a killed agent, end to end:
// one set lives through four runs of the real agent program — (1) a backup that succeeds, (2) a backup whose source
// folder is gone (failed), (3) a backup started with "Back up now" on the site and killed in the middle (the program
// dies; the computer comes back), (4) a backup that succeeds again. After each, the site's tasks page holds exactly
// the runs that happened, newest first, each with its true status; "Active backups" shows the run while it really runs
// and nothing once it is dead (no ghost); the set keeps the time of its last GOOD backup next to a red "Last run
// failed" (last backup vs last result). The last point restores identical (SHA-256).
// Oracles outside the page: the server's run records (/api/admin/tasks, /api/admin/live), the set in Profile.xml on
// the server's disk (LAST_BACKUP_COMPLETE, LAST_RESULT), the agent program's exit, SHA-256 of the restore.
import { test, expect } from '../lib/fixtures';
import { Page } from '@playwright/test';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import { openSet, openCustomer, backUpNow } from '../lib/ui';
import { diskSet } from '../lib/ui-oracle';
import { rail, until } from './q-helpers';
import { bigFiles } from '../lib/fault';
import * as fs from 'fs';
import * as path from 'path';

const SET = 'Truth';
/** The Backup rows of the set on the tasks page, top to bottom, as the status word each shows. */
async function taskStatuses(page: Page) {
  await rail(page, 'dash'); await rail(page, 'tasks');
  await expect(page.locator('main h1')).toHaveText(/Tasks/);
  await page.waitForLoadState('networkidle');
  const rows = page.getByRole('row').filter({ hasText: SET }).filter({ has: page.getByRole('cell', { name: 'Backup', exact: true }) });
  const texts = await rows.allInnerTexts();
  return texts.map((t) => (/Succeeded/.test(t) ? 'Succeeded' : /Failed/.test(t) ? 'Failed' : /Stopped/.test(t) ? 'Stopped' : /Warnings/.test(t) ? 'Warnings' : '? ' + t.replace(/\s+/g, ' ')));
}
async function liveRows(page: Page) {
  await rail(page, 'dash'); await rail(page, 'live');
  await expect(page.locator('main h1')).toHaveText('Active backups');
  await page.waitForLoadState('networkidle');
  return page.getByRole('row').filter({ hasText: SET }).count();
}
async function setRow(page: Page, login: string) {
  await rail(page, 'cust');
  await openCustomer(page, login);
  return page.getByRole('row').filter({ hasText: SET }).first();
}

test('P06 SH-01 SH-02 after a success, a failure and a killed agent, the site\'s history and "running now" are exact, and the last good backup time is kept', async ({ admin: page, world, evidence }) => {
  const login = 'qa-p06';
  world.addCustomer(login);
  const ag = world.agent(login, 'P06-PC'); ag.register();
  const src = path.join(world.dir, 'data'); goldenDataset(src);
  const id = ag.addSet(SET, [src]);
  const backups = async () => (await world.runs(login)).filter((r) => r.kind === 'Backup' && r.set === id).map((r) => r.status);

  // ---------------------------------------------------------------- 1. success
  evidence.step('1. a backup that succeeds');
  const b1 = ag.backup(id); expect(b1.out, b1.out).toMatch(/^BS_STOP_SUCCESS /m);
  expect(await taskStatuses(page)).toEqual(['Succeeded']);
  expect(await liveRows(page)).toBe(0);
  expect(await backups()).toEqual(['ok']);
  const goodAt = diskSet(world, login, id).attrs.LAST_BACKUP_COMPLETE;
  expect(goodAt, 'the server keeps the time of the good backup').toMatch(/^\d+$/);
  const row1 = await setRow(page, login);
  const shownGood = (await row1.locator('.lastrun').innerText()).trim();
  expect(shownGood, 'the set shows the time of its last backup').not.toBe('—');
  await expect(row1.locator('.lastrun .pill.bad')).toHaveCount(0);

  // ---------------------------------------------------------------- 2. failure
  evidence.step('2. the source folder is gone: the backup fails');
  const away = src + '-away'; fs.renameSync(src, away);
  const b2 = ag.backup(id); expect(b2.code, b2.out).not.toBe(0);
  expect(b2.out).not.toMatch(/^BS_STOP_SUCCESS /m);
  fs.renameSync(away, src);
  expect(await taskStatuses(page), 'tasks page, newest first').toEqual(['Failed', 'Succeeded']);
  expect(await liveRows(page)).toBe(0);
  expect(await backups()).toEqual(['bad', 'ok']);
  evidence.step('SH-01: the last GOOD backup time is kept, next to a red "Last run failed"');
  expect(diskSet(world, login, id).attrs.LAST_BACKUP_COMPLETE).toBe(goodAt);
  expect(diskSet(world, login, id).attrs.LAST_RESULT).not.toMatch(/^BS_STOP_SUCCESS/);
  const row2 = await setRow(page, login);
  await expect(row2.locator('.lastrun .pill.bad')).toContainText('Last run failed');
  expect((await row2.locator('.lastrun').innerText()).startsWith(shownGood), 'the last-backup time shown after the failure: ' + (await row2.locator('.lastrun').innerText())).toBe(true);

  // ---------------------------------------------------------------- 3. killed agent
  evidence.step('3. 30 MB of new files; a slow backup started with "Back up now" on the site, run by the agent service');
  bigFiles(src, 1, 30);
  await world.setBandwidth(login, id, 150);
  ag.startService();
  await openSet(page, login, SET);
  await backUpNow(page);
  evidence.step('SH-02: while it runs, "Active backups" shows it exactly once, and the history does not call it done');
  await expect.poll(async () => liveRows(page), { timeout: 150000, intervals: [3000] }).toBe(1);
  expect(await world.live()).toEqual([id]);
  expect(await taskStatuses(page), 'a running backup is not in the history as finished').toEqual(['Failed', 'Succeeded']);
  evidence.step('KILL the agent program in the middle of the backup');
  ag.killService();
  evidence.step('the computer comes back (the service starts again): the dead run is closed at once');
  await world.setBandwidth(login, id, 0);
  ag.startService();
  try {
    await until('the killed run recorded on the server', async () => (await backups()).length === 3, 120000, 2000);
  } finally { ag.stopService(); }
  expect(await backups()).toEqual(['bad', 'bad', 'ok']);
  expect(await world.live(), 'no ghost on the server').toEqual([]);
  expect(await liveRows(page), 'no ghost on the site').toBe(0);
  expect(await taskStatuses(page), 'tasks page after the killed run').toEqual(['Failed', 'Failed', 'Succeeded']);
  expect(diskSet(world, login, id).attrs.LAST_BACKUP_COMPLETE, 'a killed run does not move the last good backup').toBe(goodAt);
  const row3 = await setRow(page, login);
  await expect(row3.locator('.lastrun .pill.bad')).toContainText('Last run failed');

  // ---------------------------------------------------------------- 4. success again
  evidence.step('4. the next backup succeeds');
  await new Promise((r) => setTimeout(r, 1100));
  const b4 = ag.backup(id); expect(b4.out, b4.out).toMatch(/^BS_STOP_SUCCESS /m);
  const now = manifest(src);
  expect(await taskStatuses(page)).toEqual(['Succeeded', 'Failed', 'Failed', 'Succeeded']);
  expect(await liveRows(page)).toBe(0);
  expect(await backups()).toEqual(['ok', 'bad', 'bad', 'ok']);
  expect(Number(diskSet(world, login, id).attrs.LAST_BACKUP_COMPLETE)).toBeGreaterThan(Number(goodAt));
  const row4 = await setRow(page, login);
  await expect(row4.locator('.lastrun .pill.bad')).toHaveCount(0);

  evidence.step('ORACLE: the last point restores identical (SHA-256)');
  const t = path.join(world.dir, 'restore');
  const r = ag.restore(id, t); expect(r.code, r.out).toBe(0);
  expect(compare(now, manifest(restoredPath(t, src)))).toEqual([]);
});
