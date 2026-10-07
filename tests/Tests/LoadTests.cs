using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// LOAD-010: a big server — 500 customers with 10 sets each (5,000 sets), 50 computers asking for their settings at the
    /// same moment, real backups running meanwhile. Every page of the admin site must open within 2 seconds, a computer
    /// must get its answer within 1 second (95% of the requests). Runs with OB_LOAD=1 (a few minutes); OB_LOAD_OUT=file
    /// writes the measurements.
    /// </summary>
    public class LoadTests
    {
        static double Ms(Action a) { var sw = Stopwatch.StartNew(); a(); return sw.Elapsed.TotalMilliseconds; }

        [Fact]
        public void BigServer_500Customers_5000Sets_PagesAndComputersStayFast()
        {
            if (Environment.GetEnvironmentVariable("OB_LOAD") != "1") throw NotTested.Because("the load run is its own step (OB_LOAD=1; the gate runs it in job tests-extra)");
            int customers = int.Parse(Environment.GetEnvironmentVariable("OB_LOAD_CUSTOMERS") ?? "500");
            var report = new StringBuilder();
            using (var env = new Env())
            {
                var admin = env.Admin();
                var sw = Stopwatch.StartNew();
                Parallel.For(0, customers, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
                    env.Admin().Call("POST", "/api/admin/users", new Msg().Set("login", "cust" + i.ToString("000")).Set("password", "Customer-Pass-1").Set("alias", "Customer " + i).Set("quotaGB", 100).Set("email", "it@c" + i + ".example")));
                report.AppendLine("created " + customers + " customers: " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
                // 9 sets each (the 10th: a real one below), written straight into the profiles (as an existing installation has them)
                sw.Restart();
                foreach (var path in Directory.GetFiles(env.HomeA, "Profile.xml", SearchOption.AllDirectories))
                {
                    var p = Profile.Load(path);
                    for (int s = 0; s < 9; s++)
                    {
                        var set = new BackupSetInfo { Id = (1700000000000L + s).ToString(), Name = "Set " + s, Type = s == 3 ? "MSSQL" : "FILE", Computer = "PC-" + (s % 3), Sources = { @"D:\Data\" + s } };
                        var e = set.ToXml(); e.SetAttributeValue("TOTAL_UNCOMPRESS_FILE_SIZE", (s + 1) * 3L * 1024 * 1024 * 1024); e.SetAttributeValue("LAST_BACKUP_COMPLETE", "1759600000000");
                        p.Root.Add(e);
                    }
                    p.Save(path);
                }
                report.AppendLine("wrote " + customers * 9 + " sets (+ the real ones): " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");

                // computers: 50 registered, a few with real backups (logs, statistics, the AI learning)
                var apps = new List<AgentApp>();
                for (int i = 0; i < 50; i++) apps.Add(env.Agent("cust" + i.ToString("000"), "Customer-Pass-1", name: "pc" + i));
                var src = env.Dir("src"); for (int i = 0; i < 200; i++) File.WriteAllText(Path.Combine(src, "f" + i + ".txt"), new string('x', 2000 + i));
                var real = apps.Take(5).Select(a => new { a, set = a.CreateSet(a.Interactive("Customer-Pass-1", null), "Customer-Pass-1", new BackupSetInfo { Name = "Real", Sources = { src } }) }).ToList();
                foreach (var r in real) Assert.Equal("BS_STOP_SUCCESS", r.a.Backup(r.set.Id).Result);

                // the admin site, every heavy page (the slowest of 3)
                var pages = new[] { "dashboard", "users", "tasks", "live", "checks", "insights", "tickets/", "recycle", "users/cust250/computers", "users/cust250/sets/1700000000003" };
                var slow = new List<string>();
                foreach (var pg in pages)
                {
                    double worst = 0;
                    try { for (int k = 0; k < 3; k++) worst = Math.Max(worst, Ms(() => admin.Call("GET", "/api/admin/" + pg))); }
                    catch (AgentException e) { slow.Add(pg + " answered " + e.Code); continue; }
                    report.AppendLine("page " + pg + ": " + worst.ToString("0") + " ms");
                    if (worst > 2000) slow.Add(pg + " " + worst.ToString("0") + " ms");
                }

                // 50 computers at the same moment, 10 times each, while a backup runs
                var times = new System.Collections.Concurrent.ConcurrentBag<double>();
                var backup = Task.Run(() => real[0].a.Backup(real[0].set.Id));
                Parallel.ForEach(apps, new ParallelOptions { MaxDegreeOfParallelism = 50 }, a => { for (int k = 0; k < 10; k++) times.Add(Ms(() => a.Profile())); });
                Assert.Equal("BS_STOP_SUCCESS", backup.Result.Result);
                var sorted = times.OrderBy(x => x).ToList();
                double p95 = sorted[(int)(sorted.Count * 0.95)], p50 = sorted[sorted.Count / 2];
                report.AppendLine("computers: " + sorted.Count + " requests, median " + p50.ToString("0") + " ms, 95% within " + p95.ToString("0") + " ms, slowest " + sorted.Last().ToString("0") + " ms");

                var outFile = Environment.GetEnvironmentVariable("OB_LOAD_OUT");
                if (!string.IsNullOrEmpty(outFile)) File.WriteAllText(outFile, report.ToString());
                Assert.True(slow.Count == 0, "Slow pages with " + customers + " customers: " + string.Join("; ", slow) + "\n" + report);
                Assert.True(p95 < 1000, "Computers wait too long: " + report);
            }
        }
    }
}
