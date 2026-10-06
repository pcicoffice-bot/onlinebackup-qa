using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// VND-010..060: the server is sold to IT companies (vendors). Each vendor has its own administrators, sees only its
    /// own customers (Profile.xml OWNER = vendor id, as Ahsay's sub-admin owner), has its own branding on mails and on the
    /// agent, and its limits (number of users, total quota). The system administrator (ADMIN) sees everything.
    /// In system.xml: &lt;VENDOR ID NAME PRODUCT COMPANY PHONE EMAIL WEBSITE COLOR MAX_USERS MAX_QUOTA_GB&gt;
    ///                  &lt;VENDOR_ADMIN LOGIN_NAME HASHED_PWD TOTP_SECRET/&gt;…&lt;/VENDOR&gt;
    /// </summary>
    public static class Vendors
    {
        public static readonly string[] BrandAttrs = { "PRODUCT", "SLOGAN", "COMPANY", "PHONE", "EMAIL", "WEBSITE", "COLOR", "ACCENT", "LOGO", "LANGUAGE" };

        /// <summary>VND-075: a logo is a PNG / JPEG data URI of at most 300KB (no SVG: it can carry scripts).</summary>
        public static void CheckLogo(string v)
        {
            if (string.IsNullOrEmpty(v)) return;
            if (v.Length > 400000 || !Regex.IsMatch(v, "^data:image/(png|jpeg);base64,[A-Za-z0-9+/=]+$")) throw new ApiException(400, "LOGO", "The logo must be PNG or JPEG, up to 300 KB.");
        }

        public static IEnumerable<XElement> All(SystemConfig cfg) { return cfg.Doc.Root.Elements("VENDOR"); }

        public static XElement Find(SystemConfig cfg, string id)
        {
            return string.IsNullOrEmpty(id) ? null : All(cfg).FirstOrDefault(v => string.Equals((string)v.Attribute("ID"), id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>VND-010: create or update a vendor (system administrator only).</summary>
        public static XElement Save(SystemConfig cfg, Msg b)
        {
            var id = (b["id"] ?? "").Trim().ToLowerInvariant();
            if (!Regex.IsMatch(id, "^[a-z0-9][a-z0-9_-]{1,31}$")) throw new ApiException(400, "VENDOR_ID", "Reseller id: 2–32 lowercase Latin letters, digits, - or _.");
            lock (cfg)
            {
                var v = Find(cfg, id);
                if (v == null) { v = new XElement("VENDOR", new XAttribute("ID", id)); cfg.Doc.Root.Add(v); }
                if (b["name"] != null) v.SetAttributeValue("NAME", b["name"]);
                CheckLogo(b["brandLOGO"]);
                foreach (var a in BrandAttrs) if (b["brand" + a] != null) v.SetAttributeValue(a, b["brand" + a]);
                if (b["maxUsers"] != null) v.SetAttributeValue("MAX_USERS", b.Long("maxUsers"));
                if (b["maxQuotaGB"] != null) v.SetAttributeValue("MAX_QUOTA_GB", b["maxQuotaGB"]);
                if (b["disabled"] != null) v.SetAttributeValue("DISABLED", b["disabled"] == "1" ? "Y" : "N");
                cfg.Save();
                return v;
            }
        }

        /// <summary>VND-020: an administrator of a vendor (a login name unique on the whole server).</summary>
        public static void SaveAdmin(SystemConfig cfg, string vendorId, string login, string password)
        {
            if (string.IsNullOrEmpty(login) || login.Length < 3) throw new ApiException(400, "LOGIN", "The user name is too short.");
            if (!Passwords.Ok(password)) throw new ApiException(400, "PASSWORD", Passwords.Rule);
            lock (cfg)
            {
                var v = Find(cfg, vendorId);
                if (v == null) throw new ApiException(404, "NOT_FOUND", "The reseller does not exist.");
                if (string.Equals((string)cfg.Admin.Attribute("LOGIN_NAME"), login, StringComparison.OrdinalIgnoreCase)) throw new ApiException(409, "EXISTS", "The name is taken.");
                var other = All(cfg).SelectMany(x => x.Elements("VENDOR_ADMIN")).FirstOrDefault(a => string.Equals((string)a.Attribute("LOGIN_NAME"), login, StringComparison.OrdinalIgnoreCase));
                if (other != null && other.Parent != v) throw new ApiException(409, "EXISTS", "The name is taken by another reseller.");
                if (other == null) { other = new XElement("VENDOR_ADMIN", new XAttribute("LOGIN_NAME", login), new XAttribute("TOTP_SECRET", "")); v.Add(other); }
                other.SetAttributeValue("HASHED_PWD", PasswordHash.Create(password));
                cfg.Save();
            }
        }

        /// <summary>The vendor of an administrator login; null = not a vendor administrator.</summary>
        public static XElement AdminOf(SystemConfig cfg, string login, out XElement adminElement)
        {
            adminElement = All(cfg).SelectMany(x => x.Elements("VENDOR_ADMIN")).FirstOrDefault(a => string.Equals((string)a.Attribute("LOGIN_NAME"), login, StringComparison.Ordinal));
            return adminElement == null ? null : adminElement.Parent;
        }

        /// <summary>Branding value: the vendor's own, else the server's, else the fallback.</summary>
        public static string Brand(SystemConfig cfg, string vendorId, string attr, string fallback)
        {
            var v = Find(cfg, vendorId);
            var own = v == null ? null : (string)v.Attribute(attr);
            if (!string.IsNullOrEmpty(own)) return own;
            var b = cfg.Doc.Root.Element("BRANDING");
            var s = b == null ? null : (string)b.Attribute(attr);
            return string.IsNullOrEmpty(s) ? fallback : s;
        }

        /// <summary>VND-030: a new user of a vendor within its limits (number of users, total quota given out).</summary>
        public static void CheckLimits(SystemConfig cfg, Users users, string vendorId, long? newQuota)
        {
            var v = Find(cfg, vendorId);
            if (v == null) throw new ApiException(404, "NOT_FOUND", "The reseller does not exist.");
            if ((string)v.Attribute("DISABLED") == "Y") throw new ApiException(403, "VENDOR_DISABLED", "The reseller is disabled.");
            var mine = users.Logins().Select(l => users.LoadProfile(l)).Where(p => string.Equals(p.Get("OWNER"), vendorId, StringComparison.OrdinalIgnoreCase)).ToList();
            long maxUsers; long.TryParse((string)v.Attribute("MAX_USERS") ?? "0", out maxUsers);
            if (maxUsers > 0 && mine.Count >= maxUsers) throw new ApiException(409, "VENDOR_LIMIT", "You have reached the number of users in your plan (" + maxUsers + ").");
            double maxGb; double.TryParse((string)v.Attribute("MAX_QUOTA_GB") ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture, out maxGb);
            if (maxGb > 0)
            {
                if (newQuota == null || newQuota <= 0) throw new ApiException(400, "QUOTA_REQUIRED", "A reseller with a total quota must set a quota for every user.");
                long given = mine.Sum(p => p.GetLong("QUOTA"));
                if (given + newQuota.Value > (long)(maxGb * 1024 * 1024 * 1024)) throw new ApiException(409, "VENDOR_QUOTA", "The reseller's total quota is used up (" + maxGb + " GB).");
            }
        }
    }
}
