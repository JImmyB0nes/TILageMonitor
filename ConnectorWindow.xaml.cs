using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace TILageMonitor;

public partial class ConnectorWindow : Window
{
    private readonly MainWindow _ownerMain;
    private ConnectorSnapshot? _snapshot;
    private bool _busy;
    private CancellationTokenSource? _cts;

    public ConnectorWindow(MainWindow owner)
    {
        InitializeComponent();
        LocalizationService.Apply(this);
        Owner = owner.IsVisible ? owner : null;
        _ownerMain = owner;

        LoadForm(owner.GetSettingsSnapshot().Connector);
        Closed += (_, _) =>
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
        };

        // Bereits vorhandenen Datenstand der Hintergrundüberwachung sofort anzeigen.
        var monitor = owner.ConnectorMonitor;
        if (monitor.LastSnapshot is not null)
            Render(monitor.LastSnapshot);

        Loaded += async (_, _) =>
        {
            var settings = owner.GetSettingsSnapshot().Connector;
            if (settings.Enabled && settings.IsConfigured)
            {
                ConnectionExpander.IsExpanded = false;
                await LoadSnapshotAsync(settings, interactive: false);
            }
        };
    }

    private static string L(string de, string en) => LocalizationService.Pick(de, en);

    // =================================================================
    // Formular
    // =================================================================

    private void LoadForm(ConnectorSettings s)
    {
        HostBox.Text = s.Host;
        UseTlsToggle.IsChecked = s.UseTls;
        CertPathBox.Text = s.ClientCertificatePath;
        MandantBox.Text = s.MandantId;
        ClientSystemBox.Text = s.ClientSystemId;
        WorkplaceBox.Text = s.WorkplaceId;
        UserBox.Text = s.UserId;
        BasicUserBox.Text = s.BasicAuthUser;
        RewriteHostToggle.IsChecked = s.RewriteEndpointHost;
        EnabledToggle.IsChecked = s.Enabled || !s.IsConfigured;
        MonitoringToggle.IsChecked = s.MonitoringEnabled;
        PollMinutesBox.Text = s.PollMinutes.ToString(CultureInfo.InvariantCulture);
        CertWarnDaysBox.Text = s.CertificateWarningDays.ToString(CultureInfo.InvariantCulture);
        CertPasswordHint.Text = string.IsNullOrEmpty(s.ClientCertificatePasswordProtected) ? "" : L("gespeichert", "saved");
        UpdatePinnedText(s.PinnedServerThumbprint);
        UpdateClientCertInfo(s);
    }

    private ConnectorSettings ReadForm()
    {
        var current = _ownerMain.GetSettingsSnapshot().Connector.Clone();
        current.Host = HostBox.Text.Trim();
        current.UseTls = UseTlsToggle.IsChecked == true;
        current.ClientCertificatePath = CertPathBox.Text.Trim().Trim('"');
        current.MandantId = MandantBox.Text.Trim();
        current.ClientSystemId = ClientSystemBox.Text.Trim();
        current.WorkplaceId = WorkplaceBox.Text.Trim();
        current.UserId = UserBox.Text.Trim();
        current.BasicAuthUser = BasicUserBox.Text.Trim();
        current.RewriteEndpointHost = RewriteHostToggle.IsChecked == true;
        current.Enabled = EnabledToggle.IsChecked == true;
        current.MonitoringEnabled = MonitoringToggle.IsChecked == true;
        if (int.TryParse(PollMinutesBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var poll))
            current.PollMinutes = Math.Clamp(poll, 1, 120);
        if (int.TryParse(CertWarnDaysBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
            current.CertificateWarningDays = Math.Clamp(days, 1, 365);

        if (CertPasswordBox.Password.Length > 0)
            current.ClientCertificatePasswordProtected = SecretProtector.Protect(CertPasswordBox.Password);
        if (BasicPasswordBox.Password.Length > 0)
            current.BasicAuthPasswordProtected = SecretProtector.Protect(BasicPasswordBox.Password);

        // Neue Adresse => anderes Gerät => alten Pin verwerfen.
        var previous = _ownerMain.GetSettingsSnapshot().Connector;
        if (!string.Equals(previous.Host, current.Host, StringComparison.OrdinalIgnoreCase))
            current.PinnedServerThumbprint = null;

        return current;
    }

    private string? Validate(ConnectorSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.Host))
            return L("Bitte die IP-Adresse des Konnektors eintragen.", "Please enter the connector's IP address.");
        try
        {
            ConnectorProtocol.BuildBaseUri(s.Host, s.UseTls);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        if (string.IsNullOrWhiteSpace(s.MandantId) || string.IsNullOrWhiteSpace(s.ClientSystemId) || string.IsNullOrWhiteSpace(s.WorkplaceId))
            return L("Mandant-ID, Clientsystem-ID und Arbeitsplatz-ID sind Pflichtfelder (Aufrufkontext).",
                     "Tenant ID, client system ID and workplace ID are required (call context).");
        if (!string.IsNullOrWhiteSpace(s.ClientCertificatePath) && !File.Exists(s.ClientCertificatePath))
            return L($"Die Zertifikatsdatei wurde nicht gefunden:\n{s.ClientCertificatePath}", $"Certificate file not found:\n{s.ClientCertificatePath}");
        return null;
    }

    private void Persist(ConnectorSettings connector)
    {
        var settings = _ownerMain.GetSettingsSnapshot();
        settings.Connector = connector;
        SettingsStore.Save(settings);
        _ownerMain.ApplyConnectorSettings();
        CertPasswordBox.Clear();
        BasicPasswordBox.Clear();
        CertPasswordHint.Text = string.IsNullOrEmpty(connector.ClientCertificatePasswordProtected) ? "" : L("gespeichert", "saved");
        UpdatePinnedText(connector.PinnedServerThumbprint);
    }

    private void UpdatePinnedText(string? thumbprint) =>
        PinnedThumbprintText.Text = string.IsNullOrWhiteSpace(thumbprint)
            ? L("nicht gepinnt – wird bei der ersten Verbindung abgefragt", "not pinned – you will be asked on first connection")
            : FormatThumbprint(thumbprint);

    private void UpdateClientCertInfo(ConnectorSettings s)
    {
        ClientCertInfo.Text = "";
        if (string.IsNullOrWhiteSpace(s.ClientCertificatePath) || !File.Exists(s.ClientCertificatePath))
            return;
        try
        {
            var (cert, bag) = ConnectorClient.LoadClientCertificate(s.ClientCertificatePath, SecretProtector.Unprotect(s.ClientCertificatePasswordProtected));
            ClientCertInfo.Text = L($"Zertifikat: {cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false)} · gültig bis {cert.NotAfter:dd.MM.yyyy}",
                                    $"Certificate: {cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false)} · valid until {cert.NotAfter:yyyy-MM-dd}");
            if (cert.NotAfter < DateTime.Now.AddDays(30))
                ClientCertInfo.Text += L("  ⚠ läuft bald ab", "  ⚠ expires soon");
            foreach (var c in bag)
                c.Dispose();
        }
        catch (Exception ex)
        {
            ClientCertInfo.Text = "⚠ " + ex.Message;
        }
    }

    private void BrowseCertificate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L("Client-Zertifikat auswählen", "Choose client certificate"),
            Filter = L("Zertifikat (*.p12;*.pfx)|*.p12;*.pfx|Alle Dateien (*.*)|*.*", "Certificate (*.p12;*.pfx)|*.p12;*.pfx|All files (*.*)|*.*"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
            CertPathBox.Text = dialog.FileName;
    }

    private void ResetTrust_Click(object sender, RoutedEventArgs e)
    {
        var s = _ownerMain.GetSettingsSnapshot().Connector.Clone();
        s.PinnedServerThumbprint = null;
        Persist(s);
        Log(L("Gepinntes Konnektor-Zertifikat entfernt.", "Pinned connector certificate removed."));
    }

    private async void SaveConnect_Click(object sender, RoutedEventArgs e)
    {
        var s = ReadForm();
        var error = Validate(s);
        if (error is not null)
        {
            ShowWarning(error);
            return;
        }
        Persist(s);
        UpdateClientCertInfo(s);
        if (await LoadSnapshotAsync(s, interactive: true))
            ConnectionExpander.IsExpanded = false;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var s = ReadForm();
        var error = Validate(s);
        if (error is not null)
        {
            ShowWarning(error);
            return;
        }
        UpdateClientCertInfo(s);
        await LoadSnapshotAsync(s, interactive: true, persistTrustOnly: true);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var s = _ownerMain.GetSettingsSnapshot().Connector;
        if (!s.IsConfigured)
        {
            ConnectionExpander.IsExpanded = true;
            ShowWarning(L("Bitte zuerst die Verbindung einrichten.", "Please configure the connection first."));
            return;
        }
        await LoadSnapshotAsync(s, interactive: true);
    }

    // =================================================================
    // Abruf
    // =================================================================

    private async Task<bool> LoadSnapshotAsync(ConnectorSettings settings, bool interactive, bool persistTrustOnly = false)
    {
        if (_busy)
            return false;

        SetBusy(true, L("Verbinde mit Konnektor …", "Connecting to connector …"));
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var client = new ConnectorClient(settings, Log);
                    var snapshot = await client.GetSnapshotAsync(includeCertificates: true, ct);
                    Render(snapshot);
                    if (!persistTrustOnly)
                        _ownerMain.ConnectorMonitor.Accept(snapshot);
                    return true;
                }
                catch (ConnectorUntrustedServerException untrusted) when (attempt == 0 && interactive)
                {
                    if (!AskTrust(untrusted))
                    {
                        ShowStatus(L("Verbindung abgebrochen", "Connection cancelled"),
                                   L("Das TLS-Zertifikat des Konnektors wurde nicht bestätigt.", "The connector's TLS certificate was not confirmed."),
                                   "StatusPartial");
                        return false;
                    }

                    settings = settings.Clone();
                    settings.PinnedServerThumbprint = untrusted.Thumbprint;
                    // Vertrauen auch beim reinen Test speichern, alle anderen Felder bleiben unverändert.
                    var stored = _ownerMain.GetSettingsSnapshot().Connector.Clone();
                    if (persistTrustOnly)
                    {
                        if (string.Equals(stored.Host, settings.Host, StringComparison.OrdinalIgnoreCase))
                        {
                            stored.PinnedServerThumbprint = untrusted.Thumbprint;
                            Persist(stored);
                        }
                    }
                    else
                    {
                        Persist(settings);
                    }
                    Log(L($"Konnektor-Zertifikat gepinnt: {FormatThumbprint(untrusted.Thumbprint)}", $"Connector certificate pinned: {FormatThumbprint(untrusted.Thumbprint)}"));
                }
            }
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            var message = DescribeException(ex);
            Log("✖ " + message);
            ShowStatus(L("Konnektor nicht erreichbar", "Connector unreachable"), message, "StatusOutage");
            if (interactive)
                ShowWarning(message);
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool AskTrust(ConnectorUntrustedServerException ex)
    {
        var text = L(
            "Der Konnektor verwendet ein TLS-Zertifikat, dem Windows nicht vertraut (bei Konnektoren üblich, da es aus der TI-PKI stammt).\n\n" +
            $"Zertifikat: {ex.Subject}\nGültig bis: {ex.NotAfter:dd.MM.yyyy}\nSHA-256:\n{FormatThumbprint(ex.Thumbprint)}\n\n" +
            "Vergleichen Sie den Fingerprint mit der Anzeige in der Konnektor-Administration. Soll diesem Zertifikat dauerhaft vertraut werden?",
            "The connector uses a TLS certificate that Windows does not trust (common for connectors because it is issued by the TI PKI).\n\n" +
            $"Certificate: {ex.Subject}\nValid until: {ex.NotAfter:yyyy-MM-dd}\nSHA-256:\n{FormatThumbprint(ex.Thumbprint)}\n\n" +
            "Compare the fingerprint with the connector administration. Trust this certificate permanently?");
        return System.Windows.MessageBox.Show(this, text, L("Konnektor-Zertifikat bestätigen", "Confirm connector certificate"),
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private static string DescribeException(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null && inner is System.Net.Http.HttpRequestException)
            inner = inner.InnerException;

        var text = inner.Message;
        if (inner is System.Security.Authentication.AuthenticationException || text.Contains("SSL", StringComparison.OrdinalIgnoreCase))
        {
            text += L(" – TLS-Handshake fehlgeschlagen. Ist das Client-Zertifikat im Konnektor für dieses Clientsystem hinterlegt?",
                      " – TLS handshake failed. Is the client certificate registered in the connector for this client system?");
        }
        else if (inner is System.Net.Sockets.SocketException)
        {
            text += L(" – Adresse/Port prüfen und ob der Arbeitsplatz den Konnektor im Netz erreicht.",
                      " – Check address/port and whether this workstation can reach the connector.");
        }
        return text;
    }

    // =================================================================
    // Darstellung
    // =================================================================

    private void Render(ConnectorSnapshot snapshot)
    {
        _snapshot = snapshot;
        var settings = _ownerMain.GetSettingsSnapshot().Connector;
        var alerts = ConnectorHealth.Evaluate(snapshot, settings.CertificateWarningDays, DateTime.Now);

        // Banner
        var worst = ConnectorHealth.Worst(alerts);
        var dir = snapshot.Directory;
        var name = dir is null ? L("Konnektor", "Connector") : $"{dir.ProductVendorName} {dir.ProductName}".Trim();
        var tiState = snapshot.Resource?.IsTiOnline switch
        {
            true => L("TI online", "TI online"),
            false => L("TI offline", "TI offline"),
            null => L("TI-Status unbekannt", "TI status unknown")
        };
        ShowStatus(
            worst switch
            {
                ToastUrgency.Error => L($"{name}: Problem erkannt", $"{name}: problem detected"),
                ToastUrgency.Warning => L($"{name}: Hinweise beachten", $"{name}: attention needed"),
                _ => L($"{name}: alles in Ordnung", $"{name}: all good")
            },
            $"{tiState} · {snapshot.Cards.Count} {L("Karten", "cards")} · {snapshot.Terminals.Count} {L("Terminals", "terminals")} · " +
            L($"Stand {snapshot.RetrievedAt:HH:mm:ss}", $"as of {snapshot.RetrievedAt:HH:mm:ss}"),
            worst switch
            {
                ToastUrgency.Error => "StatusOutage",
                ToastUrgency.Warning => "StatusPartial",
                _ => "StatusOk"
            });

        RenderAlerts(alerts, snapshot.Warnings);
        RenderConnectorInfo(snapshot);
        RenderCards(snapshot);
        RenderTerminals(snapshot);
        RenderServices(snapshot);
        FooterText.Text = L($"Letzter Abruf: {snapshot.RetrievedAt:dd.MM.yyyy HH:mm:ss}", $"Last retrieved: {snapshot.RetrievedAt:yyyy-MM-dd HH:mm:ss}");
    }

    private void ShowStatus(string title, string detail, string statusKey)
    {
        StatusTitle.Text = title;
        StatusDetail.Text = detail;
        StatusBanner.Background = Brush(statusKey + "Surface");
        StatusBanner.BorderBrush = Brush(statusKey + "Border");
    }

    private void RenderAlerts(List<ConnectorAlert> alerts, List<string> warnings)
    {
        AlertsPanel.Children.Clear();
        foreach (var alert in alerts.OrderByDescending(a => a.Urgency))
        {
            var icon = alert.Urgency == ToastUrgency.Error ? "⛔" : alert.Urgency == ToastUrgency.Warning ? "⚠" : "ℹ";
            AlertsPanel.Children.Add(Line($"{icon}  {alert.Title}", alert.Body, alert.Urgency == ToastUrgency.Error ? "StatusOutage" : "StatusPartial"));
        }
        foreach (var warning in warnings)
            AlertsPanel.Children.Add(Line("ℹ  " + L("Teilabfrage fehlgeschlagen", "Partial query failed"), warning, null));

        AlertsCard.Visibility = AlertsPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderConnectorInfo(ConnectorSnapshot snapshot)
    {
        ConnectorInfoGrid.Children.Clear();
        ConnectorInfoGrid.RowDefinitions.Clear();
        var dir = snapshot.Directory;
        var res = snapshot.Resource;

        void Row(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            var row = ConnectorInfoGrid.RowDefinitions.Count;
            ConnectorInfoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Foreground = Brush("TextMuted"), FontSize = 12, Margin = new Thickness(0, 0, 12, 6) };
            var v = new TextBlock { Text = value, Foreground = Brush("TextMain"), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            Grid.SetRow(l, row);
            Grid.SetRow(v, row);
            Grid.SetColumn(v, 1);
            ConnectorInfoGrid.Children.Add(l);
            ConnectorInfoGrid.Children.Add(v);
        }

        if (dir is not null)
        {
            Row(L("Hersteller", "Vendor"), Join(dir.ProductVendorName, dir.ProductVendorId));
            Row(L("Produkt", "Product"), Join(dir.ProductName, dir.ProductCode));
            Row(L("Produkttyp-Version (PTV)", "Product type version"), Join(dir.ProductType, dir.ProductTypeVersion));
            Row(L("Firmware", "Firmware"), dir.FirmwareVersion);
            Row(L("Hardware", "Hardware"), dir.HardwareVersion);
            Row(L("TLS verpflichtend", "TLS mandatory"), YesNo(dir.TlsMandatory));
            Row(L("Client-Authentisierung verpflichtend", "Client authentication mandatory"), YesNo(dir.ClientAuthMandatory));
        }

        if (res is not null)
        {
            Row(L("VPN TI", "VPN TI"), Join(res.VpnTiStatus, res.VpnTiTimestamp?.ToString("g", LocalizationService.DisplayCulture)));
            Row(L("VPN SIS (Internet)", "VPN SIS (internet)"), Join(res.VpnSisStatus, res.VpnSisTimestamp?.ToString("g", LocalizationService.DisplayCulture)));
            var active = res.ActiveErrors.ToList();
            Row(L("Fehlerzustände", "Error states"), active.Count == 0
                ? L("keine aktiven", "none active")
                : string.Join("\n", active.Select(e => $"{e.Condition} ({e.Severity}, {e.Type}) {L("seit", "since")} {e.ValidFrom?.ToString("g", LocalizationService.DisplayCulture)}")));
        }

        Row(L("TLS-Zertifikat Konnektor", "Connector TLS certificate"), snapshot.ServerCertificateSubject is null ? null
            : $"{snapshot.ServerCertificateSubject}\n{L("gültig bis", "valid until")} {snapshot.ServerCertificateNotAfter:dd.MM.yyyy}");

        ConnectorInfoCard.Visibility = ConnectorInfoGrid.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderCards(ConnectorSnapshot snapshot)
    {
        CardsPanel.Children.Clear();
        var terminals = snapshot.Terminals.ToDictionary(t => t.CtId, t => t, StringComparer.OrdinalIgnoreCase);

        foreach (var card in snapshot.Cards
                     .OrderBy(c => c.IsSmcB ? 0 : c.IsHba ? 1 : c.IsEgk ? 3 : 2)
                     .ThenBy(c => c.CtId)
                     .ThenBy(c => c.SlotId))
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = card.DisplayName,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("TextMain")
            });

            var ctLabel = terminals.TryGetValue(card.CtId, out var ct) && !string.IsNullOrWhiteSpace(ct.Name) ? ct.Name : card.CtId;
            var details = new List<string>
            {
                $"{L("Terminal", "Terminal")}: {ctLabel} · Slot {card.SlotId}",
            };
            if (!string.IsNullOrWhiteSpace(card.Iccsn)) details.Add($"ICCSN: {card.Iccsn}");
            if (!string.IsNullOrWhiteSpace(card.CardVersion)) details.Add($"COS: {card.CardVersion}");
            if (card.InsertTime is { } inserted) details.Add(L($"gesteckt seit {inserted:dd.MM.yyyy HH:mm}", $"inserted {inserted:yyyy-MM-dd HH:mm}"));
            if (card.IsEgk && !string.IsNullOrWhiteSpace(card.Kvnr)) details.Add($"KVNR: {card.Kvnr}");

            var cert = card.AuthCertificate;
            var expiry = cert?.NotAfter ?? card.CertificateExpirationDate;
            if (expiry is { } notAfter)
            {
                var days = (int)Math.Floor((notAfter - DateTime.Now).TotalDays);
                details.Add(L($"Zertifikat gültig bis {notAfter:dd.MM.yyyy} ({days} Tage)", $"Certificate valid until {notAfter:yyyy-MM-dd} ({days} days)"));
            }
            if (!string.IsNullOrWhiteSpace(cert?.TelematikId)) details.Add($"Telematik-ID: {cert!.TelematikId}");
            if (!string.IsNullOrWhiteSpace(cert?.Profession)) details.Add(L("Rolle", "Role") + $": {cert!.Profession}");
            if (!string.IsNullOrWhiteSpace(cert?.Organization) && cert!.Organization != card.CardHolderName) details.Add(L("Organisation", "Organization") + $": {cert.Organization}");
            if (cert is not null) details.Add($"{cert.CertRef} · {cert.KeyAlgorithm} · {L("Aussteller", "Issuer")} {cert.Issuer}");

            panel.Children.Add(new TextBlock
            {
                Text = string.Join("   ·   ", details),
                FontSize = 12,
                Foreground = Brush("TextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 6)
            });

            // PIN-Status und Aktionen
            foreach (var pinType in card.PinTypes)
            {
                var state = card.PinStates.FirstOrDefault(p => p.PinType == pinType);
                panel.Children.Add(BuildPinRow(card, pinType, state));
            }

            if (card.SupportsPinManagement)
            {
                var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                if (cert is not null)
                    actions.Children.Add(ActionButton(L("Zertifikat online prüfen (OCSP)", "Verify certificate online (OCSP)"), async () => await VerifyCertificateAsync(card)));
                actions.Children.Add(ActionButton(L("PIN-Status neu lesen", "Re-read PIN status"), async () => await RefreshPinStatusAsync(card)));
                panel.Children.Add(actions);
            }

            CardsPanel.Children.Add(new Border
            {
                Child = panel,
                Background = Brush("ChipBackground"),
                BorderBrush = Brush("BorderSubtle"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 10)
            });
        }

        if (snapshot.Cards.Count == 0)
            CardsPanel.Children.Add(new TextBlock { Text = L("Keine Karten gefunden.", "No cards found."), Foreground = Brush("TextMuted") });

        CardsCard.Visibility = Visibility.Visible;
    }

    private UIElement BuildPinRow(ConnectorCard card, string pinType, ConnectorPinState? state)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };

        string statusText;
        string color;
        if (state is null)
        {
            statusText = L("unbekannt", "unknown");
            color = "TextMuted";
        }
        else if (state.Error is not null)
        {
            statusText = L("Fehler: ", "Error: ") + state.Error;
            color = "StatusOutage";
        }
        else
        {
            statusText = DescribePinStatus(state.Status);
            if (state.LeftTries is { } tries && !state.IsVerified)
                statusText += L($" · {tries} Versuche übrig", $" · {tries} tries left");
            color = state.IsVerified ? "StatusOk" : state.IsBlocked ? "StatusOutage" : "StatusPartial";
        }

        row.Children.Add(new TextBlock
        {
            Text = $"{pinType}: ",
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextMain"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        });
        row.Children.Add(new TextBlock
        {
            Text = statusText,
            Foreground = Brush(color),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420
        });

        var blocked = state?.IsBlocked == true;
        var transport = state?.IsTransportPin == true;

        if (!blocked && !transport && state?.IsVerified != true)
            row.Children.Add(ActionButton(L("PIN verifizieren", "Verify PIN"), async () => await PinActionAsync(card, pinType, "VerifyPin")));
        if (!blocked)
            row.Children.Add(ActionButton(transport ? L("Transport-PIN ändern", "Change transport PIN") : L("PIN ändern", "Change PIN"),
                async () => await PinActionAsync(card, pinType, "ChangePin")));
        if (blocked || state?.LeftTries is <= 1)
            row.Children.Add(ActionButton(L("Mit PUK entsperren", "Unblock with PUK"), async () => await PinActionAsync(card, pinType, "UnblockPin")));

        return row;
    }

    private static string DescribePinStatus(string status) => status.ToUpperInvariant() switch
    {
        "VERIFIED" => L("verifiziert ✓", "verified ✓"),
        "VERIFIABLE" => L("nicht verifiziert", "not verified"),
        "TRANSPORT_PIN" => L("Transport-PIN aktiv", "transport PIN active"),
        "EMPTY_PIN" => L("Leer-PIN – PIN muss gesetzt werden", "empty PIN – PIN must be set"),
        "BLOCKED" => L("gesperrt", "blocked"),
        "DISABLED" => L("deaktiviert", "disabled"),
        "" => L("unbekannt", "unknown"),
        _ => status
    };

    private void RenderTerminals(ConnectorSnapshot snapshot)
    {
        TerminalsPanel.Children.Clear();
        foreach (var t in snapshot.Terminals.OrderBy(t => t.CtId))
        {
            var cards = snapshot.Cards.Where(c => string.Equals(c.CtId, t.CtId, StringComparison.OrdinalIgnoreCase))
                .Select(c => $"Slot {c.SlotId}: {c.DisplayName}");
            var state = t.Connected switch
            {
                true => L("verbunden", "connected"),
                false => L("NICHT verbunden", "NOT connected"),
                null => L("Status unbekannt", "status unknown")
            };
            var parts = new List<string> { state };
            if (!string.IsNullOrWhiteSpace(t.IpAddress)) parts.Add($"IP {t.IpAddress}");
            if (!string.IsNullOrWhiteSpace(t.MacAddress)) parts.Add($"MAC {t.MacAddress}");
            if (t.Slots is { } slots) parts.Add($"{slots} Slots");
            if (t.IsPhysical == false) parts.Add(L("virtuell", "virtual"));
            var cardText = string.Join(" · ", cards);
            if (cardText.Length > 0) parts.Add(cardText);

            TerminalsPanel.Children.Add(Line(
                $"{(t.Connected == false ? "⚠" : "🖥")}  {(string.IsNullOrWhiteSpace(t.Name) ? t.CtId : t.Name)}  ({t.CtId})",
                string.Join("   ·   ", parts),
                t.Connected == false ? "StatusPartial" : null));
        }
        if (snapshot.Terminals.Count == 0)
            TerminalsPanel.Children.Add(new TextBlock { Text = L("Keine Kartenterminals gefunden.", "No card terminals found."), Foreground = Brush("TextMuted") });
        TerminalsCard.Visibility = Visibility.Visible;
    }

    private void RenderServices(ConnectorSnapshot snapshot)
    {
        ServicesPanel.Children.Clear();
        var services = snapshot.Directory?.Services ?? [];
        foreach (var group in services.GroupBy(s => s.Name).OrderBy(g => g.Key))
        {
            ServicesPanel.Children.Add(new TextBlock
            {
                Text = $"{group.Key}  —  {string.Join(", ", group.Select(s => s.Version).Distinct())}",
                Foreground = Brush("TextMain"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 2)
            });
            ServicesPanel.Children.Add(new TextBlock
            {
                Text = string.Join("\n", group.Select(s => s.Endpoint).Distinct()),
                Foreground = Brush("TextMuted"),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap
            });
        }
        ServicesCard.Visibility = services.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement Line(string title, string detail, string? colorKey)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Foreground = colorKey is null ? Brush("TextMain") : Brush(colorKey),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 12,
            Foreground = Brush("TextMuted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        return panel;
    }

    private System.Windows.Controls.Button ActionButton(string text, Func<Task> action)
    {
        var button = new System.Windows.Controls.Button
        {
            Content = text,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 2, 8, 2),
            FontSize = 12
        };
        button.Click += async (_, _) =>
        {
            if (_busy)
                return;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                App.LogUnhandled("ConnectorAction", ex);
                ShowWarning(DescribeException(ex));
            }
        };
        return button;
    }

    // =================================================================
    // Karten-Aktionen
    // =================================================================

    private ConnectorClient CreateClient() => new(_ownerMain.GetSettingsSnapshot().Connector, Log);

    private async Task PinActionAsync(ConnectorCard card, string pinType, string operation)
    {
        var terminal = _snapshot?.Terminals.FirstOrDefault(t => string.Equals(t.CtId, card.CtId, StringComparison.OrdinalIgnoreCase));
        var where = terminal is null || string.IsNullOrWhiteSpace(terminal.Name) ? card.CtId : terminal.Name;
        bool? setNewPin = null;

        string prompt;
        switch (operation)
        {
            case "VerifyPin":
                prompt = L($"{card.DisplayName} wird freigeschaltet.\n\nBitte nach dem Bestätigen die {pinType} am Kartenterminal „{where}“ eingeben.",
                           $"{card.DisplayName} will be unlocked.\n\nAfter confirming, enter {pinType} on card terminal \"{where}\".");
                break;
            case "ChangePin":
                prompt = L($"PIN von {card.DisplayName} ({pinType}) ändern.\n\nAm Kartenterminal „{where}“ zuerst die bisherige PIN (bzw. Transport-PIN), dann zweimal die neue PIN eingeben.",
                           $"Change PIN of {card.DisplayName} ({pinType}).\n\nOn card terminal \"{where}\" enter the current PIN (or transport PIN), then the new PIN twice.");
                break;
            case "UnblockPin":
                var choice = System.Windows.MessageBox.Show(this,
                    L($"{pinType} von {card.DisplayName} mit der PUK entsperren.\n\n" +
                      "⚠ Die PUK kann nur begrenzt oft verwendet werden (bei SMC-B/HBA i. d. R. 10× über die gesamte Kartenlaufzeit).\n\n" +
                      "Ja = entsperren und gleichzeitig eine neue PIN setzen\nNein = nur entsperren (bisherige PIN bleibt)\nAbbrechen = nichts tun",
                      $"Unblock {pinType} of {card.DisplayName} with the PUK.\n\n" +
                      "⚠ The PUK can only be used a limited number of times (usually 10× over the card's lifetime for SMC-B/HBA).\n\n" +
                      "Yes = unblock and set a new PIN\nNo = unblock only (current PIN stays)\nCancel = do nothing"),
                    L("PIN entsperren", "Unblock PIN"), MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
                if (choice == MessageBoxResult.Cancel)
                    return;
                setNewPin = choice == MessageBoxResult.Yes;
                prompt = L($"Bitte nach dem Bestätigen am Kartenterminal „{where}“ die PUK{(setNewPin == true ? " und anschließend zweimal die neue PIN" : "")} eingeben.",
                           $"After confirming, enter the PUK{(setNewPin == true ? " and then the new PIN twice" : "")} on card terminal \"{where}\".");
                break;
            default:
                return;
        }

        if (operation != "UnblockPin" &&
            System.Windows.MessageBox.Show(this, prompt, L("PIN-Eingabe am Kartenterminal", "PIN entry on card terminal"),
                MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
            return;
        if (operation == "UnblockPin" &&
            System.Windows.MessageBox.Show(this, prompt, L("PUK-Eingabe am Kartenterminal", "PUK entry on card terminal"),
                MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
            return;

        SetBusy(true, L($"Warte auf Eingabe am Kartenterminal „{where}“ …", $"Waiting for input on card terminal \"{where}\" …"));
        try
        {
            using var client = CreateClient();
            var result = await client.PinOperationAsync(operation, card.CardHandle, pinType, setNewPin, CancellationToken.None);
            Log($"{operation} {card.DisplayName} {pinType}: {result.Result} {(result.LeftTries is { } t ? $"(LeftTries {t})" : "")}");

            var message = result.Result.ToUpperInvariant() switch
            {
                "OK" => L("Erfolgreich.", "Successful."),
                "REJECTED" => L($"PIN falsch.{Tries(result)}", $"Wrong PIN.{Tries(result)}"),
                "WASBLOCKED" => L("Die PIN war bereits gesperrt – bitte mit PUK entsperren.", "The PIN was already blocked – please unblock with the PUK."),
                "NOWBLOCKED" => L("Die PIN ist jetzt gesperrt – bitte mit PUK entsperren.", "The PIN is now blocked – please unblock with the PUK."),
                "TRANSPORT_PIN" => L("Transport-PIN aktiv – bitte zuerst „PIN ändern“ ausführen.", "Transport PIN active – please run \"Change PIN\" first."),
                _ => result.Result
            };
            System.Windows.MessageBox.Show(this, message, card.DisplayName, MessageBoxButton.OK,
                result.IsOk ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log("✖ " + ex.Message);
            ShowWarning(DescribeException(ex));
        }
        finally
        {
            SetBusy(false);
        }

        await RefreshPinStatusAsync(card);
    }

    private static string Tries(ConnectorPinResult result) =>
        result.LeftTries is { } t ? L($" Verbleibende Versuche: {t}.", $" Tries left: {t}.") : "";

    private async Task RefreshPinStatusAsync(ConnectorCard card)
    {
        if (_snapshot is null)
            return;
        SetBusy(true, L("Lese PIN-Status …", "Reading PIN status …"));
        try
        {
            using var client = CreateClient();
            card.PinStates.Clear();
            foreach (var pinType in card.PinTypes)
            {
                try
                {
                    card.PinStates.Add(await client.GetPinStatusAsync(card.CardHandle, pinType, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    card.PinStates.Add(new ConnectorPinState { PinType = pinType, Error = ex.Message });
                }
            }
        }
        finally
        {
            SetBusy(false);
        }
        Render(_snapshot);
        _ownerMain.ConnectorMonitor.Accept(_snapshot);
    }

    private async Task VerifyCertificateAsync(ConnectorCard card)
    {
        if (card.AuthCertificate is null)
            return;
        SetBusy(true, L("Prüfe Zertifikat beim Konnektor (OCSP) …", "Verifying certificate with connector (OCSP) …"));
        try
        {
            using var client = CreateClient();
            var result = await client.VerifyCertificateAsync(card.AuthCertificate.Base64, CancellationToken.None);
            Log($"VerifyCertificate {card.DisplayName}: {result.Result}");
            var text = result.Result.ToUpperInvariant() switch
            {
                "VALID" => L("Zertifikat ist gültig ✓", "Certificate is valid ✓"),
                "INCONCLUSIVE" => L("Prüfung nicht eindeutig (z. B. OCSP nicht erreichbar).", "Verification inconclusive (e.g. OCSP unreachable)."),
                "INVALID" => L("Zertifikat ist UNGÜLTIG (gesperrt oder abgelaufen).", "Certificate is INVALID (revoked or expired)."),
                _ => result.Result
            };
            if (result.Roles.Count > 0)
                text += "\n\n" + L("Rollen: ", "Roles: ") + string.Join(", ", result.Roles);
            System.Windows.MessageBox.Show(this, $"{card.DisplayName} · {card.AuthCertificate.CertRef}\n\n{text}",
                L("Zertifikatsprüfung", "Certificate verification"), MessageBoxButton.OK,
                result.Result.Equals("VALID", StringComparison.OrdinalIgnoreCase) ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log("✖ " + ex.Message);
            ShowWarning(DescribeException(ex));
        }
        finally
        {
            SetBusy(false);
        }
    }

    // =================================================================
    // Bericht / Hilfen
    // =================================================================

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null)
        {
            ShowWarning(L("Noch keine Konnektor-Daten vorhanden.", "No connector data available yet."));
            return;
        }
        try
        {
            System.Windows.Clipboard.SetText(BuildReport(_snapshot, _ownerMain.GetSettingsSnapshot().Connector));
            FooterText.Text = L("Bericht in die Zwischenablage kopiert.", "Report copied to clipboard.");
        }
        catch (Exception ex)
        {
            ShowWarning(ex.Message);
        }
    }

    /// <summary>Diagnosebericht ohne Geheimnisse (keine Passwörter, kein Zertifikatsinhalt).</summary>
    public static string BuildReport(ConnectorSnapshot s, ConnectorSettings settings)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"TI-Lage Monitor {UpdateService.CurrentVersion} – Konnektor-Bericht {s.RetrievedAt:dd.MM.yyyy HH:mm:ss}");
        sb.AppendLine($"Adresse: {settings.Host}  Mandant: {settings.MandantId}  Clientsystem: {settings.ClientSystemId}  Arbeitsplatz: {settings.WorkplaceId}");
        if (s.Directory is { } d)
        {
            sb.AppendLine($"Konnektor: {d.ProductVendorName} {d.ProductName} ({d.ProductCode}), PTV {d.ProductTypeVersion}, FW {d.FirmwareVersion}, HW {d.HardwareVersion}");
            sb.AppendLine("Dienste: " + string.Join(", ", d.Services.Select(x => $"{x.Name} {x.Version}")));
        }
        if (s.Resource is { } r)
        {
            sb.AppendLine($"VPN TI: {r.VpnTiStatus} ({r.VpnTiTimestamp:g})  VPN SIS: {r.VpnSisStatus}");
            foreach (var e in r.ActiveErrors)
                sb.AppendLine($"Fehlerzustand: {e.Condition} {e.Severity} {e.Type} seit {e.ValidFrom:g}");
        }
        sb.AppendLine();
        sb.AppendLine("Kartenterminals:");
        foreach (var t in s.Terminals)
            sb.AppendLine($"  {t.CtId} {t.Name} IP {t.IpAddress} MAC {t.MacAddress} verbunden={t.Connected}");
        sb.AppendLine("Karten:");
        foreach (var c in s.Cards)
        {
            sb.Append($"  {c.CardType} {c.CardHolderName} ICCSN {c.Iccsn} @ {c.CtId}/{c.SlotId}");
            if ((c.AuthCertificate?.NotAfter ?? c.CertificateExpirationDate) is { } exp)
                sb.Append($" gültig bis {exp:dd.MM.yyyy}");
            if (c.AuthCertificate?.TelematikId is { } tid)
                sb.Append($" Telematik-ID {tid}");
            foreach (var p in c.PinStates)
                sb.Append($" {p.PinType}={(p.Error is null ? p.Status : "Fehler")}{(p.LeftTries is { } lt ? $"({lt})" : "")}");
            sb.AppendLine();
        }
        if (s.Warnings.Count > 0)
        {
            sb.AppendLine("Teilfehler:");
            foreach (var w in s.Warnings)
                sb.AppendLine("  " + w);
        }
        foreach (var a in ConnectorHealth.Evaluate(s, settings.CertificateWarningDays, DateTime.Now))
            sb.AppendLine($"Hinweis [{a.Urgency}]: {a.Title} – {a.Body}");
        return sb.ToString();
    }

    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;
        SaveConnectButton.IsEnabled = !busy;
        TestButton.IsEnabled = !busy;
        CardsPanel.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.AppStarting : null;
        if (text is not null)
            FooterText.Text = text;
        else if (!busy && _snapshot is not null)
            FooterText.Text = L($"Letzter Abruf: {_snapshot.RetrievedAt:dd.MM.yyyy HH:mm:ss}", $"Last retrieved: {_snapshot.RetrievedAt:yyyy-MM-dd HH:mm:ss}");
    }

    private void Log(string message)
    {
        void Append()
        {
            LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        }
        if (Dispatcher.CheckAccess())
            Append();
        else
            Dispatcher.BeginInvoke(Append);
    }

    private void ShowWarning(string message) =>
        System.Windows.MessageBox.Show(this, message, L("Konnektor", "Connector"), MessageBoxButton.OK, MessageBoxImage.Warning);

    private System.Windows.Media.Brush Brush(string key) =>
        TryFindResource(key) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;

    private static string FormatThumbprint(string hex) =>
        string.Join(":", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));

    private static string? Join(params string?[] parts)
    {
        var filtered = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return filtered.Count == 0 ? null : string.Join(" · ", filtered);
    }

    private static string? YesNo(bool? value) => value switch
    {
        true => L("ja", "yes"),
        false => L("nein", "no"),
        null => null
    };

    public void ApplyThemeRefresh()
    {
        if (_snapshot is not null)
            Render(_snapshot);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
