using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OnlineBackup.Agent;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// R1 (GPT audit 8–9): the client update is all or nothing. The Windows service control (sc.exe) is replaced by a
    /// scripted one; the files are real files in a real folder.
    /// </summary>
    [Collection("ClientDir")]
    public class UpdateInstallTests
    {
        sealed class Box : IDisposable
        {
            public string From, To; public List<string> Calls = new List<string>(); public string State = "RUNNING";
            public Func<string, string> Behave;
            public Box(params string[] names)
            {
                var root = Path.Combine(Path.GetTempPath(), "obupd-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                From = Path.Combine(root, "stage"); To = Path.Combine(root, "install"); Directory.CreateDirectory(From); Directory.CreateDirectory(To);
                foreach (var n in names) { File.WriteAllText(Path.Combine(To, n), "old " + n); File.WriteAllText(Path.Combine(From, n), "new " + n); }
                File.WriteAllText(Path.Combine(From, "version.txt"), "9.9.9");
                File.WriteAllLines(Path.Combine(From, "update-files.txt"), names);
                ClientUpdate.StopWait = TimeSpan.FromSeconds(1); ClientUpdate.StartWait = TimeSpan.FromSeconds(1);
                ClientUpdate.ScHook = a =>
                {
                    Calls.Add(a);
                    if (Behave != null) { var r = Behave(a); if (r != null) return r; }
                    if (a.StartsWith("stop")) State = "STOPPED"; else if (a.StartsWith("start")) State = "RUNNING";
                    return "STATE : " + State;
                };
            }
            public string Read(string n) { return File.ReadAllText(Path.Combine(To, n)); }
            public string Result { get { return File.ReadAllText(ClientUpdate.ResultPath(To)); } }
            public void Dispose() { ClientUpdate.ScHook = null; ClientUpdate.StopWait = ClientUpdate.StartWait = TimeSpan.FromSeconds(60); try { Directory.Delete(Path.GetDirectoryName(From), true); } catch (IOException) { } }
        }

        [Fact]
        public void Normal_EveryFileNew_Checked_ServiceRunning_ResultOk()
        {
            using (var b = new Box("OnlineBackup.Agent.exe", "OnlineBackup.Core.dll"))
            {
                Assert.Equal(0, ClientUpdate.Install(b.From, b.To));
                Assert.Equal("new OnlineBackup.Agent.exe", b.Read("OnlineBackup.Agent.exe"));
                Assert.Equal("new OnlineBackup.Core.dll", b.Read("OnlineBackup.Core.dll"));
                Assert.StartsWith("OK\t9.9.9", b.Result);
                Assert.Equal("RUNNING", b.State);
            }
        }

        /// <summary>GPT 8: the service does not stop (a long backup, a hung driver) → nothing is replaced.</summary>
        [Fact]
        public void ServiceDoesNotStop_NothingIsChanged_ResultFailed()
        {
            using (var b = new Box("OnlineBackup.Agent.exe", "OnlineBackup.Core.dll"))
            {
                b.Behave = a => a.StartsWith("stop") || a.StartsWith("query") ? "STATE : STOP_PENDING" : null;
                Assert.NotEqual(0, ClientUpdate.Install(b.From, b.To));
                Assert.Equal("old OnlineBackup.Agent.exe", b.Read("OnlineBackup.Agent.exe"));
                Assert.Equal("old OnlineBackup.Core.dll", b.Read("OnlineBackup.Core.dll"));
                Assert.StartsWith("FAILED", b.Result);
                Assert.Contains(b.Calls, c => c.StartsWith("start"));
            }
        }

        /// <summary>GPT 9: one file cannot be replaced → every file is back to the previous version (never two versions).</summary>
        [Fact]
        public void OneFileCannotBeReplaced_AllFilesBackToThePreviousVersion()
        {
            using (var b = new Box("A.dll", "B.dll", "C.dll"))
            {
                File.Delete(Path.Combine(b.To, "C.dll")); Directory.CreateDirectory(Path.Combine(b.To, "C.dll"));   // C cannot be written
                Directory.CreateDirectory(Path.Combine(b.To, "C.dll", "x"));
                Assert.NotEqual(0, ClientUpdate.Install(b.From, b.To));
                Assert.Equal("old A.dll", b.Read("A.dll"));
                Assert.Equal("old B.dll", b.Read("B.dll"));
                Assert.StartsWith("FAILED", b.Result);
                Assert.Equal("RUNNING", b.State);
            }
        }

        /// <summary>The new version does not start → the previous one is put back and started.</summary>
        [Fact]
        public void NewVersionDoesNotStart_PreviousVersionPutBackAndStarted()
        {
            using (var b = new Box("OnlineBackup.Agent.exe", "New.dll"))
            {
                File.Delete(Path.Combine(b.To, "New.dll"));   // a file that is new in this version
                int starts = 0;
                b.Behave = a => { if (a.StartsWith("start")) { starts++; b.State = starts == 1 ? "STOPPED" : "RUNNING"; return "STATE : " + b.State; } return null; };
                Assert.Equal(5, ClientUpdate.Install(b.From, b.To));
                Assert.Equal("old OnlineBackup.Agent.exe", b.Read("OnlineBackup.Agent.exe"));
                Assert.False(File.Exists(Path.Combine(b.To, "New.dll")));
                Assert.StartsWith("FAILED", b.Result);
                Assert.Equal("RUNNING", b.State);
            }
        }
    
        /// <summary>
        /// Static review, class "swallowed exception": a file that could not be put back during the rollback was only written
        /// to update.log — the result still said "every file was put back to the previous version" for an installation
        /// that was now half old, half new. The result names the files and says to install again.
        /// </summary>
        [Fact]
        public void AFileThatCannotBePutBack_IsNamed_TheResultNeverClaimsACleanRollback()
        {
            using (var b = new Box("A.dll", "B.dll", "C.dll"))
            {
                File.Delete(Path.Combine(b.To, "C.dll")); Directory.CreateDirectory(Path.Combine(b.To, "C.dll", "x"));   // C cannot be written
                ClientUpdate.BeforePutBack = name => { if (name == "A.dll") throw new IOException("A.dll is locked"); };
                try
                {
                    Assert.NotEqual(0, ClientUpdate.Install(b.From, b.To));
                    Assert.StartsWith("FAILED", b.Result);
                    Assert.DoesNotContain("every file was put back", b.Result);
                    Assert.Contains("A.dll", b.Result);
                    Assert.Contains("install the client again", b.Result);
                    Assert.Equal("old B.dll", b.Read("B.dll"));                       // the others were put back
                }
                finally { ClientUpdate.BeforePutBack = null; }
            }
        }
}
}
