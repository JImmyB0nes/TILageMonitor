using System.Net.NetworkInformation;

namespace TILageMonitor;

/// <summary>
/// Periodische Hintergrundabfrage des Konnektors. Meldet neu auftretende Probleme einmalig
/// per Toast und benachrichtigt bei Wiederherstellung.
///
/// Zwei Ergaenzungen gegenueber dem reinen Pollen:
///  - Ereignis-Abos: der Konnektor meldet Karte rein/raus bzw. Terminal getrennt per cetp://,
///    woraufhin sofort ein Abbruch des Wartens und eine neue Abfrage angestoessen wird.
///  - Lokaler Konnektor-Verlauf, damit Timeline und Stoerdauer auch ohne gematik-API funktionieren.
/// </summary>
public sealed class ConnectorMonitor : IDisposable
{
    private readonly Func<AppSettings> _settingsProvider;
    private readonly HashSet<string> _activeAlertKeys = new(StringComparer.Ordinal);
    private readonly ConnectorEventStream _events = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _consecutiveFailures;
    private bool _unreachableNotified;
    private bool _subscribed;
    private bool _subscriptionAttempted;
    private DateTime _lastEventPoll = DateTime.MinValue;

    /// <summary>Mindestabstand zwischen zwei ereignisgesteuerten Abfragen, damit ein
    /// fehlerhaftes Topic mit Dauuerereignissen nicht zum Entenrennen fuehrt.</summary>
    private static readonly TimeSpan EventPollCooldown = TimeSpan.FromSeconds(10);

    public ConnectorSnapshot? LastSnapshot { get; private set; }
    public IReadOnlyList<ConnectorAlert> LastAlerts { get; private set; } = [];
    public string? LastError { get; private set; }
    public DateTime? LastAttempt { get; private set; }

    /// <summary>Letztes verarbeitete Konnektor-Ereignis (fuer Anzeige und Diagnosebericht).</summary>
    public ConnectorEvent? LastEvent { get; private set; }

    /// <summary>
    /// Karte rein/raus, moeglichst frueh erkannt. Steht ein Ereignis bereit, fuehrt es die
    /// Anzeige – andernfalls uebernimmt der letzte Datenstand, damit nie etwas ganz fehlt.
    /// </summary>
    public ConnectorPresence? LastPresence { get; private set; }

    /// <summary>Bemerkung des Lauschers, z. B. "kein freier Port" – ohne eigenes Ereignis.</summary>
    public string? LastEventNote { get; private set; }

    /// <summary>Lauscht der Listener bereits – und damit: moeglich, ohne dass ein Abo geklappt hat.</summary>
    public bool EventStreamActive => _events.IsListening;

    /// <summary>Wird nach jedem Abruf auf dem Thread-Pool ausgelöst.</summary>
    public event Action? Updated;

    public ConnectorMonitor(Func<AppSettings> settingsProvider) => _settingsProvider = settingsProvider;

    public void Restart()
    {
        Stop();
        var settings = _settingsProvider().Connector;
        if (!settings.Enabled || !settings.MonitoringEnabled || !settings.IsConfigured)
        {
            LastSnapshot = null;
            LastAlerts = [];
            LastError = null;
            // Ohne ueberwachung darf nichts nachstehen, was nach Abschaltung der Anbindung
            // noch auf "Karte rein" steht – dieselbe Begruendung, die fuer LastSnapshot gilt.
            LastPresence = null;
            LastEvent = null;
            Updated?.Invoke();
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => LoopAsync(token), token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
        _activeAlertKeys.Clear();
        _consecutiveFailures = 0;
        _unreachableNotified = false;
        _subscribed = false;
        _subscriptionAttempted = false;
        _events.EventReceived -= OnEvent;
        _events.Dispose();
    }

    /// <summary>Übernimmt einen Datenstand aus dem Konnektor-Fenster, damit Anzeige und Tray konsistent sind.</summary>
    public void Accept(ConnectorSnapshot snapshot)
    {
        var settings = _settingsProvider().Connector;
        LastSnapshot = snapshot;
        LastError = null;
        LastAttempt = DateTime.Now;
        LastAlerts = ConnectorHealth.Evaluate(snapshot, settings.CertificateWarningDays, DateTime.Now);

        // Das Fenster uebergibt hier seinen eigenen Stand, der aelter sein kann als das, was
        // der Lauscher gerade gemeldet hat. Dieselbe Kante wie im Pollabgleich, sonst
        // dreht ein Accept die frische Anwesenheit auf den Datenstand zurueck.
        RefreshPresence(snapshot);
        if (settings.HistoryEnabled)
            ConnectorHistoryStore.UpsertNow(snapshot, DateTime.Now);
        Updated?.Invoke();
    }

    /// <summary>
    /// Fuehrt die Anwesenheit nach: ein frisches Ereignis fuehrt, sonst der Datenstand. Dieselbe
    /// Kante an Poll- und Accept-Pfad, damit der eine den anderen nicht rueckgaengig macht.
    /// </summary>
    private void RefreshPresence(ConnectorSnapshot snapshot)
    {
        if (ConnectorPresence.FromEvent(LastEvent) is { } fromEvent && (DateTime.Now - fromEvent.At).TotalSeconds <= ConnectorPresence.LiveWindowSeconds)
            LastPresence = fromEvent;
        else
            LastPresence = ConnectorPresence.FromSnapshot(snapshot);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var settings = _settingsProvider().Connector;
            await PollOnceAsync(settings, ct);
            try
            {
                // Mit erfolgreichem Abo darf seltener gepollt werden, weil Ereignisse ohnehin
                // sofort nachziehen. Ohne Abo bleibt es beim eingestellten Intervall.
                var minutes = _subscribed ? Math.Clamp(settings.PollMinutes * 2, 10, 120) : Math.Clamp(settings.PollMinutes, 1, 120);
                await Task.Delay(TimeSpan.FromMinutes(minutes), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollOnceAsync(ConnectorSettings settings, CancellationToken ct)
    {
        if (!await _pollGate.WaitAsync(TimeSpan.FromSeconds(30), ct))
            return; // ereignisgesteuerte Abfrage ist gerade am Werk

        LastAttempt = DateTime.Now;
        try
        {
            using var client = new ConnectorClient(settings);
            var snapshot = await client.GetSnapshotAsync(includeCertificates: false, ct);
            ct.ThrowIfCancellationRequested(); // Neustart während des Abrufs: Ergebnis verwerfen
            LastSnapshot = snapshot;
            LastError = null;

            if (_unreachableNotified)
            {
                Notify(L("Konnektor wieder erreichbar", "Connector reachable again"), L("Die Verbindung zum Konnektor ist wiederhergestellt.", "The connection to the connector has been restored."), ToastUrgency.Info, "konnektor");
            }
            _consecutiveFailures = 0;
            _unreachableNotified = false;

            var alerts = ConnectorHealth.Evaluate(snapshot, settings.CertificateWarningDays, DateTime.Now);
            LastAlerts = alerts;

            // Ein Ereignis darf nicht vom naechsten Poll uebermaalt werden, sonst faellt der
            // Indikator zuruck, obwohl die Karte laengst bekannt ist. Nur der eigene Stand des
            // Polls ersetzt ihn, wenn kein frisches Ereignis vorliegt.
            RefreshPresence(snapshot);

            foreach (var alert in alerts.Where(a => !_activeAlertKeys.Contains(a.Key)))
                Notify(alert.Title, alert.Body, alert.Urgency, "konnektor");

            _activeAlertKeys.Clear();
            foreach (var alert in alerts)
                _activeAlertKeys.Add(alert.Key);

            if (settings.EventSubscriptionsEnabled)
                await EnsureSubscriptionsAsync(client, ct);

            if (settings.HistoryEnabled)
                ConnectorHistoryStore.UpsertNow(snapshot, DateTime.Now);

            // Konfigurationsabgleich ueber Zeit: meldet nur Verstellungen, keine Normalfaelle.
            // Bewusst auerhalb des Verlastkastens – der Vergleich braucht keine Ablage in der
            // Timeline, nur den letzten Stand. Wer den Verlauf abschaltet, dem faellt sonst
            // lautlos auch die Warnung vor still gerollter Firmware oder verlegten Terminals weg.
            foreach (var change in ConnectorConfigurationDiff.CompareAndRemember(snapshot))
                Notify(L("Konnektor-Konfiguration geaendert", "Connector configuration changed"), change, ToastUrgency.Warning, "konnektor-konfig");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _consecutiveFailures++;
            if (_consecutiveFailures >= 2 && !_unreachableNotified)
            {
                _unreachableNotified = true;
                Notify(L("Konnektor nicht erreichbar", "Connector unreachable"), ex.Message, ToastUrgency.Error, "konnektor");
            }
        }
        finally
        {
            _pollGate.Release();
        }

        Updated?.Invoke();
    }

    /// <summary>
    /// Legt einmalig Abos auf die wesentlichen Ereignisse an. Schlaegt etwas fehl, bleibt die
    /// App beim Pollen – die Benutzereinstellung darf keine Stoerung ausloesen.
    /// </summary>
    private async Task EnsureSubscriptionsAsync(ConnectorClient client, CancellationToken ct)
    {
        if (_subscribed)
            return;

        // Ein Anlauf pro Neustart: ohne diesen Schutz wurde jeder Poll erneut einen Port
        // besetzen und funf Subscribe-Aufrufe senden, wenn die Firmware keine Abos annimmt.
        if (_subscriptionAttempted)
            return;
        _subscriptionAttempted = true;

        if (!_events.IsListening && !_events.EnsureListening(msg => LastEventNote = msg))
            return;

        var succeeded = 0;
        foreach (var topic in EventTopics())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (id, _) = await client.SubscribeAsync(_events.EventTo ?? "", topic, ct);
                if (!string.IsNullOrWhiteSpace(id))
                    succeeded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Topic von dieser Firmware nicht unterstuetzt – naechstes probieren.
            }
        }

        _subscribed = succeeded > 0;
        if (_subscribed)
        {
            _events.EventReceived += OnEvent;
        }
        else
        {
            // Kein einziges Abo geschafft: Listener wieder zuhalten und beim Pollen bleiben.
            _events.EventReceived -= OnEvent;
            _events.Dispose();
            LastEventNote = L("Kein Ereignis-Abo durchgefuehrt – die App pollt im eingestellten Intervall weiter.",
                "No event subscription succeeded – the app keeps polling at the configured interval.");
        }
    }

    /// <summary>
    /// Typische Topics. Die Schreibweise variiert je Hersteller, deshalb mehrere Varianten –
    /// unbekannte laufen einfach ins Leere.
    /// </summary>
    private static IEnumerable<string> EventTopics() =>
    [
        "http://ws.gematik.de/conn/EventService/v7.2#CardInserted",
        "http://ws.gematik.de/conn/EventService/v7.2#CardRemoved",
        "http://ws.gematik.de/conn/EventService/v7.2#CardTerminalConnected",
        "http://ws.gematik.de/conn/EventService/v7.2#CardTerminalDisconnected",
        "http://ws.gematik.de/conn/EventService/v7.2#StatusChanged"
    ];

    private void OnEvent(ConnectorEvent evt)
    {
        LastEvent = evt;

        // Karte rein/raus sofort, nicht erst nach dem naechsten Abgleich: wer die Karte zieht,
        // will das ohne Umweg sehen, waehrend die eigentlichen Einzelheiten nachgezogen werden.
        if (ConnectorPresence.FromEvent(evt) is { } presence)
            LastPresence = presence;

        Updated?.Invoke();

        // Abo ist aktiv, aber die Anzeige soll trotzdem aktuelle Daten zeigen: sofort nachziehen.
        if (DateTime.Now - _lastEventPoll < EventPollCooldown)
            return;
        _lastEventPoll = DateTime.Now;

        _ = Task.Run(async () =>
        {
            var settings = _settingsProvider().Connector;
            await PollOnceAsync(settings, CancellationToken.None);
        });
    }

    /// <summary>
    /// Praeft, ob der lokale Port ueberhaupt erreichbar ist. Nur informativ – ein Block durch
    /// die Windows-Firewall zeigt sich sonst als "Abo erfolgreich, aber nie ein Ereignis".
    /// </summary>
    public string DescribeEventStream()
    {
        if (_subscribed)
            return L($"Ereignis-Abos aktiv, lauscht auf {_events.EventTo}", $"event subscriptions active, listening on {_events.EventTo}");

        // Noch kein oder kein einziges Abo geschafft – Begruendung aus dem Listener nennen,
        // damit der Nutzer nicht raeunen muss, warum Karte rein/raus nur verspaetet ankommt.
        var reason = LastEventNote ?? L("noch kein Abo versucht", "no subscription attempted yet");
        var blocked = _events.IsListening && !IsPortUsable();
        return reason + (blocked
            ? "\n" + L("Der Port koennte durch die Windows-Firewall blockiert sein.", "The port may be blocked by Windows Firewall.")
            : "");
    }

    private bool IsPortUsable()
    {
        if (_events.EventTo is not { } target)
            return true;
        var idx = target.LastIndexOf(':');
        if (idx <= 0 || !int.TryParse(target[(idx + 1)..], out var port))
            return true;

        try
        {
            var listener = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            return listener.Any(e => e.Port == port);
        }
        catch
        {
            return true;
        }
    }

    private static string L(string de, string en) => LocalizationService.Pick(de, en);

    private static void Notify(string title, string body, ToastUrgency urgency, string focusKey)
    {
        // Toast-/Balloon-APIs am UI-Thread aufrufen (NotifyIcon-Fallback ist WinForms).
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;
        dispatcher.BeginInvoke(() => ToastService.Show(title, body, urgency, focusKey));
    }

    public void Dispose()
    {
        Stop();
        _pollGate.Dispose();
    }
}
