using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Pilot release blockers, the admin site — COMPONENT layer (no web server is started: the units behind the pages alone).
    /// Contracts from tests/QA/specs.py:
    ///   UI-01 every admin page opens without errors; sign-in, reload, sign-out → the page files exist and are the shipped
    ///         ones; a sign-in gives a session that a reload (and a restart) keeps; sign-out ends it everywhere
    ///   UI-02 the set editor saves every tab → each tab's change is read back the same (from the file on disk, not through
    ///         the product's own reader); a refused save changes nothing; two technicians cannot undo each other
    ///   UI-03 the admin site shows the truth after failures → a failed or silent run is never green; the failure stays
    ///         in the history after the next good run
    /// Oracle: the Profile.xml / runs file read with XDocument / File here, SHA-256 of the files, exact values.
    /// </summary>
    public class PilotAdminUiComponentTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "obpilot-adm-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public void Dispose() { SystemClock.Use(null); try { Directory.Delete(root, true); } catch (Exception) { } }

        const string Remote = "203.0.113.20";   // a technician's office, not the server itself (two-step, 2-hour sessions)

        SystemConfig Config(bool pilot = false)
        {
            var cfg = SystemConfig.Init(Path.Combine(root, "system"), "admin", "Admin-Pass-1", "localhost", new[] { Path.Combine(root, "home") + "|UNLIMITED|100" });
            if (pilot) { cfg.Doc.Root.SetAttributeValue("SCOPE", "PILOT"); cfg.Save(); }
            return cfg;
        }

        static string Sha(string file) { using (var s = File.OpenRead(file)) using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(s)); }
        static string Sha(byte[] b) { using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(b)); }

        public static string RepoRoot()
        {
            var d = AppContext.BaseDirectory;
            while (d != null && !Directory.Exists(Path.Combine(d, "src", "Server", "Web"))) d = Path.GetDirectoryName(d);
            if (d == null) throw NotTested.Because("the source tree (src/Server/Web) is not beside the test binaries");
            return d;
        }

        // ================================================================== UI-01

        /// <summary>
        /// UI-01 happy: the admin site's page and every file it loads are in the server, byte-identical to the shipped source
        /// (src/Server/Web, src/Core/i18n) — nothing the page asks for is missing (a missing file is a blank page).
        /// </summary>
        [Fact]
        public void UI01_ThePageAndEveryFileItLoads_AreInTheServer_ByteIdenticalToTheSource()
        {
            var repo = RepoRoot();
            var server = typeof(AdminUi).Assembly; var core = typeof(L).Assembly;
            Func<Assembly, string, byte[]> res = (a, n) => { using (var s = a.GetManifestResourceStream(n)) { if (s == null) return null; var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); } };
            var index = res(server, "web.index.html");
            Assert.NotNull(index);
            Assert.Equal(Sha(Path.Combine(repo, "src", "Server", "Web", "index.html")), Sha(index));
            var html = Encoding.UTF8.GetString(index);
            var refs = Regex.Matches(html, "(?:src|href)=\"(/[^\"]+)\"").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            Assert.Equal(new[] { "/i18n/theme.css", "/admin/app.css", "/i18n/i18n.js", "/i18n/qrcode.js", "/admin/app.js" }, refs);   // exactly these, in this order
            foreach (var r in refs)
            {
                var name = r.Substring(r.IndexOf('/', 1) + 1);
                byte[] got; string disk;
                if (r.StartsWith("/admin/")) { got = res(server, "web." + name); disk = Path.Combine(repo, "src", "Server", "Web", name); }
                else if (name == "theme.css") { string type; got = WebAssets.Get(name, out type); Assert.Equal("text/css; charset=utf-8", type); disk = Path.Combine(repo, "src", "Core", "i18n", name); }
                else { got = res(core, name); disk = Path.Combine(repo, "src", "Core", "i18n", name); }
                Assert.True(got != null, r + " is not in the server: the page would not load it");
                Assert.Equal(Sha(disk), Sha(got));
            }
            // the fonts the design system names are there too
            var css = File.ReadAllText(Path.Combine(repo, "src", "Core", "i18n", "theme.css"));
            var fonts = Regex.Matches(css, @"url\(['""]?(?:/i18n/)?(fonts/[^'"")]+\.woff2)").Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
            foreach (var f in fonts) { string type; Assert.True(WebAssets.Get(f, out type) != null, f + " is named by theme.css and missing"); Assert.Equal("font/woff2", type); }
            // the languages the page offers (pilot: English and Hebrew only) are served; no other
            Assert.Equal(new[] { "en", "he" }, L.Languages);
            Assert.Contains("\"strings\"", L.Raw("he"));
        }

        /// <summary>Two-step set up for the administrator, as the enrolment page does; returns the secret.</summary>
        static string Enrol(SystemConfig cfg)
        {
            var si = Staff.Check(cfg, "admin", "Admin-Pass-1", null, Remote, SystemClock.UtcNow);
            Assert.True(si.Enroll);   // TECH-010: the first sign-in only opens the set-up
            var secret = Staff.TotpEnable(cfg, "admin")["secret"];
            Staff.TotpConfirm(cfg, "admin", Totp.Code(secret, DateTime.UtcNow), SystemClock.UtcNow);
            return secret;
        }

        static string Kept(SystemConfig cfg) { var p = Path.Combine(cfg.SystemHome, "conf", "kept-sessions.xml"); return File.Exists(p) ? File.ReadAllText(p) : ""; }

        /// <summary>
        /// UI-01 happy + recovery: the administrator signs in (password + code) and gets a session; a reload of the page sends
        /// the same session and is still signed in — also after the server restarted (a new Users over the same files);
        /// sign-out ends it at once, in this process and after a restart (the kept copy, stored as SHA-256 of the token, is
        /// gone). Signing in again gives a new session; the old one stays dead.
        /// </summary>
        [Fact]
        public void UI01_SignIn_AReloadAndARestartKeepIt_SignOutEndsItEverywhere_SignInAgainWorks()
        {
            var cfg = Config(); var users = new Users(cfg);
            var secret = Enrol(cfg);
            var si = Staff.Check(cfg, "admin", "Admin-Pass-1", Totp.Code(secret, DateTime.UtcNow), Remote, SystemClock.UtcNow);
            Assert.False(si.Enroll);
            var token = users.NewStaffSession(si, Staff.LocalTrusted(cfg, Remote));
            var s = users.GetSession(token);
            Assert.True(s != null && s.Admin && s.Login == "admin" && !s.Enroll);
            Assert.NotNull(users.GetSession(token));                                   // the reload
            Assert.Contains(Sha(Encoding.UTF8.GetBytes(token)), Kept(cfg));            // kept for a restart …
            Assert.DoesNotContain(token, Kept(cfg));                                   // … as a hash, never the token
            Assert.Equal("admin", new Users(cfg).GetSession(token).Login);             // the server restarted: still signed in

            users.EndSession(token);                                                   // sign-out
            Assert.Null(users.GetSession(token));
            Assert.Null(new Users(cfg).GetSession(token));
            Assert.DoesNotContain(Sha(Encoding.UTF8.GetBytes(token)), Kept(cfg));

            // sign in again (the next code): a new session; the old one stays dead
            var si2 = Staff.Check(cfg, "admin", "Admin-Pass-1", Totp.Code(secret, DateTime.UtcNow.AddSeconds(30)), Remote, SystemClock.UtcNow);
            var token2 = users.NewStaffSession(si2, false);
            Assert.NotEqual(token, token2);
            Assert.Equal("admin", users.GetSession(token2).Login);
            Assert.Null(users.GetSession(token));
        }

        /// <summary>
        /// UI-01 failure + boundary: a wrong password, an empty one, an unknown administrator give no session (401, the same
        /// code); a token never issued, an empty one, none at all are not a session. Away from the server a session in use
        /// lives on (sliding), and ends 2 hours after its LAST use: alive 1 minute before, dead 1 minute after.
        /// </summary>
        [Fact]
        public void UI01_WrongDetailsGiveNoSession_AnIdleSessionEndsExactlyTwoHoursAfterItsLastUse()
        {
            var now = DateTime.UtcNow; SystemClock.Use(() => now);
            var cfg = Config(); var users = new Users(cfg);
            var secret = Enrol(cfg);
            foreach (var bad in new[] { new[] { "admin", "Admin-Pass-2" }, new[] { "admin", "" }, new[] { "nobody", "Admin-Pass-1" } })
            {
                var e = Assert.Throws<ApiException>(() => Staff.Check(cfg, bad[0], bad[1], Totp.Code(secret, DateTime.UtcNow), Remote, now));
                Assert.Equal(401, e.Status); Assert.Equal("LOGIN", e.Code);
            }
            Assert.Null(users.GetSession(null)); Assert.Null(users.GetSession("")); Assert.Null(users.GetSession(Bytes.Hex(Bytes.Random(24))));

            var si = Staff.Check(cfg, "admin", "Admin-Pass-1", Totp.Code(secret, DateTime.UtcNow), Remote, now);
            var token = users.NewStaffSession(si, false);
            now = now.AddMinutes(100); Assert.NotNull(users.GetSession(token));        // in use: 2 more hours from here
            var lastUse = now;
            now = lastUse.AddHours(2).AddMinutes(-1); Assert.NotNull(users.GetSession(token));
            var lastUse2 = now;
            now = lastUse2.AddHours(2).AddMinutes(1);
            Assert.Null(users.GetSession(token));                                      // idle 2 h 1 min: signed out
            Assert.Null(new Users(cfg).GetSession(token));                             // also after a restart
        }

        // ================================================================== UI-02 the set editor

        sealed class Rig
        {
            public SystemConfig Cfg; public Users Users; public string Id, Src1, Src2, Skip, ProfilePath;
            public XElement Disk() { return XDocument.Load(ProfilePath).Root.Elements("BACKUP_SET").Single(e => (string)e.Attribute("ID") == Id); }
            public Msg Detail() { return SetControl.Detail(Users, "anna", Id); }
            public XElement Open() { return XElement.Parse(Detail()["set"]); }
            public BackupSetInfo Save(XElement edited, string version) { return SetControl.Save(Users, "anna", Id, BackupSetInfo.FromXml(edited), "admin", Remote, version); }
        }

        Rig NewRig(bool pilot)
        {
            var cfg = Config(pilot); var users = new Users(cfg);
            users.Create("anna", "Anna-Pass-1", "Anna", null, "COMPRESSED", "anna@example.com", Remote);
            var r = new Rig { Cfg = cfg, Users = users, Src1 = Path.Combine(root, "data", "Accounting"), Src2 = Path.Combine(root, "data", "Mail") };
            r.Skip = Path.Combine(r.Src1, "Temp");
            var s = users.CreateSet("anna", new BackupSetInfo { Name = "Docs", Sources = { r.Src1 }, Computer = "PC-ANNA", KeyType = "PASSWORD", KeyCheck = "check-value-1", KeySalt = "c2FsdA==" }, Remote);
            r.Id = s.Id; r.ProfilePath = Path.Combine(users.UserDir("anna"), "db", "Profile.xml");
            return r;
        }

        static XElement Filter(string name, string type, string include, string applyDir, params string[] patterns)
        {
            var f = new XElement("FILTER", new XAttribute("TYPE", type), new XAttribute("TOP_DIR", ""), new XAttribute("INCLUDE", include), new XAttribute("ONLY", "Y"),
                new XAttribute("APPLY_DIR", applyDir), new XAttribute("APPLY_FILE", applyDir == "Y" ? "N" : "Y"), new XAttribute("NAME", name), new XAttribute("ID", "1"));
            foreach (var p in patterns) f.Add(new XElement("PATTERN", p));
            return f;
        }

        public const string HebrewName = "Docs — הנהלת חשבונות 2026";

        /// <summary>What the set editor's tabs write into the set (app.js setEditor collectors), one change on every tab.</summary>
        public static XElement EveryTabChanged(XElement e, string src1, string src2, string skip)
        {
            // General
            e.SetAttributeValue("NAME", HebrewName); e.SetAttributeValue("ENABLED_SHADOW_COPY", "N"); e.SetAttributeValue("BSET_UPLOAD_PERMISSION", "N");
            // What to back up: two folders, one skipped inside, "skip system and temporary files" on
            e.Elements("SEL-SOURCE").Remove(); e.Elements("DE-SOURCE").Remove(); e.Elements("FILTER").Remove();
            e.Add(new XElement("SEL-SOURCE", src1), new XElement("SEL-SOURCE", src2), new XElement("DE-SOURCE", skip));
            e.Add(Filter("COMMON", "WILDCARD", "N", "N", "*.tmp", "~$*", "Thumbs.db"));
            // Schedule: Monday + Wednesday 07:15 and every day 19:45, stop after 4 hours, missed-run choices
            e.Elements("DAILY_SCHEDULE").Remove(); e.Elements("WEEKLY_SCHEDULE").Remove();
            var w = new XElement("WEEKLY_SCHEDULE", new XAttribute("ID", "1"), new XAttribute("NAME", "Backup Schedule"));
            foreach (var d in new[] { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" }) w.SetAttributeValue(d, d == "MON" || d == "WED" ? "Y" : "N");
            w.SetAttributeValue("HOUR", 7); w.SetAttributeValue("MINUTE", 15); w.SetAttributeValue("DURATION", 4); w.SetAttributeValue("BACKUP_TYPE", "FILE"); w.SetAttributeValue("ENABLED_SKIP_BACKUP", "N");
            e.Add(w, new XElement("DAILY_SCHEDULE", new XAttribute("ID", "2"), new XAttribute("NAME", "Backup Schedule 2"), new XAttribute("HOUR", 19), new XAttribute("MINUTE", 45), new XAttribute("DURATION", 4),
                new XAttribute("BACKUP_TYPE", "FILE"), new XAttribute("BACKUP_INTERVAL", -1), new XAttribute("ENABLED_SKIP_BACKUP", "N")));
            e.SetAttributeValue("RUN_MISSED", "N"); e.SetAttributeValue("RUN_MISSED_NET", "N"); e.SetAttributeValue("MISSED_DELAY_MINUTES", 17); e.SetAttributeValue("MISSED_MIN_HOURS", 9);
            // Backup method
            e.SetAttributeValue("DEFAULT_DELTA_TYPE", "D");
            // Versions kept: 12 backups, 4 weekly, 2 yearly
            e.Elements("RETENTION_POLICY").Remove();
            e.Add(new XElement("RETENTION_POLICY", new XAttribute("UNIT", "JOBS"), new XAttribute("PERIOD", 12),
                new XElement("RETENTION_SETTING", new XAttribute("TYPE", "WEEKLY"), new XAttribute("NAME", "WEEKLY"), new XAttribute("KEEP", 4), new XAttribute("ID", "1")),
                new XElement("RETENTION_SETTING", new XAttribute("TYPE", "YEARLY"), new XAttribute("NAME", "YEARLY"), new XAttribute("KEEP", 2), new XAttribute("ID", "1"))));
            // Filters: skip "~$…", back up only "….docx" folders
            e.Add(Filter("Filter", "START_WITH", "N", "N", "~$"), Filter("Filter", "END_WITH", "Y", "Y", ".docx"));
            // Encryption and compression; Resources
            e.SetAttributeValue("COMPRESSION", "FAST"); e.SetAttributeValue("BANDWIDTH_KBPS", 512); e.SetAttributeValue("BUSY_CPU_PERCENT", 70); e.SetAttributeValue("LOW_PRIORITY", "N");
            return e;
        }

        static string A(XElement e, string n) { return (string)e.Attribute(n); }

        /// <summary>The oracle of "every tab read back": the set as stored, read here with XDocument (not BackupSetInfo).</summary>
        public static void AssertEveryTabStored(XElement d, string src1, string src2, string skip)
        {
            Assert.Equal(HebrewName, A(d, "NAME"));
            Assert.Equal("N", A(d, "ENABLED_SHADOW_COPY")); Assert.Equal("N", A(d, "BSET_UPLOAD_PERMISSION"));
            Assert.Equal(new[] { src1, src2 }, d.Elements("SEL-SOURCE").Select(x => x.Value));
            Assert.Equal(new[] { skip }, d.Elements("DE-SOURCE").Select(x => x.Value));
            var times = d.Elements().Where(x => x.Name == "DAILY_SCHEDULE" || x.Name == "WEEKLY_SCHEDULE").ToList();
            Assert.Equal(2, times.Count);
            Assert.Equal("WEEKLY_SCHEDULE", times[0].Name.LocalName);
            Assert.Equal("N Y N Y N N N", string.Join(" ", new[] { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" }.Select(k => A(times[0], k))));
            Assert.Equal("7 15 4", A(times[0], "HOUR") + " " + A(times[0], "MINUTE") + " " + A(times[0], "DURATION"));
            Assert.Equal("DAILY_SCHEDULE", times[1].Name.LocalName);
            Assert.Equal("19 45 4", A(times[1], "HOUR") + " " + A(times[1], "MINUTE") + " " + A(times[1], "DURATION"));
            Assert.Equal("N N 17 9", A(d, "RUN_MISSED") + " " + A(d, "RUN_MISSED_NET") + " " + A(d, "MISSED_DELAY_MINUTES") + " " + A(d, "MISSED_MIN_HOURS"));
            Assert.Equal("D", A(d, "DEFAULT_DELTA_TYPE"));
            var rp = d.Elements("RETENTION_POLICY").Single();
            Assert.Equal("JOBS 12", A(rp, "UNIT") + " " + A(rp, "PERIOD"));
            Assert.Equal("WEEKLY=4 YEARLY=2", string.Join(" ", rp.Elements("RETENTION_SETTING").Select(x => A(x, "TYPE") + "=" + A(x, "KEEP")).OrderBy(x => x, StringComparer.Ordinal)));
            var filters = d.Elements("FILTER").Select(f => A(f, "NAME") + "|" + A(f, "TYPE") + "|" + A(f, "INCLUDE") + "|" + A(f, "APPLY_DIR") + "|" + A(f, "APPLY_FILE") + "|" + string.Join(",", f.Elements("PATTERN").Select(p => p.Value))).ToList();
            Assert.Equal(new[] { "COMMON|WILDCARD|N|N|Y|*.tmp,~$*,Thumbs.db", "Filter|START_WITH|N|N|Y|~$", "Filter|END_WITH|Y|Y|N|.docx" }, filters);
            Assert.Equal("FAST 512 70 N", A(d, "COMPRESSION") + " " + A(d, "BANDWIDTH_KBPS") + " " + A(d, "BUSY_CPU_PERCENT") + " " + A(d, "LOW_PRIORITY"));
        }

        /// <summary>
        /// UI-02 happy: a change on every tab of the set editor (general, folders, schedule, method, versions, filters,
        /// compression, resources) is saved and read back exactly — from Profile.xml on disk and from the editor's next
        /// open; the version moves; opening and saving again without a change changes nothing. With the pilot switch the
        /// in-scope set saves the same way and carries no refusal.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void UI02_EveryTabOfTheSetEditor_IsSavedAndReadBackExactly(bool pilot)
        {
            var r = NewRig(pilot);
            var before = r.Detail();
            Assert.Null(before["scope"]);                                              // in scope: no warning, "Back up now" offered
            var keyBefore = r.Disk().Element("ENCRYPTING_KEY").ToString();
            var saved = r.Save(EveryTabChanged(XElement.Parse(before["set"]), r.Src1, r.Src2, r.Skip), before["version"]);
            Assert.Equal(HebrewName, saved.Name);

            AssertEveryTabStored(r.Disk(), r.Src1, r.Src2, r.Skip);
            Assert.Equal("FILE", A(r.Disk(), "TYPE")); Assert.Equal("", A(r.Disk(), "ENGINE")); Assert.Equal("PC-ANNA", A(r.Disk(), "SCHEDULE_HOST"));
            Assert.Equal(keyBefore, r.Disk().Element("ENCRYPTING_KEY").ToString());       // the key is never touched by the editor
            var after = r.Detail();
            AssertEveryTabStored(XElement.Parse(after["set"]), r.Src1, r.Src2, r.Skip);  // the editor opens what was saved
            Assert.NotEqual(before["version"], after["version"]);
            Assert.Equal(SetControl.Version(r.Disk()), after["version"]);
            Assert.Null(after["scope"]);

            // opened again and saved without a change: the same settings, the same version
            r.Save(XElement.Parse(after["set"]), after["version"]);
            AssertEveryTabStored(r.Disk(), r.Src1, r.Src2, r.Skip);
            Assert.Equal(after["version"], r.Detail()["version"]);
        }

        static ApiException Refused(Action a, int status, string code)
        {
            var e = Assert.Throws<ApiException>(a);
            Assert.Equal(status, e.Status); Assert.Equal(code, e.Code);
            Assert.False(string.IsNullOrWhiteSpace(e.Message));
            return e;
        }

        /// <summary>
        /// UI-02 failure: every save the editor must refuse is refused with its reason — an empty name, 13 times a day, a
        /// maximum duration of 0 or 169 hours, keeping versions for 0 days, a set that does not exist, and (pilot) a command
        /// or a local copy added — and the stored set is byte-identical afterwards (SHA-256 of Profile.xml): the editor's
        /// next open shows the stored values, never the refused input.
        /// </summary>
        [Fact]
        public void UI02_ARefusedSave_ChangesNothingOnDisk_TheNextOpenShowsTheStoredValues()
        {
            var r = NewRig(true);
            var d = r.Detail(); var v = d["version"];
            Func<Action<XElement>, XElement> edit = f => { var e = XElement.Parse(d["set"]); f(e); return e; };
            var sha = Sha(r.ProfilePath);

            Refused(() => r.Save(edit(e => e.SetAttributeValue("NAME", "   ")), v), 400, "NAME");
            Refused(() => r.Save(edit(e => { for (int i = 0; i < 12; i++) e.Add(new XElement("DAILY_SCHEDULE", new XAttribute("HOUR", i), new XAttribute("MINUTE", 0), new XAttribute("DURATION", -1))); }), v), 400, "TIME");
            Refused(() => r.Save(edit(e => e.Elements("DAILY_SCHEDULE").Concat(e.Elements("WEEKLY_SCHEDULE")).First().SetAttributeValue("DURATION", 0)), v), 400, "DURATION");
            Refused(() => r.Save(edit(e => e.Elements("DAILY_SCHEDULE").Concat(e.Elements("WEEKLY_SCHEDULE")).First().SetAttributeValue("DURATION", 169)), v), 400, "DURATION");
            Refused(() => r.Save(edit(e => e.Element("RETENTION_POLICY").SetAttributeValue("PERIOD", 0)), v), 400, "RETENTION");
            Refused(() => SetControl.Save(r.Users, "anna", "1700000000999", BackupSetInfo.FromXml(XElement.Parse(d["set"])), "admin", Remote, null), 404, "NO_SET");
            // PILOT-010: the Commands and Destination tabs are not offered; a save that adds them anyway is refused
            var cmd = Refused(() => r.Save(edit(e => e.Add(new XElement("PRE_CMD", new XAttribute("PATH", "net stop sql"), new XAttribute("STOP_ON_FAILURE", "Y")))), v), 403, "SCOPE");
            Assert.Contains("commands", cmd.Message);
            Refused(() => r.Save(edit(e => { e.Elements("EXTRA_LOCAL_BACKUP").Remove(); e.Add(new XElement("EXTRA_LOCAL_BACKUP", new XAttribute("ENABLED", "Y"), new XAttribute("BACKUP_TO", Path.Combine(root, "usb")), new XAttribute("PERIOD", 7))); }), v), 403, "SCOPE");
            Refused(() => r.Save(edit(e => { e.SetAttributeValue("DEST_MODE", "BOTH"); e.Element("EXTRA_LOCAL_BACKUP").SetAttributeValue("BACKUP_TO", Path.Combine(root, "usb")); }), v), 403, "SCOPE");

            Assert.Equal(sha, Sha(r.ProfilePath));                                   // nothing was written
            var again = r.Detail();
            Assert.Equal(v, again["version"]); Assert.Equal(d["set"], again["set"]);
            Assert.Equal("Docs", A(XElement.Parse(again["set"]), "NAME"));
            Assert.Empty(XElement.Parse(again["set"]).Elements("PRE_CMD"));
        }

        /// <summary>
        /// UI-02 boundary: the limits themselves are taken — a maximum duration of exactly 1 and 168 hours and "no limit",
        /// exactly 12 times a day, versions kept for exactly 1 day — each stored as sent. What the editor may not change
        /// (type, engine, encryption key, computer) stays as it was even when a save sends other values: a pilot set can
        /// never be turned into a restic set from the editor.
        /// </summary>
        [Fact]
        public void UI02_TheLimitsThemselvesAreTaken_TypeEngineKeyAndComputerNeverChangeThroughTheEditor()
        {
            var r = NewRig(true);
            foreach (var dur in new[] { 1, 168, -1 })
            {
                var d = r.Detail(); var e = XElement.Parse(d["set"]);
                e.Elements("DAILY_SCHEDULE").Concat(e.Elements("WEEKLY_SCHEDULE")).First().SetAttributeValue("DURATION", dur);
                r.Save(e, d["version"]);
                Assert.Equal(dur.ToString(CultureInfo.InvariantCulture), A(r.Disk().Elements("DAILY_SCHEDULE").Concat(r.Disk().Elements("WEEKLY_SCHEDULE")).First(), "DURATION"));
            }
            {
                var d = r.Detail(); var e = XElement.Parse(d["set"]);
                e.Elements("DAILY_SCHEDULE").Remove(); e.Elements("WEEKLY_SCHEDULE").Remove();
                for (int i = 0; i < 12; i++) e.Add(new XElement("DAILY_SCHEDULE", new XAttribute("HOUR", i * 2), new XAttribute("MINUTE", 5 * i), new XAttribute("DURATION", -1)));
                e.Element("RETENTION_POLICY").SetAttributeValue("UNIT", "DAYS"); e.Element("RETENTION_POLICY").SetAttributeValue("PERIOD", 1);
                r.Save(e, d["version"]);
                var times = r.Disk().Elements("DAILY_SCHEDULE").Select(x => A(x, "HOUR") + ":" + A(x, "MINUTE")).ToList();
                Assert.Equal(Enumerable.Range(0, 12).Select(i => (i * 2) + ":" + (5 * i)), times);
                Assert.Equal("DAYS 1", A(r.Disk().Element("RETENTION_POLICY"), "UNIT") + " " + A(r.Disk().Element("RETENTION_POLICY"), "PERIOD"));
            }
            {
                var keyBefore = r.Disk().Element("ENCRYPTING_KEY").ToString();
                var d = r.Detail(); var e = XElement.Parse(d["set"]);
                e.SetAttributeValue("TYPE", "FILE"); e.SetAttributeValue("ENGINE", "RESTIC"); e.SetAttributeValue("SCHEDULE_HOST", "PC-OTHER");
                e.Element("ENCRYPTING_KEY").SetAttributeValue("KEY", "forged-check"); e.Element("ENCRYPTING_KEY").SetAttributeValue("KEY_TYPE", "DEFAULT");
                r.Save(e, d["version"]);
                Assert.Equal("FILE", A(r.Disk(), "TYPE")); Assert.Equal("", A(r.Disk(), "ENGINE")); Assert.Equal("PC-ANNA", A(r.Disk(), "SCHEDULE_HOST"));
                Assert.Equal(keyBefore, r.Disk().Element("ENCRYPTING_KEY").ToString());
                Assert.Null(Scope.Refusal(BackupSetInfo.FromXml(r.Disk())));          // still inside the pilot
            }
        }

        /// <summary>
        /// UI-02 concurrency: two technicians open the same set. The first saves a new name; the second saves a change made
        /// on the settings as they were before (the old version) — refused 409 CHANGED, and the first one's change is what is
        /// stored. The second reloads (the new version) and makes the change again: both changes are stored.
        /// </summary>
        [Fact]
        public void UI02_TwoTechnicians_TheStaleSaveIsRefused_TheFirstChangeStays_AfterAReloadBothAreKept()
        {
            var r = NewRig(true);
            var first = r.Detail(); var second = r.Detail();
            Assert.Equal(first["version"], second["version"]);
            var e1 = XElement.Parse(first["set"]); e1.SetAttributeValue("NAME", "Changed by Dana");
            r.Save(e1, first["version"]);
            var sha = Sha(r.ProfilePath);
            var e2 = XElement.Parse(second["set"]); e2.SetAttributeValue("COMPRESSION", "NONE");
            Refused(() => r.Save(e2, second["version"]), 409, "CHANGED");
            Assert.Equal(sha, Sha(r.ProfilePath));
            Assert.Equal("Changed by Dana", A(r.Disk(), "NAME")); Assert.Equal("MAX", A(r.Disk(), "COMPRESSION"));
            var reload = r.Detail(); var e3 = XElement.Parse(reload["set"]); e3.SetAttributeValue("COMPRESSION", "NONE");
            r.Save(e3, reload["version"]);
            Assert.Equal("Changed by Dana", A(r.Disk(), "NAME")); Assert.Equal("NONE", A(r.Disk(), "COMPRESSION"));
        }

        // ================================================================== UI-03 the truth after failures

        static Msg Body(string result, int n = 0) { var b = new Msg().Set("new", n).Set("upd", 0).Set("del", 0).Set("bytes", n * 100); if (result != null) b.Set("result", result); return b; }

        /// <summary>
        /// UI-03 failure: the colour of every run in the tasks / dashboard / set reports is the truth — success ok, success
        /// with warnings warn, stopped by the user stopped, and EVERY other end (a system error, the quota, a missing
        /// source, an unknown or empty result, a run that never told its result) bad; a restore with a failed file and a
        /// restore test that checked nothing are bad. Nothing failed is ever shown green.
        /// </summary>
        [Fact]
        public void UI03_AFailedOrSilentRun_IsNeverGreen_EveryResultHasItsTrueColour()
        {
            var cfg = Config(); var runs = new RunLog(cfg);
            var expected = new Dictionary<string, string>
            {
                { "BS_STOP_SUCCESS", "ok" }, { "BS_STOP_SUCCESS_WITH_WARNING", "warn" }, { "BS_STOP_BY_USER", "stopped" },
                { "BS_STOP_BY_SYSTEM_ERROR", "bad" }, { "BS_STOP_BY_QUOTA_EXCEEDED", "bad" }, { "BS_STOP_SUCCESS_WITH_ERROR", "bad" }, { "BS_STOP_MISSED_BACKUP", "bad" },
                { "SOMETHING_NEW", "bad" }, { "", "bad" }
            };
            foreach (var kv in expected) Assert.True(RunLog.Status("Backup", kv.Key) == kv.Value, kv.Key + " shown as " + RunLog.Status("Backup", kv.Key));
            Assert.Equal("bad", RunLog.Status("Backup", null));

            var t0 = new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc);
            var set = new BackupSetInfo { Id = "1700000000123", Name = "Docs", Computer = "PC-ANNA" };
            int i = 0;
            foreach (var kv in expected) runs.Add(t0.AddMinutes(i++), "anna", set, "Backup", RunId.From(t0.AddMinutes(i)), Body(kv.Key.Length == 0 ? null : kv.Key, 1), null);
            runs.Add(t0.AddMinutes(30), "anna", set, "Restore", "r1", new Msg().Set("failed", 1).Set("ok", 9), null);
            runs.Add(t0.AddMinutes(31), "anna", set, "Restore", "r2", new Msg().Set("failed", 0).Set("ok", 9), null);
            runs.Add(t0.AddMinutes(32), "anna", set, "RestoreTest", "t1", new Msg().Set("checked", 0), null);
            var got = runs.Since(t0.AddHours(-1), t0.AddHours(1));
            Assert.Equal(expected.Count + 3, got.Count);
            var backups = got.Where(x => x["kind"] == "Backup").OrderBy(x => x.Long("time")).ToList();
            Assert.Equal(expected.Values.ToList(), backups.Select(x => x["status"]).ToList());
            Assert.Equal("BS_STOP_BY_SYSTEM_ERROR", backups.Last()["result"]);          // a run that never said how it ended is a failure
            Assert.Equal("bad", got.Single(x => x["job"] == "r1")["status"]);
            Assert.Equal("ok", got.Single(x => x["job"] == "r2")["status"]);
            Assert.Equal("warn", got.Single(x => x["job"] == "t1")["status"]);                   // nothing to compare: not green, and not a failed test (R1; bug 110)
            Assert.Equal("NOT_CHECKED", got.Single(x => x["job"] == "t1")["result"]);
            Assert.Equal(1, got.Count(x => x["status"] == "ok" && x["kind"] == "Backup"));
        }

        /// <summary>
        /// UI-03 recovery + boundary: after a failed run, the next good run is green — and the failure stays in the history
        /// (newest first, both there, nothing rewritten). A line cut in the middle (a crash while writing) is left out, never
        /// shown as a run; a set name with a tab or a line break does not shift the columns (the status is still right);
        /// a run exactly at the start of the window is in it, one a millisecond before is not.
        /// </summary>
        [Fact]
        public void UI03_AfterAFailure_TheNextGoodRunIsGreen_TheFailureStaysInTheHistory_ACutLineIsNeverARun()
        {
            var cfg = Config(); var runs = new RunLog(cfg);
            var t0 = new DateTime(2026, 10, 6, 22, 0, 0, DateTimeKind.Utc);
            var set = new BackupSetInfo { Id = "1700000000123", Name = "Docs\tQ3\nfinal", Computer = "PC-ANNA" };
            runs.Add(t0, "anna", set, "Backup", RunId.From(t0), Body("BS_STOP_BY_SYSTEM_ERROR"), null);
            var file = Path.Combine(cfg.SystemHome, "runs", "20261006.log");
            File.AppendAllText(file, RunId.UnixMs(t0.AddMinutes(1)).ToString(CultureInfo.InvariantCulture) + "\tanna\t1700000000123\tDocs\tPC-ANNA\tBackup\t2026-10-06-22-01-00\tBS_STOP_SUC");   // cut short, no line end
            File.AppendAllText(file, "\n");
            runs.Add(t0.AddHours(1), "anna", set, "Backup", RunId.From(t0.AddHours(1)), Body("BS_STOP_SUCCESS", 5), null);
            var got = runs.Since(t0, t0.AddHours(2));
            Assert.Equal(new[] { "ok", "bad" }, got.Select(x => x["status"]));          // newest first: green now, the failure kept
            Assert.Equal(new[] { "BS_STOP_SUCCESS", "BS_STOP_BY_SYSTEM_ERROR" }, got.Select(x => x["result"]));
            Assert.All(got, x => Assert.Equal("Backup", x["kind"]));
            Assert.Equal("Docs Q3 final", got[0]["setName"]);
            Assert.Equal("5", got[0]["new"]);
            Assert.Single(runs.Since(t0.AddMilliseconds(1), t0.AddHours(2)));           // the window's start is exact
            Assert.Equal(2, runs.Since(t0, t0.AddHours(2), l => l == "anna").Count);
            Assert.Empty(runs.Since(t0, t0.AddHours(2), l => l == "someone-else"));
        }
    }
}
