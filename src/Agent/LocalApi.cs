using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    public interface IClientApi { Msg Call(string op, Msg body, IDictionary<string, string> query); }

    /// <summary>The service's local API: http://127.0.0.1:port with the key from ui.txt (only this computer, only with the key).</summary>
    public sealed class LocalApi : IClientApi
    {
        string baseUrl, key;
        readonly Func<string> reread;
        public LocalApi(string uiUrl) : this(uiUrl, null) { }
        public LocalApi(string uiUrl, Func<string> reread) { this.reread = reread; Use(uiUrl); }
        void Use(string uiUrl)
        {
            current = uiUrl.Trim(); var u = new Uri(current);
            baseUrl = "http://127.0.0.1:" + u.Port + "/api/"; key = u.Fragment.TrimStart('#');
        }
        string current;
        /// <summary>
        /// Bug 89 (Windows run 12): the service makes a new key — and may take another port — every time it starts, and
        /// writes them to ui.txt. The window read them once, so after a restart of the service (an update, a crash, a
        /// technician) every action failed with "Wrong key" until the customer closed the window. Now, when the service
        /// refuses the key or nothing listens on the port, ui.txt is read again and, if it changed, the call is made once
        /// more. Only when the request certainly was not carried out (refused before any work, or never connected).
        /// </summary>
        public Msg Call(string op, Msg body, IDictionary<string, string> query)
        {
            try { return Send(op, body, query); }
            catch (AgentException e) when ((e.Code == "KEY" || e.Code == "NOT_LISTENING") && reread != null)
            {
                string now;
                try { now = (reread() ?? "").Trim(); } catch (Exception) { throw e; }
                if (now.Length == 0 || now == current) throw;
                Use(now);
                return Send(op, body, query);
            }
        }

        /// <summary>Nothing listens on the port (refused before any request was sent) — .NET Framework says ConnectFailure,
        /// .NET 8 a socket error "connection refused" inside.</summary>
        static bool NotListening(WebException e)
        {
            if (e.Status == WebExceptionStatus.ConnectFailure) return true;
            for (Exception x = e.InnerException; x != null; x = x.InnerException)
            {
                var se = x as System.Net.Sockets.SocketException;
                if (se != null && se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused) return true;
            }
            return false;
        }

        Msg Send(string op, Msg body, IDictionary<string, string> query)
        {
            var q = query == null || query.Count == 0 ? "" : "?" + string.Join("&", query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value ?? "")).ToArray());
            var r = (HttpWebRequest)WebRequest.Create(baseUrl + op + q);
            r.Method = body == null ? "GET" : "POST"; r.Headers["X-Key"] = key; r.Timeout = 600000; r.ReadWriteTimeout = 600000; r.Proxy = null;
            try
            {
                // the body too: a refused connection shows here first for a POST ("Back up now", a restore)
                if (body != null) { var b = body.ToBytes(); r.ContentType = "application/xml"; r.ContentLength = b.Length; using (var s = r.GetRequestStream()) s.Write(b, 0, b.Length); }
                using (var w = (HttpWebResponse)r.GetResponse()) using (var s = w.GetResponseStream()) return Msg.Read(s);
            }
            catch (WebException e)
            {
                var w = e.Response as HttpWebResponse;
                if (w == null) throw new AgentException(0, NotListening(e) ? "NOT_LISTENING" : "SERVICE", "The backup service on this computer does not answer.");
                // CLI-130 (owner's screen: "Cannot access a disposed object 'HttpWebResponse'"): the status is read before the
                // response is closed — after it, .NET Framework throws and the real message was lost
                var status = (int)w.StatusCode; Msg m;
                using (w) using (var s = w.GetResponseStream()) { try { m = Msg.Read(s); } catch (Exception) { m = new Msg(); } }
                throw new AgentException(status, m["code"] ?? m["error"] ?? "", m["message"] ?? ("Error " + status));
            }
        }
    }
}
