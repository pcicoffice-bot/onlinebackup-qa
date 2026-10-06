// The journey fixtures: a fresh world per test, a signed-in administrator page, and the evidence of every failure.
import { test as base, expect, Page, TestInfo } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { World, ADMIN, totp } from './world';

type Fixtures = { world: World; evidence: Evidence; admin: Page };

export class Evidence {
  console: string[] = []; network: string[] = []; steps: string[] = [];
  watch(page: Page) {
    page.on('console', (m) => { if (m.type() === 'error') this.console.push(m.text() + (m.location()?.url ? ' @ ' + m.location().url : '')); });
    page.on('pageerror', (e) => this.console.push('pageerror: ' + (e.message || e)));
    page.on('response', (r) => { if (r.status() >= 400) this.network.push(r.status() + ' ' + r.request().method() + ' ' + r.url()); });
    page.on('requestfailed', (r) => this.network.push('FAILED ' + r.method() + ' ' + r.url() + ' ' + (r.failure()?.errorText || '')));
  }
  step(s: string) { this.steps.push(new Date().toISOString().slice(11, 19) + ' ' + s); }
}

/** Signs in through the page as a person does (password + authenticator code). */
export async function signIn(page: Page, world: World) {
  await page.goto(world.url + '/admin');
  await page.locator('form.login input[autocomplete=username]').fill(ADMIN.login);
  await page.locator('form.login input[type=password]').fill(ADMIN.password);
  await page.locator('form.login input.otp').fill(totp(ADMIN.totp));
  await page.locator('form.login button[type=submit]').click();
  await expect(page.locator('.rail button[data-k]').first()).toBeVisible();
}

export const test = base.extend<Fixtures>({
  world: async ({}, use, info) => {
    const w = await new World().start();
    await use(w);
    await w.stop();
    if (info.status !== info.expectedStatus) {
      await info.attach('server.log', { path: w.serverLog });
      for (const a of w.agents) if (fs.existsSync(a.log)) await info.attach('agent-' + a.computer + '.log', { path: a.log });
      await info.attach('system.xml', { path: path.join(w.sys, 'conf', 'system.xml') }).catch(() => {});
    } else if (!process.env.QA_KEEP) fs.rmSync(w.dir, { recursive: true, force: true });
  },
  evidence: async ({ page }, use, info) => {
    const e = new Evidence(); e.watch(page);
    await use(e);
    if (info.status !== info.expectedStatus) bugReport(info, e);
  },
  admin: async ({ page, world, evidence }, use) => {
    evidence.step('sign in as the administrator');
    await signIn(page, world);
    await use(page);
  },
});
export { expect };

/** BUG-REPORT: one Markdown file per failure, with everything needed to reproduce it in the morning. */
function bugReport(info: TestInfo, e: Evidence) {
  const dir = path.join(__dirname, '..', 'reports', 'bugs'); fs.mkdirSync(dir, { recursive: true });
  const name = info.titlePath.slice(1).join(' › ');
  const severity = /restore|backup|kill|crash|update|install/i.test(name) ? 'CRITICAL' : 'MAJOR';
  const md = [
    '# ' + name, '',
    '| | |', '|---|---|',
    '| Severity | ' + severity + ' |', '| Journey | ' + info.file.split(/[\\/]/).slice(-2).join('/') + ' |', '| When | ' + new Date().toISOString() + ' |',
    '| Commit | ' + (process.env.QA_COMMIT || '') + ' |', '',
    '## Steps done (the last one is where it failed)', '', ...e.steps.map((s, i) => (i + 1) + '. ' + s), '',
    '## Expected / actual', '', '```', (info.errors.map((x) => x.message || '').join('\n\n') || '').slice(0, 6000), '```', '',
    '## Console errors', '', ...(e.console.length ? e.console.map((c) => '- ' + c) : ['(none)']), '',
    '## Failed API calls', '', ...(e.network.length ? e.network.map((c) => '- ' + c) : ['(none)']), '',
    '## Attached', '', 'Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `' + path.relative(path.join(__dirname, '..'), info.outputDir) + '`.', '',
  ].join('\n');
  fs.writeFileSync(path.join(dir, name.replace(/[^A-Za-z0-9א-ת]+/g, '-').slice(0, 100) + '.md'), md);
}
