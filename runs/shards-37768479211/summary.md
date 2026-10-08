# windows-latest, select 'OnlineBackup.Tests.Net48NativeTests OnlineBackup.Tests.TlsTests.Net40Agent_NativeOnWindows', 1 shards x 1 - run 37768479211 (67a36e2ad031ad7a8cc0e2bbfa9125d4abdae6be)

**7 tests: 4 PASS, 3 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `999ff632bc23ccd46db26b825ec3f34ecf45f45f07b5ca799914d8670ba5f8ba` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 7 | GitHub Actions 1000001797 | 2 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.Net48NativeTests.LongPath_Over260_BackedUpAndRestoredIdentical(tfm: "net40") | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s01-r01: backup (1): BS_STOP_BY_SYSTEM_ERROR new=1 upd=0 perm=0 del=0 bytes=506 err:  PathTooLongException: The specified path, file name, or both are too long. The ful |
| OnlineBackup.Tests.Net48NativeTests.LongPath_Over260_BackedUpAndRestoredIdentical(tfm: "net48") | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s01-r01: Assert.Contains() Failure: Sub-string not found String:    "BS_STOP_SUCCESS_WITH_ERROR new=1 upd=0 pe"··· Not found: "new=2 " |
| OnlineBackup.Tests.Net48NativeTests.Net40LocalState_UpgradedToNet48_NothingChangesTwice_AndBackAgain | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s01-r01: System.UnauthorizedAccessException : Access to the path 'ro.txt' is denied. |

6 test(s) of this build are not yet in tools/qa-shards/known-tests.txt (add them so that a later disappearance is caught): OnlineBackup.Tests.Net48NativeTests.LongPath_Over260_BackedUpAndRestoredIdentical(tfm: "net40"), OnlineBackup.Tests.Net48NativeTests.LongPath_Over260_BackedUpAndRestoredIdentical(tfm: "net48"), OnlineBackup.Tests.Net48NativeTests.Net40LocalState_UpgradedToNet48_NothingChangesTwice_AndBackAgain, OnlineBackup.Tests.Net48NativeTests.Net48Agent_NativeOnWindows_Tls12OnlyServer_BacksUpAndRestoresIdentical, OnlineBackup.Tests.Net48NativeTests.PermissionOnlyChange_IsOnePermVersion(tfm: "net40"), OnlineBackup.Tests.Net48NativeTests.PermissionOnlyChange_IsOnePermVersion(tfm: "net48")
