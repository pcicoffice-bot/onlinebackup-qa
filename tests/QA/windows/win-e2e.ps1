# OnlineBackup QA — the full chain on a real Windows machine, from the real installation package:
#   install the server (install-server.ps1, a Windows service) → sign in → a customer → download the client Setup.exe
#   from the server → the installer robot (setup-robot.ps1) → register → golden dataset → "Back up now" → restore →
#   SHA-256 of every file → restart the service → kill the backup service mid-backup → recover → uninstall → reinstall →
#   backup + restore again.
# Every proof comes from outside the product: Windows services, files, SHA-256.
#   pwsh -File win-e2e.ps1 -Package C:\path\OnlineBackup-Server-x.zip -Out C:\qa\out
# Writes <Out>\windows.json (install / update / reboot → PASS, FAIL or NOT TESTED) and <Out>\win-e2e.json (every step).
param([Parameter(Mandatory = $true)][string]$Package, [Parameter(Mandatory = $true)][string]$Out)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Q = 'C:\obqa'; Remove-Item $Q -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force -Path $Q | Out-Null
$steps = New-Object System.Collections.ArrayList; $fail = 0
$Url = 'https://localhost:8443'; $AdminPass = 'Admin-Pass-QA-2026'; $Secret = 'JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP'; $CustPass = 'Customer-Pass-1'

function Step([string]$what, [scriptblock]$do) {
  $t = Get-Date; $ok = $false; $actual = ''
  try { $r = & $do; $ok = [bool]$r[0]; $actual = [string]$r[1] } catch { $actual = 'ERROR: ' + $_.Exception.Message + ' @ ' + $_.InvocationInfo.ScriptLineNumber }
  [void]$steps.Add([ordered]@{ step = $what; result = $(if ($ok) { 'PASS' } else { 'FAIL' }); actual = $actual; seconds = [int]((Get-Date) - $t).TotalSeconds })
  Write-Host ("[{0}] {1} — {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $what, $actual)
  if (-not $ok) { $script:fail++ }
  return $ok
}
function Totp([string]$s) {
  $A = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'; $bits = ($s.ToCharArray() | ForEach-Object { [Convert]::ToString($A.IndexOf($_), 2).PadLeft(5, '0') }) -join ''
  $key = [byte[]]@(for ($i = 0; $i + 8 -le $bits.Length; $i += 8) { [Convert]::ToByte($bits.Substring($i, 8), 2) })
  $c = [BitConverter]::GetBytes([long][Math]::Floor([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() / 30)); [Array]::Reverse($c)
  $h = (New-Object Security.Cryptography.HMACSHA1(, $key)).ComputeHash($c); $o = $h[19] -band 15
  return (((($h[$o] -band 0x7f) -shl 24) -bor ($h[$o + 1] -shl 16) -bor ($h[$o + 2] -shl 8) -bor $h[$o + 3]) % 1000000).ToString('000000')
}
function Msg([hashtable]$d) { '<m>' + (($d.GetEnumerator() | ForEach-Object { '<f n="' + $_.Key + '">' + [Security.SecurityElement]::Escape([string]$_.Value) + '</f>' }) -join '') + '</m>' }
$script:ses = ''
function Api([string]$method, [string]$path, [hashtable]$body = @{}) {
  $h = @{ 'Content-Type' = 'application/xml' }; if ($script:ses) { $h['X-Session'] = $script:ses }
  $p = @{ Uri = "$Url/api/admin/$path"; Method = $method; Headers = $h; SkipCertificateCheck = $true; SkipHttpErrorCheck = $true }
  if ($method -ne 'GET') { $p.Body = (Msg $body) }
  $r = Invoke-WebRequest @p
  if ($r.StatusCode -ge 400) { throw "$method $path → $($r.StatusCode) $($r.Content)" }
  return [string]$r.Content
}
function F([string]$xml, [string]$n) { if ($xml -match "<f n=`"$n`">([^<]*)</f>") { return $Matches[1] } return '' }
function Manifest([string]$root) {
  $m = @{}; if (-not (Test-Path $root)) { return $m }
  Get-ChildItem $root -Recurse -File -Force | ForEach-Object { $m[$_.FullName.Substring($root.Length).TrimStart('\')] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
  return $m
}
function Compare-Manifest($a, $b) {
  $d = @(); foreach ($k in $a.Keys) { if (-not $b.ContainsKey($k)) { $d += "MISSING $k" } elseif ($a[$k] -ne $b[$k]) { $d += "DIFFERENT $k" } }
  foreach ($k in $b.Keys) { if (-not $a.ContainsKey($k)) { $d += "UNEXPECTED $k" } }; return $d
}
function Golden([string]$root) {
  $enc = New-Object Text.UTF8Encoding($false)
  $w = { param($rel, $data) $f = Join-Path $root $rel; New-Item -ItemType Directory -Force -Path (Split-Path $f) | Out-Null; if ($data -is [byte[]]) { [IO.File]::WriteAllBytes($f, $data) } else { [IO.File]::WriteAllText($f, $data, $enc) } }
  & $w 'Documents\letter.txt' "Dear customer,`nthis is a test letter.`n"
  & $w 'מסמכים\מכתב ללקוח.txt' "שלום עולם — מכתב בעברית`n"
  & $w 'מסמכים\תיקייה עם רווחים\דוח שנתי 2026.csv' "שנה,סכום`n2026,100`n"
  & $w 'Unicode\日本語 ファイル.txt' 'こんにちは'
  & $w 'Empty\zero.bin' ([byte[]]@())
  $rnd = New-Object Random 7; $b = New-Object byte[] (20MB); $rnd.NextBytes($b); & $w 'Binary\large.bin' $b
  $b2 = New-Object byte[] (3MB + 17); $rnd.NextBytes($b2); & $w 'Binary\random.bin' $b2
  & $w 'Duplicates\a.txt' 'same'; & $w 'Duplicates\b.txt' 'same'
  for ($i = 0; $i -lt 100; $i++) { & $w ("Many\file-{0:D3}.csv" -f $i) "row,$i`n" }
  New-Item -ItemType Directory -Force -Path (Join-Path $root 'Empty folder') | Out-Null
}
# A Windows service needs some seconds to start or stop: wait for the state (never a fixed sleep), return the state seen.
function WaitService([string]$name, [string]$state, [int]$seconds = 90) {
  $until = (Get-Date).AddSeconds($seconds)
  do { $s = Get-Service $name -ErrorAction SilentlyContinue; if ($s -and [string]$s.Status -eq $state) { return [string]$s.Status }; Start-Sleep -Milliseconds 500 } while ((Get-Date) -lt $until)
  if ($s) { return [string]$s.Status } else { return 'no service' }
}
# Evidence is written even when the run stops on an error
function WriteEvidence {
  $steps | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Out 'win-e2e.json') -Encoding UTF8
  Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddHours(-2) } -ErrorAction SilentlyContinue | Where-Object { $_.ProviderName -match 'OnlineBackup|\.NET Runtime|Application Error' } |
    Select-Object -First 50 TimeCreated, ProviderName, Id, Message | Format-List | Out-File (Join-Path $Out 'event-log.txt') -Encoding UTF8
  foreach ($d in @("$Q\sys\logs")) { if (Test-Path $d) { Copy-Item $d (Join-Path $Out 'server-logs') -Recurse -Force -ErrorAction SilentlyContinue } }
}
trap { [void]$steps.Add([ordered]@{ step = 'run stopped'; result = 'FAIL'; actual = $_.Exception.Message + ' @ ' + $_.InvocationInfo.ScriptLineNumber }); WriteEvidence
  [ordered]@{ install = 'FAIL'; recovery = 'NOT TESTED'; update = 'NOT TESTED'; reboot = 'NOT TESTED' } | ConvertTo-Json | Set-Content (Join-Path $Out 'windows.json') -Encoding UTF8; exit 1 }
function AgentCli { param([Parameter(ValueFromRemainingArguments = $true)]$a) $o = & $script:AgentExe @a 2>&1 | Out-String; return @($LASTEXITCODE, $o) }
function WaitRun([string]$login, [string]$status, [int]$minutes = 6) {
  $until = (Get-Date).AddMinutes($minutes)
  while ((Get-Date) -lt $until) {
    $t = Api 'GET' 'tasks?hours=24'
    $items = [regex]::Matches($t, '<i>(.*?)</i>') | ForEach-Object { $_.Groups[1].Value } | Where-Object { (F $_ 'login') -eq $login -and (F $_ 'kind') -eq 'Backup' }
    $hit = $items | Where-Object { (F $_ 'status') -eq $status } | Select-Object -First 1
    if ($hit) { return $hit }
    Start-Sleep -Seconds 5
  }
  return $null
}

# ---- 1. the server, from the real package, as a Windows service
$pkg = Join-Path $Q 'pkg'; Expand-Archive -Path $Package -DestinationPath $pkg -Force
$serverOk = Step 'Server: install-server.ps1 (real package, Windows service)' {
  & (Join-Path $pkg 'install-server.ps1') -HostName localhost -Port 8443 -SystemHome "$Q\sys" -UserHome "$Q\users" -AdminPassword $AdminPass | Out-Host
  $st = WaitService OnlineBackupServer 'Running' 90; @(($st -eq 'Running'), "$st after up to 90 s")
}
# FIXTURES: the administrator's authenticator secret and a licence from a throw-away key of this run
$srvExe = Join-Path $env:ProgramFiles 'OnlineBackup Server\OnlineBackup.Server.exe'
Stop-Service OnlineBackupServer -ErrorAction SilentlyContinue
Step 'Server service stops when asked' { $st = WaitService OnlineBackupServer 'Stopped' 60; @(($st -eq 'Stopped'), $st) } | Out-Null
$sx = "$Q\sys\conf\system.xml"; (Get-Content $sx -Raw -Encoding UTF8) -replace '(<ADMIN [^>]*?)TOTP_SECRET="[^"]*"', "`$1TOTP_SECRET=`"$Secret`"" | Set-Content $sx -Encoding UTF8 -NoNewline
$pub = (& $srvExe license-keygen --out "$Q\lic.key" | Select-Object -Last 1).Trim()
[Environment]::SetEnvironmentVariable('OB_LICENSE_PUBKEY', $pub, 'Machine'); $env:OB_LICENSE_PUBKEY = $pub
$sid = (& $srvExe server-id --system-home "$Q\sys" | Select-Object -Last 1).Trim()
$lic = (& $srvExe license-issue --key "$Q\lic.key" --server-id $sid --company 'QA IT' --users 100 --storage-gb 1000 --days 30 | Select-Object -Last 1).Trim()
(Get-Content $sx -Raw -Encoding UTF8) -replace '<LICENSE KEY="" />', "<LICENSE KEY=`"$lic`" />" | Set-Content $sx -Encoding UTF8 -NoNewline
Start-Service OnlineBackupServer -ErrorAction SilentlyContinue
Step 'Server service starts again (with the licence)' { $st = WaitService OnlineBackupServer 'Running' 90; @(($st -eq 'Running'), $st) } | Out-Null

Step 'Admin sign-in over HTTPS (password + authenticator)' {
  $r = Invoke-WebRequest -Uri "$Url/api/admin/login" -Method POST -Body (Msg @{ login = 'admin'; password = $AdminPass; otp = (Totp $Secret) }) -ContentType 'application/xml' -SkipCertificateCheck -SkipHttpErrorCheck
  $script:ses = F $r.Content 'session'; @([bool]$script:ses, "HTTP $($r.StatusCode)")
} | Out-Null
# the server's certificate fingerprint, read from the TLS connection (what the client package pins)
$tcp = New-Object Net.Sockets.TcpClient('localhost', 8443); $ssl = New-Object Net.Security.SslStream($tcp.GetStream(), $false, { $true }); $ssl.AuthenticateAsClient('localhost')
$pin = ([Security.Cryptography.SHA256]::Create().ComputeHash($ssl.RemoteCertificate.GetRawCertData()) | ForEach-Object { $_.ToString('x2') }) -join ''; $ssl.Dispose(); $tcp.Dispose()
Api 'POST' 'settings' @{ publicUrl = $Url; certPin = $pin } | Out-Null
Api 'POST' 'users' @{ login = 'qa-win'; password = $CustPass; alias = 'QA Windows'; quotaGB = 5; email = 'it@example.invalid' } | Out-Null

# ---- 2. the client installer from the server, installed by the robot
$setup = Join-Path $Q 'Setup.exe'
Step 'Download the client Setup.exe from the server' {
  Invoke-WebRequest -Uri "$Url/api/admin/clientpackage?os=windows" -Headers @{ 'X-Session' = $script:ses } -OutFile $setup -SkipCertificateCheck
  @(((Get-Item $setup).Length -gt 1MB), "$((Get-Item $setup).Length) bytes")
} | Out-Null
$installOk = Step 'Client installation (setup-robot: Welcome → License → Accept → Install → Finish → service)' {
  & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'setup-robot.ps1') -Setup $setup -Out (Join-Path $Out 'install') | Out-Host
  @($LASTEXITCODE -eq 0, "robot exit $LASTEXITCODE (see install\setup-robot.json)")
}
$svc = Get-CimInstance Win32_Service -Filter "Name='OnlineBackupAgent'"
$script:AgentExe = ($svc.PathName -replace '^"([^"]+)".*$', '$1'); $AgentHome = ($svc.PathName -replace '^.*--home "([^"]+)".*$', '$1')

# ---- 3. register, back up, restore, compare
Step 'Register this computer with the server' { $r = AgentCli register --home $AgentHome --server "$Url/" --login qa-win --password $CustPass --computer QA-WIN --pin $pin; @($r[0] -eq 0, $r[1]) } | Out-Null
$data = 'C:\OnlineBackup-QA\Data'; Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue; Golden $data; $before = Manifest $data
$setId = ''
Step 'Backup set on the golden dataset' { $r = AgentCli addset --home $AgentHome --password $CustPass --name 'QA files' --source $data --keytype PASSWORD; $script:setId = ($r[1].Trim() -split "`n")[-1].Trim(); @(($r[0] -eq 0) -and ($script:setId -match '^\d+$'), $r[1]) } | Out-Null
$backupOk = Step 'Back up now (admin) → the Windows service backs up → Succeeded' {
  Api 'POST' "users/qa-win/sets/$setId/run" | Out-Null
  $hit = WaitRun 'qa-win' 'ok'; @([bool]$hit, $(if ($hit) { "new=$(F $hit 'new') bytes=$(F $hit 'bytes')" } else { 'no successful run in 6 min' }))
}
$restoreOk = Step 'ORACLE: restore to an empty folder → SHA-256 of every file identical' {
  $t = "$Q\restore1"; $r = AgentCli restore --home $AgentHome --set $setId --password $CustPass --target $t
  $d = Compare-Manifest $before (Manifest (Join-Path $t 'C\OnlineBackup-QA\Data')); @(($r[0] -eq 0) -and ($d.Count -eq 0), "exit $($r[0]); differences: $($d.Count) $($d | Select-Object -First 5)")
}

# ---- 4. the service: restart; killed in the middle of a backup
Step 'Restart the backup service → Running' { Restart-Service OnlineBackupAgent; $st = WaitService OnlineBackupAgent 'Running' 90; @(($st -eq 'Running'), $st) } | Out-Null
$killOk = Step 'Kill the service process mid-backup → service back → next backup Succeeded → restore identical' {
  Add-Content -Path (Join-Path $data 'Binary\grow.bin') -Value ('x' * 50MB) -NoNewline
  $before2 = Manifest $data
  Api 'POST' "users/qa-win/sets/$setId/run" | Out-Null
  $until = (Get-Date).AddMinutes(3); do { Start-Sleep 2; $live = Api 'GET' 'live' } while ($live -notmatch $setId -and (Get-Date) -lt $until)
  $pidSvc = (Get-CimInstance Win32_Service -Filter "Name='OnlineBackupAgent'").ProcessId; Stop-Process -Id $pidSvc -Force
  Start-Sleep 5; if ((Get-Service OnlineBackupAgent).Status -ne 'Running') { Start-Service OnlineBackupAgent -ErrorAction SilentlyContinue }; [void](WaitService OnlineBackupAgent 'Running' 90)
  $until = (Get-Date).AddMinutes(4); do { Start-Sleep 5; $live = Api 'GET' 'live' } while ($live -match $setId -and (Get-Date) -lt $until)
  $ghost = $live -match $setId
  Api 'POST' "users/qa-win/sets/$setId/run" | Out-Null
  Start-Sleep 90; $hit = WaitRun 'qa-win' 'ok'
  $t = "$Q\restore2"; $r = AgentCli restore --home $AgentHome --set $setId --password $CustPass --target $t
  $d = Compare-Manifest $before2 (Manifest (Join-Path $t 'C\OnlineBackup-QA\Data'))
  @((-not $ghost) -and ($d.Count -eq 0), "ghost running: $ghost; differences after: $($d.Count)")
}

# ---- 5. remove, install again, back up and restore again
$removeOk = Step 'Uninstall (setup-robot -Remove) → service and entry gone' {
  & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'setup-robot.ps1') -Setup $setup -Out (Join-Path $Out 'remove') -Remove | Out-Host
  @($LASTEXITCODE -eq 0, "robot exit $LASTEXITCODE")
}
$reOk = Step 'Install again → the computer keeps its backups → restore identical' {
  & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'setup-robot.ps1') -Setup $setup -Out (Join-Path $Out 'reinstall') | Out-Host
  $svc2 = Get-CimInstance Win32_Service -Filter "Name='OnlineBackupAgent'"; $script:AgentExe = ($svc2.PathName -replace '^"([^"]+)".*$', '$1'); $h2 = ($svc2.PathName -replace '^.*--home "([^"]+)".*$', '$1')
  $reg = AgentCli register --home $h2 --server "$Url/" --login qa-win --password $CustPass --computer QA-WIN --pin $pin
  $t = "$Q\restore3"; $r = AgentCli restore --home $h2 --set $setId --password $CustPass --target $t
  $d = Compare-Manifest $before (Manifest (Join-Path $t 'C\OnlineBackup-QA\Data'))
  @(($r[0] -eq 0) -and ($d.Count -eq 0), "register exit $($reg[0]); restore exit $($r[0]); differences $($d.Count)")
}

WriteEvidence
$st = { param($b) if ($b) { 'PASS' } else { 'FAIL' } }
[ordered]@{ install = (& $st ($serverOk -and $installOk -and $backupOk -and $restoreOk -and $removeOk -and $reOk)); recovery = (& $st $killOk); update = 'NOT TESTED'; reboot = 'NOT TESTED' } | ConvertTo-Json | Set-Content (Join-Path $Out 'windows.json') -Encoding UTF8
Write-Host ("{0} steps, {1} failed" -f $steps.Count, $fail)
exit $(if ($fail -gt 0) { 1 } else { 0 })
