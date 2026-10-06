// N8 — The whole source folder disappears (a disk unplugged, a share gone; here: renamed away):
//   (a) DURING backup 2 (while a large changed file is being sent)
//   (b) BEFORE backup 2 (F5 covers what the site shows; here the data oracle: nothing deleted on the server)
// The fault is proven by the file system: the folder is not there while the run goes on (stat), and is back after.
// Contract BK-05: "partly unreadable = success with error; all gone = failure; last good backup kept and still
// restorable … no deletion sent". Certainly required: no file of point 1 missing from the newest point.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath, Manifest } from '../lib/world';
import { backupAsync, result, setRuns, points, sh, until, keep } from '../lib/nfault';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';

/** Each restored file is one of the versions the source had (v1 or v2), and every file of v1 is there. */
function eachFromAVersion(v1: Manifest, v2: Manifest, got: Manifest) {
  const bad: string[] = [];
  for (const k of v1.keys()) if (!got.has(k)) bad.push('MISSING ' + k);
  for (const [k, g] of got) if (g.sha256 !== v1.get(k)?.sha256 && g.sha256 !== v2.get(k)?.sha256) bad.push('NEITHER VERSION ' + k);
  return bad;
}

test('N8a source folder renamed away DURING backup 2 → not a plain success, nothing deleted, newest point holds every file → folder back → backup 3 → restore identical (SHA-256)', async ({ world, evidence }) => {
  world.addCustomer('qa-n8a');
  const ag = world.agent('qa-n8a', 'N8A-PC'); ag.register();
  const src = path.join(world.dir, 'data'); const v1 = goldenDataset(src);
  const id = ag.addSet('Files', [src]);
  const b1 = ag.backup(id); expect(b1.out, b1.out).toMatch(/^BS_STOP_SUCCESS /m);
  // run 2 sends a new 20 MB file in the folder the agent walks FIRST (it walks the sorted folders from the last:
  // "מסמכים"), so the rest of the tree is still to be read when the folder goes away; a file in "Many" changes too
  fs.writeFileSync(path.join(src, 'מסמכים/big-new-in-run-2.bin'), crypto.randomBytes(20 * 1024 * 1024));
  fs.writeFileSync(path.join(src, 'Many/file-149.csv'), 'changed in run 2\n');
  const v2 = manifest(src);
  await world.setBandwidth('qa-n8a', id, 1000);
  const run = backupAsync(ag, id);
  expect(await until(async () => (await world.live()).includes(id), 90000), 'run 2 is under way').toBe(true);
  await new Promise((r) => setTimeout(r, 3000));
  evidence.step('RENAME the source folder away while run 2 is sending');
  fs.renameSync(src, src + '-away');
  const ev: Record<string, unknown> = { statDuring: sh('stat -c %n "' + src + '"'), aliveAtRename: run.p.exitCode === null };
  expect(ev.statDuring, 'PROOF: the folder is not there').toMatch(/No such file/);
  expect(ev.aliveAtRename, 'the run was still going when the folder went away').toBe(true);
  const r2 = await run.done;
  ev.run2 = r2.out;
  evidence.step('run 2: exit ' + r2.code + '\n' + r2.out.trim().slice(0, 2000));
  expect(result(r2.out), 'a run that lost its source is not a plain success').not.toBe('BS_STOP_SUCCESS');
  expect(r2.out, 'no file deleted because its folder went away').toMatch(/ del=0 |^\(none\)/m);
  const runs = await setRuns(world, 'qa-n8a', id);
  ev.runs = runs;
  expect(runs[0].status, 'the server shows it red: ' + JSON.stringify(runs[0])).toBe('bad');
  evidence.step('ORACLE: the newest point still holds every file (each in one of its two versions)');
  const tn = path.join(world.dir, 'restore-during'); const rn = ag.restore(id, tn); expect(rn.code, rn.out).toBe(0);
  expect(eachFromAVersion(v1, v2, manifest(restoredPath(tn, src)))).toEqual([]);

  evidence.step('RECOVERY: the folder is back → backup 3');
  fs.renameSync(src + '-away', src);
  await world.setBandwidth('qa-n8a', id, 0);
  const b3 = ag.backup(id); expect(b3.out, b3.out).toMatch(/^BS_STOP_SUCCESS /m);
  const t = path.join(world.dir, 'restore'); const r = ag.restore(id, t); expect(r.code, r.out).toBe(0);
  expect(compare(v2, manifest(restoredPath(t, src)))).toEqual([]);
  const t1 = path.join(world.dir, 'restore-1'); const r1 = ag.restore(id, t1, ['--point', points(ag, id)[0]]); expect(r1.code, r1.out).toBe(0);
  expect(compare(v1, manifest(restoredPath(t1, src)))).toEqual([]);
  keep('n8a', ev);
});

test('N8b source folder gone BEFORE backup 2 → failure (BK-05), nothing deleted, newest point = point 1 → folder back → backup 3 → restore identical (SHA-256)', async ({ world, evidence }) => {
  world.addCustomer('qa-n8b');
  const ag = world.agent('qa-n8b', 'N8B-PC'); ag.register();
  const src = path.join(world.dir, 'data'); const v1 = goldenDataset(src);
  const id = ag.addSet('Files', [src]);
  const b1 = ag.backup(id); expect(b1.out, b1.out).toMatch(/^BS_STOP_SUCCESS /m);
  fs.renameSync(src, src + '-away');
  const ev: Record<string, unknown> = { stat: sh('stat -c %n "' + src + '"') };
  expect(ev.stat, 'PROOF: the folder is not there').toMatch(/No such file/);
  const b2 = ag.backup(id);
  ev.run2 = b2.out;
  evidence.step('run 2: exit ' + b2.code + '\n' + b2.out.trim());
  expect(b2.code).not.toBe(0);
  expect(result(b2.out), 'all sources gone = failure (BK-05)').toBe('BS_STOP_BY_SYSTEM_ERROR');
  expect(b2.out).toMatch(/ del=0 /);
  const runs = await setRuns(world, 'qa-n8b', id);
  expect(runs[0].result).toBe('BS_STOP_BY_SYSTEM_ERROR');
  const tn = path.join(world.dir, 'restore-during'); const rn = ag.restore(id, tn); expect(rn.code, rn.out).toBe(0);
  expect(compare(v1, manifest(restoredPath(tn, src))), 'the newest point still holds every file').toEqual([]);
  fs.renameSync(src + '-away', src);
  fs.writeFileSync(path.join(src, 'Documents/after.txt'), 'after the folder came back\n');
  const v3 = manifest(src);
  const b3 = ag.backup(id); expect(b3.out, b3.out).toMatch(/^BS_STOP_SUCCESS /m);
  expect(b3.out, 'only the new file is sent (the index was not emptied by the failed run)').toMatch(/new=1 upd=0 perm=0 del=0/);
  const t = path.join(world.dir, 'restore'); const r = ag.restore(id, t); expect(r.code, r.out).toBe(0);
  expect(compare(v3, manifest(restoredPath(t, src)))).toEqual([]);
  keep('n8b', ev);
});
