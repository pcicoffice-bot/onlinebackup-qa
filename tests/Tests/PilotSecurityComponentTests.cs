using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Pilot release blockers AU-02 / AU-04 / AU-05 / AU-06, each part alone (no web server, no agent). Contracts (tests/QA/specs.py):
    ///   AU-02  a login, a registered device token, a revoked token          → valid = allowed; revoked = refused at once
    ///   AU-04  many failed sign-ins from one address                         → the address blocked; others not affected; the administrator alerted
    ///   AU-05  data with key A; reading with key B; a changed byte           → B cannot read; the changed byte detected
    ///   AU-06  two vendors with customers                                    → each sees and changes only its own
    /// Oracles are independent of the product: published vectors (RFC 6070 PBKDF2, NIST SP 800-38A F.2.5 AES-256-CBC), and
    /// values computed outside .NET (Python hashlib / hmac, OpenSSL) and written here as constants; exact counts and times.
    /// </summary>
    public class PilotSecurityComponentTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "obpsec-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        DateTime now = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);   // whole milliseconds: the stored times are exact

        public PilotSecurityComponentTests() { SystemClock.Use(() => now); }
        public void Dispose() { SystemClock.Use(null); try { Directory.Delete(root, true); } catch (Exception) { } }

        SystemConfig NewServer(string name)
        {
            var home = Path.Combine(root, name);
            return SystemConfig.Init(Path.Combine(home, "system"), "admin", "Admin-Pass-1", "localhost", new[] { Path.Combine(home, "home") + "|UNLIMITED|100" });
        }

        static int Status(Action a) { try { a(); return 200; } catch (ApiException e) { return e.Status; } }
        static string Code(Action a) { try { a(); return "OK"; } catch (ApiException e) { return e.Code; } }
        static byte[] H(string hex) { var b = new byte[hex.Length / 2]; for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16); return b; }
        static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant(); }

        // ================================================================== AU-02: customer sign-in, computer registration, device token

        [Fact]
        public void DeviceToken_ValidIsAllowed_RevokedIsRefusedAtOnce_TheOtherComputerGoesOn()
        {
            var cfg = NewServer("dev"); var users = new Users(cfg);
            users.Create("anna", "Anna-Pass-1", "Anna", null, "COMPRESSED", "anna@example.com", "203.0.113.5");
            Assert.NotNull(users.CheckUser("anna", "Anna-Pass-1", null, "203.0.113.5"));                     // the sign-in that registers
            var pc1 = users.RegisterDevice("anna", "PC-ONE", "203.0.113.5"); now = now.AddSeconds(1);
            var pc2 = users.RegisterDevice("anna", "PC-TWO", "203.0.113.6"); now = now.AddSeconds(1);
            Assert.Equal("anna", users.CheckDevice(pc1, "203.0.113.5"));
            Assert.Equal("anna", users.CheckDevice(pc2, "203.0.113.6"));

            // the secret is never stored: only its SHA-256 (oracle: the token's third part hashed here)
            var devices = File.ReadAllText(Path.Combine(users.UserDir("anna"), "db", "devices.xml"));
            var secret1 = pc1.Split('.')[2];
            Assert.DoesNotContain(secret1, devices);
            Assert.Contains(Hex(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(secret1))), devices);

            // revoked: refused at once (the very next call), the other computer is untouched
            Assert.Equal(1, users.Disconnect("anna", "PC-ONE", "admin", "10.0.0.1"));
            var refused = Assert.Throws<ApiException>(() => users.CheckDevice(pc1, "203.0.113.5"));
            Assert.Equal(401, refused.Status); Assert.Equal("DEVICE", refused.Code);
            Assert.Equal("anna", users.CheckDevice(pc2, "203.0.113.6"));
            // a server restart (a new Users over the same files) does not bring it back
            Assert.Equal(401, Status(() => new Users(cfg).CheckDevice(pc1, "203.0.113.5")));
        }

        [Fact]
        public void DeviceToken_ForgedOrDamaged_IsRefusedLikeAnUnknownOne()
        {
            var cfg = NewServer("forge"); var users = new Users(cfg);
            users.Create("anna", "Anna-Pass-1", "Anna", null, "COMPRESSED", "anna@example.com", "203.0.113.5");
            var t = users.RegisterDevice("anna", "PC-ONE", "203.0.113.5");
            var p = t.Split('.');
            var flipped = p[2].Substring(0, p[2].Length - 1) + (p[2].EndsWith("0") ? "1" : "0");
            foreach (var bad in new[] { null, "", "x", "a.b", p[0] + "." + p[1] + "." + flipped, p[0] + "." + (long.Parse(p[1]) + 1) + "." + p[2],
                                        "!!notbase64." + p[1] + "." + p[2], Convert.ToBase64String(Encoding.UTF8.GetBytes("nobody")) + "." + p[1] + "." + p[2],
                                        Convert.ToBase64String(Encoding.UTF8.GetBytes("..\\..\\x")) + "." + p[1] + "." + p[2], t + ".extra" })
            {
                var e = Assert.Throws<ApiException>(() => users.CheckDevice(bad, "203.0.113.5"));
                Assert.Equal(401, e.Status); Assert.Equal("DEVICE", e.Code);   // one answer for all: nothing tells a guesser which part was right
            }
            Assert.Equal("anna", users.CheckDevice(t, "203.0.113.5"));          // the real one still works after the attempts
            // a suspended customer's computer is refused too, with the right token
            var prof = users.LoadProfile("anna"); prof.SetAttr("DISABLED", "Y"); users.SaveProfile("anna", prof);
            Assert.Equal(403, Status(() => users.CheckDevice(t, "203.0.113.5")));
        }

        [Fact]
        public void ARevokedComputer_RegistersAgain_GetsANewToken_TheOldStaysRefused()
        {
            var cfg = NewServer("again"); var users = new Users(cfg);
            users.Create("anna", "Anna-Pass-1", "Anna", null, "COMPRESSED", "anna@example.com", "203.0.113.5");
            var old = users.RegisterDevice("anna", "PC-ONE", "203.0.113.5"); now = now.AddSeconds(1);
            users.Disconnect("anna", "PC-ONE", "admin", "10.0.0.1");
            Assert.Equal(401, Status(() => users.CheckDevice(old, "203.0.113.5")));
            var fresh = users.RegisterDevice("anna", "PC-ONE", "203.0.113.5");
            Assert.NotEqual(old, fresh);
            Assert.Equal("anna", users.CheckDevice(fresh, "203.0.113.5"));
            Assert.Equal(401, Status(() => users.CheckDevice(old, "203.0.113.5")));
            Assert.Equal(1, users.ActiveComputers());
        }

        [Fact]
        public void ComputerLimitOfTheLicence_ExactlyTheLimitIsAccepted_OneMoreRefused_TheSameComputerAgainIsNotANewOne()
        {
            var cfg = NewServer("lic"); var users = new Users(cfg);
            int max = cfg.License.MaxDevices;
            if (max <= 0 || max > 50) throw NotTested.Because("the server without a licence key has no small computer limit here (" + max + ")");
            users.Create("anna", "Anna-Pass-1", "Anna", null, "COMPRESSED", "anna@example.com", "203.0.113.5");
            users.Create("bob", "Bob-Pass-11", "Bob", null, "COMPRESSED", "bob@example.com", "203.0.113.5");
            for (int i = 0; i < max; i++) { users.RegisterDevice(i % 2 == 0 ? "anna" : "bob", "PC-" + i, "203.0.113.5"); now = now.AddMilliseconds(5); }
            Assert.Equal(max, users.ActiveComputers());
            Assert.Equal("LICENSE", Code(() => users.RegisterDevice("anna", "PC-NEW", "203.0.113.5")));
            Assert.Equal(402, Status(() => users.RegisterDevice("bob", "PC-NEW", "203.0.113.5")));
            var again = users.RegisterDevice("anna", "pc-0", "203.0.113.5");                   // the same computer (any case) re-registering
            Assert.Equal("anna", users.CheckDevice(again, "203.0.113.5"));
            Assert.Equal(max, users.ActiveComputers());
            users.Disconnect("bob", "PC-1", "admin", "10.0.0.1"); now = now.AddMilliseconds(5);  // a freed place can be taken
            Assert.Equal("anna", users.CheckDevice(users.RegisterDevice("anna", "PC-NEW", "203.0.113.5"), "203.0.113.5"));
        }

        // ================================================================== AU-04: Guard (IP blocking)

        List<string> CatchAlerts(SystemConfig cfg, CountdownEvent got = null)
        {
            var list = new List<string>(); var before = Guard.Mail;
            Guard.Mail = (c, subject, html) =>
            {
                if (c == cfg) { lock (list) list.Add(subject + " | " + html); if (got != null && got.CurrentCount > 0) got.Signal(); }
                else if (before != null) before(c, subject, html);
            };
            return list;
        }

        [Fact]
        public void Guard_TenWrongSignIns_BlockThatAddress_OnlyIt_AndTheAdministratorIsAlerted()
        {
            var cfg = NewServer("guard"); Guard.Reset();
            using (var mailed = new CountdownEvent(1))
            {
                var mails = CatchAlerts(cfg, mailed);
                const string attacker = "203.0.113.66", other = "198.51.100.7", office = "192.168.1.20";
                for (int i = 0; i < 9; i++) { Guard.Failed(cfg, attacker, "admin"); now = now.AddSeconds(10); }
                Assert.False(Guard.IsBlocked(cfg, attacker));                                     // 9: not yet
                for (int i = 0; i < 9; i++) Guard.Failed(cfg, other, "admin");
                Guard.Failed(cfg, attacker, "admin");                                             // the 10th within 10 minutes
                Assert.True(Guard.IsBlocked(cfg, attacker));
                Assert.False(Guard.IsBlocked(cfg, other));                                        // its own 9 do not add to the attacker's
                for (int i = 0; i < 50; i++) Guard.Failed(cfg, office, "admin");                  // the office's own network is never blocked
                Assert.False(Guard.IsBlocked(cfg, office));
                Assert.False(Guard.IsBlocked(cfg, "127.0.0.1"));

                var listed = Guard.List(cfg).List("blocked");
                Assert.Equal(new[] { attacker }, listed.Select(b => b["ip"]).ToArray());
                Assert.Equal(RunId.UnixMs(now.AddHours(24)).ToString(), listed[0]["until"]);   // 24 hours by default, to the millisecond
                Assert.Contains("10 wrong sign-ins", listed[0]["reason"]);

                Assert.True(mailed.Wait(TimeSpan.FromSeconds(30)), "the administrators were alerted");
                lock (mails)
                {
                    Assert.Contains(mails, m => m.StartsWith("An address was blocked: " + attacker));
                    Assert.DoesNotContain(mails, m => m.Contains(other));
                }
            }
        }

        [Fact]
        public void Guard_Limits_AreExact_OldFailuresLeaveTheWindow_SprayingAndScanningBlockAtTheirCount()
        {
            var cfg = NewServer("limits"); Guard.Reset(); CatchAlerts(cfg);
            // failures older than the 10-minute window do not count
            for (int i = 0; i < 9; i++) Guard.Failed(cfg, "203.0.113.10", "admin");
            now = now.AddMinutes(10).AddSeconds(1);
            Guard.Failed(cfg, "203.0.113.10", "admin");
            Assert.False(Guard.IsBlocked(cfg, "203.0.113.10"));
            // spraying: 3 names from one address pass, the 4th different name blocks
            foreach (var n in new[] { "office", "backup", "manager" }) Guard.Failed(cfg, "203.0.113.11", n);
            Assert.False(Guard.IsBlocked(cfg, "203.0.113.11"));
            Guard.Failed(cfg, "203.0.113.11", "sales");
            Assert.True(Guard.IsBlocked(cfg, "203.0.113.11"));
            // scanning: 29 refused requests pass, the 30th blocks
            for (int i = 0; i < 29; i++) Guard.Refused(cfg, "203.0.113.12");
            Assert.False(Guard.IsBlocked(cfg, "203.0.113.12"));
            Guard.Refused(cfg, "203.0.113.12");
            Assert.True(Guard.IsBlocked(cfg, "203.0.113.12"));
            // the block ends exactly at its time: 1 ms before it still holds
            var blockedAt = now;
            now = blockedAt.AddHours(24).AddMilliseconds(-1); Assert.True(Guard.IsBlocked(cfg, "203.0.113.12"));
            now = blockedAt.AddHours(24); Assert.False(Guard.IsBlocked(cfg, "203.0.113.12"));
            // the settings cannot weaken it below its floor (FAILS at least 3) nor be junk
            lock (cfg) { cfg.Doc.Root.Add(new XElement("GUARD", new XAttribute("FAILS", "1"), new XAttribute("PROBES", "nonsense"), new XAttribute("BLOCK_HOURS", "0"))); }
            var s = Guard.Read(cfg);
            Assert.Equal(3, s.Fails); Assert.Equal(30, s.Probes); Assert.Equal(1, s.BlockHours);
            // not an address: refused as the sender's mistake, nothing stored
            Assert.Equal(400, Status(() => Guard.Block(cfg, "../../etc/passwd", "x", "admin", 1)));
            Assert.DoesNotContain("passwd", File.ReadAllText(Path.Combine(cfg.SystemHome, "conf", "guard.xml")));
        }

        [Fact]
        public void Guard_ABlockSurvivesARestart_UnblockLetsItIn_AndItsCountStartsAgain()
        {
            var cfg = NewServer("restart"); Guard.Reset(); CatchAlerts(cfg);
            const string ip = "203.0.113.77";
            for (int i = 0; i < 10; i++) Guard.Failed(cfg, ip, "anna");
            Assert.True(Guard.IsBlocked(cfg, ip));
            // on the disk (oracle: the file itself)
            var onDisk = XDocument.Load(Path.Combine(cfg.SystemHome, "conf", "guard.xml")).Root.Elements("BLOCK").Single();
            Assert.Equal(ip, (string)onDisk.Attribute("IP"));
            Assert.Equal(RunId.UnixMs(now.AddHours(24)), (long)onDisk.Attribute("UNTIL"));
            // a restart: the memory is gone, the block is not
            Guard.Reset();
            Assert.True(Guard.IsBlocked(cfg, ip));
            // a second server in the same process is not affected by the first one's block
            var other = NewServer("other");
            Assert.False(Guard.IsBlocked(other, ip));
            // the administrator lets it in; it is not blocked again by its old count
            Assert.True(Guard.Unblock(cfg, ip, "admin", "10.0.0.1"));
            Assert.False(Guard.IsBlocked(cfg, ip));
            for (int i = 0; i < 9; i++) Guard.Failed(cfg, ip, "anna");
            Assert.False(Guard.IsBlocked(cfg, ip));
            Assert.Empty(XDocument.Load(Path.Combine(cfg.SystemHome, "conf", "guard.xml")).Root.Elements("BLOCK"));
            Assert.False(Guard.Unblock(cfg, ip, "admin", "10.0.0.1"));                           // nothing left to unblock
        }

        // ================================================================== AU-05: encryption

        // NIST SP 800-38A F.2.5 (CBC-AES256): key, IV, 4 plaintext blocks and their ciphertext (published). The fifth (PKCS7
        // padding) block's ciphertext and the HMAC-SHA256 tag were computed with OpenSSL and Python hmac, outside .NET.
        const string NistKey = "603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4";
        const string NistPlain = "6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e5130c81c46a35ce411e5fbc1191a0a52eff69f2445df4f9b17ad2b417be66c3710";
        const string NistCipher4 = "f58c4c04d6e5f1ba779eabfb5f7bfbd69cfc4e967edb808d679f777bc6702c7d39f23369a9d9bacfa530e26304231461b2eb05e2c39be9fcda6c19078c6a9d1b";
        const string KatBlob = "01000102030405060708090a0b0c0d0e0f" + NistCipher4 + "3f461796d6b0d6b2e0c2a72b4d80e644" + "685b9aa1987963e044ce733ba422ca32edf758ccab97299d4357ee1bd4117c07";
        static KeySet KatKey() { var raw = new byte[96]; Buffer.BlockCopy(H(NistKey), 0, raw, 0, 32); for (int i = 0; i < 32; i++) raw[32 + i] = (byte)(32 + i); return KeySet.FromRaw(raw); }

        [Fact]
        public void Encryption_KnownAnswers_Pbkdf2Rfc6070_AesNist_HmacCheckValue_NameSegments()
        {
            // RFC 6070: PBKDF2-HMAC-SHA1("password", "salt", 1) = 0c60c80f...; the key's first 20 bytes are that block
            Assert.Equal("0c60c80f961f0e71f3a9b524af6012062fe037a6", Hex(KeySet.Derive("password", Encoding.ASCII.GetBytes("salt"), 1).EncKey).Substring(0, 40));
            // a password key: all 96 bytes (Python hashlib.pbkdf2_hmac('sha1', 'Pa$$w0rd-Ü' as UTF-8, bytes 0..15, 1000, 96))
            var k = KeySet.Derive("Pa$$w0rd-Ü", Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(), 1000);
            Assert.Equal("fbb7492f2b3756ffac8295ef51dec9aa6c207212753b96777f3e9c6d6e2e72eb8f7e3a1fb274e65415c804549963cea4cfe4b91053f76f697833045632f375d82d83092273988c6f08653746d02b0cecbd961dac962d12a13b807f4616206b7b", Hex(k.ToRaw()));
            // the check value: HMAC-SHA256(mac key, "key-check-v1"), first 16 bytes (Python hmac)
            Assert.Equal("d684c20a3de84a34cab238ce5d718162", k.CheckValue());
            // server names: Base32(HMAC-SHA256(name key, parent + NUL + lower-case name)), 26 letters (Python hmac + base64.b32encode)
            Assert.Equal("4RDAEV4IJBK4HRYGDLGP4XUAIK/57RTXSQVYBN5GQN4ST35MMQNEZ", NameCipher.RelPath(k, @"\DATA\Report.XLSX"));
            // AES-256-CBC + HMAC: the published NIST ciphertext opens to the published plaintext
            Assert.Equal(NistPlain, Hex(Cipher.Decrypt(KatKey(), H(KatBlob))));
            // and what the product writes has the stated layout, and its first block is AES(key, P1 xor IV) — checked with plain AES-ECB
            var enc = Cipher.Encrypt(KatKey(), H(NistPlain));
            Assert.Equal(1 + 16 + 80 + 32, enc.Length); Assert.Equal(1, enc[0]);
            var x = new byte[16]; for (int i = 0; i < 16; i++) x[i] = (byte)(H(NistPlain)[i] ^ enc[1 + i]);
            using (var aes = Aes.Create()) { aes.Key = H(NistKey); Assert.Equal(enc.Skip(17).Take(16).ToArray(), aes.EncryptEcb(x, PaddingMode.None)); }
            using (var mac = new HMACSHA256(KatKey().MacKey)) Assert.Equal(mac.ComputeHash(enc, 0, enc.Length - 32), enc.Skip(enc.Length - 32).ToArray());
            Assert.Equal(NistPlain, Hex(Cipher.Decrypt(KatKey(), enc)));
        }

        [Fact]
        public void Encryption_EveryChangedByte_EveryCut_AndEveryOtherKey_IsRefused_NeverAWrongPlaintext()
        {
            var blob = H(KatBlob); var key = KatKey();
            // every bit of every byte: refused
            for (int i = 0; i < blob.Length; i++)
                for (int bit = 0; bit < 8; bit++)
                {
                    var t = (byte[])blob.Clone(); t[i] ^= (byte)(1 << bit);
                    Assert.Throws<CryptographicException>(() => Cipher.Decrypt(key, t));
                }
            // every shorter length, one byte more, nothing at all
            for (int n = 0; n < blob.Length; n++) Assert.Throws<CryptographicException>(() => Cipher.Decrypt(key, blob.Take(n).ToArray()));
            Assert.Throws<CryptographicException>(() => Cipher.Decrypt(key, blob.Concat(new byte[] { 0 }).ToArray()));
            Assert.Throws<CryptographicException>(() => Cipher.Decrypt(key, null));
            // a block moved (swapping two ciphertext blocks keeps the length): refused
            var swapped = (byte[])blob.Clone(); Buffer.BlockCopy(blob, 17, swapped, 33, 16); Buffer.BlockCopy(blob, 33, swapped, 17, 16);
            Assert.Throws<CryptographicException>(() => Cipher.Decrypt(key, swapped));
            // key B: another password, a random key, a key differing in one bit of any part — refused
            var salt = Bytes.Random(16);
            var a = KeySet.Derive("Customer-Pass-1", salt, 1000);
            var data = Encoding.UTF8.GetBytes("payroll 2026 — confidential");
            var encA = Cipher.Encrypt(a, data);
            Assert.Throws<CryptographicException>(() => Cipher.Decrypt(KeySet.Derive("Customer-Pass-2", salt, 1000), encA));
            Assert.Throws<CryptographicException>(() => Cipher.Decrypt(KeySet.Derive("Customer-Pass-1", Bytes.Random(16), 1000), encA));   // same password, other salt
            Assert.Throws<CryptographicException>(() => Cipher.Decrypt(KeySet.Random(), encA));
            foreach (var at in new[] { 32, 63 })   // one bit in the authentication part of the key
            {
                var raw = a.ToRaw(); raw[at] ^= 1;
                Assert.Throws<CryptographicException>(() => Cipher.Decrypt(KeySet.FromRaw(raw), encA));
            }
            // the check value tells a wrong key apart before anything is read
            Assert.NotEqual(a.CheckValue(), KeySet.Derive("Customer-Pass-2", salt, 1000).CheckValue());
            Assert.Equal(data, Cipher.Decrypt(a, encA));
            // key material of a wrong size is refused, not padded
            Assert.Throws<ArgumentException>(() => KeySet.FromRaw(new byte[95]));
            Assert.Throws<ArgumentException>(() => KeySet.FromRaw(null));
        }

        [Fact]
        public void Encryption_Sizes_EmptyOneByteBlockEdgesAndOneMegabyte_RoundTripExactly()
        {
            var k = KeySet.Random();
            var rnd = new Random(2026);
            foreach (var n in new[] { 0, 1, 15, 16, 17, 31, 32, 33, 1024 * 1024 })
            {
                var data = new byte[n]; rnd.NextBytes(data);
                var enc = Cipher.Encrypt(k, data);
                Assert.Equal(1 + 16 + (n / 16 + 1) * 16 + 32, enc.Length);                       // PKCS7: always one more block at a block edge
                var back = Cipher.Decrypt(k, enc);
                Assert.Equal(Hex(SHA256.Create().ComputeHash(data)), Hex(SHA256.Create().ComputeHash(back)));
                Assert.NotEqual(enc, Cipher.Encrypt(k, data));                                   // a fresh IV every time
            }
            // the empty password is still a key (not a crash), and differs from a one-space password
            var salt = Bytes.Random(16);
            Assert.NotEqual(KeySet.Derive("", salt, 1000).CheckValue(), KeySet.Derive(" ", salt, 1000).CheckValue());
        }

        /// <summary>A backup object written with the key of a set, as on the first computer.</summary>
        static byte[] WriteObject(KeySet k, IList<byte[]> chunks, out string sha)
        {
            var ms = new MemoryStream();
            var w = new BackupObject.Writer(ms, k);
            foreach (var c in chunks) w.AddChunk(BackupObject.ChunkId(k, c), c);
            w.Finish(new Msg().Set("path", @"C:\Data\payroll.xlsx").Set("size", chunks.Sum(c => c.Length)));
            sha = w.Sha256;
            return ms.ToArray();
        }

        static byte[] ReadObject(KeySet k, byte[] obj)
        {
            using (var s = new MemoryStream(obj))
            {
                var header = BackupObject.ReadHeader(s, k);
                return BackupObject.ReadChunks(s, k, header).SelectMany(c => c.Value).ToArray();
            }
        }

        [Fact]
        public void KeyRecovery_PasswordCustomAndRandomKeys_OnANewComputer_ReadTheSameBytes_TheWrongOneReadsNothing()
        {
            var rnd = new Random(7);
            var chunks = Enumerable.Range(0, 3).Select(i => { var b = new byte[70000 + i]; rnd.NextBytes(b); return b; }).ToList();
            var original = chunks.SelectMany(c => c).ToArray();
            var wantSha = Hex(SHA256.Create().ComputeHash(original));
            var salt = Bytes.Random(16);

            // PASSWORD and CUSTOM keys: nothing kept on the new computer — the key is derived again from the secret and the stored salt
            foreach (var secret in new[] { "Customer-Pass-1", "my own custom key ✓ 2026" })
            {
                var first = KeySet.Derive(secret, salt);
                var check = first.CheckValue(); string objSha;
                var obj = WriteObject(first, chunks, out objSha);
                Assert.Equal(Hex(SHA256.Create().ComputeHash(obj)), objSha);                      // what was sent is what the writer hashed
                var again = KeySet.Derive(secret, salt);
                Assert.Equal(check, again.CheckValue());
                Assert.Equal(wantSha, Hex(SHA256.Create().ComputeHash(ReadObject(again, obj))));
                var wrong = KeySet.Derive(secret + "x", salt);
                Assert.NotEqual(check, wrong.CheckValue());
                Assert.ThrowsAny<Exception>(() => ReadObject(wrong, obj));
                Assert.DoesNotContain(secret, Encoding.UTF8.GetString(obj));
            }
            // RANDOM (DEFAULT) key: only the 96 bytes kept by the provider (key recovery) open it
            var random = KeySet.Random(); string rs;
            var robj = WriteObject(random, chunks, out rs);
            var recovered = KeySet.FromRaw(Convert.FromBase64String(Convert.ToBase64String(random.ToRaw())));
            Assert.Equal(random.CheckValue(), recovered.CheckValue());
            Assert.Equal(wantSha, Hex(SHA256.Create().ComputeHash(ReadObject(recovered, robj))));
            Assert.ThrowsAny<Exception>(() => ReadObject(KeySet.Random(), robj));
        }

        [Fact]
        public void ABackupObject_WithAChangedByteAnywhere_IsRefused_NeverRestoredWrong()
        {
            var k = KeySet.Random();
            var rnd = new Random(11);
            var chunks = Enumerable.Range(0, 2).Select(i => { var b = new byte[3000]; rnd.NextBytes(b); return b; }).ToList();
            string sha; var obj = WriteObject(k, chunks, out sha);
            var original = chunks.SelectMany(c => c).ToArray();
            Assert.Equal(original, ReadObject(k, obj));
            int refused = 0;
            for (int i = 0; i < obj.Length; i += 7)                                              // every 7th byte over the whole object
            {
                var t = (byte[])obj.Clone(); t[i] ^= 0x40;
                byte[] got = null;
                try { got = ReadObject(k, t); } catch (Exception) { refused++; continue; }
                Assert.Equal(original, got);   // only acceptable non-refusal: a byte the reader never uses, giving the exact original
            }
            Assert.True(refused > obj.Length / 7 - 4, "almost every changed byte is refused: " + refused);
            // a key damaged only in its encryption part (its check value still matches): the object is refused, never read wrong
            foreach (var at in new[] { 0, 31 })
            {
                var raw = k.ToRaw(); raw[at] ^= 1; var damaged = KeySet.FromRaw(raw);
                Assert.Equal(k.CheckValue(), damaged.CheckValue());
                Assert.ThrowsAny<Exception>(() => ReadObject(damaged, obj));
            }
        }

        // ================================================================== AU-06: customers and resellers each see only their own

        [Fact]
        public void Resellers_EachWithinItsOwnLimits_AnotherResellersCustomersNeverCount()
        {
            var cfg = NewServer("vnd"); var users = new Users(cfg);
            Vendors.Save(cfg, new Msg().Set("id", "acme-it").Set("name", "Acme IT").Set("maxUsers", 2).Set("maxQuotaGB", "3"));
            Vendors.Save(cfg, new Msg().Set("id", "beta").Set("name", "Beta").Set("maxUsers", 1));
            const long GB = 1024L * 1024 * 1024;
            Action<string, string, long?> create = (vendor, login, quota) =>
            {
                Vendors.CheckLimits(cfg, users, vendor, quota);
                users.Create(login, "Customer-Pass-1", login, quota, "COMPRESSED", login + "@example.invalid", "10.0.0.1");
                var p = users.LoadProfile(login); p.SetAttr("OWNER", vendor); users.SaveProfile(login, p);
            };
            create("beta", "beta-c1", null);
            create("acme-it", "acme-c1", 1 * GB);
            Assert.Equal("QUOTA_REQUIRED", Code(() => Vendors.CheckLimits(cfg, users, "acme-it", null)));
            // exactly the room left (3 GB in all) is accepted, one byte more is not
            Assert.Equal("VENDOR_QUOTA", Code(() => Vendors.CheckLimits(cfg, users, "acme-it", 2 * GB + 1)));
            create("acme-it", "acme-c2", 2 * GB);
            // two users is acme's plan: the third is refused, while beta's customer never counted for acme
            Assert.Equal("VENDOR_LIMIT", Code(() => Vendors.CheckLimits(cfg, users, "acme-it", 1)));
            Assert.Equal("VENDOR_LIMIT", Code(() => Vendors.CheckLimits(cfg, users, "beta", null)));
            Assert.Equal(404, Status(() => Vendors.CheckLimits(cfg, users, "nobody", 1)));
            Vendors.Save(cfg, new Msg().Set("id", "acme-it").Set("disabled", "1"));
            Assert.Equal("VENDOR_DISABLED", Code(() => Vendors.CheckLimits(cfg, users, "acme-it", 1)));
            // whose is whose (oracle: the profile files)
            Assert.Equal(new[] { "acme-c1", "acme-c2" }, users.Logins().Where(l => users.LoadProfile(l).Get("OWNER") == "acme-it").OrderBy(l => l).ToArray());
            Assert.Equal(new[] { "beta-c1" }, users.Logins().Where(l => users.LoadProfile(l).Get("OWNER") == "beta").ToArray());
            // a reseller id that is a path or junk is refused
            Assert.Equal("VENDOR_ID", Code(() => Vendors.Save(cfg, new Msg().Set("id", "../admin"))));
            Assert.Equal("VENDOR_ID", Code(() => Vendors.Save(cfg, new Msg().Set("id", ""))));
        }

        [Fact]
        public void ResellerAdministrators_AreTheirResellersOnly_AndLoseTheSignInAtOnceWhenTheResellerIsDisabled()
        {
            var cfg = NewServer("vadm"); var users = new Users(cfg);
            Vendors.Save(cfg, new Msg().Set("id", "acme-it")); Vendors.Save(cfg, new Msg().Set("id", "beta"));
            Vendors.SaveAdmin(cfg, "acme-it", "acme-admin", "Acme-Admin-Pass-1");
            Assert.Equal(409, Status(() => Vendors.SaveAdmin(cfg, "beta", "ACME-ADMIN", "Another-Pass-11")));   // a name is unique on the server
            Assert.Equal(409, Status(() => Vendors.SaveAdmin(cfg, "beta", "admin", "Another-Pass-11")));        // and never the system administrator's
            var si = Staff.Check(cfg, "acme-admin", "Acme-Admin-Pass-1", null, "203.0.113.5", now);
            Assert.Equal("acme-it", si.Account.Vendor);
            var token = users.NewStaffSession(si);
            Assert.Equal("acme-it", users.GetSession(token).Vendor);
            var sys = users.NewStaffSession(Staff.Check(cfg, "admin", "Admin-Pass-1", null, "203.0.113.5", now));
            Assert.Equal("", users.GetSession(sys).Vendor);                                      // the system administrator: no reseller filter
            Vendors.Save(cfg, new Msg().Set("id", "acme-it").Set("disabled", "1"));
            Assert.Null(users.GetSession(token));                                                // at once, not at the session's end
            Assert.Equal(401, Status(() => Staff.Check(cfg, "acme-admin", "Acme-Admin-Pass-1", null, "203.0.113.5", now)));
            Assert.NotNull(users.GetSession(sys));
        }

        [Fact]
        public void Customers_ADeviceTokenOrSessionOpensOnlyItsOwnAccount()
        {
            var cfg = NewServer("iso"); var users = new Users(cfg);
            users.Create("anna", "Anna-Pass-1", "Anna", null, "COMPRESSED", "anna@example.com", "203.0.113.5");
            users.Create("bob", "Bob-Pass-111", "Bob", null, "COMPRESSED", "bob@example.com", "203.0.113.5");
            var a = users.RegisterDevice("anna", "PC-A", "203.0.113.5").Split('.'); now = now.AddSeconds(1);
            var b = users.RegisterDevice("bob", "PC-B", "203.0.113.5").Split('.');
            // anna's id and secret under bob's name, and bob's under anna's: refused
            Assert.Equal(401, Status(() => users.CheckDevice(b[0] + "." + a[1] + "." + a[2], "203.0.113.5")));
            Assert.Equal(401, Status(() => users.CheckDevice(a[0] + "." + b[1] + "." + b[2], "203.0.113.5")));
            Assert.Equal("anna", users.CheckDevice(string.Join(".", a), "203.0.113.5"));
            Assert.Equal("bob", users.CheckDevice(string.Join(".", b), "203.0.113.5"));
            // a password opens only its own account
            Assert.Equal(401, Status(() => users.CheckUser("bob", "Anna-Pass-1", null, "203.0.113.5")));
            var s = users.NewSession("anna", false);
            Assert.Equal("anna", users.GetSession(s).Login);
            Assert.False(users.GetSession(s).Admin);
        }
    }
}
