// P05 (UI-04) — the customer's client window, end to end in a real browser: the agent program serves client.html on
// this computer (the desktop shortcut's "ui", its one-time key in the address); the customer adds a backup of a folder
// (typed into the folder picker), is asked to sign in — a wrong password is refused and creates nothing — signs in,
// backs up with "Back up now", and restores one chosen file to a new folder. The restored file is identical to the
// source (SHA-256) and is the only file written there.
// Oracles outside the page: the agent program's own list of sets (CLI), the server's run records, the target folder
// on disk and SHA-256 of the source file.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, CUSTOMER_PASSWORD } from '../lib/world';
import { agentProcess } from '../lib/fault';
import { tree, until } from './q-helpers';
import { ChildProcess } from 'child_process';
import * as net from 'net';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';

const freePort = () => new Promise<number>((res) => { const s = net.createServer(); s.listen(0, '127.0.0.1', () => { const p = (s.address() as net.AddressInfo).port; s.close(() => res(p)); }); });

test('P05 UI-04 the client window in the browser: a wrong password refused, sign in, add a folder set, back up, restore one file to a new folder — identical', async ({ page, world, evidence }) => {
  const login = 'qa-p05';
  world.addCustomer(login);
  const ag = world.agent(login, 'P05-PC'); ag.register();
  const src = path.join(world.dir, 'data'); const data = goldenDataset(src);
  const SET = 'From the window';

  evidence.step('the desktop shortcut: the agent program serves the client window on this computer');
  const port = await freePort();
  let ui: ChildProcess | undefined = agentProcess(ag, ['ui', '--port', String(port), '--no-browser', '--minutes', '10']);
  try {
    const url = await until('the client window\'s address in the agent output', () => {
      const m = new RegExp('http://127\\.0\\.0\\.1:\\d+/#[0-9a-f]+').exec(fs.existsSync(ag.log) ? fs.readFileSync(ag.log, 'utf8') : '');
      return m ? m[0] : undefined;
    }, 60000, 500);
    await page.goto(url);
    await expect(page.locator('#nav button').first()).toBeVisible();
    await expect(page.locator('#main')).toContainText('No backups are set up yet');

    evidence.step('New backup: name, the folder typed into the picker, ✓ Back up');
    await page.locator('#nav button', { hasText: 'New backup' }).click();
    const nameBox = page.locator('#main label', { hasText: /^Backup name$/ }).locator('xpath=following-sibling::input[1]');
    await nameBox.fill(SET);
    await page.getByPlaceholder('D:\\Data', { exact: true }).fill(src);
    await page.getByRole('button', { name: '✓ Back up', exact: true }).click();
    await expect(page.locator('#main .srow').filter({ hasText: src })).toHaveCount(1);
    await page.getByRole('button', { name: 'Save and exit', exact: true }).click();

    evidence.step('the window asks for the password: a wrong one is refused, nothing is created');
    const dlg = page.locator('dialog[open]');
    await expect(dlg).toContainText('Sign in');
    await dlg.locator('input[type=password]').fill('Not-The-Password-1');
    await dlg.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(page.locator('#toast.err')).toBeVisible();
    await expect(dlg, 'the sign-in stays open after a wrong password').toBeVisible();
    expect(ag.sets(), 'sets on the server after a refused sign-in').toEqual([]);

    evidence.step('the right password: signed in, the set is added');
    await dlg.locator('input[type=password]').fill(CUSTOMER_PASSWORD);
    await dlg.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(page.locator('#toast').filter({ hasText: 'The backup was added' })).toBeVisible();
    const sets = ag.sets();
    expect(sets.map((s) => [s.name, s.sources]), 'the set as the agent program sees it').toEqual([[SET, [src]]]);

    evidence.step('Back up now in the window; the window shows it Succeeded');
    const card = page.locator('#main .card').filter({ hasText: SET });
    await card.getByRole('button', { name: 'Back up now' }).click();
    await expect(page.locator('#toast')).toContainText('The backup has started');
    const activity = page.locator('#main table tbody tr');
    await expect(activity.filter({ hasText: 'Backup' }).filter({ hasText: 'Succeeded' })).toHaveCount(1, { timeout: 180000 });
    const runs = (await world.runs(login)).filter((r) => r.kind === 'Backup');
    expect(runs.map((r) => r.status), 'the server\'s record of the backup').toEqual(['ok']);

    evidence.step('Restore: the newest point, one file chosen (Binary/random.bin), to a new folder');
    await page.locator('#nav button', { hasText: 'Restore' }).click();
    const target = path.join(world.dir, 'restored-from-window');
    expect(fs.existsSync(target)).toBe(false);
    await expect(page.locator('#main .files table')).toBeVisible({ timeout: 60000 });
    await page.getByPlaceholder('Filter by file or folder name').fill('random.bin');
    const fileRows = page.locator('#main .files tbody tr');
    await expect(fileRows).toHaveCount(1);
    await fileRows.locator('input[type=checkbox]').check();
    await page.getByPlaceholder('C:\\Restore', { exact: true }).fill(target);
    await page.locator('#main .bar button.primary', { hasText: 'Restore' }).click();
    await expect(page.locator('#toast')).toContainText('The restore has started');
    await expect(activity.filter({ hasText: 'Restore' }).filter({ hasText: 'Succeeded' })).toHaveCount(1, { timeout: 180000 });

    evidence.step('ORACLE (disk): exactly the chosen file is in the new folder, identical to the source (SHA-256)');
    const got = [...tree(target).keys()];
    expect(got.length, 'files written by the restore: ' + got.join(', ')).toBe(1);
    expect(got[0].endsWith('Binary/random.bin'), got[0]).toBe(true);
    const sha = crypto.createHash('sha256').update(fs.readFileSync(path.join(target, got[0]))).digest('hex');
    expect(sha).toBe(data.get('Binary/random.bin')!.sha256);
  } finally {
    ui?.kill('SIGTERM'); ui = undefined;
  }
});
