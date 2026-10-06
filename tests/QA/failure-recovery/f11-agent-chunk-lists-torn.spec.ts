// F11 — The computer's chunk lists (the local record of the large files' pieces, used to send only what changed) are
// torn in the middle of a line between two backups (a disk error, a torn write, a cleanup tool).
// Expected: the next backup is still correct — it succeeds (or fails loudly once and the one after succeeds), never a
// "success" with files missing — and the newest point restores identical to the files as they are now, while the first
// point still restores identical to the original dataset.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import { bigFiles, leftovers } from '../lib/fault';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';

test('F11 agent chunk lists torn between backups → next backup still correct → newest and first points restore identical', async ({ world, evidence }) => {
  world.addCustomer('qa-f11');
  const ag = world.agent('qa-f11', 'F11-PC'); ag.register();
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

  evidence.step('DAMAGE the computer\'s state: every chunk list (the large file\'s) torn in the middle of a line');
  const setDir = path.join(ag.home, 'sets', id);
  const st = path.join(setDir, 'state.txt');
  expect(fs.existsSync(st), 'the local index exists where the agent keeps it').toBe(true);
  const chunkDir = path.join(setDir, 'chunks');
  const lists = fs.existsSync(chunkDir) ? fs.readdirSync(chunkDir) : [];
  expect(lists.length, 'the large file has a chunk list on the computer').toBeGreaterThan(0);
  for (const f of lists) {
    // torn in the middle of a chunk id: a line start near the half, plus 10 characters
    const p = path.join(chunkDir, f); const c = fs.readFileSync(p, 'utf8'); const cut = c.indexOf('\n', Math.floor(c.length / 2)) + 1 + 10;
    expect(cut, 'the chunk list has a line to tear').toBeGreaterThan(11);
    fs.writeFileSync(p, c.slice(0, cut));
  }

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
