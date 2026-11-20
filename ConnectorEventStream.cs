using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TILageMonitor;

/// <summary>
/// Haelt den TCP-Listener fuer Konnektor-Ereignisse ("cetp://"). Der Konnektor baut die
/// Verbindung aktiv zum Client auf, deshalb hoert die App auf einem lokalen Port und meldet
/// diese Adresse beim Abo als EventTo.
///
/// Warum ueberhaupt: ohne Abo erkennt die App eine gesteckte oder gezogene Karte erst beim
/// naechsten Poll (Standard 5 Minuten). Mit Abo kommt die Meldung sofort.
///
/// Robust ausgelegt: schlaegt das Abo fehl (Topic unbekannt, Firmware ohne EventSupport),
/// faellt die App kommentarlos auf reines Pollen zurueck. Der Listener bleibt trotzdem
/// aktiv, damit man manuell eingetragene Abos des Konnektors nicht ueberrascht verliert.
/// </summary>
public sealed class ConnectorEventStream : IDisposable
{
    /// <summary>Portbereich, damit mehrere Instanzen und andere Apps nicht kollidieren.</summary>
    private const int BasePort = 48150;
    private const int MaxPortAttempts = 12;

    private readonly object _gate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _port;

    /// <summary>Lokale Adresse, die gegenueber dem Konnektor als EventTo genannt wird.</summary>
    public string? EventTo { get; private set; }

    public bool IsListening
    {
        get
        {
            lock (_gate)
                return _listener is not null;
        }
    }

    /// <summary>Wird fuer jedes empfangene Ereignis ausgelöst (Thread-Pool).</summary>
    public event Action<ConnectorEvent>? EventReceived;

    /// <summary>
    /// Startet den Listener, falls noch nicht geschehen. Bei Erfolg wird die EventTo-Adresse
    /// gesetzt; der Aufrufer entscheidet, ob er damit ein Abo anlegt.
    /// </summary>
    public bool EnsureListening(Action<string>? log = null)
    {
        lock (_gate)
        {
            if (_listener is not null)
                return true;

            for (var attempt = 0; attempt < MaxPortAttempts; attempt++)
            {
                var candidate = BasePort + attempt;
                try
                {
                    var listener = new TcpListener(IPAddress.Any, candidate);
                    listener.Start();
                    _listener = listener;
                    _port = candidate;
                    _cts = new CancellationTokenSource();
                    EventTo = $"cetp://{LocalAddressFor(listener)}:{candidate}";
                    _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
                    log?.Invoke($"Ereignis-Listener auf Port {candidate} ({EventTo})");
                    return true;
                }
                catch (SocketException)
                {
                    // Belegter Port – weiterzählen.
                }
            }

            log?.Invoke("Ereignis-Listener konnte keinen freien Port besetzen – reine Pollabfrage");
            return false;
        }
    }

    /// <summary>
    /// Ermittelt die IP, die der Konnektor erreichen kann. Ein einzelnes Socket zu seiner
    /// Basis-URL liefert die richtige Quelle, auch bei mehreren Netzwerkkarten.
    /// </summary>
    private static string LocalAddressFor(TcpListener listener)
    {
        if (listener.LocalEndpoint is not IPEndPoint endpoint)
            return "127.0.0.1";
        if (!IPAddress.Any.Equals(endpoint.Address))
            return endpoint.Address.ToString();

        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect("10.255.255.255", 65530); // route only, no traffic sent
            if (probe.LocalEndPoint is IPEndPoint local)
                return local.Address.ToString();
        }
        catch
        {
            // Kein Standardweg – besser zuhoeren als 0.0.0.0 zu nennen.
        }
        return "127.0.0.1";
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
                return; // Listener wurde verworfen
            }

            _ = Task.Run(() => ReadLoopAsync(client, ct), ct);
        }
    }

    private async Task ReadLoopAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var buffer = new byte[8192];
            var pending = new StringBuilder();
            try
            {
                using var stream = client.GetStream();
                while (!ct.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer, ct);
                    if (read <= 0)
                        return;

                    pending.Append(Encoding.UTF8.GetString(buffer, 0, read));

                    // Der Konnektor schickt mehrere XML-Nachrichten hintinander. Alles, was
                    // bereits ein schliessendes Envelope-Ende enthaelt, sofort verarbeiten.
                    while (TakeMessage(ref pending, out var message))
                        Dispatch(message);
                }
            }
            catch (OperationCanceledException)
            {
                // normaler Shutdown
            }
            catch (Exception)
            {
                // Verbindung zerschossen – naechster Versuch kommt vom Konnektor.
            }
        }
    }

    /// <summary>
    /// Holt eine moeglichst vollstaendige XML-Nachricht aus dem Puffer. Ein geschlossenes
    /// Dokument wird am passenden "</Envelope>" bzw. "</Event>" geschnitten; bleibt nichts
    /// uebrig, wird verworfen, damit ein beschadigter Stream nicht endlos anwaechst.
    /// </summary>
    private static bool TakeMessage(ref StringBuilder pending, out string message)
    {
        message = "";
        var text = pending.ToString();
        var eventEnd = text.LastIndexOf("</Event>", StringComparison.OrdinalIgnoreCase);
        var envelopeEnd = text.LastIndexOf("</Envelope>", StringComparison.OrdinalIgnoreCase);
        var closingTag = envelopeEnd > eventEnd ? "</Envelope>" : "</Event>";
        var end = Math.Max(eventEnd, envelopeEnd);

        if (end < 0)
        {
            if (text.Length > 2_000_000)
                pending.Clear();
            return false;
        }

        var start = text.IndexOf("<?xml", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            start = text.IndexOf('<', StringComparison.Ordinal);
        if (start < 0)
            start = 0;

        message = text.Substring(start, end + closingTag.Length - start);
        pending.Clear();
        pending.Append(text[(end + closingTag.Length)..]);
        return message.Contains('<') && message.Contains('>');
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
            try { _cts?.Cancel(); } catch { /* ignore */ }
            _cts?.Dispose();
            _cts = null;
            try { _listener?.Stop(); } catch { /* ignore */ }
            _listener = null;
            EventTo = null;
        }
    }
}
