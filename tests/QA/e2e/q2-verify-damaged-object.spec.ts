// Q2 (ST-04) — verify from the admin site, end to end: an object on the server's disk is damaged (the damage is proven:
// its bytes changed, and a restore of that file fails before the check), "Check the data (verify)" on the set's
// Maintenance tab finds it, the object leaves the store into the set's Quarantine folder, the agent's next backup sends
// the file again, and the next restore is identical to the source (SHA-256).
// What the product claims (src/Server/SetStore.cs VerifyRows): a damaged object is quarantined, its version is recorded
// as lost, and "the agent sends a full copy of this file from the source in its next run".
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import { openSet, tab } from '../lib/ui';
import { find } from './q-helpers';
import * as fs from 'fs';
import * as path from 'path';
import * as crypto from 'crypto';

const sha = (f: string) => crypto.createHash('sha256').update(fs.readFileSync(f)).digest('hex');

test('Q2 ST-04 a damaged object on the server is found by verify on the site, quarantined, sent again, and the next restore is identical', async ({ admin: page, world, evidence }) => {
  world.addCustomer('qa-ver');
  const ag = world.agent('qa-ver', 'VER-PC1'); ag.register();
  const src = path.join(world.dir, 'data'); const before = goldenDataset(src);
  const id = ag.addSet('Office files', [src]);
  const b = ag.backup(id); expect(b.out, b.out).toMatch(/^BS_STOP_SUCCESS /m);
  const store = path.join(world.usersDir, 'qa-ver', 'files', id);

  evidence.step('FAULT: one stored object (the one of Documents/letter.txt is not known from outside, so: the smallest object) gets one byte flipped');
  const objs = find(path.join(store, 'Current'), /\.000$/).sort((a, b2) => fs.statSync(a).size - fs.statSync(b2).size);
  expect(objs.length, 'objects in Current').toBeGreaterThan(10);
  const victim = objs[Math.floor(objs.length / 2)];
  const rel = path.relative(store, victim);
  const good = sha(victim);
  const bytes = fs.readFileSync(victim); bytes[Math.floor(bytes.length / 2)] ^= 0xff; fs.writeFileSync(victim, bytes);
  const damaged = sha(victim);
  evidence.step('PROOF of the fault: the bytes on disk changed (' + good.slice(0, 12) + ' → ' + damaged.slice(0, 12) + ')');
  expect(damaged).not.toBe(good);
  evidence.step('PROOF of the fault: a restore now fails for exactly one file');
  const t0 = path.join(world.dir, 'restore-damaged');
  const r0 = ag.restore(id, t0);
  expect(r0.code, r0.out).not.toBe(0);
  expect(r0.out).toMatch(/failed=1 /);
  const diff0 = compare(before, manifest(restoredPath(t0, src)));
  expect(diff0.length, diff0.join('\n')).toBe(1);
  const hit = diff0[0].replace(/^(MISSING|DIFFERENT) /, '').replace(/ \(.*$/, '');

  evidence.step('the site: open the set, Maintenance, Check the data (verify)');
  await openSet(page, 'qa-ver', 'Office files');
  await tab(page, 'Maintenance');
  await page.getByRole('button', { name: 'Check the data (verify)' }).click();
  await expect(page.locator('#toast')).toHaveText('Checked: 1 damaged');

  evidence.step('ORACLE (disk): the damaged object left the store and is in the set\'s Quarantine, byte for byte the damaged one');
  expect(fs.existsSync(victim), 'the damaged object in the store after verify').toBe(false);
  const q = find(path.join(store, 'Quarantine'), new RegExp(rel.split(path.sep).join('/').replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '$'));
  expect(q.length, 'quarantined copies of ' + rel).toBe(1);
  expect(sha(q[0])).toBe(damaged);

  evidence.step('the agent\'s next backup (nothing changed at the source) sends the file again');
  const b2 = ag.backup(id); expect(b2.out, b2.out).toMatch(/^BS_STOP_SUCCESS /m);
  expect(b2.out, 'the next backup sent something again').not.toMatch(/ bytes=0\b/);

  evidence.step('ORACLE: the next restore is identical to the source (SHA-256), ' + hit + ' included');
  const t1 = path.join(world.dir, 'restore-after');
  const r1 = ag.restore(id, t1); expect(r1.code, r1.out).toBe(0);
  expect(compare(before, manifest(restoredPath(t1, src)))).toEqual([]);
  evidence.step('a second verify finds nothing damaged');
  await page.getByRole('button', { name: 'Check the data (verify)' }).click();
  await expect(page.locator('#toast')).toHaveText('Checked: 0 damaged');
});
