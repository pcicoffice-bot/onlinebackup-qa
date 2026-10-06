// J6 — A restic backup set, as a customer has it: "Back up now" on the site, the computer's service runs restic to the
// server, a second run after a change sends only the change, and the backup restores identical.
// Oracle: SHA-256 of every restored file against the manifest made before (the product's own "success" is not enough).
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath, CUSTOMER_PASSWORD } from '../lib/world';
import { openSet, backUpNow, waitForTask } from '../lib/ui';
import * as fs from 'fs';
import * as path from 'path';

test('J6 restic set: Back up now from the site → change → backup → restore identical (SHA-256), every point listed', async ({ admin: page, world, evidence }) => {
  test.skip(!process.env.OB_RESTIC || !fs.existsSync(process.env.OB_RESTIC), 'needs the restic program (OB_RESTIC)');
  world.addCustomer('qa-restic');
  const ag = world.agent('qa-restic', 'RESTIC-PC1'); ag.register();
  const src = path.join(world.dir, 'data'); goldenDataset(src);
  const id = ag.addSet('Restic files', [src], ['--engine', 'RESTIC']);
  ag.startService();
  evidence.step('Back up now on the site');
  await openSet(page, 'qa-restic', 'Restic files');
  await backUpNow(page);
  await waitForTask(page, 'Restic files', 'Succeeded');
  ag.stopService();

  evidence.step('the customer changes a file and adds one; backup 2');
  fs.appendFileSync(path.join(src, 'Documents/letter.txt'), 'added line\n');
  fs.writeFileSync(path.join(src, 'מסמכים/נוסף.txt'), 'חדש');
  const v2 = manifest(src);
  const b = ag.backup(id);
  expect(b.out, b.out).toMatch(/^BS_STOP_SUCCESS /m);

  evidence.step('two points are listed (with the encryption password)');
  const points = ag.cli(['points', '--set', id, '--password', CUSTOMER_PASSWORD]).out.trim().split('\n').filter((l) => /^[0-9a-f]{8}\t/.test(l));
  expect(points.length).toBe(2);

  evidence.step('ORACLE: restore the latest point into an empty folder, every file identical');
  const target = path.join(world.dir, 'restore');
  const r = ag.restore(id, target);
  expect(r.code, r.out).toBe(0);
  expect(compare(v2, manifest(restoredPath(target, src)))).toEqual([]);
});
