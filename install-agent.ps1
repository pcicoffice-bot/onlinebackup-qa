# Installs the backup agent on a customer computer (Windows Server 2003 SP2 / XP SP3 and later, .NET Framework 4.0).
# Run as Administrator in the folder of OnlineBackup.Agent.exe:
#   .\install-agent.ps1 -Server https://backup.example.com:8443 -Login customer -Password ****** [-Otp 123456]
param(
 [Parameter(Mandatory=$true)][string]$Server,
 [Parameter(Mandatory=$true)][string]$Login,
 [Parameter(Mandatory=$true)][string]$Password,
 [string]$Otp = '',
 # Windows 2003 / XP: SHA-256 of the server certificate (printed by install-server.ps1). Required when the certificate
 # is not trusted by the old root store of the computer.
 [string]$Pin = '',
 [string]$InstallDir = (Join-Path $env:ProgramFiles 'OnlineBackup'),
 [string]$DataDir = (Join-Path $env:ProgramData 'OnlineBackup')
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $InstallDir, $DataDir | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'OnlineBackup.*') $InstallDir -Force
$exe = Join-Path $InstallDir 'OnlineBackup.Agent.exe'
$args = @('register', '--home', $DataDir, '--server', $Server, '--login', $Login, '--password', $Password, '--computer', $env:COMPUTERNAME)
if ($Otp) { $args += @('--otp', $Otp) }
if ($Pin) { $args += @('--pin', $Pin) }
& $exe @args
if ($LASTEXITCODE -ne 0) { throw 'Registration failed.' }
# Only the service account and administrators may read the agent folder (keys and the device token are DPAPI-protected too).
icacls $DataDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null
& $exe install-service --home $DataDir
Write-Output 'Installed. Add backup sets with: OnlineBackup.Agent.exe addset --home "' + $DataDir + '" --password ... --name Files --source C:\Data'
