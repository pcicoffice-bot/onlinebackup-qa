// J4 — The customer loses the data (the folder is deleted), then restores it to where it was.
// Also: an incremental backup (changed, added, deleted files) and restoring an older version.
// Oracle: SHA-256 of every file against the manifest made before — one difference = FAIL.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare } from '../lib/world';
import * as fs from 'fs';
import * as path from 'path';

test('J4 backup → change → backup → delete the source → restore to the original place → identical; the first version too', async ({ world, evidence }) => {
  world.addCustomer('qa-gamma');
  const ag = world.agent('qa-gamma', 'GAMMA-PC1'); ag.register();
  const src = path.join(world.dir, 'data'); const v1 = goldenDataset(src);
  const id = ag.addSet('Office files', [src]);
  evidence.step('backup 1');
  let b = ag.backup(id); expect(b.out, b.out).toMatch(/^BS_STOP_SUCCESS /m);

  evidence.step('the customer works: changes, adds and deletes files');
  fs.appendFileSync(path.join(src, 'Documents/letter.txt'), 'second paragraph\n');
  fs.writeFileSync(path.join(src, 'מסמכים/חדש.txt'), 'קובץ חדש');
  fs.rmSync(path.join(src, 'Many/file-007.csv'));
  fs.rmSync(path.join(src, 'Duplicates'), { recursive: true });
  const v2 = manifest(src);
  evidence.step('backup 2 (incremental)');
  b = ag.backup(id); expect(b.out, b.out).toMatch(/^BS_STOP_SUCCESS new=1 upd=1 perm=0 del=3 /m);

  evidence.step('disaster: the folder is deleted');
  fs.rmSync(src, { recursive: true, force: true });
  expect(fs.existsSync(src)).toBe(false);

  evidence.step('restore to the original place');
  const r = ag.restore(id, '/', ['--overwrite']);
  expect(r.code, r.out).toBe(0);
  evidence.step('ORACLE: every file identical to the latest version');
  expect(compare(v2, manifest(src))).toEqual([]);

  evidence.step('restore the FIRST version to another folder: identical to version 1');
  const points = ag.cli(['points', '--set', id]).out.trim().split('\n').filter((l) => /^\d{4}-/.test(l)).map((l) => l.split(/\s/)[0]).sort();
  expect(points.length).toBe(2);
  const old = path.join(world.dir, 'old');
  const r1 = ag.restore(id, old, ['--point', points[0]]);
  expect(r1.code, r1.out).toBe(0);
  expect(compare(v1, manifest(path.join(old, src.replace(/^\//, ''))))).toEqual([]);
});
