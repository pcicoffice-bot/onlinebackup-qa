using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Settings changed on the server reach the computer — AgentApp.Backup (which reads the set from the server's profile
    /// before every run) and BackupRun alone, against the recording stand-in server (StubServer) that serves the profile.
    /// Component contract (tests/QA/specs.py AG-03):
    ///   purpose  settings changed on the server reach the computer
    ///   input    a set edited on the server (a source added, a folder taken out, a filter added); the server unable to give
    ///            the settings at the moment of a run; an edit that arrives in the middle of a run; the set removed
    ///   expected the computer uses the new settings in its next run: exactly the newly selected files are sent, the files
    ///            no longer selected leave the newest point, and the newest point restores to exactly the new selection
    ///            (SHA-256). A run never starts with settings the computer could not read (no run begins, nothing is sent,
    ///            the local index does not move); the next run uses the new ones. A run keeps the settings it started with
    ///            (one consistent point); the run after it uses the edit. A removed set is no longer backed up.
    /// Oracle: the requests the stand-in server received, and the files restored from the objects it stored (SHA-256 of
    /// the source files computed by the test).
    /// </summary>
    public class PilotAgentSettingsComponentTests
    {
        static AgentApp Registered(AgentRig rig)
        {
            rig.Home.SaveRegistration(rig.Server.Url, "rig", "pc-1", "device-of-the-test");
            rig.Set.KeyCheck = rig.Key.CheckValue();
            rig.Home.SaveKey(rig.Set.Id, rig.Key);
            return new AgentApp(rig.Home.Dir);
        }

        /// <summary>The server's profile with these sets (what an edit in the admin site makes the server answer).</summary>
        static void Serve(AgentRig rig, params BackupSetInfo[] sets)
        {
            var p = Profile.Create("rig", "", "", "en", null);
            foreach (var s in sets) p.Root.Add(s.ToXml());
            rig.Server.ProfileXml = p.Doc.ToString();
        }

        static BackupSetInfo Copy(BackupSetInfo s) { return BackupSetInfo.FromXml(s.ToXml()); }

        static Dictionary<string, string> RestoreNewest(AgentRig rig, string name, string source)
        {
            var target = Path.Combine(rig.Root, name);
            var r = new Restore(rig.Server.Point(rig.Key), rig.Key, Path.Combine(rig.Root, "rtmp-" + name));
            r.Run(null, target, null, false);
            Assert.Equal(0, r.Failed);
            return AgentRig.Tree(AgentRig.Under(target, source));
        }

        static Dictionary<string, string> Expect(string root, params string[] rels)
        {
            return rels.ToDictionary(r => r, r => AgentRig.Sha(Path.Combine(root, r)));
        }

        [Fact]
        public void ASetEditedOnTheServer_TheNextRunSendsExactlyTheNewSelection_AndTheNewestPointRestoresToIt()
        {
            using (var rig = new AgentRig())
            {
                var app = Registered(rig);
                var other = Path.Combine(rig.Root, "other"); Directory.CreateDirectory(other);
                File.WriteAllText(Path.Combine(other, "keep.txt"), "a folder added on the server");
                var contract = Path.Combine("Docs", "contract.txt"); var draft = Path.Combine("Docs", "draft.tmp"); var cache = Path.Combine("Cache", "x.bin");
                rig.File(contract, "the contract");
                rig.File(draft, "a draft");
                var big = new byte[200 * 1024]; new Random(3).NextBytes(big); rig.File(cache, big);
                Serve(rig, rig.Set);

                var r1 = app.Backup(rig.Set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r1.Result);
                Assert.Equal(3, rig.Server.Of("object").Count(q => q.Job == r1.Job));
                Assert.Equal(Expect(rig.Src, contract, draft, cache), RestoreNewest(rig, "restore1", rig.Src));   // control: the old selection

                // the admin edits the set: a source added, a folder taken out, a filter added
                var edited = Copy(rig.Set);
                edited.Sources.Add(other);
                edited.Deselected.Add(Path.Combine(rig.Src, "Cache"));
                edited.Filters.Add(new FilterRule { Type = "WILDCARD", Patterns = { "*.tmp" }, ApplyFile = true });
                Serve(rig, edited);

                var r2 = app.Backup(rig.Set.Id);
                Assert.True(r2.Result == "BS_STOP_SUCCESS", r2.Result + "\n" + string.Join("\n", r2.LogLines));
                Assert.Equal(1, rig.Server.Of("object").Count(q => q.Job == r2.Job));                      // keep.txt only
                Assert.Equal(1, r2.New);
                var deleted = rig.Server.Of("delete").Where(q => q.Job == r2.Job).SelectMany(q => q.Msg.List("rels")).Count();
                Assert.Equal(2, deleted);                                                                  // draft.tmp and Cache/x.bin left the newest point
                Assert.Equal(Expect(rig.Src, contract), RestoreNewest(rig, "restore2", rig.Src));
                Assert.Equal(Expect(other, "keep.txt"), RestoreNewest(rig, "restore2b", other));
            }
        }

        [Fact]
        public void TheSettingsCannotBeRead_NoRunBegins_NothingSent_TheIndexDoesNotMove_AndTheNextRunUsesTheNewSettings()
        {
            using (var rig = new AgentRig())
            {
                var app = Registered(rig);
                rig.File("a.txt", "alpha");
                Serve(rig, rig.Set);
                Assert.Equal("BS_STOP_SUCCESS", app.Backup(rig.Set.Id).Result);
                var index = File.ReadAllBytes(rig.StateFile);
                var before = rig.Server.Requests.Count(q => q.Action != "profile");

                rig.File("b.log", "excluded by the new filter");
                rig.File("c.txt", "gamma");
                var edited = Copy(rig.Set);
                edited.Filters.Add(new FilterRule { Type = "END_WITH", Patterns = { ".log" }, ApplyFile = true });
                Serve(rig, edited);

                // the server answers an error, then the answer is lost on the line
                rig.Server.Handler = q => q.Action == "profile" ? StubServer.Answer.Error(503, "HTTP", "Service Unavailable") : null;
                Assert.Throws<AgentException>(() => app.Backup(rig.Set.Id));
                rig.Server.Handler = q => q.Action == "profile" ? new StubServer.Answer { Drop = true } : null;
                Assert.Throws<AgentException>(() => app.Backup(rig.Set.Id));
                Assert.Equal(before, rig.Server.Requests.Count(q => q.Action != "profile"));             // no begin, no object, no delete
                Assert.Equal(index, File.ReadAllBytes(rig.StateFile));

                rig.Server.Handler = q => null;
                var r = app.Backup(rig.Set.Id);
                Assert.True(r.Result == "BS_STOP_SUCCESS", r.Result + "\n" + string.Join("\n", r.LogLines));
                Assert.Equal(1, r.New);                                                                    // c.txt; b.log filtered by the new settings
                Assert.Equal(1, rig.Server.Of("object").Count(q => q.Job == r.Job));
                Assert.Equal(Expect(rig.Src, "a.txt", "c.txt"), RestoreNewest(rig, "restore", rig.Src));
            }
        }

        [Fact]
        public void AnEditDuringARun_TheRunKeepsItsSettings_TheNextRunUsesTheEdit_ARemovedSetIsNoLongerBackedUp()
        {
            using (var rig = new AgentRig())
            {
                var app = Registered(rig);
                for (int i = 1; i <= 4; i++) rig.File("f" + i + ".txt", "file " + i);
                Serve(rig, rig.Set);
                var edited = Copy(rig.Set);
                edited.Filters.Add(new FilterRule { Type = "EXACT", Patterns = { "f3.txt" }, ApplyFile = true });
                int objects = 0;
                rig.Server.OnRequest = q => { if (q.Action == "object" && ++objects == 1) Serve(rig, edited); };   // the admin saves the edit while the run sends

                var r1 = app.Backup(rig.Set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r1.Result);
                Assert.Equal(4, rig.Server.Of("object").Count(q => q.Job == r1.Job));                     // one consistent point: the settings it started with
                Assert.Equal(Expect(rig.Src, "f1.txt", "f2.txt", "f3.txt", "f4.txt"), RestoreNewest(rig, "restore1", rig.Src));

                rig.Server.OnRequest = q => { };
                var r2 = app.Backup(rig.Set.Id);
                Assert.Equal("BS_STOP_SUCCESS", r2.Result);
                Assert.Equal(0, rig.Server.Of("object").Count(q => q.Job == r2.Job));
                Assert.Equal(1, r2.Deleted);                                                               // f3.txt filtered out by the edit
                Assert.Equal(Expect(rig.Src, "f1.txt", "f2.txt", "f4.txt"), RestoreNewest(rig, "restore2", rig.Src));

                // the set is removed on the server: no run of it begins
                Serve(rig);
                var begins = rig.Server.Of("begin").Count;
                var e = Assert.Throws<AgentException>(() => app.Backup(rig.Set.Id));
                Assert.Equal("NO_SET", e.Code);
                Assert.Equal(begins, rig.Server.Of("begin").Count);
            }
        }
    }
}
