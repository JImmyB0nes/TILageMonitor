using System.IO;
using System.Net.Sockets;
using System.Text;
using Org.BouncyCastle.Tls;

namespace TILageMonitor;

public sealed record ConnectorHttpRequest(
    string Method,
    Uri Uri,
    IReadOnlyDictionary<string, string>? Headers = null,
    byte[]? Body = null,
    string? ContentType = null);

public sealed class ConnectorHttpResponse
{
    public int StatusCode { get; init; }
    public string Reason { get; init; } = "";
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; init; } = [];
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    public string BodyText
    {
        get
        {
            var encoding = Encoding.UTF8;
            if (Headers.TryGetValue("Content-Type", out var ct))
            {
                var idx = ct.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    try { encoding = Encoding.GetEncoding(ct[(idx + 8)..].Trim().Trim('"').Split(';')[0]); }
                    catch (ArgumentException) { /* keep UTF-8 */ }
                }
            }
            return encoding.GetString(Body);
        }
    }
}

public interface IConnectorHttpTransport
{
    Task<ConnectorHttpResponse> SendAsync(ConnectorHttpRequest request, TimeSpan timeout, CancellationToken ct);
    ConnectorServerCertificate? LastServerCertificate { get; }
    PinEvaluation LastPin { get; }
}

/// <summary>
/// Minimales HTTP/1.1 (eine Verbindung je Request, Connection: close).
/// HTTPS läuft über BouncyCastle-TLS (Brainpool/RSA), reines HTTP nur wenn TLS nicht vorgeschrieben ist.
/// </summary>
public sealed class ConnectorHttpTransport : IConnectorHttpTransport
{
    public const int MaxBodyBytes = 16 * 1024 * 1024;
    private const int MaxLineBytes = 16 * 1024;

    private readonly Func<string?> _pinnedFingerprint;
    private readonly ConnectorClientCredentials? _credentials;
    private readonly bool _allowTls13;
    private readonly string _userAgent;

    public ConnectorServerCertificate? LastServerCertificate { get; private set; }
    public PinEvaluation LastPin { get; private set; } = PinEvaluation.NotPinned;

    public ConnectorHttpTransport(
        Func<string?> pinnedFingerprint,
        ConnectorClientCredentials? credentials,
        bool allowTls13 = true,
        string? userAgent = null)
    {
        _pinnedFingerprint = pinnedFingerprint;
        _credentials = credentials;
        _allowTls13 = allowTls13;
        _userAgent = string.IsNullOrWhiteSpace(userAgent) ? "TILageMonitor" : userAgent!;
    }

    public async Task<ConnectorHttpResponse> SendAsync(ConnectorHttpRequest request, TimeSpan timeout, CancellationToken ct)
    {
        var https = string.Equals(request.Uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        if (!https && !string.Equals(request.Uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(LocalizationService.Pick("Nur HTTP/HTTPS wird unterstützt.", "Only HTTP/HTTPS is supported."));

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var tcp = new TcpClient();
        using var registration = linked.Token.Register(() => { try { tcp.Dispose(); } catch { /* ignore */ } });

        try
        {
            var port = request.Uri.IsDefaultPort ? (https ? 443 : 80) : request.Uri.Port;
            await tcp.ConnectAsync(request.Uri.IdnHost, port, linked.Token).ConfigureAwait(false);
            var timeoutMs = (int)Math.Clamp(timeout.TotalMilliseconds, 1000, int.MaxValue);
            tcp.ReceiveTimeout = timeoutMs;
            tcp.SendTimeout = timeoutMs;

            return await Task.Run(() => Exchange(tcp.GetStream(), request, https), linked.Token).ConfigureAwait(false);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (Exception ex) when (timeoutCts.IsCancellationRequested || ex is IOException { InnerException: SocketException { SocketErrorCode: SocketError.TimedOut } })
        {
            throw new TimeoutException(LocalizationService.Pick(
                "Zeitüberschreitung bei der Verbindung zum Konnektor.",
                "Connection to the connector timed out."), ex);
        }
        finally
        {
            tcp.Dispose();
        }
    }

    private ConnectorHttpResponse Exchange(NetworkStream network, ConnectorHttpRequest request, bool https)
    {
        if (!https)
        {
            LastServerCertificate = null;
            LastPin = PinEvaluation.NotPinned;
            WriteRequest(network, request);
            return ReadResponse(network);
        }

        var client = new ConnectorTlsClient(_pinnedFingerprint(), _credentials, _allowTls13, request.Uri.IdnHost);
        var protocol = new TlsClientProtocol(network);
        try
        {
            try
            {
                protocol.Connect(client);
            }
            catch (Exception ex) when (ex is TlsException or IOException)
            {
                throw ConnectorTlsClient.Translate(ex, client);
            }
            finally
            {
                LastServerCertificate = client.ServerCertificate;
                LastPin = client.Pin;
            }

            var tls = protocol.Stream;
            WriteRequest(tls, request);
            return ReadResponse(tls);
        }
        finally
        {
            try { protocol.Close(); } catch { /* ignore */ }
        }
    }

    private void WriteRequest(Stream stream, ConnectorHttpRequest request)
    {
        var head = BuildRequestHead(request, _userAgent);
        stream.Write(head, 0, head.Length);
        if (request.Body is { Length: > 0 })
            stream.Write(request.Body, 0, request.Body.Length);
        stream.Flush();
    }

    public static byte[] BuildRequestHead(ConnectorHttpRequest request, string userAgent = "TILageMonitor")
    {
        var uri = request.Uri;
        var host = uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.IdnHost.Trim('[', ']')}]" : uri.IdnHost;
        if (!uri.IsDefaultPort)
            host += ":" + uri.Port;

        var sb = new StringBuilder();
        sb.Append(request.Method).Append(' ').Append(uri.PathAndQuery).Append(" HTTP/1.1\r\n");
        sb.Append("Host: ").Append(host).Append("\r\n");
        sb.Append("User-Agent: ").Append(userAgent).Append("\r\n");
        sb.Append("Accept: */*\r\n");
        sb.Append("Connection: close\r\n");
        if (request.Body is not null)
        {
            sb.Append("Content-Type: ").Append(request.ContentType ?? "application/octet-stream").Append("\r\n");
            sb.Append("Content-Length: ").Append(request.Body.Length).Append("\r\n");
        }
        foreach (var (name, value) in request.Headers ?? new Dictionary<string, string>())
        {
            if (name.IndexOfAny(['\r', '\n', ':']) >= 0 || value.IndexOfAny(['\r', '\n']) >= 0)
                throw new ArgumentException("Invalid header.");
            sb.Append(name).Append(": ").Append(value).Append("\r\n");
        }
        sb.Append("\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Parst eine HTTP/1.1-Antwort (Content-Length, chunked oder read-to-close; überspringt 1xx).</summary>
    public static ConnectorHttpResponse ReadResponse(Stream stream)
    {
        var reader = new BufferedReader(stream);
        while (true)
        {
            var statusLine = reader.ReadLine() ?? throw new IOException("Leere HTTP-Antwort / empty HTTP response.");
            var parts = statusLine.Split(' ', 3);
            if (parts.Length < 2 || !parts[0].StartsWith("HTTP/", StringComparison.Ordinal) || !int.TryParse(parts[1], out var status))
                throw new IOException($"Ungültige HTTP-Statuszeile / invalid status line: {statusLine}");

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? line;
            while (!string.IsNullOrEmpty(line = reader.ReadLine()))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                var name = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim();
                headers[name] = headers.TryGetValue(name, out var existing) ? existing + ", " + value : value;
            }

            if (status is >= 100 and < 200)
                continue;

            byte[] body;
            if (headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                body = ReadChunked(reader);
            else if (headers.TryGetValue("Content-Length", out var cl) && long.TryParse(cl, out var length))
            {
                if (length is < 0 or > MaxBodyBytes) throw new IOException("Content-Length ungültig / invalid.");
                body = reader.ReadExact((int)length);
            }
            else if (status is 204 or 304)
                body = [];
            else
                body = reader.ReadToEnd();

            var response = new ConnectorHttpResponse { StatusCode = status, Reason = parts.Length > 2 ? parts[2] : "", Body = body };
            foreach (var kv in headers) response.Headers[kv.Key] = kv.Value;
            return response;
        }
    }

    private static byte[] ReadChunked(BufferedReader reader)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var sizeLine = reader.ReadLine() ?? throw new IOException("Chunked-Antwort abgeschnitten / truncated.");
            var semi = sizeLine.IndexOf(';');
            var hex = (semi >= 0 ? sizeLine[..semi] : sizeLine).Trim();
            if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var size) || size < 0)
                throw new IOException($"Ungültige Chunk-Größe / invalid chunk size: {sizeLine}");
            if (size == 0)
            {
                while (!string.IsNullOrEmpty(reader.ReadLine())) { }
                return ms.ToArray();
            }
            if (ms.Length + size > MaxBodyBytes) throw new IOException("Antwort zu groß / response too large.");
            var chunk = reader.ReadExact(size);
            ms.Write(chunk, 0, chunk.Length);
            reader.ReadLine();
        }
    }

    private sealed class BufferedReader
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer = new byte[16 * 1024];
        private int _pos;
        private int _len;
        private bool _eof;

        public BufferedReader(Stream stream) => _stream = stream;

        private bool Fill()
        {
            if (_eof) return false;
            int read;
            try
            {
                read = _stream.Read(_buffer, 0, _buffer.Length);
            }
            catch (TlsNoCloseNotifyException)
            {
                read = 0;
            }
            _pos = 0;
            _len = Math.Max(0, read);
            if (read <= 0) _eof = true;
            return read > 0;
        }

        public string? ReadLine()
        {
            var line = new List<byte>();
            while (true)
            {
                if (_pos >= _len && !Fill())
                    return line.Count == 0 ? null : Encoding.ASCII.GetString(line.ToArray());
                var b = _buffer[_pos++];
                if (b == (byte)'\n')
                {
                    if (line.Count > 0 && line[^1] == (byte)'\r') line.RemoveAt(line.Count - 1);
                    return Encoding.ASCII.GetString(line.ToArray());
                }
                line.Add(b);
                if (line.Count > MaxLineBytes) throw new IOException("HTTP-Zeile zu lang / line too long.");
            }
        }

        public byte[] ReadExact(int count)
        {
            var result = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                if (_pos >= _len && !Fill())
                    throw new IOException("Antwort vorzeitig beendet / response truncated.");
                var n = Math.Min(count - offset, _len - _pos);
                Buffer.BlockCopy(_buffer, _pos, result, offset, n);
                _pos += n;
                offset += n;
            }
            return result;
        }

        public byte[] ReadToEnd()
        {
            using var ms = new MemoryStream();
            while (true)
            {
                if (_pos >= _len && !Fill()) return ms.ToArray();
                ms.Write(_buffer, _pos, _len - _pos);
                _pos = _len;
                if (ms.Length > MaxBodyBytes) throw new IOException("Antwort zu groß / response too large.");
            }
        }
    }
}
