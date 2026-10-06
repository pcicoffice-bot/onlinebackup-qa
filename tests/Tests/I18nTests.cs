using System.Linq;
using OnlineBackup.Core;
using Xunit;

namespace OnlineBackup.Tests
{
    /// <summary>I18N-010: English source, dictionaries per language, templates with values, direction per language.</summary>
    public class I18nTests
    {
        [Fact]
        public void TemplatesTranslateFinishedMessages_AndEveryDictionaryKeepsItsPlaceholders()
        {
            Assert.Equal("הגעת למספר הסטים המקסימלי (10).", L.Tr("he", "Maximum number of backup sets reached (10)."));
            Assert.Equal("Maximum number of backup sets reached (10).", L.Tr("de-AT", "Maximum number of backup sets reached (10)."));   // I18N-050: only English and Hebrew for now
            Assert.Equal("Wrong user name or password.", L.Tr("en", "Wrong user name or password."));
            Assert.Equal("Something new", L.Tr("he", "Something new"));                                           // unknown text stays English
            Assert.Equal("✗ בדיקת שחזור נכשלה — dana", L.Tr("he", "✗ Restore test failed — dana"));
            Assert.True(L.Rtl("he") && !L.Rtl("en") && !L.Rtl("ar") && L.Norm("ar") == "en");
            Assert.Equal("en", L.Norm("xx")); Assert.Equal("he", L.Norm("iw"));
            foreach (var lang in L.Languages.Where(l => l != "en"))
            {
                var raw = L.Raw(lang);
                var d = Json.Obj(Json.Obj(Json.Parse(raw))["strings"]);
                if (d.Count == 0) continue;   // a language whose dictionary is not shipped yet falls back to English
                foreach (var kv in d)
                    for (int i = 0; i < 4; i++)
                        Assert.True(kv.Key.Contains("{" + i + "}") == ((string)kv.Value).Contains("{" + i + "}"), lang + ": " + kv.Key);
            }
        }
    }
}
