using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>
    /// Pilot release blocker UI-07 (the pilot: English and Hebrew only) — COMPONENT layer: the dictionaries and the texts
    /// of the screens alone. Contract (tests/QA/specs.py UI-07): every screen in its languages, Hebrew right to left → no
    /// missing key: every text the admin site, the client window and the installation wizard write through t('…') has a
    /// Hebrew translation, in Hebrew letters, with the same placeholders; Hebrew is right to left; only English and Hebrew
    /// are offered. Oracle: the screens' source files read here and he.json read with this test's own JSON reading.
    /// </summary>
    public class PilotI18nComponentTests
    {
        public static string Unescape(string js) { return Regex.Replace(js, @"\\(.)", m => m.Groups[1].Value == "n" ? "\n" : m.Groups[1].Value); }

        /// <summary>Every literal text a screen passes to t(…): t('…'), t("…"), plus the labels of the menus and the set
        /// editor's tabs (passed to t() as values).</summary>
        public static SortedSet<string> ScreenTexts(string file)
        {
            var s = File.ReadAllText(file, Encoding.UTF8);
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(s, @"\bt\(\s*'((?:[^'\\]|\\.)*)'")) keys.Add(Unescape(m.Groups[1].Value));
            foreach (Match m in Regex.Matches(s, "\\bt\\(\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")) keys.Add(Unescape(m.Groups[1].Value));
            foreach (var line in s.Split('\n').Where(l => Regex.IsMatch(l, @"const (NAV_SYSTEM|NAV_VENDOR|tabs) =|^\s*\['(Service|AI|Operations|Server|Settings)?', \[")))
                foreach (Match m in Regex.Matches(line, @"\[\s*'[a-z0-9]+'\s*,\s*(?:'[^']{1,3}'\s*,\s*)?'([A-Z][^']+)'")) keys.Add(Unescape(m.Groups[1].Value));
            return keys;
        }

        public static Dictionary<string, string> Hebrew()
        {
            var j = Json.Obj(Json.Parse(File.ReadAllText(Path.Combine(PilotAdminUiComponentTests.RepoRoot(), "src", "Core", "i18n", "he.json"), Encoding.UTF8)));
            return Json.Obj(j["strings"]).ToDictionary(kv => kv.Key, kv => kv.Value as string, StringComparer.Ordinal);
        }

        static IEnumerable<string> Placeholders(string s) { return Regex.Matches(s, @"\{\d\}").Cast<Match>().Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal); }
        static readonly Regex HebrewLetter = new Regex("[א-ת]");
        static readonly string[] Names = { "SQL Server", "SLA" };   // names that stay as they are in Hebrew

        /// <summary>
        /// UI-07 happy: every text of the pilot's screens — the admin site (app.js, restore.js), the client window
        /// (client.html) and the installation wizard (setup-client.html), the menus and the set editor's tabs included — has
        /// a Hebrew translation that is not empty, is written in Hebrew letters (names excepted), and keeps every {n}
        /// placeholder; the dictionary says right to left and is named in Hebrew; the shipped dictionary is the one the
        /// product reads.
        /// </summary>
        [Fact]
        public void UI07_EveryTextOfTheAdminSiteTheClientWindowAndTheWizard_HasItsHebrewTranslation()
        {
            var repo = PilotAdminUiComponentTests.RepoRoot();
            var he = Hebrew();
            var screens = new[] { Path.Combine(repo, "src", "Server", "Web", "app.js"), Path.Combine(repo, "src", "Server", "Web", "restore.js"), Path.Combine(repo, "src", "Agent", "client.html"), Path.Combine(repo, "src", "Agent", "setup-client.html") };
            var problems = new List<string>(); int total = 0;
            foreach (var f in screens)
            {
                var texts = ScreenTexts(f);
                Assert.True(texts.Count > (f.EndsWith("app.js") ? 500 : 30), Path.GetFileName(f) + ": only " + texts.Count + " texts found — the reader of the screen is broken");
                total += texts.Count;
                foreach (var k in texts)
                {
                    string v;
                    if (!he.TryGetValue(k, out v) || string.IsNullOrWhiteSpace(v)) { problems.Add(Path.GetFileName(f) + ": no Hebrew for \"" + k + "\""); continue; }
                    if (!Placeholders(k).SequenceEqual(Placeholders(v))) problems.Add(Path.GetFileName(f) + ": placeholders differ: \"" + k + "\" → \"" + v + "\"");
                    if (Regex.IsMatch(k, "[a-z]{4,}") && !Names.Contains(k) && !HebrewLetter.IsMatch(v)) problems.Add(Path.GetFileName(f) + ": not in Hebrew: \"" + k + "\" → \"" + v + "\"");
                }
            }
            Assert.True(problems.Count == 0, problems.Count + " of " + total + " texts:\n" + string.Join("\n", problems));
            Assert.Contains("General", ScreenTexts(screens[0])); Assert.Contains("Dashboard", ScreenTexts(screens[0]));   // the tab and menu labels are read too
            var raw = Json.Obj(Json.Parse(L.Raw("he")));
            Assert.Equal("rtl", raw["dir"]); Assert.Equal("עברית", raw["name"]);
            Assert.Equal("ltr", Json.Obj(Json.Parse(L.Raw("en")))["dir"]);
            Assert.Equal(File.ReadAllText(Path.Combine(repo, "src", "Core", "i18n", "he.json"), Encoding.UTF8).TrimStart('﻿'), L.Raw("he").TrimStart('﻿'));
            // the messages the pilot screens show that are translated arrive in Hebrew, with their values
            Assert.Equal(he["Wrong user name or password."], L.Tr("he", "Wrong user name or password."));
            Assert.Equal(he["Another administrator changed this set after you opened it. Reload it and make your change again."], L.Tr("he", "Another administrator changed this set after you opened it. Reload it and make your change again."));
            Assert.Equal("הגעת למספר הסטים המקסימלי (7).", L.Tr("he", "Maximum number of backup sets reached (7)."));
            Assert.Equal(L.Tr("he", "Maximum number of backup sets reached (7)."), L.T("he", "Maximum number of backup sets reached ({0}).", 7));
        }

        /// <summary>
        /// UI-07 boundary: the pilot offers English and Hebrew only — the product's languages are exactly en, he; Hebrew
        /// is chosen by "he", "HE", "he-IL", "iw" (the old code); every other language, an empty or unknown one opens in
        /// English (never a half-translated screen); right to left is Hebrew only; the browser side offers the same two
        /// and turns the page right to left for Hebrew. An English text with no translation stays English (never empty);
        /// English is never changed.
        /// </summary>
        [Fact]
        public void UI07_OnlyEnglishAndHebrew_HebrewByEveryName_EverythingElseOpensInEnglish_RightToLeftOnlyForHebrew()
        {
            Assert.Equal(new[] { "en", "he" }, L.Languages);
            foreach (var h in new[] { "he", "HE", "he-IL", "iw", " he " }) { Assert.Equal("he", L.Norm(h)); Assert.True(L.Rtl(h), h); }
            foreach (var o in new[] { null, "", "en", "en-US", "ar", "ar-SA", "de", "fr-BE", "xx", "hebrew" })
            {
                var n = L.Norm(o);
                Assert.True(n == "en" || (o ?? "").Trim().ToLowerInvariant().StartsWith("he"), (o ?? "null") + " → " + n);
                if (n == "en") Assert.False(L.Rtl(o), o);
            }
            Assert.Equal(L.Raw("en"), L.Raw("de"));                                    // another language is the English dictionary
            Assert.Equal("Something nobody translated", L.Tr("he", "Something nobody translated"));
            Assert.Equal("", L.Tr("he", "")); Assert.Null(L.Tr("he", null));
            Assert.Equal("Wrong user name or password.", L.Tr("en", "Wrong user name or password."));
            Assert.Equal("Wrong user name or password.", L.Tr("de", "Wrong user name or password."));

            var js = File.ReadAllText(Path.Combine(PilotAdminUiComponentTests.RepoRoot(), "src", "Core", "i18n", "i18n.js"), Encoding.UTF8);
            var names = Regex.Match(js, @"const NAMES = \{([^}]*)\}").Groups[1].Value;
            Assert.Equal(new[] { "en", "he" }, Regex.Matches(names, @"(\w+):").Cast<Match>().Select(m => m.Groups[1].Value));
            Assert.Contains("document.documentElement.dir = RTL.includes(I.lang) ? 'rtl' : 'ltr'", js);
            Assert.Contains("I.pick = (fallback, tz, browser) => norm(store.get()) || 'en';", js);   // I18N-040: opens in English until the person chooses
        }
    }
}
