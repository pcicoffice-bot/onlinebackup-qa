# OnlineBackup QA - the robot's own self-test, before any journey (Windows PowerShell 5.1). Each line is a defect the
# robot once had; it must never come back, or a journey could PASS without the condition it claims.
#   powershell -File selftest.ps1      (exit 1 on any failure)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$bad = 0
# every failure is also a GitHub annotation: the job's log cannot always be read, its annotations can
function Expect([string]$what, $got, $want) { if ("$got" -ne "$want") { Write-Host "[SELFTEST FAIL] $what -- got '$got', want '$want'"; Write-Host "::error title=robot self-test::$what -- got '$got', want '$want'"; $script:bad++ } else { Write-Host "[SELFTEST PASS] $what" } }
trap { Write-Host ("::error title=robot self-test::stopped: " + $_.Exception.Message + " at " + $_.InvocationInfo.PositionMessage.Replace("`r", ' ').Replace("`n", ' ')); exit 1 }
$x = '<m><l n="tasks"><i><f n="login">qa-win</f><f n="kind">Backup</f></i><i><f n="login">qa-win</f><f n="kind">Restore</f></i></l></m>'
# Q5 (W1 run 4): a list returned as one array and piped on was one item - every server check saw no run
Expect 'server items piped on are elements' @(Items $x 'tasks' | Where-Object { $_['kind'] -eq 'Backup' }).Count 1
Expect 'a single server item is a list of one' @(Items '<m><l n="t"><i><f n="a">1</f></i></l></m>' 't').Count 1
Expect 'no server items is an empty list' @(Items '<m></m>' 't').Count 0
# Q2 (W1 run 2): a check written without its own parentheses ignored its second condition
$r = @(((1 -eq 1) -and (2 -eq 3)), 'x'); Expect 'a check keeps both conditions' $r[0] $false
# the SHA-256 oracle: a changed byte, a missing and an extra file are all seen
$a = @{ 'x' = 'AA'; 'y' = 'BB' }; Expect 'manifests equal' (Compare-Manifest $a @{ 'x' = 'AA'; 'y' = 'BB' }).Count 0
Expect 'a changed file is seen' (Compare-Manifest $a @{ 'x' = 'AA'; 'y' = 'BC' }).Count 1
Expect 'a missing and an extra file are seen' (Compare-Manifest $a @{ 'x' = 'AA'; 'z' = 'BB' }).Count 2
$g1 = Join-Path $env:TEMP ('obqa-g1-' + [guid]::NewGuid().ToString('N').Substring(0, 6)); $g2 = $g1 + 'b'
Golden $g1; Golden $g2; $m1 = Manifest $g1; Expect 'the golden dataset is the same bytes every time' (Compare-Manifest $m1 (Manifest $g2)).Count 0
# Q11: a manifest's names are relative to its root, whatever form of the path (short 8.3, trailing \) names the root
Expect 'a manifest names files relative to its root' (@($m1.Keys) -contains 'Documents\letter.txt') $true
Expect 'the same folder by another form of its path gives the same manifest' (Compare-Manifest $m1 (Manifest ($g1 + '\'))).Count 0
Expect 'the golden dataset has its 112 files' $m1.Count 112
Remove-Tree $g1; Remove-Tree $g2
Expect 'an authenticator code has 6 digits' ((Totp 'JBSWY3DPEHPK3PXP') -match '^\d{6}$') $true
# Agent G: a check whose text part is a -join without its own parentheses is ONE string "False; ..." - [bool] of its first character was True
Expect 'a check that returns one string is a FAIL' (CheckResult 'False; x')[0] $false
Expect 'a check whose first item is not a bool is a FAIL' (CheckResult @('False', 'x'))[0] $false
Expect 'a check with stray output before its result is a FAIL' (CheckResult @((Get-Item $PSScriptRoot), $false, 'x'))[0] $false
Expect 'a true check passes' (CheckResult @($true, 'x'))[0] $true
Expect 'a false check fails' (CheckResult @($false, 'x'))[0] $false
# Agent G: one run in the server's history came back as one hashtable: (Runs).Count was its number of fields
function Api { '<m><l n="tasks"><i><f n="time">1000</f><f n="login">qa-win</f><f n="set">1000001</f><f n="kind">Backup</f><f n="job">J1</f><f n="status">ok</f></i></l></m>' }
Expect 'one run in the history is a list of one' @(Runs 'qa-win' 'Backup').Count 1
$mark = RunMark 'qa-win' 'Backup'
Expect 'a run that was in the history before the action is not the new run' @(NewRuns 'qa-win' 'Backup' $mark '1000001').Count 0
$late = (NowMs) + 1000
function Api { '<m><l n="tasks"><i><f n="time">1000</f><f n="login">qa-win</f><f n="set">1000001</f><f n="kind">Backup</f><f n="job">J1</f><f n="status">ok</f></i><i><f n="time">' + $late + '</f><f n="login">qa-win</f><f n="set">1000001</f><f n="kind">Backup</f><f n="job">J0</f><f n="started">2000</f><f n="status">ok</f></i></l></m>' }
Expect 'an earlier run recorded late (started before the action) is not the new run' @(NewRuns 'qa-win' 'Backup' $mark '1000001').Count 0
$e = ''; try { WaitNewRun 'qa-win' 'Backup' 1 1 | Out-Null } catch { $e = $_.Exception.Message }
Expect 'WaitNewRun refuses a count' ([bool]$e) $true
Write-Host "selftest: $bad failed"
exit $(if ($bad -gt 0) { 1 } else { 0 })
