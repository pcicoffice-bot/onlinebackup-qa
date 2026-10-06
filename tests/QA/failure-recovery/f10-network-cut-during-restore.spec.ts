// F10 — The customer's internet line is cut in the middle of a RESTORE for 30 seconds, then comes back.
// Expected: the files that could not be fetched are reported (exit code, "failed=" > 0) — never a plain success with
// files missing; nothing half-written is left (temporary names, or real names that are not identical); and restoring
// again into the same folder (no overwrite) gives every file identical (SHA-256). The server's record is not "OK".
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath, Net } from '../lib/world';
import { restoreProcess, bigFiles, notWhole, leftovers, fileCount, restoreRuns } from '../lib/fault';
import * as path from 'path';

test('F10 network cut during a restore → missing files reported, nothing half-written → line back → restore again identical', async ({ world, evidence }) => {
  const line = await new Net(world).start();
  try {
    world.addCustomer('qa-f10');
    const ag = world.agent('qa-f10', 'F10-PC'); ag.register(line.url);
    const src = path.join(world.dir, 'data'); goldenDataset(src);
    bigFiles(src, 4, 40);
    const data = manifest(src);
    const id = ag.addSet('Files', [src]);
    evidence.step('backup through the line');
    const b = ag.backup(id); expect(b.out, b.out).toMatch(/^BS_STOP_SUCCESS /m);

    const target = path.join(world.dir, 'restore');
    evidence.step('restore as its own process');
    const r = restoreProcess(ag, id, target);
    await expect.poll(() => fileCount(target), { timeout: 120000, intervals: [50] }).toBeGreaterThan(3);
    evidence.step('CUT the line with ' + fileCount(target) + ' files written, for 30 s');
    line.cut();
    await new Promise((res) => setTimeout(res, 30000));
    line.restore();
    evidence.step('the line is back');
    const r1 = await r.done;
    evidence.step('restore ended: exit ' + r1.code + ' · ' + r1.out.trim().split('\n').pop());
    const got = manifest(restoredPath(target, src));
    const missing = compare(data, got).filter((d) => d.startsWith('MISSING'));
    if (missing.length > 0) {
      expect(r1.code, 'files are missing after the restore, it must not end as a success:\n' + missing.join('\n') + '\n' + r1.out).not.toBe(0);
      expect(r1.out).toMatch(/failed=[1-9]/);
      const rr = restoreRuns(await world.runs('qa-f10'));
      evidence.step('server records: ' + JSON.stringify(rr.map((x) => x.status)));
      if (rr.length > 0) expect(rr[0].status, 'the server\'s record of a restore with failed files').not.toBe('ok');
    } else expect(r1.code, r1.out).toBe(0);
    expect(leftovers(manifest(target)), 'temporary half files left in the customer\'s folder').toEqual([]);
    expect(notWhole(data, got), 'files present under their real name but not identical').toEqual([]);

    evidence.step('RECOVERY: restore again into the same folder (no overwrite) → identical');
    const r2 = ag.restore(id, target);
    expect(r2.code, r2.out).toBe(0);
    expect(compare(data, manifest(restoredPath(target, src)))).toEqual([]);
    expect(leftovers(manifest(target))).toEqual([]);
  } finally { line.stop(); }
});
