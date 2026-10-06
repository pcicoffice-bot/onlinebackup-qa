using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// QA agent B (code audit) — the server's store (ST-04 index rebuild, ST-08 storage opened as it is).
    /// SetStore.Rebuild deletes every file whose FULL PATH contains ".tmp" (meant for leftovers of Atomic.WriteBytes,
    /// "&lt;name&gt;.tmp&lt;guid&gt;"). A user name may contain a dot ("^[A-Za-z0-9][A-Za-z0-9_.-]{2,63}$"), so a customer
    /// called e.g. "acme.tmp" or "j.tmpson" — or a storage folder like "D:\Backup.tmp" — loses every stored object at the
    /// first index rebuild (lost index.db, a recovered commit, or the admin's "rebuild").
    /// </summary>
    public class AuditB_StoreTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "obauditb-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        const string SetId = "1700000000042";
        readonly KeySet key = KeySet.Random();
        public void Dispose() { try { Directory.Delete(root, true); } catch (Exception) { } }

        byte[] Obj(string content)
        {
            var ms = new MemoryStream();
            var w = new BackupObject.Writer(ms, key);
            var data = System.Text.Encoding.UTF8.GetBytes(content);
            w.AddChunk(BackupObject.ChunkId(key, data), data);
            w.Finish(new Msg().Set("path", content));
            return ms.ToArray();
        }
        static string N(string path) { return string.Join("/", path.Split('/').Select(seg => { using (var h = SHA256.Create()) return Base32.Encode(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(seg))).Substring(0, 26); })); }

        [Theory]
        [InlineData("acme.tmp")]       // a valid user name (Users.LoginRe allows dots)
        [InlineData("j.tmpson")]
        [InlineData("plainuser")]      // control: the same steps with a name without ".tmp" keep the data
        public void IndexRebuild_KeepsEveryObject_WhateverTheUserFolderIsCalled(string login)
        {
            var userDir = Path.Combine(root, login);
            var st = new SetStore(userDir, SetId);
            var j1 = st.BeginJob(DateTime.UtcNow);
            var bytes = Obj("customer data");
            st.StageObject(j1, new ChkRecord { Rel = N("C/data/a.txt"), Seq = 0, Kind = "F", EncPath = "e", Orig = 13, Mtime = 1 }, new MemoryStream(bytes), 1L << 30);
            st.Commit(j1, new Msg().Set("new", 1));
            Assert.Single(st.FilesAt(null).List("files"));
            var onDisk = Directory.GetFiles(Path.Combine(userDir, "files", SetId, SetStore.Current), "*.000", SearchOption.AllDirectories);
            Assert.Single(onDisk);

            // the documented promise: the index can be rebuilt from the .chk files at any time (admin "rebuild")
            var r = st.Rebuild(false);

            // oracle: the object file on disk and the files the restore would list
            Assert.True(File.Exists(onDisk[0]), "the stored backup object was DELETED by the index rebuild (user folder '" + login + "')");
            Assert.Equal(1, r.Int("files"));
            Assert.Single(new SetStore(userDir, SetId).FilesAt(null).List("files"));
        }

        [Theory]
        [InlineData("acme.tmp")]
        [InlineData("plainuser")]
        public void LostIndex_IsRebuiltAtTheNextOpen_WithTheDataKept(string login)
        {
            var userDir = Path.Combine(root, login);
            var st = new SetStore(userDir, SetId);
            var j1 = st.BeginJob(DateTime.UtcNow);
            st.StageObject(j1, new ChkRecord { Rel = N("C/data/a.txt"), Seq = 0, Kind = "F", EncPath = "e", Orig = 13, Mtime = 1 }, new MemoryStream(Obj("customer data")), 1L << 30);
            st.Commit(j1, new Msg().Set("new", 1));
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var set = Path.Combine(userDir, "files", SetId);
            foreach (var f in Directory.GetFiles(set, "index.db*")) File.Delete(f);   // the index is lost (damaged disk, restored folder)

            var again = new SetStore(userDir, SetId);   // the constructor rebuilds a missing index from the .chk files
            Assert.Single(again.FilesAt(null).List("files"));
            Assert.Single(Directory.GetFiles(Path.Combine(set, SetStore.Current), "*.000", SearchOption.AllDirectories));
        }
    }
}
