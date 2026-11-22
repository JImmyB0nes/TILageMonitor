using System.IO;
using System.Net;
using System.Net.Sockets;
using Org.BouncyCastle.Tls;

namespace TILageMonitor;

/// <summary>Parameter für die CETP-Ereignissenke (gemSpec_Kon TIP1-A_4594/4595/4596).</summary>
public sealed class ConnectorEventSinkOptions
{
    public required string Host { get; init; }

    /// <summary>TLS ist Pflicht (ANCL_TLS_MANDATORY). Ohne Pflicht erkennt die Senke TLS
    /// je Verbindung am ersten Byte und nutzt dann – falls vorhanden – das Zertifikat.</summary>
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

    /// <summary>Angenommene Verbindungen – werden beim Stoppen geschlossen, damit weder Sockets
    /// noch blockierte Lese-Threads (BouncyCastle-Streams ignorieren das CancellationToken)
    /// ueber einen Neustart hinaus haengen bleiben.</summary>
    private readonly HashSet<TcpClient> _clients = new();

    /// <summary>Hoechstdauer fuer den ersten Datenverkehr bzw. den TLS-Handshake.</summary>
    internal static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

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

            lock (_gate)
            {
                if (ct.IsCancellationRequested)
                {
                    try { client.Dispose(); } catch { /* ignore */ }
                    return;
                }
                _clients.Add(client);
            }
            var options = _options;
            _ = Task.Run(() => ReadLoopAsync(client, options, ct), ct);
        }
    }

    private async Task ReadLoopAsync(TcpClient client, ConnectorEventSinkOptions? options, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                await ReadConnectionAsync(client, options, ct);
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
        finally
        {
            lock (_gate)
                _clients.Remove(client);
        }
    }

    /// <summary>
    /// Liest eine Verbindung. Ob TLS oder reines CETP kommt, entscheidet das erste Byte
    /// (0x16 = TLS-Handshake, sonst CETP). Nach gemSpec_Kon TIP1-A_4595 nutzt der Konnektor TLS
    /// nur bei ANCL_TLS_MANDATORY=Enabled – eine feste Erwartung aus der App-Einstellung wuerde
    /// sonst jede Zustellung verwerfen.
    /// </summary>
    private async Task ReadConnectionAsync(TcpClient client, ConnectorEventSinkOptions? options, CancellationToken ct)
    {
        Stream stream = client.GetStream();
        TlsServerProtocol? tls = null;
        try
        {
            client.ReceiveTimeout = (int)HandshakeTimeout.TotalMilliseconds;
            var first = new byte[1];
            using (var firstByteTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                firstByteTimeout.CancelAfter(HandshakeTimeout);
                var peeked = await client.Client.ReceiveAsync(first, SocketFlags.Peek, firstByteTimeout.Token);
                if (peeked <= 0)
                    return;
            }

            if (IsTlsHandshake(first[0]))
            {
                if (options?.Credentials is null)
                    return; // TLS ohne Serverzertifikat nicht moeglich
                tls = new TlsServerProtocol(stream);
                var server = new ConnectorTlsServer(options.Credentials, options.PinnedFingerprint, requireClientCertificate: false);
                // Accept blockiert synchron; das ReceiveTimeout begrenzt einen haengenden Peer,
                // StopLocked schliesst den Socket und beendet ihn sofort.
                await Task.Run(() => tls.Accept(server), ct);
                stream = tls.Stream;
            }

            // Nach dem Aufbau darf die Verbindung beliebig lange ruhen (Ereignisse sind selten).
            client.ReceiveTimeout = 0;

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
                if (reader.Closed)
                    return;
            }
        }
        finally
        {
            try { tls?.Close(); } catch { /* ignore */ }
        }
    }

    /// <summary>TLS-Record "Handshake" beginnt mit 0x16; CETP mit 'C'.</summary>
    internal static bool IsTlsHandshake(byte first) => first == 0x16;

    private void Dispatch(string xml)
    {
        try
        {
            // Auch ein unerwartet aufgebautes Ereignis darf die Verbindung nicht kippen –
            // sonst geht mit ihm jedes folgende Ereignis derselben Verbindung verloren.
            var evt = ConnectorProtocol.ParseEvent(xml);
            if (evt is null)
                return;
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
        foreach (var client in _clients.ToList())
        {
            try { client.Dispose(); } catch { /* ignore */ }
        }
        _clients.Clear();
        EventTo = null;
        _options = null;
        _allowedPeers = [];
    }
}
