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

    public bool IsSmcB => CardType.Equals("SMC-B", StringComparison.OrdinalIgnoreCase);
    public bool IsHba => CardType.Equals("HBA", StringComparison.OrdinalIgnoreCase) ||
                         CardType.Equals("HBA-qSig", StringComparison.OrdinalIgnoreCase) ||
                         CardType.Equals("HBAx", StringComparison.OrdinalIgnoreCase);
    public bool IsEgk => CardType.Equals("EGK", StringComparison.OrdinalIgnoreCase);

    /// <summary>Institutions- und Heilberufsausweise unterstützen PIN-Verwaltung über den Konnektor.</summary>
    public bool SupportsPinManagement => IsSmcB || IsHba;

    /// <summary>PIN-Typen nach gemSpec_Kon (PinTyp) für diese Karte.</summary>
    public IReadOnlyList<string> PinTypes =>
        IsSmcB ? ["PIN.SMC"]
        : IsHba ? ["PIN.CH", "PIN.QES"]
        : [];

    public string DisplayName
    {
        get
        {
            var type = CardType switch
            {
                "SMC-B" => "SMC-B",
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
    public bool IsTransportPin => Status.Equals("TRANSPORT_PIN", StringComparison.OrdinalIgnoreCase) ||
                                  Status.Equals("EMPTY_PIN", StringComparison.OrdinalIgnoreCase);
}

public sealed class ConnectorCertificateInfo
{
    public string CertRef { get; set; } = "";
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

    public IEnumerable<ConnectorErrorState> ActiveErrors => ErrorStates.Where(e => e.Value);
}

public sealed class ConnectorVerificationResult
{
    /// <summary>VALID, INCONCLUSIVE, INVALID</summary>
    public string Result { get; set; } = "";
    public List<string> Roles { get; set; } = new();
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
