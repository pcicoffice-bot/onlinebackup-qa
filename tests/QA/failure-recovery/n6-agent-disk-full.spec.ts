// N6 — The COMPUTER's own disk is full where the agent writes:
//   (a) its temporary folder (<agent home>/temp: the default working folder of a set, and where a restore downloads each
//       object before it is decrypted) — a full 2 MB tmpfs mounted there
//   (b) the set's own folder (<agent home>/sets/<id>: the local index state.txt, the open-run note, last-attempt.txt),
//       full but for a few KB — the index cannot be saved AFTER the server committed the run
// The fault is proven by df of the mount (100 % / bytes free) and by a write that fails there.
// Certainly required: no data loss, no false success, no half file, recovery by the next run, SHA-256 identical.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import { notWhole, leftovers, unmount } from '../lib/fault';
import { result, setRuns, points, tmpfs, fill, df, sh, keep } from '../lib/nfault';
import * as fs from 'fs';
import * as path from 'path';

test('N6a agent temp folder full → backup still correct; restore through the full temp fails truthfully, no half file → temp freed → restore identical (SHA-256)', async ({ world, evidence }) => {
  world.addCustomer('qa-n6a');
  const ag = world.agent('qa-n6a', 'N6A-PC'); ag.register();
  const src = path.join(world.dir, 'data'); const v1 = goldenDataset(src);
  const id = ag.addSet('Files', [src]);
  const temp = path.join(ag.home, 'temp');
  if (!tmpfs(temp, 2)) test.skip(true, 'NOT TESTED: this machine does not allow mounting a small disk');
  const ev: Record<string, unknown> = {};
  try {
    fill(temp);
    ev.df = df(temp); ev.probe = sh('dd if=/dev/zero of="' + temp + '/probe.bin" bs=4k count=1');
    evidence.step('PROOF: the temp folder is full: ' + ev.df + ' / a write there: ' + ev.probe);
    expect(ev.probe, 'a write in the temp folder fails').toMatch(/No space left/);
    const b1 = ag.backup(id);
    ev.backup = b1.out;
    evidence.step('backup with a full temp folder: ' + b1.out.trim());
    const runs = await setRuns(world, 'qa-n6a', id);
    if (b1.code === 0) {
      expect(runs[0].status, 'the server agrees').toMatch(/ok|warn/);
    } else {
      expect(result(b1.out)).not.toMatch(/^BS_STOP_SUCCESS/);
      expect(runs[0].status).toBe('bad');
    }
    evidence.step('restore of 2 files (filter "Duplicates") while the temp folder is full');
    // (a first run restoring all 170 files was still going after 12 minutes: each file waits through the download
    // retries — evidence/n6a-first-attempt.txt; 2 files keep the test short)
    const tr = path.join(world.dir, 'restore-during');
    const t0 = Date.now(); const rr = ag.restore(id, tr, ['--filter', 'Duplicates']); const ms = Date.now() - t0;
    ev.restoreDuring = rr.out; ev.restoreMs = ms;
    const tempLeft = fs.readdirSync(temp).filter((f) => f.endsWith('.obj'));
    ev.tempLeftovers = tempLeft.map((f) => f + ' ' + fs.statSync(path.join(temp, f)).size);
    evidence.step('restore: exit ' + rr.code + ' in ' + ms + ' ms\n' + rr.out.trim().slice(0, 1500) + '\ntemp leftovers: ' + JSON.stringify(ev.tempLeftovers));
    const got = manifest(restoredPath(tr, src));
    expect(notWhole(v1, got), 'never a half file passing as whole').toEqual([]);
    expect(leftovers(manifest(tr)), 'no temporary half file in the target').toEqual([]);
    expect(rr.code, 'a restore that wrote nothing is not a success').not.toBe(0);
    expect(rr.out).toMatch(/failed=2/);
    expect.soft(rr.out, 'the reason is the computer\'s own full disk, not the line').toMatch(/space/i);
    expect.soft(tempLeft, 'the failed downloads leave nothing in the temp folder').toEqual([]);
  } finally { unmount(temp); keep('n6a', ev); }

  evidence.step('RECOVERY: the temp folder has room again; a new backup and a restore');
  fs.writeFileSync(path.join(src, 'Documents/after-temp.txt'), 'after the temp folder was full\n');
  const v2 = manifest(src);
  const b2 = ag.backup(id); expect(b2.out, b2.out).toMatch(/^BS_STOP_SUCCESS /m);
  const t = path.join(world.dir, 'restore'); const r = ag.restore(id, t); expect(r.code, r.out).toBe(0);
  expect(compare(v2, manifest(restoredPath(t, src)))).toEqual([]);
});

test('N6b the set folder on the computer is full after the server committed → server and computer tell the truth → next run correct → restore identical (SHA-256)', async ({ world, evidence }) => {
  world.addCustomer('qa-n6b');
  const ag = world.agent('qa-n6b', 'N6B-PC'); ag.register();
  const src = path.join(world.dir, 'data'); const v1 = goldenDataset(src);
  const id = ag.addSet('Files', [src]);
  const setDir = path.join(ag.home, 'sets', id);
  fs.mkdirSync(setDir, { recursive: true });
  const keepAside = path.join(world.dir, 'setdir-before'); fs.cpSync(setDir, keepAside, { recursive: true });
  if (!tmpfs(setDir, 1)) test.skip(true, 'NOT TESTED: this machine does not allow mounting a small disk');
  const ev: Record<string, unknown> = {};
  try {
    fs.cpSync(keepAside, setDir, { recursive: true });
    const filler = fill(setDir);
    fs.truncateSync(filler, Math.max(0, fs.statSync(filler).size - 12 * 1024));   // ~12 KB left: the small notes fit, the index (~30 KB) does not
    ev.df = df(setDir);
    evidence.step('PROOF: the set folder has ~12 KB free: ' + ev.df + ' / 64 KB written there: ' + sh('head -c 65536 /dev/zero > "' + setDir + '/probe.bin"'));
    try { fs.rmSync(path.join(setDir, 'probe.bin')); } catch { }
    const b1 = ag.backup(id);
    ev.backup1 = b1.out;
    evidence.step('backup 1: exit ' + b1.code + '\n' + b1.out.trim());
    const runs = await setRuns(world, 'qa-n6b', id);
    ev.serverRuns = runs;
    evidence.step('server runs: ' + JSON.stringify(runs.map((r) => [r.job, r.result])));
    ev.points = points(ag, id);
    evidence.step('points: ' + JSON.stringify(ev.points) + '; set folder: ' + fs.readdirSync(setDir).join(','));
    // what is certainly required: no "success" on the computer for a run whose server record says otherwise, and
    // the other way round — the two must not contradict each other
    const agentOk = b1.code === 0 && /^BS_STOP_SUCCESS/.test(result(b1.out));
    const serverOk = runs.length > 0 && runs[0].status !== 'bad';
    ev.agentOk = agentOk; ev.serverOk = serverOk;
    expect(runs.length, 'one record of the run').toBe(1);
    // soft: the recovery below is checked too (NEEDS OWNER DECISION which of the two outcomes is the right one)
    expect.soft({ agentOk, serverOk }, 'the computer and the server must tell the same outcome of one run').toEqual({ agentOk: serverOk, serverOk });
  } finally {
    unmount(setDir);
    fs.cpSync(keepAside, setDir, { recursive: true });    // the set folder as it was before the mount (the index was never saved)
    keep('n6b', ev);
  }
  evidence.step('RECOVERY: room again; backup 2 and restore');
  fs.writeFileSync(path.join(src, 'Documents/after-full.txt'), 'after the set folder was full\n');
  const v2 = manifest(src);
  const b2 = ag.backup(id); expect(b2.out, b2.out).toMatch(/^BS_STOP_SUCCESS /m);
  evidence.step('backup 2: ' + b2.out.trim().split('\n').slice(0, 4).join(' | '));
  const t = path.join(world.dir, 'restore'); const r = ag.restore(id, t); expect(r.code, r.out).toBe(0);
  expect(compare(v2, manifest(restoredPath(t, src)))).toEqual([]);
  const pts = points(ag, id);
  const t1 = path.join(world.dir, 'restore-1'); const r1 = ag.restore(id, t1, ['--point', pts[0]]); expect(r1.code, r1.out).toBe(0);
  expect(compare(v1, manifest(restoredPath(t1, src))), 'the run made while the computer\'s disk was full is a complete point').toEqual([]);
});
