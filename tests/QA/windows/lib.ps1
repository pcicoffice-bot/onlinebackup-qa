# OnlineBackup QA - shared helpers of the Windows journeys (dot-sourced). Windows PowerShell 5.1 and PowerShell 7.
# ASCII only: Windows PowerShell 5.1 reads a file without a byte-order mark as ANSI (a dash in a string once broke the
# installer robot before it clicked anything). Non-ASCII names are made with [char] codes.
#
# Every PASS here comes from an oracle outside the product: Windows UI Automation (what is on the screen), the Windows
# service manager, the process list, the registry's installed-programs list, files on disk, SHA-256 of every file.

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type -Namespace ObQa -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
'@
[void][ObQa.Win]::SetProcessDPIAware()
$script:AE = [System.Windows.Automation.AutomationElement]
$script:TS = [System.Windows.Automation.TreeScope]
$script:PC = [System.Windows.Automation.PropertyCondition]
$script:CT = [System.Windows.Automation.ControlType]

# ------------------------------------------------------------------ evidence
$script:Journey = $null
function New-Journey([string]$id, [string]$title, [string]$root) {
  $dir = Join-Path $root $id; New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $script:Journey = [ordered]@{ id = $id; title = $title; dir = $dir; started = (Get-Date).ToString('o'); result = 'NOT TESTED'; reason = ''; steps = (New-Object System.Collections.ArrayList); shot = 0 }
  Write-Host ""; Write-Host "==== $id $title"
  return $script:Journey
}
function Shot([string]$name) {
  $j = $script:Journey; if (-not $j) { return '' }
  $j.shot++; $file = Join-Path $j.dir ('{0:D2}-{1}.png' -f $j.shot, ($name -replace '[^A-Za-z0-9-]', '-'))
  try {
    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
    return (Split-Path $file -Leaf)
  } catch { return "(no screenshot: $($_.Exception.Message))" }
}
# One checked step: the check returns @(ok, what was seen). Exceptions are a FAIL with the message, never a PASS.
function Step([string]$what, [string]$expected, [scriptblock]$check, [switch]$NoShot) {
  $t = Get-Date; $ok = $false; $actual = ''
  try { $r = & $check; $ok = [bool]$r[0]; $actual = [string]$r[1] } catch { $actual = 'ERROR: ' + $_.Exception.Message + ' @ line ' + $_.InvocationInfo.ScriptLineNumber }
  $shot = ''; if (-not $NoShot) { $shot = Shot $what }
  $row = [ordered]@{ step = $what; expected = $expected; actual = $actual; result = $(if ($ok) { 'PASS' } else { 'FAIL' }); seconds = [int]((Get-Date) - $t).TotalSeconds; screenshot = $shot; at = (Get-Date).ToString('HH:mm:ss') }
  [void]$script:Journey.steps.Add($row)
  Write-Host ('[{0}] {1} -- {2}' -f $row.result, $what, $actual)
  return $ok
}
function Note([string]$what, [string]$text) {
  [void]$script:Journey.steps.Add([ordered]@{ step = $what; expected = ''; actual = $text; result = 'INFO'; seconds = 0; screenshot = ''; at = (Get-Date).ToString('HH:mm:ss') })
  Write-Host "[INFO] $what -- $text"
}
# The journey's verdict: PASS only when every step ran and passed; a journey stopped early is FAIL with its reason.
function Close-Journey([string]$notTested = '') {
  $j = $script:Journey
  if ($notTested) { $j.result = 'NOT TESTED'; $j.reason = $notTested }
  else {
    $bad = @($j.steps | Where-Object { $_.result -eq 'FAIL' })
    if ($j.steps.Count -eq 0) { $j.result = 'NOT TESTED'; $j.reason = 'no step ran' }
    elseif ($bad.Count -gt 0) { $j.result = 'FAIL'; $j.reason = ($bad | ForEach-Object { $_.step + ': ' + $_.actual }) -join ' || ' }
    else { $j.result = 'PASS' }
  }
  $j.ended = (Get-Date).ToString('o')
  $copy = [ordered]@{}; foreach ($k in $j.Keys) { if ($k -ne 'shot') { $copy[$k] = $j[$k] } }
  $copy | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $j.dir 'journey.json') -Encoding UTF8
  Write-Host ("==== {0}: {1} {2}" -f $j.id, $j.result, $j.reason)
  $script:Journey = $null
  return $copy
}

# ------------------------------------------------------------------ Windows facts (the oracles)
function WaitService([string]$name, [string]$state, [int]$seconds = 90) {
  $until = (Get-Date).AddSeconds($seconds)
  do { $s = Get-Service $name -ErrorAction SilentlyContinue; if ($s -and [string]$s.Status -eq $state) { return [string]$s.Status }; Start-Sleep -Milliseconds 500 } while ((Get-Date) -lt $until)
  if ($s) { return [string]$s.Status } else { return 'no service' }
}
function ServiceFacts([string]$name) {
  $c = Get-CimInstance Win32_Service -Filter "Name='$name'" -ErrorAction SilentlyContinue
  if (-not $c) { return [ordered]@{ exists = $false } }
  $p = $null; if ($c.ProcessId) { $p = Get-Process -Id $c.ProcessId -ErrorAction SilentlyContinue }
  return [ordered]@{ exists = $true; state = $c.State; start = $c.StartMode; account = $c.StartName; path = $c.PathName; pid = $c.ProcessId; process = $(if ($p) { $p.Path } else { '' }) }
}
function UninstallEntries([string]$like) {
  Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
    Get-ItemProperty | Where-Object { $_.UninstallString -like $like }
}
function Shortcuts([string]$targetLike) {
  $sh = New-Object -ComObject WScript.Shell
  $dirs = @([Environment]::GetFolderPath('CommonDesktopDirectory'), [Environment]::GetFolderPath('CommonPrograms'), [Environment]::GetFolderPath('CommonStartup'), [Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))
  foreach ($d in $dirs) { if ($d -and (Test-Path $d)) { Get-ChildItem $d -Filter *.lnk -Recurse -ErrorAction SilentlyContinue | ForEach-Object { $l = $sh.CreateShortcut($_.FullName); if ($l.TargetPath -like $targetLike) { [ordered]@{ file = $_.FullName; target = $l.TargetPath; args = $l.Arguments } } } } }
}
function ShadowCount { @(Get-CimInstance Win32_ShadowCopy -ErrorAction SilentlyContinue).Count }

# ------------------------------------------------------------------ data
function Manifest([string]$root) {
  $m = @{}; if (-not (Test-Path -LiteralPath $root)) { return $m }
  Get-ChildItem -LiteralPath $root -Recurse -File -Force | ForEach-Object { $m[$_.FullName.Substring($root.Length).TrimStart('\')] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
  return $m
}
function Compare-Manifest($want, $got) {
  $d = @(); foreach ($k in $want.Keys) { if (-not $got.ContainsKey($k)) { $d += "MISSING $k" } elseif ($want[$k] -ne $got[$k]) { $d += "DIFFERENT $k" } }
  foreach ($k in $got.Keys) { if (-not $want.ContainsKey($k)) { $d += "UNEXPECTED $k" } }; return ,$d
}
function Save-Manifest($m, [string]$file) { $m.GetEnumerator() | Sort-Object Name | ForEach-Object { '{0}  {1}' -f $_.Value, $_.Name } | Set-Content $file -Encoding UTF8 }
function U([int[]]$codes) { -join ($codes | ForEach-Object { [char]$_ }) }
# The golden dataset: text, Hebrew and Japanese names, spaces, an empty file, 20 MB + 3 MB random, duplicates, 100 small
# files, an Office-like zip, a read-only file, a hidden file, a long path segment.
function Golden([string]$root) {
  $enc = New-Object Text.UTF8Encoding($false)
  $w = { param($rel, $data) $f = Join-Path $root $rel; New-Item -ItemType Directory -Force -Path (Split-Path $f) | Out-Null; if ($data -is [byte[]]) { [IO.File]::WriteAllBytes($f, $data) } else { [IO.File]::WriteAllText($f, $data, $enc) } }
  $he = U @(0x5DE, 0x5E1, 0x5DE, 0x5DB, 0x5D9, 0x5DD)                     # Hebrew "documents"
  $heFile = (U @(0x5DE, 0x5DB, 0x5EA, 0x5D1)) + ' ' + (U @(0x5DC, 0x5DC, 0x5E7, 0x5D5, 0x5D7)) + '.txt'
  $ja = (U @(0x65E5, 0x672C, 0x8A9E)) + '.txt'
  & $w 'Documents\letter.txt' "Dear customer,`nthis is a test letter.`n"
  & $w ($he + '\' + $heFile) ((U @(0x5E9, 0x5DC, 0x5D5, 0x5DD)) + "`n")
  & $w ($he + '\folder with spaces\annual report 2026.csv' ) "year,amount`n2026,100`n"
  & $w ('Unicode\' + $ja) (U @(0x3053, 0x3093, 0x306B, 0x3061, 0x306F))
  & $w 'Empty\zero.bin' ([byte[]]@())
  $rnd = New-Object Random 7; $b = New-Object byte[] (20MB); $rnd.NextBytes($b); & $w 'Binary\large.bin' $b
  $b2 = New-Object byte[] (3MB + 17); $rnd.NextBytes($b2); & $w 'Binary\random.bin' $b2
  & $w 'Duplicates\a.txt' 'same'; & $w 'Duplicates\b.txt' 'same'
  for ($i = 0; $i -lt 100; $i++) { & $w ('Many\file-{0:D3}.csv' -f $i) "row,$i`n" }
  & $w ('Long\' + ('x' * 120) + '\deep.txt') 'deep'
  & $w 'Attrs\readonly.txt' 'read only'; (Get-Item (Join-Path $root 'Attrs\readonly.txt')).IsReadOnly = $true
  & $w 'Attrs\hidden.txt' 'hidden'; (Get-Item (Join-Path $root 'Attrs\hidden.txt') -Force).Attributes = 'Hidden'
  New-Item -ItemType Directory -Force -Path (Join-Path $root 'Empty folder') | Out-Null
}
function Remove-Tree([string]$p) { if (Test-Path -LiteralPath $p) { Get-ChildItem -LiteralPath $p -Recurse -Force -File | ForEach-Object { $_.IsReadOnly = $false }; Remove-Item -LiteralPath $p -Recurse -Force } }

# ------------------------------------------------------------------ the server's admin API (a second, independent view)
$script:Url = 'https://localhost:8443'; $script:Ses = ''
# Windows PowerShell 5.1 has no -SkipCertificateCheck: accept exactly the server's own certificate, nothing else
if (-not ('ObQa.Trust' -as [type])) {
  Add-Type -TypeDefinition @'
namespace ObQa {
  public static class Trust {
    public static string Pin = "";
    public static bool Check(object s, System.Security.Cryptography.X509Certificates.X509Certificate c, System.Security.Cryptography.X509Certificates.X509Chain ch, System.Net.Security.SslPolicyErrors e) {
      if (e == System.Net.Security.SslPolicyErrors.None) return true;
      if (Pin.Length == 0 || c == null) return false;
      using (var h = System.Security.Cryptography.SHA256.Create()) return System.BitConverter.ToString(h.ComputeHash(c.GetRawCertData())).Replace("-", "").ToLowerInvariant() == Pin;
    }
    public static void Use() { System.Net.ServicePointManager.ServerCertificateValidationCallback = Check; System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12; }
  }
}
'@
}
function Msg([hashtable]$d) { '<m>' + (($d.GetEnumerator() | ForEach-Object { '<f n="' + $_.Key + '">' + [Security.SecurityElement]::Escape([string]$_.Value) + '</f>' }) -join '') + '</m>' }
function Http([string]$method, [string]$url, [string]$body = $null, [hashtable]$headers = @{}, [string]$outFile = $null, [int]$timeoutMs = 120000) {
  [ObQa.Trust]::Use()
  $r = [Net.HttpWebRequest]::Create($url); $r.Method = $method; $r.Timeout = $timeoutMs; $r.ReadWriteTimeout = $timeoutMs; $r.Proxy = $null
  foreach ($k in $headers.Keys) { $r.Headers[$k] = $headers[$k] }
  if ($body -ne $null -and $method -ne 'GET') { $bytes = [Text.Encoding]::UTF8.GetBytes($body); $r.ContentType = 'application/xml'; $r.ContentLength = $bytes.Length; $s = $r.GetRequestStream(); $s.Write($bytes, 0, $bytes.Length); $s.Close() }
  try { $w = $r.GetResponse() } catch [Net.WebException] { $w = $_.Exception.InnerException.Response; if (-not $w) { $w = $_.Exception.Response }; if (-not $w) { throw } }
  $code = [int]$w.StatusCode; $st = $w.GetResponseStream()
  if ($outFile) { $f = [IO.File]::Create($outFile); $st.CopyTo($f); $f.Close(); $text = '' } else { $text = (New-Object IO.StreamReader($st, [Text.Encoding]::UTF8)).ReadToEnd() }
  $w.Close(); return @($code, $text)
}
function Api([string]$method, [string]$path, [hashtable]$body = @{}) {
  $h = @{}; if ($script:Ses) { $h['X-Session'] = $script:Ses }
  $r = Http $method "$script:Url/api/admin/$path" $(if ($method -eq 'GET') { $null } else { Msg $body }) $h
  if ($r[0] -ge 400) { throw "$method $path -> $($r[0]) $($r[1])" }
  return [string]$r[1]
}
function F([string]$xml, [string]$n) { $x = [xml]$xml; $e = $x.m.f | Where-Object { $_.n -eq $n } | Select-Object -First 1; if ($e) { return $e.'#text' } return '' }
# the items of a list in a reply, as hashtables
function Items([string]$xml, [string]$list) {
  $x = [xml]$xml; $out = @()
  foreach ($l in @($x.m.l | Where-Object { $_.n -eq $list })) { foreach ($i in @($l.i)) { if ($i) { $h = @{}; foreach ($f in @($i.f)) { if ($f) { $h[$f.n] = $f.'#text' } }; $out += $h } } }
  return ,$out
}
function Totp([string]$s) {
  $A = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'; $bits = ($s.ToCharArray() | ForEach-Object { [Convert]::ToString($A.IndexOf($_), 2).PadLeft(5, '0') }) -join ''
  $key = [byte[]]@(for ($i = 0; $i + 8 -le $bits.Length; $i += 8) { [Convert]::ToByte($bits.Substring($i, 8), 2) })
  $c = [BitConverter]::GetBytes([long][Math]::Floor(([DateTime]::UtcNow - [DateTime]'1970-01-01').TotalSeconds / 30)); [Array]::Reverse($c)
  $h = (New-Object Security.Cryptography.HMACSHA1(, $key)).ComputeHash($c); $o = $h[19] -band 15
  return (((($h[$o] -band 0x7f) -shl 24) -bor ($h[$o + 1] -shl 16) -bor ($h[$o + 2] -shl 8) -bor $h[$o + 3]) % 1000000).ToString('000000')
}
# runs of one customer from the server's history (kind Backup / Restore), newest last
function Runs([string]$login, [string]$kind) { @(Items (Api 'GET' 'tasks?hours=48') 'tasks' | Where-Object { $_['login'] -eq $login -and $_['kind'] -eq $kind } | Sort-Object { $_['time'] }) }
function WaitNewRun([string]$login, [string]$kind, [int]$after, [int]$minutes = 10) {
  $until = (Get-Date).AddMinutes($minutes)
  while ((Get-Date) -lt $until) { $r = Runs $login $kind; if ($r.Count -gt $after) { return $r[-1] }; Start-Sleep -Seconds 4 }
  return $null
}
function LiveRuns([string]$set) { @(Items (Api 'GET' 'live') 'live' | Where-Object { $_['set'] -eq $set }) }

# ------------------------------------------------------------------ Windows UI Automation: what a person sees and does
function TopWindows([string]$processName) {
  $ids = @(Get-Process -Name $processName -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
  if ($ids.Count -eq 0) { return @() }
  @($AE::RootElement.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $ids -contains $_.Current.ProcessId })
}
function WaitWindow([string]$processName, [scriptblock]$match = { $true }, [int]$seconds = 60) {
  $until = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $until) { foreach ($w in (TopWindows $processName)) { if (& $match $w) { return $w } }; Start-Sleep -Milliseconds 400 }
  return $null
}
function Texts($w) { try { (@($w.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join ' | ' } catch { '' } }
function Find($w, $type, [string]$name = $null, [switch]$Like) {
  $all = @($w.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $type))))
  if ($name) { if ($Like) { $all = @($all | Where-Object { $_.Current.Name -like $name }) } else { $all = @($all | Where-Object { $_.Current.Name -eq $name }) } }
  return ,$all
}
function Button($w, [string]$name, [switch]$Like) {
  $b = @(Find $w $CT::Button $name -Like:$Like) | Where-Object { -not $_.Current.IsOffscreen } | Select-Object -First 1
  if (-not $b) { $b = @(Find $w $CT::Button $name -Like:$Like) | Select-Object -First 1 }
  if (-not $b) { throw "no button '$name' (on screen: $(Texts $w))" }
  return $b
}
function Front($w) { try { $h = [IntPtr]$w.Current.NativeWindowHandle; [void][ObQa.Win]::ShowWindow($h, 9); [void][ObQa.Win]::SetForegroundWindow($h) } catch { } }
# A click as the button receives it from the mouse (BM_CLICK, posted: a click that opens a modal window never blocks the robot)
function Click($el) {
  $h = [IntPtr]$el.Current.NativeWindowHandle
  if ($h -ne [IntPtr]::Zero) { [void][ObQa.Win]::PostMessage($h, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) }
  else { ($el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
  Start-Sleep -Milliseconds 800
}
function IsOn($el) { try { return [string]($el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Current.ToggleState -eq 'On' } catch { return $false } }
function SetCheck($el, [bool]$on) { if ((IsOn $el) -ne $on) { Click $el }; Start-Sleep -Milliseconds 300; return (IsOn $el) -eq $on }
function Edit($w, [string]$name) {
  $e = @(Find $w $CT::Edit $name) | Select-Object -First 1
  if (-not $e) { throw "no field '$name' (fields: $((@(Find $w $CT::Edit) | ForEach-Object { $_.Current.Name }) -join ', '))" }
  return $e
}
# types into a field: the value pattern (WM_SETTEXT, which the program sees as typing); a password field refuses to be
# read back, so it is typed with the keyboard instead
function TypeInto($el, [string]$text) {
  $done = $false
  try { ($el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($text); $done = $true } catch { }
  if (-not $done -or $el.Current.IsPassword) {
    $el.SetFocus(); Start-Sleep -Milliseconds 200
    [System.Windows.Forms.SendKeys]::SendWait('^a{DEL}'); [System.Windows.Forms.SendKeys]::SendWait(($text -replace '([+^%~(){}\[\]])', '{$1}'))
  }
  Start-Sleep -Milliseconds 300
}
# message boxes and dialogs of a process (Windows' dialog class #32770, or a form owned by the main window)
function Dialogs([string]$processName, $main) {
  @(TopWindows $processName | Where-Object { $_.Current.ClassName -eq '#32770' -or ($main -and $_.Current.NativeWindowHandle -ne $main.Current.NativeWindowHandle) })
}
function DialogText($d) { (@(Find $d $CT::Text) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join ' ' }

# ------------------------------------------------------------------ Visual QA: what the customer sees
# Every screen the robot reaches is kept three ways: the real screenshot, a crop of the window, and the window's control
# tree (type, name, position, size, enabled). Automatic checks on that tree and on the pixels find what is not an
# exception: a blank window, a control outside the window, controls on top of each other, a cut label, a window larger
# than the screen, a dialog behind its window, English on a Hebrew screen. They are FINDINGS for a person to judge with
# the screenshot - never a reason to change the product's flow without the owner.
Add-Type -Namespace ObQa -Name Screen -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)] public struct DEVMODE {
  [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName; public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
  public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput; public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
  [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName; public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
  public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight; }
[DllImport("user32.dll", CharSet = CharSet.Ansi)] public static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
[DllImport("user32.dll", CharSet = CharSet.Ansi)] public static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);
public static string Set(int w, int h) {
  var dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
  if (!EnumDisplaySettings(null, -1, ref dm)) return "cannot read the display mode";
  dm.dmPelsWidth = w; dm.dmPelsHeight = h; dm.dmFields = 0x80000 | 0x100000;
  int r = ChangeDisplaySettings(ref dm, 0); return r == 0 ? "" : "ChangeDisplaySettings " + r; }
'@
$script:Visual = New-Object System.Collections.ArrayList
$script:ScreenNo = 0
function Resolution { $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds; '{0}x{1}' -f $b.Width, $b.Height }
function Dpi { $g = [System.Drawing.Graphics]::FromHwnd([IntPtr]::Zero); $d = [int]$g.DpiX; $g.Dispose(); return [int]($d * 100 / 96) }
function Set-Resolution([int]$w, [int]$h) { $e = [ObQa.Screen]::Set($w, $h); Start-Sleep -Seconds 2; if ($e) { return $e }; if ((Resolution) -ne "${w}x${h}") { return "asked ${w}x${h}, the screen is $(Resolution)" }; return '' }

function Rect($el) { $r = $el.Current.BoundingRectangle; return @{ x = [int]$r.X; y = [int]$r.Y; w = [int]$r.Width; h = [int]$r.Height } }
function Inside($a, $b, [int]$slack = 3) { ($a.x -ge $b.x - $slack) -and ($a.y -ge $b.y - $slack) -and ($a.x + $a.w -le $b.x + $b.w + $slack) -and ($a.y + $a.h -le $b.y + $b.h + $slack) }
function OverlapArea($a, $b) { $w = [Math]::Min($a.x + $a.w, $b.x + $b.w) - [Math]::Max($a.x, $b.x); $h = [Math]::Min($a.y + $a.h, $b.y + $b.h) - [Math]::Max($a.y, $b.y); if ($w -le 0 -or $h -le 0) { return 0 }; return $w * $h }
function Finding([string]$screen, [string]$shot, [string]$problem, [string]$expected, [string]$actual, [string]$severity, [string]$recommend, [string]$kind = 'VISUAL') {
  $f = [ordered]@{ journey = $(if ($script:Journey) { $script:Journey.id } else { '' }); screen = $screen; screenshot = $shot; kind = $kind; problem = $problem; expected = $expected; actual = $actual; severity = $severity; recommendation = $recommend; resolution = (Resolution); dpi = (Dpi) }
  [void]$script:Visual.Add($f)
  if ($script:Journey) { if (-not $script:Journey.Contains('visual')) { $script:Journey.visual = New-Object System.Collections.ArrayList }; [void]$script:Journey.visual.Add($f) }
  Write-Host "[$kind $severity] $screen -- $problem -- $actual"
}
$script:Allow = @('OnlineBackup', 'Backup', 'Windows', 'SHA-256', 'HTTPS', 'TOTP', 'Microsoft', 'SQL', 'Server', 'QA', 'IT', 'OK', 'VSS', 'AI', 'localhost', 'https', 'Program Files', 'ProgramData')
# Looks at one window as the customer sees it; returns the screenshot file name. $lang: the language the screen should be in.
function Look($w, [string]$screen, [string]$lang = 'en', [string]$process = $null) {
  if (-not $script:Journey) { return '' }
  $script:ScreenNo++
  $shot = Shot $screen
  $j = $script:Journey
  if (-not $w) { Finding $screen $shot 'The expected window is not on the screen' 'a window' 'none' 'High' 'Find why the window did not open'; return $shot }
  $wr = Rect $w
  $els = @(); try { $els = @($w.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) } catch { }
  $tree = @(foreach ($e in $els) { try { $c = $e.Current; [ordered]@{ type = $c.ControlType.ProgrammaticName -replace 'ControlType\.', ''; name = $c.Name; cls = $c.ClassName; rect = (Rect $e); enabled = $c.IsEnabled; offscreen = $c.IsOffscreen } } catch { } })
  $base = [IO.Path]::GetFileNameWithoutExtension($shot)
  [ordered]@{ screen = $screen; window = $w.Current.Name; rect = $wr; resolution = (Resolution); dpi = (Dpi); controls = $tree } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $j.dir ($base + '.tree.json')) -Encoding UTF8
  # the window alone (what a reviewer zooms into)
  try {
    if ($wr.w -gt 0 -and $wr.h -gt 0) {
      $bmp = New-Object System.Drawing.Bitmap $wr.w, $wr.h; $g = [System.Drawing.Graphics]::FromImage($bmp)
      $g.CopyFromScreen($wr.x, $wr.y, 0, 0, (New-Object System.Drawing.Size($wr.w, $wr.h))); $g.Dispose()
      $bmp.Save((Join-Path $j.dir ($base + '.window.png')), [System.Drawing.Imaging.ImageFormat]::Png)
      # blank: almost every sampled pixel one colour
      $counts = @{}; $n = 0
      for ($y = 5; $y -lt $wr.h - 5; $y += [Math]::Max(1, [int]($wr.h / 40))) { for ($x = 5; $x -lt $wr.w - 5; $x += [Math]::Max(1, [int]($wr.w / 40))) { $p = $bmp.GetPixel($x, $y).ToArgb(); $counts[$p] = 1 + [int]$counts[$p]; $n++ } }
      $top = ($counts.Values | Measure-Object -Maximum).Maximum
      if ($n -gt 0 -and $top / $n -gt 0.985) { Finding $screen $shot 'The window is empty (one colour)' 'the screen content' ('{0:P1} of the window one colour' -f ($top / $n)) 'High' 'The content did not draw: check what the window was waiting for' }
      $bmp.Dispose()
    }
  } catch { }
  # larger than the screen
  $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
  if ($wr.w -gt $wa.Width + 2 -or $wr.h -gt $wa.Height + 2) { Finding $screen $shot 'The window is larger than the screen' "fits in $($wa.Width)x$($wa.Height)" "$($wr.w)x$($wr.h)" 'High' 'Smaller minimum size, or scroll inside the window' }
  elseif ($wr.x -lt $wa.X - 8 -or $wr.y -lt $wa.Y - 8 -or $wr.x + $wr.w -gt $wa.Right + 8 -or $wr.y + $wr.h -gt $wa.Bottom + 8) { Finding $screen $shot 'Part of the window is off the screen' 'the whole window on the screen' "window at $($wr.x),$($wr.y) $($wr.w)x$($wr.h); screen $($wa.Width)x$($wa.Height)" 'Medium' 'Centre the window on the screen it opens on' }
  $hasScroll = @($tree | Where-Object { $_.type -eq 'ScrollBar' -and -not $_.offscreen }).Count -gt 0
  $inter = @('Button', 'Edit', 'CheckBox', 'RadioButton', 'ComboBox')
  $vis = @($tree | Where-Object { -not $_.offscreen -and $_.rect.w -gt 0 -and $_.rect.h -gt 0 -and $_.type -ne 'TitleBar' -and $_.type -ne 'MenuBar' -and $_.cls -notlike '*ScrollBar*' -and $_.type -ne 'ScrollBar' -and $_.type -ne 'Thumb' })
  foreach ($c in $vis) {
    if (-not (Inside $c.rect $wr)) {
      $sev = $(if ($inter -contains $c.type) { 'High' } else { 'Medium' })
      Finding $screen $shot "A $($c.type) is outside the window" 'every control inside the window' ("'{0}' at {1},{2} {3}x{4}; window {5},{6} {7}x{8}" -f $c.name, $c.rect.x, $c.rect.y, $c.rect.w, $c.rect.h, $wr.x, $wr.y, $wr.w, $wr.h) $sev 'Lay the screen out for this window size (or let it scroll)'
    }
  }
  # controls on top of each other (interactive ones, and a label over an interactive one)
  $ic = @($vis | Where-Object { $inter -contains $_.type })
  for ($a = 0; $a -lt $ic.Count; $a++) { for ($b = $a + 1; $b -lt $ic.Count; $b++) {
    $o = OverlapArea $ic[$a].rect $ic[$b].rect; $small = [Math]::Min($ic[$a].rect.w * $ic[$a].rect.h, $ic[$b].rect.w * $ic[$b].rect.h)
    if ($small -gt 0 -and $o / $small -gt 0.15 -and -not (Inside $ic[$a].rect $ic[$b].rect 0) -and -not (Inside $ic[$b].rect $ic[$a].rect 0)) {
      Finding $screen $shot 'Two controls on top of each other' 'controls side by side' ("'{0}' ({1}) and '{2}' ({3}) share {4:P0}" -f $ic[$a].name, $ic[$a].type, $ic[$b].name, $ic[$b].type, ($o / $small)) 'Medium' 'Fix the layout of this screen' } } }
  # a single-line caption wider than its control (cut text): the caption measured in the window's font
  $font = New-Object System.Drawing.Font('Segoe UI', 10)
  foreach ($c in @($vis | Where-Object { @('Button', 'CheckBox', 'RadioButton') -contains $_.type -and $_.name })) {
    $need = [System.Windows.Forms.TextRenderer]::MeasureText($c.name, $font).Width + $(if ($c.type -eq 'Button') { 8 } else { 22 })
    if ($need -gt $c.rect.w + 6 -and $c.rect.h -lt 40) { Finding $screen $shot 'The text may be cut' 'the whole caption visible' ("'{0}' needs about {1}px, the {2} is {3}px wide" -f $c.name, $need, $c.type, $c.rect.w) 'Low' 'Check the crop; widen the control or shorten the caption' }
  }
  # language: English words on a Hebrew screen (names, addresses and the allow-list aside), Hebrew on an English one
  if ($lang -eq 'he') {
    foreach ($c in @($vis | Where-Object { $_.name -and @('Text', 'Button', 'CheckBox', 'RadioButton', 'Window') -contains $_.type })) {
      $t = $c.name; foreach ($a2 in $script:Allow) { $t = $t.Replace($a2, '') }
      $t = $t -replace '[A-Za-z]:\\[^\s]*', '' -replace 'https?://\S+', '' -replace '\S+@\S+', '' -replace '\b[A-Z0-9-]{2,}\b', ''
      if ($t -match '[A-Za-z]{4,}' -and $t -notmatch '\p{IsHebrew}') { Finding $screen $shot 'English text on a Hebrew screen' 'Hebrew' ("{0}: '{1}'" -f $c.type, $c.name) 'Medium' 'Translate this text' }
    }
  } else {
    foreach ($c in @($vis | Where-Object { $_.name -match '\p{IsHebrew}' -and $_.type -ne 'Edit' -and $_.type -ne 'TreeItem' -and $_.type -ne 'ListItem' -and $_.type -ne 'ComboBox' })) { Finding $screen $shot 'Hebrew text on an English screen' 'English' ("{0}: '{1}'" -f $c.type, $c.name) 'Low' 'Translate this text' }
  }
  # a dialog that is not in front
  if ($process) {
    $fg = [ObQa.Screen]::GetForegroundWindow()
    foreach ($d in @(TopWindows $process | Where-Object { $_.Current.ClassName -eq '#32770' })) {
      if ([IntPtr]$d.Current.NativeWindowHandle -ne $fg) { Finding $screen $shot 'A message is not in front' 'the message in front of the window' ("'{0}' is open but another window is in front" -f (DialogText $d)) 'High' 'Show the message owned by the window that asked for it' }
    }
  }
  return $shot
}
# A step that is also a screen: the functional check, then the look
function StepLook([string]$what, [string]$expected, $window, [scriptblock]$check, [string]$lang = 'en', [string]$process = $null) {
  $ok = Step $what $expected $check -NoShot
  $shot = Look $window $what $lang $process
  $script:Journey.steps[$script:Journey.steps.Count - 1].screenshot = $shot
  return $ok
}
# UX: a fixed, written check of the flow (never the robot's taste): records a UX FAIL as a finding with its screenshot
function UxCheck([string]$what, [string]$expected, [bool]$ok, [string]$actual, [string]$severity = 'Medium', [string]$recommend = '') {
  $row = [ordered]@{ step = 'UX: ' + $what; expected = $expected; actual = $actual; result = $(if ($ok) { 'PASS' } else { 'FAIL' }); seconds = 0; screenshot = ''; at = (Get-Date).ToString('HH:mm:ss'); layer = 'UX' }
  if (-not $script:Journey.Contains('ux')) { $script:Journey.ux = New-Object System.Collections.ArrayList }
  [void]$script:Journey.ux.Add($row)
  if (-not $ok) { Finding $what (Shot ('ux-' + $what)) $what $expected $actual $severity $recommend 'UX' }
  Write-Host ('[UX {0}] {1} -- {2}' -f $row.result, $what, $actual)
}
