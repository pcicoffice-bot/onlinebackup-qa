// N3 — A slow line: every connection between the computer and the server throttled to 50 KB/s in each direction by the
// proxy (runner/nproxy.mjs), for a whole first backup of ~9.5 MB — one object of 6 MB takes ~2 minutes in one request
// (the agent's request time limit is 300 s per read/write, Client.TimeoutMs).
// Expected (BK-01 / AG-05): only slower — the run succeeds and its point restores identical (SHA-256); no object is
// cut by a time limit and stored half.
// The fault is proven by the proxy (the bytes that went through while throttled) and by the duration (≥ bytes / rate).
import { test, expect } from '../lib/fixtures';
import { manifest, compare, restoredPath } from '../lib/world';
import { bigFiles } from '../lib/fault';
import { Line, backupAsync, result, setRuns, smallGolden, keep } from '../lib/nfault';
import * as fs from 'fs';
import * as path from 'path';

test('N3 line throttled to 50 KB/s for a whole backup → succeeds, only slower (duration recorded) → new backup → restore identical (SHA-256)', async ({ world, evidence }) => {
  test.setTimeout(20 * 60 * 1000);
  const RATE = 50 * 1024;
  const line = await new Line(world).start();
  try {
    world.addCustomer('qa-n3');
    const ag = world.agent('qa-n3', 'N3-PC'); ag.register(line.url);
    const src = path.join(world.dir, 'data'); smallGolden(src); bigFiles(src, 1, 6);
    const v1 = manifest(src);
    const id = ag.addSet('Files', [src]);
    await line.ctl('/rate?bps=' + RATE);
    evidence.step('backup 1 through a 50 KB/s line');
    const run = backupAsync(ag, id); const r1 = await run.done;
    const st = await line.stats();
    evidence.step('backup 1: ' + result(r1.out) + ' in ' + r1.ms + ' ms; bytes up through the throttled line ' + st.c2sWhileThrottled + '\n' + r1.out.trim());
    // PROOF of the fault: what went up could not have gone faster than the rate
    expect(st.c2sWhileThrottled, 'the backup went through the throttled line').toBeGreaterThan(8.5 * 1024 * 1024);
    expect(r1.ms, 'the duration shows the throttle (bytes / rate)').toBeGreaterThan(st.c2sWhileThrottled / RATE * 1000 * 0.9);
    expect(r1.out, 'a slow line is not a failure').toMatch(/^BS_STOP_SUCCESS /m);
    const runs = await setRuns(world, 'qa-n3', id);
    expect(runs.length).toBe(1); expect(runs[0].status).toBe('ok');

    evidence.step('the line is fast again; a change; backup 2');
    await line.ctl('/rate?bps=0');
    fs.writeFileSync(path.join(src, 'Documents/after-slow.txt'), 'after the slow line\n');
    const v2 = manifest(src);
    const t0 = Date.now(); const b2 = ag.backup(id); const ms2 = Date.now() - t0;
    expect(b2.out, b2.out).toMatch(/^BS_STOP_SUCCESS /m);
    evidence.step('ORACLE: point 1 and point 2 restore identical');
    const pts = ag.cli(['points', '--set', id]).out.trim().split('\n').filter((l) => /^\d+/.test(l)).map((l) => l.trim());
    const t1 = path.join(world.dir, 'restore-1'); const rr1 = ag.restore(id, t1, ['--point', pts[0]]); expect(rr1.code, rr1.out).toBe(0);
    expect(compare(v1, manifest(restoredPath(t1, src)))).toEqual([]);
    const t2 = path.join(world.dir, 'restore-2'); const rr2 = ag.restore(id, t2); expect(rr2.code, rr2.out).toBe(0);
    expect(compare(v2, manifest(restoredPath(t2, src)))).toEqual([]);
    keep('n3', { rateBps: RATE, backup1Ms: r1.ms, bytesUpThrottled: st.c2sWhileThrottled, backup2Ms: ms2, result1: result(r1.out), proxy: line.events() });
  } finally { line.stop(); }
});
