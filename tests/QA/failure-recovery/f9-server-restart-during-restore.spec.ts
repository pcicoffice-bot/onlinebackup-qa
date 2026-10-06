// F9 — The backup SERVER is killed and started again while a customer's RESTORE is running.
// Expected: the restore either fails clearly (exit code, "failed=" > 0) or resumes and finishes — but it never says
// success unless every file is identical; no half file is left under a temporary name or passes as whole under its real
// name; and simply restoring again into the same folder (no overwrite) gives every file identical (SHA-256).
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import { restoreProcess, bigFiles, notWhole, leftovers, fileCount, restoreRuns } from '../lib/fault';
import * as path from 'path';

test('F9 server killed and restarted during a restore → clear failure or full resume, never a false success → restore again identical', async ({ world, evidence }) => {
  world.addCustomer('qa-f9');
  const ag = world.agent('qa-f9', 'F9-PC'); ag.register();
  const src = path.join(world.dir, 'data'); goldenDataset(src);
  bigFiles(src, 4, 40);                                           // 160 MB of random data: the restore takes long enough
  const data = manifest(src);
  const id = ag.addSet('Files', [src]);
  evidence.step('backup');
  const b = ag.backup(id); expect(b.out, b.out).toMatch(/^BS_STOP_SUCCESS /m);

  const target = path.join(world.dir, 'restore');
  evidence.step('restore as its own process');
  const r = restoreProcess(ag, id, target);
  await expect.poll(() => fileCount(target), { timeout: 120000, intervals: [50] }).toBeGreaterThan(3);
  const before = fileCount(target);
  expect(r.p.exitCode, 'the fault is injected while the restore runs (Agent L: not after it ended)').toBeNull();
  evidence.step('KILL the server with ' + before + ' files written; 20 s later it starts again');
  world.killServer();
  await new Promise((res) => setTimeout(res, 20000));
  await world.startServer();
  const r1 = await r.done;
  evidence.step('restore ended: exit ' + r1.code + ' · ' + r1.out.trim().split('\n').pop());
  const got = manifest(restoredPath(target, src));
  if (r1.code === 0) {
    evidence.step('the restore says success: then every file must be there and identical');
    expect(r1.out).toMatch(/failed=0/);
    expect(compare(data, got), 'a restore that said success:\n' + r1.out).toEqual([]);
  } else {
    evidence.step('the restore says failed: what it wrote must be whole');
    expect(r1.out, 'the failure is said with counts, not a crash').toMatch(/failed=[1-9]\d*|No connection|download failed/i);
    expect(r1.out).not.toMatch(/Unhandled exception/i);
  }
  expect(leftovers(manifest(target)), 'temporary half files left in the customer\'s folder').toEqual([]);
  expect(notWhole(data, got), 'files present under their real name but not identical').toEqual([]);

  evidence.step('RECOVERY: restore again into the same folder (no overwrite) → identical');
  const r2 = ag.restore(id, target);
  expect(r2.code, r2.out).toBe(0);
  expect(compare(data, manifest(restoredPath(target, src)))).toEqual([]);
  expect(leftovers(manifest(target))).toEqual([]);

  if (r1.code !== 0) {
    evidence.step('the interrupted restore is in the server\'s history as failed (the technician must see it)');
    const rr = restoreRuns(await world.runs('qa-f9'));
    evidence.step('restore records on the server: ' + JSON.stringify(rr.map((x) => x.result)));
    expect(rr.filter((x) => x.status === 'bad').length, 'the restore that failed (' + r1.out.trim() + ') has no failed record on the server; records: ' + JSON.stringify(rr.map((x) => x.result))).toBeGreaterThan(0);
  }
});
