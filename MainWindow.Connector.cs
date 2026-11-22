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
        _settingsWindow?.SyncFrom(_settings);
    }

    /// <summary>Karte nach einer Einstellungsaenderung neu bewerten.</summary>
    public void RefreshConnectorCard() => UpdateConnectorButton();

    /// <summary>Offenes Konnektor-Fenster neu zeichnen (z. B. nach "PIN-Verwaltung erlauben").</summary>
    public void RefreshConnectorWindow() => _connectorWindow?.ApplyThemeRefresh();

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

        var state = ConnectorCardVisibility.Classify(settings, monitor.LastSnapshot is not null, monitor.LastError is not null, monitor.LastErrorIsConfiguration);
        switch (state)
        {
            case ConnectorCardVisibility.State.NotSetUp:
                marker = "";
                tooltip = LocalizationService.Pick("Konnektor einrichten und auslesen", "Set up and read connector");
                break;
            case ConnectorCardVisibility.State.MonitoringOff:
                marker = "";
                tooltip = LocalizationService.Pick("Hintergrundüberwachung aus – zum Auslesen öffnen", "Background monitoring off – open to read");
                break;
            case ConnectorCardVisibility.State.Unreachable:
                marker = "  ⛔";
                tooltip = LocalizationService.Pick("Konnektor nicht erreichbar: ", "Connector unreachable: ") + monitor.LastError;
                break;
            case ConnectorCardVisibility.State.Rejected:
                marker = "  ⚠";
                tooltip = LocalizationService.Pick("Konnektor lehnt die Abfrage ab: ", "Connector rejects the request: ") + monitor.LastError;
                break;
            case ConnectorCardVisibility.State.Querying:
                marker = "";
                tooltip = LocalizationService.Pick("Konnektor wird abgefragt …", "Querying connector …");
                break;
            default:
            {
                var worst = ConnectorHealth.Worst(monitor.LastAlerts);
                marker = worst switch
                {
                    ToastUrgency.Error => "  ⛔",
                    ToastUrgency.Warning => "  ⚠",
                    _ => "  ✓"
                };
                tooltip = monitor.LastAlerts.Count == 0
                    ? LocalizationService.Pick($"Konnektor OK · Stand {monitor.LastSnapshot!.RetrievedAt:HH:mm}", $"Connector OK · as of {monitor.LastSnapshot!.RetrievedAt:HH:mm}")
                    : string.Join(Environment.NewLine, monitor.LastAlerts.Select(a => "• " + a.Title));
                break;
            }
        }

        ConnectorButton.Content = LocalizationService.Pick("🔌  Konnektor", "🔌  Connector") + marker;
        ConnectorButton.ToolTip = tooltip;
        UpdateConnectorCard(settings, monitor, tooltip, state);
    }

    private void ConnectorCard_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left || e.Handled)
            return;
        // Klick auf X oder "Öffnen": die Knoepfe erledigen das selbst – die Karte darf nicht
        // zusaetzlich das Fenster oeffnen (beim X waere das genau das Gegenteil).
        if (IsInsideButton(e.OriginalSource as DependencyObject))
            return;
        OpenConnectorWindow();
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        for (var node = source; node is not null; node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                 ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                 : LogicalTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase)
                return true;
            if (node is System.Windows.Controls.Border { Name: "ConnectorStatusCard" })
                return false;
        }
        return false;
    }

    /// <summary>X auf der Konnektor-Karte: ausblenden und merken. Wieder einblenden in den Einstellungen.</summary>
    private void ConnectorCardClose_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _settings.Connector.ShowMainWindowCard = false;
        SettingsStore.Save(_settings);
        UpdateConnectorButton();
        // Offene Einstellungen nachziehen, sonst steht dort weiter "Karte anzeigen" angehakt.
        _settingsWindow?.SyncFrom(_settings);
    }

    private void ConnectorCard_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space)
        {
            e.Handled = true;
            OpenConnectorWindow();
        }
    }

    private void UpdateConnectorCard(ConnectorSettings settings, ConnectorMonitor monitor, string tooltip, ConnectorCardVisibility.State state)
    {
        if (ConnectorStatusCard is null || ConnectorCardTitle is null)
            return;

        // Eingerichtet: immer sichtbar, ohne X. Nicht eingerichtet: sichtbar mit X, bis der
        // Nutzer sie ausblendet. Collapsed statt Hidden, damit die Dienste-Karte nachrueckt.
        var show = ConnectorCardVisibility.ShouldShow(settings);
        ConnectorStatusCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (ConnectorCardCloseButton is not null)
            ConnectorCardCloseButton.Visibility = ConnectorCardVisibility.ShowCloseButton(settings) ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
            return;

        ConnectorStatusCard.ToolTip = tooltip;
        ConnectorCardTitle.Text = LocalizationService.Pick("Konnektor", "Connector");

        string detail;
        string iconBg = "AppIconCircleBackground";
        if (state == ConnectorCardVisibility.State.NotSetUp)
            detail = LocalizationService.Pick("Lokale TI-Anbindung einrichten und auslesen", "Set up and read the local TI connection");
        else if (state == ConnectorCardVisibility.State.MonitoringOff)
            detail = LocalizationService.Pick("Hintergrundüberwachung ist aus – „Öffnen“ liest den Konnektor einmalig aus.",
                "Background monitoring is off – “Open” reads the connector once.");
        else if (state == ConnectorCardVisibility.State.Unreachable)
        {
            detail = LocalizationService.Pick("Nicht erreichbar: ", "Unreachable: ") + monitor.LastError;
            iconBg = "StatusOutage";
        }
        else if (state == ConnectorCardVisibility.State.Rejected)
        {
            detail = LocalizationService.Pick("Abfrage abgelehnt: ", "Request rejected: ") + monitor.LastError;
            iconBg = "StatusPartial";
        }
        else if (state == ConnectorCardVisibility.State.Querying)
            detail = LocalizationService.Pick("Wird abgefragt …", "Querying …");
        else
        {
            var snap = monitor.LastSnapshot!;
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
        if (state == ConnectorCardVisibility.State.NotSetUp)
        {
            Chip(LocalizationService.Pick("Nicht eingerichtet", "Not set up"), null);
            return;
        }
        if (state == ConnectorCardVisibility.State.MonitoringOff)
        {
            Chip(LocalizationService.Pick("Überwachung aus", "Monitoring off"), null);
            return;
        }
        Chip(LocalizationService.Pick("Erreichbar", "Reachable"), ConnectorCardVisibility.Reachable(state));
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
