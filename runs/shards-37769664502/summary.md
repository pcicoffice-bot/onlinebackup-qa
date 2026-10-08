# windows-latest, select 'OnlineBackup.Tests.Net48NativeTests', 1 shards x 1 - run 37769664502 (ee0a46ec12d55bc0815fa47cb13fde6d7faf122a)

**6 tests: 4 PASS, 2 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `224fdb41c62e36ee653fd15bcafabf5ca1f219fd463f816fdda8ee7b8da5d584` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 6 | GitHub Actions 1000001818 | 3 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.Net48NativeTests.LongPath_Over260_BackedUpAndRestoredIdentical(tfm: "net40") | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s01-r01: backup (1): BS_STOP_BY_SYSTEM_ERROR new=1 upd=0 perm=0 del=0 bytes=506 err:  PathTooLongException: The specified path, file name, or both are too long. The ful |
| OnlineBackup.Tests.Net48NativeTests.LongPath_Over260_BackedUpAndRestoredIdentical(tfm: "net48") | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s01-r01: Assert.Contains() Failure: Sub-string not found String:    "BS_STOP_SUCCESS_WITH_ERROR new=1 upd=0 pe"··· Not found: "new=2 " |

6 test(s) of this build are not yet in tools/qa-shards/known-tests.txt (add them so that a later disappearance is caught): OnlineBackup.Tests.Net48NativeTests.LongPath_Over260_BackedUpAndRestoredIdentical(tfm: "net40"), OnlineBackup.Tests.Net48NativeTests.LongPath_Over260_BackedUpAndRestoredIdentical(tfm: "net48"), OnlineBackup.Tests.Net48NativeTests.Net40LocalState_UpgradedToNet48_NothingChangesTwice_AndBackAgain, OnlineBackup.Tests.Net48NativeTests.Net48Agent_NativeOnWindows_Tls12OnlyServer_BacksUpAndRestoresIdentical, OnlineBackup.Tests.Net48NativeTests.PermissionOnlyChange_IsOnePermVersion(tfm: "net40"), OnlineBackup.Tests.Net48NativeTests.PermissionOnlyChange_IsOnePermVersion(tfm: "net48")
