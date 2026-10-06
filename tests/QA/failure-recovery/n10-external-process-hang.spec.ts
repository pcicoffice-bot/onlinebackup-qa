// N10 — An external program the backup waits for never ends:
//   (a) a pre-command that sleeps "for ever"; the product's limit is Limits.Command = 1 hour (src/Core/ProcessRunner.cs),
//       with no setting or environment variable to shorten it. The REAL limit is tested with the agent's clock sped up
//       120× by libfaketime (only the agent process; the hung command itself is started outside faketime, so it really
//       hangs): the product's own deadline (DateTime.UtcNow + limit) passes after ~30 s of real time.
//   (b) the agent is killed while its pre-command hangs: what is left behind (the hung command's process), and what the
//       server shows during the hang — observed, not asserted (not written down).
//   (c) a restic that hangs: a stand-in "restic" whose backup sleeps without a word (every other command is the real
//       restic); the product stops a silent restic backup after Limits.ResticIdle = 30 min — also reached with the clock
//       sped up 120× (~15 s).
// The fault is proven from outside: the hung process (pgrep on its unique argument) alive during the run, its state.
// Contracts: BK-09 "a stuck command ends at the limit"; AP-07 "stopped at its limit with its children; never a hung
// backup". Certainly required after it: nothing left running, no false success, next backup + restore identical.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import { hasProgram } from '../lib/fault';
import { backupAsync, result, setRuns, editSet, xmlAttr, sh, until, keep } from '../lib/nfault';
import * as fs from 'fs';
import * as path from 'path';

const FAST = { wrap: ['faketime', '-f', '+0 x120'], env: { FAKETIME_DONT_FAKE_MONOTONIC: '1' } };
const hung = (tag: string) => sh('pgrep -f "slee[p] ' + tag + '" || true');
const preCmd = (cmd: string) => (xml: string) => xml.replace(/<\/BACKUP_SET>\s*$/, '<PRE_CMD ID="9001" NAME="pre1" PATH="' + xmlAttr(cmd) + '" WORKING_DIR="" STOP_ON_FAILURE="N" /></BACKUP_SET>');
const noPreCmd = (xml: string) => xml.replace(/<PRE_CMD [^>]*\/>/g, '');

test('N10a pre-command that never ends → stopped at the real 1-hour limit (agent clock 120×) with its child → backup completes → restore identical (SHA-256)', async ({ world, evidence }) => {
  test.skip(!hasProgram('faketime'), 'NOT TESTED: libfaketime is not installed (the 1-hour limit cannot be shortened by a setting)');
  world.addCustomer('qa-n10a');
  const ag = world.agent('qa-n10a', 'N10A-PC'); ag.register();
  const src = path.join(world.dir, 'data'); const v1 = goldenDataset(src);
  const id = ag.addSet('Files', [src]);
  await editSet(world, 'qa-n10a', id, preCmd('exec env -u LD_PRELOAD -u FAKETIME sleep 987651'));
  const ev: Record<string, unknown> = {};
  const run = backupAsync(ag, id, FAST);
  try {
    expect(await until(() => hung('987651') !== '', 30000), 'PROOF: the pre-command is running').toBe(true);
    const pid = hung('987651').split('\n')[0];
    ev.hungState = sh('ps -o pid,stat,etimes,args -p ' + pid);
    evidence.step('the hung pre-command: ' + ev.hungState);
    ev.liveDuringHang = await world.live();
    const r = await Promise.race([run.done, new Promise<null>((res) => setTimeout(() => res(null), 5 * 60 * 1000))]);
    expect(r, 'the run ended by itself (the limit is real), within 5 minutes of real time at 120×').not.toBeNull();
    ev.out = r!.out; ev.ms = r!.ms;
    evidence.step('run: exit ' + r!.code + ' in ' + r!.ms + ' ms real\n' + r!.out.trim());
    expect(hung('987651'), 'the hung pre-command was stopped (no process left)').toBe('');
    expect(r!.out, 'the log says the pre-command was stopped at its limit').toMatch(/pre-command timed out after 1 h/);
    // BK-09: a failing pre-command stops the backup only when the set says so (STOP_ON_FAILURE="N" here): data is stored
    expect(result(r!.out), 'the backup went on and is not a plain success').toBe('BS_STOP_SUCCESS_WITH_WARNING');
    const runs = await setRuns(world, 'qa-n10a', id);
    expect(runs.length).toBe(1); expect(runs[0].result).toBe('BS_STOP_SUCCESS_WITH_WARNING');
  } finally { run.p.kill('SIGKILL'); sh('pkill -f "slee[p] 987651"'); keep('n10a', ev); }

  evidence.step('RECOVERY: the pre-command removed; a new backup; restore');
  await editSet(world, 'qa-n10a', id, noPreCmd);
  fs.writeFileSync(path.join(src, 'Documents/after-hang.txt'), 'after the hang\n');
  const v2 = manifest(src);
  const b2 = ag.backup(id); expect(b2.out, b2.out).toMatch(/^BS_STOP_SUCCESS /m);
  const t = path.join(world.dir, 'restore'); const rr = ag.restore(id, t); expect(rr.code, rr.out).toBe(0);
  expect(compare(v2, manifest(restoredPath(t, src)))).toEqual([]);
  const pts = ag.cli(['points', '--set', id]).out.trim().split('\n').filter((l) => /^\d+/.test(l)).map((l) => l.trim());
  const t1 = path.join(world.dir, 'restore-1'); const r1 = ag.restore(id, t1, ['--point', pts[0]]); expect(r1.code, r1.out).toBe(0);
  expect(compare(v1, manifest(restoredPath(t1, src)))).toEqual([]);
});

test('N10b agent killed while its pre-command hangs (real clock) → observed: what the server shows during the hang, what is left running → next backup + restore identical (SHA-256)', async ({ world, evidence }) => {
  world.addCustomer('qa-n10b');
  const ag = world.agent('qa-n10b', 'N10B-PC'); ag.register();
  const src = path.join(world.dir, 'data'); const v1 = goldenDataset(src);
  const id = ag.addSet('Files', [src]);
  await editSet(world, 'qa-n10b', id, preCmd('sleep 987652'));
  const ev: Record<string, unknown> = {};
  const run = backupAsync(ag, id);
  try {
    expect(await until(() => hung('987652') !== '', 30000), 'PROOF: the pre-command is running').toBe(true);
    await new Promise((r) => setTimeout(r, 70000));          // more than one heartbeat period (60 s)
    ev.after70s = { hung: sh('ps -o pid,ppid,stat,etimes,args -p ' + hung('987652').split('\n')[0]), live: await world.live(), runs: await setRuns(world, 'qa-n10b', id) };
    evidence.step('70 s into the hang: ' + JSON.stringify(ev.after70s));
    evidence.step('KILL the agent (a crash / a forced stop of the service)');
    run.p.kill('SIGKILL'); await run.done;
    await new Promise((r) => setTimeout(r, 2000));
    ev.leftAfterKill = hung('987652');
    ev.leftState = ev.leftAfterKill ? sh('ps -o pid,ppid,stat,etimes,args -p ' + String(ev.leftAfterKill).split('\n')[0]) : '';
    evidence.step('left running after the agent was killed: ' + (ev.leftState || '(nothing)'));
  } finally { sh('pkill -f "slee[p] 987652"'); keep('n10b', ev); }
  await editSet(world, 'qa-n10b', id, noPreCmd);
  const b2 = ag.backup(id); expect(b2.out, b2.out).toMatch(/^BS_STOP_SUCCESS /m);
  const t = path.join(world.dir, 'restore'); const rr = ag.restore(id, t); expect(rr.code, rr.out).toBe(0);
  expect(compare(v1, manifest(restoredPath(t, src)))).toEqual([]);
});

test('N10c restic hangs silently during the backup → stopped at the real 30-min idle limit (agent clock 120×), nothing left running, no false success → real restic → backup → restore identical (SHA-256)', async ({ world, evidence }) => {
  test.skip(!hasProgram('faketime'), 'NOT TESTED: libfaketime is not installed');
  const realRestic = process.env.OB_RESTIC || '';
  test.skip(!realRestic || !fs.existsSync(realRestic), 'NOT TESTED: no restic program (OB_RESTIC)');
  world.addCustomer('qa-n10c');
  const ag = world.agent('qa-n10c', 'N10C-PC'); ag.register();
  const src = path.join(world.dir, 'data'); const v1 = goldenDataset(src);
  const id = ag.addSet('Files', [src], ['--engine', 'RESTIC']);
  const standIn = path.join(world.dir, 'restic-hangs');
  fs.writeFileSync(standIn, '#!/bin/sh\nif [ "$1" = "backup" ]; then exec env -u LD_PRELOAD -u FAKETIME sleep 987653; fi\nexec "' + realRestic + '" "$@"\n', { mode: 0o755 });
  const ev: Record<string, unknown> = {};
  const run = backupAsync(ag, id, { wrap: FAST.wrap, env: { ...FAST.env, OB_RESTIC: standIn } });
  try {
    expect(await until(() => hung('987653') !== '', 60000), 'PROOF: the stand-in restic backup is hanging').toBe(true);
    ev.hungState = sh('ps -o pid,ppid,stat,etimes,args -p ' + hung('987653').split('\n')[0]);
    evidence.step('the hung restic: ' + ev.hungState);
    const r = await Promise.race([run.done, new Promise<null>((res) => setTimeout(() => res(null), 5 * 60 * 1000))]);
    expect(r, 'the run ended by itself (the idle limit is real)').not.toBeNull();
    ev.out = r!.out; ev.ms = r!.ms;
    evidence.step('run: exit ' + r!.code + ' in ' + r!.ms + ' ms real\n' + r!.out.trim());
    expect(hung('987653'), 'the hung restic was stopped').toBe('');
    expect(r!.code, 'not a success').not.toBe(0);
    expect(result(r!.out)).not.toMatch(/^BS_STOP_SUCCESS/);
    expect(r!.out, 'the reason names the hang').toMatch(/restic stopped answering for 30 min/);
    const runs = await setRuns(world, 'qa-n10c', id);
    ev.runs = runs;
    expect(runs.length, 'the server has the record').toBe(1);
    expect(runs[0].status).toBe('bad');
  } finally { run.p.kill('SIGKILL'); sh('pkill -f "slee[p] 987653"'); keep('n10c', ev); }

  evidence.step('RECOVERY: the real restic; backup; restore');
  const b2 = ag.backup(id); expect(b2.out, b2.out).toMatch(/^BS_STOP_SUCCESS /m);
  const t = path.join(world.dir, 'restore'); const rr = ag.restore(id, t); expect(rr.code, rr.out).toBe(0);
  expect(compare(v1, manifest(restoredPath(t, src)))).toEqual([]);
  expect(await world.live()).not.toContain(id);
});
