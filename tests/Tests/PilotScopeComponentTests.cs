using System.Collections.Generic;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>PILOT-010: the one rule both the server and the agent apply — which set is inside the pilot "Windows File
    /// Backup" (files and folders, this product's own engine, no commands, no local copy) and which Windows is too old.</summary>
    public class PilotScopeComponentTests
    {
        [Theory]
        [InlineData("MSSQL", "SQL Server")] [InlineData("MYSQL", "MySQL")] [InlineData("POSTGRESQL", "PostgreSQL")] [InlineData("ORACLE", "Oracle")]
        [InlineData("DOMINO", "Domino")] [InlineData("SYSTEMSTATE", "System State")] [InlineData("BAREMETAL", "bare-metal")] [InlineData("HYPERV", "Hyper-V")]
        [InlineData("VMWARE", "VMware")] [InlineData("M365", "Microsoft 365")] [InlineData("GWS", "Google Workspace")] [InlineData("SOMETHING", "SOMETHING")]
        public void EveryTypeButFiles_IsOutside_WithItsName(string type, string name)
        {
            var why = Scope.Refusal(new BackupSetInfo { Type = type, Sources = { "C:\\Data" } });
            Assert.NotNull(why);
            Assert.Contains(name, why);
            Assert.EndsWith("is not supported in this version (Windows file backup).", why);
        }

        [Fact]
        public void TheOptionsOutside_AreNamed_AndAPlainFileSetIsInside()
        {
            Assert.Null(Scope.Refusal(new BackupSetInfo { Sources = { "C:\\Data" } }));
            Assert.Null(Scope.Refusal(new BackupSetInfo { Type = "file", Sources = { "C:\\Data" }, LocalCopy = false, DestMode = "SERVER" }));
            Assert.Contains("restic", Scope.Refusal(new BackupSetInfo { Engine = "RESTIC" }));
            Assert.Contains("commands", Scope.Refusal(new BackupSetInfo { PreCommands = { "net stop x" } }));
            Assert.Contains("commands", Scope.Refusal(new BackupSetInfo { PostCommands = { "net start x" } }));
            Assert.Contains("local copy", Scope.Refusal(new BackupSetInfo { LocalCopy = true }));
            Assert.Contains("local copy", Scope.Refusal(new BackupSetInfo { DestMode = "BOTH" }));
            Assert.Contains("local copy", Scope.Refusal(new BackupSetInfo { DestMode = "LOCAL" }));
            // a set read back from its XML keeps the verdict (what the agent and the server both see)
            var x = new BackupSetInfo { Sources = { "C:\\Data" }, PostCommands = { "echo" } }.ToXml();
            Assert.Contains("commands", Scope.Refusal(BackupSetInfo.FromXml(x)));
            Assert.Null(Scope.Refusal(BackupSetInfo.FromXml(new BackupSetInfo { Sources = { "C:\\Data" } }.ToXml())));
            Assert.Null(Scope.Refusal(null));
        }

        [Fact]
        public void TheRefusal_IsTranslated_ToHebrew_WithItsReason()
        {
            var he = L.Tr("he", Scope.Refused("The restic engine"));
            // UI-07 (pilot found English reasons on Hebrew screens): the reason is translated too, not only the sentence around it
            Assert.Equal("מנוע restic אינו נתמך בגרסה זו (גיבוי קבצים של Windows).", he);
        }

        [Fact]
        public void ThePilotSwitchValue()
        {
            Assert.True(Scope.IsPilot("PILOT")); Assert.True(Scope.IsPilot("pilot"));
            Assert.False(Scope.IsPilot(null)); Assert.False(Scope.IsPilot("")); Assert.False(Scope.IsPilot("FULL"));
        }

        [Theory]
        [InlineData("1.0.0.0; Microsoft Windows NT 5.1.2600 Service Pack 3", true)]     // XP
        [InlineData("1.0.0.0; Microsoft Windows NT 5.2.3790 Service Pack 2", true)]     // 2003
        [InlineData("1.0.0.0; Microsoft Windows NT 4.0.1381", true)]
        [InlineData("1.0.0.0; Microsoft Windows NT 6.1.7601 Service Pack 1", false)]    // 7 / 2008 R2
        [InlineData("1.0.0.0; Microsoft Windows NT 10.0.19045.0", false)]
        [InlineData("1.0.0.0; Unix 6.8.0.0", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("1.0.0.0; Microsoft Windows NT x.y", false)]
        public void OldWindows_IsNt5AndOlder(string agent, bool old)
        {
            Assert.Equal(old, Scope.OldWindows(agent));
        }
    }
}
