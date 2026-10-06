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
    /// QA agent B (code audit) — filters, skipped folders, links (BK-04).
    /// Scanner.Files follows a directory link when FollowLink is on (the default: WIN_FOLLOW_LINK = "Y") with no memory of
    /// the folders it already walked, so a link that points to its own parent (a junction loop, as in Windows profiles:
    /// "AppData\Local\Application Data" → "AppData\Local") is walked again and again until the path is too long — every
    /// file under it is backed up many times under ever longer names, and the run ends with errors every time.
    /// </summary>
    public class AuditB_FilterTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "obauditb-f-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public void Dispose() { try { Directory.Delete(root, true); } catch (Exception) { } }

        [Fact]
        public void ALinkToItsOwnParent_IsNotWalkedForEver_EachFileIsBackedUpOnce()
        {
            var src = Path.Combine(root, "data");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "a.txt"), "a");
            File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "b");
            try { Directory.CreateSymbolicLink(Path.Combine(src, "sub", "loop"), src); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is PlatformNotSupportedException) { return; /* no links here: NOT TESTED */ }

            var set = new BackupSetInfo { Name = "F", Sources = { src } };
            Assert.True(set.FollowLink);   // the default
            var warnings = new List<string>(); var unreachable = new List<string>();
            var files = Scanner.Files(set, warnings.Add, unreachable).Take(10000).Select(f => f.FullName).ToList();

            Assert.True(files.Count <= 2 && unreachable.Count == 0,
                files.Count + " files yielded for 2 real files; " + unreachable.Count + " folders reported unreachable (each is an error in the run); deepest: "
                + files.OrderByDescending(f => f.Length).First().Length + " characters");
        }

        /// <summary>Control: with links not followed the same tree gives each file once (proves the oracle can pass).</summary>
        [Fact]
        public void Control_LinksNotFollowed_EachFileOnce()
        {
            var src = Path.Combine(root, "data2");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "a.txt"), "a");
            File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "b");
            try { Directory.CreateSymbolicLink(Path.Combine(src, "sub", "loop"), src); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is PlatformNotSupportedException) { return; }
            var set = new BackupSetInfo { Name = "F", Sources = { src }, FollowLink = false };
            var unreachable = new List<string>();
            var files = Scanner.Files(set, _ => { }, unreachable).Take(10000).ToList();
            Assert.Equal(2, files.Count);
            Assert.Empty(unreachable);
        }
    }
}
