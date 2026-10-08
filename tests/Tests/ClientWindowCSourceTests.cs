using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// The customer's window, alternative C ("Hybrid", chosen by the owner) — what the two windows' SOURCES must say:
    /// the native Windows window (src/Agent/ClientForm.cs, what Program.cs shows on Windows) and its web fallback
    /// (src/Agent/client.html). The rules themselves (states, words, colours, layout) are pinned in ClientWindowCTests
    /// against ClientView, which ClientForm draws; here: that both windows use them, in the IA's order, in English and
    /// Hebrew, with no claim the product does not make, and that nothing the customer could do before disappears.
    /// </summary>
    public class ClientWindowCSourceTests
    {
        static string Repo { get { return PilotAdminUiComponentTests.RepoRoot(); } }
        static string Src(params string[] p) { return File.ReadAllText(Path.Combine(new[] { Repo, "src" }.Concat(p).ToArray()), Encoding.UTF8); }
        static string Form { get { return Src("Agent", "ClientForm.cs"); } }
        static string Html { get { return Src("Agent", "client.html"); } }
        static string ViewOrEmpty { get { var f = Path.Combine(Repo, "src", "Agent", "ClientView.cs"); return File.Exists(f) ? File.ReadAllText(f, Encoding.UTF8) : ""; } }

        static Dictionary<string, string> Hebrew()
        {
            var j = Json.Obj(Json.Parse(File.ReadAllText(Path.Combine(Repo, "src", "Core", "i18n", "he.json"), Encoding.UTF8).TrimStart('\uFEFF')));
            return Json.Obj(j["strings"]).ToDictionary(kv => kv.Key, kv => kv.Value as string);
        }

        /// <summary>The English texts a C# window translates: T("…") and L.T(lang, "…"), and the constants handed to T.</summary>
        static List<string> CsTexts(string src)
        {
            var r = new List<string>();
            foreach (Match m in Regex.Matches(src, @"\bT\(\s*""((?:[^""\\]|\\.)*)""")) r.Add(Regex.Unescape(m.Groups[1].Value));
            foreach (Match m in Regex.Matches(src, @"L\.T\(\s*\w+\s*,\s*""((?:[^""\\]|\\.)*)""")) r.Add(Regex.Unescape(m.Groups[1].Value));
            foreach (Match m in Regex.Matches(src, @"const string \w+ = ""((?:[^""\\]|\\.)*)""")) r.Add(Regex.Unescape(m.Groups[1].Value));
            return r.Where(x => x.Length > 0).Distinct().ToList();
        }
        static List<string> JsTexts(string src)
        {
            return Regex.Matches(src, @"\bt\('((?:[^'\\]|\\.)*)'").Cast<Match>().Select(m => m.Groups[1].Value.Replace("\\'", "'")).Distinct().ToList();
        }

        /// <summary>IA §2/§3: the side menu, top to bottom, is Home · Backup sets · Restore · History · Settings (Restore before
        /// History, no Alerts page; "New backup" inside Backup sets, Security inside Settings). The native window builds its menu
        /// from ClientView.Nav only (the old five-entry menu is gone).</summary>
        [Fact]
        public void C_NativeWindow_MenuIsBuiltFromTheIaOrder()
        {
            var f = Form;
            Assert.Contains("ClientView.Nav", f);
            Assert.DoesNotContain("new[] { \"status\", \"Backup status\" }", f);
            Assert.DoesNotContain("new[] { \"new\", \"New backup\" }", f);
            Assert.DoesNotContain("new[] { \"security\", \"Security\" }", f);
        }

        [Fact]
        public void C_WebWindow_MenuIsTheIaOrder_NewBackupInsideBackupSets_SecurityInsideSettings()
        {
            var h = Html;
            var pages = Regex.Match(h, @"const PAGES = \[(.*?)\];").Groups[1].Value;
            var keys = Regex.Matches(pages, @"\['(\w+)',").Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
            Assert.Equal(new[] { "home", "sets", "restore", "history", "settings" }, keys);
            Assert.Contains("t('New backup')", h);            // a button inside Backup sets
            Assert.Contains("t('Two-step verification')", h); // inside Settings
            Assert.Contains("help", Regex.Match(h, @"id=""navfoot""[^>]*>|navHelp").Value + h.Substring(h.IndexOf("navHelp", StringComparison.Ordinal) < 0 ? 0 : h.IndexOf("navHelp", StringComparison.Ordinal)));
        }

        /// <summary>B5: both restore screens say what comes back and what does not, in the same words (and in Hebrew).</summary>
        [Fact]
        public void B5_BothRestoreScreensSayPermissionsAndAttributesAreNotRestored()
        {
            const string s = "Restored: the content and the modified time. Not restored: permissions and file attributes.";
            Assert.Contains("ClientView.RestoreKeeps", Form);
            Assert.Contains("\"" + s + "\"", ViewOrEmpty);
            Assert.Contains("t('" + s + "')", Html);
            var he = Hebrew();
            Assert.True(he.ContainsKey(s), "no Hebrew for the B5 sentence");
            Assert.Contains("הרשאות", he[s]);
        }

        /// <summary>B2 (client windows only): a run with files not backed up is "Partial" (amber) — never "Completed with
        /// warnings" / "Completed with errors" / "Partly protected".</summary>
        [Fact]
        public void B2_PartialIsNeverCalledCompletedInEitherWindow()
        {
            foreach (var bad in new[] { "Completed with warnings", "Completed with errors", "Partly protected" })
            {
                Assert.DoesNotContain("\"" + bad + "\"", Form);
                Assert.DoesNotContain("'" + bad + "'", Html);
            }
            Assert.Matches(@"BS_STOP_SUCCESS_WITH_ERROR: \[t\('Partial'\), 'warn'\]", Html);
            Assert.Contains("lastComplete", Html);   // "Last complete backup" (clean runs) beside "Last backup attempt"
            Assert.Contains("t('Last complete backup')", Html); Assert.Contains("t('Last backup attempt')", Html);
        }

        /// <summary>Owner Q4: no wording that the product watches for or prevents ransomware — in either window, English or
        /// Hebrew. (The server's mass-change check exists, but the window does not present it; see the report.)</summary>
        [Fact]
        public void Q4_NoRansomwareOrAiWatchingClaim_InEitherWindow_EnglishOrHebrew()
        {
            var he = Hebrew();
            var claim = new Regex(@"ransom|AI is watching|\bAI\b|watching every backup|כופר|בינה מלאכותית|\bAI\b", RegexOptions.IgnoreCase);
            var texts = CsTexts(Form).Concat(CsTexts(ViewOrEmpty)).Concat(JsTexts(Html)).Distinct().ToList();
            var bad = new List<string>();
            foreach (var k in texts)
            {
                if (claim.IsMatch(k)) bad.Add("en: " + k);
                string v; if (he.TryGetValue(k, out v) && v != null && claim.IsMatch(v)) bad.Add("he: " + v);
            }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
            Assert.DoesNotContain("ai-watch", Html);
        }

        /// <summary>Every text the native window shows (ClientForm and ClientView) has its Hebrew translation, in Hebrew
        /// letters, with the same {n} placeholders (the web window's texts are checked by UI07).</summary>
        [Fact]
        public void C_EveryTextOfTheNativeWindow_HasItsHebrew()
        {
            var he = Hebrew();
            var problems = new List<string>();
            var hebrew = new Regex("[א-ת]");
            foreach (var k in CsTexts(Form).Concat(CsTexts(ViewOrEmpty)).Distinct())
            {
                string v;
                if (!he.TryGetValue(k, out v) || string.IsNullOrWhiteSpace(v)) { problems.Add("no Hebrew: \"" + k + "\""); continue; }
                var a = Regex.Matches(k, @"\{\d\}").Cast<Match>().Select(m => m.Value).OrderBy(x => x).ToArray();
                var b = Regex.Matches(v, @"\{\d\}").Cast<Match>().Select(m => m.Value).OrderBy(x => x).ToArray();
                if (!a.SequenceEqual(b)) problems.Add("placeholders: \"" + k + "\" → \"" + v + "\"");
                if (Regex.IsMatch(k, "[a-z]{4,}") && !hebrew.IsMatch(v)) problems.Add("not Hebrew: \"" + k + "\" → \"" + v + "\"");
            }
            Assert.True(problems.Count == 0, problems.Count + " texts:\n" + string.Join("\n", problems));
        }

        /// <summary>Nothing a customer can do today disappears: every local-API operation the old native window called is
        /// still called by the new one, and every operation of the old web window by the new web window; the window's own
        /// features (tray, update, language, sign-in screen, agreement, folder picker, replace files, service calls) stay.</summary>
        [Fact]
        public void C_EveryActionOfTheOldWindowsIsStillThere()
        {
            var f = Form; var h = Html;
            foreach (var op in new[] { "state", "jobs", "backup", "points", "files", "restore", "addset", "editset", "dirs", "security", "totp-enable", "totp-confirm", "totp-disable", "help", "update", "login", "server-check", "connect" })
                Assert.True(f.Contains("api.Call(\"" + op + "\""), "the native window no longer calls " + op);
            foreach (var op in new[] { "state", "jobs", "backup", "points", "files", "restore", "addset", "editset", "dirs", "security", "totp-enable", "totp-confirm", "totp-disable", "help" })
                Assert.True(h.Contains("api('" + op + "'"), "the web window no longer calls " + op);
            Assert.Contains("/api/login", h);
            foreach (var text in new[] { "Check for updates", "Close the window", "Open", "Powered by {0}", "Replace existing files", "Change the backup", "New backup", "Switch off", "Turn on", "Send", "My calls", "I already have an account", "I am a new customer — open an account", "I have read and accept the agreement", "Use a different server address", "The backup keeps running in the background." })
                Assert.True(f.Contains("T(\"" + text + "\"") || f.Contains("T(\" " + text + "\")"), "the native window lost: " + text);
            Assert.Contains("FolderPicker", f); Assert.Contains("NotifyIcon", f); Assert.Contains("FolderBrowserDialog", f);
            Assert.Contains("Return directly to the cloud", h);   // M365 / Google back to the cloud
        }

        /// <summary>Owner Q10: the minimum window size stays 860×560 (logical pixels); 1280×800 is the design size, not a minimum.</summary>
        [Fact]
        public void Q10_MinimumWindowSizeStays860x560()
        {
            var f = Form;
            Assert.DoesNotContain("1024, 700", f);
            Assert.True(f.Contains("MinimumSize = new Size(860, 560)") || f.Contains("ClientView.MinWidth"), "the minimum size changed");
            Assert.Matches(@"MinWidth = 860, MinHeight = 560", ViewOrEmpty);
        }

        /// <summary>B2 data: the agent notes every run for the window (beside last-attempt.txt, which stays as it is).</summary>
        [Fact]
        public void B2_TheAgentNotesEveryRunForTheWindow_LastAttemptUnchanged()
        {
            var a = Src("Agent", "AgentApp.cs");
            Assert.Contains("RunNotes.Record(", a);
            Assert.Contains("\"last-attempt.txt\"), RunId.From(SystemClock.UtcNow) + \"\\t\" + run.Result);", a);
            var ui = Src("Agent", "ClientUi.cs");
            foreach (var field in new[] { "\"lastComplete\"", "\"missed\"", "\"missedCount\"", "\"runs\"", "\"lastTest\"", "case \"runlog\"" }) Assert.Contains(field, ui);
        }

        /// <summary>Owner Q3 / Q5 / Q6 (decided 08.10): no manual update in the pilot (hidden under the pilot switch, the
        /// tray included); an action the IT company locked is shown DISABLED with the short reason "This setting is managed by
        /// your service provider" (Hebrew given by the owner); white-label = logo + product / provider name only - the window
        /// takes no colours from the brand.</summary>
        [Fact]
        public void Q3_Q5_Q6_NoManualUpdateInThePilot_LockedIsDisabledWithTheReason_NoBrandColours()
        {
            var f = Form;
            Assert.Contains("ClientView.ShowsManualUpdate(state)", f);
            Assert.True(Regex.Matches(f, @"ClientView\.ShowsManualUpdate\(state\)").Count >= 3, "the update must be hidden in the menu of the tray, Settings and the frame");
            const string locked = "This setting is managed by your service provider";
            Assert.Contains("ClientView.Locked", f);
            Assert.Contains("\"" + locked + "\"", ViewOrEmpty);
            Assert.Equal("הגדרה זו מנוהלת על ידי ספק השירות", Hebrew()[locked]);
            Assert.Contains("t('" + locked + "')", Html);
            Assert.DoesNotContain("state[\"color\"]", f); Assert.DoesNotContain("state[\"accent\"]", f);
            Assert.DoesNotContain("ST.color", Html); Assert.DoesNotContain("ST.accent", Html);
        }

        /// <summary>Owner decision A7 (the engine's API, merged from fix-sched): a new backup shows the key-recovery choice from
        /// "keyrecovery" (both options' texts in full, "keep" preselected, disabled with offText when the IT company keeps no
        /// copies) and sends keyRecovery; what is really kept (the reply) is shown after creation and in the set's details;
        /// a sign-in that leaves keysMissing &gt; 0 tells the customer to contact the IT company - in both windows.</summary>
        [Fact]
        public void A7_KeyRecoveryChoice_KeptCopyShown_MissingKeysAfterSignIn_BothWindows()
        {
            var f = Form; var h = Html;
            Assert.Contains("api.Call(\"keyrecovery\"", f);
            foreach (var k in new[] { "\"keepLabel\"", "\"keepText\"", "\"noneLabel\"", "\"noneText\"", "\"offText\"", "\"allowed\"", ".Set(\"keyRecovery\"", "[\"keyRecovery\"]", "\"keysMissing\"" }) Assert.Contains(k, f);
            Assert.Contains("api('keyrecovery'", h);
            foreach (var k in new[] { "kr.keepText", "kr.noneText", "kr.offText", "keyRecovery:", "r.keyRecovery", "s.keyRecovery", "keysMissing" }) Assert.Contains(k, h);
            foreach (var k in new[] { "A recovery copy of the key is kept by {0}.", "No recovery copy of the key is kept — keep the key in a safe place.", "Recovery copy of the key kept: {0}", "A backup on this computer still needs its encryption key — contact {0}." })
            { Assert.Contains("\"" + k + "\"", f); Assert.Contains("'" + k + "'", h); }
        }

        /// <summary>UI-Q1 / UI-Q2 (engine merged 082a703): both windows check the destination before a restore
        /// ("restorecheck"), ask Replace / Skip / Cancel when files exist (existing=overwrite|skip|cancel), restore to the
        /// original location (own engine), match the error code EXISTS, and show the job's verification from its fields.</summary>
        [Fact]
        public void Q1_Q2_BothWindows_CheckTheDestination_AskForExistingFiles_ShowTheVerification()
        {
            var f = Form; var h = Html;
            Assert.Contains("api.Call(\"restorecheck\"", f);
            foreach (var k in new[] { "\"EXISTS\"", ".Set(\"existing\"", ".Set(\"location\", \"original\")", "ClientView.Existing(", "ClientView.RestoreResult(", "ClientView.OriginalLocationAvailable(" }) Assert.Contains(k, f);
            Assert.Contains("api('restorecheck'", h);
            foreach (var k in new[] { "'EXISTS'", "existing:", "location: 'original'", "verifiedSha256", "mismatch", "t('Replace existing')", "t('Skip existing')", "t('Verified (SHA-256): {0} files'", "t('Checked (chunks and size): {0} files'" }) Assert.Contains(k, h);
            Assert.DoesNotContain("disabled: true, style: 'width:auto' }), ' ', t('To the original location')", h);
        }
    }
}
