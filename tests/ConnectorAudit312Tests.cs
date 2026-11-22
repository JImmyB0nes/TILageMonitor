using System.Net;
using System.Net.Sockets;
using Org.BouncyCastle.Asn1.TeleTrust;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Xunit;

namespace TILageMonitor.Tests;

/// <summary>
/// Regressionstests zum Konnektor-Audit (v3.1.2): TLS-Ereignissenke, exakte Abo-Zuordnung,
/// Konfigurationsvergleich, Ledger bei abgeschalteten Benachrichtigungen, Ereignis-Parsing.
/// </summary>
public sealed class ConnectorAudit312Tests
{
    private const int Ecdsa = CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256;
    private const int Rsa = CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256;

    /// <summary>Konnektor-Seite (TLS-Client) mit fester Cipher-Reihenfolge, nur TLS 1.2.</summary>
    private sealed class KonnektorTls12Client : DefaultTlsClient
    {
        private readonly int[] _suites;
        public KonnektorTls12Client(int[] suites) : base(new BcTlsCrypto(new SecureRandom())) => _suites = suites;
        protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.TLSv12.Only();
        protected override int[] GetSupportedCipherSuites() => _suites;
        protected override IList<int> GetSupportedGroups(IList<int> roles) => ConnectorTlsClient.Groups.ToList();
        protected override IList<SignatureAndHashAlgorithm> GetSupportedSignatureAlgorithms() =>
            ConnectorTlsClient.SignatureSchemes.Select(SignatureScheme.GetSignatureAndHashAlgorithm).ToList();
        public override TlsAuthentication GetAuthentication() => new NoAuth();
        public int NegotiatedCipherSuite => m_context.SecurityParameters.CipherSuite;

        private sealed class NoAuth : TlsAuthentication
        {
            public void NotifyServerCertificate(TlsServerCertificate serverCertificate) { }
            public TlsCredentials? GetClientCredentials(CertificateRequest certificateRequest) => null;
        }
    }

    private static int NegotiatedSuite(bool ecKey, int[] clientSuites)
    {
        var key = ecKey ? TestPki.EcKey(TeleTrusTObjectIdentifiers.BrainpoolP256R1) : TestPki.RsaKey();
        var cert = TestPki.SelfSigned(key, "event-sink");
        var creds = ConnectorClientCredentials.Create(key.Private, [cert]);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Exception? serverError = null;
        var server = Task.Run(() =>
        {
            try
            {
                using var tcp = listener.AcceptTcpClient();
                tcp.ReceiveTimeout = 10000;
                var protocol = new TlsServerProtocol(tcp.GetStream());
                protocol.Accept(new ConnectorTlsServer(creds, null, requireClientCertificate: false));
                protocol.Close();
            }
            catch (Exception ex)
            {
                serverError = ex;
            }
        });
        try
        {
            using var tcp = new TcpClient();
            tcp.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            tcp.ReceiveTimeout = 10000;
            var protocol = new TlsClientProtocol(tcp.GetStream());
            var client = new KonnektorTls12Client(clientSuites);
            protocol.Connect(client);
            var suite = client.NegotiatedCipherSuite;
            try { protocol.Close(); } catch { /* ignore */ }
            server.Wait(10000);
            Assert.Null(serverError);
            return suite;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData(true, new[] { Ecdsa, Rsa }, Ecdsa)]
    [InlineData(true, new[] { Rsa, Ecdsa }, Ecdsa)] // frueher: illegal_parameter
    [InlineData(false, new[] { Rsa, Ecdsa }, Rsa)]
    [InlineData(false, new[] { Ecdsa, Rsa }, Rsa)] // frueher: illegal_parameter
    public void Event_sink_tls12_picks_the_suite_matching_its_key_regardless_of_client_order(bool ecKey, int[] clientSuites, int expected)
    {
        Assert.Equal(expected, NegotiatedSuite(ecKey, clientSuites));
    }

    [Fact]
    public void Event_sink_offers_only_suites_matching_the_key_type()
    {
        var ec = ConnectorTlsServer.CipherSuitesFor(ConnectorKeyType.EcSecp256r1);
        var rsa = ConnectorTlsServer.CipherSuitesFor(ConnectorKeyType.Rsa);
        Assert.Contains(Ecdsa, ec);
        Assert.DoesNotContain(Rsa, ec);
        Assert.Contains(Rsa, rsa);
        Assert.DoesNotContain(Ecdsa, rsa);
        // TLS 1.3 ist schluesselunabhaengig und bleibt in beiden Listen.
        Assert.Contains(CipherSuite.TLS_AES_128_GCM_SHA256, ec);
        Assert.Contains(CipherSuite.TLS_AES_128_GCM_SHA256, rsa);
    }

    [Fact]
    public void Event_sink_detects_tls_from_the_first_byte()
    {
        Assert.True(ConnectorEventStream.IsTlsHandshake(0x16));
        Assert.False(ConnectorEventStream.IsTlsHandshake((byte)'C'));
    }

    [Theory]
    [InlineData(true, false, true)]   // Konnektor verlangt TLS
    [InlineData(false, true, false)]  // Konnektor verlangt kein TLS – Einstellung "https" zaehlt nicht
    [InlineData(null, true, true)]    // ohne connector.sds: Einstellung
    [InlineData(null, false, false)]
    public void Cetp_tls_follows_the_connectors_tls_mandatory_flag(bool? mandatory, bool settingsTls, bool expected)
    {
        Assert.Equal(expected, ConnectorMonitor.CetpTlsRequired(mandatory, settingsTls));
    }

    [Fact]
    public async Task Event_sink_accepts_plain_cetp_even_when_a_certificate_is_loaded()
    {
        var key = TestPki.EcKey(TeleTrusTObjectIdentifiers.BrainpoolP256R1);
        var creds = ConnectorClientCredentials.Create(key.Private, [TestPki.SelfSigned(key, "event-sink")]);
        using var sink = new ConnectorEventStream();
        var received = new TaskCompletionSource<ConnectorEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        sink.EventReceived += e => received.TrySetResult(e);
        Assert.True(sink.EnsureListening(new ConnectorEventSinkOptions { Host = "127.0.0.1", UseTls = false, Credentials = creds }));
        Assert.True(ConnectorSubscription.TryParseTarget(sink.EventTo, out var host, out var port));

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Parse(host), port);
        var frame = ConnectorCetp.FrameXml("<Event><Topic>CARD/REMOVED</Topic><Message><Parameter><Key>CtID</Key><Value>CT9</Value></Parameter></Message></Event>");
        await tcp.GetStream().WriteAsync(frame);

        var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("CARD/REMOVED", evt.Topic);
        Assert.Equal("CT9", evt.Param("CtID"));
    }

    [Fact]
    public async Task Event_sink_still_speaks_tls_when_the_connector_starts_a_handshake()
    {
        var key = TestPki.EcKey(TeleTrusTObjectIdentifiers.BrainpoolP256R1);
        var creds = ConnectorClientCredentials.Create(key.Private, [TestPki.SelfSigned(key, "event-sink")]);
        using var sink = new ConnectorEventStream();
        var received = new TaskCompletionSource<ConnectorEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        sink.EventReceived += e => received.TrySetResult(e);
        Assert.True(sink.EnsureListening(new ConnectorEventSinkOptions { Host = "127.0.0.1", UseTls = false, Credentials = creds }));
        Assert.True(ConnectorSubscription.TryParseTarget(sink.EventTo, out var host, out var port));

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Parse(host), port);
        tcp.ReceiveTimeout = 10000;
        var protocol = new TlsClientProtocol(tcp.GetStream());
        protocol.Connect(new KonnektorTls12Client([Rsa, Ecdsa]));
        var frame = ConnectorCetp.FrameXml("<Event><Topic>CARD/INSERTED</Topic></Event>");
        protocol.Stream.Write(frame, 0, frame.Length);
        protocol.Stream.Flush();

        var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("CARD/INSERTED", evt.Topic);
        try { protocol.Close(); } catch { /* ignore */ }
    }

    [Fact]
    public void Tls_required_without_certificate_refuses_to_listen()
    {
        using var sink = new ConnectorEventStream();
        Assert.False(sink.EnsureListening(new ConnectorEventSinkOptions { Host = "127.0.0.1", UseTls = true, Credentials = null }));
        Assert.True(sink.EnsureListening(new ConnectorEventSinkOptions { Host = "127.0.0.1", UseTls = false, Credentials = null }));
    }

    [Fact]
    public void ParseEvent_tolerates_duplicate_and_differently_cased_keys()
    {
        const string xml = "<Event><Topic>CARD/INSERTED</Topic><Message>" +
                           "<Parameter><Key>CardType</Key><Value>SMC-B</Value></Parameter>" +
                           "<Parameter><Key>CardType</Key><Value>HBA</Value></Parameter>" +
                           "<Parameter><Key>CTID</Key><Value>CT1</Value></Parameter>" +
                           "<Parameter><Key>CtId</Key><Value>CT2</Value></Parameter>" +
                           "</Message></Event>";
        var evt = ConnectorProtocol.ParseEvent(xml);
        Assert.NotNull(evt);
        Assert.Equal("SMC-B", evt!.Param("CardType")); // der erste gilt
        Assert.Equal("CT1", evt.Param("CtID"));
    }

    [Theory]
    [InlineData("CtID")] // Schreibweise laut gemSpec_Kon
    [InlineData("CtId")]
    [InlineData("CTID")]
    public void Presence_reads_the_terminal_id_in_any_casing(string key)
    {
        var evt = ConnectorProtocol.ParseEvent(
            $"<Event><Topic>CARD/INSERTED</Topic><Message><Parameter><Key>{key}</Key><Value>CT7</Value></Parameter>" +
            "<Parameter><Key>CardType</Key><Value>SMC-B</Value></Parameter></Message></Event>");
        var presence = ConnectorPresence.FromEvent(evt);
        Assert.NotNull(presence);
        Assert.Equal("CT7", presence!.CtId);
    }

    [Fact]
    public void Configuration_diff_ignores_cards_and_connection_state()
    {
        var snapshot = new ConnectorSnapshot
        {
            Directory = new ConnectorServiceDirectory { FirmwareVersion = "5.1" },
            Terminals = [new ConnectorCardTerminal { CtId = "CT1", Name = "Empfang", Connected = true }],
            Cards =
            [
                new ConnectorCard { CardType = "EGK", Iccsn = "80276001234567890", CtId = "CT1", SlotId = "1", CardHolderName = "Erika Mustermann" },
                new ConnectorCard { CardType = "SMC-B", Iccsn = "80276009999999999", CtId = "CT1", SlotId = "2" }
            ]
        };
        var lines = ConnectorConfigurationDiff.Describe(snapshot);
        Assert.DoesNotContain(lines, l => l.StartsWith("karte", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("80276001234567890", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("verbunden", StringComparison.Ordinal));

        // Eine andere eGK, ein getrenntes Terminal: keine "Konfiguration geaendert"-Meldung.
        snapshot.Cards[0].Iccsn = "80276000000000001";
        snapshot.Terminals[0].Connected = false;
        Assert.Empty(ConnectorConfigurationDiff.Diff(lines, ConnectorConfigurationDiff.Describe(snapshot)));
    }

    private static ConnectorSnapshot SnapshotWithWarnings(DateTime now) => new()
    {
        Cards =
        [
            new ConnectorCard
            {
                CardType = "SMC-B", Iccsn = "80276001111111111", CardHandle = "h1",
                CertificateExpirationDate = now.AddDays(5),
                PinStates = [new ConnectorPinState { PinType = "PIN.SMC", Status = "VERIFIABLE", LeftTries = 3 }]
            }
        ]
    };

    [Fact]
    public void Ledger_is_not_advanced_while_notifications_are_off()
    {
        var now = new DateTime(2026, 11, 22, 12, 0, 0);
        var settings = new ConnectorSettings { Enabled = true, MonitoringEnabled = true, NotifyCertificateExpiry = true, NotifyPinStatus = true };
        var ledger = new ConnectorWarningLedger();

        var silent = ConnectorMonitor.EvaluateLedger(settings, notificationsEnabled: false, SnapshotWithWarnings(now), null, null, ledger, now);
        Assert.Empty(silent);
        Assert.Empty(ledger.CertificateStages);
        Assert.Empty(ledger.PinConditions);

        // Nach dem Wiedereinschalten kommt die Warnung – sie war nie "verbraucht".
        var due = ConnectorMonitor.EvaluateLedger(settings, notificationsEnabled: true, SnapshotWithWarnings(now), null, null, ledger, now);
        Assert.NotEmpty(due);
        Assert.NotEmpty(ledger.CertificateStages);
    }

    [Fact]
    public void Ledger_is_not_advanced_while_monitoring_is_off()
    {
        var now = new DateTime(2026, 11, 22, 12, 0, 0);
        var settings = new ConnectorSettings { Enabled = true, MonitoringEnabled = false };
        var ledger = new ConnectorWarningLedger();
        Assert.Empty(ConnectorMonitor.EvaluateLedger(settings, true, SnapshotWithWarnings(now), null, null, ledger, now));
        Assert.Empty(ledger.CertificateStages);
    }

    [Fact]
    public void Configuration_errors_are_not_outages()
    {
        Assert.True(ConnectorReconnect.IsConfigurationProblem(new ConnectorFaultException("4004: nicht berechtigt", "4004")));
        Assert.True(ConnectorReconnect.IsConfigurationProblem(new System.Net.Http.HttpRequestException("HTTP 401", null, HttpStatusCode.Unauthorized)));
        Assert.True(ConnectorReconnect.IsConfigurationProblem(new ConnectorTlsException("pin", pinFailure: true)));
        Assert.False(ConnectorReconnect.IsConfigurationProblem(new TimeoutException()));
        Assert.False(ConnectorReconnect.IsConfigurationProblem(new System.IO.IOException("reset")));
        Assert.False(ConnectorReconnect.IsConfigurationProblem(new System.Net.Http.HttpRequestException("HTTP 503", null, HttpStatusCode.ServiceUnavailable)));
    }

    [Fact]
    public void Restart_key_ignores_toast_filters_and_card_visibility()
    {
        var a = new ConnectorSettings { Host = "10.0.0.1", MandantId = "m", ClientSystemId = "c", WorkplaceId = "w", Enabled = true };
        var b = a.Clone();
        b.ToastCardInserted = !a.ToastCardInserted;
        b.ShowMainWindowCard = !a.ShowMainWindowCard;
        b.AllowPinManagement = !a.AllowPinManagement;
        b.CertificateWarningDays = a.CertificateWarningDays + 1;
        Assert.Equal(a.RestartKey, b.RestartKey);
        b.Host = "10.0.0.2";
        Assert.NotEqual(a.RestartKey, b.RestartKey);
    }

    [Fact]
    public void Pin_management_is_off_by_default()
    {
        Assert.False(new ConnectorSettings().AllowPinManagement);
        var json = System.Text.Json.JsonSerializer.Deserialize<ConnectorSettings>("{}")!;
        Assert.False(json.AllowPinManagement);
    }

    [Theory]
    [InlineData("10.0.0.1", false, "admin", true)]
    [InlineData("10.0.0.1", true, "admin", false)]
    [InlineData("http://10.0.0.1", true, "admin", true)]
    [InlineData("10.0.0.1", false, "", false)]
    public void Basic_auth_in_clear_is_detected(string host, bool useTls, string user, bool expected)
    {
        Assert.Equal(expected, new ConnectorSettings { Host = host, UseTls = useTls, BasicAuthUser = user }.SendsBasicAuthInClear);
    }

    [Fact]
    public void Sds_endpoint_follows_the_configured_transport()
    {
        var sds = """
            <ConnectorServices xmlns="http://ws.gematik.de/conn/ServiceDirectory/v3.1">
              <ServiceInformation>
                <Service Name="EventService">
                  <Versions>
                    <Version TargetNamespace="http://ws.gematik.de/conn/EventService/v7.2" Version="7.2.0">
                      <Endpoint Location="http://10.0.0.1:80/ws/EventService"/>
                      <EndpointTLS Location="https://10.0.0.1:443/ws/EventService"/>
                    </Version>
                  </Versions>
                </Service>
              </ServiceInformation>
            </ConnectorServices>
            """;
        var service = ConnectorProtocol.ParseServiceDirectory(sds).Services.Single();
        Assert.Equal("https://10.0.0.1:443/ws/EventService", service.EndpointFor(true));
        Assert.Equal("http://10.0.0.1:80/ws/EventService", service.EndpointFor(false));
    }

    [Fact]
    public void Corrupt_history_is_set_aside_and_locked_history_is_not_overwritten()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tila-hist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "connector-history.json");
            File.WriteAllText(path, "{ kaputt");
            Assert.True(ConnectorHistoryStore.TryLoad(path, out var file));
            Assert.Empty(file.Hours);
            Assert.True(File.Exists(path + ".corrupt"));

            File.WriteAllText(path, "{\"hours\":[]}");
            if (OperatingSystem.IsWindows())
            {
                using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert.False(ConnectorHistoryStore.TryLoad(path, out _));
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Theory]
    [InlineData(true, false, false, false, ConnectorCardVisibility.State.MonitoringOff)]
    [InlineData(true, true, false, false, ConnectorCardVisibility.State.Querying)]
    [InlineData(true, true, true, false, ConnectorCardVisibility.State.Unreachable)]
    [InlineData(true, true, true, true, ConnectorCardVisibility.State.Rejected)]
    [InlineData(false, true, false, false, ConnectorCardVisibility.State.NotSetUp)]
    public void Main_card_state_is_classified(bool configured, bool monitoring, bool error, bool configError, ConnectorCardVisibility.State expected)
    {
        var s = new ConnectorSettings { Enabled = true, MonitoringEnabled = monitoring };
        if (configured)
        {
            s.Host = "10.0.0.1";
            s.MandantId = "m";
            s.ClientSystemId = "c";
            s.WorkplaceId = "w";
        }
        Assert.Equal(expected, ConnectorCardVisibility.Classify(s, hasSnapshot: false, hasError: error, errorIsConfiguration: configError));
        Assert.Null(ConnectorCardVisibility.Reachable(ConnectorCardVisibility.State.MonitoringOff));
        Assert.Null(ConnectorCardVisibility.Reachable(ConnectorCardVisibility.State.Querying));
    }

    [Fact]
    public void Iccsn_is_masked_in_reports()
    {
        Assert.Equal("•••••••••••••7890", ConnectorPrivacy.MaskIccsn("80276001234567890"));
        Assert.Equal("", ConnectorPrivacy.MaskIccsn(null));
    }

    [Fact]
    public void User_id_omission_is_remembered_per_id()
    {
        var host = "10.9.9." + Random.Shared.Next(1, 250);
        ConnectorCapabilities.SetOmitUserId(ConnectorCapabilities.OmitKey(host, "alt"), true);
        Assert.True(ConnectorCapabilities.GetOmitUserId(ConnectorCapabilities.OmitKey(host, "alt")));
        Assert.Null(ConnectorCapabilities.GetOmitUserId(ConnectorCapabilities.OmitKey(host, "neu")));
        ConnectorCapabilities.SetMandantWide(host, true);
        ConnectorCapabilities.ForgetMandantWide(host);
        Assert.Null(ConnectorCapabilities.GetMandantWide(host));
        Assert.True(ConnectorCapabilities.GetOmitUserId(ConnectorCapabilities.OmitKey(host, "alt")));
        ConnectorCapabilities.Forget(host);
        Assert.Null(ConnectorCapabilities.GetOmitUserId(ConnectorCapabilities.OmitKey(host, "alt")));
    }
}
