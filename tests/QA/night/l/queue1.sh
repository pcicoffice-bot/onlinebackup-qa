#!/bin/bash
M=/tmp/claude-0/-home-user-golan-crm/a604ba29-09da-5998-b6c7-5aefd7765d0d/scratchpad/l-tools/mut.py
R='FullyQualifiedName~AuditJ_ResticServerTests|FullyQualifiedName~AuditJ_ResticEngineTests'
A='FullyQualifiedName~AuditH_SessionTests|FullyQualifiedName~AuthComponentTests|FullyQualifiedName~SecurityTests'
$M M08-size-header-listing src/Agent/BackupRun.cs '.Set("size", read)' '.Set("size", entry.Size)' 'FullyQualifiedName~AuditB_GrowingFileTests'
$M M09-place-always-overwrites src/Agent/ResticRunner.cs 'else if (overwrite && File.Exists(dest) && !isDir)' 'else if (File.Exists(dest) && !isDir)' "$R"
$M M10-restic-nothing-restored-ok src/Agent/ResticRunner.cs 'if (c[0] + c[1] + c[2] == 0) throw' 'if (false) throw' "$R"
$M M11-session-ip-not-checked src/Server/Users.cs '            if (prof.Get("STATUS") != "ENABLE" || prof.Get("DISABLED") == "Y") throw new ApiException(403, "SUSPENDED", "The user is suspended.");
            CheckIp(prof, ip);' '            if (prof.Get("STATUS") != "ENABLE" || prof.Get("DISABLED") == "Y") throw new ApiException(403, "SUSPENDED", "The user is suspended.");' "$A"
$M M12-session-suspended-not-checked src/Server/Users.cs '            if (prof.Get("STATUS") != "ENABLE" || prof.Get("DISABLED") == "Y") throw new ApiException(403, "SUSPENDED", "The user is suspended.");
            CheckIp(prof, ip);' '            CheckIp(prof, ip);' "$A"
$M M13-totp-step-reuse src/Server/Users.cs 'step <= p.GetLong("TOTP_LAST_STEP")' 'step < p.GetLong("TOTP_LAST_STEP")' "$A"
$M M14-setcontrol-version-ignored src/Server/SetControl.cs 'if (!string.IsNullOrEmpty(version) && version != Version(e))' 'if (false && version != Version(e))' 'FullyQualifiedName~SetEditVersionIntegrationTests|FullyQualifiedName~SetControlTests'
$M M15-restore-temp-fixed-name src/Agent/Restore.cs 'var tmp = dest + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".ob-restoring";' 'var tmp = dest + ".ob-restoring";' 'FullyQualifiedName~RestoreTempNameComponentTests|FullyQualifiedName~RestoreComponentTests|FullyQualifiedName~ChallengeF_RunEndTests'
$M M16-due-ignores-last-success src/Agent/AgentApp.cs 'if (lastOk >= slot.Value) return false;' '/* lastOk ignored */' 'FullyQualifiedName~SchedulerTests|FullyQualifiedName~ClockSkewScheduleComponentTests|FullyQualifiedName~TimeMachineTests'
$M M17-diskimage-stale-accepted src/Agent/DiskImage.cs 'if (written < started.AddSeconds(-2))' 'if (false)' 'FullyQualifiedName~BareMetal'
$M M18-mine-ignores-computer src/Agent/AgentApp.cs 'if (!string.IsNullOrEmpty(s.Computer) && !s.Computer.Equals(Home.Computer, StringComparison.OrdinalIgnoreCase)) return false;' '/* computer not checked */' 'FullyQualifiedName~ComputerTests|FullyQualifiedName~GuardTests|FullyQualifiedName~SchedulerTests|FullyQualifiedName~ClientUiTests'
$M M19-restore-size-unchecked src/Agent/Restore.cs 'if (total != header.Long("size")) throw' 'if (false) throw' 'FullyQualifiedName~RestoreComponentTests|FullyQualifiedName~AuditB_GrowingFileTests|FullyQualifiedName~AuditB_ChainTests'
$M M20-upload-check-skipped src/Agent/BackupRun.cs 'if (resp["sha256"] == w.Sha256 && resp.Long("size") == w.Length)' 'if (true)' 'FullyQualifiedName~NetworkTests|FullyQualifiedName~AuditB_StoreTests|FullyQualifiedName~ApiInputIntegrationTests'
echo QUEUE1 DONE
