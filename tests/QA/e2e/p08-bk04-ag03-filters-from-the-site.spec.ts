// P08 (BK-04, AG-03) — what to leave out, set by the technician in the set editor in the browser, reaches the RUNNING
// agent service, and its next backup follows it:
//  - "What to back up": the folder Drafts skipped inside the chosen folder (typed, "✕ Skip");
//  - "Filters": skip files whose name starts with ~$, files whose name ends with .bak, folders whose name is exactly Logs.
// The newest point holds exactly the files that are kept — siblings with the same beginning (DraftsArchive, Drafts2,
// DraftsNotes.txt, LogsArchive, notes.bak.txt) are kept — each byte-identical; the first point (before the change) still
// holds everything. The service was started BEFORE the change: the change reaches the program that is already running.
// Oracles outside the page: the set in Profile.xml on the server's disk (what the computer takes), the agent's own result
// line, SHA-256 of every restored file against manifests made from the source.
import { test, expect } from '../lib/fixtures';
import { manifest, compare, restoredPath } from '../lib/world';
import { openSet, backUpNow, waitForTask } from '../lib/ui';
import { diskSet, kids, editorTab } from '../lib/ui-oracle';
import { serviceResults } from './p-helpers';
import { without, until } from './q-helpers';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';

const SET = 'Office with filters';
const w = (root: string, rel: string, data: Buffer | string) => { const f = path.join(root, rel); fs.mkdirSync(path.dirname(f), { recursive: true }); fs.writeFileSync(f, data); };

test('P08 BK-04/AG-03 a skipped folder and filter rules set in the browser reach the running service: the next point holds exactly the kept files, identical; the first point everything', async ({ admin: page, world, evidence }) => {
  const login = 'qa-p08';
  world.addCustomer(login);
  const ag = world.agent(login, 'P08-PC'); ag.register();
  const src = path.join(world.dir, 'data');
  w(src, 'Docs/letter.txt', 'Dear customer,\n');
  w(src, 'Docs/report.docx', crypto.randomBytes(200 * 1024));
  w(src, 'Docs/~$report.docx', 'owner file of Word');             // skipped: starts with ~$
  w(src, 'Docs/notes.bak', 'old notes');                           // skipped: ends with .bak
  w(src, 'Docs/notes.bak.txt', 'kept: ends with .txt');
  w(src, 'Docs/BACKUP.BAK', 'skipped: the rule is not case-sensitive (Windows names)');
  w(src, 'Drafts/cache.bin', crypto.randomBytes(64 * 1024));          // skipped: the folder Drafts
  w(src, 'Drafts/sub/deep.txt', 'inside the skipped folder');
  w(src, 'DraftsArchive/keep.txt', 'kept: a sibling folder with the same beginning');
  w(src, 'Drafts2/keep.txt', 'kept: another sibling');
  w(src, 'DraftsNotes.txt', 'kept: a file with the same beginning');
  w(src, 'Logs/app.log', 'skipped: the folder named Logs');
  w(src, 'Logs/old/x.log', 'skipped');
  w(src, 'Projects/Logs/inner.log', 'skipped: a folder named Logs deeper down');
  w(src, 'Projects/plan.txt', 'kept');
  w(src, 'LogsArchive/keep.log', 'kept: not exactly Logs');
  w(src, 'Binary/data.bin', crypto.randomBytes(2 * 1024 * 1024 + 3));
  // (not "Temp": the server's default filters for new sets already skip every folder named Temp — DEF-010 GLOBAL_FILTER)
  const v1 = manifest(src);
  const far = new Date(Date.now() + 6 * 3600000);
  const id = ag.addSet(SET, [src], ['--hour', String(far.getHours()), '--minute', '0']);

  evidence.step('backup 1 (before the change): everything');
  const b1 = ag.backup(id); expect(b1.out, b1.out).toMatch(/^BS_STOP_SUCCESS new=17 /m);

  evidence.step('the agent runs as the Windows service, from now on');
  ag.startService();

  evidence.step('the set editor: What to back up — the folder Drafts skipped (typed, ✕ Skip)');
  await openSet(page, login, SET);
  await editorTab(page, 'What to back up');
  const picker = page.locator('.picker');
  const skip = path.join(src, 'Drafts');
  await picker.locator('.pick2 > .stack').nth(1).locator('input').fill(skip);
  await picker.getByRole('button', { name: '✕ Skip' }).click();
  await expect(picker.locator('.tsel .srow.sub'), 'the chosen list shows the skipped folder under the chosen one').toHaveCount(1);
  await expect(picker.locator('.tsel .srow.sub')).toContainText('Drafts');

  evidence.step('Filters: skip ~$… files, …bak files, folders named exactly Logs');
  await editorTab(page, 'Filters');
  const n0 = await page.locator('.frow.f5').count();
  const rules: [string, string, string, string][] = [['N', 'START_WITH', '~$', 'N'], ['N', 'END_WITH', '.bak', 'N'], ['N', 'EXACT', 'Logs', 'Y']];
  for (const [i, [act, ty, pat, dir]] of rules.entries()) {
    await page.getByRole('button', { name: '+ Add a rule' }).click();
    const r = page.locator('.frow.f5').nth(n0 + i);
    await r.locator('select').nth(0).selectOption(act); await r.locator('select').nth(1).selectOption(ty);
    await r.locator('input').fill(pat); await r.locator('select').nth(2).selectOption(dir);
  }
  await page.getByRole('button', { name: 'Save and exit' }).click();
  await expect(page.locator('#toast').filter({ hasText: 'Saved' })).toBeVisible();

  evidence.step('ORACLE (server disk): Profile.xml — the computer takes the skipped folder and the rules from here');
  const s = diskSet(world, login, id);
  expect(kids(s, 'DE-SOURCE').map((x) => x.text)).toEqual([skip]);
  const stored = kids(s, 'FILTER').filter((f) => f.attrs.NAME !== 'COMMON').map((f) => [f.attrs.INCLUDE, f.attrs.TYPE, kids(f, 'PATTERN').map((p) => p.text).join('|'), f.attrs.APPLY_DIR]);
  expect(stored.slice(-3)).toEqual(rules);

  evidence.step('Back up now on the site: the running service runs it with the new settings');
  await openSet(page, login, SET);
  await backUpNow(page);
  const r2 = await until('the service\'s run', () => serviceResults(ag.log, SET)[0], 180000, 2000);
  expect(r2.result, 'ORACLE (agent): the run of the running service').toBe('BS_STOP_SUCCESS');
  await waitForTask(page, SET, 'Succeeded', 2);
  ag.stopService();

  evidence.step('ORACLE (restore): the newest point holds exactly the kept files, each identical');
  const skipped = (k: string) => k.startsWith('Drafts/') || /(^|\/)~\$[^/]*$/.test(k) || /\.bak$/i.test(k) || k.startsWith('Logs/') || k.includes('/Logs/');
  const kept = without(v1, skipped);
  expect([...v1.keys()].filter(skipped).sort(), 'the dataset really has files to skip').toEqual(['Docs/BACKUP.BAK', 'Docs/notes.bak', 'Docs/~$report.docx', 'Drafts/cache.bin', 'Drafts/sub/deep.txt', 'Logs/app.log', 'Logs/old/x.log', 'Projects/Logs/inner.log']);
  const t2 = path.join(world.dir, 'restore-newest');
  const rr2 = ag.restore(id, t2); expect(rr2.code, rr2.out).toBe(0);
  expect(compare(kept, manifest(restoredPath(t2, src)))).toEqual([]);

  evidence.step('ORACLE (restore): the first point, made before the change, still holds everything');
  const pts = ag.cli(['points', '--set', id]).out.trim().split('\n').filter((l) => /^\d{4}-/.test(l)).map((l) => l.split(/\s/)[0]).sort();
  expect(pts.length).toBe(2);
  const t1 = path.join(world.dir, 'restore-first');
  const rr1 = ag.restore(id, t1, ['--point', pts[0]]); expect(rr1.code, rr1.out).toBe(0);
  expect(compare(v1, manifest(restoredPath(t1, src)))).toEqual([]);
});
