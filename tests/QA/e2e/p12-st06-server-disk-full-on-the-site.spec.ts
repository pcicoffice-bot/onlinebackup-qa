// P12 (ST-06) — the server's storage disk is full, seen the way the provider and the customer see it: a set with a good
// backup; the server's disk (a 64 MB disk of the test) is filled until ~3 MB are left; "Back up now" on the admin site;
// the agent runs as the Windows service does.
// Expected (ST-06): the run fails — never Succeeded — with the clear reason in the run's log on the site ("disk is
// full"); exactly one alert mail to the IT company; the space of the failed run is given back; the earlier point still
// restores identical during the fault; once space is freed, "Back up now" completes and the newest point restores
// identical (SHA-256). N5 covers the same fault from the computer's side only (no site, no mail).
// Oracles outside the page: df of the server's disk (the fault), the agent's own result lines, the bytes of the set on the
// server's disk, the mails received by the test's mail server, SHA-256 of every restored file.
import { test, expect, signIn } from '../lib/fixtures';
import { World, goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import { openSet, backUpNow, waitForTask } from '../lib/ui';
import { bigFiles, unmount } from '../lib/fault';
import { tmpfs, fill, df, points } from '../lib/nfault';
import { SmtpSink, mailSettings, serviceResults, bytesOn } from './p-helpers';
import { until } from './q-helpers';
import { execSync } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';

const SET = 'Disk full';
const SUBJECT = '✗ The backup server\'s disk is full';

test('P12 ST-06 server disk full under Back up now from the site: Failed with the reason on the site, one alert mail, space given back, point 1 restorable, after space the next run completes and restores identical', async ({ page, evidence }, info) => {
  test.setTimeout(10 * 60 * 1000);
  const world = new World();
  world.usersDir = path.join(world.dir, 'small-disk');
  if (!tmpfs(world.usersDir, 64)) test.skip(true, 'NOT TESTED: this machine does not allow mounting a small disk (neither root nor passwordless sudo)');
  const sink = await new SmtpSink().start();
  try {
    await world.start();
    evidence.step('the administrator sets the mail server and the IT company\'s contact on the site');
    await signIn(page, world);
    await mailSettings(page, sink.port, 'it-admin@example.invalid');

    const login = 'qa-p12';
    world.addCustomer(login);
    const ag = world.agent(login, 'P12-PC'); ag.register();
    const src = path.join(world.dir, 'data'); const v1 = goldenDataset(src);
    const far = new Date(Date.now() + 6 * 3600000);
    const id = ag.addSet(SET, [src], ['--hour', String(far.getHours()), '--minute', '0']);
    const b1 = ag.backup(id); expect(b1.out, b1.out).toMatch(/^BS_STOP_SUCCESS /m);
    const p1 = points(ag, id)[0];
    const store = path.join(world.usersDir, login, 'files', id);
    const storeBefore = bytesOn(store);

    evidence.step('FAULT: the server\'s disk filled until ~3 MB are left');
    const filler = fill(world.usersDir);
    fs.truncateSync(filler, Math.max(0, fs.statSync(filler).size - 3 * 1024 * 1024));
    const avail = Number(execSync('df -B1 --output=avail "' + world.usersDir + '" | tail -1').toString().trim());
    evidence.step('df: ' + df(world.usersDir));
    expect(avail, 'PROOF: less than 4 MB free on the server\'s disk').toBeLessThan(4 * 1024 * 1024);
    bigFiles(src, 1, 10);
    fs.writeFileSync(path.join(src, 'Documents/letter.txt'), 'changed before backup 2\n');

    evidence.step('Back up now on the site; the agent service runs it');
    await openSet(page, login, SET);
    await backUpNow(page);
    ag.startService();
    const r2 = await until('the service\'s run on the full disk', () => serviceResults(ag.log, SET)[0], 180000, 2000);
    evidence.step('the service: ' + r2.result);
    expect(r2.result, 'never a success on a full disk').not.toMatch(/^BS_STOP_SUCCESS/);

    evidence.step('the site: the run is Failed (never Succeeded) and its log says the disk is full');
    const row = await waitForTask(page, SET, 'Failed', 2);
    expect(row).not.toContain('Succeeded');
    await page.getByRole('row').filter({ hasText: SET }).filter({ hasText: 'Failed' }).first().click();
    await expect(page.locator('.logbox').first()).toContainText(/disk is full/i);
    const runs = (await world.runs(login)).filter((r) => r.kind === 'Backup');
    expect(runs.map((r) => r.status), 'the server\'s records').toEqual(['bad', 'ok']);
    expect(await world.live()).not.toContain(id);

    evidence.step('ORACLE (mail server): exactly one alert to the IT company');
    await until('the alert mail', () => sink.mails.some((m) => m.subject === SUBJECT), 60000, 1000);
    await new Promise((r) => setTimeout(r, 3000));
    const alerts = sink.mails.filter((m) => m.subject === SUBJECT);
    expect(alerts.length, 'mails: ' + JSON.stringify(sink.mails.map((m) => m.subject))).toBe(1);
    expect(alerts[0].to).toEqual(['it-admin@example.invalid']);

    evidence.step('ORACLE (server disk): the failed run\'s space is given back (the set holds what it held before)');
    const storeAfter = bytesOn(store);
    evidence.step('set on the server: ' + storeBefore + ' bytes before, ' + storeAfter + ' after the failed run');
    expect(storeAfter - storeBefore, 'bytes the failed run left in the set').toBeLessThan(512 * 1024);
    expect(points(ag, id), 'no point made by the failed run').toEqual([p1]);

    evidence.step('DURING the fault: point 1 restores identical');
    const tf = path.join(world.dir, 'restore-during'); const rf = ag.restore(id, tf, ['--point', p1]);
    expect(rf.code, rf.out).toBe(0);
    expect(compare(v1, manifest(restoredPath(tf, src)))).toEqual([]);

    evidence.step('RECOVERY: space freed; Back up now on the site — the next run completes');
    fs.rmSync(filler);
    const v3 = manifest(src);
    await openSet(page, login, SET);
    await backUpNow(page);
    const r3 = await until('the service\'s next run', () => serviceResults(ag.log, SET)[1], 180000, 2000);
    expect(r3.result).toBe('BS_STOP_SUCCESS');
    await waitForTask(page, SET, 'Succeeded', 2);
    ag.stopService();
    const t3 = path.join(world.dir, 'restore-3'); const rr3 = ag.restore(id, t3); expect(rr3.code, rr3.out).toBe(0);
    expect(compare(v3, manifest(restoredPath(t3, src)))).toEqual([]);
  } finally {
    sink.stop();
    await world.stop();
    unmount(world.usersDir);
    if (info.status === info.expectedStatus && !process.env.QA_KEEP) fs.rmSync(world.dir, { recursive: true, force: true });
  }
});
