// F12 — Two computers of one customer carry the SAME computer name (a cloned Windows image that was registered again,
// or two offices that both call their PC "RECEPTION"). Each has its own backup set, scheduled for the same time.
// Expected: each computer backs up its own set on schedule; neither damages or blocks the other's backups; no failed
// run appears; each set restores identical (SHA-256) to its own computer's files.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import * as fs from 'fs';
import * as path from 'path';

test('F12 two computers with the same name → each set backed up on schedule by its own computer, nothing blocked or corrupted → both restore identical', async ({ world, evidence }) => {
  world.addCustomer('qa-f12');
  const a = world.agent('qa-f12', 'RECEPTION');
  const b = world.agent('qa-f12', 'RECEPTION');
  b.home += '-second'; b.log = b.log.replace(/\.log$/, '-second.log');      // another computer: its own program folder
  evidence.step('two computers register with the same name');
  a.register(); b.register();
  const srcA = path.join(world.dir, 'data-a'); const dataA = goldenDataset(srcA, 1);
  const srcB = path.join(world.dir, 'data-b'); const dataB = goldenDataset(srcB, 2);
  // the schedule slot one minute ago: due now (a run within 15 minutes of its slot starts at once)
  const slot = new Date(Date.now() - 60000);
  const at = (d: Date) => ['--hour', String(d.getHours()), '--minute', String(d.getMinutes())];
  const idA = a.addSet('Reception A', [srcA], at(slot));
  const idB = b.addSet('Reception B', [srcB], at(slot));
  evidence.step('both computers run their backup service');
  a.startService(); b.startService();

  const ok = async (id: string) => (await world.runs('qa-f12')).filter((r) => r.set === id && r.kind === 'Backup' && r.status === 'ok').length;
  const deadline = Date.now() + 240000;
  while (Date.now() < deadline && ((await ok(idA)) === 0 || (await ok(idB)) === 0)) await new Promise((r) => setTimeout(r, 5000));
  a.stopService(); b.stopService();
  const runs = await world.runs('qa-f12');
  evidence.step('runs on the server: ' + JSON.stringify(runs.map((r) => ({ set: r.setName, kind: r.kind, result: r.result }))));
  const tail = (f: string) => (fs.existsSync(f) ? fs.readFileSync(f, 'utf8').split('\n').filter((l) => /waiting|error|key|Reception/i.test(l)).slice(-8).join('\n') : '');
  expect(await ok(idA), 'set A was backed up on schedule by its computer. Service log of computer A:\n' + tail(a.log)).toBeGreaterThan(0);
  expect(await ok(idB), 'set B was backed up on schedule by its computer (the other computer with the same name must not block it). Service log of computer B:\n' + tail(b.log)).toBeGreaterThan(0);
  expect(runs.filter((r) => r.kind === 'Backup' && r.status === 'bad'), 'no failed backup run').toEqual([]);

  evidence.step('ORACLE: each set restores identical to its own computer\'s files');
  const tA = path.join(world.dir, 'restore-a'); const rA = a.restore(idA, tA); expect(rA.code, rA.out).toBe(0);
  expect(compare(dataA, manifest(restoredPath(tA, srcA)))).toEqual([]);
  const tB = path.join(world.dir, 'restore-b'); const rB = b.restore(idB, tB); expect(rB.code, rB.out).toBe(0);
  expect(compare(dataB, manifest(restoredPath(tB, srcB)))).toEqual([]);
});
