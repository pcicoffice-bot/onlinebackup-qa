// Q1 (ST-09) — the recycle bin, end to end: a set and then a whole customer are deleted from the admin site, put back
// from the recycle bin on the Storage page, and the data is intact; erasing after 14 days really removes the bytes.
// What the product claims (src/Server/Recycle.cs DEL-010): "Nothing is erased at once: the folder moves to the recycle
// bin of its drive and is erased after 14 days, restorable until then." (one administrator → done at once).
// Oracles (outside the product): the server's folders on disk (what is in the store, the bin, or gone), the agent
// program's own answers (sets / restore), and SHA-256 of every restored file against the manifest made at the source.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import { openCustomer, openSet, tab } from '../lib/ui';
import { runId, tree, confirmYes, rail } from './q-helpers';
import * as fs from 'fs';
import * as path from 'path';

test('Q1 ST-09 a set and a customer deleted on the site come back from the recycle bin intact; after 14 days the bin is really erased', async ({ admin: page, world, evidence }) => {
  world.addCustomer('qa-rb');
  const ag = world.agent('qa-rb', 'RB-PC1'); ag.register();
  const src = path.join(world.dir, 'data'); const before = goldenDataset(src);
  const id = ag.addSet('Office files', [src]);
  const b = ag.backup(id); expect(b.out, b.out).toMatch(/^BS_STOP_SUCCESS /m);
  const userDir = path.join(world.usersDir, 'qa-rb'), store = path.join(userDir, 'files', id);
  const stored = tree(store);
  expect(stored.size, 'objects on the server after the backup').toBeGreaterThan(10);
  const storedSha = manifest(store);

  // ---------------------------------------------------------------- 1. delete the set on the site
  evidence.step('open the set, Maintenance, Delete the set, Yes');
  await openSet(page, 'qa-rb', 'Office files');
  await tab(page, 'Maintenance');
  await page.getByRole('button', { name: '🗑 Delete the set' }).click();
  await confirmYes(page);
  await expect(page.locator('#toast').filter({ hasText: 'Moved to the recycle bin' })).toBeVisible();

  evidence.step('ORACLE (disk): the store folder is gone, its bytes are in the customer\'s recycle bin');
  expect(fs.existsSync(store), 'the set\'s store folder after the delete').toBe(false);
  const bins = fs.readdirSync(path.join(userDir, '_recycle')).filter((d) => d.startsWith('set-' + id + '-'));
  expect(bins.length).toBe(1);
  expect(compare(storedSha, manifest(path.join(userDir, '_recycle', bins[0], 'files')))).toEqual([]);
  evidence.step('ORACLE (agent): the computer no longer has the set, a restore of it is refused');
  expect(ag.sets().map((s) => s.id)).not.toContain(id);
  const refused = ag.restore(id, path.join(world.dir, 'refused'));
  expect(refused.code, refused.out).not.toBe(0);

  evidence.step('Storage on the server → Recycle bin: the set is listed; Restore');
  await rail(page, 'storage');
  const row = page.getByRole('row').filter({ hasText: 'Set Office files of qa-rb' });
  await expect(row).toHaveCount(1);
  await row.getByRole('button', { name: 'Restore' }).click();
  await expect(page.locator('#toast').filter({ hasText: 'Restored' })).toBeVisible();
  await expect(page.getByRole('row').filter({ hasText: 'Set Office files of qa-rb' })).toHaveCount(0);

  evidence.step('ORACLE: the store is back byte for byte, the computer sees the set, the restore is identical (SHA-256)');
  expect(compare(storedSha, manifest(store))).toEqual([]);
  expect(ag.sets().map((s) => s.id)).toContain(id);
  const t1 = path.join(world.dir, 'restore-1');
  const r1 = ag.restore(id, t1); expect(r1.code, r1.out).toBe(0);
  expect(compare(before, manifest(restoredPath(t1, src)))).toEqual([]);
  evidence.step('the restored set goes on: a change is backed up and restores identical');
  fs.writeFileSync(path.join(src, 'Documents/after-recycle.txt'), 'written after the set came back');
  const b2 = ag.backup(id); expect(b2.out, b2.out).toMatch(/^BS_STOP_SUCCESS .*new=1 /m);
  const after = manifest(src);
  const t2 = path.join(world.dir, 'restore-2');
  const r2 = ag.restore(id, t2); expect(r2.code, r2.out).toBe(0);
  expect(compare(after, manifest(restoredPath(t2, src)))).toEqual([]);

  // ---------------------------------------------------------------- 2. delete the whole customer on the site
  evidence.step('open the customer, Delete customer, Yes');
  const userSha = manifest(path.join(userDir, 'files'));
  await rail(page, 'dash');
  await openCustomer(page, 'qa-rb');
  await page.getByRole('button', { name: '🗑 Delete customer' }).click();
  await confirmYes(page);
  await expect(page.locator('#toast').filter({ hasText: 'Moved to the recycle bin' })).toBeVisible();
  evidence.step('ORACLE: the customer\'s folder is gone, the computer cannot sign in any more');
  expect(fs.existsSync(userDir)).toBe(false);
  const gone = ag.cli(['sets'], true); expect(gone.code, gone.out).not.toBe(0);
  await rail(page, 'cust');
  await expect(page.getByRole('row').filter({ hasText: 'qa-rb' })).toHaveCount(0);

  evidence.step('Storage → Recycle bin: the customer is listed; Restore');
  await rail(page, 'storage');
  const crow = page.getByRole('row').filter({ hasText: 'Customer qa-rb' });
  await expect(crow).toHaveCount(1);
  await crow.getByRole('button', { name: 'Restore' }).click();
  await expect(page.locator('#toast').filter({ hasText: 'Restored' })).toBeVisible();
  evidence.step('ORACLE: the customer\'s backups are back byte for byte, the computer signs in, the restore is identical');
  expect(compare(userSha, manifest(path.join(userDir, 'files')))).toEqual([]);
  const t3 = path.join(world.dir, 'restore-3');
  const r3 = ag.restore(id, t3); expect(r3.code, r3.out).toBe(0);
  expect(compare(after, manifest(restoredPath(t3, src)))).toEqual([]);
  await rail(page, 'cust');
  await expect(page.getByRole('row').filter({ hasText: 'qa-rb' }).first()).toBeVisible();

  // ---------------------------------------------------------------- 3. erase after 14 days, not before
  evidence.step('delete the set again; maintenance at +13 days keeps it, at +15 days erases it');
  await openSet(page, 'qa-rb', 'Office files');
  await tab(page, 'Maintenance');
  await page.getByRole('button', { name: '🗑 Delete the set' }).click();
  await confirmYes(page);
  await expect(page.locator('#toast').filter({ hasText: 'Moved to the recycle bin' })).toBeVisible();
  const bin2 = fs.readdirSync(path.join(userDir, '_recycle')).filter((d) => d.startsWith('set-' + id + '-'));
  expect(bin2.length).toBe(1);
  const binDir = path.join(userDir, '_recycle', bin2[0]);
  const binBytes = [...tree(binDir).values()].reduce((a, x) => a + x, 0);
  expect(binBytes, 'bytes held in the bin').toBeGreaterThan(20 * 1024 * 1024 * 0.5);
  const m13 = await world.adminApi('POST', 'maintenance', { now: runId(new Date(Date.now() + 13 * 86400000)) });
  expect(m13.status, m13.text).toBe(200);
  expect(fs.existsSync(binDir), 'the bin after 13 days').toBe(true);
  const m15 = await world.adminApi('POST', 'maintenance', { now: runId(new Date(Date.now() + 15 * 86400000)) });
  expect(m15.status, m15.text).toBe(200);
  evidence.step('ORACLE (disk): the bin folder and its bytes are gone; the site\'s bin is empty');
  expect(fs.existsSync(binDir), 'the bin after 15 days').toBe(false);
  expect(fs.existsSync(store)).toBe(false);
  // the backed-up data of the set (objects, restic repository, run logs) — not the key in db/keys (kept for copies of
  // the set) and not stats/<set>.tsv (per-run numbers for Insights; noted in night/q/README.md, run 1)
  const left = [...tree(userDir).keys()].filter((k) => k.includes(id) && /^(files|restic|logs|_recycle)\//.test(k));
  expect(left, 'backed-up data of the erased set left anywhere in the customer\'s folder').toEqual([]);
  await rail(page, 'dash'); await rail(page, 'storage');
  await expect(page.getByText('The recycle bin is empty.')).toBeVisible();
});
