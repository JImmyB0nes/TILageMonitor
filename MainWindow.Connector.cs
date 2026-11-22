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

    public void OpenConnectorWindow()
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
        UpdateConnectorCard(settings, monitor, tooltip);
    }

    private void ConnectorCard_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left)
            return;
        OpenConnectorWindow();
    }

    private void ConnectorCard_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space)
        {
            e.Handled = true;
            OpenConnectorWindow();
        }
    }

    private void UpdateConnectorCard(ConnectorSettings settings, ConnectorMonitor monitor, string tooltip)
    {
        if (ConnectorStatusCard is null || ConnectorCardTitle is null)
            return;

        ConnectorStatusCard.ToolTip = tooltip;
        ConnectorCardTitle.Text = LocalizationService.Pick("Konnektor", "Connector");

        string detail;
        string iconBg = "AppIconCircleBackground";
        if (!settings.Enabled || !settings.IsConfigured)
            detail = LocalizationService.Pick("Lokale TI-Anbindung einrichten und auslesen", "Set up and read the local TI connection");
        else if (monitor.LastError is not null)
        {
            detail = LocalizationService.Pick("Nicht erreichbar: ", "Unreachable: ") + monitor.LastError;
            iconBg = "StatusOutage";
        }
        else if (monitor.LastSnapshot is null)
            detail = LocalizationService.Pick("Wird abgefragt …", "Querying …");
        else
        {
            var snap = monitor.LastSnapshot;
            var name = snap.Directory is null
                ? LocalizationService.Pick("Konnektor", "Connector")
                : $"{snap.Directory.ProductVendorName} {snap.Directory.ProductName}".Trim();
            var worst = ConnectorHealth.Worst(monitor.LastAlerts);
            detail = worst switch
            {
                ToastUrgency.Error => name + " · " + LocalizationService.Pick("Problem erkannt", "problem detected"),
                ToastUrgency.Warning => name + " · " + LocalizationService.Pick("Hinweise beachten", "attention needed"),
                _ => name + " · " + LocalizationService.Pick($"Stand {snap.RetrievedAt:HH:mm} Uhr", $"as of {snap.RetrievedAt:HH:mm}")
            };
            iconBg = worst switch
            {
                ToastUrgency.Error => "StatusOutage",
                ToastUrgency.Warning => "StatusPartial",
                _ => "StatusOk"
            };
        }
        ConnectorCardDetail.Text = detail;
        ConnectorCardOpenButton.Content = settings.IsConfigured
            ? LocalizationService.Pick("Öffnen", "Open")
            : LocalizationService.Pick("Einrichten", "Set up");
        try { ConnectorCardIcon.Background = (System.Windows.Media.Brush)FindResource(iconBg); } catch { /* theme fallback */ }

        ConnectorCardChips.Children.Clear();
        var snapNow = monitor.LastSnapshot;
        void Chip(string label, bool? ok)
        {
            var color = ok is true ? "StatusOk" : ok is false ? "StatusOutage" : "StatusEmpty";
            var chip = new System.Windows.Controls.Border
            {
                Background = (System.Windows.Media.Brush)FindResource("ChipBackground"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("BorderSubtle"),
                BorderThickness = new System.Windows.Thickness(1),
                CornerRadius = new System.Windows.CornerRadius(10),
                Padding = new System.Windows.Thickness(8, 4, 10, 4),
                Margin = new System.Windows.Thickness(0, 0, 8, 6)
            };
            var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            row.Children.Add(new System.Windows.Controls.Border
            {
                Width = 8, Height = 8, CornerRadius = new System.Windows.CornerRadius(4),
                Background = (System.Windows.Media.Brush)FindResource(color),
                Margin = new System.Windows.Thickness(0, 0, 6, 0),
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            });
            row.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = (System.Windows.Media.Brush)FindResource("TextMain"),
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            });
            chip.Child = row;
            ConnectorCardChips.Children.Add(chip);
        }
        if (!settings.IsConfigured)
        {
            Chip(LocalizationService.Pick("Nicht eingerichtet", "Not set up"), null);
            return;
        }
        Chip(LocalizationService.Pick("Erreichbar", "Reachable"), monitor.LastError is null && snapNow is not null);
        Chip("VPN TI", snapNow?.Resource?.IsTiOnline);
        Chip("VPN SIS", snapNow?.Resource?.IsSisOnline);
        Chip(LocalizationService.Pick("Live-Ereignisse", "Live events"), monitor.SubscriptionsAlive && monitor.EventStreamActive);
    }

    private void DisposeConnector()
    {
        try { _connectorMonitor?.Dispose(); } catch { /* ignore */ }
        try { _connectorWindow?.Close(); } catch { /* ignore */ }
    }
}
