using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>MISS-010 missed runs, RES-010 upload limit, COMP-010 compression.</summary>
    public class ResourceTests
    {
        [Fact]
        public void MissedRun_AfterTheDelay_OnlyWhenOldEnough_OrNever()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1");
                var app = env.Agent("acme", "Customer-Pass-1");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "F", Sources = { env.Dir("src") }, Hour = 22, MissedDelayMinutes = 10 });
                var back = DateTime.Now.Date.AddDays(1).AddHours(7);                  // the computer was off at 22:00, back at 07:00
                Assert.False(app.Due(set, back));                                    // seen now: waits the delay
                Assert.False(app.Due(set, back.AddMinutes(5)));
                Assert.True(app.Due(set, back.AddMinutes(11)));
                Assert.True(app.Due(set, DateTime.Now.Date.AddHours(22).AddMinutes(5)));   // on time (within 15 minutes): no delay

                set.RunMissed = false;
                Assert.False(app.Due(set, back.AddMinutes(30)));
                set.RunMissed = true; set.MissedMinHours = 48;                        // never backed up: old enough
                Assert.True(app.Due(set, back.AddMinutes(30)));
            }
        }

        [Fact]
        public void MissedOrCutOffByTheInternet_StartsWhenItIsBack()
        {
            using (var env = new Env())
            {
                env.CreateUser("acme", "Customer-Pass-1");
                var app = env.Agent("acme", "Customer-Pass-1");
                var set = app.CreateSet(app.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "F", Sources = { env.Dir("src") }, Hour = 22, MissedDelayMinutes = 30, MissedMinHours = 48 });
                var day = DateTime.Now.Date.AddDays(-1);

                // the internet was down from 21:50 to 23:20: the 22:00 backup starts as soon as it is back (no delay)
                app.NoteOffline(day.AddHours(21).AddMinutes(50).ToUniversalTime());
                app.NoteOnline(day.AddHours(23).AddMinutes(20).ToUniversalTime());
                Assert.True(app.Due(set, day.AddHours(23).AddMinutes(21)));
                set.RunMissedNet = false;
                Assert.False(app.Due(set, day.AddHours(23).AddMinutes(21)));          // switched off: waits for the next time
                set.RunMissedNet = true;

                // a backup cut off by the internet at 22:05; the internet is back at 22:40 — it starts again right away
                File.WriteAllText(Path.Combine(app.Home.SetDir(set.Id), "last-attempt.txt"), RunId.From(day.AddHours(22).AddMinutes(5).ToUniversalTime()) + "\tNETWORK");
                app.NoteOffline(day.AddHours(22).AddMinutes(5).ToUniversalTime());
                app.NoteOnline(day.AddHours(22).AddMinutes(40).ToUniversalTime());
                Assert.True(app.Due(set, day.AddHours(22).AddMinutes(41)));
                set.RunMissedNet = false;
                Assert.False(app.Due(set, day.AddHours(22).AddMinutes(41)));

                // the computer was off (no offline period): the computer rules (delay) apply as before
                set.RunMissedNet = true;
                File.Delete(Path.Combine(app.Home.SetDir(set.Id), "last-attempt.txt"));
                File.Delete(Path.Combine(app.Home.Dir, "offline-last.txt"));
                Assert.False(app.Due(set, day.AddDays(1).AddHours(7)));
            }
        }

        [Fact]
        public void UploadLimit_And_Compression()
        {
            var t = new Throttle(256);                                               // 256 KB/s
            var sw = Stopwatch.StartNew();
            using (var s = new ThrottledStream(new MemoryStream(), t)) s.Write(new byte[512 * 1024], 0, 512 * 1024);
            Assert.True(sw.Elapsed.TotalSeconds >= 1.7, "took " + sw.Elapsed.TotalSeconds);
            Assert.False(new Throttle(0).On);

            var data = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 20000).Select(i => "row," + i + ",text\n")));
            long Size(string level)
            {
                var ms = new MemoryStream(); var w = new BackupObject.Writer(ms, KeySet.Random()) { Compression = level };
                w.AddChunk("c", data); w.Finish(new Msg()); return ms.Length;
            }
            Assert.True(Size("MAX") <= Size("FAST")); Assert.True(Size("FAST") < Size("NONE")); Assert.True(Size("NONE") > data.Length);
        }

        [Fact]
        public void Settings_Survive_TheProfile()
        {
            var s = new BackupSetInfo { Id = "1", Name = "x", Compression = "FAST", BandwidthKbps = 512, BusyCpuPercent = 80, LowPriority = false, RunMissed = false, MissedDelayMinutes = 3, MissedMinHours = 12 };
            var r = BackupSetInfo.FromXml(s.ToXml());
            Assert.Equal("FAST", r.Compression); Assert.Equal(512, r.BandwidthKbps); Assert.Equal(80, r.BusyCpuPercent); Assert.False(r.LowPriority);
            Assert.False(r.RunMissed); Assert.Equal(3, r.MissedDelayMinutes); Assert.Equal(12, r.MissedMinHours);
            Assert.Equal("MAX", BackupSetInfo.FromXml(new BackupSetInfo { Id = "2", Name = "y" }.ToXml()).Compression);   // maximum by default
        }
    }
}
