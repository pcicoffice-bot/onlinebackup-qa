using System;
using System.IO;
using System.Linq;
using System.Threading;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>Bug 93: Atomic.WriteBytes gave up at the first refusal of the final rename. On Windows the rename is refused while
    /// another program has the file open for a moment (CoreFormatsTests.AStateFile_... failed in the CI Windows job with
    /// "being used by another process"). The refusal is made here, on any system, by a folder standing at the file's name.</summary>
    [Collection("Atomic-attempts")]
    public class AtomicReplaceRefusedComponentTests
    {
        static string NewDir() { var d = Path.Combine(Path.GetTempPath(), "obatomic-r-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(d); return d; }

        [Fact]
        public void ARefusalThatEnds_TheWriteWaitsAndSucceeds()
        {
            var dir = NewDir(); var path = Path.Combine(dir, "state.txt");
            Directory.CreateDirectory(path);                                   // the name is taken for a moment
            var t = new Thread(() => { Thread.Sleep(150); Directory.Delete(path); }); t.Start();
            Atomic.WriteText(path, "new state");
            t.Join();
            Assert.Equal("new state", File.ReadAllText(path));
            Assert.Equal(new[] { path }, Directory.GetFileSystemEntries(dir));  // no temporary file left
            Directory.Delete(dir, true);
        }

        [Fact]
        public void ARefusalThatNeverEnds_FailsLoudly_AndLeavesNoTemporaryFile()
        {
            var dir = NewDir(); var path = Path.Combine(dir, "state.txt");
            Directory.CreateDirectory(path);
            var was = Atomic.ReplaceAttempts; Atomic.ReplaceAttempts = 3;
            try { Assert.ThrowsAny<IOException>(() => Atomic.WriteText(path, "new state")); }
            finally { Atomic.ReplaceAttempts = was; }
            Assert.Equal(new[] { path }, Directory.GetFileSystemEntries(dir));  // only the folder that blocked it
            Directory.Delete(dir, true);
        }
    }
}
