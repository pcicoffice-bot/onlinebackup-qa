// F4 — A restore is killed in the middle (the computer restarts); the customer starts it again into the same folder.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, fileCount, compare, restoredPath, CUSTOMER_PASSWORD } from '../lib/world';
import { spawn } from 'child_process';
import { TEMP_NAME } from '../lib/fault';
import * as fs from 'fs';
import * as path from 'path';

test('F4 restore killed mid-way → restore again into the same folder → every file identical, no leftovers', async ({ world, evidence }) => {
  world.addCustomer('qa-f4');
  const ag = world.agent('qa-f4', 'F4-PC'); ag.register();
  const src = path.join(world.dir, 'data'); const data = goldenDataset(src);
  const id = ag.addSet('Files', [src]);
  expect(ag.backup(id).out).toMatch(/^BS_STOP_SUCCESS /m);
  const target = path.join(world.dir, 'restore');
  evidence.step('a restore as its own process, killed when the first files appear');
  const dll = path.join(__dirname, '../../../src/Agent/bin/Debug/net8.0/OnlineBackup.Agent.dll');
  const p = spawn('dotnet', [process.env.QA_PRODUCT ? path.join(process.env.QA_PRODUCT, 'src/Agent/bin/Debug/net8.0/OnlineBackup.Agent.dll') : dll, 'restore', '--home', ag.home, '--set', id, '--password', CUSTOMER_PASSWORD, '--target', target], { env: { ...process.env, ...world.env } });
  // counted, not read: the restore renames its .ob-restoring files while this looks (Q-PW2: manifest() hit ENOENT on one)
  await expect.poll(() => fileCount(target), { timeout: 60000, intervals: [50] }).toBeGreaterThan(5);
  p.kill('SIGKILL');
  await new Promise((r) => p.on('exit', r));
  evidence.step('restore again into the same folder');
  const r = ag.restore(id, target, ['--overwrite']); expect(r.code, r.out).toBe(0);
  expect(compare(data, manifest(restoredPath(target, src)))).toEqual([]);
  const leftovers = [...manifest(target).keys()].filter((k) => TEMP_NAME.test(k) || /~$/.test(k));
  expect(leftovers, 'no half-written files left').toEqual([]);
});
