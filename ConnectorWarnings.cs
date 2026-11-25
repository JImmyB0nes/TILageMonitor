using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TILageMonitor;

/// <summary>Welche Art Zertifikat beobachtet wird.</summary>
public enum ConnectorCertificateKind
{
    SmcB,
    Hba,
    ClientSystem,
    /// <summary>Geraetekarte im Kartenterminal – am Ende der Enum, damit gespeicherte Werte gleich bleiben.</summary>
    SmcKt
}

/// <summary>Ein Zertifikat, dessen Ablauf beobachtet wird (I/O-frei).</summary>
public sealed record ConnectorCertificateSubject(string Key, ConnectorCertificateKind Kind, string Label, DateTime NotAfter);

/// <summary>
/// Gemerkter Stand der Warnungen, damit nach einem Neustart der App nicht dieselben Toasts
/// erneut erscheinen. Je Zertifikat die zuletzt gemeldete Stufe (90/30/7/0), je PIN der
/// zuletzt gesehene Zustand.
/// </summary>
public sealed class ConnectorWarningLedger
{
    [JsonPropertyName("certificateStages")]
    public Dictionary<string, int> CertificateStages { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("pinConditions")]
    public Dictionary<string, string> PinConditions { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Ablaufwarnung fuer SMC-B-, HBA-, gSMC-KT- und Clientsystem-Zertifikate in drei Stufen (90, 30, 7
/// Tage) plus "abgelaufen". Jede Stufe wird je Zertifikat genau einmal gemeldet. Ein
/// erneuertes Zertifikat (anderes Ablaufdatum) beginnt wieder von vorn.
/// </summary>
public static class ConnectorCertificateExpiry
{
    /// <summary>Warnstufen in Tagen, absteigend.</summary>
    public static readonly int[] Thresholds = [90, 30, 7];

    /// <summary>Stufe "abgelaufen".</summary>
    public const int ExpiredStage = 0;

    /// <summary>Ganze Resttage (abgerundet). Negativ, wenn abgelaufen.</summary>
    public static int DaysLeft(DateTime notAfter, DateTime now) => (int)Math.Floor((notAfter - now).TotalDays);

    /// <summary>
    /// Kleinste erreichte Stufe: 0 = abgelaufen, 7, 30, 90 – oder null, wenn mehr als 90 Tage bleiben.
    /// </summary>
    public static int? StageFor(int daysLeft)
    {
        if (daysLeft < 0)
            return ExpiredStage;
        int? stage = null;
        foreach (var threshold in Thresholds)
        {
            if (daysLeft <= threshold)
                stage = threshold;
        }
        return stage;
    }

    /// <summary>Ressourcenschluessel fuer Anzeige-Farben: Outage ≤ 7 Tage, Partial ≤ 30, Maintenance ≤ 90.</summary>
    public static string BrushKeyFor(int daysLeft) => StageFor(daysLeft) switch
    {
        ExpiredStage or 7 => "StatusOutage",
        30 => "StatusPartial",
        90 => "StatusMaintenance",
        _ => "StatusOk"
    };

    /// <summary>
    /// Sammelt die beobachteten Zertifikate: je SMC-B und HBA das frueheste bekannte Ablaufdatum,
    /// dazu das Clientsystem-Zertifikat (P12), falls vorhanden.
    /// </summary>
    public static List<ConnectorCertificateSubject> Collect(ConnectorSnapshot snapshot, string? clientSubject, DateTime? clientNotAfter)
    {
        var list = new List<ConnectorCertificateSubject>();
        foreach (var card in snapshot.Cards)
        {
            ConnectorCertificateKind kind;
            if (card.IsSmcB)
                kind = ConnectorCertificateKind.SmcB;
            else if (card.IsHba || card.IsHbaPredecessor)
                kind = ConnectorCertificateKind.Hba;
            else if (card.IsSmcKt)
                kind = ConnectorCertificateKind.SmcKt;
            else
                continue;

            // Alle bekannten Daten zusammen: GetCards liefert CertificateExpirationDate, der
            // Hintergrund liest daneben nur C.QES nach – das frueheste Datum zaehlt.
            var candidates = card.Certificates.Select(c => c.NotAfter)
                .Append(card.AuthCertificate?.NotAfter ?? default)
                .Append(card.CertificateExpirationDate ?? default)
                .Where(d => d != default)
                .ToList();
            if (candidates.Count == 0)
                continue;
            var notAfter = candidates.Min();
            var id = string.IsNullOrWhiteSpace(card.Iccsn) ? card.CardHandle : card.Iccsn;
            if (string.IsNullOrWhiteSpace(id))
                continue;
            var label = card.DisplayName;
            if (kind == ConnectorCertificateKind.SmcKt)
            {
                // Die gSMC-KT gehoert zum Terminal – dessen Name sagt mehr als die Kartenart.
                var ct = snapshot.Terminals.FirstOrDefault(t => string.Equals(t.CtId, card.CtId, StringComparison.OrdinalIgnoreCase));
                label = LocalizationService.Pick("Terminal ", "terminal ") + (string.IsNullOrWhiteSpace(ct?.Name) ? card.CtId : ct!.Name);
            }
            list.Add(new ConnectorCertificateSubject($"{kind}:{id}", kind, label, notAfter));
        }

        if (clientNotAfter is { } clientExpiry && clientExpiry != default)
        {
            var subject = string.IsNullOrWhiteSpace(clientSubject) ? "P12" : clientSubject!;
            list.Add(new ConnectorCertificateSubject($"{ConnectorCertificateKind.ClientSystem}:{subject}", ConnectorCertificateKind.ClientSystem,
                LocalizationService.Pick("Clientsystem-Zertifikat", "Client system certificate"), clientExpiry));
        }
        return list;
    }

    /// <summary>
    /// Liefert die faelligen Meldungen und traegt die gemeldeten Stufen in den Ledger ein.
    /// Eine Stufe wird nur gemeldet, wenn sie kleiner ist als die zuletzt gemeldete – so kommt
    /// jede Stufe einmal, und wer die App erst bei 20 Resttagen startet, bekommt nur die 30er.
    /// </summary>
    public static List<ConnectorAlert> Evaluate(IEnumerable<ConnectorCertificateSubject> subjects, ConnectorWarningLedger ledger, DateTime now)
    {
        var due = new List<ConnectorAlert>();
        foreach (var subject in subjects)
        {
            var days = DaysLeft(subject.NotAfter, now);
            if (StageFor(days) is not { } stage)
                continue;
            var key = LedgerKey(subject);
            if (ledger.CertificateStages.TryGetValue(key, out var previous) && previous <= stage)
                continue;
            ledger.CertificateStages[key] = stage;
            due.Add(BuildAlert(subject, days, stage));
        }

        // Alte Eintraege nicht ewig mitschleppen: Ablaufdatum ist im Schluessel, ein
        // ersetztes Zertifikat taucht nie wieder auf.
        foreach (var stale in ledger.CertificateStages.Keys.Where(k => IsStale(k, now)).ToList())
            ledger.CertificateStages.Remove(stale);
        return due;
    }

    internal static string LedgerKey(ConnectorCertificateSubject subject) => $"{subject.Key}|{subject.NotAfter:yyyyMMdd}";

    private static bool IsStale(string ledgerKey, DateTime now)
    {
        var bar = ledgerKey.LastIndexOf('|');
        if (bar < 0 || !DateTime.TryParseExact(ledgerKey[(bar + 1)..], "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var notAfter))
            return true;
        return notAfter < now.AddDays(-60);
    }

    private static ConnectorAlert BuildAlert(ConnectorCertificateSubject subject, int days, int stage)
    {
        var what = subject.Kind switch
        {
            ConnectorCertificateKind.SmcB => L($"SMC-B-Zertifikat ({subject.Label})", $"SMC-B certificate ({subject.Label})"),
            ConnectorCertificateKind.Hba => L($"HBA-Zertifikat ({subject.Label})", $"HBA certificate ({subject.Label})"),
            ConnectorCertificateKind.SmcKt => L($"gSMC-KT-Zertifikat ({subject.Label})", $"gSMC-KT certificate ({subject.Label})"),
            _ => subject.Label
        };
        var action = subject.Kind == ConnectorCertificateKind.ClientSystem
            ? L("Neues Clientsystem-Zertifikat in der Konnektor-Administration erzeugen und in der App hinterlegen.",
                "Create a new client system certificate in the connector administration and select it in the app.")
            : subject.Kind == ConnectorCertificateKind.SmcKt
                ? L("Neue gSMC-KT beim Terminal-Hersteller bzw. Dienstleister bestellen – ohne gültige gSMC-KT ist das Kartenterminal nicht nutzbar.",
                    "Order a new gSMC-KT from the terminal vendor or service provider – without a valid gSMC-KT the card terminal cannot be used.")
                : L("Folgekarte rechtzeitig beim Kartenherausgeber bestellen.", "Order a replacement card from the card issuer in time.");

        if (stage == ExpiredStage)
        {
            return new ConnectorAlert(
                $"certexp-{subject.Key}-expired",
                L($"{what} abgelaufen", $"{what} expired"),
                L($"Abgelaufen seit {subject.NotAfter:dd.MM.yyyy}. ", $"Expired on {subject.NotAfter:yyyy-MM-dd}. ") + action,
                ToastUrgency.Error);
        }

        return new ConnectorAlert(
            $"certexp-{subject.Key}-{stage}",
            L($"{what} läuft in {Math.Max(0, days)} Tagen ab", $"{what} expires in {Math.Max(0, days)} days"),
            L($"Gültig bis {subject.NotAfter:dd.MM.yyyy}. ", $"Valid until {subject.NotAfter:yyyy-MM-dd}. ") + action,
            stage <= 7 ? ToastUrgency.Error : ToastUrgency.Warning);
    }

    private static string L(string de, string en) => LocalizationService.Pick(de, en);
}

/// <summary>Zustand einer PIN aus Sicht der Warnung.</summary>
public enum ConnectorPinCondition
{
    Ok,
    NotVerified,
    LastTry,
    Blocked
}

/// <summary>
/// PIN-Warnungen (GetPinStatus): SMC-B nicht verifiziert, eine PIN gesperrt oder nur noch ein
/// Versuch. Gemeldet wird nur ein Zustandswechsel, nicht jeder Poll.
/// </summary>
public static class ConnectorPinWatch
{
    /// <summary>Bewertet eine PIN. Null, wenn der Status nicht gelesen werden konnte.</summary>
    public static ConnectorPinCondition? Classify(ConnectorCard card, ConnectorPinState pin)
    {
        if (pin.Error is not null || string.IsNullOrWhiteSpace(pin.Status))
            return null;
        if (pin.IsBlocked)
            return ConnectorPinCondition.Blocked;
        if (pin.LeftTries == 1 && !pin.IsVerified)
            return ConnectorPinCondition.LastTry;
        // Nur die SMC-B muss dauerhaft freigeschaltet sein. Ein HBA steht im Normalfall auf
        // VERIFIABLE und wird erst beim Signieren verifiziert – das ist kein Fehler.
        if (card.IsSmcB && string.Equals(pin.PinType, "PIN.SMC", StringComparison.OrdinalIgnoreCase)
            && !pin.IsVerified && !pin.IsTransportPin && !pin.IsDisabled)
            return ConnectorPinCondition.NotVerified;
        return ConnectorPinCondition.Ok;
    }

    /// <summary>
    /// Vergleicht mit dem gemerkten Zustand und liefert die faelligen Meldungen. Erstkontakt
    /// mit einem guten Zustand meldet nichts; ein schlechter Zustand wird beim ersten Sehen
    /// gemeldet (z. B. SMC-B nach Konnektor-Neustart), danach erst wieder beim Wechsel.
    /// Rueckkehr zu "in Ordnung" wird als Info gemeldet.
    /// </summary>
    public static List<ConnectorAlert> Evaluate(ConnectorSnapshot snapshot, ConnectorWarningLedger ledger)
    {
        var due = new List<ConnectorAlert>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var card in snapshot.Cards.Where(c => c.SupportsPinManagement))
        {
            var id = string.IsNullOrWhiteSpace(card.Iccsn) ? card.CardHandle : card.Iccsn;
            if (string.IsNullOrWhiteSpace(id))
                continue;
            foreach (var pin in card.PinStates)
            {
                if (Classify(card, pin) is not { } condition)
                    continue;
                var key = $"{id}|{pin.PinType}";
                seen.Add(key);
                var had = ledger.PinConditions.TryGetValue(key, out var previousText);
                var previous = had && Enum.TryParse<ConnectorPinCondition>(previousText, out var p) ? p : (ConnectorPinCondition?)null;
                ledger.PinConditions[key] = condition.ToString();

                if (previous == condition)
                    continue;
                if (condition == ConnectorPinCondition.Ok)
                {
                    if (previous is { } before && before != ConnectorPinCondition.Ok)
                        due.Add(Recovered(card, pin, before));
                    continue;
                }
                due.Add(Build(card, pin, condition));
            }
        }

        // Karten, die nicht mehr gesteckt sind, vergessen – nur wenn GetCards geantwortet hat.
        // Wird die SMC-B neu gesteckt und ist danach nicht freigeschaltet, ist das ein neuer
        // Wechsel und soll gemeldet werden.
        var cardsKnown = !snapshot.Warnings.Any(w => w.Contains("GetCards", StringComparison.Ordinal));
        if (cardsKnown)
        {
            var presentIds = snapshot.Cards
                .Select(c => string.IsNullOrWhiteSpace(c.Iccsn) ? c.CardHandle : c.Iccsn)
                .Where(i => !string.IsNullOrWhiteSpace(i))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var key in ledger.PinConditions.Keys.ToList())
            {
                var cardId = key.Split('|')[0];
                if (!presentIds.Contains(cardId))
                    ledger.PinConditions.Remove(key);
            }
        }
        return due;
    }

    private static ConnectorAlert Build(ConnectorCard card, ConnectorPinState pin, ConnectorPinCondition condition)
    {
        var id = string.IsNullOrWhiteSpace(card.Iccsn) ? card.CardHandle : card.Iccsn;
        return condition switch
        {
            ConnectorPinCondition.Blocked => new ConnectorAlert(
                $"pinwatch-{id}-{pin.PinType}-blocked",
                L($"{card.DisplayName}: {pin.PinType} gesperrt", $"{card.DisplayName}: {pin.PinType} blocked"),
                L("Die PIN ist gesperrt und muss mit der PUK am Kartenterminal entsperrt werden.", "The PIN is blocked and must be unblocked with the PUK on the card terminal."),
                ToastUrgency.Error),
            ConnectorPinCondition.LastTry => new ConnectorAlert(
                $"pinwatch-{id}-{pin.PinType}-lasttry",
                L($"{card.DisplayName}: nur noch 1 PIN-Versuch", $"{card.DisplayName}: only 1 PIN attempt left"),
                L($"{pin.PinType} hat nur noch einen Versuch. Eine weitere Fehleingabe sperrt die PIN.", $"{pin.PinType} has only one attempt left. One more wrong entry blocks the PIN."),
                ToastUrgency.Error),
            _ => new ConnectorAlert(
                $"pinwatch-{id}-{pin.PinType}-unverified",
                L("SMC-B nicht freigeschaltet", "SMC-B not unlocked"),
                L($"{card.DisplayName} ist nicht PIN-verifiziert (z. B. nach einem Neustart des Konnektors). eRezept, ePA, KIM und VSDM benötigen eine freigeschaltete SMC-B – bitte im Primärsystem bzw. am Kartenterminal freischalten.",
                  $"{card.DisplayName} is not PIN-verified (e.g. after a connector restart). eRezept, ePA, KIM and VSDM require an unlocked SMC-B – please unlock it via the practice software or card terminal."),
                ToastUrgency.Warning)
        };
    }

    private static ConnectorAlert Recovered(ConnectorCard card, ConnectorPinState pin, ConnectorPinCondition before)
    {
        var id = string.IsNullOrWhiteSpace(card.Iccsn) ? card.CardHandle : card.Iccsn;
        var text = before == ConnectorPinCondition.NotVerified
            ? L($"{card.DisplayName} ist wieder freigeschaltet.", $"{card.DisplayName} is unlocked again.")
            : L($"{pin.PinType} ist wieder in Ordnung.", $"{pin.PinType} is fine again.");
        return new ConnectorAlert($"pinwatch-{id}-{pin.PinType}-ok", L($"{card.DisplayName}: PIN in Ordnung", $"{card.DisplayName}: PIN OK"), text, ToastUrgency.Info);
    }

    private static string L(string de, string en) => LocalizationService.Pick(de, en);
}

/// <summary>Ablage des Warn-Ledgers (atomar geschrieben, Fehler werden geschluckt).</summary>
public static class ConnectorWarningStore
{
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string LedgerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TILageMonitor", "connector-warnings.json");

    /// <summary>Laedt, veraendert und speichert unter einer Sperre.</summary>
    public static T Update<T>(Func<ConnectorWarningLedger, T> change)
    {
        lock (Gate)
        {
            var ledger = Load();
            var result = change(ledger);
            try
            {
                AtomicJsonStore.Write(LedgerPath, ledger, JsonOptions);
            }
            catch
            {
                // Ohne Ablage drohen nur doppelte Toasts nach einem Neustart – kein Absturz.
            }
            return result;
        }
    }

    private static ConnectorWarningLedger Load()
    {
        try
        {
            if (!File.Exists(LedgerPath))
                return new ConnectorWarningLedger();
            var ledger = JsonSerializer.Deserialize<ConnectorWarningLedger>(File.ReadAllText(LedgerPath), JsonOptions) ?? new ConnectorWarningLedger();
            ledger.CertificateStages = new Dictionary<string, int>(ledger.CertificateStages ?? new(), StringComparer.Ordinal);
            ledger.PinConditions = new Dictionary<string, string>(ledger.PinConditions ?? new(), StringComparer.Ordinal);
            return ledger;
        }
        catch
        {
            return new ConnectorWarningLedger();
        }
    }
}

/// <summary>
/// Welche Warnungen aus <see cref="ConnectorHealth"/> nicht mehr selbst einen Toast ausloesen,
/// weil Ablauf- und PIN-Warnung sie mit eigenem, neustartfestem Gedaechtnis melden. In der
/// Anzeige (Hinweise, Verlauf) bleiben sie erhalten.
/// </summary>
public static class ConnectorToastOwnership
{
    private static readonly string[] LedgerOwnedPrefixes =
    [
        "cert-expiring-", "cert-expired-", "qes-expired-", "pin-blocked-", "pin-lasttry-", "smcb-unverified-"
    ];

    public static bool IsOwnedByLedger(string alertKey) =>
        LedgerOwnedPrefixes.Any(p => alertKey.StartsWith(p, StringComparison.Ordinal));
}
