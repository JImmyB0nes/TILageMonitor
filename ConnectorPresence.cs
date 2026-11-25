namespace TILageMonitor;

/// <summary>
/// Anwesenheit einer Karte, abgeleitet entweder aus einem hereingekommenen Ereignis oder aus
/// einem Datenstand. Beides führt zu derselben Anzeige – der Unterschied steckt nur in der
/// Herkunft, die man getrennt sehen will: Ein Ereignis ist Minuten bis Stunden früher als der
/// nächste Poll, und wer das nicht getrennt bekommt, hält die Anzeige für schleppend, obwohl
/// der Abo-Pfad längst funktioniert.
/// </summary>
public sealed class ConnectorPresence
{
    /// <summary>Zeitfenster, in dem die Anzeige noch als "gerade eben" gilt.</summary>
    public const int LiveWindowSeconds = 45;

    public bool Inserted { get; init; }
    public string CardType { get; init; } = "";
    public string CtId { get; init; } = "";
    public DateTime At { get; init; }

    /// <summary>True, wenn ein Ereignis den Ausschlag gab – nicht eine Pollabfrage.</summary>
    public bool ViaEvent { get; init; }

    /// <summary>
    /// Ordnet ein Ereignis einer Kartenanwesenheit zu. Unbekannte Topics liefern null, damit
    /// die Anzeige beim letzten belastbaren Stand bleibt, statt auf "keine Karte" zu springen.
    /// </summary>
    public static ConnectorPresence? FromEvent(ConnectorEvent? evt)
    {
        if (evt is null)
            return null;

        var inserted = evt.Topic switch
        {
            _ when ConnectorTopics.IsCardInserted(evt.Topic) => true,
            _ when ConnectorTopics.IsCardRemoved(evt.Topic) => false,
            // Ältere Firmware oder Test-Topics mit Namespace#Name
            _ when TopicName(evt.Topic) is "CardInserted" => true,
            _ when TopicName(evt.Topic) is "CardRemoved" => false,
            _ => (bool?)null
        };
        if (inserted is null)
            return null;

        return new ConnectorPresence
        {
            Inserted = inserted.Value,
            CardType = evt.Param("CardType") ?? "",
            CtId = evt.Param("CtID") ?? "",
            At = evt.ReceivedAt,
            ViaEvent = true
        };
    }

    /// <summary>
    /// Liest dieselbe Angabe aus einem Datenstand. Ohne Ereignis-Abo ist das die einzige
    /// Quelle – sie darf deshalb nichts behaupten, was nicht tatsächlich abgetastet wurde.
    /// </summary>
    public static ConnectorPresence? FromSnapshot(ConnectorSnapshot snapshot)
    {
        if (snapshot.Cards.Count == 0)
        {
            // Eine leere Kartenliste ist zweierlei: die Karte ist raus, oder GetCards hat
            // nichts geliefert. Das eine zu melden ist eine Aussage, das andere eine
            // Unterstellung – ConnectorHealth schuetzt an gleicher Stelle schon genauso.
            if (snapshot.Warnings.Any(w => w.Contains("GetCards", StringComparison.Ordinal)))
                return null;

            return new ConnectorPresence { Inserted = false, At = snapshot.RetrievedAt, ViaEvent = false };
        }

        // Die SMC-B ist für die TI-Arbeit die entscheidende Karte, nicht irgendeine.
        var card = snapshot.Cards.FirstOrDefault(c => c.IsSmcB) ?? snapshot.Cards[0];
        return new ConnectorPresence
        {
            Inserted = true,
            CardType = card.CardType,
            CtId = card.CtId,
            At = snapshot.RetrievedAt,
            ViaEvent = false
        };
    }

    /// <summary>Nur der Ereignisname hinter dem Gedankenstrich, nicht der ganze Topic-Namespace.</summary>
    private static string TopicName(string topic)
    {
        var idx = topic.LastIndexOf('#');
        var name = idx >= 0 ? topic[(idx + 1)..] : topic;
        return name.Trim();
    }

    /// <summary>Kurzfassung für den Wert im Banner: "rein" oder "raus".</summary>
    public string StateText => Inserted
        ? LocalizationService.Pick("rein", "in")
        : LocalizationService.Pick("raus", "out");

    /// <summary>
    /// Ganze Aussage fuer die Anzeige: "SMC-B gesteckt", "HBA gezogen", "Keine Karte gesteckt".
    /// </summary>
    public string Headline
    {
        get
        {
            var type = CardType switch
            {
                "" => null,
                "SM-B" => "SMC-B",
                "HBAx" or "HBA-qSig" => "HBA",
                "EGK" => "eGK",
                "SMC-KT" => "gSMC-KT",
                var t => t
            };
            if (type is null)
                return Inserted
                    ? LocalizationService.Pick("Karte gesteckt", "Card inserted")
                    : LocalizationService.Pick("Keine Karte gesteckt", "No card inserted");
            return Inserted
                ? LocalizationService.Pick($"{type} gesteckt", $"{type} inserted")
                : LocalizationService.Pick($"{type} gezogen", $"{type} removed");
        }
    }

    /// <summary>Was und wo, soweit bekannt. Leer, wenn der Konnektor nichts hergibt.</summary>
    public string DetailText => string.Join(" · ", new[] { CardType, CtId }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>Steht die Anzeige noch unter dem Eindruck eines Ereignisses?</summary>
    public bool IsLive(DateTime now) => ViaEvent && (now - At).TotalSeconds <= LiveWindowSeconds;

    /// <summary>Alter der Angabe. Aus dem Ereignispfad "vor 12 s", sonst "Stand 14:32 Uhr".</summary>
    public string AgeText(DateTime now)
    {
        var seconds = (long)Math.Max(0, (now - At).TotalSeconds);
        if (ViaEvent)
        {
            if (seconds < 60)
                return LocalizationService.Pick($"vor {seconds} s", $"{seconds} s ago");
            if (seconds < 3600)
                return LocalizationService.Pick($"vor {seconds / 60} min", $"{seconds / 60} min ago");
            return LocalizationService.Pick($"vor {seconds / 3600} h", $"{seconds / 3600} h ago");
        }

        // Ohne Ereignis ist das Alter bloß das Alter des Datenbestands – ein Wort mehr,
        // damit niemand eine Verzögerung der App wittert, die gar keine ist.
        return LocalizationService.Pick($"Stand {At:HH:mm} Uhr", $"as of {At:HH:mm}");
    }

    /// <summary>Zeichen für die Anzeige: ein Punkt, der bei frischen Ereignissen mitzieht.</summary>
    public string Glyph(DateTime now) => IsLive(now) ? "●" : ViaEvent ? "◐" : "○";
}
