// What a person does on the admin site, step by step (shared by the journeys).
import { Page, expect } from '@playwright/test';

export async function openCustomer(page: Page, login: string) {
  await page.locator('.rail button[data-k="cust"]').click();
  await page.getByRole('row').filter({ hasText: login }).first().click();
  await expect(page.locator('main h1').first()).toBeVisible();
}

export async function openSet(page: Page, login: string, setName: string) {
  await openCustomer(page, login);
  await page.getByRole('row').filter({ hasText: setName }).first().click();
  await expect(page.locator('.tabs[role=tablist]').last()).toBeVisible();
}

export async function tab(page: Page, label: string) { await page.locator('.tabs[role=tablist]').last().locator('button', { hasText: label }).click(); }

export async function saveAndExit(page: Page) {
  await page.getByRole('button', { name: 'Save and exit' }).click();
  await expect(page.locator('#toast').filter({ hasText: 'Saved' })).toBeVisible();
}

/** "Back up now" in the set's header, as a technician does. */
export async function backUpNow(page: Page) {
  await page.getByRole('button', { name: '▶ Back up now', exact: true }).first().click();
  await expect(page.locator('#toast').filter({ hasText: /started/ })).toBeVisible();
}

/** The tasks page shows a run of the set with this status; returns its row text. */
export async function waitForTask(page: Page, setName: string, status: 'Succeeded' | 'Failed' | 'Warnings', minutes = 4) {
  const until = Date.now() + minutes * 60000;
  let seen: string[] = [];
  while (Date.now() < until) {
    await page.locator('.rail button[data-k="tasks"]').click();
    await page.waitForLoadState('networkidle');
    // Q10 (CI run 5): the rows are drawn after the page's own request; "networkidle" can come before it, and a count
    // taken at once saw no row at every try while the screenshot showed the succeeded run. Wait for the row itself.
    const row = page.getByRole('row').filter({ hasText: setName }).filter({ hasText: status }).filter({ has: page.getByRole('cell', { name: 'Backup', exact: true }) });
    if (await row.first().waitFor({ state: 'visible', timeout: 8000 }).then(() => true, () => false)) return (await row.first().innerText());
    seen = await page.getByRole('row').allInnerTexts();
    await page.waitForTimeout(5000);
  }
  throw new Error('no "' + status + '" run of "' + setName + '" on the tasks page after ' + minutes + ' minutes; rows seen last: ' + JSON.stringify(seen.map((r) => r.replace(/\s+/g, ' '))));
}
