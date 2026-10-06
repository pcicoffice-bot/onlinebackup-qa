<#
  Recovery media for bare-metal restore (Windows PowerShell 2.0+, run as Administrator).
  Makes a bootable USB drive with Windows PE (needs the free Windows ADK + WinPE add-on), from which
  "System Image Recovery" restores an image prepared by:  OnlineBackup.Agent restore-image --target <drive>
  Without the ADK: boot from the installation media of the same Windows version -> Repair your computer.
  Usage: .\make-recovery-media.ps1 -Usb F:   (ALL DATA ON THE DRIVE IS ERASED)
#>
param([Parameter(Mandatory = $true)][string]$Usb, [string]$Arch = 'amd64', [string]$Work = "$env:TEMP\OBWinPE")
$ErrorActionPreference = 'Stop'
$Usb = $Usb.TrimEnd('\').TrimEnd(':') + ':'
if ($Usb -eq $env:SystemDrive) { throw "Refusing to erase the system drive $Usb." }
$kits = @("${env:ProgramFiles(x86)}\Windows Kits\10\Assessment and Deployment Kit", "$env:ProgramFiles\Windows Kits\10\Assessment and Deployment Kit") | Where-Object { Test-Path "$_\Windows Preinstallation Environment" } | Select-Object -First 1
if (-not $kits) {
    Write-Host 'Windows ADK + WinPE add-on not found. Install them from Microsoft, or use the Windows installation media:'
    Write-Host '  boot from it -> Repair your computer -> Troubleshoot -> System Image Recovery.'
    exit 2
}
$pe = "$kits\Windows Preinstallation Environment"
$env:WinPERoot = $pe
$env:OSCDImgRoot = "$kits\Deployment Tools\$Arch\Oscdimg"
if (Test-Path $Work) { Remove-Item $Work -Recurse -Force }
& cmd /c "`"$pe\copype.cmd`" $Arch `"$Work`""
if ($LASTEXITCODE -ne 0) { throw "copype failed ($LASTEXITCODE)." }
& cmd /c "`"$pe\MakeWinPEMedia.cmd`" /UFD /F `"$Work`" $Usb"
if ($LASTEXITCODE -ne 0) { throw "MakeWinPEMedia failed ($LASTEXITCODE)." }
Set-Content -Path "$Usb\RESTORE-README.txt" -Encoding UTF8 -Value @"
Bare-metal restore:
1. Prepare the image on a second drive or a share:  OnlineBackup.Agent restore-image --set <ID> --password **** --target E:\
2. Boot the computer from this USB drive.
3. Run:  X:\Windows\System32\recenv.exe  (or boot Windows installation media -> Repair your computer)
4. Troubleshoot -> System Image Recovery -> choose the image in WindowsImageBackup.
"@
Write-Host "Recovery media ready on $Usb"
