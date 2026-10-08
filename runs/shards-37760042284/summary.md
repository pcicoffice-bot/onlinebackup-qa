# windows-latest, select 'OnlineBackup.Tests.TlsTests OnlineBackup.Tests.PilotAgentTlsComponentTests', 1 shards x 2 - run 37760042284 (d10aa80b3b0af1f71f336f66d0c90d3f55b273f7)

**9 tests: 8 PASS, 0 FAIL, 1 NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)

Build SHA-256: `fc2518c6508cbb4454cdf713048fbb823d4d555e66858196f216bd9821d25860` (every shard checks it before running). Mode: strict - NOT green.

| job | state | tests | runner | minutes |
|---|---|---|---|---|
| s01-r01 | results | 9 | GitHub Actions 1000001739 | 2 |
| s01-r02 | results | 9 | GitHub Actions 1000001738 | 2 |

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
