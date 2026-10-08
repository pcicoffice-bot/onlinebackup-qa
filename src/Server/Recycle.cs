using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// DEL-010: deleting a customer or a backup set. Nothing is erased at once: the folder moves to the recycle bin of its
    /// drive (&lt;home&gt;\_recycle) and is erased after 14 days, restorable until then. DEL-020: with two or more
    /// administrators a deletion waits for a second one to approve it (on by default) — a stolen password alone cannot wipe
    /// a customer. The requests: &lt;DELETE_REQUEST ID KIND=user|set LOGIN SET NAME BY TIME/&gt; in the server's settings.
    /// </summary>
    public static class Recycle
    {
        public const int KeepDays = 14;

        public static bool Dual(SystemConfig cfg) { var s = cfg.Doc.Root.Element("SECURITY"); return s == null || (string)s.Attribute("DUAL_DELETE") != "N"; }

        public static void SetDual(SystemConfig cfg, bool on) { lock (cfg) { cfg.Doc.Root.Element("SECURITY").SetAttributeValue("DUAL_DELETE", on ? "Y" : "N"); cfg.Save(); } }

        static int Administrators(SystemConfig cfg) { return 1 + cfg.Doc.Root.Elements("STAFF").Count(x => (string)x.Attribute("DISABLED") != "Y"); }

        // ------------------------------------------------------------------ requests

        /// <summary>Asks to delete a customer (set = null) or one set. Returns the request, or null when it was done at once.</summary>
        public static Msg Request(SystemConfig cfg, Users users, string login, string set, string by, string ip, DateTime nowUtc)
        {
            var p = users.LoadProfile(login);   // exists
            string name = p.Get("ALIAS") ?? login;
            if (set != null) { var e = SetControl.Find(p, set); name = (string)e.Attribute("NAME"); }
            if (!Dual(cfg) || Administrators(cfg) < 2)
            {
                Execute(users, set == null ? "user" : "set", login, set, by, ip, nowUtc);
                return null;
            }
            lock (cfg)
            {
                var old = cfg.Doc.Root.Elements("DELETE_REQUEST").FirstOrDefault(x => (string)x.Attribute("LOGIN") == login && (string)x.Attribute("SET") == (set ?? ""));
                if (old != null) return Row(old);
                var r = new XElement("DELETE_REQUEST", new XAttribute("ID", Bytes.Hex(Bytes.Random(6))), new XAttribute("KIND", set == null ? "user" : "set"), new XAttribute("LOGIN", login),
                    new XAttribute("SET", set ?? ""), new XAttribute("NAME", name), new XAttribute("BY", by), new XAttribute("TIME", RunId.UnixMs(nowUtc)));
                cfg.Doc.Root.Add(r); cfg.Save();
                SysLog.Write(ip, "Admin", by + " asked to delete " + (set == null ? "the customer " + login : "the backup set " + login + "/" + set) + " — waits for a second administrator");
                return Row(r);
            }
        }

        static Msg Row(XElement r)
        {
            return new Msg().Set("id", (string)r.Attribute("ID")).Set("kind", (string)r.Attribute("KIND")).Set("login", (string)r.Attribute("LOGIN")).Set("set", (string)r.Attribute("SET"))
                .Set("name", (string)r.Attribute("NAME")).Set("by", (string)r.Attribute("BY")).Set("time", (string)r.Attribute("TIME"));
        }

        public static List<Msg> Requests(SystemConfig cfg) { return cfg.Doc.Root.Elements("DELETE_REQUEST").Select(Row).ToList(); }

        /// <summary>A second administrator approves (never the one who asked); a request is valid for 7 days.</summary>
        public static void Approve(SystemConfig cfg, Users users, string id, string by, string ip, DateTime nowUtc)
        {
            XElement r;
            lock (cfg)
            {
                r = cfg.Doc.Root.Elements("DELETE_REQUEST").FirstOrDefault(x => (string)x.Attribute("ID") == id);
                if (r == null) throw new ApiException(404, "REQUEST", "The request was not found.");
                if (string.Equals((string)r.Attribute("BY"), by, StringComparison.OrdinalIgnoreCase)) throw new ApiException(403, "SAME_ADMIN", "Another administrator must approve this deletion.");
                long t; long.TryParse((string)r.Attribute("TIME"), out t);
                r.Remove(); cfg.Save();
                if ((nowUtc - RunId.FromUnixMs(t)).TotalDays > 7) throw new ApiException(410, "EXPIRED", "The request is older than 7 days. Ask again.");
            }
            var set = (string)r.Attribute("SET");
            Execute(users, (string)r.Attribute("KIND"), (string)r.Attribute("LOGIN"), set.Length == 0 ? null : set, (string)r.Attribute("BY") + " + " + by, ip, nowUtc);
        }

        public static void Cancel(SystemConfig cfg, string id, string by, string ip)
        {
            lock (cfg)
            {
                var r = cfg.Doc.Root.Elements("DELETE_REQUEST").FirstOrDefault(x => (string)x.Attribute("ID") == id);
                if (r == null) throw new ApiException(404, "REQUEST", "The request was not found.");
                r.Remove(); cfg.Save();
            }
            SysLog.Write(ip, "Admin", by + " cancelled the deletion request " + id);
        }

        static void Execute(Users users, string kind, string login, string set, string by, string ip, DateTime nowUtc)
        {
            if (kind == "user") users.RecycleUser(login, by, nowUtc);
            else users.RecycleSet(login, set, by, nowUtc);
            SysLog.Write(ip, "Admin", by + " deleted " + (kind == "user" ? "the customer " + login : "the backup set " + login + "/" + set) + " — in the recycle bin for " + KeepDays + " days");
        }
    }

    public sealed partial class Users
    {
        /// <summary>DEL-010: the customer's folder moves to the recycle bin of its drive; the customer is gone from the list.</summary>
        public void RecycleUser(string login, string by, DateTime nowUtc)
        {
            lock (gate)
            {
                var dir = UserDir(login);
                var idx = Index();
                var e = idx.Root.Elements("USER").First(x => string.Equals((string)x.Attribute("LOGIN"), login, StringComparison.OrdinalIgnoreCase));
                var bin = Path.Combine((string)e.Attribute("HOME"), "_recycle", (string)e.Attribute("LOGIN") + "-" + RunId.UnixMs(nowUtc).ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(Path.GetDirectoryName(bin));
                Directory.Move(dir, bin);
                Atomic.WriteText(Path.Combine(bin, "RECYCLED.xml"), new XElement("RECYCLED", new XAttribute("KIND", "user"), new XAttribute("LOGIN", (string)e.Attribute("LOGIN")),
                    new XAttribute("QUOTA", (string)e.Attribute("QUOTA") ?? "0"), new XAttribute("BY", by), new XAttribute("TIME", RunId.UnixMs(nowUtc))).ToString());
                e.Remove();
                WriteIndex(idx);
            }
        }

        /// <summary>DEL-010: one set leaves the customer's profile; its backups move to the customer's recycle bin.</summary>
        public void RecycleSet(string login, string setId, string by, DateTime nowUtc)
        {
            lock (gate)
            {
                var p = LoadProfile(login); var dir = UserDir(login);
                var e = SetControl.Find(p, setId);
                // sets copied from this one (on other computers) keep its key under their own name
                foreach (var c in p.SetElements.Where(x => (string)x.Attribute("PARENT_SET") == setId).ToList())
                { CopyKey(Path.Combine(dir, "db", "keys", setId + ".bin"), Path.Combine(dir, "db", "keys", (string)c.Attribute("ID") + ".bin")); c.SetAttributeValue("PARENT_SET", null); }
                var bin = Path.Combine(dir, "_recycle", "set-" + setId + "-" + RunId.UnixMs(nowUtc).ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(bin);
                foreach (var part in new[] { "files", "restic", "logs" })
                {
                    var src = Path.Combine(dir, part, setId);
                    if (Directory.Exists(src)) Directory.Move(src, Path.Combine(bin, part));
                }
                Atomic.WriteText(Path.Combine(bin, "RECYCLED.xml"), new XElement("RECYCLED", new XAttribute("KIND", "set"), new XAttribute("LOGIN", login), new XAttribute("SET", setId),
                    new XAttribute("BY", by), new XAttribute("TIME", RunId.UnixMs(nowUtc)), new XElement(e)).ToString());
                e.Remove();
                SaveProfile(login, p);
            }
        }

        /// <summary>Everything in the recycle bins (customers and sets), with when it is erased.</summary>
        public List<Msg> Recycled()
        {
            var list = new List<Msg>();
            foreach (var bin in BinDirs())
                try
                {
                    var r = OnlineBackup.Core.Atomic.LoadXElement(Path.Combine(bin, "RECYCLED.xml"));
                    long t; long.TryParse((string)r.Attribute("TIME"), out t);
                    var set = r.Element("BACKUP_SET");
                    list.Add(new Msg().Set("id", Path.GetFileName(bin) + (r.Attribute("KIND").Value == "set" ? "@" + (string)r.Attribute("LOGIN") : "")).Set("kind", (string)r.Attribute("KIND"))
                        .Set("login", (string)r.Attribute("LOGIN")).Set("set", (string)r.Attribute("SET")).Set("name", set == null ? null : (string)set.Attribute("NAME"))
                        .Set("by", (string)r.Attribute("BY")).Set("time", t).Set("erase", RunId.UnixMs(RunId.FromUnixMs(t).AddDays(Recycle.KeepDays))));
                }
                catch (Exception) { }
            return list.OrderByDescending(m => m.Long("time")).ToList();
        }

        IEnumerable<string> BinDirs()
        {
            foreach (var h in cfg.Homes.Select(x => x.Path).Distinct())
            {
                var bin = Path.Combine(h, "_recycle");
                if (Directory.Exists(bin)) foreach (var d in Directory.GetDirectories(bin)) if (File.Exists(Path.Combine(d, "RECYCLED.xml"))) yield return d;
            }
            foreach (var login in Logins())
            {
                string ud; try { ud = UserDir(login); } catch (Exception) { continue; }
                var bin = Path.Combine(ud, "_recycle");
                if (Directory.Exists(bin)) foreach (var d in Directory.GetDirectories(bin)) if (File.Exists(Path.Combine(d, "RECYCLED.xml"))) yield return d;
            }
        }

        string FindBin(string id)
        {
            var d = BinDirs().FirstOrDefault(x => Path.GetFileName(x) == id.Split('@')[0] && (!id.Contains("@") || x.Contains(Path.DirectorySeparatorChar + id.Split('@')[1] + Path.DirectorySeparatorChar)));
            if (d == null) throw new ApiException(404, "RECYCLED", "It is not in the recycle bin.");
            return d;
        }

        /// <summary>Puts a customer or a set back as it was.</summary>
        public void RestoreRecycled(string id, string by, string ip)
        {
            lock (gate)
            {
                var bin = FindBin(id);
                var r = OnlineBackup.Core.Atomic.LoadXElement(Path.Combine(bin, "RECYCLED.xml"));
                var login = (string)r.Attribute("LOGIN");
                if ((string)r.Attribute("KIND") == "user")
                {
                    if (Logins().Any(l => string.Equals(l, login, StringComparison.OrdinalIgnoreCase))) throw new ApiException(409, "EXISTS", "A customer with this name exists now.");
                    var home = Path.GetDirectoryName(Path.GetDirectoryName(bin));
                    File.Delete(Path.Combine(bin, "RECYCLED.xml"));
                    Directory.Move(bin, Path.Combine(home, login));
                    var idx = Index();
                    idx.Root.Add(new XElement("USER", new XAttribute("LOGIN", login), new XAttribute("HOME", home), new XAttribute("QUOTA", (string)r.Attribute("QUOTA") ?? "0")));
                    WriteIndex(idx);
                }
                else
                {
                    var setId = (string)r.Attribute("SET"); var dir = UserDir(login); var p = LoadProfile(login);
                    if (p.FindSet(setId) != null) throw new ApiException(409, "EXISTS", "The set exists now.");
                    foreach (var part in new[] { "files", "restic", "logs" })
                    {
                        var src = Path.Combine(bin, part);
                        if (Directory.Exists(src)) { Directory.CreateDirectory(Path.Combine(dir, part)); Directory.Move(src, Path.Combine(dir, part, setId)); }
                    }
                    p.Root.Add(new XElement(r.Element("BACKUP_SET")));
                    SaveProfile(login, p);
                    Directory.Delete(bin, true);
                }
            }
            SysLog.Write(ip, "Admin", by + " restored from the recycle bin: " + id);
        }

        /// <summary>Erases what has been in a recycle bin for more than 14 days (daily maintenance).</summary>
        public int PurgeRecycled(DateTime nowUtc)
        {
            int n = 0;
            foreach (var bin in BinDirs().ToList())
                try
                {
                    long t; long.TryParse((string)OnlineBackup.Core.Atomic.LoadXElement(Path.Combine(bin, "RECYCLED.xml")).Attribute("TIME"), out t);
                    if ((nowUtc - RunId.FromUnixMs(t)).TotalDays < Recycle.KeepDays) continue;
                    Directory.Delete(bin, true); n++;
                    SysLog.Write(null, "System", "recycle bin: erased " + Path.GetFileName(bin) + " after " + Recycle.KeepDays + " days");
                }
                catch (Exception e) { SysLog.Write(null, "System", "error: recycle bin " + bin + ": " + e.Message); }
            return n;
        }
    }
}
