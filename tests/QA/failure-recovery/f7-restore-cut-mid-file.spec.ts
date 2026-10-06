// F7 — A restore is killed in the MIDDLE of a large file, then the customer simply restores again (no "overwrite").
// Expected: every file identical, no half-written or temporary file left in the customer's folder.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath, CUSTOMER_PASSWORD } from '../lib/world';
import { spawn } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';

test('F7 restore killed in the middle of a large file → restore again without overwrite → identical, nothing half-written left', async ({ world, evidence }) => {
  world.addCustomer('qa-f7');
  const ag = world.agent('qa-f7', 'F7-PC'); ag.register();
  const src = path.join(world.dir, 'data'); const data = goldenDataset(src);
  fs.writeFileSync(path.join(src, 'Binary', 'huge.bin'), Buffer.alloc(120 * 1024 * 1024, 7));   // takes a while to write back
  const want = manifest(src);
  const id = ag.addSet('Files', [src]);
  expect(ag.backup(id).out).toMatch(/^BS_STOP_SUCCESS /m);
  const target = path.join(world.dir, 'restore');
  const dll = path.join(process.env.QA_PRODUCT || path.join(__dirname, '../../..'), 'src/Agent/bin/Debug/net8.0/OnlineBackup.Agent.dll');
  evidence.step('restore, killed while the large file is being written');
  const p = spawn('dotnet', [dll, 'restore', '--home', ag.home, '--set', id, '--password', CUSTOMER_PASSWORD, '--target', target], { env: { ...process.env, ...world.env } });
  const until = Date.now() + 120000; let caught = false;
  while (Date.now() < until && !caught) {
    const all = fs.existsSync(target) ? fs.readdirSync(target, { recursive: true }).map(String) : [];
    caught = all.some((f) => /huge\.bin\.restoring$/.test(f));
    if (!caught) await new Promise((r) => setTimeout(r, 20));
  }
  expect(caught, 'the large file was being written when the restore was killed').toBe(true);
  p.kill('SIGKILL'); await new Promise((r) => p.on('exit', r));
  evidence.step('restore again into the same folder, without overwrite');
  const r = ag.restore(id, target);
  expect(r.code, r.out).toBe(0);
  const got = manifest(target);
  const leftovers = [...got.keys()].filter((k) => /\.restoring$|\.part$|\.tmp$/.test(k));
  expect(leftovers, 'half-written files left in the customer\'s folder').toEqual([]);
  expect(compare(want, manifest(restoredPath(target, src)))).toEqual([]);
});
