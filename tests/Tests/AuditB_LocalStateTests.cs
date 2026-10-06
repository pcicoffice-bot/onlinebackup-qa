using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// QA agent B (code audit) — local state damaged (AG-06).
    /// AgentApp.ServiceLoop checks every set of the computer inside ONE try: Due() reads the set's local index
    /// (new LocalState → long.Parse per line), and a damaged state.txt throws FormatException out of the loop. The
    /// scheduler says "error: ..." and goes to sleep for a minute — so every set listed AFTER the damaged one is never
    /// checked, never backed up, on every pass, with no failed run recorded anywhere.
    /// </summary>
    public class AuditB_LocalStateTests
    {
        [Theory]
        [InlineData("damaged-index")]
        [InlineData("lost-key")]      // A's key file is gone from this computer (AG-06 "keys lost"): Key() throws NO_KEY
        [InlineData("none")]          // control: the same loop with A intact backs B up (the oracle can pass)
        public void OneSetsDamagedLocalIndex_DoesNotStopTheScheduledBackupsOfTheOtherSets(string harm)
        {
            using (var env = new Env())
            {
                env.CreateUser("auditb3", "Customer-Pass-1");
                var app = env.Agent("auditb3", "Customer-Pass-1");
                var srcA = env.Dir("srcA"); File.WriteAllText(Path.Combine(srcA, "a.txt"), "a");
                var srcB = env.Dir("srcB"); File.WriteAllText(Path.Combine(srcB, "b.txt"), "b");
                var slot = DateTime.Now.AddMinutes(-2);
                if (slot.Date != DateTime.Now.Date) slot = DateTime.Now;   // just after midnight: the slot is now
                var a = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "SetA", Sources = { srcA }, Hour = harm == "lost-key" ? slot.Hour : 3, Minute = harm == "lost-key" ? slot.Minute : 0, Days = harm == "lost-key" ? "SMTWTFS" : "-------" });
                var b = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "SetB", Sources = { srcB }, Hour = slot.Hour, Minute = slot.Minute });
                Assert.Equal(new[] { a.Id, b.Id }, app.Sets().Select(s => s.Id).ToArray());   // A is checked first

                // set A's local index is damaged (a bad sector, a half-restored folder, an antivirus): one bad number
                if (harm == "lost-key") File.Delete(Path.Combine(app.Home.Dir, "keys", a.Id + ".bin"));
                if (harm == "damaged-index") File.WriteAllText(Path.Combine(app.Home.SetDir(a.Id), "state.txt"), "#last\t\nREL\tC:\\x\tNOT-A-NUMBER\t1\t?\t0\t0\n");
                Assert.True(new AgentApp(app.Home.Dir).Due(b, DateTime.Now));   // set B on its own is due now

                var said = new List<string>();
                using (var cts = new CancellationTokenSource())
                {
                    var t = new Thread(() => app.ServiceLoop(cts.Token, m => { lock (said) said.Add(m); })) { IsBackground = true };
                    t.Start();
                    var until = DateTime.UtcNow.AddSeconds(90);
                    while (DateTime.UtcNow < until)
                    {
                        lock (said) if (said.Any(m => m.StartsWith("SetB:") || m.StartsWith("error:") || m.StartsWith("waiting:"))) break;
                        Thread.Sleep(200);
                    }
                    cts.Cancel();
                    t.Join(TimeSpan.FromSeconds(60));
                }
                string all; lock (said) all = string.Join(" | ", said);
                var points = app.RestoreFor(app.Interactive("Customer-Pass-1", null), b.Id).Points();   // the server's own record
                Assert.True(points.Count == 1, "set B was due but not backed up (server points: " + points.Count + "); the scheduler said: " + all);
            }
        }
    }
}
