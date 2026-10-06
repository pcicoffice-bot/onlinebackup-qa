// F15 — Lines of the computer's local index of a set (state.txt: what was backed up, with size and time) are lost between
// two backups, and its last line is torn (a disk error, a torn write, a cleanup tool). Ten of the lost lines are files
// the customer deleted since.
// Expected: the next backup is still correct and the newest point restores identical to the files as they are now —
// no change lost, and no deleted file coming back — while the first point still restores identical to the original.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import { bigFiles, leftovers } from '../lib/fault';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';

test('F15 lines of the agent index lost between backups → next backup still correct → newest point has no deleted file back, first point identical', async ({ world, evidence }) => {
  world.addCustomer('qa-f15');
  const ag = world.agent('qa-f15', 'F15-PC'); ag.register();
  const src = path.join(world.dir, 'data'); goldenDataset(src);
  bigFiles(src, 1, 40);                        // above the 25 MB delta threshold: it gets a chunk list on the computer
  const v1 = manifest(src);
  const id = ag.addSet('Files', [src]);
  evidence.step('backup 1');
  const b1 = ag.backup(id); expect(b1.out, b1.out).toMatch(/^BS_STOP_SUCCESS /m);
  const points1 = ag.cli(['points', '--set', id]).out.trim().split('\n').filter((l) => /^\d+/.test(l));
  const p1 = points1[points1.length - 1].trim();

  evidence.step('the files change: a text edited, the large file grows, 10 files deleted');
  fs.writeFileSync(path.join(src, 'Documents/letter.txt'), 'Dear customer,\nthis letter was changed after the first backup.\n');
  fs.appendFileSync(path.join(src, 'Big/big-0.bin'), crypto.randomBytes(1024 * 1024));
  for (let i = 0; i < 10; i++) fs.rmSync(path.join(src, 'Many/file-' + String(i).padStart(3, '0') + '.csv'));
  const v2 = manifest(src);

  evidence.step('DAMAGE the computer\'s state: 20 lines of the local index lost (10 of them files deleted since), the last line torn');
  const setDir = path.join(ag.home, 'sets', id);
  const st = path.join(setDir, 'state.txt');
  expect(fs.existsSync(st), 'the local index exists where the agent keeps it').toBe(true);
  const lost = (l: string) => /Many\/file-(00\d|10\d)\.csv\t/.test(l);
  const lines = fs.readFileSync(st, 'utf8').split('\n');
  expect(lines.filter(lost).length, 'the index has the lines that are lost now').toBe(20);
  expect(lines.some((l) => l.includes('big-0.bin\t')), 'the large file is in the index').toBe(true);
  const kept = lines.filter((l) => !lost(l) && l.length > 0);
  const lastIsBig = kept[kept.length - 1].includes('big-0.bin\t');
  if (lastIsBig) kept.unshift(kept.splice(kept.length - 1, 1)[0]);   // the torn line is not the large file's (its change goes as a delta against an intact chunk list)
  const torn = kept.join('\n'); fs.writeFileSync(st, torn.slice(0, torn.length - Math.floor(kept[kept.length - 1].length / 2)));
  evidence.step('backup 2 on the damaged state');
  const b2 = ag.backup(id);
  let b3 = { code: -1, out: '(not run)' };
  if (!/^BS_STOP_SUCCESS /m.test(b2.out)) { evidence.step('backup 2 did not succeed; backup 3 (does it heal by itself?)'); b3 = ag.backup(id); }
  expect(b2.out, 'the next backup after the damage must still be correct.\nbackup 2:\n' + b2.out + '\nbackup 3:\n' + b3.out).toMatch(/^BS_STOP_SUCCESS /m);

  evidence.step('ORACLE: the newest point restores identical to the files as they are now');
  const t2 = path.join(world.dir, 'restore-new');
  const r2 = ag.restore(id, t2); expect(r2.code, r2.out).toBe(0);
  expect(compare(v2, manifest(restoredPath(t2, src)))).toEqual([]);
  expect(leftovers(manifest(t2))).toEqual([]);
  evidence.step('ORACLE: the first point still restores identical to the original dataset');
  const t1 = path.join(world.dir, 'restore-first');
  const r1 = ag.restore(id, t1, ['--point', p1]); expect(r1.code, r1.out).toBe(0);
  expect(compare(v1, manifest(restoredPath(t1, src)))).toEqual([]);
});
