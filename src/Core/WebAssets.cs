using System.IO;
using System.Reflection;

namespace OnlineBackup.Core
{
    /// <summary>DESIGN-040: the shared design system (/i18n/theme.css) and its fonts (/i18n/fonts/*.woff2), embedded in this
    /// library and served the same way by the server, the licensing centre and the agent.</summary>
    public static class WebAssets
    {
        /// <summary>The bytes and the content type of an asset under /i18n/, or null.</summary>
        public static byte[] Get(string name, out string type)
        {
            type = null; string res = null;
            if (name == "theme.css") { res = "theme.css"; type = "text/css; charset=utf-8"; }
            else if (name.StartsWith("fonts/") && name.EndsWith(".woff2") && name.IndexOf("..") < 0 && name.IndexOf('/', 6) < 0) { res = "fonts." + name.Substring(6); type = "font/woff2"; }
            if (res == null) return null;
            using (var s = typeof(WebAssets).Assembly.GetManifestResourceStream(res))
            {
                if (s == null) { type = null; return null; }
                var m = new MemoryStream(); var buf = new byte[65536]; int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) m.Write(buf, 0, n);
                return m.ToArray();
            }
        }
    }
}
