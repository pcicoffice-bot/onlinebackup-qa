# OnlineBackup QA - the driver inside the Windows test VM, started at every sign-in. Each sign-in is one phase:
#   (first)  UAC is off only after a restart -> restart
#   start    the journeys before the restart (W01 server, W03 installer, W04 first run, W05 full chain, W18), then a
#            REAL restart with the screen scale set to 125%
#   r1       after the restart: W19 (services, agent, no ghost, schedule, backup, restore) and the visual tour at 125%;
#            then the scale 150% and a second real restart
#   r2       the same at 150%; then done
# The evidence goes to the shared folder after every phase (the host keeps it even if Windows never comes back).
# Every phase ends - with its journeys, a time limit, or an abort - in a restart or in done.txt: the Linux job waits for
# done.txt (up to 5 hours), so a phase that hangs, or a sign-in that finds no shared folder, must never mean silence.
$ErrorActionPreference = 'Continue'
$vm = 'C:\obqa-vm'; New-Item -ItemType Directory -Force -Path $vm | Out-Null
$share = $null
function Status([string]$t) { $l = "$((Get-Date).ToString('o')) $t"; Add-Content "$vm\status.log" $l; Write-Host $l; if ($share) { try { Copy-Item "$vm\status.log" (Join-Path $share 'status.log') -Force } catch { } } }
function Keep([string]$name) { if ($share) { try { Copy-Item "$vm\out\$name" (Join-Path $share "out\$name") -Recurse -Force; Status "kept $name" } catch { Status "could not keep $name : $($_.Exception.Message)" } } }
function Scale([int]$percent) { Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name LogPixels -Value ([int](96 * $percent / 100)) -Type DWord; Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name Win8DpiScaling -Value 1 -Type DWord }
function Restart([string]$next) {
  Set-Content "$vm\phase.txt" $next
  $sf = 'C:\obqa\state.json'; if (Test-Path $sf) { $s = Get-Content $sf -Raw | ConvertFrom-Json; $s | Add-Member -NotePropertyName rebootAt -NotePropertyValue (Get-Date).ToString('o') -Force; $s | ConvertTo-Json | Set-Content $sf -Encoding UTF8 }
  Status "restarting Windows (next phase: $next)"; Start-Sleep 5; Restart-Computer -Force; Start-Sleep 60
  # still here: the restart was refused - try once more, else end the run (never a silent wait of 5 hours)
  & shutdown.exe /r /f /t 0; Start-Sleep 120; Status 'Windows did not restart'; Set-Content "$vm\phase.txt" 'done'; if ($share) { Set-Content (Join-Path $share 'done.txt') ((Get-Date).ToString('o') + ' (aborted: Windows did not restart)') }; exit 1
}
$phase = $(if (Test-Path "$vm\phase.txt") { (Get-Content "$vm\phase.txt" | Select-Object -First 1).Trim() } else { '' })
Start-Sleep -Seconds 45   # the desktop settles after sign-in
# the shared folder: looked for AFTER the network is up, for up to 5 minutes (looked for once, before the wait, a slow
# network at sign-in meant no evidence kept and no done.txt - the Linux job then waited its whole 5 hours)
$until = (Get-Date).AddMinutes(5)
do { $share = @('Z:\', '\\host.lan\Data') | Where-Object { Test-Path $_ } | Select-Object -First 1; if (-not $share) { Start-Sleep -Seconds 10 } } while (-not $share -and (Get-Date) -lt $until)
if ($share) { New-Item -ItemType Directory -Force -Path (Join-Path $share 'out') | Out-Null }
Status "signed in; phase '$phase'; share '$share'; $((Get-CimInstance Win32_OperatingSystem).Caption)"
$z = Get-ChildItem C:\OEM\pkg\OnlineBackup-Server-*.zip | Sort-Object { [version]($_.BaseName -replace '^OnlineBackup-Server-', '') }
$e2e = 'C:\OEM\qa\win-e2e.ps1'
function Done([string]$why) { Set-Content "$vm\phase.txt" 'done'; Status "done$why"; if ($share) { Set-Content (Join-Path $share 'done.txt') ("{0}{1}" -f (Get-Date).ToString('o'), $why) } }
# One phase's journeys in their own process with a time limit: a hang (a window that never answers) ends in a kill,
# the evidence so far is kept and the run goes on, instead of the task's 5-hour limit killing this driver too.
function RunPhase([string]$name, [int]$minutes, [string]$log, [string]$e2eArgs) {
  Set-Content "$vm\running.txt" $name
  $p = Start-Process powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$e2e`" $e2eArgs" -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err"
  $null = $p.Handle   # Windows PowerShell 5.1 gives the exit code only to a process whose handle was opened
  if (-not $p.WaitForExit($minutes * 60000)) { Status "phase $name did not end in $minutes minutes: stopped (NOT a pass)"; & taskkill.exe /T /F /PID $p.Id | Out-Null; Start-Sleep 5 }
  else { Status "phase $name ended, exit code $($p.ExitCode)" }
  Remove-Item "$vm\running.txt" -Force -ErrorAction SilentlyContinue
}
# a phase that was running when Windows restarted by itself (a crash, an update): its sign-in must not run it again on a
# half-used machine (a second install, the evidence of two attempts mixed in one folder) - the run ends here, said so
if (Test-Path "$vm\running.txt") {
  $was = (Get-Content "$vm\running.txt" | Select-Object -First 1)
  Status "phase '$was' was interrupted by an unexpected restart: the run ends (NOT a continuous chain)"
  $n = @{ 'start' = 'vm-1-before-restart'; 'r1' = 'vm-2-after-restart-125'; 'r2' = 'vm-3-after-restart-150' }[$was]
  if ($n -and (Test-Path "$vm\out\$n")) { Copy-Item "$vm\phase*.log*" "$vm\out\$n\" -Force -ErrorAction SilentlyContinue; Keep $n }
  Remove-Item "$vm\running.txt" -Force -ErrorAction SilentlyContinue; Done " (aborted: phase '$was' interrupted by an unexpected restart)"; exit 0
}
switch ($phase) {
  '' { Restart 'start' }
  'start' {
    Status 'phase 1: the journeys before the restart'
    RunPhase 'start' 90 "$vm\phase1.log" ("-Package `"{0}`" -Package2 `"{1}`" -Out `"$vm\out\vm-1-before-restart`" -Phase before-reboot -Only `"^W0[1345]$|^W18$`"" -f $z[0].FullName, $z[-1].FullName)
    New-Item -ItemType Directory -Force -Path "$vm\out\vm-1-before-restart" | Out-Null
    Copy-Item "$vm\phase1.log*" "$vm\out\vm-1-before-restart\" -Force; Keep 'vm-1-before-restart'
    Scale 125; Restart 'r1'
  }
  'r1' {
    Status 'phase 2: after the first real restart (scale 125%)'
    RunPhase 'r1' 60 "$vm\phase2.log" ("-Package `"{0}`" -Out `"$vm\out\vm-2-after-restart-125`" -Phase after-reboot" -f $z[0].FullName)
    New-Item -ItemType Directory -Force -Path "$vm\out\vm-2-after-restart-125" | Out-Null
    Copy-Item "$vm\phase2.log*" "$vm\out\vm-2-after-restart-125\" -Force; Keep 'vm-2-after-restart-125'
    Scale 150; Restart 'r2'
  }
  'r2' {
    Status 'phase 3: after the second real restart (scale 150%)'
    RunPhase 'r2' 60 "$vm\phase3.log" ("-Package `"{0}`" -Out `"$vm\out\vm-3-after-restart-150`" -Phase after-reboot -Final" -f $z[0].FullName)
    New-Item -ItemType Directory -Force -Path "$vm\out\vm-3-after-restart-150" | Out-Null
    Copy-Item "$vm\phase3.log*" "$vm\out\vm-3-after-restart-150\" -Force; Keep 'vm-3-after-restart-150'
    Scale 100; Done ''
  }
  default { Status "nothing to do in phase '$phase'" }
}
