// N1 — The line between the computer and the server breaks in the middle of an upload:
//   (a) for 5 seconds (shorter than the agent's retries: Client.Call 4 tries ~10 s, an object 3 tries with 2 s + 8 s waits)
//   (b) for longer than every retry (the line stays down until the agent gives up)
// The fault is injected by runner/nproxy.mjs; its log (sockets destroyed, connections refused while down) is the proof.
// Contract AG-05: "the run resumes or ends as failed with a clear reason; a repeated request is not counted twice; the
// next run completes and restores identical". Whether (a) must resume is not written → both outcomes are accepted, but
// a run that SAYS success must have stored everything (its point restores identical), and one that failed must be
// recorded by the server as not ok.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import { Line, backupAsync, result, setRuns, points, until, sleep, keep } from '../lib/nfault';
import * as fs from 'fs';
import * as path from 'path';

test('N1a line down 5 s mid-upload → proven by the proxy log → run resumes or fails truthfully → next backup + restore identical (SHA-256)', async ({ world, evidence }) => {
  const line = await new Line(world).start();
  try {
    world.addCustomer('qa-n1a');
    const ag = world.agent('qa-n1a', 'N1A-PC'); ag.register(line.url);
    const src = path.join(world.dir, 'data'); const v1 = goldenDataset(src);
    const id = ag.addSet('Files', [src]);
    await world.setBandwidth('qa-n1a', id, 1000);                  // ~24 MB at 1 MB/s: ~25 s of upload
    const run = backupAsync(ag, id);
    expect(await until(async () => (await line.stats()).c2s > 6 * 1024 * 1024, 120000), 'the upload is under way (6 MB through the line)').toBe(true);
    evidence.step('CUT the line for 5 s');
    const cut = await line.ctl('/cut');
    const tCut = Date.now();
    await sleep(5000);
    const during = await line.stats();
    const alive = run.p.exitCode === null;
    await line.ctl('/back');
    evidence.step('line back after ' + (Date.now() - tCut) + ' ms; agent still running during the break: ' + alive);
    // PROOF of the fault, from the proxy: sockets destroyed at the cut; the agent's tries refused while it was down
    expect(cut.cutSockets, 'the cut destroyed the open connections').toBeGreaterThan(0);
    evidence.step('proxy: sockets destroyed ' + cut.cutSockets + ', connections refused during the break ' + during.refused + ', bytes up before the cut ' + cut.c2s);
    const r1 = await run.done;
    evidence.step('run 1: exit ' + r1.code + ' ' + result(r1.out) + ' in ' + r1.ms + ' ms\n' + r1.out.trim().split('\n').slice(0, 8).join('\n'));
    const runs1 = await setRuns(world, 'qa-n1a', id);
    evidence.step('server runs: ' + JSON.stringify(runs1.map((r) => [r.job, r.result, r.new])));
    if (r1.code === 0) {
      evidence.step('the run resumed: its point must hold every file');
      expect(result(r1.out)).toMatch(/^BS_STOP_SUCCESS/);
      expect(runs1.length, 'exactly one run recorded').toBe(1);
      expect(runs1[0].result, 'the server records the same result').toBe(result(r1.out));
      const t1 = path.join(world.dir, 'restore-1'); const rr = ag.restore(id, t1); expect(rr.code, rr.out).toBe(0);
      expect(compare(v1, manifest(restoredPath(t1, src))), 'a run reported successful must restore identical').toEqual([]);
    } else {
      expect(result(r1.out), 'a failed run never says success').not.toMatch(/^BS_STOP_SUCCESS/);
    }

    evidence.step('RECOVERY: files change, a new backup with the line healthy');
    await world.setBandwidth('qa-n1a', id, 0);
    fs.writeFileSync(path.join(src, 'Documents/after-break.txt'), 'written after the break\n');
    const v2 = manifest(src);
    const b2 = ag.backup(id);
    expect(b2.out, b2.out).toMatch(/^BS_STOP_SUCCESS /m);
    const runs2 = await setRuns(world, 'qa-n1a', id);
    if (r1.code !== 0) expect(runs2.some((r) => r.status === 'bad'), 'the broken run is in the history as failed: ' + JSON.stringify(runs2)).toBe(true);
    const jobs = runs2.map((r) => r.job); expect(new Set(jobs).size, 'no run recorded twice: ' + jobs.join(',')).toBe(jobs.length);
    evidence.step('points: ' + points(ag, id).join(','));
    const t2 = path.join(world.dir, 'restore-2'); const r2 = ag.restore(id, t2); expect(r2.code, r2.out).toBe(0);
    expect(compare(v2, manifest(restoredPath(t2, src)))).toEqual([]);
    keep('n1a', { r1: { code: r1.code, result: result(r1.out), ms: r1.ms }, cut, during, proxy: line.events() });
  } finally { line.stop(); }
});

test('N1b line down longer than every retry → the agent gives up (time measured) → no false success, server not left locked → next backup + restore identical', async ({ world, evidence }) => {
  const line = await new Line(world).start();
  try {
    world.addCustomer('qa-n1b');
    const ag = world.agent('qa-n1b', 'N1B-PC'); ag.register(line.url);
    const src = path.join(world.dir, 'data'); const v1 = goldenDataset(src);
    const id = ag.addSet('Files', [src]);
    await world.setBandwidth('qa-n1b', id, 1000);
    const run = backupAsync(ag, id);
    expect(await until(async () => (await line.stats()).c2s > 6 * 1024 * 1024, 120000), 'the upload is under way').toBe(true);
    evidence.step('CUT the line, and keep it down');
    const cut = await line.ctl('/cut'); const tCut = Date.now();
    expect(cut.cutSockets).toBeGreaterThan(0);
    const gaveUp = await Promise.race([run.done, sleep(240000).then(() => null)]);
    const down = await line.stats();
    expect(gaveUp, 'the agent ends by itself within 4 minutes of a dead line (it does not hang)').not.toBeNull();
    const r1 = gaveUp!;
    const tolerated = Date.now() - tCut;
    evidence.step('the agent gave up ' + tolerated + ' ms after the cut; connections it tried while down (refused by the proxy): ' + down.refused);
    expect(down.refused, 'the agent really tried again while the line was down (proxy log)').toBeGreaterThan(0);
    expect(r1.code, 'not reported successful').not.toBe(0);
    expect(result(r1.out)).not.toMatch(/^BS_STOP_SUCCESS/);
    evidence.step('run 1: ' + result(r1.out) + '\n' + r1.out.trim().split('\n').slice(0, 6).join('\n'));
    await line.ctl('/back');
    evidence.step('server view right after the line is back (the abort never reached it): live=' + JSON.stringify(await world.live()));

    evidence.step('RECOVERY: a new backup (not refused as "already running")');
    await world.setBandwidth('qa-n1b', id, 0);
    const b2 = ag.backup(id);
    expect(b2.out, b2.out).toMatch(/^BS_STOP_SUCCESS /m);
    expect(await world.live()).not.toContain(id);
    const runs = await setRuns(world, 'qa-n1b', id);
    evidence.step('server runs: ' + JSON.stringify(runs.map((r) => [r.job, r.result])));
    expect(runs.filter((r) => r.status === 'bad').length, 'the cut run is recorded as failed').toBeGreaterThan(0);
    expect(runs.filter((r) => r.status === 'ok').length, 'exactly one success').toBe(1);
    const jobs = runs.map((r) => r.job); expect(new Set(jobs).size, 'no run recorded twice: ' + jobs.join(',')).toBe(jobs.length);
    const t = path.join(world.dir, 'restore'); const rr = ag.restore(id, t); expect(rr.code, rr.out).toBe(0);
    expect(compare(v1, manifest(restoredPath(t, src)))).toEqual([]);
    keep('n1b', { toleratedMs: tolerated, r1: result(r1.out), proxy: line.events() });
  } finally { line.stop(); }
});
