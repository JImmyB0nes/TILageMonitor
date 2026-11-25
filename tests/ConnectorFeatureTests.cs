using System.Text;
using System.Xml.Linq;
using Xunit;

namespace TILageMonitor.Tests;

/// <summary>Uhrzeitvergleich, Ablaufkalender, SMC-B-Erinnerung und QES-Probesignatur.</summary>
public sealed class ConnectorFeatureTests
{
    // ---------------------------------------------------------------- Uhrzeit

    private static readonly DateTimeOffset Sent = new(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Clock_offset_is_measured_against_the_middle_of_the_request()
    {
        var received = Sent.AddSeconds(2);
        var offset = ConnectorClock.OffsetFromDateHeader("Wed, 25 Nov 2026 08:03:01 GMT", Sent, received);
        Assert.Equal(TimeSpan.FromMinutes(3), offset);
    }

    [Fact]
    public void Clock_offset_below_one_second_counts_as_in_sync()
    {
        var offset = ConnectorClock.OffsetFromDateHeader("Wed, 25 Nov 2026 08:00:00 GMT", Sent, Sent.AddMilliseconds(400));
        Assert.Equal(TimeSpan.Zero, offset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("kein Datum")]
    public void Clock_offset_is_null_without_a_usable_header(string? header) =>
        Assert.Null(ConnectorClock.OffsetFromDateHeader(header, Sent, Sent.AddSeconds(1)));

    [Fact]
    public void Clock_offset_is_discarded_after_a_slow_round_trip() =>
        Assert.Null(ConnectorClock.OffsetFromDateHeader("Wed, 25 Nov 2026 08:00:30 GMT", Sent, Sent.AddSeconds(30)));

    [Fact]
    public void Clock_alert_follows_the_thresholds()
    {
        Assert.Null(ConnectorClock.Evaluate(null));
        Assert.Null(ConnectorClock.Evaluate(TimeSpan.FromSeconds(90)));
        Assert.Equal(ToastUrgency.Warning, ConnectorClock.Evaluate(TimeSpan.FromMinutes(-3))!.Urgency);
        Assert.Equal(ToastUrgency.Error, ConnectorClock.Evaluate(TimeSpan.FromMinutes(6))!.Urgency);
        Assert.Equal("clock-drift", ConnectorClock.Evaluate(TimeSpan.FromMinutes(6))!.Key);
    }

    [Fact]
    public void Clock_drift_appears_in_the_health_evaluation()
    {
        var snapshot = new ConnectorSnapshot { ClockOffset = TimeSpan.FromMinutes(10) };
        snapshot.Cards.Add(new ConnectorCard { CardType = "SMC-B" });
        Assert.Contains(ConnectorHealth.Evaluate(snapshot, 30, DateTime.Now), a => a.Key == "clock-drift");
    }

    [Theory]
    [InlineData(45, "45 s")]
    [InlineData(200, "3 min 20 s")]
    [InlineData(-120, "2 min")]
    [InlineData(3900, "1 h 5 min")]
    public void Clock_offset_is_formatted_readably(int seconds, string expected) =>
        Assert.Equal(expected, ConnectorClock.Format(TimeSpan.FromSeconds(seconds)));

    // ---------------------------------------------------------------- Kalender

    [Fact]
    public void Calendar_contains_one_all_day_event_with_three_reminders_per_certificate()
    {
        var ics = ConnectorCalendarExport.Build(
            [new ConnectorCalendarEntry("C.AUT", "SMC-B Praxis Dr. Muster", new DateTime(2027, 3, 14, 23, 59, 0))],
            new DateTime(2026, 11, 25, 7, 0, 0, DateTimeKind.Utc));

        Assert.StartsWith("BEGIN:VCALENDAR\r\n", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
        Assert.Single(Lines(ics), l => l == "BEGIN:VEVENT");
        Assert.Contains("DTSTART;VALUE=DATE:20270314", Lines(ics));
        Assert.Contains("DTEND;VALUE=DATE:20270315", Lines(ics));
        Assert.Contains("DTSTAMP:20261125T070000Z", Lines(ics));
        Assert.Equal(["TRIGGER:-P90D", "TRIGGER:-P30D", "TRIGGER:-P7D"], Lines(ics).Where(l => l.StartsWith("TRIGGER:")));
    }

    [Fact]
    public void Calendar_uid_is_stable_and_duplicates_are_dropped()
    {
        var entry = new ConnectorCalendarEntry("C.QES", "HBA Dr. Muster", new DateTime(2028, 1, 1));
        Assert.Equal(ConnectorCalendarExport.Uid(entry), ConnectorCalendarExport.Uid(entry with { NotAfter = new DateTime(2028, 1, 1, 12, 0, 0) }));

        var ics = ConnectorCalendarExport.Build([entry, entry], DateTime.UtcNow);
        Assert.Single(Lines(ics), l => l == "BEGIN:VEVENT");
    }

    [Fact]
    public void Calendar_text_is_escaped_and_long_lines_are_folded_at_75_octets()
    {
        Assert.Equal("a\\, b\\; c\\\\d\\ne", ConnectorCalendarExport.Escape("a, b; c\\d\ne"));

        var sb = new StringBuilder();
        ConnectorCalendarExport.AppendFolded(sb, "SUMMARY:" + new string('ä', 80));
        var physical = sb.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.True(physical.Length > 1);
        Assert.All(physical, l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 75));
        Assert.All(physical.Skip(1), l => Assert.StartsWith(" ", l));
        Assert.Equal("SUMMARY:" + new string('ä', 80), string.Concat(physical.Select((l, i) => i == 0 ? l : l[1..])));
    }

    private static List<string> Lines(string ics) =>
        ics.Replace("\r\n ", "").Split("\r\n", StringSplitOptions.RemoveEmptyEntries).ToList();

    // ---------------------------------------------------------------- SMC-B-Erinnerung

    private static ConnectorCard Smcb(string status, string iccsn = "80276001") => new()
    {
        CardType = "SMC-B",
        Iccsn = iccsn,
        CardHandle = "h-" + iccsn,
        PinStates = [new ConnectorPinState { PinType = "PIN.SMC", Status = status }]
    };

    [Fact]
    public void Smcb_reminder_detects_an_unverified_smcb()
    {
        var snapshot = new ConnectorSnapshot { Cards = [Smcb("VERIFIED", "1"), Smcb("VERIFIABLE", "2")] };
        var (state, card) = ConnectorSmcbReminder.Evaluate(snapshot);
        Assert.Equal(ConnectorSmcbReminder.State.NotVerified, state);
        Assert.Equal("2", card!.Iccsn);
    }

    [Fact]
    public void Smcb_reminder_stays_undecided_without_readable_pin_status()
    {
        var unreadable = Smcb("");
        unreadable.PinStates[0].Error = "4000";
        Assert.Equal(ConnectorSmcbReminder.State.Unknown, ConnectorSmcbReminder.Evaluate(new ConnectorSnapshot { Cards = [unreadable] }).State);
        Assert.Equal(ConnectorSmcbReminder.State.Unknown, ConnectorSmcbReminder.Evaluate(new ConnectorSnapshot()).State);
        Assert.Equal(ConnectorSmcbReminder.State.Verified, ConnectorSmcbReminder.Evaluate(new ConnectorSnapshot { Cards = [Smcb("VERIFIED")] }).State);
    }

    [Fact]
    public void Smcb_reminder_focus_keys_round_trip()
    {
        var card = Smcb("VERIFIABLE", "80276883110000123456");
        Assert.Equal((true, "80276883110000123456"), ConnectorSmcbReminder.ParseFocus(ConnectorSmcbReminder.UnlockFocus(card)));
        Assert.Equal((false, "80276883110000123456"), ConnectorSmcbReminder.ParseFocus(ConnectorSmcbReminder.ShowFocus(card)));
        Assert.Null(ConnectorSmcbReminder.ParseFocus("konnektor"));
        Assert.Null(ConnectorSmcbReminder.ParseFocus(null));
    }

    // ---------------------------------------------------------------- QES-Probesignatur

    private static readonly ConnectorSettings Settings = new() { MandantId = "M1", ClientSystemId = "CS1", WorkplaceId = "AP1" };

    [Fact]
    public void Sign_document_follows_the_schema_order()
    {
        XNamespace sig = ConnectorProtocol.DefaultSignatureServiceNs;
        var request = ConnectorProtocol.SignDocument(sig, Settings, "HBA-1", "ABC-123", Encoding.UTF8.GetBytes("Test"), "Kurztext");

        Assert.Equal(["CardHandle", "Context", "TvMode", "JobNumber", "SignRequest"], request.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("NONE", request.Element(sig + "TvMode")!.Value);
        var signRequest = request.Element(sig + "SignRequest")!;
        Assert.Equal(["OptionalInputs", "Document", "IncludeRevocationInfo"], signRequest.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(ConnectorProtocol.CadesSignatureType, signRequest.Descendants(ConnectorProtocol.Dss + "SignatureType").Single().Value);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("Test")), signRequest.Descendants(ConnectorProtocol.Dss + "Base64Data").Single().Value);
        Assert.False(string.IsNullOrWhiteSpace((string?)signRequest.Attribute("RequestID")));
    }

    [Fact]
    public void Sign_result_ok_reports_the_signature_size()
    {
        var response = XElement.Parse("""
            <SIG:SignDocumentResponse xmlns:SIG="http://ws.gematik.de/conn/SignatureService/v7.4" xmlns:CONN="http://ws.gematik.de/conn/ConnectorCommon/v5.0" xmlns:dss="urn:oasis:names:tc:dss:1.0:core:schema">
              <SIG:SignResponse RequestID="r1">
                <CONN:Status><CONN:Result>OK</CONN:Result></CONN:Status>
                <dss:SignatureObject><dss:Base64Signature Type="urn:ietf:rfc:5652">AAECAwQ=</dss:Base64Signature></dss:SignatureObject>
              </SIG:SignResponse>
            </SIG:SignDocumentResponse>
            """);
        var result = ConnectorProtocol.ParseSignResult(response);
        Assert.True(result.Ok);
        Assert.Equal(5, result.SignatureBytes);
    }

    [Fact]
    public void Sign_result_error_carries_code_and_text()
    {
        var response = XElement.Parse("""
            <SIG:SignDocumentResponse xmlns:SIG="http://ws.gematik.de/conn/SignatureService/v7.4" xmlns:CONN="http://ws.gematik.de/conn/ConnectorCommon/v5.0" xmlns:GERROR="http://ws.gematik.de/tel/error/v2.0">
              <SIG:SignResponse RequestID="r1">
                <CONN:Status>
                  <CONN:Result>Error</CONN:Result>
                  <GERROR:Error><GERROR:Trace><GERROR:Code>4085</GERROR:Code><GERROR:ErrorText>PIN falsch</GERROR:ErrorText></GERROR:Trace></GERROR:Error>
                </CONN:Status>
              </SIG:SignResponse>
            </SIG:SignDocumentResponse>
            """);
        var result = ConnectorProtocol.ParseSignResult(response);
        Assert.False(result.Ok);
        Assert.Equal("Error", result.Result);
        Assert.Equal("4085", result.ErrorCode);
        Assert.Contains("PIN falsch", result.ErrorText);
    }

    [Fact]
    public void Job_number_is_read_from_the_response() =>
        Assert.Equal("ABC-123", ConnectorProtocol.ParseJobNumber(XElement.Parse(
            """<SIG:GetJobNumberResponse xmlns:SIG="http://ws.gematik.de/conn/SignatureService/v7.4"><SIG:JobNumber>ABC-123</SIG:JobNumber></SIG:GetJobNumberResponse>""")));
}
