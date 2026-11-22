namespace TILageMonitor;

/// <summary>Ein Dienst-Eintrag aus dem Dienstverzeichnis (connector.sds).</summary>
public sealed class ConnectorServiceEndpoint
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string TargetNamespace { get; set; } = "";
    public string Endpoint { get; set; } = "";
}

/// <summary>Produktinformationen und Dienste aus connector.sds.</summary>
public sealed class ConnectorServiceDirectory
{
    public string ProductVendorId { get; set; } = "";
    public string ProductVendorName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string ProductCode { get; set; } = "";
    public string ProductType { get; set; } = "";
    public string ProductTypeVersion { get; set; } = "";
    public string HardwareVersion { get; set; } = "";
    public string FirmwareVersion { get; set; } = "";
    public string InformationDate { get; set; } = "";
    public bool? TlsMandatory { get; set; }
    public bool? ClientAuthMandatory { get; set; }
    public List<ConnectorServiceEndpoint> Services { get; set; } = new();

    /// <summary>Höchste angebotene Version eines Dienstes (z. B. "EventService").</summary>
    public ConnectorServiceEndpoint? Find(string name) =>
        Services
            .Where(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => ParseVersion(s.Version))
            .FirstOrDefault();

    internal static Version ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new Version(0, 0);
        var digits = new string(value.Trim().TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        if (!digits.Contains('.'))
            digits += ".0";
        return Version.TryParse(digits, out var v) ? v : new Version(0, 0);
    }

    /// <summary>EnablePin/DisablePin gibt es erst ab CardService v8.2 (api-telematik OPB5).</summary>
    public bool SupportsEnableDisablePin =>
        Find("CardService") is { } card && ParseVersion(card.Version) >= new Version(8, 2);
}

public sealed class ConnectorCardTerminal
{
    public string CtId { get; set; } = "";
    public string Name { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public int? Slots { get; set; }
    public bool? IsPhysical { get; set; }
    public bool? Connected { get; set; }

    /// <summary>Arbeitsplatze, an denen dieses Terminal angemeldet ist.</summary>
    public List<string> Workplaces { get; set; } = new();

    /// <summary>Details aus ProductInformation – liefert nur GetResourceInformation.</summary>
    public string VendorName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string ProductClass { get; set; } = "";
    public string HardwareVersion { get; set; } = "";
    public string FirmwareVersion { get; set; } = "";

    /// <summary>Einzeilige Kennung fuer Anzeige und Bericht.</summary>
    public string Label => string.IsNullOrWhiteSpace(Name) ? CtId : $"{Name} ({CtId})";

    /// <summary>Hersteller / Produkt / Firmware, soweit bekannt.</summary>
    public string Details
    {
        get
        {
            var parts = new[] { VendorName, ProductName, FirmwareVersion is { Length: > 0 } fw ? $"FW {fw}" : "", HardwareVersion is { Length: > 0 } hw ? $"HW {hw}" : "" }
                .Where(p => !string.IsNullOrWhiteSpace(p));
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Ein aktives Ereignis-Abo des Konnektors.</summary>
public sealed class ConnectorSubscription
{
    public string SubscriptionId { get; set; } = "";
    public string Topic { get; set; } = "";
    public string EventTo { get; set; } = "";
    public string Filter { get; set; } = "";
    public DateTime? TerminationTime { get; set; }

    /// <summary>
    /// Steht das Abo zu <paramref name="now"/> noch? Ein Abo ohne Ablaufzeit gilt als offen –
    /// die Firmware nennt sie nicht ueberall, und sie jedes Mal als abgelaufen zu melden, wenn
    /// der Konnektor geizt, waer die schlechtere Annahme.
    /// </summary>
    public bool Alive(DateTime now) => TerminationTime is null || TerminationTime > now;

    /// <summary>
    /// Gehoert dieser Eintrag zu einer Adresse auf diesem Rechner? Nach der Maschine getrennt,
    /// nicht nach der vollen Adresse: der Lauscher wechselt den Hafen, wenn ein frueherer
    /// Anlauf den naechsten Port besetzt haelt, und wer die Hafennummer mitvergleicht, haelt
    /// das eigene Abo fuer verschwunden, waehrend es auf dem alten Hafen weiter laeuft. Nach
    /// dem Topic zu trennen, scheitert an mehreren Eintraegen derselben Adresse erst recht.
    /// </summary>
    public bool FromHost(string? host) => !string.IsNullOrWhiteSpace(host) && EventTo.Contains(host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Rechneranteil einer cetp-Adresse, Hafen abgeschnitten. Null, wenn nichts
    /// uebrig bleibt, damit ein leerer Lauscher nicht mit jeder Adresse verglichen wird.</summary>
    public static string? HostOf(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;
        var tail = address.Contains("://", StringComparison.Ordinal) ? address[(address.IndexOf("://", StringComparison.Ordinal) + 3)..] : address;
        // Eine umschlossene IPv6 fuehrt ihr eigenes Zeichen vor den andern: wuerde am ersten
        // Doppelpunkt geschnitten, bliebe eine Klammer uebrig, die in jeder Adresse
        // vorkommt – die Ordnung traefe also alles, auch die Nachbarn.
        if (tail.StartsWith('['))
        {
            var close = tail.IndexOf(']');
            return close > 0 ? tail[..(close + 1)] : null;
        }
        var cut = tail.IndexOf(':');
        if (cut >= 0)
            tail = tail[..cut];
        tail = tail.TrimEnd('/');
        return tail.Length == 0 ? null : tail;
    }
}

/// <summary>
/// Ein hereinkommendes Konnektor-Ereignis. Die Parameter sind abstrakt gehalten, well
/// Topic und Parameternamen je Firmware unterschiedlich besetzt sind.
/// </summary>
public sealed class ConnectorEvent
{
    public string Topic { get; set; } = "";
    public string Type { get; set; } = "";
    public string Severity { get; set; } = "";
    public string SubscriptionId { get; set; } = "";
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);
    public DateTime ReceivedAt { get; set; } = DateTime.Now;

    public string? Param(string key) => Parameters.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    /// <summary>Kurzfassung fuer Toast und Log: Topic plus die wichtigsten Parameter.</summary>
    public string Summary
    {
        get
        {
            var extras = Parameters
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                .Take(4)
                .Select(kv => $"{kv.Key}={kv.Value}");
            var tail = string.Join(" ", extras);
            return tail.Length == 0 ? Topic : $"{Topic}  {tail}";
        }
    }
}

public sealed class ConnectorCard
{
    public string CardHandle { get; set; } = "";
    public string CardType { get; set; } = "";
    public string CardVersion { get; set; } = "";
    public string Iccsn { get; set; } = "";
    public string CtId { get; set; } = "";
    public string SlotId { get; set; } = "";
    public DateTime? InsertTime { get; set; }
    public string CardHolderName { get; set; } = "";
    public string Kvnr { get; set; } = "";
    public DateTime? CertificateExpirationDate { get; set; }

    // Zusatzinformationen, die per CardService/CertificateService nachgeladen werden.
    public List<ConnectorPinState> PinStates { get; set; } = new();
    public ConnectorCertificateInfo? AuthCertificate { get; set; }

    /// <summary>
    /// Alle Zertifikate dieser Karte (C.AUT, C.ENC, C.SIG, C.QES). Refs, die die Karte nicht
    /// fuehrt, stehen nicht drin – ein fehlender Eintrag ist also kein Fehler.
    /// </summary>
    public List<ConnectorCertificateInfo> Certificates { get; set; } = new();

    /// <summary>Fruehestes Ablaufdatum aller gelesenen Zertifikate.</summary>
    public DateTime? EarliestCertificateExpiry => EarliestCertificateExpiryExcept(null);

    /// <summary>
    /// Fruehestes Ablaufdatum, wobei ein bereits gemeldetes Zertifikat herausfaellt. Ohne
    /// die Ausnahmeecke melden zwei Stellen denselben abgelaufenen Grund – der allgemeine
    /// Alarm nennt die Referenz, der QES-Alarm erklaert, dass qualifizierte Signaturen
    /// scheitern. Beides auf einmal haelt der Nutzer fuer zwei Gebrechen, waehrend es eines ist.
    /// </summary>
    public DateTime? EarliestCertificateExpiryExcept(ConnectorCertificateInfo? alreadyReported)
    {
        if (Certificates.Count == 0)
            return AuthCertificate?.NotAfter ?? CertificateExpirationDate;
        var rest = Certificates.Where(c => !ReferenceEquals(c, alreadyReported)).ToList();
        return rest.Count == 0 ? null : rest.Min(c => c.NotAfter);
    }

    /// <summary>
    /// QES-Zertifikat vorhanden, aber zum Zeitpunkt <paramref name="now"/> abgelaufen – dann
    /// schlaegt jede qualifizierte Signatur fehl. Der Zeitpunkt wird gereicht statt aus der Uhr
    /// zu holen, damit die Bewertung in Tests nachvollziehbar bleibt.
    /// </summary>
    public ConnectorCertificateInfo? ExpiredQesAt(DateTime now) =>
        Certificates.FirstOrDefault(c => c.RefEquals("C.QES") && c.NotAfter <= now);

    public bool IsSmcB =>
        CardType.Equals("SMC-B", StringComparison.OrdinalIgnoreCase) ||
        CardType.Equals("SM-B", StringComparison.OrdinalIgnoreCase) ||
        CardType.Equals("HSM-B", StringComparison.OrdinalIgnoreCase);

    /// <summary>Moderner HBA (G2) inkl. Sammelbezeichner HBAx aus TAB_KON_500.</summary>
    public bool IsHba =>
        CardType.Equals("HBA", StringComparison.OrdinalIgnoreCase) ||
        CardType.Equals("HBAx", StringComparison.OrdinalIgnoreCase);

    /// <summary>HBA-Vorläufer (HBA-qSig, ZOD_2.0) – typischerweise nur PIN.QES.</summary>
    public bool IsHbaPredecessor =>
        CardType.Equals("HBA-qSig", StringComparison.OrdinalIgnoreCase) ||
        CardType.Equals("ZOD_2.0", StringComparison.OrdinalIgnoreCase) ||
        CardType.Equals("HBA-VK", StringComparison.OrdinalIgnoreCase);

    public bool IsEgk => CardType.Equals("EGK", StringComparison.OrdinalIgnoreCase);

    /// <summary>Institutions- und Heilberufsausweise unterstützen PIN-Verwaltung über den Konnektor.</summary>
    public bool SupportsPinManagement => IsSmcB || IsHba || IsHbaPredecessor;

    /// <summary>PIN-Typen nach gemSpec_Kon (PinTyp) für diese Karte.</summary>
    public IReadOnlyList<string> PinTypes =>
        IsSmcB ? ["PIN.SMC"]
        : IsHba ? ["PIN.CH", "PIN.QES"]
        : IsHbaPredecessor ? ["PIN.QES"]
        : [];

    public string DisplayName
    {
        get
        {
            var type = CardType switch
            {
                "SMC-B" or "SM-B" => "SMC-B",
                "HBA" or "HBAx" or "HBA-qSig" => "HBA",
                "EGK" => "eGK",
                "SMC-KT" => "gSMC-KT",
                "HSM-B" => "HSM-B",
                _ => string.IsNullOrWhiteSpace(CardType) ? "Karte" : CardType
            };
            return string.IsNullOrWhiteSpace(CardHolderName) ? type : $"{type} · {CardHolderName}";
        }
    }
}

public sealed class ConnectorPinState
{
    public string PinType { get; set; } = "";
    /// <summary>VERIFIED, TRANSPORT_PIN, EMPTY_PIN, BLOCKED, VERIFIABLE, DISABLED</summary>
    public string Status { get; set; } = "";
    public int? LeftTries { get; set; }
    public string? Error { get; set; }

    public bool IsVerified => Status.Equals("VERIFIED", StringComparison.OrdinalIgnoreCase);
    public bool IsBlocked => Status.Equals("BLOCKED", StringComparison.OrdinalIgnoreCase);
    public bool IsDisabled => Status.Equals("DISABLED", StringComparison.OrdinalIgnoreCase);
    public bool IsTransportPin => Status.Equals("TRANSPORT_PIN", StringComparison.OrdinalIgnoreCase) ||
                                  Status.Equals("EMPTY_PIN", StringComparison.OrdinalIgnoreCase);
}

public sealed class ConnectorCertificateInfo
{
    public string CertRef { get; set; } = "";

    /// <summary>Vergleich unabhaengig von Grob- und Kleinschreibung.</summary>
    public bool RefEquals(string reference) =>
        CertRef.Equals(reference, StringComparison.OrdinalIgnoreCase);
    public string Subject { get; set; } = "";
    public string CommonName { get; set; } = "";
    public string Organization { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public DateTime NotBefore { get; set; }
    public DateTime NotAfter { get; set; }
    public string? TelematikId { get; set; }
    public string? Profession { get; set; }
    public string KeyAlgorithm { get; set; } = "";
    public string Base64 { get; set; } = "";
}

public sealed class ConnectorErrorState
{
    public string Condition { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Value { get; set; }
    public DateTime? ValidFrom { get; set; }
}

/// <summary>Betriebszustand aus EventService.GetResourceInformation.</summary>
public sealed class ConnectorResourceInfo
{
    public string? VpnTiStatus { get; set; }
    public DateTime? VpnTiTimestamp { get; set; }
    public string? VpnSisStatus { get; set; }
    public DateTime? VpnSisTimestamp { get; set; }
    public List<ConnectorErrorState> ErrorStates { get; set; } = new();

    public bool? IsTiOnline => VpnTiStatus is null
        ? null
        : VpnTiStatus.Equals("Online", StringComparison.OrdinalIgnoreCase);

    public bool? IsSisOnline => VpnSisStatus is null
        ? null
        : VpnSisStatus.Equals("Online", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<ConnectorErrorState> ActiveErrors => ErrorStates.Where(e => e.Value);
}

public sealed class ConnectorVerificationResult
{
    /// <summary>VALID, INCONCLUSIVE, INVALID</summary>
    public string Result { get; set; } = "";
    public List<string> Roles { get; set; } = new();
}

/// <summary>
/// Ergebnis einer Freigabe-/Kontextpruefung (IsContextAvailable oder VerifyCard). Die
/// Firmware antwortet je nach Stand mal boolesch, mal mit einer Ergebniszeichenkette –
/// beides bleibt deshalb getrennt, bis der Aufrufer es nach Vorrang auswertet.
/// </summary>
public sealed class ConnectorAccessCheck
{
    /// <summary>OK, REJECTED, VERIFIED, ERROR oder leer, wenn die Firmware nichts liefert.</summary>
    public string Result { get; set; } = "";

    /// <summary>Nur IsContextAvailable liefert das boolesche Feld; sonst null.</summary>
    public bool? IsAvailable { get; set; }

    public List<string> Roles { get; set; } = new();

    /// <summary>Zuendsicherstes Urteil, das die Antwort hergibt – null, wenn unentscheidbar.</summary>
    public bool? Granted =>
        IsAvailable ?? (string.IsNullOrWhiteSpace(Result)
            ? null
            : Result.Equals("OK", StringComparison.OrdinalIgnoreCase) ||
              Result.Equals("VERIFIED", StringComparison.OrdinalIgnoreCase) ||
              Result.Equals("VALID", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Ergebnis einer PIN-Operation (VerifyPin, ChangePin, UnblockPin, EnablePin …).</summary>
public sealed class ConnectorPinResult
{
    /// <summary>OK, REJECTED, WASBLOCKED, NOWBLOCKED, TRANSPORT_PIN, ERROR</summary>
    public string Result { get; set; } = "";
    public int? LeftTries { get; set; }

    public bool IsOk => Result.Equals("OK", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Kompletter Datenstand eines Konnektor-Abrufs.</summary>
public sealed class ConnectorSnapshot
{
    public DateTime RetrievedAt { get; set; } = DateTime.Now;
    public ConnectorServiceDirectory? Directory { get; set; }
    public ConnectorResourceInfo? Resource { get; set; }
    public List<ConnectorCardTerminal> Terminals { get; set; } = new();
    public List<ConnectorCard> Cards { get; set; } = new();

    /// <summary>Vom Konnektor gemeldete, aktive Ereignis-Abos (GetSubscription).</summary>
    public List<ConnectorSubscription> Subscriptions { get; set; } = new();
    public string? ServerCertificateThumbprint { get; set; }
    public string? ServerCertificateSubject { get; set; }
    public DateTime? ServerCertificateNotAfter { get; set; }

    /// <summary>Nicht-fatale Fehler einzelner Teilabfragen (Abruf läuft trotzdem weiter).</summary>
    public List<string> Warnings { get; set; } = new();
}

/// <summary>SOAP-Fault bzw. gematik-Fehler (GERROR) vom Konnektor.</summary>
public sealed class ConnectorFaultException : Exception
{
    public string? Code { get; }

    public ConnectorFaultException(string message, string? code = null) : base(message) => Code = code;
}

/// <summary>Das Server-Zertifikat des Konnektors ist nicht vertrauenswürdig und noch nicht gepinnt.</summary>
public sealed class ConnectorUntrustedServerException : Exception
{
    public string Thumbprint { get; }
    public string Subject { get; }
    public DateTime NotAfter { get; }

    public ConnectorUntrustedServerException(string thumbprint, string subject, DateTime notAfter)
        : base("Das TLS-Zertifikat des Konnektors ist nicht vertrauenswürdig.")
    {
        Thumbprint = thumbprint;
        Subject = subject;
        NotAfter = notAfter;
    }
}
