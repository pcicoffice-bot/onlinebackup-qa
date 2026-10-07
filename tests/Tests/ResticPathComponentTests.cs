using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>Bug 100 (QA shards, hosted Windows with restic, runs 37655041862 and 37668510911): a restore from the website of
    /// chosen files failed - "Fatal: All path filters must be absolute, starting with a forward slash". restic names a Windows
    /// file "/C/Users/..."; the restore API took the path as Windows writes it ("C:\Users\...") and the server (WebRestore)
    /// and the agent (ResticRunner) gave it to restic unchanged. Both now give restic its own form - by the path's shape, not
    /// by the system they run on (a Linux server restores a Windows computer's backup too). The Windows behaviour itself is
    /// proven by WebRestoreTests on the hosted-Windows QA shards.</summary>
    public class ResticPathComponentTests
    {
        [Theory]
        [InlineData(@"C:\Users\anna\Documents\invoice 7.pdf", "/C/Users/anna/Documents/invoice 7.pdf")]
        [InlineData(@"c:\Data\Report [final].docx", "/C/Data/Report [final].docx")]
        [InlineData(@"D:/Mixed\slashes/x.txt", "/D/Mixed/slashes/x.txt")]
        [InlineData(@"C:\", "/C/")]
        [InlineData(@"C:", "/C")]
        public void AWindowsPath_BecomesResticsForm(string given, string expected)
        {
            Assert.Equal(expected, ResticPaths.Of(given));
        }

        [Theory]
        [InlineData("/C/Users/anna/a.txt")]                 // already restic's form (from the point's list)
        [InlineData("/home/anna/a.txt")]                    // Linux
        [InlineData(@"\\server\share\x.txt")]               // a network path: not restic's to rename here
        [InlineData("relative/name.txt")]
        [InlineData("")]
        [InlineData(null)]
        public void EveryOtherPath_StaysAsItIs(string given)
        {
            Assert.Equal(given, ResticPaths.Of(given));
        }
    }
}
