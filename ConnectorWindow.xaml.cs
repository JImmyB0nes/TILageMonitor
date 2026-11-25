using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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
            // Das Abonnement haelt das Fenster ueber den Monitor lebendig, und der Rueckruf
            // wuerde nach dem Schliessen in eine verworfene Maske zeichnen.
            _ownerMain.ConnectorMonitor.Updated -= OnMonitorUpdated;
            HomeAssistantPublisher.Updated -= OnHomeAssistantUpdated;
            try { _cts?.Cancel(); _cts?.Dispose(); _cts = null; } catch { /* ignore */ }
            _presenceTimer?.Stop();
        };

        // Der Reiter ist der tragende Teil, die Seitenleiste bildet ihn ab – auch dann, wenn
        // der Code woanders umschaltet (Erfolg, Fehler, fehlende Konfiguration).
        Tabs.SelectionChanged += (_, _) => SyncNav();
        Nav.SelectedIndex = 0;

        // Bereits vorhandenen Datenstand der Hintergrundüberwachung sofort anzeigen.
        var monitor = owner.ConnectorMonitor;
        if (monitor.LastSnapshot is not null)
            Render(monitor.LastSnapshot);

        Loaded += async (_, _) =>
        {
            var settings = owner.GetSettingsSnapshot().Connector;
            if (settings.Enabled && settings.IsConfigured)
            {
                // Ein Scheitern fuehrt hier zu denselben Einstellungen wie der Knopf – nur ohne
                // Dialog. Auf der Uebersicht steht dann nichts, und der Nutzer muss den Umweg
                // erst finden. Steht aus dem Hintergrundpollen schon ein Stand, bleibt die
                // Uebersicht dagegen stehen: der ist ja nicht falsch, nur aelter.
                if (!await LoadSnapshotAsync(settings, interactive: false) && _snapshot is null)
                    Tabs.SelectedItem = ConnectionTab;
            }
            else
            {
                // Ohne Zugang bringt die Uebersicht nichts als Leere – der Nutzer soll dorthin,
                // wo er zuerst etwas tun kann. War frueher dasselbe Verhalten, nur ohne Umweg.
                Tabs.SelectedItem = ConnectionTab;
            }

            // Der Ticker laeuft nur, solange das Fenster offen ist – im Hintergrund soll er
            // nichts zeichnen, was niemand sieht.
            StartPresenceTicker();
        };

        // Der Hintergrundpollen war die einzige Quelle, die dieses Fenster nicht erreichte: die
        // Maske zeigte denselben Stand, bis man selbst aktualisierte, waehrend der Knopf im
        // Hauptfenster laengst umgesprungen war. Dasselbe Bild, nur ohne eigenen Abruf.
        _ownerMain.ConnectorMonitor.Updated += OnMonitorUpdated;
        HomeAssistantPublisher.Updated += OnHomeAssistantUpdated;
        RenderHomeAssistantStatus();
    }

    // =================================================================
    // Home Assistant
    // =================================================================

    private void OnHomeAssistantUpdated() => Dispatcher.BeginInvoke(RenderHomeAssistantStatus);

    private void RenderHomeAssistantStatus()
    {
        var enabled = HaEnabledToggle.IsChecked == true;
        string key;
        string text;
        if (HomeAssistantPublisher.LastError is { } error)
        {
            key = "StatusOutage";
            text = error;
        }
        else if (HomeAssistantPublisher.LastSuccess is { } at)
        {
            key = "StatusOk";
            text = L($"{HomeAssistantPublisher.LastCount} Sensoren gesendet um {at:HH:mm:ss} Uhr.",
                $"{HomeAssistantPublisher.LastCount} sensors sent at {at:HH:mm:ss}.");
        }
        else
        {
            key = "StatusEmpty";
            text = enabled
                ? L("Wird beim nächsten Abruf gesendet.", "Will be sent with the next poll.")
                : L("Aus. Einschalten, Adresse und Token eintragen, dann „Speichern & verbinden“.",
                    "Off. Switch on, enter address and token, then save.");
        }
        HaStatusDot.Fill = Brush(key);
        HaStatusText.Text = text;
    }

    private void HaNameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateHaPreview();

    private void UpdateHaPreview()
    {
        if (HaEntityPreview is null)
            return;
        var settings = _ownerMain.GetSettingsSnapshot().Connector.Clone();
        settings.HomeAssistantName = HaNameBox.Text;
        var name = ConnectorMonitor.HomeAssistantName(settings, _snapshot ?? _ownerMain.ConnectorMonitor.LastSnapshot);
        var slug = HomeAssistantStates.Slug(name);
        HaEntityPreview.Text = $"binary_sensor.{HomeAssistantStates.Prefix}_{slug}_erreichbar\n" +
                               $"binary_sensor.{HomeAssistantStates.Prefix}_{slug}_vpn_ti\n" +
                               $"sensor.{HomeAssistantStates.Prefix}_{slug}_smcb_…_ablauf";
    }

    private async void HaTest_Click(object sender, RoutedEventArgs e)
    {
        var url = HaUrlBox.Text.Trim();
        var token = HaTokenBox.Password.Length > 0
            ? HaTokenBox.Password
            : SafeUnprotect(_ownerMain.GetSettingsSnapshot().Connector.HomeAssistantTokenProtected);
        if (url.Length == 0 || token.Length == 0)
        {
            HaStatusDot.Fill = Brush("StatusPartial");
            HaStatusText.Text = L("Bitte Adresse und Token eintragen.", "Please enter address and token.");
            return;
        }
        HaTestButton.IsEnabled = false;
        HaStatusDot.Fill = Brush("StatusEmpty");
        HaStatusText.Text = L("Prüfe …", "Checking …");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var error = await HomeAssistantPublisher.TestAsync(url, token, cts.Token);
            HaStatusDot.Fill = Brush(error is null ? "StatusOk" : "StatusOutage");
            HaStatusText.Text = error ?? L("Verbindung zu Home Assistant steht. Speichern, damit gesendet wird.",
                "Connected to Home Assistant. Save to start sending.");
        }
        finally
        {
            HaTestButton.IsEnabled = true;
        }
    }

    private static string SafeUnprotect(string? value)
    {
        try { return SecretProtector.Unprotect(value); }
        catch (SecretProtectorException) { return ""; }
    }

    /// <summary>Zeichnet den Stand der Hintergrundueberwachung nach, ohne selbst abzurufen.</summary>
    private void OnMonitorUpdated()
    {
        void Apply()
        {
            // Waehrend einer laufenden Aktion bleibt die Maske stehen, sonst faellt sie mitten
            // im Dialog um – und die Knopfe, die gerade geklemmt haben, werden neu gebaut.
            if (_busy)
                return;
            var latest = _ownerMain.ConnectorMonitor.LastSnapshot;
            // Nach Alter ziehen, nicht nach Instanz. Der Monitor meldet sich naemlich auch, wenn
            // ein Poll gescheitert ist – dann bleibt sein Stand unveraendelt. Ein reiner
            // Instanzvergleich haette den aelteren Stand ueber die frischeren Angaben des
            // Nutzers gezeichnet, die gerade erst per 'Test' oder 'Aktualisieren' geholt wurden.
            if (latest is not null && latest.RetrievedAt > (_snapshot?.RetrievedAt ?? DateTime.MinValue))
            {
                CarryOverCardDetails(_snapshot, latest);
                Render(latest);
            }
            else if (_snapshot is not null)
                RenderPresence();
        }

        if (Dispatcher.CheckAccess())
            Apply();
        else
            Dispatcher.BeginInvoke(Apply);
    }

    private static string L(string de, string en) => LocalizationService.Pick(de, en);

    /// <summary>
    /// Der Hintergrund liest keine Kartenzertifikate (nur C.QES selten nach). Ohne Uebernahme
    /// verschwanden nach dem naechsten Poll die per „Alle Zertifikate lesen“ geholten
    /// Zertifikate samt OCSP-Knopf wieder. Uebernommen wird nur fuer dieselbe Karte
    /// (gleicher Kartengriff bzw. gleiche ICCSN) und nur, was der neue Stand nicht selbst hat.
    /// </summary>
    internal static void CarryOverCardDetails(ConnectorSnapshot? previous, ConnectorSnapshot latest)
    {
        if (previous is null || ReferenceEquals(previous, latest))
            return;
        foreach (var card in latest.Cards)
        {
            var old = previous.Cards.FirstOrDefault(c =>
                (!string.IsNullOrEmpty(card.CardHandle) && c.CardHandle == card.CardHandle)
                || (!string.IsNullOrEmpty(card.Iccsn) && string.Equals(c.Iccsn, card.Iccsn, StringComparison.OrdinalIgnoreCase)));
            if (old is null)
                continue;
            card.AuthCertificate ??= old.AuthCertificate;
            foreach (var cert in old.Certificates)
                if (!card.Certificates.Any(c => c.RefEquals(cert.CertRef)))
                    card.Certificates.Add(cert);
        }
    }

    /// <summary>
    /// Haelt die Anwesenheitsangabe frisch. Ohne diesen Tick steht "vor 12 s" auch noch nach
    /// einer halben Stunde da – und wirkt damit schlechter, als die Anzeige tatsaechtlich ist.
    /// </summary>
    private DispatcherTimer? _presenceTimer;

    private void StartPresenceTicker()
    {
        _presenceTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _presenceTimer.Stop();
        _presenceTimer.Tick -= OnPresenceTick;
        _presenceTimer.Tick += OnPresenceTick;
        _presenceTimer.Start();
    }

    private void OnPresenceTick(object? sender, EventArgs e)
    {
        if (!IsVisible || _busy)
            return;
        RenderPresence();
    }

    /// <summary>
    /// Seitenleiste und Reiter sind dieselbe Auswahl – der bestehende Code schaltet weiter
    /// ueber Tabs. Rueckwaerts wird die Verknuepfung ueber den Waelter abgeschirmt, damit
    /// keine Endlosschleife laeuft, wenn die Zuordnung von beiden Seiten angestoessen wird.
    /// </summary>
    private bool _syncingNav;

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingNav)
            return;
        _syncingNav = true;
        try
        {
            if (Nav.SelectedIndex >= 0)
                Tabs.SelectedIndex = Nav.SelectedIndex;
            // Der Verlauf waechst auch ohne neuen Datenstand (Fehlschlaege) – beim Oeffnen frisch lesen.
            if (ReferenceEquals(Nav.SelectedItem, NavHistory))
                RenderHistory();
        }
        finally
        {
            _syncingNav = false;
        }
    }

    /// <summary>Bringt die Seitenleiste auf denselben Stand wie den aktiven Reiter.</summary>
    private void SyncNav()
    {
        if (_syncingNav || Tabs.SelectedIndex < 0 || Tabs.SelectedIndex >= Nav.Items.Count)
            return;
        _syncingNav = true;
        try
        {
            Nav.SelectedIndex = Tabs.SelectedIndex;
        }
        finally
        {
            _syncingNav = false;
        }
    }

    /// <summary>
    /// Zaehler in der Seitenleiste. Sie zeigen die Menge, keine Staende – eine Zahl sagt,
    /// wo man hin muss, ohne dass man die Reiter einzeln durchprobiert.
    /// </summary>
    private void UpdateNavCounts(ConnectorSnapshot snapshot)
    {
        NavCardsCount.Text = snapshot.Cards.Count.ToString(CultureInfo.InvariantCulture);
        NavCardsBadge.Visibility = snapshot.Cards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NavTerminalsCount.Text = snapshot.Terminals.Count.ToString(CultureInfo.InvariantCulture);
        NavTerminalsBadge.Visibility = snapshot.Terminals.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

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
        EventSubscriptionsToggle.IsChecked = s.EventSubscriptionsEnabled;
        HistoryToggle.IsChecked = s.HistoryEnabled;
        EventStreamInfo.Text = _ownerMain.ConnectorMonitor.DescribeEventStream();
        PollMinutesBox.Text = s.PollMinutes.ToString(CultureInfo.InvariantCulture);
        CertWarnDaysBox.Text = s.CertificateWarningDays.ToString(CultureInfo.InvariantCulture);
        CertPasswordHint.Text = string.IsNullOrEmpty(s.ClientCertificatePasswordProtected) ? "" : L("gespeichert", "saved");
        UpdatePinnedText(s.PinnedServerThumbprint);
        UpdateClientCertInfo(s);
        HaEnabledToggle.IsChecked = s.HomeAssistantEnabled;
        HaUrlBox.Text = s.HomeAssistantUrl;
        HaNameBox.Text = s.HomeAssistantName;
        HaTokenHint.Text = string.IsNullOrEmpty(s.HomeAssistantTokenProtected) ? "" : L("gespeichert", "saved");
        UpdateHaPreview();
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
        current.EventSubscriptionsEnabled = EventSubscriptionsToggle.IsChecked == true;
        current.HistoryEnabled = HistoryToggle.IsChecked == true;
        if (int.TryParse(PollMinutesBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var poll))
            current.PollMinutes = Math.Clamp(poll, 1, 120);
        if (int.TryParse(CertWarnDaysBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
            current.CertificateWarningDays = Math.Clamp(days, 1, 365);

        if (CertPasswordBox.Password.Length > 0)
            current.ClientCertificatePasswordProtected = SecretProtector.Protect(CertPasswordBox.Password);
        else if (ClearCertPasswordToggle.IsChecked == true)
            current.ClientCertificatePasswordProtected = null;
        current.HomeAssistantEnabled = HaEnabledToggle.IsChecked == true;
        current.HomeAssistantUrl = HaUrlBox.Text.Trim();
        current.HomeAssistantName = HaNameBox.Text.Trim();
        if (HaTokenBox.Password.Length > 0)
            current.HomeAssistantTokenProtected = SecretProtector.Protect(HaTokenBox.Password.Trim());
        if (BasicPasswordBox.Password.Length > 0)
            current.BasicAuthPasswordProtected = SecretProtector.Protect(BasicPasswordBox.Password);
        else if (ClearBasicPasswordToggle.IsChecked == true)
            current.BasicAuthPasswordProtected = null;

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
        // Konnektoren (v. a. RISE) quittieren unzulässige IDs mit "4000 Syntaxfehler" – vorher prüfen.
        var contextProblems = ConnectorProtocol.ValidateContextIds(s);
        if (contextProblems is not null)
            return L("Die IDs im Aufrufkontext lehnt der Konnektor höchstwahrscheinlich ab:\n\n" + contextProblems,
                     "The connector most likely rejects these call-context IDs:\n\n" + contextProblems);
        if (!int.TryParse(PollMinutesBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var poll) || poll is < 1 or > 120)
            return L("Das Intervall muss eine ganze Zahl zwischen 1 und 120 Minuten sein.", "The interval must be a whole number between 1 and 120 minutes.");
        if (!int.TryParse(CertWarnDaysBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var warnDays) || warnDays is < 1 or > 365)
            return L("„Warnung ab“ muss eine ganze Zahl zwischen 1 und 365 Tagen sein.", "“Warn from” must be a whole number between 1 and 365 days.");
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
        ClearCertPasswordToggle.IsChecked = false;
        ClearBasicPasswordToggle.IsChecked = false;
        CertPasswordHint.Text = string.IsNullOrEmpty(connector.ClientCertificatePasswordProtected) ? "" : L("gespeichert", "saved");
        UpdatePinnedText(connector.PinnedServerThumbprint);
    }

    private void UpdatePinnedText(string? thumbprint) =>
        PinnedThumbprintText.Text = string.IsNullOrWhiteSpace(thumbprint)
            ? L("nicht gepinnt – wird bei der ersten Verbindung abgefragt", "not pinned – you will be asked on first connection")
            : FormatThumbprint(thumbprint);

    private void UpdateClientCertInfo(ConnectorSettings s)
    {
        ClientCertInfo.Text = L("Noch kein Client-Zertifikat geladen.", "No client certificate loaded yet.");
        if (string.IsNullOrWhiteSpace(s.ClientCertificatePath) || !File.Exists(s.ClientCertificatePath))
            return;
        try
        {
            var cert = ConnectorClient.LoadClientCertificate(s.ClientCertificatePath, SecretProtector.Unprotect(s.ClientCertificatePasswordProtected));
            ClientCertInfo.Text = L($"Zertifikat: {cert.Subject} · gültig bis {cert.NotAfter:dd.MM.yyyy}",
                                    $"Certificate: {cert.Subject} · valid until {cert.NotAfter:yyyy-MM-dd}");
            if (cert.NotAfter < DateTime.Now.AddDays(30))
                ClientCertInfo.Text += L("  ⚠ läuft bald ab", "  ⚠ expires soon");
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


    private void NoteContextWarnings(ConnectorSettings s)
    {
        var warn = ConnectorProtocol.WarnContextIds(s);
        if (warn is not null)
            Log(L("Hinweis zu den Kontext-IDs (nicht blockierend): ", "Note on context IDs (not blocking): ") + warn);
    }

    /// <summary>Basic-Auth ueber reines HTTP schickt Benutzer und Passwort im Klartext durchs LAN.</summary>
    private bool ConfirmCleartextBasicAuth(ConnectorSettings s)
    {
        if (!s.SendsBasicAuthInClear)
            return true;
        return System.Windows.MessageBox.Show(this,
            L("Basic-Auth ist ohne TLS eingestellt: Benutzername und Passwort gehen im Klartext durchs Netz.\n\nTrotzdem fortfahren?",
              "Basic auth is configured without TLS: user name and password travel the network in clear text.\n\nContinue anyway?"),
            L("Unverschlüsselte Anmeldung", "Unencrypted authentication"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
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
        if (!ConfirmCleartextBasicAuth(s))
            return;
        NoteContextWarnings(s);
        Persist(s);
        UpdateClientCertInfo(s);
        if (await LoadSnapshotAsync(s, interactive: true))
        {
            // Nach gelungener Verbindung zurueck auf die Ansicht mit den Ergebnissen, nicht die
            // Einstellungen, die man soeben erledigt hat.
            Tabs.SelectedIndex = 0;
        }
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
        if (!ConfirmCleartextBasicAuth(s))
            return;
        NoteContextWarnings(s);
        UpdateClientCertInfo(s);
        await LoadSnapshotAsync(s, interactive: true, persistTrustOnly: true);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var s = _ownerMain.GetSettingsSnapshot().Connector;
        if (!s.IsConfigured)
        {
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
        _cts?.Dispose();
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
            ShowStatus(L("Konnektor nicht erreichbar", "Connector unreachable"), message, "StatusOutage", "\u2715");
            // Scheiternder Zugang gehoert an die Einstellugen, nicht hinter einen Hinweis auf der
            // Uebersicht, den man erst finden muss.
            if (interactive)
                Tabs.SelectedItem = ConnectionTab;
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
        var dialog = new Window
        {
            Title = L("Konnektor-Zertifikat bestätigen", "Confirm connector certificate"),
            Width = 520,
            Height = 460,
            MinWidth = 440,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = Brush("WindowBackground"),
        };
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        var icon = new TextBlock
        {
            Text = "\uE72E",
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 28,
            Foreground = Brush("TextMain"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        root.Children.Add(icon);
        root.Children.Add(new TextBlock
        {
            Text = L("Dieses TLS-Zertifikat wird vom Konnektor vorgelegt. Vergleichen Sie den Fingerprint mit der Konnektor-Administration.",
                     "The connector is presenting this TLS certificate. Compare the fingerprint with the connector administration."),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextMain"),
            Margin = new Thickness(0, 0, 0, 16)
        });
        var card = new Border
        {
            Background = Brush("ChipBackground"),
            BorderBrush = Brush("BorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 0, 18)
        };
        var info = new StackPanel();
        void AddRow(string label, string value, bool mono = false)
        {
            info.Children.Add(new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextMuted"), Margin = new Thickness(0, 0, 0, 2) });
            info.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 13,
                FontFamily = new System.Windows.Media.FontFamily(mono ? "Consolas, Cascadia Mono" : "Segoe UI Variable, Segoe UI"),
                Foreground = Brush("TextMain"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });
        }
        AddRow(L("Zertifikat", "Certificate"), ex.Subject ?? "");
        AddRow(L("Gültig bis", "Valid until"), ex.NotAfter.ToString("dd.MM.yyyy", LocalizationService.DisplayCulture));
        AddRow("SHA-256", FormatThumbprint(ex.Thumbprint), mono: true);
        card.Child = info;
        root.Children.Add(card);
        root.Children.Add(new TextBlock
        {
            Text = L("Diesem Zertifikat dauerhaft vertrauen?", "Trust this certificate permanently?"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextMain"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var no = new System.Windows.Controls.Button { Content = L("Abbrechen", "Cancel"), Padding = new Thickness(16, 8, 16, 8), MinWidth = 110, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var yes = new System.Windows.Controls.Button { Content = L("Vertrauen", "Trust"), Padding = new Thickness(16, 8, 16, 8), MinWidth = 110, IsDefault = true, Style = (Style)FindResource("AccentButtonStyle") };
        var accepted = false;
        yes.Click += (_, _) => { accepted = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        root.Children.Add(buttons);
        dialog.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        dialog.ShowDialog();
        return accepted;
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
            L($"Stand {snapshot.RetrievedAt:HH:mm:ss} Uhr", $"as of {snapshot.RetrievedAt:HH:mm:ss}"),
            worst switch
            {
                ToastUrgency.Error => "StatusOutage",
                ToastUrgency.Warning => "StatusPartial",
                _ => "StatusOk"
            },
            worst switch
            {
                ToastUrgency.Error => "\u2715",
                ToastUrgency.Warning => "\u203A",
                _ => "\u2713"
            });

        // Kennzahlen direkt im Banner, damit sie nicht als Textkoloss unter dem Titel untergehen.
        StatusChips.Children.Clear();
        StatusChips.Children.Add(AmpelChip(L("Erreichbar", "Reachable"), true));
        StatusChips.Children.Add(AmpelChip(L("VPN TI", "VPN TI"), snapshot.Resource?.IsTiOnline));
        StatusChips.Children.Add(AmpelChip(L("VPN SIS", "VPN SIS"), snapshot.Resource?.IsSisOnline));
        var live = _ownerMain.ConnectorMonitor.SubscriptionsAlive && _ownerMain.ConnectorMonitor.EventStreamActive;
        StatusChips.Children.Add(AmpelChip(L("Live-Ereignisse", "Live events"), live));
        StatusChips.Children.Add(Kpi(L("Karten", "Cards"), snapshot.Cards.Count.ToString()));
        StatusChips.Children.Add(Kpi(L("Terminals", "Terminals"), snapshot.Terminals.Count.ToString()));

        UpdateNavCounts(snapshot);
        RenderAlerts(alerts, snapshot.Warnings);
        RenderPresence();
        RenderConnectorInfo(snapshot);
        RenderSubscriptions(snapshot);
        RenderCards(snapshot);
        RenderTerminals(snapshot);
        RenderServices(snapshot);
        RenderCertificates(snapshot);
        RenderEventsSummary();
        RenderHistory();
        FooterText.Text = L($"Letzter Abruf: {snapshot.RetrievedAt:dd.MM.yyyy HH:mm:ss}", $"Last retrieved: {snapshot.RetrievedAt:yyyy-MM-dd HH:mm:ss}");
        if (_ownerMain.ConnectorMonitor.DescribeReconnect(DateTime.Now) is { } reconnect)
            FooterText.Text += "  ·  " + reconnect;
    }

    /// <summary>Letzte Statusanzeige – fuer den Neuaufbau nach einem Themenwechsel ohne Datenstand.</summary>
    private (string Title, string Detail, string Key, string Glyph)? _lastStatus;

    private void ShowStatus(string title, string detail, string statusKey, string glyph = "\u2022")
    {
        _lastStatus = (title, detail, statusKey, glyph);
        StatusTitle.Text = title;
        StatusDetail.Text = detail;
        StatusGlyph.Glyph = FluentGlyph(glyph);
        StatusIconCircle.Background = Brush(statusKey);
        StatusBanner.Background = Brush(statusKey + "Surface");
        StatusBanner.BorderBrush = Brush(statusKey + "Border");
    }

    /// <summary>
    /// Die Aufrufer reichen weiterhin einfache Zeichen (✓, ✕, ›, •) – im Kopf stehen sie als
    /// Fluent-Symbol. Unbekanntes bleibt, wie es ist.
    /// </summary>
    private static string FluentGlyph(string glyph) => glyph switch
    {
        "\u2713" => "\uE73E", // Haken
        "\u2715" => "\uE711", // Kreuz
        "\u203A" => "\uE7BA", // Warnung
        "\u2022" or "" => "\uE703", // Verbindung
        _ => glyph
    };

    /// <summary>
    /// Kennzahl im Banner: winzige Bezeichnung, darunter der Wert. So ist der Zustand auf
    /// einen Blick lesbar, ohne dass vier gleich garte Kapseln aneinandergeklebt wirken.
    /// </summary>
    private Border AmpelChip(string label, bool? ok)
    {
        var color = ok is true ? "StatusOk" : ok is false ? "StatusOutage" : "StatusEmpty";
        var value = ok is true ? L("ja", "yes") : ok is false ? L("nein", "no") : L("unbekannt", "unknown");
        return Kpi(label, value, color, dot: true);
    }

    /// <summary>
    /// Kennzahl-Kachel im Kopf: oben die Bezeichnung, darunter groß der Wert – bei Ampelwerten
    /// mit farbigem Punkt davor. Alle Kacheln sind gleich breit (UniformGrid).
    /// </summary>
    private Border Kpi(string label, string value, string? colorKey = null, bool dot = false)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextMuted"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var line = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        if (dot)
        {
            line.Children.Add(new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(5),
                Background = Brush(colorKey ?? "StatusEmpty"),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        line.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(dot ? "TextMain" : colorKey ?? "TextMain"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        panel.Children.Add(line);
        return new Border { Child = panel, Style = (Style)FindResource("KpiStyle") };
    }

    /// <summary>
    /// Flache Info-Kapsel fuer Wertelisten. Haelt mehrere Angaben in einer Zeile, statt
    /// jede fuer sich auf eine eigene Zeile zu zwingen.
    /// </summary>
    private Border Chip(string value, string? colorKey)
    {
        return new Border
        {
            Child = new TextBlock
            {
                Text = value,
                FontSize = 12,
                Foreground = Brush(colorKey ?? "TextMain"),
                TextWrapping = TextWrapping.Wrap
            },
            Style = (Style)FindResource("ChipStyle")
        };
    }

    private void RenderAlerts(List<ConnectorAlert> alerts, List<string> warnings)
    {
        AlertsPanel.Children.Clear();
        foreach (var alert in alerts.OrderByDescending(a => a.Urgency))
        {
            var glyph = alert.Urgency == ToastUrgency.Error ? "\uEA39" : alert.Urgency == ToastUrgency.Warning ? "\uE7BA" : "\uE946";
            AlertsPanel.Children.Add(Alert(alert.Title, alert.Body, alert.Urgency == ToastUrgency.Error ? "StatusOutage" : "StatusPartial", glyph));
        }
        foreach (var warning in warnings)
            AlertsPanel.Children.Add(Alert(L("Teilabfrage fehlgeschlagen", "Partial query failed"), warning, null));

        AlertsCard.Visibility = AlertsPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Hinweis mit abgetrenntem Detailtext. Konnektor-Fehler enthalten sonst die komplette
    /// gesendete Nachricht – das sprengt jede Maske. Der Kernsatz steht oben, alles Weitere
    /// erst auf Knopfdruck. Der volle Text bleibt im Protokollreiter und im Bericht erhalten.
    /// </summary>
    private UIElement Alert(string title, string body, string? colorKey, string glyph = "\uE946")
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new AppIcon
        {
            Glyph = glyph,
            Size = 16,
            Stroke = Brush(colorKey ?? "TextMuted"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 12, 0)
        };
        grid.Children.Add(icon);

        var panel = new StackPanel();
        Grid.SetColumn(panel, 1);
        grid.Children.Add(panel);
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextMain"),
            TextWrapping = TextWrapping.Wrap
        });

        var lines = body.Split('\n');
        var lead = lines[0].Trim();
        if (lead.Length > 300)
            lead = lead[..300] + " …";
        panel.Children.Add(new TextBlock
        {
            Text = lead,
            FontSize = 12,
            Foreground = Brush("TextMuted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        });

        // Alles ab der zweiten Zeile ist Zusatzmaterial (IDs, Namespaces, XML): nur bei Bedarf.
        if (lines.Length > 1)
        {
            var more = new Expander
            {
                Header = L("Details anzeigen", "Show details"),
                FontSize = 11,
                Foreground = Brush("TextMuted"),
                Padding = new Thickness(0, 4, 0, 4),
                Margin = new Thickness(0, 4, 0, 0),
                Style = (Style)FindResource("ModernExpanderStyle")
            };
            more.Content = new TextBlock
            {
                Text = string.Join("\n", lines.Skip(1)).Trim(),
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
                FontSize = 11,
                Foreground = Brush("TextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            };
            panel.Children.Add(more);
        }

        // Getönte Zeile in der Farbe der Dringlichkeit – wie die Meldungen im Hauptfenster.
        return new Border
        {
            Child = grid,
            Background = colorKey is null ? Brush("ChipBackground") : Brush(colorKey + "Surface"),
            BorderBrush = colorKey is null ? Brush("BorderSubtle") : Brush(colorKey + "Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 8)
        };
    }

    private static readonly System.Windows.Media.FontFamily FluentFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>
    /// Karte rein/raus. Gezogen wird der Stand der Hintergrundueberwachung, weil der die
    /// Ereignisse zuerst sieht – die Maske hat nach einem Poll meist noch nichts getan.
    /// </summary>
    private void RenderPresence()
    {
        var monitor = _ownerMain.ConnectorMonitor;
        var presence = monitor.LastPresence ?? (_snapshot is not null ? ConnectorPresence.FromSnapshot(_snapshot) : null);
        if (presence is null)
        {
            PresenceCard.Visibility = Visibility.Collapsed;
            return;
        }

        var now = DateTime.Now;
        var tone = presence.Inserted ? "StatusOk" : "StatusOutage";
        PresenceTile.Background = Brush(tone + "Surface");
        PresenceTile.BorderBrush = Brush(tone + "Border");
        PresenceIcon.Stroke = Brush(tone);
        PresenceEyebrow.Text = presence.ViaEvent
            ? L("Letzte Kartenbewegung", "Last card event")
            : L("Gesteckte Karte", "Inserted card");
        PresenceState.Text = presence.Headline;

        // Statt der nackten Terminal-MAC den Namen des Terminals zeigen, sofern bekannt.
        var terminalName = presence.CtId;
        if (_snapshot is not null && IndexTerminals(_snapshot).TryGetValue(presence.CtId, out var terminal)
            && !string.IsNullOrWhiteSpace(terminal.Name))
            terminalName = terminal.Name;
        PresenceDetail.Text = string.IsNullOrWhiteSpace(terminalName)
            ? L("Terminal nicht gemeldet", "terminal not reported")
            : L($"im Kartenterminal {terminalName}", $"in card terminal {terminalName}");
        PresenceDetail.ToolTip = string.IsNullOrWhiteSpace(presence.CtId) ? null : $"CtId {presence.CtId}";
        PresenceAge.Text = presence.AgeText(now);
        PresenceSource.Text = presence.ViaEvent
            ? L("per Ereignis gemeldet", "reported by event")
            : L("aus der regelmäßigen Abfrage", "from regular polling");
        PresenceCard.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Was der Konnektor derzeit an diese App meldet. Dieselbe Angabe, die bisher nur im
    /// Diagnosebericht stand, aber dort liest niemand, wenn es klemmt.
    /// </summary>
    private void RenderSubscriptions(ConnectorSnapshot snapshot)
    {
        SubscriptionsList.Items.Clear();
        var mine = OwnTarget();
        var ours = 0;

        foreach (var sub in snapshot.Subscriptions)
        {
            // Wer auf einer Konfigurationsseite nur die Topic-Zeile vorfuehrt, erzaehlt dem
            // Nutzer, dass etwas gut aussieht. Ein Konnektor fuehrt aber auch die Abos der
            // Nachbarn und frueherer Sitzungen: die Liste ist wahr, wenn sie gefuellt ist,
            // und sagt doch nichts ueber diese App.
            var isOurs = IsOurs(sub, mine);
            if (isOurs)
                ours++;

            var panel = new StackPanel();
            var head = new DockPanel();
            if (isOurs)
            {
                var mineBadge = Badge(L("diese App", "this app"), "StatusMaintenance");
                DockPanel.SetDock(mineBadge, Dock.Right);
                mineBadge.Margin = new Thickness(10, 0, 0, 0);
                mineBadge.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(mineBadge);
            }
            head.Children.Add(new TextBlock
            {
                Text = sub.Topic,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("TextMain"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(head);
            var tail = new List<string> { sub.EventTo };
            if (sub.TerminationTime is { } until)
            {
                // Die Ablaufzeit entscheidet, ob ein Abo ueberhaupt noch liefert. Sie so
                // selbstverstaendlich mitlaufen zu lassen, war der Grund, warum abgelaufene
                // Abos wochenlang als aktiv galten – die Zahl stand da, niemand las sie.
                var expired = until <= DateTime.Now;
                tail.Add((expired ? L("abgelaufen am", "expired on") : L("gültig bis", "valid until")) + " " + until.ToString("dd.MM.yyyy HH:mm", LocalizationService.DisplayCulture));
            }
            var line = new TextBlock
            {
                Text = string.Join(" · ", tail.Where(t => !string.IsNullOrWhiteSpace(t))),
                FontSize = 11,
                Foreground = sub.Alive(DateTime.Now) ? Brush("TextMuted") : Brush("StatusOutage"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            };
            panel.Children.Add(line);
            SubscriptionsList.Items.Add(new Border
            {
                Child = panel,
                Background = Brush(isOurs ? "StatusMaintenanceSurface" : "ChipBackground"),
                BorderBrush = Brush(isOurs ? "StatusMaintenanceBorder" : "BorderSubtle"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 6)
            });
        }

        if (snapshot.Subscriptions.Count > 0)
        {
            // Der Hinweis entscheidet, ob die Liste beruhigt oder beunruhigt. Ohne ihn steht
            // unter einer gefuellten Liste, dass alles gut sei, waehrend die eigenen Abos
            // langst weg sind und die Nachbarn die Liste vollhalten.
            SubscriptionsStatus.Text = ours > 0
                ? string.Format(
                    L("{0} von {1} Abos gehören zu dieser App und stehen noch.", "{0} of {1} subscriptions belong to this app and are still standing."),
                    ours, snapshot.Subscriptions.Count)
                : L("Kein Eintrag gehört zu dieser App – die App pollt im eingestellten Intervall.",
                    "No entry belongs to this app – the app polls at the configured interval.");
            SubscriptionsStatus.Visibility = Visibility.Visible;
            return;
        }

        // Leer ist keine Fehlmeldung, sondern die Erklaerung. Die Begruendung des Lauschers
        // gehoert daneben, sonst steht "keine Abos" da wie ein Fehler.
        SubscriptionsStatus.Text = _ownerMain.ConnectorMonitor.DescribeEventStream();
        SubscriptionsStatus.Visibility = Visibility.Visible;
    }

    /// <summary>Rechneranteil der eigenen Adresse, Hafen abgeschnitten. Null, wenn kein
    /// Lauscher steht – damit nicht mit einer leeren Zeichenkette jede Adresse getroffen wird.</summary>
    private string? OwnTarget() => _ownerMain.ConnectorMonitor.EventTarget;

    /// <summary>Dieselbe Ordnung wie im Hintergrundpoller, damit die Anzeige und die
    /// Erneuerung nicht an einem Eintrag auseinandergeraten.</summary>
    private bool IsOurs(ConnectorSubscription sub, string? target) =>
        _ownerMain.ConnectorMonitor.IsOwnSubscription(sub) || sub.TargetsExactly(target);

    /// <summary>
    /// Kuendigt alle Abos, die auf diesem Rechner zeigen. Ein Laeufer, der nach
    /// Fehlversuchen mehrfach steht, blockiert dieselbe Adresse immer wieder neu – und
    /// uebermaalt damit die Anzeige, die man eigentlich sehen will.
    /// </summary>
    private async void Unsubscribe_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null || _snapshot.Subscriptions.Count == 0)
        {
            ShowWarning(L("Es sind keine Ereignis-Abos eingetragen.", "No event subscriptions are registered."));
            return;
        }

        // Ohne stehenden Lauscher kennt die App ihre Adresse nicht. Sie dann alle zu
        // kuendigen, waer der einzige Fall, in dem der Knopf etwas anderes tut, als er
        // verspricht: auf einem Konnektor mit andern Anwendern loescht er deren Abos.
        var mine = OwnTarget();
        if (string.IsNullOrWhiteSpace(mine))
        {
            ShowWarning(L("Die App horcht derzeit auf keinem Port. Ohne eigene Adresse lässt sich nicht sagen, welche Einträge zu ihr gehören – gekündigt wird deshalb nichts.",
                          "The app is not listening on any port. Without its own address it cannot be told which entries belong to it, so nothing is cancelled."));
            return;
        }

        var ours = _snapshot.Subscriptions.Where(s => IsOurs(s, mine)).ToList();
        if (ours.Count == 0)
        {
            ShowWarning(L("Kein Eintrag zeigt auf diese App. Die eingetragenen Abos gehören anderen Anwendern oder einer früheren Adresse.",
                          "No entry points at this app. The listed subscriptions belong to other users or to an earlier address."));
            return;
        }

        if (System.Windows.MessageBox.Show(this,
                string.Format(
                    L("{0} von {1} eingetragenen Abos kündigen, die an diese App gehen?\n\nDanach fällt die Anzeige auf reines Pollen zurück, bis erneut abonniert wird.",
                      "Unsubscribe {0} of {1} registered subscriptions pointing at this app?\n\nThe display falls back to plain polling until something is subscribed again."),
                    ours.Count, _snapshot.Subscriptions.Count) +
                (_snapshot.Subscriptions.Count > ours.Count
                    ? "\n\n" + L("Andere Eintraege bleiben bestehen.", "Other entries stay untouched.")
                    : ""),
                L("Abos kündigen", "Unsubscribe"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        SetBusy(true, L("Kündige Abos …", "Unsubscribing …"));
        try
        {
            using var client = CreateClient();
            var failed = 0;
            var cancelled = 0;
            foreach (var sub in ours)
            {
                if (string.IsNullOrWhiteSpace(sub.SubscriptionId))
                    continue;
                try
                {
                    await client.UnsubscribeAsync(sub.SubscriptionId, CancellationToken.None);
                    cancelled++;
                    Log(L("Abo gekündigt", "subscription cancelled") + ": " + sub.SubscriptionId + " " + sub.Topic);
                }
                catch (Exception ex)
                {
                    failed++;
                    Log("✖ " + L("Abo", "subscription") + " " + sub.SubscriptionId + ": " + ex.Message);
                }
            }

            // Der Lauscher meldet sich erst ab, wenn die Liste leer laeuft. Behielte er
            // "Abos aktiv" bei, stünde unter der leeren Liste genau das Gegenteil davon – und
            // die Erklaerung, fuer die die Klappe gebaut wurde, waere selbst die Fehlmeldung.
            if (cancelled > 0)
                _ownerMain.ConnectorMonitor.ForgetSubscriptions();

            if (failed > 0)
                ShowWarning(L($"{failed} von {ours.Count} Abos konnten nicht gekündigt werden. Der Konnektor behält sie unter Umständen bis zur Ablaufzeit.",
                              $"{failed} of {ours.Count} subscriptions could not be cancelled. The connector may keep them until their termination time."));
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

        await LoadSnapshotAsync(_ownerMain.GetSettingsSnapshot().Connector, interactive: false);
    }

    private void RenderConnectorInfo(ConnectorSnapshot snapshot)
    {
        ConnectorInfoGrid.Children.Clear();
        var dir = snapshot.Directory;
        var res = snapshot.Resource;

        // Kapseln nebeneinander statt einer langen Zweispalten-Liste: der Konnektor liefert
        // nur wenige Angaben, und die sollen nicht halbleerig untereinander stehen.
        void Row(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("TextMuted")
            });
            panel.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("TextMain"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });
            ConnectorInfoGrid.Children.Add(new Border
            {
                Child = panel,
                Style = (Style)FindResource("ChipStyle")
            });
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

        Row(L("Uhrzeit-Abweichung", "Clock offset"), snapshot.ClockOffset is null && snapshot.Directory is null ? null : ConnectorClock.Describe(snapshot.ClockOffset));
        Row(L("TLS-Zertifikat Konnektor", "Connector TLS certificate"), snapshot.ServerCertificateSubject is null ? null
            : $"{snapshot.ServerCertificateSubject}\n{L("gültig bis", "valid until")} {snapshot.ServerCertificateNotAfter:dd.MM.yyyy}");

        ConnectorInfoCard.Visibility = ConnectorInfoGrid.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Auswahl in den Zwei-Spalten-Ansichten. Nach einem Pollen dieselbe Karte
    /// wieder anzeigen, sonst springt die Maske beim Hintergrundaktualisieren um.</summary>
    private ConnectorCard? _selectedCard;
    private ConnectorCardTerminal? _selectedTerminal;
    private bool _fillingLists;

    /// <summary>
    /// Reihenfolge der Karten: SMC-B zuerst, dann HBA, dann die ubrigen, zum Schluss die eGK.
    /// Absteigend nach Rang, nicht absteigend nach der Zahl – ein OrderDescending auf dieselben
    /// Rangzahlen dreht die Folge vollstaendig um und schiebt die SMC-B ans Ende, also genau
    /// dorthin, wo sie niemand sucht.
    /// </summary>
    private static IEnumerable<ConnectorCard> CardOrder(IEnumerable<ConnectorCard> cards) =>
        cards.OrderBy(c => c.IsSmcB ? 0 : c.IsHba ? 1 : c.IsEgk ? 3 : 2)
             .ThenBy(c => c.CtId, StringComparer.OrdinalIgnoreCase)
             .ThenBy(c => c.SlotId, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Wem ein Kartenzertifikat gehoert: SMC-B und HBA nach Inhaber, die gSMC-KT nach ihrem
    /// Kartenterminal – deren Ablauf legt das Terminal still, nicht eine Person.
    /// </summary>
    private (string TypeLabel, string Title, string Origin) CertificateOwner(ConnectorCard card, IReadOnlyDictionary<string, ConnectorCardTerminal> terminals)
    {
        var ctName = terminals.TryGetValue(card.CtId, out var ct) && !string.IsNullOrWhiteSpace(ct.Name) ? ct.Name : card.CtId;
        if (card.IsSmcKt)
            return ("gSMC-KT", L($"Terminal {ctName}", $"Terminal {ctName}"),
                string.IsNullOrWhiteSpace(card.Iccsn) ? $"Slot {card.SlotId}" : $"ICCSN …{card.Iccsn[^Math.Min(6, card.Iccsn.Length)..]}");
        var title = string.IsNullOrWhiteSpace(card.CardHolderName) ? KindOf(card) : card.CardHolderName;
        return (KindOf(card), title, $"{ctName} · Slot {card.SlotId}");
    }

    private static string KindOf(ConnectorCard card) =>
        card.IsSmcB ? "SMC-B" : card.IsHba ? "HBA" : card.IsEgk ? "eGK"
        : string.IsNullOrWhiteSpace(card.CardType) ? "KARTE" : card.CardType;


    private void RenderCertificates(ConnectorSnapshot snapshot)
    {
        if (CertificatesPanel is null)
            return;
        CertificatesPanel.Children.Clear();
        var now = DateTime.Now;
        var any = false;

        // Kachel: oben Kartenart als Etikett, darunter wem die Karte gehoert und welches
        // Zertifikat gemeint ist. Die grosse Zahl darunter ist die eigentliche Aussage.
        void AddTile(string typeLabel, string title, string subtitle, DateTime notAfter, string glyph = "\uE8C7")
        {
            any = true;
            var daysLeft = ConnectorCertificateExpiry.DaysLeft(notAfter, now);
            var color = ConnectorCertificateExpiry.BrushKeyFor(daysLeft);

            var body = new StackPanel();
            var head = new DockPanel();
            var iconTile = new Border
            {
                Width = 38,
                Height = 38,
                CornerRadius = new CornerRadius(10),
                Background = Brush(color + "Surface"),
                BorderBrush = Brush(color + "Border"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 12, 0),
                Child = new AppIcon
                {
                    Glyph = glyph,
                    Size = 18,
                    Stroke = Brush(color),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            head.Children.Add(iconTile);
            var type = Badge(typeLabel, null, small: true);
            type.VerticalAlignment = VerticalAlignment.Top;
            type.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(type, Dock.Right);
            head.Children.Add(type);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("TextMain"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = title
            });
            if (subtitle.Length > 0)
                names.Children.Add(new TextBlock
                {
                    Text = subtitle,
                    FontSize = 12,
                    Foreground = Brush("TextMuted"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = subtitle
                });
            head.Children.Add(names);
            body.Children.Add(head);

            // Resttage groß – die Zahl selbst ist die Information, die Farbe die Warnstufe.
            var days = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
            days.Children.Add(new TextBlock
            {
                Text = Math.Abs(daysLeft).ToString(CultureInfo.CurrentCulture),
                FontSize = 28,
                FontWeight = FontWeights.SemiBold,
                Foreground = daysLeft <= 30 ? Brush(color) : Brush("TextMain"),
                VerticalAlignment = VerticalAlignment.Bottom
            });
            days.Children.Add(new TextBlock
            {
                Text = daysLeft < 0 ? L("Tage abgelaufen", "days expired") : L("Tage übrig", "days left"),
                FontSize = 12,
                Foreground = Brush("TextMuted"),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(8, 0, 0, 6)
            });
            body.Children.Add(days);

            var pct = Math.Clamp(daysLeft / 365.0, 0, 1);
            var bar = new Grid { Height = 6, Margin = new Thickness(0, 8, 0, 0) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(pct, 0.015), GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - Math.Max(pct, 0.015), GridUnitType.Star) });
            var track = new Border { CornerRadius = new CornerRadius(3), Background = Brush("ChipBackground") };
            Grid.SetColumnSpan(track, 2);
            bar.Children.Add(track);
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = Brush(color) });
            body.Children.Add(bar);

            body.Children.Add(new TextBlock
            {
                Text = L($"gültig bis {notAfter:dd.MM.yyyy}", $"valid until {notAfter:yyyy-MM-dd}"),
                FontSize = 12,
                Foreground = Brush("TextMuted"),
                Margin = new Thickness(0, 8, 0, 0)
            });

            var tile = new Border
            {
                Style = (Style)FindResource("PanelStyle"),
                Padding = new Thickness(18, 16, 18, 16),
                Margin = new Thickness(0, 0, 14, 14),
                Child = body
            };
            CertificatesPanel.Children.Add(tile);
        }

        var terminals = IndexTerminals(snapshot);
        foreach (var card in CardOrder(snapshot.Cards))
        {
            if (!card.HasTrackedCertificate)
                continue;
            var (typeLabel, title, origin) = CertificateOwner(card, terminals);
            foreach (var cert in card.Certificates.Where(c => c.NotAfter != default))
                AddTile(typeLabel, title, $"{cert.CertRef} · {origin}", cert.NotAfter);
            // Ohne gelesene Zertifikate wenigstens das Ablaufdatum aus GetCards zeigen.
            if (card.Certificates.Count == 0 && card.CertificateExpirationDate is { } expiry && expiry != default)
                AddTile(typeLabel, title, $"{L("Kartenzertifikat", "card certificate")} · {origin}", expiry);
        }

        var monitor = _ownerMain.ConnectorMonitor;
        if (monitor.ClientCertificateNotAfter is { } clientNotAfter && clientNotAfter != default)
        {
            var subject = string.IsNullOrWhiteSpace(monitor.ClientCertificateSubject) ? "P12" : monitor.ClientCertificateSubject;
            if (subject.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                subject = subject[3..].Split(',')[0];
            AddTile(L("Clientsystem", "Client system"), subject,
                L("TLS-Zertifikat der App (P12)", "TLS certificate of the app (P12)"), clientNotAfter, "\uEB95");
        }

        if (CertificatesEmpty is not null)
            CertificatesEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderEventsSummary()
    {
        if (EventsPresenceSummary is null)
            return;
        var monitor = _ownerMain.ConnectorMonitor;
        var presence = monitor.LastPresence ?? (_snapshot is not null ? ConnectorPresence.FromSnapshot(_snapshot) : null);
        if (presence is not null)
            EventsPresenceSummary.Text = $"{presence.Headline}  ·  {presence.DetailText}  ·  {presence.AgeText(DateTime.Now)}"
                + (string.IsNullOrWhiteSpace(monitor.LastEventNote) ? "" : "\n" + monitor.LastEventNote);
        else
            EventsPresenceSummary.Text = monitor.LastEventNote
                ?? L("Noch keine Ereignisse empfangen.", "No events received yet.");
    }

    private void RenderCards(ConnectorSnapshot snapshot)
    {
        // IndexTerminals statt ToDictionary: meldet der Konnektor dieselbe CtId zweimal,
        // wirft ein ToDictionary eine Ausnahme aus der Doppelkennung.
        var terminals = IndexTerminals(snapshot);
        var ordered = CardOrder(snapshot.Cards).ToList();

        // Liste aufbauen. Der frueheren Fassung ist der Stapel Karten durch eine Liste
        // ersetzt – die Auswahl bleibt erhalten, der Detailtext steht rechts.
        // Die Auswahl zuweisen, ohne dass der Umschalter zweimal laeuert – beim Pollen
        // wird dieselbe Karte nur einmal neu aufgebaut.
        _fillingLists = true;
        try
        {
            CardsList.Items.Clear();
            foreach (var card in ordered)
                CardsList.Items.Add(new ListBoxItem { Tag = card, Content = CardRow(card, terminals) });

            CardsCountText.Text = ordered.Count == 0
                ? L("keine Karten gefunden", "no cards found")
                : L($"{ordered.Count} gesteckt", $"{ordered.Count} inserted");

            var keep = ordered.FirstOrDefault(c => ReferenceEquals(c, _selectedCard)
                                                  || (!string.IsNullOrEmpty(_selectedCard?.CardHandle)
                                                      && c.CardHandle == _selectedCard!.CardHandle));
            _selectedCard = keep ?? ordered.FirstOrDefault();
            CardsList.SelectedIndex = _selectedCard is null ? -1 : ordered.IndexOf(_selectedCard);
            RenderCardDetail(_selectedCard, terminals);
        }
        finally
        {
            _fillingLists = false;
        }
    }

    /// <summary>Zeile in der Auswahlliste: Symbolkachel mit Zustandspunkt, Name, Art und Herkunft.</summary>
    private UIElement CardRow(ConnectorCard card, IReadOnlyDictionary<string, ConnectorCardTerminal> terminals)
    {
        var worstPin = WorstPinState(card);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var avatar = Avatar("\uE8C7", card.IsSmcB || card.IsHba ? "StatusMaintenance" : null, 40, worstPin is null ? null : StateColorKey(worstPin));
        grid.Children.Add(avatar);

        var body = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(new TextBlock
        {
            Text = card.IsSmcB || card.IsHba ? card.CardHolderName : card.DisplayName,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextMain"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        // Orientation gibt es in WPF wie in WinForms – mit UseWindowsForms laeuft sonst
        // eine Mehrdeutigkeitspruefung, deshalb mit dem WPF-Typnamen qualifizieren.
        var ctLabel = terminals.TryGetValue(card.CtId, out var ct) && !string.IsNullOrWhiteSpace(ct.Name) ? ct.Name : card.CtId;
        var meta = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var kind = Badge(KindOf(card), card.IsSmcB || card.IsHba ? "StatusMaintenance" : null, small: true);
        kind.Margin = new Thickness(0, 0, 7, 0);
        kind.VerticalAlignment = VerticalAlignment.Center;
        meta.Children.Add(kind);
        meta.Children.Add(new TextBlock
        {
            Text = $"{ctLabel} · Slot {card.SlotId}",
            FontSize = 11,
            Foreground = Brush("TextMuted"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        body.Children.Add(meta);

        if (!string.IsNullOrWhiteSpace(worstPin))
        {
            var pinBadge = Badge(worstPin, StateColorKey(worstPin), small: true);
            pinBadge.Margin = new Thickness(0, 6, 0, 0);
            pinBadge.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            body.Children.Add(pinBadge);
        }

        var expiry = card.CertificateExpirationDate
                     ?? card.Certificates.Select(c => (DateTime?)c.NotAfter).DefaultIfEmpty().Min();
        if (expiry is DateTime until)
        {
            var daysLeft = (until - DateTime.Now).TotalDays;
            var warn = _ownerMain.GetSettingsSnapshot().Connector.CertificateWarningDays;
            var color = daysLeft <= 0 ? "StatusOutage" : daysLeft <= warn ? "StatusPartial" : "StatusOk";
            var bar = ProgressBarLine(Math.Clamp(daysLeft / 365.0, 0, 1), color, 4);
            bar.Margin = new Thickness(0, 8, 0, 0);
            bar.ToolTip = L($"Zertifikat gültig bis {until:dd.MM.yyyy}", $"Certificate valid until {until:yyyy-MM-dd}");
            body.Children.Add(bar);
        }

        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    /// <summary>
    /// Runde Symbolkachel. Mit <paramref name="dotKey"/> sitzt unten rechts ein Zustandspunkt –
    /// so steht der Zustand an der Kachel, ohne eine eigene Spalte zu kosten.
    /// </summary>
    private FrameworkElement Avatar(string glyph, string? toneKey, double size, string? dotKey = null)
    {
        var host = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(size * 0.3),
            Background = toneKey is null ? Brush("ChipBackground") : Brush(toneKey + "Surface"),
            BorderBrush = toneKey is null ? Brush("BorderSubtle") : Brush(toneKey + "Border"),
            BorderThickness = new Thickness(1),
            Child = new AppIcon
            {
                Glyph = glyph,
                Size = size * 0.42,
                Stroke = toneKey is null ? Brush("TextMuted") : Brush(toneKey),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
        if (dotKey is not null && dotKey != "TextMuted")
        {
            var dot = size * 0.3;
            host.Children.Add(new Border
            {
                Width = dot,
                Height = dot,
                CornerRadius = new CornerRadius(dot / 2),
                Background = Brush(dotKey),
                BorderBrush = Brush("CardBackground"),
                BorderThickness = new Thickness(2),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -3, -3)
            });
        }
        return host;
    }

    /// <summary>Schmaler Fortschrittsbalken, der sich der verfuegbaren Breite anpasst.</summary>
    private Grid ProgressBarLine(double fraction, string colorKey, double height)
    {
        var f = Math.Clamp(fraction, 0.015, 1);
        var bar = new Grid { Height = height };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(f, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - f + 0.0001, GridUnitType.Star) });
        var track = new Border { CornerRadius = new CornerRadius(height / 2), Background = Brush("ChipBackground") };
        Grid.SetColumnSpan(track, 2);
        bar.Children.Add(track);
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(height / 2), Background = Brush(colorKey) });
        return bar;
    }

    /// <summary>Kopf der Detailansicht: grosse Symbolkachel, Titel, Unterzeile, rechts der Zustand.</summary>
    private UIElement DetailHeader(string glyph, string? toneKey, string title, string? subtitle, IEnumerable<FrameworkElement> badges, FrameworkElement? state)
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        header.Children.Add(Avatar(glyph, toneKey, 56));

        var text = new StackPanel { Margin = new Thickness(16, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var badgeRow = new WrapPanel();
        foreach (var b in badges)
        {
            b.Margin = new Thickness(0, 0, 6, 4);
            badgeRow.Children.Add(b);
        }
        if (badgeRow.Children.Count > 0)
            text.Children.Add(badgeRow);
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextMain"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = title
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
            text.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                Foreground = Brush("TextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });
        Grid.SetColumn(text, 1);
        header.Children.Add(text);

        if (state is not null)
        {
            state.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(state, 2);
            header.Children.Add(state);
        }
        return header;
    }

    /// <summary>Zwischenueberschrift in der Detailansicht.</summary>
    private TextBlock DetailCaption(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brush("TextMuted"),
        Margin = new Thickness(0, 22, 0, 10)
    };

    /// <summary>Eigenschafts-Kachel: Bezeichnung klein, Wert darunter, optional farbig.</summary>
    private Border PropertyTile(string label, string value, string? colorKey = null)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextMuted"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        panel.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 13,
            Foreground = Brush(colorKey ?? "TextMain"),
            FontWeight = colorKey is null ? FontWeights.Normal : FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        });
        return new Border { Child = panel, Style = (Style)FindResource("ChipStyle") };
    }

    /// <summary>Umrandeter Block mit Zeilen und Trennlinien – fuer PIN- und Kartenlisten.</summary>
    private Border RowGroup(IEnumerable<UIElement> rows)
    {
        var stack = new StackPanel();
        var first = true;
        foreach (var row in rows)
        {
            if (!first)
                stack.Children.Add(new Border { Height = 1, Background = Brush("BorderSubtle") });
            first = false;
            stack.Children.Add(new Border { Child = row, Padding = new Thickness(16, 12, 16, 12) });
        }
        return new Border
        {
            Child = stack,
            BorderBrush = Brush("BorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Background = Brush("CardBackground")
        };
    }

    private void CardsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingLists)
            return;
        _selectedCard = CardsList.SelectedItem is ListBoxItem { Tag: ConnectorCard c } ? c : _selectedCard;
        RenderCardDetail(_selectedCard, IndexTerminals(_snapshot));
    }

    private Dictionary<string, ConnectorCardTerminal> IndexTerminals(ConnectorSnapshot? snapshot) =>
        (snapshot ?? new ConnectorSnapshot()).Terminals
            .GroupBy(t => t.CtId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    private void RenderCardDetail(ConnectorCard? card, IReadOnlyDictionary<string, ConnectorCardTerminal> terminals)
    {
        CardDetailPanel.Children.Clear();
        if (card is null)
        {
            CardDetailPanel.Children.Add(new TextBlock
            {
                Text = L("Keine Karte gewaehlt.", "No card selected."),
                Foreground = Brush("TextMuted")
            });
            return;
        }

        var terminalName = terminals.TryGetValue(card.CtId, out var ct) && !string.IsNullOrWhiteSpace(ct.Name) ? ct.Name : card.CtId;
        var worstPin = WorstPinState(card);

        // Kopf: Kachel, Art, Name, rechts der grobe Zustand. Ein Blick, keine Textwand.
        CardDetailPanel.Children.Add(DetailHeader(
            "\uE8C7",
            card.IsSmcB || card.IsHba ? "StatusMaintenance" : null,
            card.CardHolderName.Length > 0 ? card.CardHolderName : card.DisplayName,
            $"{terminalName} · Slot {card.SlotId}",
            [Badge(KindOf(card), card.IsSmcB || card.IsHba ? "StatusMaintenance" : null, small: true)],
            worstPin is null ? null : Badge(worstPin, StateColorKey(worstPin))));

        var props = new List<(string Label, string Value, string? Color)>();
        if (!string.IsNullOrWhiteSpace(card.Iccsn)) props.Add(("ICCSN", card.Iccsn, null));
        if (!string.IsNullOrWhiteSpace(card.CardVersion)) props.Add(("COS", card.CardVersion, null));
        if (card.InsertTime is { } inserted) props.Add((L("Gesteckt seit", "Inserted"), inserted.ToString(L("dd.MM.yyyy HH:mm", "yyyy-MM-dd HH:mm"), LocalizationService.DisplayCulture), null));
        if (card.IsEgk && !string.IsNullOrWhiteSpace(card.Kvnr)) props.Add(("KVNR", card.Kvnr, null));

        var cert = card.AuthCertificate;
        // Alle gelesenen Refs (C.AUT/C.ENC/C.SIG/C.QES) nebeneinander – so faellt ein
        // abgelaufenes QES sofort auf, auch wenn C.AUT noch monatelang gueltig ist.
        var certProps = new List<(string Label, string Value, string? Color)>();
        if (card.Certificates.Count > 0)
        {
            foreach (var c in card.Certificates.OrderBy(c => c.CertRef, StringComparer.Ordinal))
            {
                var days = (int)Math.Floor((c.NotAfter - DateTime.Now).TotalDays);
                var label = ConnectorProtocol.KnownCertRefs.FirstOrDefault(k => k.Ref == c.CertRef).Label;
                var value = L($"bis {c.NotAfter:dd.MM.yyyy} · {days} Tage", $"until {c.NotAfter:yyyy-MM-dd} · {days} days");
                if (!string.IsNullOrWhiteSpace(label) && days <= 0)
                    value += "\n" + label;
                var color = ConnectorCertificateExpiry.BrushKeyFor(days);
                certProps.Add((c.CertRef, value, color == "StatusOk" ? null : color));
            }
        }
        else if (card.EarliestCertificateExpiry is { } notAfter)
        {
            var days = (int)Math.Floor((notAfter - DateTime.Now).TotalDays);
            var color = ConnectorCertificateExpiry.BrushKeyFor(days);
            certProps.Add((L("Zertifikat", "Certificate"),
                L($"bis {notAfter:dd.MM.yyyy} · {days} Tage", $"until {notAfter:yyyy-MM-dd} · {days} days"),
                color == "StatusOk" ? null : color));
        }
        if (!string.IsNullOrWhiteSpace(cert?.TelematikId)) props.Add(("Telematik-ID", cert!.TelematikId, null));
        if (!string.IsNullOrWhiteSpace(cert?.Profession)) props.Add((L("Rolle", "Role"), cert!.Profession, null));
        if (!string.IsNullOrWhiteSpace(cert?.Organization) && cert!.Organization != card.CardHolderName) props.Add((L("Organisation", "Organization"), cert.Organization, null));
        if (cert is not null)
        {
            props.Add((L("Schlüssel", "Key"), $"{cert.CertRef} · {cert.KeyAlgorithm}", null));
            props.Add((L("Aussteller", "Issuer"), cert.Issuer, null));
        }

        if (props.Count > 0)
        {
            CardDetailPanel.Children.Add(DetailCaption(L("Eigenschaften", "Properties")));
            var grid = new WrapPanel { ItemWidth = 232, Margin = new Thickness(0, 0, -10, -10) };
            foreach (var (label, value, color) in props)
                grid.Children.Add(PropertyTile(label, value, color));
            CardDetailPanel.Children.Add(grid);
        }
        if (certProps.Count > 0)
        {
            CardDetailPanel.Children.Add(DetailCaption(L("Zertifikate", "Certificates")));
            var grid = new WrapPanel { ItemWidth = 232, Margin = new Thickness(0, 0, -10, -10) };
            foreach (var (label, value, color) in certProps)
                grid.Children.Add(PropertyTile(label, value, color));
            CardDetailPanel.Children.Add(grid);
        }

        CardDetailPanel.Children.Add(DetailCaption(L("PIN-Zustand", "PIN state")));

        // PIN-Zeilen als Tabellenzeilen in einem Block, damit die Buttons nicht umbiegen.
        if (card.PinTypes.Count > 0)
        {
            CardDetailPanel.Children.Add(RowGroup(card.PinTypes.Select(pinType =>
                BuildPinRow(card, pinType, card.PinStates.FirstOrDefault(p => p.PinType == pinType)))));
        }
        else
            CardDetailPanel.Children.Add(new TextBlock
            {
                Text = L("Diese Karte fuehrt keine PIN-Verwaltung ueber den Konnektor.", "This card has no PIN management via the connector."),
                FontSize = 12,
                Foreground = Brush("TextMuted")
            });

        if (card.SupportsPinManagement)
        {
            CardDetailPanel.Children.Add(DetailCaption(L("Aktionen", "Actions")));
            var actions = new WrapPanel();
            if (cert is not null)
                actions.Children.Add(ActionButton(L("Zertifikat online prüfen (OCSP)", "Verify certificate online (OCSP)"), async () => await VerifyCertificateAsync(card), "\uE9D5"));
            actions.Children.Add(ActionButton(L("Alle Zertifikate lesen", "Read all certificates"), async () => await ReadAllCertificatesAsync(card), "\uEB95"));
            actions.Children.Add(ActionButton(L("PIN-Status neu lesen", "Re-read PIN status"), async () => await RefreshPinStatusAsync(card), "\uE72C"));
            if (card.IsHba)
                actions.Children.Add(ActionButton(L("QES-Probesignatur", "QES test signature"), async () => await QesProbeAsync(card), "\uE70F"));
            CardDetailPanel.Children.Add(actions);
        }

        var note = new DockPanel { Margin = new Thickness(0, 20, 0, 0) };
        note.Children.Add(new AppIcon
        {
            Glyph = "\uE72E",
            Size = 12,
            Stroke = Brush("TextMuted"),
            Margin = new Thickness(0, 1, 8, 0),
            VerticalAlignment = VerticalAlignment.Top
        });
        note.Children.Add(new TextBlock
        {
            Text = L("PIN-Eingaben erfolgen ausschließlich am Kartenterminal – die App sieht keine PIN oder PUK.",
                     "PIN entry happens exclusively at the card terminal – the app never sees a PIN or PUK."),
            FontSize = 11,
            Foreground = Brush("TextMuted"),
            TextWrapping = TextWrapping.Wrap
        });
        CardDetailPanel.Children.Add(note);
    }

    /// <summary>Farbe der Linkskante, passend zum grobsten PIN-Zustand der Karte.</summary>
    /// <remarks>
    /// Die Zuordnung vergleicht die vollen Meldungen, nicht Teilworter. "nicht verifiziert"
    /// enthaelt das Wortende von "verifiziert" und waere bei einer Teilpruefung rosa bis gruen,
    /// obwohl die Karte gerade nicht einsatzfaehig ist. Dasselbe Muster gilt fuer das Abzeichen.
    /// </remarks>
    public static string StateColorKey(string? worstPin)
        => worstPin switch
        {
            null => "TextMuted",
            "gesperrt" or "blocked" => "StatusOutage",
            "verifiziert" or "verified" => "StatusOk",
            _ => "StatusPartial"
        };

    private static System.Windows.Media.Brush AccentForState(string? worstPin)
        => BrushStatic(StateColorKey(worstPin));

    private static System.Windows.Media.Brush BrushStatic(string key)
        => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources[key];

    /// <summary>Kuerzester brauchbare PIN-Gesamtzustand fuer das Abzeichen der Karte.</summary>
    private static string? WorstPinState(ConnectorCard card)
    {
        if (card.PinStates.Count == 0)
            return null;
        if (card.PinStates.Any(p => p.IsBlocked))
            return L("gesperrt", "blocked");
        if (card.PinStates.Any(p => p.Error is not null))
            return L("Pin-Status unklar", "PIN status unclear");
        if (card.PinStates.Any(p => p.IsTransportPin))
            return L("Transport-PIN", "transport PIN");
        if (card.PinStates.All(p => p.IsVerified))
            return L("verifiziert", "verified");
        return L("nicht verifiziert", "not verified");
    }

    /// <summary>
    /// Statusabzeichen: rechteckig mit kleinem Radius, getoente Flaeche ohne Rand, davor ein
    /// Zustandspunkt. Neutrale Abzeichen (Kartenart, Version) kommen ohne Punkt aus und
    /// lesen sich wie ein Etikett, nicht wie ein Knopf.
    /// </summary>
    private Border Badge(string text, string? colorKey, bool small = false)
    {
        // TextMuted hat keine Flaechen-/Randvariante – wie "neutral" behandeln.
        if (colorKey == "TextMuted")
            colorKey = null;
        var content = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        if (colorKey is not null)
        {
            var dot = small ? 6.0 : 7.0;
            content.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = dot,
                Height = dot,
                Fill = Brush(colorKey),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, small ? 5 : 7, 0)
            });
        }
        content.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = small ? 11 : 12,
            FontWeight = FontWeights.Medium,
            Foreground = Brush(colorKey is null ? "TextMuted" : "TextMain"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        return new Border
        {
            Child = content,
            Background = colorKey is null ? Brush("ChipBackground") : Brush(colorKey + "Surface"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(small ? 4 : 6),
            Padding = small ? new Thickness(6, 2, 7, 2) : new Thickness(9, 4, 10, 4),
            SnapsToDevicePixels = true
        };
    }

    private UIElement BuildPinRow(ConnectorCard card, string pinType, ConnectorPinState? state)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        string statusText;
        string color;
        if (state is null)
        {
            statusText = L("unbekannt", "unknown");
            color = "TextMuted";
        }
        else if (state.Error is not null)
        {
            // Der Konnektor-Fehler steht nicht mehr als XML-Block in der Maske, sondern
            // nur als Klapptext. Der eigentliche Request landet im Protokollreiter.
            statusText = L("Status nicht abrufbar", "status unavailable");
            color = "StatusOutage";
        }
        else
        {
            statusText = DescribePinStatus(state.Status);
            if (state.LeftTries is { } tries && !state.IsVerified)
                statusText += L($" · {tries} Versuche übrig", $" · {tries} tries left");
            color = state.IsVerified ? "StatusOk" : state.IsBlocked ? "StatusOutage" : "StatusPartial";
        }

        var name = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new AppIcon
        {
            Glyph = "\uE8D7",
            Size = 13,
            Stroke = Brush("TextMuted"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        name.Children.Add(new TextBlock
        {
            Text = pinType,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextMain"),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(name, 0);
        row.Children.Add(name);

        var status = Badge(statusText, color);
        status.VerticalAlignment = VerticalAlignment.Center;
        status.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        if (state?.Error is { Length: > 0 } error)
        {
            // Der lange Konnektor-Text – inklusive XML – gehoert nicht in die Maske. Er bleibt
            // als Klapptext und im Protokollreiter erhalten, damit die Karte lesbar bleibt.
            status.ToolTip = error;
            status.Cursor = System.Windows.Input.Cursors.Help;
        }
        Grid.SetColumn(status, 1);
        row.Children.Add(status);

        var blocked = state?.IsBlocked == true;
        var transport = state?.IsTransportPin == true;

        // Im Objekth initializer faellt der Name rechts auf die Instanz-Eigenschaft zurueck – mit
        // Typnamen qualifizieren, sonst greift CS0176.
        var buttons = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        // PIN-Verwaltung veraendert die Karte – nur, wenn sie in den Einstellungen ausdruecklich
        // erlaubt ist (Standard: aus). Sonst ein kurzer Hinweis statt der Knoepfe.
        if (!_ownerMain.GetSettingsSnapshot().Connector.AllowPinManagement)
        {
            buttons.Children.Add(new TextBlock
            {
                Text = L("PIN-Verwaltung in den Einstellungen deaktiviert", "PIN management disabled in settings"),
                FontSize = 11,
                Foreground = Brush("TextMuted"),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                ToolTip = L("Einstellungen → Konnektor → „PIN-Verwaltung erlauben“. Der TI-Lage Monitor liest sonst nur.",
                            "Settings → Connector → “Allow PIN management”. Otherwise TI-Lage Monitor only reads.")
            });
            Grid.SetColumn(buttons, 3);
            row.Children.Add(buttons);
            return row;
        }
        if (!blocked && !transport && state?.IsVerified != true)
            buttons.Children.Add(ActionButton(L("PIN verifizieren", "Verify PIN"), async () => await PinActionAsync(card, pinType, "VerifyPin")));
        if (!blocked)
            buttons.Children.Add(ActionButton(transport ? L("Transport-PIN ändern", "Change transport PIN") : L("PIN ändern", "Change PIN"),
                async () => await PinActionAsync(card, pinType, "ChangePin")));
        if (blocked || state?.LeftTries is <= 1)
            buttons.Children.Add(ActionButton(L("Mit PUK entsperren", "Unblock with PUK"), async () => await PinActionAsync(card, pinType, "UnblockPin")));

        // An-/Abschalten ist von den übrigen Knöpfen zu trennen: DisablePin setzt die PIN nicht neu,
        // sondern nimmt ihre Prüfung ganz. Wer das versehentlich klickt, entsperrt die Karte,
        // ohne dass überhaupt noch eine PIN-Abfrage kommt.
        // EnablePin/DisablePin existieren erst ab CardService 8.2 (OPB5).
        if (!blocked && !transport && state is not null && _snapshot?.Directory?.SupportsEnableDisablePin == true)
        {
            if (state.IsDisabled)
                buttons.Children.Add(ActionButton(L("PIN aktivieren", "Enable PIN"), async () => await PinActionAsync(card, pinType, "EnablePin")));
            else
                buttons.Children.Add(ActionButton(L("PIN deaktivieren", "Disable PIN"), async () => await PinActionAsync(card, pinType, "DisablePin")));
        }
        Grid.SetColumn(buttons, 3);
        row.Children.Add(buttons);

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
        var ordered = snapshot.Terminals.OrderBy(t => t.CtId, StringComparer.OrdinalIgnoreCase).ToList();

        _fillingLists = true;
        try
        {
            TerminalsList.Items.Clear();
            foreach (var t in ordered)
                TerminalsList.Items.Add(new ListBoxItem { Tag = t, Content = TerminalRow(t, snapshot) });

            TerminalsCountText.Text = ordered.Count == 0
                ? L("keine Terminals gefunden", "no terminals found")
                : L($"{ordered.Count} gemeldet", $"{ordered.Count} reported");

            var keep = ordered.FirstOrDefault(t => ReferenceEquals(t, _selectedTerminal)
                                                  || (!string.IsNullOrEmpty(_selectedTerminal?.CtId)
                                                      && t.CtId == _selectedTerminal!.CtId));
            _selectedTerminal = keep ?? ordered.FirstOrDefault();
            TerminalsList.SelectedIndex = _selectedTerminal is null ? -1 : ordered.IndexOf(_selectedTerminal);
            RenderTerminalDetail(_selectedTerminal, snapshot);
        }
        finally
        {
            _fillingLists = false;
        }
    }

    /// <summary>Zeile in der Auswahlliste: Symbolkachel in Verbindungsfarbe, Name, Herkunft.</summary>
    private UIElement TerminalRow(ConnectorCardTerminal t, ConnectorSnapshot snapshot)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var avatar = Avatar("\uE7F4", null, 40, TerminalStateKey(t));
        avatar.ToolTip = t.Connected == false ? L("offline", "offline") : t.Connected == true ? L("online", "online") : L("unbekannt", "unknown");
        grid.Children.Add(avatar);

        var body = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(t.Name) ? t.CtId : t.Name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextMain"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var under = new List<string> { t.CtId };
        if (!string.IsNullOrWhiteSpace(t.IpAddress)) under.Add(t.IpAddress);
        body.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", under),
            FontSize = 11,
            Foreground = Brush("TextMuted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0)
        });
        var here = snapshot.Cards.Count(c => string.Equals(c.CtId, t.CtId, StringComparison.OrdinalIgnoreCase));
        var count = Badge(L($"{here} Karten", $"{here} cards"), here > 0 ? "StatusMaintenance" : null, small: true);
        count.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        count.Margin = new Thickness(0, 6, 0, 0);
        body.Children.Add(count);

        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    private static string TerminalStateKey(ConnectorCardTerminal t) =>
        t.Connected == false ? "StatusOutage" : t.Connected == true ? "StatusOk" : "StatusEmpty";

    private void TerminalsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingLists)
            return;
        _selectedTerminal = TerminalsList.SelectedItem is ListBoxItem { Tag: ConnectorCardTerminal t } ? t : _selectedTerminal;
        RenderTerminalDetail(_selectedTerminal, _snapshot);
    }

    private void RenderTerminalDetail(ConnectorCardTerminal? t, ConnectorSnapshot? snapshot)
    {
        TerminalDetailPanel.Children.Clear();
        if (t is null)
        {
            TerminalDetailPanel.Children.Add(new TextBlock
            {
                Text = L("Kein Terminal gewaehlt.", "No terminal selected."),
                Foreground = Brush("TextMuted")
            });
            return;
        }
        var cards = (snapshot?.Cards ?? new List<ConnectorCard>())
            .Where(c => string.Equals(c.CtId, t.CtId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var state = t.Connected switch
        {
            true => L("verbunden", "connected"),
            false => L("NICHT verbunden", "NOT connected"),
            null => L("Status unbekannt", "status unknown")
        };

        TerminalDetailPanel.Children.Add(DetailHeader(
            "\uE7F4",
            t.Connected == false ? "StatusOutage" : t.Connected == true ? "StatusOk" : null,
            t.Label,
            t.CtId,
            [],
            Badge(state, t.Connected == false ? "StatusPartial" : "StatusOk")));

        // Technische Werte als Kacheln – dieselbe Handschrift wie bei der Karte.
        var props = new List<(string Label, string Value)>();
        if (!string.IsNullOrWhiteSpace(t.IpAddress)) props.Add(("IP", t.IpAddress));
        if (!string.IsNullOrWhiteSpace(t.MacAddress)) props.Add(("MAC", t.MacAddress));
        if (t.Slots is { } slots) props.Add(("Slots", slots.ToString(CultureInfo.CurrentCulture)));
        if (t.IsPhysical is { } physical) props.Add((L("Bauart", "Type"), physical ? L("physisch", "physical") : L("virtuell", "virtual")));
        if (!string.IsNullOrWhiteSpace(t.Details)) props.Add((L("Details", "Details"), t.Details));
        if (t.Workplaces.Count > 0)
            props.Add((L("Arbeitsplätze", "Workplaces"), string.Join(", ", t.Workplaces)));

        if (props.Count > 0)
        {
            TerminalDetailPanel.Children.Add(DetailCaption(L("Eigenschaften", "Properties")));
            var grid = new WrapPanel { ItemWidth = 232, Margin = new Thickness(0, 0, -10, -10) };
            foreach (var (label, value) in props)
                grid.Children.Add(PropertyTile(label, value));
            TerminalDetailPanel.Children.Add(grid);
        }

        TerminalDetailPanel.Children.Add(DetailCaption(L("Gesteckte Karten", "Inserted cards")));

        if (cards.Count == 0)
        {
            TerminalDetailPanel.Children.Add(new Border
            {
                Background = Brush("ChipBackground"),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16, 14, 16, 14),
                Child = new TextBlock
                {
                    Text = L("In diesem Terminal steckt keine Karte.", "No card is inserted in this terminal."),
                    FontSize = 12,
                    Foreground = Brush("TextMuted")
                }
            });
            return;
        }

        var rows = new List<UIElement>();
        foreach (var card in CardOrder(cards))
        {
            var worstPin = WorstPinState(card);
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(Avatar("\uE8C7", card.IsSmcB || card.IsHba ? "StatusMaintenance" : null, 32));

            var text = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = card.DisplayName,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("TextMain"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            text.Children.Add(new TextBlock
            {
                Text = $"{KindOf(card)} · Slot {card.SlotId}",
                FontSize = 11,
                Foreground = Brush("TextMuted"),
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            if (worstPin is not null)
            {
                var pinBadge = Badge(worstPin, StateColorKey(worstPin));
                pinBadge.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(pinBadge, 2);
                row.Children.Add(pinBadge);
            }
            rows.Add(row);
        }
        TerminalDetailPanel.Children.Add(RowGroup(rows));
    }

    private void RenderServices(ConnectorSnapshot snapshot)
    {
        ServicesPanel.Children.Clear();
        var services = snapshot.Directory?.Services ?? [];
        var odd = false;
        foreach (var group in services.GroupBy(s => s.Name).OrderBy(g => g.Key))
        {
            // Zwei Spalten: links Name und Version, rechts die Endpunkte – als Tabellenzeile
            // mit wechselndem Hintergrund, damit lange Listen lesbar bleiben.
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var head = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            head.Children.Add(new TextBlock
            {
                Text = group.Key,
                Foreground = Brush("TextMain"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var versions = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var version in group.Select(s => s.Version).Distinct())
            {
                var v = Badge("v" + version, null, small: true);
                v.Margin = new Thickness(0, 0, 4, 2);
                versions.Children.Add(v);
            }
            head.Children.Add(versions);
            Grid.SetColumn(head, 0);
            row.Children.Add(head);

            var paths = new TextBlock
            {
                Text = string.Join("\n", group.Select(s => s.Endpoint).Distinct()),
                Foreground = Brush("TextMuted"),
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(paths, 1);
            row.Children.Add(paths);

            ServicesPanel.Children.Add(new Border
            {
                Child = row,
                Background = odd ? Brush("ChipBackground") : System.Windows.Media.Brushes.Transparent,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10, 14, 10)
            });
            odd = !odd;
        }
        ServicesCard.Visibility = services.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private System.Windows.Controls.Button ActionButton(string text, Func<Task> action, string? glyph = null)
    {
        object content = text;
        if (glyph is not null)
        {
            var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            panel.Children.Add(new AppIcon
            {
                Glyph = glyph,
                Size = 13,
                Stroke = Brush("ButtonForeground"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            content = panel;
        }
        var button = new System.Windows.Controls.Button
        {
            Content = content,
            Padding = glyph is null ? new Thickness(12, 6, 12, 6) : new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 2, 8, 6),
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

    /// <summary>
    /// Dieselbe Karte im aktuell gezeichneten Datenstand. Die Knopfe halten die Karte mit, die
    /// beim Aufbau der Maske herauskam; seit der Hintergrundpollen nachzeichnet, koennen die
    /// langst vertauscht sein. Ueber die CardHandle bleibt die Aktion trotzdem richtig, nur
    /// wuerde eine nachgetragene Angabe auf dem verworfenen Objekt stehen – dort, wo niemand
    /// mehr hinlookt. Deshalb wird vor jeder Rueckwirkung auf die gerade sichtbare Karte
    /// umgebogen.
    /// </summary>
    private ConnectorCard LiveCard(ConnectorCard card)
    {
        if (_snapshot is null || string.IsNullOrEmpty(card.CardHandle))
            return card;
        return _snapshot.Cards.FirstOrDefault(c => c.CardHandle == card.CardHandle) ?? card;
    }

    private async Task PinActionAsync(ConnectorCard card, string pinType, string operation)
    {
        var terminal = _snapshot?.Terminals.FirstOrDefault(t => string.Equals(t.CtId, card.CtId, StringComparison.OrdinalIgnoreCase));
        var where = terminal is null || string.IsNullOrWhiteSpace(terminal.Name) ? card.CtId : terminal.Name;
        bool? setNewPin = null;

        if (!_ownerMain.GetSettingsSnapshot().Connector.AllowPinManagement)
        {
            ShowWarning(L("PIN-Verwaltung ist in den Einstellungen deaktiviert. Einschalten unter Einstellungen → Konnektor → „PIN-Verwaltung erlauben“ – alternativ die SMC-B im Primärsystem freischalten.",
                          "PIN management is disabled in settings. Enable it under Settings → Connector → “Allow PIN management” – or unlock the SMC-B via the practice software."));
            return;
        }

        if ((operation is "EnablePin" or "DisablePin") && _snapshot?.Directory?.SupportsEnableDisablePin != true)
        {
            ShowWarning(L("Aktivieren und Deaktivieren der PIN unterstützt dieser Konnektor nicht (CardService unter 8.2).",
                          "Enabling and disabling the PIN is not supported by this connector (CardService below 8.2)."));
            return;
        }

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
            case "DisablePin":
                if (System.Windows.MessageBox.Show(this,
                        L($"{pinType} von {card.DisplayName} deaktivieren?\n\n" +
                          "⚠ Die PIN wird damit nicht gelöscht, sondern nur nicht mehr abgefragt.\n" +
                          "Zugriffe auf die Karte laufen anschließend ohne PIN-Abfrage – je nach Anwendung ohne Absicherung.\n\n" +
                          "Mit „PIN aktivieren“ lässt sich der Zustand zurückholen.",
                          $"Disable {pinType} of {card.DisplayName}?\n\n" +
                          "⚠ This does not delete the PIN, it only stops asking for it.\n" +
                          "Card access then proceeds without a PIN prompt – unprotected, depending on the application.\n\n" +
                          "Enable PIN restores the previous state."),
                        L("PIN deaktivieren", "Disable PIN"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
                prompt = L($"Bitte nach dem Bestätigen die {pinType} am Kartenterminal „{where}“ eingeben.",
                           $"After confirming, enter {pinType} on card terminal \"{where}\".");
                break;
            case "EnablePin":
                prompt = L($"{pinType} von {card.DisplayName} wird wieder eingeschaltet.\n\nBitte nach dem Bestätigen die {pinType} am Kartenterminal „{where}“ eingeben.",
                           $"{pinType} of {card.DisplayName} is switched back on.\n\nAfter confirming, enter {pinType} on card terminal \"{where}\".");
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
        card = LiveCard(card);
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
        PublishToMonitor();
    }

    /// <summary>
    /// Uebertraegt den gezeichneten Stand an die Hintergrundueberwachung, damit Knopf, Tray
    /// und Alarme dasselbe Bild haben. Laengst ueberholt? Dann bleibt der exactly-neuere
    /// Stand stehen: ein Accept des eigenen aelteren Datenstandes drehte die frischeren
    /// Anzeigen zurueck und schrieb denselben ueber den Konnektor-Verlauf.
    /// </summary>
    private void PublishToMonitor()
    {
        if (_snapshot is null)
            return;
        var current = _ownerMain.ConnectorMonitor.LastSnapshot;
        if (current is null || current.RetrievedAt <= _snapshot.RetrievedAt)
            _ownerMain.ConnectorMonitor.Accept(_snapshot);
    }

    /// <summary>
    /// Liest alle vier nach CertRefEnum moeglichen Zertifikate und traegt sie in die Karte ein.
    /// Refs, die die Karte nicht fuehrt, bleiben leer – das ist kein Fehler.
    /// </summary>
    private async Task ReadAllCertificatesAsync(ConnectorCard card)
    {
        if (_snapshot is null)
            return;
        card = LiveCard(card);
        SetBusy(true, L("Lese Kartenzertifikate …", "Reading card certificates …"));
        try
        {
            using var client = CreateClient();
            var certificates = await client.ReadAllCertificatesAsync(card.CardHandle, CancellationToken.None);
            card.Certificates = certificates;
            card.AuthCertificate = certificates.FirstOrDefault(c => c.RefEquals("C.AUT")) ?? card.AuthCertificate;
            Log($"{card.DisplayName}: {certificates.Count} Zertifikat(e) – {string.Join(", ", certificates.Select(c => c.CertRef))}");
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

        Render(_snapshot);
        PublishToMonitor();
    }

    private async Task VerifyCertificateAsync(ConnectorCard card)
    {
        card = LiveCard(card);
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
    // SMC-B-Erinnerung (Toast), QES-Probesignatur, Kalenderexport
    // =================================================================

    /// <summary>
    /// Ziel des Toasts "SMC-B freischalten": Karten-Ansicht mit der SMC-B, auf Wunsch gleich
    /// die Freischaltung. Wartet, bis das Fenster geladen ist und der erste Abruf steht – sonst
    /// liefe die PIN-Abfrage gegen einen veralteten Stand oder parallel zum Abruf.
    /// </summary>
    public async Task ShowSmcbAsync(string? cardId, bool unlock)
    {
        try
        {
            if (!IsLoaded)
            {
                var loaded = new TaskCompletionSource();
                void OnLoaded(object? s, RoutedEventArgs e) { Loaded -= OnLoaded; loaded.TrySetResult(); }
                Loaded += OnLoaded;
                await loaded.Task;
            }
            await Task.Yield();
            Nav.SelectedItem = NavCards;

            for (var waited = 0; (_busy || _snapshot is null) && waited < 60_000; waited += 250)
                await Task.Delay(250);
            if (_snapshot is null || !IsVisible)
                return;

            var smcbs = _snapshot.Cards.Where(c => c.IsSmcB).ToList();
            var card = smcbs.FirstOrDefault(c => cardId is not null && string.Equals(ConnectorSmcbReminder.CardId(c), cardId, StringComparison.OrdinalIgnoreCase))
                       ?? smcbs.FirstOrDefault(c => ConnectorSmcbReminder.Evaluate(new ConnectorSnapshot { Cards = [c] }).State == ConnectorSmcbReminder.State.NotVerified)
                       ?? smcbs.FirstOrDefault();
            if (card is null)
            {
                ShowWarning(L("Der Konnektor meldet derzeit keine SMC-B.", "The connector currently reports no SMC-B."));
                return;
            }

            _selectedCard = card;
            RenderCards(_snapshot);
            if (!unlock)
                return;

            var pinType = card.PinTypes.FirstOrDefault(p => string.Equals(p, "PIN.SMC", StringComparison.OrdinalIgnoreCase)) ?? "PIN.SMC";
            var state = card.PinStates.FirstOrDefault(p => string.Equals(p.PinType, pinType, StringComparison.OrdinalIgnoreCase));
            if (state?.IsVerified == true)
            {
                System.Windows.MessageBox.Show(this, L($"{card.DisplayName} ist bereits freigeschaltet.", $"{card.DisplayName} is already unlocked."),
                    L("SMC-B", "SMC-B"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            await PinActionAsync(card, pinType, "VerifyPin");
        }
        catch (Exception ex)
        {
            App.LogUnhandled("ConnectorSmcbReminder", ex);
            ShowWarning(DescribeException(ex));
        }
    }

    /// <summary>
    /// Probesignatur mit dem HBA. Erzeugt eine echte QES ueber einen Testtext – deshalb vorher
    /// ausdruecklich bestaetigen lassen. Die PIN.QES wird am Kartenterminal eingegeben.
    /// </summary>
    private async Task QesProbeAsync(ConnectorCard card)
    {
        card = LiveCard(card);
        var terminal = _snapshot?.Terminals.FirstOrDefault(t => string.Equals(t.CtId, card.CtId, StringComparison.OrdinalIgnoreCase));
        var where = terminal is null || string.IsNullOrWhiteSpace(terminal.Name) ? card.CtId : terminal.Name;

        if (System.Windows.MessageBox.Show(this,
                L($"QES-Probesignatur mit {card.DisplayName}.\n\n" +
                  "Der Konnektor signiert einen kurzen Testtext qualifiziert (CAdES). Das prüft HBA, Signaturzertifikat, PIN.QES und Signaturdienst in einem Durchgang.\n\n" +
                  $"⚠ Nach dem Bestätigen die PIN.QES am Kartenterminal „{where}“ eingeben. Die Signatur ist echt, betrifft aber nur den Testtext und wird nicht gespeichert.\n\nFortfahren?",
                  $"QES test signature with {card.DisplayName}.\n\n" +
                  "The connector creates a qualified signature (CAdES) over a short test text. This checks HBA, signature certificate, PIN.QES and signature service in one go.\n\n" +
                  $"⚠ After confirming, enter PIN.QES on card terminal \"{where}\". The signature is real but only covers the test text and is not stored.\n\nContinue?"),
                L("QES-Probesignatur", "QES test signature"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        SetBusy(true, L($"Warte auf PIN.QES am Kartenterminal „{where}“ …", $"Waiting for PIN.QES on card terminal \"{where}\" …"));
        try
        {
            using var client = CreateClient();
            var result = await client.QesTestSignatureAsync(card.CardHandle, CancellationToken.None);
            Log($"QES-Probesignatur {card.DisplayName}: {result.Result}" + (result.Ok ? $" ({result.SignatureBytes} Byte)" : $" {result.ErrorCode} {result.ErrorText}"));
            var text = result.Ok
                ? L($"Die Probesignatur ist gelungen ✓\n\n{card.DisplayName} kann qualifiziert signieren (Signatur {result.SignatureBytes} Byte).",
                    $"The test signature succeeded ✓\n\n{card.DisplayName} can create qualified signatures (signature {result.SignatureBytes} bytes).")
                : L($"Die Probesignatur ist fehlgeschlagen.\n\nErgebnis: {result.Result}" + (result.ErrorText is null ? "" : $"\n{result.ErrorText}"),
                    $"The test signature failed.\n\nResult: {result.Result}" + (result.ErrorText is null ? "" : $"\n{result.ErrorText}"));
            System.Windows.MessageBox.Show(this, text, L("QES-Probesignatur", "QES test signature"), MessageBoxButton.OK,
                result.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
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

    /// <summary>Alle bekannten Zertifikatsablaeufe – dieselbe Auswahl wie im Bereich Zertifikate.</summary>
    internal static List<ConnectorCalendarEntry> CalendarEntries(ConnectorSnapshot snapshot, string? clientSubject, DateTime? clientNotAfter)
    {
        var list = new List<ConnectorCalendarEntry>();
        foreach (var card in CardOrder(snapshot.Cards))
        {
            // Versichertenkarten gehoeren nicht in einen Praxiskalender.
            if (card.IsEgk)
                continue;
            var owner = card.CardHolderName.Length > 0 ? $"{KindOf(card)} {card.CardHolderName}" : card.DisplayName;
            foreach (var cert in card.Certificates.Where(c => c.NotAfter != default))
                list.Add(new ConnectorCalendarEntry(cert.CertRef, owner, cert.NotAfter));
            if (card.Certificates.Count == 0 && card.CertificateExpirationDate is { } expiry && expiry != default)
                list.Add(new ConnectorCalendarEntry(L("Kartenzertifikat", "Card certificate"), owner, expiry));
        }
        if (clientNotAfter is { } notAfter && notAfter != default)
            list.Add(new ConnectorCalendarEntry(L("Clientsystem-Zertifikat", "Client system certificate"),
                string.IsNullOrWhiteSpace(clientSubject) ? "P12" : clientSubject, notAfter));
        return list;
    }

    private void ExportCalendar_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null)
        {
            ShowWarning(L("Noch keine Konnektor-Daten vorhanden.", "No connector data available yet."));
            return;
        }

        var monitor = _ownerMain.ConnectorMonitor;
        var entries = CalendarEntries(_snapshot, monitor.ClientCertificateSubject, monitor.ClientCertificateNotAfter);
        if (entries.Count == 0)
        {
            ShowWarning(L("Es sind keine Ablaufdaten bekannt. Karte wählen und „Alle Zertifikate lesen“ verwenden.",
                          "No expiry dates are known. Select a card and use “Read all certificates”."));
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = L("Ablaufkalender speichern", "Save expiry calendar"),
            Filter = L("Kalender (*.ics)|*.ics", "Calendar (*.ics)|*.ics"),
            FileName = L("TI-Zertifikate.ics", "TI-certificates.ics"),
            AddExtension = true,
            DefaultExt = ".ics"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, ConnectorCalendarExport.Build(entries, DateTime.UtcNow), new UTF8Encoding(false));
            FooterText.Text = L($"{entries.Count} Ablauftermine exportiert – Datei in Outlook bzw. den Kalender importieren.",
                                $"{entries.Count} expiry dates exported – import the file into Outlook or your calendar.");
            Log($"Ablaufkalender: {entries.Count} Termine → {dialog.FileName}");
        }
        catch (Exception ex)
        {
            ShowWarning(ex.Message);
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
            System.Windows.Clipboard.SetText(BuildReport(_snapshot, _ownerMain.GetSettingsSnapshot().Connector, _ownerMain.ConnectorMonitor));
            FooterText.Text = L("Bericht in die Zwischenablage kopiert.", "Report copied to clipboard.");
        }
        catch (Exception ex)
        {
            ShowWarning(ex.Message);
        }
    }

    /// <summary>
    /// Diagnosebericht ohne Geheimnisse (keine Passwörter, kein Zertifikatsinhalt).
    /// Der uebergebenen Monitor steht fuer die Ereignisangaben offen, weil der Bericht static
    /// ist und sich der Stand nicht selbst besorgen darf.
    /// </summary>
    public static string BuildReport(ConnectorSnapshot s, ConnectorSettings settings, ConnectorMonitor? monitor = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"TI-Lage Monitor {UpdateService.CurrentVersion} – Konnektor-Bericht {s.RetrievedAt:dd.MM.yyyy HH:mm:ss}");
        sb.AppendLine($"Adresse: {settings.Host}  Mandant: {settings.MandantId}  Clientsystem: {settings.ClientSystemId}  Arbeitsplatz: {settings.WorkplaceId}");
        if (s.Directory is { } d)
        {
            sb.AppendLine($"Konnektor: {d.ProductVendorName} {d.ProductName} ({d.ProductCode}), PTV {d.ProductTypeVersion}, FW {d.FirmwareVersion}, HW {d.HardwareVersion}");
            sb.AppendLine("Dienste: " + string.Join(", ", d.Services.Select(x => $"{x.Name} {x.Version}")));
        }
        sb.AppendLine("Uhrzeit: " + ConnectorClock.Describe(s.ClockOffset));
        if (s.Resource is { } r)
        {
            sb.AppendLine($"VPN TI: {r.VpnTiStatus} ({r.VpnTiTimestamp:g})  VPN SIS: {r.VpnSisStatus}");
            foreach (var e in r.ActiveErrors)
                sb.AppendLine($"Fehlerzustand: {e.Condition} {e.Severity} {e.Type} seit {e.ValidFrom:g}");
        }
        sb.AppendLine();
        sb.AppendLine("Kartenterminals:");
        foreach (var t in s.Terminals)
        {
            sb.Append($"  {t.CtId} {t.Name} IP {t.IpAddress} MAC {t.MacAddress} verbunden={t.Connected}");
            if (!string.IsNullOrWhiteSpace(t.Details))
                sb.Append($"  [{t.Details}]");
            if (t.Workplaces.Count > 0)
                sb.Append($"  AP {string.Join(",", t.Workplaces)}");
            sb.AppendLine();
        }
        sb.AppendLine("Karten:");
        foreach (var c in s.Cards)
        {
            // Versichertendaten (eGK/KVK) gehoeren nicht in einen Bericht, der per Mail an den
            // Support geht: Name weg, ICCSN gekuerzt.
            sb.Append(c.IsEgk || c.CardType.Equals("KVK", StringComparison.OrdinalIgnoreCase)
                ? $"  {c.CardType} [Versicherte/r] ICCSN {ConnectorPrivacy.MaskIccsn(c.Iccsn)} @ {c.CtId}/{c.SlotId}"
                : $"  {c.CardType} {c.CardHolderName} ICCSN {c.Iccsn} @ {c.CtId}/{c.SlotId}");
            if (c.Certificates.Count > 0)
                sb.Append(" [").Append(string.Join(", ", c.Certificates.Select(c => $"{c.CertRef} bis {c.NotAfter:dd.MM.yyyy}"))).Append(']');
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
            if (s.Warnings.Any(w => w.Contains("Syntaxfehler", StringComparison.OrdinalIgnoreCase) || w.Contains("Syntax error", StringComparison.OrdinalIgnoreCase) || w.Contains("4000", StringComparison.Ordinal)))
                sb.AppendLine("  Hinweis: 4000 heisst nur, dass der Konnektor die Nachricht ablehnt – nicht womit. " +
                              "Der Aufrufkontext ist bei allen Diensten gleich: antworten EventService oder " +
                              "CertificateService mit denselben IDs, ist der Kontext als Ursache ausgeschlossen. " +
                              "Bleibt als Kandidat der Inhalt der betroffenen Operation, zum Beispiel der Namespace " +
                              "eines einzelnen Parameters. Bei 4003 fehlt eine zwingende Angabe – fuer " +
                              "Kartensitzungen des HBA ist das regelmaessig die Benutzer-ID im Aufrufkontext.");
        }
        if (s.Subscriptions.Count > 0)
        {
            // Dieselbe Unterscheidung wie im Fenster: der Bericht laeuft oft auf einem
            // Konnektor mit andern Anwendern, und eine Liste ohne Herr ist dort die
            // Erklaerung, die am haeufigsten in die Irre fuehrt. Der Lauscher ist hier
            // moeglicherwaise nicht dabei – ohne ihn faellt die Ordnung auf die reine
            // Aufzuehlung zurueck, statt sie zu raten.
            if (monitor is not null)
                sb.AppendLine($"Ereignis-Abos: {s.Subscriptions.Count} eingetragen, {s.Subscriptions.Count(sub => monitor.IsOwnSubscription(sub))} von dieser App"
                              + (monitor.SubscriptionsAlive ? ", stehen noch" : ", laut Konnektor nicht mehr aktiv") + ".");
            else
                sb.AppendLine($"Ereignis-Abos: {s.Subscriptions.Count} eingetragen – wem sie hoeren, steht nicht dabei, der Bericht lief ohne Lauscher.");
            foreach (var sub in s.Subscriptions)
                sb.AppendLine($"  {(monitor?.IsOwnSubscription(sub) == true ? "*" : " ")} {sub.SubscriptionId}  {sub.Topic}  ->  {sub.EventTo}"
                              + (sub.TerminationTime is { } until ? $"  bis {until:dd.MM.yyyy HH:mm}" + (sub.Alive(DateTime.Now) ? "" : " (abgelaufen)") : ""));
        }
        else
        {
            sb.AppendLine("Ereignis-Abos: keine – die App pollt im eingestellten Intervall.");
        }

        // Die Herkunft der Anwesenheitsanzeige gehoert in den Bericht: ob ein Ereignis oder ein
        // Poll gemeldet hat, entscheidet, warum eine Anzeige hinterherhinkt.
        if (monitor is not null)
        {
            if (monitor.LastPresence is { } presence)
                sb.AppendLine($"Karte: {(presence.Inserted ? "rein" : "raus")}  ({presence.AgeText(DateTime.Now)}, {(presence.ViaEvent ? "aus Ereignis-Abo" : "aus Pollabfrage")})");
            if (monitor.LastEvent is { } lastEvent)
                sb.AppendLine($"Letztes Ereignis: {lastEvent.Summary}  ({lastEvent.ReceivedAt:dd.MM.yyyy HH:mm:ss})");
            sb.AppendLine("Ereignisstrom: " + monitor.DescribeEventStream());
        }
        sb.AppendLine("Konfiguration: " + ConnectorConfigurationDiff.DescribeLastChange());
        var history = ConnectorHistoryStore.Load();
        var keys = history.Hours.Select(h => h.HourKey).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
        // Ein krummer HourKey aus einer halbbeschadigten Datei darf den Bericht nicht umbringen –
        // der Store selbst liest dieselbe Angabe mit einer Kante ab.
        sb.AppendLine(keys.Count > 0
            ? $"Konnektor-Verlauf: {keys.Count} Stunden erfasst ({ShortHour(keys.First())} bis {ShortHour(keys.Last())})"
            : "Konnektor-Verlauf: noch keine Stunde erfasst – dafuer nur, was der Konnektor selbst meldet.");
        foreach (var a in ConnectorHealth.Evaluate(s, settings.CertificateWarningDays, DateTime.Now))
            sb.AppendLine($"Hinweis [{a.Urgency}]: {a.Title} – {a.Body}");
        return sb.ToString();
    }

    /// <summary>Kantige Kurzfassung eines HourKeys, damit ein krummer Wert den Bericht nicht umbringt.</summary>
    private static string ShortHour(string key) => key.Length <= 13 ? key : key[..13];

    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;
        SaveConnectButton.IsEnabled = !busy;
        TestButton.IsEnabled = !busy;
        UnsubscribeButton.IsEnabled = !busy;
        // Die Karten- und Terminallisten enthalten die Knopfe, die waehrend eines laufenden
        // Abrufs nichts bringen – doppelte Aufrufe waeren die Folge.
        CardsList.IsEnabled = !busy;
        TerminalsList.IsEnabled = !busy;
        CardDetailPanel.IsEnabled = !busy;
        TerminalDetailPanel.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.AppStarting : null;
        if (!busy && _themeRefreshQueued)
        {
            _themeRefreshQueued = false;
            if (_snapshot is not null)
                Render(_snapshot);
        }
        if (BusyOverlay is not null)
        {
            BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy)
            {
                BusyOverlayTitle.Text = L("Bitte warten …", "Please wait …");
                BusyOverlayText.Text = text ?? L("Der Konnektor wird abgefragt.", "The connector is being queried.");
            }
        }
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

    /// <summary>
    /// Warnung als Dialog. Der gesendete XML-Rumpf wird dabei ausgeklammert – er gehoert ins
    /// Protokoll und in den Bericht, nicht in einen Dialog, der den halben Bildschirm fuellt.
    /// </summary>
    private void ShowWarning(string message)
    {
        var head = message.Split('\n')[0].Trim();
        var extra = message.Length > head.Length
            ? L("\n\nAlle Einzelheiten inklusive des gesendeten Requests stehen im Bereich „Diagnose“ " +
              "und im Diagnosebericht, den Sie mit „Bericht“ in die Zwischenablage kopieren.",
              "\n\nEverything else, including the request that was sent, is in the \u201CDiagnostics\u201D section and " +
              "in the diagnostic report you can copy with \u201CReport\u201D.")
            : "";
        System.Windows.MessageBox.Show(this, head + extra, L("Konnektor", "Connector"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

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

    /// <summary>Steht da, wenn ein Farbwechsel mitten in eine laufende Aktion fiel.</summary>
    private bool _themeRefreshQueued;

    public void ApplyThemeRefresh()
    {
        // Waehrend einer laufenden Aktion wird nicht neu gebaut, sondern nachgeholt, sobald sie
        // fertig ist. Der Neuaufbau der Knopfe mitten in einer PIN-Abfrage am Terminal wuerde
        // die gerade beschriftete Karte austauschen. Ganz ueberspringen darf man aber auch
        // nicht, sonst bleibt die Maske nach dem Farbwechsel auf den alten Pinseln stehen.
        if (_busy)
        {
            _themeRefreshQueued = true;
            return;
        }
        _themeRefreshQueued = false;
        if (_snapshot is not null)
        {
            Render(_snapshot);
            return;
        }
        // Ohne Datenstand (z. B. Fehlermeldung) die per Code gesetzten Pinsel trotzdem
        // nachziehen – sonst bleibt der Banner in den Farben des alten Themas stehen.
        if (_lastStatus is { } st)
            ShowStatus(st.Title, st.Detail, st.Key, st.Glyph);
        RenderPresence();
        RenderHistory();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
