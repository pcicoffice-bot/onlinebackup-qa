// P04 (AG-06) — the computer's local state of a set is lost between two backups (its whole folder on the computer:
// the local index state.txt, the chunk lists, the last-run notes — a cleanup tool, a restored disk image, a profile
// reset). The encryption key (kept apart) survives.
// Expected: the next backup of the real agent program is correct — exactly the 1 new, 1 changed and 1 deleted file,
// nothing else sent again (the 20 MB file is not re-uploaded) — the newest point restores identical to the files as
// they are now, the first point still restores identical to the original, and nothing stored on the server was
// deleted (every stored object the server held before is still held, byte for byte).
// Oracles outside the product: SHA-256 manifests of the source at each backup; the SHA-256 of every file of the set's
// store on the server's disk before and after.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import * as fs from 'fs';
import * as path from 'path';

/** The SHA-256 of every stored object of the set on the server's disk (the encrypted file copies "*.000", "*.001"…;
 *  not the store's own index.db / jobs.log, which every commit rewrites). */
const objects = (store: string) => new Set([...manifest(store)].filter(([k]) => /\.\d{3}$/.test(k)).map(([, v]) => v.sha256));

test('P04 AG-06 the agent\'s local index is deleted between backups: the next backup is exact, the newest point restores identical, nothing deleted on the server', async ({ world, evidence }) => {
  const login = 'qa-p04';
  world.addCustomer(login);
  const ag = world.agent(login, 'P04-PC'); ag.register();
  const src = path.join(world.dir, 'data'); const v1 = goldenDataset(src);
  const id = ag.addSet('Files', [src]);
  evidence.step('backup 1');
  const b1 = ag.backup(id); expect(b1.out, b1.out).toMatch(/^BS_STOP_SUCCESS /m);
  const p1 = ag.cli(['points', '--set', id]).out.trim().split('\n').filter((l) => /^\d{4}-/.test(l)).map((l) => l.trim());
  expect(p1.length).toBe(1);
  const store = path.join(world.usersDir, login, 'files', id);
  const storedBefore = objects(store);
  expect(storedBefore.size, 'objects on the server after backup 1').toBeGreaterThan(10);

  evidence.step('the files change: a letter edited, a file added, a file deleted');
  await new Promise((r) => setTimeout(r, 1100));
  fs.writeFileSync(path.join(src, 'Documents/letter.txt'), 'Dear customer,\nthis letter was changed after the first backup.\n');
  fs.writeFileSync(path.join(src, 'Documents/added.txt'), 'added after the first backup');
  fs.rmSync(path.join(src, 'Many/file-042.csv'));
  const v2 = manifest(src);

  evidence.step('LOSS: the computer\'s whole local state of the set is deleted (index, chunk lists, notes); the key stays');
  const setDir = path.join(ag.home, 'sets', id);
  expect(fs.existsSync(path.join(setDir, 'state.txt')), 'the local index is where the agent keeps it').toBe(true);
  fs.rmSync(setDir, { recursive: true, force: true });
  expect(fs.existsSync(path.join(ag.home, 'keys', id + '.bin')), 'the set\'s key on the computer').toBe(true);

  evidence.step('backup 2 on the lost state');
  const b2 = ag.backup(id);
  expect(b2.out, b2.out).toMatch(/^BS_STOP_SUCCESS /m);
  const m = /new=(\d+) upd=(\d+) perm=(\d+) del=(\d+) bytes=(\d+)/.exec(b2.out)!;
  evidence.step('backup 2 counted: ' + m[0]);
  expect({ new: +m[1], upd: +m[2], del: +m[4] }, 'exactly the change since backup 1').toEqual({ new: 1, upd: 1, del: 1 });
  expect(+m[5], 'bytes sent again by backup 2 (the dataset is ' + [...v1.values()].reduce((a, x) => a + x.size, 0) + ' bytes)').toBeLessThan(1024 * 1024);

  evidence.step('ORACLE (server disk): every object stored before is still stored, byte for byte');
  const storedAfter = objects(store);
  const lost = [...storedBefore].filter((h) => !storedAfter.has(h));
  expect(lost, 'objects of backup 1 no longer on the server').toEqual([]);
  const p2 = ag.cli(['points', '--set', id]).out.trim().split('\n').filter((l) => /^\d{4}-/.test(l)).map((l) => l.trim()).sort();
  expect(p2.length).toBe(2);
  expect(p2[0]).toBe(p1[0]);

  evidence.step('ORACLE: the newest point restores identical to the files as they are now (SHA-256)');
  const t2 = path.join(world.dir, 'restore-new');
  const r2 = ag.restore(id, t2); expect(r2.code, r2.out).toBe(0);
  expect(compare(v2, manifest(restoredPath(t2, src)))).toEqual([]);
  evidence.step('ORACLE: the first point still restores identical to the original');
  const t1 = path.join(world.dir, 'restore-first');
  const r1 = ag.restore(id, t1, ['--point', p1[0]]); expect(r1.code, r1.out).toBe(0);
  expect(compare(v1, manifest(restoredPath(t1, src)))).toEqual([]);

  evidence.step('backup 3 (nothing changed): sends nothing');
  const b3 = ag.backup(id); expect(b3.out, b3.out).toMatch(/^BS_STOP_SUCCESS new=0 upd=0 perm=0 del=0 /m);
});
