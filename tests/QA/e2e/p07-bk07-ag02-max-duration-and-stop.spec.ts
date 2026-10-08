// P07 (BK-07, AG-02) — the maximum duration and "Stop" from the admin site, end to end on the real programs:
//  1. On the site (Schedule: "Stop the backup after" 1 hour) — a backup of ~23 MB through a 100 KB/s line (the test's
//     proxy, runner/nproxy.mjs) cannot finish in its hour. The computer's clock runs 60× fast for the agent program
//     (libfaketime, as N10: the hour passes in ~1 minute of real time; the line keeps real time — the agent's own upload
//     limit cannot be used here, libfaketime shortens its sleeps as well). The run ends STOPPED, never Succeeded — on the
//     computer, in the server's record and on the Tasks page; what it stored restores identical, the rest is not there.
//     The line is fast again from then on.
//  2. No maximum duration any more, an upload limit of 200 KB/s instead (both set on the site); the agent runs as the
//     Windows service does (real clock); "Back up now" on the site starts it, "■ Stop" on the site ends it as stopped; the server receives nothing more after it.
//  3. The upload limit lifted on the site; "Back up now": the run completes — Succeeded on the Tasks page — and the
//     backup restores identical to the source (SHA-256).
// Oracles outside the page: the agent's own result lines, the server's run records, the bytes on the server's disk,
// SHA-256 of every restored file.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath, Manifest } from '../lib/world';
import { openSet, backUpNow, waitForTask } from '../lib/ui';
import { row, editorTab, diskSet, schedules } from '../lib/ui-oracle';
import { hasProgram, bigFiles } from '../lib/fault';
import { agentAsync, result, Line } from '../lib/nfault';
import { serviceResults, bytesOn } from './p-helpers';
import { until } from './q-helpers';
import { Page } from '@playwright/test';
import * as path from 'path';

const SET = 'Max duration';
const FAST = { wrap: ['faketime', '-f', '+0 x60'], env: { FAKETIME_DONT_FAKE_MONOTONIC: '1' } };

async function backupRows(page: Page) {
  await page.locator('.rail button[data-k="tasks"]').click();
  await page.waitForLoadState('networkidle');
  await expect(page.locator('main h1')).toHaveText(/Tasks/);
  return page.getByRole('row').filter({ hasText: SET }).filter({ has: page.getByRole('cell', { name: 'Backup', exact: true }) });
}

test('P07 BK-07/AG-02 a backup past its maximum duration ends Stopped; Stop on the site ends the service\'s run; the next run completes and restores identical', async ({ admin: page, world, evidence }) => {
  test.skip(!hasProgram('faketime'), 'NOT TESTED: libfaketime is not installed (the maximum duration is at least 1 hour)');
  test.setTimeout(10 * 60 * 1000);
  const login = 'qa-p07';
  world.addCustomer(login);
  const line = await new Line(world, 'p07-line').start();
  const ag = world.agent(login, 'P07-PC'); ag.register(line.url);
  const src = path.join(world.dir, 'data'); let data = goldenDataset(src);
  const far = new Date(Date.now() + 12 * 3600000);   // the scheduled time far away (12 h: also for the agent's fast clock)
  const id = ag.addSet(SET, [src], ['--hour', String(far.getHours()), '--minute', '0']);
  const store = path.join(world.usersDir, login, 'files', id);

  // ------------------------------------------------------------------ 1. maximum duration
  try {
  evidence.step('on the site: stop the backup after 1 hour');
  await openSet(page, login, SET);
  await editorTab(page, 'Schedule');
  await row(page, 'Stop the backup after').locator('select').selectOption('1');
  await page.getByRole('button', { name: 'Save and exit' }).click();
  await expect(page.locator('#toast').filter({ hasText: 'Saved' })).toBeVisible();
  expect(schedules(diskSet(world, login, id)).map((s) => s.duration), 'ORACLE (server disk): the duration the computer gets').toEqual(['1']);

  evidence.step('the line to the server: 100 KB/s; the computer backs up with its clock 60× fast — its hour passes in about a minute');
  await line.ctl('/rate?bps=' + 100 * 1024);
  const t0 = Date.now();
  const run = agentAsync(ag, ['backup', '--set', id], FAST);
  const r1 = await Promise.race([run.done, new Promise<null>((r) => setTimeout(() => r(null), 4 * 60000))]);
  if (!r1) run.p.kill('SIGKILL');
  expect(r1, 'the run ended by itself at its maximum duration').not.toBeNull();
  const secs = (Date.now() - t0) / 1000;
  evidence.step('the run: ' + result(r1!.out) + ' after ' + secs.toFixed(0) + ' s real');
  expect(result(r1!.out), 'past its maximum duration: stopped, never a success\n' + r1!.out).toBe('BS_STOP_BY_USER');
  expect(r1!.out).toMatch(/maximum duration of 1 hours/);
  expect(secs, 'it ran its hour (≈ 60 s real), it did not stop at once').toBeGreaterThan(40);
  const st = await line.stats();
  evidence.step('PROOF: bytes up through the 100 KB/s line: ' + st.c2sWhileThrottled);
  expect(st.c2sWhileThrottled, 'the run went through the slow line').toBeGreaterThan(1024 * 1024);
  await line.ctl('/rate?bps=0');
  const runs1 = (await world.runs(login)).filter((r) => r.kind === 'Backup');
  expect(runs1.map((r) => r.status), 'the server\'s record').toEqual(['stopped']);

  evidence.step('the Tasks page: Stopped, never Succeeded');
  const rows1 = await backupRows(page);
  await expect(rows1).toHaveCount(1);
  await expect(rows1.first()).toContainText('Stopped');
  await expect(rows1.filter({ hasText: 'Succeeded' })).toHaveCount(0);

  evidence.step('ORACLE (restore): what the stopped run stored is identical; the rest is not there (nothing half-stored)');
  const t1 = path.join(world.dir, 'restore-after-duration');
  const rr1 = ag.restore(id, t1); expect(rr1.code, rr1.out).toBe(0);
  const got1 = manifest(restoredPath(t1, src));
  const sub1: Manifest = new Map([...data].filter(([k]) => got1.has(k)));
  expect(compare(sub1, got1)).toEqual([]);
  expect(got1.size, 'files in the stopped point').toBeLessThan(data.size);

  // ------------------------------------------------------------------ 2. Stop from the site, the agent service running
  evidence.step('on the site: no maximum duration; upload limit 200 KB/s');
  await openSet(page, login, SET);
  await editorTab(page, 'Schedule');
  await row(page, 'Stop the backup after').locator('select').selectOption('-1');
  await editorTab(page, 'Resources');
  await row(page, 'Upload limit').locator('input').fill('200');
  await page.getByRole('button', { name: 'Save and exit' }).click();
  await expect(page.locator('#toast').filter({ hasText: 'Saved' })).toBeVisible();
  expect(schedules(diskSet(world, login, id)).map((s) => s.duration)).toEqual(['-1']);
  expect(diskSet(world, login, id).attrs.BANDWIDTH_KBPS).toBe('200');

  evidence.step('12 MB of new files on the computer (one minute at 200 KB/s)');
  bigFiles(src, 1, 12);
  data = manifest(src);
  evidence.step('Back up now on the site; the agent service (real clock) starts the run');
  await openSet(page, login, SET);
  await backUpNow(page);
  const before = bytesOn(store);
  ag.startService();
  await until('the server receives the run\'s bytes', () => bytesOn(store) > before + 300 * 1024, 150000, 1000);
  evidence.step('■ Stop on the site');
  await openSet(page, login, SET);
  await page.locator('.head .acts').getByRole('button', { name: '■ Stop' }).click();
  await expect(page.locator('#toast')).toContainText('A stop request was sent');
  const r2 = await until('the service\'s stopped run', () => serviceResults(ag.log, SET)[0], 120000, 1000);
  expect(r2.result, 'ORACLE (agent): the service ended the run as stopped').toBe('BS_STOP_BY_USER');
  const sent = bytesOn(store); await new Promise((r) => setTimeout(r, 6000));
  expect(bytesOn(store), 'the server receives nothing more after the stop').toBe(sent);
  const runs2 = (await world.runs(login)).filter((r) => r.kind === 'Backup');
  expect(runs2.map((r) => r.status)).toEqual(['stopped', 'stopped']);
  const rows2 = await backupRows(page);
  await expect(rows2).toHaveCount(2);
  await expect(rows2.filter({ hasText: 'Stopped' })).toHaveCount(2);
  await expect(rows2.filter({ hasText: 'Succeeded' })).toHaveCount(0);

  // ------------------------------------------------------------------ 3. the next run completes
  evidence.step('on the site: the upload limit lifted; Back up now — the next run completes');
  await openSet(page, login, SET);
  await editorTab(page, 'Resources');
  await row(page, 'Upload limit').locator('input').fill('0');
  await page.getByRole('button', { name: 'Save and exit' }).click();
  await expect(page.locator('#toast').filter({ hasText: 'Saved' })).toBeVisible();
  ag.stopService();   // the service is started again below (as after a restart of the computer): its first round is at once
  await new Promise((r) => setTimeout(r, 3000));
  await openSet(page, login, SET);
  await backUpNow(page);
  ag.startService();
  const r3 = await until('the service\'s next run', () => serviceResults(ag.log, SET)[1], 180000, 2000);
  expect(r3.result).toBe('BS_STOP_SUCCESS');
  await waitForTask(page, SET, 'Succeeded', 2);
  ag.stopService();

  evidence.step('ORACLE (restore): every file identical to the source (SHA-256)');
  const t3 = path.join(world.dir, 'restore-final');
  const rr3 = ag.restore(id, t3); expect(rr3.code, rr3.out).toBe(0);
  expect(compare(data, manifest(restoredPath(t3, src)))).toEqual([]);
  } finally { line.stop(); }
});
