using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// TIME-010: the server's clock for people — a country and its time zone (with daylight saving as that country keeps it)
    /// for the admin site, the e-mails, the reports and the logs; and the time source (NTP) of the server computer, set only
    /// when the administrator asks (Windows: w32tm). &lt;TIME COUNTRY ZONE NTP NTP_ON/&gt;.
    /// </summary>
    public static class TimeSettings
    {
        /// <summary>Countries offered first, with their zone (IANA). Any zone of the system can be chosen too.</summary>
        public static readonly string[][] Countries = {
            new[] { "IL", "Asia/Jerusalem" }, new[] { "US", "America/New_York" }, new[] { "GB", "Europe/London" }, new[] { "DE", "Europe/Berlin" }, new[] { "FR", "Europe/Paris" },
            new[] { "ES", "Europe/Madrid" }, new[] { "IT", "Europe/Rome" }, new[] { "NL", "Europe/Amsterdam" }, new[] { "PL", "Europe/Warsaw" }, new[] { "TR", "Europe/Istanbul" },
            new[] { "PT", "Europe/Lisbon" }, new[] { "BR", "America/Sao_Paulo" }, new[] { "AE", "Asia/Dubai" }, new[] { "JP", "Asia/Tokyo" }, new[] { "CN", "Asia/Shanghai" } };

        static XElement El(SystemConfig cfg) { var e = cfg.Doc.Root.Element("TIME"); if (e == null) { e = new XElement("TIME"); cfg.Doc.Root.Add(e); } return e; }

        public static TimeZoneInfo Zone(SystemConfig cfg)
        {
            var id = (string)(cfg.Doc.Root.Element("TIME") ?? new XElement("T")).Attribute("ZONE");
            if (string.IsNullOrEmpty(id)) return TimeZoneInfo.Local;
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (Exception) { return TimeZoneInfo.Local; }
        }

        /// <summary>A moment (UTC) as the IT company reads it.</summary>
        public static DateTime Local(SystemConfig cfg, DateTime utc) { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc.ToUniversalTime(), DateTimeKind.Utc), Zone(cfg)); }

        public static Msg Get(SystemConfig cfg)
        {
            var e = cfg.Doc.Root.Element("TIME") ?? new XElement("TIME");
            var z = Zone(cfg);
            var m = new Msg().Set("country", (string)e.Attribute("COUNTRY")).Set("zone", z.Id).Set("now", Local(cfg, SystemClock.UtcNow).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                .Set("offset", z.GetUtcOffset(SystemClock.UtcNow).TotalMinutes).Set("dst", z.IsDaylightSavingTime(SystemClock.UtcNow) ? 1 : 0)
                .Set("ntp", (string)e.Attribute("NTP") ?? "pool.ntp.org").Set("ntpOn", (string)e.Attribute("NTP_ON") == "Y" ? 1 : 0).Set("canSetNtp", OperatingSystem() ? 1 : 0);
            foreach (var c in Countries) m.Add("countries", new Msg().Set("code", c[0]).Set("zone", c[1]));
            return m;
        }

        static bool OperatingSystem() { return Environment.OSVersion.Platform == PlatformID.Win32NT; }

        /// <summary>Saves the country / zone (checked against the system's zones) and the NTP server; on Windows applies NTP when on.</summary>
        public static Msg Save(SystemConfig cfg, Msg b, string admin, string ip)
        {
            lock (cfg)
            {
                var e = El(cfg);
                var country = (b["country"] ?? "").Trim().ToUpperInvariant();
                var zone = (b["zone"] ?? "").Trim();
                if (zone.Length == 0 && country.Length > 0) { var c = Countries.FirstOrDefault(x => x[0] == country); if (c != null) zone = c[1]; }
                if (zone.Length > 0) { try { TimeZoneInfo.FindSystemTimeZoneById(zone); } catch (Exception) { throw new ApiException(400, "ZONE", "Unknown time zone: " + zone); } e.SetAttributeValue("ZONE", zone); }
                if (country.Length > 0) e.SetAttributeValue("COUNTRY", country);
                if (b["ntp"] != null)
                {
                    var n = b["ntp"].Trim();
                    if (n.Length == 0 || n.Length > 200 || n.IndexOfAny(new[] { '"', '\'', ';', '&', '|', '<', '>', '`', '$', '\\' }) >= 0 || n.Contains("..")) throw new ApiException(400, "NTP", "The time server is not valid.");
                    e.SetAttributeValue("NTP", n);
                }
                if (b["ntpOn"] != null) e.SetAttributeValue("NTP_ON", b.Bool("ntpOn") ? "Y" : "N");
                cfg.Save();
                SysLog.Write(ip, "Admin", admin + " time settings: " + (string)e.Attribute("COUNTRY") + " " + (string)e.Attribute("ZONE") + " NTP " + (string)e.Attribute("NTP") + " " + (string)e.Attribute("NTP_ON"));
                var r = Get(cfg);
                if ((string)e.Attribute("NTP_ON") == "Y" && OperatingSystem() && b.Bool("apply")) r.Set("ntpResult", ApplyNtp((string)e.Attribute("NTP")));
                return r;
            }
        }

        /// <summary>Windows only: the computer takes its time from this server (w32tm), then synchronises now.</summary>
        static string ApplyNtp(string server)
        {
            Func<string, string> run = (args) =>
            {
                var psi = new ProcessStartInfo("w32tm.exe", args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                var r = ProcessRunner.Run(psi, Limits.Short);   // static review: stdout was read to the end before stderr, without a limit
                return (r.Out + r.Err + (r.TimedOut ? "w32tm did not finish in " + ProcessRunner.Describe(Limits.Short) : "")).Trim();
            };
            try { return run("/config /manualpeerlist:\"" + server + "\" /syncfromflags:manual /update") + "\n" + run("/resync"); }
            catch (Exception x) { return "w32tm: " + x.Message; }
        }
    }
}
