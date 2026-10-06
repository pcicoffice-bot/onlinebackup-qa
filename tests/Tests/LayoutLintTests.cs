using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// UI-010: every screen is right to left in Hebrew / Arabic and left to right elsewhere, by itself — so no screen may fix
    /// a side (text-align: left, margin-right, float: left …). Use the logical forms (start / end, margin-inline-start …).
    /// A line that must stay physical (a log, an address) says why with "ltr-ok".
    /// </summary>
    public class LayoutLintTests
    {
        static readonly Regex Physical = new Regex(@"text-align\s*:\s*['""]?(left|right)|textAlign\s*[:=]\s*['""](left|right)|(margin|padding|border)-(left|right)\b|(margin|padding|border)(Left|Right)\b|float\s*:\s*(left|right)|(^|[\s;{'""])(left|right)\s*:\s*-?[0-9]");

        [Fact]
        public void NoScreenFixesLeftOrRight()
        {
            var root = AppContext.BaseDirectory;
            while (root != null && !Directory.Exists(Path.Combine(root, "src", "Server", "Web"))) root = Path.GetDirectoryName(root);
            Assert.NotNull(root);
            var files = Directory.GetFiles(Path.Combine(root, "src", "Server", "Web")).Where(f => f.EndsWith(".css") || f.EndsWith(".js") || f.EndsWith(".html"))
                .Concat(new[] { Path.Combine(root, "src", "Agent", "client.html"), Path.Combine(root, "src", "Server", "Compliance.cs"), Path.Combine(root, "src", "Server", "Notify.cs") })
                .Where(f => !f.EndsWith("qrcode.js")).ToList();
            var bad = files.SelectMany(f => File.ReadAllLines(f).Select((l, i) => new { f, l, i }))
                .Where(x => Physical.IsMatch(x.l) && !x.l.Contains("ltr-ok"))
                .Select(x => Path.GetFileName(x.f) + ":" + (x.i + 1) + ": " + x.l.Trim()).ToList();
            Assert.True(bad.Count == 0, "Fixed left / right (use start / end, or mark the line ltr-ok with the reason):\n" + string.Join("\n", bad));
        }
    }
}
