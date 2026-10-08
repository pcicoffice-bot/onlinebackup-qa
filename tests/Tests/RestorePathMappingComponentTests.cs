using System;
using System.IO;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>Q25: where the tests look for a restored source folder. Path.Combine(target, "C:\\src") is "C:\\src" itself, so on
    /// Windows AgentRig.Restored() read the SOURCE, not the restore (3 FAILs in the CI Windows job, and every PASS there that
    /// used it proved nothing). AgentRig.Under maps like the product (C:\x -> target\C\x) and refuses a path outside target.</summary>
    public class RestorePathMappingComponentTests
    {
        static readonly string Target = Path.Combine(Path.GetTempPath(), "restore-target");

        [Fact]
        public void TheSourceFolder_IsLookedForUnderTheRestoreFolder_NeverAtTheSourceItself()
        {
            var src = Path.Combine(Path.GetTempPath(), "obrig-12345678", "src");
            var p = AgentRig.Under(Target, src);
            Assert.StartsWith(Path.GetFullPath(Target) + Path.DirectorySeparatorChar, p);
            Assert.NotEqual(Path.GetFullPath(src), p);
            Assert.EndsWith(Path.Combine("obrig-12345678", "src"), p);
        }

        [Fact]
        public void AWindowsDrivePath_IsMappedToTheDriveLetterFolder()
        {
            var p = AgentRig.Under(Target, @"C:\Users\qa\src");
            Assert.StartsWith(Path.GetFullPath(Target) + Path.DirectorySeparatorChar, p);
            Assert.DoesNotContain(":", p.Substring(Path.GetPathRoot(p).Length));
        }

        [Fact]
        public void APathThatLeavesTheRestoreFolder_IsRefused_NotLookedForElsewhere()
        {
            Assert.Throws<InvalidOperationException>(() => AgentRig.Under(Target, "../../outside"));
        }
    }
}
