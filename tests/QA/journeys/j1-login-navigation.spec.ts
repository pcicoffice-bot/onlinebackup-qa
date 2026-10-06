// J1 — Web sign-in and navigation, as an administrator does it every morning.
// Oracle: what the browser shows AND what the server says about the session (never only "the page answered 200").
import { test, expect, signIn } from '../lib/fixtures';
import { ADMIN, totp, freshTotp } from '../lib/world';

test('J1 sign-in, every menu page opens without errors, reload keeps the session, sign-out ends it on the server', async ({ page, world, evidence }) => {
  evidence.step('open the admin site');
  await page.goto(world.url + '/admin');
  await expect(page.locator('form.login')).toBeVisible();

  evidence.step('wrong password → the error is shown on the form, the form stays, the password is emptied');
  await page.locator('form.login input[autocomplete=username]').fill(ADMIN.login);
  await page.locator('form.login input[type=password]').fill('Wrong-Pass-999');
  await page.locator('form.login input.otp').fill(await freshTotp(ADMIN.totp, world.url));
  await page.locator('form.login button[type=submit]').click();
  await expect(page.locator('.loginerr')).toBeVisible();
  await expect(page.locator('.loginerr')).not.toHaveText('');
  await expect(page.locator('form.login input[type=password]')).toHaveValue('');
  await expect(page.locator('.rail')).toHaveCount(0);

  evidence.step('right password + authenticator code → the menu');
  await signIn(page, world);
  const session = await page.evaluate(() => sessionStorage.getItem('obAdmin') || localStorage.getItem('obAdminLocal'));
  expect(session, 'the page keeps a session').toBeTruthy();
  const me = await fetch(world.url + '/api/admin/me', { headers: { 'X-Session': session! } });
  expect(me.status, 'the server knows the session').toBe(200);

  evidence.step('every page of the menu');
  const pages = await page.$$eval('.rail button[data-k]', (bs) => bs.map((b) => ({ k: (b as HTMLElement).dataset.k!, name: (b.textContent || '').trim() })));
  expect(pages.length).toBeGreaterThan(5);
  const before = { console: evidence.console.length, network: evidence.network.length };
  for (const p of pages) {
    evidence.step('open "' + p.name + '" (' + p.k + ')');
    await page.locator('.rail button[data-k="' + p.k + '"]').click();
    await page.waitForLoadState('networkidle');
    await expect(page.locator('main h1, main h2').first(), 'page "' + p.name + '" shows a title').toBeVisible();
    await expect(page.locator('main'), 'page "' + p.name + '" shows no raw error').not.toContainText(/undefined|NaN|\[object Object\]|Exception/);
  }
  // from the very first page (the sign-in page too), except the one refusal we caused on purpose (wrong password → 401)
  const wanted401 = (m: string) => /\b401\b/.test(m) && /\/api\/admin\/login(\b|$)/.test(m);   // Agent L: only the sign-in refusal, never any other 401
  expect(evidence.console.filter((m) => !wanted401(m)), 'no console errors on any page (fonts, scripts, styles included)').toEqual([]);
  expect(evidence.network.filter((m) => !/^401 POST .*\/api\/admin\/login$/.test(m)), 'no failed request on any page').toEqual([]);

  evidence.step('reload → still signed in');
  await page.reload();
  await expect(page.locator('.rail button[data-k]').first()).toBeVisible();
  await expect(page.locator('form.login')).toHaveCount(0);

  evidence.step('sign out → the sign-in form; the old session no longer works on the server');
  const token = await page.evaluate(() => sessionStorage.getItem('obAdmin') || localStorage.getItem('obAdminLocal'));
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page.locator('form.login')).toBeVisible();
  await page.waitForTimeout(500);
  const after = await fetch(world.url + '/api/admin/me', { headers: { 'X-Session': token! } });
  expect(after.status, 'the server ended the session').toBe(401);
  await page.reload();
  await expect(page.locator('form.login')).toBeVisible();
});
