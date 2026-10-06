# J1 sign-in, every menu page opens without errors, reload keeps the session, sign-out ends it on the server

| | |
|---|---|
| Severity | MAJOR |
| Journey | journeys/j1-login-navigation.spec.ts |
| When | 2026-10-06T09:24:53.790Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 09:24:50 open the admin site
2. 09:24:50 wrong password → the error is shown on the form, the form stays, the password is emptied
3. 09:24:50 right password + authenticator code → the menu
4. 09:24:51 every page of the menu
5. 09:24:51 open "◧Dashboard" (dash)
6. 09:24:52 open "▦Customers and sets" (cust)
7. 09:24:52 open "☰All backup sets" (allsets)
8. 09:24:52 open "✓Tasks — 24 hours" (tasks)
9. 09:24:52 open "⟳Active backups" (live)
10. 09:24:52 open "🎫Service calls" (tickets)
11. 09:24:52 open "✦Insights (AI)" (ai)
12. 09:24:52 open "≣Logs" (logs)
13. 09:24:52 open "⎙Reports" (reports)
14. 09:24:52 open "⤺Restore tests" (restoretests)
15. 09:24:52 open "⛁Storage on the server" (storage)
16. 09:24:52 open "⚿Licence" (license)
17. 09:24:52 open "✚Defaults for new customers" (defaults)
18. 09:24:52 open "⚙Policies and templates" (policies)
19. 09:24:53 open "✉E-mails and alerts" (notify)
20. 09:24:53 open "👥Administrators" (admins)
21. 09:24:53 open "🎫Service call settings" (tset)
22. 09:24:53 open "🔒Security and sign-in" (security)
23. 09:24:53 open "◷Clock and time zone" (time)
24. 09:24:53 open "⇄Integrations" (integr)
25. 09:24:53 open "⬇Client software" (client)
26. 09:24:53 open "✍Contract and sign-up" (contract)
27. 09:24:53 open "◐Branding" (brand)

## Expected / actual

```
Error: no console errors on any page (fonts, scripts, styles included)

[2mexpect([22m[31mreceived[39m[2m).[22mtoEqual[2m([22m[32mexpected[39m[2m) // deep equality[22m

[32m- Expected  - 1[39m
[31m+ Received  + 3[39m

[32m- Array [][39m
[31m+ Array [[39m
[31m+   "Failed to load resource: the server responded with a status of 404 (Not Found) @ http://localhost:39999/favicon.ico",[39m
[31m+ ][39m
```

## Console errors

- Failed to load resource: the server responded with a status of 404 (Not Found) @ http://localhost:39999/favicon.ico
- Failed to load resource: the server responded with a status of 401 (Unauthorized) @ http://localhost:39999/api/admin/login

## Failed API calls

- 401 POST http://localhost:39999/api/admin/login

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/journeys-j1-login-navigati-91945-n-out-ends-it-on-the-server`.
