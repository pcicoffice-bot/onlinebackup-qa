# OnlineBackup QA - the QA driver starts at every sign-in of the test user, elevated, on the interactive desktop
$user = "$env:COMPUTERNAME\Docker"
$a = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -ExecutionPolicy Bypass -File C:\OEM\qa\vm\vm-phase.ps1'
$t = New-ScheduledTaskTrigger -AtLogOn -User $user
$p = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$s = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 5) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName 'OnlineBackupQA' -Action $a -Trigger $t -Principal $p -Settings $s -Force
