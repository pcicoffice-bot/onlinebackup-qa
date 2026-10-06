# OnlineBackup QA - the product on a real Windows, as a customer gets and uses it.
#   powershell -ExecutionPolicy Bypass -File win-e2e.ps1 -Package <OnlineBackup-Server-x.zip> [-Package2 <newer zip>] -Out <dir> [-Phase main|before-reboot|after-reboot]
# The server is installed from the real package (install-server.ps1); the client's Setup.exe is downloaded from that
# server, as the IT company hands it out; then the robots use the installer and the program window like a person.
# Every verdict comes from outside the product: Windows (services, processes, installed programs, files), the server's
# own records through its API, and SHA-256 of every restored file. The screens are kept and looked at (Visual QA).
# Writes <Out>\<journey>\journey.json + screenshots + control trees, <Out>\windows.json (every journey: FUNCTIONAL,
# VISUAL, UX), <Out>\visual-findings.json.
# Windows PowerShell 5.1 (what every Windows has) and PowerShell 7. ASCII only (see lib.ps1).
param([Parameter(Mandatory = $true)][string]$Package, [string]$Package2 = '', [Parameter(Mandatory = $true)][string]$Out,
      [ValidateSet('main', 'before-reboot', 'after-reboot')][string]$Phase = 'main', [string]$Only = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
. (Join-Path $PSScriptRoot 'robots.ps1')
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Q = 'C:\obqa'; $StateFile = Join-Path $Q 'state.json'
$AdminPass = 'Admin-Pass-QA-2026'; $Secret = 'JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP'; $Login = 'qa-win'; $CustPass = 'Customer-Pass-1'
$Data = 'C:\OnlineBackup-QA\Data'
$Results = New-Object System.Collections.ArrayList
$S = @{}   # what the journeys learn (the set's id, the install folder ...), kept across a reboot
if ($Phase -ne 'after-reboot') { Remove-Item $Q -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force -Path $Q | Out-Null }
elseif (Test-Path $StateFile) { (Get-Content $StateFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $S[$_.Name] = $_.Value } }
function SaveState { $S | ConvertTo-Json | Set-Content $StateFile -Encoding UTF8 }

# The run's facts: Windows, the product's version, the commit
$facts = [ordered]@{ phase = $Phase; windows = (Get-CimInstance Win32_OperatingSystem | ForEach-Object { "$($_.Caption) $($_.Version) build $($_.BuildNumber)" }); computer = $env:COMPUTERNAME
  package = (Split-Path $Package -Leaf); commit = $env:GITHUB_SHA; run = $env:GITHUB_RUN_ID; started = (Get-Date).ToString('o'); resolution = (Resolution); dpi = (Dpi); powershell = $PSVersionTable.PSVersion.ToString()
  interactive = [Environment]::UserInteractive; user = "$env:USERDOMAIN\$env:USERNAME" }
$facts | ConvertTo-Json | Set-Content (Join-Path $Out "facts-$Phase.json") -Encoding UTF8
Write-Host ($facts | ConvertTo-Json)

function Journey([string]$id, [string]$title, [scriptblock]$body) {
  if ($Only -and $id -notmatch $Only) { return }
  New-Journey $id $title $Out | Out-Null
  $nt = ''
  try { $r = @(& $body); $last = $(if ($r.Count) { $r[-1] } else { $null }); if ($last -is [string] -and $last -like 'NOT TESTED:*') { $nt = $last.Substring(11).Trim() } }
  catch { [void]$script:Journey.steps.Add([ordered]@{ step = 'the journey stopped'; expected = 'no error'; actual = $_.Exception.Message + ' @ line ' + $_.InvocationInfo.ScriptLineNumber; result = 'FAIL'; seconds = 0; screenshot = (Shot 'stopped'); at = (Get-Date).ToString('HH:mm:ss') }); Write-Host "[FAIL] stopped: $($_.Exception.Message) @ $($_.InvocationInfo.ScriptLineNumber)" }
  Evidence $script:Journey.dir
  $j = Close-Journey $nt
  $vis = @($j.visual | Where-Object { $_ }); $ux = @($j.ux | Where-Object { $_ })
  $shots = @($j.steps | Where-Object { $_.screenshot -like '*.png' }).Count + $vis.Count
  $row = [ordered]@{ id = $j.id; title = $j.title; functional = $j.result; reason = $j.reason
    visual = $(if ($j.result -eq 'NOT TESTED') { 'NOT TESTED' } elseif (@(Get-ChildItem $j.dir -Filter *.tree.json -ErrorAction SilentlyContinue).Count -eq 0) { 'NOT TESTED' } elseif (@($vis | Where-Object { $_.kind -eq 'VISUAL' -and ($_.severity -eq 'High' -or $_.severity -eq 'Medium') }).Count -gt 0) { 'FAIL' } else { 'PASS' })
    ux = $(if ($ux.Count -eq 0) { 'NOT TESTED' } elseif (@($ux | Where-Object { $_.result -eq 'FAIL' }).Count -gt 0) { 'FAIL' } else { 'PASS' })
    visualFindings = @($vis | Where-Object { $_.kind -eq 'VISUAL' }).Count; uxFindings = @($vis | Where-Object { $_.kind -eq 'UX' }).Count }
  [void]$Results.Add($row)
  WriteSummary
}
function WriteSummary {
  [ordered]@{ facts = $facts; journeys = $Results } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $Out "windows-$Phase.json") -Encoding UTF8
  $script:Visual | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $Out "visual-findings-$Phase.json") -Encoding UTF8
}
# What a reviewer needs to see that it really ran: services, processes, the product's logs, the event log
function Evidence([string]$dir) {
  try {
    [ordered]@{ agent = (ServiceFacts 'OnlineBackupAgent'); server = (ServiceFacts 'OnlineBackupServer'); shadows = (ShadowCount)
      processes = @(Get-Process -Name 'OnlineBackup*', 'restic', 'Setup' -ErrorAction SilentlyContinue | ForEach-Object { [ordered]@{ name = $_.ProcessName; pid = $_.Id; path = $_.Path; started = $_.StartTime } }) } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $dir 'windows-state.json') -Encoding UTF8
    if ($S.dataDir -and (Test-Path (Join-Path $S.dataDir 'logs'))) { Copy-Item (Join-Path $S.dataDir 'logs') (Join-Path $dir 'agent-logs') -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path "$Q\sys\logs") { Copy-Item "$Q\sys\logs" (Join-Path $dir 'server-logs') -Recurse -Force -ErrorAction SilentlyContinue }
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddMinutes(-30) } -ErrorAction SilentlyContinue | Where-Object { $_.ProviderName -match 'OnlineBackup|\.NET Runtime|Application Error|VSS|Windows Error Reporting' } |
      Select-Object -First 40 TimeCreated, ProviderName, Id, LevelDisplayName, Message | Format-List | Out-File (Join-Path $dir 'event-log.txt') -Encoding UTF8
    Get-WinEvent -FilterHashtable @{ LogName = 'System'; StartTime = (Get-Date).AddMinutes(-30); ProviderName = 'Service Control Manager' } -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'OnlineBackup' } |
      Select-Object -First 30 TimeCreated, Id, Message | Format-List | Out-File (Join-Path $dir 'service-events.txt') -Encoding UTF8
  } catch { }
}

# ---------------------------------------------------------------- shared actions (each one checked from outside)
function SignInAdmin {
  $r = Http 'POST' "$Url/api/admin/login" (Msg @{ login = 'admin'; password = $AdminPass; otp = (Totp $Secret) })
  $script:Ses = F $r[1] 'session'; return @([bool]$script:Ses, "HTTP $($r[0])")
}
function AgentFacts { $s = ServiceFacts 'OnlineBackupAgent'; if ($s.exists) { $S.agentExe = ($s.path -replace '^"([^"]+)".*$', '$1'); $S.installDir = Split-Path $S.agentExe; $S.dataDir = ($s.path -replace '^.*--home "([^"]+)".*$', '$1'); SaveState }; return $s }
# the customer's backup sets as the server lists them
function SetsOf([string]$login) {
  $x = [xml](Api 'GET' 'users'); $out = @()
  foreach ($i in @($x.SelectNodes("//l[@n='users']/i[f[@n='login']='$login']/l[@n='sets']/i"))) { $h = @{}; foreach ($f in @($i.f)) { $h[$f.n] = $f.'#text' }; $out += $h }
  return ,$out
}
# the schedules of the customer's sets, as the server stores them (its own profile file)
function ScheduleText { (Get-ChildItem "$Q\users\$Login" -Recurse -File -Include *.xml -ErrorAction SilentlyContinue | ForEach-Object { [regex]::Matches((Get-Content $_.FullName -Raw), '<(DAILY|WEEKLY)_SCHEDULE[^>]*>') | ForEach-Object { $_.Value } } | Sort-Object -Unique) -join "`n" }
function Connected { $c = @(Items (Api 'GET' "users/$Login/computers") 'computers'); return $c }
# "Back up now" in the window, then the server's history must show a new finished run of the set
function BackupChecked([string]$label, [string]$want = 'ok') {
  $w = Open-Client $S.installDir
  $before = (Runs $Login 'Backup').Count
  $msgs = BackupNow-ViaUi $w $label
  $run = WaitNewRun $Login 'Backup' $before 20
  $okUi = WaitIdle-Client $w 5
  Start-Sleep -Seconds 5
  $t = Texts $w
  $res = $(if ($run) { $run['result'] } else { 'no run' })
  $statusOk = $(if ($want -eq 'not-ok') { $run -and $run['status'] -ne 'ok' } else { $run -and $run['status'] -eq $want })
  StepLook "$label - result" "the server records a new run: $want; the window shows the result" $w { @(($statusOk -and $okUi), "server: $res (new $($run['new']), bytes $($run['bytes'])); window: $(($t -split '\|' | Where-Object { $_ -like '*last backup*' }) -join ';'); messages: $($msgs -join ' || ')") } 'en' $ClientProc | Out-Null
  UxCheck "$label - the window shows the result of the backup" 'the card says the result' (($t -like '*Completed*') -or ($t -like '*Failed*') -or ($t -like '*warnings*')) (($t -split '\|' | Where-Object { $_ -like '*last backup*' }) -join ';')
  return $run
}
# delete the source, restore in the window to a new folder, compare every file's SHA-256 with the manifest
function RestoreChecked([string]$label, $want, [switch]$KeepSource) {
  if (-not $KeepSource) { Remove-Tree $Data; Step "$label - the source folder is deleted" 'no source folder' { @((-not (Test-Path $Data)), $Data) } -NoShot | Out-Null }
  $w = Open-Client $S.installDir
  $S.restoreNo = 1 + [int]$S.restoreNo; SaveState
  $target = "$Q\restore-$($S.restoreNo)"
  $before = (Runs $Login 'Restore').Count
  $msgs = Restore-ViaUi $w $target $CustPass $label
  $run = WaitNewRun $Login 'Restore' $before 20
  WaitIdle-Client $w 5 | Out-Null
  Look $w "$label - restore result in the window" 'en' $ClientProc | Out-Null
  $got = Manifest (Join-Path $target ($Data.Replace(':', '')))
  $d = Compare-Manifest $want $got
  Save-Manifest $got (Join-Path $script:Journey.dir "$($S.restoreNo)-restored.sha256")
  ($d -join "`r`n") | Set-Content (Join-Path $script:Journey.dir "$($S.restoreNo)-differences.txt") -Encoding UTF8
  $ok = Step "$label - ORACLE: every restored file has the SHA-256 of the original" "$($want.Count) files identical, nothing missing or extra" { @((($run -ne $null) -and ($run['status'] -eq 'ok') -and ($d.Count -eq 0) -and ($got.Count -eq $want.Count)), "server: $(if ($run) { $run['result'] } else { 'no restore run' }); files $($got.Count)/$($want.Count); differences $($d.Count): $(($d | Select-Object -First 6) -join '; ')") } -NoShot
  if ($d.Count -gt 0 -or -not $run) { Note "$label - messages" ($msgs -join ' || ') }
  if (-not $KeepSource) { Golden $Data }   # the same bytes again (the dataset is made from a fixed seed)
  return $ok
}
function BigFile([int]$mb) { $f = Join-Path $Data 'Big\video.bin'; New-Item -ItemType Directory -Force -Path (Split-Path $f) | Out-Null; $b = New-Object byte[] (1MB); $r = New-Object Random 11; $s = [IO.File]::Create($f); for ($i = 0; $i -lt $mb; $i++) { $r.NextBytes($b); $s.Write($b, 0, $b.Length) }; $s.Close() }
function WaitLive([int]$seconds = 120) { $until = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $until) { if ((LiveRuns $S.setId).Count -gt 0) { return $true }; Start-Sleep -Milliseconds 700 }; return $false }

function Tour {
  $orig = Resolution; $done = 0
  foreach ($res in @(@(1920, 1080), @(1366, 768), @(1024, 768))) {
    $e = Set-Resolution $res[0] $res[1]
    if ($e) { Note "Resolution $($res[0])x$($res[1])" "NOT TESTED: $e"; continue }
    $done++
    foreach ($lang in @('en', 'he')) {
      Close-Client; Set-ItemProperty -Path 'HKCU:\Software\OnlineBackup' -Name Language -Value $lang -ErrorAction SilentlyContinue
      if (-not (Test-Path 'HKCU:\Software\OnlineBackup')) { New-Item 'HKCU:\Software\OnlineBackup' -Force | Out-Null; Set-ItemProperty -Path 'HKCU:\Software\OnlineBackup' -Name Language -Value $lang }
      $w = Open-Client $S.installDir
      if (-not $w) { Look $null "Client $lang $(Resolution)" $lang; continue }
      $count = (NavButtons $w).Count
      if ($count -eq 0) { Look $w ("Tour {0} {1} - no menu found" -f (Resolution), $lang) $lang $ClientProc | Out-Null }
      for ($i = 0; $i -lt $count; $i++) { $n = (NavButtons $w)[$i]; $name = $n.Current.Name.Trim(); Click $n; Start-Sleep -Seconds 3; foreach ($d in @(Dialogs $ClientProc $w)) { Look $d "Tour dialog" $lang $ClientProc | Out-Null; try { Click (@(Find $d $CT::Button) | Select-Object -First 1) } catch { } }; $w = ClientWindow 10; Look $w ("Tour {0} {1} page {2} {3}" -f (Resolution), $lang, ($i + 1), $name) $lang $ClientProc | Out-Null }
    }
    Close-Client
    # the installer's first page in this resolution (maintenance page: the program is installed)
    Start-Process -FilePath $S.setup | Out-Null; $sw = SetupWindow 60; Look $sw ("Tour {0} installer" -f (Resolution)) 'en' $SetupProc | Out-Null
    if ($sw) { try { Click (Button $sw 'Cancel') } catch { Get-Process $SetupProc -ErrorAction SilentlyContinue | Stop-Process -Force } }
  }
  Set-ItemProperty -Path 'HKCU:\Software\OnlineBackup' -Name Language -Value 'en' -ErrorAction SilentlyContinue
  $p = $orig.Split('x'); [void](Set-Resolution ([int]$p[0]) ([int]$p[1]))
  if ($done -eq 0) { return 'NOT TESTED: the screen resolution could not be changed on this machine' }
  Note 'DPI' "125% and 150%: NOT TESTED in this job - the scale of a Windows session changes only after signing out, and a hosted runner cannot sign out; screen DPI here: $(Dpi)%"
}

# ======================================================================================== main / before-reboot
if ($Phase -ne 'after-reboot') {

Journey 'W01' 'Server: install from the real package (Windows service) and sign in' {
  $pkg = Join-Path $Q 'pkg'; Expand-Archive -Path $Package -DestinationPath $pkg -Force
  Step 'install-server.ps1 in Windows PowerShell (as the README says)' 'exit 0, service OnlineBackupServer Running' {
    $o = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $pkg 'install-server.ps1') -HostName localhost -Port 8443 -SystemHome "$Q\sys" -UserHome "$Q\users" -AdminPassword $AdminPass 2>&1 | Out-String
    $o | Set-Content (Join-Path $script:Journey.dir 'install-server.txt') -Encoding UTF8
    $st = WaitService OnlineBackupServer 'Running' 90; @((($LASTEXITCODE -eq 0) -and ($st -eq 'Running')), "exit $LASTEXITCODE; $st") } -NoShot | Out-Null
  # FIXTURES (test setup, not product behaviour): the administrator's authenticator secret and a licence from a throw-away key
  $srvExe = Join-Path $env:ProgramFiles 'OnlineBackup Server\OnlineBackup.Server.exe'
  Stop-Service OnlineBackupServer; [void](WaitService OnlineBackupServer 'Stopped' 60)
  $sx = "$Q\sys\conf\system.xml"; (Get-Content $sx -Raw -Encoding UTF8) -replace '(<ADMIN [^>]*?)TOTP_SECRET="[^"]*"', "`$1TOTP_SECRET=`"$Secret`"" | Set-Content $sx -Encoding UTF8 -NoNewline
  $pub = (& $srvExe license-keygen --out "$Q\lic.key" | Select-Object -Last 1).Trim()
  [Environment]::SetEnvironmentVariable('OB_LICENSE_PUBKEY', $pub, 'Machine'); $env:OB_LICENSE_PUBKEY = $pub
  $sid = (& $srvExe server-id --system-home "$Q\sys" | Select-Object -Last 1).Trim()
  $lic = (& $srvExe license-issue --key "$Q\lic.key" --server-id $sid --company 'QA IT' --users 100 --storage-gb 1000 --days 30 | Select-Object -Last 1).Trim()
  (Get-Content $sx -Raw -Encoding UTF8) -replace '<LICENSE KEY="" />', "<LICENSE KEY=`"$lic`" />" | Set-Content $sx -Encoding UTF8 -NoNewline
  Start-Service OnlineBackupServer
  Step 'The server service starts again with the licence' 'Running' { $st = WaitService OnlineBackupServer 'Running' 90; @(($st -eq 'Running'), $st) } -NoShot | Out-Null
  Step 'Server service: automatic start, restarts after a failure' 'StartMode Auto, failure actions = restart' { $s = ServiceFacts 'OnlineBackupServer'; $f = (& sc.exe qfailure OnlineBackupServer | Out-String); @((($s.start -eq 'Auto') -and ($f -match 'RESTART')), "$($s.start); $(($f -split "`n" | Where-Object { $_ -match 'RESTART|RESET' }) -join ' ')") } -NoShot | Out-Null
  Step 'Admin sign-in over HTTPS (password + authenticator)' 'a session' { $tcp = New-Object Net.Sockets.TcpClient('localhost', 8443); $ssl = New-Object Net.Security.SslStream($tcp.GetStream(), $false, { $true }); $ssl.AuthenticateAsClient('localhost')
    $S.pin = ([Security.Cryptography.SHA256]::Create().ComputeHash($ssl.RemoteCertificate.GetRawCertData()) | ForEach-Object { $_.ToString('x2') }) -join ''; $ssl.Dispose(); $tcp.Dispose(); [ObQa.Trust]::Pin = $S.pin; SaveState
    SignInAdmin } -NoShot | Out-Null
  Api 'POST' 'settings' @{ publicUrl = $Url; certPin = $S.pin } | Out-Null
  Step 'A customer account for this computer' "user $Login" { Api 'POST' 'users' @{ login = $Login; password = $CustPass; alias = 'QA Windows'; quotaGB = 20; email = 'it@example.invalid' } | Out-Null; $u = Items (Api 'GET' 'users') 'users'; @((@($u | Where-Object { $_['login'] -eq $Login }).Count -eq 1), "$($u.Count) users") } -NoShot | Out-Null
  $S.setup = Join-Path $Q 'Setup.exe'
  Step 'Download the client Setup.exe from the server (what the IT company hands out)' 'a signed-size program' { $r = Http 'GET' "$Url/api/admin/clientpackage?os=windows" $null @{ 'X-Session' = $script:Ses } $S.setup; $len = (Get-Item $S.setup).Length; SaveState; @((($r[0] -eq 200) -and ($len -gt 1MB)), "HTTP $($r[0]), $len bytes") } -NoShot | Out-Null
}

Journey 'W02' 'Installer failure: a damaged Setup.exe installs nothing and says so' {
  $bad = Join-Path $Q 'Setup-damaged.exe'; $b = [IO.File]::ReadAllBytes($S.setup); [IO.File]::WriteAllBytes($bad, $b[0..([int]($b.Length * 0.6))])
  Start-Process -FilePath $bad | Out-Null
  $d = WaitWindow 'Setup-damaged' { $true } 60
  StepLook 'The damaged file says it is damaged' 'a message: damaged, download again' $d { $t = $(if ($d) { (DialogText $d) + ' ' + (Texts $d) } else { 'no window' }); @(($t -like '*damaged*'), $t) } 'en' 'Setup-damaged' | Out-Null
  if ($d) { try { Click (Button $d 'OK') } catch { } }
  Start-Sleep -Seconds 2; Get-Process 'Setup-damaged' -ErrorAction SilentlyContinue | Stop-Process -Force
  Step 'Nothing was installed (Windows service manager, installed programs)' 'no service, no entry' { $s = Get-Service OnlineBackupAgent -ErrorAction SilentlyContinue; $u = @(UninstallEntries '*OnlineBackup.Agent*'); @(((-not $s) -and $u.Count -eq 0), "service: $([bool]$s); entries: $($u.Count)") } -NoShot | Out-Null
  UxCheck 'Installer: a damaged download tells the customer what to do' 'download it again' ((DialogText $d) -like '*Download it again*') (DialogText $d)
}

Journey 'W03' 'Client installation through the installer window, proven by Windows' {
  $ok = Install-ViaUi $S.setup -Launch
  $s = AgentFacts
  Step 'Windows service manager: the backup service exists, starts automatically and is Running' 'OnlineBackupAgent Running, Auto' { $st = WaitService OnlineBackupAgent 'Running' 60; $s2 = ServiceFacts 'OnlineBackupAgent'; @((($st -eq 'Running') -and ($s2.start -eq 'Auto')), "$st $($s2.start) as $($s2.account)") } -NoShot | Out-Null
  Step 'Its process runs from the installed program' 'the service PID is OnlineBackup.Agent.exe in the install folder' { $s2 = ServiceFacts 'OnlineBackupAgent'; @((($s2.pid -gt 0) -and ($s2.process -eq $S.agentExe)), "pid $($s2.pid) $($s2.process)") } -NoShot | Out-Null
  Step 'Installed programs list (Apps / Programs and Features)' 'one entry with a name, version and an uninstall command' { $u = @(UninstallEntries '*OnlineBackup.Agent*'); @(($u.Count -eq 1), ($u | ForEach-Object { "$($_.DisplayName) $($_.DisplayVersion) [$($_.Publisher)] $($_.InstallLocation)" }) -join '; ') } -NoShot | Out-Null
  Step 'The program files on disk' 'Agent, Client, Core, BouncyCastle, restic in the install folder' { $miss = @('OnlineBackup.Agent.exe', 'OnlineBackup.Client.exe', 'OnlineBackup.Core.dll', 'BouncyCastle.Crypto.dll', 'restic.exe') | Where-Object { -not (Test-Path (Join-Path $S.installDir $_)) }; @(($miss.Count -eq 0), "$($S.installDir); missing: $($miss -join ', ')") } -NoShot | Out-Null
  Step 'The settings folder (ProgramData) belongs to SYSTEM and Administrators only' 'no Users / Everyone access' { $a = (Get-Acl $S.dataDir).Access | ForEach-Object { "$($_.IdentityReference):$($_.FileSystemRights)" }; @((($a -join ' ') -notmatch 'Users|Everyone|Authenticated'), ($a -join '; ')) } -NoShot | Out-Null
  Step 'Start menu shortcut and start with Windows' 'a shortcut to the program, and one with --tray in Startup' { $l = @(Shortcuts '*OnlineBackup.Client.exe'); @((($l.Count -ge 2) -and (@($l | Where-Object { $_.args -like '*--tray*' }).Count -ge 1)), ($l | ForEach-Object { $_.file + ' ' + $_.args }) -join '; ') } -NoShot | Out-Null
  $w = ClientWindow 60
  StepLook 'Finish with "Open now" opens the program window' 'the program window' $w { @([bool]$w, $(if ($w) { $w.Current.Name } else { 'no window in 60 s' })) } 'en' $ClientProc | Out-Null
}

Journey 'W04' 'First run: sign in through the window, the agent connects to the server' {
  $w = Open-Client $S.installDir
  $ok = Connect-ViaUi $w $Login $CustPass
  Step 'ORACLE (server): this computer is registered and connected' 'the computer in the customer''s list, connected' { $c = @(Connected); $me = @($c | Where-Object { $_['name'] -eq $env:COMPUTERNAME -and $_['connected'] -eq '1' }); @(($me.Count -eq 1), ($c | ForEach-Object { "$($_['name']) connected=$($_['connected']) seen=$($_['lastSeen']) version=$($_['version'])" }) -join '; ') } -NoShot | Out-Null
}

Journey 'W05' 'FULL CHAIN: backup set in the window -> Back up now -> delete the source -> Restore in the window -> SHA-256' {
  Remove-Tree $Data; Golden $Data
  $S.manifest = "$Q\golden.sha256"; $want = Manifest $Data; Save-Manifest $want $S.manifest; Save-Manifest $want (Join-Path $script:Journey.dir 'source.sha256'); SaveState
  Note 'The golden dataset' "$($want.Count) files: text, Hebrew and Japanese names, spaces, an empty file, 20 MB + 3 MB random, duplicates, 100 small files, a long path, read-only and hidden files"
  $w = Open-Client $S.installDir
  NewSet-ViaUi $w 'QA files' $Data $CustPass | Out-Null
  $set = $null
  Step 'ORACLE (server): the backup set exists with the chosen folder' "a set 'QA files' with source $Data" { $set = @(SetsOf $Login | Where-Object { $_['name'] -eq 'QA files' }) | Select-Object -First 1; if ($set) { $S.setId = $set['id']; SaveState }; @((($set -ne $null) -and ($set['sources'] -eq $Data)), "set id $($S.setId); sources $($set['sources']); computer $($set['computer'])") } -NoShot | Out-Null
  $run = BackupChecked 'Backup 1'
  RestoreChecked 'Restore 1' $want | Out-Null
}

Journey 'W06' 'Service restart: Windows restarts the backup service, then backup and restore again' {
  $want = Manifest $Data
  Step 'Restart the service (Windows service manager)' 'Running again' { Restart-Service OnlineBackupAgent; $st = WaitService OnlineBackupAgent 'Running' 90; @(($st -eq 'Running'), $st) } -NoShot | Out-Null
  Add-Content (Join-Path $Data 'Documents\letter.txt') 'a new line after the restart'; $want = Manifest $Data
  BackupChecked 'Backup after restart' | Out-Null
  RestoreChecked 'Restore after restart' $want | Out-Null
}

Journey 'W07' 'Agent crash mid-backup: the process is killed; Windows brings it back; no ghost run; the next backup restores identical' {
  BigFile 600; $want = Manifest $Data
  $w = Open-Client $S.installDir
  $before = (Runs $Login 'Backup').Count
  BackupNow-ViaUi $w 'Backup to be killed' | Out-Null
  $live = WaitLive 120
  Step 'The backup is running (server live view)' 'a live run' { @($live, "live=$live") } -NoShot | Out-Null
  Start-Sleep -Seconds 3
  $pid0 = (ServiceFacts 'OnlineBackupAgent').pid
  Step 'Kill the service process (as a crash)' 'killed' { Stop-Process -Id $pid0 -Force; Start-Sleep 2; @((-not (Get-Process -Id $pid0 -ErrorAction SilentlyContinue)), "pid $pid0") } -NoShot | Out-Null
  Look (ClientWindow 5) 'Window right after the crash' 'en' $ClientProc | Out-Null
  Step 'Windows restarts the service by itself (failure actions)' 'Running with a new process within 3 minutes' { $st = WaitService OnlineBackupAgent 'Running' 180; $p = (ServiceFacts 'OnlineBackupAgent').pid; @((($st -eq 'Running') -and ($p -ne $pid0)), "$st pid $p") } -NoShot | Out-Null
  $until = (Get-Date).AddMinutes(8); while ((Get-Date) -lt $until -and (LiveRuns $S.setId).Count -gt 0) { Start-Sleep 5 }
  $until = (Get-Date).AddMinutes(8); while ((Get-Date) -lt $until -and (Runs $Login 'Backup').Count -le $before) { Start-Sleep 10 }
  Step 'No ghost: the killed run is not "running" any more' 'not in the live view; recorded as interrupted' { $l = (LiveRuns $S.setId).Count; $r = Runs $Login 'Backup'; $last = $r[-1]; @((($l -eq 0) -and ($r.Count -gt $before) -and ($last['status'] -ne 'ok')), "live $l; last run: $($last['result'])") } -NoShot | Out-Null
  $w = Open-Client $S.installDir
  Look $w 'Window after recovery' 'en' $ClientProc | Out-Null
  BackupChecked 'Backup after the crash' | Out-Null
  RestoreChecked 'Restore after the crash' $want | Out-Null
}

Journey 'W08' 'Service stopped mid-backup: the run ends cleanly; start; backup and restore identical' {
  Add-Content (Join-Path $Data 'Big\video.bin') ('y' * 1MB) -NoNewline; $want = Manifest $Data
  $w = Open-Client $S.installDir
  $before = (Runs $Login 'Backup').Count
  BackupNow-ViaUi $w 'Backup to be stopped' | Out-Null
  Step 'The backup is running' 'a live run' { $l = WaitLive 120; @($l, "live=$l") } -NoShot | Out-Null
  Start-Sleep -Seconds 2
  Step 'Stop the service (Windows service manager) while it backs up' 'Stopped within 2 minutes' { Stop-Service OnlineBackupAgent -ErrorAction SilentlyContinue; $st = WaitService OnlineBackupAgent 'Stopped' 150; @(($st -eq 'Stopped'), $st) } -NoShot | Out-Null
  Step 'The stopped run is closed on the server (not running)' 'not live; a recorded result' { Start-Sleep 5; $l = (LiveRuns $S.setId).Count; $r = Runs $Login 'Backup'; @((($l -eq 0) -and ($r.Count -gt $before)), "live $l; last: $($r[-1]['result'])") } -NoShot | Out-Null
  Start-Service OnlineBackupAgent; [void](WaitService OnlineBackupAgent 'Running' 90)
  BackupChecked 'Backup after the stop' | Out-Null
  RestoreChecked 'Restore after the stop' $want | Out-Null
}

Journey 'W09' 'Server crash mid-backup: the server process is killed; Windows restarts it; the next backup restores identical' {
  Add-Content (Join-Path $Data 'Big\video.bin') ('z' * 1MB) -NoNewline; $want = Manifest $Data
  $w = Open-Client $S.installDir
  BackupNow-ViaUi $w 'Backup when the server dies' | Out-Null
  Step 'The backup is running' 'a live run' { $l = WaitLive 120; @($l, "live=$l") } -NoShot | Out-Null
  Start-Sleep -Seconds 3
  $sp = (ServiceFacts 'OnlineBackupServer').pid
  Step 'Kill the server process' 'killed' { Stop-Process -Id $sp -Force; Start-Sleep 2; @((-not (Get-Process -Id $sp -ErrorAction SilentlyContinue)), "pid $sp") } -NoShot | Out-Null
  Look (ClientWindow 5) 'Window while the server is down' 'en' $ClientProc | Out-Null
  Step 'Windows restarts the server by itself' 'Running with a new process within 3 minutes' { $st = WaitService OnlineBackupServer 'Running' 180; $p = (ServiceFacts 'OnlineBackupServer').pid; @((($st -eq 'Running') -and ($p -ne $sp)), "$st pid $p") } -NoShot | Out-Null
  if ((Get-Service OnlineBackupServer).Status -ne 'Running') { Start-Service OnlineBackupServer; [void](WaitService OnlineBackupServer 'Running' 60) }
  Start-Sleep 5; [void](SignInAdmin)
  $until = (Get-Date).AddMinutes(10); while ((Get-Date) -lt $until -and (LiveRuns $S.setId).Count -gt 0) { Start-Sleep 5 }
  Step 'No ghost run after the server came back' 'nothing live for the set' { $l = (LiveRuns $S.setId).Count; @(($l -eq 0), "live $l") } -NoShot | Out-Null
  BackupChecked 'Backup after the server crash' | Out-Null
  RestoreChecked 'Restore after the server crash' $want | Out-Null
}

Journey 'W10' 'VSS: a file locked by another program is backed up through the shadow copy; no shadow copy is left behind' {
  $locked = Join-Path $Data 'Documents\locked.pst'; $b = New-Object byte[] (5MB); (New-Object Random 5).NextBytes($b); [IO.File]::WriteAllBytes($locked, $b)
  $want = Manifest $Data
  $holder = Hold $locked
  Step 'The file is really locked (another program cannot read it)' 'reading fails' { $e = ''; try { [IO.File]::ReadAllBytes($locked) | Out-Null } catch { $e = $_.Exception.Message }; @([bool]$e, $e) } -NoShot | Out-Null
  $sh0 = ShadowCount
  BackupChecked 'Backup with a locked file' | Out-Null
  Step 'No shadow copy is left on the computer after the run' "shadow copies before = after ($sh0)" { $n = ShadowCount; @(($n -eq $sh0), "before $sh0, after $n") } -NoShot | Out-Null
  Stop-Process -Id $holder.Id -Force -ErrorAction SilentlyContinue
  RestoreChecked 'Restore with the locked file' $want | Out-Null
}

Journey 'W11' 'VSS failure: the shadow copy service is disabled; the locked file is reported, never a silent success; recovery' {
  $locked = Join-Path $Data 'Documents\locked.pst'; if (-not (Test-Path $locked)) { $b = New-Object byte[] (5MB); (New-Object Random 5).NextBytes($b); [IO.File]::WriteAllBytes($locked, $b) }
  $holder = Hold $locked
  Step 'Disable the Volume Shadow Copy service' 'VSS disabled and stopped' { Stop-Service VSS -Force -ErrorAction SilentlyContinue; Set-Service VSS -StartupType Disabled; $s = Get-CimInstance Win32_Service -Filter "Name='VSS'"; @(($s.StartMode -eq 'Disabled'), "$($s.State) $($s.StartMode)") } -NoShot | Out-Null
  $run = BackupChecked 'Backup without VSS' 'not-ok'
  Step 'The result names the problem (not a plain success)' 'warning or error; the log names the locked file or the shadow copy' { $log = Get-ChildItem (Join-Path $S.dataDir 'logs') -Recurse -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1 | Get-Content -Raw -ErrorAction SilentlyContinue; @((($run['result'] -ne 'BS_STOP_SUCCESS') -and ($log -match 'locked\.pst|Shadow')), "result $($run['result']); log mentions: $(@([regex]::Matches([string]$log, '[^\r\n]*(locked\.pst|Shadow)[^\r\n]*') | Select-Object -First 3 | ForEach-Object { $_.Value }) -join ' / ')") } -NoShot | Out-Null
  Step 'Recovery: enable VSS again' 'Manual' { Set-Service VSS -StartupType Manual; @($true, 'Manual') } -NoShot | Out-Null
  Stop-Process -Id $holder.Id -Force -ErrorAction SilentlyContinue
  $want = Manifest $Data
  BackupChecked 'Backup with VSS back' | Out-Null
  RestoreChecked 'Restore after VSS came back' $want | Out-Null
}

Journey 'W12' 'Permission denied: a folder the service may not read is an error, not a deletion; recovery' {
  $want = Manifest $Data
  $sec = Join-Path $Data 'Documents'
  Step 'Deny the service (SYSTEM) reading one folder' 'icacls deny' { $o = & icacls $sec /deny 'NT AUTHORITY\SYSTEM:(OI)(CI)(R)' 2>&1 | Out-String; @(($LASTEXITCODE -eq 0), $o.Trim()) } -NoShot | Out-Null
  $run = BackupChecked 'Backup with a denied folder' 'bad'
  Step 'The run is "completed with errors" and names the folder' 'BS_STOP_SUCCESS_WITH_ERROR' { @(($run['result'] -eq 'BS_STOP_SUCCESS_WITH_ERROR'), "result $($run['result'])") } -NoShot | Out-Null
  & icacls $sec /remove:d 'NT AUTHORITY\SYSTEM' | Out-Null
  RestoreChecked 'Restore: the denied folder''s files are still in the backup' $want | Out-Null
  BackupChecked 'Backup after the permission is back' | Out-Null
}

Journey 'W13' 'Service that does not start: its program file is missing (as after an antivirus quarantine); Repair in the installer window brings it back' {
  $want = Manifest $Data
  Stop-Service OnlineBackupAgent; [void](WaitService OnlineBackupAgent 'Stopped' 120)
  Close-Client
  Move-Item (Join-Path $S.installDir 'OnlineBackup.Agent.exe') "$Q\OnlineBackup.Agent.exe.moved" -Force
  Step 'The service cannot start (Windows says so)' 'Start-Service fails or the service stops again' { $e = ''; try { Start-Service OnlineBackupAgent -ErrorAction Stop } catch { $e = $_.Exception.Message }; Start-Sleep 8; $st = (Get-Service OnlineBackupAgent).Status; @((($e -ne '') -or ($st -ne 'Running')), "error: $e; state $st") } -NoShot | Out-Null
  $w = Open-Client $S.installDir
  Look $w 'The program window while the service cannot start' 'en' $ClientProc | Out-Null
  UxCheck 'Client: when the service is not running the window says so and what to do' 'a clear message' (((Texts $w) + ' ' + (@(Dialogs $ClientProc $w | ForEach-Object { DialogText $_ }) -join ' ')) -like '*not running*') ((Texts $w) + ' ' + (@(Dialogs $ClientProc $w | ForEach-Object { DialogText $_ }) -join ' '))
  foreach ($d in @(Dialogs $ClientProc $w)) { try { Click (Button $d 'OK') } catch { } }
  Close-Client
  Maintain-ViaUi $S.setup 'repair' | Out-Null
  Step 'After Repair: the file is back and the service runs' 'OnlineBackup.Agent.exe present, Running' { $st = WaitService OnlineBackupAgent 'Running' 90; @(((Test-Path (Join-Path $S.installDir 'OnlineBackup.Agent.exe')) -and ($st -eq 'Running')), $st) } -NoShot | Out-Null
  BackupChecked 'Backup after Repair' | Out-Null
  RestoreChecked 'Restore after Repair' $want | Out-Null
}

Journey 'W14' 'Update of the agent: interrupted update rolls back; then Update in the window; backup and restore after' {
  if (-not $Package2) { return 'NOT TESTED: no second, newer package was given to this run' }
  $v1 = (Get-Content (Join-Path $S.installDir 'version.txt') -ErrorAction SilentlyContinue | Select-Object -First 1)
  $pkg2 = Join-Path $Q 'pkg2'; Expand-Archive -Path $Package2 -DestinationPath $pkg2 -Force
  Step 'The server is updated to the newer package (install-server.ps1, update mode)' 'Running, the newer client offered' { $o = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $pkg2 'install-server.ps1') -HostName localhost -Port 8443 -SystemHome "$Q\sys" -UserHome "$Q\users" 2>&1 | Out-String; $o | Set-Content (Join-Path $script:Journey.dir 'update-server.txt'); $st = WaitService OnlineBackupServer 'Running' 90; Start-Sleep 3; [void](SignInAdmin); @(($st -eq 'Running'), "$st; package $((Get-Content (Join-Path $pkg2 'version.txt')))") } -NoShot | Out-Null
  $v2 = (Get-Content (Join-Path $pkg2 'version.txt') | Select-Object -First 1)
  # interrupted: a program file held open by another program, so the copy fails in the middle
  $hold = Join-Path $S.installDir 'restic.exe'
  $holder = Hold $hold 600
  $w = Open-Client $S.installDir
  $link = @($w.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) | Where-Object { $_.Current.Name -like '*Check for updates*' } | Select-Object -First 1
  Step 'Interrupted update: start it in the window' 'the window offers the update' { if ($link) { try { ($link.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() } catch { } }; Start-Sleep 4; $m = Answer-Dialogs $w $CustPass 'Interrupted update' 30; @(($m.Count -gt 0), ($m -join ' || ')) } | Out-Null
  Start-Sleep 60
  Step 'Interrupted update: the old version still runs (rolled back), the service is Running' "version $v1, Running" { $st = WaitService OnlineBackupAgent 'Running' 180; $v = (Get-Content (Join-Path $S.installDir 'version.txt') | Select-Object -First 1); @((($st -eq 'Running') -and ($v -eq $v1) -and (Test-Path (Join-Path $S.installDir 'OnlineBackup.Agent.exe'))), "$st; version $v") } -NoShot | Out-Null
  Stop-Process -Id $holder.Id -Force -ErrorAction SilentlyContinue
  $want = Manifest $Data
  BackupChecked 'Backup after the interrupted update' | Out-Null
  $w = Open-Client $S.installDir
  $ub = @(Find $w $CT::Button) | Where-Object { $_.Current.Name -like '*Update*' -and $_.Current.Name -notlike '*Update to*' } | Select-Object -First 1
  if (-not $ub) { $link = @($w.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) | Where-Object { $_.Current.Name -like '*Check for updates*' } | Select-Object -First 1; if ($link) { ($link.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() } } else { Click $ub }
  $m = Answer-Dialogs $w $CustPass 'Update' 30
  Note 'Update messages' ($m -join ' || ')
  Step 'Update in the window: the newer version is installed and the service runs' "version $v2, Running" { $until = (Get-Date).AddMinutes(4); do { Start-Sleep 5; $v = (Get-Content (Join-Path $S.installDir 'version.txt') -ErrorAction SilentlyContinue | Select-Object -First 1) } while ($v -ne $v2 -and (Get-Date) -lt $until); $st = WaitService OnlineBackupAgent 'Running' 120; @((($v -eq $v2) -and ($st -eq 'Running')), "version $v; $st") } -NoShot | Out-Null
  $w = Open-Client $S.installDir; Look $w 'The window after the update' 'en' $ClientProc | Out-Null
  Add-Content (Join-Path $Data 'Documents\letter.txt') 'after the update'; $want = Manifest $Data
  BackupChecked 'Backup after the update' | Out-Null
  RestoreChecked 'Restore after the update' $want | Out-Null
}

Journey 'W15' 'Uninstall through the installer window, reinstall, sign in again, backup and restore' {
  Close-Client
  Maintain-ViaUi $S.setup 'remove' | Out-Null
  Step 'Removed: no service, no entry in the installed programs, no program files' 'all gone' { $s = Get-Service OnlineBackupAgent -ErrorAction SilentlyContinue; $u = @(UninstallEntries '*OnlineBackup.Agent*'); $f = Test-Path (Join-Path $S.installDir 'OnlineBackup.Agent.exe'); @(((-not $s) -and ($u.Count -eq 0) -and (-not $f)), "service $([bool]$s); entries $($u.Count); files $f") } -NoShot | Out-Null
  Step 'Removed: the backups are still on the server' 'the set and its runs are on the server' { $r = Runs $Login 'Backup'; @(($r.Count -gt 0), "$($r.Count) runs") } -NoShot | Out-Null
  Install-ViaUi $S.setup -Launch | Out-Null
  AgentFacts | Out-Null
  $w = ClientWindow 60; if (-not $w) { $w = Open-Client $S.installDir }
  $t = Texts $w
  if ($t -like '*User name*') { Connect-ViaUi $w $Login $CustPass | Out-Null } else { Note 'After reinstall' 'the program was still connected (settings kept), no sign-in asked' }
  $w = Open-Client $S.installDir; Nav $w 'Backup status'
  StepLook 'After reinstall the backup set is there' "'QA files'" $w { $t = Texts $w; @(($t -like '*QA files*'), $t) } 'en' $ClientProc | Out-Null
  $want = Manifest $Data
  BackupChecked 'Backup after reinstall' | Out-Null
  RestoreChecked 'Restore after reinstall' $want | Out-Null
}

Journey 'W16' 'Visual tour: every screen in English and Hebrew at 1920x1080, 1366x768 and 1024x768' { Tour }

Journey 'W17' 'System State backup (Windows Server Backup)' {
  if (-not (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue)) { return 'NOT TESTED: this Windows is not a server edition (no Windows Server Backup feature)' }
  Step 'Windows Server Backup feature' 'installed' { $r = Install-WindowsFeature Windows-Server-Backup; @($r.Success, "$($r.ExitCode) restart=$($r.RestartNeeded)") } -NoShot | Out-Null
  $w = Open-Client $S.installDir
  Nav $w 'New backup'
  $combo = @(Find $w $CT::ComboBox) | Where-Object { $_.Current.BoundingRectangle.X -gt ($w.Current.BoundingRectangle.X + 240) } | Select-Object -First 1
  ($combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand(); Start-Sleep 1
  $item = @($combo.FindAll($TS::Descendants, ($PC::new($AE::ControlTypeProperty, $CT::ListItem)))) | Where-Object { $_.Current.Name -like '*System State*' } | Select-Object -First 1
  ($item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select(); Start-Sleep 1
  Look $w 'System State backup configured' 'en' $ClientProc | Out-Null
  Click (PageButton $w 'New backup'); $m = Answer-Dialogs $w $CustPass 'System State added' 30
  Step 'The System State set is added' 'was added' { @((($m -join ' ') -like '*was added*'), ($m -join ' || ')) } -NoShot | Out-Null
  $w = Open-Client $S.installDir; Nav $w 'Backup status'
  $btns = @(Find $w $CT::Button 'Back up now'); $b = $btns | Sort-Object { $_.Current.BoundingRectangle.Y } | Select-Object -Last 1
  $before = (Runs $Login 'Backup').Count; Click $b; Answer-Dialogs $w $null 'System State started' 15 | Out-Null
  $run = WaitNewRun $Login 'Backup' $before 45
  Step 'The System State backup finishes successfully (server record)' 'ok' { @((($run -ne $null) -and ($run['status'] -eq 'ok')), $(if ($run) { "$($run['result']) bytes $($run['bytes'])" } else { 'no run in 45 min' })) } | Out-Null
  Note 'System State restore' 'NOT TESTED: restoring the system state of the test machine would replace its own registry and boot files; only the backup is tested here'
}

Journey 'W18' 'Reboot' {
  if ($Phase -eq 'main') { return 'NOT TESTED: a GitHub-hosted runner cannot restart (the job ends with the machine). The real reboot runs in the virtual-machine job (phase before-reboot / after-reboot).' }
  $S.scheduleBefore = ScheduleText; $S.runsBefore = (Runs $Login 'Backup').Count; $S.rebootAt = (Get-Date).ToString('o'); SaveState
  Step 'The schedule before the restart' 'a daily schedule' { @([bool]$S.scheduleBefore, $S.scheduleBefore) } -NoShot | Out-Null
  Step 'Ready for a real restart' 'the state is saved; the VM driver (vm-phase.ps1) restarts Windows after keeping this evidence' { @((Test-Path $StateFile), $StateFile) } -NoShot | Out-Null
}

WriteSummary
}

# ======================================================================================== after-reboot (a real restart)
if ($Phase -eq 'after-reboot') {
  [ObQa.Trust]::Pin = $S.pin
  Journey 'W19' 'After a real Windows restart: services back, agent connected, no ghost run, no lock, schedule kept, backup and restore' {
    Step 'Windows really restarted' 'boot time after the journey started' { $b = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime; @(($b -gt [datetime]$S.rebootAt), "boot $b; asked $($S.rebootAt)") } -NoShot | Out-Null
    Step 'Both services came back by themselves' 'Running, Running' { $a = WaitService OnlineBackupAgent 'Running' 300; $s = WaitService OnlineBackupServer 'Running' 300; @((($a -eq 'Running') -and ($s -eq 'Running')), "agent $a, server $s") } -NoShot | Out-Null
    Start-Sleep 20; [void](SignInAdmin)
    Step 'The agent is connected again (server view)' 'connected, seen after the restart' { $c = @(Connected | Where-Object { $_['name'] -eq $env:COMPUTERNAME }); @((($c.Count -eq 1) -and ($c[0]['connected'] -eq '1')), ($c | ForEach-Object { "connected=$($_['connected']) seen=$($_['lastSeen'])" }) -join ';') } -NoShot | Out-Null
    Step 'No ghost run, no locked set' 'nothing live' { $l = (LiveRuns $S.setId).Count; @(($l -eq 0), "live $l") } -NoShot | Out-Null
    Step 'The schedule is kept' 'the same set settings as before the restart' { $now = ScheduleText; @((($now -eq $S.scheduleBefore) -and [bool]$now), $now) } -NoShot | Out-Null
    $w = Open-Client $S.installDir
    Look $w 'The program after the restart' 'en' $ClientProc | Out-Null
    Add-Content (Join-Path $Data 'Documents\letter.txt') 'after the restart'; $want = Manifest $Data
    BackupChecked 'Backup after the restart' | Out-Null
    RestoreChecked 'Restore after the restart' $want | Out-Null
  }
  Journey 'W20' "Visual tour after the restart, at this session's scale" { Note 'Scale' "$(Dpi)% (set before the restart; Windows applies it at sign-in)"; Tour }
  WriteSummary
}

$bad = @($Results | Where-Object { $_.functional -eq 'FAIL' }).Count
Write-Host ("{0} journeys: {1} PASS, {2} FAIL, {3} NOT TESTED" -f $Results.Count, @($Results | Where-Object { $_.functional -eq 'PASS' }).Count, $bad, @($Results | Where-Object { $_.functional -eq 'NOT TESTED' }).Count)
exit $(if ($bad -gt 0) { 1 } else { 0 })
