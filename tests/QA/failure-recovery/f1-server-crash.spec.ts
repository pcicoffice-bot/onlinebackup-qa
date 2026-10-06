// F1 — The backup SERVER crashes (kill -9) in the middle of a backup and comes back.
// Expected: the computer's run ends as a failure (never "success"); after the restart nothing stays "running",
// the set is not locked, the next backup succeeds, and its content restores identical.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import * as path from 'path';

test('F1 server killed mid-backup → the run fails cleanly → server back → no ghost, not locked, next backup restores identical', async ({ world, evidence }) => {
  world.addCustomer('qa-f1');
  const ag = world.agent('qa-f1', 'F1-PC'); ag.register();
  const src = path.join(world.dir, 'data'); const data = goldenDataset(src);
  const id = ag.addSet('Files', [src]);
  await world.setBandwidth('qa-f1', id, 300);

  evidence.step('a slow backup as its own process');
  const p = ag.backupProcess(id);
  await expect.poll(async () => (await world.live()).includes(id), { timeout: 90000 }).toBe(true);
  evidence.step('KILL the server');
  world.killServer();
  const code = await new Promise<number>((r) => p.on('exit', (c) => r(c ?? -1)));
  expect(code, 'the computer must not report this run as successful').not.toBe(0);

  evidence.step('the server starts again');
  await world.startServer();
  evidence.step('nothing is shown as running');
  expect(await world.live()).not.toContain(id);
  evidence.step('the next backup is not refused (the set is not locked) and succeeds');
  await world.setBandwidth('qa-f1', id, 0);
  const b = ag.backup(id);
  expect(b.out, b.out).toMatch(/^BS_STOP_SUCCESS /m);
  evidence.step('the interrupted run is in the history as failed');
  expect((await world.runs('qa-f1')).some((r) => r.status === 'bad')).toBe(true);
  evidence.step('ORACLE: restore → identical');
  const target = path.join(world.dir, 'restore');
  const r = ag.restore(id, target); expect(r.code, r.out).toBe(0);
  expect(compare(data, manifest(restoredPath(target, src)))).toEqual([]);
});
