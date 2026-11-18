using System.Windows;

namespace TILageMonitor;

/// <summary>Einbindung der lokalen Konnektor-Anbindung in Hauptfenster, Tray und Toasts.</summary>
public partial class MainWindow
{
    private ConnectorMonitor? _connectorMonitor;
    private ConnectorWindow? _connectorWindow;

    public ConnectorMonitor ConnectorMonitor =>
        _connectorMonitor ??= CreateConnectorMonitor();

    private ConnectorMonitor CreateConnectorMonitor()
    {
        var monitor = new ConnectorMonitor(() => _settings);
        monitor.Updated += () => Dispatcher.BeginInvoke(UpdateConnectorButton);
        return monitor;
    }

    private void InitializeConnector()
    {
        ConnectorMonitor.Restart();
        UpdateConnectorButton();
    }

    /// <summary>Nach Änderung der Konnektor-Einstellungen: Überwachung neu starten.</summary>
    public void ApplyConnectorSettings()
    {
        ConnectorMonitor.Restart();
        UpdateConnectorButton();
    }

    private void Connector_Click(object sender, RoutedEventArgs e) => OpenConnectorWindow();

    private void OpenConnectorWindow()
    {
        if (_connectorWindow is not null)
        {
            if (!_connectorWindow.IsVisible)
                _connectorWindow.Show();
            if (_connectorWindow.WindowState == WindowState.Minimized)
                _connectorWindow.WindowState = WindowState.Normal;
            _connectorWindow.Activate();
            return;
        }

        _connectorWindow = new ConnectorWindow(this);
        _connectorWindow.Closed += (_, _) => _connectorWindow = null;
        _connectorWindow.Show();
    }

    private void UpdateConnectorButton()
    {
        if (_disposed || ConnectorButton is null)
            return;

        var settings = _settings.Connector;
        var monitor = ConnectorMonitor;
        string marker;
        string tooltip;

        if (!settings.Enabled || !settings.IsConfigured)
        {
            marker = "";
            tooltip = LocalizationService.Pick("Konnektor einrichten und auslesen", "Set up and read connector");
        }
        else if (monitor.LastError is not null)
        {
            marker = "  ⛔";
            tooltip = LocalizationService.Pick("Konnektor nicht erreichbar: ", "Connector unreachable: ") + monitor.LastError;
        }
        else if (monitor.LastSnapshot is null)
        {
            marker = "";
            tooltip = LocalizationService.Pick("Konnektor wird abgefragt …", "Querying connector …");
        }
        else
        {
            var worst = ConnectorHealth.Worst(monitor.LastAlerts);
            marker = worst switch
            {
                ToastUrgency.Error => "  ⛔",
                ToastUrgency.Warning => "  ⚠",
                _ => "  ✓"
            };
            tooltip = monitor.LastAlerts.Count == 0
                ? LocalizationService.Pick($"Konnektor OK · Stand {monitor.LastSnapshot.RetrievedAt:HH:mm}", $"Connector OK · as of {monitor.LastSnapshot.RetrievedAt:HH:mm}")
                : string.Join(Environment.NewLine, monitor.LastAlerts.Select(a => "• " + a.Title));
        }

        ConnectorButton.Content = LocalizationService.Pick("🔌  Konnektor", "🔌  Connector") + marker;
        ConnectorButton.ToolTip = tooltip;
    }

    private void DisposeConnector()
    {
        try { _connectorMonitor?.Dispose(); } catch { /* ignore */ }
        try { _connectorWindow?.Close(); } catch { /* ignore */ }
    }
}
