using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>HTTPS in front of the test server (TLS 1.2 only, self-signed certificate), like the HTTPS binding in production.</summary>
    public sealed class TlsFront : IDisposable
    {
        readonly TcpListener l = new TcpListener(IPAddress.Loopback, 0);
        readonly X509Certificate2 cert;
        readonly int backend;
        volatile bool stop;
        public int Port { get { return ((IPEndPoint)l.LocalEndpoint).Port; } }
        public string Url { get { return "https://localhost:" + Port; } }
        public string Fingerprint { get { return Bytes.Hex(Bytes.Sha256(cert.RawData)); } }

        public TlsFront(int backendPort)
        {
            backend = backendPort;
            using (var rsa = RSA.Create(2048))
            {
                var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); req.CertificateExtensions.Add(san.Build());
                using (var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30)))
                    cert = new X509Certificate2(c.Export(X509ContentType.Pfx));
            }
            l.Start();
            new Thread(() =>
            {
                while (!stop)
                {
                    TcpClient c; try { c = l.AcceptTcpClient(); } catch { return; }
                    new Thread(() => Serve(c)) { IsBackground = true }.Start();
                }
            }) { IsBackground = true }.Start();
        }

        void Serve(TcpClient c)
        {
            try
            {
                using (c)
                using (var ssl = new SslStream(c.GetStream()))
                using (var b = new TcpClient("localhost", backend))
                {
                    ssl.AuthenticateAsServer(cert, false, SslProtocols.Tls12, false);
                    var bs = b.GetStream();
                    var up = new Thread(() => { try { ssl.CopyTo(bs); b.Client.Shutdown(SocketShutdown.Send); } catch { } }) { IsBackground = true };
                    up.Start();
                    try { bs.CopyTo(ssl); } catch { }
                    up.Join(2000);
                }
            }
            catch { }
        }

        public void Dispose() { stop = true; l.Stop(); }
    }

    public class TlsTests
    {
        [Fact]
        public void BuiltinTls12WithPinnedCertificate_BacksUpAndRestores_WrongPinIsRefused()
        {
            using (var env = new Env())
            using (var front = new TlsFront(new Uri(env.Url).Port))
            {
                env.CreateUser("tls2026", "Customer-Pass-1");
                var app = new AgentApp(Path.Combine(env.Root, "tlsagent"));
                app.Register(front.Url, "tls2026", "Customer-Pass-1", null, "PC-2003", "BUILTIN", front.Fingerprint);   // pin from the installer
                Assert.Equal("BUILTIN", app.Home.Tls);
                Assert.Equal(front.Fingerprint, app.Home.Pin);
                var src = env.Dir("src");
                File.WriteAllText(Path.Combine(src, "a.txt"), "over TLS 1.2");
                File.WriteAllBytes(Path.Combine(src, "b.bin"), Enumerable.Range(0, 300000).Select(i => (byte)(i * 7)).ToArray());
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "T", Sources = { src } });
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);
                var t = env.Dir("restore");
                var r = app.RestoreFor(app.Interactive("Customer-Pass-1", null), set.Id);
                r.Run(null, t, null, false);
                Assert.Equal(0, r.Failed);
                Assert.Equal("over TLS 1.2", File.ReadAllText(Path.Combine(t, Env.Rel(src), "a.txt")));

                var other = new AgentApp(Path.Combine(env.Root, "tlsagent2"));
                var ex = Assert.Throws<AgentException>(() => other.Register(front.Url, "tls2026", "Customer-Pass-1", null, "PC-X", "BUILTIN", new string('0', 64)));
                Assert.Equal("CERT", ex.Code);
                var untrusted = Assert.Throws<AgentException>(() => new Client(front.Url) { Builtin = true, Retries = 0 }.Call("GET", "/api/brand"));
                Assert.Equal("CERT", untrusted.Code);                         // no pin, self-signed: not trusted by the chain
            }
        }

        [Fact]
        public void ModernWindowsWithTheCompanysSelfSignedCertificate_PinnedForTheAgentAndForRestic()
        {
            using (var env = new Env())
            using (var front = new TlsFront(new Uri(env.Url).Port))
            {
                env.CreateUser("pin2026", "Customer-Pass-1");
                var app = new AgentApp(Path.Combine(env.Root, "pinagent"));
                app.Register(front.Url, "pin2026", "Customer-Pass-1", null, "PC-10", "SYSTEM", front.Fingerprint);      // the system's TLS + the pin
                Assert.Equal("SYSTEM", app.Home.Tls);
                Assert.Equal(front.Fingerprint, app.Home.Pin);
                Assert.Empty(app.Sets());                                                                              // a call over TLS works (no sets yet)
                var pem = Setup.ServerCertificatePem(new Uri(front.Url), front.Fingerprint);
                Assert.StartsWith("-----BEGIN CERTIFICATE-----", pem);
                Assert.Throws<AgentException>(() => Setup.ServerCertificatePem(new Uri(front.Url), new string('1', 64)));

                var r = Environment.GetEnvironmentVariable("OB_RESTIC");
                if (string.IsNullOrEmpty(r) || !File.Exists(r)) return;
                var pemFile = Path.Combine(env.Root, "server.pem"); File.WriteAllText(pemFile, pem);
                var cfg = app.Home.Config; cfg.SetAttributeValue("CACERT", pemFile); app.Home.Config = cfg;
                var src = env.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "restic over the company's TLS");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "R", Engine = "RESTIC", Sources = { src } });
                var b = app.Backup(set.Id);
                Assert.True(b.Result == "BS_STOP_SUCCESS", string.Join("\n", b.LogLines));
            }
        }

        [Fact]
        public void Net40AgentUnderMonoUsesTheBuiltinTls()
        {
            var mono = new[] { "/usr/bin/mono", "/usr/local/bin/mono" }.FirstOrDefault(File.Exists);
            var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Agent", "bin", "Debug", "net40", "OnlineBackup.Agent.exe"));
            if (mono == null || !File.Exists(exe)) return;
            using (var env = new Env())
            using (var front = new TlsFront(new Uri(env.Url).Port))
            {
                env.CreateUser("monotls26", "Customer-Pass-1");
                var home = Path.Combine(env.Root, "monotls");
                var src = env.Dir("src");
                File.WriteAllText(Path.Combine(src, "doc.txt"), "2003-style agent");
                Func<string, string> run = args =>
                {
                    var p = Process.Start(new ProcessStartInfo(mono, "\"" + exe + "\" " + args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                    var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    Assert.True(p.ExitCode == 0, args.Split(' ')[0] + " failed: " + o);
                    return o.Trim();
                };
                run("register --home \"" + home + "\" --server " + front.Url + " --login monotls26 --password Customer-Pass-1 --computer W2003 --tls builtin --pin " + front.Fingerprint);
                var setId = run("addset --home \"" + home + "\" --password Customer-Pass-1 --name Docs --source \"" + src + "\"").Split('\n').Last().Trim();
                Assert.StartsWith("BS_STOP_SUCCESS", run("backup --home \"" + home + "\" --set " + setId));
                var target = Path.Combine(env.Root, "monorestore");
                Assert.Contains("restored=1", run("restore --home \"" + home + "\" --set " + setId + " --password Customer-Pass-1 --target \"" + target + "\""));
            }
        }
    }
}
