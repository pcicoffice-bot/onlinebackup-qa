using OnlineBackup.Agent;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>Bug 100 (QA shards, hosted Windows with restic, run 37655041862): a restore from the website of chosen files
    /// failed - "Fatal: All path filters must be absolute, starting with a forward slash '/'". restic names a Windows file
    /// "/C/Users/..." (the drive letter as the first folder, forward slashes); the restore API takes the customer's path as
    /// Windows writes it ("C:\Users\..."), and the agent gave it to restic unchanged. The agent now gives restic its own
    /// form; a path already in restic's form, and every path on Linux, stay as they are. The Windows behaviour itself is
    /// proven by WebRestoreTests on the hosted-Windows QA shards.</summary>
    public class ResticPathComponentTests
    {
        [Theory]
        [InlineData(@"C:\Users\anna\Documents\invoice 7.pdf", "/C/Users/anna/Documents/invoice 7.pdf")]
        [InlineData(@"c:\Data\Report [final].docx", "/C/Data/Report [final].docx")]
        [InlineData(@"D:/Mixed\slashes/x.txt", "/D/Mixed/slashes/x.txt")]
        [InlineData(@"C:\", "/C/")]
        [InlineData(@"C:", "/C")]
        [InlineData("/C/Users/anna/a.txt", "/C/Users/anna/a.txt")]                  // already restic's form (from the point's list)
        [InlineData(@"\\server\share\x.txt", @"\\server\share\x.txt")]              // a network path: not restic's to rename here
        public void OnWindows_TheCustomersPathBecomesResticsForm(string given, string expected)
        {
            Assert.Equal(expected, ResticRunner.ToResticPath(given, windows: true));
        }

        [Theory]
        [InlineData("/home/anna/a.txt")]
        [InlineData(@"C:\not\a\linux\path")]   // on Linux this is a (strange) relative name: left as it is
        public void OnLinux_EveryPathStaysAsItIs(string given)
        {
            Assert.Equal(given, ResticRunner.ToResticPath(given, windows: false));
        }
    }
}
