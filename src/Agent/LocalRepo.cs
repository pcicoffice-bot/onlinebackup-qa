using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    /// <summary>
    /// Local copy (Ahsay EXTRA_LOCAL_BACKUP): the same encrypted objects, in the same layout as the server, written to a
    /// local disk / NAS beside the online backup — &lt;path&gt;\&lt;login&gt;\&lt;set&gt;\Current, run folders, .chk, jobs.log.
    /// Restoring from it needs no internet. Its own retention (days). A failure here never fails the online backup.
    /// </summary>
    public sealed class LocalRepo
    {
        public string Dir { get; private set; }
        string staging;
        readonly List<string> deletes = new List<string>();
        readonly List<ChkRecord> uploads = new List<ChkRecord>();

        public LocalRepo(string root, string login, string setId)
        {
            Dir = Path.Combine(Path.Combine(root, login ?? "user"), setId);
            Directory.CreateDirectory(Path.Combine(Dir, "Current"));
        }

        static string Os(string rel) { return rel.Replace('/', Path.DirectorySeparatorChar); }

        public void Begin(string job)
        {
            staging = Path.Combine(Path.Combine(Dir, "jobs"), job);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            deletes.Clear(); uploads.Clear();
        }

        /// <summary>A stream the object is written to while it is uploaded (tee).</summary>
        public Stream Open(string rel, int seq)
        {
            var p = Path.Combine(staging, Os(ChkRecord.ObjectName(rel, seq)));
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            return new FileStream(p, FileMode.Create, FileAccess.Write);
        }

        public void Staged(ChkRecord rec)
        {
            Atomic.WriteText(Path.Combine(staging, Os(ChkRecord.ObjectName(rec.Rel, rec.Seq))) + ".chk", rec.ToText());
            uploads.Add(rec);
        }

        public void Delete(string rel) { deletes.Add(rel); }

        IEnumerable<string> Chain(string rel)
        {
            var dir = Path.Combine(Dir, "Current", Os(Path.GetDirectoryName(Os(rel)) ?? ""));
            var name = Path.GetFileName(Os(rel));
            if (!Directory.Exists(dir)) return Enumerable.Empty<string>();
            return Directory.GetFiles(dir, name + ".*").Where(f => !f.EndsWith(".chk") && Path.GetFileName(f).Length == name.Length + 4);
        }

        /// <summary>Same order as the server: old chains to the run folder, staged objects into Current, then jobs.log.</summary>
        public void Commit(string job, Msg stats)
        {
            var replaced = new HashSet<string>(deletes);
            foreach (var u in uploads) if (u.Kind == "F") replaced.Add(u.Rel);
            foreach (var rel in replaced)
                foreach (var obj in Chain(rel).ToList())
                {
                    var relObj = obj.Substring(Path.Combine(Dir, "Current").Length + 1);
                    var dst = Path.Combine(Path.Combine(Dir, job), relObj);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Move(obj, dst);
                    if (File.Exists(obj + ".chk")) File.Move(obj + ".chk", dst + ".chk");
                }
            foreach (var u in uploads)
            {
                var name = Os(ChkRecord.ObjectName(u.Rel, u.Seq));
                var src = Path.Combine(staging, name);
                var dst = Path.Combine(Path.Combine(Dir, "Current"), name);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                if (File.Exists(dst)) File.Delete(dst);
                if (File.Exists(dst + ".chk")) File.Delete(dst + ".chk");
                File.Move(src, dst);
                File.Move(src + ".chk", dst + ".chk");
            }
            Atomic.AppendLine(Path.Combine(Dir, "jobs.log"), "C\t" + job + "\tnew=" + stats.Long("new") + "\tupd=" + stats.Long("upd") + "\tperm=" + stats.Long("perm") + "\tdel=" + stats.Long("del"));
            Directory.Delete(staging, true);
        }

        public void Abort() { if (staging != null && Directory.Exists(staging)) Directory.Delete(staging, true); }

        public List<string> Points()
        {
            var p = Path.Combine(Dir, "jobs.log");
            if (!File.Exists(p)) return new List<string>();
            var expired = new HashSet<string>(OnlineBackup.Core.Atomic.ReadAllLines(p).Where(l => l.StartsWith("X\t")).Select(l => l.Split('\t')[1]));
            return OnlineBackup.Core.Atomic.ReadAllLines(p).Where(l => l.StartsWith("C\t")).Select(l => l.Split('\t')[1]).Where(j => !expired.Contains(j)).Distinct().OrderBy(j => j, StringComparer.Ordinal).ToList();
        }

        /// <summary>Retention of the local copy: points older than "days" expire; run folders no kept point needs are deleted.</summary>
        public int ApplyRetention(int days, DateTime nowUtc)
        {
            var points = Points();
            var keep = new RetentionPolicy { Unit = "DAYS", Period = days }.Keep(points, nowUtc);
            foreach (var j in points.Where(j => !keep.Contains(j))) Atomic.AppendLine(Path.Combine(Dir, "jobs.log"), "X\t" + j);
            int deleted = 0;
            foreach (var folder in Directory.GetDirectories(Dir).Where(d => RunId.TryParse(Path.GetFileName(d), out _)))
            {
                var removed = Path.GetFileName(folder);
                var chks = Directory.GetFiles(folder, "*.chk", SearchOption.AllDirectories);
                string minCreated = chks.Length == 0 ? removed : chks.Select(c => ChkRecord.Parse(OnlineBackup.Core.Atomic.ReadAllText(c)).Job).OrderBy(j => j, StringComparer.Ordinal).First();
                if (keep.Any(p => string.CompareOrdinal(p, minCreated) >= 0 && string.CompareOrdinal(p, removed) < 0)) continue;
                Directory.Delete(folder, true);
                deleted++;
            }
            return deleted;
        }

        /// <summary>Same answer as the server's FilesAt, computed from the .chk files of the local copy.</summary>
        public Msg FilesAt(string point)
        {
            if (point == null) point = Points().LastOrDefault();
            var m = new Msg().Set("point", point);
            if (point == null) return m;
            var rows = new List<KeyValuePair<string, ChkRecord>>();
            foreach (var area in Directory.GetDirectories(Dir))
            {
                var name = Path.GetFileName(area);
                if (name != "Current" && !RunId.TryParse(name, out _)) continue;
                if (name != "Current" && string.CompareOrdinal(name, point) <= 0) continue;   // removed at or before the point
                foreach (var c in Directory.GetFiles(area, "*.chk", SearchOption.AllDirectories))
                {
                    var rec = ChkRecord.Parse(OnlineBackup.Core.Atomic.ReadAllText(c));
                    if (string.CompareOrdinal(rec.Job, point) > 0) continue;
                    rows.Add(new KeyValuePair<string, ChkRecord>(name + "/" + c.Substring(area.Length + 1, c.Length - area.Length - 5).Replace(Path.DirectorySeparatorChar, '/'), rec));
                }
            }
            foreach (var g in rows.GroupBy(r => r.Value.Rel))
            {
                var items = g.OrderBy(r => r.Value.Seq).ToList();
                if (items[0].Value.Kind != "F") continue;
                var last = items[items.Count - 1].Value;
                var f = new Msg().Set("rel", g.Key).Set("enc", last.EncPath).Set("orig", last.Orig).Set("mtime", last.Mtime).Set("perm", last.PermOnly ? 1 : 0);
                foreach (var r in items) f.Add("objects", new Msg().Set("loc", r.Key).Set("seq", r.Value.Seq).Set("kind", r.Value.Kind).Set("size", r.Value.Size).Set("sha", r.Value.Sha256));
                m.Add("files", f);
            }
            return m;
        }

        public string ObjectPath(string loc)
        {
            if (loc.Contains("..")) throw new AgentException(400, "BAD_PATH", "Invalid path.");
            return Path.Combine(Dir, Os(loc));
        }
    }

    /// <summary>Writes to two streams at once (the upload and the local copy) and hashes nothing — the object writer does.</summary>
    public sealed class TeeStream : Stream
    {
        readonly Stream a, b;
        bool bFailed;
        public Exception SecondError { get; private set; }
        public TeeStream(Stream a, Stream b) { this.a = a; this.b = b; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            a.Write(buffer, offset, count);
            if (b != null && !bFailed)
                try { b.Write(buffer, offset, count); } catch (Exception e) { bFailed = true; SecondError = e; }
        }
        public override void Flush() { a.Flush(); if (b != null && !bFailed) try { b.Flush(); } catch (Exception e) { bFailed = true; SecondError = e; } }
        public bool SecondOk { get { return b != null && !bFailed; } }
        public override bool CanRead { get { return false; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
    }
}
