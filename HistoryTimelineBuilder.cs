using System.Globalization;

namespace TILageMonitor;

/// <summary>
/// Compact incident/outage timeline entries for the HistoryWindow „Ereignisse“ surface.
/// Intervals are derived similarly to HistoryStore incident/outage hour overlaps.
/// </summary>
public sealed class HistoryTimelineEvent
{
    public string ServiceKey { get; }
    public string ServiceName { get; }
    public string KindLabel { get; }
    public DateTime StartLocal { get; }
    public DateTime EndLocal { get; }
    public DateTime DayDate => StartLocal.Date;

    public string Title { get; }
    public string TimeRangeText { get; }
    public string Body { get; }

    /// <summary>
    /// Eigentlicher Grund, wenn er ueber die reine Art (Störung/Teilausfall) hinausgeht –
    /// beim lokalen Konnektor-Verlauf die konkrete Meldung statt einer allgemeinen Floskel.
    /// </summary>
    public string? Detail { get; }
    public string ClickHint { get; }
    public string? StatusCode { get; }
    public string DisplayLine { get; }

    /// <summary>True when this event is selected/highlighted (e.g. from Stunden click).</summary>
    public bool IsHighlighted { get; set; }

    public System.Windows.Media.Brush AccentBarBrush =>
        HistoryDayCell.BrushForStatus(StatusCode);

    public HistoryTimelineEvent(
        string serviceKey,
        string serviceName,
        string kindLabel,
        DateTime startLocal,
        DateTime endLocal,
        string? detail = null)
    {
        ServiceKey = serviceKey;
        ServiceName = serviceName;
        KindLabel = kindLabel;
        Detail = detail;
        StartLocal = startLocal;
        EndLocal = endLocal;
        StatusCode = StatusCodeFromKind(kindLabel);
        Title = $"{serviceName} · {LocalizationService.Translate(kindLabel)}";
        TimeRangeText = FormatRange(startLocal, endLocal);
        Body = detail ?? LocalizationService.Translate(kindLabel switch
        {
            "Störung" => "Vollständige Störung im TI-Status — Stundenansicht für Details.",
            "Teilausfall" => "Einschränkung / Teilausfall — Zeitraum lokal dargestellt.",
            "Wartung" => "Geplante oder laufende Wartung im erfassten Fenster.",
            _ => "Ereignis aus dem TI-Status (lokal dargestellt)."
        });
        ClickHint =
            $"{LocalizationService.Translate("Stundenansicht öffnen")} · {startLocal.ToString("d", LocalizationService.DisplayCulture)}";
        DisplayLine = $"{Title} · {TimeRangeText}";
    }

    private static string? StatusCodeFromKind(string kindLabel) => kindLabel switch
    {
        "Störung" => "full",
        "Teilausfall" => "partial",
        "Wartung" => "maintenance",
        _ => "partial"
    };

    private static string FormatRange(DateTime start, DateTime end)
    {
        var culture = LocalizationService.DisplayCulture;
        var weekday = culture.DateTimeFormat.AbbreviatedDayNames[(int)start.DayOfWeek].TrimEnd('.');
        if (weekday.Length > 0)
            weekday = char.ToUpper(weekday[0], culture) + weekday[1..];

        if (start.Date == end.Date)
            return $"{weekday} {start:HH:mm}–{end:HH:mm}";

        var endWeekday = culture.DateTimeFormat.AbbreviatedDayNames[(int)end.DayOfWeek].TrimEnd('.');
        if (endWeekday.Length > 0)
            endWeekday = char.ToUpper(endWeekday[0], culture) + endWeekday[1..];
        return $"{weekday} {start.ToString("d", culture)} {start:HH:mm} – {endWeekday} {end.ToString("d", culture)} {end:HH:mm}";
    }
}

public static class HistoryTimelineBuilder
{
    private static CultureInfo UiCulture => LocalizationService.DisplayCulture;

    /// <summary>
    /// Builds recent timeline events from official incident steps and outage slots,
    /// clipped to the current 14-day local window. Newest first.
    /// </summary>
    public static List<HistoryTimelineEvent> Build(
        IncidentResponse? incidents,
        OutageResponse? outages,
        string? serviceFilterKey = null,
        int maxEvents = 80,
        ConnectorHistoryFile? connectorHistory = null)
    {
        var windowStart = DateTime.Today.AddDays(-13);
        var windowEnd = DateTime.Today.AddDays(1);
        var events = new List<HistoryTimelineEvent>();

        events.AddRange(FromConnectorHistory(connectorHistory, serviceFilterKey, windowStart, windowEnd));

        if (incidents?.Data is { Count: > 0 })
        {
            foreach (var incident in incidents.Data)
            {
                var services = (incident.App ?? Enumerable.Empty<string>())
                    .Select(HistoryStore.TryMapServiceKey)
                    .Where(key => key is not null)
                    .Select(key => key!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(key => serviceFilterKey is null ||
                                  string.Equals(key, serviceFilterKey, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (services.Count == 0)
                    continue;

                var steps = (incident.Steps ?? Enumerable.Empty<IncidentStep>())
                    .OrderBy(step => step.Timestamp)
                    .ToList();

                string? lastKind = null;
                for (var i = 0; i < steps.Count; i++)
                {
                    var kind = KindFromIncidentStep(steps[i]);
                    if (kind is not null)
                    {
                        lastKind = kind;
                    }
                    else if (steps[i].Status == 2 && lastKind is not null)
                    {
                        kind = lastKind;
                    }
                    else
                    {
                        lastKind = null;
                        continue;
                    }

                    var startLocal = steps[i].Timestamp.ToLocalTime();
                    var endUtc = i + 1 < steps.Count
                        ? steps[i + 1].Timestamp
                        : incident.ClosedAt ?? DateTime.UtcNow;
                    var endLocal = endUtc.ToLocalTime();

                    if (endLocal <= startLocal)
                        continue;
                    if (endLocal <= windowStart || startLocal >= windowEnd)
                        continue;

                    var clippedStart = startLocal < windowStart ? windowStart : startLocal;
                    var clippedEnd = endLocal > windowEnd ? windowEnd : endLocal;
                    if (clippedEnd <= clippedStart)
                        continue;

                    foreach (var key in services)
                    {
                        var name = AppSettings.ServiceDisplayNames.TryGetValue(key, out var n) ? n : key;
                        events.Add(new HistoryTimelineEvent(key, name, kind, clippedStart, clippedEnd));
                    }
                }
            }
        }

        if (outages?.Data is { Count: > 0 })
        {
            foreach (var outage in outages.Data)
            {
                var serviceKey = HistoryStore.TryMapServiceKey(outage.Service);
                if (serviceKey is null)
                    continue;
                if (serviceFilterKey is not null &&
                    !string.Equals(serviceKey, serviceFilterKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = AppSettings.ServiceDisplayNames.TryGetValue(serviceKey, out var n) ? n : serviceKey;

                foreach (var slot in outage.Slots ?? Enumerable.Empty<OutageSlot>())
                {
                    var startLocal = slot.StartTimestamp.ToLocalTime();
                    var endLocal = (slot.EndTimestamp ?? DateTime.Now).ToLocalTime();
                    if (endLocal <= startLocal)
                        continue;
                    if (endLocal <= windowStart || startLocal >= windowEnd)
                        continue;

                    var clippedStart = startLocal < windowStart ? windowStart : startLocal;
                    var clippedEnd = endLocal > windowEnd ? windowEnd : endLocal;
                    if (clippedEnd <= clippedStart)
                        continue;

                    events.Add(new HistoryTimelineEvent(
                        serviceKey, name, "Teilausfall", clippedStart, clippedEnd));
                }
            }
        }

        return MergeAdjacentSameKind(DedupeOverlapping(events))
            .OrderByDescending(e => e.StartLocal)
            .ThenBy(e => e.ServiceName, StringComparer.Create(UiCulture, ignoreCase: true))
            .Take(Math.Max(1, maxEvents))
            .ToList();
    }

    /// <summary>
    /// Uebersetzt den lokalen Konnektor-Verlauf in Timeline-Eintraege. Noetig, weil Karten-,
    /// Terminal- und Zertifikatsstoerungen nicht ueber die gematik-API laufen und im Verlauf
    /// sonst leer waeren, obwohl der Konnektor sehr wohl etwas gemeldet hat.
    /// </summary>
    private static IEnumerable<HistoryTimelineEvent> FromConnectorHistory(
        ConnectorHistoryFile? history,
        string? serviceFilterKey,
        DateTime windowStart,
        DateTime windowEnd)
    {
        if (history?.Hours is not { Count: > 0 })
            yield break;

        // Eine Stoerung kann sich ueber mehrere Stunden ziehen – also je Beobachtungspunkt
        // aneinandergereihte Stunden zu einem Intervall bndeln.
        foreach (var group in history.Hours
                     .SelectMany(h => h.States.Select(s => (Hour: h, s.Key, s.Value)))
                     .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var hours = group
                .Select(x => (Start: ParseHour(x.Hour.HourKey), Severity: x.Value))
                .Where(x => x.Start >= windowStart && x.Start < windowEnd)
                .OrderBy(x => x.Start)
                .ToList();
            if (hours.Count == 0)
                continue;

            var severity = hours.Max(x => HistoryStore.StatusSeverity(x.Severity));
            var kind = severity >= 3 ? "Störung" : "Teilausfall";

            // Der Filter steht auf einem gematik-Dienst: lokale Konnektor-Eintraege passen
            // nicht dazu und sollen dann nicht unter einem fremden Dienstkey auftauchen.
            if (serviceFilterKey is not null &&
                !string.Equals(serviceFilterKey, ConnectorServiceKey, StringComparison.OrdinalIgnoreCase))
                continue;

            var name = AppSettings.ServiceDisplayNames.TryGetValue(ConnectorServiceKey, out var n) ? n : ConnectorServiceKey;
            var description = DescribeConnectorKey(group.Key);
            yield return new HistoryTimelineEvent(
                ConnectorServiceKey,
                name,
                kind,
                hours.First().Start,
                hours.Last().Start.AddHours(1),
                $"{description} – ueber {hours.Count} Stunde(n) im lokalen Konnektor-Verlauf erfasst.");
        }
    }

    /// <summary>Beobachtungspunkt fuer den Konnektor-Verlauf, damit die Timeline einen Namen hat.</summary>
    public const string ConnectorServiceKey = "konnektor";

    private static bool TryParseHour(string hourKey, out DateTime value)
    {
        value = DateTime.MinValue;
        return hourKey.Length >= 13 &&
               DateTime.TryParseExact(hourKey[..13], "yyyy-MM-dd-HH",
                   CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    private static DateTime ParseHour(string hourKey) => TryParseHour(hourKey, out var value) ? value : DateTime.MinValue;

    /// <summary>
    /// Macht aus den Beobachtungskeys lesbare Texte. Die Keys stammen aus
    /// <see cref="ConnectorHealth.Evaluate"/> und koennen einen Anhaenger (Terminal-/Karten-
    /// Kennung oder PIN-Typ) tragen, der hier mit ausgegeben wird.
    /// </summary>
    private static string DescribeConnectorKey(string key)
    {
        // Die Keys stammen aus ConnectorHealth.Evaluate. "vpn-ti" und "no-smcb" enthalten
        // selbst schon einen Strich, ohne einen Anhaenger zu fuehren – deshalb vorrang
        // nach den mehrdeutigen Praefixen suchen, bevor gesplittet wird.
        foreach (var (prefix, format) in new[]
                 {
                     ("smcb-unverified-", "SMC-B {0} nicht PIN-verifiziert"),
                     ("cert-expired-", "Zertifikat der Karte {0} abgelaufen"),
                     ("cert-expiring-", "Zertifikat der Karte {0} laeuft ab"),
                     ("qes-expired-", "QES-Zertifikat der Karte {0} abgelaufen"),
                     ("pin-blocked-", "{1} der Karte {0} ist gesperrt"),
                     ("ct-", "Kartenterminal {0} nicht verbunden")
                 })
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var tail = key[prefix.Length..];
            // "2-PIN.CH" enthaelt Kartenkennung plus PIN-Typ – beides soll vorkommen.
            if (format.Contains("{1}"))
            {
                var split = tail.LastIndexOf('-');
                if (split <= 0)
                    return string.Format(format.Replace("{1}", "PIN"), tail, "");
                return string.Format(format, tail[..split], tail[(split + 1)..]);
            }
            return string.Format(format, tail);
        }

        return key switch
        {
            var k when k.Equals("vpn-ti", StringComparison.OrdinalIgnoreCase) => "TI-Anschluss offline",
            var k when k.Equals("no-smcb", StringComparison.OrdinalIgnoreCase) => "keine SMC-B gesteckt",
            _ => $"Beobachtungspunkt \u201E{key}\u201C gemeldet"
        };
    }

    private static string? KindFromIncidentStep(IncidentStep step)
    {
        // Align with HistoryStore.IncidentSeverity / live classifier: outage before maintenance.
        return step.Status switch
        {
            1 => "Störung",
            4 => "Teilausfall",
            _ => step.HasMaintenance ? "Wartung" : null
        };
    }

    /// <summary>
    /// Merge adjacent/overlapping intervals with the same ServiceKey + KindLabel
    /// (e.g. after status-2 carry splits one outage into many tiny cards).
    /// Prefers earliest StartLocal and extends EndLocal.
    /// </summary>
    private static List<HistoryTimelineEvent> MergeAdjacentSameKind(List<HistoryTimelineEvent> events)
    {
        var merged = new List<HistoryTimelineEvent>();
        foreach (var group in events.GroupBy(
                     e => (Service: e.ServiceKey.ToLowerInvariant(), e.KindLabel)))
        {
            HistoryTimelineEvent? current = null;
            foreach (var ev in group.OrderBy(e => e.StartLocal).ThenBy(e => e.EndLocal))
            {
                if (current is null)
                {
                    current = ev;
                    continue;
                }

                // Adjacent or overlapping: extend the open interval.
                if (ev.StartLocal <= current.EndLocal)
                {
                    var start = current.StartLocal <= ev.StartLocal ? current.StartLocal : ev.StartLocal;
                    var end = current.EndLocal >= ev.EndLocal ? current.EndLocal : ev.EndLocal;
                    current = new HistoryTimelineEvent(
                        current.ServiceKey,
                        current.ServiceName,
                        current.KindLabel,
                        start,
                        end,
                        current.Detail);
                }
                else
                {
                    merged.Add(current);
                    current = ev;
                }
            }

            if (current is not null)
                merged.Add(current);
        }

        return merged;
    }

    /// <summary>
    /// Drop outage Teilausfall entries that overlap an incident event for the same service.
    /// </summary>
    private static List<HistoryTimelineEvent> DedupeOverlapping(List<HistoryTimelineEvent> events)
    {
        var kept = new List<HistoryTimelineEvent>();
        foreach (var ev in events
                     .OrderByDescending(e => HistoryStore.StatusSeverity(e.StatusCode ?? "none"))
                     .ThenByDescending(e => e.StartLocal))
        {
            var redundantOutage = string.Equals(ev.KindLabel, "Teilausfall", StringComparison.Ordinal) &&
                kept.Any(k =>
                    !string.Equals(k.KindLabel, "Teilausfall", StringComparison.Ordinal) &&
                    string.Equals(k.ServiceKey, ev.ServiceKey, StringComparison.OrdinalIgnoreCase) &&
                    // Nur dieselbe Beobachtung schluckt, nicht alles am selben Dienst: sonst
                    // fiel eine getrennte Terminalstunde unter eine zeitgleiche Zertifikatswarnung.
                    string.Equals(k.Detail ?? "", ev.Detail ?? "", StringComparison.Ordinal) &&
                    k.StartLocal < ev.EndLocal && ev.StartLocal < k.EndLocal);
            if (redundantOutage)
                continue;
            kept.Add(ev);
        }

        return kept;
    }
}
