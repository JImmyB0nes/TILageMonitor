using System.Globalization;

namespace TILageMonitor;

/// <summary>
/// Uhrzeitvergleich Konnektor ↔ PC. Gemessen wird am HTTP-Date-Header der Konnektor-Antwort,
/// bezogen auf die Mitte der Anfrage (halbe Laufzeit). Der Header hat Sekundengenauigkeit –
/// das reicht: TLS, Kerberos und Signaturzeitstempel kippen erst bei Minuten.
/// </summary>
public static class ConnectorClock
{
    /// <summary>Ab dieser Abweichung wird gewarnt.</summary>
    public static readonly TimeSpan WarningThreshold = TimeSpan.FromMinutes(2);

    /// <summary>Ab dieser Abweichung gilt es als Fehler (TLS-/Signaturprobleme wahrscheinlich).</summary>
    public static readonly TimeSpan ErrorThreshold = TimeSpan.FromMinutes(5);

    /// <summary>Messung verwerfen, wenn die Anfrage zu lange dauerte – dann ist die Mitte zu ungenau.</summary>
    public static readonly TimeSpan MaxRoundTrip = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Abweichung aus dem Date-Header. Positiv: der Konnektor geht vor. Null, wenn kein oder
    /// ein unlesbarer Header kam oder die Laufzeit die Messung unbrauchbar macht.
    /// </summary>
    public static TimeSpan? OffsetFromDateHeader(string? dateHeader, DateTimeOffset sentUtc, DateTimeOffset receivedUtc)
    {
        if (string.IsNullOrWhiteSpace(dateHeader) || receivedUtc < sentUtc || receivedUtc - sentUtc > MaxRoundTrip)
            return null;

        var text = dateHeader.Trim();
        if (!DateTimeOffset.TryParseExact(text, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var server) &&
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out server))
            return null;

        var middle = sentUtc + TimeSpan.FromTicks((receivedUtc - sentUtc).Ticks / 2);
        var offset = server - middle;
        // Der Header schneidet auf volle Sekunden ab. Abweichungen unter einer Sekunde sind
        // Messrauschen, keine Aussage ueber die Uhr.
        return offset.Duration() < TimeSpan.FromSeconds(1) ? TimeSpan.Zero : offset;
    }

    /// <summary>Warnung zur Zeitabweichung, null wenn sie im Rahmen liegt.</summary>
    public static ConnectorAlert? Evaluate(TimeSpan? offset)
    {
        if (offset is not { } value || value.Duration() < WarningThreshold)
            return null;

        var direction = value > TimeSpan.Zero
            ? L("vor", "ahead")
            : L("nach", "behind");
        var amount = Format(value);
        return new ConnectorAlert(
            "clock-drift",
            L("Konnektor-Uhrzeit weicht ab", "Connector clock is off"),
            L($"Die Uhr des Konnektors geht gegenüber diesem PC {amount} {direction}. Abweichende Uhrzeiten führen zu TLS-Fehlern, " +
              "ungültigen Signaturzeitpunkten und abgelehnten Anmeldungen – bitte Zeitsynchronisation (NTP) von Konnektor und PC prüfen.",
              $"The connector's clock is {amount} {direction} of this PC. Clock differences cause TLS errors, invalid signature times " +
              "and rejected logins – please check time synchronisation (NTP) of connector and PC."),
            value.Duration() >= ErrorThreshold ? ToastUrgency.Error : ToastUrgency.Warning);
    }

    /// <summary>Lesbare Dauer ohne Vorzeichen, z. B. "3 min 20 s" oder "1 h 5 min".</summary>
    public static string Format(TimeSpan offset)
    {
        var d = offset.Duration();
        if (d < TimeSpan.FromMinutes(1))
            return $"{(int)d.TotalSeconds} s";
        if (d < TimeSpan.FromHours(1))
            return d.Seconds == 0 ? $"{(int)d.TotalMinutes} min" : $"{(int)d.TotalMinutes} min {d.Seconds} s";
        if (d < TimeSpan.FromDays(1))
            return d.Minutes == 0 ? $"{(int)d.TotalHours} h" : $"{(int)d.TotalHours} h {d.Minutes} min";
        return L($"{(int)d.TotalDays} Tage", $"{(int)d.TotalDays} days");
    }

    /// <summary>Kurztext für Anzeige und Bericht.</summary>
    public static string Describe(TimeSpan? offset)
    {
        if (offset is not { } value)
            return L("nicht messbar (kein Date-Header)", "not measurable (no Date header)");
        if (value == TimeSpan.Zero)
            return L("synchron (unter 1 s)", "in sync (below 1 s)");
        return value > TimeSpan.Zero
            ? L($"Konnektor {Format(value)} vor", $"connector {Format(value)} ahead")
            : L($"Konnektor {Format(value)} nach", $"connector {Format(value)} behind");
    }

    private static string L(string de, string en) => LocalizationService.Pick(de, en);
}
