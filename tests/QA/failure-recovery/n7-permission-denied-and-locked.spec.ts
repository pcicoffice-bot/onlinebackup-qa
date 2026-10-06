// N7 — Parts of the source cannot be read on the second backup:
//   a folder with no permission (chmod 000 "Many", 150 files), a single file with no permission (chmod 000, changed
//   since backup 1), and a file held under an exclusive lock by another program (flock -x: on Linux an advisory lock,
//   which .NET honours when it opens a file — NOT the same mechanism as a Windows sharing lock, which is NOT TESTED here).
// The agent runs as root on this machine, so its DAC override is dropped (setpriv bounding set) — the permission bits
// then apply to it as to any user. The faults are proven from outside: stat (the bits), the same command line without
// the override failing to read, /proc/locks and `flock -n -s` failing.
// Contract BK-05: "partly unreadable = success with error (red) … last good backup kept and still restorable …
// an err line naming that file, no deletion sent for it; after the lock is released the next run is BS_STOP_SUCCESS".
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import { agentAsync, backupAsync, result, setRuns, points, sh, NO_DAC, keep } from '../lib/nfault';
import { spawn } from 'child_process';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';

test('N7 unreadable folder, unreadable file, exclusively locked file → success WITH ERROR naming each, nothing deleted, old versions kept → fault removed → BS_STOP_SUCCESS → restore identical (SHA-256)', async ({ world, evidence }) => {
  world.addCustomer('qa-n7');
  const ag = world.agent('qa-n7', 'N7-PC'); ag.register();
  const src = path.join(world.dir, 'data'); const v1 = goldenDataset(src);
  fs.chmodSync(world.dir, 0o755);                                   // mkdtemp makes it 700 (owner root: fine for the agent)
  const id = ag.addSet('Files', [src]);
  const opts = { wrap: NO_DAC };
  const b1 = await backupAsync(ag, id, opts).done; expect(b1.out, b1.out).toMatch(/^BS_STOP_SUCCESS /m);
  const p1 = points(ag, id)[0];

  const letter = path.join(src, 'Documents/letter.txt'), many = path.join(src, 'Many'), locked = path.join(src, 'Binary/random.bin');
  fs.writeFileSync(letter, 'changed after backup 1, then made unreadable\n');
  fs.writeFileSync(path.join(src, 'Documents/new-in-run-2.txt'), 'new in run 2\n');
  fs.writeFileSync(locked, crypto.randomBytes(64 * 1024));         // changed since backup 1: run 2 must open it
  const vNow = manifest(src);
  fs.chmodSync(letter, 0o000); fs.chmodSync(many, 0o000);
  const holder = spawn('flock', ['-x', locked, 'sleep', '600'], { stdio: 'ignore', detached: true });   // its own group: the sleep holds the lock too
  const ev: Record<string, unknown> = {};
  try {
    await new Promise((r) => setTimeout(r, 500));
    ev.proof = {
      modes: sh('stat -c "%a %U %n" "' + letter + '" "' + many + '"'),
      readFile: sh(NO_DAC.join(' ') + ' cat "' + letter + '"'),
      listFolder: sh(NO_DAC.join(' ') + ' ls "' + many + '"'),
      lockHolder: sh('cat /proc/locks | grep -c FLOCK'), lockTest: sh('flock -n -s "' + locked + '" true && echo FREE || echo HELD'),
    };
    evidence.step('PROOF of the faults: ' + JSON.stringify(ev.proof));
    expect((ev.proof as any).modes).toMatch(/^0 /m);
    expect((ev.proof as any).readFile).toMatch(/Permission denied/);
    expect((ev.proof as any).listFolder).toMatch(/Permission denied/);
    expect((ev.proof as any).lockTest).toMatch(/HELD/);

    const b2 = await backupAsync(ag, id, opts).done;
    ev.backup2 = b2.out;
    evidence.step('backup 2: exit ' + b2.code + '\n' + b2.out.trim());
    expect(result(b2.out), 'partly unreadable = success with error (BK-05)').toBe('BS_STOP_SUCCESS_WITH_ERROR');
    expect(b2.out, 'the unreadable file is named').toMatch(/letter\.txt/);
    expect(b2.out, 'the unreadable folder is named').toMatch(/Many/);
    // a Linux flock is advisory: whether .NET's open (FileShare.ReadWrite) is refused by it is the runtime's choice —
    // either the file is read whole (new version in the point) or it is named as an error (old version kept)
    const lockNamed = /random\.bin/.test(b2.out); ev.lockNamed = lockNamed;
    evidence.step('the locked file named as not read: ' + lockNamed);
    expect(b2.out, 'nothing deleted for what could not be read').toMatch(/ del=0 /);
    const runs = await setRuns(world, 'qa-n7', id);
    expect(runs[0].result, 'the server records the same result').toBe('BS_STOP_SUCCESS_WITH_ERROR');
    expect(runs[0].status, 'shown red').toBe('bad');

    evidence.step('ORACLE during the fault: the newest point keeps the last good version of what could not be read');
    const expected = new Map(v1); expected.set('Documents/new-in-run-2.txt', vNow.get('Documents/new-in-run-2.txt')!);
    if (!lockNamed) expected.set('Binary/random.bin', vNow.get('Binary/random.bin')!);
    const tn = path.join(world.dir, 'restore-during'); const rn = agentAsync(ag, ['restore', '--set', id, '--password', 'Customer-Pass-1', '--target', tn]);
    const rr = await rn.done; expect(rr.code, rr.out).toBe(0);
    expect(compare(expected, manifest(restoredPath(tn, src))), 'newest point = backup 1 + the new file (unreadable ones kept as they were)').toEqual([]);
  } finally {
    try { process.kill(-holder.pid!, 'SIGKILL'); } catch { }
    await new Promise((r) => setTimeout(r, 500));
    ev.lockAfterRelease = sh('flock -n -s "' + locked + '" true && echo FREE || echo HELD');
    try { fs.chmodSync(letter, 0o644); fs.chmodSync(many, 0o755); } catch { }
    keep('n7', ev);
  }

  evidence.step('RECOVERY: permissions back, lock released → backup 3');
  const b3 = await backupAsync(ag, id, opts).done;
  evidence.step('backup 3: ' + b3.out.trim());
  expect(result(b3.out), b3.out).toBe('BS_STOP_SUCCESS');
  expect(b3.out, 'the file changed during the fault is sent now').toMatch(/upd=[1-9]/);
  const t = path.join(world.dir, 'restore'); const r = ag.restore(id, t); expect(r.code, r.out).toBe(0);
  expect(compare(vNow, manifest(restoredPath(t, src)))).toEqual([]);
  const t1 = path.join(world.dir, 'restore-1'); const r1 = ag.restore(id, t1, ['--point', p1]); expect(r1.code, r1.out).toBe(0);
  expect(compare(v1, manifest(restoredPath(t1, src)))).toEqual([]);
});
