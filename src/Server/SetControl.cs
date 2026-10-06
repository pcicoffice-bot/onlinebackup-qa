using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// SET-010: the IT company changes a customer's backup set from the admin site, like Ahsay — every setting except what
    /// the backups depend on (type, engine, encryption key). The agent reads the profile every minute, so the change reaches
    /// the computer within a minute. SET-020: one set on several computers (one copy per computer, its own backups, the
    /// settings of the first). SET-030: "back up now" and "stop" from the server.
    /// </summary>
    public static class SetControl
    {
        /// <summary>The attributes that belong to one copy (its computer and its backups) and are never copied.</summary>
        static readonly string[] Own = { "ID", "SCHEDULE_HOST", "SCHEDULE_DEVICE", "PARENT_SET", "RUN_REQUEST", "STOP_REQUEST", "LAST_BACKUP_COMPLETE", "LAST_MISSED_ALERT", "TICKET_FAILS", "TICKET_WARNS",
            "TOTAL_BSET_SIZE", "TOTAL_UNCOMPRESS_FILE_SIZE", "NO_OF_FILES", "TOTAL_BSET_RETAIN_FILE_SIZE", "TOTAL_BSET_RETAIN_UNCOMPRESS", "TOTAL_BSET_RETAIN_FILE_NO",
            "LAST_RESTORE_TEST", "RESTORE_TEST_RESULT", "SUSPECT_RANSOMWARE", "DETACHED" };

        public static XElement Find(Profile p, string id)
        {
            var e = p.FindSet(id);
            if (e == null) throw new ApiException(404, "NO_SET", "The backup set was not found.");
            return e;
        }

        /// <summary>The set's family: the first set and its copies on other computers (the first first).</summary>
        public static List<XElement> Family(Profile p, string id)
        {
            var e = Find(p, id);
            var root = (string)e.Attribute("PARENT_SET");
            var head = string.IsNullOrEmpty(root) ? e : Find(p, root);
            var hid = (string)head.Attribute("ID");
            return new[] { head }.Concat(p.SetElements.Where(x => (string)x.Attribute("PARENT_SET") == hid)).ToList();
        }

        /// <summary>
        /// The set as the admin site shows it. Like Ahsay OBM, a set belongs to one computer and has its own paths; "related"
        /// lists the sets copied from the same set to other computers (they share its key, nothing else).
        /// </summary>
        public static Msg Detail(Users users, string login, string id) { lock (users.ProfileLock) return Detail(users.LoadProfile(login), id); }

        public static Msg Detail(Profile p, string id)
        {
            var e = Find(p, id);
            var m = new Msg().Set("set", e.ToString(SaveOptions.DisableFormatting)).Set("version", Version(e)).Set("head", (string)Family(p, id)[0].Attribute("ID")).Set("computer", (string)e.Attribute("SCHEDULE_HOST"));
            m.Add("computers", new Msg().Set("id", id).Set("computer", (string)e.Attribute("SCHEDULE_HOST")).Set("detached", (string)e.Attribute("DETACHED") == "Y" ? 1 : 0)
                .Set("lastBackup", (string)e.Attribute("LAST_BACKUP_COMPLETE")).Set("size", (string)e.Attribute("TOTAL_BSET_SIZE")));
            foreach (var x in Family(p, id).Where(x => x != e))
                m.Add("related", new Msg().Set("id", (string)x.Attribute("ID")).Set("name", (string)x.Attribute("NAME")).Set("computer", (string)x.Attribute("SCHEDULE_HOST")).Set("detached", (string)x.Attribute("DETACHED") == "Y" ? 1 : 0));
            return m;
        }

        /// <summary>
        /// Saves the settings sent by the admin site to this set only (each computer's set has its own paths and settings,
        /// like Ahsay). The type, the engine and the encryption key stay as they are (changing them would orphan the
        /// backups); the computer is changed with Move.
        /// </summary>
        /// <summary>I-3: the version of the set's SETTINGS (not its statistics or run state, which change with every backup):
        /// the hash of what the editor can change.</summary>
        public static string Version(XElement e)
        {
            var x = BackupSetInfo.FromXml(e).ToXml();
            foreach (var d in x.Descendants()) d.SetAttributeValue("ID", null);   // ToXml stamps its schedules' ids with the time of the call
            var settings = x.ToString(SaveOptions.DisableFormatting);
            return Bytes.Hex(Bytes.Sha256(System.Text.Encoding.UTF8.GetBytes(settings))).Substring(0, 16);
        }

        public static BackupSetInfo Save(Users users, string login, string id, BackupSetInfo inc, string admin, string ip, string version = null)
        {
            if (string.IsNullOrWhiteSpace(inc.Name)) throw new ApiException(400, "NAME", "Give the set a name.");
            if (inc.Hour < 0 || inc.Hour > 23 || inc.Minute < 0 || inc.Minute > 59) throw new ApiException(400, "TIME", "The time is not valid.");
            if (inc.MoreSchedules.Count > 11) throw new ApiException(400, "TIME", "At most 12 times a day.");
            if (inc.DurationHours == 0 || inc.DurationHours > 168) throw new ApiException(400, "DURATION", "The maximum duration is 1 to 168 hours, or no limit.");
            if (inc.DestMode == "LOCAL" && inc.Engine != "RESTIC") throw new ApiException(400, "DEST", "A local-only backup needs the restic engine (Windows 10 / Server 2016 or later, Linux, Mac).");
            if (inc.DestMode == "BOTH" || inc.DestMode == "LOCAL") { if (string.IsNullOrWhiteSpace(inc.LocalCopyPath)) throw new ApiException(400, "DEST", "Choose the local disk or network folder."); if (inc.DestMode == "BOTH") inc.LocalCopy = true; }
            if (inc.Retention == null || inc.Retention.Period < 1) throw new ApiException(400, "RETENTION", "Choose how long to keep versions — unlimited is not allowed.");
            lock (users.ProfileLock)
            {
                var p = users.LoadProfile(login);
                var e = Find(p, id);
                // I-3: two technicians edit one set — the later save, made on the settings as they were before the first
                // save, silently undid it. A save names the version it was made on; a newer one is refused.
                if (!string.IsNullOrEmpty(version) && version != Version(e))
                    throw new ApiException(409, "CHANGED", "Another administrator changed this set after you opened it. Reload it and make your change again.");
                var cur = BackupSetInfo.FromXml(e);
                inc.Id = cur.Id; inc.Type = cur.Type; inc.Engine = cur.Engine; inc.Computer = cur.Computer; inc.Device = cur.Device; inc.Parent = cur.Parent;
                inc.KeyType = cur.KeyType; inc.KeyCheck = cur.KeyCheck; inc.KeySalt = cur.KeySalt;
                inc.ToXml(e);
                users.SaveProfile(login, p);
            }
            SysLog.Write(ip, "Admin", admin + " changed the backup set " + login + "/" + id + " (" + inc.Name + ")");
            return inc;
        }

        /// <summary>SET-030: "back up now" or "stop" for the set on its computer.</summary>
        public static int Request(Users users, string login, string id, bool run, string admin, string ip, DateTime nowUtc)
        {
            int n = 0;
            lock (users.ProfileLock)
            {
                var p = users.LoadProfile(login);
                var e = Find(p, id);
                if ((string)e.Attribute("DETACHED") != "Y") { e.SetAttributeValue(run ? "RUN_REQUEST" : "STOP_REQUEST", RunId.UnixMs(nowUtc)); n++; }
                users.SaveProfile(login, p);
            }
            SysLog.Write(ip, "Admin", admin + (run ? " started" : " stopped") + " the backup set " + login + "/" + id);
            return n;
        }

        /// <summary>
        /// SET-020: the set copied to another computer of the customer — a set of its own (Ahsay OBM: a set for each server)
        /// with its own paths, settings and backups, starting from this set's settings. It shares only the encryption key.
        /// </summary>
        public static string AddComputer(Users users, string login, string id, string computer, string admin, string ip)
        {
            computer = (computer ?? "").Trim();
            if (computer.Length == 0 || computer.Length > 64 || computer.StartsWith("~", StringComparison.Ordinal)) throw new ApiException(400, "COMPUTER", "Choose a computer.");
            string nid;
            lock (users.ProfileLock)
            {
                var p = users.LoadProfile(login);
                var fam = Family(p, id);
                var same = fam.FirstOrDefault(x => string.Equals((string)x.Attribute("SCHEDULE_HOST"), computer, StringComparison.OrdinalIgnoreCase));
                if (same != null && (string)same.Attribute("DETACHED") != "Y") throw new ApiException(409, "COMPUTER", "The set already runs on this computer.");
                if (string.IsNullOrEmpty((string)fam[0].Attribute("SCHEDULE_HOST"))) throw new ApiException(400, "COMPUTER", "Choose the set's first computer before adding another.");
                if (same != null) { same.SetAttributeValue("DETACHED", null); same.SetAttributeValue("SCHEDULE_HOST", computer); same.SetAttributeValue("SCHEDULE_DEVICE", null); nid = (string)same.Attribute("ID"); }
                else
                {
                    int max = (int)Math.Max(1, p.GetLong("MAX_BACKUP_SET"));
                    if (p.SetElements.Count() >= max) throw new ApiException(409, "SET_LIMIT", "Maximum number of backup sets reached (" + max + ").");
                    long n = RunId.UnixMs(SystemClock.UtcNow);
                    while (p.FindSet(n.ToString(CultureInfo.InvariantCulture)) != null) n++;
                    nid = n.ToString(CultureInfo.InvariantCulture);
                    var copy = new XElement(fam[0]);
                    foreach (var a in Own) copy.SetAttributeValue(a, null);
                    copy.SetAttributeValue("ID", nid); copy.SetAttributeValue("SCHEDULE_HOST", computer); copy.SetAttributeValue("PARENT_SET", (string)fam[0].Attribute("ID"));
                    copy.SetAttributeValue("NAME", ((string)fam[0].Attribute("NAME") ?? "") + " — " + computer);
                    p.Root.Add(copy);
                }
                users.SaveProfile(login, p);
            }
            SysLog.Write(ip, "Admin", admin + " the backup set " + login + "/" + id + " also runs on " + computer);
            return nid;
        }

        /// <summary>The set stops running on a computer. Its backups are kept (restorable, deleted only on purpose).</summary>
        public static void RemoveComputer(Users users, string login, string id, string computer, string admin, string ip)
        {
            lock (users.ProfileLock)
            {
                var p = users.LoadProfile(login);
                var fam = Family(p, id);
                var e = fam.Skip(1).FirstOrDefault(x => string.Equals((string)x.Attribute("SCHEDULE_HOST"), computer, StringComparison.OrdinalIgnoreCase));
                if (e == null) throw new ApiException(400, "COMPUTER", "Only an added computer can be removed. To change the first computer, move the set.");
                // no agent matches "~…": the copy stops running, its backups stay
                e.SetAttributeValue("DETACHED", "Y"); e.SetAttributeValue("SCHEDULE_HOST", "~" + computer);
                users.SaveProfile(login, p);
            }
            SysLog.Write(ip, "Admin", admin + " the backup set " + login + "/" + id + " no longer runs on " + computer + " (backups kept)");
        }

        /// <summary>The set's first computer changes (a new computer replaced the old one). Its copies stay as they are.</summary>
        public static void Move(Users users, string login, string id, string computer, string admin, string ip)
        {
            computer = (computer ?? "").Trim();
            if (computer.Length == 0 || computer.StartsWith("~", StringComparison.Ordinal)) throw new ApiException(400, "COMPUTER", "Choose a computer.");
            lock (users.ProfileLock)
            {
                var p = users.LoadProfile(login);
                var e = Find(p, id);
                if (Family(p, id).Any(x => x != e && string.Equals((string)x.Attribute("SCHEDULE_HOST"), computer, StringComparison.OrdinalIgnoreCase)))
                    throw new ApiException(409, "COMPUTER", "The set already runs on this computer.");
                e.SetAttributeValue("SCHEDULE_HOST", computer);
                users.SaveProfile(login, p);
            }
            SysLog.Write(ip, "Admin", admin + " moved the backup set " + login + "/" + id + " to " + computer);
        }

        // ------------------------------------------------------------------ what the customer may change (SET-040)

        /// <summary>SET-040: what the customer may change in the client software; the rest is locked and managed by the IT company.</summary>
        public static readonly string[][] Rights = {
            new[] { "CAN_ADD_SETS", "Y" }, new[] { "CAN_EDIT_SOURCES", "Y" }, new[] { "CAN_EDIT_SCHEDULE", "Y" },
            new[] { "CAN_EDIT_RETENTION", "N" }, new[] { "CAN_EDIT_DESTINATION", "N" }, new[] { "CAN_EDIT_OPTIONS", "N" } };

        public static bool Can(Profile p, string right)
        {
            var v = p.Get(right);
            return (v ?? Rights.First(r => r[0] == right)[1]) == "Y";
        }

        public static Msg RightsMsg(Profile p) { var m = new Msg(); foreach (var r in Rights) m.Set(r[0].ToLowerInvariant(), Can(p, r[0]) ? 1 : 0); return m; }

        /// <summary>
        /// The customer changes its set from the client software: only the parts its IT company allows; a locked part that was
        /// changed is refused (never silently dropped), with its name.
        /// </summary>
        // a filter's meaning, without its ID (a new ID every time it is written)
        static string FilterKey(FilterRule f) { return string.Join("\u0001", new[] { f.Type, f.TopDir, f.Include ? "Y" : "N", f.Only ? "Y" : "N", f.ApplyDir ? "Y" : "N", f.ApplyFile ? "Y" : "N" }.Concat(f.Patterns).ToArray()); }

        public static BackupSetInfo CustomerSave(Users users, string login, string id, BackupSetInfo inc, string ip)
        {
            BackupSetInfo cur;
            Profile p;
            lock (users.ProfileLock) { p = users.LoadProfile(login); cur = BackupSetInfo.FromXml(Find(p, id)); }
            Func<object, string> x = (o) => o == null ? "" : string.Join("|", o is System.Collections.IEnumerable && !(o is string) ? ((System.Collections.IEnumerable)o).Cast<object>().Select(y => y is FilterRule ? FilterKey((FilterRule)y) : Convert.ToString(y, CultureInfo.InvariantCulture)) : new[] { Convert.ToString(o, CultureInfo.InvariantCulture) });
            var locked = new List<string>();
            Action<string, string, bool> check = (right, name, changed) => { if (changed && !Can(p, right)) locked.Add(name); };
            check("CAN_EDIT_SOURCES", "sources", x(cur.Sources) != x(inc.Sources) || x(cur.Deselected) != x(inc.Deselected) || x(cur.Filters) != x(inc.Filters));
            check("CAN_EDIT_SCHEDULE", "schedule", cur.Hour != inc.Hour || cur.Minute != inc.Minute || cur.Days != inc.Days || cur.DurationHours != inc.DurationHours
                || string.Join(";", cur.MoreSchedules.Select(z => z.Days + z.Hour + ":" + z.Minute)) != string.Join(";", inc.MoreSchedules.Select(z => z.Days + z.Hour + ":" + z.Minute)));
            check("CAN_EDIT_RETENTION", "retention", x(new object[] { cur.Retention.Unit, cur.Retention.Period, cur.Retention.Daily, cur.Retention.Weekly, cur.Retention.Monthly, cur.Retention.Quarterly, cur.Retention.Yearly })
                != x(new object[] { inc.Retention.Unit, inc.Retention.Period, inc.Retention.Daily, inc.Retention.Weekly, inc.Retention.Monthly, inc.Retention.Quarterly, inc.Retention.Yearly }));
            check("CAN_EDIT_DESTINATION", "destination", cur.LocalCopy != inc.LocalCopy || cur.LocalCopyPath != inc.LocalCopyPath || cur.LocalCopyDays != inc.LocalCopyDays);
            check("CAN_EDIT_OPTIONS", "options", cur.Vss != inc.Vss || cur.BackupPermissions != inc.BackupPermissions || x(cur.PreCommands) != x(inc.PreCommands) || x(cur.PostCommands) != x(inc.PostCommands) || cur.DeltaType != inc.DeltaType);
            if (locked.Count > 0) throw new ApiException(403, "LOCKED", "Your IT provider manages these settings: " + string.Join(", ", locked));
            return Save(users, login, id, inc, login, ip);
        }
    }
}
