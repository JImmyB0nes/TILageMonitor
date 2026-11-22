using System.Xml.Linq;
using Xunit;

namespace TILageMonitor.Tests;

/// <summary>
/// Karte rein/raus: dieselbe Anzeige, zwei Ursprunge. Was daraus folgt, ist hier nachlesbar –
/// insbesondere dass ein unbekanntes Topic die Anzeige nicht auf "raus" wirft, sondern beim
/// letzten belastbaren Stand bleibt.
/// </summary>
public sealed class ConnectorPresenceTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 14, 30, 0);

    private static ConnectorEvent Event(string topic, params (string Key, string Value)[] parameters)
    {
        var evt = new ConnectorEvent { Topic = topic, ReceivedAt = Now };
        foreach (var (key, value) in parameters)
            evt.Parameters[key] = value;
        return evt;
    }

    [Fact]
    public void CardInserted_event_reads_type_and_terminal()
    {
        var presence = ConnectorPresence.FromEvent(Event(
            ConnectorTopics.CardInserted,
            ("CardType", "SMC-B"), ("CtId", "CT1")));

        Assert.NotNull(presence);
        Assert.True(presence!.Inserted);
        Assert.True(presence.ViaEvent);
        Assert.Equal("SMC-B · CT1", presence.DetailText);
    }

    [Fact]
    public void CardRemoved_event_is_read_as_out()
    {
        var presence = ConnectorPresence.FromEvent(Event(
            ConnectorTopics.CardRemoved));

        Assert.NotNull(presence);
        Assert.False(presence!.Inserted);
    }

    [Fact]
    public void An_unrelated_topic_leaves_the_display_where_it_was()
    {
        // StatusChanged oder ein Firmware-Thema darf nicht als Kartensignal gedeutet werden.
        Assert.Null(ConnectorPresence.FromEvent(Event("http://ws.gematik.de/conn/EventService/v7.2#StatusChanged")));
        Assert.Null(ConnectorPresence.FromEvent(Event(ConnectorTopics.TerminalConnected)));
        Assert.Null(ConnectorPresence.FromEvent(Event(ConnectorTopics.CertCardStatus)));
        Assert.Null(ConnectorPresence.FromEvent(null));
    }

    [Fact]
    public void Official_and_legacy_card_topics_are_recognized()
    {
        Assert.True(ConnectorPresence.FromEvent(Event("CARD/INSERTED"))!.Inserted);
        Assert.False(ConnectorPresence.FromEvent(Event("CARD/REMOVED"))!.Inserted);
        Assert.True(ConnectorPresence.FromEvent(Event("http://ws.gematik.de/conn/EventService/v7.2#CardInserted"))!.Inserted);
        Assert.False(ConnectorPresence.FromEvent(Event("#CardRemoved"))!.Inserted);
    }

    [Fact]
    public void A_snapshot_without_cards_says_out_instead_of_nothing()
    {
        var presence = ConnectorPresence.FromSnapshot(new ConnectorSnapshot { RetrievedAt = Now });

        Assert.NotNull(presence);
        Assert.False(presence!.Inserted);
        Assert.False(presence.ViaEvent);
    }

    [Fact]
    public void A_snapshot_prefers_the_smc_b_over_another_card()
    {
        var snapshot = new ConnectorSnapshot
        {
            RetrievedAt = Now,
            Cards =
            [
                new ConnectorCard { CardType = "EGK", CtId = "CT2" },
                new ConnectorCard { CardType = "SMC-B", CtId = "CT1" }
            ]
        };

        var presence = ConnectorPresence.FromSnapshot(snapshot);

        Assert.NotNull(presence);
        Assert.True(presence!.Inserted);
        Assert.Equal("SMC-B", presence.CardType);
        Assert.Equal("CT1", presence.CtId);
    }

    [Fact]
    public void Only_an_event_keeps_the_display_live()
    {
        var fromEvent = ConnectorPresence.FromEvent(Event("#CardInserted"))!;
        var fromPoll = ConnectorPresence.FromSnapshot(new ConnectorSnapshot { RetrievedAt = Now })!;

        Assert.True(fromEvent.IsLive(Now));
        // Ein Stand aus dem Pollen ist nie "gerade eben" – sonst wurde der Unterschied
        // zwischen Abo und Pollen wieder unsichtbar, um dessen Willen die Anzeige gebaut wurde.
        Assert.False(fromPoll.IsLive(Now));

        // Nach dem Zeitfenster gilt auch das Ereignis nicht mehr als frisch.
        Assert.False(fromEvent.IsLive(Now.AddSeconds(ConnectorPresence.LiveWindowSeconds + 1)));
    }

    [Fact]
    public void Age_reads_differently_for_the_two_sources()
    {
        // Die Altersangabe wird je Oberfachensprache anders formuliert, verglichen wird
        // deshalb gegen dieselbe Verzweigung – sonst kippt der Test je nach Voreinstellung.
        var fromEvent = ConnectorPresence.FromEvent(Event("#CardInserted"))!;
        Assert.Equal(LocalizationService.Pick("vor 0 s", "0 s ago"), fromEvent.AgeText(Now));
        Assert.Equal(LocalizationService.Pick("vor 5 min", "5 min ago"), fromEvent.AgeText(Now.AddMinutes(5)));
        Assert.Equal(LocalizationService.Pick("vor 2 h", "2 h ago"), fromEvent.AgeText(Now.AddHours(2)));

        var fromPoll = ConnectorPresence.FromSnapshot(new ConnectorSnapshot { RetrievedAt = Now })!;
        // Ohne Abo ist das Alter bloß das Alter des Datenbestands, nie "gerade eben".
        Assert.Contains(LocalizationService.Pick("Stand", "as of"), fromPoll.AgeText(Now.AddMinutes(30)));
    }

    [Fact]
    public void An_unanswered_GetCards_leaves_the_display_silent()
    {
        // Leere Kartenliste und fehlgeschlagenes GetCards sehen gleich aus, sind aber
        // verschiedenes: das eine ist eine Aussage, das andere eine Unterlassung.
        var empty = new ConnectorSnapshot { RetrievedAt = Now };
        Assert.NotNull(ConnectorPresence.FromSnapshot(empty));

        var failed = new ConnectorSnapshot
        {
            RetrievedAt = Now,
            Warnings = ["Karten (GetCards): The request was canceled"]
        };
        Assert.Null(ConnectorPresence.FromSnapshot(failed));
    }

    [Fact]
    public void Disabled_pin_is_reported_as_a_deviation_not_as_an_error()
    {
        var snapshot = new ConnectorSnapshot
        {
            Resource = new ConnectorResourceInfo { VpnTiStatus = "Online" },
            Cards =
            [
                new ConnectorCard
                {
                    CardType = "SMC-B", Iccsn = "1",
                    PinStates = [new ConnectorPinState { PinType = "PIN.SMC", Status = "DISABLED" }]
                }
            ]
        };

        var alerts = ConnectorHealth.Evaluate(snapshot, 60, Now);
        var alert = Assert.Single(alerts, a => a.Key == "pin-disabled-1-PIN.SMC");
        Assert.Equal(ToastUrgency.Warning, alert.Urgency);
    }

    [Fact]
    public void Subscriptions_keep_their_target_and_end_of_validity()
    {
        // Dieselbe Angabe, die jetzt in der Verbindungsmaske steht, statt nur im Bericht.
        var xml = """
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
            <EVT:GetSubscriptionResponse xmlns:EVT="http://ws.gematik.de/conn/EventService/v7.2"
                                         xmlns:CONN="http://ws.gematik.de/conn/ConnectorCommon/v5.0">
              <CONN:Status><CONN:Result>OK</CONN:Result></CONN:Status>
              <EVT:Subscriptions>
                <EVT:Subscription>
                  <EVT:SubscriptionID>sub-1</EVT:SubscriptionID>
                  <EVT:TerminationTime>2026-11-21T09:00:00Z</EVT:TerminationTime>
                  <EVT:EventTo>cetp://192.168.1.20:48150</EVT:EventTo>
                  <EVT:Topic>CARD/INSERTED</EVT:Topic>
                </EVT:Subscription>
                <EVT:Subscription>
                  <EVT:SubscriptionID>sub-2</EVT:SubscriptionID>
                  <EVT:EventTo>cetp://192.168.1.20:48150</EVT:EventTo>
                  <EVT:Topic>http://ws.gematik.de/conn/EventService/v7.2#CardTerminalDisconnected</EVT:Topic>
                </EVT:Subscription>
              </EVT:Subscriptions>
            </EVT:GetSubscriptionResponse>
              </soap:Body>
            </soap:Envelope>
            """;

        var subs = ConnectorProtocol.ParseSubscriptions(ConnectorProtocol.ParseBody(xml));

        Assert.Equal(2, subs.Count);
        // Ein Abonnement ohne Ablaufzeit darf die Anzeige nicht mit "bis " stehen lassen.
        Assert.NotNull(subs[0].TerminationTime);
        Assert.Null(subs[1].TerminationTime);
        Assert.Equal("cetp://192.168.1.20:48150", subs[1].EventTo);
    }

    [Fact]
    public void Unsubscribe_names_the_subscription_to_cancel()
    {
        XNamespace evt = "http://ws.gematik.de/conn/EventService/v7.2";
        var settings = new ConnectorSettings { Host = "192.168.1.10", MandantId = "M1", ClientSystemId = "C1", WorkplaceId = "A1" };

        var element = ConnectorProtocol.Unsubscribe(evt, settings, " sub-1 ");

        Assert.Equal("sub-1", element.Element(evt + "SubscriptionID")!.Value);
        Assert.NotNull(element.Element(ConnectorProtocol.Cctx + "Context"));
    }
}
