# OnlineBackup QA - the driver inside the Windows test VM, started at every sign-in. Each sign-in is one phase:
#   (first)  UAC is off only after a restart -> restart
#   start    the journeys before the restart (W01 server, W03 installer, W04 first run, W05 full chain, W18), then a
#            REAL restart with the screen scale set to 125%
#   r1       after the restart: W19 (services, agent, no ghost, schedule, backup, restore) and the visual tour at 125%;
#            then the scale 150% and a second real restart
#   r2       the same at 150%; then done
# The evidence goes to the shared folder after every phase (the host keeps it even if Windows never comes back).
$ErrorActionPreference = 'Continue'
$vm = 'C:\obqa-vm'; New-Item -ItemType Directory -Force -Path $vm | Out-Null
$share = @('Z:\', '\\host.lan\Data') | Where-Object { Test-Path $_ } | Select-Object -First 1
function Status([string]$t) { $l = "$((Get-Date).ToString('o')) $t"; Add-Content "$vm\status.log" $l; Write-Host $l; if ($share) { try { Copy-Item "$vm\status.log" (Join-Path $share 'status.log') -Force } catch { } } }
function Keep([string]$name) { if ($share) { try { Copy-Item "$vm\out\$name" (Join-Path $share "out\$name") -Recurse -Force; Status "kept $name" } catch { Status "could not keep $name : $($_.Exception.Message)" } } }
function Scale([int]$percent) { Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name LogPixels -Value ([int](96 * $percent / 100)) -Type DWord; Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name Win8DpiScaling -Value 1 -Type DWord }
function Restart([string]$next) {
  Set-Content "$vm\phase.txt" $next
  $sf = 'C:\obqa\state.json'; if (Test-Path $sf) { $s = Get-Content $sf -Raw | ConvertFrom-Json; $s | Add-Member -NotePropertyName rebootAt -NotePropertyValue (Get-Date).ToString('o') -Force; $s | ConvertTo-Json | Set-Content $sf -Encoding UTF8 }
  Status "restarting Windows (next phase: $next)"; Start-Sleep 5; Restart-Computer -Force; exit 0
}
if ($share) { New-Item -ItemType Directory -Force -Path (Join-Path $share 'out') | Out-Null }
$phase = $(if (Test-Path "$vm\phase.txt") { (Get-Content "$vm\phase.txt" | Select-Object -First 1).Trim() } else { '' })
Status "signed in; phase '$phase'; share '$share'; $((Get-CimInstance Win32_OperatingSystem).Caption)"
$z = Get-ChildItem C:\OEM\pkg\OnlineBackup-Server-*.zip | Sort-Object { [version]($_.BaseName -replace '^OnlineBackup-Server-', '') }
$e2e = 'C:\OEM\qa\win-e2e.ps1'
Start-Sleep -Seconds 45   # the desktop settles after sign-in
switch ($phase) {
  '' { Restart 'start' }
  'start' {
    Status 'phase 1: the journeys before the restart'
    & $e2e -Package $z[0].FullName -Package2 $z[-1].FullName -Out "$vm\out\vm-1-before-restart" -Phase before-reboot -Only '^W0[1345]$|^W18$' *> "$vm\phase1.log"
    Copy-Item "$vm\phase1.log" "$vm\out\vm-1-before-restart\" -Force; Keep 'vm-1-before-restart'
    Scale 125; Restart 'r1'
  }
  'r1' {
    Status 'phase 2: after the first real restart (scale 125%)'
    & $e2e -Package $z[0].FullName -Out "$vm\out\vm-2-after-restart-125" -Phase after-reboot *> "$vm\phase2.log"
    Copy-Item "$vm\phase2.log" "$vm\out\vm-2-after-restart-125\" -Force; Keep 'vm-2-after-restart-125'
    Scale 150; Restart 'r2'
  }
  'r2' {
    Status 'phase 3: after the second real restart (scale 150%)'
    & $e2e -Package $z[0].FullName -Out "$vm\out\vm-3-after-restart-150" -Phase after-reboot *> "$vm\phase3.log"
    Copy-Item "$vm\phase3.log" "$vm\out\vm-3-after-restart-150\" -Force; Keep 'vm-3-after-restart-150'
    Scale 100; Set-Content "$vm\phase.txt" 'done'
    Status 'done'; if ($share) { Set-Content (Join-Path $share 'done.txt') (Get-Date).ToString('o') }
  }
  default { Status "nothing to do in phase '$phase'" }
}
