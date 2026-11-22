using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace TILageMonitor;

/// <summary>
/// Zugangsdaten und Aufrufkontext für die Konnektor-Anbindung.
/// Passwörter werden ausschließlich DPAPI-geschützt (CurrentUser) gespeichert.
/// </summary>
public sealed class ConnectorSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>IP-Adresse oder Hostname, optional mit Port (z. B. 192.168.1.10 oder 10.0.0.5:8443).</summary>
    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("useTls")]
    public bool UseTls { get; set; } = true;

    /// <summary>Pfad zur Client-Zertifikatsdatei (.p12/.pfx) aus der Konnektor-Administration.</summary>
    [JsonPropertyName("clientCertificatePath")]
    public string ClientCertificatePath { get; set; } = "";

    [JsonPropertyName("clientCertificatePasswordProtected")]
    public string? ClientCertificatePasswordProtected { get; set; }

    /// <summary>Optional: Basic-Auth statt/zusätzlich zum Client-Zertifikat.</summary>
    [JsonPropertyName("basicAuthUser")]
    public string BasicAuthUser { get; set; } = "";

    [JsonPropertyName("basicAuthPasswordProtected")]
    public string? BasicAuthPasswordProtected { get; set; }

    [JsonPropertyName("mandantId")]
    public string MandantId { get; set; } = "";

    [JsonPropertyName("clientSystemId")]
    public string ClientSystemId { get; set; } = "";

    [JsonPropertyName("workplaceId")]
    public string WorkplaceId { get; set; } = "";

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = "";

    /// <summary>SHA-256-Fingerprint des vom Benutzer bestätigten Konnektor-TLS-Zertifikats.</summary>
    [JsonPropertyName("pinnedServerThumbprint")]
    public string? PinnedServerThumbprint { get; set; }

    /// <summary>
    /// Endpunkt-Adressen aus connector.sds auf die konfigurierte IP umschreiben.
    /// Viele Konnektoren melden interne Hostnamen, die vom Arbeitsplatz nicht auflösbar sind.
    /// </summary>
    [JsonPropertyName("rewriteEndpointHost")]
    public bool RewriteEndpointHost { get; set; } = true;

    /// <summary>Hintergrundüberwachung (Toasts bei Konnektor-/Kartenproblemen).</summary>
    [JsonPropertyName("monitoringEnabled")]
    public bool MonitoringEnabled { get; set; } = true;

    /// <summary>
    /// Ereignis-Abos (Subscribe) statt nur Pollen. Der Konnektor meldet dann Karte rein/raus
    /// und Terminal getrennt sofort. Schlaegt das Abo fehl, faellt die App automatisch
    /// zurueck auf das normale Intervall.
    /// </summary>
    [JsonPropertyName("eventSubscriptionsEnabled")]
    public bool EventSubscriptionsEnabled { get; set; } = true;

    /// <summary>Lokaler Verlauf der Konnektor-Staenden (14 Tage) fuer Timeline und Stoerdauer.</summary>
    [JsonPropertyName("historyEnabled")]
    public bool HistoryEnabled { get; set; } = true;

    [JsonPropertyName("pollMinutes")]
    public int PollMinutes { get; set; } = 5;

    /// <summary>Warnung, wenn ein Kartenzertifikat in weniger als N Tagen abläuft.</summary>
    [JsonPropertyName("certificateWarningDays")]
    public int CertificateWarningDays { get; set; } = 60;

    /// <summary>Toast, wenn ein SMC-B-, HBA- oder Clientsystem-Zertifikat in 90, 30 oder 7 Tagen abläuft.</summary>
    [JsonPropertyName("notifyCertificateExpiry")]
    public bool NotifyCertificateExpiry { get; set; } = true;

    /// <summary>Toast bei SMC-B nicht verifiziert, PIN gesperrt oder nur noch einem Versuch.</summary>
    [JsonPropertyName("notifyPinStatus")]
    public bool NotifyPinStatus { get; set; } = true;

    /// <summary>Ereignis-Toasts (CETP): welche Ereignisse …</summary>
    [JsonPropertyName("toastCardRemoved")]
    public bool ToastCardRemoved { get; set; } = true;

    [JsonPropertyName("toastCardInserted")]
    public bool ToastCardInserted { get; set; }

    [JsonPropertyName("toastTerminalDisconnected")]
    public bool ToastTerminalDisconnected { get; set; } = true;

    [JsonPropertyName("toastTerminalConnected")]
    public bool ToastTerminalConnected { get; set; }

    /// <summary>… und für welche Karten (eGK wird nie gemeldet).</summary>
    [JsonPropertyName("toastForSmcB")]
    public bool ToastForSmcB { get; set; } = true;

    [JsonPropertyName("toastForHba")]
    public bool ToastForHba { get; set; } = true;

    /// <summary>
    /// Konnektor-Karte im Hauptfenster anzeigen, solange kein Konnektor eingerichtet ist (per X
    /// ausblendbar). Ein eingerichteter Konnektor wird unabhängig davon immer angezeigt.
    /// </summary>
    [JsonPropertyName("showMainWindowCard")]
    public bool ShowMainWindowCard { get; set; } = true;

    [JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host) &&
        !string.IsNullOrWhiteSpace(MandantId) &&
        !string.IsNullOrWhiteSpace(ClientSystemId) &&
        !string.IsNullOrWhiteSpace(WorkplaceId);

    public ConnectorSettings Clone() => (ConnectorSettings)MemberwiseClone();
}

/// <summary>Windows-DPAPI-Schutz für gespeicherte Geheimnisse (nur der angemeldete Benutzer kann entschlüsseln).</summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TILageMonitor.Connector.v1");

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain))
            return null;
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public static string Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
            return "";
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex)
        {
            throw new SecretProtectorException(
                LocalizationService.Pick(
                    "Das gespeicherte Passwort konnte nicht entschlüsselt werden (beschädigt oder von einem anderen Windows-Konto geschützt).",
                    "The stored password could not be decrypted (corrupted or protected by a different Windows account)."),
                ex);
        }
    }
}

/// <summary>DPAPI-Blob ist unlesbar (beschädigt, anderer Benutzer, falsches Konto).</summary>
public sealed class SecretProtectorException : Exception
{
    public SecretProtectorException(string message, Exception? inner = null) : base(message, inner) { }
}
