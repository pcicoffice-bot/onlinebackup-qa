using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// TICKETS-010: service calls built into the backup server. The rules are those of ITSguard's calls (TCK-*), carried
    /// over as they are: the same statuses and urgencies, SLA hours by urgency (green under 50% of the time, yellow under
    /// 100%, red = late), a call with no handler goes to the default handler (TCK-390), a call is closed only with a handler
    /// (TCK-350), "move to a date" brings it back later with a new SLA time (TCK-280/285), every change of status, urgency,
    /// handler or field goes to the call's log (TCK-055), a revision stops two people overwriting each other, a deleted call
    /// is kept and can be restored (TCK-290/330), and a journal line for every change (TCK-320).
    /// Only what a backup product needs: a call belongs to a customer (login), optionally a computer and a backup set.
    /// Calls opened by the server itself (a failed / missed backup, ransomware, quota…) close themselves when the backup
    /// succeeds — deleted when nobody worked on them, marked "resolved" when someone did (ITSguard RTK-310/311).
    /// One store: &lt;system&gt;\tickets\tickets.xml, written atomically under one lock.
    /// </summary>
    public sealed class Tickets
    {
        public static readonly string[] Statuses = { "New", "InProgress", "Waiting", "Deferred", "Resolved", "ToBill", "Closed" };
        public static readonly string[] Done = { "Resolved", "ToBill", "Closed" };
        public static readonly string[] Priorities = { "Urgent", "High", "Normal", "Low" };
        public static readonly string[] AutoKinds = { "fail", "missed", "warn", "offline", "quota", "restoretest", "ransom" };
        public const string SystemUser = "system";

        readonly SystemConfig cfg;
        readonly object gate = new object();
        public Func<DateTime> Clock = () => SystemClock.UtcNow;
        /// <summary>A call got a new handler (TCN: mail on assignment) — the call and who assigned it. Never stops the save.</summary>
        public Action<Msg, string> Assigned;

        public Tickets(SystemConfig cfg) { this.cfg = cfg; }

        string Home { get { var d = Path.Combine(cfg.SystemHome, "tickets"); Directory.CreateDirectory(d); return d; } }
        string StorePath { get { return Path.Combine(Home, "tickets.xml"); } }
        string BinPath { get { return Path.Combine(Home, "deleted.xml"); } }

        public static bool IsDone(string status) { return Done.Contains(status); }
        static string Iso(DateTime t) { return t.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture); }
        static DateTime At(string iso) { return DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(); }

        Msg Load(string path) { return File.Exists(path) ? Msg.Parse(OnlineBackup.Core.Atomic.ReadAllBytes(path)) : new Msg().Set("next", 1); }
        void Write(string path, Msg m) { Atomic.WriteBytes(path, m.ToBytes()); }

        // ------------------------------------------------------------------ settings

        /// <summary>SLA hours by urgency, the default handler, and when the server opens a call by itself (TICKETS-020).</summary>
        public sealed class Settings
        {
            public Dictionary<string, int> Hours = new Dictionary<string, int> { { "Urgent", 4 }, { "High", 8 }, { "Normal", 24 }, { "Low", 72 } };
            public string DefaultAssignee = "";
            /// <summary>Auto calls: failures in a row (0 = off), hours without a backup, warnings in a row, days the client did
            /// not connect, quota percent, restore test, ransomware — and the urgency of each.</summary>
            public int Fail = 2, MissedHours = 48, Warn = 0, OfflineDays = 3, QuotaPercent = 90;
            public bool RestoreTest = true, Ransom = true, AutoClose = true, ClientCalls = true;
            public Dictionary<string, string> Urgency = new Dictionary<string, string> { { "fail", "Urgent" }, { "missed", "High" }, { "warn", "Low" }, { "offline", "Normal" }, { "quota", "Normal" }, { "restoretest", "High" }, { "ransom", "Urgent" } };
        }

        static readonly string[] AutoKeys = { "Fail", "MissedHours", "Warn", "OfflineDays", "QuotaPercent" };

        public Settings Read(Profile customer = null)
        {
            var s = new Settings();
            var e = cfg.Doc.Root.Element("TICKETS");
            Func<string, string> g = (k) => e == null ? null : (string)e.Attribute(k);
            foreach (var p in Priorities) { int h; if (int.TryParse(g("HOURS_" + p.ToUpperInvariant()), out h) && h >= 1 && h <= 2000) s.Hours[p] = h; }
            s.DefaultAssignee = g("DEFAULT_ASSIGNEE") ?? "";
            ReadAuto(s, g);
            foreach (var k in s.Urgency.Keys.ToList()) { var u = g("URGENCY_" + k.ToUpperInvariant()); if (Priorities.Contains(u)) s.Urgency[k] = u; }
            // a customer may have thresholds of its own (TICKET_FAIL, TICKET_MISSEDHOURS…), else the general ones
            if (customer != null) ReadAuto(s, (k) => customer.Get("TICKET_" + k));
            return s;
        }

        static void ReadAuto(Settings s, Func<string, string> g)
        {
            int v;
            if (int.TryParse(g("FAIL"), out v) && v >= 0 && v <= 20) s.Fail = v;
            if (int.TryParse(g("MISSEDHOURS"), out v) && v >= 0 && v <= 24 * 60) s.MissedHours = v;
            if (int.TryParse(g("WARN"), out v) && v >= 0 && v <= 50) s.Warn = v;
            if (int.TryParse(g("OFFLINEDAYS"), out v) && v >= 0 && v <= 365) s.OfflineDays = v;
            if (int.TryParse(g("QUOTAPERCENT"), out v) && v >= 0 && v <= 100) s.QuotaPercent = v;
            var b = g("RESTORETEST"); if (b != null) s.RestoreTest = b == "Y";
            b = g("RANSOM"); if (b != null) s.Ransom = b == "Y";
            b = g("AUTOCLOSE"); if (b != null) s.AutoClose = b == "Y";
            b = g("CLIENTCALLS"); if (b != null) s.ClientCalls = b == "Y";
        }

        /// <summary>Saves the general settings from the admin site (only the fields given).</summary>
        public void SaveSettings(Msg b)
        {
            lock (cfg)
            {
                var e = cfg.Doc.Root.Element("TICKETS");
                if (e == null) { e = new System.Xml.Linq.XElement("TICKETS"); cfg.Doc.Root.Add(e); }
                foreach (var p in Priorities)
                {
                    var v = b["hours" + p]; if (v == null) continue;
                    int h; if (!int.TryParse(v, out h) || h < 1 || h > 2000) throw new ApiException(400, "HOURS", "SLA hours must be 1 to 2000.");
                    e.SetAttributeValue("HOURS_" + p.ToUpperInvariant(), h);
                }
                if (b["defaultAssignee"] != null) e.SetAttributeValue("DEFAULT_ASSIGNEE", b["defaultAssignee"].Trim());
                foreach (var k in new[] { "FAIL", "MISSEDHOURS", "WARN", "OFFLINEDAYS", "QUOTAPERCENT" })
                {
                    var v = b[k.ToLowerInvariant()]; if (v == null) continue;
                    int n; if (!int.TryParse(v, out n) || n < 0) throw new ApiException(400, "VALUE", "A threshold is not valid: " + k.ToLowerInvariant());
                    e.SetAttributeValue(k, n);
                }
                foreach (var k in new[] { "RESTORETEST", "RANSOM", "AUTOCLOSE", "CLIENTCALLS" }) { var v = b[k.ToLowerInvariant()]; if (v != null) e.SetAttributeValue(k, b.Bool(k.ToLowerInvariant()) ? "Y" : "N"); }
                foreach (var k in AutoKinds) { var v = b["urgency" + k]; if (v == null) continue; if (!Priorities.Contains(v)) throw new ApiException(400, "PRIORITY", "Unknown urgency."); e.SetAttributeValue("URGENCY_" + k.ToUpperInvariant(), v); }
                cfg.Save();
            }
        }

        public Msg SettingsMsg(Settings s)
        {
            var m = new Msg().Set("defaultAssignee", s.DefaultAssignee).Set("fail", s.Fail).Set("missedhours", s.MissedHours).Set("warn", s.Warn).Set("offlinedays", s.OfflineDays)
                .Set("quotapercent", s.QuotaPercent).Set("restoretest", s.RestoreTest ? 1 : 0).Set("ransom", s.Ransom ? 1 : 0).Set("autoclose", s.AutoClose ? 1 : 0).Set("clientcalls", s.ClientCalls ? 1 : 0);
            foreach (var kv in s.Hours) m.Set("hours" + kv.Key, kv.Value);
            foreach (var kv in s.Urgency) m.Set("urgency" + kv.Key, kv.Value);
            return m;
        }

        // ------------------------------------------------------------------ SLA

        /// <summary>TCK-030: the SLA state of one call — ok / warn (half the time used) / late / wait / done.</summary>
        public Msg Sla(Msg t, DateTime? nowUtc = null)
        {
            var now = nowUtc ?? Clock();
            var open = At(t["slaStart"] ?? t["opened"]); var due = At(t["due"]);
            if (IsDone(t["status"])) { var end = t["resolved"] != null ? At(t["resolved"]) : now; return new Msg().Set("state", "done").Set("met", end <= due ? 1 : 0); }
            if (t["status"] == "Waiting") return new Msg().Set("state", "wait");
            if (t["status"] == "Deferred") return new Msg().Set("state", "wait").Set("followUp", t["followUp"]);
            var left = due - now; double total = (due - open).TotalMinutes, used = (now - open).TotalMinutes;
            if (left.TotalMinutes <= 0) return new Msg().Set("state", "late").Set("minutes", (long)-left.TotalMinutes);
            return new Msg().Set("state", total > 0 && used / total >= 0.5 ? "warn" : "ok").Set("minutes", (long)left.TotalMinutes);
        }

        // ------------------------------------------------------------------ calls

        static void Note(Msg t, string kind, string text, string user, string ip, DateTime now, string from = null, string to = null)
        {
            t.Add("notes", new Msg().Set("time", Iso(now)).Set("user", user).Set("ip", ip).Set("kind", kind).Set("text", text).Set("from", from).Set("to", to));
            if (user != SystemUser) t.Set("worked", 1);
        }

        /// <summary>
        /// TCK-020: opens or changes one call. In: id (empty = new), revision, login, computer, set, subject, description,
        /// priority, status, channel, assignee, followUp (yyyy-MM-ddTHH:mm, when moved to a date), note.
        /// </summary>
        public Msg Save(Msg inp, string user, string ip)
        {
            var subject = (inp["subject"] ?? "").Trim();
            if (subject.Length == 0) throw new ApiException(400, "SUBJECT", "Write a subject for the call.");
            if (subject.Length > 200) throw new ApiException(400, "SUBJECT", "The subject is too long.");
            var priority = inp["priority"] ?? "Normal"; var status = inp["status"] ?? "New";
            if (!Priorities.Contains(priority)) throw new ApiException(400, "PRIORITY", "Unknown urgency.");
            if (!Statuses.Contains(status)) throw new ApiException(400, "STATUS", "Unknown status.");
            Msg t; string action; string assignedTo = null;
            lock (gate)
            {
                var d = Load(StorePath); var now = Clock(); var st = Read(); var assign = (inp["assignee"] ?? "").Trim();
                var id = inp["id"];
                t = string.IsNullOrEmpty(id) ? null : d.List("t").FirstOrDefault(x => x["id"] == id);
                if (!string.IsNullOrEmpty(id) && t == null) throw new ApiException(404, "TICKET", "The call was not found.");
                bool fresh = t == null;
                if (fresh)
                {
                    long n = Math.Max(1, d.Long("next", 1));
                    t = new Msg().Set("id", n.ToString(CultureInfo.InvariantCulture)).Set("rev", 0).Set("opened", Iso(now)).Set("openedBy", user).Set("source", inp["source"] ?? "manual").Set("kind", inp["kind"]);
                    d.Set("next", n + 1); d.Add("t", t);
                    Note(t, "open", null, user, ip, now);
                    if (assign.Length == 0 && st.DefaultAssignee.Length > 0) { assign = st.DefaultAssignee; Note(t, "assign", "default", user, ip, now, null, assign); }
                }
                else if (t.Int("rev") != inp.Int("revision", -1)) throw new ApiException(409, "REVISION", "Someone else changed the call meanwhile. Close it and open it again.");
                var old = t["status"];
                if (IsDone(status) && (!IsDone(old) || fresh) && assign.Length == 0) throw new ApiException(400, "ASSIGNEE", "A call can be closed only with a handler. Choose a handler, then close it.");
                string follow = null;
                if (status == "Deferred")
                {
                    DateTime fd; var raw = (inp["followUp"] ?? "").Trim();
                    if (!DateTime.TryParseExact(raw, new[] { "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out fd))
                        throw new ApiException(400, "FOLLOWUP", "Choose the date and hour to come back to the call.");
                    if (raw.Length == 10) fd = fd.AddHours(8);
                    if (fd <= now) throw new ApiException(400, "FOLLOWUP", "The date must be in the future.");
                    if (fd > now.AddDays(730)) throw new ApiException(400, "FOLLOWUP", "The date is too far.");
                    follow = Iso(fd);
                    if (follow != t["followUp"]) Note(t, "defer", null, user, ip, now, null, follow);
                }
                if (!fresh)
                {
                    if (old != status) Note(t, "status", null, user, ip, now, old, status);
                    if (t["priority"] != priority) Note(t, "priority", null, user, ip, now, t["priority"], priority);
                    if ((t["assignee"] ?? "") != assign) Note(t, "assign", null, user, ip, now, t["assignee"], assign);
                    foreach (var f in new[] { "subject", "description", "login", "computer", "set", "channel" })
                    {
                        var nv = f == "subject" ? subject : (inp[f] ?? "").Trim(); var ov = t[f] ?? "";
                        if (ov != nv) Note(t, "change", f, user, ip, now, Cut(ov), Cut(nv));
                    }
                }
                if ((t["assignee"] ?? "") != assign && assign.Length > 0 && !string.Equals(assign, user, StringComparison.OrdinalIgnoreCase)) assignedTo = assign;
                t.Set("followUp", follow);
                if (IsDone(status)) { if (t["resolved"] == null) t.Set("resolved", Iso(now)); } else t.Set("resolved", null);
                if (t["priority"] != priority) t.Set("due", Iso(At(t["slaStart"] ?? t["opened"]).AddHours(st.Hours[priority])));
                t.Set("subject", subject).Set("description", (inp["description"] ?? "").Trim()).Set("login", (inp["login"] ?? "").Trim()).Set("computer", (inp["computer"] ?? "").Trim())
                    .Set("set", (inp["set"] ?? "").Trim()).Set("channel", (inp["channel"] ?? "").Trim()).Set("priority", priority).Set("status", status).Set("assignee", assign)
                    .Set("updated", Iso(now)).Set("updatedBy", user).Set("rev", t.Int("rev") + 1);
                var note = (inp["note"] ?? "").Trim();
                if (note.Length > 0) Note(t, "note", note, user, ip, now);
                if (user != SystemUser && !fresh) t.Set("worked", 1);
                Write(StorePath, d);
                action = fresh ? "open" : "save";
            }
            Journal(user, action, t);
            if (assignedTo != null && Assigned != null) try { Assigned(t, user); } catch (Exception) { }
            return t;
        }

        static string Cut(string v) { v = (v ?? "").Replace('\r', ' ').Replace('\n', ' '); return v.Length > 80 ? v.Substring(0, 77) + "..." : v; }

        /// <summary>Adds a note (and optionally a picture name) without changing anything else.</summary>
        public Msg AddNote(string id, string text, string user, string ip)
        {
            text = (text ?? "").Trim(); if (text.Length == 0) throw new ApiException(400, "NOTE", "Write the note.");
            Msg t;
            lock (gate)
            {
                var d = Load(StorePath); t = d.List("t").FirstOrDefault(x => x["id"] == id);
                if (t == null) throw new ApiException(404, "TICKET", "The call was not found.");
                Note(t, "note", text, user, ip, Clock()); t.Set("rev", t.Int("rev") + 1).Set("updated", Iso(Clock())).Set("updatedBy", user);
                Write(StorePath, d);
            }
            Journal(user, "note", t);
            return t;
        }

        public Msg Get(string id) { lock (gate) return Load(StorePath).List("t").FirstOrDefault(x => x["id"] == id); }

        /// <summary>TCK-040: the list — scope open / future / closed / tobill / mine / late / auto / all, a customer, a text.</summary>
        public List<Msg> List(string scope = "open", string login = null, string text = null, string me = null, Func<Msg, bool> visible = null)
        {
            Wake();
            List<Msg> all; lock (gate) all = Load(StorePath).List("t").ToList();
            var now = Clock(); var q = (text ?? "").Trim().ToLowerInvariant();
            return all.Where(t =>
            {
                if (visible != null && !visible(t)) return false;
                var s = t["status"]; bool fut = s == "Deferred", open = !IsDone(s) && !fut;
                bool ok;
                switch (scope)
                {
                    case "all": ok = true; break;
                    case "future": ok = fut; break;
                    case "closed": ok = IsDone(s) && s != "ToBill"; break;
                    case "tobill": ok = s == "ToBill"; break;
                    case "mine": ok = open && string.Equals(t["assignee"], me, StringComparison.OrdinalIgnoreCase); break;
                    case "late": ok = open && Sla(t, now)["state"] == "late"; break;
                    case "auto": ok = open && t["source"] == "auto"; break;
                    case "waiting": ok = s == "Waiting" || fut; break;
                    default: ok = open; break;
                }
                if (!ok || (!string.IsNullOrEmpty(login) && t["login"] != login)) return false;
                return q.Length == 0 || string.Join(" ", new[] { t["id"], t["login"], t["computer"], t["set"], t["subject"], t["description"], t["assignee"] }).ToLowerInvariant().Contains(q);
            }).OrderByDescending(t => t["opened"], StringComparer.Ordinal).ThenByDescending(t => t.Long("id")).ToList();
        }

        /// <summary>TCK-285: calls whose follow-up date came are back "in progress" with a new SLA time from now.</summary>
        public List<Msg> Wake()
        {
            var back = new List<Msg>();
            lock (gate)
            {
                var d = Load(StorePath); var now = Clock(); var st = Read();
                foreach (var t in d.List("t").Where(x => x["status"] == "Deferred" && x["followUp"] != null && At(x["followUp"]) <= now))
                {
                    t.Set("status", "InProgress").Set("slaStart", Iso(now)).Set("due", Iso(now.AddHours(st.Hours[t["priority"]]))).Set("followUp", null).Set("updated", Iso(now)).Set("updatedBy", SystemUser).Set("rev", t.Int("rev") + 1);
                    Note(t, "back", null, SystemUser, null, now); back.Add(t);
                }
                if (back.Count > 0) Write(StorePath, d);
            }
            foreach (var t in back) Journal(SystemUser, "back", t);
            return back;
        }

        /// <summary>TCK-290: deleting keeps a full copy in deleted.xml with who and when; TCK-330 restores it.</summary>
        public Msg Delete(string id, int revision, string user)
        {
            Msg t;
            lock (gate)
            {
                var d = Load(StorePath); t = d.List("t").FirstOrDefault(x => x["id"] == id);
                if (t == null) throw new ApiException(404, "TICKET", "The call was not found.");
                if (revision >= 0 && t.Int("rev") != revision) throw new ApiException(409, "REVISION", "Someone else changed the call meanwhile. Close it and open it again.");
                var bin = Load(BinPath); bin.Add("t", new Msg().Set("deleted", Iso(Clock())).Set("by", user).Add("call", t)); Write(BinPath, bin);
                d.List("t").Remove(t); Write(StorePath, d);
            }
            Journal(user, "delete", t);
            return t;
        }

        public List<Msg> Deleted() { lock (gate) return Load(BinPath).List("t").ToList(); }

        public Msg Restore(int index, string user)
        {
            Msg t;
            lock (gate)
            {
                var bin = Load(BinPath); var items = bin.List("t");
                if (index < 0 || index >= items.Count) throw new ApiException(404, "TICKET", "The call is not in the deleted calls.");
                var x = items[index]; t = x.List("call").First(); var d = Load(StorePath);
                if (d.List("t").Any(y => y["id"] == t["id"])) { long n = d.Long("next", 1); t.Set("id", n.ToString(CultureInfo.InvariantCulture)); d.Set("next", n + 1); }
                Note(t, "restored", null, user, null, Clock(), x["by"], null); t.Set("rev", t.Int("rev") + 1);
                d.Add("t", t); items.RemoveAt(index); Write(StorePath, d); Write(BinPath, bin);
            }
            Journal(user, "restore", t);
            return t;
        }

        /// <summary>TCK-320: one line per change, so a call can always be traced. Never stops the save.</summary>
        void Journal(string user, string action, Msg t)
        {
            try
            {
                var p = Path.Combine(Home, "journal.log");
                if (File.Exists(p) && new FileInfo(p).Length > 5 << 20) File.Copy(p, p + ".old", true);
                if (File.Exists(p + ".old") && File.Exists(p) && new FileInfo(p).Length > 5 << 20) File.Delete(p);
                var line = "[" + SystemClock.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "] " + user + " | " + action + " | " + t["id"] + " | " + t["status"] + " | " + t["assignee"] + " | " + t["login"] + " | " + Cut(t["subject"]);
                File.AppendAllText(p, line + "\r\n", new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------ calls the server opens and closes by itself

        /// <summary>
        /// A backup problem: opens a call of this kind for the customer's set — or, when one is already open for the same
        /// set, adds a note to it (never a second call). Returns the call, or null when the rule is off.
        /// </summary>
        public Msg Auto(string kind, string login, string setId, string setName, string computer, string subject, string detail, Profile customer = null)
        {
            var st = Read(customer);
            if (!Enabled(st, kind)) return null;
            Msg open;
            lock (gate) open = Load(StorePath).List("t").FirstOrDefault(t => !IsDone(t["status"]) && t["source"] == "auto" && t["login"] == login && (t["set"] ?? "") == (setId ?? ""));
            if (open != null) return AddNote(open["id"], detail ?? subject, SystemUser, null);
            return Save(new Msg().Set("source", "auto").Set("kind", kind).Set("login", login).Set("set", setId).Set("computer", computer).Set("subject", subject)
                .Set("description", detail).Set("priority", st.Urgency[kind]).Set("status", "New").Set("channel", "auto-" + kind), SystemUser, null);
        }

        static bool Enabled(Settings s, string kind)
        {
            switch (kind)
            {
                case "fail": return s.Fail > 0;
                case "missed": return s.MissedHours > 0;
                case "warn": return s.Warn > 0;
                case "offline": return s.OfflineDays > 0;
                case "quota": return s.QuotaPercent > 0;
                case "restoretest": return s.RestoreTest;
                case "ransom": return s.Ransom;
                default: return false;
            }
        }

        /// <summary>
        /// The result of one backup run. Counts failures / warnings in a row per set (kept in the set's profile entry by
        /// the caller through <paramref name="fails"/> / <paramref name="warns"/>), opens a call when the customer's threshold is reached, and on a
        /// success closes the set's open automatic calls (RTK-310/311).
        /// </summary>
        public Msg BackupResult(string login, string setId, string setName, string computer, string result, ref int fails, ref int warns, string detail, Profile customer = null, bool byDuration = false)
        {
            var st = Read(customer);
            // bug 103: a run cut at its maximum duration is now "stopped" - it still counts as before (as a warning): a set that
            // never finishes within its window must not go quiet
            if (result == "BS_STOP_BY_USER" && byDuration) result = "BS_STOP_SUCCESS_WITH_WARNING";
            // "completed with errors" is a failure (no silent failures); only a clean run closes the calls
            if (result == "BS_STOP_BY_USER") return null;   // stopped on purpose (SET-030): changes nothing
            bool ok = result == "BS_STOP_SUCCESS", warn = result == "BS_STOP_SUCCESS_WITH_WARNING";
            if (ok) { fails = 0; warns = 0; if (st.AutoClose) CloseAuto(login, setId); return null; }
            if (warn) { fails = 0; warns++; if (st.Warn > 0 && warns >= st.Warn) return Auto("warn", login, setId, setName, computer, "Backup with warnings " + warns + " times in a row — " + setName, detail, customer); if (st.AutoClose) CloseAuto(login, setId, "fail"); return null; }
            fails++; warns = 0;
            if (st.Fail > 0 && fails >= st.Fail) return Auto("fail", login, setId, setName, computer, "Backup failed " + fails + " times in a row — " + setName, detail, customer);
            return null;
        }

        /// <summary>The set's open automatic calls (all kinds, or one): untouched → deleted; worked on → resolved by the system.</summary>
        public int CloseAuto(string login, string setId, string onlyKind = null)
        {
            var touched = new List<Msg>(); var untouched = new List<Msg>();
            lock (gate)
            {
                var d = Load(StorePath); var now = Clock();
                foreach (var t in d.List("t").Where(x => !IsDone(x["status"]) && x["status"] != "Deferred" && x["source"] == "auto" && x["login"] == login && (x["set"] ?? "") == (setId ?? "")
                    && (onlyKind == null || x["kind"] == onlyKind) && new[] { "fail", "missed", "warn", "offline" }.Contains(x["kind"])).ToList())
                {
                    if (t.Int("worked") == 1)
                    {
                        t.Set("status", "Resolved").Set("resolved", Iso(now)).Set("updated", Iso(now)).Set("updatedBy", SystemUser).Set("rev", t.Int("rev") + 1);
                        Note(t, "autoclose", null, SystemUser, null, now, null, "Resolved"); touched.Add(t);
                    }
                    else untouched.Add(t);
                }
                if (touched.Count > 0) Write(StorePath, d);
            }
            foreach (var t in touched) Journal(SystemUser, "autoclose", t);
            foreach (var t in untouched) Delete(t["id"], -1, SystemUser);
            return touched.Count + untouched.Count;
        }

        /// <summary>Open calls with no handler go to the default handler (TCK-391/394); returns how many.</summary>
        public int AssignUnassigned()
        {
            var st = Read(); if (st.DefaultAssignee.Length == 0) return 0; int n = 0;
            lock (gate)
            {
                var d = Load(StorePath); var now = Clock();
                foreach (var t in d.List("t").Where(x => !IsDone(x["status"]) && string.IsNullOrEmpty(x["assignee"])))
                { Note(t, "assign", "default", SystemUser, null, now, null, st.DefaultAssignee); t.Set("assignee", st.DefaultAssignee).Set("rev", t.Int("rev") + 1); n++; }
                if (n > 0) Write(StorePath, d);
            }
            return n;
        }

        /// <summary>The list row the admin site needs: the call without its notes, with its SLA state.</summary>
        public Msg Row(Msg t)
        {
            var r = new Msg();
            foreach (var k in t.Keys) r.Set(k, t[k]);
            var sla = Sla(t); r.Set("sla", sla["state"]).Set("slaMinutes", sla["minutes"]).Set("slaMet", sla["met"]);
            return r;
        }
    }
}
