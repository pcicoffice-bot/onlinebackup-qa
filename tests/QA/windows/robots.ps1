# OnlineBackup QA - the robots: the client's Setup.exe and the customer's program window, used as a person uses them
# (dot-sourced after lib.ps1). Every action goes to the real window through Windows UI Automation: a click is the
# button's own click message, typing goes into the field, a choice is made in the list. Nothing calls the product's
# code directly. Each screen is looked at (Look: screenshot + control tree + visual checks).

$script:SetupProc = 'Setup'
$script:ClientProc = 'OnlineBackup.Client'

# ------------------------------------------------------------------ Setup.exe
function Close-Setup { Get-Process -Name $script:SetupProc -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep 1 }
function SetupWindow([int]$seconds = 90) { WaitWindow $script:SetupProc { $_.Current.ClassName -ne '#32770' } $seconds }
function SetupPage($w) { Texts $w }

# The whole installation through the wizard. Returns $true when the Finish page says it is installed.
# -Launch keeps "Open ... now" ticked (what a customer leaves as it is); -Lang he picks Hebrew in the wizard's list.
function Install-ViaUi([string]$setup, [switch]$Launch) {
  $lang = 'en'
  Close-Setup
  $p = Start-Process -FilePath $setup -PassThru
  $w = SetupWindow 120
  if (-not (Step 'Setup.exe opens its window' 'the setup window within 120 s' { @([bool]$w, $(if ($w) { $w.Current.Name } else { 'no window' })) } -NoShot)) { Look $null 'Launch' | Out-Null; return $false }
  Front $w; Start-Sleep -Seconds 1
  $okW = StepLook 'Installer 01 - Welcome' 'Welcome, the product, Next and Cancel' $w { $t = Texts $w; @((($t -like '*Welcome*') -and ($t -like '*Next*') -and ($t -like '*Cancel*')), $t) } $lang $script:SetupProc
  UxCheck 'Installer: the first page says what will be installed' 'the product name and what it does' ((Texts $w) -like '*installs*') (Texts $w)
  Click (Button $w 'Next')
  Start-Sleep -Seconds 1
  $lic = StepLook 'Installer 02 - License agreement' 'the license text and an accept box' $w { $t = Texts $w; @((($t -like '*License agreement*') -and [bool](Named $w '*accept the terms*')), $t) } $lang $script:SetupProc
  $install = Button $w 'Install'
  Step 'Install is disabled before the license is accepted' 'not enabled' { @((-not $install.Current.IsEnabled), ('enabled=' + $install.Current.IsEnabled)) } -NoShot | Out-Null
  UxCheck 'Installer: a license agreement must be accepted before installing' 'Install disabled until accepted' (-not $install.Current.IsEnabled) ('enabled=' + $install.Current.IsEnabled) 'High'
  if ($true) {
    Step 'Back returns to Welcome, Next comes back' 'Welcome, then the license again' { Click (Button $w 'Back'); $t = Texts $w; $ok = $t -like '*Welcome*'; Click (Button $w 'Next'); @(($ok -and ((Texts $w) -like '*License*')), $t) } -NoShot | Out-Null
  }
  $box = Named $w '*accept the terms*'
  Step 'Accept the license (tick the box)' 'ticked, Install enabled' { $ok = SetCheck $box $true $false; Start-Sleep -Milliseconds 400; @(($ok -and $install.Current.IsEnabled), ('ticked=' + (IsOn $box) + ' install enabled=' + $install.Current.IsEnabled)) } -NoShot | Out-Null
  Look $w 'Installer 03 - License accepted' $lang $script:SetupProc | Out-Null
  Note 'Installer pages that exist' 'Welcome -> License agreement -> Installation (progress) -> Finish. There is no configuration page (folder, options) and no "Ready to install" page in the current installer.'
  UxCheck 'Installer: a "Ready to install" summary before installing' 'a page listing what will be installed and where' $false 'the license page''s button installs at once (no configuration or ready page)' 'Low' 'Proposal only - the owner decides (the UX spec is not approved yet)'
  Click $install
  Start-Sleep -Milliseconds 600
  $prog = StepLook 'Installer 04 - Installing (progress)' 'an "Installing" page with a progress bar' $w { $t = Texts $w; $bar = @(Find $w $CT::ProgressBar); @((($bar.Count -ge 1) -or ($t -like '*installed*')), $t) } $lang $script:SetupProc
  UxCheck 'Installer: progress is visible while installing' 'a progress bar and what is being done' ($prog) 'see the screenshot'
  $until = (Get-Date).AddMinutes(5); $fin = $null
  while ((Get-Date) -lt $until) { $f = @(Find $w $CT::Button) | Where-Object { $_.Current.Name -eq 'Finish' } | Select-Object -First 1; if ($f) { $fin = $f; break }; Start-Sleep -Milliseconds 700 }
  $text = Texts $w
  # the page must SAY it is installed ("<product> is installed"), not only lack the failure sentence
  $ok = StepLook 'Installer 05 - Finish' 'the Finish page says it is installed' $w { @(([bool]$fin -and ($text -like '*is installed*') -and ($text -notlike '*did not finish*')), $text) } $lang $script:SetupProc
  UxCheck 'Installer: the last page says the result and what to do next' 'installed + how to sign in' (($text -like '*installed*') -and ($text -like '*sign in*')) $text
  $open = Named $w 'Open * now'
  if ($open) { [void](SetCheck $open ([bool]$Launch) $true) }   # ticked when the page opens
  if ($fin) { Click $fin }
  Start-Sleep -Seconds 3
  Step 'The setup window closes after Finish' 'no setup window' { $left = TopWindows $script:SetupProc; @(($left.Count -eq 0), "$($left.Count) window(s) left") } -NoShot | Out-Null
  return $ok
}

function Choose-Language($w, [string]$lang) {
  $combo = @(Find $w $CT::ComboBox) | Select-Object -First 1
  if (-not $combo) { throw 'no language list' }
  $want = $(if ($lang -eq 'he') { U @(0x5E2, 0x5D1, 0x5E8, 0x5D9, 0x5EA) } else { $lang })
  try { ($combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand(); Start-Sleep -Milliseconds 500 } catch { }
  $item = @($combo.FindAll($TS::Descendants, ($PC::new($AE::ControlTypeProperty, $CT::ListItem)))) | Where-Object { $_.Current.Name -eq $want } | Select-Object -First 1
  if (-not $item) { throw "no '$want' in the language list" }
  ($item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
  try { ($combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Collapse() } catch { }
  Start-Sleep -Seconds 1
}

# Repair / Update / Remove of an installed program, through the same wizard
function Maintain-ViaUi([string]$setup, [ValidateSet('repair', 'remove', 'update')][string]$action, [switch]$RemoveSettings) {
  Close-Setup
  Start-Process -FilePath $setup | Out-Null
  $w = SetupWindow 120
  if (-not (StepLook "Maintenance - Setup sees the installation ($action)" '"already installed" with the choices' $w { $t = Texts $w; @(($t -like '*already installed*'), $t) } 'en' $script:SetupProc)) { return $false }
  $radios = @(Find $w $CT::RadioButton)
  $pick = $(switch ($action) { 'remove' { $radios | Where-Object { $_.Current.Name -like 'Remove*' } } 'update' { $radios | Where-Object { $_.Current.Name -like 'Update*' } } default { $radios | Where-Object { $_.Current.Name -like 'Repair*' } } }) | Select-Object -First 1
  if (-not (Step "Choose $action" "a '$action' choice" { @([bool]$pick, (($radios | ForEach-Object { $_.Current.Name }) -join ' / ')) } -NoShot)) { return $false }
  Click $pick; Start-Sleep -Milliseconds 700
  if ($action -eq 'remove' -and $RemoveSettings) { $c = Named $w '*remove this computer*settings*'; if ($c) { [void](SetCheck $c $true $false) } }
  Look $w "Maintenance - $action chosen" 'en' $script:SetupProc | Out-Null
  $go = @(Find $w $CT::Button) | Where-Object { @('Remove', 'Repair', 'Update') -contains $_.Current.Name } | Select-Object -First 1
  Click $go
  if ($action -eq 'remove') {
    $d = WaitWindow $script:SetupProc { $_.Current.ClassName -eq '#32770' } 15
    StepLook 'Removal asks for confirmation' 'a Yes / No question in front' $d { @([bool]$d, $(if ($d) { DialogText $d } else { 'no question' })) } 'en' $script:SetupProc | Out-Null
    if ($d) { Click (Button $d 'Yes') }
  }
  Start-Sleep -Seconds 1
  Look $w "Maintenance - $action running" 'en' $script:SetupProc | Out-Null
  $until = (Get-Date).AddMinutes(5); $fin = $null
  while ((Get-Date) -lt $until) { $fin = @(Find $w $CT::Button 'Finish') | Select-Object -First 1; if ($fin) { break }; Start-Sleep -Milliseconds 700 }
  $text = Texts $w
  $want = $(if ($action -eq 'remove') { '*was removed*' } else { '*is installed*' })
  $ok = StepLook "Maintenance - $action finished" "the Finish page ($want)" $w { @(([bool]$fin -and ($text -like $want)), $text) } 'en' $script:SetupProc
  $open = Named $w 'Open * now'; if ($open) { [void](SetCheck $open $false $true) }
  if ($fin) { Click $fin }
  Start-Sleep -Seconds 3
  return $ok
}

# ------------------------------------------------------------------ the customer's program window
function ClientWindow([int]$seconds = 60) { WaitWindow $script:ClientProc { $_.Current.ClassName -ne '#32770' -and $_.Current.BoundingRectangle.Width -gt 300 } $seconds }
# what Windows says about the program's processes and windows (when the robot cannot find its window)
function ClientProcesses { (@(Get-Process -Name $script:ClientProc -ErrorAction SilentlyContinue | ForEach-Object { "pid $($_.Id) window '$($_.MainWindowTitle)' handle $($_.MainWindowHandle) responding $($_.Responding)" }) -join '; ') }
function Open-Client([string]$installDir) {
  $w = ClientWindow 2
  if ($w) {
    # a message that came after Answer-Dialogs stopped waiting (a result shown minutes later) is modal: every click of
    # the next journey on the window would be ignored. It is read, kept and answered here, never left open.
    $late = @(Dialogs $script:ClientProc $w)
    if ($late.Count -gt 0) { $m = Answer-Dialogs $w $null 'A message left open' 5; if ($script:Journey) { Note 'A message that was still open' ($m -join ' || ') } }
    Front $w; return $w
  }
  # a person opens it from the Start menu: the shortcut the installation made
  $lnk = @(Shortcuts '*OnlineBackup.Client.exe' | Where-Object { $_.args -notlike '*--tray*' }) | Select-Object -First 1
  if ($lnk) { Start-Process -FilePath $lnk.file | Out-Null } else { Start-Process -FilePath (Join-Path $installDir 'OnlineBackup.Client.exe') | Out-Null }
  $w = ClientWindow 60; if ($w) { Front $w } else { Note 'The program window' ("not found; processes: " + (ClientProcesses) + " || desktop: " + (DesktopWindows)) }
  return $w
}
function Close-Client { foreach ($p in @(Get-Process -Name $script:ClientProc -ErrorAction SilentlyContinue)) { $p.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 1; if (-not $p.HasExited) { $p.Kill() } } }
# the menu on the side: the button with this caption that is nearest the window's edge
function Nav($w, [string]$label) {
  $b = @(Find $w $CT::Button) | Where-Object { $_.Current.Name.Trim() -eq $label } | Sort-Object { $_.Current.BoundingRectangle.X } | Select-Object -First 1
  if (-not $b) { throw "no menu item '$label' (on screen: $(Texts $w))" }
  Click $b; Start-Sleep -Seconds 2
}
# the side menu's buttons, top to bottom (the column of buttons that share one left edge and width)
function NavButtons($w) {
  $b = @(Find $w $CT::Button | Where-Object { -not $_.Current.IsOffscreen })
  $col = $b | Group-Object { '{0}:{1}' -f [int]$_.Current.BoundingRectangle.X, [int]$_.Current.BoundingRectangle.Width } | Sort-Object Count -Descending | Select-Object -First 1
  if (-not $col -or $col.Count -lt 4) { return @() }
  return @($col.Group | Sort-Object { $_.Current.BoundingRectangle.Y })
}
# the page's own button (not the menu's): the one with this caption furthest from the menu
function PageButton($w, [string]$label) {
  $b = @(Find $w $CT::Button) | Where-Object { $_.Current.Name.Trim() -eq $label -and -not $_.Current.IsOffscreen } | Sort-Object { $_.Current.BoundingRectangle.X } | Select-Object -Last 1
  if (-not $b) { throw "no button '$label' (on screen: $(Texts $w))" }
  return $b
}
# After an action the program answers with a message box (or asks for the password): read it, answer it, keep it
function Answer-Dialogs($main, [string]$password, [string]$screen, [int]$seconds = 20) {
  $seen = @(); $until = (Get-Date).AddSeconds($seconds); $last = $null
  while ((Get-Date) -lt $until) {
    $d = @(Dialogs $script:ClientProc $main) | Select-Object -First 1
    # after an answer the program may ask or say something more (the certificate, then the result): wait 10 s for it
    if (-not $d) { if ($last -and ((Get-Date) - $last).TotalSeconds -gt 10) { break }; Start-Sleep -Milliseconds 500; continue }
    $last = Get-Date
    $text = (DialogText $d); if (-not $text) { $text = Texts $d }
    Look $d ("$screen - dialog: " + $d.Current.Name) 'en' $script:ClientProc | Out-Null
    $seen += ($d.Current.Name + ': ' + $text)
    $pw = @(Find $d $CT::Edit) | Where-Object { $_.Current.IsPassword -or $_.Current.Name -eq 'Password' } | Select-Object -First 1
    if ($pw -and $password) { TypeInto $pw $password; Click (@(Find $d $CT::Button) | Where-Object { $_.Current.Name -eq 'Sign in' -or $_.Current.Name -eq 'OK' } | Select-Object -First 1); Start-Sleep -Seconds 2; continue }
    $agree = Named $d '*read and accept*'; if ($agree) { [void](SetCheck $agree $true $false) }   # the provider's agreement
    $yes = @(Find $d $CT::Button) | Where-Object { @('OK', 'Yes', 'Continue') -contains $_.Current.Name } | Select-Object -First 1
    if ($yes) { Click $yes } else { Click (@(Find $d $CT::Button) | Select-Object -First 1) }
    Start-Sleep -Seconds 1; $last = Get-Date
  }
  return ,$seen
}

# First run: the sign-in page -> user name, password -> Sign in -> (the server's certificate, the agreement) -> connected
function Connect-ViaUi($w, [string]$login, [string]$password) {
  StepLook 'Client 01 - First run: the sign-in page' 'Sign in, the server, user name and password' $w { $t = Texts $w; @((($t -like '*Sign in*') -and ($t -like '*User name*')), $t) } 'en' $script:ClientProc | Out-Null
  UxCheck 'Client: the first screen asks only for what the customer received' 'server (pre-filled), user name, password' ((Texts $w) -like '*Backup server*') (Texts $w)
  TypeInto (Edit $w 'User name') $login
  TypeInto (Edit $w 'Password') $password
  Look $w 'Client 02 - Sign-in filled in' 'en' $script:ClientProc | Out-Null
  Click (PageButton $w 'Sign in')
  $msgs = Answer-Dialogs $w $password 'Client 03 - Signing in' 60
  Note 'Messages while signing in' ($msgs -join ' || ')
  $until = (Get-Date).AddSeconds(60)
  while ((Get-Date) -lt $until -and (Texts $w) -like '*User name*') { Start-Sleep -Seconds 1 }
  $t = Texts $w
  $ok = StepLook 'Client 04 - Connected: the program asks what to back up' 'the "New backup" page' $w { @((($t -like '*New backup*') -and ($t -notlike '*User name*')), $t) } 'en' $script:ClientProc
  UxCheck 'Client: after signing in the program says the computer is connected and what to do next' 'a confirmation and the next step' (($msgs -join ' ') -like '*connected*') ($msgs -join ' || ')
  return $ok
}

# The folder tree of "New backup": open C:\ -> ... -> the folder, tick it (as a person clicks the box)
# The keyboard way through a folder tree (when UI Automation shows no items): Home, then for each level type the
# folder's name (the tree selects the item that starts so), the right arrow opens it; returns $null (no item element)
function TreePathKeys($tree, [string]$path) {
  $parts = $path.TrimEnd('\').Split('\')
  Key $tree 0x24   # Home
  for ($i = 0; $i -lt $parts.Count; $i++) {
    Start-Sleep -Milliseconds 1600   # the tree's type-to-find forgets after about a second
    $h = [IntPtr]$tree.Current.NativeWindowHandle
    foreach ($ch in $parts[$i].ToCharArray()) { [void][ObQa.Win]::PostMessage($h, 0x102, [IntPtr][int]$ch, [IntPtr]::Zero) }
    Start-Sleep -Milliseconds 500
    if ($i -lt $parts.Count - 1) { Key $tree 0x27; Start-Sleep -Milliseconds 1200 }
  }
  return $null
}
function TreePath($tree, [string]$path) {
  $items = @($tree.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition))
  if ($items.Count -eq 0 -or -not ($items | Where-Object { $_.Current.Name })) { return TreePathKeys $tree $path }
  $parts = $path.TrimEnd('\').Split('\'); $node = $null; $scope = $tree
  for ($i = 0; $i -lt $parts.Count; $i++) {
    $name = $(if ($i -eq 0) { $parts[0] + '\' } else { $parts[$i] })
    $until = (Get-Date).AddSeconds(15); $node = $null
    while (-not $node -and (Get-Date) -lt $until) {
      $node = @($scope.FindAll($TS::Children, ($PC::new($AE::ControlTypeProperty, $CT::TreeItem)))) | Where-Object { $_.Current.Name -eq $name -or $_.Current.Name -eq $parts[$i] } | Select-Object -First 1
      if (-not $node) { Start-Sleep -Milliseconds 500 }
    }
    if (-not $node) { throw "no '$name' in the folder tree under '$($parts[0..([Math]::Max(0,$i-1))] -join '\')'" }
    if ($i -lt $parts.Count - 1) {
      try { ($node.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand() }
      catch { Select-Node $node; Key $tree 0x27 }   # the right arrow opens a folder
      Start-Sleep -Milliseconds 800
    }
    $scope = $node
  }
  return $node
}
function Select-Node($node) { ($node.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select(); Start-Sleep -Milliseconds 300 }
# a key pressed in a control (posted to its window, as the keyboard does)
function Key($el, [int]$vk) { $h = [IntPtr]$el.Current.NativeWindowHandle; [void][ObQa.Win]::PostMessage($h, 0x100, [IntPtr]$vk, [IntPtr]::Zero); [void][ObQa.Win]::PostMessage($h, 0x101, [IntPtr]$vk, [IntPtr]0xC0000001); Start-Sleep -Milliseconds 500 }
# the box of a folder: select it and press the space bar, as a person does with the keyboard (the program sees it as a tick)
function Tick($tree, $node) { Select-Node $node; Key $tree 0x20; return 'space bar' }
function NewSet-ViaUi($w, [string]$name, [string]$folder, [string]$password) {
  Nav $w 'New backup'
  StepLook 'Client 05 - New backup page' 'type, name, the folder tree, time, the button' $w { $t = Texts $w; @((($t -like '*Folders to back up*') -and ($t -like '*Backup name*')), $t) } 'en' $script:ClientProc | Out-Null
  TypeInto (Edit $w 'Backup name') $name
  $tree = @(Find $w $CT::Tree) | Select-Object -First 1
  $node = TreePath $tree $folder
  if ($node) { $how = Tick $tree $node } else { Key $tree 0x20; $how = 'keyboard: name typed, space bar' }; Start-Sleep -Milliseconds 600
  $state = 'not readable through UI Automation'; if ($node) { $v = IsOn $node; if ($v -ne $null) { $state = "ticked=$v" } }
  Note 'Tick the folder in the tree' "$folder ($how); $state - the server's copy of the set is the proof (next step)"
  Look $w 'Client 06 - Backup configured' 'en' $script:ClientProc | Out-Null
  Click (PageButton $w 'New backup')
  $msgs = Answer-Dialogs $w $password 'Client 07 - Adding the backup' 30
  Note 'Messages when adding the backup' ($msgs -join ' || ')
  Start-Sleep -Seconds 2
  $t = Texts $w
  return (StepLook 'Client 08 - The backup is listed on the status page' "a card '$name' with Back up now" $w { @((($t -like "*$name*") -and ($t -like '*Back up now*') -and (($msgs -join ' ') -like '*was added*')), ($t + ' || ' + ($msgs -join ' || '))) } 'en' $script:ClientProc)
}
function BackupNow-ViaUi($w, [string]$screen) {
  Nav $w 'Backup status'
  $b = PageButton $w 'Back up now'
  Click $b
  $msgs = Answer-Dialogs $w $null "$screen - started" 15
  Start-Sleep -Seconds 2
  Look $w "$screen - running" 'en' $script:ClientProc | Out-Null
  return ,$msgs
}
function WaitIdle-Client($w, [int]$minutes = 15) {
  $until = (Get-Date).AddMinutes($minutes)
  while ((Get-Date) -lt $until) { $t = Texts $w; if ($t -notlike '*Backing up now*' -and $t -notlike '*Restoring*' -and $t -notlike '*Running*') { return $true }; Start-Sleep -Seconds 3 }
  return $false
}
function Restore-ViaUi($w, [string]$target, [string]$password, [string]$screen) {
  Nav $w 'Restore'
  Start-Sleep -Seconds 3
  StepLook "$screen - Restore page" 'the backup, the latest point, the files, the folder' $w { $t = Texts $w; @((($t -like '*Restore point*') -and ($t -like '*Latest*')), $t) } 'en' $script:ClientProc | Out-Null
  TypeInto (Edit $w 'Restore to folder') $target
  Look $w "$screen - Restore selection" 'en' $script:ClientProc | Out-Null
  Click (PageButton $w 'Restore')
  $msgs = Answer-Dialogs $w $password "$screen - Restore started" 40
  Note "$screen - messages" ($msgs -join ' || ')
  Start-Sleep -Seconds 2
  return ,$msgs
}
