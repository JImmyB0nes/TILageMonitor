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

    /// <summary>
    /// Gemeinsame Typen des CardService. <c>PinTyp</c> und <c>SetNewPin</c> gehoeren hierher,
    /// nicht in den Namespace des Dienstes selbst. Belegt durch das Beispiel im offiziellen
    /// Implementierungsleitfaden der gematik (gemILF_PS, Beispiel "ChangePin"): dort steht
    /// <c>xmlns:m2=".../CardServiceCommon/v2.0"</c> und <c>&lt;m2:PinTyp&gt;PIN.CH&lt;/m2:PinTyp&gt;</c>.
    /// Sendet man PinTyp stattdessen im CardService-Namespace, erkennt der Konnektor das
    /// Element nicht und antwortet mit 4000 Syntaxfehler – unabhaengig vom Namespace des Dienstes.
    /// </summary>
    public static readonly XNamespace CardCmn = "http://ws.gematik.de/conn/CardServiceCommon/v2.0";
    public static readonly XNamespace Pi = "http://ws.gematik.de/int/version/ProductInformation/v1.1";

    // Fallback-Namespaces, falls das Dienstverzeichnis keine TargetNamespaces liefert.
    public const string DefaultEventServiceNs = "http://ws.gematik.de/conn/EventService/v7.2";
    public const string DefaultCardServiceNs = "http://ws.gematik.de/conn/CardService/v8.1";
    public const string DefaultCertificateServiceNs = "http://ws.gematik.de/conn/CertificateService/v6.0";

    /// <summary>
    /// Zertifikatsreferenzen laut CertificateServiceCommon.xsd (CertRefEnum). Mehr gibt die
    /// Spezifikation nicht her – die App bleibt bei dieser Liste, damit unbekannte Refs nicht
    /// als Fehler erscheinen.
    /// </summary>
    public static readonly (string Ref, string Label)[] KnownCertRefs =
    [
        ("C.AUT", "Autentisierung (SMC-B)"),
        ("C.ENC", "Verschluesselung"),
        ("C.SIG", "Signatur (nicht qualifiziert)"),
        ("C.QES", "Qualifizierte Signatur")
    ];

    /// <summary>
    /// Topic zu einem Ereignis, Konvention "Dienst-Namespace#Ereignis" aus gemSpec_Kon. Der
    /// Namespace wird uebergeben, nicht hartcodiert – der Aufrufer soll denjenigen nennen, mit
    /// dem der Konnektor tatsaechlich geantwortet hat. Ein festgeschriebener Namespace laeuft
    /// bei einer Firmware mit anderer EventService-Version ins Leere, und das Leere ist hier
    /// das boesartige: das Abo gilt als geschlossen, es kommt nur nie ein Ereignis an.
    /// </summary>
    public static string EventTopic(string serviceNs, string name) =>
        $"{serviceNs.Trim().TrimEnd('#')}#{name.Trim()}";

    /// <summary>OID der Admission-Extension (ISIS-MTT/Common PKI), enthält die Telematik-ID.</summary>
    public const string AdmissionOid = "1.3.36.8.3.3";

    // ------------------------------------------------------------------
    // Dienstverzeichnis
    // ------------------------------------------------------------------

    public static ConnectorServiceDirectory ParseServiceDirectory(string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root ?? throw new FormatException(LocalizationService.Pick("connector.sds ist leer.", "connector.sds is empty."));
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
                var tls = Descendants(version, "EndpointTLS").Select(e => (string?)e.Attribute("Location")).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
                var plain = Descendants(version, "Endpoint").Select(e => (string?)e.Attribute("Location")).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
                result.Services.Add(new ConnectorServiceEndpoint
                {
                    Name = name,
                    Version = (string?)version.Attribute("Version") ?? "",
                    TargetNamespace = (string?)version.Attribute("TargetNamespace") ?? "",
                    Endpoint = tls.Length > 0 ? tls : plain,
                    EndpointTls = tls,
                    EndpointPlain = plain
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
            throw new ArgumentException(LocalizationService.Pick("Keine Konnektor-Adresse angegeben.", "No connector address given."));
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = (useTls ? "https://" : "http://") + trimmed;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException(LocalizationService.Pick($"Ungültige Konnektor-Adresse: {host}", $"Invalid connector address: {host}"));
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

    public static XElement GetCards(XNamespace evt, ConnectorSettings s, bool mandantWide = true) =>
        new(evt + "GetCards", new XAttribute("mandant-wide", mandantWide ? "true" : "false"), Context(s));

    public static XElement GetCardTerminals(XNamespace evt, ConnectorSettings s, bool mandantWide = true) =>
        new(evt + "GetCardTerminals", new XAttribute("mandant-wide", mandantWide ? "true" : "false"), Context(s));

    public static XElement GetResourceInformation(XNamespace evt, ConnectorSettings s) =>
        new(evt + "GetResourceInformation", Context(s));

    /// <summary>
    /// Abo auf Konnektor-Ereignisse. Der Konnektor baut die Verbindung zum Client auf
    /// (cetp://), der Client muss also einen TCP-Listener auf dieser Adresse halten.
    /// </summary>
    public static XElement Subscribe(XNamespace evt, ConnectorSettings s, string eventTo, string topic, string? filter = null, string? subscriptionId = null)
    {
        var subscription = new XElement(evt + "Subscription");
        if (!string.IsNullOrWhiteSpace(subscriptionId))
            subscription.Add(new XElement(evt + "SubscriptionID", subscriptionId.Trim()));
        subscription.Add(new XElement(evt + "EventTo", eventTo.Trim()));
        subscription.Add(new XElement(evt + "Topic", topic.Trim()));
        if (!string.IsNullOrWhiteSpace(filter))
            subscription.Add(new XElement(evt + "Filter", filter.Trim()));
        return new XElement(evt + "Subscribe", Context(s), subscription);
    }

    public static XElement GetSubscription(XNamespace evt, ConnectorSettings s, bool mandantWide = false, string? subscriptionId = null)
    {
        var element = new XElement(evt + "GetSubscription", new XAttribute("mandant-wide", mandantWide ? "true" : "false"), Context(s));
        if (!string.IsNullOrWhiteSpace(subscriptionId))
            element.Add(new XElement(evt + "SubscriptionID", subscriptionId.Trim()));
        return element;
    }

    public static XElement Unsubscribe(XNamespace evt, ConnectorSettings s, string subscriptionId) =>
        new(evt + "Unsubscribe", Context(s), new XElement(evt + "SubscriptionID", subscriptionId.Trim()));

    /// <summary>Verlängert bestehende Abos (gemSpec_Kon RenewSubscriptions, TerminationTime typisch +25 h).</summary>
    public static XElement RenewSubscriptions(XNamespace evt, ConnectorSettings s, IEnumerable<string> subscriptionIds)
    {
        var element = new XElement(evt + "RenewSubscriptions", Context(s));
        foreach (var id in subscriptionIds.Where(i => !string.IsNullOrWhiteSpace(i)))
            element.Add(new XElement(evt + "SubscriptionID", id.Trim()));
        return element;
    }

    public static List<(string SubscriptionId, DateTime? TerminationTime)> ParseRenewals(XElement response) =>
        Descendants(response, "SubscriptionRenewal")
            .Select(e => (
                Value(e, "SubscriptionID"),
                Date(Value(e, "TerminationTime"))))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Item1))
            .ToList();

    public static XElement GetPinStatus(XNamespace card, ConnectorSettings s, string cardHandle, string pinType) =>
        new(card + "GetPinStatus", Context(s), new XElement(Conn + "CardHandle", cardHandle), new XElement(CardCmn + "PinTyp", pinType));

    public static XElement PinOperation(XNamespace card, string operation, ConnectorSettings s, string cardHandle, string pinType, bool? setNewPin = null)
    {
        var element = new XElement(card + operation, Context(s), new XElement(Conn + "CardHandle", cardHandle), new XElement(CardCmn + "PinTyp", pinType));
        // SetNewPin ist im CardService-Schema ein lokales Element (nicht CardServiceCommon).
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

    /// <summary>
    /// Prüft die IDs des Aufrufkontexts auf die von Konnektoren erlaubten Zeichen.
    /// Vor allem RISE-Konnektoren quittieren Abweichungen (Punkt, Leerzeichen, Umlaute,
    /// zu lange Werte) mit SOAP-Fehler 4000 "Syntaxfehler" – ohne dass der eigentliche
    /// Grund irgendwo nachvollziehbar wäre. Der Aufrufrumpf ist sonst korrekt.
    /// </summary>
    public static string? ValidateContextIds(ConnectorSettings s)
    {
        var problems = new List<string>();
        CheckIdLength("Mandant-ID", s.MandantId, problems, required: true);
        CheckIdLength("Clientsystem-ID", s.ClientSystemId, problems, required: true);
        CheckIdLength("Arbeitsplatz-ID", s.WorkplaceId, problems, required: true);
        if (!string.IsNullOrWhiteSpace(s.UserId))
            CheckIdLength("Benutzer-ID", s.UserId, problems, required: false);
        return problems.Count == 0 ? null : string.Join(Environment.NewLine, problems);
    }

    /// <summary>
    /// Hinweise auf ungewöhnliche Zeichen (RISE akzeptiert oft nur A-Z, a-z, 0-9, - und _).
    /// Das XSD (ConnectorCommon, maxLength 64) verbietet sie nicht – deshalb nur Warnung, kein Blockieren.
    /// </summary>
    public static string? WarnContextIds(ConnectorSettings s)
    {
        var warnings = new List<string>();
        WarnUnusual("Mandant-ID", s.MandantId, warnings);
        WarnUnusual("Clientsystem-ID", s.ClientSystemId, warnings);
        WarnUnusual("Arbeitsplatz-ID", s.WorkplaceId, warnings);
        if (!string.IsNullOrWhiteSpace(s.UserId))
            WarnUnusual("Benutzer-ID", s.UserId, warnings);
        return warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings);
    }

    private static void CheckIdLength(string label, string value, List<string> problems, bool required)
    {
        value ??= "";
        if (value.Length == 0)
        {
            if (required)
                problems.Add($"{label}: leer gelassen – mindestens 1 Zeichen erforderlich.");
            return;
        }
        if (value.Length > 64)
            problems.Add($"{label}: {value.Length} Zeichen – laut ConnectorCommon.xsd sind maximal 64 erlaubt.");
    }

    private static void WarnUnusual(string label, string value, List<string> warnings)
    {
        value ??= "";
        if (value.Length == 0)
            return;
        var unusual = value.Where(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')).Distinct().ToList();
        if (unusual.Count == 0)
            return;
        warnings.Add(
            $"{label} \u201E{value}\u201C enthält ungewöhnliche Zeichen: {string.Join(" ", unusual.Select(DescribeChar))}.\n" +
            "Die gematik-XSD erlaubt sie (max. 64 Zeichen), einige Konnektoren (z. B. RISE) antworten darauf aber mit 4000 Syntaxfehler.");
    }

    private static string DescribeChar(char c) => c switch
    {
        ' ' => "\u201ELeerzeichen\u201C",
        '\t' => "\u201ETabulator\u201C",
        '.' => "\u201EPunkt\u201C",
        _ => $"\u201E{c}\u201C"
    };

    /// <summary>Fehlercode 4000 (bzw. die Meldung "Syntaxfehler") deutet auf eine abgelehnte Eingabe hin.</summary>
    public static bool IsSyntaxError(ConnectorFaultException ex) =>
        string.Equals(ex.Code, "4000", StringComparison.Ordinal) ||
        ex.Message.Contains("Syntaxfehler", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ueberfuehrt einen TargetNamespace aus connector.sds in den Namespace, der tatsaechlich
    /// in der Nachricht stehen muss. Manche Konnektoren (u. a. t2connexta) tragen dort den
    /// Namespace der WSDL-Beschreibung ein (.../EventService/WSDL/v7.2) statt des Nachrichten-
    /// Namespaces (.../EventService/v7.2). Sendet man den WSDL-Namespace, erkennt der
    /// Konnektor die Operation nicht und quittiert pauschal mit 4000 "Syntaxfehler".
    /// </summary>
    public static string NormalizeMessageNamespace(string targetNamespace)
    {
        if (string.IsNullOrWhiteSpace(targetNamespace))
            return targetNamespace;
        return targetNamespace.Contains("/WSDL/", StringComparison.Ordinal)
            ? targetNamespace.Replace("/WSDL/", "/", StringComparison.Ordinal)
            : targetNamespace;
    }

    /// <summary>
    /// Namespace-Varianten, die der Reihe nach ausprobiert werden: erst der aufloeste, dann der
    /// rohe Wert aus dem Dienstverzeichnis, zuletzt der Fallback. Identische Varianten werden
    /// verworfen, damit ein Aufruf nicht doppelt laeuft.
    /// </summary>
    public static List<string> NamespaceCandidates(string? targetNamespace, string defaultNs)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(targetNamespace))
        {
            candidates.Add(NormalizeMessageNamespace(targetNamespace));
            candidates.Add(targetNamespace!);
        }
        candidates.Add(defaultNs);
        return candidates
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

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
            throw new ConnectorFaultException(LocalizationService.Pick($"Antwort des Konnektors ist kein gültiges XML: {ex.Message}", $"The connector's response is not valid XML: {ex.Message}"));
        }

        var body = doc.Root is null ? null : Descendants(doc.Root, "Body").FirstOrDefault();
        var first = body?.Elements().FirstOrDefault()
                    ?? throw new ConnectorFaultException(LocalizationService.Pick("Antwort des Konnektors enthält keinen SOAP-Body.", "The connector's response contains no SOAP body."));

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
        message.Append(!string.IsNullOrWhiteSpace(text) ? text : !string.IsNullOrWhiteSpace(faultString) ? faultString : LocalizationService.Pick("Unbekannter Konnektor-Fehler", "Unknown connector error"));
        if (!string.IsNullOrWhiteSpace(detail) && detail != text)
            message.Append(" (").Append(detail).Append(')');

        // 4000 pauschal auf den Aufrufkontext zu schieben, war falsch: der Kontext ist bei allen
        // Diensten identisch, und wo andere Dienste mit demselben Kontext antworten, kann er die
        // Ursache nicht sein. Der Dienstabgleich kommt spaeter in ConnectorClient.DescribeFault
        // – dort, wo wir die erfolgreichen Aufrufe tatsaechlich sehen. An dieser Stelle steht nur
        // die nachweislich korrekte Kurz erklaerung, dazu die Codes, die eine eigene Handlung haben.
        if (string.Equals(code, "4000", StringComparison.Ordinal) || message.ToString().Contains("Syntaxfehler", StringComparison.OrdinalIgnoreCase))
            message.Append(" Der Konnektor hat die Nachricht als unlesbar abgelehnt. Er hat die Huelle verstanden " +
                           "(es ist ein SOAP-Fehler, kein Verbindungsproblem), aber den Inhalt der Operation nicht akzeptiert.");
        else if (string.Equals(code, "4003", StringComparison.Ordinal))
            message.Append(" Fuer diese Operation fehlt eine Angabe im Aufrufkontext, die der Konnektor zwingend braucht.");

        // Klartext zum gematik-Code (gemSpec_Kon) anhaengen – der Originaltext bleibt vorn,
        // damit Support und Protokoll den Wortlaut des Konnektors sehen.
        var normalizedCode = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        return new ConnectorFaultException(ConnectorErrorCodes.Annotate(message.ToString(), normalizedCode), normalizedCode);
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

    /// <summary>
    /// COS-Stand einer Karte. Was fehlt, ist nicht leer, sondern unbekannt – eine Karte, die
    /// nur die Hauptversion nennt, fuehrt sonst "4.." vor, und das liest sich wie ein
    /// Beschädigungsfall, obwohl der Konnektor nur duenner liefert als erwartet.
    /// </summary>
    /// <remarks>
    /// Gelesen wird aus beiden moeglichen Verästen: COSVersion ist die Form, die im
    /// Implementierungsleitfaden steht, Version diejenige, die vereinzelt Firmwaren statt der
    /// COS-Auskunft fuellen. Beide koennen sich ueberlagern, deshalb wird der erste Eintrag
    /// mit Inhalt gefuehrt, nicht die Reihenfolge der Kindelemente.
    /// </remarks>
    private static string CardVersionText(XElement card)
    {
        var version = card.Elements().FirstOrDefault(e => e.Name.LocalName == "CardVersion");
        if (version is null)
            return "";

        foreach (var branch in new[] { "COSVersion", "Version", "OSVersion" })
        {
            var node = version.Elements().FirstOrDefault(e => e.Name.LocalName == branch);
            if (node is null)
                continue;

            var parts = new[] { "Major", "Minor", "Revision" }
                .Select(p => Value(node, p))
                .Where(p => !string.IsNullOrWhiteSpace(p));
            var text = string.Join(".", parts);
            if (text.Length > 0)
                return text;
        }

        return "";
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
                Connected = Bool(Value(t, "Connected")),
                // GetResourceInformation liefert pro Terminal die komplette ProductInformation
                // (Hersteller, Produkt, HW-/FW-Version) – GetCardTerminals nicht, deshalb hier lesen.
                Workplaces = Descendants(t, "WorkplaceId").Select(w => w.Value.Trim()).Where(w => w.Length > 0).Distinct().ToList(),
                VendorName = Value(t, "ProductVendorName"),
                ProductName = Value(t, "ProductName"),
                ProductClass = Value(t, "ProductClass"),
                HardwareVersion = Value(t, "HWVersion"),
                FirmwareVersion = Value(t, "FWVersion")
            })
            .ToList();

    /// <summary>Aktive Ereignis-Abos aus GetSubscription.</summary>
    public static List<ConnectorSubscription> ParseSubscriptions(XElement response) =>
        Descendants(response, "Subscription")
            .Where(s => s.Elements().Any(e => e.Name.LocalName == "SubscriptionID"))
            .Select(s => new ConnectorSubscription
            {
                SubscriptionId = Value(s, "SubscriptionID"),
                Topic = Value(s, "Topic"),
                EventTo = Value(s, "EventTo"),
                Filter = Value(s, "Filter"),
                TerminationTime = Date(Value(s, "TerminationTime"))
            })
            .ToList();

    /// <summary>
    /// Ein hereinkommendes Ereignis (Konnektor -> Client, "cetp://"). Gelesen wird rein nach
    /// Lokalen Elementnamen, damit Topic- und Namespace-Varianten nicht zu leeren Zeilen fuehren.
    /// </summary>
    public static ConnectorEvent? ParseEvent(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch
        {
            return null;
        }

        var root = doc.Root;
        if (root is null)
            return null;

        // Erlaubt ist sowohl eine einzelne Ereignis-Nachricht als auch eine SOAP-Huelle.
        var element = Descendants(root, "Event").FirstOrDefault() ?? root;
        // Schluessel ohne Ruecksicht auf Gross-/Kleinschreibung (Spec: "CtID", manche Firmware
        // "CtId"); doppelte Schluessel duerfen das Ereignis nicht verwerfen – der erste gilt.
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Descendants(element, "Parameter"))
        {
            var key = Value(p, "Key");
            if (key.Length > 0)
                parameters.TryAdd(key, Value(p, "Value"));
        }

        var topic = Value(element, "Topic");
        if (string.IsNullOrWhiteSpace(topic))
            return null;

        return new ConnectorEvent
        {
            Topic = topic,
            Type = Value(element, "Type"),
            Severity = Value(element, "Severity"),
            SubscriptionId = Value(element, "SubscriptionID"),
            Parameters = parameters
        };
    }

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
    // SignatureService (QES-Probesignatur)
    // ------------------------------------------------------------------

    public const string DefaultSignatureServiceNs = "http://ws.gematik.de/conn/SignatureService/v7.4";
    public static readonly XNamespace Dss = "urn:oasis:names:tc:dss:1.0:core:schema";

    /// <summary>CAdES (CMS, RFC 5652) – die Signaturart, die jeder Konnektor fuer QES anbietet.</summary>
    public const string CadesSignatureType = "urn:ietf:rfc:5652";

    /// <summary>Jobnummer fuer SignDocument. Laut gemSpec_Kon muss sie vom Konnektor stammen.</summary>
    public static XElement GetJobNumber(XNamespace sig, ConnectorSettings s) =>
        new(sig + "GetJobNumber", Context(s));

    public static string ParseJobNumber(XElement response) => Value(response, "JobNumber");

    /// <summary>
    /// SignDocument mit genau einem Dokument (CAdES, eingebetteter Inhalt). Reihenfolge laut
    /// SignatureService.xsd: CardHandle, Context, TvMode, JobNumber, SignRequest.
    /// </summary>
    public static XElement SignDocument(XNamespace sig, ConnectorSettings s, string cardHandle, string jobNumber, byte[] document, string shortText)
    {
        var requestId = "TLM-" + Guid.NewGuid().ToString("N")[..12];
        return new XElement(sig + "SignDocument",
            new XAttribute(XNamespace.Xmlns + "dss", Dss.NamespaceName),
            new XElement(Conn + "CardHandle", cardHandle),
            Context(s),
            new XElement(sig + "TvMode", "NONE"),
            new XElement(sig + "JobNumber", jobNumber),
            new XElement(sig + "SignRequest",
                new XAttribute("RequestID", requestId),
                new XElement(sig + "OptionalInputs",
                    new XElement(Dss + "SignatureType", CadesSignatureType),
                    new XElement(sig + "IncludeEContent", "true")),
                new XElement(sig + "Document",
                    new XAttribute("ID", requestId + "-Doc"),
                    new XAttribute("ShortText", shortText),
                    new XElement(Dss + "Base64Data",
                        new XAttribute("MimeType", "text/plain; charset=utf-8"),
                        Convert.ToBase64String(document))),
                new XElement(sig + "IncludeRevocationInfo", "true")));
    }

    /// <summary>
    /// Wertet die SignResponse aus. Der Status steht eine Ebene tiefer als bei den anderen
    /// Operationen (SignDocumentResponse/SignResponse/Status), deshalb fängt ParseBody einen
    /// Fehler hier nicht ab.
    /// </summary>
    public static ConnectorSignatureResult ParseSignResult(XElement response)
    {
        var signResponse = Descendants(response, "SignResponse").FirstOrDefault() ?? response;
        var status = Descendants(signResponse, "Status").FirstOrDefault();
        var result = status is null ? "" : Value(status, "Result");
        var signature = Descendants(signResponse, "SignatureObject").FirstOrDefault();
        var base64 = signature is null ? "" : Value(signature, "Base64Signature");
        if (base64.Length == 0 && signature is not null)
            base64 = signature.Value.Trim();

        var ok = string.Equals(result, "OK", StringComparison.OrdinalIgnoreCase) && base64.Length > 0;
        string? code = null;
        string? text = null;
        if (!ok && status is not null)
        {
            var trace = Descendants(status, "Trace").FirstOrDefault();
            code = NullIfEmpty(trace is null ? Value(status, "Code") : Value(trace, "Code"));
            text = NullIfEmpty(trace is null ? Value(status, "ErrorText") : Value(trace, "ErrorText"));
        }

        var length = 0;
        if (base64.Length > 0)
        {
            try { length = Convert.FromBase64String(base64).Length; }
            catch (FormatException) { ok = false; text ??= "Signatur ist kein gültiges Base64 / signature is not valid base64"; }
        }

        return new ConnectorSignatureResult
        {
            Ok = ok,
            Result = string.IsNullOrWhiteSpace(result) ? "?" : result,
            ErrorCode = code,
            ErrorText = text is null ? null : ConnectorErrorCodes.Annotate(text, code),
            SignatureBytes = length
        };
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
