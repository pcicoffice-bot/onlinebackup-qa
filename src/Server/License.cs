using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using OnlineBackup.Core;

namespace OnlineBackup.Server
{
    /// <summary>
    /// LIC-010..060: licences, as Ahsay's licence server does. The product owner signs a licence for one IT company's
    /// server (its SERVER_ID) with his private key (ECDSA P-256, kept only by him); the server checks the signature with
    /// the public key built into the product. No valid licence → the free basic edition (Free) with its limits.
    /// Licence text: base64url(JSON payload) "." base64url(signature).
    /// Payload: id, company, edition (FREE / PRO), serverId, maxUsers, maxStorageGB, modules[], issued, expires.
    /// The owner's tools: "license-keygen" (once, on his own computer) and "license-issue" (per customer).
    /// </summary>
    public sealed class License
    {
        /// <summary>The product owner's public key (SubjectPublicKeyInfo, base64). Empty until he runs license-keygen and sends it.</summary>
        public const string BuiltInPublicKey = "";

        /// <summary>The product owner's own product name, shown as "powered by" without the WHITELABEL module.</summary>
        public const string OwnerProduct = "ITSguard Server Online";

        public static readonly string[] AllModules = { "FILE", "MSSQL", "MYSQL", "POSTGRESQL", "SYSTEMSTATE", "BAREMETAL", "HYPERV", "VMWARE", "ORACLE", "DOMINO", "M365", "GWS", "WHITELABEL", "REPLICATION", "VENDORS" };

        /// <summary>The free basic edition: to enter the market — files only, small, with "powered by".</summary>
        public static License Free(string reason)
        {
            // LIC-015 (owner): the free edition is limited by the computers backed up and the stored volume
            return new License { Edition = "FREE", Company = "", MaxUsers = 0, MaxDevices = 10, MaxStorageGB = 500, Modules = new List<string> { "FILE", "SYSTEMSTATE" }, Reason = reason };
        }

        public string Id = "", Company = "", Edition = "FREE", ServerId = "", Reason = "", Center = "";
        /// <summary>LIC-115: no OK from the licensing centre: fully working until TemporaryUntil, then the free edition.</summary>
        public bool Temporary; public DateTime TemporaryUntil;
        public int MaxUsers, MaxDevices; public double MaxStorageGB;
        public List<string> Modules = new List<string>();
        public DateTime Issued, Expires;
        public bool Valid { get { return Edition != "FREE"; } }

        public bool Has(string module) { return Modules.Contains(module, StringComparer.OrdinalIgnoreCase); }

        // LIC-130: only a test build (Debug) takes another verification key; the shipped build (Release) knows only the
        // owner's key — otherwise anyone could set a variable, sign a licence of their own and get every module for free
#if DEBUG
        static string PublicKey { get { var env = Environment.GetEnvironmentVariable("OB_LICENSE_PUBKEY"); return !string.IsNullOrEmpty(env) ? env : BuiltInPublicKey; } }
#else
        static string PublicKey { get { return BuiltInPublicKey; } }
#endif

        static string B64u(byte[] b) { return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        static byte[] UnB64u(string s) { s = s.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4)); }

        /// <summary>LIC-020: the licence of this server, or Free with the reason.</summary>
        public static License Check(string text, string serverId, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(text)) return Free("No licence");
            var pub = PublicKey;
            if (string.IsNullOrEmpty(pub)) return Free("This version has no licensing key");
            var parts = text.Trim().Split('.');
            if (parts.Length != 2) return Free("Invalid licence");
            byte[] payload, sig;
            try { payload = UnB64u(parts[0]); sig = UnB64u(parts[1]); } catch (FormatException) { return Free("Invalid licence"); }
            using (var ec = ECDsa.Create())
            {
                ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pub), out _);
                if (!ec.VerifyData(payload, sig, HashAlgorithmName.SHA256)) return Free("The licence signature is wrong");
            }
            Dictionary<string, object> j;
            try { j = Json.Obj(Json.Parse(Encoding.UTF8.GetString(payload))); } catch (FormatException) { return Free("Invalid licence"); }
            var l = new License
            {
                Id = Json.Str(j, "id") ?? "", Company = Json.Str(j, "company") ?? "", Edition = (Json.Str(j, "edition") ?? "PRO").ToUpperInvariant(),
                ServerId = Json.Str(j, "serverId") ?? "", Center = Json.Str(j, "center") ?? "", MaxUsers = (int)Json.Num(j, "maxUsers"), MaxDevices = (int)Json.Num(j, "maxDevices"),
                MaxStorageGB = j.ContainsKey("maxStorageGB") && j["maxStorageGB"] is double ? (double)j["maxStorageGB"] : 0,
                Modules = Json.Arr(j.ContainsKey("modules") ? j["modules"] : null).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToList(),
                Issued = DateTime.Parse(Json.Str(j, "issued") ?? "2000-01-01", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                Expires = DateTime.Parse(Json.Str(j, "expires") ?? "2000-01-01", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
            };
            if (!string.Equals(l.ServerId, serverId, StringComparison.OrdinalIgnoreCase)) return Free("The licence belongs to another server (" + l.ServerId + ")");
            if (nowUtc > l.Expires) return Free("The licence expired on " + l.Expires.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
            if (l.Edition == "FREE") return Free("Free licence");
            return l;
        }

        // ---------------------------------------------------------------- the product owner's tools (private key never leaves his computer)

        /// <summary>LIC-040: a new key pair: the private key (PKCS#8, base64) to keep safe, the public key to build into the product.</summary>
        public static string[] KeyGen()
        {
            using (var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                return new[] { Convert.ToBase64String(ec.ExportPkcs8PrivateKey()), Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()) };
        }

        /// <summary>LIC-050: signs a licence.</summary>
        public static string Issue(string privateKeyB64, string id, string company, string edition, string serverId, int maxUsers, double maxStorageGB, IEnumerable<string> modules, DateTime issued, DateTime expires, int maxDevices = 0, string center = "")
        {
            var payload = Json.Write(new Dictionary<string, object>
            {
                { "id", id }, { "company", company }, { "center", center ?? "" }, { "edition", edition.ToUpperInvariant() }, { "serverId", serverId }, { "maxUsers", maxUsers }, { "maxDevices", maxDevices }, { "maxStorageGB", maxStorageGB },
                { "modules", modules.Select(m => (object)m.ToUpperInvariant()).ToList() },
                { "issued", issued.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) }, { "expires", expires.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) }
            });
            var bytes = Encoding.UTF8.GetBytes(payload);
            using (var ec = ECDsa.Create())
            {
                ec.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyB64), out _);
                return B64u(bytes) + "." + B64u(ec.SignData(bytes, HashAlgorithmName.SHA256));
            }
        }

        /// <summary>LIC-120: an answer of the licensing centre is signed with the owner's key.</summary>
        public static bool VerifyCenter(byte[] answer, byte[] signature)
        {
            var pub = PublicKey;
            if (string.IsNullOrEmpty(pub)) return false;
            using (var ec = ECDsa.Create()) { ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pub), out _); return ec.VerifyData(answer, signature, HashAlgorithmName.SHA256); }
        }

        /// <summary>The signed licence with the licensing centre's verdict applied (system.xml LICENSE attributes).</summary>
        public static License Effective(string text, string serverId, DateTime nowUtc, System.Xml.Linq.XElement state)
        {
            var l = Check(text, serverId, nowUtc);
            if (!l.Valid || string.IsNullOrEmpty(l.Center) || state == null) return l;
            var st = (string)state.Attribute("ONLINE_STATUS");
            if (st == "REVOKED" || st == "INVALID") return Free(((string)state.Attribute("ONLINE_MESSAGE")) ?? "The licence was revoked by the licensing centre");
            var since = (string)state.Attribute("TEMP_SINCE");
            if (!string.IsNullOrEmpty(since))
            {
                var t = DateTime.Parse(since, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal).AddDays(LicenseCheckin.GraceDays);
                if (nowUtc > t) return Free("No confirmation from the licensing centre for more than " + LicenseCheckin.GraceDays + " days: " + ((string)state.Attribute("ONLINE_MESSAGE") ?? ""));
                l.Temporary = true; l.TemporaryUntil = t; l.Reason = (string)state.Attribute("ONLINE_MESSAGE") ?? "";
            }
            return l;
        }

        public Msg ToMsg()
        {
            var m = new Msg().Set("edition", Edition).Set("company", Company).Set("id", Id).Set("maxUsers", MaxUsers).Set("maxDevices", MaxDevices).Set("maxStorageGB", MaxStorageGB)
                .Set("modules", string.Join(",", Modules.ToArray())).Set("reason", Reason);
            if (Valid) m.Set("expires", Expires.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (Temporary) m.Set("temporaryUntil", TemporaryUntil.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            return m;
        }
    }
}
