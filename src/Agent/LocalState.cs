using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    /// <summary>
    /// The agent's own folder (ProgramData\OnlineBackup on Windows): settings, the device token and the set keys —
    /// protected with DPAPI (machine scope) on Windows — and the local index of every set.
    /// </summary>
    public sealed class AgentHome
    {
        public string Dir { get; private set; }
        public AgentHome(string dir) { Dir = Path.GetFullPath(dir); Directory.CreateDirectory(Dir); }

        string ConfigPath { get { return Path.Combine(Dir, "agent.xml"); } }

        public XElement Config
        {
            get { return File.Exists(ConfigPath) ? OnlineBackup.Core.Atomic.LoadXElement(ConfigPath) : new XElement("AGENT"); }
            set { Atomic.WriteText(ConfigPath, value.ToString()); }
        }

        public string Server { get { return (string)Config.Attribute("SERVER"); } }
        public string Login { get { return (string)Config.Attribute("LOGIN"); } }
        public string Computer { get { return (string)Config.Attribute("COMPUTER") ?? Environment.MachineName; } }
        /// <summary>BUILTIN = the agent's own TLS 1.2 (automatic on Windows 2003 / XP); PIN = SHA-256 of the server certificate.</summary>
        public string Tls { get { return (string)Config.Attribute("TLS"); } }
        public string Pin { get { return (string)Config.Attribute("PIN"); } }

        public string DeviceToken
        {
            get { var v = (string)Config.Attribute("DEVICE"); return string.IsNullOrEmpty(v) ? null : Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(v))); }
        }

        public void SaveRegistration(string server, string login, string computer, string device, string tls = null, string pin = null)
        {
            var c = Config;
            if (tls != null) c.SetAttributeValue("TLS", tls);
            if (pin != null) c.SetAttributeValue("PIN", pin);
            c.SetAttributeValue("SERVER", server); c.SetAttributeValue("LOGIN", login); c.SetAttributeValue("COMPUTER", computer);
            c.SetAttributeValue("DEVICE", Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(device))));
            Config = c;
        }

        public void SaveKey(string setId, KeySet k) { Atomic.WriteBytes(Path.Combine(Dir, "keys", setId + ".bin"), Protect(k.ToRaw())); }
        public KeySet LoadKey(string setId)
        {
            var p = Path.Combine(Dir, "keys", setId + ".bin");
            return File.Exists(p) ? KeySet.FromRaw(Unprotect(OnlineBackup.Core.Atomic.ReadAllBytes(p))) : null;
        }

        /// <summary>A secret of this computer only (e.g. the SQL Server password of a set), protected like the keys.</summary>
        public void SaveSecret(string name, string value) { Atomic.WriteBytes(Path.Combine(Dir, "secrets", name + ".bin"), Protect(Encoding.UTF8.GetBytes(value ?? ""))); }
        public string LoadSecret(string name)
        {
            var p = Path.Combine(Dir, "secrets", name + ".bin");
            return File.Exists(p) ? Encoding.UTF8.GetString(Unprotect(OnlineBackup.Core.Atomic.ReadAllBytes(p))) : null;
        }

        public string SetDir(string setId) { var d = Path.Combine(Dir, "sets", setId); Directory.CreateDirectory(d); return d; }

        static bool IsWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }

        static byte[] Protect(byte[] data)
        {
#if NETFRAMEWORK
            if (IsWindows) return ProtectedData.Protect(data, null, DataProtectionScope.LocalMachine);
#endif
            return data;   // non-Windows test runs only
        }

        static byte[] Unprotect(byte[] data)
        {
#if NETFRAMEWORK
            if (IsWindows) return ProtectedData.Unprotect(data, null, DataProtectionScope.LocalMachine);
#endif
            return data;
        }
    }

    /// <summary>
    /// Local index of one set: every file backed up (server path, size, time, attributes, delta chain) and, for large
    /// files, the chunk list of the last version — so a delta is computed without downloading anything.
    /// </summary>
    public sealed class LocalState
    {
        public sealed class Entry
        {
            public string Rel, Path;
            public long Size, Mtime;
            public string Attrs;
            public int Seq;            // last object number in the chain (0 = only the full copy)
            public long DeltaBytes;    // bytes sent as deltas since the full copy
        }

        readonly string dir;
        public Dictionary<string, Entry> Files = new Dictionary<string, Entry>(StringComparer.Ordinal);
        public string LastSuccess = "";
        /// <summary>Agent D (D-5): the start of the last successful run on THIS computer's clock (Unix ms). LastSuccess is the
        /// server's run id, on the server's clock — the schedule compares slots on the computer's clock with it, so a
        /// computer ahead of the server saw the day's backup as not done and ran it again.</summary>
        public long LastSuccessLocalMs;

        public LocalState(string setDir)
        {
            dir = setDir;
            var p = System.IO.Path.Combine(dir, "state.txt");
            if (!File.Exists(p)) return;
            try { Read(p); }
            catch (Exception e) when (e is FormatException || e is OverflowException || e is IOException || e is ArgumentException)
            {
                // bug 38 (Agent B): a damaged state.txt threw at every look and stopped the schedule. Kept aside for
                // diagnosis; the index then counts as missing and the next backup rebuilds it from the server
                Files.Clear(); LastSuccess = null;
                try { File.Move(p, p + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)); } catch (Exception) { }
            }
        }

        /// <summary>Agent D (D-2): the index ends with its own line count ("#end"). Lines lost or a torn end (a disk error, a
        /// torn write, a cleanup tool) used to read as a shorter index: the files on the lost lines were never deleted from
        /// the newest point although the customer had deleted them. An index that does not add up is damaged — it is put
        /// aside and rebuilt from the server (bug 38), which knows every file it holds.</summary>
        void Read(string p)
        {
            bool v2 = false; long end = -1; int entries = 0;
            foreach (var line in OnlineBackup.Core.Atomic.ReadAllLines(p, Encoding.UTF8))
            {
                if (line == "#v2") { v2 = true; continue; }
                if (line.StartsWith("#lastlocal\t")) { LastSuccessLocalMs = long.Parse(line.Substring(11), NumberStyles.None, CultureInfo.InvariantCulture); continue; }
                if (line.StartsWith("#last\t")) { LastSuccess = line.Substring(6); continue; }
                if (line.StartsWith("#end\t")) { end = long.Parse(line.Substring(5), NumberStyles.None, CultureInfo.InvariantCulture); continue; }
                if (v2 && end >= 0) throw new FormatException("lines after the end of the index");
                var f = line.Split('\t');
                if (f.Length < 7) { if (v2) throw new FormatException("a damaged line in the index"); continue; }
                entries++;
                Files[f[0]] = new Entry
                {
                    Rel = f[0], Path = f[1], Size = long.Parse(f[2], CultureInfo.InvariantCulture), Mtime = long.Parse(f[3], CultureInfo.InvariantCulture),
                    Attrs = f[4], Seq = int.Parse(f[5], CultureInfo.InvariantCulture), DeltaBytes = long.Parse(f[6], CultureInfo.InvariantCulture)
                };
            }
            if (v2 && end != entries) throw new FormatException("the index has " + entries + " lines of " + (end < 0 ? "an unknown number (its end is missing)" : end.ToString(CultureInfo.InvariantCulture)));
        }

        /// <summary>Drops the whole index and every chunk list (they describe versions the server may not have).</summary>
        public void Forget()
        {
            Files.Clear(); LastSuccess = "";
            var ch = System.IO.Path.Combine(dir, "chunks");
            try { if (Directory.Exists(ch)) Directory.Delete(ch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        public bool Exists { get { return File.Exists(System.IO.Path.Combine(dir, "state.txt")); } }

        public void Save()
        {
            var sb = new StringBuilder();
            sb.Append("#v2\n");
            sb.Append("#last\t").Append(LastSuccess).Append('\n');
            if (LastSuccessLocalMs > 0) sb.Append("#lastlocal\t").Append(LastSuccessLocalMs.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var e in Files.Values)
                sb.Append(e.Rel).Append('\t').Append(e.Path).Append('\t').Append(e.Size.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(e.Mtime.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(e.Attrs).Append('\t')
                  .Append(e.Seq.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(e.DeltaBytes.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("#end\t").Append(Files.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            Atomic.WriteText(System.IO.Path.Combine(dir, "state.txt"), sb.ToString());
        }

        string ChunkFile(string rel)
        {
            using (var sha = SHA256.Create())
                return System.IO.Path.Combine(dir, "chunks", Bytes.Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(rel)), 16) + ".txt");
        }

        /// <summary>Chunk list of the last version: "id length" per line, in file order.</summary>
        public List<KeyValuePair<string, int>> LoadChunks(string rel)
        {
            return ReadChunkList(ChunkFile(rel));
        }

        /// <summary>Bug 44 (Agent C, F3): one damaged chunk list threw in every later backup (the run failed, again and
        /// again). A list that does not read is dropped: the file is then sent as a new full copy — more data, never less.</summary>
        static List<KeyValuePair<string, int>> ReadChunkList(string p)
        {
            if (!File.Exists(p)) return null;
            var list = new List<KeyValuePair<string, int>>();
            try
            {
                foreach (var l in OnlineBackup.Core.Atomic.ReadAllLines(p))
                {
                    if (l.Length == 0) continue;
                    var x = l.Split(' '); int n;
                    if (x.Length != 2 || x[0].Length == 0 || !int.TryParse(x[1], NumberStyles.None, CultureInfo.InvariantCulture, out n) || n <= 0) throw new FormatException("bad chunk line");
                    list.Add(new KeyValuePair<string, int>(x[0], n));
                }
                return list;
            }
            catch (Exception e) when (e is FormatException || e is IOException)
            {
                try { File.Delete(p); } catch (Exception) { }
                return null;
            }
        }

        public void SaveChunks(string rel, List<KeyValuePair<string, int>> chunks)
        {
            Atomic.WriteText(ChunkFile(rel), string.Join("\n", chunks.Select(c => c.Key + " " + c.Value.ToString(CultureInfo.InvariantCulture)).ToArray()));
        }

        public void DropChunks(string rel) { var p = ChunkFile(rel); if (File.Exists(p)) File.Delete(p); var b = BaseFile(rel); if (File.Exists(b)) File.Delete(b); }

        // differential: the chunk list of the last FULL copy (each delta is made against it)
        string BaseFile(string rel) { return ChunkFile(rel).Replace(".txt", ".full.txt"); }
        public List<KeyValuePair<string, int>> LoadBaseChunks(string rel)
        {
            return ReadChunkList(BaseFile(rel));
        }
        public void SaveBaseChunks(string rel, List<KeyValuePair<string, int>> chunks)
        {
            Atomic.WriteText(BaseFile(rel), string.Join("\n", chunks.Select(c => c.Key + " " + c.Value.ToString(CultureInfo.InvariantCulture)).ToArray()));
        }
    }
}
