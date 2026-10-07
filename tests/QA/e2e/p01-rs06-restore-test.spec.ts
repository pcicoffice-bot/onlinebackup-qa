// P01 (RS-06) — the automatic monthly restore test, end to end, on the real programs:
// the agent program runs as the service does (its own loop decides that the test is due), on a real backup; the admin
// site's "Restore tests" page shows Passed with the exact counts; a source file changed after its backup is not a
// candidate (so not counted failed); a month later (the computer's clock moved +31 days with libfaketime, as F13 does)
// the test runs again by itself, and an object damaged on the server's disk makes it FAILED — red on the site; the
// test's temporary folder is gone after each test.
// Oracles outside the product: the counts come from the dataset the test made (4 files, 1 changed → 3 to compare;
// later 4, 1 damaged → 3 of 4), the damage is proven by a restore that fails for exactly one file, the server's
// Profile.xml on disk (RESTORE_TEST_RESULT), the agent's temp folder on disk, SHA-256 of a full restore.
import { test, expect } from '../lib/fixtures';
import { manifest, compare, restoredPath, World, Agent } from '../lib/world';
import { agentProcess, agentRun, hasProgram } from '../lib/fault';
import { diskSet } from '../lib/ui-oracle';
import { find, rail, until } from './q-helpers';
import { Page } from '@playwright/test';
import { ChildProcess } from 'child_process';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';

const SET = 'Monthly test';
const sha = (f: string) => crypto.createHash('sha256').update(fs.readFileSync(f)).digest('hex');
const restoreTests = async (world: World, login: string) => (await world.runs(login)).filter((r) => r.kind === 'RestoreTest');
/** Folders the restore test left in the agent's temp folder (it makes "restoretest-<8 hex>" there). */
const leftTemp = (ag: Agent) => { const t = path.join(ag.home, 'temp'); return fs.existsSync(t) ? fs.readdirSync(t).filter((n) => n.startsWith('restoretest-')) : []; };

/** The agent as the Windows service runs it (its own loop), optionally under a moved clock, until the server has `count`
 *  restore-test records of the customer; then stopped (SIGTERM), and gone before the next phase. */
async function serviceUntilTests(world: World, ag: Agent, login: string, count: number, wrap: string[] = []) {
  let svc: ChildProcess | undefined = agentProcess(ag, ['service'], wrap);
  try {
    await until('restore test record #' + count + ' on the server', async () => (await restoreTests(world, login)).length >= count, 180000, 2000);
  } finally {
    const pid = svc.pid!; svc.kill('SIGTERM'); await new Promise((r) => svc!.on('exit', r)); svc = undefined;
    for (let i = 0; i < 50; i++) { try { process.kill(-pid, 0); await new Promise((r) => setTimeout(r, 200)); } catch { break; } }
  }
}

const kpi = (page: Page, label: string) => page.locator('.card.kpi').filter({ has: page.locator('.l', { hasText: new RegExp('^' + label + '$') }) }).locator('.v');

async function restoreTestRow(page: Page) {
  await rail(page, 'dash'); await rail(page, 'restoretests');
  await expect(page.locator('main h1').first()).toHaveText('Restore tests');
  const row = page.getByRole('row').filter({ hasText: SET });
  await expect(row).toHaveCount(1);
  return row;
}

test('P01 RS-06 the monthly restore test runs by itself on a real backup: Passed 3/3 on the site, a changed source is not counted, a damaged object a month later is FAILED in red, no temp folder left', async ({ admin: page, world, evidence }) => {
  test.skip(!hasProgram('faketime'), 'NOT TESTED: libfaketime (faketime) is not installed — the month cannot pass for the agent program');
  const login = 'qa-p01';
  world.addCustomer(login);
  const ag = world.agent(login, 'P01-PC'); ag.register();
  const src = path.join(world.dir, 'data');
  fs.mkdirSync(path.join(src, 'Docs'), { recursive: true });
  fs.writeFileSync(path.join(src, 'Docs/letter.txt'), 'Dear customer,\nthe restore test reads this letter.\n');
  fs.writeFileSync(path.join(src, 'Docs/photo.bin'), crypto.randomBytes(300 * 1024 + 7));
  fs.writeFileSync(path.join(src, 'Docs/table.csv'), 'a,b\n1,2\n');
  fs.writeFileSync(path.join(src, 'Docs/changes-later.txt'), 'version 1 — backed up');
  const id = ag.addSet(SET, [src]);

  evidence.step('backup 1 (the computer\'s program, as the customer runs it)');
  const b1 = ag.backup(id); expect(b1.out, b1.out).toMatch(/^BS_STOP_SUCCESS .*new=4 /m);
  const atBackup = manifest(src);
  expect(atBackup.size).toBe(4);

  evidence.step('a source file changes after its backup (content and time): it is no longer a candidate');
  await new Promise((r) => setTimeout(r, 1100));
  fs.writeFileSync(path.join(src, 'Docs/changes-later.txt'), 'version 2 — changed after the backup, not backed up yet');
  expect(manifest(src).get('Docs/changes-later.txt')!.sha256).not.toBe(atBackup.get('Docs/changes-later.txt')!.sha256);

  evidence.step('the agent runs as the service: the first restore test is due after the first good backup');
  expect(await restoreTests(world, login)).toEqual([]);
  await serviceUntilTests(world, ag, login, 1);
  const t1 = await restoreTests(world, login);
  expect(t1.length, JSON.stringify(t1)).toBe(1);
  expect(t1[0].status, 'the first restore test on the server: ' + JSON.stringify(t1[0])).toBe('ok');

  evidence.step('ORACLE (server disk): exactly the 3 unchanged files were compared, all identical; the changed one is not a failure');
  expect(diskSet(world, login, id).attrs.RESTORE_TEST_RESULT).toBe('OK 3/3');
  evidence.step('ORACLE (agent disk): the restore test\'s temporary folder is gone');
  expect(leftTemp(ag)).toEqual([]);

  evidence.step('the site: Restore tests — Passed · 3/3, green; the KPI counts it');
  const row1 = await restoreTestRow(page);
  await expect(row1.locator('.pill')).toHaveText('Passed · 3/3');
  await expect(row1.locator('.pill')).toHaveClass(/\bok\b/);
  await expect(kpi(page, 'Passed')).toHaveText('1');
  await expect(kpi(page, 'Failed')).toHaveText('0');

  evidence.step('ORACLE: the backed-up point restores identical to the files at backup time (SHA-256)');
  const r1 = path.join(world.dir, 'restore-1');
  const rr1 = ag.restore(id, r1); expect(rr1.code, rr1.out).toBe(0);
  expect(compare(atBackup, manifest(restoredPath(r1, src)))).toEqual([]);

  // ------------------------------------------------------------------ a month later
  evidence.step('+31 days on the computer: the nightly backup takes the changed file');
  const month = ['faketime', '-f', '+31d'];
  const b2 = agentRun(ag, ['backup', '--set', id], month); expect(b2.out, b2.out).toMatch(/^BS_STOP_SUCCESS .*upd=1 /m);
  const atBackup2 = manifest(src);

  evidence.step('FAULT: one stored object on the server\'s disk gets one byte flipped');
  const store = path.join(world.usersDir, login, 'files', id);
  const objs = find(path.join(store, 'Current'), /\.000$/).sort((a, b) => fs.statSync(a).size - fs.statSync(b).size);
  expect(objs.length, 'objects in Current').toBeGreaterThanOrEqual(4);
  const victim = objs[objs.length - 1];   // the largest: the photo
  const good = sha(victim);
  const bytes = fs.readFileSync(victim); bytes[Math.floor(bytes.length / 2)] ^= 0xff; fs.writeFileSync(victim, bytes);
  expect(sha(victim)).not.toBe(good);
  evidence.step('PROOF of the fault: a restore now fails for exactly one file');
  const r0 = path.join(world.dir, 'restore-damaged');
  const rr0 = ag.restore(id, r0); expect(rr0.code, rr0.out).not.toBe(0);
  const diff0 = compare(atBackup2, manifest(restoredPath(r0, src)));
  expect(diff0.length, diff0.join('\n')).toBe(1);

  evidence.step('the agent runs as the service a month later: the monthly restore test is due again and runs by itself');
  await serviceUntilTests(world, ag, login, 2, month);
  const t2 = await restoreTests(world, login);
  expect(t2.length).toBe(2);
  expect(t2[0].status, 'the second restore test on the server: ' + JSON.stringify(t2[0])).toBe('bad');
  evidence.step('ORACLE (server disk): 4 compared, 3 identical, the damaged one failed');
  expect(diskSet(world, login, id).attrs.RESTORE_TEST_RESULT).toBe('FAILED 3/4');
  expect(leftTemp(ag), 'the restore test\'s temporary folder after a failed test').toEqual([]);

  evidence.step('the site: Restore tests — Failed · 3/4, in red');
  const row2 = await restoreTestRow(page);
  const pill = row2.locator('.pill');
  await expect(pill).toHaveText('Failed · 3/4');
  await expect(pill).toHaveClass(/\bbad\b/);
  await expect(kpi(page, 'Failed')).toHaveText('1');
  await expect(kpi(page, 'Passed')).toHaveText('0');
  const [r, g, b] = (await pill.evaluate((e) => getComputedStyle(e).color)).match(/\d+/g)!.map(Number);
  expect(r > 150 && g < 100 && b < 100, 'the pill is red: rgb(' + r + ',' + g + ',' + b + ')').toBe(true);
});
