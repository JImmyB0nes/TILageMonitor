using System.Xml.Linq;
using Xunit;

namespace TILageMonitor.Tests;

public sealed class ConnectorProtocolTests
{
    private const string Sds = """
        <?xml version="1.0" encoding="UTF-8"?>
        <ns3:ConnectorServices xmlns:ns3="http://ws.gematik.de/conn/ServiceDirectory/v3.1"
                               xmlns:ns2="http://ws.gematik.de/conn/ServiceInformation/v2.0"
                               xmlns:ns4="http://ws.gematik.de/int/version/ProductInformation/v1.1">
          <ns4:ProductInformation>
            <ns4:InformationDate>2026-10-01T10:00:00Z</ns4:InformationDate>
            <ns4:ProductTypeInformation>
              <ns4:ProductType>Konnektor</ns4:ProductType>
              <ns4:ProductTypeVersion>5.6.0</ns4:ProductTypeVersion>
            </ns4:ProductTypeInformation>
            <ns4:ProductIdentification>
              <ns4:ProductVendorID>TEST</ns4:ProductVendorID>
              <ns4:ProductCode>KON1</ns4:ProductCode>
              <ns4:ProductVersion>
                <ns4:Local>
                  <ns4:HWVersion>2.0.0</ns4:HWVersion>
                  <ns4:FWVersion>5.1.2</ns4:FWVersion>
                </ns4:Local>
              </ns4:ProductVersion>
            </ns4:ProductIdentification>
            <ns4:ProductMiscellaneous>
              <ns4:ProductVendorName>Testhersteller</ns4:ProductVendorName>
              <ns4:ProductName>Testkonnektor</ns4:ProductName>
            </ns4:ProductMiscellaneous>
          </ns4:ProductInformation>
          <ns3:TLSMandatory>true</ns3:TLSMandatory>
          <ns3:ClientAutMandatory>false</ns3:ClientAutMandatory>
          <ns2:ServiceInformation>
            <ns2:Service Name="EventService">
              <ns2:Versions>
                <ns2:Version TargetNamespace="http://ws.gematik.de/conn/EventService/v7.2" Version="7.2.0">
                  <ns2:EndpointTLS Location="https://kon.intern:443/ws/EventService"/>
                </ns2:Version>
                <ns2:Version TargetNamespace="http://ws.gematik.de/conn/EventService/v6.1" Version="6.1.1">
                  <ns2:EndpointTLS Location="https://kon.intern:443/ws/EventService61"/>
                </ns2:Version>
              </ns2:Versions>
            </ns2:Service>
            <ns2:Service Name="CardService">
              <ns2:Versions>
                <ns2:Version TargetNamespace="http://ws.gematik.de/conn/CardService/v8.1" Version="8.1.2">
                  <ns2:Endpoint Location="http://kon.intern/ws/CardService"/>
                  <ns2:EndpointTLS Location="https://kon.intern/ws/CardService"/>
                </ns2:Version>
              </ns2:Versions>
            </ns2:Service>
          </ns2:ServiceInformation>
        </ns3:ConnectorServices>
        """;

    [Fact]
    public void ParseServiceDirectory_reads_product_and_highest_service_version()
    {
        var dir = ConnectorProtocol.ParseServiceDirectory(Sds.Trim());

        Assert.Equal("Testhersteller", dir.ProductVendorName);
        Assert.Equal("Testkonnektor", dir.ProductName);
        Assert.Equal("5.6.0", dir.ProductTypeVersion);
        Assert.Equal("5.1.2", dir.FirmwareVersion);
        Assert.True(dir.TlsMandatory);
        Assert.False(dir.ClientAuthMandatory);

        var evt = dir.Find("EventService");
        Assert.NotNull(evt);
        Assert.Equal("7.2.0", evt!.Version);
        Assert.Equal("http://ws.gematik.de/conn/EventService/v7.2", evt.TargetNamespace);

        // EndpointTLS hat Vorrang vor Endpoint
        Assert.Equal("https://kon.intern/ws/CardService", dir.Find("CardService")!.Endpoint);
    }

    [Theory]
    [InlineData("https://kon.intern:443/ws/EventService", "https://192.168.1.10/", true, "https://192.168.1.10/ws/EventService")]
    [InlineData("https://kon.intern/ws/CardService", "https://10.0.0.5:8443/", true, "https://10.0.0.5:8443/ws/CardService")]
    [InlineData("https://kon.intern:9443/ws/X", "https://10.0.0.5/", true, "https://10.0.0.5:9443/ws/X")]
    [InlineData("https://kon.intern/ws/X", "https://10.0.0.5/", false, "https://kon.intern/ws/X")]
    [InlineData("/ws/Relative", "https://10.0.0.5/", true, "https://10.0.0.5/ws/Relative")]
    public void ResolveEndpoint_rewrites_host_but_keeps_explicit_port_and_path(string endpoint, string baseUri, bool rewrite, string expected)
    {
        Assert.Equal(expected, ConnectorProtocol.ResolveEndpoint(endpoint, new Uri(baseUri), rewrite).ToString());
    }

    [Theory]
    [InlineData("192.168.1.10", true, "https://192.168.1.10/")]
    [InlineData("192.168.1.10:8443", true, "https://192.168.1.10:8443/")]
    [InlineData("konnektor.praxis.local", false, "http://konnektor.praxis.local/")]
    [InlineData("https://10.0.0.1/some/path", false, "https://10.0.0.1/")]
    public void BuildBaseUri_normalizes_host(string host, bool tls, string expected)
    {
        Assert.Equal(expected, ConnectorProtocol.BuildBaseUri(host, tls).ToString());
    }

    private static ConnectorSettings Settings() => new()
    {
        Host = "192.168.1.10",
        MandantId = "Mandant1",
        ClientSystemId = "CS1",
        WorkplaceId = "AP1"
    };

    [Fact]
    public void GetCards_request_contains_context_in_common_namespace()
    {
        XNamespace evt = "http://ws.gematik.de/conn/EventService/v7.2";
        var envelope = XDocument.Parse(ConnectorProtocol.Envelope(ConnectorProtocol.GetCards(evt, Settings())));
        var getCards = envelope.Descendants(evt + "GetCards").Single();

        Assert.Equal("true", (string?)getCards.Attribute("mandant-wide"));
        var ctx = getCards.Element(ConnectorProtocol.Cctx + "Context")!;
        Assert.Equal("Mandant1", ctx.Element(ConnectorProtocol.Conn + "MandantId")!.Value);
        Assert.Equal("CS1", ctx.Element(ConnectorProtocol.Conn + "ClientSystemId")!.Value);
        Assert.Equal("AP1", ctx.Element(ConnectorProtocol.Conn + "WorkplaceId")!.Value);
        Assert.Null(ctx.Element(ConnectorProtocol.Conn + "UserId"));
    }

    [Fact]
    public void ReadCardCertificate_puts_card_handle_before_context()
    {
        XNamespace cert = "http://ws.gematik.de/conn/CertificateService/v6.0";
        var element = ConnectorProtocol.ReadCardCertificate(cert, Settings(), "H1", "C.AUT", "ECC");
        var names = element.Elements().Select(e => e.Name.LocalName).ToList();

        Assert.Equal(["CardHandle", "Context", "CertRefList", "Crypt"], names);
    }

    [Fact]
    public void PinOperation_unblock_contains_set_new_pin()
    {
        XNamespace card = "http://ws.gematik.de/conn/CardService/v8.1";
        var element = ConnectorProtocol.PinOperation(card, "UnblockPin", Settings(), "H1", "PIN.SMC", setNewPin: true);

        Assert.Equal(["Context", "CardHandle", "PinTyp", "SetNewPin"], element.Elements().Select(e => e.Name.LocalName).ToList());
        Assert.Equal("PIN.SMC", element.Element(card + "PinTyp")!.Value);
        Assert.Equal("true", element.Element(card + "SetNewPin")!.Value);
        Assert.Equal("http://ws.gematik.de/conn/CardService/v8.1#UnblockPin", ConnectorProtocol.SoapAction(card, "UnblockPin"));
    }

    private static string Wrap(string body) => $"""
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
          <soap:Body>{body}</soap:Body>
        </soap:Envelope>
        """;

    [Fact]
    public void ParseCards_reads_all_card_fields()
    {
        var xml = Wrap("""
            <EVT:GetCardsResponse xmlns:EVT="http://ws.gematik.de/conn/EventService/v7.2"
                                  xmlns:CONN="http://ws.gematik.de/conn/ConnectorCommon/v5.0"
                                  xmlns:CARD="http://ws.gematik.de/conn/CardService/v8.1"
                                  xmlns:CARDCMN="http://ws.gematik.de/conn/CardServiceCommon/v2.0">
              <CONN:Status><CONN:Result>OK</CONN:Result></CONN:Status>
              <CARD:Cards>
                <CARD:Card>
                  <CONN:CardHandle>SMCB-1</CONN:CardHandle>
                  <CARDCMN:CardType>SMC-B</CARDCMN:CardType>
                  <CARD:CardVersion><CARD:COSVersion><CARD:Major>4</CARD:Major><CARD:Minor>5</CARD:Minor><CARD:Revision>0</CARD:Revision></CARD:COSVersion></CARD:CardVersion>
                  <CARDCMN:Iccsn>80276001011699901234</CARDCMN:Iccsn>
                  <CARDCMN:CtId>CT1</CARDCMN:CtId>
                  <CARDCMN:SlotId>2</CARDCMN:SlotId>
                  <CARD:InsertTime>2026-11-01T08:00:00Z</CARD:InsertTime>
                  <CARD:CardHolderName>Praxis Dr. Test</CARD:CardHolderName>
                  <CARD:CertificateExpirationDate>2027-03-31</CARD:CertificateExpirationDate>
                </CARD:Card>
                <CARD:Card>
                  <CONN:CardHandle>EGK-1</CONN:CardHandle>
                  <CARDCMN:CardType>EGK</CARDCMN:CardType>
                  <CARDCMN:CtId>CT1</CARDCMN:CtId>
                  <CARDCMN:SlotId>1</CARDCMN:SlotId>
                  <CARDCMN:Kvnr>X110000000</CARDCMN:Kvnr>
                </CARD:Card>
              </CARD:Cards>
            </EVT:GetCardsResponse>
            """);

        var cards = ConnectorProtocol.ParseCards(ConnectorProtocol.ParseBody(xml));

        Assert.Equal(2, cards.Count);
        var smcb = cards[0];
        Assert.True(smcb.IsSmcB);
        Assert.Equal("SMCB-1", smcb.CardHandle);
        Assert.Equal("4.5.0", smcb.CardVersion);
        Assert.Equal("CT1", smcb.CtId);
        Assert.Equal("2", smcb.SlotId);
        Assert.Equal("Praxis Dr. Test", smcb.CardHolderName);
        Assert.Equal(new DateTime(2027, 3, 31), smcb.CertificateExpirationDate!.Value.Date);
        Assert.Equal(["PIN.SMC"], smcb.PinTypes);

        Assert.True(cards[1].IsEgk);
        Assert.Equal("X110000000", cards[1].Kvnr);
        Assert.False(cards[1].SupportsPinManagement);
    }

    [Fact]
    public void ParseCardTerminals_reads_connection_state()
    {
        var xml = Wrap("""
            <EVT:GetCardTerminalsResponse xmlns:EVT="http://ws.gematik.de/conn/EventService/v7.2"
                                          xmlns:CONN="http://ws.gematik.de/conn/ConnectorCommon/v5.0"
                                          xmlns:CT="http://ws.gematik.de/conn/CardTerminalInfo/v8.0">
              <CONN:Status><CONN:Result>OK</CONN:Result></CONN:Status>
              <CT:CardTerminals>
                <CT:CardTerminal>
                  <CONN:CtId>CT1</CONN:CtId>
                  <CT:Name>Empfang</CT:Name>
                  <CT:MacAddress>00:0D:F8:01:02:03</CT:MacAddress>
                  <CT:IPAddress><CT:IPV4Address>192.168.1.20</CT:IPV4Address></CT:IPAddress>
                  <CT:Slots>4</CT:Slots>
                  <CT:IS_PHYSICAL>true</CT:IS_PHYSICAL>
                  <CT:Connected>false</CT:Connected>
                </CT:CardTerminal>
              </CT:CardTerminals>
            </EVT:GetCardTerminalsResponse>
            """);

        var terminal = Assert.Single(ConnectorProtocol.ParseCardTerminals(ConnectorProtocol.ParseBody(xml)));
        Assert.Equal("CT1", terminal.CtId);
        Assert.Equal("Empfang", terminal.Name);
        Assert.Equal("192.168.1.20", terminal.IpAddress);
        Assert.Equal(4, terminal.Slots);
        Assert.False(terminal.Connected);
    }

    [Fact]
    public void ParseResourceInformation_reads_vpn_and_error_states()
    {
        var xml = Wrap("""
            <EVT:GetResourceInformationResponse xmlns:EVT="http://ws.gematik.de/conn/EventService/v7.2"
                                                xmlns:CONN="http://ws.gematik.de/conn/ConnectorCommon/v5.0">
              <CONN:Status><CONN:Result>OK</CONN:Result></CONN:Status>
              <CONN:Connector>
                <CONN:VPNTIStatus><CONN:ConnectionStatus>Offline</CONN:ConnectionStatus><CONN:Timestamp>2026-11-18T09:00:00Z</CONN:Timestamp></CONN:VPNTIStatus>
                <CONN:VPNSISStatus><CONN:ConnectionStatus>Online</CONN:ConnectionStatus><CONN:Timestamp>2026-11-18T09:00:00Z</CONN:Timestamp></CONN:VPNSISStatus>
                <CONN:OperatingState>
                  <CONN:ErrorState>
                    <CONN:ErrorCondition>EC_TI_Network_Not_Reachable</CONN:ErrorCondition>
                    <CONN:Severity>Error</CONN:Severity>
                    <CONN:Type>Operation</CONN:Type>
                    <CONN:Value>true</CONN:Value>
                    <CONN:ValidFrom>2026-11-18T08:55:00Z</CONN:ValidFrom>
                  </CONN:ErrorState>
                  <CONN:ErrorState>
                    <CONN:ErrorCondition>EC_Time_Sync_Not_Successful</CONN:ErrorCondition>
                    <CONN:Severity>Warning</CONN:Severity>
                    <CONN:Type>Operation</CONN:Type>
                    <CONN:Value>false</CONN:Value>
                  </CONN:ErrorState>
                </CONN:OperatingState>
              </CONN:Connector>
            </EVT:GetResourceInformationResponse>
            """);

        var info = ConnectorProtocol.ParseResourceInformation(ConnectorProtocol.ParseBody(xml));
        Assert.False(info.IsTiOnline);
        Assert.Equal("Online", info.VpnSisStatus);
        Assert.Equal(2, info.ErrorStates.Count);
        var active = Assert.Single(info.ActiveErrors);
        Assert.Equal("EC_TI_Network_Not_Reachable", active.Condition);
    }

    [Fact]
    public void ParsePinStatus_and_result()
    {
        var status = ConnectorProtocol.ParsePinStatus(ConnectorProtocol.ParseBody(Wrap("""
            <CARD:GetPinStatusResponse xmlns:CARD="http://ws.gematik.de/conn/CardService/v8.1"
                                       xmlns:CONN="http://ws.gematik.de/conn/ConnectorCommon/v5.0">
              <CONN:Status><CONN:Result>OK</CONN:Result></CONN:Status>
              <CARD:PinStatus>VERIFIABLE</CARD:PinStatus>
              <CARD:LeftTries>2</CARD:LeftTries>
            </CARD:GetPinStatusResponse>
            """)), "PIN.SMC");
        Assert.Equal("VERIFIABLE", status.Status);
        Assert.Equal(2, status.LeftTries);
        Assert.False(status.IsVerified);

        var result = ConnectorProtocol.ParsePinResult(ConnectorProtocol.ParseBody(Wrap("""
            <CARD:VerifyPinResponse xmlns:CARD="http://ws.gematik.de/conn/CardService/v8.1"
                                    xmlns:CONN="http://ws.gematik.de/conn/ConnectorCommon/v5.0"
                                    xmlns:CARDCMN="http://ws.gematik.de/conn/CardServiceCommon/v2.0">
              <CONN:Status><CONN:Result>OK</CONN:Result></CONN:Status>
              <CARDCMN:PinResult>REJECTED</CARDCMN:PinResult>
              <CARD:LeftTries>1</CARD:LeftTries>
            </CARD:VerifyPinResponse>
            """)));
        Assert.Equal("REJECTED", result.Result);
        Assert.Equal(1, result.LeftTries);
        Assert.False(result.IsOk);
    }

    [Fact]
    public void ParseBody_throws_gematik_error_from_soap_fault()
    {
        var xml = Wrap("""
            <soap:Fault xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <faultcode>soap:Server</faultcode>
              <faultstring>Technischer Fehler</faultstring>
              <detail>
                <GERROR:Error xmlns:GERROR="http://ws.gematik.de/tel/error/v2.0">
                  <GERROR:MessageID>1</GERROR:MessageID>
                  <GERROR:Timestamp>2026-11-18T09:00:00Z</GERROR:Timestamp>
                  <GERROR:Trace>
                    <GERROR:EventID>1</GERROR:EventID>
                    <GERROR:Instance>x</GERROR:Instance>
                    <GERROR:LogReference>y</GERROR:LogReference>
                    <GERROR:CompType>Konnektor</GERROR:CompType>
                    <GERROR:Code>4004</GERROR:Code>
                    <GERROR:Severity>Error</GERROR:Severity>
                    <GERROR:ErrorType>Technical</GERROR:ErrorType>
                    <GERROR:ErrorText>Ungültige Mandanten-ID</GERROR:ErrorText>
                  </GERROR:Trace>
                </GERROR:Error>
              </detail>
            </soap:Fault>
            """);

        var ex = Assert.Throws<ConnectorFaultException>(() => ConnectorProtocol.ParseBody(xml));
        Assert.Equal("4004", ex.Code);
        Assert.Contains("Ungültige Mandanten-ID", ex.Message);
    }

    [Fact]
    public void ParseAdmission_extracts_telematik_id_and_profession()
    {
        // AdmissionSyntax ::= SEQUENCE { contentsOfAdmissions SEQUENCE OF Admissions }
        // Admissions ::= SEQUENCE { professionInfos SEQUENCE OF ProfessionInfo }
        // ProfessionInfo ::= SEQUENCE { professionItems SEQUENCE OF UTF8String,
        //                               professionOIDs SEQUENCE OF OID, registrationNumber PrintableString }
        byte[] Tlv(byte tag, params byte[][] content)
        {
            var body = content.SelectMany(c => c).ToArray();
            return [tag, (byte)body.Length, .. body];
        }
        byte[] Str(byte tag, string s) => Tlv(tag, System.Text.Encoding.UTF8.GetBytes(s));

        var professionInfo = Tlv(0x30,
            Tlv(0x30, Str(0x0C, "Betriebsstätte Arzt")),
            Tlv(0x30, Tlv(0x06, [0x2A, 0x82, 0x14, 0x00, 0x4C, 0x04, 0x32])),
            Str(0x13, "1-2arvtst-ap000001"));
        var der = Tlv(0x30, Tlv(0x30, Tlv(0x30, Tlv(0x30, professionInfo))));

        var (telematikId, profession) = ConnectorProtocol.ParseAdmission(der);
        Assert.Equal("1-2arvtst-ap000001", telematikId);
        Assert.Equal("Betriebsstätte Arzt", profession);
    }
}

public sealed class ConnectorHealthTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0);

    [Fact]
    public void Healthy_snapshot_has_no_alerts()
    {
        var snapshot = new ConnectorSnapshot
        {
            Resource = new ConnectorResourceInfo { VpnTiStatus = "Online" },
            Terminals = [new ConnectorCardTerminal { CtId = "CT1", Connected = true }],
            Cards =
            [
                new ConnectorCard
                {
                    CardType = "SMC-B", Iccsn = "1", CertificateExpirationDate = Now.AddYears(2),
                    PinStates = [new ConnectorPinState { PinType = "PIN.SMC", Status = "VERIFIED" }]
                }
            ]
        };

        Assert.Empty(ConnectorHealth.Evaluate(snapshot, 60, Now));
        Assert.Null(ConnectorHealth.Worst([]));
    }

    [Fact]
    public void Detects_offline_vpn_unverified_smcb_expiring_cert_and_disconnected_terminal()
    {
        var snapshot = new ConnectorSnapshot
        {
            Resource = new ConnectorResourceInfo { VpnTiStatus = "Offline" },
            Terminals = [new ConnectorCardTerminal { CtId = "CT1", Connected = false }],
            Cards =
            [
                new ConnectorCard
                {
                    CardType = "SMC-B", Iccsn = "1", CertificateExpirationDate = Now.AddDays(20),
                    PinStates = [new ConnectorPinState { PinType = "PIN.SMC", Status = "VERIFIABLE", LeftTries = 3 }]
                },
                new ConnectorCard
                {
                    CardType = "HBA", Iccsn = "2", CertificateExpirationDate = Now.AddDays(-1),
                    PinStates = [new ConnectorPinState { PinType = "PIN.CH", Status = "BLOCKED" }]
                }
            ]
        };

        var keys = ConnectorHealth.Evaluate(snapshot, 60, Now).Select(a => a.Key).ToList();

        Assert.Contains("vpn-ti", keys);
        Assert.Contains("ct-CT1", keys);
        Assert.Contains("smcb-unverified-1", keys);
        Assert.Contains("cert-expiring-1", keys);
        Assert.Contains("cert-expired-2", keys);
        Assert.Contains("pin-blocked-2-PIN.CH", keys);
        Assert.DoesNotContain("no-smcb", keys);
    }

    [Fact]
    public void Missing_smcb_only_reported_when_GetCards_succeeded()
    {
        var ok = new ConnectorSnapshot();
        Assert.Contains(ConnectorHealth.Evaluate(ok, 60, Now), a => a.Key == "no-smcb");

        var failed = new ConnectorSnapshot { Warnings = ["Karten (GetCards): Timeout"] };
        Assert.DoesNotContain(ConnectorHealth.Evaluate(failed, 60, Now), a => a.Key == "no-smcb");
    }
}
