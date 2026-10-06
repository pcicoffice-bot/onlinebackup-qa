using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using OnlineBackup.Agent;
using OnlineBackup.Core;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// QA agent H — component layer: AU-01 sign-in timing (does the answer time tell which names exist), AU-05 keys
    /// (password / random / custom key: wrong key, check value, any changed byte refused), AG-07 the certificate pin of the
    /// system TLS path.
    /// </summary>
    public class AuditH_ComponentTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "obaudh-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public void Dispose() { try { Directory.Delete(root, true); } catch (Exception) { } }

        // ------------------------------------------------------------------ AU-01 timing oracle

        [Fact]
        public void SignIn_UnknownName_TakesAboutAsLongAsAWrongPassword_NoTimingOracle()
        {
            var cfg = SystemConfig.Init(Path.Combine(root, "system"), "admin", "Admin-Pass-1", "localhost", new[] { Path.Combine(root, "home") + "|UNLIMITED|100" });
            var users = new Users(cfg);
            users.Create("anna", "Anna-Pass-1", "Anna", null, "COMPRESSED", "anna@example.com", "203.0.113.5");
            var p = users.LoadProfile("anna"); p.SetAttr("LOCK_ATTEMPTS", 10); users.SaveProfile("anna", p);   // 10 is the most allowed: no lock below
            Func<string, double> time = login =>
            {
                var sw = Stopwatch.StartNew();
                try { users.CheckUser(login, "Wrong-Pass-9", null, "203.0.113.5"); } catch (ApiException) { }
                return sw.Elapsed.TotalMilliseconds;
            };
            time("anna"); time("nobody");                                                      // warm up
            var known = Enumerable.Range(0, 4).Select(_ => time("anna")).OrderBy(x => x).ElementAt(1);
            var unknown = Enumerable.Range(0, 4).Select(_ => time("nobody")).OrderBy(x => x).ElementAt(1);
            Assert.True(known > 5, "control: the wrong password costs a key derivation (" + known.ToString("0.0") + " ms)");
            Assert.True(unknown * 3 >= known, "an unknown name answers in " + unknown.ToString("0.00") + " ms, a known name with a wrong password in " + known.ToString("0.0") + " ms: the time tells which names exist");
        }

        // ------------------------------------------------------------------ AU-05 keys

        [Fact]
        public void Keys_PasswordRandomAndCustom_WrongKeyRefused_CheckValueDiffers_EveryChangedByteDetected()
        {
            // coverage (AU-05, component): the three key types through the same authenticated cipher
            var salt = Bytes.Random(16);
            var byPassword = KeySet.Derive("Key-Pass-1", salt, 1000);
            var samePassword = KeySet.Derive("Key-Pass-1", salt, 1000);
            var otherPassword = KeySet.Derive("Key-Pass-2", salt, 1000);
            var random = KeySet.Random();
            var custom = KeySet.FromRaw(Enumerable.Range(0, 96).Select(i => (byte)(i * 3 + 1)).ToArray());
            Assert.Equal(byPassword.CheckValue(), samePassword.CheckValue());
            var checks = new[] { byPassword, otherPassword, random, custom }.Select(k => k.CheckValue()).ToList();
            Assert.Equal(4, checks.Distinct().Count());
            Assert.Throws<ArgumentException>(() => KeySet.FromRaw(new byte[95]));

            var plain = System.Text.Encoding.UTF8.GetBytes("ledger 2026: 1,234,567.89");
            foreach (var k in new[] { byPassword, random, custom })
            {
                var ct = Cipher.Encrypt(k, plain);
                Assert.Equal(plain, Cipher.Decrypt(k, ct));
                Assert.DoesNotContain("ledger", System.Text.Encoding.UTF8.GetString(ct));
                foreach (var wrong in new[] { otherPassword, KeySet.Random() }) Assert.ThrowsAny<CryptographicException>(() => Cipher.Decrypt(wrong, ct));
                for (int i = 0; i < ct.Length; i++)                                              // every single changed byte, header to tag
                {
                    var t = (byte[])ct.Clone(); t[i] ^= 0x01;
                    Assert.ThrowsAny<CryptographicException>(() => Cipher.Decrypt(k, t));
                }
                Assert.ThrowsAny<CryptographicException>(() => Cipher.Decrypt(k, ct.Take(ct.Length - 1).ToArray()));   // cut short
                Assert.ThrowsAny<CryptographicException>(() => Cipher.Decrypt(k, ct.Concat(new byte[16]).ToArray()));   // grown
            }
            // a backup object written with a custom key cannot be read with the default (password) key of the same set
            var ms = new MemoryStream();
            var w = new BackupObject.Writer(ms, custom); w.AddChunk(BackupObject.ChunkId(custom, plain), plain); w.Finish(new Msg());
            ms.Position = 0;
            Assert.ThrowsAny<Exception>(() => { var hd = BackupObject.ReadHeader(ms, byPassword); BackupObject.ReadChunks(ms, byPassword, hd).ToList(); });
            ms.Position = 0; var okHead = BackupObject.ReadHeader(ms, custom); Assert.Equal(plain, BackupObject.ReadChunks(ms, custom, okHead).Single().Value);   // control: the right key reads it
        }

        // ------------------------------------------------------------------ AG-07 pin on the system TLS path

        [Fact]
        public void SystemTls_WithAPin_RefusesAnotherCertificate_EvenOneTheSystemTrusts()
        {
            // the IT company pinned its own certificate at installation; a different certificate for the same host — one the
            // computer trusts (a company TLS-inspection proxy, a mis-issued certificate) — must still be refused, as the
            // built-in TLS 1.2 path does (BuiltinTls: a pin is compared and nothing else)
            X509Certificate2 pinned, other;
            using (var rsa = RSA.Create(2048)) using (var c = new CertificateRequest("CN=backup.example.invalid", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30))) pinned = new X509Certificate2(c.RawData);
            using (var rsa = RSA.Create(2048)) using (var c = new CertificateRequest("CN=backup.example.invalid", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30))) other = new X509Certificate2(c.RawData);
            var pin = Bytes.Hex(Bytes.Sha256(pinned.RawData));
            new Client("https://backup.example.invalid:8443", pin);                             // installs the process-wide check with this pin
            var check = ServicePointManager.ServerCertificateValidationCallback;
            Assert.NotNull(check);
            Assert.True(check(null, pinned, null, SslPolicyErrors.RemoteCertificateChainErrors), "control: the pinned self-signed certificate is accepted");
            Assert.False(check(null, other, null, SslPolicyErrors.RemoteCertificateChainErrors), "control: an untrusted other certificate is refused");
            Assert.False(check(null, other, null, SslPolicyErrors.None), "a certificate that is not the pinned one is accepted because the system trusts it");
        }
    }
}
