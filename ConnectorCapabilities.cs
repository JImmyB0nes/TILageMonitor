using System.Collections.Concurrent;

namespace TILageMonitor;

/// <summary>
/// Was dieser Rechner über diesen Konnektor schon weiß. Zwei Angaben lernt die App beim ersten
/// Abruf, und ohne die Klappe würde sie bei jedem Poll aufs Neue geraten – mit dem Erfolg, dass
/// ein Konnektor, dessen Eigenart längst bekannt ist, dauerhaft so behandelt wird wie ein
/// ganz neuer.
///
/// Absichtlich nur für die Laufzeit gemerkt: ein Konnektor wird gerollt, ein Mandant umgebaut,
/// und was gestern galt, ist heute falsch. Ein Neustart der App ist die einzige Rücksetzung,
/// die der Nutzer nachvollziehen kann.
/// </summary>
public static class ConnectorCapabilities
{
    private static readonly ConcurrentDictionary<string, bool> MandantWide = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> EventNamespaces = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Versuchsfolge für den Mandanten-Radius. Ist nichts bekannt, zuerst der Radius, weil er
    /// nach gemSpec_Kon der Regelfall ist; ist etwas bekannt, genau dieses eine Mal.
    /// </summary>
    public static bool[] MandantWideAttempts(bool? known) => known.HasValue ? [known.Value] : [true, false];

    /// <summary>Null, wenn dieser Rechner noch keinen Versuch gehabt hat.</summary>
    public static bool? GetMandantWide(string host) =>
        MandantWide.TryGetValue(host ?? "", out var wide) ? wide : (bool?)null;

    public static void SetMandantWide(string host, bool value) => MandantWide[host ?? ""] = value;

    /// <summary>Namespace, mit dem ein Ereignis-Abo schon geklappt hat – damit nicht beim
    /// nächsten Anlauf wieder eine Variante probiert wird, die der Konnektor ablehnt.</summary>
    public static string? GetEventNamespace(string host) =>
        EventNamespaces.TryGetValue(host ?? "", out var ns) ? ns : null;

    public static void SetEventNamespace(string host, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            EventNamespaces[host ?? ""] = value.Trim();
    }

    /// <summary>Alles, was über diese Adresse bekannt ist. Nach einem Umbau des Mandanten die
    /// saubere Rücksetzung, statt die App mit einer alten Vermutung laufen zu lassen.</summary>
    public static void Forget(string host)
    {
        MandantWide.TryRemove(host ?? "", out _);
        EventNamespaces.TryRemove(host ?? "", out _);
    }

}
