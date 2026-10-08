# windows-latest, select 'OnlineBackup.Tests.MissedScheduleAwareTests OnlineBackup.Tests.AgentTlsComponentTests OnlineBackup.Tests.TlsTests OnlineBackup.Tests.NightM_TimeRulesTests', 1 shards x 2 - run 37755906734 (2a8c32eac871f9991e2246f8e12439102e7b9da1)

**33 tests: 32 PASS, 0 FAIL, 1 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `02ec8bbabff6d03c01277919067a642261b56266539be143153a21d37b3bf3a9` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 33 | GitHub Actions 1000001731 | 2 |
| s01-r02 | results | 33 | GitHub Actions 1000001732 | 2 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.TlsTests.Net40AgentUnderMonoUsesTheBuiltinTls | NOT TESTED | 0 PASS / 0 FAIL / 2 NOT TESTED of 2 - s01-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs mono and the .NET 4.0 agent build; s01-r02: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs mono and the .NET 4.0 agent build |

11 test(s) of this build are not yet in tools/qa-shards/known-tests.txt (add them so that a later disappearance is caught): OnlineBackup.Tests.AgentTlsComponentTests.TheAgentsClient_OffersTls12_NeverTls10Or11, OnlineBackup.Tests.MissedScheduleAwareTests.ADailySet_Unchanged_48Hours, OnlineBackup.Tests.MissedScheduleAwareTests.AWeeklyDayPlusASecondDailyTime_IsDaily_48HoursAsBefore, OnlineBackup.Tests.MissedScheduleAwareTests.AWeeklySet_IsNotReportedTwoDaysLater_OnlyAfterItsWeekPlusOneDay, OnlineBackup.Tests.MissedScheduleAwareTests.LongestScheduleGap(days: "-------", h: 22, m: 0, moreDays: null, hours: 0), OnlineBackup.Tests.MissedScheduleAwareTests.LongestScheduleGap(days: "-M-----", h: 22, m: 0, moreDays: "SMTWTFS", hours: 24), OnlineBackup.Tests.MissedScheduleAwareTests.LongestScheduleGap(days: "-M-----", h: 22, m: 0, moreDays: null, hours: 168), OnlineBackup.Tests.MissedScheduleAwareTests.LongestScheduleGap(days: "-M--T--", h: 22, m: 0, moreDays: null, hours: 96), OnlineBackup.Tests.MissedScheduleAwareTests.LongestScheduleGap(days: "SMTWTFS", h: 22, m: 0, moreDays: null, hours: 24), OnlineBackup.Tests.MissedScheduleAwareTests.TheDataProtectionReport_MarksLateByTheSetsSchedule, OnlineBackup.Tests.MissedScheduleAwareTests.TwoDaysAWeek_TheLongestGapCounts

## Repetitions

| test | PASS | FAIL | NOT TESTED |
|---|---|---|---|
| OnlineBackup.Tests.AgentTlsComponentTests.TheAgentsClient_OffersTls12_NeverTls10Or11 | 2 | 0 | 0 |
| OnlineBackup.Tests.MissedScheduleAwareTests.ADailySet_Unchanged_48Hours | 2 | 0 | 0 |
| OnlineBackup.Tests.MissedScheduleAwareTests.AWeeklyDayPlusASecondDailyTime_IsDaily_48HoursAsBefore | 2 | 0 | 0 |
| OnlineBackup.Tests.MissedScheduleAwareTests.AWeeklySet_IsNotReportedTwoDaysLater_OnlyAfterItsWeekPlusOneDay | 2 | 0 | 0 |
| OnlineBackup.Tests.MissedScheduleAwareTests.LongestScheduleGap(days: "-------", h: 22, m: 0, moreDays: null, hours: 0) | 2 | 0 | 0 |
| OnlineBackup.Tests.MissedScheduleAwareTests.LongestScheduleGap(days: "-M-----", h: 22, m: 0, moreDays: "SMTWTFS", hours: 24) | 2 | 0 | 0 |
| OnlineBackup.Tests.MissedScheduleAwareTests.LongestScheduleGap(days: "-M-----", h: 22, m: 0, moreDays: null, hours: 168) | 2 | 0 | 0 |
| OnlineBackup.Tests.MissedScheduleAwareTests.LongestScheduleGap(days: "-M--T--", h: 22, m: 0, moreDays: null, hours: 96) | 2 | 0 | 0 |
| OnlineBackup.Tests.MissedScheduleAwareTests.LongestScheduleGap(days: "SMTWTFS", h: 22, m: 0, moreDays: null, hours: 24) | 2 | 0 | 0 |
| OnlineBackup.Tests.MissedScheduleAwareTests.TheDataProtectionReport_MarksLateByTheSetsSchedule | 2 | 0 | 0 |
| OnlineBackup.Tests.MissedScheduleAwareTests.TwoDaysAWeek_TheLongestGapCounts | 2 | 0 | 0 |
| OnlineBackup.Tests.NightM_TimeRulesTests.Lease_OpenAtExactlyFiveMinutes_ClosedOneTickLater_AcrossMidnight(h: 12, m: 0) | 2 | 0 | 0 |
| OnlineBackup.Tests.NightM_TimeRulesTests.Lease_OpenAtExactlyFiveMinutes_ClosedOneTickLater_AcrossMidnight(h: 23, m: 58) | 2 | 0 | 0 |
| OnlineBackup.Tests.NightM_TimeRulesTests.LogDue_FalseOneTickBeforeTheInterval_TrueAtIt(interval: 15, y: 2026, mo: 10, d: 24, h: 22, mi: 55) | 2 | 0 | 0 |
| OnlineBackup.Tests.NightM_TimeRulesTests.LogDue_FalseOneTickBeforeTheInterval_TrueAtIt(interval: 15, y: 2026, mo: 12, d: 31, h: 23, mi: 50) | 2 | 0 | 0 |
| OnlineBackup.Tests.NightM_TimeRulesTests.LogDue_FalseOneTickBeforeTheInterval_TrueAtIt(interval: 60, y: 2028, mo: 2, d: 28, h: 23, mi: 30) | 2 | 0 | 0 |
| OnlineBackup.Tests.NightM_TimeRulesTests.MissedBackupAlert_At48HoursExactly_ThenEvery24Hours | 2 | 0 | 0 |
| OnlineBackup.Tests.NightM_TimeRulesTests.ReportCannotRun_ReachesTheServer_OncePer20Hours | 2 | 0 | 0 |
| OnlineBackup.Tests.NightM_TimeRulesTests.ReportCannotRun_TheFirstFailureAfterASuccessfulRunEarlierThatDay_ReachesTheServer | 2 | 0 | 0 |
| OnlineBackup.Tests.NightM_TimeRulesTests.RestoreTestDue_ThirtyDaysExactly_AcrossMonthLeapYearAndDst(y: 2026, mo: 1, d: 31) | 2 | 0 | 0 |
| OnlineBackup.Tests.NightM_TimeRulesTests.RestoreTestDue_ThirtyDaysExactly_AcrossMonthLeapYearAndDst(y: 2026, mo: 10, d: 1) | 2 | 0 | 0 |
| OnlineBackup.Tests.NightM_TimeRulesTests.RestoreTestDue_ThirtyDaysExactly_AcrossMonthLeapYearAndDst(y: 2026, mo: 12, d: 15) | 2 | 0 | 0 |
| OnlineBackup.Tests.NightM_TimeRulesTests.RestoreTestDue_ThirtyDaysExactly_AcrossMonthLeapYearAndDst(y: 2028, mo: 2, d: 1) | 2 | 0 | 0 |
| OnlineBackup.Tests.NightM_TimeRulesTests.RetentionDays_KeepsAPointExactlyNDaysOld_AndDropsItOneSecondLater(days: 1, y: 2027, m: 1, d: 1) | 2 | 0 | 0 |
| OnlineBackup.Tests.NightM_TimeRulesTests.RetentionDays_KeepsAPointExactlyNDaysOld_AndDropsItOneSecondLater(days: 30, y: 2026, m: 3, d: 1) | 2 | 0 | 0 |
| OnlineBackup.Tests.NightM_TimeRulesTests.RetentionDays_KeepsAPointExactlyNDaysOld_AndDropsItOneSecondLater(days: 30, y: 2028, m: 3, d: 1) | 2 | 0 | 0 |
| OnlineBackup.Tests.NightM_TimeRulesTests.RetentionDays_KeepsAPointExactlyNDaysOld_AndDropsItOneSecondLater(days: 7, y: 2026, m: 3, d: 30) | 2 | 0 | 0 |
| OnlineBackup.Tests.NightM_TimeRulesTests.RetentionDays_OnTheStore_TheFolderOfThePointExactlyNDaysOldStays | 2 | 0 | 0 |
| OnlineBackup.Tests.NightM_TimeRulesTests.RetentionJobs_KeepsExactlyTheLastN_AndTheLatestAlways | 2 | 0 | 0 |
| OnlineBackup.Tests.NightM_TimeRulesTests.RunIds_AreUtc_AndSortInTimeOrder_AcrossMidnightMonthAndYearEnds | 2 | 0 | 0 |
| OnlineBackup.Tests.TlsTests.BuiltinTls12WithPinnedCertificate_BacksUpAndRestores_WrongPinIsRefused | 2 | 0 | 0 |
| OnlineBackup.Tests.TlsTests.ModernWindowsWithTheCompanysSelfSignedCertificate_PinnedForTheAgentAndForRestic | 2 | 0 | 0 |
| OnlineBackup.Tests.TlsTests.Net40AgentUnderMonoUsesTheBuiltinTls | 0 | 0 | 2 |
