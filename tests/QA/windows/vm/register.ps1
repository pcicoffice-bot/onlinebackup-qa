# OnlineBackup QA - the QA driver starts at every sign-in of the test user, elevated, on the interactive desktop
$user = "$env:COMPUTERNAME\Docker"
$a = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -ExecutionPolicy Bypass -File C:\OEM\qa\vm\vm-phase.ps1'
$t = New-ScheduledTaskTrigger -AtLogOn -User $user
$p = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$s = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 5) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName 'OnlineBackupQA' -Action $a -Trigger $t -Principal $p -Settings $s -Force
# Q39: install.bat runs DURING the first sign-in, so an at-sign-in trigger first fires at the NEXT sign-in - which never came
# (the driver's own first phase is the restart). Runs 14-21: Windows sat at the desktop for 5 hours and no status ever came.
# Start it now, and say so where the host can see it.
$r = 'not started'; try { Start-ScheduledTask -TaskName 'OnlineBackupQA' -ErrorAction Stop; $r = 'started now' } catch { $r = 'start failed: ' + $_.Exception.Message }
foreach ($sh in @('Z:\', '\\host.lan\Data')) { if (Test-Path $sh) { Add-Content (Join-Path $sh 'status.log') ("{0} register.ps1: task registered, {1}" -f (Get-Date).ToString('o'), $r); break } }
