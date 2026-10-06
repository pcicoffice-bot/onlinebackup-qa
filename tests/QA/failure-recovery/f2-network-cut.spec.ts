// F2 — The internet line of the customer is cut in the middle of a backup, then comes back.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath, Net } from '../lib/world';
import * as path from 'path';

test('F2 network cut mid-backup → failure, not success → line back → next backup restores identical, no ghost', async ({ world, evidence }) => {
  const line = await new Net(world).start();
  world.addCustomer('qa-f2');
  const ag = world.agent('qa-f2', 'F2-PC'); ag.register(line.url);
  const src = path.join(world.dir, 'data'); const data = goldenDataset(src);
  const id = ag.addSet('Files', [src]);
  await world.setBandwidth('qa-f2', id, 300);
  const p = ag.backupProcess(id);
  await expect.poll(async () => (await world.live()).includes(id), { timeout: 90000 }).toBe(true);
  evidence.step('CUT the line');
  line.cut();
  const code = await new Promise<number>((r) => p.on('exit', (c) => r(c ?? -1)));
  expect(code, 'not reported as successful').not.toBe(0);
  evidence.step('the line comes back');
  line.restore();
  await world.setBandwidth('qa-f2', id, 0);
  const b = ag.backup(id);
  expect(b.out, b.out).toMatch(/^BS_STOP_SUCCESS /m);
  expect(await world.live()).not.toContain(id);
  expect((await world.runs('qa-f2')).filter((r) => r.status === 'bad').length, 'the cut run is in the history as failed').toBeGreaterThan(0);
  const target = path.join(world.dir, 'restore');
  const r = ag.restore(id, target); expect(r.code, r.out).toBe(0);
  expect(compare(data, manifest(restoredPath(target, src)))).toEqual([]);
  line.stop();
});
