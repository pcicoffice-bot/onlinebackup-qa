# windows-latest, select 'OnlineBackup.Tests.TlsTests OnlineBackup.Tests.PilotAgentTlsComponentTests', 1 shards x 2 - run 38034822289 (97c57bae10ec1f69e2ce1e2998bcea3d8fc2c667)

**9 tests: 8 PASS, 0 FAIL, 1 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `8a341c16e6bb9fb123c597330e3b4e755fd7a1b05dcdffb242f3fd590e345165` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 9 | GitHub Actions 1000002335 | 2 |
| s01-r02 | results | 9 | GitHub Actions 1000002368 | 2 |

## Not PASS

| test | verdict | per job |
|---|---|---|
| OnlineBackup.Tests.TlsTests.Net40AgentUnderMonoUsesTheBuiltinTls | NOT TESTED | 0 PASS / 0 FAIL / 2 NOT TESTED of 2 - s01-r01: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs mono and the .NET 4.0 agent build; s01-r02: [declared] OnlineBackup.Tests.NotTestedException : NOT TESTED: needs mono and the .NET 4.0 agent build |

## Repetitions

| test | PASS | FAIL | NOT TESTED |
|---|---|---|---|
| OnlineBackup.Tests.PilotAgentTlsComponentTests.APinWithColonsAndCapitals_IsTheSamePin_WithoutAPinASelfSignedCertificateIsRefused | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotAgentTlsComponentTests.AnotherCertificate_IsRefused_BeforeAnyRequestReachesTheServer_NothingIsSent(builtin: False) | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotAgentTlsComponentTests.AnotherCertificate_IsRefused_BeforeAnyRequestReachesTheServer_NothingIsSent(builtin: True) | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotAgentTlsComponentTests.ThePinnedCertificate_IsAccepted_AWholeBackupGoesThroughTls12_AndThePointRestoresIdentical(builtin: False) | 2 | 0 | 0 |
| OnlineBackup.Tests.PilotAgentTlsComponentTests.ThePinnedCertificate_IsAccepted_AWholeBackupGoesThroughTls12_AndThePointRestoresIdentical(builtin: True) | 2 | 0 | 0 |
| OnlineBackup.Tests.TlsTests.BuiltinTls12WithPinnedCertificate_BacksUpAndRestores_WrongPinIsRefused | 2 | 0 | 0 |
| OnlineBackup.Tests.TlsTests.ModernWindowsWithTheCompanysSelfSignedCertificate_PinnedForTheAgentAndForRestic | 2 | 0 | 0 |
| OnlineBackup.Tests.TlsTests.Net40AgentUnderMonoUsesTheBuiltinTls | 0 | 0 | 2 |
| OnlineBackup.Tests.TlsTests.Net40Agent_NativeOnWindows_Tls12OnlyServer_BacksUpAndRestoresIdentical | 2 | 0 | 0 |
