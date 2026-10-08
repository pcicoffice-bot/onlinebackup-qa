// Q4 (ST-07) — replication to a second server, end to end, with two real server processes.
// What the product claims (src/Server/Replication.cs): "every commit is replayed on a second server in the same order ...
// The second server is a normal server: agents can restore from it with the same users, devices and keys." The queue
// "stops at the first failure and retries it next time (order is never broken)". The site (Storage on the server):
// "Copy every backup to a second server", "Waiting to copy".
// Oracles: the second server's folders on disk, the first server's queue count, a computer registered on the second
// server restoring with the agent program, SHA-256 of every restored file against the manifest made at the source.
import { test, expect } from '../lib/fixtures';
import { World, goldenDataset, manifest, compare, restoredPath, CUSTOMER_PASSWORD } from '../lib/world';
import { rail, until, tree } from './q-helpers';
import * as fs from 'fs';
import * as path from 'path';

const TOKEN = 'qa-repl-token-1';
async function pending(w: World) { const r = await w.adminApi('GET', 'settings'); return Number(/<f n="replicationPending">(\d+)/.exec(r.text)![1]); }
async function receiver(B: World) {
  // FIXTURE: the second server is made a receiver through its API — the admin site has no field for it (finding Q-F2)
  const r = await B.adminApi('POST', 'settings', { replicaReceiverToken: TOKEN, replicaDeleteDelayDays: 14 });
  expect(r.status, r.text).toBe(200);
}
async function newCustomer(A: World, login: string) {
  // what the provider's "new customer" call does (customers otherwise sign up from the client program)
  const r = await A.adminApi('POST', 'users', { login, password: CUSTOMER_PASSWORD, quotaGB: 5, email: 'it@example.invalid' });
  expect(r.status, r.text).toBe(200);
}
function points(ag: ReturnType<World['agent']>, id: string) { return ag.cli(['points', '--set', id]).out.trim().split('\n').filter((l) => /^\d{4}-/.test(l)).map((l) => l.split(/\s/)[0]).sort(); }

test('Q4a ST-07 switched on in the site: every backup reaches the second server; a computer restores from it; second server down, behind, back', async ({ admin: page, world: A, evidence }) => {
  const B = await new World().start();
  try {
    await receiver(B);
    evidence.step('Storage on the server → A copy on a second server: on, address, token, Save and exit');
    await rail(page, 'storage');
    const card = page.locator('section.card').filter({ has: page.getByRole('heading', { name: 'A copy on a second server' }) });
    await card.locator('label.tog').filter({ has: page.getByRole('checkbox', { name: 'Copy every backup to a second server' }) }).click();
    await expect(card.getByRole('checkbox', { name: 'Copy every backup to a second server' })).toBeChecked();
    await card.locator('.fr').filter({ hasText: 'Address of the second server' }).locator('input').fill(B.url);
    await card.locator('.fr').filter({ hasText: 'Access token' }).locator('input').fill(TOKEN);
    await card.getByRole('button', { name: 'Save and exit' }).click();
    await expect(page.locator('#toast').filter({ hasText: 'Saved' })).toBeVisible();
    const s = (await A.adminApi('GET', 'settings')).text;
    expect(s).toContain('<f n="replicationOn">1</f>');

    evidence.step('a customer, a computer, a set, a backup on the first server');
    await newCustomer(A, 'qa-rep');
    const ag = A.agent('qa-rep', 'REP-PC1'); ag.register();
    const src = path.join(A.dir, 'data'); const v1 = goldenDataset(src);
    const id = ag.addSet('Office files', [src]);
    const b1 = ag.backup(id); expect(b1.out, b1.out).toMatch(/^BS_STOP_SUCCESS /m);

    evidence.step('wait until the first server has nothing waiting to copy (it copies every minute)');
    await until('replication queue empty', async () => (await pending(A)) === 0, 240000, 5000);
    const onB = path.join(B.usersDir, 'qa-rep', 'files', id);
    expect(tree(onB).size, 'objects of the set on the second server').toBeGreaterThan(10);

    evidence.step('DISASTER: a computer registers on the second server with the same customer, restores (SHA-256)');
    const dr1 = B.agent('qa-rep', 'DR-PC1'); dr1.register();
    expect(dr1.sets().map((x) => x.id)).toContain(id);
    const t1 = path.join(B.dir, 'restore-1');
    const r1 = dr1.restore(id, t1); expect(r1.code, r1.out).toBe(0);
    expect(compare(v1, manifest(restoredPath(t1, src)))).toEqual([]);

    evidence.step('FAULT: the second server is down; the first one goes on backing up');
    B.killServer();
    const downCheck = await fetch(B.url + '/api/admin/settings').then(() => 'answers', () => 'down');
    expect(downCheck, 'PROOF: the second server does not answer').toBe('down');
    fs.writeFileSync(path.join(src, 'Documents/letter.txt'), 'Dear customer,\nversion two.\n');
    fs.rmSync(path.join(src, 'Many/file-007.csv'));
    fs.writeFileSync(path.join(src, 'Documents/new.txt'), 'new in version two');
    const b2 = ag.backup(id); expect(b2.out, b2.out).toMatch(/^BS_STOP_SUCCESS /m);
    const v2 = manifest(src);
    evidence.step('the first server keeps the copy waiting (its log says why); the site shows it waiting');
    await until('a failed replication attempt in the first server\'s log', () => {
      const logs = path.join(A.sys, 'logs', 'System');
      return fs.existsSync(logs) && fs.readdirSync(logs).some((f) => /replication waiting \(.*qa-rep/.test(fs.readFileSync(path.join(logs, f), 'utf8')));
    }, 150000, 5000);
    const waiting = await pending(A);
    expect(waiting, 'copies waiting while the second server is down').toBeGreaterThan(0);
    await rail(page, 'dash'); await rail(page, 'storage');
    await expect(card.locator('.fr').filter({ hasText: 'Waiting to copy' })).toContainText(String(waiting));

    evidence.step('BEHIND: the second server is back; before the next copy, a restore from it gives the first version, whole');
    await B.startServer();
    const t2 = path.join(B.dir, 'restore-behind');
    const r2 = dr1.restore(id, t2);
    const stillBehind = (await pending(A)) > 0;
    console.log('Q4a BEHIND CHECK: copies waiting before ' + waiting + ', after the restore ' + (await pending(A)) + ' → ' + (stillBehind ? 'restored while behind' : 'RACE, inconclusive'));
    test.info().annotations.push({ type: 'behind', description: stillBehind ? 'the restore ran while copies were still waiting' : 'RACE: the queue drained during the restore — the behind check is inconclusive' });
    expect(r2.code, r2.out).toBe(0);
    const got = manifest(restoredPath(t2, src));
    expect(compare(stillBehind ? v1 : v2, got)).toEqual([]);

    evidence.step('BACK: the queue drains; the second server now restores the second version, and the first one still');
    await until('replication queue empty after the second server came back', async () => (await pending(A)) === 0, 240000, 5000);
    // Run 1 (night/q/q4-run1.txt): DR-PC1, registered on the second server while the first one still copied, is now
    // "revoked or unknown" there — the first server's copy of the customer's devices replaced it. Whether a computer may
    // register on the second server while it still receives copies is NEEDS OWNER DECISION (Q-D1); this test does not
    // assume an answer and restores with a computer registered now.
    const dr = B.agent('qa-rep', 'DR-PC2'); dr.register();
    expect(points(dr, id).length).toBe(2);
    const t3 = path.join(B.dir, 'restore-3');
    const r3 = dr.restore(id, t3); expect(r3.code, r3.out).toBe(0);
    expect(compare(v2, manifest(restoredPath(t3, src)))).toEqual([]);
    const t4 = path.join(B.dir, 'restore-4');
    const r4 = dr.restore(id, t4, ['--point', points(dr, id)[0]]); expect(r4.code, r4.out).toBe(0);
    expect(compare(v1, manifest(restoredPath(t4, src)))).toEqual([]);
  } finally { await B.stop(); }
});

test('Q4b ST-07 a customer that existed before the copy was switched on must not stop the copy of every other customer', async ({ world: A, evidence }) => {
  const B = await new World().start();
  try {
    await receiver(B);
    evidence.step('a customer who already backs up (the server runs before the second server is set up)');
    A.addCustomer('qa-old');
    const old = A.agent('qa-old', 'OLD-PC1'); old.register();
    const srcOld = path.join(A.dir, 'old'); fs.mkdirSync(srcOld); fs.writeFileSync(path.join(srcOld, 'a.txt'), 'old customer');
    const idOld = old.addSet('Old files', [srcOld]);
    expect(old.backup(idOld).out).toMatch(/^BS_STOP_SUCCESS /m);

    evidence.step('the copy to the second server is switched on (as in Q4a), then a new customer starts');
    const on = await A.adminApi('POST', 'settings', { replicationOn: 1, replicationUrl: B.url, replicationToken: TOKEN });
    expect(on.status, on.text).toBe(200);
    fs.writeFileSync(path.join(srcOld, 'b.txt'), 'old customer, after the switch');
    expect(old.backup(idOld).out).toMatch(/^BS_STOP_SUCCESS /m);
    await newCustomer(A, 'qa-new');
    const ag = A.agent('qa-new', 'NEW-PC1'); ag.register();
    const src = path.join(A.dir, 'data'); const v1 = goldenDataset(src);
    const id = ag.addSet('Office files', [src]);
    expect(ag.backup(id).out).toMatch(/^BS_STOP_SUCCESS /m);

    evidence.step('ORACLE: the new customer\'s backup reaches the second server and restores from it (SHA-256)');
    let why = '';
    try {
      await until('the new customer\'s set on the second server', () => tree(path.join(B.usersDir, 'qa-new', 'files', id)).size > 10 && fs.existsSync(path.join(B.usersDir, 'qa-new', 'db', 'Profile.xml')), 240000, 5000);
    } catch (e) {
      const logs = path.join(A.sys, 'logs', 'System');
      why = fs.readdirSync(logs).map((f) => fs.readFileSync(path.join(logs, f), 'utf8')).join('').split('\n').filter((l) => /replication/.test(l)).slice(-5).join('\n');
      throw new Error(String(e) + '\nwaiting to copy on the first server: ' + (await pending(A)) + '\nits log:\n' + why);
    }
    // as in Q4a: a computer is registered on the second server only after the queue has drained — a copy that arrives
    // later replaces db/ and drops a computer registered there meanwhile (Q-D1, NEEDS OWNER DECISION, not assumed here)
    await until('the first server\'s queue is empty', async () => (await pending(A)) === 0, 240000);
    const dr = B.agent('qa-new', 'DR-PC2'); dr.register();
    const t = path.join(B.dir, 'restore');
    const r = dr.restore(id, t); expect(r.code, r.out).toBe(0);
    expect(compare(v1, manifest(restoredPath(t, src)))).toEqual([]);
  } finally { await B.stop(); }
});
