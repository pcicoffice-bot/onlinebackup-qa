import { defineConfig } from '@playwright/test';

// OnlineBackup QA — black-box journeys against the real product (see README.md).
// One worker: each journey starts its own server and agent, and some kill processes on purpose.
export default defineConfig({
  testDir: '.',
  testMatch: ['journeys/**/*.spec.ts', 'regression/**/*.spec.ts', 'failure-recovery/**/*.spec.ts', 'e2e/**/*.spec.ts', 'web/**/*.spec.ts', 'seed.spec.ts'],
  timeout: 15 * 60 * 1000,
  expect: { timeout: 20000 },
  workers: 1,
  retries: 0,                       // no blind retries: a test that fails once is a finding (see README: flaky tests)
  globalSetup: './lib/global-setup.ts',
  outputDir: 'artifacts/test-results',
  reporter: [['list'], ['json', { outputFile: 'reports/last-run.json' }], ['html', { outputFolder: 'reports/html', open: 'never' }]],
  use: {
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    locale: 'en-US',
    viewport: { width: 1366, height: 860 },
    // a Chromium already on the machine (CI installs its own): CHROMIUM=/path/chrome
    launchOptions: { executablePath: process.env.CHROMIUM || (require('fs').existsSync('/opt/pw-browsers/chromium-1194/chrome-linux/chrome') ? '/opt/pw-browsers/chromium-1194/chrome-linux/chrome' : undefined) },
  },
});
