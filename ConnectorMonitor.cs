using System.IO;
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

    /// <summary>
    /// Ob der Konnektor die eigenen Abos zuletzt noch gefuehrt hat. Wird aus dem Datenstand
    /// gezogen, nicht aus dem Erfolg eines frueheren Subscribe-Aufrufs – nur so faellt ein
    /// abgelaufenes Abo ueberhaupt auf.
    /// </summary>
    private bool _subscriptionsAlive;
    private bool _eventHandlerRegistered;
    private DateTime _lastEventPoll = DateTime.MinValue;

    /// <summary>Mindestabstand zwischen zwei ereignisgesteuerten Abfragen, damit ein
    /// fehlerhaftes Topic mit Dauuerereignissen nicht zum Entenrennen fuehrt.</summary>
    private static readonly TimeSpan EventPollCooldown = TimeSpan.FromSeconds(10);

    /// <summary>Wie oft die Signaturzertifikate nachgelesen werden – selten, weil ihr Ablauf
    /// kein Vorgang ist, der im Fuenfminutentakt passiert.</summary>
    private static readonly TimeSpan QesReadInterval = TimeSpan.FromHours(12);
    private DateTime _lastQesRead = DateTime.MinValue;

    /// <summary>Das Signaturzertifikat je Karte, damit der Alarm zwischen zwei Nachlesen
    /// nicht verschwindet. Nachgezogen wird ueber die Kartengriffangabe, die der Konnektor
    /// ueber die Laufzeit gleich haelt.</summary>
    private readonly Dictionary<string, ConnectorCertificateInfo> _qesByCard = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Ob der Konnektor die Abos dieser App beim letzten Datenstand noch gefuehrt hat. Nicht
    /// danach gefragt, ob mal ein Subscribe durchkam – das ist nach einer Woche das Einzige,
    /// was die App noch zu wissen glaubt, und ein abgelaufenes Abo steht genau daneben.
    /// </summary>
    public bool SubscriptionsAlive => _subscriptionsAlive;

    /// <summary>Adresse, auf die die eigenen Abos zeigen. Leer, solange kein Lauscher steht –
    /// damit das Fenster sie nicht mit einer Adresse aus einer frueheren Sitzung vergleicht.</summary>
    public string? EventTarget => _events.EventTo;

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
        _subscriptionsAlive = false;
        _subscriptionAttempted = false;
        _events.EventReceived -= OnEvent;
        _eventHandlerRegistered = false;
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
                // Seltener gepollt wird nur, wenn die Abos nachweislich noch stehen. Ein
                // abgelaufenes Abo liefert nichts mehr, und wer dann nach dem eigenen Erfolg
                // geht, pollt halb so oft wie ohne Abo – schlechter als ohne, bei gleicher
                // Erklaerung. Dasselbe Urteil wie in der Anzeige, damit nicht die eine Stelle
                // "aktiv" behauptet und die andere daneben pollt.
                var minutes = _subscriptionsAlive ? Math.Clamp(settings.PollMinutes * 2, 10, 120) : Math.Clamp(settings.PollMinutes, 1, 120);
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
            await ReadQesAsync(client, snapshot, ct);
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
                await EnsureSubscriptionsAsync(client, snapshot, ct);

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
    /// Stellt sicher, dass die eigenen Abos noch stehen. Schlaegt etwas fehl, bleibt die App
    /// beim Pollen – die Benutzereinstellung darf keine Stoerung ausloesen.
    /// </summary>
    /// <remarks>
    /// Die Frage steht nicht mehr "haben wir es mal geschafft", sondern "steht es noch da".
    /// Dafuer reicht der Datenstand, der ohnehin schon hereingelesen wird: GetSubscription
    /// liefert die aktiven Abos samt Ablaufzeit. Ein Abo, das abgelaufen ist, war die meiste
    /// Zeit unsichtbar – die Anzeige haelt "Abos aktiv", der Konnektor sendet nichts mehr, und
    /// weil _subscribed auf true steht, wird auch noch seltener gepollt als ohne Abo. Der
    /// Fehler waehre lautlos, fuer unbegrenzte Zeit, und nur am ersten Tag faellig.
    /// </remarks>
    private async Task EnsureSubscriptionsAsync(ConnectorClient client, ConnectorSnapshot snapshot, CancellationToken ct)
    {
        var asked = !snapshot.Warnings.Any(w => w.Contains("GetSubscription", StringComparison.Ordinal));
        var ours = Ours(snapshot);
        var action = ConnectorSubscriptionPolicy.Decide(_subscriptionAttempted, _subscribed, asked, ours, DateTime.Now);
        _subscriptionsAlive = ours.Any(s => s.Alive(DateTime.Now)) || (action == ConnectorSubscriptionPolicy.Action.None && _subscribed);

        if (action == ConnectorSubscriptionPolicy.Action.None)
            return;
        if (action == ConnectorSubscriptionPolicy.Action.GiveUp)
        {
            if (!_subscribed)
            {
                LastEventNote = LastEventNote ?? LocalizationService.Pick(
                    "Kein Ereignis-Abo durchführbar – die App pollt im eingestellten Intervall weiter.",
                    "Event subscription not available – the app keeps polling at the configured interval.");
            }
            return;
        }

        _subscriptionAttempted = true;
        var settings = _settingsProvider().Connector;
        var useTls = settings.UseTls || snapshot.Directory?.TlsMandatory == true;
        ConnectorClientCredentials? credentials = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(settings.ClientCertificatePath) && File.Exists(settings.ClientCertificatePath))
                credentials = ConnectorClient.LoadClientCertificate(settings.ClientCertificatePath, SecretProtector.Unprotect(settings.ClientCertificatePasswordProtected));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastEventNote = ex.Message;
            if (useTls)
                return;
        }

        var options = new ConnectorEventSinkOptions
        {
            Host = settings.Host,
            UseTls = useTls,
            Credentials = credentials,
            PinnedFingerprint = settings.PinnedServerThumbprint
        };
        if (!_events.IsListening && !_events.EnsureListening(options, msg => LastEventNote = msg))
            return;

        RegisterEventHandler();

        if (action == ConnectorSubscriptionPolicy.Action.Renew)
        {
            var ids = ours.Where(s => !string.IsNullOrWhiteSpace(s.SubscriptionId)).Select(s => s.SubscriptionId).ToList();
            try
            {
                ct.ThrowIfCancellationRequested();
                if (ids.Count > 0)
                {
                    await client.RenewSubscriptionsAsync(ids, ct);
                    _subscribed = true;
                    _subscriptionsAlive = true;
                    LastEventNote = LocalizationService.Pick("Ereignis-Abos verlängert.", "Event subscriptions renewed.");
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LastEventNote = LocalizationService.Pick(
                    "Abo-Verlängerung fehlgeschlagen, neuer Anlauf: " + ex.Message,
                    "Subscription renewal failed, retrying: " + ex.Message);
                // fall through to Subscribe
            }
        }

        var succeeded = 0;
        foreach (var topic in ConnectorTopics.DefaultSubscriptions)
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
                // Topic von dieser Firmware nicht unterstützt – nächstes probieren.
            }
        }

        _subscribed = succeeded > 0;
        if (_subscribed)
        {
            _subscriptionsAlive = true;
            LastEventNote = LocalizationService.Pick($"Ereignis-Abos angelegt ({succeeded}).", $"Event subscriptions created ({succeeded}).");
        }
        else
        {
            _events.EventReceived -= OnEvent;
            _eventHandlerRegistered = false;
            _events.Dispose();
            LastEventNote = LocalizationService.Pick(
                "Kein Ereignis-Abo durchgeführt – die App pollt im eingestellten Intervall weiter.",
                "No event subscription succeeded – the app keeps polling at the configured interval.");
        }
    }

    private void RegisterEventHandler()
    {
        if (_eventHandlerRegistered)
            return;
        _events.EventReceived += OnEvent;
        _eventHandlerRegistered = true;
    }

        /// <summary>
    /// Liest C.QES nach, damit der Hintergrund ueberhaupt bemerkt, dass qualifizierte
    /// Signaturen ins Leere laufen. Bewusst nicht jeder Poll: die Abfrage ist teuer, und ein
    /// Ablauf ist kein Vorgang, der alle fuenf Minuten passiert. Ein Versuch am Tag reicht, und
    /// er betrifft nur Karten, die ueberhaupt signieren koennen – eine eGK fuehrt kein C.QES.
    /// </summary>
    /// <remarks>
    /// Ohne diesen Schritt ist der QES-Alarm der einzige, den der Hintergrund nie liefert: Er
    /// braucht ein Zertifikat, der Hintergrund laesst es aber nicht lesen. Die Karte wirkt dann
    /// einwandfrei, die PIN passt, und trotzdem scheitert jede Signatur – bis jemand manuell
    /// „Alle Zertifikate lesen" drueckt.
    /// </remarks>
    private async Task ReadQesAsync(ConnectorClient client, ConnectorSnapshot snapshot, CancellationToken ct)
    {
        if (DateTime.Now - _lastQesRead < QesReadInterval)
        {
            // Nachlesen ist selten, Bewerten ist staendig. Ohne die Uebernahme wuerde der
            // Alarm nur auf dem einen Poll erscheinen, auf dem gelesen wurde, und am
            // naechsten wieder weg sein – als Alarm taugt er so nicht, weil er bloe
            // anzeigt, dass eben gerade gelesen wurde.
            foreach (var card in snapshot.Cards)
                if (_qesByCard.TryGetValue(card.CardHandle, out var known))
                    card.Certificates.Add(known);
            return;
        }

        _lastQesRead = DateTime.Now;
        _qesByCard.Clear();

        foreach (var card in snapshot.Cards.Where(c => c.SupportsPinManagement))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var qes = await client.ReadCardCertificateAsync(card.CardHandle, "C.QES", ct);
                if (qes is not null)
                {
                    card.Certificates.Add(qes);
                    _qesByCard[card.CardHandle] = qes;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Karte fuehrt kein C.QES – das ist der Regelfall, keine Meldung.
            }
        }
    }

    /// <summary>
    /// Die Abos, die auf diese App zeigen. Getrennt wird nach Adresse, nicht nach Topic: der
    /// Konnektor faehrt dieselbe Adresse mehrfach vor, wenn ein frueherer Anlauf fehl
    /// geschlagen hat, und wer dann nach dem Topic sucht, haelt die Menge fuer leer.
    /// </summary>
    private List<ConnectorSubscription> Ours(ConnectorSnapshot snapshot)
    {
        // Der Hafen ist der wechselnde Teil, die Maschine ist es nicht. Dieselbe App, die
        // nach einem besetzten Port auf den naechsten ausweicht, haelt ihr Abo andernfalls
        // fuer verschwunden, obwohl es auf dem alten Hafen weiter laeuft.
        var host = ConnectorSubscription.HostOf(_events.EventTo);
        if (host is null)
            return [];
        return snapshot.Subscriptions.Where(s => s.FromHost(host)).ToList();
    }

    /// <summary>
    /// Laeuft die Abos zurueck, nachdem das Fenster sie gekuendigt hat. Ohne diesen Schritt
    /// behielt der Lauscher "Abos aktiv" und stünde unter der leeren Liste genau das Gegenteil
    /// davon. Der Anlauf bleibt fuers Protokoll auf true gestellt, sonst wuerde der naechste
    /// Poll dieselben Adressen sofort wieder besetzen – die Kuendigung waere wirkungslos. Wer
    /// zurueck zum Abo will, schaltet die Einstellung um (Neustart setzt beide Schalter).
    /// </summary>
    public void ForgetSubscriptions()
    {
        _subscribed = false;
        _subscriptionsAlive = false;
        _subscriptionAttempted = true;
        _events.EventReceived -= OnEvent;
        _eventHandlerRegistered = false;
        _events.Dispose();
        LastEventNote = L("Alle Abos dieser App gekuendigt – die App pollt wieder.",
            "All subscriptions of this app cancelled – the app polls again.");
        Updated?.Invoke();
    }

    /// <summary>
    /// Typische Topics. Der Namespace kommt aus dem Geaechtnis, nicht aus einer Geschriebenen –
    /// der Konnektor hat damit schon geantwortet, und ein Abo auf eine ihm fremde Version ist
    /// genau der Fall, der als Erfolg durchgeht, ohne dass je ein Ereignis ankommt.
    /// </summary>
    private static IReadOnlyList<string> EventTopics() => ConnectorTopics.DefaultSubscriptions;

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

        var token = _cts?.Token ?? new CancellationToken(canceled: true);
        if (token.IsCancellationRequested)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                var settings = _settingsProvider().Connector;
                await PollOnceAsync(settings, token);
            }
            catch (OperationCanceledException)
            {
                // Monitor wurde beendet.
            }
            catch
            {
                // Einzelner Ereignis-Poll darf den Listener nicht kippen.
            }
        }, token);
    }

    /// <summary>
    /// Praeft, ob der lokale Port ueberhaupt erreichbar ist. Nur informativ – ein Block durch
    /// die Windows-Firewall zeigt sich sonst als "Abo erfolgreich, aber nie ein Ereignis".
    /// </summary>
    public string DescribeEventStream()
    {
        // Nicht danach gefragt, ob mal ein Subscribe durchkam, sondern danach, ob der
        // Konnektor sie zuletzt noch gefuehrt hat. Ein abgelaufenes Abo ist genau der Fall,
        // in dem "aktiv" dasteht und doch nichts kommt – und der Nutzer haelte die Erklaerung
        // fuer die Erklaerung, die sie nicht mehr ist.
        if (_subscribed && _subscriptionsAlive)
            return L($"Ereignis-Abos aktiv, lauscht auf {_events.EventTo}", $"event subscriptions active, listening on {_events.EventTo}");

        if (_subscribed)
            return L($"Abos angelegt, aber der Konnektor fuehrt sie nicht mehr – Ablauf erreicht oder kuendigt. Die App pollt wieder regelmaessig.\n({LastEventNote ?? ""})",
                     $"Subscriptions were created, but the connector no longer lists them – expired or cancelled. The app polls again on schedule.\n({LastEventNote ?? ""})");

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
