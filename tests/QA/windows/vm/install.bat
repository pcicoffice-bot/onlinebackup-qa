@echo off
rem OnlineBackup QA - runs once, at the end of the unattended Windows installation of the test VM.
rem Test environment only: UAC off (the robot cannot click the secure desktop of a UAC prompt; a customer clicks Yes),
rem sign-in without a password prompt after every restart, and the QA driver started at every sign-in.
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v EnableLUA /t REG_DWORD /d 0 /f
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v ConsentPromptBehaviorAdmin /t REG_DWORD /d 0 /f
reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v AutoAdminLogon /t REG_SZ /d 1 /f
reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v DefaultUserName /t REG_SZ /d Docker /f
reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v DefaultPassword /t REG_SZ /d admin /f
reg delete "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v AutoLogonCount /f
powershell -NoProfile -ExecutionPolicy Bypass -File C:\OEM\qa\vm\register.ps1 > C:\OEM\register.log 2>&1
