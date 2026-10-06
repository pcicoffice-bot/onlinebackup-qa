// The seed of the Playwright Test Agents (Planner / Generator / Healer): the state every generated test starts from.
// A fresh real server, a signed-in administrator, a customer "qa-seed" with a computer, a backup set on the golden
// dataset, one real backup, and the computer's backup service running. Generated tests use the same fixtures.
import { test, expect } from './lib/fixtures';
import { goldenDataset } from './lib/world';
import * as path from 'path';

test('seed', async ({ admin: page, world }) => {
  world.addCustomer('qa-seed');
  const ag = world.agent('qa-seed', 'SEED-PC1'); ag.register();
  const src = path.join(world.dir, 'data'); goldenDataset(src);
  const id = ag.addSet('Office files', [src]);
  expect(ag.backup(id).out).toMatch(/^BS_STOP_SUCCESS /m);
  ag.startService();
  await page.reload();
  await expect(page.locator('.rail button[data-k]').first()).toBeVisible();
});
