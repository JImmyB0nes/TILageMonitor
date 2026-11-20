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
            // Das Abonnement haelt das Fenster ueber den Monitor lebendig, und der Rueckruf
            // wuerde nach dem Schliessen in eine verworfene Maske zeichnen.
            _ownerMain.ConnectorMonitor.Updated -= OnMonitorUpdated;
            try { _cts?.Cancel(); _cts?.Dispose(); _cts = null; } catch { /* ignore */ }
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
                ConnectionExpander.IsExpanded = false;
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
        };

        // Der Hintergrundpollen war die einzige Quelle, die dieses Fenster nicht erreichte: die
        // Maske zeigte denselben Stand, bis man selbst aktualisierte, waehrend der Knopf im
        // Hauptfenster laengst umgesprungen war. Dasselbe Bild, nur ohne eigenen Abruf.
        _ownerMain.ConnectorMonitor.Updated += OnMonitorUpdated;
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
                Render(latest);
        }

        if (Dispatcher.CheckAccess())
            Apply();
        else
            Dispatcher.BeginInvoke(Apply);
    }

    private static string L(string de, string en) => LocalizationService.Pick(de, en);

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
        NavCardsCount.Text = snapshot.Cards.Count > 0 ? $"  {snapshot.Cards.Count}" : "";
        NavTerminalsCount.Text = snapshot.Terminals.Count > 0 ? $"  {snapshot.Terminals.Count}" : "";
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
        // Konnektoren (v. a. RISE) quittieren unzulässige IDs mit "4000 Syntaxfehler" – vorher prüfen.
        var contextProblems = ConnectorProtocol.ValidateContextIds(s);
        if (contextProblems is not null)
            return L("Die IDs im Aufrufkontext lehnt der Konnektor höchstwahrscheinlich ab:\n\n" + contextProblems,
                     "The connector most likely rejects these call-context IDs:\n\n" + contextProblems);
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
        {
            ConnectionExpander.IsExpanded = false;
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
        StatusChips.Children.Add(Kpi(L("TI-Status", "TI status"), tiState,
            snapshot.Resource?.IsTiOnline == true ? "StatusOk" : snapshot.Resource?.IsTiOnline == false ? "StatusOutage" : null));
        StatusChips.Children.Add(Kpi(L("Karten", "Cards"), snapshot.Cards.Count.ToString()));
        StatusChips.Children.Add(Kpi(L("Terminals", "Terminals"), snapshot.Terminals.Count.ToString()));
        StatusChips.Children.Add(Kpi(L("Abruf", "Retrieved"), snapshot.RetrievedAt.ToString("HH:mm:ss")));

        UpdateNavCounts(snapshot);
        RenderAlerts(alerts, snapshot.Warnings);
        RenderConnectorInfo(snapshot);
        RenderCards(snapshot);
        RenderTerminals(snapshot);
        RenderServices(snapshot);
        FooterText.Text = L($"Letzter Abruf: {snapshot.RetrievedAt:dd.MM.yyyy HH:mm:ss}", $"Last retrieved: {snapshot.RetrievedAt:yyyy-MM-dd HH:mm:ss}");
    }

    private void ShowStatus(string title, string detail, string statusKey, string glyph = "\u2022")
    {
        StatusTitle.Text = title;
        StatusDetail.Text = detail;
        StatusGlyph.Text = glyph;
        StatusIconCircle.Background = Brush(statusKey);
        StatusBanner.Background = Brush(statusKey + "Surface");
        StatusBanner.BorderBrush = Brush(statusKey + "Border");
    }

    /// <summary>
    /// Kennzahl im Banner: winzige Bezeichnung, darunter der Wert. So ist der Zustand auf
    /// einen Blick lesbar, ohne dass vier gleich garte Kapseln aneinandergeklebt wirken.
    /// </summary>
    private Border Kpi(string label, string value, string? colorKey = null)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextMuted")
        });
        panel.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(colorKey ?? "TextMain"),
            Margin = new Thickness(0, 1, 0, 0)
        });
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
            var icon = alert.Urgency == ToastUrgency.Error ? "⛔" : alert.Urgency == ToastUrgency.Warning ? "⚠" : "ℹ";
            AlertsPanel.Children.Add(Alert($"{icon}  {alert.Title}", alert.Body, alert.Urgency == ToastUrgency.Error ? "StatusOutage" : "StatusPartial"));
        }
        foreach (var warning in warnings)
            AlertsPanel.Children.Add(Alert("ℹ  " + L("Teilabfrage fehlgeschlagen", "Partial query failed"), warning, null));

        AlertsCard.Visibility = AlertsPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Hinweis mit abgetrenntem Detailtext. Konnektor-Fehler enthalten sonst die komplette
    /// gesendete Nachricht – das sprengt jede Maske. Der Kernsatz steht oben, alles Weitere
    /// erst auf Knopfdruck. Der volle Text bleibt im Protokollreiter und im Bericht erhalten.
    /// </summary>
    private UIElement Alert(string title, string body, string? colorKey)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Foreground = colorKey is null ? Brush("TextMain") : Brush(colorKey),
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
            Margin = new Thickness(0, 2, 0, 0)
        });

        // Alles ab der zweiten Zeile ist Zusatzmaterial (IDs, Namespaces, XML): nur bei Bedarf.
        if (lines.Length > 1)
        {
            var more = new Expander
            {
                Header = L("Details anzeigen", "Show details"),
                FontSize = 11,
                Foreground = Brush("TextMuted"),
                Margin = new Thickness(0, 4, 0, 0)
            };
            more.Content = new TextBlock
            {
                Text = string.Join("\n", lines.Skip(1)).Trim(),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 11,
                Foreground = Brush("TextMuted"),
                TextWrapping = TextWrapping.Wrap
            };
            panel.Children.Add(more);
        }
        return panel;
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
                Text = label.ToUpperInvariant(),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("TextMuted")
            });
            panel.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 12,
                Foreground = Brush("TextMain"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0)
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

        Row(L("TLS-Zertifikat Konnektor", "Connector TLS certificate"), snapshot.ServerCertificateSubject is null ? null
            : $"{snapshot.ServerCertificateSubject}\n{L("gültig bis", "valid until")} {snapshot.ServerCertificateNotAfter:dd.MM.yyyy}");

        ConnectorInfoCard.Visibility = ConnectorInfoGrid.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Auswahl in den Zwei-Spalten-Ansichten. Nach einem Pollen dieselbe Karte
    /// wieder anzeigen, sonst springt die Maske beim Hintergrundaktualisieren um.</summary>
    private ConnectorCard? _selectedCard;
    private ConnectorCardTerminal? _selectedTerminal;
    private bool _fillingLists;

    private static IEnumerable<ConnectorCard> CardOrder(IEnumerable<ConnectorCard> cards) =>
        cards.OrderByDescending(c => c.IsSmcB ? 0 : c.IsHba ? 1 : c.IsEgk ? 3 : 2)
             .ThenBy(c => c.CtId, StringComparer.OrdinalIgnoreCase)
             .ThenBy(c => c.SlotId, StringComparer.OrdinalIgnoreCase);

    private static string KindOf(ConnectorCard card) =>
        card.IsSmcB ? "SMC-B" : card.IsHba ? "HBA" : card.IsEgk ? "eGK"
        : string.IsNullOrWhiteSpace(card.CardType) ? "KARTE" : card.CardType;

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

    /// <summary>Zeile in der Auswahlliste: Kante, Art, Name, eine winzige Herkunftszeile.</summary>
    private UIElement CardRow(ConnectorCard card, IReadOnlyDictionary<string, ConnectorCardTerminal> terminals)
    {
        var worstPin = WorstPinState(card);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var edge = new Border
        {
            Background = AccentForState(worstPin),
            CornerRadius = new CornerRadius(3, 0, 0, 3),
            Margin = new Thickness(0, 2, 0, 2)
        };
        Grid.SetColumn(edge, 0);
        grid.Children.Add(edge);

        var body = new StackPanel { Margin = new Thickness(11, 2, 0, 2) };
        // Orientation gibt es in WPF wie in WinForms – mit UseWindowsForms laeuft sonst
        // eine Mehrdeutigkeitspruefung, deshalb mit dem WPF-Typnamen qualifizieren.
        var line = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        line.Children.Add(Badge(KindOf(card), card.IsSmcB || card.IsHba ? "StatusMaintenance" : null));
        line.Children.Add(new TextBlock
        {
            Text = card.IsSmcB || card.IsHba ? card.CardHolderName : card.DisplayName,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextMain"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        body.Children.Add(line);

        var ctLabel = terminals.TryGetValue(card.CtId, out var ct) && !string.IsNullOrWhiteSpace(ct.Name) ? ct.Name : card.CtId;
        body.Children.Add(new TextBlock
        {
            Text = $"{ctLabel} · Slot {card.SlotId}",
            FontSize = 11,
            Foreground = Brush("TextMuted"),
            Margin = new Thickness(0, 2, 0, 0)
        });

        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
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

        // Kopfzeile: Art-Abzeichen, Name, rechts der grobe Zustand. Ein Blick, keine Textwand.
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = Badge(KindOf(card), card.IsSmcB || card.IsHba ? "StatusMaintenance" : null);
        badge.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(badge, 0);
        header.Children.Add(badge);

        var title = new TextBlock
        {
            Text = card.CardHolderName.Length > 0 ? card.CardHolderName : card.DisplayName,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextMain"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);

        var worstPin = WorstPinState(card);
        if (worstPin is not null)
        {
            var stateBadge = Badge(worstPin, StateColorKey(worstPin));
            stateBadge.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(stateBadge, 2);
            header.Children.Add(stateBadge);
        }
        CardDetailPanel.Children.Add(header);

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
        // Alle gelesenen Refs (C.AUT/C.ENC/C.SIG/C.QES) nebeneinander – so faellt ein
        // abgelaufenes QES sofort auf, auch wenn C.AUT noch monatelang gueltig ist.
        if (card.Certificates.Count > 0)
        {
            foreach (var c in card.Certificates.OrderBy(c => c.CertRef, StringComparer.Ordinal))
            {
                var days = (int)Math.Floor((c.NotAfter - DateTime.Now).TotalDays);
                var label = ConnectorProtocol.KnownCertRefs.FirstOrDefault(k => k.Ref == c.CertRef).Label;
                details.Add(L($"{c.CertRef} bis {c.NotAfter:dd.MM.yyyy} ({days} Tage)", $"{c.CertRef} until {c.NotAfter:yyyy-MM-dd} ({days} days)"));
                if (!string.IsNullOrWhiteSpace(label) && days <= 0)
                    details.Add(label);
            }
        }
        else if (card.EarliestCertificateExpiry is { } notAfter)
        {
            var days = (int)Math.Floor((notAfter - DateTime.Now).TotalDays);
            details.Add(L($"Zertifikat gültig bis {notAfter:dd.MM.yyyy} ({days} Tage)", $"Certificate valid until {notAfter:yyyy-MM-dd} ({days} days)"));
        }
        if (!string.IsNullOrWhiteSpace(cert?.TelematikId)) details.Add($"Telematik-ID: {cert!.TelematikId}");
        if (!string.IsNullOrWhiteSpace(cert?.Profession)) details.Add(L("Rolle", "Role") + $": {cert!.Profession}");
        if (!string.IsNullOrWhiteSpace(cert?.Organization) && cert!.Organization != card.CardHolderName) details.Add(L("Organisation", "Organization") + $": {cert.Organization}");
        if (cert is not null) details.Add($"{cert.CertRef} · {cert.KeyAlgorithm} · {L("Aussteller", "Issuer")} {cert.Issuer}");

        // Kennzahlen als Kapseln statt Textwand – dieselbe Handschrift wie im Banner.
        var metaRow = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var part in details)
            metaRow.Children.Add(Chip(part, null));
        CardDetailPanel.Children.Add(metaRow);

        CardDetailPanel.Children.Add(new Border { Height = 1, Background = Brush("BorderSubtle"), Margin = new Thickness(0, 12, 0, 12) });

        var pinTitle = new TextBlock
        {
            Text = L("PIN-Zustand", "PIN state"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextMuted"),
            Margin = new Thickness(0, 0, 0, 6)
        };
        CardDetailPanel.Children.Add(pinTitle);

        // PIN-Zeilen als schmale Tabellenzeilen, damit die Buttons nicht umbiegen.
        foreach (var pinType in card.PinTypes)
        {
            var state = card.PinStates.FirstOrDefault(p => p.PinType == pinType);
            CardDetailPanel.Children.Add(BuildPinRow(card, pinType, state));
        }
        if (card.PinTypes.Count == 0)
            CardDetailPanel.Children.Add(new TextBlock
            {
                Text = L("Diese Karte fuehrt keine PIN-Verwaltung ueber den Konnektor.", "This card has no PIN management via the connector."),
                FontSize = 12,
                Foreground = Brush("TextMuted")
            });

        if (card.SupportsPinManagement)
        {
            var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            if (cert is not null)
                actions.Children.Add(ActionButton(L("Zertifikat online prüfen (OCSP)", "Verify certificate online (OCSP)"), async () => await VerifyCertificateAsync(card)));
            actions.Children.Add(ActionButton(L("Alle Zertifikate lesen", "Read all certificates"), async () => await ReadAllCertificatesAsync(card)));
            actions.Children.Add(ActionButton(L("PIN-Status neu lesen", "Re-read PIN status"), async () => await RefreshPinStatusAsync(card)));
            // Freigabepruefung nur auf Knopfdruck: VerifyCard kann je nach Firmware eine
            // PIN-Abfrage am Terminal ausloesen, was im Hintergrundpolling unangenehme
            // Folgen haette (Terminal sperrt sich, wenn jemand die PIN nicht kennt).
            actions.Children.Add(ActionButton(L("Freigabe prüfen (VSDM)", "Check access (VSDM)"), async () => await CheckAccessAsync(card)));
            CardDetailPanel.Children.Add(actions);
        }

        CardDetailPanel.Children.Add(new TextBlock
        {
            Text = L("PIN-Eingaben erfolgen ausschließlich am Kartenterminal – die App sieht keine PIN oder PUK.",
                     "PIN entry happens exclusively at the card terminal – the app never sees a PIN or PUK."),
            FontSize = 11,
            Foreground = Brush("TextMuted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0)
        });
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

    /// <summary>Kleines farbiges Abzeichen – flach, ohne Schatten, nur Rand und Text.</summary>
    private Border Badge(string text, string? colorKey)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(colorKey ?? "TextMuted"),
            TextWrapping = TextWrapping.Wrap
        };
        return new Border
        {
            Child = label,
            Background = colorKey is null ? Brush("ChipBackground") : Brush(colorKey + "Surface"),
            BorderBrush = colorKey is null ? Brush("BorderSubtle") : Brush(colorKey + "Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(9, 3, 9, 3)
        };
    }

    private UIElement BuildPinRow(ConnectorCard card, string pinType, ConnectorPinState? state)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
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

        var name = new TextBlock
        {
            Text = pinType,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextMuted"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(name, 0);
        row.Children.Add(name);

        var status = new TextBlock
        {
            Text = statusText,
            FontSize = 12,
            Foreground = Brush(color),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
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
        var buttons = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        if (!blocked && !transport && state?.IsVerified != true)
            buttons.Children.Add(ActionButton(L("PIN verifizieren", "Verify PIN"), async () => await PinActionAsync(card, pinType, "VerifyPin")));
        if (!blocked)
            buttons.Children.Add(ActionButton(transport ? L("Transport-PIN ändern", "Change transport PIN") : L("PIN ändern", "Change PIN"),
                async () => await PinActionAsync(card, pinType, "ChangePin")));
        if (blocked || state?.LeftTries is <= 1)
            buttons.Children.Add(ActionButton(L("Mit PUK entsperren", "Unblock with PUK"), async () => await PinActionAsync(card, pinType, "UnblockPin")));
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

    /// <summary>Zeile in der Auswahlliste: Kante in Verbindungsfarbe, Name, eine Herkunftszeile.</summary>
    private UIElement TerminalRow(ConnectorCardTerminal t, ConnectorSnapshot snapshot)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var edge = new Border
        {
            Background = Brush(t.Connected == false ? "StatusPartial" : "StatusOk"),
            CornerRadius = new CornerRadius(3, 0, 0, 3),
            Margin = new Thickness(0, 2, 0, 2)
        };
        Grid.SetColumn(edge, 0);
        grid.Children.Add(edge);

        var body = new StackPanel { Margin = new Thickness(11, 2, 0, 2) };
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
        var here = snapshot.Cards.Count(c => string.Equals(c.CtId, t.CtId, StringComparison.OrdinalIgnoreCase));
        under.Add(L($"{here} Karten", $"{here} cards"));
        body.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", under),
            FontSize = 11,
            Foreground = Brush("TextMuted"),
            Margin = new Thickness(0, 2, 0, 0)
        });

        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }

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

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock
        {
            Text = t.Label,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextMain"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(title, 0);
        header.Children.Add(title);
        var stateBadge = Badge(state, t.Connected == false ? "StatusPartial" : "StatusOk");
        stateBadge.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(stateBadge, 1);
        header.Children.Add(stateBadge);
        TerminalDetailPanel.Children.Add(header);

        // Technische Werte als Kapseln – dieselbe Handschrift wie bei der Karte.
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(t.IpAddress)) parts.Add($"IP {t.IpAddress}");
        if (!string.IsNullOrWhiteSpace(t.MacAddress)) parts.Add($"MAC {t.MacAddress}");
        if (t.Slots is { } slots) parts.Add($"{slots} Slots");
        if (t.IsPhysical == false) parts.Add(L("virtuell", "virtual"));
        if (!string.IsNullOrWhiteSpace(t.Details)) parts.Add(t.Details);
        if (t.Workplaces.Count > 0)
            parts.Add(L("Arbeitsplaetze: ", "Workplaces: ") + string.Join(", ", t.Workplaces));

        if (parts.Count > 0)
        {
            var chips = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            foreach (var part in parts)
                chips.Children.Add(Chip(part, null));
            TerminalDetailPanel.Children.Add(chips);
        }

        TerminalDetailPanel.Children.Add(new Border { Height = 1, Background = Brush("BorderSubtle"), Margin = new Thickness(0, 12, 0, 12) });

        var cardTitle = new TextBlock
        {
            Text = L("Gesteckte Karten", "Inserted cards"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextMuted"),
            Margin = new Thickness(0, 0, 0, 6)
        };
        TerminalDetailPanel.Children.Add(cardTitle);

        if (cards.Count == 0)
        {
            TerminalDetailPanel.Children.Add(new TextBlock
            {
                Text = L("In diesem Terminal steckt keine Karte.", "No card is inserted in this terminal."),
                FontSize = 12,
                Foreground = Brush("TextMuted")
            });
            return;
        }

        foreach (var card in CardOrder(cards))
        {
            var worstPin = WorstPinState(card);
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var badge = Badge(KindOf(card), card.IsSmcB || card.IsHba ? "StatusMaintenance" : null);
            badge.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(badge, 0);
            row.Children.Add(badge);

            var name = new TextBlock
            {
                Text = $"Slot {card.SlotId} · {card.DisplayName}",
                FontSize = 13,
                Foreground = Brush("TextMain"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            if (worstPin is not null)
            {
                var pinBadge = Badge(worstPin, StateColorKey(worstPin));
                pinBadge.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(pinBadge, 2);
                row.Children.Add(pinBadge);
            }
            TerminalDetailPanel.Children.Add(row);
        }
    }

    private void RenderServices(ConnectorSnapshot snapshot)
    {
        ServicesPanel.Children.Clear();
        var services = snapshot.Directory?.Services ?? [];
        foreach (var group in services.GroupBy(s => s.Name).OrderBy(g => g.Key))
        {
            // Zwei Spalten: links Name und Version, rechts die Endpunkte. Die fruehere Bauart
            // schrieb die Version mitten in die Kopfzeile und die Endpunkte darunter – dadurch
            // liess sich nicht erkennen, welche Version zu welchem Pfad gehoert.
            var row = new Grid { Margin = new Thickness(0, 0, 0, 9) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var head = new StackPanel();
            head.Children.Add(new TextBlock
            {
                Text = group.Key,
                Foreground = Brush("TextMain"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            });
            head.Children.Add(new TextBlock
            {
                Text = string.Join(", ", group.Select(s => s.Version).Distinct()),
                Foreground = Brush("TextMuted"),
                FontSize = 11,
                Margin = new Thickness(0, 1, 0, 0)
            });
            Grid.SetColumn(head, 0);
            row.Children.Add(head);

            var paths = new TextBlock
            {
                Text = string.Join("\n", group.Select(s => s.Endpoint).Distinct()),
                Foreground = Brush("TextMuted"),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(paths, 1);
            row.Children.Add(paths);

            ServicesPanel.Children.Add(row);
        }
        ServicesCard.Visibility = services.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
    /// Fragt den Konnektor, ob fuer diese Karte die Berechtigung VSDM vorliegt. Zuerst wird
    /// IsContextAvailable gelesen (unschaedlich); liefert der Konnektor dazu nichts, wird auf
    /// VerifyCard umgestiegen – das kann dann aber eine PIN-Abfrage am Terminal ausloesen.
    /// </summary>
    private async Task CheckAccessAsync(ConnectorCard card)
    {
        SetBusy(true, L("Prüfe Freigabe …", "Checking access …"));
        try
        {
            using var client = CreateClient();
            ConnectorAccessCheck check;
            var via = L("IsContextAvailable", "IsContextAvailable");
            try
            {
                check = await client.IsContextAvailableAsync("VSDM", CancellationToken.None);
                if (check.Granted is null && check.Roles.Count == 0)
                {
                    via = L("VerifyCard", "VerifyCard");
                    check = await client.VerifyCardAsync(card.CardHandle, "VSDM", CancellationToken.None);
                }
            }
            catch (ConnectorFaultException)
            {
                via = L("VerifyCard", "VerifyCard");
                check = await client.VerifyCardAsync(card.CardHandle, "VSDM", CancellationToken.None);
            }

            var text = check.Granted switch
            {
                true => L("VSDM-Freigabe liegt vor ✓", "VSDM access is granted ✓"),
                false => L("VSDM-Freigabe NICHT erteilt – die Karte darf fuer VSDM nicht verwendet werden.", "VSDM access is NOT granted – the card must not be used for VSDM."),
                _ => L("Der Konnektor aeussert sich nicht eindeutig zur Freigabe.", "The connector does not give a clear answer about the access.")
            };
            if (!string.IsNullOrWhiteSpace(check.Result))
                text += "\n" + L("Ergebnis: ", "Result: ") + check.Result;
            if (check.Roles.Count > 0)
                text += "\n" + L("Rollen: ", "Roles: ") + string.Join(", ", check.Roles);
            text += "\n\n" + L("Quelle: ", "Source: ") + via;

            Log($"{card.DisplayName}: VSDM-Freigabe {check.Granted?.ToString() ?? "unbestimmt"} ({via})");
            System.Windows.MessageBox.Show(this, $"{card.DisplayName}\n\n{text}",
                L("Freigabe-Prüfung", "Access check"), MessageBoxButton.OK,
                check.Granted == true ? MessageBoxImage.Information : MessageBoxImage.Warning);
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

    /// <summary>
    /// Liest alle vier nach CertRefEnum moeglichen Zertifikate und traegt sie in die Karte ein.
    /// Refs, die die Karte nicht fuehrt, bleiben leer – das ist kein Fehler.
    /// </summary>
    private async Task ReadAllCertificatesAsync(ConnectorCard card)
    {
        if (_snapshot is null)
            return;
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
            sb.Append($"  {c.CardType} {c.CardHolderName} ICCSN {c.Iccsn} @ {c.CtId}/{c.SlotId}");
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
            if (s.Warnings.Any(w => w.Contains("Syntaxfehler", StringComparison.OrdinalIgnoreCase)))
                sb.AppendLine("  Hinweis: 4000 heisst nur, dass der Konnektor die Nachricht ablehnt – nicht womit. " +
                              "Der Aufrufkontext ist bei allen Diensten gleich: antworten EventService oder " +
                              "CertificateService mit denselben IDs, ist der Kontext als Ursache ausgeschlossen. " +
                              "Bleibt als Kandidat der Inhalt der betroffenen Operation, zum Beispiel der Namespace " +
                              "eines einzelnen Parameters. Bei 4003 fehlt eine zwingende Angabe – fuer " +
                              "Kartensitzungen des HBA ist das regelmaessig die Benutzer-ID im Aufrufkontext.");
        }
        if (s.Subscriptions.Count > 0)
        {
            sb.AppendLine("Ereignis-Abos:");
            foreach (var sub in s.Subscriptions)
                sb.AppendLine($"  {sub.SubscriptionId}  {sub.Topic}  ->  {sub.EventTo}");
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
        // Die Karten- und Terminallisten enthalten die Knopfe, die waehrend eines laufenden
        // Abrufs nichts bringen – doppelte Aufrufe waeren die Folge.
        CardsList.IsEnabled = !busy;
        TerminalsList.IsEnabled = !busy;
        CardDetailPanel.IsEnabled = !busy;
        TerminalDetailPanel.IsEnabled = !busy;
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

    /// <summary>
    /// Warnung als Dialog. Der gesendete XML-Rumpf wird dabei ausgeklammert – er gehoert ins
    /// Protokoll und in den Bericht, nicht in einen Dialog, der den halben Bildschirm fuellt.
    /// </summary>
    private void ShowWarning(string message)
    {
        var head = message.Split('\n')[0].Trim();
        var extra = message.Length > head.Length
            ? L("\n\nAlle Einzelheiten inklusive des gesendeten Requests stehen im Reiter „Protokoll“ " +
              "und im Diagnosebericht, den Sie mit „Bericht“ in die Zwischenablage kopieren.",
              "\n\nEverything else, including the request that was sent, is in the \u201CProtocol\u201D tab and " +
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

    public void ApplyThemeRefresh()
    {
        if (_snapshot is not null)
            Render(_snapshot);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
