using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TILageMonitor;

/// <summary>
/// Vergleich der Konnektor-Konfiguration mit dem zuletzt gespeicherten Stand desselben
/// Rechners. Es wird keine Referenzdatei gepflegt – die App merkt sich einfach, wie der
/// Konnektor zuletzt aussah, und meldet jede Verstellung.
///
/// Warum das haelt: Die typische Stoerung "seit heute morgen geht nichts mehr" ist selten ein
/// Totalausfall, sondern eine still gerollte Firmware, eine neue Dienstversion oder ein
/// Terminal, das aus der Konfiguration geflogen ist. Genau das faellt sonst erst beim
/// naechsten Fehlversuch auf.
/// </summary>
public static class ConnectorConfigurationDiff
{
    private const string DirectoryName = "TILageMonitor";
    private const string FileName = "connector-config.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), DirectoryName);

    public static string ConfigPath => Path.Combine(DirectoryPath, FileName);

    /// <summary>Ein einzelner Konfigurationsstand, kanonisch serialisiert und als Hash abgelegt.</summary>
    public sealed class State
    {
        [JsonPropertyName("capturedAt")]
        public DateTime CapturedAt { get; set; }

        [JsonPropertyName("fingerprint")]
        public string Fingerprint { get; set; } = "";

        [JsonPropertyName("lines")]
        public List<string> Lines { get; set; } = new();
    }

    public static State? Load()
    {
        try
        {
            return File.Exists(ConfigPath)
                ? JsonSerializer.Deserialize<State>(File.ReadAllText(ConfigPath), JsonOptions)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(State state)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var temp = ConfigPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temp, ConfigPath, overwrite: true);
        }
        catch
        {
            // optional – ein Schreibfehler darf die Anzeige nicht kippen
        }
    }

    /// <summary>
    /// Baut die kanonische Liste aller Konfigurationsmerkmale. Bewusst sortiert und ohne
    /// Zeitwerte, damit ein normaler Poll nicht als Aenderung durchgeht.
    /// </summary>
    public static List<string> Describe(ConnectorSnapshot snapshot)
    {
        var lines = new List<string>();
        var dir = snapshot.Directory;
        if (dir is not null)
        {
            Add(lines, "hersteller", $"{dir.ProductVendorName} {dir.ProductName}".Trim());
            Add(lines, "typ", $"{dir.ProductType} {dir.ProductTypeVersion}".Trim());
            Add(lines, "firmware", dir.FirmwareVersion);
            Add(lines, "hardware", dir.HardwareVersion);
            foreach (var service in dir.Services
                         .OrderBy(s => s.Name, StringComparer.Ordinal)
                         .ThenBy(s => s.Version, StringComparer.Ordinal))
            {
                Add(lines, "dienst", $"{service.Name}={service.Version}");
            }
        }

        foreach (var terminal in snapshot.Terminals.OrderBy(t => t.CtId, StringComparer.Ordinal))
        {
            Add(lines, "terminal", $"{terminal.CtId}={terminal.Label}|{terminal.Details}|" +
                                   $"{(terminal.Connected == true ? "verbunden" : terminal.Connected == false ? "getrennt" : "unbekannt")}");
        }

        foreach (var card in snapshot.Cards.OrderBy(c => c.Iccsn, StringComparer.Ordinal).ThenBy(c => c.CardType, StringComparer.Ordinal))
        {
            Add(lines, "karte", $"{card.Iccsn}={card.CardType}|{card.CtId}/{card.SlotId}");
        }

        return lines;
    }

    /// <summary>
    /// Traegt den aktuellen Stand ein und liefert die Unterschiede zum vorigen. Beim ersten
    /// Start (noch kein Stand) bleibt die Liste leer, damit niemand gleich nach dem Einrichten
    /// zugaetigt wird.
    /// </summary>
    public static IReadOnlyList<string> CompareAndRemember(ConnectorSnapshot snapshot)
    {
        var current = Describe(snapshot);
        var previous = Load();
        var diff = Diff(previous?.Lines ?? [], current);

        Save(new State
        {
            CapturedAt = DateTime.Now,
            Fingerprint = Fingerprint(current),
            Lines = current
        });

        return previous is null ? [] : diff;
    }

    /// <summary>Reine Differenz zweier Merkmalsserien: "neu", "entfernt", "geaendert".</summary>
    public static List<string> Diff(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var changes = new List<string>();
        var beforeKeys = Group(before);
        var afterKeys = Group(after);

        foreach (var (key, value) in afterKeys.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!beforeKeys.TryGetValue(key, out var old))
                changes.Add($"+ {key}: {value}");
            else if (old != value)
                changes.Add($"~ {key}: {old} → {value}");
        }

        foreach (var key in beforeKeys.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!afterKeys.ContainsKey(key))
                changes.Add($"- {key}: entfallen");
        }

        return changes;
    }

    /// <summary>
    /// Zerlegt die Merkmalsserien in (kennung) → inhalt. Kennung ist bei einzweiligen Merkmalen
    /// die Art selbst ("firmware"), bei mehrfachen Eintraegen Art plus erster Abschnitt, damit
    /// zwei Terminals nicht als ein Merkmal mit wechselndem Inhalt durchgehen.
    /// </summary>
    private static Dictionary<string, string> Group(IReadOnlyList<string> lines)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;
            var kind = line[..separator].Trim();
            var body = line[(separator + 1)..].Trim();
            // Eintraege mit eigener Kennung (=) bekommen diese als Unterkey, damit zwei
            // Terminals nicht als ein Merkmal mit wechselndem Inhalt durchgehen.
            var equals = body.IndexOf('=');
            var key = equals > 0 ? $"{kind} {body[..equals]}" : kind;
            map[key] = body;
        }
        return map;
    }

    public static string Fingerprint(IReadOnlyList<string> lines)
    {
        var canonical = string.Join("\n", lines.OrderBy(l => l, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }

    /// <summary>Kurzfassung fuer den Diagnosebericht: was hat sich zuletzt verandert.</summary>
    public static string DescribeLastChange()
    {
        var state = Load();
        return state is null
            ? LocalizationService.Pick("noch kein Konfigurationsstand gespeichert", "no configuration state stored yet")
            : LocalizationService.Pick($"zuletzt gemerkt am {state.CapturedAt:dd.MM.yyyy HH:mm} ({state.Fingerprint})",
                $"last stored {state.CapturedAt:yyyy-MM-dd HH:mm} ({state.Fingerprint})");
    }

    private static void Add(List<string> lines, string kind, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            lines.Add($"{kind}: {value.Trim()}");
    }
}
