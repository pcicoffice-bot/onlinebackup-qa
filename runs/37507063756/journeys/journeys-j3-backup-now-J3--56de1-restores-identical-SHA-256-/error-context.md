# Instructions

- Following Playwright test failed.
- Explain why, be concise, respect Playwright best practices.
- Provide a snippet of code with the fix, if possible.

# Test info

- Name: journeys/j3-backup-now.spec.ts >> J3 Back up now from the site → a real backup whose content restores identical (SHA-256)
- Location: journeys/j3-backup-now.spec.ts:9:5

# Error details

```
Error: no "Succeeded" run of "Office files" on the tasks page after 4 minutes
```

# Page snapshot

```yaml
- generic [active] [ref=e1]:
  - banner [ref=e2]:
    - generic [ref=e3]:
      - generic [ref=e4]: ✦
      - generic [ref=e5]:
        - text: ITSguard Server Online
        - generic [ref=e6]: Backup server
    - combobox "Language" [ref=e7]:
      - option "English" [selected]
      - option "עברית"
    - generic [ref=e8]: admin
    - button "⟳ Version 0" [ref=e9] [cursor=pointer]
    - button "⛶ Full screen" [ref=e10] [cursor=pointer]
    - button "Sign out" [ref=e11] [cursor=pointer]
  - generic [ref=e13]:
    - navigation "Menu" [ref=e14]:
      - button "◧ Dashboard" [ref=e15] [cursor=pointer]:
        - generic [ref=e16]: ◧
        - text: Dashboard
      - button "▦ Customers and sets" [ref=e17] [cursor=pointer]:
        - generic [ref=e18]: ▦
        - text: Customers and sets
      - button "☰ All backup sets" [ref=e19] [cursor=pointer]:
        - generic [ref=e20]: ☰
        - text: All backup sets
      - button "✓ Tasks — 24 hours" [ref=e21] [cursor=pointer]:
        - generic [ref=e22]: ✓
        - text: Tasks — 24 hours
      - button "⟳ Active backups" [ref=e23] [cursor=pointer]:
        - generic [ref=e24]: ⟳
        - text: Active backups
      - generic [ref=e25]: Service
      - button "🎫 Service calls" [ref=e26] [cursor=pointer]:
        - generic [ref=e27]: 🎫
        - text: Service calls
      - generic [ref=e28]: AI
      - button "✦ Insights (AI)" [ref=e29] [cursor=pointer]:
        - generic [ref=e30]: ✦
        - text: Insights (AI)
      - generic [ref=e31]: Operations
      - button "≣ Logs" [ref=e32] [cursor=pointer]:
        - generic [ref=e33]: ≣
        - text: Logs
      - button "⎙ Reports" [ref=e34] [cursor=pointer]:
        - generic [ref=e35]: ⎙
        - text: Reports
      - button "⤺ Restore tests" [ref=e36] [cursor=pointer]:
        - generic [ref=e37]: ⤺
        - text: Restore tests
      - generic [ref=e38]: Server
      - button "⛁ Storage on the server" [ref=e39] [cursor=pointer]:
        - generic [ref=e40]: ⛁
        - text: Storage on the server
      - button "⚿ Licence" [ref=e41] [cursor=pointer]:
        - generic [ref=e42]: ⚿
        - text: Licence
      - generic [ref=e43]: Settings
      - button "✚ Defaults for new customers" [ref=e44] [cursor=pointer]:
        - generic [ref=e45]: ✚
        - text: Defaults for new customers
      - button "⚙ Policies and templates" [ref=e46] [cursor=pointer]:
        - generic [ref=e47]: ⚙
        - text: Policies and templates
      - button "✉ E-mails and alerts" [ref=e48] [cursor=pointer]:
        - generic [ref=e49]: ✉
        - text: E-mails and alerts
      - button "👥 Administrators" [ref=e50] [cursor=pointer]:
        - generic [ref=e51]: 👥
        - text: Administrators
      - button "🎫 Service call settings" [ref=e52] [cursor=pointer]:
        - generic [ref=e53]: 🎫
        - text: Service call settings
      - button "🔒 Security and sign-in" [ref=e54] [cursor=pointer]:
        - generic [ref=e55]: 🔒
        - text: Security and sign-in
      - button "◷ Clock and time zone" [ref=e56] [cursor=pointer]:
        - generic [ref=e57]: ◷
        - text: Clock and time zone
      - button "⇄ Integrations" [ref=e58] [cursor=pointer]:
        - generic [ref=e59]: ⇄
        - text: Integrations
      - button "⬇ Client software" [ref=e60] [cursor=pointer]:
        - generic [ref=e61]: ⬇
        - text: Client software
      - button "✍ Contract and sign-up" [ref=e62] [cursor=pointer]:
        - generic [ref=e63]: ✍
        - text: Contract and sign-up
      - button "◐ Branding" [ref=e64] [cursor=pointer]:
        - generic [ref=e65]: ◐
        - text: Branding
    - main [ref=e66]:
      - generic [ref=e67]:
        - generic [ref=e69]:
          - generic [ref=e70]:
            - heading "Tasks — 24 hours" [level=1] [ref=e71]
            - paragraph [ref=e72]: Every backup, restore and restore test of the last 24 hours, with its status and details.
          - combobox [ref=e74]:
            - option "24 hours" [selected]
            - option "3 days"
            - option "7 days"
        - generic [ref=e75]:
          - generic [ref=e76]:
            - generic [ref=e77]: Tasks
            - generic [ref=e78]: "2"
          - generic [ref=e79]:
            - generic [ref=e80]: Succeeded
            - generic [ref=e81]: "2"
          - generic [ref=e82]:
            - generic [ref=e83]: Warnings
            - generic [ref=e84]: "0"
          - generic [ref=e85]:
            - generic [ref=e86]: Failed
            - generic [ref=e87]: "0"
        - generic [ref=e88]:
          - generic [ref=e89]:
            - generic [ref=e90]:
              - button "All (2)" [ref=e91] [cursor=pointer]
              - button "Succeeded (2)" [ref=e92] [cursor=pointer]
              - button "Warnings (0)" [ref=e93] [cursor=pointer]
              - button "Failed (0)" [ref=e94] [cursor=pointer]
              - button "Stopped (0)" [ref=e95] [cursor=pointer]
            - 'textbox "Filter: customer, computer, set…" [ref=e96]'
          - table [ref=e99]:
            - rowgroup [ref=e100]:
              - row [ref=e101]:
                - columnheader "When" [ref=e102]
                - columnheader "Customer" [ref=e103]
                - columnheader "Computer" [ref=e104]
                - columnheader "Set" [ref=e105]
                - columnheader "Kind" [ref=e106]
                - columnheader "Status" [ref=e107]
                - columnheader "New / changed / deleted" [ref=e108]
                - columnheader "Sent" [ref=e109]
            - rowgroup [ref=e110]:
              - row [ref=e111] [cursor=pointer]:
                - cell "06/10/2026 18:01" [ref=e112]
                - cell "qa-beta" [ref=e113]
                - cell "BETA-PC1" [ref=e114]
                - cell "Office files" [ref=e115]
                - cell "Restore test" [ref=e116]
                - cell "✓ Succeeded" [ref=e117]
                - cell "5 / 5 / 0" [ref=e119]
                - cell "0 B" [ref=e120]
              - row [ref=e121] [cursor=pointer]:
                - cell "06/10/2026 18:01" [ref=e122]
                - cell "qa-beta" [ref=e123]
                - cell "BETA-PC1" [ref=e124]
                - cell "Office files" [ref=e125]
                - cell "Backup" [ref=e126]
                - cell "✓ Succeeded" [ref=e127]
                - cell "163 / 0 / 0" [ref=e129]
                - cell "23.1 MB" [ref=e130]
```

# Test source

```ts
  1  | // What a person does on the admin site, step by step (shared by the journeys).
  2  | import { Page, expect } from '@playwright/test';
  3  | 
  4  | export async function openCustomer(page: Page, login: string) {
  5  |   await page.locator('.rail button[data-k="cust"]').click();
  6  |   await page.getByRole('row').filter({ hasText: login }).first().click();
  7  |   await expect(page.locator('main h1').first()).toBeVisible();
  8  | }
  9  | 
  10 | export async function openSet(page: Page, login: string, setName: string) {
  11 |   await openCustomer(page, login);
  12 |   await page.getByRole('row').filter({ hasText: setName }).first().click();
  13 |   await expect(page.locator('.tabs[role=tablist]').last()).toBeVisible();
  14 | }
  15 | 
  16 | export async function tab(page: Page, label: string) { await page.locator('.tabs[role=tablist]').last().locator('button', { hasText: label }).click(); }
  17 | 
  18 | export async function saveAndExit(page: Page) {
  19 |   await page.getByRole('button', { name: 'Save and exit' }).click();
  20 |   await expect(page.locator('#toast').filter({ hasText: 'Saved' })).toBeVisible();
  21 | }
  22 | 
  23 | /** "Back up now" in the set's header, as a technician does. */
  24 | export async function backUpNow(page: Page) {
  25 |   await page.getByRole('button', { name: '▶ Back up now', exact: true }).first().click();
  26 |   await expect(page.locator('#toast').filter({ hasText: /started/ })).toBeVisible();
  27 | }
  28 | 
  29 | /** The tasks page shows a run of the set with this status; returns its row text. */
  30 | export async function waitForTask(page: Page, setName: string, status: 'Succeeded' | 'Failed' | 'Warnings', minutes = 4) {
  31 |   const until = Date.now() + minutes * 60000;
  32 |   while (Date.now() < until) {
  33 |     await page.locator('.rail button[data-k="tasks"]').click();
  34 |     await page.waitForLoadState('networkidle');
  35 |     const row = page.getByRole('row').filter({ hasText: setName }).filter({ hasText: status }).filter({ has: page.getByRole('cell', { name: 'Backup', exact: true }) });
  36 |     if (await row.count()) return (await row.first().innerText());
  37 |     await page.waitForTimeout(5000);
  38 |   }
> 39 |   throw new Error('no "' + status + '" run of "' + setName + '" on the tasks page after ' + minutes + ' minutes');
     |         ^ Error: no "Succeeded" run of "Office files" on the tasks page after 4 minutes
  40 | }
  41 | 
```