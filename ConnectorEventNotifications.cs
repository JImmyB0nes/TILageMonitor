namespace TILageMonitor;

/// <summary>Einstellbarer Filter fuer Ereignis-Toasts (I/O-frei, testbar).</summary>
public sealed record ConnectorEventToastFilter(
    bool CardRemoved,
    bool CardInserted,
    bool TerminalDisconnected,
    bool TerminalConnected,
    bool SmcB,
    bool Hba)
{
    public static ConnectorEventToastFilter From(ConnectorSettings settings) => new(
        settings.ToastCardRemoved,
        settings.ToastCardInserted,
        settings.ToastTerminalDisconnected,
        settings.ToastTerminalConnected,
        settings.ToastForSmcB,
        settings.ToastForHba);
}

/// <summary>
/// Entscheidet, ob ein CETP-Ereignis (CARD/INSERTED, CARD/REMOVED, CT/CONNECTED,
/// CT/DISCONNECTED nach gemSpec_Kon TUC_KON_256) einen Toast ausloest. Kartenereignisse
/// werden nur fuer SMC-B und HBA gemeldet – eine eGK, die den ganzen Tag rein und raus
/// geht, wuerde sonst jeden Toast entwerten.
/// </summary>
public static class ConnectorEventNotifications
{
    /// <summary>Gleiches Ereignis innerhalb dieses Fensters nur einmal (doppelte Abos, Wiederholungen).</summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(15);

    public enum CardKind
    {
        Other,
        SmcB,
        Hba
    }

    /// <summary>Kartentyp aus den Ereignisparametern (CardType bzw. Type laut gemSpec_Kon).</summary>
    public static CardKind KindOf(ConnectorEvent evt)
    {
        var type = evt.Param("CardType") ?? evt.Param("Type") ?? "";
        var probe = new ConnectorCard { CardType = type.Trim() };
        if (probe.IsSmcB)
            return CardKind.SmcB;
        if (probe.IsHba || probe.IsHbaPredecessor)
            return CardKind.Hba;
        return CardKind.Other;
    }

    /// <summary>Toast fuer dieses Ereignis oder null, wenn der Filter es nicht will.</summary>
    public static ConnectorAlert? Decide(ConnectorEvent evt, ConnectorEventToastFilter filter)
    {
        var topic = (evt.Topic ?? "").Trim();
        var ct = evt.Param("CtID") ?? "";
        var where = string.IsNullOrWhiteSpace(ct) ? "" : L($" (Terminal {ct})", $" (terminal {ct})");

        if (ConnectorTopics.EqualsTopic(topic, ConnectorTopics.CardRemoved) || ConnectorTopics.EqualsTopic(topic, ConnectorTopics.CardInserted))
        {
            var inserted = ConnectorTopics.EqualsTopic(topic, ConnectorTopics.CardInserted);
            if (inserted ? !filter.CardInserted : !filter.CardRemoved)
                return null;
            var kind = KindOf(evt);
            if (kind == CardKind.Other || (kind == CardKind.SmcB && !filter.SmcB) || (kind == CardKind.Hba && !filter.Hba))
                return null;

            var name = kind == CardKind.SmcB ? "SMC-B" : "HBA";
            var holder = evt.Param("CardHolderName");
            var label = string.IsNullOrWhiteSpace(holder) ? name : $"{name} · {holder}";
            var id = evt.Param("ICCSN") ?? evt.Param("CardHandle") ?? "";
            return inserted
                ? new ConnectorAlert($"evt-card-in-{id}",
                    L($"{name} gesteckt", $"{name} inserted"),
                    L($"{label} wurde gesteckt{where}.", $"{label} was inserted{where}."),
                    ToastUrgency.Info)
                : new ConnectorAlert($"evt-card-out-{id}",
                    L($"{name} entfernt", $"{name} removed"),
                    L($"{label} wurde gezogen{where}.", $"{label} was removed{where}.")
                    + (kind == CardKind.SmcB ? L(" Ohne SMC-B funktionieren eRezept, ePA, KIM und VSDM nicht.", " Without SMC-B, eRezept, ePA, KIM and VSDM do not work.") : ""),
                    kind == CardKind.SmcB ? ToastUrgency.Warning : ToastUrgency.Info);
        }

        if (ConnectorTopics.EqualsTopic(topic, ConnectorTopics.TerminalDisconnected) || ConnectorTopics.EqualsTopic(topic, ConnectorTopics.TerminalConnected))
        {
            var connected = ConnectorTopics.EqualsTopic(topic, ConnectorTopics.TerminalConnected);
            if (connected ? !filter.TerminalConnected : !filter.TerminalDisconnected)
                return null;
            var host = evt.Param("Hostname");
            var label = string.IsNullOrWhiteSpace(host) ? (string.IsNullOrWhiteSpace(ct) ? L("Kartenterminal", "Card terminal") : ct)
                : string.IsNullOrWhiteSpace(ct) ? host! : $"{host} ({ct})";
            return connected
                ? new ConnectorAlert($"evt-ct-up-{ct}",
                    L("Kartenterminal verbunden", "Card terminal connected"),
                    L($"{label} ist wieder mit dem Konnektor verbunden.", $"{label} is connected to the connector again."),
                    ToastUrgency.Info)
                : new ConnectorAlert($"evt-ct-down-{ct}",
                    L("Kartenterminal getrennt", "Card terminal disconnected"),
                    L($"{label} ist vom Konnektor getrennt.", $"{label} is disconnected from the connector."),
                    ToastUrgency.Warning);
        }

        return null;
    }

    /// <summary>
    /// Schluessel der Poll-Warnung, die dasselbe meint wie das Ereignis – damit der sofort
    /// nachgezogene Poll nicht einen zweiten Toast fuer denselben Vorgang zeigt.
    /// </summary>
    public static string? MatchingHealthKey(ConnectorEvent evt)
    {
        var topic = (evt.Topic ?? "").Trim();
        if (ConnectorTopics.EqualsTopic(topic, ConnectorTopics.TerminalDisconnected))
        {
            var ct = evt.Param("CtID");
            return string.IsNullOrWhiteSpace(ct) ? null : "ct-" + ct;
        }
        if (ConnectorTopics.EqualsTopic(topic, ConnectorTopics.CardRemoved) && KindOf(evt) == CardKind.SmcB)
            return "no-smcb";
        return null;
    }

    private static string L(string de, string en) => LocalizationService.Pick(de, en);
}

/// <summary>Unterdrueckt dasselbe Ereignis innerhalb eines kurzen Fensters (thread-sicher).</summary>
public sealed class ConnectorEventDeduplicator
{
    private readonly Dictionary<string, DateTime> _seen = new(StringComparer.Ordinal);
    private readonly TimeSpan _window;

    public ConnectorEventDeduplicator(TimeSpan window) => _window = window;

    /// <summary>True, wenn das Ereignis neu ist und gemeldet werden darf.</summary>
    public bool TryAccept(string key, DateTime now)
    {
        lock (_seen)
        {
            foreach (var old in _seen.Where(kv => now - kv.Value > _window).Select(kv => kv.Key).ToList())
                _seen.Remove(old);
            if (_seen.TryGetValue(key, out var at) && now - at <= _window)
                return false;
            _seen[key] = now;
            return true;
        }
    }
}
