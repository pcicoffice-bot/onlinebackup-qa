using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// Adversarial challenge of the pilot backup and agent tests that passed (BK-01, BK-02, BK-04, BK-05, BK-07, AG-02,
    /// AG-05, AG-07). Each test here takes a case the passing tests left out — a kind of change they never made, a fault
    /// at another moment, a boundary next to the one they tried, the real server where they used the stand-in — and checks
    /// it against the contract in tests/QA/specs.py with an independent oracle (SHA-256 of every file of every point,
    /// exact object lists). The failing ones (bug candidates) are in PilotChallengeBackupFailingTests.cs.
    /// </summary>
    public partial class PilotChallengeBackupTests
    {
        const string Pass = "Customer-Pass-1";

        static List<string> Sent(AgentRig rig, BackupRun r)
        {
            return PilotRig.Objects(rig, r.Job).Select(q => PilotRig.RelOf(rig, q)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        }

        static void Touch(string path, int minutes) { File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(minutes)); }

        static void Write(string path, byte[] data, int minutes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, data); Touch(path, minutes);
        }

        // ------------------------------------------------------------------ BK-01: every kind of change, every point

        /// <summary>
        /// BK-01 (challenge of KnownTree_Run2SendsOnlyTheNewChanged..., which changed files only by append / delete / new /
        /// read-only and restored only the newest point of the stand-in): through the REAL server, three runs with the kinds of
        /// change it never made — a file that shrinks to 0 bytes, a 0-byte file that grows, a file replaced by a folder of the
        /// same name and back, a folder replaced by a file and back, a rename, a file deleted and created again later, a Hebrew
        /// folder and file name. At the end EVERY point is restored and compared file by file (SHA-256) with the tree as it was
        /// when that point was taken: nothing more, nothing less.
        /// </summary>
        [Fact]
        public void BK01_EveryKindOfChange_ThroughTheRealServer_EveryPointRestoresExactlyItsOwnTree()
        {
            using (var env = new Env())
            {
                env.CreateUser("chk01", Pass);
                var app = env.Agent("chk01", Pass);
                var src = env.Dir("src");
                Func<string, string> P = rel => Path.Combine(src, rel);
                Write(P("stays.txt"), Encoding.UTF8.GetBytes("never changes"), -10);
                Write(P("grows.bin"), new byte[0], -10);
                Write(P("shrinks.bin"), PilotRig.Rnd(100000, 1), -10);
                Write(P("x"), Encoding.UTF8.GetBytes("x is a file first"), -10);
                Write(P(Path.Combine("y", "inner.txt")), Encoding.UTF8.GetBytes("y is a folder first"), -10);
                Write(P("old-name.txt"), Encoding.UTF8.GetBytes("renamed in run 2"), -10);
                Write(P("comes-back.txt"), Encoding.UTF8.GetBytes("version 1"), -10);
                Write(P(Path.Combine("תיקייה", "קובץ.txt")), Encoding.UTF8.GetBytes("שלום v1"), -10);
                var set = app.CreateSet(app.Interactive(Pass, null), Pass, new BackupSetInfo { Name = "Kinds", Sources = { src }, Vss = false });
                var trees = new List<Dictionary<string, string>>();

                trees.Add(AgentRig.Tree(src));
                var r1 = app.Backup(set.Id);
                Assert.True(r1.Result == "BS_STOP_SUCCESS", r1.Result + "\n" + string.Join("\n", r1.LogLines));
                Assert.Equal(8, r1.New);

                Thread.Sleep(1100);
                Write(P("grows.bin"), PilotRig.Rnd(50000, 2), 1);                               // 0 bytes → 50 KB
                Write(P("shrinks.bin"), new byte[0], 1);                                        // 100 KB → 0 bytes
                File.Delete(P("x")); Write(P(Path.Combine("x", "child.txt")), Encoding.UTF8.GetBytes("x is a folder now"), 1);
                Directory.Delete(P("y"), true); Write(P("y"), Encoding.UTF8.GetBytes("y is a file now"), 1);
                File.Move(P("old-name.txt"), P("new-name.txt"));
                File.Delete(P("comes-back.txt"));
                Write(P(Path.Combine("תיקייה", "קובץ.txt")), Encoding.UTF8.GetBytes("שלום v2 — longer"), 1);
                trees.Add(AgentRig.Tree(src));
                var r2 = app.Backup(set.Id);
                Assert.True(r2.Result == "BS_STOP_SUCCESS", r2.Result + "\n" + string.Join("\n", r2.LogLines));
                // new: x/child.txt, y, new-name.txt; updated: grows, shrinks, Hebrew; deleted: x, y/inner.txt, old-name, comes-back
                Assert.True(r2.New == 3 && r2.Updated == 3 && r2.Deleted == 4, "new " + r2.New + " upd " + r2.Updated + " del " + r2.Deleted + "\n" + string.Join("\n", r2.LogLines));

                Thread.Sleep(1100);
                Write(P("comes-back.txt"), Encoding.UTF8.GetBytes("version 3, created again"), 2);
                Directory.Delete(P("x"), true); Write(P("x"), Encoding.UTF8.GetBytes("x is a file again"), 2);
                File.Delete(P("y")); Write(P(Path.Combine("y", "inner.txt")), Encoding.UTF8.GetBytes("y is a folder again"), 2);
                trees.Add(AgentRig.Tree(src));
                var r3 = app.Backup(set.Id);
                Assert.True(r3.Result == "BS_STOP_SUCCESS", r3.Result + "\n" + string.Join("\n", r3.LogLines));

                var points = PilotEnv.Points(app, set.Id);
                Assert.Equal(3, points.Count);
                for (int i = 0; i < 3; i++)
                    Assert.Equal(trees[i].OrderBy(k => k.Key, StringComparer.Ordinal).ToList(),
                        PilotEnv.Restored(env, app, set.Id, src, points[i], "p" + i).OrderBy(k => k.Key, StringComparer.Ordinal).ToList());

                Thread.Sleep(1100);
                var r4 = app.Backup(set.Id);                                                     // nothing changed: nothing sent
                Assert.True(r4.New == 0 && r4.Updated == 0 && r4.Deleted == 0 && r4.PermOnly == 0, "an unchanged tree sent new " + r4.New + " upd " + r4.Updated + " del " + r4.Deleted + " perm " + r4.PermOnly);
            }
        }

        // ------------------------------------------------------------------ BK-02: a chain that shrinks, changes rights, grows

        /// <summary>
        /// BK-02 (challenge of the delta-chain tests, which only inserted 2 KB in the middle and restored each point only while
        /// it was the newest): one large file through the REAL server, incremental, chain limit 4 — a delta, a change of its
        /// rights only, a shrink below the delta threshold (its chunk list is dropped), a growth back over it, a delta again.
        /// At the end every one of the 6 points restores to the bytes it had (SHA-256), and the small changes stay small.
        /// </summary>
        [Fact]
        public void BK02_AChainThatChangesRightsShrinksBelowTheThresholdAndGrowsAgain_EveryPointRestoresAtTheEnd()
        {
            using (var env = new Env())
            {
                env.CreateUser("chk02", Pass);
                var app = env.Agent("chk02", Pass);
                var src = env.Dir("src"); var path = Path.Combine(src, "db.mdf");
                const int Size = 8 * 1024 * 1024;
                var set = app.CreateSet(app.Interactive(Pass, null), Pass,
                    new BackupSetInfo { Name = "Chain", Sources = { src }, MinDeltaFileSize = 1024 * 1024, MaxDeltaNo = 4, MaxDeltaRatio = 50, Compression = "NONE", Vss = false, DeltaType = "I" });
                var versions = new List<byte[]>();
                Func<byte[], int, int, byte[]> insert = (cur, at, seed) => cur.Take(at).Concat(PilotRig.Rnd(2048, seed)).Concat(cur.Skip(at + 512)).ToArray();
                try
                {
                    versions.Add(PilotRig.Rnd(Size, 71)); Write(path, versions[0], -10);
                    var r = app.Backup(set.Id); Assert.Equal("BS_STOP_SUCCESS", r.Result);

                    Thread.Sleep(1100);                                                              // 2: a delta
                    versions.Add(insert(versions[0], 3 * 1024 * 1024, 72)); Write(path, versions[1], 1);
                    r = app.Backup(set.Id); Assert.True(r.Result == "BS_STOP_SUCCESS", string.Join("\n", r.LogLines));
                    Assert.True(r.Updated == 1 && r.BytesSent < Size / 4, "a 2 KB change sent " + r.BytesSent + " bytes");

                    Thread.Sleep(1100);                                                              // 3: its rights only
                    File.SetAttributes(path, FileAttributes.ReadOnly); versions.Add(versions[1]);
                    r = app.Backup(set.Id); Assert.True(r.Result == "BS_STOP_SUCCESS", string.Join("\n", r.LogLines));
                    Assert.True(r.PermOnly == 1 && r.BytesSent < Size / 4, "a change of rights only: perm " + r.PermOnly + ", " + r.BytesSent + " bytes");
                    File.SetAttributes(path, FileAttributes.Normal);

                    Thread.Sleep(1100);                                                              // 4: below the threshold
                    versions.Add(versions[1].Take(500 * 1024).ToArray()); Write(path, versions[3], 2);
                    r = app.Backup(set.Id); Assert.True(r.Result == "BS_STOP_SUCCESS", string.Join("\n", r.LogLines));

                    Thread.Sleep(1100);                                                              // 5: over it again
                    versions.Add(versions[3].Concat(PilotRig.Rnd(Size, 73)).ToArray()); Write(path, versions[4], 3);
                    r = app.Backup(set.Id); Assert.True(r.Result == "BS_STOP_SUCCESS", string.Join("\n", r.LogLines));

                    Thread.Sleep(1100);                                                              // 6: a delta on the new full copy
                    versions.Add(insert(versions[4], 6 * 1024 * 1024, 74)); Write(path, versions[5], 4);
                    r = app.Backup(set.Id); Assert.True(r.Result == "BS_STOP_SUCCESS", string.Join("\n", r.LogLines));
                    Assert.True(r.BytesSent < Size / 4, "a 2 KB change after the regrowth sent " + r.BytesSent + " bytes");

                    var points = PilotEnv.Points(app, set.Id);
                    Assert.Equal(6, points.Count);
                    for (int i = 0; i < 6; i++)
                        Assert.True(PilotRig.Sha(versions[i]) == PilotEnv.Restored(env, app, set.Id, src, points[i], "p" + i)["db.mdf"], "point " + (i + 1) + " of 6 does not restore its version");
                }
                finally { try { File.SetAttributes(path, FileAttributes.Normal); } catch (IOException) { } }
            }
        }

        // ------------------------------------------------------------------ BK-05 / AG-07: a file that cannot be opened, over each TLS

        /// <summary>A loopback TLS 1.2 front for the stand-in server on 127.0.0.2 (no other test class pins a certificate
        /// for that host; the pin is kept per host for the whole process). The stand-in answers only its own host:port, so
        /// "127.0.0.2:front" is rewritten to "localhost:server" (the same length) on the way up.</summary>
        internal sealed class TlsFront : IDisposable
        {
            static readonly IPAddress Host = IPAddress.Parse("127.0.0.2");
            readonly TcpListener l = new TcpListener(Host, 0);
            readonly X509Certificate2 cert;
            readonly int backend;
            public int Port { get { return ((IPEndPoint)l.LocalEndpoint).Port; } }
            public string Url { get { return "https://127.0.0.2:" + Port + "/"; } }
            public string Fingerprint { get { return Bytes.Hex(Bytes.Sha256(cert.RawData)); } }
            public volatile string LastError;

            public static TlsFront For(AgentRig rig)
            {
                var port = new Uri(rig.Server.Url).Port;
                TlsFront f;
                try { f = new TlsFront(port); }
                catch (SocketException e) { throw NotTested.Because("this machine cannot listen on 127.0.0.2: " + e.Message); }
                if (f.Port.ToString().Length != port.ToString().Length) { f.Dispose(); throw NotTested.Because("the free ports of this run differ in length (the Host rewrite keeps the length)"); }
                return f;
            }

            TlsFront(int backendPort)
            {
                backend = backendPort; l.Start();
                using (var rsa = RSA.Create(2048))
                {
                    var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(Host); req.CertificateExtensions.Add(san.Build());
                    using (var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30)))
                        cert = new X509Certificate2(c.Export(X509ContentType.Pfx));
                }
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
                        try { ssl.AuthenticateAsServer(cert, false, SslProtocols.Tls12, false); } catch (Exception e) { LastError = e.ToString(); return; }
                        using (var b = new TcpClient("localhost", backend))
                        {
                            var bs = b.GetStream();
                            var up = new Thread(() => { try { Proxy.CopyRewriting(ssl, bs, "127.0.0.2:" + Port, "localhost:" + backend); b.Client.Shutdown(SocketShutdown.Send); } catch (Exception) { } }) { IsBackground = true };
                            up.Start();
                            try { bs.CopyTo(ssl); } catch (Exception) { }
                            up.Join(2000);
                        }
                    }
                }
                catch (Exception) { }
            }

            public void Dispose() { l.Stop(); }
        }

        /// <summary>
        /// A source file that the listing shows but that cannot be opened for reading: on Windows another program's
        /// exclusive lock; on Linux (where locks do not stop a reader) a Unix socket — open() fails, the listing shows it.
        /// </summary>
        internal static IDisposable Unopenable(string path)
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                File.WriteAllBytes(path, PilotRig.Rnd(5000, 9));
                return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Bind(new UnixDomainSocketEndPoint(path));
            try { using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { } }
            catch (IOException) { return s; }
            catch (UnauthorizedAccessException) { }
            s.Dispose();
            throw NotTested.Because("on this machine a Unix socket in the source does not fail to open with an I/O error");
        }

        // ------------------------------------------------------------------ AG-05: faults at other moments, through the real server

        /// <summary>
        /// A plain TCP proxy in front of the real server that breaks ONE exchange: it cuts the line in the middle of the
        /// request that matches (mid-object), or lets the request through and throws its answer away (the answer lost).
        /// </summary>
        internal sealed class Proxy : IDisposable
        {
            readonly TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            readonly int backend;
            string trigger; bool cut; int cutAfter; int armed;
            public int Fired;
            public int Port { get { return ((IPEndPoint)l.LocalEndpoint).Port; } }
            public string Url { get { return "http://localhost:" + Port + "/"; } }

            public static Proxy For(Env env)
            {
                var port = new Uri(env.Url).Port; var p = new Proxy(port);
                if (p.Port.ToString().Length != port.ToString().Length) { p.Dispose(); throw NotTested.Because("the free ports of this run differ in length (the Host rewrite keeps the length)"); }
                return p;
            }

            Proxy(int backendPort)
            {
                backend = backendPort; l.Start();
                new Thread(() =>
                {
                    while (true)
                    {
                        TcpClient c; try { c = l.AcceptTcpClient(); } catch (Exception) { return; }
                        new Thread(() => Serve(c)) { IsBackground = true }.Start();
                    }
                }) { IsBackground = true }.Start();
            }

            /// <summary>The next request whose bytes contain <paramref name="text"/>: cut after that many more bytes up (cut), or its answer dropped.</summary>
            public void Arm(string text, bool cutUpload, int afterBytes = 0) { trigger = text; cut = cutUpload; cutAfter = afterBytes; Interlocked.Exchange(ref armed, 1); }

            void Serve(TcpClient c)
            {
                using (c)
                using (var b = new TcpClient("localhost", backend))
                {
                    var cs = c.GetStream(); var bs = b.GetStream();
                    var dropAnswer = 0;
                    var up = new Thread(() =>
                    {
                        try
                        {
                            var seen = new StringBuilder(); long countdown = -1; var buf = new byte[16384]; int n;
                            var a = Encoding.ASCII.GetBytes("localhost:" + Port); var z = Encoding.ASCII.GetBytes("localhost:" + backend);
                            while ((n = cs.Read(buf, 0, buf.Length)) > 0)
                            {
                                Replace(buf, n, a, z);
                                if (countdown < 0 && Volatile.Read(ref armed) == 1)
                                {
                                    seen.Append(Encoding.Latin1.GetString(buf, 0, n));
                                    if (seen.Length > 1 << 20) seen.Remove(0, seen.Length - 4096);
                                    var at = seen.ToString().IndexOf(trigger, StringComparison.Ordinal);
                                    if (at >= 0 && Interlocked.CompareExchange(ref armed, 0, 1) == 1)
                                    {
                                        Interlocked.Increment(ref Fired);
                                        if (cut) countdown = cutAfter; else Volatile.Write(ref dropAnswer, 1);
                                    }
                                }
                                if (countdown >= 0)
                                {
                                    var pass = (int)Math.Min(n, countdown);
                                    bs.Write(buf, 0, pass); bs.Flush(); countdown -= pass;
                                    if (countdown == 0) { c.Client.Close(); b.Client.Close(); return; }   // the line breaks mid-request
                                    continue;
                                }
                                bs.Write(buf, 0, n); bs.Flush();
                            }
                            b.Client.Shutdown(SocketShutdown.Send);
                        }
                        catch (Exception) { }
                    }) { IsBackground = true };
                    up.Start();
                    try
                    {
                        var buf = new byte[16384]; int n;
                        while ((n = bs.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (Volatile.Read(ref dropAnswer) == 1) { c.Client.Close(); break; }   // the server answered; the answer never arrives
                            cs.Write(buf, 0, n); cs.Flush();
                        }
                    }
                    catch (Exception) { }
                    up.Join(2000);
                }
            }

            static void Replace(byte[] p, int n, byte[] a, byte[] z)
            {
                for (int i = 0; i + a.Length <= n; i++)
                {
                    int k = 0; while (k < a.Length && p[i + k] == a[k]) k++;
                    if (k == a.Length) Buffer.BlockCopy(z, 0, p, i, z.Length);
                }
            }

            /// <summary>Copies up, "from" → "to" in the bytes (equal length), also across two reads.</summary>
            public static void CopyRewriting(Stream from, Stream to, string fromText, string toText)
            {
                var a = Encoding.ASCII.GetBytes(fromText); var z = Encoding.ASCII.GetBytes(toText);
                if (a.Length != z.Length) throw new InvalidOperationException("test setup: the rewrite changes the length");
                var pending = new List<byte>(); var buf = new byte[16384]; int n;
                while ((n = from.Read(buf, 0, buf.Length)) > 0)
                {
                    for (int i = 0; i < n; i++) pending.Add(buf[i]);
                    var p = pending.ToArray();
                    Replace(p, p.Length, a, z);
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

        /// <summary>
        /// AG-05 (challenge of the network component tests, which lost only the answer of one OBJECT on the stand-in server
        /// and never broke a commit or a delete): through the REAL server and a proxy that breaks one exchange of run 2 —
        /// the line cut in the middle of an object's upload, the answer of the deletion list lost, the answer of the commit
        /// lost (the server committed; the computer does not know). Contract: the run resumes or ends as failed with a clear
        /// reason; nothing counted twice; the next run completes and restores identical. Here the agent's own retries must
        /// carry the run through: one point per run (not two, not none), every point restores its own tree (SHA-256), and the
        /// run after it sends nothing (its index moved exactly once).
        /// </summary>
        [Theory]
        [InlineData("object-cut")]
        [InlineData("delete-answer-lost")]
        [InlineData("commit-answer-lost")]
        public void AG05_OneExchangeBrokenAtAnotherMoment_TheRunGoesThrough_OnePointPerRun_EveryPointRestores(string fault)
        {
            using (var env = new Env())
            using (var proxy = Proxy.For(env))
            {
                env.CreateUser("chk05", Pass);
                var app = new AgentApp(env.Dir("agent"));
                app.Register(proxy.Url, "chk05", Pass, null, "PC-net");
                var src = env.Dir("src");
                File.WriteAllText(Path.Combine(src, "a.txt"), "alpha 1"); File.WriteAllText(Path.Combine(src, "c.txt"), "gamma, deleted in run 2");
                var set = app.CreateSet(app.Interactive(Pass, null), Pass, new BackupSetInfo { Name = "Net", Sources = { src }, Vss = false, Compression = "NONE" });
                var t1 = AgentRig.Tree(src);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(set.Id).Result);

                Thread.Sleep(1100);
                Write(Path.Combine(src, "a.txt"), Encoding.UTF8.GetBytes("alpha 2, changed"), 1);
                Write(Path.Combine(src, "big.bin"), PilotRig.Rnd(1536 * 1024, 75), 1);
                File.Delete(Path.Combine(src, "c.txt"));
                var t2 = AgentRig.Tree(src);
                if (fault == "object-cut") proxy.Arm("&orig=1572864&", true, 256 * 1024);                     // big.bin's upload, cut 256 KB in
                else if (fault == "delete-answer-lost") proxy.Arm("/delete HTTP", false);
                else proxy.Arm("/commit HTTP", false);
                var r2 = app.Backup(set.Id);
                Assert.Equal(1, proxy.Fired);
                if (fault == "object-cut") Assert.Contains(r2.LogLines, l => l.Contains("The connection broke while sending") && l.Contains("big.bin"));   // the cut hit the object
                Assert.True(r2.Result == "BS_STOP_SUCCESS" || r2.Result == "BS_STOP_SUCCESS_WITH_WARNING", fault + ": " + r2.Result + "\n" + string.Join("\n", r2.LogLines));
                Assert.True(r2.New == 1 && r2.Updated == 1 && r2.Deleted == 1, fault + ": new " + r2.New + " upd " + r2.Updated + " del " + r2.Deleted);

                var points = PilotEnv.Points(app, set.Id);
                Assert.Equal(2, points.Count);
                Assert.Equal(t1, PilotEnv.Restored(env, app, set.Id, src, points[0], "p1"));
                Assert.Equal(t2, PilotEnv.Restored(env, app, set.Id, src, points[1], "p2"));

                Thread.Sleep(1100);
                var r3 = app.Backup(set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r3.Result);
                Assert.True(r3.New == 0 && r3.Updated == 0 && r3.Deleted == 0, fault + ": the run after it sent again new " + r3.New + " upd " + r3.Updated + " del " + r3.Deleted);
            }
        }
    }
}
