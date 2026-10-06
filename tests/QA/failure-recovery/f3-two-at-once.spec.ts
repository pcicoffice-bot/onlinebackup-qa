// F3 — Two backups of the same set at the same time (the schedule and "Back up now", or two programs).
// Expected: one runs, the other is refused clearly (not a crash, not two half backups); the result restores identical.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import * as path from 'path';

test('F3 two backups of one set at once → one succeeds, the other is refused cleanly, the backup restores identical', async ({ world, evidence }) => {
  world.addCustomer('qa-f3');
  const ag = world.agent('qa-f3', 'F3-PC'); ag.register();
  const src = path.join(world.dir, 'data'); const data = goldenDataset(src);
  const id = ag.addSet('Files', [src]);
  await world.setBandwidth('qa-f3', id, 2000);
  const a = ag.backupProcess(id);
  await expect.poll(async () => (await world.live()).includes(id), { timeout: 90000 }).toBe(true);
  evidence.step('a second backup of the same set while the first runs');
  const second = ag.backup(id);
  expect(second.code, 'the second run is refused').not.toBe(0);
  expect(second.out).toMatch(/still running|already running|BUSY/i);
  const code = await new Promise<number>((r) => a.on('exit', (c) => r(c ?? -1)));
  expect(code, 'the first run completes').toBe(0);
  const target = path.join(world.dir, 'restore');
  const r = ag.restore(id, target); expect(r.code, r.out).toBe(0);
  expect(compare(data, manifest(restoredPath(target, src)))).toEqual([]);
});
