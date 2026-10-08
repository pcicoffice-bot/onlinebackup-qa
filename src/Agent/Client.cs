using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    public sealed class AgentException : Exception
    {
        public int Status { get; private set; }
        public string Code { get; private set; }
        public AgentException(int status, string code, string message) : base(message) { Status = status; Code = code; }
    }

    /// <summary>HTTP(S) client on HttpWebRequest (available on .NET 4.0). Retries network errors; never logs secrets.</summary>
    public sealed class Client
    {
        readonly string baseUrl;
        public string Device { get; set; }
        public string Session { get; set; }
        public int Retries = 3;
        /// <summary>How long one request may wait for the server (a big commit can take minutes).</summary>
        public int TimeoutMs = 300000;
        /// <summary>Use the agent's own TLS 1.2 (Windows 2003 / XP), with the server certificate pinned.</summary>
        public bool Builtin;
        public string Pin;
        public string SeenPin { get; private set; }
        bool UseBuiltin { get { return Builtin && baseUrl.StartsWith("https:", StringComparison.OrdinalIgnoreCase); } }

        Dictionary<string, string> AuthHeaders()
        {
            // PILOT-010 / AG-08: the agent always says what it runs on — also on Windows XP / 2003, whose requests go this way
            // (built-in TLS) and said nothing, and before it has a device token (registration, sign-in)
            var h = new Dictionary<string, string> { { "X-Agent", AgentInfo } };
            if (Device != null) h["X-Device"] = Device;
            if (Session != null) h["X-Session"] = Session;
            return h;
        }

        BuiltinTls.Response Builtin_(string method, string path, Dictionary<string, string> headers, byte[] body, Action<Stream> stream)
        {
            return BuiltinTls.Send(new Uri(baseUrl + path), method, headers, body, stream, Pin, p => SeenPin = p);
        }

        static Msg BuiltinMsg(BuiltinTls.Response r)
        {
            try
            {
                Msg m;
                try { m = Msg.Read(r.Body); } catch (Exception) { m = new Msg(); }
                if (r.Status >= 400) throw new AgentException(r.Status, m["error"] ?? "HTTP", m["message"] ?? ("HTTP " + r.Status));
                return m;
            }
            finally { r.Close(); }
        }

        /// <summary>
        /// TLS-020: with the system's TLS too, a pinned server certificate (the IT company's own, often self-signed) is
        /// accepted by its SHA-256 — and only that one; without a pin the normal Windows validation applies.
        /// The agent talks to one server, so the process-wide callback is safe.
        /// </summary>
        static volatile string systemPin;
        static readonly Dictionary<string, string> hostPins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static void UsePin(string pin, string host = null)
        {
            if (string.IsNullOrEmpty(pin)) return;
            systemPin = pin.Replace(":", "").ToLowerInvariant();
            // H-08: the pin belongs to the backup server's host — other hosts this process talks to (Microsoft 365, Google,
            // the licence centre) keep the system's validation
            if (!string.IsNullOrEmpty(host)) lock (hostPins) hostPins[host] = systemPin;
            Install();
        }

        /// <summary>VMW-015: another host with a self-signed certificate (an ESXi host / vCenter), trusted by its SHA-256 only.</summary>
        public static void TrustHost(string host, string pin)
        {
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(pin)) return;
            lock (hostPins) hostPins[host] = pin.Replace(":", "").Replace(" ", "").ToLowerInvariant();
            Install();
        }

        static void Install()
        {
            ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) =>
            {
                // H-08: with a pin, the pin alone decides — a certificate the system trusts (a TLS-inspection proxy, a
                // mis-issued certificate) is still not the IT company's server. Without a pin, the system's trust.
                var hex = cert == null ? null : Bytes.Hex(Bytes.Sha256(cert.GetRawCertData()));
                var req = sender as HttpWebRequest;
                if (req != null) { string hp; lock (hostPins) if (hostPins.TryGetValue(req.RequestUri.Host, out hp)) return hex != null && hex == hp; }
                if (req != null) return errors == System.Net.Security.SslPolicyErrors.None;
                if (systemPin != null) return hex != null && hex == systemPin;   // no request to name the host: the pin decides
                return errors == System.Net.Security.SslPolicyErrors.None;
            };
        }

        public Client(string serverUrl, string pin = null)
        {
            baseUrl = serverUrl.TrimEnd('/');
            Pin = pin; string host = null; try { host = new Uri(baseUrl).Host; } catch (UriFormatException) { } UsePin(pin, host);
            // TLS 1.2 where the OS offers it (2008 R2+ with updates). Windows 2003 needs the bundled TLS library (later phase).
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | SecurityProtocolType.Tls; } catch (NotSupportedException) { }
            ServicePointManager.Expect100Continue = false;
        }

        // COMP-010: this computer's software version and operating system, shown in the admin site's computer list
        static readonly string AgentInfo = typeof(Client).Assembly.GetName().Version + "; " + Environment.OSVersion.VersionString;

        /// <summary>The wait before the next try; none after the last one (bug 126: a failed call paused 1-4 s before saying so).</summary>
        void Pause(int attempt) { if (attempt < Retries) System.Threading.Thread.Sleep(1000 * (attempt + 1)); }

        HttpWebRequest Create(string method, string path)
        {
            var r = (HttpWebRequest)WebRequest.Create(baseUrl + path);
            r.Method = method;
            r.Timeout = TimeoutMs; r.ReadWriteTimeout = TimeoutMs;
            r.KeepAlive = true;
            r.Headers["X-Agent"] = AgentInfo;
            if (Device != null) r.Headers["X-Device"] = Device;
            if (Session != null) r.Headers["X-Session"] = Session;
            return r;
        }

        public Msg Call(string method, string path, Msg body = null)
        {
            Exception last = null;
            for (int attempt = 0; attempt <= Retries; attempt++)
            {
                try
                {
                    if (UseBuiltin)
                    {
                        var h = AuthHeaders();
                        if (body != null) h["Content-Type"] = "application/xml; charset=utf-8";
                        return BuiltinMsg(Builtin_(method, path, h, body == null ? null : body.ToBytes(), null));
                    }
                    var r = Create(method, path);
                    if (body != null)
                    {
                        var b = body.ToBytes();
                        r.ContentType = "application/xml; charset=utf-8";
                        r.ContentLength = b.Length;
                        using (var s = r.GetRequestStream()) s.Write(b, 0, b.Length);
                    }
                    else if (method != "GET") r.ContentLength = 0;   // mono does not send "Content-Length: 0" by itself
                    return ReadResponse(r);
                }
                catch (AgentException) { throw; }
                catch (WebException e) { last = e; Pause(attempt); }
                catch (IOException e) { last = e; Pause(attempt); }
                catch (SocketException e) { last = e; Pause(attempt); }
            }
            throw new AgentException(0, "NETWORK", "No connection to the backup server: " + (last == null ? "" : last.Message));
        }

        static Msg ReadResponse(HttpWebRequest r)
        {
            try
            {
                using (var resp = (HttpWebResponse)r.GetResponse())
                using (var s = resp.GetResponseStream())
                    return Msg.Read(s);
            }
            catch (WebException e)
            {
                var resp = e.Response as HttpWebResponse;
                if (resp == null) throw;
                var status = (int)resp.StatusCode; var description = resp.StatusDescription; Msg m;   // CLI-130: before closing
                using (resp) using (var s = resp.GetResponseStream()) { try { m = Msg.Read(s); } catch { m = new Msg(); } }
                throw new AgentException(status, m["error"] ?? "HTTP", m["message"] ?? description);
            }
        }

        /// <summary>Streams one object to the server (chunked transfer: no temporary copy even for a 2TB file).</summary>
        public Msg Put(string path, Dictionary<string, string> headers, Action<Stream> write)
        {
            if (UseBuiltin)
            {
                var h = AuthHeaders();
                foreach (var kv in headers) h[kv.Key] = kv.Value;
                h["Content-Type"] = "application/octet-stream";
                // bug 118 (was 104b): every IOException here was "the network" - also the SOURCE file's own (locked, unreadable),
                // read inside write(): the file was sent again and again and the whole run ended in a system error. As on the
                // system TLS path: the network stream is wrapped, a failure writing to it is the network's, anything else is the file's.
                Exception source = null;
                Action<Stream> w = s => { try { write(new NetStream(s)); } catch (NetStream.Failure) { throw; } catch (Exception e) { source = e; throw; } };
                try { return BuiltinMsg(Builtin_("PUT", path, h, null, w)); }
                catch (Exception) when (source != null) { throw source; }
                catch (NetStream.Failure e) { throw new AgentException(0, "NETWORK", "The connection to the backup server broke: " + e.InnerException.Message); }
                catch (IOException e) { throw new AgentException(0, "NETWORK", "No connection to the backup server: " + e.Message); }
                catch (SocketException e) { throw new AgentException(0, "NETWORK", "No connection to the backup server: " + e.Message); }
            }
            var r = Create("PUT", path);
            r.SendChunked = true;
            r.AllowWriteStreamBuffering = false;
            r.ContentType = "application/octet-stream";
            foreach (var h in headers) r.Headers[h.Key] = h.Value;
            // Bug 24: a connection cut while sending was a raw WebException / IOException that ended the whole backup; it is
            // now a NETWORK error, which the caller sends again. An error reading the source file is not a network error.
            try
            {
                using (var s = r.GetRequestStream()) write(new NetStream(s));
                // bug 104: the object is sent; a connection cut while its answer comes back is the network's too (it was
                // reported as "Cannot read file" of the customer's source and never sent again)
                try { return ReadResponse(r); }
                catch (IOException e) { throw new AgentException(0, "NETWORK", "The connection to the backup server broke while it answered: " + e.Message); }
            }
            catch (WebException e) { throw new AgentException(0, "NETWORK", "The connection to the backup server broke: " + e.Message); }
            catch (NetStream.Failure e) { throw new AgentException(0, "NETWORK", "The connection to the backup server broke: " + e.InnerException.Message); }
        }

        /// <summary>The request stream: a failure writing to it is the network's, told apart from a failure reading the source.</summary>
        sealed class NetStream : Stream
        {
            public sealed class Failure : Exception { public Failure(Exception inner) : base(inner.Message, inner) { } }
            readonly Stream s;
            public NetStream(Stream s) { this.s = s; }
            public override void Write(byte[] buffer, int offset, int count)
            {
                try { s.Write(buffer, offset, count); }
                catch (IOException e) { throw new Failure(e); }
                catch (WebException e) { throw new Failure(e); }
                catch (ObjectDisposedException e) { throw new Failure(e); }
            }
            public override void Flush() { try { s.Flush(); } catch (IOException e) { throw new Failure(e); } catch (WebException e) { throw new Failure(e); } }
            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
        }

        public void Download(string path, string toFile)
        {
            Exception last = null;
            for (int attempt = 0; attempt <= Retries; attempt++)
            {
                try
                {
                    if (UseBuiltin)
                    {
                        var br = Builtin_("GET", path, AuthHeaders(), null, null);
                        if (br.Status >= 400) { BuiltinMsg(br); }
                        try { var fs = LocalFile(toFile); try { CopyTo(br.Body, fs); } finally { Close(fs); } }
                        finally { br.Close(); }
                        return;
                    }
                    var r = Create("GET", path);
                    using (var resp = (HttpWebResponse)r.GetResponse())
                    using (var s = resp.GetResponseStream())
                    {
                        var fs = LocalFile(toFile);
                        try { CopyTo(s, fs); } finally { Close(fs); }
                    }
                    return;
                }
                catch (WebException e)
                {
                    var resp = e.Response as HttpWebResponse;
                    if (resp != null && (int)resp.StatusCode < 500) ReadResponse(Create("GET", path));
                    last = e; Pause(attempt);
                }
                catch (IOException e) { last = e; Pause(attempt); }
                catch (SocketException e) { last = e; Pause(attempt); }
            }
            throw new AgentException(0, "NETWORK", "The download failed: " + (last == null ? "" : last.Message));
        }

        /// <summary>Agent N (N-2): a failure of THIS computer's disk (full, no permission) while downloading is not the
        /// network: it was retried four times with pauses, for every file — a restore onto a full disk took days to say so.
        /// It stops at once with what Windows said (AgentException LOCAL_DISK).</summary>
        static FileStream LocalFile(string path)
        {
            try { return new FileStream(path, FileMode.Create, FileAccess.Write); }
            catch (IOException e) { throw new AgentException(0, "LOCAL_DISK", "This computer cannot write " + path + ": " + e.Message); }
            catch (UnauthorizedAccessException e) { throw new AgentException(0, "LOCAL_DISK", "This computer cannot write " + path + ": " + e.Message); }
        }

        static void Close(FileStream f) { try { f.Dispose(); } catch (IOException) { } }   // what was written was flushed; a full disk is already said

        static void CopyTo(Stream from, FileStream to)
        {
            var buf = new byte[1 << 16]; int n;
            while ((n = from.Read(buf, 0, buf.Length)) > 0)   // a read error is the line's: retried by Download
            {
                try { to.Write(buf, 0, n); }
                catch (IOException e) { throw new AgentException(0, "LOCAL_DISK", "This computer cannot write " + to.Name + ": " + e.Message); }
            }
            try { to.Flush(); } catch (IOException e) { throw new AgentException(0, "LOCAL_DISK", "This computer cannot write " + to.Name + ": " + e.Message); }
        }

        public static string Url(string s) { return Uri.EscapeDataString(s ?? ""); }
    }
}
