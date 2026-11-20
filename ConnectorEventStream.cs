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
    /// Holt die naechstfertige XML-Nachricht aus dem Puffer.
    /// </summary>
    /// <remarks>
    /// Der Konnektor liefert eine SOAP-Huelle, und die kommt ueblicherweise in mehreren
    /// TCP-Stuecken an. Ein Schnitt auf das INHERE </Event> – also die fruehere Fassung –
    /// warf bei einer verteilten Nachricht ein halbes Dokument weg: XDocument.Parse
    /// scheiterte, ParseEvent lieferte null, das Ereignis war lautlos weg. Geschnitten wird
    /// deshalb erst am aeussersten Schluss-Tag, und nur wenn mit Sicherheit feststeht, dass
    /// die Nachricht vollstaendig ist. Ein laufender envelope-Anfang haelt die Innen-Tag
    /// bis zum naechsten Lesedurchgang zurueck.
    /// </summary>
    private static bool TakeMessage(ref StringBuilder pending, out string message)
    {
        message = "";
        var text = pending.ToString();
        const string envelopeClosing = "</Envelope>";
        const string eventClosing = "</Event>";

        var envelopeEnd = text.IndexOf(envelopeClosing, StringComparison.OrdinalIgnoreCase);
        if (envelopeEnd >= 0)
        {
            Cut(ref pending, text, envelopeEnd + envelopeClosing.Length, out message);
            return true;
        }

        // Ohne Huelle ist das Ereignis selbst die ganze Nachricht – aber nur, solange nicht
        // gerade eine Huelle laeuft, deren Ende noch fehlt. Der Oeffnungsanker ist mit
        // Vorsicht zu sehen: ueblich steht ein Namespace-Praefix dazwischen (<soap:Envelope>),
        // ein blosses Suchen auf "<Envelope" haette die Huelle uebersehen und zu frueh
        // geschnitten – derselbe Fehler, den die fruehere Fassung am Innen-Tag beging.
        if (!HasOpeningEnvelope(text))
        {
            var eventEnd = text.IndexOf(eventClosing, StringComparison.OrdinalIgnoreCase);
            if (eventEnd < 0)
            {
                Guard(ref pending, text);
                return false;
            }

            Cut(ref pending, text, eventEnd + eventClosing.Length, out message);
            return true;
        }

        Guard(ref pending, text);
        return false;
    }

    /// <summary>
    /// Steht irgendwo ein oeffnendes Envelope-Element? Es kann ein Praefix voranstehen
    /// (<soap:Envelope>, <ns1:Envelope>), deshalb wird beides geprueft.
    /// </summary>
    private static bool HasOpeningEnvelope(string text)
    {
        var at = text.IndexOf("Envelope", StringComparison.OrdinalIgnoreCase);
        while (at > 0)
        {
            var before = text[at - 1];
            if (before is '<' or ':')
                return true;
            at = text.IndexOf("Envelope", at + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>Schneidet die Nachricht bis <paramref name="end"/> ab und stellt den Rest zurueck.</summary>
    private static void Cut(ref StringBuilder pending, string text, int end, out string message)
    {
        message = text[..end];
        pending.Clear();
        if (end < text.Length)
            pending.Append(text[end..]);
    }

    /// <summary>Ein beschaedigter Stream darf den Puffer nicht endlos anwachsen lassen.</summary>
    private static void Guard(ref StringBuilder pending, string text)
    {
        if (text.Length > 2_000_000)
            pending.Clear();
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
