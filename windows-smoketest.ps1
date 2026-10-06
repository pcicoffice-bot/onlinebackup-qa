<#
  First real-Windows check of the online backup (Windows PowerShell 2.0+, run as Administrator).
  Everything stays on this computer: the server listens on http://localhost only (no firewall rule, no IIS, no binding).
  Checks: server + user, agent registration (DPAPI), file backup with VSS, change -> delta, restore + hash compare,
  restore test; optional MSSQL (-SqlInstance) and bare-metal image (-ImageStaging / -ImageTarget).
  Passwords are generated here, used in memory and never printed or written to logs.
  .\windows-smoketest.ps1 [-Work C:\OBTest] [-Port 18080] [-SqlInstance SQLEXPRESS] [-ImageStaging E:] [-ImageTarget F:\] [-Keep]
#>
param([string]$Work = 'C:\OBTest', [int]$Port = 18080, [string]$SqlInstance, [string]$ImageStaging, [string]$ImageTarget, [switch]$Keep)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$server = Join-Path $here 'server\OnlineBackup.Server.exe'
$agent = Join-Path $here 'agent\OnlineBackup.Agent.exe'
$results = New-Object System.Collections.ArrayList
function Step([string]$name, [scriptblock]$body) {
  Write-Host "`n== $name" -ForegroundColor Cyan
  try { & $body; [void]$results.Add(@($name, 'PASS', '')); Write-Host "   PASS" -ForegroundColor Green }
  catch { [void]$results.Add(@($name, 'FAIL', $_.Exception.Message)); Write-Host "   FAIL: $($_.Exception.Message)" -ForegroundColor Red }
}
function Run([string]$exe, [string[]]$a) {
  $out = & $exe @a 2>&1 | ForEach-Object { "$_" }
  $code = $LASTEXITCODE
  $out | ForEach-Object { Write-Host "   $_" }
  if ($code -ne 0) { throw "$([IO.Path]::GetFileName($exe)) $($a[0]) exit $code" }
  return $out
}
function NewPass { $c = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'; $r = New-Object Random; 'Ob-' + (-join (1..18 | ForEach-Object { $c[$r.Next($c.Length)] })) + '-9' }
function Hashes([string]$dir) {
  $sha = New-Object Security.Cryptography.SHA256Managed; $h = @{}
  Get-ChildItem $dir -Recurse | Where-Object { -not $_.PSIsContainer } | ForEach-Object {
    $s = [IO.File]::OpenRead($_.FullName); try { $h[$_.FullName.Substring($dir.Length)] = [BitConverter]::ToString($sha.ComputeHash($s)) } finally { $s.Close() } }
  return $h
}

if (Test-Path $Work) { Remove-Item $Work -Recurse -Force }
$sys = Join-Path $Work 'system'; $homes = Join-Path $Work 'users'; $ah = Join-Path $Work 'agent'; $src = Join-Path $Work 'src'; $rst = Join-Path $Work 'restore'
New-Item -ItemType Directory -Force -Path $sys, $homes, $ah, $src | Out-Null
$adminPass = NewPass; $userPass = NewPass; $url = "http://localhost:$Port"
$proc = $null
Write-Host "Windows $([Environment]::OSVersion.VersionString), PowerShell $($PSVersionTable.PSVersion), work folder $Work"

Step 'Server: init + user' {
  Run $server @('init', '--system-home', $sys, '--admin', 'admin', '--password', $adminPass, '--host', "localhost:$Port", '--user-home', "$homes|UNLIMITED") | Out-Null
  Run $server @('adduser', '--system-home', $sys, '--login', 'smoketest', '--password', $userPass, '--quota-gb', '5') | Out-Null
}
Step 'Server: start on localhost' {
  $sp = @{ FilePath = $server; ArgumentList = @('run', '--system-home', "`"$sys`"", '--prefix', "$url/"); PassThru = $true }
  if ([Environment]::OSVersion.Platform -eq 'Win32NT') { $sp.WindowStyle = 'Hidden' }
  $script:proc = Start-Process @sp
  $ok = $false
  for ($i = 0; $i -lt 30 -and -not $ok; $i++) { Start-Sleep 1; try { (New-Object Net.WebClient).DownloadString("$url/api/brand") | Out-Null; $ok = $true } catch { } }
  if (-not $ok) { throw 'server did not answer in 30 seconds' }
}
Step 'Agent: register (keys protected with DPAPI)' {
  Run $agent @('register', '--home', $ah, '--server', $url, '--login', 'smoketest', '--password', $userPass) | Out-Null
  $cfg = [xml](Get-Content (Join-Path $ah 'agent.xml'))
  if ($cfg.AGENT.DEVICE -match 'smoketest') { throw 'device token is not protected' }
}
Step 'Sample data (small files, Hebrew names, one 60MB file, open file)' {
  New-Item -ItemType Directory -Force -Path (Join-Path $src 'הנהלת חשבונות\2026') | Out-Null
  1..200 | ForEach-Object { Set-Content -Path (Join-Path $src "הנהלת חשבונות\2026\file$_.txt") -Value ("line $_ " * 200) }
  $b = New-Object byte[] (60MB); (New-Object Random 5).NextBytes($b); [IO.File]::WriteAllBytes((Join-Path $src 'big.pst'), $b)
  $script:openFile = [IO.File]::Open((Join-Path $src 'big.pst'), 'Open', 'ReadWrite', 'None')   # locked like Outlook does
}
$setId = $null
Step 'File set + first backup (VSS reads the locked file)' {
  $script:setId = (Run $agent @('addset', '--home', $ah, '--password', $userPass, '--name', 'Smoke', '--source', $src) | Select-Object -Last 1).Trim()
  $o = Run $agent @('backup', '--home', $ah, '--set', $script:setId)
  if (-not ($o -match 'new=201')) { throw 'expected 201 new files (200 small + the locked 60MB file through VSS)' }
}
Step 'Change 1MB inside the 60MB file -> only the change is sent' {
  $script:openFile.Close()
  $f = [IO.File]::Open((Join-Path $src 'big.pst'), 'Open', 'ReadWrite'); $f.Seek(30MB, 'Begin') | Out-Null
  $x = New-Object byte[] (1MB); (New-Object Random 9).NextBytes($x); $f.Write($x, 0, $x.Length); $f.Close()
  Start-Sleep 2
  $o = Run $agent @('backup', '--home', $ah, '--set', $script:setId)
  $sent = [long](($o -join ' ') -replace '.*bytes=(\d+).*', '$1')
  if ($sent -gt 30MB) { throw "sent $sent bytes for a 1MB change (more than half of the 60MB file)" }
}
Step 'Restore everything + compare SHA-256 of every file' {
  Run $agent @('restore', '--home', $ah, '--set', $script:setId, '--password', $userPass, '--target', $rst) | Out-Null
  $a = Hashes $src
  $b = Hashes (Join-Path $rst ($src.Replace(':', '')))
  if ($a.Count -ne $b.Count) { throw "files: source $($a.Count), restored $($b.Count)" }
  foreach ($k in $a.Keys) { if ($a[$k] -ne $b[$k]) { throw "content differs: $k" } }
}
Step 'Automatic restore test' { Run $agent @('restore-test', '--home', $ah, '--set', $script:setId) | Out-Null }

Step 'restic engine (Windows 10 / 2016+): backup with VSS, change, restore + compare, restic check' {
  $id = (Run $agent @('addset', '--home', $ah, '--password', $userPass, '--name', 'SmokeRestic', '--engine', 'RESTIC', '--source', $src) | Select-Object -Last 1).Trim()
  $o = Run $agent @('backup', '--home', $ah, '--set', $id)
  if (-not (($o -join ' ') -match 'BS_STOP_SUCCESS new=201')) { throw 'restic: expected 201 new files' }
  $f = [IO.File]::Open((Join-Path $src 'big.pst'), 'Open', 'ReadWrite'); $f.Seek(20MB, 'Begin') | Out-Null
  $x = New-Object byte[] (1MB); (New-Object Random 11).NextBytes($x); $f.Write($x, 0, $x.Length); $f.Close()
  $o = Run $agent @('backup', '--home', $ah, '--set', $id)
  $sent = [long](($o -join ' ') -replace '.*bytes=(\d+).*', '$1')
  if (-not (($o -join ' ') -match 'upd=1') -or $sent -gt 30MB) { throw "restic: change not detected or $sent bytes sent (more than half of the 60MB file)" }
  $rr = Join-Path $Work 'restore-restic'
  Run $agent @('restore', '--home', $ah, '--set', $id, '--password', $userPass, '--target', $rr) | Out-Null
  $a = Hashes $src; $b = Hashes (Join-Path $rr ($src.Replace(':', '')))
  if ($a.Count -ne $b.Count) { throw "restic: files source $($a.Count), restored $($b.Count)" }
  foreach ($k in $a.Keys) { if ($a[$k] -ne $b[$k]) { throw "restic: content differs: $k" } }
  Run $agent @('restore-test', '--home', $ah, '--set', $id) | Out-Null
}

if ($SqlInstance) {
  Step "MSSQL ${SqlInstance}: full + log backup, restore .bak" {
    $id = (Run $agent @('addset', '--home', $ah, '--password', $userPass, '--name', 'SQL', '--type', 'MSSQL', '--source', "Microsoft SQL Server\$SqlInstance") | Select-Object -Last 1).Trim()
    Run $agent @('backup', '--home', $ah, '--set', $id) | Out-Null
    Run $agent @('restore', '--home', $ah, '--set', $id, '--password', $userPass, '--target', (Join-Path $Work 'sql-restore')) | Out-Null
    if (-not (Get-ChildItem (Join-Path $Work 'sql-restore') -Recurse -Filter *.bak)) { throw 'no .bak restored' }
  }
}
if ($ImageStaging -and $ImageTarget) {
  Step "Bare-metal image via $ImageStaging, restore to $ImageTarget" {
    $id = (Run $agent @('addset', '--home', $ah, '--password', $userPass, '--name', 'Image', '--type', 'BAREMETAL', '--source', $ImageStaging) | Select-Object -Last 1).Trim()
    Run $agent @('backup', '--home', $ah, '--set', $id) | Out-Null
    Run $agent @('restore-image', '--home', $ah, '--set', $id, '--password', $userPass, '--target', $ImageTarget) | Out-Null
    if (-not (Get-ChildItem (Join-Path $ImageTarget 'WindowsImageBackup') -Recurse -Include *.vhd, *.vhdx)) { throw 'no disk image restored' }
  }
}

if ($proc) { try { Stop-Process -Id $proc.Id -Force } catch { } }
Write-Host "`n===== Results" -ForegroundColor Cyan
$fail = 0
foreach ($r in $results) { if ($r[1] -eq 'FAIL') { $fail++ }; Write-Host ("{0,-4} {1} {2}" -f $r[1], $r[0], $r[2]) }
$report = Join-Path $here ("smoketest-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".txt")
$results | ForEach-Object { "{0}`t{1}`t{2}" -f $_[1], $_[0], $_[2] } | Set-Content -Path $report -Encoding UTF8
Get-ChildItem (Join-Path $homes 'smoketest') -Recurse -Filter *.log -ErrorAction SilentlyContinue | Select-Object -First 3 | ForEach-Object { Add-Content $report "`n--- $($_.Name)"; Get-Content $_.FullName -Tail 40 | Add-Content $report }
Write-Host "Report: $report (send it back)"
if (-not $Keep) { Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue }
exit $fail
