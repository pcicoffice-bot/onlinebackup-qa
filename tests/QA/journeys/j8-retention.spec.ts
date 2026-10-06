// J8 — Retention on the real server: a set that keeps the last 2 backups, 4 backups with changes, the server's
// maintenance runs. Only the 2 newest points stay, each restores exactly what it held; the deleted points are gone.
// Oracle: manifests made at each backup, compared with SHA-256 against each restore.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare } from '../lib/world';
import * as fs from 'fs';
import * as path from 'path';

test('J8 retention keeps exactly the policy\'s points, and each kept point restores identical', async ({ world, evidence }) => {
  world.addCustomer('qa-ret');
  const ag = world.agent('qa-ret', 'RET-PC1'); ag.register();
  const src = path.join(world.dir, 'data'); goldenDataset(src);
  const id = ag.addSet('Kept two', [src], ['--keep-last', '2']);
  const versions: ReturnType<typeof manifest>[] = [];
  for (let i = 0; i < 4; i++) {
    if (i > 0) {
      await new Promise((r) => setTimeout(r, 1100));
      fs.writeFileSync(path.join(src, 'Documents/version.txt'), 'version ' + i);
      if (i === 2) fs.rmSync(path.join(src, 'Many/file-001.csv'));
    }
    const b = ag.backup(id); expect(b.out, b.out).toMatch(/^BS_STOP_SUCCESS /m);
    versions.push(manifest(src));
  }
  evidence.step('the server\'s nightly maintenance runs');
  const m = await world.adminApi('POST', 'maintenance', { now: new Date(Date.now() + 60000).toISOString().replace(/[-T:]/g, (c) => (c === 'T' ? '-' : '-')).slice(0, 19) });
  expect(m.status, m.text).toBe(200);
  const points = ag.cli(['points', '--set', id]).out.trim().split('\n').filter((l) => /^\d{4}-/.test(l)).map((l) => l.split(/\s/)[0]).sort();
  expect(points.length, points.join(',')).toBe(2);
  evidence.step('ORACLE: each kept point restores exactly its version');
  for (let i = 0; i < 2; i++) {
    const t = path.join(world.dir, 'restore-' + i);
    const r = ag.restore(id, t, ['--point', points[i]]);
    expect(r.code, r.out).toBe(0);
    expect(compare(versions[2 + i], manifest(path.join(t, src.replace(/^\//, ''))))).toEqual([]);
  }
});
