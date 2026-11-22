namespace TILageMonitor;

/// <summary>
/// Wiederverbindung mit exponentiellem Backoff und Jitter, wenn der Konnektor nicht
/// erreichbar ist (z. B. waehrend eines Neustarts). I/O-frei und testbar: der Zufall wird
/// als Wert zwischen 0 und 1 hereingereicht.
/// </summary>
public static class ConnectorReconnect
{
    /// <summary>Erster Wiederholungsabstand nach einem Fehlschlag.</summary>
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(15);

    /// <summary>Obergrenze fuer den Abstand zwischen zwei Wiederholungen.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    /// <summary>Kleinster Abstand, auch mit Jitter – der Konnektor soll nicht gehaemmert werden.</summary>
    public static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(5);

    /// <summary>Jitter-Anteil (±20 %), damit mehrere Arbeitsplaetze nicht im Gleichtakt anklopfen.</summary>
    public const double JitterFraction = 0.2;

    /// <summary>
    /// Abstand bis zum naechsten Versuch. Ohne Fehlschlag gilt das regulaere Intervall; danach
    /// 15 s, 30 s, 60 s … bis hoechstens 5 Minuten – und nie laenger als das regulaere
    /// Intervall, damit ein kurzer Pollabstand nicht durch den Backoff verlaengert wird.
    /// </summary>
    /// <param name="consecutiveFailures">Fehlschlaege in Folge (0 = alles in Ordnung).</param>
    /// <param name="regular">Regulaerer Pollabstand.</param>
    /// <param name="random01">Zufallswert in [0, 1).</param>
    public static TimeSpan NextDelay(int consecutiveFailures, TimeSpan regular, double random01)
    {
        if (consecutiveFailures <= 0)
            return regular;

        var cap = regular < MaxDelay ? regular : MaxDelay;
        if (cap < MinDelay)
            cap = MinDelay;

        // 2^(n-1) begrenzen, bevor es ueberlaeuft.
        var exponent = Math.Min(consecutiveFailures - 1, 16);
        var raw = BaseDelay.TotalSeconds * Math.Pow(2, exponent);
        var clampedRandom = double.IsNaN(random01) ? 0.5 : Math.Clamp(random01, 0, 1);
        var jitter = 1 + JitterFraction * (2 * clampedRandom - 1);
        var seconds = Math.Min(raw, cap.TotalSeconds) * jitter;
        seconds = Math.Clamp(seconds, MinDelay.TotalSeconds, cap.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// "Nicht erreichbar" erst melden, wenn es nicht bloss ein kurzer Aussetzer ist: mindestens
    /// zwei Fehlschlaege und zwei Minuten Dauer. Ein Konnektor-Neustart, der schneller fertig
    /// ist, erzeugt so keine zwei Toasts.
    /// </summary>
    public static bool ShouldNotifyUnreachable(int consecutiveFailures, DateTime? outageSince, DateTime now, bool alreadyNotified) =>
        !alreadyNotified && consecutiveFailures >= 2 && outageSince is { } since && now - since >= TimeSpan.FromMinutes(2);

    /// <summary>
    /// Ein Fehler, bei dem der Konnektor erreichbar ist, die Anfrage aber ablehnt (gematik-
    /// Fehlercode, HTTP 401/403/404, nicht gepinntes Zertifikat, fehlendes/falsches P12). Solche
    /// Fehler sind kein Ausfall: kein Backoff, kein "nicht erreichbar"-Toast, kein roter Balken
    /// im Verlauf – sondern ein Hinweis auf die Einstellungen.
    /// </summary>
    public static bool IsConfigurationProblem(Exception? ex) => ex switch
    {
        null => false,
        ConnectorFaultException => true,
        ConnectorUntrustedServerException => true,
        ConnectorCredentialException => true,
        SecretProtectorException => true,
        System.IO.FileNotFoundException => true,
        System.Net.Http.HttpRequestException http => http.StatusCode is System.Net.HttpStatusCode.Unauthorized
            or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound,
        ConnectorTlsException tls => tls.PinFailure || tls.Alert is 42 or 43 or 44 or 45 or 46 or 48 or 49,
        _ => false
    };
}
