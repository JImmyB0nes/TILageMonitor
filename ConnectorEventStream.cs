using System.IO;
using System.Net;
using System.Net.Sockets;
using Org.BouncyCastle.Tls;

namespace TILageMonitor;

/// <summary>Parameter für die CETP-Ereignissenke (gemSpec_Kon TIP1-A_4594/4595/4596).</summary>
public sealed class ConnectorEventSinkOptions
{
    public required string Host { get; init; }
    public required bool UseTls { get; init; }
    public ConnectorClientCredentials? Credentials { get; init; }
    public string? PinnedFingerprint { get; init; }
}

/// <summary>
/// Hält den TCP- (CETP2) bzw. TLS-Listener (CETP1) für Konnektor-Ereignisse.
/// EventTo-Syntax: cetp://host:port bzw. cetp://[ipv6]:port. Nachrichten folgen dem CETP-Rahmen.
/// </summary>
public sealed class ConnectorEventStream : IDisposable
{
    private const int BasePort = 48150;
    private const int MaxPortAttempts = 12;

    private readonly object _gate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _port;
    private ConnectorEventSinkOptions? _options;
    private IPAddress[] _allowedPeers = [];

    public string? EventTo { get; private set; }

    public bool IsListening
    {
        get { lock (_gate) return _listener is not null; }
    }

    public event Action<ConnectorEvent>? EventReceived;

    public bool EnsureListening(ConnectorEventSinkOptions options, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            if (_listener is not null && SameOptions(_options, options))
                return true;
            StopLocked();

            if (options.UseTls && options.Credentials is null)
            {
                log?.Invoke(LocalizationService.Pick(
                    "CETP über TLS braucht das Clientzertifikat (P12) als Serverzertifikat der Ereignissenke.",
                    "CETP over TLS needs the client certificate (P12) as the event sink's server certificate."));
                return false;
            }

            var allowed = ConnectorEventTo.ResolveHost(options.Host).ToArray();
            if (allowed.Length == 0)
            {
                log?.Invoke(LocalizationService.Pick(
                    "Die Konnektor-Adresse für die Ereignissenke konnte nicht aufgelöst werden.",
                    "The connector address for the event sink could not be resolved."));
                return false;
            }

            var local = ConnectorEventTo.LocalAddressToward(allowed[0])
                        ?? (allowed[0].AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback);

            for (var attempt = 0; attempt < MaxPortAttempts; attempt++)
            {
                var candidate = BasePort + attempt;
                try
                {
                    var listener = new TcpListener(local, candidate);
                    listener.Start();
                    _listener = listener;
                    _port = candidate;
                    _cts = new CancellationTokenSource();
                    _options = options;
                    _allowedPeers = allowed;
                    EventTo = ConnectorEventTo.Format(local, candidate);
                    var token = _cts.Token;
                    _ = Task.Run(() => AcceptLoopAsync(listener, token), token);
                    log?.Invoke(LocalizationService.Pick(
                        $"Ereignis-Listener auf Port {candidate} ({EventTo}){(options.UseTls ? ", TLS" : "")}",
                        $"Event listener on port {candidate} ({EventTo}){(options.UseTls ? ", TLS" : "")}"));
                    return true;
                }
                catch (SocketException)
                {
                    // Belegter Port – weiterzählen.
                }
            }

            log?.Invoke(LocalizationService.Pick(
                "Ereignis-Listener konnte keinen freien Port besetzen – reine Pollabfrage",
                "Event listener could not bind a free port – polling only"));
            return false;
        }
    }

    private static bool SameOptions(ConnectorEventSinkOptions? a, ConnectorEventSinkOptions b)
    {
        if (a is null)
            return false;
        return string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
               && a.UseTls == b.UseTls
               && string.Equals(a.PinnedFingerprint, b.PinnedFingerprint, StringComparison.OrdinalIgnoreCase)
               && ReferenceEquals(a.Credentials, b.Credentials);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            if (client.Client.RemoteEndPoint is not IPEndPoint remote || !ConnectorEventTo.IsAllowedPeer(remote, _allowedPeers))
            {
                try { client.Dispose(); } catch { /* ignore */ }
                continue;
            }

            _ = Task.Run(() => ReadLoopAsync(client, ct), ct);
        }
    }

    private async Task ReadLoopAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                Stream stream = client.GetStream();
                TlsServerProtocol? tls = null;
                try
                {
                    var options = _options;
                    if (options?.UseTls == true && options.Credentials is not null)
                    {
                        tls = new TlsServerProtocol(stream);
                        var server = new ConnectorTlsServer(options.Credentials, options.PinnedFingerprint, requireClientCertificate: false);
                        await Task.Run(() => tls.Accept(server), ct);
                        stream = tls.Stream;
                    }

                    var buffer = new byte[8192];
                    var reader = new ConnectorCetp.Reader();
                    while (!ct.IsCancellationRequested)
                    {
                        var read = await stream.ReadAsync(buffer, ct);
                        if (read <= 0)
                            return;

                        reader.Add(buffer.AsSpan(0, read));
                        if (reader.Closed)
                            return;

                        while (reader.TryDequeueXml(out var xml))
                            Dispatch(xml);
                    }
                }
                finally
                {
                    try { tls?.Close(); } catch { /* ignore */ }
                }
            }
            catch (OperationCanceledException)
            {
                // normaler Shutdown
            }
            catch (Exception)
            {
                // Verbindung zerschossen – nächster Versuch kommt vom Konnektor.
            }
        }
    }

    private void Dispatch(string xml)
    {
        var evt = ConnectorProtocol.ParseEvent(xml);
        if (evt is null)
            return;
        try
        {
            EventReceived?.Invoke(evt);
        }
        catch
        {
            // Anzeigefehler sollen den Listener nicht kippen.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
        _cts = null;
        try { _listener?.Stop(); } catch { /* ignore */ }
        _listener = null;
        EventTo = null;
        _options = null;
        _allowedPeers = [];
    }
}
