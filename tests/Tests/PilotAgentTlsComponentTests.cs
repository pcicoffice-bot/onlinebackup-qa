using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// TLS and the certificate pin — Client (the system's TLS and the agent's built-in TLS 1.2) and BackupRun alone, in front
    /// of the recording stand-in server (StubServer) behind a TLS 1.2 front with its own self-signed certificate.
    /// Component contract (tests/QA/specs.py AG-07):
    ///   purpose  talk TLS 1.2 (also on old Windows, with the built-in TLS) and pin the server certificate
    ///   input    the server's certificate pinned (as recorded, and written with colons and capitals); another certificate;
    ///            no pin and a self-signed certificate
    ///   expected the pinned one accepted: a whole backup goes through TLS 1.2 and its newest point restores identical
    ///            (SHA-256); another certificate refused before any request reaches the server (nothing sent); without a
    ///            pin a self-signed certificate is refused
    /// The front listens on 127.0.0.1 (not "localhost"): the pin is kept per host for the whole process (Client.UsePin),
    /// so this class never changes the pin other test classes use for "localhost". The stand-in server answers only its
    /// own host and port, so the front rewrites "127.0.0.1:front" to "localhost:server" (same length) on the way up.
    /// </summary>
    public class PilotAgentTlsComponentTests
    {
        sealed class Front : IDisposable
        {
            readonly TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            readonly X509Certificate2 cert;
            readonly int backend;
            public int Handshakes, Refused;
            public readonly List<SslProtocols> Protocols = new List<SslProtocols>();
            public int Port { get { return ((IPEndPoint)l.LocalEndpoint).Port; } }
            public string Url { get { return "https://127.0.0.1:" + Port + "/"; } }
            public string Fingerprint { get { return Bytes.Hex(Bytes.Sha256(cert.RawData)); } }

            public static X509Certificate2 NewCertificate()
            {
                using (var rsa = RSA.Create(2048))
                {
                    var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); req.CertificateExtensions.Add(san.Build());
                    using (var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30)))
                        return new X509Certificate2(c.Export(X509ContentType.Pfx));
                }
            }

            public Front(int backendPort)
            {
                backend = backendPort; cert = NewCertificate(); l.Start();
                new Thread(() =>
                {
                    while (true)
                    {
                        TcpClient c; try { c = l.AcceptTcpClient(); } catch (Exception) { return; }
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
                    {
                        try { ssl.AuthenticateAsServer(cert, false, SslProtocols.Tls12, false); }
                        catch (Exception) { Interlocked.Increment(ref Refused); return; }
                        Interlocked.Increment(ref Handshakes); lock (Protocols) Protocols.Add(ssl.SslProtocol);
                        using (var b = new TcpClient("localhost", backend))
                        {
                            var bs = b.GetStream();
                            var up = new Thread(() => { try { Up(ssl, bs); b.Client.Shutdown(SocketShutdown.Send); } catch (Exception) { } }) { IsBackground = true };
                            up.Start();
                            try { bs.CopyTo(ssl); } catch (Exception) { }
                            up.Join(2000);
                        }
                    }
                }
                catch (Exception) { }
            }

            /// <summary>Copies the requests up, "127.0.0.1:front" → "localhost:server" (also across two reads).</summary>
            void Up(Stream from, Stream to)
            {
                var a = Encoding.ASCII.GetBytes("127.0.0.1:" + Port); var z = Encoding.ASCII.GetBytes("localhost:" + backend);
                if (a.Length != z.Length) throw new InvalidOperationException("test setup: the ports differ in length");
                var pending = new List<byte>(); var buf = new byte[16384]; int n;
                while ((n = from.Read(buf, 0, buf.Length)) > 0)
                {
                    for (int i = 0; i < n; i++) pending.Add(buf[i]);
                    var p = pending.ToArray();
                    for (int i = 0; i + a.Length <= p.Length; i++)
                    {
                        int k = 0; while (k < a.Length && p[i + k] == a[k]) k++;
                        if (k == a.Length) Buffer.BlockCopy(z, 0, p, i, z.Length);
                    }
                    // hold back only a tail that could be the start of the host (never a whole request's end)
                    int keep = 0;
                    for (int len = Math.Min(a.Length - 1, p.Length); len > 0 && keep == 0; len--)
                    {
                        int k = 0; while (k < len && p[p.Length - len + k] == a[k]) k++;
                        if (k == len) keep = len;
                    }
                    to.Write(p, 0, p.Length - keep); to.Flush();
                    pending = p.Skip(p.Length - keep).ToList();
                }
                if (pending.Count > 0) { to.Write(pending.ToArray(), 0, pending.Count); to.Flush(); }
            }

            public void Dispose() { l.Stop(); }
        }

        static Front NewFront(AgentRig rig)
        {
            var f = new Front(new Uri(rig.Server.Url).Port);
            if (f.Port.ToString().Length != new Uri(rig.Server.Url).Port.ToString().Length) { f.Dispose(); throw NotTested.Because("the free ports of this run differ in length (the Host rewrite keeps the length)"); }
            return f;
        }

        static Client Over(Front front, string pin, bool builtin) { return new Client(front.Url, pin) { Builtin = builtin, Device = "device-of-the-test", Retries = 0, TimeoutMs = 30000 }; }

        static void Fill(AgentRig rig)
        {
            rig.File("a.txt", "over TLS 1.2");
            var big = new byte[1200 * 1024]; new Random(5).NextBytes(big); rig.File(Path.Combine("data", "big.bin"), big);
            rig.File("empty.txt", "");
        }

        [Theory]
        [InlineData(true)]    // the agent's own TLS 1.2 (Windows 2003 / XP)
        [InlineData(false)]   // the system's TLS with the pin
        public void ThePinnedCertificate_IsAccepted_AWholeBackupGoesThroughTls12_AndThePointRestoresIdentical(bool builtin)
        {
            using (var rig = new AgentRig())
            using (var front = NewFront(rig))
            {
                Fill(rig);
                var want = AgentRig.Tree(rig.Src);
                var client = Over(front, front.Fingerprint, builtin);
                var run = new BackupRun(client, rig.Home, rig.Set, rig.Key); run.Run();
                Assert.True(run.Result == "BS_STOP_SUCCESS", run.Result + "\n" + string.Join("\n", run.LogLines));
                Assert.Equal(3, rig.Server.Of("object").Count(q => q.Job == run.Job && q.Answered == 200));
                Assert.True(front.Handshakes > 0, "nothing went through the TLS front");
                lock (front.Protocols) Assert.All(front.Protocols, p => Assert.Equal(SslProtocols.Tls12, p));
                if (builtin) Assert.Equal(front.Fingerprint, client.SeenPin);
                Assert.Equal(want, rig.Restored());
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AnotherCertificate_IsRefused_BeforeAnyRequestReachesTheServer_NothingIsSent(bool builtin)
        {
            using (var rig = new AgentRig())
            using (var front = NewFront(rig))
            {
                Fill(rig);
                string otherPin; using (var other = Front.NewCertificate()) otherPin = Bytes.Hex(Bytes.Sha256(other.RawData));
                var run = new BackupRun(Over(front, otherPin, builtin), rig.Home, rig.Set, rig.Key);
                string result;
                try { result = run.Run() + "\n" + string.Join("\n", run.LogLines); }
                catch (AgentException e) { result = e.Code + " " + e.Message; }
                Assert.False(result.StartsWith("BS_STOP_SUCCESS", StringComparison.Ordinal), result);
                Assert.Empty(rig.Server.Requests);                                                        // nothing reached the server behind the front
                if (builtin) Assert.Equal(0, front.Handshakes);                                           // the built-in TLS refuses inside the handshake
                if (builtin) Assert.Contains("pinned", result);
                Assert.False(File.Exists(rig.StateFile), "the local index moved although nothing was backed up");

                // direct call, the built-in TLS names the reason
                if (builtin)
                {
                    var e = Assert.Throws<AgentException>(() => Over(front, otherPin, true).Call("GET", "/api/profile"));
                    Assert.Equal("CERT", e.Code);
                }
            }
        }

        [Fact]
        public void APinWithColonsAndCapitals_IsTheSamePin_WithoutAPinASelfSignedCertificateIsRefused()
        {
            using (var rig = new AgentRig())
            using (var front = NewFront(rig))
            {
                rig.Server.ProfileXml = Profile.Create("rig", "", "", "en", null).Doc.ToString();
                var shown = string.Join(":", Enumerable.Range(0, front.Fingerprint.Length / 2).Select(i => front.Fingerprint.Substring(i * 2, 2))).ToUpperInvariant();
                foreach (var builtin in new[] { true, false })
                    Assert.NotNull(Over(front, shown, builtin).Call("GET", "/api/profile")["profile"]);
                Assert.Equal(2, rig.Server.Of("profile").Count);

                // no pin: the built-in TLS checks the chain, which a self-signed certificate does not pass
                var e = Assert.Throws<AgentException>(() => new Client(front.Url) { Builtin = true, Retries = 0 }.Call("GET", "/api/profile"));
                Assert.Equal("CERT", e.Code);
                Assert.Equal(2, rig.Server.Of("profile").Count);
            }
        }
    }
}
