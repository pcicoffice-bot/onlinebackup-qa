using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// COMP-010: a customer's computers — when each last connected, from which address, its software version and system;
    /// disconnect one (it stops backing up, its backups are kept, the licence place is freed); move one to another customer
    /// with its sets and their backups (the computer then signs in as the other customer).
    /// &lt;user&gt;\db\devices.xml: &lt;DEVICE ID NAME TOKEN_HASH CREATED REVOKED LAST_SEEN LAST_IP AGENT/&gt;
    /// </summary>
    public sealed partial class Users
    {
        string DevicesPath(string login) { return Path.Combine(UserDir(login), "db", "devices.xml"); }

        /// <summary>Notes the computer's connection — at most once in 10 minutes, to keep writes few.</summary>
        void ComputerSeen(string path, string deviceId, string ip, string agent, DateTime nowUtc)
        {
            try
            {
                // most requests: seen less than 10 minutes ago from the same address — only a read, no lock (LOAD-010)
                {
                    var d0 = XDocument.Load(path).Root.Elements("DEVICE").FirstOrDefault(x => (string)x.Attribute("ID") == deviceId);
                    long last0; long.TryParse(d0 == null ? null : (string)d0.Attribute("LAST_SEEN"), out last0);
                    if (d0 == null || (RunId.UnixMs(nowUtc) - last0 < 10 * 60 * 1000L && (string)d0.Attribute("LAST_IP") == ip && (agent == null || (string)d0.Attribute("AGENT") == agent))) return;
                }
                lock (gate)
                {
                    var doc = XDocument.Load(path);
                    var d = doc.Root.Elements("DEVICE").FirstOrDefault(x => (string)x.Attribute("ID") == deviceId);
                    if (d == null) return;
                    long last; long.TryParse((string)d.Attribute("LAST_SEEN"), out last);
                    long now = RunId.UnixMs(nowUtc);
                    if (now - last < 10 * 60 * 1000L && (string)d.Attribute("LAST_IP") == ip && (agent == null || (string)d.Attribute("AGENT") == agent)) return;
                    d.SetAttributeValue("LAST_SEEN", now); d.SetAttributeValue("LAST_IP", ip);
                    if (!string.IsNullOrEmpty(agent) && agent.Length < 200) d.SetAttributeValue("AGENT", agent);
                    Atomic.WriteText(path, doc.ToString());
                }
            }
            catch (IOException) { }   // a busy file: the next connection notes it
        }

        /// <summary>The customer's computers: the registered ones and the ones its sets name.</summary>
        public Msg ComputerList(string login)
        {
            var m = new Msg();
            lock (gate)
            {
                var path = DevicesPath(login);
                var devs = File.Exists(path) ? XDocument.Load(path).Root.Elements("DEVICE").ToList() : new List<XElement>();
                var p = LoadProfile(login);
                var names = devs.Select(d => (string)d.Attribute("NAME") ?? "").Concat(p.SetElements.Select(e => (string)e.Attribute("SCHEDULE_HOST") ?? ""))
                    .Where(n => n.Length > 0 && !n.StartsWith("~", StringComparison.Ordinal)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
                foreach (var name in names)
                {
                    var mine = devs.Where(d => string.Equals((string)d.Attribute("NAME"), name, StringComparison.OrdinalIgnoreCase)).ToList();
                    var live = mine.Where(d => (string)d.Attribute("REVOKED") != "Y").ToList();
                    var seen = mine.OrderByDescending(d => (long?)d.Attribute("LAST_SEEN") ?? 0).FirstOrDefault();
                    var sets = p.SetElements.Where(e => string.Equals((string)e.Attribute("SCHEDULE_HOST"), name, StringComparison.OrdinalIgnoreCase)).ToList();
                    var agent = seen == null ? "" : (string)seen.Attribute("AGENT") ?? "";
                    var i = agent.IndexOf("; ", StringComparison.Ordinal);
                    m.Add("computers", new Msg().Set("name", name).Set("connected", live.Count > 0 ? 1 : 0)
                        .Set("registered", mine.Count == 0 ? null : mine.Min(d => (string)d.Attribute("CREATED")))
                        .Set("lastSeen", seen == null ? null : (string)seen.Attribute("LAST_SEEN")).Set("lastIp", seen == null ? null : (string)seen.Attribute("LAST_IP"))
                        .Set("version", i > 0 ? agent.Substring(0, i) : agent).Set("os", i > 0 ? agent.Substring(i + 2) : "")
                        .Set("sets", sets.Count).Set("setNames", string.Join(", ", sets.Select(e => (string)e.Attribute("NAME"))))
                        .Set("lastBackup", sets.Count == 0 ? null : sets.Max(e => (string)e.Attribute("LAST_BACKUP_COMPLETE") ?? "")));
                }
            }
            return m;
        }

        /// <summary>The computer stops backing up: its sign-in is revoked; its sets and backups stay (restorable).</summary>
        public int Disconnect(string login, string computer, string by, string ip)
        {
            int n = 0;
            lock (gate)
            {
                var path = DevicesPath(login);
                if (!File.Exists(path)) return 0;
                var doc = XDocument.Load(path);
                foreach (var d in doc.Root.Elements("DEVICE").Where(x => (string)x.Attribute("REVOKED") != "Y" && string.Equals((string)x.Attribute("NAME"), computer, StringComparison.OrdinalIgnoreCase)))
                { d.SetAttributeValue("REVOKED", "Y"); n++; }
                Atomic.WriteText(path, doc.ToString());
            }
            SysLog.Write(ip, "Admin", by + " disconnected the computer " + computer + " of " + login + " (" + n + " sign-in(s) revoked, backups kept)");
            return n;
        }

        /// <summary>
        /// The computer moves to another customer with its sets and backups. Its sign-in here is revoked; installed again
        /// with the other customer's user name, it goes on backing up into the same sets (the keys stay on the computer).
        /// A set copied from a set that stays (or the other way round) keeps the shared key under its own name.
        /// </summary>
        public List<string> MoveComputer(string login, string computer, string target, string by, string ip)
        {
            if (string.Equals(login, target, StringComparison.OrdinalIgnoreCase)) throw new ApiException(400, "TARGET", "Choose another customer.");
            var moved = new List<string>();
            lock (gate)
            {
                if (!Logins().Contains(target, StringComparer.OrdinalIgnoreCase)) throw new ApiException(404, "TARGET", "The customer was not found.");
                target = Logins().First(l => string.Equals(l, target, StringComparison.OrdinalIgnoreCase));
                var from = LoadProfile(login); var to = LoadProfile(target);
                var sets = from.SetElements.Where(e => string.Equals((string)e.Attribute("SCHEDULE_HOST"), computer, StringComparison.OrdinalIgnoreCase)).ToList();
                if (sets.Count == 0) throw new ApiException(404, "COMPUTER", "The computer has no backup sets here.");
                int max = (int)Math.Max(1, to.GetLong("MAX_BACKUP_SET"));
                if (to.SetElements.Count() + sets.Count > max) throw new ApiException(409, "SET_LIMIT", "The other customer can have at most " + max + " backup sets.");
                foreach (var e in sets) if (to.FindSet((string)e.Attribute("ID")) != null) throw new ApiException(409, "SET_ID", "A set with the same number exists at the other customer.");
                string a = UserDir(login), b = UserDir(target);
                var ids = new HashSet<string>(sets.Select(e => (string)e.Attribute("ID")));
                foreach (var e in sets)
                {
                    var id = (string)e.Attribute("ID");
                    foreach (var part in new[] { "files", "restic", "logs" }) MovePath(Path.Combine(a, part, id), Path.Combine(b, part, id));
                    MovePath(Path.Combine(a, "db", "restic", id + ".token"), Path.Combine(b, "db", "restic", id + ".token"));
                    MovePath(Path.Combine(a, "db", "keys", id + ".bin"), Path.Combine(b, "db", "keys", id + ".bin"));
                    // a set copied from one that stays: its key, kept here under the parent's name, goes along under its own
                    var parent = (string)e.Attribute("PARENT_SET");
                    if (!string.IsNullOrEmpty(parent) && !ids.Contains(parent)) { CopyKey(Path.Combine(a, "db", "keys", parent + ".bin"), Path.Combine(b, "db", "keys", id + ".bin")); e.SetAttributeValue("PARENT_SET", null); }
                    e.Remove(); to.Root.Add(e);
                    moved.Add(id);
                }
                // sets that stay and were copied from a moved one keep the key under their own name
                foreach (var e in from.SetElements.Where(x => ids.Contains((string)x.Attribute("PARENT_SET") ?? "")).ToList())
                { CopyKey(Path.Combine(b, "db", "keys", (string)e.Attribute("PARENT_SET") + ".bin"), Path.Combine(a, "db", "keys", (string)e.Attribute("ID") + ".bin")); e.SetAttributeValue("PARENT_SET", null); }
                MovePath(Path.Combine(a, "folders", FolderTree.FileName(computer)), Path.Combine(b, "folders", FolderTree.FileName(computer)));
                SaveProfile(login, from); SaveProfile(target, to);
                var path = DevicesPath(login);
                if (File.Exists(path))
                {
                    var doc = XDocument.Load(path);
                    foreach (var d in doc.Root.Elements("DEVICE").Where(x => string.Equals((string)x.Attribute("NAME"), computer, StringComparison.OrdinalIgnoreCase))) d.SetAttributeValue("REVOKED", "Y");
                    Atomic.WriteText(path, doc.ToString());
                }
            }
            SysLog.Write(ip, "Admin", by + " moved the computer " + computer + " from " + login + " to " + target + " with " + moved.Count + " set(s)");
            return moved;
        }

        static void MovePath(string src, string dst)
        {
            if (Directory.Exists(src)) { Directory.CreateDirectory(Path.GetDirectoryName(dst)); if (Directory.Exists(dst)) throw new ApiException(409, "EXISTS", "The data already exists at the other customer."); Directory.Move(src, dst); }
            else if (File.Exists(src)) { Directory.CreateDirectory(Path.GetDirectoryName(dst)); if (File.Exists(dst)) File.Delete(dst); File.Move(src, dst); }
        }

        static void CopyKey(string src, string dst)
        {
            if (!File.Exists(src) || File.Exists(dst)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)); File.Copy(src, dst);
        }
    }
}
