# seed

| | |
|---|---|
| Severity | MAJOR |
| Journey | QA/seed.spec.ts |
| When | 2026-10-06T09:40:13.283Z |
| Commit |  |

## Steps done (the last one is where it failed)

1. 09:22:06 sign in as the administrator

## Expected / actual

```

```

## Console errors

- Failed to load resource: the server responded with a status of 404 (Not Found)
- Failed to load resource: the server responded with a status of 404 (Not Found)
- Failed to load resource: the server responded with a status of 404 (Not Found)
- Failed to load resource: the server responded with a status of 404 (Not Found)
- Failed to load resource: the server responded with a status of 404 (Not Found)

## Failed API calls

- FAILED GET http://localhost:43471/i18n/fonts/inter-latin.woff2 net::ERR_ABORTED
- FAILED GET http://localhost:43471/i18n/fonts/heebo-hebrew.woff2 net::ERR_ABORTED
- FAILED GET http://localhost:43471/i18n/fonts/inter-latin.woff2 net::ERR_ABORTED
- FAILED GET http://localhost:43471/i18n/fonts/heebo-hebrew.woff2 net::ERR_ABORTED

## Attached

Screenshot, video and Playwright trace (`npx playwright show-trace <trace.zip>`), server log, agent log, system.xml: in `artifacts/test-results/seed-seed`.
