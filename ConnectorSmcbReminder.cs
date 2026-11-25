namespace TILageMonitor;

/// <summary>
/// Erinnerung an die SMC-B-Freischaltung. Nach einem Neustart des PCs (App-Start) oder nachdem
/// der Konnektor wieder erreichbar ist (Stromausfall, Neustart), ist die SMC-B häufig nicht
/// mehr PIN-verifiziert – dann stehen eRezept, ePA, KIM und VSDM still, ohne dass es jemand
/// merkt. Der Ledger meldet nur Zustandswechsel; nach einem PC-Neustart mit weiterhin gesperrter
/// SMC-B käme sonst nichts. Diese Erinnerung kommt deshalb einmal je Anlass.
/// </summary>
public static class ConnectorSmcbReminder
{
    /// <summary>Toast-Fokus: Fenster öffnen und SMC-B zeigen.</summary>
    public const string ShowFocusPrefix = "konnektor-smcb";

    /// <summary>Toast-Fokus des Knopfes: Fenster öffnen und Freischaltung starten.</summary>
    public const string UnlockFocusPrefix = "konnektor-smcb-unlock";

    public enum State
    {
        /// <summary>Keine SMC-B oder PIN-Status nicht lesbar – noch keine Aussage möglich.</summary>
        Unknown,
        Verified,
        NotVerified
    }

    /// <summary>Zustand der SMC-B(s): eine nicht verifizierte genügt für "NotVerified".</summary>
    public static (State State, ConnectorCard? Card) Evaluate(ConnectorSnapshot snapshot)
    {
        ConnectorCard? verified = null;
        foreach (var card in snapshot.Cards.Where(c => c.IsSmcB))
        {
            var pin = card.PinStates.FirstOrDefault(p => string.Equals(p.PinType, "PIN.SMC", StringComparison.OrdinalIgnoreCase));
            if (pin is null || ConnectorPinWatch.Classify(card, pin) is not { } condition)
                continue;
            if (condition == ConnectorPinCondition.NotVerified)
                return (State.NotVerified, card);
            if (pin.IsVerified)
                verified ??= card;
        }
        return verified is null ? (State.Unknown, null) : (State.Verified, verified);
    }

    public static string CardId(ConnectorCard card) =>
        string.IsNullOrWhiteSpace(card.Iccsn) ? card.CardHandle : card.Iccsn;

    public static string ShowFocus(ConnectorCard card) => $"{ShowFocusPrefix}|{CardId(card)}";

    public static string UnlockFocus(ConnectorCard card) => $"{UnlockFocusPrefix}|{CardId(card)}";

    /// <summary>Zerlegt einen Toast-Fokus. Null, wenn er nicht zur SMC-B-Erinnerung gehört.</summary>
    public static (bool Unlock, string? CardId)? ParseFocus(string? focus)
    {
        if (string.IsNullOrWhiteSpace(focus))
            return null;
        var parts = focus.Split('|', 2);
        var head = parts[0].Trim();
        var id = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1].Trim() : null;
        if (string.Equals(head, UnlockFocusPrefix, StringComparison.OrdinalIgnoreCase))
            return (true, id);
        if (string.Equals(head, ShowFocusPrefix, StringComparison.OrdinalIgnoreCase))
            return (false, id);
        return null;
    }

    public static ConnectorAlert Build(ConnectorCard card, bool afterOutage) => new(
        "smcb-reminder",
        L("SMC-B freischalten", "Unlock SMC-B"),
        afterOutage
            ? L($"Der Konnektor ist wieder erreichbar, aber {card.DisplayName} ist nicht freigeschaltet. eRezept, ePA, KIM und VSDM funktionieren erst nach der PIN-Eingabe am Kartenterminal.",
                $"The connector is reachable again, but {card.DisplayName} is not unlocked. eRezept, ePA, KIM and VSDM only work after entering the PIN at the card terminal.")
            : L($"{card.DisplayName} ist nicht freigeschaltet. eRezept, ePA, KIM und VSDM funktionieren erst nach der PIN-Eingabe am Kartenterminal.",
                $"{card.DisplayName} is not unlocked. eRezept, ePA, KIM and VSDM only work after entering the PIN at the card terminal."),
        ToastUrgency.Warning);

    public static string ActionLabel => L("Jetzt freischalten", "Unlock now");

    private static string L(string de, string en) => LocalizationService.Pick(de, en);
}
