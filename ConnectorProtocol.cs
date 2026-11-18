using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;

namespace TILageMonitor;

/// <summary>
/// Reine (I/O-freie) Hilfsfunktionen für das Konnektor-Protokoll nach gemSpec_Kon:
/// Dienstverzeichnis parsen, SOAP-Requests bauen, SOAP-Antworten auswerten.
/// Antworten werden bewusst über lokale Elementnamen gelesen, damit unterschiedliche
/// Schema-Versionen (z. B. EventService v7.2 vs. v7.2.1) ohne Anpassung funktionieren.
/// </summary>
public static class ConnectorProtocol
{
    public static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    public static readonly XNamespace Conn = "http://ws.gematik.de/conn/ConnectorCommon/v5.0";
    public static readonly XNamespace Cctx = "http://ws.gematik.de/conn/ConnectorContext/v2.0";
    public static readonly XNamespace CertCmn = "http://ws.gematik.de/conn/CertificateServiceCommon/v2.0";

    // Fallback-Namespaces, falls das Dienstverzeichnis keine TargetNamespaces liefert.
    public const string DefaultEventServiceNs = "http://ws.gematik.de/conn/EventService/v7.2";
    public const string DefaultCardServiceNs = "http://ws.gematik.de/conn/CardService/v8.1";
    public const string DefaultCertificateServiceNs = "http://ws.gematik.de/conn/CertificateService/v6.0";

    /// <summary>OID der Admission-Extension (ISIS-MTT/Common PKI), enthält die Telematik-ID.</summary>
    public const string AdmissionOid = "1.3.36.8.3.3";

    // ------------------------------------------------------------------
    // Dienstverzeichnis
    // ------------------------------------------------------------------

    public static ConnectorServiceDirectory ParseServiceDirectory(string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root ?? throw new FormatException("connector.sds ist leer.");
        var result = new ConnectorServiceDirectory
        {
            ProductVendorId = Value(root, "ProductVendorID"),
            ProductVendorName = Value(root, "ProductVendorName"),
            ProductName = Value(root, "ProductName"),
            ProductCode = Value(root, "ProductCode"),
            ProductType = Value(root, "ProductType"),
            ProductTypeVersion = Value(root, "ProductTypeVersion"),
            HardwareVersion = Value(root, "HWVersion"),
            FirmwareVersion = Value(root, "FWVersion"),
            InformationDate = Value(root, "InformationDate"),
            TlsMandatory = Bool(Value(root, "TLSMandatory")),
            ClientAuthMandatory = Bool(Value(root, "ClientAutMandatory"))
        };

        foreach (var service in Descendants(root, "Service"))
        {
            var name = (string?)service.Attribute("Name") ?? "";
            foreach (var version in Descendants(service, "Version"))
            {
                var endpoint =
                    Descendants(version, "EndpointTLS").Select(e => (string?)e.Attribute("Location")).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ??
                    Descendants(version, "Endpoint").Select(e => (string?)e.Attribute("Location")).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ??
                    "";
                result.Services.Add(new ConnectorServiceEndpoint
                {
                    Name = name,
                    Version = (string?)version.Attribute("Version") ?? "",
                    TargetNamespace = (string?)version.Attribute("TargetNamespace") ?? "",
                    Endpoint = endpoint
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Ersetzt Host (nicht Port/Pfad) eines SDS-Endpunkts durch die konfigurierte Adresse.
    /// Relative Endpunkte werden gegen die Basis-URL aufgelöst.
    /// </summary>
    public static Uri ResolveEndpoint(string endpoint, Uri baseUri, bool rewriteHost)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return new Uri(baseUri, endpoint);
        if (!rewriteHost)
            return uri;

        var builder = new UriBuilder(uri)
        {
            Host = baseUri.Host,
            Scheme = baseUri.Scheme
        };
        // Wenn der SDS-Endpunkt keinen expliziten Port nennt, den der Basis-URL übernehmen.
        if (uri.IsDefaultPort)
            builder.Port = baseUri.Port;
        return builder.Uri;
    }

    public static Uri BuildBaseUri(string host, bool useTls)
    {
        var trimmed = (host ?? "").Trim().TrimEnd('/');
        if (trimmed.Length == 0)
            throw new ArgumentException("Keine Konnektor-Adresse angegeben.");
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = (useTls ? "https://" : "http://") + trimmed;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException($"Ungültige Konnektor-Adresse: {host}");
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    // ------------------------------------------------------------------
    // Requests
    // ------------------------------------------------------------------

    public static XElement Context(ConnectorSettings s)
    {
        var ctx = new XElement(Cctx + "Context",
            new XElement(Conn + "MandantId", s.MandantId.Trim()),
            new XElement(Conn + "ClientSystemId", s.ClientSystemId.Trim()),
            new XElement(Conn + "WorkplaceId", s.WorkplaceId.Trim()));
        if (!string.IsNullOrWhiteSpace(s.UserId))
            ctx.Add(new XElement(Conn + "UserId", s.UserId.Trim()));
        return ctx;
    }

    public static string Envelope(XElement body) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        new XElement(Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soap", Soap.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "CONN", Conn.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "CCTX", Cctx.NamespaceName),
            new XElement(Soap + "Header"),
            new XElement(Soap + "Body", body)).ToString(SaveOptions.DisableFormatting);

    public static XElement GetCards(XNamespace evt, ConnectorSettings s) =>
        new(evt + "GetCards", new XAttribute("mandant-wide", "true"), Context(s));

    public static XElement GetCardTerminals(XNamespace evt, ConnectorSettings s) =>
        new(evt + "GetCardTerminals", new XAttribute("mandant-wide", "true"), Context(s));

    public static XElement GetResourceInformation(XNamespace evt, ConnectorSettings s) =>
        new(evt + "GetResourceInformation", Context(s));

    public static XElement GetPinStatus(XNamespace card, ConnectorSettings s, string cardHandle, string pinType) =>
        new(card + "GetPinStatus", Context(s), new XElement(Conn + "CardHandle", cardHandle), new XElement(card + "PinTyp", pinType));

    public static XElement PinOperation(XNamespace card, string operation, ConnectorSettings s, string cardHandle, string pinType, bool? setNewPin = null)
    {
        var element = new XElement(card + operation, Context(s), new XElement(Conn + "CardHandle", cardHandle), new XElement(card + "PinTyp", pinType));
        if (setNewPin.HasValue)
            element.Add(new XElement(card + "SetNewPin", setNewPin.Value ? "true" : "false"));
        return element;
    }

    public static XElement ReadCardCertificate(XNamespace cert, ConnectorSettings s, string cardHandle, string certRef, string? crypt = null)
    {
        // Achtung: Bei ReadCardCertificate steht CardHandle laut Schema VOR dem Context.
        var element = new XElement(cert + "ReadCardCertificate",
            new XElement(Conn + "CardHandle", cardHandle),
            Context(s),
            new XElement(cert + "CertRefList", new XElement(cert + "CertRef", certRef)));
        if (!string.IsNullOrWhiteSpace(crypt))
            element.Add(new XElement(cert + "Crypt", crypt));
        return element;
    }

    public static XElement VerifyCertificate(XNamespace cert, ConnectorSettings s, string base64Certificate) =>
        new(cert + "VerifyCertificate", Context(s), new XElement(CertCmn + "X509Certificate", base64Certificate));

    public static string SoapAction(XNamespace serviceNs, string operation) =>
        $"{serviceNs.NamespaceName}#{operation}";

    // ------------------------------------------------------------------
    // Responses
    // ------------------------------------------------------------------

    /// <summary>Liefert das erste Element im SOAP-Body oder wirft bei Fault/Fehlerstatus.</summary>
    public static XElement ParseBody(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            throw new ConnectorFaultException($"Antwort des Konnektors ist kein gültiges XML: {ex.Message}");
        }

        var body = doc.Root is null ? null : Descendants(doc.Root, "Body").FirstOrDefault();
        var first = body?.Elements().FirstOrDefault()
                    ?? throw new ConnectorFaultException("Antwort des Konnektors enthält keinen SOAP-Body.");

        if (first.Name.LocalName == "Fault")
            throw FaultFrom(first);

        var status = first.Elements().FirstOrDefault(e => e.Name.LocalName == "Status");
        var result = status is null ? null : Value(status, "Result");
        if (string.Equals(result, "Error", StringComparison.OrdinalIgnoreCase))
            throw FaultFrom(status!);

        return first;
    }

    private static ConnectorFaultException FaultFrom(XElement element)
    {
        var trace = Descendants(element, "Trace").FirstOrDefault();
        var code = trace is null ? Value(element, "Code") : Value(trace, "Code");
        var text = trace is null ? Value(element, "ErrorText") : Value(trace, "ErrorText");
        var detail = trace is null ? "" : Value(trace, "Detail");
        var faultString = Value(element, "faultstring");

        var message = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(code))
            message.Append(code).Append(": ");
        message.Append(!string.IsNullOrWhiteSpace(text) ? text : !string.IsNullOrWhiteSpace(faultString) ? faultString : "Unbekannter Konnektor-Fehler");
        if (!string.IsNullOrWhiteSpace(detail) && detail != text)
            message.Append(" (").Append(detail).Append(')');

        return new ConnectorFaultException(message.ToString(), string.IsNullOrWhiteSpace(code) ? null : code);
    }

    public static List<ConnectorCard> ParseCards(XElement response) =>
        Descendants(response, "Card")
            .Where(c => c.Elements().Any(e => e.Name.LocalName == "CardHandle"))
            .Select(c => new ConnectorCard
            {
                CardHandle = Value(c, "CardHandle"),
                CardType = Value(c, "CardType"),
                CardVersion = CardVersionText(c),
                Iccsn = Value(c, "Iccsn"),
                CtId = Value(c, "CtId"),
                SlotId = Value(c, "SlotId"),
                InsertTime = Date(Value(c, "InsertTime")),
                CardHolderName = Value(c, "CardHolderName"),
                Kvnr = Value(c, "Kvnr"),
                CertificateExpirationDate = Date(Value(c, "CertificateExpirationDate"))
            })
            .ToList();

    private static string CardVersionText(XElement card)
    {
        var version = card.Elements().FirstOrDefault(e => e.Name.LocalName == "CardVersion");
        var cos = version?.Elements().FirstOrDefault(e => e.Name.LocalName == "COSVersion");
        if (cos is null)
            return "";
        return $"{Value(cos, "Major")}.{Value(cos, "Minor")}.{Value(cos, "Revision")}".Trim('.');
    }

    public static List<ConnectorCardTerminal> ParseCardTerminals(XElement response) =>
        Descendants(response, "CardTerminal")
            .Where(t => t.Elements().Any(e => e.Name.LocalName == "CtId"))
            .Select(t => new ConnectorCardTerminal
            {
                CtId = Value(t, "CtId"),
                Name = Value(t, "Name"),
                IpAddress = FirstNonEmpty(Value(t, "IPV4Address"), Value(t, "IPV6Address"), Value(t, "IPAddress")),
                MacAddress = Value(t, "MacAddress"),
                Slots = Int(Value(t, "Slots")),
                IsPhysical = Bool(Value(t, "IS_PHYSICAL")),
                Connected = Bool(Value(t, "Connected"))
            })
            .ToList();

    public static ConnectorResourceInfo ParseResourceInformation(XElement response)
    {
        var info = new ConnectorResourceInfo();
        var connector = Descendants(response, "Connector").FirstOrDefault();
        if (connector is null)
            return info;

        var ti = Descendants(connector, "VPNTIStatus").FirstOrDefault();
        if (ti is not null)
        {
            info.VpnTiStatus = NullIfEmpty(Value(ti, "ConnectionStatus"));
            info.VpnTiTimestamp = Date(Value(ti, "Timestamp"));
        }

        var sis = Descendants(connector, "VPNSISStatus").FirstOrDefault();
        if (sis is not null)
        {
            info.VpnSisStatus = NullIfEmpty(Value(sis, "ConnectionStatus"));
            info.VpnSisTimestamp = Date(Value(sis, "Timestamp"));
        }

        foreach (var error in Descendants(connector, "ErrorState"))
        {
            info.ErrorStates.Add(new ConnectorErrorState
            {
                Condition = Value(error, "ErrorCondition"),
                Severity = Value(error, "Severity"),
                Type = Value(error, "Type"),
                Value = Bool(Value(error, "Value")) ?? false,
                ValidFrom = Date(Value(error, "ValidFrom"))
            });
        }

        return info;
    }

    public static ConnectorPinState ParsePinStatus(XElement response, string pinType) =>
        new()
        {
            PinType = pinType,
            Status = Value(response, "PinStatus"),
            LeftTries = Int(Value(response, "LeftTries"))
        };

    public static ConnectorPinResult ParsePinResult(XElement response) =>
        new()
        {
            Result = FirstNonEmpty(Value(response, "PinResult"), Value(response, "Result")),
            LeftTries = Int(Value(response, "LeftTries"))
        };

    public static List<(string CertRef, string Base64)> ParseCardCertificates(XElement response)
    {
        var list = new List<(string, string)>();
        foreach (var info in Descendants(response, "X509DataInfo"))
        {
            var certRef = Value(info, "CertRef");
            var base64 = Value(info, "X509Certificate");
            if (!string.IsNullOrWhiteSpace(base64))
                list.Add((certRef, base64.Trim()));
        }
        return list;
    }

    public static ConnectorVerificationResult ParseVerification(XElement response) =>
        new()
        {
            Result = Value(response, "VerificationResult"),
            Roles = Descendants(response, "Role").Select(r => r.Value.Trim()).Where(r => r.Length > 0).ToList()
        };

    // ------------------------------------------------------------------
    // Zertifikate
    // ------------------------------------------------------------------

    public static ConnectorCertificateInfo DescribeCertificate(string certRef, string base64)
    {
        var raw = Convert.FromBase64String(base64);
        using var cert = X509CertificateLoader.LoadCertificate(raw);
        var info = new ConnectorCertificateInfo
        {
            CertRef = certRef,
            Subject = cert.Subject,
            CommonName = cert.GetNameInfo(X509NameType.SimpleName, false),
            Organization = RdnValue(cert.SubjectName, "O"),
            Issuer = cert.GetNameInfo(X509NameType.SimpleName, true),
            SerialNumber = cert.SerialNumber,
            NotBefore = cert.NotBefore,
            NotAfter = cert.NotAfter,
            KeyAlgorithm = cert.PublicKey.Oid.FriendlyName ?? cert.PublicKey.Oid.Value ?? "",
            Base64 = base64
        };

        var admission = cert.Extensions.FirstOrDefault(e => e.Oid?.Value == AdmissionOid);
        if (admission is not null)
        {
            var (telematikId, profession) = ParseAdmission(admission.RawData);
            info.TelematikId = telematikId;
            info.Profession = profession;
        }

        return info;
    }

    /// <summary>
    /// Liest aus der Admission-Extension die registrationNumber (PrintableString = Telematik-ID)
    /// und das erste professionItem (UTF8/Printable DirectoryString).
    /// Minimaler DER-Walker, robust gegen optionale Felder.
    /// </summary>
    public static (string? TelematikId, string? Profession) ParseAdmission(byte[] der)
    {
        var strings = new List<(byte Tag, string Value)>();
        try
        {
            WalkDer(der, 0, der.Length, strings, 0);
        }
        catch
        {
            // Defekte Extension – Teilergebnis verwenden.
        }

        // professionItems ist eine SEQUENCE OF DirectoryString (meist UTF8String),
        // registrationNumber folgt als PrintableString.
        var profession = strings.FirstOrDefault(s => s.Tag == 0x0C).Value
                         ?? strings.FirstOrDefault(s => s.Tag is 0x13 && !LooksLikeTelematikId(s.Value)).Value;
        var telematikId = strings.Where(s => s.Tag == 0x13).Select(s => s.Value).FirstOrDefault(LooksLikeTelematikId);
        return (telematikId, profession);
    }

    private static bool LooksLikeTelematikId(string value) =>
        value.Length >= 3 && char.IsDigit(value[0]) && value.Contains('-');

    private static void WalkDer(byte[] data, int offset, int end, List<(byte, string)> strings, int depth)
    {
        if (depth > 16)
            return;
        while (offset < end)
        {
            var tag = data[offset++];
            if (offset >= end)
                return;
            int length = data[offset++];
            if ((length & 0x80) != 0)
            {
                var count = length & 0x7F;
                if (count is 0 or > 4)
                    return;
                length = 0;
                for (var i = 0; i < count; i++)
                    length = (length << 8) | data[offset++];
            }
            if (length < 0 || offset + length > end)
                return;

            var constructed = (tag & 0x20) != 0;
            if (constructed)
            {
                WalkDer(data, offset, offset + length, strings, depth + 1);
            }
            else if (tag is 0x0C or 0x13 or 0x16 or 0x1E)
            {
                var text = tag == 0x1E
                    ? Encoding.BigEndianUnicode.GetString(data, offset, length)
                    : Encoding.UTF8.GetString(data, offset, length);
                strings.Add((tag == 0x1E ? (byte)0x0C : tag, text));
            }
            else if (tag == 0x04 && depth == 0)
            {
                // Extension-Value ist in X509Extension.RawData bereits ausgepackt; zur Sicherheit
                // auch einen eingebetteten OCTET STRING durchsuchen.
                WalkDer(data, offset, offset + length, strings, depth + 1);
            }

            offset += length;
        }
    }

    private static string RdnValue(X500DistinguishedName name, string key)
    {
        foreach (var part in name.Decode(X500DistinguishedNameFlags.UseNewLines).Split('\n'))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                return trimmed[(key.Length + 1)..].Trim().Trim('"');
        }
        return "";
    }

    // ------------------------------------------------------------------
    // XML-Helfer
    // ------------------------------------------------------------------

    private static IEnumerable<XElement> Descendants(XElement element, string localName) =>
        element.Descendants().Where(e => e.Name.LocalName == localName);

    private static string Value(XElement element, string localName) =>
        Descendants(element, localName).FirstOrDefault()?.Value.Trim() ?? "";

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool? Bool(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "true" or "1" => true,
            "false" or "0" => false,
            _ => null
        };

    private static int? Int(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    private static DateTime? Date(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d.ToLocalTime()
            : null;
}
