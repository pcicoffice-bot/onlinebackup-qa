// J7 — The schedule, as the Windows service runs it: a set due at a minute in the near future runs by itself, once —
// not again a minute later, not again after the service restarts — and what it backed up restores identical.
// Oracle: the server's run history counted directly (exactly one run), and SHA-256 of the restore.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, compare, manifest, restoredPath } from '../lib/world';
import * as path from 'path';

test('J7 a scheduled backup runs on time, exactly once, also across a restart of the service, and restores identical', async ({ world, evidence }) => {
  test.setTimeout(10 * 60 * 1000);
  world.addCustomer('qa-sched');
  const ag = world.agent('qa-sched', 'SCHED-PC1'); ag.register();
  const src = path.join(world.dir, 'data'); const v1 = goldenDataset(src);
  const due = new Date(Date.now() + 2 * 60 * 1000);   // the agent works in local time, as the computer does
  const id = ag.addSet('Nightly', [src], ['--hour', String(due.getHours()), '--minute', String(due.getMinutes())]);
  evidence.step('the service runs; the set is due at ' + due.toTimeString().slice(0, 5));
  ag.startService();
  const until = Date.now() + 5 * 60 * 1000;
  let runs: any[] = [];
  while (Date.now() < until) {
    runs = (await world.runs('qa-sched')).filter((r) => (r.setName === 'Nightly' || r.set === id) && r.kind === 'Backup');
    if (runs.length > 0) break;
    await new Promise((r) => setTimeout(r, 5000));
  }
  expect(runs.length, 'the scheduled backup did not run').toBe(1);
  expect(Date.now()).toBeGreaterThanOrEqual(due.getTime() - 1000);   // not before its time
  evidence.step('the service restarts; two more minutes pass');
  ag.stopService(); ag.startService();
  await new Promise((r) => setTimeout(r, 150 * 1000));
  ag.stopService();
  runs = (await world.runs('qa-sched')).filter((r) => (r.setName === 'Nightly' || r.set === id) && r.kind === 'Backup');
  expect(runs.length, 'the scheduled backup ran more than once: ' + JSON.stringify(runs)).toBe(1);
  expect(runs[0].status).toBe('ok');

  evidence.step('ORACLE: the scheduled backup restores identical');
  const target = path.join(world.dir, 'restore');
  const r = ag.restore(id, target);
  expect(r.code, r.out).toBe(0);
  expect(compare(v1, manifest(restoredPath(target, src)))).toEqual([]);
});
