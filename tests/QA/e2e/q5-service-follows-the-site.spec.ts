// Q5 (AG-03, AG-02) — what is changed or pressed on the admin site reaches the RUNNING agent service, proven by what the
// agent does (its own output, the bytes the server receives, what the restore holds) — never by what the site shows.
// What the product claims: "A change of the settings reaches the computer within a minute" (set editor, Maintenance);
// SET-030 "back up now" and "stop" from the server; the stopped run: "what was sent is kept and the rest continues in
// the next run" (src/Agent/BackupRun.cs).
//  1. AG-03 schedule: the time is changed on the site → the running service backs up by itself at that minute.
//  2. AG-03 upload limit: 200 KB/s set on the site → the running service's next backup reaches the server at ≤ that rate.
//  3. AG-02 back up now + stop: pressed on the site → the service starts, then ends the run as stopped; what was sent
//     restores identical, the rest is missing; the limit lifted + back up now → everything restores identical.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath, Manifest } from '../lib/world';
import { openSet, tab, saveAndExit } from '../lib/ui';
import { tree, until } from './q-helpers';
import * as fs from 'fs';
import * as path from 'path';
import * as crypto from 'crypto';

/** The service's own result lines: "<local time> Office files: BS_STOP_..." */
function results(log: string, set: string) {
  if (!fs.existsSync(log)) return [] as { at: string, result: string }[];
  return [...fs.readFileSync(log, 'utf8').matchAll(new RegExp('^(\\d{4}-\\d\\d-\\d\\dT\\d\\d:\\d\\d:\\d\\d) ' + set + ': (BS_[A-Z_]+)', 'gm'))].map((m) => ({ at: m[1], result: m[2] }));
}
const bytesOn = (dir: string) => [...tree(dir).values()].reduce((a, x) => a + x, 0);
const localIso = (d: Date) => new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 19);

test('Q5 AG-03/AG-02 schedule, upload limit, back up now and stop set on the site are obeyed by the running agent service', async ({ admin: page, world, evidence }) => {
  test.setTimeout(20 * 60 * 1000);
  world.addCustomer('qa-svc');
  const ag = world.agent('qa-svc', 'SVC-PC1'); ag.register();
  const src = path.join(world.dir, 'data'); goldenDataset(src);
  const far = new Date(Date.now() + 3 * 3600000);
  const id = ag.addSet('Office files', [src], ['--hour', String(far.getHours()), '--minute', '0']);
  evidence.step('the customer\'s first backup (from the client program), then the service runs');
  expect(ag.backup(id).out).toMatch(/^BS_STOP_SUCCESS /m);
  ag.startService();
  const store = path.join(world.usersDir, 'qa-svc', 'files', id);

  // ---------------------------------------------------------------- 1. schedule
  const slot = new Date(Date.now() + 100000); slot.setSeconds(0, 0); slot.setMinutes(Math.ceil(slot.getMinutes() / 5) * 5);
  evidence.step('Schedule on the site: ' + slot.toTimeString().slice(0, 5) + ' (the agent works in the computer\'s local time)');
  await openSet(page, 'qa-svc', 'Office files');
  await tab(page, 'Schedule');
  await page.locator('.sched').first().locator('select').nth(0).selectOption(String(slot.getHours()));
  await page.locator('.sched').first().locator('select').nth(1).selectOption(String(slot.getMinutes()));
  await saveAndExit(page);
  fs.writeFileSync(path.join(src, 'Documents/before-the-slot.txt'), 'changed before the scheduled time');
  const v2 = manifest(src);
  evidence.step('ORACLE (agent): the service runs the backup by itself at that minute, not before');
  const r1 = await until('a scheduled run in the service\'s output', () => results(ag.log, 'Office files')[0], 10 * 60000, 5000);
  console.log('Q5 FACT schedule: slot ' + localIso(slot) + ', the service\'s run ended ' + r1.at + ' ' + r1.result);
  expect(r1.result).toBe('BS_STOP_SUCCESS');
  expect(r1.at >= localIso(slot), 'the run at ' + r1.at + ' is not before the slot ' + localIso(slot)).toBe(true);
  const t1 = path.join(world.dir, 'restore-scheduled');
  const rr1 = ag.restore(id, t1); expect(rr1.code, rr1.out).toBe(0);
  expect(compare(v2, manifest(restoredPath(t1, src)))).toEqual([]);

  // ---------------------------------------------------------------- 2. upload limit + 3. back up now and stop
  evidence.step('Resources on the site: upload limit 200 KB/s');
  await openSet(page, 'qa-svc', 'Office files');
  await tab(page, 'Resources');
  await page.locator('.form .fr').filter({ hasText: 'Upload limit' }).locator('input').first().fill('200');
  await saveAndExit(page);
  fs.mkdirSync(path.join(src, 'New'), { recursive: true });
  for (let i = 0; i < 40; i++) fs.writeFileSync(path.join(src, 'New', 'part-' + String(i).padStart(2, '0') + '.bin'), crypto.randomBytes(256 * 1024));
  const v3 = manifest(src);
  evidence.step('Maintenance on the site: Back up now');
  await openSet(page, 'qa-svc', 'Office files');
  await tab(page, 'Maintenance');
  const startBytes = bytesOn(store);
  await page.getByRole('button', { name: '▶ Back up now' }).last().click();
  await expect(page.locator('#toast').filter({ hasText: 'The backup was started' })).toBeVisible();
  evidence.step('ORACLE (server disk): the agent started sending');
  await until('the server receives new bytes', () => bytesOn(store) > startBytes + 100 * 1024, 120000, 1000);
  const a = bytesOn(store), ta = Date.now();
  await new Promise((r) => setTimeout(r, 15000));
  const rate = (bytesOn(store) - a) / ((Date.now() - ta) / 1000) / 1024;
  evidence.step('ORACLE (server disk): it arrives at ' + rate.toFixed(0) + ' KB/s (the limit is 200; without it a local upload of 10 MB takes about a second)');
  console.log('Q5 FACT upload limit 200 KB/s: measured on the server disk ' + rate.toFixed(1) + ' KB/s over 15 s');
  expect(rate).toBeGreaterThan(20);
  expect(rate).toBeLessThan(300);

  evidence.step('Stop on the site');
  await page.getByRole('button', { name: '■ Stop' }).last().click();
  await expect(page.locator('#toast').filter({ hasText: 'A stop request was sent' })).toBeVisible();
  const stopAt = Date.now();
  evidence.step('ORACLE (agent): the service ends the run as stopped');
  const r2 = await until('the stopped run in the service\'s output', () => results(ag.log, 'Office files')[1], 180000, 2000);
  expect(r2.result).toBe('BS_STOP_BY_USER');
  const stoppedAfter = (Date.now() - stopAt) / 1000;
  const sent = bytesOn(store); await new Promise((r) => setTimeout(r, 8000));
  expect(bytesOn(store), 'the server receives nothing more after the stop').toBe(sent);
  evidence.step('ORACLE (restore): what was sent is kept and identical; the rest is not there (stopped ' + stoppedAfter.toFixed(0) + ' s after the press)');
  const t2 = path.join(world.dir, 'restore-stopped');
  const rr2 = ag.restore(id, t2); expect(rr2.code, rr2.out).toBe(0);
  const got = manifest(restoredPath(t2, src));
  const notThere = [...v3.keys()].filter((k) => !got.has(k));
  console.log('Q5 FACT stop: ' + r2.result + ' ' + stoppedAfter.toFixed(0) + ' s after the press; files of the 40 new ones not in the stopped point: ' + notThere.length);
  expect(notThere.length, 'files the stopped run did not send').toBeGreaterThan(0);
  expect(notThere.every((k) => k.startsWith('New/')), notThere.join(', ')).toBe(true);
  const sub: Manifest = new Map([...v3].filter(([k]) => got.has(k)));
  expect(compare(sub, got)).toEqual([]);

  evidence.step('the limit is lifted on the site; Back up now: the rest arrives, everything restores identical');
  await openSet(page, 'qa-svc', 'Office files');
  await tab(page, 'Resources');
  await page.locator('.form .fr').filter({ hasText: 'Upload limit' }).locator('input').first().fill('0');
  await saveAndExit(page);
  await openSet(page, 'qa-svc', 'Office files');
  await tab(page, 'Maintenance');
  await page.getByRole('button', { name: '▶ Back up now' }).last().click();
  const r3 = await until('the next run in the service\'s output', () => results(ag.log, 'Office files')[2], 180000, 2000);
  expect(r3.result).toBe('BS_STOP_SUCCESS');
  ag.stopService();
  const t3 = path.join(world.dir, 'restore-final');
  const rr3 = ag.restore(id, t3); expect(rr3.code, rr3.out).toBe(0);
  expect(compare(v3, manifest(restoredPath(t3, src)))).toEqual([]);
});
