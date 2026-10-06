using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// TPL-010: set templates — named settings (schedule, versions kept, filters, compression, resources, missed runs, SQL
    /// method) applied to many customers' sets of the same type at once; never the sources, the computer or the key.
    /// &lt;system&gt;\policy\templates.xml. BULK-010: one action on many customers (quota, what they may change, a template,
    /// back up now), each customer's result reported.
    /// </summary>
    public static class Templates
    {
        static string PathOf(SystemConfig cfg) { return Path.Combine(cfg.SystemHome, "policy", "templates.xml"); }
        static XElement Load(SystemConfig cfg) { var p = PathOf(cfg); return File.Exists(p) ? XElement.Load(p) : new XElement("TEMPLATES"); }

        public static Msg List(SystemConfig cfg)
        {
            var m = new Msg();
            foreach (var t in Load(cfg).Elements("TEMPLATE"))
                m.Add("templates", new Msg().Set("name", (string)t.Attribute("NAME")).Set("type", (string)t.Attribute("TYPE")).Set("set", t.Element("BACKUP_SET").ToString(SaveOptions.DisableFormatting)));
            return m;
        }

        public static void Save(SystemConfig cfg, string name, string type, BackupSetInfo s, string admin, string ip)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name.Length > 80) throw new ApiException(400, "NAME", "Give the template a name.");
            if (s.Retention == null || s.Retention.Period < 1) throw new ApiException(400, "RETENTION", "Choose how long to keep versions — unlimited is not allowed.");
            lock (cfg)
            {
                var all = Load(cfg);
                all.Elements("TEMPLATE").Where(x => (string)x.Attribute("NAME") == name).Remove();
                s.Id = "template"; s.Sources.Clear(); s.Deselected.Clear(); s.Computer = ""; s.KeyCheck = ""; s.KeySalt = "";
                all.Add(new XElement("TEMPLATE", new XAttribute("NAME", name), new XAttribute("TYPE", string.IsNullOrEmpty(type) ? s.Type : type), s.ToXml()));
                Atomic.WriteText(PathOf(cfg), all.ToString());
            }
            SysLog.Write(ip, "Admin", admin + " saved the set template " + name);
        }

        public static void Delete(SystemConfig cfg, string name, string admin, string ip)
        {
            lock (cfg) { var all = Load(cfg); all.Elements("TEMPLATE").Where(x => (string)x.Attribute("NAME") == name).Remove(); Atomic.WriteText(PathOf(cfg), all.ToString()); }
            SysLog.Write(ip, "Admin", admin + " deleted the set template " + name);
        }

        /// <summary>Applies a template to every set of its type of one customer; returns how many sets changed.</summary>
        public static int Apply(SystemConfig cfg, Users users, string name, string login, string admin, string ip)
        {
            var t = Load(cfg).Elements("TEMPLATE").FirstOrDefault(x => (string)x.Attribute("NAME") == name);
            if (t == null) throw new ApiException(404, "TEMPLATE", "The template was not found.");
            var tp = BackupSetInfo.FromXml(t.Element("BACKUP_SET")); var type = (string)t.Attribute("TYPE");
            var ids = users.LoadProfile(login).Sets.Where(s => s.Type == type && string.IsNullOrEmpty(s.Parent)).Select(s => s.Id).ToList();
            foreach (var id in ids)
            {
                BackupSetInfo cur; lock (users.ProfileLock) cur = BackupSetInfo.FromXml(SetControl.Find(users.LoadProfile(login), id));
                cur.Hour = tp.Hour; cur.Minute = tp.Minute; cur.Days = tp.Days; cur.MoreSchedules = tp.MoreSchedules; cur.DurationHours = tp.DurationHours; cur.Retention = tp.Retention;
                foreach (var f in tp.Filters) if (!cur.Filters.Any(x => x.ToXml().ToString() == f.ToXml().ToString())) cur.Filters.Add(f);
                cur.Compression = tp.Compression; cur.BandwidthKbps = tp.BandwidthKbps; cur.BusyCpuPercent = tp.BusyCpuPercent; cur.LowPriority = tp.LowPriority;
                cur.RunMissed = tp.RunMissed; cur.RunMissedNet = tp.RunMissedNet; cur.MissedDelayMinutes = tp.MissedDelayMinutes; cur.MissedMinHours = tp.MissedMinHours;
                cur.Vss = tp.Vss; cur.DeltaType = tp.DeltaType; cur.SqlFullDay = tp.SqlFullDay; cur.LogIntervalMinutes = tp.LogIntervalMinutes; cur.LogRetentionDays = tp.LogRetentionDays;
                SetControl.Save(users, login, id, cur, admin, ip);
            }
            return ids.Count;
        }
    }
}
