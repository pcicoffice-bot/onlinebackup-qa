using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Reflection;

namespace OnlineBackup.Server
{
    /// <summary>The management web UI (/admin): static files embedded in the server; all data through /api/admin.</summary>
    public static class AdminUi
    {
        public static void Serve(HttpListenerContext ctx, string[] seg)
        {
            var name = seg.Length == 1 ? "index.html" : seg[1];
            if (name != "index.html" && name != "app.js" && name != "app.css" && name != "restore.html" && name != "restore.js" && name != "report.css" && name != "report.js") { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("web." + name))
            {
                if (s == null) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
                ctx.Response.ContentType = name.EndsWith(".html") ? "text/html; charset=utf-8" : name.EndsWith(".js") ? "application/javascript; charset=utf-8" : "text/css; charset=utf-8";
                ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data: https:; style-src 'self'; script-src 'self'; frame-ancestors 'none'";
                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                ctx.Response.Headers["Cache-Control"] = "no-store";
                ctx.Response.ContentLength64 = s.Length;
                s.CopyTo(ctx.Response.OutputStream);
            }
            ctx.Response.OutputStream.Close();
        }
    
        /// <summary>I18N-010: the browser side and the dictionaries (public texts).</summary>
        public static void ServeI18n(HttpListenerContext ctx, string name)
        {
            string assetType; var asset = OnlineBackup.Core.WebAssets.Get(name, out assetType);   // DESIGN-040
            if (asset != null)
            {
                ctx.Response.ContentType = assetType; ctx.Response.Headers["X-Content-Type-Options"] = "nosniff"; ctx.Response.Headers["Cache-Control"] = name.StartsWith("fonts/") ? "public, max-age=604800" : "no-cache";
                ctx.Response.ContentLength64 = asset.Length; ctx.Response.OutputStream.Write(asset, 0, asset.Length); ctx.Response.OutputStream.Close(); return;
            }
            string body = null, type = "application/json; charset=utf-8";
            if (name == "qrcode.js") { using (var s = typeof(OnlineBackup.Core.L).Assembly.GetManifestResourceStream("qrcode.js")) using (var r = new System.IO.StreamReader(s, System.Text.Encoding.UTF8)) body = r.ReadToEnd(); type = "application/javascript; charset=utf-8"; }   // SEC-010: QR codes for the authenticator app (MIT, Kazuhiko Arase)
            if (name == "i18n.js") { using (var s = typeof(OnlineBackup.Core.L).Assembly.GetManifestResourceStream("i18n.js")) using (var r = new System.IO.StreamReader(s, System.Text.Encoding.UTF8)) body = r.ReadToEnd(); type = "application/javascript; charset=utf-8"; }
            else if (name.EndsWith(".json") && OnlineBackup.Core.L.Languages.Contains(name.Substring(0, name.Length - 5))) body = OnlineBackup.Core.L.Raw(name.Substring(0, name.Length - 5));
            if (body == null) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
            var b = System.Text.Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = type; ctx.Response.Headers["X-Content-Type-Options"] = "nosniff"; ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.ContentLength64 = b.Length; ctx.Response.OutputStream.Write(b, 0, b.Length); ctx.Response.OutputStream.Close();
        }
    }
}
