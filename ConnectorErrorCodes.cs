namespace TILageMonitor;

/// <summary>Ein gematik-Fehlercode (GERROR Trace/Code) mit Spezifikationstext und Klartext.</summary>
/// <param name="Code">Vierstelliger Code, z. B. "4004".</param>
/// <param name="SpecDe">Fehlertext wie in gemSpec_Kon (deutsch).</param>
/// <param name="SpecEn">Englische Uebersetzung des Fehlertexts.</param>
/// <param name="HintDe">Optional: was in der Praxis zu tun ist (deutsch).</param>
/// <param name="HintEn">Optional: was in der Praxis zu tun ist (englisch).</param>
public sealed record ConnectorErrorCode(string Code, string SpecDe, string SpecEn, string? HintDe = null, string? HintEn = null)
{
    public string Text => LocalizationService.Pick(SpecDe, SpecEn);
    public string? Hint => HintDe is null ? null : LocalizationService.Pick(HintDe, HintEn ?? HintDe);

    /// <summary>"4004 – Ungültige Mandanten-ID. Hinweis …" in der aktuellen Sprache.</summary>
    public string PlainText => Hint is { Length: > 0 } hint ? $"{Code} – {Text}. {hint}" : $"{Code} – {Text}";
}

/// <summary>
/// Uebersetzt gematik-Fehlercodes in Klartext. Die Spezifikationstexte stammen aus den
/// Fehlertabellen von gemSpec_Kon V5.28.0 (Stand 22.07.2026, u. a. TAB_KON_503,
/// TAB_KON_090 ff.). Es stehen bewusst nur Codes drin, deren Text dort nachgelesen ist –
/// fehlt ein Code, bleibt es beim Originaltext des Konnektors, statt etwas zu erfinden.
/// Auf die App bezogen sind vor allem Aufrufkontext, Karten, Terminals, PIN-Status,
/// Zertifikate, Ereignis-Abos, TLS und VPN interessant; Signatur-/Verschluesselungscodes,
/// die eine rein lesende App nie ausloest, fehlen absichtlich.
/// </summary>
public static class ConnectorErrorCodes
{
    private static readonly Dictionary<string, ConnectorErrorCode> Codes = Build(
        // --- allgemein ---
        new("4000", "Syntaxfehler", "Syntax error",
            "Der Konnektor hat die Anfrage formal abgelehnt – meist ungewöhnliche Zeichen im Aufrufkontext oder ein nicht passender Dienst-Namespace.",
            "The connector rejected the request formally – usually unusual characters in the call context or a mismatching service namespace."),
        new("4001", "Interner Fehler", "Internal error",
            "Fehler im Konnektor selbst. Bei Wiederholung Konnektor-Protokoll prüfen bzw. Dienstleister informieren.",
            "Error inside the connector. If it persists, check the connector log or contact your service provider."),
        new("4002", "Der Konnektor befindet sich in einem kritischen Betriebszustand", "The connector is in a critical operating state",
            "Fehlerzustände in der Konnektor-Administration prüfen.",
            "Check the error states in the connector administration."),
        new("4003", "Keine User-ID angegeben, die zur Identifikation der Kartensitzung_HBAx benötigt wird", "No user ID given, which is required to identify the HBAx card session",
            "Für HBA-Zugriffe im Aufrufkontext eine Benutzer-ID eintragen.",
            "Enter a user ID in the call context for HBA access."),

        // --- Aufrufkontext / Infomodell (TAB_KON_503) ---
        new("4004", "Ungültige Mandanten-ID", "Invalid tenant (Mandant) ID",
            "Die Mandanten-ID ist dem Konnektor nicht bekannt. Schreibweise in den Einstellungen und im Infomodell des Konnektors prüfen (Groß-/Kleinschreibung zählt).",
            "The connector does not know this tenant ID. Check the spelling in the settings and in the connector's information model (case-sensitive)."),
        new("4005", "Ungültige Clientsystem-ID", "Invalid client system ID",
            "Die Clientsystem-ID ist dem Konnektor nicht bekannt. Im Infomodell des Konnektors anlegen bzw. Schreibweise prüfen.",
            "The connector does not know this client system ID. Create it in the connector's information model or check the spelling."),
        new("4006", "Ungültige Arbeitsplatz-ID", "Invalid workplace ID",
            "Die Arbeitsplatz-ID ist dem Konnektor nicht bekannt. Im Infomodell des Konnektors anlegen bzw. Schreibweise prüfen.",
            "The connector does not know this workplace ID. Create it in the connector's information model or check the spelling."),
        new("4007", "Ungültige Kartenterminal-ID", "Invalid card terminal ID"),
        new("4008", "Karte nicht als gesteckt identifiziert", "Card not identified as inserted",
            "Karte stecken bzw. prüfen, ob das Kartenterminal verbunden ist.",
            "Insert the card or check that the card terminal is connected."),
        new("4009", "SM-B ist dem Konnektor nicht als SM-B_Verwaltet bekannt", "SM-B is not known to the connector as a managed SM-B",
            "Die SMC-B muss in der Konnektor-Administration als verwaltete SM-B eingetragen sein.",
            "The SMC-B must be registered as a managed SM-B in the connector administration."),
        new("4010", "Clientsystem ist dem Mandanten nicht zugeordnet", "Client system is not assigned to the tenant",
            "Im Infomodell des Konnektors das Clientsystem dem Mandanten zuordnen.",
            "Assign the client system to the tenant in the connector's information model."),
        new("4011", "Arbeitsplatz ist dem Mandanten nicht zugeordnet", "Workplace is not assigned to the tenant",
            "Im Infomodell des Konnektors den Arbeitsplatz dem Mandanten zuordnen.",
            "Assign the workplace to the tenant in the connector's information model."),
        new("4012", "Kartenterminal ist dem Mandanten nicht zugeordnet", "Card terminal is not assigned to the tenant"),
        new("4013", "SM-B_Verwaltet ist dem Mandanten nicht zugeordnet", "Managed SM-B is not assigned to the tenant"),
        new("4014", "Für den Mandanten ist der Arbeitsplatz nicht dem Clientsystem zugeordnet", "For this tenant the workplace is not assigned to the client system",
            "Im Infomodell des Konnektors den Arbeitsplatz dem Clientsystem zuordnen.",
            "Assign the workplace to the client system in the connector's information model."),
        new("4015", "Kartenterminal ist weder lokal noch entfernt vom Arbeitsplatz aus zugreifbar", "Card terminal is neither locally nor remotely accessible from the workplace"),
        new("4016", "Kartenterminal ist nicht lokal vom Arbeitsplatz aus zugreifbar", "Card terminal is not locally accessible from the workplace"),
        new("4017", "Die eGK hat bereits eine Kartensitzung, die einem anderen Arbeitsplatz zugeordnet ist", "The eGK already has a card session assigned to another workplace"),
        new("4018", "Der HBAx hat mindestens eine Kartensitzung zu einer anderen UserId, deren Sicherheitszustand erhöht ist", "The HBAx has at least one card session for another user ID with an elevated security state"),
        new("4019", "Zu den Parametern konnte keine Regel ermittelt werden", "No rule could be determined for the parameters"),
        new("4020", "Kartenterminal ist weder lokal noch entfernt über irgendeinen dem Clientsystem zugeordneten Arbeitsplatz aus zugreifbar", "Card terminal is not accessible from any workplace assigned to the client system"),
        new("4021", "Es sind nicht alle Pflichtparameter mandantId, clientSystemId, workplaceId gefüllt", "Not all mandatory parameters mandantId, clientSystemId, workplaceId are set",
            "Mandant, Clientsystem und Arbeitsplatz in den Konnektor-Einstellungen ausfüllen.",
            "Fill in tenant, client system and workplace in the connector settings."),
        new("4204", "Clientsystem aus dem Aufrufkontext konnte nicht authentifiziert werden", "The client system from the call context could not be authenticated",
            "Client-Zertifikat (P12) bzw. Benutzername/Passwort prüfen und ob sie im Konnektor diesem Clientsystem zugeordnet sind.",
            "Check the client certificate (P12) or user name/password and whether they are assigned to this client system in the connector."),

        // --- Kartenterminals ---
        new("4028", "Fehler beim Versuch eines Verbindungsaufbaus zu KT", "Error while trying to connect to the card terminal"),
        new("4033", "Kartenterminal antwortet nicht, Zufügen fehlgeschlagen", "Card terminal does not respond, adding failed"),
        new("4039", "Kartenterminal durch andere Nutzung aktuell belegt", "Card terminal is currently busy with another use",
            "Kurz warten und erneut versuchen.", "Wait a moment and try again."),
        new("4040", "Fehler beim Versuch eines Verbindungsaufbaus zum KT", "Error while trying to connect to the card terminal"),
        new("4042", "Die Version des Kartenterminals wird nicht unterstützt", "The card terminal version is not supported"),
        new("4044", "Fehler beim Zugriff auf das Kartenterminal", "Error accessing the card terminal"),
        new("4221", "Kartenterminal nicht aktiv", "Card terminal not active",
            "Kartenterminal in der Konnektor-Administration aktivieren.", "Activate the card terminal in the connector administration."),
        new("4222", "Kartenterminal ist nicht verbunden", "Card terminal is not connected",
            "Netzwerk- und Stromversorgung des Kartenterminals prüfen.", "Check the card terminal's network connection and power."),

        // --- Karten ---
        new("4043", "Timeout bei der PIN-Eingabe", "Timeout during PIN entry"),
        new("4049", "Abbruch durch den Benutzer", "Cancelled by the user"),
        new("4051", "Falscher Kartentyp", "Wrong card type"),
        new("4060", "Ressource belegt", "Resource busy"),
        new("4085", "Zugriffsbedingung nicht erfüllt", "Access condition not fulfilled",
            "Häufig ist die Karte nicht PIN-verifiziert (z. B. SMC-B nach Neustart).",
            "Often the card is not PIN-verified (e.g. SMC-B after a restart)."),
        new("4093", "Karte wird in einer anderen Kartensitzung exklusiv verwendet", "Card is used exclusively in another card session"),
        new("4094", "Timeout beim Kartenzugriff aufgetreten", "Timeout during card access"),
        new("4097", "Ungültige Kartenslot-ID", "Invalid card slot ID"),
        new("4098", "keine Karte im angegebenen Slot gefunden", "No card found in the given slot"),
        new("4099", "keine Karte zur angegebenen Iccsn gefunden", "No card found for the given ICCSN"),
        new("4101", "Karten-Handle ungültig", "Card handle invalid",
            "Die Karte wurde vermutlich gezogen oder neu gesteckt – Daten neu abrufen.",
            "The card was probably removed or re-inserted – refresh the data."),
        new("4120", "Kartenfehler", "Card error"),
        new("4131", "Zum angegebenen CardHandle keine Karte gefunden", "No card found for the given card handle",
            "Die Karte wurde vermutlich gezogen oder neu gesteckt – Daten neu abrufen.",
            "The card was probably removed or re-inserted – refresh the data."),
        new("4146", "Kartenhandle existiert nicht", "Card handle does not exist",
            "Die Karte wurde vermutlich gezogen oder neu gesteckt – Daten neu abrufen.",
            "The card was probably removed or re-inserted – refresh the data."),
        new("4203", "Karte deaktiviert, aber nicht entnommen", "Card deactivated but not removed"),
        new("4209", "Kartentyp wird durch diese Operation nicht unterstützt", "Card type is not supported by this operation"),
        new("4232", "der Aufrufer ist nicht im Besitz des Karten-Locks", "The caller does not hold the card lock"),

        // --- PIN ---
        new("4063", "PIN bereits gesperrt (BLOCKED)", "PIN already blocked (BLOCKED)",
            "Die PIN muss mit der PUK am Kartenterminal entsperrt werden.", "The PIN must be unblocked with the PUK on the card terminal."),
        new("4064", "alte PIN bereits blockiert (hier: PUK)", "Old PIN already blocked (here: PUK)"),
        new("4065", "PIN ist transportgeschützt, Änderung erforderlich", "PIN is transport-protected, change required"),
        new("4066", "PIN Pad nicht verfügbar", "PIN pad not available"),
        new("4072", "ungültige PIN-Referenz PinRef", "Invalid PIN reference PinRef"),

        // --- Zertifikate / TLS ---
        new("4147", "Zertifikat nicht vorhanden (z. B. kein QES-Zertifikat in SM-B)", "Certificate not present (e.g. no QES certificate on SM-B)"),
        new("4148", "Fehler beim Extrahieren von Zertifikatsinformationen", "Error extracting certificate information"),
        new("4149", "Ungültige Zertifikatsreferenz", "Invalid certificate reference"),
        new("4156", "Server konnte bei TLS-Verbindungsaufbau nicht authentisiert werden", "Server could not be authenticated during TLS handshake"),
        new("4157", "Clientauthentisierung bei TLS-Verbindungsaufbau fehlgeschlagen", "Client authentication failed during TLS handshake",
            "Client-Zertifikat (P12) prüfen – abgelaufen, falsches Passwort oder im Konnektor nicht hinterlegt?",
            "Check the client certificate (P12) – expired, wrong password or not registered in the connector?"),
        new("4158", "Adressierte TLS-Verbindung nicht vorhanden", "Addressed TLS connection does not exist"),
        new("4220", "Rollenprüfung bei TLS-Verbindungsaufbau fehlgeschlagen", "Role check failed during TLS handshake"),
        new("4256", "Zertifikat auf Karte nicht vorhanden", "Certificate not present on card"),

        // --- Ereignisdienst ---
        new("4102", "ungültige SubscriptionId", "Invalid subscription ID",
            "Das Abo existiert nicht mehr (z. B. nach Konnektor-Neustart). Die App meldet sich automatisch neu an.",
            "The subscription no longer exists (e.g. after a connector restart). The app re-subscribes automatically."),

        // --- Netz / VPN / Zeit ---
        new("4172", "Es ist keine Online-Verbindung zulässig", "No online connection allowed",
            "Die Online-Anbindung ist in der Konnektor-Administration nicht freigegeben – der Konnektor baut keine VPN-Verbindung auf.",
            "Online operation is not enabled in the connector administration – the connector does not establish a VPN connection."),
        new("4173", "Die CRL ist nicht mehr gültig (outdated)", "The CRL is no longer valid (outdated)"),
        new("4174", "TI-VPN-Tunnel: Verbindung konnte nicht aufgebaut werden", "TI VPN tunnel: connection could not be established",
            "Internetzugang des Konnektors bzw. Router prüfen.", "Check the connector's internet access or the router."),
        new("4176", "SIS-VPN-Tunnel: Verbindung konnte nicht aufgebaut werden", "SIS VPN tunnel: connection could not be established"),
        new("4177", "Der NTP-Server des Konnektors konnte nicht synchronisiert werden", "The connector's NTP server could not be synchronized"),
        new("4179", "DNS: Anfrage wurde wegen Timeout abgebrochen", "DNS: request aborted due to timeout"),
        new("4180", "DNS: Es ist ein Fehler bei der Namensauflösung aufgetreten", "DNS: an error occurred during name resolution"),
        new("4188", "Konfigurationsdienst nicht erreichbar, konfigurierte Adresse kontrollieren", "Configuration service unreachable, check the configured address"));

    private static Dictionary<string, ConnectorErrorCode> Build(params ConnectorErrorCode[] codes)
    {
        var map = new Dictionary<string, ConnectorErrorCode>(StringComparer.Ordinal);
        foreach (var code in codes)
            map[code.Code] = code;
        return map;
    }

    /// <summary>Alle hinterlegten Codes (fuer Tests und den Diagnosebericht).</summary>
    public static IReadOnlyCollection<ConnectorErrorCode> All => Codes.Values;

    /// <summary>Klartext zu einem Code oder null, wenn er nicht hinterlegt ist.</summary>
    public static ConnectorErrorCode? Find(string? code)
    {
        var key = Normalize(code);
        return key is not null && Codes.TryGetValue(key, out var found) ? found : null;
    }

    /// <summary>"4004 – Ungültige Mandanten-ID. …" oder null.</summary>
    public static string? Describe(string? code) => Find(code)?.PlainText;

    /// <summary>
    /// Haengt den Klartext an eine Konnektor-Meldung an, sofern der Code bekannt ist und der
    /// Hinweis nicht ohnehin schon dasteht. Die Originalmeldung bleibt vorn, damit der
    /// Support den Wortlaut des Konnektors sieht.
    /// </summary>
    public static string Annotate(string message, string? code)
    {
        var info = Find(code);
        if (info is null)
            return message;
        var prefix = LocalizationService.Pick("Klartext", "Plain text");
        var line = $"{prefix}: {info.PlainText}";
        return message.Contains(line, StringComparison.Ordinal) ? message : $"{message}\n{line}";
    }

    /// <summary>Holt einen Code aus "4004", " 4004 ", "gem:4004" oder "GERROR 4004".</summary>
    internal static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;
        var digits = new string(code.Where(char.IsDigit).ToArray());
        return digits.Length is >= 1 and <= 5 ? digits : null;
    }
}
