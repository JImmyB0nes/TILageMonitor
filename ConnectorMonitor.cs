namespace TILageMonitor;

/// <summary>
/// Periodische Hintergrundabfrage des Konnektors. Meldet neu auftretende Probleme einmalig
/// per Toast und benachrichtigt bei Wiederherstellung.
/// </summary>
public sealed class ConnectorMonitor : IDisposable
{
    private readonly Func<AppSettings> _settingsProvider;
    private readonly HashSet<string> _activeAlertKeys = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _consecutiveFailures;
    private bool _unreachableNotified;

    public ConnectorSnapshot? LastSnapshot { get; private set; }
    public IReadOnlyList<ConnectorAlert> LastAlerts { get; private set; } = [];
    public string? LastError { get; private set; }
    public DateTime? LastAttempt { get; private set; }

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
    }

    /// <summary>Übernimmt einen Datenstand aus dem Konnektor-Fenster, damit Anzeige und Tray konsistent sind.</summary>
    public void Accept(ConnectorSnapshot snapshot)
    {
        var settings = _settingsProvider().Connector;
        LastSnapshot = snapshot;
        LastError = null;
        LastAttempt = DateTime.Now;
        LastAlerts = ConnectorHealth.Evaluate(snapshot, settings.CertificateWarningDays, DateTime.Now);
        Updated?.Invoke();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var settings = _settingsProvider().Connector;
            await PollOnceAsync(settings, ct);
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Clamp(settings.PollMinutes, 1, 120)), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollOnceAsync(ConnectorSettings settings, CancellationToken ct)
    {
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

            foreach (var alert in alerts.Where(a => !_activeAlertKeys.Contains(a.Key)))
                Notify(alert.Title, alert.Body, alert.Urgency, "konnektor");

            _activeAlertKeys.Clear();
            foreach (var alert in alerts)
                _activeAlertKeys.Add(alert.Key);
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

        Updated?.Invoke();
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

    public void Dispose() => Stop();
}
