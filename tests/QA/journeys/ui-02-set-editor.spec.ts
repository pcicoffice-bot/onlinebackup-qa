// UI-02 — the backup set editor of the admin site: every tab's fields are saved and read back EXACTLY.
// The set is made where a customer makes it (the client program / agent). The technician edits it in the browser.
// Oracle (outside the page): the server's API record of the set AND the customer's Profile.xml on the server's disk
// (the file the computer takes its settings from); then the page itself after a reload.
import { test, expect, signIn } from '../lib/fixtures';
import { Page } from '@playwright/test';
import { openSet, saveAndExit } from '../lib/ui';
import { storedSet, diskSet, kids, kid, schedules, row, toggle, editorTab, El } from '../lib/ui-oracle';
import * as fs from 'fs';
import * as path from 'path';

const NAME = 'Q&A <Office> "ש" 2026';   // XML-special characters and Hebrew: must be stored as typed

async function customerWithSet(world: any, login: string, extra: string[] = []) {
  world.addCustomer(login);
  const ag = world.agent(login, 'UI-PC1'); ag.register();
  const src = path.join(world.dir, 'data-' + login); fs.mkdirSync(src, { recursive: true }); fs.writeFileSync(path.join(src, 'a.txt'), 'a');
  const id = ag.addSet('Office files', [src], extra);
  return { ag, id, src };
}

/** Everything the technician sets in the full pass, as the server must keep it. */
const rulesOf = (s: El) => kids(s, 'FILTER').filter((f) => f.attrs.NAME !== 'COMMON').map((f) => [f.attrs.INCLUDE, f.attrs.TYPE, kids(f, 'PATTERN').map((p) => p.text).join('|'), f.attrs.APPLY_DIR, f.attrs.APPLY_FILE]);
const MY_RULES = [['N', 'END_WITH', '.bak', 'N', 'Y'], ['Y', 'CONTAIN', 'report & co', 'Y', 'N']];
function expectFullPass(s: El, src: string, rulesBefore: string[][]) {
  const a = s.attrs;
  expect(a.NAME, 'General: name').toBe(NAME);
  expect(a.ENABLED_SHADOW_COPY, 'General: VSS off').toBe('N');
  expect(a.BSET_UPLOAD_PERMISSION, 'General: permissions off').toBe('N');
  expect(kids(s, 'SEL-SOURCE').map((x) => x.text), 'What to back up: the folder chosen on the computer is kept').toEqual([src]);
  const common = kids(s, 'FILTER').filter((f) => f.attrs.NAME === 'COMMON');
  expect(common.length, 'What to back up: "skip system and temporary files" on').toBeGreaterThan(0);
  expect(common.flatMap((f) => kids(f, 'PATTERN').map((p) => p.text))).toEqual(expect.arrayContaining(['~$*', 'Thumbs.db', '$RECYCLE.BIN']));
  expect(schedules(s), 'Schedule: Mon/Wed/Fri 03:35 and every day 18:10, 6 hours max').toEqual([
    { days: '-M-W-F-', hour: '3', minute: '35', duration: '6' },
    { days: 'SMTWTFS', hour: '18', minute: '10', duration: '6' },
  ]);
  expect([a.RUN_MISSED, a.RUN_MISSED_NET, a.MISSED_DELAY_MINUTES, a.MISSED_MIN_HOURS], 'Schedule: missed-backup options').toEqual(['N', 'N', '17', '9']);
  expect(a.DEFAULT_DELTA_TYPE, 'Backup method: differential').toBe('D');
  expect(a.DEST_MODE, 'Destination: server + local copy').toBe('BOTH');
  const lc = kid(s, 'EXTRA_LOCAL_BACKUP');
  expect([lc.attrs.ENABLED, lc.attrs.BACKUP_TO, lc.attrs.PERIOD], 'Destination: local copy folder and days').toEqual(['Y', '/srv/qa-local copy', '21']);
  const rp = kid(s, 'RETENTION_POLICY');
  expect([rp.attrs.UNIT, rp.attrs.PERIOD], 'Versions kept: 12 backups').toEqual(['JOBS', '12']);
  expect(Object.fromEntries(kids(rp, 'RETENTION_SETTING').map((x) => [x.attrs.TYPE, x.attrs.KEEP])), 'Versions kept: GFS').toEqual({ DAILY: '7', WEEKLY: '4', QUARTERLY: '2', YEARLY: '1' });
  expect(rulesOf(s), 'Filters: the rules the set had (one per pattern) + the two new ones exactly as typed').toEqual([...rulesBefore, ...MY_RULES]);
  expect(a.COMPRESSION, 'Compression: fast').toBe('FAST');
  expect([a.BANDWIDTH_KBPS, a.LOW_PRIORITY, a.BUSY_CPU_PERCENT], 'Resources').toEqual(['640', 'N', '70']);
  expect(kids(s, 'PRE_CMD').map((c) => [c.attrs.PATH, c.attrs.STOP_ON_FAILURE]), 'Commands before').toEqual([['echo "before" & exit 0', 'Y']]);
  expect(kids(s, 'POST_CMD').map((c) => c.attrs.PATH), 'Commands after').toEqual(['echo after']);
  expect(kid(s, 'ENCRYPTING_KEY').attrs.KEY_TYPE, 'Encryption key: untouched').toBe('PASSWORD');
}

test('UI-02 every tab of the set editor: saved exactly (server API + Profile.xml on disk) and shown again after a reload', async ({ admin: page, world, evidence }) => {
  const { id, src } = await customerWithSet(world, 'qa-ui2');
  const before = await storedSet(world, 'qa-ui2', id);
  expect(before.attrs.ENABLED_SHADOW_COPY, 'precondition: VSS on by default').toBe('Y');
  expect(kid(before, 'ENCRYPTING_KEY').attrs.KEY, 'precondition: the set has its key check').toBeTruthy();
  // the rules the computer made (one row per pattern in the editor, so one FILTER per pattern once saved)
  const rulesBefore = kids(before, 'FILTER').filter((f) => f.attrs.NAME !== 'COMMON').flatMap((f) => kids(f, 'PATTERN').map((p) => [f.attrs.INCLUDE === 'Y' ? 'Y' : 'N', f.attrs.TYPE, p.text, f.attrs.APPLY_DIR === 'Y' ? 'Y' : 'N', f.attrs.APPLY_DIR === 'Y' ? 'N' : 'Y']));

  await openSet(page, 'qa-ui2', 'Office files');
  evidence.step('General: name, VSS off, permissions off');
  await editorTab(page, 'General');
  await row(page, 'Set name').locator('input').fill(NAME);
  await toggle(row(page, 'Open files'), false);
  await toggle(row(page, 'Permissions'), false);

  evidence.step('What to back up: skip system and temporary files');
  await editorTab(page, 'What to back up');
  await toggle(row(page, 'Skip system and temporary files'), true);

  evidence.step('Schedule: Mon/Wed/Fri 03:35 + a second time every day 18:10; stop after 6 hours; missed backups off, 17 min, 9 h');
  await editorTab(page, 'Schedule');
  const s1 = page.locator('.sched').nth(0);
  const days = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
  for (const [i, d] of days.entries()) await s1.locator('label', { hasText: d }).locator('input').setChecked([1, 3, 5].includes(i));
  await s1.locator('select').nth(0).selectOption('3'); await s1.locator('select').nth(1).selectOption('35');
  await page.getByRole('button', { name: '+ Add a time' }).click();
  const s2 = page.locator('.sched').nth(1);
  await s2.locator('select').nth(0).selectOption('18'); await s2.locator('select').nth(1).selectOption('10');
  await row(page, 'Stop the backup after').locator('select').selectOption('6');
  const missed = row(page, 'Missed backup');
  await toggle(missed, false);
  await missed.locator('input[type=number]').nth(0).fill('17'); await missed.locator('input[type=number]').nth(1).fill('9');
  await toggle(row(page, 'Internet down'), false);

  evidence.step('Backup method: differential');
  await editorTab(page, 'Backup method');
  await page.locator('.opt label', { hasText: 'Differential' }).click();

  evidence.step('Destination: server + local copy, folder, 21 days');
  await editorTab(page, 'Destination');
  await page.locator('.opt label', { hasText: 'The server + a local copy' }).click();
  await row(page, 'Local disk or network folder').locator('input').fill('/srv/qa-local copy');
  await row(page, 'Keep the local copy').locator('input').fill('21');

  evidence.step('Versions kept: 12 backups; GFS 7 daily, 4 weekly, 0 monthly, 2 quarterly, 1 yearly');
  await editorTab(page, 'Versions kept');
  const keep = row(page, 'Keep versions for');
  await keep.locator('input').fill('12'); await keep.locator('select').selectOption('JOBS');
  const gfs = row(page, 'Long-term versions (GFS)');
  for (const [l, v] of [['Daily', '7'], ['Weekly', '4'], ['Monthly', '0'], ['Quarterly', '2'], ['Yearly', '1']]) await gfs.locator('label', { hasText: l }).locator('input').fill(v);

  evidence.step('Filters: skip names ending .bak (files); back up only names containing "report & co" (folders)');
  await editorTab(page, 'Filters');
  const n0 = await page.locator('.frow.f5').count();
  expect(n0, 'the editor shows one row per pattern the set already has').toBe(rulesBefore.length);
  await page.getByRole('button', { name: '+ Add a rule' }).click();
  await page.getByRole('button', { name: '+ Add a rule' }).click();
  const r1 = page.locator('.frow.f5').nth(n0), r2 = page.locator('.frow.f5').nth(n0 + 1);
  await r1.locator('select').nth(0).selectOption('N'); await r1.locator('select').nth(1).selectOption('END_WITH'); await r1.locator('input').fill('.bak'); await r1.locator('select').nth(2).selectOption('N');
  await r2.locator('select').nth(0).selectOption('Y'); await r2.locator('select').nth(1).selectOption('CONTAIN'); await r2.locator('input').fill('report & co'); await r2.locator('select').nth(2).selectOption('Y');

  evidence.step('Encryption and compression: fast');
  await editorTab(page, 'Encryption and compression');
  await row(page, 'Compression').locator('select').selectOption('FAST');

  evidence.step('Resources: 640 KB/s, not low priority, wait above 70% CPU');
  await editorTab(page, 'Resources');
  await row(page, 'Upload limit').locator('input').fill('640');
  await toggle(row(page, 'Priority'), false);
  await row(page, 'Wait while the computer is busy').locator('input').fill('70');

  evidence.step('Commands: before (stop on failure), after');
  await editorTab(page, 'Commands');
  const pre = row(page, 'Before the backup'), post = row(page, 'After the backup');
  await pre.getByRole('button', { name: '+ Add a row' }).click(); await pre.locator('.prow input').fill('echo "before" & exit 0');
  await toggle(pre, true);
  await post.getByRole('button', { name: '+ Add a row' }).click(); await post.locator('.prow input').fill('echo after');

  evidence.step('Reports and Maintenance open (no fields)');
  await editorTab(page, 'Reports'); await expect(page.locator('.kpi').first()).toBeVisible();
  await editorTab(page, 'Maintenance'); await expect(page.getByRole('button', { name: 'Rebuild the index' })).toBeVisible();

  evidence.step('Save and exit');
  await saveAndExit(page);

  evidence.step('ORACLE 1: the server API record of the set');
  expectFullPass(await storedSet(world, 'qa-ui2', id), src, rulesBefore);
  evidence.step('ORACLE 2: Profile.xml on the server disk (what the computer gets)');
  expectFullPass(diskSet(world, 'qa-ui2', id), src, rulesBefore);

  evidence.step('reload and read every tab back in the page');
  await page.reload();
  await openSet(page, 'qa-ui2', NAME);
  await expect(row(page, 'Set name').locator('input')).toHaveValue(NAME);
  await expect(row(page, 'Open files').locator('input[type=checkbox]')).not.toBeChecked();
  await expect(row(page, 'Permissions').locator('input[type=checkbox]')).not.toBeChecked();
  await editorTab(page, 'What to back up');
  await expect(row(page, 'Skip system and temporary files').locator('label.tog input')).toBeChecked();
  await editorTab(page, 'Schedule');
  await expect(page.locator('.sched')).toHaveCount(2);
  for (const [i, d] of days.entries()) await expect(page.locator('.sched').nth(0).locator('label', { hasText: d }).locator('input')).toBeChecked({ checked: [1, 3, 5].includes(i) });
  await expect(page.locator('.sched').nth(0).locator('select').nth(0)).toHaveValue('3');
  await expect(page.locator('.sched').nth(0).locator('select').nth(1)).toHaveValue('35');
  await expect(page.locator('.sched').nth(1).locator('select').nth(0)).toHaveValue('18');
  await expect(page.locator('.sched').nth(1).locator('select').nth(1)).toHaveValue('10');
  await expect(row(page, 'Stop the backup after').locator('select')).toHaveValue('6');
  await expect(row(page, 'Missed backup').locator('label.tog input')).not.toBeChecked();
  await expect(row(page, 'Missed backup').locator('input[type=number]').nth(0)).toHaveValue('17');
  await expect(row(page, 'Missed backup').locator('input[type=number]').nth(1)).toHaveValue('9');
  await expect(row(page, 'Internet down').locator('label.tog input')).not.toBeChecked();
  await editorTab(page, 'Backup method');
  await expect(page.locator('input[name=dt][value=D]')).toBeChecked();
  await editorTab(page, 'Destination');
  await expect(page.locator('input[name=dm][value=BOTH]')).toBeChecked();
  await expect(row(page, 'Local disk or network folder').locator('input')).toHaveValue('/srv/qa-local copy');
  await expect(row(page, 'Keep the local copy').locator('input')).toHaveValue('21');
  await editorTab(page, 'Versions kept');
  await expect(row(page, 'Keep versions for').locator('input')).toHaveValue('12');
  await expect(row(page, 'Keep versions for').locator('select')).toHaveValue('JOBS');
  for (const [l, v] of [['Daily', '7'], ['Weekly', '4'], ['Monthly', '0'], ['Quarterly', '2'], ['Yearly', '1']]) await expect(row(page, 'Long-term versions (GFS)').locator('label', { hasText: l }).locator('input')).toHaveValue(v);
  await editorTab(page, 'Filters');
  await expect(page.locator('.frow.f5')).toHaveCount(n0 + 2);
  await expect(page.locator('.frow.f5').nth(n0).locator('input')).toHaveValue('.bak');
  await expect(page.locator('.frow.f5').nth(n0 + 1).locator('input')).toHaveValue('report & co');
  await expect(page.locator('.frow.f5').nth(n0 + 1).locator('select').nth(0)).toHaveValue('Y');
  await expect(page.locator('.frow.f5').nth(n0 + 1).locator('select').nth(2)).toHaveValue('Y');
  await editorTab(page, 'Encryption and compression');
  await expect(row(page, 'Compression').locator('select')).toHaveValue('FAST');
  await editorTab(page, 'Resources');
  await expect(row(page, 'Upload limit').locator('input')).toHaveValue('640');
  await expect(row(page, 'Priority').locator('label.tog input')).not.toBeChecked();
  await expect(row(page, 'Wait while the computer is busy').locator('input')).toHaveValue('70');
  await editorTab(page, 'Commands');
  await expect(row(page, 'Before the backup').locator('.prow input')).toHaveValue('echo "before" & exit 0');
  await expect(row(page, 'Before the backup').locator('label.tog input')).toBeChecked();
  await expect(row(page, 'After the backup').locator('.prow input')).toHaveValue('echo after');

  evidence.step('a second save without changes changes nothing the technician set (idempotent)');
  await saveAndExit(page);
  expectFullPass(await storedSet(world, 'qa-ui2', id), src, rulesBefore);
});

test('UI-02 boundary: a time made on the computer (22:33) is kept when the technician only looks at the Schedule tab and saves', async ({ admin: page, world, evidence }) => {
  const { id } = await customerWithSet(world, 'qa-ui2b', ['--hour', '22', '--minute', '33']);
  expect(schedules(await storedSet(world, 'qa-ui2b', id)).map((x) => x.hour + ':' + x.minute), 'precondition: the computer made 22:33').toEqual(['22:33']);
  await openSet(page, 'qa-ui2b', 'Office files');
  evidence.step('open the Schedule tab — what does it show?');
  await editorTab(page, 'Schedule');
  const shown = (await page.locator('.sched').first().locator('select').nth(0).inputValue()) + ':' + (await page.locator('.sched').first().locator('select').nth(1).inputValue());
  evidence.step('the page shows ' + shown + '; rename the set only, Save and exit');
  await editorTab(page, 'General');
  await row(page, 'Set name').locator('input').fill('Office files renamed');
  await saveAndExit(page);
  const after = await storedSet(world, 'qa-ui2b', id);
  expect(after.attrs.NAME).toBe('Office files renamed');
  expect(schedules(after).map((x) => x.hour + ':' + x.minute), 'the schedule the technician did not change must stay 22:33 (the page showed ' + shown + ')').toEqual(['22:33']);
  expect(schedules(diskSet(world, 'qa-ui2b', id)).map((x) => x.hour + ':' + x.minute)).toEqual(['22:33']);
});

test('UI-02 boundary: 22:58 made on the computer is kept (the page has no :60 to round to)', async ({ admin: page, world, evidence }) => {
  const { id } = await customerWithSet(world, 'qa-ui2f', ['--hour', '22', '--minute', '58']);
  expect(schedules(await storedSet(world, 'qa-ui2f', id)).map((x) => x.hour + ':' + x.minute), 'precondition: the computer made 22:58').toEqual(['22:58']);
  await openSet(page, 'qa-ui2f', 'Office files');
  await editorTab(page, 'Schedule');
  const shown = (await page.locator('.sched').first().locator('select').nth(0).inputValue()) + ':' + (await page.locator('.sched').first().locator('select').nth(1).inputValue());
  evidence.step('the page shows ' + shown + '; Save and exit without touching the time');
  await saveAndExit(page);
  expect(schedules(await storedSet(world, 'qa-ui2f', id)).map((x) => x.hour + ':' + x.minute), 'the time nobody changed must stay 22:58 (the page showed ' + shown + ')').toEqual(['22:58']);
});

test('UI-02 boundary: unticking every day of the only time never turns into a daily backup at a time nobody chose', async ({ admin: page, world, evidence }) => {
  const { id } = await customerWithSet(world, 'qa-ui2c', ['--hour', '3', '--minute', '0']);
  await openSet(page, 'qa-ui2c', 'Office files');
  await editorTab(page, 'Schedule');
  evidence.step('untick all seven days of the only time (03:00)');
  for (const d of ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']) await page.locator('.sched').first().locator('label', { hasText: d }).locator('input').setChecked(false);
  await page.getByRole('button', { name: 'Save and exit' }).click();
  await expect(page.locator('#toast')).toBeVisible();
  const toast = (await page.locator('#toast').innerText()).trim();
  const sch = schedules(await storedSet(world, 'qa-ui2c', id));
  evidence.step('toast "' + toast + '"; stored schedules ' + JSON.stringify(sch));
  // acceptable: the save is refused with a message and the set keeps 03:00 every day, or the set keeps no scheduled time.
  // NOT acceptable: a schedule the technician never chose (every day at 22:00, the product default).
  const refusedAndKept = !/^Saved$/.test(toast) && JSON.stringify(sch) === JSON.stringify([{ days: 'SMTWTFS', hour: '3', minute: '0', duration: '-1' }]);
  const noSchedule = sch.length === 0 || sch.every((x) => x.days === '-------');
  expect(refusedAndKept || noSchedule, 'after unticking every day the server keeps ' + JSON.stringify(sch) + ' (toast: "' + toast + '")').toBe(true);
});

test('UI-02 failure: a save the server refuses shows the reason, keeps the editor open, and stores nothing', async ({ admin: page, world, evidence }) => {
  const { id } = await customerWithSet(world, 'qa-ui2d');
  const before = await storedSet(world, 'qa-ui2d', id);
  await openSet(page, 'qa-ui2d', 'Office files');
  evidence.step('empty name + a bandwidth change → Save');
  await row(page, 'Set name').locator('input').fill('   ');
  await editorTab(page, 'Resources');
  await row(page, 'Upload limit').locator('input').fill('333');
  await page.getByRole('button', { name: 'Save and exit' }).click();
  await expect(page.locator('#toast.err')).toContainText('Give the set a name');
  await expect(page.getByRole('button', { name: 'Save and exit' }), 'the editor stays open with the typing').toBeVisible();
  await expect(row(page, 'Upload limit').locator('input')).toHaveValue('333');
  let now = await storedSet(world, 'qa-ui2d', id);
  expect([now.attrs.NAME, now.attrs.BANDWIDTH_KBPS], 'nothing of the refused save is stored').toEqual([before.attrs.NAME, before.attrs.BANDWIDTH_KBPS ?? '0']);

  evidence.step('server + local copy without a folder → Save');
  await editorTab(page, 'General');
  await row(page, 'Set name').locator('input').fill('Office files');
  await editorTab(page, 'Destination');
  await page.locator('.opt label', { hasText: 'The server + a local copy' }).click();
  await row(page, 'Local disk or network folder').locator('input').fill('');
  await page.getByRole('button', { name: 'Save and exit' }).click();
  await expect(page.locator('#toast.err')).toContainText('local disk or network folder');
  now = await storedSet(world, 'qa-ui2d', id);
  expect([now.attrs.DEST_MODE ?? 'SERVER', now.attrs.BANDWIDTH_KBPS ?? '0'], 'nothing of the refused save is stored').toEqual(['SERVER', before.attrs.BANDWIDTH_KBPS ?? '0']);
  expect(diskSet(world, 'qa-ui2d', id).attrs.DEST_MODE ?? 'SERVER').toBe('SERVER');

  evidence.step('the technician fixes the folder → Saved, and now both changes are stored');
  await row(page, 'Local disk or network folder').locator('input').fill('/srv/fixed');
  await saveAndExit(page);
  now = await storedSet(world, 'qa-ui2d', id);
  expect([now.attrs.NAME, now.attrs.DEST_MODE, now.attrs.BANDWIDTH_KBPS, kid(now, 'EXTRA_LOCAL_BACKUP').attrs.BACKUP_TO]).toEqual(['Office files', 'BOTH', '333', '/srv/fixed']);
});

test('UI-02 concurrency: two technicians edit different tabs of the same set — neither change is lost silently', async ({ admin: page, world, evidence, browser }) => {
  const { id } = await customerWithSet(world, 'qa-ui2e');
  const ctx2 = await browser.newContext(); const page2: Page = await ctx2.newPage();
  try {
    await signIn(page2, world);
    evidence.step('both technicians open the set');
    await openSet(page, 'qa-ui2e', 'Office files');
    await openSet(page2, 'qa-ui2e', 'Office files');
    evidence.step('technician A: upload limit 444 → Save');
    await editorTab(page, 'Resources');
    await row(page, 'Upload limit').locator('input').fill('444');
    await saveAndExit(page);
    expect((await storedSet(world, 'qa-ui2e', id)).attrs.BANDWIDTH_KBPS).toBe('444');
    evidence.step('technician B (opened before A saved): rename → Save');
    await row(page2, 'Set name').locator('input').fill('Renamed by B');
    await page2.getByRole('button', { name: 'Save and exit' }).click();
    await expect(page2.locator('#toast')).toBeVisible();
    const toastB = (await page2.locator('#toast').innerText()).trim();
    const s = await storedSet(world, 'qa-ui2e', id);
    evidence.step('stored: name "' + s.attrs.NAME + '", upload limit ' + s.attrs.BANDWIDTH_KBPS + '; B saw "' + toastB + '"');
    // acceptable: both changes kept, or B's save refused/warned (and A's change kept). Not acceptable: A's change silently gone.
    const bothKept = s.attrs.NAME === 'Renamed by B' && s.attrs.BANDWIDTH_KBPS === '444';
    const bWarned = !/^Saved$/.test(toastB) && s.attrs.BANDWIDTH_KBPS === '444';
    expect(bothKept || bWarned, 'A set the upload limit to 444 and B only renamed — the server now keeps ' + s.attrs.BANDWIDTH_KBPS + ' KB/s and B was told "' + toastB + '"').toBe(true);
  } finally { await ctx2.close(); }
});
