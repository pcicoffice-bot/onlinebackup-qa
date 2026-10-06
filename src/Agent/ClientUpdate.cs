using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    /// <summary>
    /// UPD-020 (owner: "an update button for the client too — everything must be easy"): the client software updates
    /// itself from its backup server. The server lists the files of the client it carries with their SHA-256; only the
    /// files that differ are downloaded (and checked), then a copy of the updater stops the service, puts the files in
    /// place and starts the service again. By itself every 6 hours, or at once with "Update" in the program.
    /// </summary>
    public static class ClientUpdate
    {
        public const string Service = "OnlineBackupAgent";
        static DateTime lastAuto = DateTime.MinValue;
        static readonly object gate = new object();

        public static string InstallDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }

        public static string CurrentVersion
        {
            get { try { var p = Path.Combine(InstallDir, "version.txt"); return File.Exists(p) ? File.ReadAllText(p).Trim() : "0"; } catch (Exception) { return "0"; } }
        }

        static string Sha(string f) { using (var s = File.OpenRead(f)) using (var h = SHA256.Create()) return Bytes.Hex(h.ComputeHash(s)); }

        /// <summary>What the server has and which of its files differ here.</summary>
        public static Msg Check(AgentApp app, out List<Msg> changed)
        {
            var list = app.DeviceClient().Call("GET", "/api/client/files");
            changed = list.List("files").Where(f =>
            {
                var local = Path.Combine(InstallDir, f["name"] ?? "");
                try { return !File.Exists(local) || Sha(local) != (f["sha256"] ?? "").ToLowerInvariant(); } catch (Exception) { return true; }
            }).ToList();
            var windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            return new Msg().Set("current", CurrentVersion).Set("latest", list["version"] ?? "").Set("available", windows && changed.Count > 0 ? 1 : 0).Set("files", changed.Count);
        }

        /// <summary>Downloads the changed files (each checked by its SHA-256) and starts the updater; the service restarts.</summary>
        public static string Apply(AgentApp app, Action<string> say)
        {
            lock (gate)
            {
                List<Msg> changed;
                var st = Check(app, out changed);
                if (st["available"] != "1") return "CURRENT";
                var stage = Path.Combine(app.Home.Dir, "update", DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
                Directory.CreateDirectory(stage);
                // the updater runs from the stage (a copy of this program), so the program files can be replaced
                foreach (var f in Directory.GetFiles(InstallDir).Where(x => new[] { ".exe", ".dll", ".config" }.Contains(Path.GetExtension(x).ToLowerInvariant())))
                    File.Copy(f, Path.Combine(stage, Path.GetFileName(f)), true);
                var names = new List<string>();
                foreach (var f in changed)
                {
                    var name = Path.GetFileName(f["name"] ?? ""); if (name.Length == 0) continue;
                    var dst = Path.Combine(stage, name);
                    app.DeviceClient().Download("/api/client/file?name=" + Uri.EscapeDataString(name), dst);
                    if (Sha(dst) != (f["sha256"] ?? "").ToLowerInvariant()) throw new AgentException(0, "UPDATE", "A downloaded file is damaged (" + name + ") — the update was not installed.");
                    names.Add(name);
                }
                File.WriteAllLines(Path.Combine(stage, "update-files.txt"), names.ToArray());
                say("update " + CurrentVersion + " → " + st["latest"] + ": " + string.Join(", ", names.ToArray()));
                var psi = new ProcessStartInfo(Path.Combine(stage, "OnlineBackup.Agent.exe"), "apply-update --from \"" + stage + "\" --to \"" + InstallDir.TrimEnd('\\') + "\"") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = stage };
                Process.Start(psi);
                return "UPDATING";
            }
        }

        /// <summary>The service: every 6 hours, by itself (Windows), unless the IT company turned it off for this computer.</summary>
        public static void Auto(AgentApp app, Action<string> say)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || (DateTime.UtcNow - lastAuto).TotalHours < 6) return;
            lastAuto = DateTime.UtcNow;
            if ((string)app.Home.Config.Attribute("AUTO_UPDATE") == "N") return;
            try { Apply(app, say); } catch (Exception e) { say("update: " + e.Message); }
        }

        /// <summary>Tests: sc.exe replaced (the service control), and the waits.</summary>
        public static Func<string, string> ScHook;
        public static TimeSpan StopWait = TimeSpan.FromSeconds(60), StartWait = TimeSpan.FromSeconds(60);

        /// <summary>The last update attempt on this computer: OK or FAILED, the version, the reason (shown by the service).</summary>
        public static string ResultPath(string installDir) { return Path.Combine(installDir, "update-result.txt"); }

        /// <summary>
        /// The updater (a copy in the stage). R1 (GPT audit 8–9): all or nothing.
        ///  1. The service must really stop — else nothing is changed and the update is reported as not installed.
        ///  2. Every file it replaces is kept first; if one file cannot be put in place, every file goes back to the
        ///     previous version (never a mix of two versions).
        ///  3. Each new file is checked by its SHA-256 after copying.
        ///  4. The service must really start again with the new files — else the previous version is put back and started.
        /// The outcome is written to update-result.txt (OK only after all of this).
        /// </summary>
        public static int Install(string from, string to)
        {
            var log = Path.Combine(from, "update.log");
            Action<string> say = m => { try { File.AppendAllText(log, DateTime.UtcNow.ToString("o") + " " + m + Environment.NewLine); } catch (Exception) { } };
            Action<string, string> result = (st, why) => { try { File.WriteAllText(ResultPath(to), st + "\t" + NewVersion(from) + "\t" + why + "\t" + DateTime.UtcNow.ToString("o")); } catch (Exception) { } say(st + ": " + why); };
            if (ScHook == null) Thread.Sleep(1500);   // the service answers the program first
            Sc("stop " + Service);
            if (!WaitState("STOPPED", StopWait))
            {
                Sc("start " + Service);   // a stop that hangs: ask it to run on, untouched
                result("FAILED", "the backup service did not stop in " + (int)StopWait.TotalSeconds + " s — nothing was changed, the previous version runs on");
                return 3;
            }
            var names = File.ReadAllLines(Path.Combine(from, "update-files.txt")).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            var keep = Path.Combine(from, "previous"); Directory.CreateDirectory(keep);
            var touched = new List<string>(); var aside = new Dictionary<string, string>();
            try
            {
                foreach (var name in names)
                {
                    var src = Path.Combine(from, name); var dst = Path.Combine(to, name);
                    if (File.Exists(dst)) File.Copy(dst, Path.Combine(keep, name), true);
                    touched.Add(name);
                    try { File.Copy(src, dst, true); }
                    catch (IOException)
                    {
                        // in use (the customer's program window): moved aside, the new file in its place — Windows allows renaming a running program
                        var old = dst + ".old-" + DateTime.UtcNow.Ticks; File.Move(dst, old); aside[name] = old; File.Copy(src, dst, true);
                    }
                    if (Sha(dst) != Sha(src)) throw new IOException(name + " is different after copying");
                    say("updated " + name);
                }
            }
            catch (Exception e)
            {
                var notBack = Rollback(to, keep, touched, aside, say);
                Sc("start " + Service);
                result("FAILED", "a file could not be replaced (" + e.Message + ") — " + PutBack(notBack));
                return 4;
            }
            say(Sc("start " + Service));
            if (!WaitState("RUNNING", StartWait))
            {
                Sc("stop " + Service); WaitState("STOPPED", StopWait);
                var notBack = Rollback(to, keep, touched, aside, say);
                Sc("start " + Service);
                result("FAILED", "the new version did not start — " + (notBack.Count == 0 ? "the previous version was put back and started" : PutBack(notBack)));
                return 5;
            }
            result("OK", "every file is in place and checked, and the service runs");
            return 0;
        }

        /// <summary>Bug 27: the result said "every file was put back" even when one could not be — a broken installation called a
        /// clean rollback. Now the files that could not be put back are named, with what to do.</summary>
        static string PutBack(List<string> notBack)
        {
            return notBack.Count == 0 ? "every file was put back to the previous version"
                : "these files could not be put back: " + string.Join(", ", notBack.ToArray()) + " — the installation is incomplete; install the client again";
        }

        /// <summary>Tests: called before each file is put back (a failure is injected here).</summary>
        public static Action<string> BeforePutBack;

        static List<string> Rollback(string to, string keep, List<string> touched, Dictionary<string, string> aside, Action<string> say)
        {
            var failed = new List<string>();
            foreach (var name in touched)
                try
                {
                    var hook = BeforePutBack; if (hook != null) hook(name);
                    var dst = Path.Combine(to, name); var prev = Path.Combine(keep, name);
                    string moved;
                    if (aside.TryGetValue(name, out moved)) { if (File.Exists(dst)) File.Delete(dst); File.Move(moved, dst); }
                    else if (File.Exists(prev)) File.Copy(prev, dst, true);
                    else if (File.Exists(dst)) File.Delete(dst);   // it was new in this version
                    say("put back " + name);
                }
                catch (Exception e) { say("COULD NOT PUT BACK " + name + ": " + e.Message); failed.Add(name); }
            return failed;
        }

        static bool WaitState(string state, TimeSpan limit)
        {
            var until = DateTime.UtcNow + limit;
            do { if (Sc("query " + Service).Contains(state)) return true; Thread.Sleep(ScHook != null ? 50 : 1000); } while (DateTime.UtcNow < until);
            return false;
        }

        static string NewVersion(string from) { try { var v = Path.Combine(from, "version.txt"); return File.Exists(v) ? File.ReadAllText(v).Trim() : "?"; } catch (Exception) { return "?"; } }

        static string Sc(string args)
        {
            if (ScHook != null) return ScHook(args);
            try
            {
                var r = OnlineBackup.Core.ProcessRunner.Run(new ProcessStartInfo("sc.exe", args), OnlineBackup.Core.Limits.Short);
                return r.Out + r.Err + (r.TimedOut ? " (sc.exe did not answer)" : "");
            }
            catch (Exception e) { return e.Message; }
        }
    }
}
