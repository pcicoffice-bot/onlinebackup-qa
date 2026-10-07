// P10 (AU-02) — a customer's computer signs in, and a computer disconnected on the admin site is refused at once:
//  - the client program's sign-in with a wrong password is refused and registers nothing; the right one registers
//    the computer (its device token), which backs up;
//  - the technician presses "Disconnect" on the customer's Computers tab in the browser: from that moment the computer's
//    token is refused — the backup program at once, the running agent service at its next round — and no run reaches the
//    server; the backups made before are kept;
//  - the computer signed in again with the customer's password backs up again, and everything restores identical.
// Oracles outside the page: the agent program's exit codes and messages, the server's device list on disk
// (devices.xml: REVOKED), the server's run records, SHA-256 of every restored file.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import { openCustomer } from '../lib/ui';
import { confirmYes, find, until } from './q-helpers';
import * as fs from 'fs';
import * as path from 'path';

test('P10 AU-02 a wrong customer password registers nothing; a computer disconnected on the site is refused at once (program and service); signed in again it backs up and restores identical', async ({ admin: page, world, evidence }) => {
  const login = 'qa-p10', PC = 'P10-PC';
  world.addCustomer(login);
  const ag = world.agent(login, PC);
  const devices = () => { const f = find(path.join(world.usersDir, login), /(^|\/)devices\.xml$/)[0]; return f ? fs.readFileSync(f, 'utf8') : ''; };

  evidence.step('the client program signs in with a wrong password: refused, nothing registered');
  const bad = ag.cli(['register', '--server', world.url + '/', '--login', login, '--password', 'Not-The-Password-1', '--computer', PC], true);
  expect(bad.code, bad.out).not.toBe(0);
  expect(bad.out).toMatch(/password|login|sign/i);
  expect(devices(), 'no device on the server').not.toMatch(/<DEVICE /);
  expect(ag.cli(['sets'], true).code, 'the program has no sign-in to use').not.toBe(0);

  evidence.step('the right password: the computer is registered and backs up with its device token');
  ag.register();
  expect((devices().match(/<DEVICE /g) || []).length).toBe(1);
  const src = path.join(world.dir, 'data'); const v1 = goldenDataset(src);
  const far = new Date(Date.now() + 6 * 3600000);
  const id = ag.addSet('Office', [src], ['--hour', String(far.getHours()), '--minute', '0']);
  const b1 = ag.backup(id); expect(b1.out, b1.out).toMatch(/^BS_STOP_SUCCESS /m);
  ag.startService();

  evidence.step('the technician: the customer\'s Computers tab — Disconnect ' + PC);
  await openCustomer(page, login);
  await page.locator('.tabs[role=tablist] button[data-k="pcs"]').click();
  const pcRow = page.getByRole('row').filter({ hasText: PC });
  await expect(pcRow.locator('.pill')).toHaveText('Connected');
  await pcRow.getByRole('button', { name: 'Disconnect' }).click();
  await confirmYes(page);
  await expect(page.locator('#toast').filter({ hasText: 'Disconnected' })).toBeVisible();
  const at = Date.now();
  await expect(page.getByRole('row').filter({ hasText: PC }).locator('.pill')).toHaveText('Disconnected');
  expect(devices(), 'ORACLE (server disk): the device token is revoked').toMatch(/<DEVICE [^>]*REVOKED="Y"/);

  evidence.step('at once: the backup program is refused, with the reason; no run reaches the server');
  const runsBefore = (await world.runs(login)).filter((r) => r.kind === 'Backup').length;
  fs.writeFileSync(path.join(src, 'Documents/after-disconnect.txt'), 'must not reach the server through a revoked token');
  const b2 = ag.backup(id);
  evidence.step('backup after the disconnect (' + ((Date.now() - at) / 1000).toFixed(1) + ' s later): exit ' + b2.code + '\n' + b2.out.trim());
  expect(b2.code, b2.out).not.toBe(0);
  expect(b2.out).not.toMatch(/^BS_STOP_SUCCESS/m);
  expect(b2.out).toMatch(/revoked|unknown/i);
  expect(ag.cli(['sets'], true).code, 'the program cannot even read its sets').not.toBe(0);

  evidence.step('the running service is refused at its next round (it reads the settings every minute)');
  const mark = fs.readFileSync(ag.log, 'utf8').length;
  await until('the service is refused', () => /revoked/i.test(fs.readFileSync(ag.log, 'utf8').slice(mark)) || undefined, 90000, 2000);
  ag.stopService();
  const runsAfter = (await world.runs(login)).filter((r) => r.kind === 'Backup');
  expect(runsAfter.length, 'no backup run of the disconnected computer: ' + JSON.stringify(runsAfter)).toBe(runsBefore);

  evidence.step('the earlier backup is kept: it restores identical');
  const t1 = path.join(world.dir, 'restore-kept');
  // the computer cannot restore with a revoked token: signed in again first (as the customer does)
  ag.register();
  expect((devices().match(/<DEVICE (?![^>]*REVOKED="Y")/g) || []).length, 'one valid device after signing in again').toBe(1);
  const pts = ag.cli(['points', '--set', id]).out.trim().split('\n').filter((l) => /^\d{4}-/.test(l)).map((l) => l.split(/\s/)[0]);
  expect(pts.length, 'the points of the set').toBe(1);
  const rr1 = ag.restore(id, t1); expect(rr1.code, rr1.out).toBe(0);
  expect(compare(v1, manifest(restoredPath(t1, src)))).toEqual([]);

  evidence.step('signed in again: the next backup completes and restores identical');
  const v2 = manifest(src);
  const b3 = ag.backup(id); expect(b3.out, b3.out).toMatch(/^BS_STOP_SUCCESS /m);
  await openCustomer(page, login);
  await page.locator('.tabs[role=tablist] button[data-k="pcs"]').click();
  await expect(page.getByRole('row').filter({ hasText: PC }).locator('.pill')).toHaveText('Connected');
  const t2 = path.join(world.dir, 'restore-again');
  const rr2 = ag.restore(id, t2); expect(rr2.code, rr2.out).toBe(0);
  expect(compare(v2, manifest(restoredPath(t2, src)))).toEqual([]);
});
