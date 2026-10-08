using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;
using Xunit.Abstractions;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Owner architecture plan §2, step 1: the AUTHORIZATION ORACLE. Independent of how the server implements its checks: every
    /// HTTP route of docs/AUTHZ-MATRIX.md is called as every kind of caller (anonymous, a junk or forged credential, a revoked
    /// device, the customer's device on its own and on its other computer, the customer's interactive sign-in, ANOTHER customer's
    /// device and sign-in, the system's administrator, a second administrator, the reseller that owns the customer and ANOTHER
    /// reseller) and the answer is held to the matrix:
    ///   allowed  - the request reaches the handler: never 401, never a 403 of an authorization / pilot code, never the router's
    ///              plain 404 NOT_FOUND, never a server error;
    ///   refused  - a caller without a valid credential for the route's realm gets exactly 401 (authentication comes first); a
    ///              signed-in caller without the right gets 401/403/404; and a refused call changes nothing (SHA-256 of every
    ///              file of the target customer and of the server's settings before and after) and shows none of the target
    ///              customer's names (alias, set name, file name, service call).
    ///   UNKNOWN  - the intended rule is not written anywhere (docs/AUTHZ-MATRIX.md says why): the answer is reported, not judged.
    /// Order of checks: authentication -> tenant / customer / computer / set -> capability / pilot -> handler (the pilot facts
    /// below). Fail closed: an unknown route or an unknown caller is refused.
    /// A difference between today's code and the matrix is a FINDING (docs/AUTHZ-MATRIX.md, "Findings"). The merged suite may not
    /// carry a failing test, so each known finding is listed in <see cref="AuthzMatrix.Known"/>: a case that still behaves as
    /// recorded ends the test as NOT TESTED (the repository's declared, never-PASS mechanism, NotTested.Because) naming the
    /// finding; any OTHER difference fails; and a known finding that no longer reproduces fails too, so the list is kept honest
    /// (remove it from the list in the commit that fixes it).
    /// </summary>
    public static class AuthzMatrix
    {
        // ------------------------------------------------------------------ the callers

        public const string Anon = "anon", JunkS = "junkS", JunkD = "junkD", Revoked = "revoked", DevA = "devA", DevA2 = "devA2", SesA = "sesA",
            DevB = "devB", SesB = "sesB", Admin = "admin", Staff = "staff", VndA = "vndA", VndB = "vndB";
        public static readonly string[] Callers = { Anon, JunkS, JunkD, Revoked, DevA, DevA2, SesA, DevB, SesB, Admin, Staff, VndA, VndB };
        /// <summary>Callers holding a valid credential of the customers' API (/api/...).</summary>
        public static readonly string[] AgentRealm = { DevA, DevA2, SesA, DevB, SesB };
        /// <summary>Callers holding a valid credential of the admin API (/api/admin/...).</summary>
        public static readonly string[] AdminRealm = { Admin, Staff, VndA, VndB };

        const string ANY = "anon junkS junkD revoked devA devA2 sesA devB sesB admin staff vndA vndB";
        const string SELF = "devA devA2 sesA devB sesB", SELF_SES = "sesA sesB", SET_DS = "devA sesA", SET_S = "sesA";
        const string ALLADM = "admin staff vndA vndB", SUPER = "admin staff", CUST = "admin staff vndA", VND = "vndA vndB", NONE = "";

        public sealed class Row
        {
            public string Id, Method, Path, Realm, Pilot, Allowed, Unknown = "";
            /// <summary>Body of the request for a caller (null: none).</summary>
            public Func<AuthzWorld, string, Msg> Body;
            /// <summary>The allowed callers' request is not sent (it would leave the test machine or start a background job).</summary>
            public bool NoAllowedCall;
            /// <summary>A list the caller sees filtered to its own customers: the other reseller is allowed but must see and change nothing of anna's.</summary>
            public bool Filtered;
            /// <summary>The route ends the caller's sign-in (sign out): each allowed caller sends it with a sign-in of its own made for it.</summary>
            public bool Disposable;
            public bool IsAllowed(string caller) { return (" " + Allowed + " ").Contains(" " + caller + " "); }
            public bool IsUnknown(string caller) { return (" " + Unknown + " ").Contains(" " + caller + " "); }
            public override string ToString() { return Id; }
            public Row Disposing() { Disposable = true; return this; }
        }

        static Row R(string id, string method, string path, string realm, string pilot, string allowed, Func<AuthzWorld, string, Msg> body = null, string unknown = "", bool noAllowedCall = false, bool filtered = false)
        {
            return new Row { Id = id, Method = method, Path = path, Realm = realm, Pilot = pilot, Allowed = allowed, Body = body, Unknown = unknown, NoAllowedCall = noAllowedCall, Filtered = filtered };
        }
        static Func<AuthzWorld, string, Msg> B(Func<Msg> f) { return (w, c) => f(); }
        static int seq;
        static string Next(string p) { return p + System.Threading.Interlocked.Increment(ref seq); }
        static Msg SetXml(string name) { return new Msg().Set("set", "<BACKUP_SET ID=\"1\" NAME=\"" + name + "\" TYPE=\"FILE\"><SEL-SOURCE>C:\\oracle</SEL-SOURCE><DAILY_SCHEDULE HOUR=\"1\" MINUTE=\"0\"/></BACKUP_SET>"); }

        /// <summary>Every route of src/Server/Api.cs (and what it dispatches to) in the customers' API, the admin API and the public pages.
        /// /restic/... and /api/replica/... have their own credentials and are in their own facts (rows AZ-R.., AZ-Q..).</summary>
        public static readonly Row[] Rows =
        {
            // ---------------------------------------------------------------- public (no credential)
            R("AZ-P01", "GET",  "/api/brand", "public", "-", ANY),
            R("AZ-P02", "GET",  "/api/contract", "public", "-", ANY),
            R("AZ-P03", "POST", "/api/signup", "public", "P", ANY, B(() => new Msg().Set("login", Next("oracle-signup")).Set("password", "Signup-Pass-1").Set("alias", "x").Set("email", "s@cust.invalid"))),
            R("AZ-P04", "POST", "/api/register", "public", "-", ANY, B(() => new Msg().Set("login", "anna").Set("password", "Customer-Pass-1").Set("computer", Next("PC-reg")))),
            R("AZ-P05", "POST", "/api/login", "public", "-", ANY, B(() => new Msg().Set("login", "anna").Set("password", "Customer-Pass-1"))),
            R("AZ-P06", "POST", "/api/admin/login", "public", "-", ANY, B(() => new Msg().Set("login", "nobody-oracle").Set("password", "wrong"))),
            R("AZ-P07", "GET",  "/admin", "public", "-", ANY),
            R("AZ-P08", "GET",  "/admin/app.js", "public", "-", ANY),
            R("AZ-P09", "GET",  "/admin/restore.html", "public", "P", ANY),
            R("AZ-P10", "GET",  "/restore", "public", "P", ANY),
            R("AZ-P11", "GET",  "/i18n/theme.css", "public", "-", ANY),

            // ---------------------------------------------------------------- customers' API, the caller's own account
            R("AZ-A01", "POST", "/api/logout", "agent", "-", SELF_SES).Disposing(),
            R("AZ-A02", "GET",  "/api/client/files", "agent", "P", SELF),
            R("AZ-A03", "GET",  "/api/client/file?name={clientfile}", "agent", "P", SELF),
            R("AZ-A04", "GET",  "/api/quota", "agent", "-", SELF),
            R("AZ-A05", "POST", "/api/folders", "agent", "-", SELF, B(() => new Msg().Set("computer", "PC-oracle").Set("dirs", "C:\\oracle"))),
            R("AZ-A06", "GET",  "/api/profile", "agent", "-", SELF),
            R("AZ-A07", "POST", "/api/totp/enable", "agent", "-", SELF_SES),
            R("AZ-A08", "POST", "/api/totp/confirm", "agent", "-", SELF_SES, B(() => new Msg().Set("code", "000000"))),
            R("AZ-A09", "POST", "/api/totp/disable", "agent", "-", SELF_SES),
            R("AZ-A10", "GET",  "/api/tickets", "agent", "-", SELF),
            R("AZ-A11", "POST", "/api/tickets", "agent", "-", SELF, B(() => new Msg().Set("computer", "PC-oracle").Set("subject", "oracle call").Set("description", "d"))),
            R("AZ-A12", "POST", "/api/sets", "agent", "S", SELF_SES, B(() => SetXml(Next("OracleNewSet")))),
            R("AZ-A13", "GET",  "/api/webrestore/sets", "agent", "P", SELF_SES),
            R("AZ-A14", "POST", "/api/webrestore/{rset}/points", "agent", "P", SET_S, B(() => new Msg().Set("key", "wrong-key"))),
            R("AZ-A15", "POST", "/api/webrestore/{rset}/search", "agent", "P", SET_S, B(() => new Msg().Set("key", "wrong-key").Set("q", "x"))),
            R("AZ-A16", "POST", "/api/webrestore/{rset}/ls", "agent", "P", SET_S, B(() => new Msg().Set("key", "wrong-key"))),
            R("AZ-A17", "POST", "/api/webrestore/{rset}/download", "agent", "P", SET_S, B(() => new Msg().Set("key", "wrong-key"))),
            R("AZ-A99", "GET",  "/api/no-such-route", "agent", "-", NONE),

            // ---------------------------------------------------------------- customers' API, one set of anna's (S1 = anna's set on PC-annapc)
            R("AZ-S01", "POST", "/api/sets/{set}/key", "agent", "-", SET_S, B(() => new Msg().Set("key", Convert.ToBase64String(new byte[96])))),
            R("AZ-S02", "POST", "/api/sets/{set}/settings", "agent", "S", SET_S, B(() => SetXml("AnnaSetNameX9"))),
            R("AZ-S03", "GET",  "/api/sets/{copy}/sharedkey", "agent", "-", SET_S),
            R("AZ-S04", "POST", "/api/sets/{rset}/restic", "agent", "P", SET_DS, unknown: DevA2),
            R("AZ-S05", "POST", "/api/sets/{rset}/resticreport", "agent", "P", SET_DS, B(() => new Msg().Set("result", "BS_STOP_SUCCESS").Set("job", "2026-01-01-00-00-00")), unknown: DevA2),
            R("AZ-S06", "GET",  "/api/sets/{set}/points", "agent", "S", SET_DS, unknown: DevA2),
            R("AZ-S07", "GET",  "/api/sets/{set}/files", "agent", "S", SET_DS, unknown: DevA2),
            R("AZ-S08", "GET",  "/api/sets/{set}/object?loc={loc}&job={ljob}", "agent", "S", SET_S),
            R("AZ-S09", "GET",  "/api/sets/{set}/object?loc={loc}&job={ljob}&test=1", "agent", "S", SET_DS, unknown: DevA2),
            R("AZ-S10", "POST", "/api/sets/{set}/restoretest", "agent", "S", SET_DS, B(() => new Msg().Set("checked", 1).Set("ok", 1).Set("failed", 0)), unknown: DevA2),
            R("AZ-S11", "POST", "/api/sets/{set}/restorelog", "agent", "S", SET_S, B(() => new Msg().Set("result", "RESTORE_STOP_SUCCESS"))),
            R("AZ-S12", "GET",  "/api/sets/{set}/begin", "agent", "S", SET_DS, unknown: DevA2),
            R("AZ-S13", "POST", "/api/sets/{set}/interrupted", "agent", "-", SET_DS, (w, c) => new Msg().Set("job", "2020-01-01-00-00-00").Set("why", "oracle"), unknown: DevA2),
            R("AZ-S14", "POST", "/api/sets/{set}/progress", "agent", "-", SET_DS, (w, c) => new Msg().Set("job", w.Job), unknown: DevA2),
            R("AZ-S15", "PUT",  "/api/sets/{set}/jobs/{job}/object?rel=AAAAAAAAAAAAAAAAAAAAAAAAAA&seq=0&kind=F", "agent", "S", SET_DS, B(() => new Msg()), unknown: DevA2),
            R("AZ-S16", "POST", "/api/sets/{set}/jobs/{job}/delete", "agent", "S", SET_DS, B(() => new Msg().Add("rels", new Msg().Set("rel", "AAAAAAAAAAAAAAAAAAAAAAAAAA"))), unknown: DevA2),
            R("AZ-S17", "POST", "/api/sets/{set}/jobs/{job}/commit", "agent", "S", SET_DS, B(() => new Msg().Set("result", "BS_STOP_SUCCESS")), unknown: DevA2),
            R("AZ-S18", "POST", "/api/sets/{set}/jobs/{job}/abort", "agent", "-", SET_DS, B(() => new Msg().Set("result", "BS_STOP_BY_USER")), unknown: DevA2),
            R("AZ-S99", "GET",  "/api/sets/{set}/no-such-route", "agent", "-", NONE),

            // ---------------------------------------------------------------- admin API, the server's own pages (every administrator; a reseller: refused)
            R("AZ-M01", "POST", "/api/admin/logout", "admin", "-", ALLADM).Disposing(),
            R("AZ-M02", "GET",  "/api/admin/me", "admin", "-", ALLADM),
            R("AZ-M03", "POST", "/api/admin/totp/enable", "admin", "-", ALLADM, noAllowedCall: true),
            R("AZ-M04", "POST", "/api/admin/totp/confirm", "admin", "-", ALLADM, B(() => new Msg().Set("code", "000000")), noAllowedCall: true),
            R("AZ-M05", "GET",  "/api/admin/deletes", "admin", "-", SUPER),
            R("AZ-M06", "POST", "/api/admin/deletes/settings", "admin", "-", SUPER, B(() => new Msg().Set("dual", "1"))),
            R("AZ-M07", "POST", "/api/admin/deletes/nope/approve", "admin", "-", SUPER),
            R("AZ-M08", "POST", "/api/admin/deletes/nope/cancel", "admin", "-", SUPER),
            R("AZ-M09", "GET",  "/api/admin/recycle", "admin", "-", SUPER),
            R("AZ-M10", "POST", "/api/admin/recycle/nope/restore", "admin", "-", SUPER),
            R("AZ-M11", "GET",  "/api/admin/templates", "admin", "-", SUPER),
            R("AZ-M12", "POST", "/api/admin/templates", "admin", "-", SUPER, B(() => SetXml("OracleTemplate").Set("name", Next("oracle-tpl")).Set("type", "FILE"))),
            R("AZ-M13", "POST", "/api/admin/templates/nope/delete", "admin", "-", SUPER),
            R("AZ-M14", "GET",  "/api/admin/time", "admin", "-", SUPER),
            R("AZ-M15", "POST", "/api/admin/time", "admin", "-", SUPER, B(() => new Msg())),
            R("AZ-M16", "GET",  "/api/admin/contract", "admin", "-", SUPER),
            R("AZ-M17", "POST", "/api/admin/contract", "admin", "-", SUPER, B(() => new Msg())),
            R("AZ-M18", "GET",  "/api/admin/ticketsettings", "admin", "-", SUPER),
            R("AZ-M19", "POST", "/api/admin/ticketsettings", "admin", "-", SUPER, B(() => new Msg())),
            R("AZ-M20", "GET",  "/api/admin/vendors", "admin", "?", SUPER),
            R("AZ-M21", "POST", "/api/admin/vendors", "admin", "?", SUPER, B(() => new Msg().Set("id", Next("oracle-v")).Set("name", "Oracle V"))),
            R("AZ-M22", "POST", "/api/admin/vendors/beta/admins", "admin", "?", SUPER, B(() => new Msg().Set("login", Next("oracle-badmin")).Set("password", "Oracle-Admin-Pass-1"))),
            R("AZ-M23", "GET",  "/api/admin/defaults", "admin", "-", SUPER),
            R("AZ-M24", "POST", "/api/admin/defaults", "admin", "-", SUPER, B(() => new Msg())),
            R("AZ-M25", "GET",  "/api/admin/guard", "admin", "-", SUPER),
            R("AZ-M26", "POST", "/api/admin/guard", "admin", "-", SUPER, B(() => new Msg())),
            R("AZ-M27", "POST", "/api/admin/guard/block", "admin", "-", SUPER, B(() => new Msg().Set("ip", "203.0.113.77").Set("hours", "1"))),
            R("AZ-M28", "POST", "/api/admin/guard/unblock", "admin", "-", SUPER, B(() => new Msg().Set("ip", "203.0.113.77"))),
            R("AZ-M29", "GET",  "/api/admin/configbackup", "admin", "-", SUPER),
            R("AZ-M30", "POST", "/api/admin/configbackup", "admin", "-", SUPER, B(() => new Msg())),
            R("AZ-M31", "POST", "/api/admin/configbackup/now", "admin", "-", SUPER),
            R("AZ-M32", "GET",  "/api/admin/configbackup/{cfgfile}", "admin", "-", SUPER),
            R("AZ-M33", "GET",  "/api/admin/homes", "admin", "-", SUPER),
            R("AZ-M34", "POST", "/api/admin/rebuild", "admin", "-", SUPER, (w, c) => new Msg().Set("login", "anna").Set("set", w.Set1)),
            R("AZ-M35", "POST", "/api/admin/verify", "admin", "-", SUPER, (w, c) => new Msg().Set("login", "anna").Set("set", w.Set1)),
            R("AZ-M36", "POST", "/api/admin/maintenance", "admin", "-", SUPER, B(() => new Msg())),
            R("AZ-M37", "GET",  "/api/admin/update/source", "admin", "-", SUPER),
            R("AZ-M38", "POST", "/api/admin/update/source", "admin", "-", SUPER, B(() => new Msg().Set("repo", "").Set("token", ""))),
            R("AZ-M39", "POST", "/api/admin/update/upload", "admin", "-", SUPER, B(() => new Msg())),
            R("AZ-M40", "GET",  "/api/admin/update", "admin", "-", SUPER),
            R("AZ-M41", "POST", "/api/admin/update", "admin", "-", SUPER, noAllowedCall: true),
            R("AZ-M42", "GET",  "/api/admin/license", "admin", "-", SUPER),
            R("AZ-M43", "POST", "/api/admin/license", "admin", "-", SUPER, B(() => new Msg().Set("key", "not-a-licence"))),
            R("AZ-M44", "GET",  "/api/admin/settings", "admin", "-", SUPER),
            R("AZ-M45", "POST", "/api/admin/settings", "admin", "-", SUPER, B(() => new Msg().Set("senderName", "Oracle IT"))),
            R("AZ-M46", "POST", "/api/admin/testmail", "admin", "-", SUPER, B(() => new Msg().Set("to", "oracle@example.invalid"))),
            R("AZ-M47", "GET",  "/api/admin/staff", "admin", "-", SUPER),
            R("AZ-M48", "POST", "/api/admin/staff", "admin", "-", SUPER, B(() => new Msg().Set("login", Next("oracle-staff")).Set("name", "S").Set("password", "Oracle-Staff-Pass-1"))),
            R("AZ-M49", "POST", "/api/admin/staff/nope/unlock", "admin", "-", SUPER),
            R("AZ-M50", "GET",  "/api/admin/logs?cat=System", "admin", "-", SUPER),
            R("AZ-M51", "GET",  "/api/admin/logs?cat=Backup&login=anna&set={set}", "admin", "-", SUPER),
            R("AZ-M52", "POST", "/api/admin/replicate", "admin", "P", SUPER),
            R("AZ-M53", "POST", "/api/admin/aitest", "admin", "P", SUPER),
            R("AZ-M54", "GET",  "/api/admin/tickets/deleted", "admin", "-", SUPER),
            R("AZ-M55", "POST", "/api/admin/tickets/deleted/0/restore", "admin", "-", SUPER),
            R("AZ-M56", "POST", "/api/admin/brand", "admin", "-", VND, B(() => new Msg().Set("brandSLOGAN", "oracle"))),
            R("AZ-M57", "GET",  "/api/admin/no-such-route", "admin", "-", NONE),

            // ---------------------------------------------------------------- admin API, lists every administrator opens (a reseller: its own customers only)
            R("AZ-L01", "GET",  "/api/admin/tickets?scope=all", "admin", "-", ALLADM, filtered: true),
            R("AZ-L02", "GET",  "/api/admin/tasks?hours=744", "admin", "-", ALLADM, filtered: true),
            R("AZ-L03", "GET",  "/api/admin/dashboard", "admin", "-", ALLADM, filtered: true),
            R("AZ-L04", "GET",  "/api/admin/live", "admin", "-", ALLADM, filtered: true),
            R("AZ-L05", "GET",  "/api/admin/checks", "admin", "-", ALLADM, filtered: true),
            R("AZ-L06", "GET",  "/api/admin/users", "admin", "-", ALLADM, filtered: true),
            R("AZ-L07", "POST", "/api/admin/users", "admin", "-", ALLADM, B(() => new Msg().Set("login", Next("oracle-cust")).Set("password", "Customer-Pass-9").Set("alias", "o").Set("quotaGB", 1).Set("email", "o@cust.invalid"))),
            R("AZ-L08", "GET",  "/api/admin/clientpackage?os=zip", "admin", "-", ALLADM),
            R("AZ-L09", "GET",  "/api/admin/insights", "admin", "P", ALLADM, filtered: true),
            R("AZ-L10", "POST", "/api/admin/bulk", "admin", "-", ALLADM, B(() => new Msg().Set("action", "quota").Set("quotaGB", "3").Add("logins", new Msg().Set("login", "anna"))), filtered: true),

            // ---------------------------------------------------------------- admin API, one customer (anna, owned by the reseller vndA) / one of its sets (S2)
            R("AZ-C01", "POST", "/api/admin/tickets", "admin", "-", CUST, B(() => new Msg().Set("login", "anna").Set("subject", "oracle call").Set("priority", "Normal").Set("status", "New"))),
            R("AZ-C02", "GET",  "/api/admin/tickets/{ticket}", "admin", "-", CUST),
            R("AZ-C03", "POST", "/api/admin/tickets/{ticket}/note", "admin", "-", CUST, B(() => new Msg().Set("text", "oracle note"))),
            R("AZ-C04", "POST", "/api/admin/tickets/{ticket2}/delete", "admin", "-", CUST, B(() => new Msg().Set("revision", "-1"))),
            R("AZ-C05", "GET",  "/api/admin/users/anna/compliance", "admin", "-", CUST),
            R("AZ-C06", "POST", "/api/admin/users/anna/aidiagnose", "admin", "P", CUST, (w, c) => new Msg().Set("set", w.Set1).Set("cat", "Backup").Set("file", w.LogFile)),
            R("AZ-C07", "GET",  "/api/admin/users/anna/sets/{set2}", "admin", "-", CUST),
            R("AZ-C08", "POST", "/api/admin/users/anna/sets/{set2}", "admin", "S", CUST, B(() => SetXml("AnnaAdminSet"))),
            R("AZ-C09", "GET",  "/api/admin/users/anna/sets/{set2}/runs", "admin", "-", CUST),
            R("AZ-C10", "POST", "/api/admin/users/anna/sets/{set2}/run", "admin", "S", CUST, B(() => new Msg())),
            R("AZ-C11", "POST", "/api/admin/users/anna/sets/{set2}/stop", "admin", "-", CUST, B(() => new Msg())),
            R("AZ-C12", "POST", "/api/admin/users/anna/sets/{set2}/addcomputer", "admin", "S", CUST, B(() => new Msg().Set("computer", Next("PC-added")))),
            R("AZ-C13", "POST", "/api/admin/users/anna/sets/{set2}/removecomputer", "admin", "-", CUST, B(() => new Msg().Set("computer", "PC-ghost"))),
            R("AZ-C14", "POST", "/api/admin/users/anna/sets/{set2}/move", "admin", "S", CUST, B(() => new Msg().Set("computer", "PC-ghost"))),
            R("AZ-C15", "GET",  "/api/admin/users/anna/computers", "admin", "-", CUST),
            R("AZ-C16", "POST", "/api/admin/users/anna/computers/disconnect", "admin", "-", CUST, B(() => new Msg().Set("computer", "PC-ghost"))),
            R("AZ-C17", "POST", "/api/admin/users/anna/computers/move", "admin", "P", CUST, B(() => new Msg().Set("computer", "PC-ghost").Set("target", "anna"))),
            R("AZ-C18", "GET",  "/api/admin/users/anna/folders?computer=PC-annapc", "admin", "-", CUST),
            R("AZ-C19", "POST", "/api/admin/users/anna/browse", "admin", "-", CUST, B(() => new Msg().Set("computer", "PC-annapc").Set("path", "C:\\"))),
            R("AZ-C20", "POST", "/api/admin/users/anna/unlock", "admin", "-", CUST, B(() => new Msg())),
            R("AZ-C21", "POST", "/api/admin/users/anna/delete", "admin", "-", CUST, B(() => new Msg().Set("set", "1"))),
            R("AZ-C22", "POST", "/api/admin/users/anna/contacts", "admin", "-", CUST, B(() => new Msg().Add("contacts", new Msg().Set("name", "A").Set("email", "anna@cust.invalid")))),
            R("AZ-C23", "POST", "/api/admin/users/anna/details", "admin", "-", CUST, B(() => new Msg().Set("phone", "000"))),
            R("AZ-C24", "POST", "/api/admin/users/anna/untrash?set={rset}", "admin", "S", CUST, B(() => new Msg())),
            R("AZ-C25", "POST", "/api/admin/users/anna/resettotp", "admin", "-", CUST, B(() => new Msg())),
            R("AZ-C26", "POST", "/api/admin/users/anna/security", "admin", "-", CUST, B(() => new Msg().Set("lockAttempts", "5"))),
            R("AZ-C27", "POST", "/api/admin/users/anna/quota", "admin", "-", CUST, B(() => new Msg().Set("quotaGB", "2"))),
            R("AZ-C28", "POST", "/api/admin/users/anna/unfreeze", "admin", "-", CUST, B(() => new Msg())),
            R("AZ-C29", "GET",  "/api/admin/keys/anna/{set}", "admin", "-", CUST),
        };

        // ------------------------------------------------------------------ known findings (today's code differs from the matrix)

        public sealed class Finding { public string Id, Severity, Text; }
        /// <summary>"row/caller" -> the finding. Kept in step with docs/AUTHZ-MATRIX.md "Findings".</summary>
        public static readonly Dictionary<string, Finding> Known = new Dictionary<string, Finding>
        {
            { "AZ-R01/anon-pilot", new Finding { Id = "AZF-2", Severity = "low", Text = "order of checks: /restic/... answers 403 SCOPE (pilot) before authentication; an anonymous caller learns the feature state instead of 401" } },
            { "AZ-Q01/anon-pilot", new Finding { Id = "AZF-3", Severity = "low", Text = "order of checks: /api/replica/... answers 403 SCOPE (pilot) before the replica token is checked" } },
            { "AZ-A14/sesB-pilot", new Finding { Id = "AZF-5", Severity = "low", Text = "order of checks: web restore answers 403 SCOPE (pilot) to another customer's sign-in before the tenant check (same answer for any set id: nothing of anna's is shown)" } },
            { "AZ-A15/sesB-pilot", new Finding { Id = "AZF-5", Severity = "low", Text = "order of checks: web restore answers 403 SCOPE (pilot) to another customer's sign-in before the tenant check (same answer for any set id: nothing of anna's is shown)" } },
            { "AZ-A16/sesB-pilot", new Finding { Id = "AZF-5", Severity = "low", Text = "order of checks: web restore answers 403 SCOPE (pilot) to another customer's sign-in before the tenant check (same answer for any set id: nothing of anna's is shown)" } },
            { "AZ-A17/sesB-pilot", new Finding { Id = "AZF-5", Severity = "low", Text = "order of checks: web restore answers 403 SCOPE (pilot) to another customer's sign-in before the tenant check (same answer for any set id: nothing of anna's is shown)" } },
            { "AZ-X01/anon-pilot", new Finding { Id = "AZF-4", Severity = "low", Text = "order of checks: an old-Windows X-Agent header is refused 403 SCOPE before authentication on every /api/... route" } },
        };

        public static readonly string[] Secrets = { "AnnaAliasK3", "AnnaSetNameX9", "AnnaFileQ7", "AnnaTicketW1", "AnnaAdminSetZ5" };
    }

    /// <summary>One server with two resellers, two customers and every kind of caller, built once for the oracle's rows.</summary>
    public sealed class AuthzWorld : IDisposable
    {
        public Env Env;
        public string Ticket2, LogFile, Set1, Copy1, Set2, RSet, Job, Loc, LocJob, Ticket, CfgFile, ClientFile, ResticToken;
        public readonly Dictionary<string, Dictionary<string, string>> Headers = new Dictionary<string, Dictionary<string, string>>();
        public string AnnaDir;

        public AuthzWorld()
        {
            Env = new Env();
            var sys = Env.Admin();
            sys.Call("POST", "/api/admin/vendors", new Msg().Set("id", "acme-it").Set("name", "Acme IT"));
            sys.Call("POST", "/api/admin/vendors/acme-it/admins", new Msg().Set("login", "acme-admin").Set("password", "Acme-Admin-Pass-1"));
            sys.Call("POST", "/api/admin/vendors", new Msg().Set("id", "beta").Set("name", "Beta"));
            sys.Call("POST", "/api/admin/vendors/beta/admins", new Msg().Set("login", "beta-admin").Set("password", "Beta-Admin-Pass-1"));
            sys.Call("POST", "/api/admin/staff", new Msg().Set("login", "tech").Set("name", "Tech").Set("password", "Tech-Pass-12"));
            var acme = TestAuth.Admin(Env.Url, "acme-admin", "Acme-Admin-Pass-1", 0);
            var beta = TestAuth.Admin(Env.Url, "beta-admin", "Beta-Admin-Pass-1", 0);
            var staff = TestAuth.Admin(Env.Url, "tech", "Tech-Pass-12", 0);
            acme.Call("POST", "/api/admin/users", new Msg().Set("login", "anna").Set("password", "Customer-Pass-1").Set("alias", "AnnaAliasK3").Set("quotaGB", 5).Set("email", "anna@cust.invalid"));
            beta.Call("POST", "/api/admin/users", new Msg().Set("login", "bob").Set("password", "Customer-Pass-2").Set("alias", "Bob").Set("quotaGB", 5).Set("email", "bob@cust.invalid"));
            AnnaDir = Path.Combine(Env.HomeA, "anna");

            var annaPc = Env.Agent("anna", "Customer-Pass-1", name: "annapc");
            var anna2 = Env.Agent("anna", "Customer-Pass-1", name: "anna2");
            var annaOld = Env.Agent("anna", "Customer-Pass-1", name: "annaold");
            var bobPc = Env.Agent("bob", "Customer-Pass-2", name: "bobpc");
            sys.Call("POST", "/api/admin/users/anna/computers/disconnect", new Msg().Set("computer", "PC-annaold"));

            // S1: anna's set with one backup (PC-annapc); its key kept for recovery; C1: its copy on PC-anna2
            var src = Env.Dir("anna-src");
            File.WriteAllText(Path.Combine(src, "AnnaFileQ7.txt"), "anna's data");
            var sesA = annaPc.Interactive("Customer-Pass-1", null);
            var s1 = annaPc.CreateSet(sesA, "Customer-Pass-1", new BackupSetInfo { Name = "AnnaSetNameX9", Sources = { src } });
            Assert.Equal("BS_STOP_SUCCESS", annaPc.Backup(s1.Id).Result);
            Set1 = s1.Id;
            LogFile = Path.GetFileName(Directory.GetFiles(Path.Combine(AnnaDir, "logs", Set1, "Backup"), "*.log").First());
            var keyFile = Path.Combine(AnnaDir, "db", "keys", Set1 + ".bin");
            if (!File.Exists(keyFile)) sesA.Call("POST", "/api/sets/" + Set1 + "/key", new Msg().Set("key", Convert.ToBase64String(Encoding.ASCII.GetBytes("oracle-test-key-material-0123456789"))));
            Copy1 = sys.Call("POST", "/api/admin/users/anna/sets/" + Set1 + "/addcomputer", new Msg().Set("computer", "PC-anna2"))["id"];
            // S2: the set the admin rows act on; RS: a restic set (its repository exists, a token issued)
            var users = Env.Api.UserStore;
            Set2 = users.CreateSet("anna", new BackupSetInfo { Name = "AnnaAdminSetZ5", Computer = "PC-annapc", Sources = { "C:\\admin" } }, "127.0.0.1").Id;
            RSet = users.CreateSet("anna", new BackupSetInfo { Name = "AnnaResticSet", Engine = "RESTIC", Computer = "PC-annapc", Sources = { "C:\\restic" } }, "127.0.0.1").Id;
            var rs = new ResticStore(AnnaDir, RSet); rs.Create(); File.WriteAllText(Path.Combine(rs.Dir, "config"), "oracle-config");
            ResticToken = ResticStore.NewToken(AnnaDir, RSet);

            var files = sesA.Call("GET", "/api/sets/" + Set1 + "/files");
            var obj = files.List("files").SelectMany(f => f.List("objects")).First();
            Loc = obj["loc"]; LocJob = obj["job"];
            // a run of S1 open now
            Job = annaPc.DeviceClient().Call("GET", "/api/sets/" + Set1 + "/begin")["job"];
            Ticket = sys.Call("POST", "/api/admin/tickets", new Msg().Set("login", "anna").Set("subject", "AnnaTicketW1").Set("priority", "Normal").Set("status", "New"))["id"];
            Ticket2 = sys.Call("POST", "/api/admin/tickets", new Msg().Set("login", "anna").Set("subject", "AnnaTicketW1 (the one deleted)").Set("priority", "Normal").Set("status", "New"))["id"];
            var cb = sys.Call("POST", "/api/admin/configbackup/now");
            CfgFile = Regex.Match(cb.ToString(), @"[A-Za-z0-9_.-]+\.zip").Value;
            if (CfgFile.Length == 0) CfgFile = "none.zip";
            var cf = Regex.Match(annaPc.DeviceClient().Call("GET", "/api/client/files").ToString(), "name=\"([^\"]+)\"");
            ClientFile = cf.Success ? cf.Groups[1].Value : "none.exe";

            Func<string, string, Dictionary<string, string>> h = (k, v) => new Dictionary<string, string> { { k, v } };
            Headers[AuthzMatrix.Anon] = new Dictionary<string, string>();
            Headers[AuthzMatrix.JunkS] = h("X-Session", "0123456789abcdef0123456789abcdef0123456789abcdef");
            Headers[AuthzMatrix.JunkD] = h("X-Device", Convert.ToBase64String(Encoding.UTF8.GetBytes("anna")) + "." + annaPc.DeviceId + ".00deadbeef");
            Headers[AuthzMatrix.Revoked] = h("X-Device", annaOld.Home.DeviceToken);
            Headers[AuthzMatrix.DevA] = h("X-Device", annaPc.Home.DeviceToken);
            Headers[AuthzMatrix.DevA2] = h("X-Device", anna2.Home.DeviceToken);
            Headers[AuthzMatrix.SesA] = h("X-Session", sesA.Session);
            Headers[AuthzMatrix.DevB] = h("X-Device", bobPc.Home.DeviceToken);
            Headers[AuthzMatrix.SesB] = h("X-Session", bobPc.Interactive("Customer-Pass-2", null).Session);
            Headers[AuthzMatrix.Admin] = h("X-Session", sys.Session);
            Headers[AuthzMatrix.Staff] = h("X-Session", staff.Session);
            Headers[AuthzMatrix.VndA] = h("X-Session", acme.Session);
            Headers[AuthzMatrix.VndB] = h("X-Session", beta.Session);
        }

        /// <summary>A new sign-in of the same kind for one caller (a route that ends the sign-in gets this one, not the shared one).</summary>
        public Dictionary<string, string> Fresh(string caller)
        {
            if (caller == AuthzMatrix.SesA || caller == AuthzMatrix.SesB)
            {
                var c = new Client(Env.Url);
                var r = c.Call("POST", "/api/login", caller == AuthzMatrix.SesA ? new Msg().Set("login", "anna").Set("password", "Customer-Pass-1") : new Msg().Set("login", "bob").Set("password", "Customer-Pass-2"));
                return new Dictionary<string, string> { { "X-Session", r["session"] } };
            }
            var login = caller == AuthzMatrix.Admin ? "admin" : caller == AuthzMatrix.Staff ? "tech" : caller == AuthzMatrix.VndA ? "acme-admin" : caller == AuthzMatrix.VndB ? "beta-admin" : null;
            if (login == null) return Headers[caller];
            return new Dictionary<string, string> { { "X-Session", Env.Api.UserStore.NewStaffSession(new Staff.SignIn { Account = Staff.Find(Env.Cfg, login), Enroll = false }) } };
        }

        public string Expand(string path)
        {
            return path.Replace("{set}", Set1).Replace("{copy}", Copy1).Replace("{set2}", Set2).Replace("{rset}", RSet).Replace("{job}", Job)
                .Replace("{loc}", Uri.EscapeDataString(Loc ?? "")).Replace("{ljob}", LocJob).Replace("{ticket2}", Ticket2).Replace("{ticket}", Ticket).Replace("{cfgfile}", CfgFile).Replace("{clientfile}", Uri.EscapeDataString(ClientFile));
        }

        public void Dispose() { Env.Dispose(); }

        // ------------------------------------------------------------------ one raw request

        public sealed class Ans { public int Status; public string Text = "", Code = "", Message = ""; }
        static readonly HttpClient http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(120) };

        public static Ans Send(string baseUrl, string method, string path, IDictionary<string, string> headers, Msg body)
        {
            var req = new HttpRequestMessage(new HttpMethod(method), baseUrl.TrimEnd('/') + path);
            foreach (var kv in headers ?? new Dictionary<string, string>()) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            if (body != null) req.Content = new ByteArrayContent(body.ToBytes());
            else if (method == "POST" || method == "PUT") req.Content = new ByteArrayContent(new Msg().ToBytes());
            using (var resp = http.SendAsync(req).GetAwaiter().GetResult())
            {
                var a = new Ans { Status = (int)resp.StatusCode };
                var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                a.Text = Encoding.UTF8.GetString(bytes);
                try { var m = Msg.Parse(bytes); a.Code = m["error"] ?? ""; a.Message = m["message"] ?? ""; } catch (Exception) { }
                return a;
            }
        }

        // ------------------------------------------------------------------ the state a refused call must not change

        static string Sha(byte[] b) { using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(b)); }
        static byte[] Read(string f) { using (var s = File.Open(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); } }

        /// <summary>SHA-256 of every file of anna's folder and of the server's settings. Left out, because every request may touch
        /// them without changing anything a person decides: the device list's "last seen" (devices.xml), the system log, the
        /// address guard's counters, the kept sign-ins, and the licence check-in's own attributes in system.xml.</summary>
        public Dictionary<string, string> State()
        {
            var d = new Dictionary<string, string>();
            foreach (var root in new[] { AnnaDir, Env.SystemHome })
            {
                if (!Directory.Exists(root)) continue;
                foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    var rel = f.Substring(Env.Root.Length).Replace('\\', '/');
                    var name = Path.GetFileName(f).ToLowerInvariant();
                    if (name == "devices.xml" || name.StartsWith("kept-sessions") || name.Contains("guard") || rel.Contains("/logs/System") || rel.Contains("/logs/Access") || rel.Contains("/logs/Admin")
                        || rel.StartsWith("/system/logs/") || name.EndsWith(".tmp") || name.Contains(".recv")) continue;
                    try
                    {
                        if (name == "system.xml")
                        {
                            var x = XDocument.Parse(Encoding.UTF8.GetString(Read(f)));
                            x.Root.Elements("LICENSE").Remove();
                            d[rel] = Sha(Encoding.UTF8.GetBytes(x.ToString()));
                        }
                        else d[rel] = Sha(Read(f));
                    }
                    catch (IOException) { }
                }
            }
            return d;
        }

        public static string Diff(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            var ch = a.Keys.Union(b.Keys).Where(k => !a.ContainsKey(k) || !b.ContainsKey(k) || a[k] != b[k]).OrderBy(k => k).ToList();
            return string.Join(", ", ch.Select(k => (!a.ContainsKey(k) ? "+" : !b.ContainsKey(k) ? "-" : "~") + k));
        }
    }

    [Collection("Guard")]   // Guard's state is one per process: never at the same time as the other classes that use it
    public class AuthzOracleTests : IClassFixture<AuthzWorld>
    {
        readonly AuthzWorld w; readonly ITestOutputHelper output;
        public AuthzOracleTests(AuthzWorld w, ITestOutputHelper output) { this.w = w; this.output = output; }

        static readonly HashSet<string> AuthzCodes = new HashSet<string> { "SESSION", "DEVICE", "VENDOR", "RIGHTS", "TOTP_ENROLL", "SUSPENDED", "IP_NOT_ALLOWED", "BLOCKED", "SCOPE", "LOCAL_ONLY" };

        /// <summary>The request reached the route's handler (whatever the handler then answered about the body).</summary>
        static bool Reached(AuthzWorld.Ans a, AuthzMatrix.Row row)
        {
            // a public sign-in route answers a wrong password itself (that is its handler, not an authorization refusal)
            if (row.Realm == "public" && (a.Status == 401 || a.Status == 423) && (a.Code == "LOGIN" || a.Code == "LOCKED")) return true;
            // a server error of the handler is still past every authorization check (no check answers 500); it is reported apart
            if (a.Status == 401) return false;
            if (a.Status == 403 && AuthzCodes.Contains(a.Code)) return false;
            // the router's and the scope check's own "Not found." (and a static page that is not there); a handler's own 404 says what is missing
            if (a.Status == 404 && (a.Code == "" || (a.Code == "NOT_FOUND" && a.Message == "Not found."))) return false;
            return true;
        }

        static bool AuthenticatedFor(string realm, string caller)
        {
            if (realm == "agent") return AuthzMatrix.AgentRealm.Contains(caller);
            if (realm == "admin") return AuthzMatrix.AdminRealm.Contains(caller);
            return true;
        }

        public static IEnumerable<object[]> RowIds() { return AuthzMatrix.Rows.Select(r => new object[] { r.Id }); }

        /// <summary>Applies the known findings: the differences seen in one test are all known -> NOT TESTED naming them; any other
        /// difference, or a known finding of these cases that no longer reproduces -> the test fails.</summary>
        internal static void Verdict(IEnumerable<string> keysChecked, Dictionary<string, string> differences, ITestOutputHelper output, string what)
        {
            var unexpected = differences.Where(d => !AuthzMatrix.Known.ContainsKey(d.Key)).Select(d => d.Key + ": " + d.Value).ToList();
            var fixedNow = keysChecked.Where(k => AuthzMatrix.Known.ContainsKey(k) && !differences.ContainsKey(k)).Select(k => k + " (" + AuthzMatrix.Known[k].Id + ") now matches the matrix - remove it from AuthzMatrix.Known and docs/AUTHZ-MATRIX.md").ToList();
            Assert.True(unexpected.Count == 0 && fixedNow.Count == 0, what + ": " + (unexpected.Count + fixedNow.Count) + " difference(s) from the authorization matrix:\n" + string.Join("\n", unexpected.Concat(fixedNow)));
            var known = differences.Where(d => AuthzMatrix.Known.ContainsKey(d.Key)).ToList();
            if (known.Count > 0)
                throw NotTested.Because("KNOWN AUTHZ FINDING(S) " + string.Join("; ", known.Select(k => AuthzMatrix.Known[k.Key].Id + " [" + AuthzMatrix.Known[k.Key].Severity + "] " + k.Key + " -> " + k.Value + " (" + AuthzMatrix.Known[k.Key].Text + ")")));
        }

        [Theory]
        [MemberData(nameof(RowIds))]
        public void EveryCaller_OnThisRoute_GetsTheMatrixAnswer(string id)
        {
            var row = AuthzMatrix.Rows.Single(r => r.Id == id);
            var path = w.Expand(row.Path);
            var differences = new Dictionary<string, string>();
            var keys = new List<string>();
            var log = new List<string>();
            // refused callers first (each with the state before and after), then the UNKNOWN ones (reported), then the allowed ones
            var order = AuthzMatrix.Callers.Where(c => !row.IsAllowed(c) && !row.IsUnknown(c))
                .Concat(AuthzMatrix.Callers.Where(row.IsUnknown)).Concat(AuthzMatrix.Callers.Where(row.IsAllowed));
            foreach (var caller in order)
            {
                var key = row.Id + "/" + caller; keys.Add(key);
                var body = row.Body == null ? null : row.Body(w, caller);
                if (row.IsAllowed(caller))
                {
                    if (row.NoAllowedCall) { log.Add(caller + ": allowed (not sent)"); continue; }
                    var before = row.Filtered ? w.State() : null;
                    var a = AuthzWorld.Send(w.Env.Url, row.Method, path, row.Disposable ? w.Fresh(caller) : w.Headers[caller], body);
                    log.Add(caller + ": " + a.Status + " " + a.Code + (a.Status >= 500 ? " (HANDLER ERROR after the authorization checks - not an authorization finding)" : ""));
                    if (!Reached(a, row)) differences[key] = "expected ALLOWED, got " + a.Status + " " + a.Code;
                    // a list filtered to the caller's own customers: the other reseller sees and changes nothing of anna's
                    if (row.Filtered && caller == AuthzMatrix.VndB)
                    {
                        foreach (var s in AuthzMatrix.Secrets) if (a.Text.Contains(s)) differences[key] = "the other reseller's list showed anna's " + s;
                        var ch = AuthzWorld.Diff(before, w.State());
                        if (ch.Length > 0) differences[key] = "the other reseller's call changed anna's data: " + ch;
                    }
                }
                else if (row.IsUnknown(caller))
                {
                    var a = AuthzWorld.Send(w.Env.Url, row.Method, path, w.Headers[caller], body);
                    log.Add(caller + ": " + a.Status + " " + a.Code + " (UNKNOWN rule: reported, not judged)");
                }
                else
                {
                    var before = w.State();
                    var a = AuthzWorld.Send(w.Env.Url, row.Method, path, w.Headers[caller], body);
                    var after = w.State();
                    log.Add(caller + ": " + a.Status + " " + a.Code);
                    bool unauth = !AuthenticatedFor(row.Realm, caller);
                    if (unauth && a.Status != 401) differences[key] = "expected 401 (no valid credential for this API), got " + a.Status + " " + a.Code;
                    else if (!unauth && a.Status != 401 && a.Status != 403 && a.Status != 404) differences[key] = "expected REFUSED 401/403/404, got " + a.Status + " " + a.Code;
                    foreach (var s in AuthzMatrix.Secrets) if (a.Text.Contains(s)) differences[key] = (differences.ContainsKey(key) ? differences[key] + "; " : "") + "the refusal showed anna's " + s;
                    var ch = AuthzWorld.Diff(before, after);
                    if (ch.Length > 0) differences[key] = (differences.ContainsKey(key) ? differences[key] + "; " : "") + "the refused call changed state: " + ch;
                }
            }
            output.WriteLine(row.Id + " " + row.Method + " " + row.Path + "\n  " + string.Join("\n  ", log));
            Verdict(keys, differences, output, row.Id + " " + row.Method + " " + row.Path);
        }

        /// <summary>The oracle and the document list the same rows: a route added to one is added to the other.</summary>
        [Fact]
        public void TheMatrixDocument_AndTheOracle_ListTheSameRoutes()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "docs", "AUTHZ-MATRIX.md"))) dir = dir.Parent;
            if (dir == null) throw NotTested.Because("docs/AUTHZ-MATRIX.md is not in a parent folder of the test build (the tests run outside the repository)");
            var doc = File.ReadAllLines(Path.Combine(dir.FullName, "docs", "AUTHZ-MATRIX.md"));
            var inDoc = doc.Select(l => Regex.Match(l, @"^\|\s*(AZ-[A-Z]\d\d)\s*\|")).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
            var inOracle = AuthzMatrix.Rows.Select(r => r.Id).Concat(ResticAndReplicaIds).ToList();
            Assert.Equal(inDoc.Count, inDoc.Distinct().Count());
            Assert.Equal(inOracle.OrderBy(x => x), inDoc.OrderBy(x => x));
            foreach (var f in AuthzMatrix.Known.Values.Select(f => f.Id).Distinct()) Assert.True(doc.Any(l => l.Contains(f)), f + " is not described in docs/AUTHZ-MATRIX.md");
        }
        internal static readonly string[] ResticAndReplicaIds = { "AZ-R01", "AZ-R02", "AZ-R03", "AZ-R04", "AZ-R05", "AZ-Q01", "AZ-Q02", "AZ-Q03", "AZ-Q04", "AZ-X01" };

        /// <summary>RST-010: /restic/&lt;login&gt;/&lt;set&gt;/... is opened only by HTTP Basic with the set's own access token and its
        /// customer's name. No other credential of the product (a device, a sign-in, an administrator) opens it; another customer's
        /// name or another set's path with the token is refused; the repository is never deleted. (Fewer than 10 refusals: the
        /// address limit of the restic endpoint.)</summary>
        [Fact]
        public void Restic_OnlyTheSetsOwnToken_OpensItsRepository()
        {
            Func<string, string, Dictionary<string, string>> basic = (u, p) => new Dictionary<string, string> { { "Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(u + ":" + p)) } };
            var token = w.ResticToken = ResticStore.NewToken(w.AnnaDir, w.RSet);
            var cfgPath = "/restic/anna/" + w.RSet + "/config";
            var differences = new Dictionary<string, string>(); var keys = new List<string>(); var log = new List<string>();
            Action<string, string, string, Dictionary<string, string>, bool> check = (key, method, path, headers, allowed) =>
            {
                keys.Add(key);
                var before = w.State();
                var a = AuthzWorld.Send(w.Env.Url, method, path, headers, null);
                var ch = AuthzWorld.Diff(before, w.State());
                log.Add(key + " " + method + " " + path + " -> " + a.Status);
                if (allowed && a.Status != 200) differences[key] = "expected ALLOWED, got " + a.Status;
                if (!allowed && a.Status != 401 && a.Status != 403) differences[key] = "expected 401/403, got " + a.Status;
                if (!allowed && ch.Length > 0) differences[key] = "the refused call changed state: " + ch;
            };
            check("AZ-R01/anon", "GET", cfgPath, new Dictionary<string, string>(), false);
            check("AZ-R01/junk", "GET", cfgPath, basic("anna", "wrong-token"), false);
            check("AZ-R01/devA", "GET", cfgPath, w.Headers[AuthzMatrix.DevA], false);
            check("AZ-R01/admin", "GET", cfgPath, w.Headers[AuthzMatrix.Admin], false);
            check("AZ-R01/otherName", "GET", cfgPath, basic("bob", token), false);
            check("AZ-R02/otherSet", "GET", "/restic/anna/" + w.Set1 + "/config", basic("anna", token), false);
            check("AZ-R03/otherCustomerPath", "GET", "/restic/bob/" + w.RSet + "/config", basic("bob", token), false);
            check("AZ-R01/token", "GET", cfgPath, basic("anna", token), true);
            check("AZ-R04/token", "GET", "/restic/anna/" + w.RSet + "/keys/", basic("anna", token), true);
            check("AZ-R05/token-delete-repo", "DELETE", "/restic/anna/" + w.RSet, basic("anna", token), false);
            output.WriteLine(string.Join("\n", log));
            Verdict(keys, differences, output, "restic");
        }

        /// <summary>REP-010: /api/replica/... is opened only by the replica token of the second server; nothing else of the product opens it.</summary>
        [Fact]
        public void Replica_OnlyTheReplicaToken_OpensTheReceiver()
        {
            var tok = "oracle-replica-token-" + Guid.NewGuid().ToString("N");
            lock (w.Env.Cfg)
            {
                var el = w.Env.Cfg.Doc.Root.Element("REPLICA_RECEIVER"); if (el == null) { el = new XElement("REPLICA_RECEIVER"); w.Env.Cfg.Doc.Root.Add(el); }
                el.SetAttributeValue("TOKEN_HASH", Bytes.Hex(Bytes.Sha256(Encoding.UTF8.GetBytes(tok)))); w.Env.Cfg.Save();
            }
            var differences = new Dictionary<string, string>(); var keys = new List<string>(); var log = new List<string>();
            var routes = new[]
            {
                Tuple.Create("AZ-Q01", "POST", "/api/replica/user", new Msg().Set("login", "oracle-replica-user").Set("quota", "1")),
                Tuple.Create("AZ-Q02", "PUT", "/api/replica/file?login=anna&path=logs/oracle.txt", new Msg()),
                Tuple.Create("AZ-Q03", "POST", "/api/replica/commit", new Msg().Set("login", "anna").Set("set", w.Set1).Set("job", "2020-01-01-00-00-00")),
                Tuple.Create("AZ-Q04", "POST", "/api/replica/retention", new Msg().Set("login", "anna").Set("set", w.Set1)),
            };
            var callers = new Dictionary<string, Dictionary<string, string>>
            {
                { "anon", new Dictionary<string, string>() }, { "junk", new Dictionary<string, string> { { "X-Replica-Token", "wrong" } } },
                { "devA", w.Headers[AuthzMatrix.DevA] }, { "sesA", w.Headers[AuthzMatrix.SesA] }, { "admin", w.Headers[AuthzMatrix.Admin] },
            };
            foreach (var r in routes)
            {
                foreach (var c in callers)
                {
                    var key = r.Item1 + "/" + c.Key; keys.Add(key);
                    var before = w.State();
                    var a = AuthzWorld.Send(w.Env.Url, r.Item2, r.Item3, c.Value, r.Item4);
                    var ch = AuthzWorld.Diff(before, w.State());
                    log.Add(key + " -> " + a.Status + " " + a.Code);
                    if (a.Status != 401) differences[key] = "expected 401, got " + a.Status + " " + a.Code;
                    if (ch.Length > 0) differences[key] = "the refused call changed state: " + ch;
                }
            }
            // the second server's own token reaches the receiver (only the harmless route is sent)
            var ok = AuthzWorld.Send(w.Env.Url, "POST", "/api/replica/user", new Dictionary<string, string> { { "X-Replica-Token", tok } }, routes[0].Item4);
            keys.Add("AZ-Q01/token"); log.Add("AZ-Q01/token -> " + ok.Status + " " + ok.Code);
            if (ok.Status != 200) differences["AZ-Q01/token"] = "expected ALLOWED, got " + ok.Status + " " + ok.Code;
            output.WriteLine(string.Join("\n", log));
            Verdict(keys, differences, output, "replica");
        }

        /// <summary>
        /// PILOT-010 and the order of the checks: with the pilot switch on, (1) every route outside the pilot (matrix column Pilot =
        /// P) and every set route on a set outside the pilot (S: a restic set) is refused 403 SCOPE for a caller who IS allowed;
        /// (2) a caller without a credential still gets 401 first - the pilot never answers before authentication; (3) a
        /// cross-tenant caller is refused without SCOPE telling it anything about anna's set.
        /// </summary>
        [Fact]
        public void PilotOn_TheBlockedRoutesAreRefused_AfterAuthentication_AndAfterTheTenantCheck()
        {
            var differences = new Dictionary<string, string>(); var keys = new List<string>(); var log = new List<string>();
            PilotScopeServerTests.PilotOn(w.Env);
            try
            {
                foreach (var row in AuthzMatrix.Rows.Where(r => r.Pilot == "P" || r.Pilot == "S"))
                {
                    // S rows: the same route on anna's restic set (outside the pilot)
                    var path = w.Expand(row.Pilot == "S" ? row.Path.Replace("{set}", "{rset}").Replace("{set2}", "{rset}") : row.Path);
                    if (row.Pilot == "S" && path == w.Expand(row.Path) && !row.Path.Contains("{rset}")) continue;   // a set route with no set in its path (AZ-A12: the set is in the body)
                    var allowed = AuthzMatrix.Callers.FirstOrDefault(row.IsAllowed);
                    if (allowed != null && !row.NoAllowedCall)
                    {
                        var key = row.Id + "/" + allowed + "-pilot"; keys.Add(key);
                        var before = w.State();
                        var a = AuthzWorld.Send(w.Env.Url, row.Method, path, w.Headers[allowed], row.Body == null ? null : row.Body(w, allowed));
                        var ch = AuthzWorld.Diff(before, w.State());
                        log.Add(key + " " + path + " -> " + a.Status + " " + a.Code);
                        if (a.Status != 403 || a.Code != "SCOPE") differences[key] = "expected 403 SCOPE (outside the pilot), got " + a.Status + " " + a.Code;
                        if (ch.Length > 0) differences[key] = "the call refused by the pilot changed state: " + ch;
                    }
                    if (row.Realm != "public")
                    {
                        var key = row.Id + "/anon-pilot"; keys.Add(key);
                        var a = AuthzWorld.Send(w.Env.Url, row.Method, path, w.Headers[AuthzMatrix.Anon], row.Body == null ? null : row.Body(w, AuthzMatrix.Anon));
                        log.Add(key + " -> " + a.Status + " " + a.Code);
                        if (a.Status != 401) differences[key] = "expected 401 before the pilot's refusal, got " + a.Status + " " + a.Code;
                        var cross = row.Realm == "agent" ? AuthzMatrix.SesB : AuthzMatrix.VndB;
                        if (!row.IsAllowed(cross) && (path.Contains(w.RSet) || path.Contains("/anna/")))
                        {
                            var ck = row.Id + "/" + cross + "-pilot"; keys.Add(ck);
                            var c = AuthzWorld.Send(w.Env.Url, row.Method, path, w.Headers[cross], row.Body == null ? null : row.Body(w, cross));
                            log.Add(ck + " -> " + c.Status + " " + c.Code);
                            if (c.Code == "SCOPE" || (c.Status != 403 && c.Status != 404)) differences[ck] = "expected the tenant refusal (403/404, not SCOPE), got " + c.Status + " " + c.Code;
                        }
                    }
                }
                // the two realms with their own credential, and the old-Windows header, without any credential
                foreach (var x in new[] { Tuple.Create("AZ-R01/anon-pilot", "GET", "/restic/anna/" + w.RSet + "/config", new Dictionary<string, string>()),
                                          Tuple.Create("AZ-Q01/anon-pilot", "POST", "/api/replica/user", new Dictionary<string, string>()),
                                          Tuple.Create("AZ-X01/anon-pilot", "GET", "/api/profile", new Dictionary<string, string> { { "X-Agent", "OnlineBackup/1.0 (Windows NT 5.1)" } }) })
                {
                    keys.Add(x.Item1);
                    var a = AuthzWorld.Send(w.Env.Url, x.Item2, x.Item3, x.Item4, null);
                    log.Add(x.Item1 + " -> " + a.Status + " " + a.Code);
                    if (a.Status != 401) differences[x.Item1] = "expected 401 before the pilot's refusal, got " + a.Status + " " + a.Code;
                }
                // the old-Windows computer that IS signed in: refused by the pilot
                var old = new Dictionary<string, string>(w.Headers[AuthzMatrix.DevA]) { { "X-Agent", "OnlineBackup/1.0 (Windows NT 5.1)" } };
                keys.Add("AZ-X01/devA-pilot");
                var o = AuthzWorld.Send(w.Env.Url, "GET", "/api/profile", old, null);
                log.Add("AZ-X01/devA-pilot -> " + o.Status + " " + o.Code);
                if (o.Status != 403 || o.Code != "SCOPE") differences["AZ-X01/devA-pilot"] = "expected 403 SCOPE, got " + o.Status + " " + o.Code;
            }
            finally { PilotScopeServerTests.PilotOff(w.Env); }
            output.WriteLine(string.Join("\n", log));
            Verdict(keys, differences, output, "pilot");
        }

        /// <summary>Fail closed: a path no route knows, a method a route does not take, a header of the wrong kind, a credential of
        /// one API on the other, and an empty or malformed credential are all refused - never a 200, never a server error.</summary>
        [Fact]
        public void FailClosed_UnknownRoutesMethodsAndCallers_AreRefused()
        {
            var bad = new List<string>();
            var probes = new[]
            {
                Tuple.Create("GET", "/", AuthzMatrix.Anon), Tuple.Create("GET", "/nothing", AuthzMatrix.Admin), Tuple.Create("GET", "/api", AuthzMatrix.SesA),
                Tuple.Create("GET", "/api/admin", AuthzMatrix.Admin), Tuple.Create("DELETE", "/api/profile", AuthzMatrix.DevA), Tuple.Create("PUT", "/api/admin/users", AuthzMatrix.Admin),
                Tuple.Create("GET", "/api/sets/" + w.Set1, AuthzMatrix.SesA), Tuple.Create("GET", "/api/sets/" + w.Set1 + "/jobs/" + w.Job + "/nothing", AuthzMatrix.DevA),
                Tuple.Create("GET", "/api/admin/users/anna", AuthzMatrix.Admin), Tuple.Create("GET", "/api/admin/users/anna/nothing", AuthzMatrix.VndA),
                Tuple.Create("GET", "/admin/../conf/system.xml", AuthzMatrix.Anon), Tuple.Create("GET", "/admin/nothing.js", AuthzMatrix.Anon),
                Tuple.Create("GET", "/api/profile", AuthzMatrix.Admin), Tuple.Create("GET", "/api/admin/me", AuthzMatrix.SesA), Tuple.Create("GET", "/api/admin/me", AuthzMatrix.DevA),
            };
            foreach (var p in probes)
            {
                var a = AuthzWorld.Send(w.Env.Url, p.Item1, p.Item2, w.Headers[p.Item3], null);
                output.WriteLine(p.Item3 + " " + p.Item1 + " " + p.Item2 + " -> " + a.Status + " " + a.Code);
                if (a.Status < 400 || a.Status >= 500) bad.Add(p.Item3 + " " + p.Item1 + " " + p.Item2 + " -> " + a.Status + " " + a.Code);
            }
            foreach (var hdr in new[] { new Dictionary<string, string> { { "X-Session", "" } }, new Dictionary<string, string> { { "X-Device", "" } }, new Dictionary<string, string> { { "X-Device", "a.b" } },
                                        new Dictionary<string, string> { { "X-Device", "!!!.1.2" } }, new Dictionary<string, string> { { "X-Device", Convert.ToBase64String(Encoding.UTF8.GetBytes("../anna")) + ".1.2" } },
                                        new Dictionary<string, string> { { "X-Session", w.Headers[AuthzMatrix.Admin]["X-Session"] + "x" } } })
                foreach (var p in new[] { "/api/profile", "/api/admin/me" })
                {
                    var a = AuthzWorld.Send(w.Env.Url, "GET", p, hdr, null);
                    if (a.Status != 401) bad.Add(string.Join(",", hdr.Select(k => k.Key + "=" + k.Value)) + " GET " + p + " -> " + a.Status + " " + a.Code + " (expected 401)");
                }
            Assert.True(bad.Count == 0, "not refused:\n" + string.Join("\n", bad));
        }
    }

    /// <summary>Expired credentials: a customer's sign-in after its 12 hours and an administrator's after its 2 hours are refused 401 on
    /// every route of their API (the server's clock is moved; nothing else changes).</summary>
    [Collection("Guard")]
    public class AuthzOracleExpiryTests
    {
        readonly ITestOutputHelper output;
        public AuthzOracleExpiryTests(ITestOutputHelper output) { this.output = output; }

        [Fact]
        public void ExpiredSignIns_AreRefused401_OnEveryRouteOfTheirApi()
        {
            var shift = TimeSpan.Zero;
            SystemClock.Use(() => DateTime.UtcNow + shift);
            try
            {
                using (var env = new Env())
                {
                    env.CreateUser("anna", "Customer-Pass-1");
                    var app = env.Agent("anna", "Customer-Pass-1", name: "annapc");
                    var ses = app.Interactive("Customer-Pass-1", null).Session;
                    var adm = env.Admin().Session;
                    Assert.Equal(200, AuthzWorld.Send(env.Url, "GET", "/api/profile", new Dictionary<string, string> { { "X-Session", ses } }, null).Status);
                    Assert.Equal(200, AuthzWorld.Send(env.Url, "GET", "/api/admin/me", new Dictionary<string, string> { { "X-Session", adm } }, null).Status);
                    shift = TimeSpan.FromHours(13);
                    var bad = new List<string>();
                    foreach (var row in AuthzMatrix.Rows.Where(r => r.Realm != "public"))
                    {
                        var path = row.Path.Replace("{set}", "1").Replace("{copy}", "1").Replace("{set2}", "1").Replace("{rset}", "1").Replace("{job}", "2020-01-01-00-00-00")
                            .Replace("{loc}", "x").Replace("{ljob}", "2020-01-01-00-00-00").Replace("{ticket2}", "2").Replace("{ticket}", "1").Replace("{cfgfile}", "x.zip").Replace("{clientfile}", "x");
                        var tok = row.Realm == "agent" ? ses : adm;
                        var a = AuthzWorld.Send(env.Url, row.Method, path, new Dictionary<string, string> { { "X-Session", tok } }, new Msg());
                        if (a.Status != 401) bad.Add(row.Id + " " + row.Method + " " + path + " -> " + a.Status + " " + a.Code);
                    }
                    output.WriteLine(bad.Count + " not refused");
                    Assert.True(bad.Count == 0, "an expired sign-in was not refused 401:\n" + string.Join("\n", bad));
                    // the device token does not expire with the clock (it is revoked by hand): the computer still works
                    Assert.Equal(200, AuthzWorld.Send(env.Url, "GET", "/api/profile", new Dictionary<string, string> { { "X-Device", app.Home.DeviceToken } }, null).Status);
                }
            }
            finally { SystemClock.Use(null); }
        }
    }
}
