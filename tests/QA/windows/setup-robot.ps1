# OnlineBackup QA — Windows installer robot (Windows UI Automation, built into Windows; no extra tools).
# Uses the client's Setup.exe like a person: reads each page, clicks, checks a box, and proves the outcome from outside
# the product: the Windows service manager, the installed-programs list, the files on disk.
#   powershell -ExecutionPolicy Bypass -File setup-robot.ps1 -Setup C:\path\Setup.exe -Out C:\qa\setup [-Remove]
# Writes <Out>\setup-robot.json (every step: expected, actual, PASS/FAIL, screenshot) and one PNG per step. Exit 1 on any FAIL.
param([Parameter(Mandatory = $true)][string]$Setup, [Parameter(Mandatory = $true)][string]$Out, [switch]$Remove)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]; $PC = [System.Windows.Automation.PropertyCondition]
$steps = New-Object System.Collections.ArrayList
$fail = 0; $n = 0

function Shot([string]$name) {
  $script:n++; $file = Join-Path $Out ('{0:D2}-{1}.png' -f $script:n, ($name -replace '[^A-Za-z0-9-]', '-'))
  try {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds; $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size); $bmp.Save($file); $g.Dispose(); $bmp.Dispose()
  } catch { $file = "(no screenshot: $($_.Exception.Message))" }
  return $file
}
function Step([string]$what, [string]$expected, [scriptblock]$check) {
  $actual = ''; $ok = $false
  try { $r = & $check; $ok = [bool]$r[0]; $actual = [string]$r[1] } catch { $actual = 'ERROR: ' + $_.Exception.Message }
  $shot = Shot $what
  [void]$steps.Add([ordered]@{ step = $what; expected = $expected; actual = $actual; result = $(if ($ok) { 'PASS' } else { 'FAIL' }); screenshot = $shot })
  Write-Host ("[{0}] {1} — {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $what, $actual)
  if (-not $ok) { $script:fail++ }
  return $ok
}
function Window([int]$procId, [int]$seconds = 60) {
  $until = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $until) {
    $w = $AE::RootElement.FindFirst($TS::Children, (New-Object $PC($AE::ProcessIdProperty, $procId)))
    if ($w) { return $w }
    Start-Sleep -Milliseconds 300
  }
  throw "the setup window did not open in $seconds s"
}
function Texts($w) { ($w.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join ' | ' }
function Button($w, [string]$name) {
  $b = $w.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition((New-Object $PC($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)), (New-Object $PC($AE::NameProperty, $name)))))
  if (-not $b) { throw "no button '$name' (on screen: $(Texts $w))" }
  return $b
}
function Click($el) { ($el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); Start-Sleep -Milliseconds 700 }
function WaitText($w, [string]$text, [int]$seconds = 300) {
  $until = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $until) { if ((Texts $w) -like "*$text*") { return $true }; Start-Sleep -Milliseconds 500 }
  return $false
}

$p = Start-Process -FilePath $Setup -PassThru
# Setup.exe unpacks itself and starts the wizard (the same process, or a child for elevation): find the window that appears
Start-Sleep -Seconds 3
$w = $null; $until = (Get-Date).AddSeconds(90)
while (-not $w -and (Get-Date) -lt $until) {
  foreach ($cand in @($p.Id) + @(Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $p.Id } | ForEach-Object { $_.ProcessId })) {
    $w = $AE::RootElement.FindFirst($TS::Children, (New-Object $PC($AE::ProcessIdProperty, [int]$cand))); if ($w) { break }
  }
  if (-not $w) { Start-Sleep -Milliseconds 500 }
}
if (-not $w) { Step 'Setup window opens' 'a window' { @($false, 'no window in 90 s') } | Out-Null; $steps | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Out 'setup-robot.json') -Encoding UTF8; exit 1 }

if (-not $Remove) {
  Step 'Welcome page' 'a Welcome page with Next and Cancel' { $t = Texts $w; @(($t -like '*Welcome*') -and ($t -like '*Next*') -and ($t -like '*Cancel*'), $t) } | Out-Null
  Click (Button $w 'Next')
  Step 'License agreement page' 'the license text and an "I accept" box' { $t = Texts $w; @(($t -like '*License agreement*') -and ($t -like '*I accept*'), $t) } | Out-Null
  Step 'Install is disabled before accepting' 'Install not enabled' { $b = Button $w 'Install'; @(-not $b.Current.IsEnabled, 'enabled=' + $b.Current.IsEnabled) } | Out-Null
  Step 'Back returns to Welcome' 'the Welcome page' { Click (Button $w 'Back'); $t = Texts $w; $ok = $t -like '*Welcome*'; Click (Button $w 'Next'); @($ok, $t) } | Out-Null
  $box = $w.FindFirst($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)))
  ($box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle(); Start-Sleep -Milliseconds 500
  Step 'Accept the license' 'Install enabled' { $b = Button $w 'Install'; @($b.Current.IsEnabled, 'enabled=' + $b.Current.IsEnabled) } | Out-Null
  Click (Button $w 'Install')
  Step 'Installing with progress' 'an "Installing" page with a progress bar' { $t = Texts $w; @(($t -like '*Installing*') -or ($t -like '*is installed*'), $t) } | Out-Null
  Step 'Finish page only after the installation' '"is installed" and Finish' { $ok = WaitText $w 'is installed' 300; $t = Texts $w; @($ok -and ($t -like '*Finish*'), $t) } | Out-Null
  # untick "Open now" so the robot owns the next steps, then Finish
  $open = $w.FindFirst($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)))
  if ($open -and ($open.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Current.ToggleState -eq 'On') { ($open.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle() }
  Click (Button $w 'Finish')
  # ORACLE — Windows itself, not the product
  Step 'Service exists and runs (Windows service manager)' 'OnlineBackupAgent Running, start automatic' { $s = Get-Service OnlineBackupAgent -ErrorAction SilentlyContinue; @(($s -ne $null) -and ($s.Status -eq 'Running') -and ($s.StartType -eq 'Automatic'), $(if ($s) { "$($s.Status) $($s.StartType)" } else { 'no service' })) } | Out-Null
  Step 'Listed in Programs and Features' 'an uninstall entry' { $k = Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue | Get-ItemProperty | Where-Object { $_.UninstallString -like '*OnlineBackup*' }; @([bool]$k, ($k | ForEach-Object { $_.DisplayName + ' ' + $_.DisplayVersion }) -join '; ') } | Out-Null
  Step 'Program files on disk' 'OnlineBackup.Agent.exe and OnlineBackup.Client.exe' { $svc = Get-CimInstance Win32_Service -Filter "Name='OnlineBackupAgent'"; $exe = ($svc.PathName -replace '^"([^"]+)".*$', '$1'); $dir = Split-Path $exe; @((Test-Path (Join-Path $dir 'OnlineBackup.Agent.exe')) -and (Test-Path (Join-Path $dir 'OnlineBackup.Client.exe')), $dir) } | Out-Null
} else {
  Step 'Setup sees the installation' '"already installed" with Repair/Update and Remove' { $t = Texts $w; @(($t -like '*already installed*') -and ($t -like '*Remove*'), $t) } | Out-Null
  $radios = $w.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::RadioButton)))
  $rm = $radios | Where-Object { $_.Current.Name -like 'Remove*' } | Select-Object -First 1
  ($rm.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select(); Start-Sleep -Milliseconds 500
  Click (Button $w 'Remove')
  # the confirmation message box (Yes)
  Start-Sleep -Seconds 1
  $dlg = $AE::RootElement.FindFirst($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)))
  $yes = $AE::RootElement.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition((New-Object $PC($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)), (New-Object $PC($AE::NameProperty, 'Yes')))))
  Step 'Removal asks for confirmation' 'a Yes / No question' { @([bool]$yes, $(if ($yes) { 'Yes button found' } else { 'no confirmation' })) } | Out-Null
  if ($yes) { Click $yes }
  Step 'Removal finishes' '"was removed"' { $ok = WaitText $w 'was removed' 300; @($ok, (Texts $w)) } | Out-Null
  Click (Button $w 'Finish')
  Start-Sleep -Seconds 3
  Step 'Service is gone (Windows service manager)' 'no OnlineBackupAgent' { $s = Get-Service OnlineBackupAgent -ErrorAction SilentlyContinue; @($s -eq $null, $(if ($s) { "still there: $($s.Status)" } else { 'gone' })) } | Out-Null
  Step 'Not listed in Programs and Features' 'no uninstall entry' { $k = Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue | Get-ItemProperty | Where-Object { $_.UninstallString -like '*OnlineBackup*' }; @(-not $k, ($k | ForEach-Object { $_.DisplayName }) -join '; ') } | Out-Null
}
$steps | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Out 'setup-robot.json') -Encoding UTF8
Write-Host ("{0} steps, {1} failed" -f $steps.Count, $fail)
exit $(if ($fail -gt 0) { 1 } else { 0 })
