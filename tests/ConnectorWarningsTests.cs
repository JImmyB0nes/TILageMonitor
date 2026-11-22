using Xunit;

namespace TILageMonitor.Tests;

/// <summary>Reine Logik der Konnektor-Warnungen aus 3.1: Ablaufstufen, PIN-Wechsel,
/// Fehlercodes, Backoff, Verlauf und Ereignisfilter.</summary>
public sealed class ConnectorWarningsTests
{
    private static readonly DateTime Now = new(2026, 11, 22, 10, 0, 0);

    private static ConnectorCard SmcB(string iccsn = "80276001", DateTime? expiry = null, params ConnectorPinState[] pins)
    {
        var card = new ConnectorCard { CardType = "SMC-B", Iccsn = iccsn, CardHandle = "H-" + iccsn, CertificateExpirationDate = expiry };
        card.PinStates.AddRange(pins);
        return card;
    }

    private static ConnectorCard Hba(string iccsn = "80276002", DateTime? expiry = null, params ConnectorPinState[] pins)
    {
        var card = new ConnectorCard { CardType = "HBA", Iccsn = iccsn, CardHandle = "H-" + iccsn, CertificateExpirationDate = expiry };
        card.PinStates.AddRange(pins);
        return card;
    }

    private static ConnectorPinState Pin(string type, string status, int? left = null) =>
        new() { PinType = type, Status = status, LeftTries = left };

    private static ConnectorSnapshot Snapshot(params ConnectorCard[] cards)
    {
        var s = new ConnectorSnapshot { RetrievedAt = Now };
        s.Cards.AddRange(cards);
        return s;
    }

    // ---------------- Zertifikatsablauf ----------------

    [Theory]
    [InlineData(200, null)]
    [InlineData(91, null)]
    [InlineData(90, 90)]
    [InlineData(31, 90)]
    [InlineData(30, 30)]
    [InlineData(8, 30)]
    [InlineData(7, 7)]
    [InlineData(0, 7)]
    [InlineData(-1, 0)]
    public void Expiry_stage_follows_90_30_7_thresholds(int days, int? expected)
    {
        Assert.Equal(expected, ConnectorCertificateExpiry.StageFor(days));
    }

    [Fact]
    public void Expiry_notifies_each_stage_once_and_survives_restart_via_ledger()
    {
        var ledger = new ConnectorWarningLedger();
        var notAfter = Now.AddDays(85);
        var subjects = ConnectorCertificateExpiry.Collect(Snapshot(SmcB(expiry: notAfter)), null, null);

        var first = ConnectorCertificateExpiry.Evaluate(subjects, ledger, Now);
        Assert.Single(first);
        Assert.Contains("-90", first[0].Key);

        // Gleiche Stufe, auch nach "Neustart" (selber Ledger) – nichts Neues.
        Assert.Empty(ConnectorCertificateExpiry.Evaluate(subjects, ledger, Now.AddDays(10)));

        // 30er-Stufe kommt einmal.
        var at30 = ConnectorCertificateExpiry.Evaluate(subjects, ledger, notAfter.AddDays(-29));
        Assert.Single(at30);
        Assert.Contains("-30", at30[0].Key);
        Assert.Empty(ConnectorCertificateExpiry.Evaluate(subjects, ledger, notAfter.AddDays(-20)));

        // 7er und abgelaufen.
        Assert.Single(ConnectorCertificateExpiry.Evaluate(subjects, ledger, notAfter.AddDays(-6)));
        var expired = ConnectorCertificateExpiry.Evaluate(subjects, ledger, notAfter.AddDays(1));
        Assert.Single(expired);
        Assert.EndsWith("-expired", expired[0].Key);
        Assert.Equal(ToastUrgency.Error, expired[0].Urgency);
    }

    [Fact]
    public void Expiry_starting_late_skips_higher_stages()
    {
        var ledger = new ConnectorWarningLedger();
        var subjects = ConnectorCertificateExpiry.Collect(Snapshot(SmcB(expiry: Now.AddDays(20))), null, null);
        var due = ConnectorCertificateExpiry.Evaluate(subjects, ledger, Now);
        Assert.Single(due);
        Assert.Contains("-30", due[0].Key);
    }

    [Fact]
    public void Expiry_renewed_certificate_starts_over()
    {
        var ledger = new ConnectorWarningLedger();
        var old = ConnectorCertificateExpiry.Collect(Snapshot(SmcB(expiry: Now.AddDays(5))), null, null);
        Assert.Single(ConnectorCertificateExpiry.Evaluate(old, ledger, Now));

        var renewed = ConnectorCertificateExpiry.Collect(Snapshot(SmcB(expiry: Now.AddDays(80))), null, null);
        Assert.Single(ConnectorCertificateExpiry.Evaluate(renewed, ledger, Now));
    }

    [Fact]
    public void Expiry_collects_smcb_hba_and_client_certificate_but_not_egk()
    {
        var egk = new ConnectorCard { CardType = "EGK", Iccsn = "1", CertificateExpirationDate = Now.AddDays(3) };
        var subjects = ConnectorCertificateExpiry.Collect(
            Snapshot(SmcB(expiry: Now.AddDays(10)), Hba(expiry: Now.AddDays(40)), egk),
            "CN=Praxis", Now.AddDays(60));

        Assert.Equal(3, subjects.Count);
        Assert.Contains(subjects, s => s.Kind == ConnectorCertificateKind.SmcB);
        Assert.Contains(subjects, s => s.Kind == ConnectorCertificateKind.Hba);
        Assert.Contains(subjects, s => s.Kind == ConnectorCertificateKind.ClientSystem);
    }

    [Fact]
    public void Expiry_uses_earliest_of_getcards_date_and_read_certificates()
    {
        var card = SmcB(expiry: Now.AddDays(200));
        card.Certificates.Add(new ConnectorCertificateInfo { CertRef = "C.QES", NotAfter = Now.AddDays(25) });
        var subject = Assert.Single(ConnectorCertificateExpiry.Collect(Snapshot(card), null, null));
        Assert.Equal(Now.AddDays(25), subject.NotAfter);
    }

    // ---------------- PIN-Status ----------------

    [Fact]
    public void Pin_classify_covers_blocked_lasttry_unverified_and_hba_verifiable()
    {
        var smcb = SmcB();
        var hba = Hba();
        Assert.Equal(ConnectorPinCondition.Blocked, ConnectorPinWatch.Classify(smcb, Pin("PIN.SMC", "BLOCKED", 0)));
        Assert.Equal(ConnectorPinCondition.LastTry, ConnectorPinWatch.Classify(smcb, Pin("PIN.SMC", "VERIFIABLE", 1)));
        Assert.Equal(ConnectorPinCondition.NotVerified, ConnectorPinWatch.Classify(smcb, Pin("PIN.SMC", "VERIFIABLE", 3)));
        Assert.Equal(ConnectorPinCondition.Ok, ConnectorPinWatch.Classify(smcb, Pin("PIN.SMC", "VERIFIED")));
        Assert.Equal(ConnectorPinCondition.Ok, ConnectorPinWatch.Classify(hba, Pin("PIN.CH", "VERIFIABLE", 3)));
        Assert.Equal(ConnectorPinCondition.LastTry, ConnectorPinWatch.Classify(hba, Pin("PIN.CH", "VERIFIABLE", 1)));
        Assert.Null(ConnectorPinWatch.Classify(smcb, new ConnectorPinState { PinType = "PIN.SMC", Error = "4085" }));
    }

    [Fact]
    public void Pin_notifies_only_on_state_transition()
    {
        var ledger = new ConnectorWarningLedger();

        // Erstes Sehen: verifiziert -> nichts.
        Assert.Empty(ConnectorPinWatch.Evaluate(Snapshot(SmcB(pins: Pin("PIN.SMC", "VERIFIED"))), ledger));

        // Konnektor-Neustart: nicht verifiziert -> eine Meldung, danach Ruhe.
        var unverified = Snapshot(SmcB(pins: Pin("PIN.SMC", "VERIFIABLE", 3)));
        var due = ConnectorPinWatch.Evaluate(unverified, ledger);
        Assert.Single(due);
        Assert.EndsWith("-unverified", due[0].Key);
        Assert.Empty(ConnectorPinWatch.Evaluate(unverified, ledger));

        // Letzter Versuch -> neue Meldung.
        var lastTry = ConnectorPinWatch.Evaluate(Snapshot(SmcB(pins: Pin("PIN.SMC", "VERIFIABLE", 1))), ledger);
        Assert.EndsWith("-lasttry", Assert.Single(lastTry).Key);

        // Gesperrt -> neue Meldung.
        var blocked = ConnectorPinWatch.Evaluate(Snapshot(SmcB(pins: Pin("PIN.SMC", "BLOCKED", 0))), ledger);
        Assert.EndsWith("-blocked", Assert.Single(blocked).Key);

        // Wieder in Ordnung -> Info.
        var ok = ConnectorPinWatch.Evaluate(Snapshot(SmcB(pins: Pin("PIN.SMC", "VERIFIED"))), ledger);
        Assert.Equal(ToastUrgency.Info, Assert.Single(ok).Urgency);
    }

    [Fact]
    public void Pin_bad_state_on_first_sight_is_reported_once()
    {
        var ledger = new ConnectorWarningLedger();
        var snap = Snapshot(SmcB(pins: Pin("PIN.SMC", "VERIFIABLE", 3)));
        Assert.Single(ConnectorPinWatch.Evaluate(snap, ledger));
        Assert.Empty(ConnectorPinWatch.Evaluate(snap, ledger));
    }

    [Fact]
    public void Pin_removed_card_is_forgotten_so_reinsertion_notifies_again()
    {
        var ledger = new ConnectorWarningLedger();
        var unverified = Snapshot(SmcB(pins: Pin("PIN.SMC", "VERIFIABLE", 3)));
        Assert.Single(ConnectorPinWatch.Evaluate(unverified, ledger));
        Assert.Empty(ConnectorPinWatch.Evaluate(Snapshot(), ledger));
        Assert.Empty(ledger.PinConditions);
        Assert.Single(ConnectorPinWatch.Evaluate(unverified, ledger));
    }

    [Fact]
    public void Pin_ledger_is_kept_when_getcards_failed()
    {
        var ledger = new ConnectorWarningLedger();
        ConnectorPinWatch.Evaluate(Snapshot(SmcB(pins: Pin("PIN.SMC", "VERIFIABLE", 3))), ledger);
        var failed = Snapshot();
        failed.Warnings.Add("Karten (GetCards): Timeout");
        ConnectorPinWatch.Evaluate(failed, ledger);
        Assert.NotEmpty(ledger.PinConditions);
    }

    [Fact]
    public void Health_alerts_for_pin_and_expiry_are_owned_by_ledger()
    {
        Assert.True(ConnectorToastOwnership.IsOwnedByLedger("pin-blocked-1-PIN.SMC"));
        Assert.True(ConnectorToastOwnership.IsOwnedByLedger("smcb-unverified-1"));
        Assert.True(ConnectorToastOwnership.IsOwnedByLedger("cert-expiring-1"));
        Assert.True(ConnectorToastOwnership.IsOwnedByLedger("pin-lasttry-1-PIN.CH"));
        Assert.False(ConnectorToastOwnership.IsOwnedByLedger("vpn-ti"));
        Assert.False(ConnectorToastOwnership.IsOwnedByLedger("qes-expired-1"));
    }

    [Fact]
    public void Health_reports_last_pin_try()
    {
        var alerts = ConnectorHealth.Evaluate(Snapshot(Hba(pins: Pin("PIN.CH", "VERIFIABLE", 1))), 60, Now);
        Assert.Contains(alerts, a => a.Key.StartsWith("pin-lasttry-", StringComparison.Ordinal));
    }

    // ---------------- Fehlercodes ----------------

    [Theory]
    [InlineData("4004", "Ungültige Mandanten-ID")]
    [InlineData("4005", "Ungültige Clientsystem-ID")]
    [InlineData("4006", "Ungültige Arbeitsplatz-ID")]
    [InlineData("4008", "Karte nicht als gesteckt identifiziert")]
    [InlineData("4010", "Clientsystem ist dem Mandanten nicht zugeordnet")]
    [InlineData("4204", "Clientsystem aus dem Aufrufkontext konnte nicht authentifiziert werden")]
    [InlineData("4222", "Kartenterminal ist nicht verbunden")]
    [InlineData("4063", "PIN bereits gesperrt (BLOCKED)")]
    public void Error_codes_map_to_gemspec_text(string code, string expected)
    {
        var info = ConnectorErrorCodes.Find(code);
        Assert.NotNull(info);
        Assert.Equal(expected, info!.SpecDe);
        Assert.False(string.IsNullOrWhiteSpace(info.SpecEn));
    }

    [Theory]
    [InlineData(" 4004 ")]
    [InlineData("gem:4004")]
    public void Error_code_lookup_tolerates_prefixes_and_whitespace(string raw)
    {
        Assert.Equal("4004", ConnectorErrorCodes.Find(raw)?.Code);
    }

    [Fact]
    public void Unknown_error_code_keeps_message_unchanged()
    {
        Assert.Null(ConnectorErrorCodes.Find("9999"));
        Assert.Null(ConnectorErrorCodes.Find(null));
        Assert.Equal("x", ConnectorErrorCodes.Annotate("x", "9999"));
    }

    [Fact]
    public void Annotate_appends_plain_text_once()
    {
        var once = ConnectorErrorCodes.Annotate("4004: Ungültige Mandanten-ID", "4004");
        Assert.Contains("4004 –", once);
        Assert.Equal(once, ConnectorErrorCodes.Annotate(once, "4004"));
    }

    [Fact]
    public void Error_code_table_has_unique_four_digit_codes()
    {
        var codes = ConnectorErrorCodes.All.Select(c => c.Code).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^4\\d{3}$", c));
    }

    [Fact]
    public void Soap_fault_message_carries_code_and_plain_text()
    {
        const string xml = """
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <soap:Fault>
                  <faultcode>soap:Server</faultcode>
                  <faultstring>Konnektorfehler</faultstring>
                  <detail>
                    <GERROR:Error xmlns:GERROR="http://ws.gematik.de/tel/error/v2.0">
                      <GERROR:Trace><GERROR:Code>4204</GERROR:Code><GERROR:ErrorText>Clientsystem nicht authentifiziert</GERROR:ErrorText></GERROR:Trace>
                    </GERROR:Error>
                  </detail>
                </soap:Fault>
              </soap:Body>
            </soap:Envelope>
            """;
        var ex = Assert.Throws<ConnectorFaultException>(() => ConnectorProtocol.ParseBody(xml));
        Assert.Equal("4204", ex.Code);
        Assert.Contains("4204 – ", ex.Message);
    }

    // ---------------- Backoff ----------------

    [Fact]
    public void Backoff_without_failures_uses_regular_interval()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), ConnectorReconnect.NextDelay(0, TimeSpan.FromMinutes(5), 0.5));
    }

    [Fact]
    public void Backoff_grows_exponentially_and_is_capped_at_five_minutes()
    {
        var regular = TimeSpan.FromMinutes(30);
        Assert.Equal(15, ConnectorReconnect.NextDelay(1, regular, 0.5).TotalSeconds, 3);
        Assert.Equal(30, ConnectorReconnect.NextDelay(2, regular, 0.5).TotalSeconds, 3);
        Assert.Equal(60, ConnectorReconnect.NextDelay(3, regular, 0.5).TotalSeconds, 3);
        Assert.Equal(300, ConnectorReconnect.NextDelay(10, regular, 0.5).TotalSeconds, 3);
        Assert.Equal(300, ConnectorReconnect.NextDelay(1000, regular, 0.99).TotalSeconds, 3);
    }

    [Fact]
    public void Backoff_never_exceeds_regular_interval()
    {
        Assert.True(ConnectorReconnect.NextDelay(8, TimeSpan.FromMinutes(1), 0.99) <= TimeSpan.FromMinutes(1));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Backoff_jitter_stays_within_twenty_percent(double random)
    {
        var delay = ConnectorReconnect.NextDelay(3, TimeSpan.FromMinutes(30), random).TotalSeconds;
        Assert.InRange(delay, 60 * 0.8 - 0.001, 60 * 1.2 + 0.001);
    }

    [Fact]
    public void Unreachable_toast_waits_for_two_failures_and_two_minutes()
    {
        Assert.False(ConnectorReconnect.ShouldNotifyUnreachable(1, Now.AddMinutes(-10), Now, false));
        Assert.False(ConnectorReconnect.ShouldNotifyUnreachable(3, Now.AddSeconds(-60), Now, false));
        Assert.True(ConnectorReconnect.ShouldNotifyUnreachable(3, Now.AddMinutes(-3), Now, false));
        Assert.False(ConnectorReconnect.ShouldNotifyUnreachable(3, Now.AddMinutes(-3), Now, true));
    }

    // ---------------- Verlauf ----------------

    private static ConnectorSnapshot Online(bool ti, bool sis, params ConnectorCard[] cards)
    {
        var s = Snapshot(cards);
        s.Resource = new ConnectorResourceInfo { VpnTiStatus = ti ? "Online" : "Offline", VpnSisStatus = sis ? "Online" : "Offline" };
        return s;
    }

    [Fact]
    public void History_records_reachability_vpn_and_card_presence()
    {
        var file = new ConnectorHistoryFile();
        ConnectorHistoryStore.Apply(file, Online(true, true, SmcB()), Now);
        ConnectorHistoryStore.Apply(file, Online(false, true, SmcB()), Now.AddMinutes(5));
        ConnectorHistoryStore.ApplyUnreachable(file, Now.AddMinutes(10));

        var hour = Assert.Single(file.Hours);
        Assert.Equal(2, hour.Polls);
        Assert.Equal(1, hour.Unreachable);
        Assert.False(hour.TiOnline);
        Assert.True(hour.SisOnline);
        Assert.Equal(2, hour.SmcbPolls);
        Assert.Equal(0, hour.HbaPolls);

        Assert.Equal(ConnectorHistoryState.Partial, ConnectorHistoryTimeline.Evaluate(hour, ConnectorHistoryLane.Reachable).State);
        Assert.Equal(ConnectorHistoryState.Down, ConnectorHistoryTimeline.Evaluate(hour, ConnectorHistoryLane.VpnTi).State);
        Assert.Equal(ConnectorHistoryState.Ok, ConnectorHistoryTimeline.Evaluate(hour, ConnectorHistoryLane.VpnSis).State);
        Assert.Equal(ConnectorHistoryState.Ok, ConnectorHistoryTimeline.Evaluate(hour, ConnectorHistoryLane.SmcB).State);
        Assert.Equal(ConnectorHistoryState.Down, ConnectorHistoryTimeline.Evaluate(hour, ConnectorHistoryLane.Hba).State);
    }

    [Fact]
    public void History_card_presence_ignores_polls_where_getcards_failed()
    {
        var file = new ConnectorHistoryFile();
        var failed = Online(true, true);
        failed.Warnings.Add("Karten (GetCards): Fehler");
        ConnectorHistoryStore.Apply(file, failed, Now);
        var hour = Assert.Single(file.Hours);
        Assert.Equal(0, hour.CardPolls);
        Assert.Equal(ConnectorHistoryState.NoData, ConnectorHistoryTimeline.Evaluate(hour, ConnectorHistoryLane.SmcB).State);
    }

    [Fact]
    public void History_prunes_entries_older_than_fourteen_days()
    {
        var file = new ConnectorHistoryFile();
        ConnectorHistoryStore.Apply(file, Online(true, true), Now.AddDays(-20));
        ConnectorHistoryStore.Apply(file, Online(true, true), Now.AddDays(-13));
        ConnectorHistoryStore.Apply(file, Online(true, true), Now);
        ConnectorHistoryStore.Prune(file, Now.Date);
        Assert.Equal(2, file.Hours.Count);
    }

    [Fact]
    public void History_timeline_builds_fourteen_days_with_day_summary()
    {
        var file = new ConnectorHistoryFile();
        ConnectorHistoryStore.Apply(file, Online(true, true, SmcB()), Now.Date.AddHours(8));
        ConnectorHistoryStore.ApplyUnreachable(file, Now.Date.AddHours(9));
        ConnectorHistoryStore.Apply(file, Online(true, true, SmcB()), Now.Date.AddDays(-1).AddHours(8));

        var days = ConnectorHistoryTimeline.Build(file, Now.Date);
        Assert.Equal(14, days.Count);
        Assert.Equal(Now.Date, days[^1].Date);
        Assert.Equal(Now.Date.AddDays(-13), days[0].Date);

        var today = days[^1];
        Assert.Equal(2, today.CoveredHours);
        Assert.Equal(1, today.ProblemHours);
        Assert.Equal(ConnectorHistoryState.Partial, today.Summary[ConnectorHistoryLane.Reachable].State);
        Assert.Equal(ConnectorHistoryState.Down, today.Hours[ConnectorHistoryLane.Reachable][9].State);

        var yesterday = days[^2];
        Assert.Equal(ConnectorHistoryState.Ok, yesterday.Summary[ConnectorHistoryLane.Reachable].State);
        Assert.Equal(ConnectorHistoryState.NoData, days[0].Summary[ConnectorHistoryLane.Reachable].State);
    }

    [Fact]
    public void History_legacy_hours_without_counters_count_as_reachable()
    {
        var hour = new ConnectorHourSnapshot { HourKey = "2026-11-22-10+01:00", TiOnline = true, Cards = 1 };
        Assert.Equal(ConnectorHistoryState.Ok, ConnectorHistoryTimeline.Evaluate(hour, ConnectorHistoryLane.Reachable).State);
    }

    // ---------------- Ereignisfilter ----------------

    private static ConnectorEvent Event(string topic, params (string Key, string Value)[] parameters)
    {
        var evt = new ConnectorEvent { Topic = topic, ReceivedAt = Now };
        foreach (var (key, value) in parameters)
            evt.Parameters[key] = value;
        return evt;
    }

    private static readonly ConnectorEventToastFilter Defaults = ConnectorEventToastFilter.From(new ConnectorSettings());

    [Fact]
    public void Event_filter_defaults_toast_removed_and_disconnected_only()
    {
        Assert.NotNull(ConnectorEventNotifications.Decide(Event(ConnectorTopics.CardRemoved, ("CardType", "SMC-B"), ("CtID", "CT1")), Defaults));
        Assert.NotNull(ConnectorEventNotifications.Decide(Event(ConnectorTopics.TerminalDisconnected, ("CtID", "CT1")), Defaults));
        Assert.Null(ConnectorEventNotifications.Decide(Event(ConnectorTopics.CardInserted, ("CardType", "SMC-B")), Defaults));
        Assert.Null(ConnectorEventNotifications.Decide(Event(ConnectorTopics.TerminalConnected, ("CtID", "CT1")), Defaults));
    }

    [Fact]
    public void Event_filter_never_toasts_egk_and_respects_card_type_switches()
    {
        Assert.Null(ConnectorEventNotifications.Decide(Event(ConnectorTopics.CardRemoved, ("CardType", "EGK")), Defaults));
        var noHba = Defaults with { Hba = false };
        Assert.Null(ConnectorEventNotifications.Decide(Event(ConnectorTopics.CardRemoved, ("CardType", "HBA")), noHba));
        Assert.NotNull(ConnectorEventNotifications.Decide(Event(ConnectorTopics.CardRemoved, ("CardType", "SMC-B")), noHba));
        var noSmcb = Defaults with { SmcB = false };
        Assert.Null(ConnectorEventNotifications.Decide(Event(ConnectorTopics.CardRemoved, ("CardType", "SMC-B")), noSmcb));
    }

    [Fact]
    public void Event_filter_accepts_type_parameter_from_tip1_a_text()
    {
        // TIP1-A_4562 nennt den Kartentyp bei CARD/REMOVED "Type", die Ereignistabelle "CardType".
        Assert.Equal(ConnectorEventNotifications.CardKind.Hba, ConnectorEventNotifications.KindOf(Event(ConnectorTopics.CardRemoved, ("Type", "HBA"))));
    }

    [Fact]
    public void Event_filter_can_enable_inserted_and_connected()
    {
        var all = Defaults with { CardInserted = true, TerminalConnected = true };
        Assert.Equal(ToastUrgency.Info, ConnectorEventNotifications.Decide(Event(ConnectorTopics.CardInserted, ("CardType", "HBA")), all)?.Urgency);
        Assert.NotNull(ConnectorEventNotifications.Decide(Event(ConnectorTopics.TerminalConnected, ("CtID", "CT1")), all));
    }

    [Fact]
    public void Event_maps_to_matching_health_key()
    {
        Assert.Equal("ct-CT1", ConnectorEventNotifications.MatchingHealthKey(Event(ConnectorTopics.TerminalDisconnected, ("CtID", "CT1"))));
        Assert.Equal("no-smcb", ConnectorEventNotifications.MatchingHealthKey(Event(ConnectorTopics.CardRemoved, ("CardType", "SMC-B"))));
        Assert.Null(ConnectorEventNotifications.MatchingHealthKey(Event(ConnectorTopics.CardInserted, ("CardType", "SMC-B"))));
    }

    [Fact]
    public void Event_deduplicator_suppresses_repeats_within_window()
    {
        var dedup = new ConnectorEventDeduplicator(TimeSpan.FromSeconds(15));
        Assert.True(dedup.TryAccept("CARD/REMOVED|1", Now));
        Assert.False(dedup.TryAccept("CARD/REMOVED|1", Now.AddSeconds(5)));
        Assert.True(dedup.TryAccept("CARD/REMOVED|2", Now.AddSeconds(5)));
        Assert.True(dedup.TryAccept("CARD/REMOVED|1", Now.AddSeconds(30)));
    }
}
