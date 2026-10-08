// F14 — A computer is CLONED (a virtual machine copied from a template, a disk image put on a second PC): the copy
// carries the same program folder — the same registration, the same set, the same local index — and from then on each
// copy has its own files at the same path and backs up "its" set.
// Expected: neither copy silently damages the other's backups — after each copy's backup, its newest point restores
// identical (SHA-256) to that copy's files; if the product cannot keep the two apart it must say so (a failed or
// refused run), never report success with another computer's content in the point.
// Each copy sees its own folder at the same path through a private mount namespace (unshare -m, needs root; with
// passwordless sudo only the namespace is made as root, the agent itself runs as the normal user — lib/fault privateView).
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import { agentRun, privateView } from '../lib/fault';
import * as fs from 'fs';
import * as path from 'path';

test('F14 cloned computer (same registration, same set, own files) → no success with the other copy\'s content → each newest point restores identical', async ({ world, evidence }) => {
  const nsOk = privateView(world.dir, world.dir) !== null;
  test.skip(!nsOk, 'NOT TESTED: this machine does not allow a private mount namespace (unshare -m needs root or passwordless sudo)');
  world.addCustomer('qa-f14');
  const a = world.agent('qa-f14', 'CLONED-PC'); a.register();
  const src = path.join(world.dir, 'data'); const dataA = goldenDataset(src);
  const id = a.addSet('Files', [src]);
  evidence.step('the original computer backs up');
  const b1 = a.backup(id); expect(b1.out, b1.out).toMatch(/^BS_STOP_SUCCESS /m);

  evidence.step('the computer is CLONED: program folder and disk copied');
  const b = world.agent('qa-f14', 'CLONED-PC'); b.home += '-clone'; b.log = b.log.replace(/\.log$/, '-clone.log');
  fs.cpSync(a.home, b.home, { recursive: true });
  const disk = path.join(world.dir, 'clone-disk');
  fs.cpSync(src, disk, { recursive: true, preserveTimestamps: true });
  // the clone sees its own disk at the same path
  const onClone = privateView(disk, src)!;

  evidence.step('the clone\'s files diverge: 2 edited, 1 deleted, 1 new');
  fs.writeFileSync(path.join(disk, 'Documents/letter.txt'), 'the CLONE\'s own letter\n');
  fs.writeFileSync(path.join(disk, 'Duplicates/a.txt'), 'the clone changed this one');
  fs.rmSync(path.join(disk, 'Folder with spaces/file with spaces.txt'));
  fs.writeFileSync(path.join(disk, 'Documents/clone-only.txt'), 'only on the clone');
  const dataB = manifest(disk);

  evidence.step('the clone backs up');
  const b2 = agentRun(b, ['backup', '--set', id], onClone);
  evidence.step('clone backup: ' + b2.out.trim().split('\n')[0]);
  expect(b2.out, 'the clone\'s backup program ran (a result line of the agent)').toMatch(/^BS_STOP_/m);
  if (/^BS_STOP_SUCCESS /m.test(b2.out)) {
    const t = path.join(world.dir, 'restore-clone'); const r = b.restore(id, t); expect(r.code, r.out).toBe(0);
    expect(compare(dataB, manifest(restoredPath(t, src))), 'the clone\'s backup said success: its newest point must be the clone\'s files').toEqual([]);
  }

  evidence.step('the original computer backs up again (its files did not change)');
  const b3 = a.backup(id);
  evidence.step('original backup: ' + b3.out.trim().split('\n')[0]);
  if (/^BS_STOP_SUCCESS /m.test(b3.out)) {
    evidence.step('ORACLE: the original\'s backup said success → its newest point must be the original\'s files');
    const t = path.join(world.dir, 'restore-original'); const r = a.restore(id, t); expect(r.code, r.out).toBe(0);
    expect(compare(dataA, manifest(restoredPath(t, src))), 'the original computer\'s backup said success, but its newest point holds the clone\'s content').toEqual([]);
  } else expect(b3.out, 'a refused run must say why').toMatch(/clone|another computer|copied|registration|index/i);

  evidence.step('ORACLE: the first point still restores identical to the original files');
  const p1 = a.cli(['points', '--set', id]).out.trim().split('\n').filter((l) => /^\d+/.test(l)).map((l) => l.trim()).sort()[0];
  const t1 = path.join(world.dir, 'restore-first'); const r1 = a.restore(id, t1, ['--point', p1]); expect(r1.code, r1.out).toBe(0);
  expect(compare(dataA, manifest(restoredPath(t1, src)))).toEqual([]);
});
