using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TILageMonitor;

/// <summary>
/// Staendliche Konnektor-Snapshot fuer den lokalen Verlauf. Anders als die gematik-Dienste
/// laufen Karten- und Terminal-Stoerungen nicht ueber die API, sondern ausschliesslich ueber
/// den lokalen Konnektor – ohne diese Datei waere der Verlauf fuer die TI-Anbindung leer.
/// </summary>
public sealed class ConnectorHourSnapshot
{
    [JsonPropertyName("hourKey")]
    public string HourKey { get; set; } = "";

    /// <summary>Schlechtester Status je Beobachtungspunkt (vpn-ti, ct-CT1, smcb-…), Wert = Urgency.</summary>
    [JsonPropertyName("states")]
    public Dictionary<string, string> States { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Kurzzaehler pro Stunde, damit die Timeline ohne Roheinträge rekonstruiert werden kann.</summary>
    [JsonPropertyName("cards")]
    public int Cards { get; set; }

    [JsonPropertyName("terminals")]
    public int Terminals { get; set; }

    [JsonPropertyName("tiOnline")]
    public bool? TiOnline { get; set; }

    /// <summary>VPN SIS (Internet über den Konnektor) – schlechtester Stand der Stunde.</summary>
    [JsonPropertyName("sisOnline")]
    public bool? SisOnline { get; set; }

    /// <summary>Erfolgreiche Abrufe in dieser Stunde (Konnektor erreichbar).</summary>
    [JsonPropertyName("polls")]
    public int Polls { get; set; }

    /// <summary>Fehlgeschlagene Abrufe in dieser Stunde (Konnektor nicht erreichbar).</summary>
    [JsonPropertyName("unreachable")]
    public int Unreachable { get; set; }

    /// <summary>Abrufe, in denen GetCards geantwortet hat – Bezugsgröße für die Kartenanwesenheit.</summary>
    [JsonPropertyName("cardPolls")]
    public int CardPolls { get; set; }

    /// <summary>Davon: SMC-B gesteckt.</summary>
    [JsonPropertyName("smcbPolls")]
    public int SmcbPolls { get; set; }

    /// <summary>Davon: HBA gesteckt.</summary>
    [JsonPropertyName("hbaPolls")]
    public int HbaPolls { get; set; }
}

public sealed class ConnectorHistoryFile
{
    [JsonPropertyName("hours")]
    public List<ConnectorHourSnapshot> Hours { get; set; } = new();
}

public static class ConnectorHistoryStore
{
    public const int KeepDays = 14;
    private const int HoursPerDay = 24;

    /// <summary>
    /// Haelt die Folge Laden – Aendern – Speichern zusammen. Ohne diese Klappe koennen der
    /// Hintergrundpoller und ein Aufruf aus dem Konnektor-Fenster gleichzeitig denselben Stand
    /// aufmachen, beide fuergen ihre Meldungen ein, und wer zuletzt schreibt, loescht die des
    /// anderen mitsamt der Datei. Bei einem fruehen Schreibversuch auf leere Menge wuerde aus
    /// der Datei eine Stunde ohne Eintrag.
    /// </summary>
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TILageMonitor");

    public static string HistoryPath => Path.Combine(DirectoryPath, "connector-history.json");

    public static ConnectorHistoryFile Load()
    {
        try
        {
            if (!File.Exists(HistoryPath))
                return new ConnectorHistoryFile();
            var file = JsonSerializer.Deserialize<ConnectorHistoryFile>(File.ReadAllText(HistoryPath), JsonOptions)
                       ?? new ConnectorHistoryFile();
            file.Hours ??= new List<ConnectorHourSnapshot>();
            return file;
        }
        catch
        {
            return new ConnectorHistoryFile();
        }
    }

    public static void Save(ConnectorHistoryFile file)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var temp = HistoryPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions));
            File.Move(temp, HistoryPath, overwrite: true);
        }
        catch
        {
            // optional – IO-Fehler (Disk voll, Recht) nicht zum Absturz der App fuehren lassen
        }
    }

    /// <summary>
    /// Uebernimmt einen Datenstand in die aktuelle Stunde. Es wird nur verschaerft, niemals
    /// entschaerft: ein einzelner Fehler in einer Stunde soll in der Timeline sichtbar bleiben.
    /// </summary>
    public static ConnectorHistoryFile UpsertNow(ConnectorSnapshot snapshot, DateTime now)
    {
        lock (Gate)
            return UpsertLocked(snapshot, now);
    }

    private static ConnectorHistoryFile UpsertLocked(ConnectorSnapshot snapshot, DateTime now)
    {
        var file = Load();
        Apply(file, snapshot, now);
        Prune(file, now.Date);
        Save(file);
        return file;
    }

    /// <summary>
    /// Vermerkt einen fehlgeschlagenen Abruf (Konnektor nicht erreichbar) in der aktuellen Stunde.
    /// </summary>
    public static ConnectorHistoryFile RecordUnreachable(DateTime now)
    {
        lock (Gate)
        {
            var file = Load();
            ApplyUnreachable(file, now);
            Prune(file, now.Date);
            Save(file);
            return file;
        }
    }

    /// <summary>Stunde zum Zeitpunkt anlegen oder holen (I/O-frei).</summary>
    internal static ConnectorHourSnapshot HourFor(ConnectorHistoryFile file, DateTime now)
    {
        file.Hours ??= new List<ConnectorHourSnapshot>();
        var hourKey = HistoryStore.GetHourKey(new DateTimeOffset(now, TimeZoneInfo.Local.GetUtcOffset(now)));
        var hour = file.Hours.FirstOrDefault(h => h.HourKey == hourKey);
        if (hour is null)
        {
            hour = new ConnectorHourSnapshot { HourKey = hourKey };
            file.Hours.Add(hour);
        }
        return hour;
    }

    /// <summary>Uebernimmt einen erfolgreichen Abruf in die Stunde (I/O-frei, testbar).</summary>
    public static void Apply(ConnectorHistoryFile file, ConnectorSnapshot snapshot, DateTime now)
    {
        var hour = HourFor(file, now);

        foreach (var alert in ConnectorHealth.Evaluate(snapshot, 60, now))
        {
            // Gleiche Rangfolge wie im UI-Verlauf, damit ein Eintrag ohne Weiteres daneben
            // ausgewertet werden kann: Error = "full", Warning = "partial".
            var severity = alert.Urgency == ToastUrgency.Error ? "full" : "partial";
            if (!hour.States.TryGetValue(alert.Key, out var previous) ||
                HistoryStore.StatusSeverity(severity) > HistoryStore.StatusSeverity(previous))
            {
                hour.States[alert.Key] = severity;
            }
        }

        hour.Cards = Math.Max(hour.Cards, snapshot.Cards.Count);
        hour.Terminals = Math.Max(hour.Terminals, snapshot.Terminals.Count);
        if (snapshot.Resource?.IsTiOnline is { } online)
            hour.TiOnline = hour.TiOnline == false ? false : online;
        if (snapshot.Resource?.IsSisOnline is { } sis)
            hour.SisOnline = hour.SisOnline == false ? false : sis;

        hour.Polls++;
        var cardsKnown = !snapshot.Warnings.Any(w => w.Contains("GetCards", StringComparison.Ordinal));
        if (cardsKnown)
        {
            hour.CardPolls++;
            if (snapshot.Cards.Any(c => c.IsSmcB))
                hour.SmcbPolls++;
            if (snapshot.Cards.Any(c => c.IsHba || c.IsHbaPredecessor))
                hour.HbaPolls++;
        }
    }

    /// <summary>Vermerkt einen Fehlschlag in der Stunde (I/O-frei, testbar).</summary>
    public static void ApplyUnreachable(ConnectorHistoryFile file, DateTime now) => HourFor(file, now).Unreachable++;

    /// <summary>Deckung der letzten Stunden – Abschaetzung, ob der Verlauf ueberhaupt traegt.</summary>
    public static int CountCoveredHours(ConnectorHistoryFile file) =>
        file.Hours.Select(h => h.HourKey).Distinct(StringComparer.Ordinal).Count();

    /// <summary>Entfernt Stunden, die älter als 14 Tage sind (heute zählt mit).</summary>
    public static void Prune(ConnectorHistoryFile file, DateTime todayLocal)
    {
        var oldest = todayLocal.AddDays(-(KeepDays - 1));
        file.Hours.RemoveAll(h =>
            DateTime.TryParseExact(h.HourKey.Substring(0, Math.Min(13, h.HourKey.Length)),
                "yyyy-MM-dd-HH", CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp)
            && stamp < oldest);
    }
}
