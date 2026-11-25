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
    private readonly object _lifecycle = new();
    private bool _disposed;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _consecutiveFailures;
    private bool _unreachableNotified;
    private bool _configErrorNotified;
    private bool _subscribed;
    private bool _subscriptionAttempted;

    /// <summary>
    /// Ob der Konnektor die eigenen Abos zuletzt noch gefuehrt hat. Wird aus dem Datenstand
    /// gezogen, nicht aus dem Erfolg eines frueheren Subscribe-Aufrufs – nur so faellt ein
    /// abgelaufenes Abo ueberhaupt auf.
    /// </summary>
    private bool _subscriptionsAlive;
    private bool _eventHandlerRegistered;

    /// <summary>
    /// Abo-IDs, die diese App-Instanz selbst angelegt hat (ueber Restarts hinweg). Nur sie
    /// verlaengert die App ohne GetSubscription, und nur sie raeumt sie nach einem Portwechsel
    /// ab – fremde Abos (anderer Arbeitsplatz, andere Terminalserver-Sitzung) bleiben unberuehrt.
    /// </summary>
    private readonly HashSet<string> _ownSubscriptionIds = new(StringComparer.Ordinal);

    /// <summary>Die Adresse, auf die die selbst angelegten Abos zeigen.</summary>
    private string? _ownSubscriptionTarget;
    private DateTime _lastEventPoll = DateTime.MinValue;

    /// <summary>Beginn der laufenden Nichterreichbarkeit (null, solange alles laeuft).</summary>
    private DateTime? _outageSince;

    /// <summary>
    /// Nach einer Nichterreichbarkeit neu anmelden: ein Konnektor-Neustart verwirft alle Abos.
    /// Ob das eigene Abo noch steht, entscheidet danach die Liste aus GetSubscription.
    /// </summary>
    private bool _resubscribeAfterOutage;

    /// <summary>Zeitpunkt des letzten erfolgreichen Subscribe/Renew – fuer Firmware ohne GetSubscription.</summary>
    private DateTime _lastSubscribedAt = DateTime.MinValue;

    /// <summary>
    /// SMC-B-Erinnerung scharf: beim Start der Ueberwachung (PC-/App-Neustart) und nachdem der
    /// Konnektor wieder erreichbar ist (Stromausfall, Konnektor-Neustart).
    /// </summary>
    private bool _smcbReminderArmed = true;
    private bool _smcbReminderAfterOutage;

    /// <summary>Gleiche Ereignisse (doppelte Abos, Wiederholungen) nur einmal melden.</summary>
    private readonly ConnectorEventDeduplicator _eventDedup = new(ConnectorEventNotifications.DuplicateWindow);

    /// <summary>Mindestabstand zwischen zwei ereignisgesteuerten Abfragen, damit ein
    /// fehlerhaftes Topic mit Dauuerereignissen nicht zum Entenrennen fuehrt.</summary>
    private static readonly TimeSpan EventPollCooldown = TimeSpan.FromSeconds(10);

    /// <summary>Wie oft die Signaturzertifikate nachgelesen werden – selten, weil ihr Ablauf
    /// kein Vorgang ist, der im Fuenfminutentakt passiert.</summary>
    private static readonly TimeSpan QesReadInterval = TimeSpan.FromHours(12);
    private DateTime _lastQesRead = DateTime.MinValue;

    /// <summary>Karten, fuer die C.QES im laufenden Intervall schon versucht wurde.</summary>
    private readonly HashSet<string> _qesAttempted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Das Signaturzertifikat je Karte, damit der Alarm zwischen zwei Nachlesen
    /// nicht verschwindet. Nachgezogen wird ueber die Kartengriffangabe, die der Konnektor
    /// ueber die Laufzeit gleich haelt.</summary>
    private readonly Dictionary<string, ConnectorCertificateInfo> _qesByCard = new(StringComparer.OrdinalIgnoreCase);

    public ConnectorSnapshot? LastSnapshot { get; private set; }
    public IReadOnlyList<ConnectorAlert> LastAlerts { get; private set; } = [];
    public string? LastError { get; private set; }
    public DateTime? LastAttempt { get; private set; }

    /// <summary>gematik-Fehlercode des letzten Fehlschlags, falls der Konnektor einen geliefert hat.</summary>
    public string? LastErrorCode { get; private set; }

    /// <summary>Der letzte Fehler war eine Ablehnung (Konfiguration), kein Ausfall.</summary>
    public bool LastErrorIsConfiguration { get; private set; }

    /// <summary>Fehlschlaege in Folge – Grundlage fuer den Backoff.</summary>
    public int ConsecutiveFailures => _consecutiveFailures;

    /// <summary>Wann der naechste Abruf geplant ist (regulaer oder Wiederverbindung).</summary>
    public DateTime? NextAttemptAt { get; private set; }

    /// <summary>Clientsystem-Zertifikat (P12) aus dem letzten Abruf – fuer Ablaufwarnung und Anzeige.</summary>
    public string? ClientCertificateSubject { get; private set; }
    public DateTime? ClientCertificateNotAfter { get; private set; }

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

    /// <summary>Restart-Schluessel der laufenden Ueberwachung (null = keine).</summary>
    private string? _runningKey;

    /// <summary>
    /// Startet die Ueberwachung neu – aber nur, wenn sich etwas Verbindungsrelevantes geaendert
    /// hat (oder <paramref name="force"/>). Frueher loeste jedes Verlassen des Intervallfelds
    /// einen Neustart aus: Abos wurden gekappt, und alle aktiven Warnungen kamen erneut als Toast.
    /// </summary>
    public void Restart(bool force = false)
    {
        if (_disposed)
            return;
        var settings = _settingsProvider().Connector;
        var key = settings.RestartKey;
        if (!force && _loop is not null && string.Equals(key, _runningKey, StringComparison.Ordinal))
        {
            Updated?.Invoke();
            return;
        }

        var active = settings.Enabled && settings.MonitoringEnabled && settings.IsConfigured;
        // Bereits gemeldete Warnungen ueber den Neustart behalten – sie stehen ja noch an.
        StopCore(keepAlertState: active);
        if (!active)
        {
            _runningKey = null;
            LastSnapshot = null;
            LastAlerts = [];
            LastError = null;
            LastErrorCode = null;
            // Ohne ueberwachung darf nichts nachstehen, was nach Abschaltung der Anbindung
            // noch auf "Karte rein" steht – dieselbe Begruendung, die fuer LastSnapshot gilt.
            LastPresence = null;
            LastEvent = null;
            _ = HomeAssistantMqtt.StopAsync(); // Home Assistant: "nicht verfuegbar" statt alter Werte
            Updated?.Invoke();
            return;
        }

        _runningKey = key;
        _smcbReminderArmed = true;
        _smcbReminderAfterOutage = false;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => LoopAsync(token), token);
    }

    public void Stop()
    {
        StopCore(keepAlertState: false);
        // Ohne Ueberwachung kommen keine Daten mehr – Home Assistant soll das als
        // "nicht verfuegbar" sehen statt alter Werte mit "online".
        _ = HomeAssistantMqtt.StopAsync();
    }

    private void StopCore(bool keepAlertState)
    {
        lock (_lifecycle)
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            _cts?.Dispose();
            _cts = null;
            _loop = null;
            _runningKey = null;
            if (!keepAlertState)
            {
                lock (_activeAlertKeys)
                    _activeAlertKeys.Clear();
            }
            _consecutiveFailures = 0;
            _outageSince = null;
            _resubscribeAfterOutage = false;
            _lastSubscribedAt = DateTime.MinValue;
            NextAttemptAt = null;
            _unreachableNotified = false;
            _configErrorNotified = false;
            _subscribed = false;
            _subscriptionsAlive = false;
            _subscriptionAttempted = false;
            _events.EventReceived -= OnEvent;
            _eventHandlerRegistered = false;
            _events.Dispose();
        }
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
        if (settings.MonitoringEnabled && (settings.NotifyCertificateExpiry || settings.NotifyPinStatus))
            NotifyLedgerWarnings(settings, snapshot);
        PublishToHomeAssistant(CancellationToken.None);
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
                // Nicht erreichbar (z. B. Neustart des Konnektors): exponentiell mit Jitter
                // wiederholen statt erst nach dem vollen Intervall – hoechstens alle 5 Minuten.
                var delay = ConnectorReconnect.NextDelay(_consecutiveFailures, TimeSpan.FromMinutes(minutes), Random.Shared.NextDouble());
                NextAttemptAt = DateTime.Now + delay;
                await Task.Delay(delay, ct);
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
            LastErrorCode = null;
            LastErrorIsConfiguration = false;
            ClientCertificateSubject = client.ClientCertificate?.Subject;
            ClientCertificateNotAfter = client.ClientCertificate?.NotAfter;

            if (_unreachableNotified)
            {
                Notify(L("Konnektor wieder erreichbar", "Connector reachable again"), L("Die Verbindung zum Konnektor ist wiederhergestellt.", "The connection to the connector has been restored."), ToastUrgency.Info, "konnektor");
            }
            if (_consecutiveFailures > 0)
            {
                // Wieder da: connector.sds wurde mit diesem Abruf frisch gelesen (neuer Client).
                // Die Abos werden neu bewertet – ein Neustart des Konnektors hat sie verworfen,
                // und ein frueheres "aufgegeben" soll die Wiederverbindung nicht blockieren.
                _subscriptionAttempted = false;
                _resubscribeAfterOutage = true;
                _smcbReminderArmed = true;
                _smcbReminderAfterOutage = true;
            }
            _consecutiveFailures = 0;
            _outageSince = null;
            _unreachableNotified = false;
            _configErrorNotified = false;

            var alerts = ConnectorHealth.Evaluate(snapshot, settings.CertificateWarningDays, DateTime.Now);
            LastAlerts = alerts;

            // Ein Ereignis darf nicht vom naechsten Poll uebermaalt werden, sonst faellt der
            // Indikator zuruck, obwohl die Karte laengst bekannt ist. Nur der eigene Stand des
            // Polls ersetzt ihn, wenn kein frisches Ereignis vorliegt.
            RefreshPresence(snapshot);

            lock (_activeAlertKeys)
            {
                // Ablauf- und PIN-Warnungen kommen aus dem Ledger (neustartfest, je Stufe bzw.
                // Zustandswechsel einmal) – hier nicht doppelt melden.
                foreach (var alert in alerts.Where(a => !_activeAlertKeys.Contains(a.Key) && !ConnectorToastOwnership.IsOwnedByLedger(a.Key)))
                    Notify(alert.Title, alert.Body, alert.Urgency, "konnektor");

                _activeAlertKeys.Clear();
                foreach (var alert in alerts)
                    _activeAlertKeys.Add(alert.Key);
            }

            var smcbToastShown = false;
            if (settings.NotifyCertificateExpiry || settings.NotifyPinStatus)
                smcbToastShown = NotifyLedgerWarnings(settings, snapshot);
            RemindSmcb(settings, snapshot, smcbToastShown);

            if (settings.EventSubscriptionsEnabled)
                await EnsureSubscriptionsAsync(client, snapshot, ct);

            if (settings.HistoryEnabled)
                ConnectorHistoryStore.UpsertNow(snapshot, DateTime.Now);

            // Konfigurationsabgleich ueber Zeit: meldet nur Verstellungen, keine Normalfaelle.
            // Bewusst auerhalb des Verlastkastens – der Vergleich braucht keine Ablage in der
            // Timeline, nur den letzten Stand. Wer den Verlauf abschaltet, dem faellt sonst
            // lautlos auch die Warnung vor still gerollter Firmware oder verlegten Terminals weg.
            // Alle Aenderungen eines Abrufs in einem Toast statt einer Toast-Flut.
            var changes = ConnectorConfigurationDiff.CompareAndRemember(snapshot);
            if (changes.Count > 0)
                Notify(L("Konnektor-Konfiguration geändert", "Connector configuration changed"),
                    string.Join("\n", changes.Take(5)) + (changes.Count > 5 ? L($"\n… und {changes.Count - 5} weitere", $"\n… and {changes.Count - 5} more") : ""),
                    ToastUrgency.Warning, "konnektor-konfig");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ConnectorReconnect.IsConfigurationProblem(ex))
        {
            // Erreichbar, aber abgelehnt: kein Ausfall, kein Backoff, kein Verlaufseintrag.
            LastError = ex.Message;
            LastErrorIsConfiguration = true;
            LastErrorCode = (ex as ConnectorFaultException)?.Code;
            if (!_configErrorNotified)
            {
                _configErrorNotified = true;
                Notify(L("Konnektor lehnt die Abfrage ab", "Connector rejects the request"),
                    ex.Message + "\n" + L("Bitte die Konnektor-Einstellungen prüfen.", "Please check the connector settings."),
                    ToastUrgency.Warning, "konnektor");
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            LastErrorIsConfiguration = false;
            LastErrorCode = (ex as ConnectorFaultException)?.Code;
            _consecutiveFailures++;
            _outageSince ??= DateTime.Now;
            if (settings.HistoryEnabled)
                ConnectorHistoryStore.RecordUnreachable(DateTime.Now);
            if (ConnectorReconnect.ShouldNotifyUnreachable(_consecutiveFailures, _outageSince, DateTime.Now, _unreachableNotified))
            {
                _unreachableNotified = true;
                Notify(L("Konnektor nicht erreichbar", "Connector unreachable"),
                    ex.Message + "\n" + L("Die App versucht es automatisch erneut.", "The app retries automatically."),
                    ToastUrgency.Error, "konnektor");
            }
        }
        finally
        {
            _pollGate.Release();
        }

        PublishToHomeAssistant(ct);
        Updated?.Invoke();
    }

    /// <summary>
    /// Schickt den Stand an Home Assistant, falls eingerichtet. Laeuft neben der Ueberwachung
    /// her – ein langsames oder nicht erreichbares Home Assistant haelt keinen Abruf auf.
    /// </summary>
    private void PublishToHomeAssistant(CancellationToken ct)
    {
        var settings = _settingsProvider().Connector;
        if (!settings.HomeAssistantEnabled)
        {
            _ = HomeAssistantMqtt.StopAsync();
            return;
        }
        var ok = LastError is null && LastSnapshot is not null;
        var input = new HomeAssistantInput(
            HomeAssistantName(settings, LastSnapshot),
            Reachable: LastError is null || LastErrorIsConfiguration,
            Snapshot: ok ? LastSnapshot : null,
            Alerts: ok ? LastAlerts : [],
            ClientCertificateSubject,
            ClientCertificateNotAfter,
            LiveEvents: settings.EventSubscriptionsEnabled ? SubscriptionsAlive && EventStreamActive : null,
            LastError,
            DateTime.Now)
        {
            Machine = Environment.MachineName,
            AppVersion = AppVersionText,
            LastKnownSnapshot = LastSnapshot
        };
        _ = Task.Run(() => HomeAssistantPublisher.PublishAsync(settings, input, ct), CancellationToken.None);
    }

    private static readonly string AppVersionText =
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "";

    private static string? _lastDerivedHaName;

    /// <summary>Anzeigename in Home Assistant: eingetragen, sonst SMC-B-Inhaber, sonst Adresse.</summary>
    public static string HomeAssistantName(ConnectorSettings settings, ConnectorSnapshot? snapshot)
    {
        if (!string.IsNullOrWhiteSpace(settings.HomeAssistantName))
            return settings.HomeAssistantName.Trim();
        var holder = snapshot?.Cards.FirstOrDefault(c => c.IsSmcB)?.CardHolderName;
        if (!string.IsNullOrWhiteSpace(holder))
            return _lastDerivedHaName = holder.Trim();
        // SMC-B gerade gezogen: beim zuletzt abgeleiteten Namen bleiben, sonst entstuende in
        // Home Assistant unter der IP ein zweites Geraet.
        if (_lastDerivedHaName is { } last)
            return last;
        return string.IsNullOrWhiteSpace(settings.Host) ? "Konnektor" : settings.Host.Trim();
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
        ct.ThrowIfCancellationRequested();
        var asked = !snapshot.Warnings.Any(w => w.Contains("GetSubscription", StringComparison.Ordinal));
        var ours = Ours(snapshot);

        // Firmware ohne GetSubscription: die Liste ist leer, obwohl das Abo steht. Ohne diese
        // Bremse legte jeder Poll ein weiteres Abo an. Neu angemeldet wird nur nach einer
        // Nichterreichbarkeit (Neustart verwirft Abos) oder vor Ablauf der 25 Stunden.
        if (!asked && _subscribed && !_resubscribeAfterOutage &&
            DateTime.Now - _lastSubscribedAt < ConnectorSubscriptionPolicy.RenewLead)
        {
            _subscriptionsAlive = true;
            return;
        }

        // Nach einer Nichterreichbarkeit ohne GetSubscription laesst sich nicht pruefen, ob das
        // Abo den Neustart ueberlebt hat – dann neu anmelden (doppelte Zustellungen faengt der
        // Ereignisfilter ab). Mit GetSubscription entscheidet die tatsaechliche Liste.
        var action = !asked && _resubscribeAfterOutage
            ? ConnectorSubscriptionPolicy.Action.Subscribe
            : ConnectorSubscriptionPolicy.Decide(_subscriptionAttempted, _subscribed, asked, ours, DateTime.Now);
        // Ohne GetSubscription nach Ablauf der Vorlaufzeit: die eigenen IDs verlaengern statt
        // alle 12 Stunden einen weiteren Satz Abos anzulegen (die alten laufen sonst bis zu
        // 25 Stunden parallel und verdoppeln jede Zustellung).
        if (!asked && action == ConnectorSubscriptionPolicy.Action.Subscribe && _subscribed && !_resubscribeAfterOutage
            && OwnSubscriptionIds.Count > 0 && string.Equals(_ownSubscriptionTarget, _events.EventTo, StringComparison.OrdinalIgnoreCase))
            action = ConnectorSubscriptionPolicy.Action.Renew;
        _resubscribeAfterOutage = false;
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
        // TLS fuer CETP verlangt der Konnektor nur bei ANCL_TLS_MANDATORY (TIP1-A_4595). Ob eine
        // Verbindung TLS spricht, erkennt die Senke am ersten Byte; das Zertifikat wird
        // trotzdem geladen, falls vorhanden, damit beide Faelle funktionieren.
        var tlsRequired = CetpTlsRequired(snapshot.Directory?.TlsMandatory, settings.UseTls);
        ConnectorClientCredentials? credentials = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(settings.ClientCertificatePath) && File.Exists(settings.ClientCertificatePath))
                credentials = ConnectorClient.LoadClientCertificate(settings.ClientCertificatePath, SecretProtector.Unprotect(settings.ClientCertificatePasswordProtected));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastEventNote = ex.Message;
            if (tlsRequired)
                return;
        }

        var options = new ConnectorEventSinkOptions
        {
            Host = settings.Host,
            UseTls = tlsRequired,
            Credentials = credentials,
            PinnedFingerprint = settings.PinnedServerThumbprint
        };
        // Ein Neustart des Monitors waehrend des Abrufs: nichts mehr anmelden und keinen
        // Handler an einen Lauscher haengen, den Stop() gerade abgebaut hat.
        ct.ThrowIfCancellationRequested();
        if (!_events.IsListening && !_events.EnsureListening(options, msg => LastEventNote = msg))
            return;

        RegisterEventHandler();

        if (action == ConnectorSubscriptionPolicy.Action.Renew)
        {
            var ids = ours.Where(s => !string.IsNullOrWhiteSpace(s.SubscriptionId)).Select(s => s.SubscriptionId).ToList();
            if (ids.Count == 0 && !asked && string.Equals(_ownSubscriptionTarget, _events.EventTo, StringComparison.OrdinalIgnoreCase))
                ids = OwnSubscriptionIds.ToList();
            try
            {
                ct.ThrowIfCancellationRequested();
                if (ids.Count > 0)
                {
                    await client.RenewSubscriptionsAsync(ids, ct);
                    _subscribed = true;
                    _subscriptionsAlive = true;
                    _lastSubscribedAt = DateTime.Now;
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

        // Eigene Abos, die auf eine fruehere Adresse zeigen (Portwechsel nach Neustart des
        // Monitors), vorher abraeumen – sonst stapeln sich mit jedem Restart fuenf weitere.
        await DropStaleOwnSubscriptionsAsync(client, ct);

        var succeeded = 0;
        foreach (var topic in ConnectorTopics.DefaultSubscriptions)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (id, _) = await client.SubscribeAsync(_events.EventTo ?? "", topic, ct);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    succeeded++;
                    lock (_ownSubscriptionIds)
                    {
                        if (!string.Equals(_ownSubscriptionTarget, _events.EventTo, StringComparison.OrdinalIgnoreCase))
                            _ownSubscriptionIds.Clear();
                        _ownSubscriptionTarget = _events.EventTo;
                        _ownSubscriptionIds.Add(id);
                    }
                }
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
            _lastSubscribedAt = DateTime.Now;
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
        var due = DateTime.Now - _lastQesRead >= QesReadInterval;
        if (due)
        {
            _lastQesRead = DateTime.Now;
            _qesByCard.Clear();
            _qesAttempted.Clear();
        }

        // Bekannte Zertifikate uebernehmen; neu gesteckte Karten (neuer Kartengriff) sofort
        // nachlesen, statt bis zu 12 Stunden auf das naechste Intervall zu warten.
        var pending = snapshot.Cards
            .Where(c => c.SupportsPinManagement && !string.IsNullOrEmpty(c.CardHandle) && !_qesAttempted.Contains(c.CardHandle))
            .ToList();
        if (pending.Count == 0)
        {
            // Nachlesen ist selten, Bewerten ist staendig. Ohne die Uebernahme wuerde der
            // Alarm nur auf dem einen Poll erscheinen, auf dem gelesen wurde, und am
            // naechsten wieder weg sein – als Alarm taugt er so nicht, weil er bloe
            // anzeigt, dass eben gerade gelesen wurde.
            AddKnownQes(snapshot);
            return;
        }

        foreach (var card in pending)
        {
            ct.ThrowIfCancellationRequested();
            _qesAttempted.Add(card.CardHandle);
            try
            {
                var qes = await client.ReadCardCertificateAsync(card.CardHandle, "C.QES", ct);
                if (qes is not null)
                    _qesByCard[card.CardHandle] = qes;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Karte fuehrt kein C.QES – das ist der Regelfall, keine Meldung.
            }
        }
        AddKnownQes(snapshot);
    }

    private void AddKnownQes(ConnectorSnapshot snapshot)
    {
        foreach (var card in snapshot.Cards)
            if (!string.IsNullOrEmpty(card.CardHandle) && _qesByCard.TryGetValue(card.CardHandle, out var known)
                && !card.Certificates.Any(c => c.RefEquals(known.CertRef)))
                card.Certificates.Add(known);
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
        var target = _events.EventTo;
        if (string.IsNullOrWhiteSpace(target))
            return [];
        return snapshot.Subscriptions.Where(s => s.TargetsExactly(target)).ToList();
    }

    /// <summary>IDs der Abos, die diese App-Instanz selbst angelegt hat.</summary>
    public IReadOnlyCollection<string> OwnSubscriptionIds
    {
        get { lock (_ownSubscriptionIds) return _ownSubscriptionIds.ToList(); }
    }

    /// <summary>Gehoert das Abo dieser App? Exakt die eigene Adresse oder eine selbst angelegte ID.</summary>
    public bool IsOwnSubscription(ConnectorSubscription sub)
    {
        if (sub is null)
            return false;
        if (!string.IsNullOrWhiteSpace(sub.SubscriptionId))
            lock (_ownSubscriptionIds)
                if (_ownSubscriptionIds.Contains(sub.SubscriptionId.Trim()))
                    return true;
        return sub.TargetsExactly(_events.EventTo);
    }

    /// <summary>Entscheidet, ob die CETP-Senke TLS verlangen muss (TIP1-A_4595).</summary>
    internal static bool CetpTlsRequired(bool? tlsMandatoryFromDirectory, bool settingsUseTls) =>
        tlsMandatoryFromDirectory ?? settingsUseTls;

    private async Task DropStaleOwnSubscriptionsAsync(ConnectorClient client, CancellationToken ct)
    {
        List<string> stale;
        lock (_ownSubscriptionIds)
        {
            if (_ownSubscriptionIds.Count == 0 || string.Equals(_ownSubscriptionTarget, _events.EventTo, StringComparison.OrdinalIgnoreCase))
                return;
            stale = _ownSubscriptionIds.ToList();
            _ownSubscriptionIds.Clear();
            _ownSubscriptionTarget = null;
        }
        foreach (var id in stale)
        {
            ct.ThrowIfCancellationRequested();
            try { await client.UnsubscribeAsync(id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* laeuft nach 25 h ab */ }
        }
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
        lock (_ownSubscriptionIds)
        {
            _ownSubscriptionIds.Clear();
            _ownSubscriptionTarget = null;
        }
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
        NotifyEvent(evt);

        // Karte rein/raus sofort, nicht erst nach dem naechsten Abgleich: wer die Karte zieht,
        // will das ohne Umweg sehen, waehrend die eigentlichen Einzelheiten nachgezogen werden.
        if (ConnectorPresence.FromEvent(evt) is { } presence)
            LastPresence = presence;

        Updated?.Invoke();

        // Abo ist aktiv, aber die Anzeige soll trotzdem aktuelle Daten zeigen: sofort nachziehen.
        if (DateTime.Now - _lastEventPoll < EventPollCooldown)
            return;
        _lastEventPoll = DateTime.Now;

        CancellationToken token;
        lock (_lifecycle)
            token = _cts?.Token ?? new CancellationToken(canceled: true);
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

    /// <summary>
    /// Toast fuer Karten- und Terminalereignisse nach dem eingestellten Filter. Doppelt
    /// zugestellte Ereignisse (zwei Abos auf denselben Lauscher) werden nur einmal gemeldet.
    /// </summary>
    private void NotifyEvent(ConnectorEvent evt)
    {
        try
        {
            var settings = _settingsProvider().Connector;
            if (!settings.MonitoringEnabled)
                return;
            var id = evt.Param("ICCSN") ?? evt.Param("CardHandle") ?? evt.Param("CtID") ?? "";
            if (!_eventDedup.TryAccept($"{evt.Topic?.Trim()}|{id}", DateTime.Now))
                return;
            if (ConnectorEventNotifications.Decide(evt, ConnectorEventToastFilter.From(settings)) is not { } toast)
                return;
            Notify(toast.Title, toast.Body, toast.Urgency, "konnektor");

            // Der sofort nachgezogene Poll sieht denselben Vorgang als Warnung – nicht zweimal melden.
            if (ConnectorEventNotifications.MatchingHealthKey(evt) is { } healthKey)
                lock (_activeAlertKeys)
                    _activeAlertKeys.Add(healthKey);
        }
        catch
        {
            // Ein Anzeigefehler darf den Lauscher nicht kippen.
        }
    }

    /// <summary>
    /// Ablauf- und PIN-Warnungen ueber den gespeicherten Ledger: jede Ablaufstufe einmal je
    /// Zertifikat, PIN-Meldungen nur beim Zustandswechsel – auch ueber Neustarts hinweg.
    /// </summary>
    /// <returns>Ob dabei bereits ein Toast zur nicht freigeschalteten SMC-B kam.</returns>
    private bool NotifyLedgerWarnings(ConnectorSettings settings, ConnectorSnapshot snapshot)
    {
        var due = ConnectorWarningStore.Update(ledger =>
            EvaluateLedger(settings, ToastService.AreNotificationsEnabled, snapshot, ClientCertificateSubject, ClientCertificateNotAfter, ledger, DateTime.Now));
        var smcbShown = false;
        foreach (var alert in due)
        {
            // Nicht freigeschaltete SMC-B: mit Knopf "Jetzt freischalten".
            var smcb = alert.Key.EndsWith("-unverified", StringComparison.Ordinal)
                ? snapshot.Cards.FirstOrDefault(c => c.IsSmcB && alert.Key.StartsWith($"pinwatch-{ConnectorSmcbReminder.CardId(c)}-", StringComparison.Ordinal))
                : null;
            if (smcb is not null)
            {
                smcbShown = true;
                Notify(alert.Title, alert.Body, alert.Urgency, ConnectorSmcbReminder.ShowFocus(smcb),
                    ConnectorSmcbReminder.ActionLabel, ConnectorSmcbReminder.UnlockFocus(smcb));
            }
            else
                Notify(alert.Title, alert.Body, alert.Urgency, "konnektor");
        }
        return smcbShown;
    }

    /// <summary>
    /// Einmal je Anlass (Start, Wiederverbindung) an eine nicht freigeschaltete SMC-B erinnern.
    /// Bleibt scharf, solange der PIN-Status nicht lesbar ist – eine unklare Antwort ist keine
    /// Entwarnung.
    /// </summary>
    private void RemindSmcb(ConnectorSettings settings, ConnectorSnapshot snapshot, bool alreadyShown)
    {
        if (!_smcbReminderArmed)
            return;
        var (state, card) = ConnectorSmcbReminder.Evaluate(snapshot);
        if (state == ConnectorSmcbReminder.State.Unknown)
            return;

        _smcbReminderArmed = false;
        if (state != ConnectorSmcbReminder.State.NotVerified || card is null || alreadyShown)
            return;
        if (!settings.MonitoringEnabled || !settings.NotifyPinStatus)
            return;

        var alert = ConnectorSmcbReminder.Build(card, _smcbReminderAfterOutage);
        Notify(alert.Title, alert.Body, alert.Urgency, ConnectorSmcbReminder.ShowFocus(card),
            ConnectorSmcbReminder.ActionLabel, ConnectorSmcbReminder.UnlockFocus(card));
    }

    /// <summary>
    /// Bewertet Ablauf- und PIN-Warnungen gegen den Ledger. Sind Benachrichtigungen aus
    /// (global oder fuer den Konnektor), bleibt der Ledger unberuehrt: sonst gilt eine
    /// Ablaufstufe als "gemeldet", obwohl nie ein Toast erschien – und nach dem Wiedereinschalten
    /// kaeme die 7-Tage-Warnung nie.
    /// </summary>
    internal static IReadOnlyList<ConnectorAlert> EvaluateLedger(
        ConnectorSettings settings,
        bool notificationsEnabled,
        ConnectorSnapshot snapshot,
        string? clientSubject,
        DateTime? clientNotAfter,
        ConnectorWarningLedger ledger,
        DateTime now)
    {
        if (!notificationsEnabled || !settings.MonitoringEnabled)
            return [];
        var list = new List<ConnectorAlert>();
        if (settings.NotifyCertificateExpiry)
            list.AddRange(ConnectorCertificateExpiry.Evaluate(ConnectorCertificateExpiry.Collect(snapshot, clientSubject, clientNotAfter), ledger, now));
        if (settings.NotifyPinStatus)
            list.AddRange(ConnectorPinWatch.Evaluate(snapshot, ledger));
        return list;
    }

    /// <summary>Kurzbeschreibung der Wiederverbindung fuer Anzeige und Bericht, null wenn alles laeuft.</summary>
    public string? DescribeReconnect(DateTime now)
    {
        if (_consecutiveFailures <= 0)
            return null;
        var wait = NextAttemptAt is { } next ? Math.Max(0, (int)Math.Ceiling((next - now).TotalSeconds)) : (int?)null;
        return wait is { } seconds
            ? L($"Konnektor nicht erreichbar ({_consecutiveFailures}× in Folge) – nächster Versuch in {seconds} s.",
                $"Connector unreachable ({_consecutiveFailures}× in a row) – next attempt in {seconds} s.")
            : L($"Konnektor nicht erreichbar ({_consecutiveFailures}× in Folge).", $"Connector unreachable ({_consecutiveFailures}× in a row).");
    }

    private static void Notify(string title, string body, ToastUrgency urgency, string focusKey, string? actionLabel = null, string? actionFocusKey = null)
    {
        // Toast-/Balloon-APIs am UI-Thread aufrufen (NotifyIcon-Fallback ist WinForms).
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;
        dispatcher.BeginInvoke(() => ToastService.Show(title, body, urgency, focusKey, actionLabel, actionFocusKey));
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        Stop();
        // Den Gate nicht entsorgen: ein laufender Abruf gibt ihn im finally noch frei und
        // wuerde sonst mit ObjectDisposedException als unbeobachtete Task-Ausnahme enden.
    }
}
