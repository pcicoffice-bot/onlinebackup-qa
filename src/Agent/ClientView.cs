using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    /// <summary>How one run ended, as the customer's window says it (owner decision B2, docs/RESULT-TRUTH-TABLE.md target).</summary>
    public enum RunKind { Never, Complete, CompleteWithWarnings, Partial, Failed, Stopped, Running, Blocked }
    public enum Tone { Ok, Warn, Bad, Neutral, Info }
    public enum HomeState { NoSets, NotYet, Protected, Partial, Failed, Stopped, Running, NoConnection }

    public sealed class Missed { public string Path = "", Why = ""; }
    public sealed class RunRow { public DateTime Utc; public string Result = "", SetId = "", SetName = "", Kind = "backup", Detail = ""; public int Errors; public RunKind Run; }

    public sealed class SetView
    {
        public string Id = "", Name = "", Sources = "", Hour = "", Type = "", Blocked = "", LastResult = "", Computer = "";
        public bool Mine = true;
        public RunKind Kind; public Tone Tone; public string Word = "", Chip = "";
        public DateTime? LastAttempt, LastComplete, LastTest;
        public int MissedCount; public List<Missed> Missed = new List<Missed>();
        public List<RunRow> Runs = new List<RunRow>();
        public string[] Src = new string[0], Skip = new string[0];
    }

    /// <summary>UI-Q2: the question when files already exist at the destination (from the local API's restorecheck).</summary>
    public sealed class ExistingQuestion
    {
        public string Title = "", More = "", Text = "";
        public List<string> Files = new List<string>();
        /// <summary>The bar's buttons (primary, secondary, third) and what each sends as existing=.</summary>
        public string[] Buttons = new string[0], Decisions = new string[0];
    }

    /// <summary>UI-Q1: a restore job as the result step shows it.</summary>
    public sealed class RestoreView
    {
        public Tone Tone; public bool Success, Running;
        public string Caption = "", Headline = "", Counts = "", MismatchNote = "";
        public List<string> Verification = new List<string>(), Mismatch = new List<string>();
    }

    public sealed class Fact { public string Label = "", Value = "", Sub = ""; public Tone Tone; }

    public sealed class HomeView
    {
        public HomeState State; public Tone Tone;
        public string Title = "", Sub = "";
        /// <summary>The main action: its words and what it does (backup-all, add, retry, contact, or "" for none).</summary>
        public string Primary = "", PrimaryAction = "";
        public string Secondary = "", SecondaryAction = "";
        public Fact[] Facts = new Fact[0];
        public List<SetView> Sets = new List<SetView>();
        /// <summary>The sets whose last run left files not backed up, or failed: the card "What was not backed up and why".</summary>
        public List<SetView> Problems = new List<SetView>();
        public SetView RunningSet;
    }

    /// <summary>A rectangle of the window, in pixels of the client area.</summary>
    public sealed class Box
    {
        public int X, Y, W, H;
        public Box(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; }
        public int Right { get { return X + W; } }
        public int Bottom { get { return Y + H; } }
        public bool Inside(Box o) { return X >= o.X && Y >= o.Y && Right <= o.Right && Bottom <= o.Bottom && W > 0 && H > 0; }
        public bool Overlaps(Box o) { return X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom; }
        public override string ToString() { return X + "," + Y + " " + W + "x" + H; }
    }

    /// <summary>Where the parts of the window go: the side menu, the page (it scrolls), the fixed bar of the restore steps.</summary>
    public sealed class FrameLayout
    {
        public Box Client, Nav, Page, Bar, Primary, Secondary, Tertiary;
        public bool NavCollapsed, TwoColumns;
    }

    /// <summary>
    /// The customer's window, alternative C ("Hybrid", chosen by the owner): what it shows, in which words and colours, and
    /// where its parts go - without Windows Forms, so the same rules are tested here and drawn by ClientForm (and followed by
    /// client.html). Every text is an English template translated with L.T (src/Core/i18n/he.json).
    /// </summary>
    public static class ClientView
    {
        // ---------------------------------------------------------------- the menu (IA §2: Restore before History, no Alerts page)

        /// <summary>The side menu, top to bottom. "New backup" is inside Backup sets, Security inside Settings; Help from the
        /// IT company stays at the bottom of the menu.</summary>
        public static readonly string[][] Nav = { new[] { "home", "Home" }, new[] { "sets", "Backups" }, new[] { "restore", "Restore" }, new[] { "history", "History" }, new[] { "settings", "Settings" } };
        public static readonly string HelpPage = "help";
        /// <summary>Pages reached from a page, not from the menu: a set's details, a new backup, the help (bottom of the menu).</summary>
        public static readonly Dictionary<string, string> ReachedFrom = new Dictionary<string, string> { { "set", "sets" }, { "new", "sets" }, { "help", "settings" } };

        /// <summary>
        /// Everything the customer could do in the window before alternative C (the local API operations ClientForm called),
        /// and the page where it is now. Nothing a customer can do today disappears.
        /// </summary>
        public static readonly string[][] Actions =
        {
            new[] { "backup", "home" }, new[] { "backup", "sets" }, new[] { "backup", "set" },
            new[] { "points", "restore" }, new[] { "files", "restore" }, new[] { "restore", "restore" },
            new[] { "addset", "new" }, new[] { "dirs", "new" }, new[] { "editset", "set" },
            new[] { "security", "settings" }, new[] { "totp-enable", "settings" }, new[] { "totp-confirm", "settings" }, new[] { "totp-disable", "settings" },
            new[] { "help", "help" }, new[] { "update", "settings" }, new[] { "login", "any" }, new[] { "jobs", "home" }, new[] { "jobs", "history" },
            new[] { "state", "any" }, new[] { "server-check", "connect" }, new[] { "connect", "connect" }, new[] { "language", "settings" }, new[] { "language", "connect" },
        };

        // ---------------------------------------------------------------- one result

        public static RunKind Classify(string result)
        {
            result = (result ?? "").Trim();
            if (result.Length == 0) return RunKind.Never;
            if (result == "BS_STOP_SUCCESS") return RunKind.Complete;
            if (result == "BS_STOP_SUCCESS_WITH_WARNING") return RunKind.CompleteWithWarnings;
            if (result.StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal)) return RunKind.Partial;   // WITH_ERROR: files missing (B2)
            if (result == "BS_STOP_BY_USER") return RunKind.Stopped;
            return RunKind.Failed;   // SYSTEM_ERROR, QUOTA_EXCEEDED, PRE_COMMAND, NETWORK, interrupted, a failed job
        }

        /// <summary>B2: "Last complete backup" counts only these.</summary>
        public static bool CountsAsComplete(RunKind k) { return k == RunKind.Complete || k == RunKind.CompleteWithWarnings; }

        public static Tone ToneOf(RunKind k)
        {
            switch (k)
            {
                case RunKind.Complete: case RunKind.CompleteWithWarnings: return Tone.Ok;
                case RunKind.Partial: return Tone.Warn;
                case RunKind.Failed: return Tone.Bad;
                case RunKind.Running: return Tone.Info;
                default: return Tone.Neutral;   // never, stopped, blocked in the pilot
            }
        }

        /// <summary>The fixed words of the five states (IA §5.3). A partial run is never "completed with warnings".</summary>
        public static string Word(string lang, RunKind k, string result = null)
        {
            switch (k)
            {
                case RunKind.Complete: return L.T(lang, "Complete");
                case RunKind.CompleteWithWarnings: return L.T(lang, "Complete — with warnings");
                case RunKind.Partial: return L.T(lang, "Partial");
                case RunKind.Failed: return (result ?? "").Contains("QUOTA") ? L.T(lang, "Failed — the quota is full") : L.T(lang, "Failed");
                case RunKind.Stopped: return L.T(lang, "Stopped");
                case RunKind.Running: return L.T(lang, "Running");
                case RunKind.Blocked: return L.T(lang, "Does not run in the pilot");
                default: return L.T(lang, "Not backed up yet");
            }
        }

        /// <summary>The state colours (IA §1): always with an icon and a word, never the colour alone.</summary>
        public static string Hex(Tone t)
        {
            switch (t) { case Tone.Ok: return "#11764A"; case Tone.Warn: return "#8A4B00"; case Tone.Bad: return "#B42318"; case Tone.Info: return "#1F5FBF"; default: return "#5B6472"; }
        }
        public static string Fill(Tone t)
        {
            switch (t) { case Tone.Ok: return "#E7F4EC"; case Tone.Warn: return "#FFF3DD"; case Tone.Bad: return "#FDECEA"; case Tone.Info: return "#E8F0FB"; default: return "#EEF0F3"; }
        }
        public static string Icon(Tone t, RunKind k)
        {
            if (k == RunKind.Stopped) return "■";
            switch (t) { case Tone.Ok: return "✓"; case Tone.Warn: return "!"; case Tone.Bad: return "✕"; case Tone.Info: return "↻"; default: return "–"; }
        }

        // ---------------------------------------------------------------- time

        /// <summary>Times from the local API: a RunId (yyyy-MM-dd-HH-mm-ss, UTC) or Unix milliseconds.</summary>
        public static DateTime? ParseTime(string v)
        {
            if (string.IsNullOrEmpty(v)) return null;
            DateTime d; if (RunId.TryParse(v.Trim(), out d)) return d;
            long ms; if (long.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ms) && ms > 0) return RunId.FromUnixMs(ms);
            return null;
        }

        /// <summary>An absolute local time, Israeli style in Hebrew (07.10.2026 21:38).</summary>
        public static string When(string lang, DateTime? utc)
        {
            if (utc == null) return L.T(lang, "Not yet");
            var t = utc.Value.ToLocalTime();
            if (L.Norm(lang) == "he") return t.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
            return t.ToString("dd MMM yyyy HH:mm", CultureInfo.GetCultureInfo("en-US"));
        }

        // ---------------------------------------------------------------- the sets

        public static SetView Set(string lang, Msg s, IList<Msg> jobs)
        {
            var v = new SetView
            {
                Id = s["id"] ?? "", Name = s["name"] ?? "", Sources = s["sources"] ?? "", Hour = s["hour"] ?? "", Type = s["type"] ?? "", Blocked = s["blocked"] ?? "",
                Computer = s["computer"] ?? "", Mine = s["mine"] != "0", LastResult = (s["result"] ?? "").Trim(),
                LastAttempt = ParseTime(s["last"]), LastComplete = ParseTime(s["lastComplete"]), LastTest = ParseTime(s["lastTest"]),
                MissedCount = s.Int("missedCount"),
                Src = (s["src"] ?? "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray(),
                Skip = (s["skip"] ?? "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray(),
            };
            foreach (var m in s.List("missed")) v.Missed.Add(new Missed { Path = m["p"] ?? "", Why = m["why"] ?? "" });
            if (v.MissedCount < v.Missed.Count(x => x.Path.Length > 0)) v.MissedCount = v.Missed.Count(x => x.Path.Length > 0);
            foreach (var r in s.List("runs"))
            {
                var t = ParseTime(r["t"]); if (t == null) continue;
                v.Runs.Add(new RunRow { Utc = t.Value, Result = r["r"] ?? "", Errors = r.Int("e"), SetId = v.Id, SetName = v.Name, Run = Classify(r["r"]) });
            }
            // before the notes of every run existed: the last attempt is the one run known
            if (v.Runs.Count == 0 && v.LastAttempt != null) v.Runs.Add(new RunRow { Utc = v.LastAttempt.Value, Result = v.LastResult, Errors = v.MissedCount, SetId = v.Id, SetName = v.Name, Run = Classify(v.LastResult) });
            v.Runs = v.Runs.OrderBy(x => x.Utc).ToList();
            bool running = jobs != null && jobs.Any(j => j["set"] == v.Id && j["state"] == "running" && j["kind"] != "restore");
            v.Kind = v.Blocked.Length > 0 ? RunKind.Blocked : running ? RunKind.Running : Classify(v.LastResult);
            v.Tone = ToneOf(v.Kind);
            v.Word = Word(lang, v.Kind, v.LastResult);
            v.Chip = v.Kind == RunKind.Partial && v.MissedCount > 0 ? L.T(lang, "Partial · {0} files not backed up", v.MissedCount) : v.Word;
            return v;
        }

        public static List<SetView> Sets(string lang, Msg state, IList<Msg> jobs, bool mineOnly = true)
        {
            return state.List("sets").Where(s => !mineOnly || s["mine"] != "0").Select(s => Set(lang, s, jobs)).ToList();
        }

        // ---------------------------------------------------------------- Home: the protection bar

        public static HomeView Home(string lang, Msg state, IList<Msg> jobs)
        {
            var h = new HomeView { Sets = Sets(lang, state, jobs) };
            var company = Company(lang, state);
            var active = h.Sets.Where(s => s.Kind != RunKind.Blocked).ToList();
            h.Problems = active.Where(s => s.Kind == RunKind.Partial || s.Kind == RunKind.Failed).ToList();
            h.RunningSet = active.FirstOrDefault(s => s.Kind == RunKind.Running);
            h.Secondary = L.T(lang, "Restore files"); h.SecondaryAction = "restore";
            var backUp = L.T(lang, "Back up now");
            if (!string.IsNullOrEmpty(state["offline"]))
            {
                h.State = HomeState.NoConnection; h.Tone = Tone.Neutral;
                h.Title = L.T(lang, "No connection to the backup server");
                h.Sub = L.T(lang, "The backups wait and run when the connection is back. What was backed up already is safe.");
                h.Primary = L.T(lang, "Try again"); h.PrimaryAction = "retry";
            }
            else if (active.Count == 0)
            {
                h.State = HomeState.NoSets; h.Tone = Tone.Info;
                h.Title = L.T(lang, "This computer is not backed up yet");
                if (state["canAdd"] == "0") { h.Sub = L.T(lang, "Your IT provider adds new backups — contact {0}.", company); h.Primary = L.T(lang, "Contact {0}", company); h.PrimaryAction = "contact"; }
                else { h.Sub = L.T(lang, "Choose which folders to back up and when. The first backup can take a few hours."); h.Primary = L.T(lang, "Choose folders to back up"); h.PrimaryAction = "add"; }
                h.Secondary = ""; h.SecondaryAction = "";
            }
            else if (h.RunningSet != null)
            {
                h.State = HomeState.Running; h.Tone = Tone.Info;
                h.Title = L.T(lang, "Backup running");
                h.Sub = L.T(lang, "Backing up \"{0}\". You can close this window — the backup continues.", h.RunningSet.Name);
                h.Primary = backUp; h.PrimaryAction = "backup-all";
            }
            else if (active.Any(s => s.Kind == RunKind.Failed))
            {
                var f = active.First(s => s.Kind == RunKind.Failed);
                h.State = HomeState.Failed; h.Tone = Tone.Bad;
                h.Title = L.T(lang, "The backup of \"{0}\" failed", f.Name);
                h.Sub = f.LastComplete != null ? L.T(lang, "The files are protected up to {0}. Changes since then are not protected yet.", When(lang, f.LastComplete)) : L.T(lang, "There is no complete backup of it yet.");
                if (f.LastResult.Contains("QUOTA")) { h.Sub = L.T(lang, "The quota is full.") + " " + h.Sub; h.Primary = L.T(lang, "Contact {0}", company); h.PrimaryAction = "contact"; }
                else { h.Primary = L.T(lang, "Back up again now"); h.PrimaryAction = "backup-all"; }
            }
            else if (active.Any(s => s.Kind == RunKind.Partial))
            {
                var n = active.Where(s => s.Kind == RunKind.Partial).Sum(s => s.MissedCount);
                h.State = HomeState.Partial; h.Tone = Tone.Warn;
                h.Title = L.T(lang, "Attention needed");
                h.Sub = n > 0 ? L.T(lang, "{0} files were not backed up · everything else was backed up", n) : L.T(lang, "Some files were not backed up · everything else was backed up");
                h.Primary = L.T(lang, "Back up again now"); h.PrimaryAction = "backup-all";
            }
            else if (active.Any(s => s.Kind == RunKind.Stopped))
            {
                var s0 = active.First(s => s.Kind == RunKind.Stopped);
                h.State = HomeState.Stopped; h.Tone = Tone.Neutral;
                h.Title = L.T(lang, "The backup was stopped");
                h.Sub = L.T(lang, "\"{0}\" was stopped before it finished. The next backup runs as usual at {1}.", s0.Name, s0.Hour);
                h.Primary = backUp; h.PrimaryAction = "backup-all";
            }
            else if (active.Any(s => s.Kind == RunKind.Never))
            {
                var s0 = active.First(s => s.Kind == RunKind.Never);
                h.State = HomeState.NotYet; h.Tone = Tone.Info;
                h.Title = L.T(lang, "Waiting for the first backup");
                h.Sub = L.T(lang, "\"{0}\" has not been backed up yet. It runs every day at {1}, or click \"Back up now\".", s0.Name, s0.Hour);
                h.Primary = backUp; h.PrimaryAction = "backup-all";
            }
            else
            {
                h.State = HomeState.Protected; h.Tone = Tone.Ok;
                h.Title = L.T(lang, "This computer is protected");
                h.Sub = active.Count == 1 ? L.T(lang, "The last backup finished completely.") : L.T(lang, "All {0} backups finished completely.", active.Count);
                h.Primary = backUp; h.PrimaryAction = "backup-all";
            }
            h.Facts = Facts(lang, h, active, company);
            return h;
        }

        static Fact[] Facts(string lang, HomeView h, List<SetView> active, string company)
        {
            // 1. Owner Q7: each set shows its OWN last complete backup (the list under the bar). At computer level, with more
            //    than one set, the time is "the last time ALL sets were complete" (from the runs this computer noted) - never
            //    the earliest date, and never a date that is not known.
            var complete = new Fact { Label = L.T(lang, active.Count > 1 ? "Last time all backups were complete" : "Last complete backup"), Tone = Tone.Neutral };
            if (active.Count == 0) { complete.Value = "—"; }
            else if (active.Count == 1)
            {
                var s0 = active[0];
                complete.Value = When(lang, s0.LastComplete);
                complete.Sub = s0.LastComplete == null ? L.T(lang, "\"{0}\" has no complete backup yet", s0.Name) : L.T(lang, "Every selected file was backed up");
                if (h.State == HomeState.Protected) complete.Tone = Tone.Ok;
            }
            else
            {
                var all = AllComplete(active);
                complete.Value = all == null ? "—" : When(lang, all);
                complete.Sub = all == null ? L.T(lang, "Not known yet — see each backup below") : L.T(lang, "Every backup had finished completely");
                if (all != null && h.State == HomeState.Protected) complete.Tone = Tone.Ok;
            }
            // 2. Last backup attempt: the newest run, whatever its result
            var attempt = new Fact { Label = L.T(lang, "Last backup attempt"), Tone = Tone.Neutral };
            if (h.RunningSet != null) { attempt.Value = L.T(lang, "Now"); attempt.Sub = h.RunningSet.Name + " · " + Word(lang, RunKind.Running); attempt.Tone = Tone.Info; }
            else
            {
                var last = active.Where(s => s.LastAttempt != null).OrderByDescending(s => s.LastAttempt.Value).FirstOrDefault();
                if (last == null) attempt.Value = L.T(lang, "Not yet");
                else
                {
                    var k = Classify(last.LastResult);
                    attempt.Value = When(lang, last.LastAttempt); attempt.Sub = last.Name + " · " + Word(lang, k, last.LastResult);
                    attempt.Tone = k == RunKind.Complete || k == RunKind.CompleteWithWarnings ? Tone.Neutral : ToneOf(k);
                }
            }
            // 3. The automatic restore test (the agent's own, every 30 days after a good backup). Its result goes to the IT
            //    company; this computer knows only that it ran - so the window says "ran", never "verified".
            var test = new Fact { Label = L.T(lang, "Last restore test"), Tone = Tone.Neutral };
            var lt = active.Where(s => s.LastTest != null).OrderByDescending(s => s.LastTest.Value).FirstOrDefault();
            test.Value = lt == null ? L.T(lang, "Not yet") : When(lang, lt.LastTest);
            test.Sub = L.T(lang, "Automatic test · the result goes to {0}", company);
            return new[] { complete, attempt, test };
        }

        /// <summary>Owner Q7: the last moment at which the newest run of EVERY set was complete (from the noted runs), or null.</summary>
        public static DateTime? AllComplete(IList<SetView> sets)
        {
            if (sets.Count == 0) return null;
            var times = sets.SelectMany(s => s.Runs.Where(r => CountsAsComplete(r.Run)).Select(r => r.Utc)).Distinct().OrderByDescending(t => t);
            foreach (var t in times)
                if (sets.All(s => { var r = s.Runs.Where(x => x.Utc <= t).OrderByDescending(x => x.Utc).FirstOrDefault(); return r != null && CountsAsComplete(r.Run); })) return t;
            return null;
        }

        /// <summary>Owner Q8: how long the backups are kept, in plain words, from the set's own retention ("" when the window
        /// was not given it - nothing is made up).</summary>
        public static string Keep(string lang, Msg s)
        {
            var unit = s["retUnit"]; int period = s.Int("retPeriod");
            if (string.IsNullOrEmpty(unit) || period <= 0) return "";
            var text = unit == "JOBS" ? L.T(lang, "Kept: the last {0} backups", period) : L.T(lang, "Kept: {0} days", period);
            var extra = new List<string>();
            foreach (var k in new[] { new[] { "retDaily", "{0} daily versions" }, new[] { "retWeekly", "{0} weekly versions" }, new[] { "retMonthly", "{0} monthly versions" }, new[] { "retQuarterly", "{0} quarterly versions" }, new[] { "retYearly", "{0} yearly versions" } })
                if (s.Int(k[0]) > 0) extra.Add(L.T(lang, k[1], s.Int(k[0])));
            return extra.Count == 0 ? text : text + L.T(lang, ", and {0}", string.Join(", ", extra.ToArray()));
        }

        /// <summary>Owner Q3: no manual update by the customer in the pilot (the IT company updates); hidden everywhere.</summary>
        public static bool ShowsManualUpdate(Msg state) { return state == null || state["pilot"] != "1"; }

        /// <summary>Owner Q5: the reason beside an action the IT company locked (the action is shown disabled).</summary>
        public const string Locked = "This setting is managed by your service provider";

        /// <summary>Owner Q2 (engine merged 082a703): restore to the original location - the product's own engine only; a restic
        /// set keeps the choice disabled with this reason.</summary>
        public static bool OriginalLocationAvailable(string engine) { return engine != "RESTIC"; }
        public const string OriginalLocationWhy = "Restoring to the original location is available for the product's own engine only — choose a folder.";

        public static string Company(string lang, Msg state)
        {
            var c = state == null ? null : state["company"];
            return string.IsNullOrEmpty(c) ? L.T(lang, "your IT provider") : c;
        }

        /// <summary>The fixed sentence of "What to do" for files not backed up (no promise the product does not keep: the
        /// alert on a repeated partial run is not built yet, so it is not promised).</summary>
        public static string WhatToDo(string lang, Msg state)
        {
            return L.T(lang, "Close the programs that keep these files open and click \"Back up again now\". If it happens again, contact {0}.", Company(lang, state));
        }

        // ---------------------------------------------------------------- history

        public static List<RunRow> History(string lang, Msg state, IList<Msg> jobs, string filter, string setId)
        {
            var rows = new List<RunRow>();
            foreach (var s in Sets(lang, state, jobs, false).Where(x => x.Mine)) rows.AddRange(s.Runs);
            if (jobs != null)
                foreach (var j in jobs)
                {
                    var t = ParseTime(j["started"]); if (t == null) continue;
                    var name = (state.List("sets").FirstOrDefault(x => x["id"] == j["set"]) ?? new Msg())["name"] ?? "";
                    var kind = j["kind"] == "restore" ? "restore" : "backup";
                    var run = j["state"] == "running" ? RunKind.Running : kind == "restore" ? (j["state"] == "ok" ? RunKind.Complete : RunKind.Failed) : Classify(j["result"]);
                    if (kind == "backup" && run != RunKind.Running) continue;   // an ended backup is in the set's own runs
                    rows.Add(new RunRow { Utc = t.Value, Kind = kind, SetId = j["set"] ?? "", SetName = name, Result = j["result"] ?? "", Detail = j["detail"] ?? "", Run = run });
                }
            if (!string.IsNullOrEmpty(setId)) rows = rows.Where(r => r.SetId == setId).ToList();
            if (filter == "backups") rows = rows.Where(r => r.Kind == "backup").ToList();
            else if (filter == "restores") rows = rows.Where(r => r.Kind == "restore").ToList();
            else if (filter == "problems") rows = rows.Where(r => r.Run == RunKind.Partial || r.Run == RunKind.Failed).ToList();
            return rows.OrderByDescending(r => r.Utc).ToList();
        }

        // ---------------------------------------------------------------- restore (3 steps, UX-1, B5)

        public static readonly string[] RestoreSteps = { "When and what", "Where and check", "Result" };

        /// <summary>B5 (pilot): what a restore brings back and what it does not - said on the restore screen.</summary>
        public const string RestoreKeeps = "Restored: the content and the modified time. Not restored: permissions and file attributes.";
        public const string RestoreKeepsAfter = "Permissions and file attributes were not restored.";

        public static string RestoreButton(string lang, int chosen)
        {
            return chosen == 0 ? L.T(lang, "Restore everything") : chosen == 1 ? L.T(lang, "Restore 1 selected item") : L.T(lang, "Restore {0} selected items", chosen);
        }

        /// <summary>The words of each step's buttons in the fixed bar: (primary, secondary, third).</summary>
        public static string[] RestoreBar(string lang, int step, int chosen)
        {
            if (step == 1) return new[] { L.T(lang, "Next: where to restore"), L.T(lang, "Cancel"), "" };
            if (step == 2) return new[] { RestoreButton(lang, chosen), L.T(lang, "Back"), "" };
            return new[] { L.T(lang, "Open the folder"), L.T(lang, "Back to Home"), L.T(lang, "Restore more") };
        }

        /// <summary>UI-Q2: the question for files that already exist (null when none do). Replace / Cancel / Skip, never silent.</summary>
        public static ExistingQuestion Existing(string lang, Msg check)
        {
            int existing = check.Int("existing"), total = check.Int("total");
            if (existing <= 0) return null;
            var q = new ExistingQuestion();
            q.Title = check["location"] == "original" ? L.T(lang, "{0} of the {1} files already exist at the original location", existing, total) : L.T(lang, "{0} of the {1} files already exist in the folder", existing, total);
            q.Text = L.T(lang, "Replace them with the files from the backup, skip them (they stay as they are), or cancel. Nothing was restored yet.");
            q.Files = check.List("files").Select(x => x["p"] ?? "").Where(x => x.Length > 0).ToList();
            if (existing > q.Files.Count) q.More = L.T(lang, "and {0} more", existing - q.Files.Count);
            q.Buttons = new[] { L.T(lang, "Replace existing"), L.T(lang, "Cancel"), L.T(lang, "Skip existing") };
            q.Decisions = new[] { "overwrite", "cancel", "skip" };
            return q;
        }

        /// <summary>
        /// UI-Q1: the result of a restore job. "Verified" only when the job has a verified count; whole-file SHA-256
        /// (verifiedSha256) apart from the files checked by their chunks and size only (an older backup: "Checked" - the
        /// headline wording for that case is an open owner question). A file that did not match the backup makes the restore
        /// NOT successful, named, with the note that the file at the destination was kept.
        /// </summary>
        public static RestoreView RestoreResult(string lang, Msg job, int chosen)
        {
            var v = new RestoreView();
            var st = job == null ? "running" : job["state"] ?? "running";
            if (st == "running")
            {
                v.Running = true; v.Tone = Tone.Info; v.Caption = L.T(lang, "The restore has started"); v.Headline = L.T(lang, "Restoring…");
                return v;
            }
            int mism = job.Int("mismatched"), failed = job.Int("failed");
            v.Mismatch = job.List("mismatch").Select(x => x["p"] ?? "").Where(x => x.Length > 0).ToList();
            if (mism < v.Mismatch.Count) mism = v.Mismatch.Count;
            if (job["restored"] != null) v.Counts = L.T(lang, "Restored {0} · skipped {1} · failed {2}", job.Int("restored"), job.Int("skipped"), failed);
            if (job["verified"] != null)
            {
                int all = job.Int("verified"), sha = Math.Min(all, job.Int("verifiedSha256")), chunks = all - sha;
                if (sha > 0) v.Verification.Add(L.T(lang, "Verified (SHA-256): {0} files", sha));
                if (chunks > 0) v.Verification.Add(L.T(lang, "Checked (chunks and size): {0} files", chunks));
            }
            v.Success = st == "ok" && mism == 0 && failed == 0;
            v.Tone = v.Success ? Tone.Ok : Tone.Bad;
            v.Caption = v.Success ? L.T(lang, "The restore finished") : L.T(lang, "The restore did not finish");
            if (mism > 0)
            {
                v.Headline = L.T(lang, "{0} files did not match the backup", mism);
                v.MismatchNote = L.T(lang, "They were not put in place: the file already at the destination was kept.");
            }
            else if (!v.Success) v.Headline = L.T(lang, "Some files were not restored");
            else v.Headline = chosen == 0 ? L.T(lang, "Everything in the point was restored") : chosen == 1 ? L.T(lang, "1 selected item was restored") : L.T(lang, "{0} selected items were restored", chosen);
            return v;
        }

        /// <summary>The restore points grouped by local day, newest first (the day strip of step 1).</summary>
        public static List<KeyValuePair<DateTime, List<Msg>>> Days(IEnumerable<Msg> points)
        {
            return points.Select(p => new { p, t = ParseTime(p["time"]) ?? ParseTime(p["id"]) })
                .Where(x => x.t != null).GroupBy(x => x.t.Value.ToLocalTime().Date)
                .OrderByDescending(g => g.Key).Select(g => new KeyValuePair<DateTime, List<Msg>>(g.Key, g.OrderByDescending(x => x.t.Value).Select(x => x.p).ToList())).ToList();
        }

        // ---------------------------------------------------------------- layout (UX-1; owner Q10: 860x560 minimum, 100/125/150 %)

        public const int MinWidth = 860, MinHeight = 560;
        static int S(double scale, int px) { return (int)Math.Round(px * scale); }

        /// <summary>
        /// The window's frame for a client area (pixels) at a display scale: the side menu (collapsed to icons below 1100
        /// logical pixels), the page (it scrolls) and - in the restore steps - the bar fixed at the bottom with its buttons
        /// (primary on the reading end). The buttons never leave the window: they shrink before that.
        /// </summary>
        public static FrameLayout Frame(int clientW, int clientH, double scale, bool rtl, bool bar, int primaryW = 200, int secondaryW = 140, int tertiaryW = 0)
        {
            var f = new FrameLayout { Client = new Box(0, 0, clientW, clientH) };
            f.NavCollapsed = clientW < S(scale, 1100);
            int nav = f.NavCollapsed ? S(scale, 64) : S(scale, 232);
            f.Nav = new Box(rtl ? clientW - nav : 0, 0, nav, clientH);
            int px = rtl ? 0 : nav, pw = clientW - nav;
            int barH = bar ? S(scale, 72) : 0;
            f.Page = new Box(px, 0, pw, clientH - barH);
            f.TwoColumns = pw - S(scale, 64) >= S(scale, 900);
            if (bar)
            {
                f.Bar = new Box(px, clientH - barH, pw, barH);
                int m = S(scale, 24), bh = S(scale, 40), gap = S(scale, 12), y = f.Bar.Y + (barH - bh) / 2;
                int avail = pw - 2 * m - gap * (tertiaryW > 0 ? 2 : 1);
                int total = primaryW + secondaryW + tertiaryW;
                double k = total > avail ? (double)avail / total : 1.0;
                int p = (int)(primaryW * k), s = (int)(secondaryW * k), t = (int)(tertiaryW * k);
                // LTR: primary at the right end; RTL: at the left end (the reading end of the bar)
                f.Primary = new Box(rtl ? px + m : px + pw - m - p, y, p, bh);
                f.Secondary = new Box(rtl ? px + pw - m - s : px + m, y, s, bh);
                if (tertiaryW > 0) f.Tertiary = new Box(rtl ? f.Secondary.X - gap - t : f.Secondary.Right + gap, y, t, bh);
            }
            return f;
        }

        /// <summary>The protection bar's two buttons: beside the state when the page is wide, under it when narrow.</summary>
        public static Box[] HeroButtons(int pageW, double scale, bool rtl, int primaryW, int secondaryW, out bool below)
        {
            int m = S(scale, 24), gap = S(scale, 12), bh = S(scale, 44);
            below = pageW < S(scale, 760);
            int avail = pageW - 2 * m - gap;
            double k = primaryW + secondaryW > avail ? (double)avail / (primaryW + secondaryW) : 1.0;
            int p = (int)(primaryW * k), s = (int)(secondaryW * k);
            int y = below ? S(scale, 120) : S(scale, 32);
            // LTR: the state on the left, the buttons on the right; RTL mirrored
            var prim = new Box(rtl ? m + s + gap : pageW - m - p - s - gap, y, p, bh);
            var sec = new Box(rtl ? m : pageW - m - s, y, s, bh);
            if (below) { prim = new Box(rtl ? pageW - m - p : m, y, p, bh); sec = new Box(rtl ? prim.X - gap - s : prim.Right + gap, y, s, bh); }
            return new[] { prim, sec };
        }
    }
}
