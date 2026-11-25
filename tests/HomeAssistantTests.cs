using TILageMonitor;
using Xunit;

namespace TILageMonitor.Tests;

/// <summary>Zuordnung eines Abrufs auf Home-Assistant-Sensoren (ohne Netz).</summary>
public class HomeAssistantTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 10, 0, 0);

    private static ConnectorSnapshot Snapshot() => new()
    {
        RetrievedAt = Now,
        Resource = new ConnectorResourceInfo { VpnTiStatus = "Online", VpnSisStatus = "Offline" },
        Terminals =
        {
            new ConnectorCardTerminal { CtId = "CT1", Name = "Cherry-AnmLi", Connected = true },
            new ConnectorCardTerminal { CtId = "CT2", Name = "Cherry-AnmRe", Connected = false }
        },
        Cards =
        {
            new ConnectorCard { CardType = "SMC-B", Iccsn = "80276002711611083818", CardHolderName = "Praxis Dr. Müller", CtId = "CT1", SlotId = "4", CertificateExpirationDate = Now.AddDays(1047) },
            new ConnectorCard { CardType = "HBA", Iccsn = "80276001234567890123", CardHolderName = "Thomas Börner", CtId = "CT1", SlotId = "2", CertificateExpirationDate = Now.AddDays(1460) },
            new ConnectorCard { CardType = "SMC-KT", Iccsn = "80276003600000125111", CtId = "CT2", SlotId = "3", CertificateExpirationDate = Now.AddDays(40) },
            new ConnectorCard { CardType = "EGK", Iccsn = "80276883110000000000", CtId = "CT1", SlotId = "1", CertificateExpirationDate = Now.AddDays(5) }
        }
    };

    private static HomeAssistantInput Input(bool reachable = true, ConnectorSnapshot? snapshot = null) =>
        new("Praxis Dr. Müller", reachable, reachable ? snapshot ?? Snapshot() : null, [], "CN=konnektor-client", Now.AddDays(1755), true, reachable ? null : "Zeitüberschreitung", Now);

    [Fact]
    public void Slug_is_ascii_and_stable()
    {
        Assert.Equal("praxis_dr_mueller", HomeAssistantStates.Slug("Praxis Dr. Müller"));
        Assert.Equal("konnektor", HomeAssistantStates.Slug("  "));
        Assert.Equal("sportorthopaedie_karlsruhe", HomeAssistantStates.Slug("Sportorthopädie  Karlsruhe!"));
    }

    [Fact]
    public void Connector_state_becomes_binary_sensors()
    {
        var states = HomeAssistantStates.Build(Input()).ToDictionary(e => e.EntityId);

        Assert.Equal("on", states["binary_sensor.tilage_praxis_dr_mueller_erreichbar"].State);
        Assert.Equal("on", states["binary_sensor.tilage_praxis_dr_mueller_vpn_ti"].State);
        Assert.Equal("off", states["binary_sensor.tilage_praxis_dr_mueller_vpn_sis"].State);
        Assert.Equal("on", states["binary_sensor.tilage_praxis_dr_mueller_live_ereignisse"].State);
        Assert.Equal("1", states["sensor.tilage_praxis_dr_mueller_terminals"].State);
        Assert.Equal("4", states["sensor.tilage_praxis_dr_mueller_karten"].State);
        Assert.Equal("connectivity", states["binary_sensor.tilage_praxis_dr_mueller_vpn_ti"].Attributes["device_class"]);
    }

    [Fact]
    public void Expiry_sensors_cover_smcb_hba_smckt_and_client_but_not_egk()
    {
        var expiry = HomeAssistantStates.Build(Input()).Where(e => e.EntityId.EndsWith("_ablauf", StringComparison.Ordinal)).ToList();

        Assert.Equal(4, expiry.Count);
        Assert.Contains(expiry, e => e.EntityId == "sensor.tilage_praxis_dr_mueller_smcb_083818_ablauf" && e.State == "1047");
        Assert.Contains(expiry, e => e.EntityId == "sensor.tilage_praxis_dr_mueller_hba_890123_ablauf" && e.State == "1460");
        var kt = Assert.Single(expiry, e => e.EntityId == "sensor.tilage_praxis_dr_mueller_smckt_125111_ablauf");
        Assert.Equal("40", kt.State);
        Assert.Equal("Cherry-AnmRe", kt.Attributes["terminal"]);
        Assert.Equal("d", kt.Attributes["unit_of_measurement"]);
        Assert.Contains(expiry, e => e.EntityId == "sensor.tilage_praxis_dr_mueller_clientsystem_ablauf");
    }

    [Fact]
    public void Unreachable_connector_reports_off_and_unknown()
    {
        var states = HomeAssistantStates.Build(Input(reachable: false)).ToDictionary(e => e.EntityId);

        Assert.Equal("off", states["binary_sensor.tilage_praxis_dr_mueller_erreichbar"].State);
        Assert.Equal("unknown", states["binary_sensor.tilage_praxis_dr_mueller_vpn_ti"].State);
        Assert.Equal("nicht_erreichbar", states["sensor.tilage_praxis_dr_mueller_status"].State);
        Assert.DoesNotContain(states.Keys, k => k.EndsWith("_ablauf", StringComparison.Ordinal));
    }

    [Fact]
    public void Failed_card_query_reports_unknown_instead_of_card_removed()
    {
        var snapshot = Snapshot();
        snapshot.Cards.Clear();
        snapshot.Warnings.Add("Karten (GetCards): Zeitüberschreitung");
        var states = HomeAssistantStates.Build(Input(snapshot: snapshot)).ToDictionary(e => e.EntityId);

        Assert.Equal("unknown", states["binary_sensor.tilage_praxis_dr_mueller_smcb_gesteckt"].State);
        Assert.Equal("unknown", states["sensor.tilage_praxis_dr_mueller_karten"].State);
    }

    [Fact]
    public void Rejected_query_is_not_reported_as_ok()
    {
        var input = new HomeAssistantInput("Praxis", true, null, [], null, null, null, "4204 – Clientsystem nicht authentifiziert", Now);
        var status = HomeAssistantStates.Build(input).Single(e => e.Key == "status");

        Assert.Equal("abfrage_abgelehnt", status.State);
    }

    [Fact]
    public void Expiry_sensors_survive_an_outage_from_last_known_data()
    {
        var withLast = new HomeAssistantInput("Praxis Dr. Müller", false, null, [], null, null, null, "weg", Now) { LastKnownSnapshot = Snapshot() };
        var expiry = HomeAssistantStates.Build(withLast).Where(e => e.Key.EndsWith("_ablauf", StringComparison.Ordinal)).ToList();

        Assert.Equal(3, expiry.Count);
        Assert.Equal("off", HomeAssistantStates.Build(withLast).Single(e => e.Key == "erreichbar").State);
    }

    [Fact]
    public void Json_contains_state_and_skips_null_attributes()
    {
        var entity = new HomeAssistantEntity("sensor.x", "12", new Dictionary<string, object?> { ["unit_of_measurement"] = "d", ["iccsn"] = null });
        var json = HomeAssistantPublisher.ToJson(entity);

        Assert.Contains("\"state\":\"12\"", json);
        Assert.Contains("\"unit_of_measurement\":\"d\"", json);
        Assert.DoesNotContain("iccsn", json);
    }

    [Fact]
    public void State_uri_accepts_bare_host_and_api_suffix()
    {
        Assert.Equal("http://ha.local:8123/api/states/sensor.x", HomeAssistantPublisher.BuildStateUri("ha.local:8123", "sensor.x").ToString());
        Assert.Equal("https://abc.ui.nabu.casa/api/states/sensor.x", HomeAssistantPublisher.BuildStateUri("https://abc.ui.nabu.casa/api/", "sensor.x").ToString());
    }
}

/// <summary>MQTT-Discovery: Geraet je Praxis, feste unique_id, "None" fuer unbekannt.</summary>
public class HomeAssistantMqttTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 10, 0, 0);

    private static HomeAssistantInput Input(bool reachable = true) =>
        new("Praxis Dr. Müller", reachable, reachable ? new ConnectorSnapshot
        {
            RetrievedAt = Now,
            Resource = new ConnectorResourceInfo { VpnTiStatus = "Online", VpnSisStatus = "Offline" },
            Cards = { new ConnectorCard { CardType = "SMC-B", Iccsn = "80276002711611083818", CardHolderName = "Praxis", CtId = "CT1", CertificateExpirationDate = Now.AddDays(100) } }
        } : null, [], null, null, true, null, Now) { Machine = "PRAXIS-PC", AppVersion = "3.3.0" };

    [Fact]
    public void Discovery_groups_entities_under_one_device_per_practice()
    {
        var entity = HomeAssistantStates.Build(Input()).Single(e => e.Key == "vpn_ti");
        var directory = new ConnectorServiceDirectory { ProductVendorName = "RISE", ProductName = "Konnektor", FirmwareVersion = "5.1.2" };
        var message = HomeAssistantMqtt.Build(entity, "praxis_dr_mueller", "Praxis Dr. Müller", directory, 900);

        Assert.Equal("homeassistant/binary_sensor/tilage_praxis_dr_mueller_vpn_ti/config", message.ConfigTopic);
        Assert.Equal("tilage/praxis_dr_mueller/vpn_ti/state", message.StateTopic);
        Assert.Equal("on", message.StatePayload);
        var config = System.Text.Json.Nodes.JsonNode.Parse(message.ConfigJson)!;
        Assert.Equal("VPN TI", (string?)config["name"]);
        Assert.Equal("tilage_praxis_dr_mueller_vpn_ti", (string?)config["unique_id"]);
        Assert.Equal("connectivity", (string?)config["device_class"]);
        Assert.Equal("tilage/praxis_dr_mueller/availability", (string?)config["availability_topic"]);
        Assert.Equal("tilage_praxis_dr_mueller", (string?)config["device"]!["identifiers"]![0]);
        Assert.Equal("5.1.2", (string?)config["device"]!["sw_version"]);
    }

    [Fact]
    public void Unknown_state_becomes_none_and_units_move_into_config()
    {
        var entities = HomeAssistantStates.Build(Input(reachable: false));
        var vpn = HomeAssistantMqtt.Build(entities.Single(e => e.Key == "vpn_ti"), "x", "X", null, 900);
        Assert.Equal("None", vpn.StatePayload);

        var expiry = HomeAssistantMqtt.Build(HomeAssistantStates.Build(Input()).Single(e => e.Key.EndsWith("_ablauf", StringComparison.Ordinal) && e.Key.StartsWith("smcb", StringComparison.Ordinal)), "x", "X", null, 900);
        var config = System.Text.Json.Nodes.JsonNode.Parse(expiry.ConfigJson)!;
        Assert.Equal("d", (string?)config["unit_of_measurement"]);
        Assert.DoesNotContain("unit_of_measurement", expiry.AttributesJson);
        Assert.Contains("gueltig_bis", expiry.AttributesJson);
    }

    [Fact]
    public void Heartbeat_carries_machine_and_never_expires()
    {
        var heartbeat = HomeAssistantStates.Build(Input(reachable: false)).Single(e => e.Key == "app_lebenszeichen");
        Assert.Equal("PRAXIS-PC", heartbeat.Attributes["rechner"]);
        var message = HomeAssistantMqtt.Build(heartbeat, "x", "X", null, 900);
        Assert.DoesNotContain("expire_after", message.ConfigJson);
    }

    [Fact]
    public void Connect_packet_carries_will_and_credentials()
    {
        var packet = MqttConnection.BuildConnect(new MqttOptions("h", 1883, false, "user", "pw", "client", "tilage/x/availability", "offline", TimeSpan.FromSeconds(60)));
        Assert.Equal(0x10, packet[0]);
        // Flags: Username, Passwort, Will-Retain, Will, Clean Session
        Assert.Equal(0x80 | 0x40 | 0x20 | 0x04 | 0x02, packet[9]);
        Assert.Equal(60, (packet[10] << 8) | packet[11]);
    }
}

public class ConnectorLastHoursTests
{
    [Fact]
    public void Last_hours_end_with_current_hour_and_mark_gaps()
    {
        var now = new DateTime(2026, 11, 25, 10, 30, 0);
        var file = new ConnectorHistoryFile();
        ConnectorHistoryStore.ApplyUnreachable(file, now);
        ConnectorHistoryStore.ApplyUnreachable(file, now.AddHours(-2));
        file.Hours.Single(h => h.HourKey.Contains("-08", StringComparison.Ordinal)).Polls = 3;

        var hours = ConnectorHistoryTimeline.LastHours(file, now);
        var reach = hours[ConnectorHistoryLane.Reachable];

        Assert.Equal(24, reach.Length);
        Assert.Equal(ConnectorHistoryState.Down, reach[23].State);
        Assert.Equal(ConnectorHistoryState.NoData, reach[22].State);
        Assert.Equal(ConnectorHistoryState.Partial, reach[21].State);
        Assert.Equal(37.5, ConnectorHistoryTimeline.Availability(reach));
    }
}
