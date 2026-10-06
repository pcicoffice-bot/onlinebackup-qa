#!/bin/bash
M=/tmp/claude-0/-home-user-golan-crm/a604ba29-09da-5998-b6c7-5aefd7765d0d/scratchpad/l-tools/mut.py
Q='FullyQualifiedName~QaL_MutationGapTests'
A='FullyQualifiedName~AuditH_SessionTests|FullyQualifiedName~AuthComponentTests|FullyQualifiedName~SecurityTests'
CTX='            try { prof = LoadProfile(login); } catch (Exception e) when (!(e is ApiException)) { throw new ApiException(401, "SESSION", "Sign in again."); }
            if (prof.Get("STATUS") != "ENABLE" || prof.Get("DISABLED") == "Y") throw new ApiException(403, "SUSPENDED", "The user is suspended.");
            CheckIp(prof, ip);'
$M M11-session-ip-not-checked src/Server/Users.cs "$CTX" "${CTX%
            CheckIp(prof, ip);}" "$A"
$M M12-session-suspended-not-checked src/Server/Users.cs "$CTX" '            try { prof = LoadProfile(login); } catch (Exception e) when (!(e is ApiException)) { throw new ApiException(401, "SESSION", "Sign in again."); }
            CheckIp(prof, ip);' "$A"
$M M01b-endcount-with-new-test src/Agent/LocalState.cs 'if (v2 && end != entries) throw' 'if (false && v2 && end != entries) throw' "FullyQualifiedName~IndexLinesLostComponentTests|FullyQualifiedName~LocalStateComponentTests|FullyQualifiedName~AuditB_LocalStateTests|$Q"
$M M03b-restore-sha-with-new-test src/Agent/Restore.cs 'if (Bytes.Sha256Hex(fs) != o["sha"]) throw' 'if (false && Bytes.Sha256Hex(fs) != o["sha"]) throw' "FullyQualifiedName~RestoreComponentTests|FullyQualifiedName~RestoreTempNameComponentTests|$Q"
$M M15b-temp-fixed-name-with-new-test src/Agent/Restore.cs 'var tmp = dest + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".ob-restoring";' 'var tmp = dest + ".ob-restoring";' "FullyQualifiedName~RestoreTempNameComponentTests|FullyQualifiedName~RestoreComponentTests|$Q"
$M M18b-mine-computer-with-new-test src/Agent/AgentApp.cs 'if (!string.IsNullOrEmpty(s.Computer) && !s.Computer.Equals(Home.Computer, StringComparison.OrdinalIgnoreCase)) return false;' '/* computer not checked */' "FullyQualifiedName~ComputerTests|FullyQualifiedName~GuardTests|$Q"
$M M18c-mine-key-not-checked src/Agent/AgentApp.cs 'return Home.LoadKey(s.Id) != null;' 'return true;' "FullyQualifiedName~ComputerTests|FullyQualifiedName~GuardTests|FullyQualifiedName~SchedulerTests|FullyQualifiedName~ClientUiTests"
$M M18d-mine-key-with-new-test src/Agent/AgentApp.cs 'return Home.LoadKey(s.Id) != null;' 'return true;' "FullyQualifiedName~ComputerTests|FullyQualifiedName~GuardTests|$Q"
$M M19b-size-unchecked-with-new-test src/Agent/Restore.cs 'if (total != header.Long("size")) throw' 'if (false) throw' "FullyQualifiedName~RestoreComponentTests|FullyQualifiedName~AuditB_GrowingFileTests|$Q"
echo QUEUE2 DONE
