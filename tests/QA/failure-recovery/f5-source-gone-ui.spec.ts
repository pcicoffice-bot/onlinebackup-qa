// F5 — The folder of a set disappears (a disconnected disk). The site must not show a quiet date: the run is Failed and
// the set shows "Last run failed". When the folder comes back, the next backup is fine and restores identical.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import { openSet, backUpNow, waitForTask, openCustomer } from '../lib/ui';
import * as fs from 'fs';
import * as path from 'path';

test('F5 source disappears → the site shows the failure truthfully → source back → backup restores identical', async ({ admin: page, world, evidence }) => {
  world.addCustomer('qa-f5');
  const ag = world.agent('qa-f5', 'F5-PC'); ag.register();
  const src = path.join(world.dir, 'data'); const data = goldenDataset(src);
  const id = ag.addSet('Shared disk', [src]);
  expect(ag.backup(id).out).toMatch(/^BS_STOP_SUCCESS /m);
  evidence.step('the disk is disconnected');
  fs.renameSync(src, src + '-away');
  ag.startService();
  await openSet(page, 'qa-f5', 'Shared disk');
  await backUpNow(page);
  evidence.step('the tasks page: Failed');
  await waitForTask(page, 'Shared disk', 'Failed');
  evidence.step('the customer\'s set list: a red "Last run failed"');
  await openCustomer(page, 'qa-f5');
  const setRow = page.getByRole('row').filter({ hasText: 'Shared disk' }).first();
  await expect(setRow.locator('.lastrun .pill.bad')).toContainText('Last run failed');
  evidence.step('…and the restore test does not claim the backup is broken (nothing to compare while the disk is away)');
  await expect(setRow.getByText('Failed', { exact: true })).toHaveCount(0);
  evidence.step('the disk is back; Back up now');
  fs.renameSync(src + '-away', src);
  await openSet(page, 'qa-f5', 'Shared disk');
  await backUpNow(page);
  await expect(async () => {
    await openCustomer(page, 'qa-f5');
    await expect(page.getByRole('row').filter({ hasText: 'Shared disk' }).first().locator('.lastrun .pill.bad')).toHaveCount(0);
  }).toPass({ timeout: 240000, intervals: [5000] });
  ag.stopService();
  const target = path.join(world.dir, 'restore');
  const r = ag.restore(id, target); expect(r.code, r.out).toBe(0);
  expect(compare(data, manifest(restoredPath(target, src)))).toEqual([]);
});
