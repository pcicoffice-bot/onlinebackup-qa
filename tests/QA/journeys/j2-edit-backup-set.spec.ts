// J2 — A technician edits a customer's backup set on the admin site (name, schedule, versions kept, upload limit).
// The set is created where a customer creates it: on the computer, in the client program (the agent).
// Oracle: after a reload the site shows the new values, the server's stored set has them, and the computer gets them.
import { test, expect } from '../lib/fixtures';
import { goldenDataset } from '../lib/world';
import { openSet, tab, saveAndExit } from '../lib/ui';
import * as path from 'path';

test('J2 edit a backup set from the site: the change is saved and reaches the customer\'s computer', async ({ admin: page, world, evidence }) => {
  evidence.step('a customer with a computer and a backup set made in the client program');
  world.addCustomer('qa-acme');
  const ag = world.agent('qa-acme', 'ACME-PC1'); ag.register();
  const src = path.join(world.dir, 'data'); goldenDataset(src);
  ag.addSet('Office files', [src]);

  evidence.step('open the customer and the set');
  await openSet(page, 'qa-acme', 'Office files');

  evidence.step('General: rename');
  await tab(page, 'General');
  const name = page.locator('.form .fr').filter({ hasText: 'Set name' }).locator('input');
  await name.fill('Office files QA');
  evidence.step('Schedule: 03:35');
  await tab(page, 'Schedule');
  const sched = page.locator('.sched').first();
  await sched.locator('select').nth(0).selectOption('3');
  await sched.locator('select').nth(1).selectOption('35');
  evidence.step('Versions kept: 45 days');
  await tab(page, 'Versions kept');
  await page.locator('.form .fr').filter({ hasText: 'Keep versions for' }).locator('input').first().fill('45');
  evidence.step('Resources: upload limit 512 KB/s');
  await tab(page, 'Resources');
  await page.locator('.form .fr').filter({ hasText: 'Upload limit' }).locator('input').first().fill('512');
  evidence.step('Save and exit');
  await saveAndExit(page);

  evidence.step('reload the site and open the set again: the values are there');
  await page.reload();
  await openSet(page, 'qa-acme', 'Office files QA');
  await expect(page.locator('.form .fr').filter({ hasText: 'Set name' }).locator('input')).toHaveValue('Office files QA');
  await tab(page, 'Schedule');
  await expect(page.locator('.sched').first().locator('select').nth(0)).toHaveValue('3');
  await expect(page.locator('.sched').first().locator('select').nth(1)).toHaveValue('35');
  await tab(page, 'Versions kept');
  await expect(page.locator('.form .fr').filter({ hasText: 'Keep versions for' }).locator('input').first()).toHaveValue('45');
  await tab(page, 'Resources');
  await expect(page.locator('.form .fr').filter({ hasText: 'Upload limit' }).locator('input').first()).toHaveValue('512');

  evidence.step('the server stored them (its own record of the set)');
  const id = ag.sets()[0].id;
  const stored = (await world.adminApi('GET', 'users/qa-acme/sets/' + id)).text.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&amp;/g, '&');
  expect(stored).toMatch(/<BACKUP_SET [^>]*NAME="Office files QA"/);
  expect(stored).toMatch(/<DAILY_SCHEDULE [^>]*HOUR="3" MINUTE="35"/);
  expect(stored).toMatch(/<RETENTION_POLICY UNIT="DAYS" PERIOD="45"/);
  expect(stored).toMatch(/<BACKUP_SET [^>]*BANDWIDTH_KBPS="512"/);

  evidence.step('the customer\'s computer gets the new set (what the agent reads from the server)');
  expect(ag.sets().map((s) => s.name)).toContain('Office files QA');
});
