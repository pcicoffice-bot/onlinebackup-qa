using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>The suite itself: no test may end early as Passed. A "return" at the level of a [Fact]/[Theory] method (not inside a
    /// lambda, delegate, local function or nested type) makes xUnit report Passed although the test's main part did not run -
    /// 76 such places were found (owner, 07.10: a missing precondition is never PASS); each is now
    /// "throw NotTested.Because(reason)". This test keeps it so: it reads every test file and names any such return.</summary>
    public class NoSilentPassTests
    {
        static readonly Regex Control = new Regex(@"^(if|else|else if|for|foreach|while|do|using|try|catch|finally|lock|switch|checked|unchecked|unsafe|fixed)\b|^(case\b.*|default\s*):$|^$", RegexOptions.Compiled);

        /// <summary>Strings, characters and comments blanked (same length), so braces and words inside them do not count.</summary>
        static string Strip(string s)
        {
            var o = s.ToCharArray(); int n = s.Length;
            for (int i = 0; i < n; )
            {
                if (i + 1 < n && s[i] == '/' && s[i + 1] == '/') { int j = s.IndexOf('\n', i); if (j < 0) j = n; for (int k = i; k < j; k++) o[k] = ' '; i = j; continue; }
                if (i + 1 < n && s[i] == '/' && s[i + 1] == '*') { int j = s.IndexOf("*/", i + 2, StringComparison.Ordinal); j = j < 0 ? n : j + 2; for (int k = i; k < j; k++) if (o[k] != '\n') o[k] = ' '; i = j; continue; }
                if (s[i] == '@' && i + 1 < n && s[i + 1] == '"')
                {
                    int j = i + 2;
                    while (j < n) { if (s[j] == '"' && j + 1 < n && s[j + 1] == '"') { j += 2; continue; } if (s[j] == '"') break; j++; }
                    for (int k = i + 1; k < Math.Min(j, n); k++) if (o[k] != '\n') o[k] = ' ';
                    i = j + 1; continue;
                }
                if (s[i] == '"' || s[i] == '\'')
                {
                    char q = s[i]; int j = i + 1;
                    while (j < n && s[j] != q) j += s[j] == '\\' ? 2 : 1;
                    for (int k = i + 1; k < Math.Min(j, n); k++) o[k] = ' ';
                    i = j + 1; continue;
                }
                i++;
            }
            return new string(o);
        }

        internal static List<string> EarlyPassedReturns(string file, string raw)
        {
            var found = new List<string>();
            var src = Strip(raw);
            foreach (Match m in Regex.Matches(src, @"\[(Fact|Theory)\b[^\]]*\](\s*\[[^\]]*\])*\s*public\s+(async\s+)?[\w<>]+\s+(\w+)\s*\("))
            {
                int b = src.IndexOf('{', m.Index + m.Length);
                var stack = new List<string>();
                for (int i = b; i < src.Length; i++)
                {
                    char c = src[i];
                    if (c == '{')
                    {
                        int k = i - 1; while (k > 0 && ";{}".IndexOf(src[k]) < 0) k--;
                        stack.Add(Regex.Replace(src.Substring(k + 1, i - k - 1), @"\s+", " ").Trim());
                    }
                    else if (c == '}') { stack.RemoveAt(stack.Count - 1); if (stack.Count == 0) break; }
                    else if (string.CompareOrdinal(src, i, "return", 0, 6) == 0 && (i == 0 || !(char.IsLetterOrDigit(src[i - 1]) || src[i - 1] == '_'))
                             && i + 6 < src.Length && !(char.IsLetterOrDigit(src[i + 6]) || src[i + 6] == '_'))
                    {
                        // inside the method itself: every enclosing block below the method's own is plain control flow
                        bool inMethod = stack.Skip(1).All(o => !o.Contains("=>") && !o.Contains("delegate") && (Control.IsMatch(StripCondition(o)) || o == "else" || o == "try" || o == "finally" || o == "do"));
                        if (inMethod)
                        {
                            int line = raw.Take(i).Count(ch => ch == '\n') + 1;
                            found.Add(file + ":" + line + " " + m.Groups[4].Value + "  " + raw.Split('\n')[line - 1].Trim());
                        }
                    }
                }
            }
            return found;
        }

        /// <summary>"if (a && (b)) " -> "if": the opener's keyword is what decides.</summary>
        static string StripCondition(string opener)
        {
            var kw = Regex.Match(opener, @"^(else if|if|for|foreach|while|using|lock|switch|catch|fixed|checked|unchecked|unsafe)\b");
            if (kw.Success) return kw.Value;
            return opener;
        }

        static string TestsFolder()
        {
            // OB_SOURCES_TO_CHECK: another copy of the test sources (the proof that the checker fails on the old ones)
            var other = Environment.GetEnvironmentVariable("OB_SOURCES_TO_CHECK");
            if (!string.IsNullOrEmpty(other)) return other;
            var root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "tests", "Tests", "Tests.csproj"))) root = Path.GetDirectoryName(root);
            Assert.True(root != null, "the test sources were not found from " + AppContext.BaseDirectory);
            return Path.Combine(root, "tests", "Tests");
        }

        [Fact]
        public void NoTestCanEndEarly_AsPassed()
        {
            var dir = TestsFolder();
            var files = Directory.GetFiles(dir, "*.cs").OrderBy(f => f, StringComparer.Ordinal).ToList();
            Assert.True(files.Count > 100, "only " + files.Count + " test files found in " + dir);
            var bad = files.SelectMany(f => EarlyPassedReturns(Path.GetFileName(f), File.ReadAllText(f, Encoding.UTF8))).ToList();
            Assert.True(bad.Count == 0, bad.Count + " place(s) where a test can end early and be reported Passed (use throw NotTested.Because(reason)):\n" + string.Join("\n", bad));
        }

        /// <summary>The checker itself finds what it must find and leaves alone what it must leave alone.</summary>
        [Fact]
        public void TheChecker_FindsAnEarlyReturn_AndIgnoresLambdasAndLocalFunctions()
        {
            const string q = "\"";
            var src = string.Join("\n",
                "public class X {",
                "  [Fact] public void A() { if (!Have) return; Run(); }",                                   // found
                "  [Fact] public void B() { try { Make(); } catch (Exception e) { return; } Run(); }",          // found
                "  [Theory] [InlineData(1)] public void C(int x) { foreach (var i in L) { if (i == x) return; } }", // found
                "  [Fact] public void D() { var t = new Thread(() => { while (true) { return; } }); Run(); }", // a lambda: not found
                "  [Fact] public void E() { int F(int a) { if (a > 0) { return a; } return 0; } Run(F(1)); }", // a local function: not found
                "  [Fact] public void G() { var s = " + q + "return; {" + q + "; Run(s); }",                   // in a string: not found
                "  public void H() { if (x) return; }",                                                       // not a test: not found
                "}");
            var found = EarlyPassedReturns("x.cs", src);
            Assert.Equal(new[] { " A ", " B ", " C " }, found.Select(f => " " + Regex.Match(f, @":\d+ (\w+)").Groups[1].Value + " ").ToArray());
        }
    }
}
