using System.Globalization;

namespace TILageMonitor;

/// <summary>Zeilen des Konnektor-Verlaufs.</summary>
public enum ConnectorHistoryLane
{
    Reachable,
    VpnTi,
    VpnSis,
    SmcB,
    Hba
}

/// <summary>
/// Zustand einer Stunde bzw. eines Tages in einer Zeile. Bei Karten bedeutet Ok "gesteckt",
/// Down "nicht gesteckt", Partial "zeitweise".
/// </summary>
public enum ConnectorHistoryState
{
    NoData,
    Ok,
    Partial,
    Down
}

public sealed class ConnectorHistoryCell
{
    public ConnectorHistoryState State { get; init; }

    /// <summary>Anteil "gut" in Prozent (erreichbar bzw. gesteckt), null ohne Daten.</summary>
    public int? Percent { get; init; }
}

public sealed class ConnectorHistoryDay
{
    public DateTime Date { get; init; }

    /// <summary>Zusammenfassung des Tages je Zeile.</summary>
    public Dictionary<ConnectorHistoryLane, ConnectorHistoryCell> Summary { get; } = new();

    /// <summary>24 Stundenzellen je Zeile (Index = Stunde).</summary>
    public Dictionary<ConnectorHistoryLane, ConnectorHistoryCell[]> Hours { get; } = new();

    /// <summary>Stunden mit Problem (nicht erreichbar oder VPN TI getrennt).</summary>
    public int ProblemHours { get; set; }

    /// <summary>Stunden mit irgendeinem Datenpunkt.</summary>
    public int CoveredHours { get; set; }
}

/// <summary>
/// Baut aus den Stundenstaenden des Konnektor-Verlaufs die 14-Tage-Ansicht (I/O-frei, testbar).
/// </summary>
public static class ConnectorHistoryTimeline
{
    public static readonly ConnectorHistoryLane[] Lanes =
    [
        ConnectorHistoryLane.Reachable, ConnectorHistoryLane.VpnTi, ConnectorHistoryLane.VpnSis,
        ConnectorHistoryLane.SmcB, ConnectorHistoryLane.Hba
    ];

    /// <summary>Stundenwert einer Zeile.</summary>
    public static ConnectorHistoryCell Evaluate(ConnectorHourSnapshot? hour, ConnectorHistoryLane lane)
    {
        if (hour is null)
            return new ConnectorHistoryCell { State = ConnectorHistoryState.NoData };

        switch (lane)
        {
            case ConnectorHistoryLane.Reachable:
            {
                var total = hour.Polls + hour.Unreachable;
                if (total == 0)
                {
                    // Aeltere Eintraege (vor 3.1) kennen keine Zaehler: wer gespeichert wurde, war erreichbar.
                    return HasLegacyData(hour)
                        ? new ConnectorHistoryCell { State = ConnectorHistoryState.Ok, Percent = 100 }
                        : new ConnectorHistoryCell { State = ConnectorHistoryState.NoData };
                }
                var pct = (int)Math.Round(100.0 * hour.Polls / total);
                var state = hour.Unreachable == 0 ? ConnectorHistoryState.Ok
                    : hour.Polls == 0 ? ConnectorHistoryState.Down
                    : ConnectorHistoryState.Partial;
                return new ConnectorHistoryCell { State = state, Percent = pct };
            }
            case ConnectorHistoryLane.VpnTi:
                return Flag(hour.TiOnline);
            case ConnectorHistoryLane.VpnSis:
                return Flag(hour.SisOnline);
            case ConnectorHistoryLane.SmcB:
                return Presence(hour.SmcbPolls, hour.CardPolls);
            case ConnectorHistoryLane.Hba:
                return Presence(hour.HbaPolls, hour.CardPolls);
            default:
                return new ConnectorHistoryCell { State = ConnectorHistoryState.NoData };
        }
    }

    /// <summary>
    /// Die letzten <paramref name="days"/> Tage bis einschliesslich <paramref name="today"/>,
    /// aeltester zuerst. Stunden aus dem Store werden nach ihrem lokalen Datum einsortiert.
    /// </summary>
    public static List<ConnectorHistoryDay> Build(ConnectorHistoryFile file, DateTime today, int days = ConnectorHistoryStore.KeepDays)
    {
        var byDayHour = new Dictionary<(DateTime Day, int Hour), ConnectorHourSnapshot>();
        foreach (var hour in file.Hours ?? [])
        {
            if (!TryParseHourKey(hour.HourKey, out var stamp))
                continue;
            // Doppelte Schluessel (Zeitumstellung) zusammenlegen: der schlechtere Stand gewinnt.
            var key = (stamp.Date, stamp.Hour);
            byDayHour[key] = byDayHour.TryGetValue(key, out var existing) ? Merge(existing, hour) : hour;
        }

        var result = new List<ConnectorHistoryDay>();
        for (var offset = days - 1; offset >= 0; offset--)
        {
            var date = today.Date.AddDays(-offset);
            var day = new ConnectorHistoryDay { Date = date };
            foreach (var lane in Lanes)
                day.Hours[lane] = new ConnectorHistoryCell[24];

            for (var h = 0; h < 24; h++)
            {
                byDayHour.TryGetValue((date, h), out var snapshot);
                if (snapshot is not null)
                    day.CoveredHours++;
                var problem = false;
                foreach (var lane in Lanes)
                {
                    var cell = Evaluate(snapshot, lane);
                    day.Hours[lane][h] = cell;
                    if ((lane is ConnectorHistoryLane.Reachable or ConnectorHistoryLane.VpnTi) &&
                        cell.State is ConnectorHistoryState.Down or ConnectorHistoryState.Partial)
                        problem = true;
                }
                if (problem)
                    day.ProblemHours++;
            }

            foreach (var lane in Lanes)
                day.Summary[lane] = Summarize(day.Hours[lane]);
            result.Add(day);
        }
        return result;
    }

    /// <summary>Tageswert aus 24 Stunden: alle gut = Ok, alle schlecht = Down, sonst Partial.</summary>
    public static ConnectorHistoryCell Summarize(IEnumerable<ConnectorHistoryCell> hours)
    {
        var withData = hours.Where(c => c.State != ConnectorHistoryState.NoData).ToList();
        if (withData.Count == 0)
            return new ConnectorHistoryCell { State = ConnectorHistoryState.NoData };
        var good = withData.Count(c => c.State == ConnectorHistoryState.Ok);
        var bad = withData.Count(c => c.State == ConnectorHistoryState.Down);
        var state = good == withData.Count ? ConnectorHistoryState.Ok
            : bad == withData.Count ? ConnectorHistoryState.Down
            : ConnectorHistoryState.Partial;
        var pct = (int)Math.Round(withData.Average(c => c.Percent ?? (c.State == ConnectorHistoryState.Ok ? 100 : 0)));
        return new ConnectorHistoryCell { State = state, Percent = pct };
    }

    internal static bool TryParseHourKey(string? key, out DateTime stamp)
    {
        stamp = default;
        if (string.IsNullOrWhiteSpace(key) || key.Length < 13)
            return false;
        return DateTime.TryParseExact(key[..13], "yyyy-MM-dd-HH", CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp);
    }

    private static bool HasLegacyData(ConnectorHourSnapshot hour) =>
        hour.TiOnline is not null || hour.Cards > 0 || hour.Terminals > 0 || hour.States.Count > 0;

    private static ConnectorHistoryCell Flag(bool? online) => online switch
    {
        true => new ConnectorHistoryCell { State = ConnectorHistoryState.Ok, Percent = 100 },
        false => new ConnectorHistoryCell { State = ConnectorHistoryState.Down, Percent = 0 },
        _ => new ConnectorHistoryCell { State = ConnectorHistoryState.NoData }
    };

    private static ConnectorHistoryCell Presence(int present, int total)
    {
        if (total <= 0)
            return new ConnectorHistoryCell { State = ConnectorHistoryState.NoData };
        var pct = (int)Math.Round(100.0 * Math.Min(present, total) / total);
        var state = present >= total ? ConnectorHistoryState.Ok
            : present <= 0 ? ConnectorHistoryState.Down
            : ConnectorHistoryState.Partial;
        return new ConnectorHistoryCell { State = state, Percent = pct };
    }

    private static ConnectorHourSnapshot Merge(ConnectorHourSnapshot a, ConnectorHourSnapshot b) => new()
    {
        HourKey = a.HourKey,
        States = new Dictionary<string, string>(a.States.Concat(b.States.Where(kv => !a.States.ContainsKey(kv.Key))).ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.OrdinalIgnoreCase),
        Cards = Math.Max(a.Cards, b.Cards),
        Terminals = Math.Max(a.Terminals, b.Terminals),
        TiOnline = a.TiOnline == false || b.TiOnline == false ? false : a.TiOnline ?? b.TiOnline,
        SisOnline = a.SisOnline == false || b.SisOnline == false ? false : a.SisOnline ?? b.SisOnline,
        Polls = a.Polls + b.Polls,
        Unreachable = a.Unreachable + b.Unreachable,
        CardPolls = a.CardPolls + b.CardPolls,
        SmcbPolls = a.SmcbPolls + b.SmcbPolls,
        HbaPolls = a.HbaPolls + b.HbaPolls
    };
}
