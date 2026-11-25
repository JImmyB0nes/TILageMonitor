using Xunit;

namespace TILageMonitor.Tests;

/// <summary>
/// Der Hintergrund-Poll liest keine Kartenzertifikate. Das Fenster muss die zuvor gelesenen
/// Zertifikate derselben Karte in den neuen Stand übernehmen – sonst verschwinden sie samt
/// OCSP-Knopf beim nächsten Poll.
/// </summary>
public sealed class ConnectorWindowCarryOverTests
{
    private static ConnectorCertificateInfo Cert(string reference) =>
        new() { CertRef = reference, NotAfter = new DateTime(2030, 1, 1) };

    [Fact]
    public void Certificates_of_the_same_card_survive_a_background_poll()
    {
        var aut = Cert("C.AUT");
        var previous = new ConnectorSnapshot();
        previous.Cards.Add(new ConnectorCard { CardHandle = "h1", Iccsn = "8027", AuthCertificate = aut, Certificates = [aut, Cert("C.ENC")] });

        var latest = new ConnectorSnapshot();
        latest.Cards.Add(new ConnectorCard { CardHandle = "h1", Iccsn = "8027" });

        ConnectorWindow.CarryOverCardDetails(previous, latest);

        var card = Assert.Single(latest.Cards);
        Assert.Same(aut, card.AuthCertificate);
        Assert.Equal(["C.AUT", "C.ENC"], card.Certificates.Select(c => c.CertRef).OrderBy(r => r));
    }

    [Fact]
    public void Newer_certificates_are_not_overwritten_and_other_cards_are_untouched()
    {
        var oldQes = Cert("C.QES");
        var newQes = Cert("C.QES");
        var previous = new ConnectorSnapshot();
        previous.Cards.Add(new ConnectorCard { CardHandle = "h1", Certificates = [oldQes] });

        var latest = new ConnectorSnapshot();
        latest.Cards.Add(new ConnectorCard { CardHandle = "h1", Certificates = [newQes] });
        latest.Cards.Add(new ConnectorCard { CardHandle = "h2" });

        ConnectorWindow.CarryOverCardDetails(previous, latest);

        Assert.Same(newQes, Assert.Single(latest.Cards[0].Certificates));
        Assert.Empty(latest.Cards[1].Certificates);
        Assert.Null(latest.Cards[1].AuthCertificate);
    }
}
