<#
  Installs the backup server on the IT company's Windows server (Windows Server 2012 R2 or later, run as Administrator).
  Everything the customers will see carries the company's own name: set it afterwards in the management site
  (https://<server>:<port>/admin → "החברה והמוצר") and download the client software from there.

  First install (creates a self-signed certificate when none is given):
    .\install-server.ps1 -HostName backup.company.co.il -Port 8443 -SystemHome D:\Backup\system -UserHome E:\Backup\users -Admin admin -AdminPassword '<strong password>'
  With an existing certificate (LocalMachine\My): add -CertThumbprint <thumbprint>
  Update (same parameters, no -AdminPassword): stops the service, replaces the files, starts it again.
#>
param(
 [Parameter(Mandatory=$true)][string]$HostName,
 [int]$Port = 8443,
 [string]$SystemHome = 'C:\OnlineBackup\system',
 [string[]]$UserHome = @('C:\OnlineBackup\users'),
 [string]$Admin = 'admin',
 [string]$AdminPassword,
 [string]$CertThumbprint,
 [string]$InstallDir = (Join-Path $env:ProgramFiles 'OnlineBackup Server')
)
$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run PowerShell as Administrator.' }

# 1. files (the service is stopped first when it exists)
$svc = Get-Service OnlineBackupServer -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') { Stop-Service OnlineBackupServer; Start-Sleep 2 }
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'server\*') $InstallDir -Recurse -Force
$exe = Join-Path $InstallDir 'OnlineBackup.Server.exe'
Write-Host "Files in $InstallDir"

# 2. first-time configuration
if (-not (Test-Path (Join-Path $SystemHome 'conf\system.xml'))) {
  if (-not $AdminPassword) { throw 'First install: -AdminPassword is required.' }
  $homes = @(); foreach ($h in $UserHome) { $homes += '--user-home'; $homes += $h }
  & $exe init --system-home $SystemHome --admin $Admin --password $AdminPassword --host "$($HostName):$Port" @homes
  if ($LASTEXITCODE -ne 0) { throw 'init failed' }
}

# 3. certificate: the given one, or a self-signed one for the host name (the agents pin it)
if (-not $CertThumbprint) {
  $existing = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.FriendlyName -eq 'OnlineBackup Server' -and $_.NotAfter -gt (Get-Date).AddDays(30) } | Select-Object -First 1
  if ($existing) { $CertThumbprint = $existing.Thumbprint }
  else {
    $c = New-SelfSignedCertificate -DnsName $HostName -CertStoreLocation Cert:\LocalMachine\My -FriendlyName 'OnlineBackup Server' -NotAfter (Get-Date).AddYears(10) -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256
    $CertThumbprint = $c.Thumbprint
    Write-Host "Self-signed certificate created for $HostName"
  }
}
$cert = Get-Item "Cert:\LocalMachine\My\$CertThumbprint"
$sha = [BitConverter]::ToString((New-Object Security.Cryptography.SHA256Managed).ComputeHash($cert.RawData)).Replace('-', '').ToLowerInvariant()

# 4. HTTPS on the chosen port only (no IIS, no other site touched), firewall rule, service
$prefix = "https://+:$Port/"
netsh http delete sslcert ipport=0.0.0.0:$Port 2>$null | Out-Null
netsh http add sslcert ipport=0.0.0.0:$Port certhash=$CertThumbprint appid='{6f1f6a9e-6a3b-4b0e-9a65-6b2f0f3b1a10}' | Out-Null
netsh http add urlacl url=$prefix user="NT AUTHORITY\SYSTEM" 2>$null | Out-Null
if (-not (Get-NetFirewallRule -DisplayName "OnlineBackup Server $Port" -ErrorAction SilentlyContinue)) {
  New-NetFirewallRule -DisplayName "OnlineBackup Server $Port" -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow | Out-Null
}
& $exe config --system-home $SystemHome --public-url "https://$($HostName):$Port" --cert-pin $sha | Out-Null
if (-not $svc) { & $exe install-service --system-home $SystemHome --prefix $prefix } else { Start-Service OnlineBackupServer }

Write-Host ''
Write-Host "Installed. Management site: https://$($HostName):$Port/admin" -ForegroundColor Green
Write-Host "Certificate pin (already set for the client software): $sha"
Write-Host 'Next: sign in, open "החברה והמוצר", fill in your company and product, then "צור תוכנת לקוח".'
