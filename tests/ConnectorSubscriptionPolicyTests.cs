using System.Net;
using Xunit;

namespace TILageMonitor.Tests;

public sealed class ConnectorSubscriptionPolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 22, 12, 0, 0);

    private static ConnectorSubscription Sub(string id, DateTime? until) =>
        new() { SubscriptionId = id, TerminationTime = until, EventTo = "cetp://10.0.0.8:48150", Topic = ConnectorTopics.CardInserted };

    [Fact]
    public void First_attempt_subscribes()
    {
        Assert.Equal(ConnectorSubscriptionPolicy.Action.Subscribe,
            ConnectorSubscriptionPolicy.Decide(false, false, true, [], Now));
    }

    [Fact]
    public void Firmware_without_GetSubscription_is_not_retried_endlessly()
    {
        Assert.Equal(ConnectorSubscriptionPolicy.Action.GiveUp,
            ConnectorSubscriptionPolicy.Decide(subscriptionAttempted: true, previouslySucceeded: false, getSubscriptionSucceeded: false, [], Now));
    }

    [Fact]
    public void Failed_first_subscribe_with_working_GetSubscription_is_not_retried()
    {
        Assert.Equal(ConnectorSubscriptionPolicy.Action.GiveUp,
            ConnectorSubscriptionPolicy.Decide(true, false, true, [], Now));
    }

    [Fact]
    public void Alive_subscription_far_from_expiry_is_left_alone()
    {
        var ours = new[] { Sub("s1", Now.AddHours(20)) };
        Assert.Equal(ConnectorSubscriptionPolicy.Action.None,
            ConnectorSubscriptionPolicy.Decide(true, true, true, ours, Now));
    }

    [Fact]
    public void Subscription_is_renewed_before_the_25_hour_expiry()
    {
        var ours = new[] { Sub("s1", Now.AddHours(11)) };
        Assert.Equal(ConnectorSubscriptionPolicy.Action.Renew,
            ConnectorSubscriptionPolicy.Decide(true, true, true, ours, Now));
        Assert.True(Now.AddHours(11) - Now <= ConnectorSubscriptionPolicy.RenewLead);
    }

    [Fact]
    public void Empty_list_after_prior_success_resubscribes()
    {
        // Regression: die Bedingung war invertiert und hat nach dem ersten Erfolg nie wieder abonniert.
        Assert.Equal(ConnectorSubscriptionPolicy.Action.Subscribe,
            ConnectorSubscriptionPolicy.Decide(true, previouslySucceeded: true, getSubscriptionSucceeded: true, [], Now));
    }

    [Fact]
    public void Expired_subscriptions_after_prior_success_resubscribe()
    {
        var ours = new[] { Sub("s1", Now.AddMinutes(-1)) };
        Assert.Equal(ConnectorSubscriptionPolicy.Action.Subscribe,
            ConnectorSubscriptionPolicy.Decide(true, true, true, ours, Now));
    }
}

public sealed class ConnectorTopicsAndCardModelTests
{
    [Fact]
    public void Official_topics_use_the_slash_tree_from_the_spec()
    {
        Assert.Equal("CARD/INSERTED", ConnectorTopics.CardInserted);
        Assert.Equal("CARD/REMOVED", ConnectorTopics.CardRemoved);
        Assert.Equal("CT/CONNECTED", ConnectorTopics.TerminalConnected);
        Assert.Equal("CT/DISCONNECTED", ConnectorTopics.TerminalDisconnected);
        Assert.Equal("CERT/CARD/STATUS", ConnectorTopics.CertCardStatus);
        Assert.True(ConnectorTopics.IsCardInserted("CARD/INSERTED"));
        Assert.False(ConnectorTopics.IsCardInserted("CARD/REMOVED"));
        Assert.Contains(ConnectorTopics.CardInserted, ConnectorTopics.DefaultSubscriptions);
    }

    [Theory]
    [InlineData("SMC-B")]
    [InlineData("SM-B")]
    [InlineData("HSM-B")]
    public void Sm_b_and_hsm_b_are_treated_as_smc_b(string type)
    {
        var card = new ConnectorCard { CardType = type, CardHolderName = "Praxis" };
        Assert.True(card.IsSmcB);
        Assert.Equal(["PIN.SMC"], card.PinTypes);
        Assert.True(card.SupportsPinManagement);
        Assert.Contains("SMC-B", card.DisplayName + "HSM-B"); // SM-B zeigt als SMC-B, HSM-B bleibt HSM-B
        if (type == "HSM-B")
            Assert.StartsWith("HSM-B", card.DisplayName);
        else
            Assert.StartsWith("SMC-B", card.DisplayName);
    }

    [Fact]
    public void Hba_qsig_offers_only_pin_qes()
    {
        var card = new ConnectorCard { CardType = "HBA-qSig" };
        Assert.True(card.IsHbaPredecessor);
        Assert.False(card.IsHba);
        Assert.Equal(["PIN.QES"], card.PinTypes);
        Assert.DoesNotContain("PIN.CH", card.PinTypes);
    }

    [Fact]
    public void Enable_and_disable_pin_require_card_service_8_2()
    {
        var dir = new ConnectorServiceDirectory
        {
            Services =
            [
                new ConnectorServiceEndpoint { Name = "CardService", Version = "8.1.0" }
            ]
        };
        Assert.False(dir.SupportsEnableDisablePin);

        dir.Services[0].Version = "8.2";
        Assert.True(dir.SupportsEnableDisablePin);

        dir.Services[0].Version = "8.2.1";
        Assert.True(dir.SupportsEnableDisablePin);
    }
}

public sealed class ConnectorEventToTests
{
    [Fact]
    public void Ipv4_and_ipv6_are_formatted_as_cetp_urls()
    {
        Assert.Equal("cetp://192.168.1.20:48150", ConnectorEventTo.Format(IPAddress.Parse("192.168.1.20"), 48150));
        Assert.Equal("cetp://[fe80::1]:48150", ConnectorEventTo.Format(IPAddress.Parse("fe80::1"), 48150));
    }

    [Fact]
    public void Only_the_connector_address_is_an_allowed_peer()
    {
        var allowed = new[] { IPAddress.Parse("10.1.2.3") };
        Assert.True(ConnectorEventTo.IsAllowedPeer(new IPEndPoint(IPAddress.Parse("10.1.2.3"), 1234), allowed));
        Assert.False(ConnectorEventTo.IsAllowedPeer(new IPEndPoint(IPAddress.Parse("10.9.9.9"), 1234), allowed));
        Assert.True(ConnectorEventTo.IsAllowedPeer(
            new IPEndPoint(IPAddress.Parse("::ffff:10.1.2.3"), 9), allowed));
    }

    [Fact]
    public void Host_with_port_is_resolved_to_the_literal_address()
    {
        var ips = ConnectorEventTo.ResolveHost("10.0.0.5:8443");
        Assert.Equal(IPAddress.Parse("10.0.0.5"), Assert.Single(ips));
    }
}

public sealed class ConnectorCapabilitiesOmitUserIdTests
{
    [Fact]
    public void Omit_user_id_is_cached_per_host_and_forgotten_with_the_rest()
    {
        var host = "omit-user-id-test.example";
        ConnectorCapabilities.Forget(host);
        Assert.Null(ConnectorCapabilities.GetOmitUserId(host));
        ConnectorCapabilities.SetOmitUserId(host, true);
        Assert.True(ConnectorCapabilities.GetOmitUserId(host));
        ConnectorCapabilities.Forget(host);
        Assert.Null(ConnectorCapabilities.GetOmitUserId(host));
    }
}
