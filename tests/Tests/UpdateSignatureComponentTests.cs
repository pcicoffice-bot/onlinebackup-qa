using System;
using System.Security.Cryptography;
using System.Text;
using OnlineBackup.Server;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// The update signature alone — component contract (tests/QA/specs.py IN-04): only a version the vendor signed is
    /// installed. Input: "OBUPDATE|version|sha256" signed with the vendor's key; the same with one character changed in the
    /// version, in the SHA-256, or in the signature; another key's signature; an empty or broken signature. Expected: the
    /// first is accepted, every other one refused — never an exception that an installer could take for "no answer".
    /// </summary>
    public class UpdateSignatureComponentTests
    {
        static byte[] Sign(string privateKey, string text)
        {
            using (var ec = ECDsa.Create()) { ec.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey), out _); return ec.SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256); }
        }

        [Fact]
        public void OnlyTheVendorsSignatureOfThisExactVersionAndFile_IsAccepted()
        {
            var vendor = Env.TestKey;                        // the public half is the one the server trusts in the tests
            var sha = new string('a', 64); var msg = "OBUPDATE|2.4.0|" + sha;
            var sig = Sign(vendor[0], msg);
            Assert.True(License.VerifyCenter(Encoding.UTF8.GetBytes(msg), sig));
            Assert.False(License.VerifyCenter(Encoding.UTF8.GetBytes("OBUPDATE|2.4.1|" + sha), sig));                 // another version
            Assert.False(License.VerifyCenter(Encoding.UTF8.GetBytes("OBUPDATE|2.4.0|" + new string('b', 64)), sig));  // another file
            var bad = (byte[])sig.Clone(); bad[bad.Length / 2] ^= 1;
            Assert.False(License.VerifyCenter(Encoding.UTF8.GetBytes(msg), bad));                                      // a changed signature
            Assert.False(License.VerifyCenter(Encoding.UTF8.GetBytes(msg), Sign(License.KeyGen()[0], msg)));           // another key
            Assert.False(License.VerifyCenter(Encoding.UTF8.GetBytes(msg), new byte[0]));                              // none
            Assert.False(License.VerifyCenter(Encoding.UTF8.GetBytes(msg), new byte[] { 1, 2, 3 }));                   // broken
        }
    }
}
