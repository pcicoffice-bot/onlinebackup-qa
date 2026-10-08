using System;
using System.IO;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Bug 101: the temporary folders of restored (decrypted) files must really go. QA shards run 37675796567 on hosted
    /// Windows showed what stayed after a web restore: webrestore-*\C\Users [ReadOnly, Directory] - restic restores the
    /// folder's read-only attribute and Directory.Delete fails on it (the files under it were already gone); the failure was
    /// swallowed. Oracle: the folder is gone, what a link inside pointed at is untouched, and a folder that cannot go is reported.
    /// </summary>
    public class TempDirsComponentTests
    {
        static string NewDir() { var d = Path.Combine(Path.GetTempPath(), "obtd-" + Guid.NewGuid().ToString("N").Substring(0, 10)); Directory.CreateDirectory(d); return d; }

        static void ReadOnly(string p) { File.SetAttributes(p, File.GetAttributes(p) | FileAttributes.ReadOnly); }

        static void Writable(string p) { if (File.Exists(p) || Directory.Exists(p)) File.SetAttributes(p, File.GetAttributes(p) & ~FileAttributes.ReadOnly); }

        [Fact]
        public void AFolderTreeWithReadOnlyFoldersAndFiles_IsRemoved_AsAfterARestore()
        {
            var root = NewDir();
            var tmp = Path.Combine(root, "webrestore-x");
            var users = Path.Combine(tmp, "C", "Users");
            var deep = Path.Combine(users, "dana", "Documents");
            Directory.CreateDirectory(deep);
            var f = Path.Combine(deep, "contract.docx"); File.WriteAllText(f, "decrypted");
            ReadOnly(f); ReadOnly(deep); ReadOnly(users);
            try
            {
                Assert.Null(TempDirs.Remove(tmp));
                Assert.False(Directory.Exists(tmp), "the restored folder stays");
            }
            finally { foreach (var p in new[] { f, deep, users }) try { Writable(p); } catch (Exception) { } try { Directory.Delete(root, true); } catch (Exception) { } }
        }

        [Fact]
        public void ALinkInside_IsRemovedAsALink_WhatItPointsAtIsUntouched()
        {
            var root = NewDir();
            var outside = Path.Combine(root, "customer"); Directory.CreateDirectory(outside);
            var keep = Path.Combine(outside, "keep.txt"); File.WriteAllText(keep, "mine"); ReadOnly(keep); ReadOnly(outside);
            var tmp = Path.Combine(root, "stage"); Directory.CreateDirectory(tmp);
            try
            {
                try { Directory.CreateSymbolicLink(Path.Combine(tmp, "link"), outside); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { throw NotTested.Because("a folder link cannot be made here: " + e.Message); }
                Assert.Null(TempDirs.Remove(tmp));
                Assert.False(Directory.Exists(tmp));
                Assert.Equal("mine", File.ReadAllText(keep));
                Assert.True((File.GetAttributes(keep) & FileAttributes.ReadOnly) != 0, "the link was followed: the attribute of a file outside the folder was changed");
                Assert.True((File.GetAttributes(outside) & FileAttributes.ReadOnly) != 0, "the link was followed: the attribute of a folder outside was changed");
            }
            finally { Writable(keep); Writable(outside); try { Directory.Delete(root, true); } catch (Exception) { } }
        }

        [Fact]
        public void AFolderThatCannotGo_IsReported_WithItsPath_NotSwallowed()
        {
            if (!OperatingSystem.IsWindows()) throw NotTested.Because("an open file blocks a removal only on Windows");
            var tmp = NewDir();
            var f = Path.Combine(tmp, "held.bin"); File.WriteAllText(f, "x");
            using (new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var why = TempDirs.Remove(tmp);
                Assert.NotNull(why);
                Assert.Contains(tmp, why);
            }
            Assert.Null(TempDirs.Remove(tmp));                                                      // once released it goes
            Assert.False(Directory.Exists(tmp));
        }

        [Fact]
        public void NothingThere_IsNothingToReport()
        {
            Assert.Null(TempDirs.Remove(Path.Combine(Path.GetTempPath(), "obtd-never-" + Guid.NewGuid().ToString("N"))));
            Assert.Null(TempDirs.Remove(null));
        }
    }
}
