using System.Net;
using System.Net.Sockets;

namespace TILageMonitor;

/// <summary>
/// Offizielle Event-Topics nach gemSpec_Kon V5.28.0 (TUC_KON_256, Kap. 1.5.4.3).
/// Keine Namespace#Name-Konvention – die Topics sind eine /-getrennte Baumstruktur.
/// </summary>
public static class ConnectorTopics
{
    public const string CardInserted = "CARD/INSERTED";
    public const string CardRemoved = "CARD/REMOVED";
    public const string TerminalConnected = "CT/CONNECTED";
    public const string TerminalDisconnected = "CT/DISCONNECTED";
    public const string CertCardStatus = "CERT/CARD/STATUS";

    public static readonly string[] DefaultSubscriptions =
    [
        CardInserted, CardRemoved, TerminalConnected, TerminalDisconnected, CertCardStatus
    ];

    /// <summary>Vergleicht Topics ohne umgebende Leerzeichen, Groß/Kleinschreibung wie in der Spec (case-sensitiv laut Parameter, Topics selbst sind fest).</summary>
    public static bool EqualsTopic(string? actual, string expected) =>
        string.Equals((actual ?? "").Trim(), expected, StringComparison.Ordinal);

    public static bool IsCardInserted(string? topic) => EqualsTopic(topic, CardInserted);
    public static bool IsCardRemoved(string? topic) => EqualsTopic(topic, CardRemoved);
}

/// <summary>Entscheidung, ob Abos angelegt, verlängert oder nicht erneut versucht werden.</summary>
public static class ConnectorSubscriptionPolicy
{
    /// <summary>Subscriptions gelten nach gemSpec_Kon 25 Stunden; Verlängerung deutlich davor.</summary>
    public static readonly TimeSpan RenewLead = TimeSpan.FromHours(12);

    public enum Action
    {
        None,
        Subscribe,
        Renew,
        GiveUp
    }

    /// <param name="subscriptionAttempted">Es gab in dieser Sitzung schon einen Anlauf.</param>
    /// <param name="previouslySucceeded">Mindestens ein Abo war schon einmal erfolgreich.</param>
    /// <param name="getSubscriptionSucceeded">GetSubscription hat in diesem Durchlauf geantwortet (keine Warnung).</param>
    public static Action Decide(
        bool subscriptionAttempted,
        bool previouslySucceeded,
        bool getSubscriptionSucceeded,
        IReadOnlyList<ConnectorSubscription> ours,
        DateTime now)
    {
        var alive = ours.Any(s => s.Alive(now));
        var renewable = ours
            .Where(s => !string.IsNullOrWhiteSpace(s.SubscriptionId) && s.TerminationTime is DateTime t && t > now && t - now <= RenewLead)
            .ToList();

        if (previouslySucceeded && alive && renewable.Count == 0)
            return Action.None;

        // Firmware ohne GetSubscription: nach dem ersten erfolglosen Anlauf nicht endlos wiederholen.
        if (subscriptionAttempted && !getSubscriptionSucceeded && !previouslySucceeded)
            return Action.GiveUp;

        // GetSubscription ok, erster Anlauf erfolglos (Topics abgelehnt) – nicht erneut bombardieren.
        if (subscriptionAttempted && getSubscriptionSucceeded && !previouslySucceeded)
            return Action.GiveUp;

        if (previouslySucceeded && alive && renewable.Count > 0)
            return Action.Renew;

        // Abgelaufen oder Liste leer nach früherem Erfolg: neu anmelden (nicht aufgeben).
        if (previouslySucceeded)
            return ours.Count > 0 && renewable.Count > 0 && !alive ? Action.Renew : Action.Subscribe;

        if (!subscriptionAttempted)
            return Action.Subscribe;

        return Action.GiveUp;
    }
}

/// <summary>Lokale Adresse und EventTo-URL (cetp://host:port bzw. cetp://[ipv6]:port) zur Konnektor-Route.</summary>
public static class ConnectorEventTo
{
    public static string Format(IPAddress address, int port)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return $"cetp://[{address}]:{port}";
        return $"cetp://{address}:{port}";
    }

    /// <summary>IP, über die dieser Rechner den Konnektor erreicht. Schlägt die Route fehl, null.</summary>
    public static IPAddress? LocalAddressToward(IPAddress remote)
    {
        try
        {
            using var probe = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(remote, 65530);
            return probe.LocalEndPoint is IPEndPoint local ? local.Address : null;
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<IPAddress> ResolveHost(string host)
    {
        var trimmed = (host ?? "").Trim();
        if (trimmed.Length == 0)
            return [];
        // Host darf einen Port enthalten (10.0.0.1:443 oder [fe80::1]:443).
        if (Uri.TryCreate(trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "https://" + trimmed, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.Host))
            trimmed = uri.IdnHost;

        if (IPAddress.TryParse(trimmed.Trim('[', ']'), out var ip))
            return [ip];
        try
        {
            return Dns.GetHostAddresses(trimmed);
        }
        catch
        {
            return [];
        }
    }

    public static bool IsAllowedPeer(IPEndPoint remote, IReadOnlyList<IPAddress> allowed)
    {
        var addr = remote.Address;
        if (addr.IsIPv4MappedToIPv6)
            addr = addr.MapToIPv4();
        foreach (var allowedIp in allowed)
        {
            var compare = allowedIp.IsIPv4MappedToIPv6 ? allowedIp.MapToIPv4() : allowedIp;
            if (addr.Equals(compare))
                return true;
        }
        return false;
    }
}
