using System;
using System.Collections.Generic;
using OnlineBackup.Agent;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>Bug 95 (Windows run 17, W17): wbadmin refused every System State backup on a one-disk server ("You cannot use a
    /// volume that is included in the backup as a storage location") because Windows' setting AllowSSBToAnyVolume was not
    /// made. The decision is tested here; the real wbadmin run is W17 on Windows.</summary>
    public class SystemStateTargetComponentTests
    {
        [Fact]
        public void TheSettingIsMissing_ItIsMade_AndTheLogSaysSo()
        {
            var reg = new Dictionary<string, object>(); var log = new List<string>();
            SystemState.AllowTargetOnSystemVolume(n => reg.TryGetValue(n, out var v) ? v : null, (n, v) => reg[n] = v, log.Add);
            Assert.Equal(1, reg["AllowSSBToAnyVolume"]);
            Assert.Contains(log, l => l.Contains("AllowSSBToAnyVolume=1"));
        }

        [Fact]
        public void TheSettingIsAlreadyMade_NothingIsWritten()
        {
            var writes = 0; var log = new List<string>();
            SystemState.AllowTargetOnSystemVolume(n => 1, (n, v) => writes++, log.Add);
            Assert.Equal(0, writes);
            Assert.Empty(log);
        }

        [Fact]
        public void TheSettingIsZero_ItIsMadeOne()
        {
            object written = null;
            SystemState.AllowTargetOnSystemVolume(n => 0, (n, v) => written = v, l => { });
            Assert.Equal(1, written);
        }

        [Fact]
        public void TheWindowsKeyIsTheDocumentedOne()
        {
            Assert.Equal(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\wbengine\SystemStateBackup", SystemState.WbengineKey);
        }
    }
}
