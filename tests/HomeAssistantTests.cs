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
