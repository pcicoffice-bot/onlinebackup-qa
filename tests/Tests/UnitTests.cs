using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    public class CryptoTests
    {
        [Fact]
        public void TheLockAfterWrongPasswords_CannotBeSwitchedOffOrWeakened()
        {
            // SEC-040: at most 10 attempts, at least 5 minutes (0 = until an administrator unlocks)
            Assert.Equal(10, OnlineBackup.Server.Users.ClampAttempts(100)); Assert.Equal(1, OnlineBackup.Server.Users.ClampAttempts(0)); Assert.Equal(3, OnlineBackup.Server.Users.ClampAttempts(3));
            Assert.Equal(5, OnlineBackup.Server.Users.ClampMinutes(1)); Assert.Equal(0, OnlineBackup.Server.Users.ClampMinutes(0)); Assert.Equal(30, OnlineBackup.Server.Users.ClampMinutes(30));
        }

        [Fact]
        public void PasswordRule_AtLeast8Characters_WithALetter()
        {
            // SEC-030: the owner's rule, the same on every screen
            Assert.True(Passwords.Ok("abcd1234")); Assert.True(Passwords.Ok("סיסמהטובה")); Assert.True(Passwords.Ok("12345678a"));
            Assert.False(Passwords.Ok("abc123")); Assert.False(Passwords.Ok("12345678")); Assert.False(Passwords.Ok(null));
        }

        [Fact]
        public void EncryptDecryptRoundTrip()
        {
            var k = KeySet.Random();
            var data = Encoding.UTF8.GetBytes("שלום — backup data");
            Assert.Equal(data, Cipher.Decrypt(k, Cipher.Encrypt(k, data)));
            Assert.NotEqual(Cipher.Encrypt(k, data), Cipher.Encrypt(k, data));   // random IV
        }

        [Fact]
        public void TamperedOrWrongKeyIsRejected()
        {
            var k = KeySet.Random();
            var enc = Cipher.Encrypt(k, new byte[1000]);
            enc[100] ^= 1;
            Assert.Throws<CryptographicException>(() => Cipher.Decrypt(k, enc));
            Assert.Throws<CryptographicException>(() => Cipher.Decrypt(KeySet.Random(), Cipher.Encrypt(k, new byte[10])));
        }

        [Fact]
        public void PasswordKeyIsDeterministicAndCheckValueDetectsWrongPassword()
        {
            var salt = Bytes.Random(16);
            var a = KeySet.Derive("Pa$$w0rd", salt, 1000);
            Assert.Equal(a.CheckValue(), KeySet.Derive("Pa$$w0rd", salt, 1000).CheckValue());
            Assert.NotEqual(a.CheckValue(), KeySet.Derive("other", salt, 1000).CheckValue());
        }

        [Fact]
        public void ServerNamesAreDeterministicCaseInsensitiveAndRevealNothing()
        {
            var k = KeySet.Random();
            var a = NameCipher.RelPath(k, @"C:\Data\Accounting\report.xlsx");
            Assert.Equal(a, NameCipher.RelPath(k, @"c:\data\ACCOUNTING\Report.XLSX"));
            Assert.Equal(4, a.Split('/').Length);                      // same depth as the source tree
            Assert.DoesNotContain("Data", a, StringComparison.OrdinalIgnoreCase);
            Assert.Matches("^[A-Z2-7/]+$", a);
            Assert.Equal(@"C:\Data\Accounting\report.xlsx", NameCipher.DecryptPath(k, NameCipher.EncryptPath(k, @"C:\Data\Accounting\report.xlsx")));
        }

        [Fact]
        public void TotpMatchesRfc6238Vector()
        {
            // RFC 6238, SHA1 secret "12345678901234567890", T = 59 s → 94287082 (last 6 digits 287082).
            var secret = Base32.Encode(Encoding.ASCII.GetBytes("12345678901234567890"));
            Assert.Equal("287082", Totp.Code(secret, new DateTime(1970, 1, 1, 0, 0, 59, DateTimeKind.Utc)));
            Assert.True(Totp.Verify(secret, "287082", new DateTime(1970, 1, 1, 0, 1, 15, DateTimeKind.Utc)));
            Assert.False(Totp.Verify(secret, "287082", new DateTime(1970, 1, 1, 0, 5, 0, DateTimeKind.Utc)));
        }

        [Fact]
        public void PasswordHashVerifies()
        {
            var h = PasswordHash.Create("secret-123", 1000);
            Assert.True(PasswordHash.Verify("secret-123", h));
            Assert.False(PasswordHash.Verify("secret-124", h));
            Assert.DoesNotContain("secret", h);
        }
    }

    public class ChunkerTests
    {
        static byte[] RandomData(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }

        static List<string> Ids(Chunker c, byte[] data)
        {
            using (var sha = SHA256.Create()) return c.Split(new MemoryStream(data)).Select(x => Bytes.Hex(sha.ComputeHash(x))).ToList();
        }

        [Fact]
        public void ChunksRebuildTheStreamAndRespectLimits()
        {
            var c = new Chunker(64 * 1024);
            var data = RandomData(3 * 1024 * 1024 + 123, 1);
            var chunks = c.Split(new MemoryStream(data)).ToList();
            Assert.Equal(data, chunks.SelectMany(x => x).ToArray());
            Assert.All(chunks.Take(chunks.Count - 1), x => Assert.InRange(x.Length, 16 * 1024, 512 * 1024));
        }

        [Fact]
        public void InsertInTheMiddleChangesOnlyNearbyChunks()
        {
            // The point of content-defined chunking: 1KB inserted in the middle of a file keeps almost every chunk.
            var c = new Chunker(64 * 1024);
            var data = RandomData(8 * 1024 * 1024, 2);
            var changed = data.Take(4 * 1024 * 1024).Concat(RandomData(1024, 3)).Concat(data.Skip(4 * 1024 * 1024)).ToArray();
            var before = Ids(c, data); var after = Ids(c, changed);
            var shared = after.Count(before.Contains);
            Assert.True(shared >= after.Count - 3, "shared " + shared + " of " + after.Count);
        }
    }

    public class ObjectTests
    {
        [Fact]
        public void ObjectRoundTripWithHeaderAtTheEnd()
        {
            var k = KeySet.Random();
            var ms = new MemoryStream();
            var w = new BackupObject.Writer(ms, k);
            var chunks = new[] { Encoding.UTF8.GetBytes(new string('a', 5000)), Bytes.Random(3000) };
            foreach (var c in chunks) w.AddChunk(BackupObject.ChunkId(k, c), c);
            var header = new Msg().Set("path", @"C:\x.txt").Set("size", 8000);
            foreach (var c in chunks) header.Add("recipe", new Msg().Set("h", BackupObject.ChunkId(k, c)).Set("n", c.Length));
            w.Finish(header);
            Assert.Equal(Bytes.Hex(Bytes.Sha256(ms.ToArray())), w.Sha256);
            var h = BackupObject.ReadHeader(ms, k);
            Assert.Equal(@"C:\x.txt", h["path"]);
            var read = BackupObject.ReadChunks(ms, k, h).Select(x => x.Value).ToList();
            Assert.Equal(chunks[0], read[0]); Assert.Equal(chunks[1], read[1]);
            Assert.True(ms.Length < 5000 + 3000 + 600);   // the repetitive chunk was compressed
            var raw = ms.ToArray();
            Assert.DoesNotContain("x.txt", Encoding.UTF8.GetString(raw));   // nothing readable on the server
        }
    }

    public class FormatTests
    {
        [Fact]
        public void LogLinesMatchAhsayFormat()
        {
            var t = new DateTime(2026, 10, 3, 19, 0, 0, DateTimeKind.Utc);
            var l = AhsayLog.Line(t, "upd", @"D:\data\a, b.xlsx", 29556, 1791040484019, 31060);
            var f = AhsayLog.Fields(l);
            Assert.Equal(8, f.Length);
            Assert.Equal("upd", f[1]); Assert.Equal(@"D:\data\a, b.xlsx", f[2]); Assert.Equal("29556", f[3]); Assert.Equal("1791040484019", f[5]); Assert.Equal("31060", f[7]);
            var end = AhsayLog.Fields(AhsayLog.Line(t, "end", message: "BS_STOP_SUCCESS"));
            Assert.Equal("BS_STOP_SUCCESS", end[4]);
            // A real Ahsay 6 line parses the same way.
            var real = AhsayLog.Fields("1791048425041,info,,0,\"Start [ Windows NT (unknown) (server), AhsayOBM 6.29.0.0 ]\",,,0");
            Assert.Equal("Start [ Windows NT (unknown) (server), AhsayOBM 6.29.0.0 ]", real[4]);
        }

        [Fact]
        public void ProfileUsesAhsayNamesAndKeepsUnknownAttributes()
        {
            var p = Profile.Create("demo2026", "Demo", PasswordHash.Create("x12345678", 1000), "iw", null);
            var s = new BackupSetInfo { Id = "1700000000000", Name = "Files", Sources = { @"C:\Data", @"D:\Shared" }, Hour = 21, Minute = 30 };
            s.Retention = new RetentionPolicy { Unit = "DAYS", Period = 30, Weekly = 8, Monthly = 12 };
            s.Filters.Add(new FilterRule { Type = "END_WITH", Patterns = { ".tmp" } });
            var e = s.ToXml();
            e.SetAttributeValue("SOMETHING_AHSAY_HAS", "keep-me");
            p.Root.Add(e);
            var back = Profile.Parse(p.Doc.ToString());
            var set = back.FindSet("1700000000000");
            Assert.Equal("keep-me", (string)set.Attribute("SOMETHING_AHSAY_HAS"));
            Assert.Equal(2, set.Elements("SEL-SOURCE").Count());
            var info = BackupSetInfo.FromXml(set);
            Assert.Equal(21, info.Hour); Assert.Equal(30, info.Minute); Assert.Equal(8, info.Retention.Weekly); Assert.Equal(12, info.Retention.Monthly);
            Assert.Equal("DEMO2026", back.Get("LOGIN_NAME").ToUpperInvariant());
            Assert.NotNull(set.Element("RETENTION_POLICY")); Assert.NotNull(set.Element("DAILY_SCHEDULE")); Assert.NotNull(set.Element("ENCRYPTING_KEY"));
        }

        [Fact]
        public void FiltersExcludeLikeAhsay()
        {
            var s = new BackupSetInfo();
            s.Filters.Add(new FilterRule { Type = "END_WITH", ApplyFile = true, Patterns = { ".tmp" } });
            s.Filters.Add(new FilterRule { Type = "WILDCARD", ApplyFile = true, Patterns = { "~$*" } });
            Assert.True(OnlineBackup.Agent.Scanner.Excluded(s, @"C:\a\x.TMP", "x.TMP", false));
            Assert.True(OnlineBackup.Agent.Scanner.Excluded(s, @"C:\a\~$book.xlsx", "~$book.xlsx", false));
            Assert.False(OnlineBackup.Agent.Scanner.Excluded(s, @"C:\a\book.xlsx", "book.xlsx", false));
        }

        [Fact]
        public void RetentionByDaysJobsAndAdvanced()
        {
            var now = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);
            var jobs = Enumerable.Range(0, 400).Select(i => RunId.From(now.AddDays(-400 + i).AddHours(22))).ToList();
            var days = new RetentionPolicy { Unit = "DAYS", Period = 30 }.Keep(jobs, now);
            Assert.InRange(days.Count, 30, 31);
            Assert.Equal(7, new RetentionPolicy { Unit = "JOBS", Period = 7 }.Keep(jobs, now).Count);
            var adv = new RetentionPolicy { Unit = "DAYS", Period = 7, Weekly = 8, Monthly = 12, Yearly = 2 }.Keep(jobs, now);
            Assert.True(adv.Count > 7 + 8 && adv.Count <= 7 + 8 + 12 + 2 + 1, "kept " + adv.Count);
            Assert.Contains(jobs.Last(), adv);
        }
    }
}
