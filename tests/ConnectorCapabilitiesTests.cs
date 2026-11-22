using Xunit;

namespace TILageMonitor.Tests;

/// <summary>
/// Was die App sich ueber einen Konnektor merkt, und was daraus folgt, wenn sie es tut.
/// Die Kante ist nicht das Speichern, sondern die Unterscheidung zwischen "der Konnektor hat
/// nichts geliefert" und "der Konnektor hat nichts mehr". Ersteres haelt man, letzteres
/// erneuert man – verwechselt man beides, pollt die App dauerhaft schlechter als ohne Abo.
/// </summary>
public sealed class ConnectorCapabilitiesTests
{
    // Jede Probe mit eigener Adresse, damit kein Fall vom Nachbarn abhaengt. Das Geaechtnis
    // ist lauffaellig geteilt, und ein Ruecksetzen um der Tests willen waer eine API, die der
    // Betrieb nie braucht. Fest geloeset, weil ein Hashwert hier pro Lauf faellt – dieselbe
    // Adresse koennte mal der eine, mal der andere Fall sein.
    private const string UnseenHost = "10.20.1.1";
    private const string KnownHost = "10.20.1.2";
    private const string ForgottenHost = "10.20.1.3";

    [Fact]
    public void An_unseen_connector_gets_the_radius_the_spec_prefers_first()
    {
        var attempts = ConnectorCapabilities.MandantWideAttempts(ConnectorCapabilities.GetMandantWide(UnseenHost));

        Assert.Equal(new bool[] { true, false }, attempts);
    }

    [Fact]
    public void A_known_connector_is_asked_only_the_way_that_worked()
    {
        var host = KnownHost;
        ConnectorCapabilities.SetMandantWide(host, false);

        // Zwei Roundtrips pro Poll, obwohl die Antwort seit Wochen feststeht, war der
        // fruehere Zustand. Ein Konnektor, der auf "true" bestaendig mit 4000 antwortet,
        // haelt sich so nicht selbst fuer einen Fehler.
        Assert.Equal([false], ConnectorCapabilities.MandantWideAttempts(ConnectorCapabilities.GetMandantWide(host)));
    }

    [Fact]
    public void Forgetting_a_connector_opens_the_question_again()
    {
        var host = ForgottenHost;
        ConnectorCapabilities.SetMandantWide(host, true);
        ConnectorCapabilities.SetEventNamespace(host, "http://ws.gematik.de/conn/EventService/v7.3");

        ConnectorCapabilities.Forget(host);

        Assert.Null(ConnectorCapabilities.GetMandantWide(host));
        Assert.Null(ConnectorCapabilities.GetEventNamespace(host));
        Assert.Equal(new bool[] { true, false }, ConnectorCapabilities.MandantWideAttempts(ConnectorCapabilities.GetMandantWide(host)));
    }

    /// <summary>
    /// Ein Konnektor, der nur die Hauptversion nennt, war vorher als "4.." zu lesen – ein
    /// Wert, der wie eine Beschädigung aussieht, wo der Konnektor nur duenner liefert.
    /// </summary>
    [Fact]
    public void A_partial_cos_version_does_not_leave_dangling_dots()
    {
        var xml = $"""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <GetCardsResponse xmlns="http://ws.gematik.de/conn/EventService/v7.2">
                  <Cards xmlns="http://ws.gematik.de/conn/CardService/v8.1">
                    <Card xmlns="http://ws.gematik.de/conn/CardService/v8.1">
                      <CardHandle>K1</CardHandle>
                      <CardType xmlns="http://ws.gematik.de/conn/CardServiceCommon/v2.0">SMC-B</CardType>
                      <CardVersion xmlns="http://ws.gematik.de/conn/CardService/v8.1">
                        <COSVersion><Major>4</Major></COSVersion>
                      </CardVersion>
                    </Card>
                  </Cards>
                </GetCardsResponse>
              </soap:Body>
            </soap:Envelope>
            """;

        var cards = ConnectorProtocol.ParseCards(ConnectorProtocol.ParseBody(xml));

        Assert.Equal("4", cards[0].CardVersion);
    }

    [Fact]
    public void A_version_carried_under_a_different_branch_still_reads()
    {
        var xml = $"""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <GetCardsResponse xmlns="http://ws.gematik.de/conn/EventService/v7.2">
                  <Cards xmlns="http://ws.gematik.de/conn/CardService/v8.1">
                    <Card xmlns="http://ws.gematik.de/conn/CardService/v8.1">
                      <CardHandle>K2</CardHandle>
                      <CardType xmlns="http://ws.gematik.de/conn/CardServiceCommon/v2.0">SMC-B</CardType>
                      <CardVersion xmlns="http://ws.gematik.de/conn/CardService/v8.1">
                        <Version><Major>3</Major><Minor>1</Minor><Revision>2</Revision></Version>
                      </CardVersion>
                    </Card>
                  </Cards>
                </GetCardsResponse>
              </soap:Body>
            </soap:Envelope>
            """;

        var cards = ConnectorProtocol.ParseCards(ConnectorProtocol.ParseBody(xml));

        Assert.Equal("3.1.2", cards[0].CardVersion);
    }

    [Fact]
    public void A_card_without_version_information_stays_empty_not_zeroed()
    {
        var xml = $"""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <GetCardsResponse xmlns="http://ws.gematik.de/conn/EventService/v7.2">
                  <Cards xmlns="http://ws.gematik.de/conn/CardService/v8.1">
                    <Card xmlns="http://ws.gematik.de/conn/CardService/v8.1">
                      <CardHandle>K3</CardHandle>
                      <CardType xmlns="http://ws.gematik.de/conn/CardServiceCommon/v2.0">SMC-B</CardType>
                    </Card>
                  </Cards>
                </GetCardsResponse>
              </soap:Body>
            </soap:Envelope>
            """;

        var cards = ConnectorProtocol.ParseCards(ConnectorProtocol.ParseBody(xml));

        Assert.Equal("", cards[0].CardVersion);
    }

    [Fact]
    public void A_topic_is_built_from_the_namespace_that_answered()
    {
        var topic = ConnectorProtocol.EventTopic("http://ws.gematik.de/conn/EventService/v7.3", "CardInserted");

        Assert.Equal("http://ws.gematik.de/conn/EventService/v7.3#CardInserted", topic);

        // Ein Namespace, der schon einen Gedankenstrich mitbringt, fuehrt sonst zu einer
        // Doppelmarke, die der Konnektor als unbekaanues Topic quittiert.
        Assert.Equal("http://example#CardRemoved", ConnectorProtocol.EventTopic("http://example#", "CardRemoved"));
    }
}

/// <summary>
/// Ob ein Abo noch steht, und wem es gehoert. Beides wird getrennt vom Erfolg des
/// Subscribe-Aufrufs – der ist nach einer Woche das Einzige, was die App noch zu wissen glaubt.
/// </summary>
public sealed class ConnectorSubscriptionTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 12, 0, 0);

    [Fact]
    public void A_subscription_past_its_end_is_no_longer_alive()
    {
        var expired = new ConnectorSubscription { TerminationTime = Now.AddMinutes(-1) };
        var running = new ConnectorSubscription { TerminationTime = Now.AddMinutes(1) };

        Assert.False(expired.Alive(Now));
        Assert.True(running.Alive(Now));
    }

    /// <summary>
    /// Nicht jede Firmware nennt die Ablaufzeit. Sie alle als abgelaufen zu melden, hiesse,
    /// die App wuerde bei diesen Konnektoren dauerhaft behaupten, ihr fehle das Abo.
    /// </summary>
    [Fact]
    public void A_subscription_without_end_time_counts_as_open()
    {
        Assert.True(new ConnectorSubscription().Alive(Now));
    }

    /// <summary>
    /// Nur exakt dieselbe Adresse (Rechner UND Port) ist das eigene Abo. Ein Teilstring traf
    /// frueher auch 192.168.1.10 fuer 192.168.1.1 – "Abos kuendigen" loeschte dann fremde Abos.
    /// </summary>
    [Theory]
    [InlineData("cetp://192.168.1.10:48150", false)] // Nachbar mit laengerer IP
    [InlineData("cetp://192.168.1.1:48151", false)]  // andere Sitzung / anderer Port
    [InlineData("cetp://192.168.1.1:48150", true)]
    [InlineData("CETP://192.168.1.1:48150/", true)]
    [InlineData("cetp://192.168.1.100:48150", false)]
    public void Only_the_exact_target_is_this_app(string eventTo, bool expected)
    {
        var sub = new ConnectorSubscription { EventTo = eventTo };

        Assert.Equal(expected, sub.TargetsExactly("cetp://192.168.1.1:48150"));
    }

    [Fact]
    public void A_blind_listener_owns_nothing()
    {
        Assert.Null(ConnectorSubscription.HostOf(null));
        Assert.Null(ConnectorSubscription.HostOf("cetp://"));
        Assert.False(new ConnectorSubscription { EventTo = "cetp://192.168.1.20:48150" }.TargetsExactly(null));
        Assert.False(new ConnectorSubscription { EventTo = "cetp://192.168.1.20" }.TargetsExactly("cetp://192.168.1.20"));
    }

    [Fact]
    public void A_bracketed_host_keeps_its_own_shape()
    {
        Assert.Equal("[2001:db8::20]", ConnectorSubscription.HostOf("cetp://[2001:db8::20]:48150"));
        Assert.True(new ConnectorSubscription { EventTo = "cetp://[2001:db8::20]:48150" }.TargetsExactly("cetp://[2001:db8::20]:48150"));
        Assert.False(new ConnectorSubscription { EventTo = "cetp://[2001:db8::20]:48151" }.TargetsExactly("cetp://[2001:db8::20]:48150"));
        Assert.False(new ConnectorSubscription { EventTo = "cetp://[2001:db8::200]:48150" }.TargetsExactly("cetp://[2001:db8::20]:48150"));
    }
}
