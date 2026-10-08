using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// The customer's window, alternative C ("Hybrid", chosen by the owner) - its rules, without Windows Forms (ClientView,
    /// which ClientForm draws and client.html follows): the menu in the IA's order; the protection bar's state, words and
    /// colours for every result (B2: Partial is amber, "Last complete backup" counts only clean runs, the last attempt is
    /// shown apart); the restore in 3 steps with its fixed bar inside the window at 860x560 and 1280x800, at 100/125/150 %
    /// (UX-1, owner Q10) and the B5 sentence; nothing a customer could do before disappears; and the notes of every run the
    /// agent keeps for the window (RunNotes) and serves on the local API (ClientUi).
    /// </summary>
    [Collection("ClientDir")]
    public class ClientWindowCTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "obclientc-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public ClientWindowCTests() { Directory.CreateDirectory(root); }
        public void Dispose() { SystemClock.Use(null); try { Directory.Delete(root, true); } catch (Exception) { } }

        static readonly DateTime D1 = new DateTime(2026, 10, 5, 19, 0, 0, DateTimeKind.Utc), D2 = D1.AddDays(1), D3 = D1.AddDays(2), D4 = D1.AddDays(3);
        static string Id(DateTime d) { return RunId.From(d); }

        static Msg SetMsg(string id, string name, string result, DateTime? last, DateTime? lastComplete, int missed = 0, string blocked = null)
        {
            var m = new Msg().Set("id", id).Set("name", name).Set("mine", 1).Set("hour", "21:30").Set("sources", @"D:\Users").Set("src", @"D:\Users")
                .Set("result", result).Set("last", last == null ? "" : Id(last.Value)).Set("lastComplete", lastComplete == null ? "" : Id(lastComplete.Value)).Set("missedCount", missed).Set("blocked", blocked);
            for (int i = 0; i < missed; i++) m.Add("missed", new Msg().Set("p", @"D:\Users\dana\mail" + i + ".pst").Set("why", "The file is in use by another program"));
            return m;
        }
        static Msg State(params Msg[] sets) { var s = new Msg().Set("registered", 1).Set("company", "Acme IT"); foreach (var x in sets) s.Add("sets", x); return s; }
        static readonly List<Msg> NoJobs = new List<Msg>();

        // ================================================================== the menu and what stays reachable

        [Fact]
        public void C_Menu_IsHome_BackupSets_Restore_History_Settings_AndEveryOldActionHasItsPage()
        {
            Assert.Equal(new[] { "home", "sets", "restore", "history", "settings" }, ClientView.Nav.Select(n => n[0]).ToArray());
            Assert.Equal(new[] { "Home", "Backups", "Restore", "History", "Settings" }, ClientView.Nav.Select(n => n[1]).ToArray());
            Assert.Equal(new[] { "בית", "גיבויים", "שחזור", "היסטוריה", "הגדרות" }, ClientView.Nav.Select(n => L.T("he", n[1])).ToArray());
            var nav = ClientView.Nav.Select(n => n[0]).ToList();
            // every operation of the old window has a page, and that page is in the menu or one click from a menu page
            var old = new[] { "state", "jobs", "backup", "points", "files", "restore", "addset", "editset", "dirs", "security", "totp-enable", "totp-confirm", "totp-disable", "help", "update", "login", "server-check", "connect", "language" };
            foreach (var op in old)
            {
                var pages = ClientView.Actions.Where(a => a[0] == op).Select(a => a[1]).ToList();
                Assert.True(pages.Count > 0, op + " has no page");
                foreach (var p in pages)
                    Assert.True(p == "any" || p == "connect" || nav.Contains(p) || (ClientView.ReachedFrom.ContainsKey(p) && nav.Contains(ClientView.ReachedFrom[p])), op + " → " + p + " is not reachable");
            }
            Assert.Equal("help", ClientView.HelpPage);
        }

        // ================================================================== B2: one result → state, words, colour, two dates

        public static IEnumerable<object[]> Results()
        {
            yield return new object[] { "BS_STOP_SUCCESS", RunKind.Complete, "Complete", "הושלם", "#11764A" };
            yield return new object[] { "BS_STOP_SUCCESS_WITH_WARNING", RunKind.CompleteWithWarnings, "Complete — with warnings", "הושלם — עם אזהרות", "#11764A" };
            yield return new object[] { "BS_STOP_SUCCESS_WITH_ERROR", RunKind.Partial, "Partial", "חלקי", "#8A4B00" };
            yield return new object[] { "BS_STOP_BY_SYSTEM_ERROR", RunKind.Failed, "Failed", "נכשל", "#B42318" };
            yield return new object[] { "BS_STOP_QUOTA_EXCEEDED", RunKind.Failed, "Failed — the quota is full", "נכשל — המכסה מלאה", "#B42318" };
            yield return new object[] { "BS_STOP_BY_PRE_COMMAND", RunKind.Failed, "Failed", "נכשל", "#B42318" };
            yield return new object[] { "NETWORK", RunKind.Failed, "Failed", "נכשל", "#B42318" };
            yield return new object[] { "BS_STOP_BY_USER", RunKind.Stopped, "Stopped", "נעצר", "#5B6472" };
            yield return new object[] { "", RunKind.Never, "Not backed up yet", "עוד לא גובה", "#5B6472" };
        }

        [Theory]
        [MemberData(nameof(Results))]
        public void B2_EveryResult_HasItsKind_Word_AndColour_PartialNeverComplete(string result, RunKind kind, string en, string he, string hex)
        {
            Assert.Equal(kind, ClientView.Classify(result));
            Assert.Equal(en, ClientView.Word("en", kind, result));
            Assert.Equal(he, ClientView.Word("he", kind, result));
            Assert.Equal(hex, ClientView.Hex(ClientView.ToneOf(kind)));
            Assert.Equal(result == "BS_STOP_SUCCESS" || result == "BS_STOP_SUCCESS_WITH_WARNING", ClientView.CountsAsComplete(kind));
            Assert.Equal(ClientView.CountsAsComplete(kind), RunNotes.Complete(result));   // the agent's note and the window agree
        }

        [Fact]
        public void B2_Home_Complete_IsProtected_Green_LastCompleteIsTheRun()
        {
            var h = ClientView.Home("en", State(SetMsg("1", "Office", "BS_STOP_SUCCESS", D3, D3)), NoJobs);
            Assert.Equal(HomeState.Protected, h.State); Assert.Equal(Tone.Ok, h.Tone);
            Assert.Equal("This computer is protected", h.Title);
            Assert.Equal("המחשב הזה מוגן", ClientView.Home("he", State(SetMsg("1", "Office", "BS_STOP_SUCCESS", D3, D3)), NoJobs).Title);
            Assert.Equal("Back up now", h.Primary); Assert.Equal("backup-all", h.PrimaryAction); Assert.Equal("Restore files", h.Secondary);
            Assert.Equal("Last complete backup", h.Facts[0].Label); Assert.Equal(ClientView.When("en", D3), h.Facts[0].Value); Assert.Equal(Tone.Ok, h.Facts[0].Tone);
            Assert.Equal("Last backup attempt", h.Facts[1].Label); Assert.Equal(ClientView.When("en", D3), h.Facts[1].Value); Assert.Equal("Office · Complete", h.Facts[1].Sub);
            Assert.Empty(h.Problems);
            Assert.Equal(ClientView.When("en", D3), ClientView.When("en", h.Sets[0].LastComplete));
        }

        [Fact]
        public void B2_Home_CompleteWithWarnings_IsStillProtected_Green_WithItsNote()
        {
            var h = ClientView.Home("en", State(SetMsg("1", "Office", "BS_STOP_SUCCESS_WITH_WARNING", D3, D3)), NoJobs);
            Assert.Equal(HomeState.Protected, h.State); Assert.Equal(Tone.Ok, h.Tone);
            Assert.Equal("Complete — with warnings", h.Sets[0].Chip);
            Assert.Equal(ClientView.When("en", D3), h.Facts[0].Value);
        }

        /// <summary>B2 core: a partial run is amber "Attention needed"; the set's chip counts the files not backed up; "Last
        /// complete backup" stays on the clean run before it; "Last backup attempt" is the partial run, amber; the card "What
        /// was not backed up and why" has the set; the main action is "Back up again now".</summary>
        [Fact]
        public void B2_Home_Partial_IsAmber_LastCompleteStaysOnTheCleanRun_LastAttemptIsThePartialRun()
        {
            foreach (var lang in new[] { "en", "he" })
            {
                var h = ClientView.Home(lang, State(SetMsg("1", "User folders", "BS_STOP_SUCCESS_WITH_ERROR", D4, D3, 2)), NoJobs);
                Assert.Equal(HomeState.Partial, h.State); Assert.Equal(Tone.Warn, h.Tone);
                Assert.Equal("#8A4B00", ClientView.Hex(h.Tone));
                Assert.Equal(L.T(lang, "Attention needed"), h.Title);
                Assert.Equal(L.T(lang, "{0} files were not backed up · everything else was backed up", 2), h.Sub);
                Assert.Equal(L.T(lang, "Back up again now"), h.Primary);
                Assert.Equal(ClientView.When(lang, D3), h.Facts[0].Value);      // the clean run, not the partial one
                Assert.Equal(ClientView.When(lang, D4), h.Facts[1].Value);      // the partial attempt, shown apart
                Assert.Equal(Tone.Warn, h.Facts[1].Tone);
                Assert.Equal("User folders · " + L.T(lang, "Partial"), h.Facts[1].Sub);
                Assert.Single(h.Problems); Assert.Equal(2, h.Problems[0].Missed.Count);
                Assert.Equal(L.T(lang, "Partial · {0} files not backed up", 2), h.Sets[0].Chip);
                Assert.Equal(RunKind.Partial, h.Sets[0].Kind);
            }
            Assert.Equal("חלקי · 2 קבצים לא גובו", ClientView.Home("he", State(SetMsg("1", "U", "BS_STOP_SUCCESS_WITH_ERROR", D4, D3, 2)), NoJobs).Sets[0].Chip);
        }

        [Fact]
        public void B2_Home_Failed_IsRed_SaysUpToWhenFilesAreProtected_QuotaSendsToTheProvider()
        {
            var h = ClientView.Home("en", State(SetMsg("1", "User folders", "BS_STOP_BY_SYSTEM_ERROR", D4, D2)), NoJobs);
            Assert.Equal(HomeState.Failed, h.State); Assert.Equal(Tone.Bad, h.Tone);
            Assert.Equal("The backup of \"User folders\" failed", h.Title);
            Assert.Equal("The files are protected up to " + ClientView.When("en", D2) + ". Changes since then are not protected yet.", h.Sub);
            Assert.Equal("Back up again now", h.Primary);
            Assert.Equal(ClientView.When("en", D2), h.Facts[0].Value); Assert.Equal(Tone.Bad, h.Facts[1].Tone);
            var q = ClientView.Home("en", State(SetMsg("1", "User folders", "BS_STOP_QUOTA_EXCEEDED", D4, D2)), NoJobs);
            Assert.Equal("contact", q.PrimaryAction); Assert.Equal("Contact Acme IT", q.Primary); Assert.StartsWith("The quota is full.", q.Sub);
        }

        [Fact]
        public void B2_Home_Stopped_IsGrey_TheNextRunAsUsual()
        {
            var h = ClientView.Home("en", State(SetMsg("1", "Office", "BS_STOP_BY_USER", D4, D3)), NoJobs);
            Assert.Equal(HomeState.Stopped, h.State); Assert.Equal(Tone.Neutral, h.Tone);
            Assert.Equal("The backup was stopped", h.Title);
            Assert.Contains("21:30", h.Sub);
            Assert.Equal(ClientView.When("en", D3), h.Facts[0].Value);
            Assert.Equal("Office · Stopped", h.Facts[1].Sub);
        }

        [Fact]
        public void B2_Home_Running_IsBlue_TheAttemptIsNow_LastCompleteUnchanged()
        {
            var jobs = new List<Msg> { new Msg().Set("id", "j").Set("kind", "backup").Set("set", "1").Set("state", "running").Set("started", RunId.UnixMs(D4)) };
            var h = ClientView.Home("en", State(SetMsg("1", "Office", "BS_STOP_SUCCESS", D3, D3)), jobs);
            Assert.Equal(HomeState.Running, h.State); Assert.Equal(Tone.Info, h.Tone);
            Assert.Equal("Backup running", h.Title); Assert.Contains("Office", h.Sub);
            Assert.Equal("Now", h.Facts[1].Value); Assert.Equal("Office · Running", h.Facts[1].Sub);
            Assert.Equal(ClientView.When("en", D3), h.Facts[0].Value);
            Assert.Equal(RunKind.Running, h.Sets[0].Kind); Assert.Equal("Running", h.Sets[0].Chip);
        }

        [Fact]
        public void B2_Home_NeverBackedUp_NoSets_Offline_BlockedInThePilot()
        {
            var never = ClientView.Home("en", State(SetMsg("1", "Office", "", null, null)), NoJobs);
            Assert.Equal(HomeState.NotYet, never.State); Assert.Equal("Waiting for the first backup", never.Title);
            Assert.Equal("Not yet", never.Facts[0].Value); Assert.Equal("Not yet", never.Facts[1].Value);
            var none = ClientView.Home("en", State(), NoJobs);
            Assert.Equal(HomeState.NoSets, none.State); Assert.Equal("This computer is not backed up yet", none.Title); Assert.Equal("add", none.PrimaryAction);
            var managed = ClientView.Home("en", State().Set("canAdd", 0), NoJobs);
            Assert.Equal("contact", managed.PrimaryAction); Assert.Contains("Your IT provider adds new backups", managed.Sub);
            var off = ClientView.Home("en", State(SetMsg("1", "Office", "BS_STOP_SUCCESS", D3, D3)).Set("offline", "unreachable"), NoJobs);
            Assert.Equal(HomeState.NoConnection, off.State); Assert.Equal("No connection to the backup server", off.Title); Assert.Equal("retry", off.PrimaryAction);
            // PILOT-010: a set that does not run in the pilot is grey and never counts
            var pilot = ClientView.Home("en", State(SetMsg("1", "Office", "BS_STOP_SUCCESS", D3, D3), SetMsg("2", "ERP", "BS_STOP_BY_SYSTEM_ERROR", D1, null, 0, "Not in the pilot")), NoJobs);
            Assert.Equal(HomeState.Protected, pilot.State);
            Assert.Equal(RunKind.Blocked, pilot.Sets[1].Kind); Assert.Equal(Tone.Neutral, pilot.Sets[1].Tone);
        }

        /// <summary>Owner Q7: Home shows each set with its OWN last complete backup; a computer-level time means "the last time
        /// ALL sets were complete" - never the earliest date.</summary>
        [Fact]
        public void Q7_ComputerLevel_IsTheLastTimeAllSetsWereComplete_EachSetKeepsItsOwn()
        {
            var a = SetMsg("1", "Office", "BS_STOP_SUCCESS", D3, D3);
            a.Add("runs", new Msg().Set("t", Id(D1)).Set("r", "BS_STOP_SUCCESS")).Add("runs", new Msg().Set("t", Id(D3)).Set("r", "BS_STOP_SUCCESS"));
            var b = SetMsg("2", "Users", "BS_STOP_SUCCESS_WITH_ERROR", D4, D2, 1);
            b.Add("runs", new Msg().Set("t", Id(D2)).Set("r", "BS_STOP_SUCCESS")).Add("runs", new Msg().Set("t", Id(D4)).Set("r", "BS_STOP_SUCCESS_WITH_ERROR").Set("e", 1));
            var h = ClientView.Home("en", State(a, b), NoJobs);
            Assert.Equal("Last time all backups were complete", h.Facts[0].Label);
            Assert.Equal(ClientView.When("en", D3), h.Facts[0].Value);   // at D3: Office complete (D3), Users complete (D2)
            Assert.Equal(new DateTime?[] { D3, D2 }, h.Sets.Select(s => s.LastComplete).ToArray());
            // one set: its own last complete backup
            Assert.Equal("Last complete backup", ClientView.Home("en", State(b), NoJobs).Facts[0].Label);
            // no run known where every set was complete: no date is made up
            var c = SetMsg("3", "New", "BS_STOP_SUCCESS_WITH_ERROR", D4, null, 1);
            Assert.Equal("—", ClientView.Home("en", State(a, c), NoJobs).Facts[0].Value);
        }

        /// <summary>Owner Q1: the restore test fact never says "verified" (the agent sends the result to the IT company and keeps
        /// only that it ran).</summary>
        [Fact]
        public void Q1_TheRestoreTestFact_NeverSaysVerified()
        {
            var s = SetMsg("1", "Office", "BS_STOP_SUCCESS", D3, D3).Set("lastTest", Id(D2));
            var h = ClientView.Home("en", State(s), NoJobs);
            Assert.Equal("Last restore test", h.Facts[2].Label); Assert.Equal(ClientView.When("en", D2), h.Facts[2].Value);
            foreach (var lang in new[] { "en", "he" })
                foreach (var f in ClientView.Home(lang, State(s), NoJobs).Facts) Assert.DoesNotMatch("(?i)verif|אומת|מאומת|SHA", f.Label + f.Value + f.Sub);
        }

        /// <summary>Owner Q8: the set's retention in plain words from the set's own settings; nothing when the window does not have it.</summary>
        [Fact]
        public void Q8_Retention_InPlainWords_OnlyWhenKnown()
        {
            Assert.Equal("Kept: 30 days, and 12 monthly versions", ClientView.Keep("en", new Msg().Set("retUnit", "DAYS").Set("retPeriod", 30).Set("retMonthly", 12)));
            Assert.Equal("Kept: the last 10 backups", ClientView.Keep("en", new Msg().Set("retUnit", "JOBS").Set("retPeriod", 10)));
            Assert.Equal("Kept: 14 days, and 7 daily versions, 4 weekly versions", ClientView.Keep("en", new Msg().Set("retUnit", "DAYS").Set("retPeriod", 14).Set("retDaily", 7).Set("retWeekly", 4)));
            Assert.Equal("נשמר: 30 ימים, ו־12 גרסאות חודשיות", ClientView.Keep("he", new Msg().Set("retUnit", "DAYS").Set("retPeriod", 30).Set("retMonthly", 12)));
            Assert.Equal("", ClientView.Keep("en", new Msg()));
        }

        [Fact]
        public void Q3_Q5_NoManualUpdateInThePilot_LockedReasonInBothLanguages()
        {
            Assert.False(ClientView.ShowsManualUpdate(new Msg().Set("pilot", 1)));
            Assert.True(ClientView.ShowsManualUpdate(new Msg()));
            Assert.Equal("This setting is managed by your service provider", L.T("en", ClientView.Locked));
            Assert.Equal("הגדרה זו מנוהלת על ידי ספק השירות", L.T("he", ClientView.Locked));
        }

        [Fact]
        public void B2_WhatToDo_PromisesNoAlertTheProductDoesNotSend()
        {
            var w = ClientView.WhatToDo("en", State());
            Assert.Equal("Close the programs that keep these files open and click \"Back up again now\". If it happens again, contact Acme IT.", w);
            Assert.DoesNotContain("alert", w);
        }

        [Fact]
        public void C_History_Filters_AllBackupsRestoresProblems()
        {
            var a = SetMsg("1", "Office", "BS_STOP_SUCCESS_WITH_ERROR", D4, D3, 1);
            a.Add("runs", new Msg().Set("t", Id(D3)).Set("r", "BS_STOP_SUCCESS")).Add("runs", new Msg().Set("t", Id(D4)).Set("r", "BS_STOP_SUCCESS_WITH_ERROR").Set("e", 1));
            var jobs = new List<Msg> { new Msg().Set("id", "r1").Set("kind", "restore").Set("set", "1").Set("state", "ok").Set("detail", "Restored 2, skipped 0, failed 0").Set("started", RunId.UnixMs(D4.AddHours(1))) };
            var st = State(a);
            Assert.Equal(3, ClientView.History("en", st, jobs, "all", null).Count);
            Assert.Equal("restore", ClientView.History("en", st, jobs, "all", null)[0].Kind);
            Assert.Equal(2, ClientView.History("en", st, jobs, "backups", null).Count);
            Assert.Single(ClientView.History("en", st, jobs, "restores", null));
            var p = Assert.Single(ClientView.History("en", st, jobs, "problems", null)); Assert.Equal(RunKind.Partial, p.Run);
            Assert.Empty(ClientView.History("en", st, jobs, "all", "9"));
        }

        // ================================================================== restore: 3 steps, B5, UX-1 / Q10 layout

        [Fact]
        public void B5_UX1_Restore_IsThreeSteps_SaysWhatIsNotRestored_ButtonsSayWhatTheyDo()
        {
            Assert.Equal(new[] { "When and what", "Where and check", "Result" }, ClientView.RestoreSteps);
            Assert.Equal(new[] { "מתי ומה", "לאן ובדיקה", "תוצאה" }, ClientView.RestoreSteps.Select(s => L.T("he", s)).ToArray());
            Assert.Equal("Restored: the content and the modified time. Not restored: permissions and file attributes.", ClientView.RestoreKeeps);
            Assert.Equal("חוזרים: התוכן ותאריך השינוי. לא חוזרים: הרשאות ומאפייני קובץ (Attributes).", L.T("he", ClientView.RestoreKeeps));
            Assert.Equal("Restore 2 selected items", ClientView.RestoreBar("en", 2, 2)[0]);
            Assert.Equal("Restore everything", ClientView.RestoreBar("en", 2, 0)[0]);
            Assert.Equal(new[] { "Next: where to restore", "Cancel" }, ClientView.RestoreBar("en", 1, 0).Take(2).ToArray());
            Assert.Equal(new[] { "Open the folder", "Back to Home", "Restore more" }, ClientView.RestoreBar("en", 3, 0));
            Assert.DoesNotMatch("(?i)verif", string.Join(" ", ClientView.RestoreBar("en", 3, 0)));   // Q1
            // Q2: the original location is offered but disabled with its reason until the local API restores there
            Assert.False(ClientView.OriginalLocationAvailable);
            Assert.False(string.IsNullOrEmpty(L.T("he", ClientView.OriginalLocationWhy)));
        }

        public static IEnumerable<object[]> Sizes()
        {
            // the client area of a window of 1280x800 and of the minimum 860x560, at 100 / 125 / 150 % (owner Q10)
            foreach (var s in new[] { 1.0, 1.25, 1.5 })
                foreach (var wh in new[] { new[] { 1280, 800 }, new[] { 860, 560 } })
                    foreach (var rtl in new[] { false, true })
                        yield return new object[] { (int)Math.Round((wh[0] - 16) * s), (int)Math.Round((wh[1] - 39) * s), s, rtl };
        }

        [Theory]
        [MemberData(nameof(Sizes))]
        public void UX1_Q10_TheRestoreBarAndTheHomeActions_StayInsideTheWindow_AtEverySizeAndScale(int w, int h, double scale, bool rtl)
        {
            foreach (var step in new[] { 1, 2, 3 })
            {
                int third = step == 3 ? (int)(150 * scale) : 0;
                var f = ClientView.Frame(w, h, scale, rtl, true, (int)(220 * scale), (int)(140 * scale), third);
                Assert.True(f.Bar.Inside(f.Client), "bar " + f.Bar);
                Assert.Equal(h, f.Bar.Bottom);
                Assert.Equal(f.Page.Bottom, f.Bar.Y);                              // the page scrolls above the bar, never under it
                foreach (var b in new[] { f.Primary, f.Secondary, f.Tertiary }.Where(x => x != null))
                {
                    Assert.True(b.Inside(f.Bar), "button " + b + " outside the bar " + f.Bar);
                    Assert.False(b.Overlaps(f.Nav), "button " + b + " under the menu");
                    Assert.True(b.H >= (int)(32 * scale) && b.W >= (int)(80 * scale), "button too small " + b);
                }
                Assert.False(f.Primary.Overlaps(f.Secondary));
                if (f.Tertiary != null) { Assert.False(f.Tertiary.Overlaps(f.Primary)); Assert.False(f.Tertiary.Overlaps(f.Secondary)); }
                // the primary button is on the reading end of the bar
                Assert.True(rtl ? f.Primary.X < f.Secondary.X : f.Primary.X > f.Secondary.X);
            }
            var home = ClientView.Frame(w, h, scale, rtl, false);
            Assert.Null(home.Bar); Assert.Equal(h, home.Page.H);
            Assert.Equal(w < (int)(1100 * scale), home.NavCollapsed);
            bool below;
            var hb = ClientView.HeroButtons(home.Page.W - (int)(64 * scale), scale, rtl, (int)(200 * scale), (int)(170 * scale), out below);
            var page = new Box(0, 0, home.Page.W - (int)(64 * scale), (int)(400 * scale));
            Assert.True(hb[0].Inside(page) && hb[1].Inside(page), hb[0] + " / " + hb[1]);
            Assert.False(hb[0].Overlaps(hb[1]));
        }

        [Fact]
        public void Q10_MinimumIs860x560_DesignSizeIs1280x800()
        {
            Assert.Equal(860, ClientView.MinWidth); Assert.Equal(560, ClientView.MinHeight);
        }

        [Fact]
        public void C_Restore_DayStrip_GroupsPointsByDay_NewestFirst()
        {
            var pts = new[] { D1, D1.AddHours(2), D3, D2 }.Select(d => new Msg().Set("id", Id(d)).Set("time", Id(d))).ToList();
            var days = ClientView.Days(pts);
            Assert.Equal(new[] { D3, D2, D1 }.Select(d => d.ToLocalTime().Date).ToArray(), days.Select(d => d.Key).ToArray());
            Assert.Equal(2, days[2].Value.Count);
            Assert.Equal(Id(D1.AddHours(2)), days[2].Value[0]["id"]);
        }

        // ================================================================== the agent's notes of every run (B2 data)

        [Fact]
        public void B2_RunNotes_PartialKeepsTheLastCompleteTime_ListsTheMissedFiles_KeepsTheRuns()
        {
            var dir = Path.Combine(root, "set");
            RunNotes.Record(dir, "BS_STOP_SUCCESS", new List<string> { AhsayLog.Line(D3, "start"), AhsayLog.Line(D3, "new", @"D:\a.txt", 1), AhsayLog.Line(D3, "end", message: "BS_STOP_SUCCESS") }, 0, D3);
            Assert.Equal(Id(D3), RunNotes.LastComplete(dir, Id(D3), "BS_STOP_SUCCESS"));
            var log = new List<string> { AhsayLog.Line(D4, "start"), AhsayLog.Line(D4, "err", @"D:\Users\dana\mail.pst", message: "The process cannot access the file, because it is being used by another process"), AhsayLog.Line(D4, "upd", @"D:\b.txt", 1), AhsayLog.Line(D4, "err", @"D:\Users\avi\a,b.pst", message: "locked"), AhsayLog.Line(D4, "end", message: "BS_STOP_SUCCESS_WITH_ERROR") };
            RunNotes.Record(dir, "BS_STOP_SUCCESS_WITH_ERROR", log, 2, D4);
            Assert.Equal(Id(D3), RunNotes.LastComplete(dir, Id(D4), "BS_STOP_SUCCESS_WITH_ERROR"));   // a partial run never moves it
            int n; var missed = RunNotes.Missed(dir, Id(D4), out n);
            Assert.Equal(2, n);
            Assert.Equal(new[] { @"D:\Users\dana\mail.pst", @"D:\Users\avi\a,b.pst" }, missed.Select(m => m.Key).ToArray());
            Assert.Equal("locked", missed[1].Value);
            Assert.Empty(RunNotes.Missed(dir, Id(D3), out n));                  // the list belongs to its own run only
            string at; var lines = RunNotes.Log(dir, out at);
            Assert.Equal(Id(D4), at); Assert.Equal(4, lines.Count);              // the per-file line (upd) is left out
            Assert.Contains(@"D:\Users\dana\mail.pst", RunNotes.Readable(lines));
            var runs = RunNotes.Runs(dir);
            Assert.Equal(new[] { "BS_STOP_SUCCESS", "BS_STOP_SUCCESS_WITH_ERROR" }, runs.Select(r => r[1]).ToArray());
            Assert.Equal("2", runs[1][2]);
            for (int i = 0; i < 70; i++) RunNotes.Record(dir, "BS_STOP_BY_USER", null, 0, D4.AddMinutes(i + 1));
            Assert.Equal(RunNotes.MaxRuns, RunNotes.Runs(dir).Count);
            Assert.Equal(Id(D3), RunNotes.LastComplete(dir, Id(D4.AddMinutes(70)), "BS_STOP_BY_USER"));
            // before the notes existed: a complete last attempt is the last complete run, any other is unknown
            var fresh = Path.Combine(root, "fresh");
            Assert.Equal(Id(D2), RunNotes.LastComplete(fresh, Id(D2), "BS_STOP_SUCCESS_WITH_WARNING"));
            Assert.Equal("", RunNotes.LastComplete(fresh, Id(D2), "BS_STOP_SUCCESS_WITH_ERROR"));
        }

        // ================================================================== the local API serves the notes (component)

        const string Device = "YW5uYQ==.1700000000000.device-secret";
        const string FileSet = "1700000000101";

        [Fact]
        public void B2_LocalApi_State_ServesLastCompleteMissedRunsRetention_AndTheRunLog()
        {
            using (var server = new StubServer())
            {
                var src = Path.Combine(root, "pc", "Docs"); Directory.CreateDirectory(src);
                var p = Profile.Create("anna", "Anna", "x", "en", "UTC");
                var set = new BackupSetInfo { Id = FileSet, Name = "Docs", Computer = "PC-ANNA", Sources = { src }, Hour = 21, Minute = 30, Vss = false };
                set.Retention.Unit = "DAYS"; set.Retention.Period = 30; set.Retention.Monthly = 12;
                p.Root.Add(set.ToXml());
                server.ProfileXml = p.Doc.ToString();
                var app = new AgentApp(Path.Combine(root, "pc", "agent"));
                app.Home.SaveRegistration(server.Url, "anna", "PC-ANNA", Device, "SYSTEM", null);
                var dir = app.Home.SetDir(FileSet); Directory.CreateDirectory(dir);
                RunNotes.Record(dir, "BS_STOP_SUCCESS", null, 0, D3);
                RunNotes.Record(dir, "BS_STOP_SUCCESS_WITH_ERROR", new List<string> { AhsayLog.Line(D4, "err", @"C:\x\mail.pst", message: "locked"), AhsayLog.Line(D4, "end", message: "BS_STOP_SUCCESS_WITH_ERROR") }, 1, D4);
                File.WriteAllText(Path.Combine(dir, "last-attempt.txt"), Id(D4) + "\tBS_STOP_SUCCESS_WITH_ERROR");
                File.WriteAllText(Path.Combine(dir, "last-restore-test.txt"), Id(D2));
                ClientUi ui = null;
                for (int port = 19900 + new Random().Next(90); ui == null; port++) try { ui = new ClientUi(app, port); } catch (HttpListenerException) { }
                using (ui)
                {
                    var st = Get(ui, "state");
                    var s = st.List("sets").Single(x => x["id"] == FileSet);
                    Assert.Equal(Id(D4), s["last"]); Assert.Equal("BS_STOP_SUCCESS_WITH_ERROR", s["result"]);   // unchanged fields
                    Assert.Equal(Id(D3), s["lastComplete"]);
                    Assert.Equal("1", s["missedCount"]);
                    Assert.Equal(@"C:\x\mail.pst", s.List("missed").Single()["p"]); Assert.Equal("locked", s.List("missed").Single()["why"]);
                    Assert.Equal(new[] { Id(D3), Id(D4) }, s.List("runs").Select(r => r["t"]).ToArray());
                    Assert.Equal(Id(D2), s["lastTest"]);
                    Assert.Equal("Kept: 30 days, and 12 monthly versions", ClientView.Keep("en", s));
                    var h = ClientView.Home("en", st, NoJobs);
                    Assert.Equal(HomeState.Partial, h.State); Assert.Equal(ClientView.When("en", D3), h.Facts[0].Value);
                    var log = Get(ui, "runlog", "set=" + FileSet);
                    Assert.Equal(Id(D4), log["at"]); Assert.Equal(2, log.List("lines").Count);
                    Assert.Contains("mail.pst", log["text"]);
                }
            }
        }

        static Msg Get(ClientUi ui, string op, string q = null)
        {
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + ui.Port + "/api/" + op + (q == null ? "" : "?" + q));
            req.Proxy = null; req.Headers["X-Key"] = ui.Key; req.Timeout = 60000;
            using (var resp = (HttpWebResponse)req.GetResponse()) using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return Msg.Parse(rd.ReadToEnd());
        }
    }

    /// <summary>
    /// The native window AS WINDOWS DRAWS IT (only on Windows; elsewhere NOT TESTED): the agent's net40 program renders every
    /// page and state ("client-screens", made-up data) at 1280x800 and at the minimum 860x560, at 100 / 125 / 150 %
    /// (owner Q10: the scale is applied to every size and font of the window, as Windows does), and writes the place of
    /// every named control. The critical actions - the state, Back up now, Restore, the restore steps' confirm / cancel,
    /// the error - must be visible and wholly inside the window in every case. The pictures are kept with the run's results.
    /// </summary>
    public class ClientWindowCWindowsTests
    {
        [DllImport("user32.dll")] static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);
        [DllImport("user32.dll")] static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
            public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput; public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency, dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [Fact]
        public void UX1_Q10_NativeWindow_EveryCriticalActionInsideTheWindow_At860x560And1280x800_At100_125_150()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw NotTested.Because("the native window is drawn only on Windows");
            var repo = PilotAdminUiComponentTests.RepoRoot();
            var exe = new[] { "Debug", "Release" }.Select(c => Path.Combine(repo, "src", "Agent", "bin", c, "net40", "OnlineBackup.Agent.exe")).FirstOrDefault(File.Exists);
            if (exe == null) throw NotTested.Because("the net40 agent (src/Agent/bin/*/net40/OnlineBackup.Agent.exe) is not built here");
            var id = Environment.GetEnvironmentVariable("ID");
            var outDir = Path.Combine(repo, "out", string.IsNullOrEmpty(id) ? "local" : id, "client-screens");
            Directory.CreateDirectory(outDir);
            // a screen big enough for 1280x800 at 150 % (the hosted runner's screen is small); put back afterwards
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) }; EnumDisplaySettings(null, -1, ref dm);
            var orig = dm; var big = dm; big.dmPelsWidth = 1920; big.dmPelsHeight = 1080; big.dmFields = 0x80000 | 0x100000;
            var changed = ChangeDisplaySettings(ref big, 0) == 0;
            var problems = new List<string>(); int checkedShots = 0; var notTested = new List<string>();
            try
            {
                foreach (var lang in new[] { "en", "he" })
                {
                    var psi = new ProcessStartInfo(exe, "client-screens --out \"" + outDir + "\" --lang " + lang + " --matrix 1") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                    using (var pr = Process.Start(psi))
                    {
                        var o = pr.StandardOutput.ReadToEndAsync(); var e = pr.StandardError.ReadToEndAsync();
                        Assert.True(pr.WaitForExit(15 * 60 * 1000), "client-screens did not end in 15 minutes");
                        Assert.True(pr.ExitCode == 0, "client-screens " + lang + ": exit " + pr.ExitCode + "\n" + o.Result + e.Result);
                    }
                    var layout = Path.Combine(outDir, "layout_" + lang + ".txt");
                    Assert.True(File.Exists(layout), "no layout written");
                    // one line per control: shot \t requested WxH \t actual client WxH \t scale \t name \t visible \t x,y,w,h (client coordinates) \t fixed|scroll
                    foreach (var l in File.ReadAllLines(layout))
                    {
                        var f = l.Split('\t'); if (f.Length < 7) continue;
                        var shot = f[0]; var req = f[1]; var act = f[2]; var name = f[4];
                        if (name == "-") { checkedShots++; if (req != act) notTested.Add(shot + ": asked " + req + ", Windows gave " + act); continue; }
                        if (req != act) continue;
                        var cs = act.Split('x').Select(int.Parse).ToArray(); var r = f[6].Split(',').Select(int.Parse).ToArray();
                        // "fixed": wholly inside the window as it opens; "scroll": inside the window's width, reached by scrolling down (never cut at the side)
                        bool scroll = f.Length > 7 && f[7] == "scroll";
                        bool inside = r[0] >= 0 && r[0] + r[2] <= cs[0] && r[2] > 0 && r[3] > 0 && (scroll || (r[1] >= 0 && r[1] + r[3] <= cs[1]));
                        if (f[5] != "1" || !inside) problems.Add(shot + ": " + name + " " + (f[5] != "1" ? "not visible" : "outside the window") + " at " + f[6] + " in " + act);
                    }
                }
            }
            finally { if (changed) ChangeDisplaySettings(ref orig, 0); }
            Assert.True(checkedShots >= 2 * 6 * 6, "only " + checkedShots + " screens were measured");
            Assert.True(problems.Count == 0, string.Join("\n", problems));
            if (notTested.Count > 0) throw NotTested.Because("these sizes were not given by Windows: " + string.Join("; ", notTested));
        }
    }
}
