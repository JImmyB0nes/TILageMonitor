using Xunit;

namespace TILageMonitor.Tests;

/// <summary>Konnektor-Karte im Hauptfenster: eingerichtet = immer sichtbar ohne X,
/// nicht eingerichtet = sichtbar mit X, bis sie ausgeblendet wird.</summary>
public sealed class ConnectorCardVisibilityTests
{
    private static ConnectorSettings Configured(bool show = true) => new()
    {
        Enabled = true,
        Host = "10.0.0.1",
        MandantId = "M",
        ClientSystemId = "C",
        WorkplaceId = "W",
        ShowMainWindowCard = show
    };

    [Fact]
    public void Unconfigured_card_is_shown_with_close_button_by_default()
    {
        var settings = new ConnectorSettings();
        Assert.True(ConnectorCardVisibility.ShouldShow(settings));
        Assert.True(ConnectorCardVisibility.ShowCloseButton(settings));
    }

    [Fact]
    public void Unconfigured_card_stays_hidden_after_dismissal()
    {
        var settings = new ConnectorSettings { ShowMainWindowCard = false };
        Assert.False(ConnectorCardVisibility.ShouldShow(settings));
    }

    [Fact]
    public void Configured_card_is_always_shown_without_close_button()
    {
        Assert.True(ConnectorCardVisibility.ShouldShow(Configured(show: false)));
        Assert.False(ConnectorCardVisibility.ShowCloseButton(Configured()));
    }

    [Fact]
    public void Disabled_or_incomplete_connector_counts_as_not_set_up()
    {
        var disabled = Configured(show: false);
        disabled.Enabled = false;
        Assert.False(ConnectorCardVisibility.ShouldShow(disabled));
        Assert.True(ConnectorCardVisibility.ShowCloseButton(disabled));

        var incomplete = Configured(show: false);
        incomplete.WorkplaceId = "";
        Assert.False(ConnectorCardVisibility.ShouldShow(incomplete));
    }
}
