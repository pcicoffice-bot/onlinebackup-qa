using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Xml.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Pilot, restore - component layer (no backup server: the agent's own code against a stand-in that answers the few
    /// calls it makes, with real backup objects made by the agent's own writer).
    ///
    /// RS-06 (tests/QA/specs.py): "a set with the restore test on" -> "sample restored and compared; no source to compare =
    ///   NOT CHECKED, never FAILED". The owner's reading: restored in isolation (a temporary folder, never over the source)
    ///   and compared with the SOURCE byte by byte. Oracle: SHA-256 of the source taken before, the exact counts the stand-in
    ///   server receives, the agent's temporary folder listed after.
    /// RS-05: "a new computer, the account and the encryption password" -> "the keys come back, the local index is rebuilt,
    ///   every file restores identical". Oracle: SHA-256 of the files the old computer backed up; the index entries the
    ///   server's file list implies.
    /// </summary>
    public partial class PilotRestoreComponentTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "obpilotrs-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public PilotRestoreComponentTests() { Directory.CreateDirectory(root); }
        public void Dispose() { try { Directory.Delete(root, true); } catch (Exception) { } }

        static string Sha(byte[] b) { using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(b)); }
        static string ShaFile(string f) { return Sha(File.ReadAllBytes(f)); }
        static byte[] Rnd(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
        static long Ms(string f) { return RunId.UnixMs(File.GetLastWriteTimeUtc(f)); }

        /// <summary>One file as the agent uploads it (chunks -> one object with the recipe in its header), and its file-list entry.</summary>
        static byte[] MakeObject(KeySet key, string path, byte[] data, long mtime)
        {
            var ms = new MemoryStream(); var w = new BackupObject.Writer(ms, key);
            var recipe = new List<KeyValuePair<string, int>>(); var seen = new HashSet<string>();
            foreach (var c in Chunker.ForFileSize(data.Length).Split(new MemoryStream(data)))
            {
                var id = BackupObject.ChunkId(key, c); recipe.Add(new KeyValuePair<string, int>(id, c.Length));
                if (seen.Add(id)) w.AddChunk(id, c);
            }
            var header = new Msg().Set("path", path).Set("size", data.Length).Set("mtime", mtime).Set("attrs", "").Set("kind", "F").Set("seq", 0);
            foreach (var r in recipe) header.Add("recipe", new Msg().Set("h", r.Key).Set("n", r.Value));
            w.Finish(header);
            return ms.ToArray();
        }

        /// <summary>
        /// A stand-in for the backup server: the profile, the file list of the latest point, the objects, and what the agent
        /// reports. It answers exactly the calls the agent makes (the same paths as src/Server/Api.cs).
        /// </summary>
        sealed class StubServer : IDisposable
        {
            readonly HttpListener http = new HttpListener();
            readonly Thread loop;
            public readonly string Url;
            public string ProfileXml;
            public Msg Files = new Msg();
            public readonly Dictionary<string, byte[]> Objects = new Dictionary<string, byte[]>();
            public readonly List<Msg> RestoreTests = new List<Msg>();
            public readonly List<string> ObjectRequests = new List<string>();
            public readonly List<string> Writes = new List<string>();   // every non-GET call other than the restore-test report
            public volatile bool RefuseReport;                          // the restore-test report is answered 503 (the server busy for a moment)

            public StubServer()
            {
                var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
                Url = "http://localhost:" + port + "/";
                http.Prefixes.Add(Url); http.Start();
                loop = new Thread(Serve) { IsBackground = true }; loop.Start();
            }

            void Serve()
            {
                while (http.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = http.GetContext(); } catch (Exception) { return; }
                    try { Handle(ctx); }
                    catch (Exception e) { try { Reply(ctx, 500, new Msg().Set("error", "STUB").Set("message", e.Message).ToBytes()); } catch (Exception) { } }
                }
            }

            static void Reply(HttpListenerContext ctx, int status, byte[] body)
            {
                ctx.Response.StatusCode = status; ctx.Response.ContentLength64 = body.Length;
                ctx.Response.OutputStream.Write(body, 0, body.Length); ctx.Response.OutputStream.Close();
            }

            void Handle(HttpListenerContext ctx)
            {
                var path = ctx.Request.Url.AbsolutePath; var method = ctx.Request.HttpMethod;
                var seg = path.Trim('/').Split('/');
                if (method == "GET" && path == "/api/profile") { Reply(ctx, 200, new Msg().Set("profile", ProfileXml).ToBytes()); return; }
                if (seg.Length == 4 && seg[0] == "api" && seg[1] == "sets")
                {
                    if (method == "GET" && seg[3] == "files") { Reply(ctx, 200, Files.ToBytes()); return; }
                    if (method == "GET" && seg[3] == "object")
                    {
                        var loc = ctx.Request.QueryString["loc"];
                        lock (ObjectRequests) ObjectRequests.Add(loc + (ctx.Request.QueryString["test"] == "1" ? "&test=1" : ""));
                        byte[] b;
                        if (loc == null || !Objects.TryGetValue(loc, out b)) { Reply(ctx, 404, new Msg().Set("error", "NO_OBJECT").Set("message", "The object was not found.").ToBytes()); return; }
                        Reply(ctx, 200, b); return;
                    }
                    if (method == "POST" && seg[3] == "restoretest" && RefuseReport) { Reply(ctx, 503, new Msg().Set("error", "BUSY").Set("message", "The server is busy.").ToBytes()); return; }
                    if (method == "POST" && seg[3] == "restoretest") { var m = Msg.Read(ctx.Request.InputStream); lock (RestoreTests) RestoreTests.Add(m); Reply(ctx, 200, new Msg().Set("ok", 1).ToBytes()); return; }
                }
                lock (Writes) Writes.Add(method + " " + path);
                Reply(ctx, 404, new Msg().Set("error", "NOT_HERE").Set("message", "the stand-in does not serve " + method + " " + path).ToBytes());
            }

            public void Dispose() { try { http.Stop(); http.Close(); } catch (Exception) { } }
        }

        /// <summary>An agent whose set (native engine, FILE) backed up `files` (real paths on this disk) with `key`.</summary>
        sealed class Fixture
        {
            public AgentApp App; public BackupSetInfo Set; public KeySet Key; public StubServer Server;
            public Dictionary<string, string> Loc = new Dictionary<string, string>();    // source path -> object location
            public Dictionary<string, Msg> Entry = new Dictionary<string, Msg>();
        }

        Fixture Make(StubServer server, IEnumerable<string> files, string keyType = "DEFAULT", string password = null)
        {
            var fx = new Fixture { Server = server };
            var salt = Bytes.Random(16);
            fx.Key = keyType == "PASSWORD" ? KeySet.Derive(password, salt) : KeySet.Random();
            fx.Set = new BackupSetInfo { Id = "1700000000123", Name = "Pilot", Sources = { Path.Combine(root, "src") }, KeyType = keyType, KeySalt = Convert.ToBase64String(salt), KeyCheck = fx.Key.CheckValue() };
            server.ProfileXml = new XDocument(new XElement("ROOT", fx.Set.ToXml())).ToString(SaveOptions.DisableFormatting);
            int n = 0;
            foreach (var f in files)
            {
                var data = File.ReadAllBytes(f); var mtime = Ms(f);
                var obj = MakeObject(fx.Key, f, data, mtime); var loc = "Current/obj" + (n++);
                server.Objects[loc] = obj; fx.Loc[f] = loc;
                var e = new Msg().Set("rel", "R" + n).Set("enc", NameCipher.EncryptPath(fx.Key, f)).Set("orig", data.Length).Set("mtime", mtime);
                e.Add("objects", new Msg().Set("loc", loc).Set("seq", 0).Set("kind", "F").Set("size", obj.Length).Set("sha", Sha(obj)));
                server.Files.Add("files", e); fx.Entry[f] = e;
            }
            fx.App = new AgentApp(Path.Combine(root, "agent-" + Guid.NewGuid().ToString("N").Substring(0, 6)));
            fx.App.Home.SaveRegistration(server.Url, "pilot", "PC-PILOT", "dev.pilotdevice.sig");
            fx.App.Home.SaveKey(fx.Set.Id, fx.Key);
            return fx;
        }

        List<string> Sources(int count, int size = 40000)
        {
            var src = Path.Combine(root, "src"); Directory.CreateDirectory(src);
            var list = new List<string>();
            for (int i = 0; i < count; i++)
            {
                var f = Path.Combine(src, "doc" + i + ".bin"); File.WriteAllBytes(f, Rnd(size + i, 100 + i));
                File.SetLastWriteTimeUtc(f, new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc).AddMinutes(i).AddMilliseconds(137 + i));
                list.Add(f);
            }
            return list;
        }

        static Dictionary<string, string> Snapshot(string dir) { return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => f, f => ShaFile(f) + "|" + Ms(f)); }

        /// <summary>What the agent leaves in its own temporary folder (the restore test's folder and any downloaded object).</summary>
        static List<string> TempLeft(AgentApp app)
        {
            var t = Path.Combine(app.Home.Dir, "temp");
            return Directory.Exists(t) ? Directory.GetFileSystemEntries(t, "*", SearchOption.AllDirectories).ToList() : new List<string>();
        }

        // ------------------------------------------------------------------ RS-06

        [Fact]
        public void RS06_UnchangedSources_RestoreInIsolation_AllEqual_ReportedToTheServer_TempFolderGone()
        {
            using (var server = new StubServer())
            {
                var files = Sources(5);
                var fx = Make(server, files);
                var before = Snapshot(Path.Combine(root, "src"));

                var t = fx.App.RestoreTest(fx.Set.Id, 5, seed: 7);

                Assert.Equal(5, t.Int("candidates")); Assert.Equal(5, t.Int("checked")); Assert.Equal(5, t.Int("ok")); Assert.Equal(0, t.Int("failed"));
                // every sampled file was really fetched as a restore-test download (test=1), each exactly once
                Assert.Equal(files.Select(f => fx.Loc[f] + "&test=1").OrderBy(x => x), server.ObjectRequests.OrderBy(x => x));
                // the result reached the server, with the same counts and a line per file saying "identical"
                var got = Assert.Single(server.RestoreTests);
                Assert.Equal(5, got.Int("checked")); Assert.Equal(5, got.Int("ok")); Assert.Equal(0, got.Int("failed"));
                Assert.Equal(5, got.List("log").Count(l => l["l"].Contains("restore test: identical")));
                // isolation: the sources are untouched (content and time), nothing was added beside them; the temporary folder is gone
                Assert.Equal(before, Snapshot(Path.Combine(root, "src")));
                Assert.Empty(TempLeft(fx.App));
                Assert.Empty(server.Writes);
            }
        }

        [Fact]
        public void RS06_AnObjectDamagedInStorage_IsAFailedFile_NotPassed_TheOthersStillChecked_TempFolderGone()
        {
            using (var server = new StubServer())
            {
                var files = Sources(4);
                var fx = Make(server, files);
                var before = Snapshot(Path.Combine(root, "src"));
                // (a) one byte of doc1's object flipped on the storage disk: the checksum the server recorded no longer matches
                var l1 = fx.Loc[files[1]]; var b1 = (byte[])server.Objects[l1].Clone(); b1[b1.Length / 2] ^= 0x5A; server.Objects[l1] = b1;
                // (b) doc2's object damaged and its checksum taken again (a rebuilt index): only the authenticated chunks can tell
                var l2 = fx.Loc[files[2]]; var b2 = (byte[])server.Objects[l2].Clone(); b2[b2.Length / 3] ^= 0xFF; server.Objects[l2] = b2;
                fx.Entry[files[2]].List("objects")[0].Set("sha", Sha(b2));

                var t = fx.App.RestoreTest(fx.Set.Id, 4, seed: 1);

                Assert.Equal(4, t.Int("checked")); Assert.Equal(2, t.Int("ok")); Assert.Equal(2, t.Int("failed"));
                var got = Assert.Single(server.RestoreTests);
                Assert.Equal(2, got.Int("failed")); Assert.Equal(2, got.Int("ok")); Assert.Equal(4, got.Int("checked"));
                var errs = got.List("log").Select(l => l["l"]).Where(l => l.Contains("restore test failed") || l.Contains("DIFFERENT")).ToList();
                Assert.Equal(2, errs.Count);
                Assert.Contains(errs, l => l.Contains("doc1.bin")); Assert.Contains(errs, l => l.Contains("doc2.bin"));
                Assert.Equal(before, Snapshot(Path.Combine(root, "src")));                     // the source is never written
                Assert.Empty(TempLeft(fx.App));                                                // no half file, no object, no folder left
            }
        }

        [Fact]
        public void RS06_ARestoreThatSucceedsButGivesOtherBytes_IsReportedDifferent_TheComparisonIsWithTheSource()
        {
            using (var server = new StubServer())
            {
                // two files of the same size; the storage holds doc1's (valid, authenticated) object where doc0's should be:
                // the restore itself succeeds, only a comparison with the source can see that the bytes are not doc0's
                var files = Sources(2, 30000).ToList();
                File.WriteAllBytes(files[1], Rnd(30000, 999)); File.WriteAllBytes(files[0], Rnd(30000, 998));
                File.SetLastWriteTimeUtc(files[0], new DateTime(2026, 3, 2, 8, 0, 0, DateTimeKind.Utc)); File.SetLastWriteTimeUtc(files[1], new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc));
                var fx = Make(server, files);
                server.Objects[fx.Loc[files[0]]] = server.Objects[fx.Loc[files[1]]];
                fx.Entry[files[0]].List("objects")[0].Set("sha", Sha(server.Objects[fx.Loc[files[1]]]));

                var t = fx.App.RestoreTest(fx.Set.Id, 2, seed: 3);

                Assert.Equal(2, t.Int("checked")); Assert.Equal(1, t.Int("ok")); Assert.Equal(1, t.Int("failed"));
                var got = Assert.Single(server.RestoreTests);
                Assert.Contains(got.List("log").Select(l => l["l"]), l => l.Contains("DIFFERENT") && l.Contains("doc0.bin"));
                Assert.Empty(TempLeft(fx.App));
            }
        }

        [Fact]
        public void RS06_AChangedOrMissingSource_IsNotACandidate_NothingToCompare_IsZeroChecked_NeverFailed()
        {
            using (var server = new StubServer())
            {
                var files = Sources(4);
                var fx = Make(server, files);
                // after the backup: doc0 got new content (other size), doc1 same size but a new time, doc2 was deleted; doc3 unchanged
                File.WriteAllBytes(files[0], Rnd(123, 5));
                File.SetLastWriteTimeUtc(files[1], File.GetLastWriteTimeUtc(files[1]).AddSeconds(5));
                File.Delete(files[2]);

                var t = fx.App.RestoreTest(fx.Set.Id, 10, seed: 2);

                Assert.Equal(1, t.Int("candidates")); Assert.Equal(1, t.Int("checked")); Assert.Equal(1, t.Int("ok")); Assert.Equal(0, t.Int("failed"));
                Assert.Equal(new[] { fx.Loc[files[3]] + "&test=1" }, server.ObjectRequests);   // only the unchanged file was fetched
                var got = Assert.Single(server.RestoreTests);
                Assert.DoesNotContain(got.List("log").Select(l => l["l"]), l => l.Contains("doc0.bin") || l.Contains("doc1.bin") || l.Contains("doc2.bin"));

                // the last unchanged file changes too: nothing to compare -> 0 checked, 0 failed (the server shows NOT CHECKED)
                File.SetLastWriteTimeUtc(files[3], File.GetLastWriteTimeUtc(files[3]).AddSeconds(5));
                var t2 = fx.App.RestoreTest(fx.Set.Id, 10, seed: 2);
                Assert.Equal(0, t2.Int("candidates")); Assert.Equal(0, t2.Int("checked")); Assert.Equal(0, t2.Int("failed"));
                Assert.Equal(2, server.RestoreTests.Count); Assert.Equal(0, server.RestoreTests[1].Int("failed"));
                Assert.Empty(TempLeft(fx.App));
            }
        }

        [Fact]
        public void RS06_TheSampleSize_IsRespected_EachPickedFileOnce()
        {
            using (var server = new StubServer())
            {
                var files = Sources(6, 5000);
                var fx = Make(server, files);
                var t = fx.App.RestoreTest(fx.Set.Id, 2, seed: 11);
                Assert.Equal(6, t.Int("candidates")); Assert.Equal(2, t.Int("checked")); Assert.Equal(2, t.Int("ok"));
                Assert.Equal(2, server.ObjectRequests.Distinct().Count()); Assert.Equal(2, server.ObjectRequests.Count);
                Assert.Empty(TempLeft(fx.App));
            }
        }

        /// <summary>The agent's local record of a backup that ended well (what BackupRun leaves after BS_STOP_SUCCESS).</summary>
        static void BackedUpWell(AgentApp app, BackupSetInfo set, string result = "BS_STOP_SUCCESS")
        {
            File.WriteAllText(Path.Combine(app.Home.SetDir(set.Id), "last-attempt.txt"), "2026-03-01-08-00-00\t" + result);
            new LocalState(app.Home.SetDir(set.Id)) { LastSuccess = "2026-03-01-08-00-00" }.Save();
        }

        [Fact]
        public void RS06_Due_OnlyAfterASuccessfulBackup_ThenOncePer30Days()
        {
            using (var server = new StubServer())
            {
                var fx = Make(server, Sources(1));
                var now = new DateTime(2026, 4, 1, 3, 0, 0, DateTimeKind.Utc);
                Assert.False(fx.App.RestoreTestDue(fx.Set, now), "no backup yet");
                BackedUpWell(fx.App, fx.Set, "BS_STOP_BY_USER");
                Assert.False(fx.App.RestoreTestDue(fx.Set, now), "the last run was stopped");
                BackedUpWell(fx.App, fx.Set);
                Assert.True(fx.App.RestoreTestDue(fx.Set, now));
                var t = fx.App.RestoreTest(fx.Set.Id);
                Assert.Equal(1, t.Int("ok")); Assert.Single(server.RestoreTests);
                Assert.False(fx.App.RestoreTestDue(fx.Set, now.AddDays(29)));
                Assert.True(fx.App.RestoreTestDue(fx.Set, now.AddDays(30)));
            }
        }

        // ------------------------------------------------------------------ RS-05

        /// <summary>A "new computer": an agent home that has never seen the set's key, registered to the same account.</summary>
        AgentApp NewComputer(StubServer server)
        {
            var app = new AgentApp(Path.Combine(root, "newpc-" + Guid.NewGuid().ToString("N").Substring(0, 6)));
            app.Home.SaveRegistration(server.Url, "pilot", "PC-NEW", "dev.newdevice.sig");
            return app;
        }

        [Fact]
        public void RS05_NewComputer_PasswordKeyComesBack_EveryFileRestoresIdentical_AndTheKeyIsKept()
        {
            using (var server = new StubServer())
            {
                var files = Sources(3, 70000);
                var want = files.ToDictionary(f => f, f => ShaFile(f) + "|" + Ms(f));
                var fx = Make(server, files, "PASSWORD", "Customer-Pass-1");
                var fresh = NewComputer(server);
                Assert.Null(fresh.Home.LoadKey(fx.Set.Id));                                   // control: the new computer has no key

                var k = fresh.Key(fx.Set, "Customer-Pass-1");
                Assert.Equal(fx.Key.ToRaw(), k.ToRaw());
                var target = Path.Combine(root, "restored");
                var r = new Restore(new ServerSource(fresh.DeviceClient(), fx.Set.Id, true), k, Path.Combine(fresh.Home.Dir, "temp"));
                r.Run(null, target, null, false);
                Assert.Equal("RESTORE_STOP_SUCCESS", r.Result); Assert.Equal(3, r.Restored);
                foreach (var f in files)
                {
                    var d = Path.Combine(target, f.Replace(":", "").TrimStart('\\', '/'));
                    Assert.Equal(want[f], ShaFile(d) + "|" + Ms(d));
                }
                // the proven key stays on the new computer: the next start needs no password
                Assert.Equal(fx.Key.ToRaw(), new AgentApp(fresh.Home.Dir).Key(fx.Set).ToRaw());
            }
        }

        [Fact]
        public void RS05_NewComputer_WrongPassword_IsRefused_NoKeyIsKept_ARandomKeyNeedsRecovery()
        {
            using (var server = new StubServer())
            {
                var files = Sources(1);
                var fx = Make(server, files, "PASSWORD", "Customer-Pass-1");
                var fresh = NewComputer(server);
                Assert.Equal("WRONG_KEY", Assert.Throws<AgentException>(() => fresh.Key(fx.Set, "customer-pass-1")).Code);
                Assert.Equal("NO_KEY", Assert.Throws<AgentException>(() => fresh.Key(fx.Set)).Code);
                Assert.Null(fresh.Home.LoadKey(fx.Set.Id));                                   // a wrong key is never saved
                Assert.Equal("WRONG_KEY", Assert.Throws<AgentException>(() => fresh.Key(fx.Set, null, KeySet.Random().ToRaw())).Code);
                Assert.Null(fresh.Home.LoadKey(fx.Set.Id));

                // a DEFAULT (random) key cannot come from any password: only recovery gives it
                using (var server2 = new StubServer())
                {
                    var fx2 = Make(server2, files, "DEFAULT");
                    var pc = NewComputer(server2);
                    Assert.Equal("NO_KEY", Assert.Throws<AgentException>(() => pc.Key(fx2.Set, "Customer-Pass-1")).Code);
                    Assert.Null(pc.Home.LoadKey(fx2.Set.Id));
                }
            }
        }

        [Fact]
        public void RS05_NewComputer_RecoveredKey_Restores_AndTheLocalIndexIsRebuiltFromTheServerList()
        {
            using (var server = new StubServer())
            {
                var files = Sources(4, 20000);
                var fx = Make(server, files, "DEFAULT");
                // the server's list also has a version found damaged (quarantined)
                var damaged = new Msg().Set("rel", "RD").Set("enc", NameCipher.EncryptPath(fx.Key, Path.Combine(root, "src", "gone.bin"))).Set("orig", 9).Set("mtime", 1).Set("damaged", 1);
                damaged.Add("objects", new Msg().Set("loc", "Current/none").Set("seq", 0).Set("size", 1).Set("sha", "00"));
                server.Files.Add("files", damaged);

                var fresh = NewComputer(server);
                var k = fresh.Key(fx.Set, null, fx.Key.ToRaw());                             // the key the provider recovered
                var target = Path.Combine(root, "rec");
                var r = new Restore(new ServerSource(fresh.DeviceClient(), fx.Set.Id, true), k, Path.Combine(fresh.Home.Dir, "temp"));
                r.Run(null, target, p => !p.EndsWith("gone.bin"), false);
                Assert.Equal(4, r.Restored); Assert.Equal(0, r.Failed);
                foreach (var f in files) Assert.Equal(ShaFile(f), ShaFile(Path.Combine(target, f.Replace(":", "").TrimStart('\\', '/'))));

                // (and, for the rebuild only, an entry this key cannot read - a name damaged on the server)
                server.Files.Add("files", new Msg().Set("rel", "RX").Set("enc", NameCipher.EncryptPath(KeySet.Random(), "/x/y.bin")).Set("orig", 3).Set("mtime", 1));
                // the lost local index, rebuilt from the server: exactly the 4 good files with their size and time; the damaged one
                // is left out (so it is sent again) and the entry this key cannot read is skipped
                var state = new LocalState(fresh.Home.SetDir(fx.Set.Id)); var said = new List<string>();
                Restore.RebuildLocalState(fresh.DeviceClient(), fx.Set, k, state, said.Add);
                Assert.Equal(files.Select(f => fx.Entry[f]["rel"]).OrderBy(x => x), state.Files.Keys.OrderBy(x => x));
                foreach (var f in files)
                {
                    var e = state.Files[fx.Entry[f]["rel"]];
                    Assert.Equal(f, e.Path); Assert.Equal(new FileInfo(f).Length, e.Size); Assert.Equal(Ms(f), e.Mtime);
                }
                Assert.Contains(said, s => s.Contains("4 files"));
            }
        }
    }
}
