# Night QA agent M — time boundaries and races (snapshot 4c6deea)

Tests: tests/Tests/NightM_ScheduleBoundaryTests.cs, NightM_TimeRulesTests.cs, NightM_RaceTests.cs
(collection "NightM-TZ", not parallel: the schedule tests switch the process time zone with TZ + TimeZoneInfo.ClearCachedData
and restore it; the switch is asserted by TimeZoneInfo.Local.Id).

Run: `cd tests/Tests && dotnet test --filter "FullyQualifiedName~NightM_<Class>"` (one class at a time).

## Results (each run twice or more)
- NightM_ScheduleBoundaryTests: 23 pass, 3 fail (deterministic, identical in 2 runs)
  - SpringForward(... runMissed: False) Jerusalem 27 Mar 02:30 and London 29 Mar 01:30: 0 runs that day.
  - ASetDueWhileTheServiceIsBusy(runMissed: False): set B ran 0 of 3 nights.
  - observed (pass): spring forward with RUN_MISSED=Y runs at 03:05 (Jerusalem) / 02:05 (London) — the missed-run delay is
    applied although the computer was on; a 02:50 slot runs at 03:00 (within 15 wall-minutes). Fall back: the first
    occurrence runs, the second does not.
- NightM_TimeRulesTests: 18 pass, 1 fail (ReportCannotRun after a successful run 9 h earlier: never reaches the server)
  - mutation proof: moving every "one tick/second before" probe onto the boundary made 15/19 fail (run once, reverted).
- NightM_RaceTests: 6 pass, 4 fail
  - CommitAndVerifyAll: 20/50, 19/50 (+ a first run, failed too) — sound objects quarantined, the new point lost.
  - RestoreOfTheLatestPoint deterministic list→commit→fetch: 30/30 files fail; concurrent: 32/50 and 33/50 iterations.
  - TwoMaintenanceRuns: 30/30 iterations sent 2 MISSED BACKUP alerts (2 runs).
  - pass: commit vs retention, Touch vs ExpireStale (both outcomes seen: alive 24 / closed 26), two Begins, two admin
    saves, schedule vs "back up now" (in-process), two agent processes (the second process was refused every time).

## What the code does now (no answer assumed)
- GFS buckets (bug 77, owner decision pending): RetentionPolicy.KeepLatestPer (src/Core/Formats.cs:142-146) buckets on
  RunId.Parse = UTC: day = UTC date, week = UTC week starting SUNDAY (d.AddDays(-(int)d.DayOfWeek)), month/quarter/year UTC.
- DAYS retention: kept when (now - id) <= Period days, in UTC 24-hour periods (exactly N days is kept, N days + 1 s dropped).
- Run ids are UTC (01:30 on 1 Jan in Israel is "…12-31-23-30-00").
- Server daily views (Api.cs:984 daily report, BackupChecks snapshot file names, RunLog file per day) use UTC dates.
- LogDue measures from last-log.txt, written at the END of the previous log run (AgentApp.cs:203): the interval drifts by the
  run duration.
- RestoreTestDue writes the 30-day mark BEFORE the test runs (AgentApp.cs:316): a test that throws (network) is not
  retried for 30 days.
- ReportCannotRun leaves last-attempt.txt untouched when it suppresses a report, so Due's 15-minute retry throttle
  (AgentApp.cs:473) uses an old attempt and the "cannot run" path is retried every minute.
