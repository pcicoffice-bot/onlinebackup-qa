# UX findings — installer and the customer's window (QA Agent E)

Documentation only. No product file and no screen was changed. Every proposal below is for the owner to decide.
**Flow** = changes what the customer does or sees step by step, so the owner must approve it first.
**Wording** = text, translation, icon or caption only.

## Evidence used

- Evidence branch `qa-evidence` of `pcicoffice-bot/onlinebackup-qa`, commit `49a2710`.
  - `runs/37487240381/` is the only run with journeys. Windows Server 2025, 1024x768, 100% DPI, English UI.
    It was built from commit `8e9f3b9`, which is not in this repository's history. The run started at 15:28 UTC, which
    is **before** `b262143` (15:38, "a cut-off Setup.exe now says it is damaged (bug 31)"). The W02 screenshot therefore
    probably shows the build from before that fix. See UX-01.
  - `runs/37489257518/` contains only `index.html` and `vm/container.log` (the VM was still starting). It has no
    journeys and no screenshots.
- Code at `1a35d94`: `src/Setup/Program.cs`, `src/Agent/SetupForm.cs`, `src/Agent/ClientForm.cs`, `src/Agent/Setup.cs`,
  `src/Agent/AgentService.cs`, `src/Core/L.cs` and `src/Core/i18n/he.json` / `en.json`.
- `docs/PRODUCT-UX-SPEC.md`: the proposal that is waiting for the owner's approval. It is compared below and not implemented.

### Screens that were actually captured

| Screen | File (in the evidence branch) | What it shows |
|---|---|---|
| Setup.exe message for a damaged download | `runs/37487240381/W02/01-The-damaged-file-says-it-is-damaged.window.png` (+ `.png`, `.tree.json`) | Caption "Setup", **Information** icon, text "Unzip the whole package to a folder first, then run Setup.exe from there.", OK |
| (meant to show the same message) | `runs/37487240381/W02/02-ux-Installer--a-damaged-download-tells-the-customer-what-to-do.png` | Only the runner's console. The message box was already closed, so this is **not** evidence of the message |
| Installer page 1, Welcome (English) | `runs/37487240381/W03/01-Installer-01---Welcome.window.png` (+ `.png`, `.tree.json`) | Title "ITSguard Server Online", header "ITSguard Server Online — setup", steps Welcome / License agreement / Installation / Finish, language "English", Cancel and Next |
| "stopped" pictures of W03–W09 | `runs/37487240381/W0[3-9]/0?-stopped.png` | All seven are byte-identical (md5 `9b6e7ae4…`): the Welcome page still open over the console |

**Not captured yet:** the license page, installing, finish, failure, repair or remove pages, any Hebrew screen, and the
whole customer window (sign-in, status, restore, new backup, security, help, tray). Everything about those screens
is in part (b), from reading the code only.

Notes on the robot, not on the product. They are reported so the next run gives evidence for the UX findings:
- W03 stopped with "no button 'Next'", but `01-Installer-01---Welcome.tree.json` lists `name "Next"`,
  class `WindowsForms10.BUTTON…`, rect x=758 y=660. The robot looked for the control type "Button" and Windows reported
  every control as "Pane" (later robot commit `b2f9630` addresses this). The installer was never closed, so W04–W09
  photographed it again and no client window exists in the evidence.
- W02 `02-ux-…png` was taken after the message box had been closed. A UX picture has to be taken while the message is
  on screen.

---

## (a) Proven by a screenshot

### UX-01 — A damaged download tells the customer to "unzip the package"
- **Screen:** Setup.exe, the message box shown before the wizard.
- **Screenshot:** `runs/37487240381/W02/01-The-damaged-file-says-it-is-damaged.window.png`; the control tree is in `…damaged.tree.json`.
- **Expected:** a message saying that the downloaded file is damaged and must be downloaded again, with the error icon.
- **Actual:** caption `Setup`, Information icon, text `Unzip the whole package to a folder first, then run Setup.exe from there.`, button `OK`. The robot cut the file to 60% (`win-e2e.ps1:177`).
- **Problem:** the customer downloaded a single Setup.exe and has no package to unzip. The instruction cannot be followed, and nothing says the download is broken. The Information icon tells them nothing went wrong.
- **Severity:** High.
- **Recommendation:** re-run W02 on the current build. `b262143` added `SetupPayload.State.Cut`, so `Program.cs:34` should now show `The installation file is damaged (the file is incomplete). Download it again.` with the Error icon. That text still needs wording changes (see UX-02 and UX-10): drop the technical part in parentheses, use a meaningful caption, and translate it.
- **Type:** Wording (re-check that the behaviour fix holds).

### UX-02 — The "Unzip the whole package" message: Information icon and a generic caption
- **Screen:** Setup.exe message box (`src/Setup/Program.cs:39`). The text and icon are visible in the UX-01 screenshot.
- **Screenshot:** `runs/37487240381/W02/01-The-damaged-file-says-it-is-damaged.window.png`
- **Expected:** a stopping error shows the Error icon (or at least Warning) and a caption that names the product or "… Setup", so the customer knows which program is speaking.
- **Actual:** `MessageBox.Show("Unzip the whole package to a folder first, then run Setup.exe from there.", "Setup", MessageBoxButtons.OK, MessageBoxIcon.Information)`.
- **Problem:** the installation stops (`return 1`), but the icon says "for your information". The caption "Setup" is anonymous, and a customer with several setup windows open cannot tell which one this is. It also has the RTL / Hebrew gap described in UX-10.
- **Severity:** Medium.
- **Recommendation:** use `MessageBoxIcon.Error` and a caption such as "ITSguard Server Online Setup". Program.cs cannot read branding before unpacking, so either take the name from Setup.exe's own version resource or use "Backup Setup". Text proposal: "This setup file is incomplete. Download it again from the link your IT company sent you." (The same wording covers both cases a customer can actually hit.)
- **Type:** Wording.

### UX-03 — The installer window and its taskbar button show the generic Windows program icon
- **Screen:** Installer, all pages.
- **Screenshot:** `runs/37487240381/W03/01-Installer-01---Welcome.window.png` (title bar, top left) and `runs/37487240381/W03/02-stopped.png` (taskbar button next to the console).
- **Expected:** the product's or the setup's icon, as on every commercial installer.
- **Actual:** Windows' default "application" icon (a white window with a blue panel).
- **Problem:** looks unfinished and lowers trust at the moment the customer is asked for administrator rights. Cause in the code: `SetupForm.cs:86` takes `Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location)`, which is `OnlineBackup.Agent.exe`, and the Agent project has no `ApplicationIcon`. Only `src/Setup/Setup.csproj:11` sets one (`setup.ico`). `ClientForm.cs:86` uses the same pattern, so the customer window and the tray probably show the generic icon too (not seen yet).
- **Severity:** Medium.
- **Recommendation:** give the Agent and Client executables the product icon, or take the icon from the process's main module (Setup.exe) for the setup window, or draw it from the branding logo.
- **Type:** Wording/visual (no flow change).

### UX-04 — Window title and header: "setup" in lower case, and the title does not say "Setup"
- **Screen:** Installer header and title bar.
- **Screenshot:** `runs/37487240381/W03/01-Installer-01---Welcome.window.png`
- **Expected:** a title bar reading "ITSguard Server Online Setup" (the Windows convention, which also tells the installer apart from the program on the taskbar), and a header in title case.
- **Actual:** title bar `ITSguard Server Online` (`SetupForm.cs:84`, `Text = product`); header `ITSguard Server Online — setup` (`SetupForm.cs:145`, key `"{0} — setup"`).
- **Problem:** small, but it is the first thing the customer reads. Once installed, the program window has the same title (`ClientForm.cs:157`), so two windows with the same name can be open at the end of the installation.
- **Severity:** Low.
- **Recommendation:** use `"{0} Setup"` for both the title and the header (Hebrew: `התקנת {0}`).
- **Type:** Wording.

### UX-05 — Welcome page: no version or publisher, and most of the page is empty
- **Screen:** Installer page 1.
- **Screenshot:** `runs/37487240381/W03/01-Installer-01---Welcome.window.png`
- **Expected (spec §1, row 1):** "שם המוצר והחברה, גרסה, משפט מה התוכנה עושה" (the product and company name, the version, and one sentence about what the program does).
- **Actual:** heading, the sentence `This installs ITSguard Server Online on this computer. It backs up your files to your provider's backup server, encrypted on this computer before they leave.`, `Computer: runnervmfi6oq` and `Click Next to continue, or Cancel to exit.` No version and no company. The header sub-line is empty because the brand has no SLOGAN or COMPANY. The text ends at about y=330 of a 709-pixel window.
- **Problem:** the customer cannot tell which version they are installing (support will ask). The page looks sparse.
- **Severity:** Low.
- **Recommendation:** add "Version X (from version.txt) · publisher: <company>". This only adds information and does not change the flow.
- **Type:** Wording.

### UX-06 — The product name contains "Server", but this installs the client on a PC
- **Screen:** Installer, all pages.
- **Screenshot:** `runs/37487240381/W03/01-Installer-01---Welcome.window.png`
- **Expected:** a non-technical customer understands that this installs the backup program for this computer.
- **Actual:** `Welcome to the ITSguard Server Online setup` / `This installs ITSguard Server Online on this computer.` The name comes from the branding (`branding.xml` PRODUCT; the fallback in `Setup.cs:32` is the same name).
- **Problem:** "Server" can make someone at a workstation think they are installing a server. This is a brand decision and is recorded here only so the owner is aware of it.
- **Severity:** Low.
- **Recommendation:** the owner decides. One option is to keep the brand but have the Welcome sentence say "the backup program for this computer".
- **Type:** Wording (brand, owner only).

### UX-07 — Installer steps compared with a standard Windows installer (no "Ready to install" page)
- **Screen:** Installer step bar.
- **Screenshot:** `runs/37487240381/W03/01-Installer-01---Welcome.window.png` (steps `Welcome · License agreement · Installation · Finish`).
- **Expected (standard Windows wizard, spec §1 row 3):** a "Ready to install" page that shows the folder, the server and the service before anything happens.
- **Actual:** there is no configuration or ready page. In the code (not seen yet), the License page's Next button is renamed `Install` (`SetupForm.cs:254`), so the installation starts when the customer accepts the license. The install folder cannot be chosen, and the server address is not shown before installing.
- **Problem:** this is noted, not demanded. It is an acceptable simplified wizard, but the customer does not see where the program goes or which server it will talk to before it installs.
- **Severity:** Low.
- **Recommendation:** the owner decides on spec §11 item 1. If the page is not wanted, a short line on the License page is enough: "Install puts the program in C:\Program Files\… and a Windows service that backs up in the background."
- **Type:** Flow (owner approval).

---

## (b) From code reading only (not yet seen on screen)

### Installer (Setup.exe and SetupForm)

#### UX-10 — Messages shown before the wizard are English only, even for Hebrew customers
- **Screen:** Setup.exe message boxes. Code only: `src/Setup/Program.cs:34`, `:39`.
- **Expected:** the customer's language. Before the wizard there is no choice yet, so the Windows display language decides; the right-to-left options are used for Hebrew.
- **Actual:** fixed English strings, not passed through `L`. Neither `The installation file is damaged ({0}). Download it again.` nor `Unzip the whole package to a folder first, then run Setup.exe from there.` exists in `he.json`. The parenthesised part is a technical exception message (`the file is incomplete`, `length`, `count`, `name`, `size`, or a .NET message).
- **Problem:** a Hebrew customer's first contact with the product is an English error that includes technical words.
- **Severity:** Medium.
- **Recommendation:** add both texts to he.json. Choose the language from `CultureInfo.CurrentUICulture` (he → Hebrew) and pass `MessageBoxOptions.RightAlign | RtlReading` for Hebrew. Drop the parenthesised technical part or move it to a log.
- **Type:** Wording/translation.

#### UX-17 — "{product} is installed" (green) even when the Windows service was not created or did not start
- **Screen:** Installer, Finish page. Code only: `src/Agent/Setup.cs:91` `say(ServiceSetup.Install(…)); r.Service = true;`. `ServiceSetup.Install` (`AgentService.cs:36-43`) returns the sc.exe text and never throws. `SetupForm.cs:276-280` shows success whenever no exception was raised.
- **Expected (spec §1 row 5):** "הותקן בהצלחה — רק אחרי בדיקה שהשירות רץ" ("installed successfully", shown only after checking that the service is running). Otherwise the failure page.
- **Actual:** if `sc create` or `sc start` fails (for example `[SC] OpenSCManager FAILED 5: Access is denied.` or `[SC] StartService FAILED 1053`), the text only appears as a bullet on the progress page. The Finish page still says `{0} is installed` in green, followed by `It runs in the background and starts with Windows.`
- **Problem:** the customer is told the computer is set up when nothing will back it up. This is the most important message in the installer.
- **Severity:** High (not seen on screen; needs a test that makes sc.exe fail).
- **Recommendation:** after `sc start`, query the service (`sc query` STATE RUNNING, or ServiceController) and throw a plain-language error if it is not running. The existing failure page then shows it.
- **Type:** Behaviour/wording (no flow change; matches the spec).

#### UX-11 — The "Installing…" page shows raw `sc.exe` output as a bullet
- **Screen:** Installer page "Installation". Code only: `src/Agent/Setup.cs:91` `say(ServiceSetup.Install(exe, r.DataDir, product))`; `src/Agent/AgentService.cs:40-43` returns the concatenated output of `sc create`, `sc failure` and `sc start`. It is shown as `"•  " + L.Tr(lang, l)` (`SetupForm.cs:262`).
- **Expected:** steps in plain words, such as "The backup service was installed and started".
- **Actual:** text like `[SC] CreateService SUCCESS [SC] ChangeServiceConfig2 SUCCESS SERVICE_NAME: OnlineBackupAgent TYPE : 10 WIN32_OWN_PROCESS STATE : 2 START_PENDING …` (the standard sc.exe output). On a Hebrew Windows it is in Windows' own language, and it is never translated.
- **Problem:** it looks like a developer log. A customer who reads "PENDING" may think something failed.
- **Severity:** Medium.
- **Recommendation:** have `say` report "Backup service installed" and "Backup service started" (or "did not start — …") and write the sc.exe output only to the setup log. Other lines are technical too: `{product} — https://host:8443` (`Setup.cs:42`) and `Installed to C:\Program Files\OnlineBackup` (acceptable).
- **Type:** Wording (what the progress list says).

#### UX-12 — Failed installation shows a raw exception message and no log location
- **Screen:** Installer, Finish page in the failed state. Code only: `SetupForm.cs:265-270`, `installError = e.Message` (`:397`).
- **Expected (spec §1 row 5):** the reason in plain language, what state the computer is in, where the log is, and the support phone.
- **Actual:** `The installation did not finish` (red), then the exception text (translated only if it matches a he.json key, for example `Access to the path '…' is denied.` stays English), then `Run the setup again. If it happens again, contact {0}.` (good: a next step and the support contact).
- **Problem:** .NET messages are not customer language, and the customer is not told whether anything was left half-installed.
- **Severity:** Medium.
- **Recommendation:** map the known failures (service not created, folder in use, disk full) to plain sentences, and add "Details were saved in …\setup.log". Keep the existing next-step line.
- **Type:** Wording (and a log path).

#### UX-13 — Cancel and close behaviour
- **Screen:** Installer. Code only: `SetupForm.cs:106-107`.
- **Expected:** standard installers ask "Are you sure you want to cancel the installation?" once the customer has gone past the Welcome page. When the close button cannot work during installation, the customer is told why.
- **Actual:** `Cancel` closes immediately on any page. During the installation, the window's X button is silently ignored (`if (busy) e.Cancel = true`) and Cancel is disabled.
- **Problem:** the X button seems to do nothing. A customer who thinks the program has hung may kill it in Task Manager, which is what the "never half installed" rule tries to avoid.
- **Severity:** Low.
- **Recommendation:** when X is pressed during installation, show "Please wait until the installation finishes (about a minute)." Add the optional cancel confirmation on the License page.
- **Type:** Wording for the message; the confirmation is a small flow change (owner).

#### UX-14 — Remove: the "Also remove this computer's settings" checkbox is meant to be indented but is not
- **Screen:** Installer, maintenance (Remove). Code only: `SetupForm.cs:243` sets `Margin = new Padding(24, …)`, but `Stack()` (`:208`) places every row at `Left = 0` and uses only `Margin.Top` and `Margin.Bottom`.
- **Expected:** the checkbox is visibly indented under the "Remove" option it belongs to.
- **Actual:** it is aligned with the radio buttons, so it reads as a separate option.
- **Severity:** Low.
- **Recommendation:** make `Stack()` respect `Margin.Left` (or `Margin.Right` in RTL).
- **Type:** Visual only.

#### UX-15 — Many names for one product
- **Screen:** all. Code only.
- **Actual:** the installer fallback is `"Backup"` (`SetupForm.cs:71`). The install fallback is `"ITSguard Server Online"` (`Setup.cs:32`). The service display name falls back to `"ITSguard Server Online Agent"` (`AgentService.cs:36`). The service is `OnlineBackupAgent`, the folder is `C:\Program Files\OnlineBackup`, and the process is `OnlineBackup.Client.exe`. The customer window starts with the tray tooltip `"Backup"` (`ClientForm.cs:95`) and uses `state["product"] ?? "Backup"` (`:157`, `:251`, `:379`). Setup.exe message boxes are captioned `"Setup"`.
- **Problem:** if branding is missing or not loaded yet, the same software shows three different names. The Windows-level names (service, folder, process) are the old internal name. A customer who looks in Task Manager or Services sees "OnlineBackup", which matches nothing on screen.
- **Severity:** Low.
- **Recommendation:** use one fallback constant for the product name. The internal Windows names are fine, but the service display name should always be the branded product name.
- **Type:** Wording.

#### UX-16 — The language starts as English even on a Hebrew Windows
- **Screen:** installer and customer window. Code only: `SetupForm.cs:77-79`, `ClientForm.cs:153-155` (comment `I18N-040 (owner): English until the person picks a language by hand`).
- **Note:** this is a recorded owner decision, so it is not a defect. It is listed because a Hebrew customer has to find the English combo box `English` in the top corner (seen in the W03 screenshot, top right) before anything is in Hebrew. The setup also writes the choice to `language.txt` so the program follows it, which is good.
- **Severity:** Low (information for the owner).
- **Type:** Flow (owner).

### Customer window (ClientForm)

#### UX-20 — Service on this computer down: the window blames the backup server and says "No backups are set up yet"
- **Screen:** status page. Code only: `ClientForm.cs:40` (`"The backup service on this computer does not answer."`), `:133` (`state.Set("offline", err)`), `:338`, `:513`.
- **Expected:** "The backup service on this computer is not running. Restart the computer; if it continues, contact <company, phone>." The last known data is kept (spec §6, "מנותק").
- **Actual:** an orange banner `No connection to the backup server right now: The backup service on this computer does not answer.` If the first state call already failed, `state` has no sets, so the large hero below says `No backups are set up yet. Add one in the "New backup" tab.` and the menu is visible.
- **Problem:** the cause is wrong (it is the local service, not the server), there is no next step, and the hero suggests that the customer's backups are gone, which may lead them to create duplicates. `"The backup service on this computer does not answer."` is **missing from he.json**, so a Hebrew screen shows `אין חיבור לשרת הגיבוי כרגע: The backup service on this computer does not answer.`
- **Severity:** High.
- **Recommendation:** separate the "local service unreachable" state from "backup server unreachable". When the service does not answer, show only that banner with the next step and the support contact, and hide the "no backups" hero. Add the text to he.json.
- **Type:** Wording plus a state change on the status page (owner, small).

#### UX-21 — The tray menu item "Close the window" quits the program
- **Screen:** tray icon menu. Code only: `ClientForm.cs:110` (`closing = true; tray.Visible = false; Close();`). The window's X only hides it (`:97`).
- **Expected:** the label says what happens.
- **Actual:** the label is `Close the window` (Hebrew `סגירת החלון`), but it removes the tray icon and ends the program, while the window's own X keeps it running.
- **Problem:** the customer cannot predict the result. After choosing it, the tray state and notifications disappear until the next logon.
- **Severity:** Medium.
- **Recommendation:** rename it to `Exit (the backup keeps running)` / `יציאה (הגיבוי ממשיך לפעול)`.
- **Type:** Wording.

#### UX-22 — Tray "Back up now" opens one blocking message per backup, or does nothing
- **Screen:** tray menu. Code only: `ClientForm.cs:107`: `foreach (var st in Mine()) Do(…, T("The backup has started"))`.
- **Expected:** one confirmation, such as "3 backups started", ideally as a balloon rather than a blocking dialog. When there are no backups, a short explanation.
- **Actual:** with N backup sets, N modal boxes saying `The backup has started` appear. With none, nothing happens.
- **Severity:** Medium.
- **Recommendation:** start all of them and show one balloon (`tray.ShowBalloonTip`). If there are none: "No backups are set up yet — open the program to add one."
- **Type:** Wording/feedback (no flow change).

#### UX-23 — Errors use the Warning icon
- **Screen:** every message box from the customer window. Code only: `ClientForm.cs:206` (`bad ? MessageBoxIcon.Warning : MessageBoxIcon.Information`).
- **Expected:** failures that stopped an action use the Error icon. Warning is for "it worked, but…".
- **Actual:** every failure (restore refused, backup not started, sign-in error relayed) shows the yellow Warning icon.
- **Severity:** Low.
- **Recommendation:** use the Error icon for failures. The caption is already the product name, which is good.
- **Type:** Wording/icon.

#### UX-24 — Two-step setup uses a VB `InputBox`: the key and the backup codes cannot be copied, there is no QR code, and no RTL
- **Screen:** Security, "Turn on". Code only: `ClientForm.cs:712`, `Microsoft.VisualBasic.Interaction.InputBox(…)`.
- **Expected:** a dialog with a QR code (the web screens already have `qrcode.js`), the key in a selectable field with "Copy", the backup codes with "Copy" and "Save to file", and a code field. Everything in the customer's language and direction.
- **Actual:** one InputBox whose prompt label contains `Or type the key:` + the secret + `Keep these one-time backup codes in a safe place (each works once if the phone is lost):` + the codes + `Type the code the app shows now:`. The InputBox prompt is a plain label: the text cannot be selected or copied, it is always left-to-right, and "Or type the key" refers to a QR code that is not shown.
- **Problem:** the customer has to copy a long secret and several codes **by hand**. A typing mistake locks them out of restores later. This is the most error-prone screen in the program and it looks like a developer tool. In Hebrew, the mixed text in a left-to-right box reads incorrectly.
- **Severity:** High.
- **Recommendation:** a proper dialog (QR, copy buttons, "I saved the codes" checkbox, code field, right-to-left aware).
- **Type:** Flow (owner approval).

#### UX-25 — "Switch two-step verification off?" has no icon and no RTL options
- **Screen:** Security, "Switch off". Code only: `ClientForm.cs:704` (`MessageBox.Show(this, T("Switch two-step verification off?"), Text, MessageBoxButtons.YesNo)`).
- **Expected:** like the other confirmations (the remove confirmation at `SetupForm.cs:349` is a good model): a Warning icon, No as the default for a security downgrade, and RightAlign/RtlReading in Hebrew.
- **Actual:** no icon, Yes is the default, and the Hebrew text `לכבות את האימות הדו-שלבי?` is laid out left to right.
- **Severity:** Low.
- **Type:** Wording/icon.

#### UX-26 — The Security page reads synchronously and shows "off" when it cannot read
- **Screen:** Security. Code only: `ClientForm.cs:699` (`try { sec = api.Call("security", …) } catch { sec = new Msg(); }`, on the UI thread).
- **Expected:** a loading state, and an error state ("Could not read the security settings — try again"). Spec §6.
- **Actual:** when the call fails, the page shows the "off" text and a `Turn on` button even if two-step verification is on. While the call runs, the window is frozen (the local API timeout is 600 s, `:34`).
- **Problem:** misleading security state. The customer may "turn on" something that is already on.
- **Severity:** Medium.
- **Type:** Wording/state (no flow change).

#### UX-27 — Folder tree: the "No access to this folder." line can be ticked, and ticking it is expected to crash the window
- **Screen:** New backup and Change the backup, the folder tree. Code only: `ClientForm.cs:854` adds `No access to this folder.` as an ordinary node of a `CheckBoxes = true` tree, with `Tag = null`. `AfterCheck` (`:835-839`) does `p.StartsWith(…)` with `p = null` as soon as `Included` is not empty, or adds `null` to `Included`, which then fails in `:837` and `:860` (`x.TrimEnd`). There is no `Application.ThreadException` handler in `src/Agent` or `src/Setup` (grep found none).
- **Expected:** an information line that cannot be ticked (a disabled or grey node without a checkbox), and in no case a crash.
- **Actual (expected from the code, not run):** a NullReferenceException in a UI event, which brings up the .NET "Unhandled exception has occurred in your application" dialog with Details / Continue / Quit.
- **Severity:** Medium (not seen on screen; it needs a folder the service cannot list, for example `C:\System Volume Information`).
- **Recommendation for the test writers:** a component test that builds a FolderPicker with a fake `IClientApi` returning `denied=1`, ticks the node and asserts no exception. The restore tree has the same pattern for the message nodes `Loading files…` and `This backup has no restore points yet …` (`:578`, `:588`): ticking them sends a path with no value (`Checked()`, `:631`).
- **Type:** Behaviour fix (no flow change).

#### UX-28 — Restore: clicking "Restore" without a restore point does nothing
- **Screen:** Restore. Code only: `ClientForm.cs:598` (`if (pointBox.SelectedIndex < 0) return;`).
- **Expected:** a message next to the field, such as "Choose a restore point", or the Restore button disabled until a point is selected.
- **Actual:** the button silently does nothing (for example while the points are still loading, or when the set has none).
- **Severity:** Medium.
- **Type:** Wording/feedback.

#### UX-29 — Restore: no confirmation before replacing files, and the result is not reported
- **Screen:** Restore. Code only: `ClientForm.cs:571`, `:601`.
- **Expected (spec §4 R4–R7, §7):** an explicit confirmation when "Replace existing files" is ticked, and a result ("1,204 files restored" or "4 failed" with a list).
- **Actual:** a `Replace existing files` checkbox with no confirmation. After clicking: `The restore has started — follow it in the "Backup status" tab`, and nothing more unless the customer opens the status page and reads the "Recent activity" list.
- **Problem:** overwriting happens without a second chance, and a failed restore is easy to miss. The wording "tab" is also wrong: the menu is a side list, not tabs. The same applies to `Add one in the "New backup" tab.` (`:513`); Hebrew uses `בלשונית` in both.
- **Severity:** Medium.
- **Recommendation:** the owner decides on spec §4. Independent of that: a balloon or message when the restore ends (success or failure), and replace "tab" with "page" or "in the menu" (Hebrew `בתפריט`).
- **Type:** Flow (confirmation, result screen: owner) plus wording ("tab").

#### UX-30 — New backup: SQL Server, System State and Whole computer have no settings at all
- **Screen:** New backup. Code only: `ClientForm.cs:640-653`. `dbUser` is created and never used (`:651`). For MSSQL and SYSTEMSTATE the folder tree is disabled (`:653`), and choosing a type replaces the name the customer typed with the type's name.
- **Expected:** after choosing "Microsoft SQL Server", the customer sees which instance and databases will be backed up (or "all databases on this computer"), and an explanation for System State and bare-metal.
- **Actual:** a type list, a disabled tree, and the `New backup` button. Nothing says what will be backed up.
- **Severity:** Medium.
- **Recommendation:** at the least, a sentence under the type: "All SQL Server databases on this computer are backed up" (if that is the behaviour). Spec §3 (owner) proposes a wizard. Do not overwrite a name the customer has edited.
- **Type:** Wording now; flow (owner) for the wizard.

#### UX-31 — The "New backup" page's action button is also called "New backup"
- **Screen:** New backup. Code only: `ClientForm.cs:637` (title) and `:652` (button).
- **Expected:** a verb for the action: `Create backup` / `צור גיבוי` (or `Add`).
- **Severity:** Low.
- **Type:** Wording.

#### UX-32 — "Backup" and "backup set" are used for the same thing
- **Screen:** status, restore and new backup, plus the server errors relayed to the customer window. Code only.
- **Actual:** the customer window calls a set a "backup" (`גיבוי`): `New backup`, `Change the backup`, the restore field label `Backup` (`:560`). Server messages the window shows through `L.Tr` call it a "backup set" (`סט`): `Maximum number of backup sets reached ({0}).` → `הגעת למספר הסטים המקסימלי ({0}).`, `The backup set does not exist.` → `הסט לא קיים.` In the Restore page the label `Backup` above a list of sets is also ambiguous next to `Restore point (date)`, which is also "a backup".
- **Severity:** Low.
- **Recommendation:** choose one customer word. Proposal: "backup" in the customer window, with server texts that reach the customer worded the same way ("You have reached the maximum number of backups ({0})."). The admin website can keep "backup set".
- **Type:** Wording.

#### UX-33 — Two names for the same code field
- **Code only:** sign-in dialog `Verification code (if enabled)` (`ClientForm.cs:219`); connect page `Code from the authenticator app (only if it is on)` (`:404`); security page `Two-step verification`.
- **Severity:** Low. **Recommendation:** one label, such as "Code from the authenticator app (only if two-step verification is on)". **Type:** Wording.

#### UX-34 — The server address example differs between the hint and the error
- **Code only:** hint `For example backup.company.com:8443 — your IT company gives it.` (`:385`); error `Write the server address, e.g. https://backup.company.com:8443` (`:420`).
- **Severity:** Low. **Recommendation:** the same example in both, with or without `https://`. **Type:** Wording.

#### UX-35 — Certificate prompt: the question is written as a statement, and "No" gives no explanation
- **Screen:** connect page, a server with its own certificate. Code only: `ClientForm.cs:442-443`.
- **Actual:** a Yes/No box containing `The server uses its own certificate. Make sure this fingerprint is the one your provider gave you:` + fingerprint + `I trust this server`. If the customer chooses No, they return to the form with no message.
- **Problem:** "I trust this server" with Yes/No buttons is not a question. A customer who never received a fingerprint has no next step.
- **Severity:** Medium.
- **Recommendation:** "Do you trust this server?", plus "If you did not receive this fingerprint, choose No and call <company, phone>." After No, show that line on the form.
- **Type:** Wording.

#### UX-36 — A balloon every time the window is closed
- **Code only:** `ClientForm.cs:97`. `The backup keeps running in the background.` appears on every X, not only the first time.
- **Severity:** Low. **Recommendation:** show it the first time only (store a flag per user). **Type:** Wording/behaviour.

#### UX-37 — "Call" in English means a support ticket
- **Code only:** Help page `My calls`, `Call`, `The call was sent to {0}`, `No calls yet.` (`:729-738`).
- **Problem:** in English a "call" is a phone call. Hebrew `קריאה` is correct.
- **Severity:** Low. **Recommendation:** English "request" / "My requests". **Type:** Wording (English only).

#### UX-38 — Protected-state subtitle claims "AI is watching every backup for ransomware"
- **Code only:** `ClientForm.cs:516` (`ה-AI שומר על כל גיבוי מפני כופרה`).
- **Note:** it is shown to every customer whose sets succeeded. The server's own text says sets "use the fixed limits until they have 7 backups". The owner should confirm that the claim holds for every set from the first run.
- **Severity:** Low. **Type:** Wording (owner).

#### UX-39 — Sign-in dialog and Security page freeze the window while waiting
- **Code only:** `ClientForm.cs:229` (login call inside the button click, on the UI thread) and `:699`. The local API timeout is 600 s (`:34`).
- **Problem:** if the service is slow, Windows marks the window "Not responding".
- **Severity:** Medium. **Recommendation:** run the call in the background, like `Do()`, and show "Signing in…". **Type:** Behaviour (no flow change).

#### UX-40 — "Change the backup" dialog: buttons placed with fixed widths
- **Code only:** `ClientForm.cs:682` assumes Save is 160 px and Exit is 190 px, but `Btn()` sizes buttons to their text (`:314`).
- **Problem:** uneven gaps, and the buttons are not aligned to the right edge in English. With longer texts the buttons may overlap.
- **Severity:** Low. **Type:** Visual.

#### UX-41 — Activity details show bytes
- **Code only:** the detail text `New {0}, updated {1}, sent {2} bytes` (example in `SampleApi`, `:806`: `sent 18400000 bytes`). It is translated as `נשלחו {2} בתים`.
- **Severity:** Low. **Recommendation:** use readable sizes (17.5 MB). **Type:** Wording.

### Hebrew / RTL (texts)

#### UX-42 — Mixed forms of address, and three names for "your provider"
- **Code only:** in `src/Core/i18n/he.json`, among the 188 keys the two Windows screens use:
  - Plural or neutral (most texts): `כתבו את כתובת השרת…`, `הריצו את ההתקנה שוב…`, `לחצו "גבה עכשיו"`, `היכנסו…`.
  - Masculine singular: `Write a subject.` → `כתוב נושא.`; `Contact {0}.` → `פנה אל {0}.`; `Or type the key:` → `או הקלד את המפתח:`; `Type the code the app shows now:` → `הקלד את הקוד…`; `Keep these one-time backup codes…` → `שמור את קודי הגיבוי…`; `Your IT provider adds new backups — contact {0}.` → `…— פנה אל {0}.`; `Protect restores…` → `הגן על שחזורים…`.
  - "Your provider" appears as `ספק השירות שלך`, `ספק ה-IT שלך`, `הספק שלכם` and `חברת המחשוב שלכם`.
- **Problem:** inconsistent and gendered. It reads as if several people translated the product.
- **Severity:** Low.
- **Recommendation:** use the plural form everywhere (as most of the product already does). Choose one term, for example `חברת המחשוב שלכם` (matching "your IT company").
- **Type:** Translation only.

#### UX-43 — Translation coverage of the two Windows screens
- **Code only:** every literal `T("…")` text in `SetupForm.cs` and `ClientForm.cs` was checked against `he.json` (script run locally, not committed). **All of them are present.**
  - Missing texts the customer can still see in English on a Hebrew screen: `The backup service on this computer does not answer.` (UX-20), both Setup.exe messages (UX-10), and the progress or removal lines `Not added to Programs and Features: {0}`, `Shortcut not created: {0}`, `Settings not removed: {0}`, `Some files are in use and stay until a restart: {0}` (`Setup.cs:92`, `:147`, `:267`, `:275`).
  - Raw sc.exe output (UX-11) and exception messages (UX-12) are never translatable.
- **Severity:** Medium (only the few texts above; the rest is complete).
- **Type:** Translation only.

---

## Comparison with docs/PRODUCT-UX-SPEC.md (awaiting the owner)

| Spec | Today (code) | Finding |
|---|---|---|
| §1: installer Welcome → License → **Ready** → Installing → Finish; Welcome shows the version | No Ready page; Install on the License page; no version | UX-05, UX-07 |
| §1: License with "I accept / I do not accept" radios, default "do not accept" | A single checkbox `I accept the terms of the license agreement`, Install disabled until it is ticked (`SetupForm.cs:253`, `:301`) | Equivalent in effect; the radios are a style choice |
| §1: Finish only after checking that the service runs; failure gives the reason, the log and the phone | Finish after `Setup.Run`, with no check that the service is running (sc.exe output is only shown); failure gives the reason and the phone, no log | UX-12, UX-17 |
| §1 Esc = Cancel with confirmation | No CancelButton on the setup form; no confirmation | UX-13 |
| §2 first run: account → what to back up (preset) → summary and back up now | Account → message `This computer is connected. Now choose what to back up.` → the New backup page (empty tree) | Matches spec §10 row 1 "today"; owner's decision |
| §4 restore wizard with an overwrite confirmation and a real result | One page, no confirmation, result only in Recent activity | UX-28, UX-29 |
| §6 states: loading, empty, error with "try again", disconnected | Loading: tree node `Loading files…`; error states sometimes silent (`:740`, `:853`) or misleading (UX-20, UX-26) | UX-20, UX-26 |
| §8 RTL: buttons mirrored, everything in Hebrew | Layout is mirrored by hand. Gaps: UX-10, UX-24, UX-25, UX-42, UX-43. **No Hebrew screenshot exists yet** | Robot should capture the Hebrew pages |
| §9 icons and states always with a word | Status cards show a word and a colour (good) | — |

## What is already good (seen in the code)

- Remove asks for confirmation with the Question icon and **No** as the default, and is right-to-left aware (`SetupForm.cs:349`). It says that the backups on the server are kept (`:244`, `:274`).
- The installer's failure page always gives a next step and the support contact (`:269`).
- Connect-page errors appear on the page, next to the button, not in a pop-up (`ClientForm.cs:409`).
- Server addresses are kept left-to-right inside Hebrew (`:382`, `Stacker.Box(ltr)`).
- Fields are named for screen readers by the caption above them (`Stacker.Add`, `:882`).

## Summary

| ID | Screen | Severity | Evidence | Type |
|---|---|---|---|---|
| UX-01 | Setup.exe, damaged download | High | screenshot | Wording (re-check fix) |
| UX-02 | Setup.exe, "Unzip" message | Medium | screenshot | Wording |
| UX-03 | Installer icon | Medium | screenshot | Visual |
| UX-04 | Installer title/header | Low | screenshot | Wording |
| UX-05 | Welcome: version, empty page | Low | screenshot | Wording |
| UX-06 | Product name "Server" | Low | screenshot | Wording (owner) |
| UX-07 | No Ready page | Low | screenshot + code | Flow (owner) |
| UX-10 | Setup.exe messages English only | Medium | code | Translation |
| UX-11 | Raw sc.exe output in progress | Medium | code | Wording |
| UX-12 | Raw exception on failure page | Medium | code | Wording |
| UX-13 | Cancel / X during install | Low | code | Wording (+ flow) |
| UX-14 | Remove checkbox indent | Low | code | Visual |
| UX-15 | Product names | Low | code | Wording |
| UX-17 | "Installed" without the service running | High | code | Behaviour/wording |
| UX-16 | English by default | Low | code + screenshot | Owner decision |
| UX-20 | Local service down → wrong cause, "no backups" | High | code | Wording + state |
| UX-21 | Tray "Close the window" quits | Medium | code | Wording |
| UX-22 | Tray "Back up now" N dialogs | Medium | code | Feedback |
| UX-23 | Errors with Warning icon | Low | code | Icon |
| UX-24 | 2FA setup in InputBox | High | code | Flow (owner) |
| UX-25 | 2FA off confirmation | Low | code | Icon/RTL |
| UX-26 | Security page shows "off" on error | Medium | code | State |
| UX-27 | "No access" node tickable → crash | Medium | code (needs test) | Behaviour |
| UX-28 | Restore without a point: no-op | Medium | code | Feedback |
| UX-29 | Restore: no overwrite confirmation, no result; "tab" | Medium | code | Flow (owner) + wording |
| UX-30 | New backup: SQL/System State without settings | Medium | code | Wording / flow (owner) |
| UX-31 | Button "New backup" | Low | code | Wording |
| UX-32 | "backup" vs "backup set" | Low | code | Wording |
| UX-33 | Two names for the code field | Low | code | Wording |
| UX-34 | Server address examples | Low | code | Wording |
| UX-35 | Certificate prompt | Medium | code | Wording |
| UX-36 | Balloon on every close | Low | code | Behaviour |
| UX-37 | "Call" in English | Low | code | Wording |
| UX-38 | "AI is watching…" claim | Low | code | Wording (owner) |
| UX-39 | UI freezes on sign-in / security | Medium | code | Behaviour |
| UX-40 | Edit dialog button placement | Low | code | Visual |
| UX-41 | Bytes in activity details | Low | code | Wording |
| UX-42 | Hebrew address forms / provider term | Low | he.json | Translation |
| UX-43 | Missing he.json texts | Medium | he.json + code | Translation |
