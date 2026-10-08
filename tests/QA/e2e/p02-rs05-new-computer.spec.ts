// P02 (RS-05) — restore on a new computer, end to end: the first computer backs up twice (a set with its own encryption
// key, CUSTOM — known only to the customer) and is lost (its program and its folder are gone). A second agent program,
// in a fresh folder, registers as a new computer of the same customer; it lists the old computer's set and both of its
// restore points; a wrong key is refused and writes nothing; with the key given, every file of the newest point and
// of the first point restores identical (SHA-256); the new computer then keeps the key and goes on backing up the set.
// The site shows both computers of the customer.
// Oracles outside the product: SHA-256 manifests made at the source at each backup; the target folders on disk.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath, CUSTOMER_PASSWORD } from '../lib/world';
import { openCustomer, tab } from '../lib/ui';
import { tree } from './q-helpers';
import * as fs from 'fs';
import * as path from 'path';

const ENC_KEY = 'Customer-Own-Key-7f3a';

test('P02 RS-05 a new computer: registered, the key given, every file of every point restores identical, the old computer\'s points listed, a wrong key refused', async ({ admin: page, world, evidence }) => {
  const login = 'qa-p02';
  world.addCustomer(login);
  const old = world.agent(login, 'P02-OLD-PC'); old.register();
  const src = path.join(world.dir, 'data'); goldenDataset(src);
  evidence.step('the old computer: a set with the customer\'s own encryption key');
  const add = old.cli(['addset', '--password', CUSTOMER_PASSWORD, '--name', 'Office', '--source', src, '--keytype', 'CUSTOM', '--key', ENC_KEY]);
  const id = add.out.trim().split('\n').pop()!.trim();
  expect(id, add.out).toMatch(/^\d+$/);
  const b1 = old.backup(id); expect(b1.out, b1.out).toMatch(/^BS_STOP_SUCCESS /m);
  const v1 = manifest(src);
  await new Promise((r) => setTimeout(r, 1100));
  fs.writeFileSync(path.join(src, 'Documents/letter.txt'), 'Dear customer,\nthe second version of the letter.\n');
  fs.rmSync(path.join(src, 'Many/file-007.csv'));
  fs.writeFileSync(path.join(src, 'Documents/new after first.txt'), 'only in the second point');
  const b2 = old.backup(id); expect(b2.out, b2.out).toMatch(/^BS_STOP_SUCCESS .*new=1 upd=1 .*del=1 /m);
  const v2 = manifest(src);
  const oldPoints = old.cli(['points', '--set', id]).out.trim().split('\n').filter((l) => /^\d{4}-/.test(l)).map((l) => l.trim());
  expect(oldPoints.length).toBe(2);

  evidence.step('the old computer is lost: its program and its folder are gone');
  fs.rmSync(old.home, { recursive: true, force: true });
  expect(fs.existsSync(old.home)).toBe(false);

  evidence.step('a new computer: a second agent program in a fresh folder registers for the same customer');
  const fresh = world.agent(login, 'P02-NEW-PC');
  const reg = fresh.register(); expect(reg.out).toMatch(/registered/);
  evidence.step('the new computer sees the old computer\'s set and lists both of its points');
  expect(fresh.sets().map((s) => s.id), 'sets seen by the new computer').toContain(id);
  const points = fresh.cli(['points', '--set', id]).out.trim().split('\n').filter((l) => /^\d{4}-/.test(l)).map((l) => l.trim());
  expect(points, 'the old computer\'s points, listed on the new computer').toEqual(oldPoints);

  evidence.step('a wrong key is refused and writes nothing');
  const wrongT = path.join(world.dir, 'restore-wrong');
  const wrong = fresh.cli(['restore', '--set', id, '--password', CUSTOMER_PASSWORD, '--key', 'not-the-key', '--target', wrongT], true);
  expect(wrong.code, wrong.out).not.toBe(0);
  expect(wrong.out).toMatch(/encryption key is wrong/i);
  expect([...tree(wrongT).keys()], 'files written by a restore with a wrong key').toEqual([]);

  evidence.step('the key given: the newest point restores identical (SHA-256)');
  const t2 = path.join(world.dir, 'restore-newest');
  const r2 = fresh.cli(['restore', '--set', id, '--password', CUSTOMER_PASSWORD, '--key', ENC_KEY, '--target', t2], true);
  expect(r2.code, r2.out).toBe(0);
  expect(compare(v2, manifest(restoredPath(t2, src)))).toEqual([]);
  evidence.step('the first point restores identical too (the file deleted later is there, the later file is not)');
  const t1 = path.join(world.dir, 'restore-first');
  const r1 = fresh.cli(['restore', '--set', id, '--password', CUSTOMER_PASSWORD, '--key', ENC_KEY, '--target', t1, '--point', points.sort()[0]], true);
  expect(r1.code, r1.out).toBe(0);
  expect(compare(v1, manifest(restoredPath(t1, src)))).toEqual([]);

  evidence.step('the new computer kept the proven key: a restore without the key now works from this computer');
  const t3 = path.join(world.dir, 'restore-kept-key');
  const r3 = fresh.restore(id, t3); expect(r3.code, r3.out).toBe(0);
  expect(compare(v2, manifest(restoredPath(t3, src)))).toEqual([]);

  evidence.step('the site: the set stays the old computer\'s; the customer\'s Computers tab lists both computers');
  await openCustomer(page, login);
  await expect(page.getByRole('heading', { name: /P02-OLD-PC · 1 sets/ })).toBeVisible();
  await tab(page, 'Computers');
  await expect(page.getByRole('row').filter({ hasText: 'P02-OLD-PC' })).toHaveCount(1);
  await expect(page.getByRole('row').filter({ hasText: 'P02-NEW-PC' })).toHaveCount(1);
});
