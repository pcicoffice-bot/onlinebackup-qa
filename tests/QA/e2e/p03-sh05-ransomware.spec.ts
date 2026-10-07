// P03 (SH-05) — ransomware suspicion, end to end: on the computer, every document is rewritten into an encrypted copy
// with a new extension (the originals deleted) and the agent program backs that up. The admin site shows the
// suspicion (the customer "Frozen — ransomware?" in red, a service call "Suspected ransomware — <set>"); retention is
// frozen: 40 days of the server's maintenance later the point before the attack is still there and restores identical
// (SHA-256). Only when the administrator releases the freeze on the site does the retention apply again (the old point
// goes) — proof that it was the freeze that kept it.
// Oracles outside the product: the SHA-256 manifest of the documents before the attack; the points the agent program
// lists; the customer's Profile.xml on the server's disk (RETENTION_FROZEN).
import { test, expect } from '../lib/fixtures';
import { manifest, compare, restoredPath } from '../lib/world';
import { openCustomer, tab } from '../lib/ui';
import { runId, rail, confirmYes } from './q-helpers';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';

test('P03 SH-05 a mass rewrite on the computer: the site shows the suspicion, retention is frozen (the point before the attack survives 40 days of maintenance and restores identical), released only on the site', async ({ admin: page, world, evidence }) => {
  const login = 'qa-p03';
  world.addCustomer(login);
  const ag = world.agent(login, 'P03-PC'); ag.register();
  const src = path.join(world.dir, 'data');
  fs.mkdirSync(src, { recursive: true });
  for (let i = 0; i < 80; i++) fs.writeFileSync(path.join(src, 'doc' + String(i).padStart(2, '0') + '.docx'), crypto.randomBytes(2000 + i));
  const id = ag.addSet('Docs', [src]);
  evidence.step('backup 1: the documents as they should be');
  const b1 = ag.backup(id); expect(b1.out, b1.out).toMatch(/^BS_STOP_SUCCESS .*new=80 /m);
  const before = manifest(src);
  const points1 = ag.cli(['points', '--set', id]).out.trim().split('\n').filter((l) => /^\d{4}-/.test(l)).map((l) => l.trim());
  expect(points1.length).toBe(1);
  await new Promise((r) => setTimeout(r, 1100));

  evidence.step('THE ATTACK: every document replaced by an encrypted copy "*.docx.locked", the original deleted');
  for (const f of fs.readdirSync(src)) {
    const p = path.join(src, f); const b = fs.readFileSync(p); for (let k = 0; k < b.length; k++) b[k] ^= 0x5a;
    fs.writeFileSync(p + '.locked', b); fs.rmSync(p);
  }
  const b2 = ag.backup(id); expect(b2.out, b2.out).toMatch(/^BS_STOP_SUCCESS .*new=80 .*del=80 /m);

  evidence.step('the site: the customer is shown "Frozen — ransomware?" in red');
  await rail(page, 'dash'); await rail(page, 'cust');
  const custRow = page.getByRole('row').filter({ hasText: login });
  await expect(custRow.locator('.pill.bad')).toHaveText('Frozen — ransomware?');
  evidence.step('the site: a service call "Suspected ransomware — Docs"');
  await rail(page, 'tickets');
  await expect(page.getByRole('row').filter({ hasText: 'Suspected ransomware — Docs' })).toHaveCount(1);
  const prof = () => fs.readFileSync(path.join(world.usersDir, login, 'db', 'Profile.xml'), 'utf8');
  expect(prof(), 'Profile.xml on the server\'s disk').toMatch(/RETENTION_FROZEN="Y"/);

  evidence.step('40 days of the server\'s maintenance: the point before the attack is kept (frozen)');
  const m1 = await world.adminApi('POST', 'maintenance', { now: runId(new Date(Date.now() + 40 * 86400000)) });
  expect(m1.status, m1.text).toBe(200);
  const points2 = ag.cli(['points', '--set', id]).out.trim().split('\n').filter((l) => /^\d{4}-/.test(l)).map((l) => l.trim()).sort();
  expect(points2.length, points2.join(',')).toBe(2);
  expect(points2[0]).toBe(points1[0]);
  evidence.step('ORACLE: the point before the attack restores the customer\'s documents identical (SHA-256)');
  const t = path.join(world.dir, 'restore-before-attack');
  const r = ag.restore(id, t, ['--point', points1[0]]); expect(r.code, r.out).toBe(0);
  expect(compare(before, manifest(restoredPath(t, src)))).toEqual([]);

  evidence.step('the administrator releases the freeze on the site (customer → Security → Release the freeze → Yes)');
  await rail(page, 'cust');
  await openCustomer(page, login);
  await tab(page, 'Security');
  await page.getByRole('button', { name: 'Release the freeze' }).click();
  await confirmYes(page);
  await expect(page.locator('#toast').filter({ hasText: 'Released' })).toBeVisible();
  expect(prof()).not.toMatch(/RETENTION_FROZEN="Y"/);
  await rail(page, 'cust');
  await expect(page.getByRole('row').filter({ hasText: login }).locator('.pill.bad')).toHaveCount(0);
  evidence.step('retention applies again: maintenance 40 days ahead removes the old point, the newest stays');
  const m2 = await world.adminApi('POST', 'maintenance', { now: runId(new Date(Date.now() + 40 * 86400000)) });
  expect(m2.status, m2.text).toBe(200);
  const points3 = ag.cli(['points', '--set', id]).out.trim().split('\n').filter((l) => /^\d{4}-/.test(l)).map((l) => l.trim());
  expect(points3, 'points after the release').toEqual([points2[1]]);
});
