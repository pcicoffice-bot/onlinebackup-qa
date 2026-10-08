# QA shards — parallel runners, same depth

Purpose: shorten the **wait**, never the depth. Independent xUnit tests are split over isolated GitHub runners of the
public QA mirror (`.github/workflows/qa-shards.yml`, started by hand). Claude only orchestrates; the runners do the work.

| part | what it does |
|---|---|
| `plan.py` | lists the tests, selects by name parts, splits **by class** over N shards (balanced by `durations-<os>.json` when present), x R repetitions |
| `run-shard.sh` | one job: the shard's tests exactly as the serial run runs them; `.trx`, console log, `meta.json` always written |
| `aggregate.py` | one verdict. PASS only when every planned job reports the test passed. No `.trx` (cancelled / not started / crashed), test missing, skipped, or the test said `NOT TESTED` → **NOT TESTED** |
| `selftest.py` | proves the two above on made-up results before every plan |
| `keep.sh` | the verdict to `qa-evidence` → `runs/shards-<run id>/` (summary.md, results.json, durations.json, expected.json) |

**Isolation.** Every matrix job is a fresh virtual machine used by that job alone: own ports, temp folders,
repositories, processes and state. Within a job the tests run one class after another as in the serial run
(`tests/Tests/xunit.runner.json`). The temp folder is deliberately not moved (that could change what a test finds).
Shards never write to the repository; only the aggregator writes, to `qa-evidence`.

**Stays serial** (not here): chains that keep state on one machine — C1 install → backup → real restart → restore
(`qa.yml` windows / windows-reboot), Playwright journeys that share a seeded server.

**Proof before scaling.** First run: 2–4 shards with `control: true` — the same tests also run serially in one job, and
the aggregator compares every test's result (SAME / DIFFERENT). Only after SAME, repetitions (e.g. an intermittent bug x15)
and more shards; `max_parallel` leaves room for the qa.yml runs (GitHub Free, public repository: 20 jobs at once).

**Exposure.** No secrets are used (the aggregator uses the run's own `GITHUB_TOKEN`); the mirror is built by
`tools/publish-public.sh` (owner files excluded, demo names replaced, secret and address scan). Results contain only
test output of made-up data.
