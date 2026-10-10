# windows-latest, select 'Restic_LongChain_EverySnapshotRestoresIdentical WebRestoreTests', 2 shards x 1 - run 38034799468 (50d4d270380385aeba2e3789d3fa7fb154c4a22a)

**2 tests: 1 PASS, 1 FAIL, 0 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `a91bbbc9e184f738d3000c8ea4741a1ec473f8fa6f3325bc271eaa8043e29127` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 1 | GitHub Actions 1000002394 | 24 |
| s02-r01 | results | 1 | GitHub Actions 1000002403 | 1 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.WebRestoreTests.CustomerRestoresChosenFilesFromTheWebsite_WithTheEncryptionPassword | FAIL | 0 PASS / 1 FAIL / 0 NOT TESTED of 1 - s02-r01: 6 left in the server's temp folder after the web restore: webrestore-18850bce111c [Directory], webrestore-4d83fa9cdca3 [Directory], webrestore-18850bce111c\C [D |

## Evidence written by the tests (open findings)

- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 the restore could not be removed: Access to the path '\\?\C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-0\C\Users' is denied.`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 .: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C\Users: ReadOnly, Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 icacls: C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-0\C NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | runnervmfi6oq\runneradmin:(OI)(CI)(F) |  | C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-0\C\Users NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | BUILTIN\Users:(RX) | BUILTIN\Users:(OI)(CI)(IO)(GR,GE) | Everyone:(RX) | Everyone:(OI)(CI)(IO)(GR,GE) | S-1-15-3-65536-4045685566-1323397456-4055816110-285687253-194181-4019357623-1925838800-191844675:(S,RD,X) |`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 the restore could not be removed: Access to the path '\\?\C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-1\C\Users' is denied.`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 .: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C\Users: ReadOnly, Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 icacls: C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-1\C NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | runnervmfi6oq\runneradmin:(OI)(CI)(F) |  | C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-1\C\Users NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | BUILTIN\Users:(RX) | BUILTIN\Users:(OI)(CI)(IO)(GR,GE) | Everyone:(RX) | Everyone:(OI)(CI)(IO)(GR,GE) | S-1-15-3-65536-4045685566-1323397456-4055816110-285687253-194181-4019357623-1925838800-191844675:(S,RD,X) |`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 the restore could not be removed: Access to the path '\\?\C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-2\C\Users' is denied.`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 .: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C\Users: ReadOnly, Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 icacls: C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-2\C NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | runnervmfi6oq\runneradmin:(OI)(CI)(F) |  | C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-2\C\Users NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | BUILTIN\Users:(RX) | BUILTIN\Users:(OI)(CI)(IO)(GR,GE) | Everyone:(RX) | Everyone:(OI)(CI)(IO)(GR,GE) | S-1-15-3-65536-4045685566-1323397456-4055816110-285687253-194181-4019357623-1925838800-191844675:(S,RD,X) |`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 the restore could not be removed: Access to the path '\\?\C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-3\C\Users' is denied.`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 .: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C\Users: ReadOnly, Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 icacls: C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-3\C NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | runnervmfi6oq\runneradmin:(OI)(CI)(F) |  | C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-3\C\Users NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | BUILTIN\Users:(RX) | BUILTIN\Users:(OI)(CI)(IO)(GR,GE) | Everyone:(RX) | Everyone:(OI)(CI)(IO)(GR,GE) | S-1-15-3-65536-4045685566-1323397456-4055816110-285687253-194181-4019357623-1925838800-191844675:(S,RD,X) |`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 the restore could not be removed: Access to the path '\\?\C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-4\C\Users' is denied.`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 .: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C\Users: ReadOnly, Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 icacls: C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-4\C NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | runnervmfi6oq\runneradmin:(OI)(CI)(F) |  | C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-4\C\Users NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | BUILTIN\Users:(RX) | BUILTIN\Users:(OI)(CI)(IO)(GR,GE) | Everyone:(RX) | Everyone:(OI)(CI)(IO)(GR,GE) | S-1-15-3-65536-4045685566-1323397456-4055816110-285687253-194181-4019357623-1925838800-191844675:(S,RD,X) |`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 the restore could not be removed: Access to the path '\\?\C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-5\C\Users' is denied.`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 .: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C\Users: ReadOnly, Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 icacls: C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-5\C NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | runnervmfi6oq\runneradmin:(OI)(CI)(F) |  | C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-5\C\Users NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | BUILTIN\Users:(RX) | BUILTIN\Users:(OI)(CI)(IO)(GR,GE) | Everyone:(RX) | Everyone:(OI)(CI)(IO)(GR,GE) | S-1-15-3-65536-4045685566-1323397456-4055816110-285687253-194181-4019357623-1925838800-191844675:(S,RD,X) |`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 the restore could not be removed: Access to the path '\\?\C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-6\C\Users' is denied.`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 .: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C\Users: ReadOnly, Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 icacls: C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-6\C NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | runnervmfi6oq\runneradmin:(OI)(CI)(F) |  | C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-6\C\Users NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | BUILTIN\Users:(RX) | BUILTIN\Users:(OI)(CI)(IO)(GR,GE) | Everyone:(RX) | Everyone:(OI)(CI)(IO)(GR,GE) | S-1-15-3-65536-4045685566-1323397456-4055816110-285687253-194181-4019357623-1925838800-191844675:(S,RD,X) |`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 the restore could not be removed: Access to the path '\\?\C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-7\C\Users' is denied.`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 .: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C\Users: ReadOnly, Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 icacls: C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-7\C NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | runnervmfi6oq\runneradmin:(OI)(CI)(F) |  | C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-7\C\Users NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | BUILTIN\Users:(RX) | BUILTIN\Users:(OI)(CI)(IO)(GR,GE) | Everyone:(RX) | Everyone:(OI)(CI)(IO)(GR,GE) | S-1-15-3-65536-4045685566-1323397456-4055816110-285687253-194181-4019357623-1925838800-191844675:(S,RD,X) |`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 the restore could not be removed: Access to the path '\\?\C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-8\C\Users' is denied.`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 .: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C\Users: ReadOnly, Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 icacls: C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-8\C NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | runnervmfi6oq\runneradmin:(OI)(CI)(F) |  | C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-8\C\Users NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | BUILTIN\Users:(RX) | BUILTIN\Users:(OI)(CI)(IO)(GR,GE) | Everyone:(RX) | Everyone:(OI)(CI)(IO)(GR,GE) | S-1-15-3-65536-4045685566-1323397456-4055816110-285687253-194181-4019357623-1925838800-191844675:(S,RD,X) |`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 the restore could not be removed: Access to the path '\\?\C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-9\C\Users' is denied.`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 .: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C\Users: ReadOnly, Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 icacls: C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-9\C NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | runnervmfi6oq\runneradmin:(OI)(CI)(F) |  | C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-9\C\Users NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | BUILTIN\Users:(RX) | BUILTIN\Users:(OI)(CI)(IO)(GR,GE) | Everyone:(RX) | Everyone:(OI)(CI)(IO)(GR,GE) | S-1-15-3-65536-4045685566-1323397456-4055816110-285687253-194181-4019357623-1925838800-191844675:(S,RD,X) |`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 the restore could not be removed: Access to the path '\\?\C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-10\C\Users' is denied.`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 .: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C\Users: ReadOnly, Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 icacls: C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-10\C NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | runnervmfi6oq\runneradmin:(OI)(CI)(F) |  | C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-10\C\Users NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | BUILTIN\Users:(RX) | BUILTIN\Users:(OI)(CI)(IO)(GR,GE) | Everyone:(RX) | Everyone:(OI)(CI)(IO)(GR,GE) | S-1-15-3-65536-4045685566-1323397456-4055816110-285687253-194181-4019357623-1925838800-191844675:(S,RD,X) |`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 the restore could not be removed: Access to the path '\\?\C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-11\C\Users' is denied.`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 .: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C\Users: ReadOnly, Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 icacls: C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-11\C NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | runnervmfi6oq\runneradmin:(OI)(CI)(F) |  | C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-11\C\Users NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | BUILTIN\Users:(RX) | BUILTIN\Users:(OI)(CI)(IO)(GR,GE) | Everyone:(RX) | Everyone:(OI)(CI)(IO)(GR,GE) | S-1-15-3-65536-4045685566-1323397456-4055816110-285687253-194181-4019357623-1925838800-191844675:(S,RD,X) |`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 the restore could not be removed: Access to the path '\\?\C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-12\C\Users' is denied.`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 .: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C\Users: ReadOnly, Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 icacls: C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-12\C NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | runnervmfi6oq\runneradmin:(OI)(CI)(F) |  | C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-12\C\Users NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | BUILTIN\Users:(RX) | BUILTIN\Users:(OI)(CI)(IO)(GR,GE) | Everyone:(RX) | Everyone:(OI)(CI)(IO)(GR,GE) | S-1-15-3-65536-4045685566-1323397456-4055816110-285687253-194181-4019357623-1925838800-191844675:(S,RD,X) |`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 the restore could not be removed: Access to the path '\\?\C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-13\C\Users' is denied.`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 .: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C\Users: ReadOnly, Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 icacls: C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-13\C NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | runnervmfi6oq\runneradmin:(OI)(CI)(F) |  | C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-13\C\Users NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | BUILTIN\Users:(RX) | BUILTIN\Users:(OI)(CI)(IO)(GR,GE) | Everyone:(RX) | Everyone:(OI)(CI)(IO)(GR,GE) | S-1-15-3-65536-4045685566-1323397456-4055816110-285687253-194181-4019357623-1925838800-191844675:(S,RD,X) |`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 the restore could not be removed: Access to the path '\\?\C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-14\C\Users' is denied.`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 .: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C\Users: ReadOnly, Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 icacls: C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-14\C NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | runnervmfi6oq\runneradmin:(OI)(CI)(F) |  | C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-14\C\Users NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | BUILTIN\Users:(RX) | BUILTIN\Users:(OI)(CI)(IO)(GR,GE) | Everyone:(RX) | Everyone:(OI)(CI)(IO)(GR,GE) | S-1-15-3-65536-4045685566-1323397456-4055816110-285687253-194181-4019357623-1925838800-191844675:(S,RD,X) |`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 the restore could not be removed: Access to the path '\\?\C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-15\C\Users' is denied.`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 .: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C: Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 C\Users: ReadOnly, Directory`
- s01-r01, Restic_LongChain_EverySnapshotRestoresIdentical: `T-4 icacls: C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-15\C NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | runnervmfi6oq\runneradmin:(OI)(CI)(F) |  | C:\Users\runneradmin\AppData\Local\Temp\obnp-a463dce2\restore-15\C\Users NT AUTHORITY\SYSTEM:(OI)(CI)(F) | BUILTIN\Administrators:(OI)(CI)(F) | BUILTIN\Users:(RX) | BUILTIN\Users:(OI)(CI)(IO)(GR,GE) | Everyone:(RX) | Everyone:(OI)(CI)(IO)(GR,GE) | S-1-15-3-65536-4045685566-1323397456-4055816110-285687253-194181-4019357623-1925838800-191844675:(S,RD,X) |`
