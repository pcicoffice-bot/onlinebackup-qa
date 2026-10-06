using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// The run report mail alone (Mailer, a fake SMTP server) — component contract (tests/QA/specs.py SH-03):
    ///   purpose  tell the customer what happened, once, in words that match the result — no false alarm, no missed one
    ///   input    one run report per result code; the customer's choice ALL / FAILURE / NONE; two SMTP servers, the first dead
    ///   expected exactly one mail per report; success "✓ … completed successfully"; with warnings "⚠" (never the failure
    ///            mark ✗); with errors / failed / stopped / quota "✗" with a title that says which; FAILURE sends nothing for
    ///            a plain success; NONE sends nothing; the dead first SMTP server is skipped, the mail goes through the second;
    ///            with no SMTP server nothing is sent and nothing throws
    /// </summary>
    public class NotifyComponentTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "obmail-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        readonly SystemConfig cfg; readonly Users users; readonly FakeSmtp smtp = new FakeSmtp();

        public NotifyComponentTests()
        {
            cfg = SystemConfig.Init(Path.Combine(root, "system"), "admin", "Admin-Pass-1", "localhost", new[] { Path.Combine(root, "home") + "|UNLIMITED|100" });
            var dead = new TcpListener(IPAddress.Loopback, 0); dead.Start(); var deadPort = ((IPEndPoint)dead.LocalEndpoint).Port; dead.Stop();
            cfg.Doc.Root.Elements("SMTP").Remove();
            cfg.Doc.Root.Add(new XElement("SMTP", new XAttribute("HOST", "127.0.0.1"), new XAttribute("PORT", deadPort), new XAttribute("SECURITY", "NONE")));
            cfg.Doc.Root.Add(new XElement("SMTP", new XAttribute("HOST", "127.0.0.1"), new XAttribute("PORT", smtp.Port), new XAttribute("SECURITY", "NONE")));
            cfg.Doc.Root.Elements("REPORT_SENDER").Remove();
            cfg.Doc.Root.Add(new XElement("REPORT_SENDER", new XAttribute("EMAIL", "backup@example.invalid"), new XAttribute("NAME", "Backup")));
            cfg.Save();
            users = new Users(cfg);
            users.Create("anna", "Anna-Pass-1", "Anna", null, "COMPRESSED", "anna@example.invalid", "203.0.113.5");
        }
        public void Dispose() { smtp.Dispose(); try { Directory.Delete(root, true); } catch (Exception) { } }

        static string Subject(string raw)
        {
            var m = Regex.Match(raw, @"^Subject: (.*(?:\r?\n[ \t].*)*)", RegexOptions.Multiline);
            var s = Regex.Replace(m.Groups[1].Value, @"\r?\n[ \t]", "");
            return Regex.Replace(s, @"=\?utf-8\?([BQ])\?(.*?)\?=", x => x.Groups[1].Value.ToUpperInvariant() == "B"
                ? Encoding.UTF8.GetString(Convert.FromBase64String(x.Groups[2].Value))
                : Encoding.UTF8.GetString(Regex.Replace(x.Groups[2].Value.Replace('_', ' '), "=([0-9A-F]{2})", y => ((char)Convert.ToInt32(y.Groups[1].Value, 16)).ToString()).Select(c => (byte)c).ToArray()), RegexOptions.IgnoreCase);
        }

        static string Body(string raw)
        {
            var i = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal); if (i < 0) i = raw.IndexOf("\n\n", StringComparison.Ordinal);
            var head = raw.Substring(0, i); var body = raw.Substring(i).Trim();
            if (Regex.IsMatch(head, "Content-Transfer-Encoding: base64", RegexOptions.IgnoreCase)) return Encoding.UTF8.GetString(Convert.FromBase64String(Regex.Replace(body, @"\s", "")));
            if (Regex.IsMatch(head, "Content-Transfer-Encoding: quoted-printable", RegexOptions.IgnoreCase))
                return Encoding.UTF8.GetString(Regex.Replace(body.Replace("=\r\n", "").Replace("=\n", ""), "=([0-9A-F]{2})", y => ((char)Convert.ToInt32(y.Groups[1].Value, 16)).ToString()).Select(c => (byte)c).ToArray());
            return body;
        }

        string Report(string result, string notify = "ALL")
        {
            var p = users.LoadProfile("anna"); p.SetAttr("EMAIL_NOTIFY", notify); users.SaveProfile("anna", p);
            int before; lock (smtp.Messages) before = smtp.Messages.Count;
            new Mailer(cfg).BackupReport(users.LoadProfile("anna"), new BackupSetInfo { Id = "1700000000001", Name = "Office" }, "2026-03-10-22-00-00",
                new Msg().Set("result", result).Set("new", 3).Set("upd", 1).Set("del", 0).Set("bytes", 1000), new Msg());
            lock (smtp.Messages)
            {
                Assert.InRange(smtp.Messages.Count - before, 0, 1);   // never two mails for one report
                return smtp.Messages.Count == before ? null : Subject(smtp.Messages.Last()) + "\n" + Body(smtp.Messages.Last());
            }
        }

        [Fact]
        public void EachResult_OneMail_WithTheRightMarkAndTitle()
        {
            var ok = Report("BS_STOP_SUCCESS");
            Assert.StartsWith("✓", ok); Assert.Contains("completed successfully", ok);
            var warn = Report("BS_STOP_SUCCESS_WITH_WARNING");
            Assert.False(warn.StartsWith("✗"), "a backup with warnings is marked as a failure: " + warn.Split('\n')[0]);
            Assert.Contains("warnings", warn);
            var err = Report("BS_STOP_SUCCESS_WITH_ERROR");
            Assert.StartsWith("✗", err); Assert.Contains("not backed up", err);
            var failed = Report("BS_STOP_BY_SYSTEM_ERROR");
            Assert.StartsWith("✗", failed); Assert.Contains("Backup failed", failed);
            var stopped = Report("BS_STOP_BY_USER");
            Assert.StartsWith("✗", stopped);
            Assert.False(stopped.Contains("Backup failed"), "a backup stopped by the administrator / at its maximum duration is called a failure");
            Assert.Contains("stopped", stopped);
            var quota = Report("BS_STOP_QUOTA_EXCEEDED");
            Assert.StartsWith("✗", quota); Assert.Contains("quota", quota.ToLowerInvariant());
            Assert.True(smtp.All.Contains("anna@example.invalid"));
        }

        [Fact]
        public void TheCustomersChoice_FailureOnly_And_None()
        {
            Assert.Null(Report("BS_STOP_SUCCESS", "FAILURE"));
            Assert.NotNull(Report("BS_STOP_BY_SYSTEM_ERROR", "FAILURE"));
            Assert.Null(Report("BS_STOP_BY_SYSTEM_ERROR", "NONE"));
            Assert.Null(Report("BS_STOP_SUCCESS", "NONE"));
        }

        [Fact]
        public void NoSmtpServer_NothingSent_NothingThrown()
        {
            cfg.Doc.Root.Elements("SMTP").Remove(); cfg.Save();
            Assert.False(new Mailer(cfg).Send(new[] { "x@example.invalid" }, "s", "b", "test"));
            Assert.Null(Report("BS_STOP_BY_SYSTEM_ERROR"));
        }
    }
}
