// J5 — The customer's computer dies in the middle of a backup (the backup program is killed).
// Expected like any backup product a person trusts: the site does not keep showing it as running, the set is not
// locked, the failed run is in the history as failed, the next backup works, and its content restores identical.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import { openSet, tab, saveAndExit, backUpNow, waitForTask } from '../lib/ui';
import * as path from 'path';

test('J5 kill the agent during a backup → no ghost "running", the failure is shown, the next backup and its restore are right', async ({ admin: page, world, evidence }) => {
  world.addCustomer('qa-delta');
  const ag = world.agent('qa-delta', 'DELTA-PC1'); ag.register();
  const src = path.join(world.dir, 'data'); const data = goldenDataset(src);
  ag.addSet('Server files', [src]);

  evidence.step('the technician limits the upload to 200 KB/s (so the backup takes a while)');
  await openSet(page, 'qa-delta', 'Server files');
  await tab(page, 'Resources');
  await page.locator('.form .fr').filter({ hasText: 'Upload limit' }).locator('input').first().fill('200');
  await saveAndExit(page);

  evidence.step('the computer runs its service; Back up now');
  ag.startService();
  await openSet(page, 'qa-delta', 'Server files');
  await backUpNow(page);
  evidence.step('the site shows the backup as running');
  await page.locator('.rail button[data-k="live"]').click();
  await expect(page.getByRole('row').filter({ hasText: 'Server files' })).toBeVisible({ timeout: 120000 });

  evidence.step('KILL the backup program on the computer (power cut)');
  ag.killService();
  evidence.step('the computer starts again: the service comes back');
  ag.startService();

  evidence.step('the site no longer shows it as running (no ghost)');
  await expect(async () => {
    await page.locator('.rail button[data-k="live"]').click();
    await page.waitForLoadState('networkidle');
    await expect(page.getByRole('row').filter({ hasText: 'Server files' })).toHaveCount(0);
  }).toPass({ timeout: 120000, intervals: [3000] });
  evidence.step('the history shows the interrupted run as failed, with a reason');
  await waitForTask(page, 'Server files', 'Failed', 3);

  evidence.step('the technician lifts the limit and backs up again: it runs (the set is not locked) and succeeds');
  await openSet(page, 'qa-delta', 'Server files');
  await tab(page, 'Resources');
  await page.locator('.form .fr').filter({ hasText: 'Upload limit' }).locator('input').first().fill('0');
  await saveAndExit(page);
  await openSet(page, 'qa-delta', 'Server files');
  await backUpNow(page);
  await waitForTask(page, 'Server files', 'Succeeded');

  evidence.step('ORACLE: the new backup restores every file identical');
  ag.stopService();
  const target = path.join(world.dir, 'restore');
  const r = ag.restore(ag.sets()[0].id, target);
  expect(r.code, r.out).toBe(0);
  expect(compare(data, manifest(restoredPath(target, src)))).toEqual([]);
});
