using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OnlineBackup.Core
{
    /// <summary>
    /// I18N-010: the product's languages. English is the source: every text in the code is English, and each language is
    /// a dictionary "English text → translation" (i18n\&lt;lang&gt;.json, embedded). A text with values is a template:
    /// "Maximum number of backup sets reached ({0})." translates "… reached (10)." too. The same dictionaries serve the web
    /// screens (i18n.js), the e-mails and the client package. Right-to-left languages: Hebrew, Arabic.
    /// </summary>
    public static class L
    {
        // I18N-050 (owner: "too many bugs — for now only Hebrew and English"): the other dictionaries stay in the source for later.
        public static readonly string[] Languages = { "en", "he" };
        static readonly string[] RtlLanguages = { "he", "ar" };
        static readonly Dictionary<string, Dictionary<string, string>> cache = new Dictionary<string, Dictionary<string, string>>();
        static readonly Dictionary<string, List<KeyValuePair<Regex, string>>> templates = new Dictionary<string, List<KeyValuePair<Regex, string>>>();

        /// <summary>"he-IL" → "he"; unknown → "en".</summary>
        public static string Norm(string lang)
        {
            if (string.IsNullOrEmpty(lang)) return "en";
            var l = lang.Trim().ToLowerInvariant();
            if (l.Length > 2) l = l.Substring(0, 2);
            if (l == "iw") l = "he";
            return Languages.Contains(l) ? l : "en";
        }

        public static bool Rtl(string lang) { return RtlLanguages.Contains(Norm(lang)); }

        /// <summary>The dictionary file as it is (served to the web screens).</summary>
        public static string Raw(string lang)
        {
            lang = Norm(lang);
            using (var s = typeof(L).Assembly.GetManifestResourceStream("i18n." + lang + ".json"))
            {
                if (s == null) return "{\"strings\":{}}";
                using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }

        static Dictionary<string, string> Dict(string lang)
        {
            lang = Norm(lang);
            lock (cache)
            {
                Dictionary<string, string> d;
                if (cache.TryGetValue(lang, out d)) return d;
                d = new Dictionary<string, string>();
                var list = new List<KeyValuePair<Regex, string>>();
                if (lang != "en")
                {
                    var j = Json.Obj(Json.Parse(Raw(lang)));
                    foreach (var kv in Json.Obj(j.ContainsKey("strings") ? j["strings"] : null))
                    {
                        var v = kv.Value as string; if (string.IsNullOrEmpty(v)) continue;
                        d[kv.Key] = v;
                        if (kv.Key.Contains("{0}"))
                            list.Add(new KeyValuePair<Regex, string>(new Regex("^" + Regex.Replace(Regex.Escape(kv.Key), @"\\\{(\d)}", "(?<a$1>.*?)") + "$", RegexOptions.Singleline), v));
                    }
                }
                cache[lang] = d; templates[lang] = list.OrderByDescending(x => x.Key.ToString().Length).ToList();
                return d;
            }
        }

        /// <summary>A template with its values: L.T("he", "Maximum number of backup sets reached ({0}).", 10).</summary>
        public static string T(string lang, string template, params object[] args)
        {
            string tr;
            var d = Dict(lang);
            if (!d.TryGetValue(template, out tr)) tr = template;
            return Format(tr, args.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture)).ToArray());
        }

        /// <summary>A finished English text (a server message, an e-mail): exact, else by template; values that are texts themselves are translated too.</summary>
        public static string Tr(string lang, string text)
        {
            if (string.IsNullOrEmpty(text) || Norm(lang) == "en") return text;
            var d = Dict(lang);
            string tr;
            if (d.TryGetValue(text, out tr)) return tr;
            List<KeyValuePair<Regex, string>> list; lock (cache) list = templates[Norm(lang)];
            foreach (var t in list)
            {
                var m = t.Key.Match(text);
                if (!m.Success) continue;
                var args = new string[10];
                for (int i = 0; i < 10; i++) { var g = m.Groups["a" + i]; if (g.Success) { string a; args[i] = d.TryGetValue(g.Value, out a) ? a : g.Value; } }
                return Format(t.Value, args);
            }
            return text;
        }

        static string Format(string s, string[] args)
        {
            return Regex.Replace(s, @"\{(\d)}", m => { int i = m.Groups[1].Value[0] - '0'; return i < args.Length && args[i] != null ? args[i] : m.Value; });
        }
    }
}
