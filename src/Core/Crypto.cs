using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OnlineBackup.Core
{
    /// <summary>
    /// The three keys of one backup set. Content, and the names and paths inside the stored objects, are encrypted on the
    /// client before anything leaves it. Owner decision S-1 (2026-10-08): the run logs the server keeps list file names in
    /// plain text (support, reports, the AI explanation) - the product does not claim that file names are encrypted.
    /// The key itself reaches the server only as the key-recovery copy the customer agreed to (owner decision A7).
    /// </summary>
    public sealed class KeySet
    {
        public const int Iterations = 200000;
        public byte[] EncKey { get; private set; }   // AES-256
        public byte[] MacKey { get; private set; }   // HMAC-SHA256 (authentication + chunk identity)
        public byte[] NameKey { get; private set; }  // HMAC-SHA256 (deterministic folder/file names on the server)

        KeySet() { }

        /// <summary>Key type "user password" (Ahsay KEY_TYPE): derived from the password at set creation.</summary>
        public static KeySet Derive(string password, byte[] salt, int iterations = Iterations)
        {
            using (var kdf = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(password ?? ""), salt, iterations))
                return FromRaw(kdf.GetBytes(96));
        }

        public static KeySet FromRaw(byte[] raw)
        {
            if (raw == null || raw.Length != 96) throw new ArgumentException("Key material must be 96 bytes.");
            var k = new KeySet { EncKey = new byte[32], MacKey = new byte[32], NameKey = new byte[32] };
            Buffer.BlockCopy(raw, 0, k.EncKey, 0, 32);
            Buffer.BlockCopy(raw, 32, k.MacKey, 0, 32);
            Buffer.BlockCopy(raw, 64, k.NameKey, 0, 32);
            return k;
        }

        public byte[] ToRaw()
        {
            var raw = new byte[96];
            Buffer.BlockCopy(EncKey, 0, raw, 0, 32);
            Buffer.BlockCopy(MacKey, 0, raw, 32, 32);
            Buffer.BlockCopy(NameKey, 0, raw, 64, 32);
            return raw;
        }

        public static KeySet Random() { return FromRaw(Bytes.Random(96)); }

        /// <summary>Stored in the profile (ENCRYPTING_KEY KEY): lets the agent tell a wrong key from a damaged backup. Reveals nothing about the key.</summary>
        public string CheckValue() { return Bytes.Hex(Bytes.Hmac(MacKey, Encoding.UTF8.GetBytes("key-check-v1")), 16); }
    }

    /// <summary>
    /// Authenticated encryption available on .NET 4.0 without third-party code: AES-256-CBC, then HMAC-SHA256 over
    /// version + IV + ciphertext (encrypt-then-MAC). Any changed byte is detected before decryption.
    /// Layout: [1 version][16 IV][ciphertext][32 tag].
    /// </summary>
    public static class Cipher
    {
        const byte Version = 1;

        public static byte[] Encrypt(KeySet k, byte[] plain)
        {
            byte[] iv = Bytes.Random(16), ct;
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                using (var enc = aes.CreateEncryptor(k.EncKey, iv)) ct = enc.TransformFinalBlock(plain, 0, plain.Length);
            }
            var outp = new byte[1 + 16 + ct.Length + 32];
            outp[0] = Version;
            Buffer.BlockCopy(iv, 0, outp, 1, 16);
            Buffer.BlockCopy(ct, 0, outp, 17, ct.Length);
            var tag = Bytes.Hmac(k.MacKey, outp, 0, 17 + ct.Length);
            Buffer.BlockCopy(tag, 0, outp, 17 + ct.Length, 32);
            return outp;
        }

        public static byte[] Decrypt(KeySet k, byte[] data)
        {
            if (data == null || data.Length < 1 + 16 + 16 + 32 || data[0] != Version) throw new CryptographicException("Damaged or unknown encrypted block.");
            int ctLen = data.Length - 49;
            var tag = Bytes.Hmac(k.MacKey, data, 0, 17 + ctLen);
            if (!Bytes.FixedEquals(tag, 0, data, 17 + ctLen, 32)) throw new CryptographicException("Authentication failed: wrong key or damaged data.");
            var iv = new byte[16];
            Buffer.BlockCopy(data, 1, iv, 0, 16);
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                using (var dec = aes.CreateDecryptor(k.EncKey, iv)) return dec.TransformFinalBlock(data, 17, ctLen);
            }
        }
    }

    /// <summary>
    /// Server-side names. Each folder and file name becomes letters and digits derived from the name key, so the
    /// server keeps the same tree depth as the source (like Ahsay v6 Current\xxxx\xxxx) without knowing any name.
    /// Deterministic: the same path always lands in the same place. Case-insensitive like Windows.
    /// </summary>
    public static class NameCipher
    {
        public static string Segment(KeySet k, string parentRel, string name)
        {
            var mac = Bytes.Hmac(k.NameKey, Encoding.UTF8.GetBytes((parentRel ?? "") + "\u0000" + name.ToLowerInvariant()));
            return Base32.Encode(mac).Substring(0, 26);
        }

        /// <summary>"C:\Data\a.txt" → "K7.../Q2.../M9..." (forward slashes; the server maps them to its own separator).</summary>
        public static string RelPath(KeySet k, string fullPath)
        {
            var parts = SplitPath(fullPath);
            string rel = "";
            foreach (var p in parts) rel = rel.Length == 0 ? Segment(k, "", p) : rel + "/" + Segment(k, rel, p);
            return rel;
        }

        public static string[] SplitPath(string fullPath)
        {
            return fullPath.Replace('/', '\\').Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>The real path, encrypted (random IV) — kept in the .chk file so a restore can show and rebuild names.</summary>
        public static string EncryptPath(KeySet k, string fullPath) { return Convert.ToBase64String(Cipher.Encrypt(k, Encoding.UTF8.GetBytes(fullPath))); }
        public static string DecryptPath(KeySet k, string enc) { return Encoding.UTF8.GetString(Cipher.Decrypt(k, Convert.FromBase64String(enc))); }
    }

    public static class Bytes
    {
        static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        public static byte[] Random(int n) { var b = new byte[n]; lock (Rng) Rng.GetBytes(b); return b; }

        public static byte[] Hmac(byte[] key, byte[] data) { return Hmac(key, data, 0, data.Length); }
        public static byte[] Hmac(byte[] key, byte[] data, int off, int len) { using (var h = new HMACSHA256(key)) return h.ComputeHash(data, off, len); }

        public static byte[] Sha256(byte[] data) { using (var h = SHA256.Create()) return h.ComputeHash(data); }
        public static string Sha256Hex(Stream s) { using (var h = SHA256.Create()) return Hex(h.ComputeHash(s)); }

        public static string Hex(byte[] b) { return Hex(b, b.Length); }
        public static string Hex(byte[] b, int n)
        {
            var sb = new StringBuilder(n * 2);
            for (int i = 0; i < n; i++) sb.Append(b[i].ToString("x2"));
            return sb.ToString();
        }

        public static bool FixedEquals(byte[] a, int ao, byte[] b, int bo, int n)
        {
            int d = 0;
            for (int i = 0; i < n; i++) d |= a[ao + i] ^ b[bo + i];
            return d == 0;
        }
    }

    public static class Base32
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        public static string Encode(byte[] data)
        {
            var sb = new StringBuilder();
            int buffer = 0, bits = 0;
            foreach (var b in data)
            {
                buffer = (buffer << 8) | b; bits += 8;
                while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
            }
            if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
            return sb.ToString();
        }

        public static byte[] Decode(string s)
        {
            s = s.Trim().TrimEnd('=').ToUpperInvariant().Replace(" ", "");
            var outp = new MemoryStream();
            int buffer = 0, bits = 0;
            foreach (var c in s)
            {
                int v = Alphabet.IndexOf(c);
                if (v < 0) throw new FormatException("Invalid base32 text.");
                buffer = (buffer << 5) | v; bits += 5;
                if (bits >= 8) { outp.WriteByte((byte)(buffer >> (bits - 8))); bits -= 8; }
            }
            return outp.ToArray();
        }
    }

    /// <summary>Passwords are stored only as PBKDF2 hashes: "pbkdf2$iterations$salt$hash".</summary>
    public static class PasswordHash
    {
        public static string Create(string password, int iterations = 100000)
        {
            var salt = Bytes.Random(16);
            using (var kdf = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(password), salt, iterations))
                return "pbkdf2$" + iterations + "$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(kdf.GetBytes(32));
        }

        public static bool Verify(string password, string stored)
        {
            if (string.IsNullOrEmpty(stored) || password == null) return false;
            var p = stored.Split('$');
            if (p.Length != 4 || p[0] != "pbkdf2") return false;
            int it;
            if (!int.TryParse(p[1], out it)) return false;
            var salt = Convert.FromBase64String(p[2]); var want = Convert.FromBase64String(p[3]);
            using (var kdf = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(password), salt, it))
                return Bytes.FixedEquals(kdf.GetBytes(32), 0, want, 0, want.Length);
        }
    }

    /// <summary>RFC 6238 time-based one-time codes (Microsoft / Google Authenticator), 6 digits, 30 seconds, ±1 step.</summary>
    public static class Totp
    {
        public static string NewSecret() { return Base32.Encode(Bytes.Random(20)); }

        public static string Code(string secret, DateTime utc)
        {
            long step = (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds / 30;
            return CodeAt(Base32.Decode(secret), step);
        }

        public static bool Verify(string secret, string code, DateTime utc) { return Step(secret, code, utc) >= 0; }

        /// <summary>The 30-second step the code belongs to (the step before and after are accepted for clock drift), -1 = wrong.
        /// Agent H (H-02): the caller accepts a step only once — RFC 6238 §5.2: a code seen by someone else (over the
        /// shoulder, a proxy log, a phishing page) must not open a second sign-in within its 90 seconds.</summary>
        public static long Step(string secret, string code, DateTime utc)
        {
            if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(code)) return -1;
            var key = Base32.Decode(secret);
            long step = (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds / 30;
            for (long s = step - 1; s <= step + 1; s++) if (CodeAt(key, s) == code.Trim()) return s;
            return -1;
        }

        static string CodeAt(byte[] key, long step)
        {
            var msg = BitConverter.GetBytes(step);
            if (BitConverter.IsLittleEndian) Array.Reverse(msg);
            byte[] h;
            using (var hmac = new HMACSHA1(key)) h = hmac.ComputeHash(msg);
            int o = h[h.Length - 1] & 15;
            int bin = ((h[o] & 0x7f) << 24) | (h[o + 1] << 16) | (h[o + 2] << 8) | h[o + 3];
            return (bin % 1000000).ToString("D6");
        }
    }
}
