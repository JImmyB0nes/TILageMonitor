using System.Text;
using Xunit;

namespace TILageMonitor.Tests;

/// <summary>
/// Prueft das Zerlegen des hereinkommenden Ereignisstroms – die Stelle, an der ein halbes
/// Dokument bisher lautlos wegfiel. Der Konnektor liefert eine SOAP-Huelle regelmaessig in
/// mehreren TCP-Stuecken; wer am inneren Tag schneidet, verliert das Ereignis ohne jede
/// Meldung. Die Tests hier bilden genau diese Lieferformen nach, ohne echten Konnektor.
/// </summary>
public sealed class ConnectorEventStreamTests
{
    private const string Envelope =
        """
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
          <soap:Body>
            <evt:Event xmlns:evt="http://ws.gematik.de/conn/EventService/v7.2">
              <evt:Topic>http://ws.gematik.de/conn/EventService/v7.2#CardInserted</evt:Topic>
              <evt:Message><evt:Parameter><evt:Key>CtId</evt:Key><evt:Value>CT1</evt:Value></evt:Parameter></evt:Message>
            </evt:Event>
          </soap:Body>
        </soap:Envelope>
        """;

    [Fact]
    public void TakeMessage_keeps_an_incomplete_envelope_until_the_closing_tag_arrives()
    {
        // Erster Lesedurchgang: die Huelle ist angefangen, ihr Ende fehlt noch.
        var head = Envelope[..200];
        var pending = new StringBuilder(head);

        Assert.False(ConnectorEventStream.TakeMessage(ref pending, out _));
        // Der halbe Text bleibt erhalten – ein Abschneiden haette das Ereignis zerstoert.
        Assert.Equal(head, pending.ToString());
    }

    [Fact]
    public void TakeMessage_cuts_at_the_outer_envelope_end_when_the_body_is_split_across_reads()
    {
        var pending = new StringBuilder();

        // Stueck 1: Huelle und Ereignis-Anfang, aber das innerste </Event> ist schon da,
        // das aeussere </Envelope> fehlt. Genau der Fall, der zur frueheren Fassung fuehrte.
        var first = Envelope[..150];
        pending.Append(first);
        Assert.False(ConnectorEventStream.TakeMessage(ref pending, out _));

        // Stueck 2: Rest nebst Schluss-Tags.
        pending.Append(Envelope[150..]);
        Assert.True(ConnectorEventStream.TakeMessage(ref pending, out var message));
        Assert.EndsWith("</Envelope>", message.TrimEnd());
        Assert.Equal("", pending.ToString());
    }

    [Fact]
    public void TakeMessage_reads_two_back_to_back_messages_from_one_read()
    {
        var pending = new StringBuilder(Envelope + Envelope);

        Assert.True(ConnectorEventStream.TakeMessage(ref pending, out var first));
        Assert.True(ConnectorEventStream.TakeMessage(ref pending, out var second));
        Assert.NotEqual("", first);
        Assert.NotEqual("", second);
        Assert.Equal("", pending.ToString());
    }

    [Fact]
    public void TakeMessage_accepts_a_bare_event_without_any_envelope()
    {
        const string bare =
            """
            <evt:Event xmlns:evt="http://ws.gematik.de/conn/EventService/v7.2">
              <evt:Topic>http://ws.gematik.de/conn/EventService/v7.2#CardRemoved</evt:Topic>
            </evt:Event>
            """;

        var pending = new StringBuilder(bare);
        Assert.True(ConnectorEventStream.TakeMessage(ref pending, out var message));
        Assert.Contains("CardRemoved", message);
        Assert.Equal("", pending.ToString());
    }

    [Theory]
    [InlineData("<soap:Envelope", true)]     // ubliclich mit Praefix
    [InlineData("<Envelope", true)]          // ohne Praefix
    [InlineData("<ns1:Envelope", true)]      // anderes Praefix
    [InlineData("Envelope", false)]         // blosses Wort, kein Elementanfang
    [InlineData("kein XML", false)]
    public void HasOpeningEnvelope_recognizes_a_prefixed_tag(string text, bool expected)
    {
        Assert.Equal(expected, ConnectorEventStream.HasOpeningEnvelope(text));
    }

    [Fact]
    public void A_bare_event_is_not_cut_early_while_an_envelope_is_still_open()
    {
        // Die Huelle laeuft, ihr Schluss fehlt. Der Innen-Tag "durfte" nicht als Ende gelten –
        // das war der eigentliche Fehler, nicht nur ein Randfall.
        var pending = new StringBuilder("<soap:Envelope><evt:Event><evt:Topic>x</evt:Topic></evt:Event>");
        Assert.False(ConnectorEventStream.TakeMessage(ref pending, out _));
    }

    [Fact]
    public void ParseEvent_survives_the_full_envelope_form()
    {
        // Dieselbe Nachricht, die ueber dem Draht ankommt – bisher wurde nur die reine Form
        // getestet. Der Parser muss auch in der Huelle das Ereignis finden.
        var evt = ConnectorProtocol.ParseEvent(Envelope);

        Assert.NotNull(evt);
        Assert.EndsWith("#CardInserted", evt!.Topic);
        Assert.Equal("CT1", evt.Param("CtId"));
    }
}
