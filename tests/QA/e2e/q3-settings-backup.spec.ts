// Q3 (ST-10) — the backup of the server's settings, end to end: taken and downloaded on the Storage page, the server's
// settings are then lost (the whole System Home, and the customer's settings folder db), brought back exactly as the
// product documents it, and afterwards: the administrator signs in, the customer and its set are there, the customer's
// computer backs up again with its old registration, the saved key can still be read, and the restore is identical.
// What the product claims (src/Server/ConfigBackup.cs CFGBK-010): "To restore: stop the service, unzip "system" over the
// System Home and each "users\<login>\db" over that customer's db folder, start the service."
// Oracles: SHA-256 of the downloaded file against the server's own file, the folders on disk, the agent program's own
// answers, and SHA-256 of every restored file against the source manifest.
import { test, expect, signIn } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import { openSet } from '../lib/ui';
import { rail } from './q-helpers';
import { spawnSync } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import * as crypto from 'crypto';

const sha = (f: string) => crypto.createHash('sha256').update(fs.readFileSync(f)).digest('hex');

test('Q3 ST-10 the settings backup taken on the site brings a lost server back: sign-in, customer, set, registration, key, backup and restore', async ({ admin: page, world, evidence, browser }) => {
  world.addCustomer('qa-cfg');
  const ag = world.agent('qa-cfg', 'CFG-PC1'); ag.register();
  const src = path.join(world.dir, 'data'); const before = goldenDataset(src);
  const id = ag.addSet('Office files', [src]);
  const b = ag.backup(id); expect(b.out, b.out).toMatch(/^BS_STOP_SUCCESS /m);
  const keyBefore = await world.adminApi('GET', 'keys/qa-cfg/' + id);
  expect(keyBefore.status, 'the set\'s key is kept on the server: ' + keyBefore.text).toBe(200);

  evidence.step('Storage on the server → Backup of the server settings → Back up now, then Download');
  await rail(page, 'storage');
  const card = page.locator('section.card').filter({ has: page.getByRole('heading', { name: 'Backup of the server settings' }) });
  const copyDir = path.join(world.dir, 'second-disk', 'ServerSettings');
  await card.locator('.fr').filter({ hasText: 'A copy in another folder' }).locator('input').fill(copyDir);
  await card.getByRole('button', { name: 'Save and exit' }).click();
  await expect(page.locator('#toast').filter({ hasText: 'Saved' })).toBeVisible();
  await rail(page, 'dash'); await rail(page, 'storage');
  await card.getByRole('button', { name: '⟳ Back up now' }).click();
  await expect(page.locator('#toast').filter({ hasText: 'Saved' })).toBeVisible();
  const row = card.getByRole('row').filter({ hasText: /config-\d{8}-\d{6}\.zip/ }).first();
  await expect(row).toBeVisible();
  const name = /config-\d{8}-\d{6}\.zip/.exec(await row.innerText())![0];
  const dl = page.waitForEvent('download');
  await row.getByRole('button', { name: '⇩ Download' }).click();
  const kept = path.join(world.dir, 'kept-elsewhere', name);   // the administrator keeps it away from the server
  await (await dl).saveAs(kept);
  evidence.step('ORACLE: the download is the server\'s file, byte for byte; it holds the settings and the customer\'s db');
  expect(sha(kept)).toBe(sha(path.join(world.sys, 'config-backups', name)));
  expect(sha(path.join(copyDir, name)), 'the copy in the other folder').toBe(sha(kept));
  const listing = spawnSync('unzip', ['-Z1', kept], { encoding: 'utf8' }).stdout.split('\n');
  expect(listing).toContain('system/conf/system.xml');
  expect(listing).toContain('system/conf/users.xml');
  expect(listing).toContain('users/qa-cfg/db/Profile.xml');
  expect(listing.some((l) => l.startsWith('users/qa-cfg/files/')), 'backed-up data inside the settings backup').toBe(false);

  const runsBefore = (await world.runs('qa-cfg')).length;
  evidence.step('FAULT: the server stops; its whole System Home and the customer\'s db folder are lost');
  world.killServer();
  fs.rmSync(world.sys, { recursive: true, force: true });
  fs.rmSync(path.join(world.usersDir, 'qa-cfg', 'db'), { recursive: true, force: true });
  evidence.step('PROOF of the fault: the folders are gone, and the computer cannot reach its server');
  expect(fs.existsSync(world.sys)).toBe(false);
  expect(fs.existsSync(path.join(world.usersDir, 'qa-cfg', 'db'))).toBe(false);
  const down = ag.cli(['sets'], true); expect(down.code, down.out).not.toBe(0);

  evidence.step('RECOVERY as documented: unzip "system" over the System Home and "users/qa-cfg/db" over the customer\'s db folder');
  const x = path.join(world.dir, 'unzipped');
  expect(spawnSync('unzip', ['-q', kept, '-d', x]).status).toBe(0);
  fs.cpSync(path.join(x, 'system'), world.sys, { recursive: true });
  fs.cpSync(path.join(x, 'users', 'qa-cfg', 'db'), path.join(world.usersDir, 'qa-cfg', 'db'), { recursive: true });
  await world.startServer();

  evidence.step('the administrator signs in again in a new browser (password and authenticator from the restored settings)');
  const fresh = await (await browser.newContext()).newPage();
  await signIn(fresh, world);
  evidence.step('the customer and its set are on the site');
  await openSet(fresh, 'qa-cfg', 'Office files');
  evidence.step('ORACLE: the saved encryption key can still be read back');
  (world as any)._ses = '';
  const keyAfter = await world.adminApi('GET', 'keys/qa-cfg/' + id);
  expect(keyAfter.status, keyAfter.text).toBe(keyBefore.status);
  expect(keyAfter.text).toBe(keyBefore.text);
  console.log('Q3 FACT (not asserted, owner decision Q-D2): runs in the server\'s history before the loss ' + runsBefore + ', after the settings came back ' + (await world.runs('qa-cfg')).length);
  evidence.step('ORACLE: the computer, with its old registration, backs up a change and restores identical');
  fs.writeFileSync(path.join(src, 'Documents/after-the-settings-came-back.txt'), 'new file');
  const b2 = ag.backup(id); expect(b2.out, b2.out).toMatch(/^BS_STOP_SUCCESS .*new=1 /m);
  const after = manifest(src);
  const t = path.join(world.dir, 'restore');
  const r = ag.restore(id, t); expect(r.code, r.out).toBe(0);
  expect(compare(after, manifest(restoredPath(t, src)))).toEqual([]);
  evidence.step('ORACLE: the point made before the loss still restores exactly the first version');
  const points = ag.cli(['points', '--set', id]).out.trim().split('\n').filter((l) => /^\d{4}-/.test(l)).map((l) => l.split(/\s/)[0]).sort();
  expect(points.length, points.join(',')).toBe(2);
  const t0 = path.join(world.dir, 'restore-old');
  const r0 = ag.restore(id, t0, ['--point', points[0]]); expect(r0.code, r0.out).toBe(0);
  expect(compare(before, manifest(restoredPath(t0, src)))).toEqual([]);
});
