using System.Linq;

namespace OnlineBackup.Core
{
    /// <summary>SEC-030: one password rule everywhere (owner's decision): at least 8 characters, with at least one letter.</summary>
    public static class Passwords
    {
        public const int MinLength = 8;
        public const string Rule = "Password: at least 8 characters, with at least one letter.";
        public static bool Ok(string pw) { return pw != null && pw.Length >= MinLength && pw.Any(char.IsLetter); }
    }
}
