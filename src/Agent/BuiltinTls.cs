using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Org.BouncyCastle.Crypto.Tls;
using Org.BouncyCastle.Security;
using OnlineBackup.Core;

namespace OnlineBackup.Agent
{
    /// <summary>
    /// HTTPS with a TLS 1.2 implementation carried by the agent itself (BouncyCastle), for Windows Server 2003 / XP whose
    /// own TLS stops at 1.0. The server certificate is checked by its pinned SHA-256 fingerprint (taken at registration,
    /// or given by the installer); without a pin, by the Windows certificate chain and the host name.
    /// One request per connection (Connection: close) — simple and robust.
    /// </summary>
    public static class BuiltinTls
    {
        public sealed class Response
        {
            public int Status;
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Stream Body;
            public TcpClient Tcp;
            public void Close() { try { Body.Dispose(); } catch { } try { Tcp.Close(); } catch { } }
        }

        public static Response Send(Uri url, string method, Dictionary<string, string> headers, byte[] body, Action<Stream> streamBody, string pin, Action<string> seenPin)
        {
            var tcp = new TcpClient();
            tcp.Connect(url.Host, url.Port);
            tcp.ReceiveTimeout = 300000; tcp.SendTimeout = 300000;
            var proto = new TlsClientProtocol(tcp.GetStream(), new SecureRandom());
            var client = new Client(url.Host, pin);
            try { proto.Connect(client); }
            catch (Exception e) { tcp.Close(); throw new AgentException(0, client.Rejected ?? "TLS", client.RejectReason ?? ("TLS: " + e.Message)); }
            if (seenPin != null) seenPin(client.Fingerprint);
            var s = proto.Stream;
            var sb = new StringBuilder();
            sb.Append(method).Append(' ').Append(url.PathAndQuery).Append(" HTTP/1.1\r\n");
            sb.Append("Host: ").Append(url.Host).Append(url.IsDefaultPort ? "" : ":" + url.Port.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("Connection: close\r\n");
            foreach (var h in headers) sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
            if (streamBody != null) sb.Append("Transfer-Encoding: chunked\r\n");
            else sb.Append("Content-Length: ").Append((body == null ? 0 : body.Length).ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("\r\n");
            var head = Encoding.ASCII.GetBytes(sb.ToString());
            s.Write(head, 0, head.Length);
            if (streamBody != null) { using (var cs = new ChunkedWriter(s)) streamBody(cs); }
            else if (body != null && body.Length > 0) s.Write(body, 0, body.Length);
            s.Flush();
            return ReadResponse(s, tcp);
        }

        static Response ReadResponse(Stream s, TcpClient tcp)
        {
            var r = new Response { Tcp = tcp };
            var status = ReadLine(s);
            var parts = status.Split(' ');
            if (parts.Length < 2 || !parts[0].StartsWith("HTTP/")) throw new IOException("Bad HTTP response.");
            r.Status = int.Parse(parts[1], CultureInfo.InvariantCulture);
            string line;
            while ((line = ReadLine(s)).Length > 0)
            {
                int i = line.IndexOf(':');
                if (i > 0) r.Headers[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
            string te, cl;
            if (r.Headers.TryGetValue("Transfer-Encoding", out te) && te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0) r.Body = new ChunkedReader(s);
            else if (r.Headers.TryGetValue("Content-Length", out cl)) r.Body = new LimitedReader(s, long.Parse(cl, CultureInfo.InvariantCulture));
            else r.Body = s;
            return r;
        }

        static string ReadLine(Stream s)
        {
            var b = new List<byte>();
            while (true)
            {
                int c = s.ReadByte();
                if (c < 0) break;
                if (c == '\n') break;
                if (c != '\r') b.Add((byte)c);
                if (b.Count > 16384) throw new IOException("HTTP header line too long.");
            }
            return Encoding.ASCII.GetString(b.ToArray());
        }

        sealed class Client : DefaultTlsClient
        {
            readonly string host, pin;
            public string Fingerprint, Rejected, RejectReason;
            public Client(string host, string pin) { this.host = host; this.pin = pin; }

            public override ProtocolVersion MinimumVersion { get { return ProtocolVersion.TLSv12; } }
            public override ProtocolVersion ClientVersion { get { return ProtocolVersion.TLSv12; } }

            public override IDictionary GetClientExtensions()
            {
                var ext = TlsExtensionsUtilities.EnsureExtensionsInitialised(base.GetClientExtensions());
                if (Uri.CheckHostName(host) == UriHostNameType.Dns)
                    TlsExtensionsUtilities.AddServerNameExtension(ext, new ServerNameList(new ArrayList { new ServerName(NameType.host_name, host) }));
                return ext;
            }

            public override TlsAuthentication GetAuthentication() { return new Auth(this); }

            sealed class Auth : TlsAuthentication
            {
                readonly Client c;
                public Auth(Client c) { this.c = c; }

                public void NotifyServerCertificate(Certificate serverCertificate)
                {
                    if (serverCertificate == null || serverCertificate.IsEmpty) Fail("No server certificate.");
                    var der = serverCertificate.GetCertificateAt(0).GetEncoded();
                    c.Fingerprint = Bytes.Hex(Bytes.Sha256(der));
                    if (!string.IsNullOrEmpty(c.pin))
                    {
                        if (!string.Equals(c.pin.Replace(":", ""), c.Fingerprint, StringComparison.OrdinalIgnoreCase)) Fail("The server certificate does not match the pinned certificate.");
                        return;
                    }
                    var cert = new X509Certificate2(der);
                    var chain = new X509Chain();
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    for (int i = 1; i < serverCertificate.Length; i++) chain.ChainPolicy.ExtraStore.Add(new X509Certificate2(serverCertificate.GetCertificateAt(i).GetEncoded()));
                    if (!chain.Build(cert)) Fail("The server certificate is not trusted by this computer (pin it at installation).");
                    var name = cert.GetNameInfo(X509NameType.DnsName, false) ?? "";
                    if (!HostMatches(name, c.host) && !SanMatches(cert, c.host)) Fail("The server certificate is for " + name + ", not " + c.host + ".");
                }

                void Fail(string why) { c.Rejected = "CERT"; c.RejectReason = why; throw new TlsFatalAlert(AlertDescription.bad_certificate); }

                public TlsCredentials GetClientCredentials(Org.BouncyCastle.Crypto.Tls.CertificateRequest certificateRequest) { return null; }
            }
        }

        static bool HostMatches(string pattern, string host)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            if (pattern.StartsWith("*."))
            {
                int dot = host.IndexOf('.');
                return dot > 0 && string.Equals(pattern.Substring(1), host.Substring(dot), StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase);
        }

        static bool SanMatches(X509Certificate2 cert, string host)
        {
            foreach (var e in cert.Extensions)
                if (e.Oid != null && e.Oid.Value == "2.5.29.17")
                    foreach (var part in e.Format(false).Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var p = part.Trim(); int i = p.IndexOfAny(new[] { '=', ':' });
                        if (i > 0 && HostMatches(p.Substring(i + 1).Trim(), host)) return true;
                    }
            return false;
        }

        sealed class ChunkedWriter : Stream
        {
            readonly Stream s; bool done;
            public ChunkedWriter(Stream s) { this.s = s; }
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count == 0) return;
                var h = Encoding.ASCII.GetBytes(count.ToString("x", CultureInfo.InvariantCulture) + "\r\n");
                s.Write(h, 0, h.Length); s.Write(buffer, offset, count); s.Write(new byte[] { 13, 10 }, 0, 2);
            }
            protected override void Dispose(bool disposing)
            {
                if (!done) { done = true; var end = Encoding.ASCII.GetBytes("0\r\n\r\n"); s.Write(end, 0, end.Length); s.Flush(); }
                base.Dispose(disposing);
            }
            public override void Flush() { s.Flush(); }
            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
        }

        sealed class LimitedReader : Stream
        {
            readonly Stream s; long left;
            public LimitedReader(Stream s, long n) { this.s = s; left = n; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (left <= 0) return 0;
                int r = s.Read(buffer, offset, (int)Math.Min(count, left));
                if (r <= 0) throw new EndOfStreamException("Connection closed before the end of the response.");
                left -= r; return r;
            }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }

        sealed class ChunkedReader : Stream
        {
            readonly Stream s; long left; bool end;
            public ChunkedReader(Stream s) { this.s = s; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (end) return 0;
                if (left == 0)
                {
                    var line = ReadLine(s);
                    if (line.Length == 0) line = ReadLine(s);
                    int semi = line.IndexOf(';'); if (semi >= 0) line = line.Substring(0, semi);
                    left = long.Parse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if (left == 0) { end = true; ReadLine(s); return 0; }
                }
                int r = s.Read(buffer, offset, (int)Math.Min(count, left));
                if (r <= 0) throw new EndOfStreamException("Connection closed inside a chunk.");
                left -= r;
                if (left == 0) ReadLine(s);
                return r;
            }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}
