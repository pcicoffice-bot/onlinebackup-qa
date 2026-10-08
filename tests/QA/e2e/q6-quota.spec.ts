// Q6 (ST-05) — the quota, end to end: a customer fills the quota, the next backup is stopped with a clear reason (on the
// computer and on the site), every existing backup still restores identical, and after the technician raises the quota on
// the site the next backup completes and restores identical.
// What the product claims (src/Server/Api.cs Begin/Upload): "The quota is full: new backups are stopped, existing
// backups are kept." / "The user's quota was exceeded." (507).
// FIXTURE: the customer is created with a 0.03 GB quota (the site's quota field takes whole GB, at least 1 — filling 1 GB
// on a shared test machine is not reasonable); everything after that is done where a person does it.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import { openCustomer, waitForTask } from '../lib/ui';
import { rail } from './q-helpers';
import * as fs from 'fs';
import * as path from 'path';
import * as crypto from 'crypto';

test('Q6 ST-05 a full quota stops the next backup clearly, keeps every existing backup restorable, and a raised quota lets the next one complete', async ({ admin: page, world, evidence }) => {
  world.addCustomer('qa-quota', 0.03);   // 32.2 MB
  const ag = world.agent('qa-quota', 'Q-PC1'); ag.register();
  const src = path.join(world.dir, 'data'); const v1 = goldenDataset(src);
  const id = ag.addSet('Office files', [src]);
  const b1 = ag.backup(id); expect(b1.out, b1.out).toMatch(/^BS_STOP_SUCCESS /m);
  const store = path.join(world.usersDir, 'qa-quota', 'files', id);

  evidence.step('the customer adds 16 MB of new data: more than the room left in the quota');
  fs.writeFileSync(path.join(src, 'Binary/new-16mb.bin'), crypto.randomBytes(16 * 1024 * 1024));
  const v2 = manifest(src);
  const b2 = ag.backup(id);
  evidence.step('ORACLE (agent): the backup is not a success, and the computer says why (the quota)');
  expect(b2.code, b2.out).not.toBe(0);
  expect(b2.out, b2.out).not.toMatch(/^BS_STOP_SUCCESS /m);
  expect(b2.out, b2.out).toMatch(/quota/i);
  evidence.step('a further try is refused too (the quota is still full)');
  const b3 = ag.backup(id);
  expect(b3.code, b3.out).not.toBe(0);
  expect(b3.out, b3.out).toMatch(/quota/i);

  evidence.step('the site: the tasks page shows the run as failed, its detail names the quota');
  const row = await waitForTask(page, 'Office files', 'Failed', 2);
  expect(row).toContain('Office files');
  await page.getByRole('row').filter({ hasText: 'Office files' }).filter({ hasText: 'Failed' }).first().click();
  await expect(page.locator('main')).toContainText(/quota/i);

  evidence.step('ORACLE (restore): the backup made before the quota filled restores identical (SHA-256)');
  const pts = ag.cli(['points', '--set', id]).out.trim().split('\n').filter((l) => /^\d{4}-/.test(l)).map((l) => l.split(/\s/)[0]).sort();
  const t1 = path.join(world.dir, 'restore-1');
  const r1 = ag.restore(id, t1, ['--point', pts[0]]); expect(r1.code, r1.out).toBe(0);
  expect(compare(v1, manifest(restoredPath(t1, src)))).toEqual([]);
  evidence.step('ORACLE (restore): the newest point holds no half file — every file in it is identical to the source');
  const tn = path.join(world.dir, 'restore-newest');
  const rn = ag.restore(id, tn); expect(rn.code, rn.out).toBe(0);
  const got = manifest(restoredPath(tn, src));
  expect(compare(new Map([...v2].filter(([k]) => got.has(k))), got)).toEqual([]);
  expect([...v1.keys()].filter((k) => !got.has(k)), 'files of the first backup missing from the newest point').toEqual([]);
  expect(fs.readdirSync(store).filter((d) => /\.tmp$|staging/i.test(d)), 'staging left in the store').toEqual([]);

  evidence.step('FULL: the quota is lowered below what is stored (0.01 GB, the same call the Quota tab makes — its field takes whole GB)');
  const low = await world.adminApi('POST', 'users/qa-quota/quota', { quotaGB: '0.01', quotaType: 'COMPRESSED' });
  expect(low.status, low.text).toBe(200);
  const b5 = ag.backup(id);
  console.log('Q6 FACT over the room: ' + b2.out.split('\n')[0] + ' | full: ' + b5.out.trim().split('\n').slice(-2).join(' / '));
  evidence.step('ORACLE (agent): the backup does not start, and the computer says the quota is full; nothing reaches the server');
  const storeBytes = JSON.stringify([...manifest(store)].sort());
  expect(b5.code, b5.out).not.toBe(0);
  expect(b5.out, b5.out).toMatch(/quota is full/i);
  expect(JSON.stringify([...manifest(store)].sort()), 'the store changed after a refused backup').toBe(storeBytes);
  const t5 = path.join(world.dir, 'restore-full');
  const r5 = ag.restore(id, t5, ['--point', pts[0]]); expect(r5.code, r5.out).toBe(0);
  expect(compare(v1, manifest(restoredPath(t5, src)))).toEqual([]);

  evidence.step('the technician raises the quota on the site: Quota and pricing → 1 GB, Save');
  await rail(page, 'dash');
  await openCustomer(page, 'qa-quota');
  await page.locator('.tabs[role=tablist]').first().locator('button', { hasText: 'Quota and pricing' }).click();
  await page.locator('.form .fr').filter({ hasText: 'Quota (GB)' }).locator('input').fill('1');
  await page.getByRole('button', { name: 'Save and exit' }).click();
  await expect(page.locator('#toast').filter({ hasText: 'Saved' })).toBeVisible();

  evidence.step('the next backup completes and restores identical (SHA-256)');
  const b4 = ag.backup(id); expect(b4.out, b4.out).toMatch(/^BS_STOP_SUCCESS /m);
  const t2 = path.join(world.dir, 'restore-2');
  const r2 = ag.restore(id, t2); expect(r2.code, r2.out).toBe(0);
  expect(compare(v2, manifest(restoredPath(t2, src)))).toEqual([]);
});
