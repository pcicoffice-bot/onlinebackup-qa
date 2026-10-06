// J3 — "Back up now" on the site makes a real backup on the customer's computer.
// Oracle: the site shows a successful run AND the backup really holds the files — restored to an empty folder, every
// file has the same SHA-256 as the original (the product's own "success" is not enough).
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import { openSet, backUpNow, waitForTask } from '../lib/ui';
import * as path from 'path';

test('J3 Back up now from the site → a real backup whose content restores identical (SHA-256)', async ({ admin: page, world, evidence }) => {
  world.addCustomer('qa-beta');
  const ag = world.agent('qa-beta', 'BETA-PC1'); ag.register();
  const src = path.join(world.dir, 'data'); const before = goldenDataset(src);
  ag.addSet('Office files', [src]);
  evidence.step('the computer runs the backup service');
  ag.startService();

  evidence.step('open the set, press Back up now');
  await openSet(page, 'qa-beta', 'Office files');
  await backUpNow(page);
  evidence.step('the tasks page shows the run as succeeded');
  const row = await waitForTask(page, 'Office files', 'Succeeded');
  expect(row).toContain(String(before.size));                    // new files = the files of the dataset

  evidence.step('the set list shows the last backup without a problem');
  await page.locator('.rail button[data-k="cust"]').click();
  await page.getByRole('row').filter({ hasText: 'qa-beta' }).first().click();
  const setRow = page.getByRole('row').filter({ hasText: 'Office files' }).first();
  await expect(setRow).not.toContainText('—');
  await expect(setRow.locator('.pill.bad')).toHaveCount(0);

  evidence.step('ORACLE: restore the backup into an empty folder and compare every file');
  ag.stopService();
  const target = path.join(world.dir, 'restore');
  const r = ag.restore(ag.sets()[0].id, target);
  expect(r.code, r.out).toBe(0);
  expect(compare(before, manifest(restoredPath(target, src)))).toEqual([]);
});
