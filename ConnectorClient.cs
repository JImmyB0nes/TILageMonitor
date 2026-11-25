using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;

namespace TILageMonitor;

/// <summary>
/// SOAP-Client für einen TI-Konnektor (gemSpec_Kon) per IP-Adresse und Client-Zertifikat (.p12).
/// Das TLS-Zertifikat des Konnektors stammt aus der TI-PKI und ist von Windows i. d. R. nicht
/// vertrauenswürdig; es wird deshalb nach Bestätigung durch den Benutzer per SHA-256 gepinnt.
/// </summary>
public sealed class ConnectorClient : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>PIN-Eingaben erfolgen am Kartenterminal – der Konnektor antwortet erst danach.</summary>
    public static readonly TimeSpan PinOperationTimeout = TimeSpan.FromMinutes(5);

    private readonly ConnectorSettings _settings;
    private readonly IConnectorHttpTransport _transport;
    private readonly ConnectorClientCredentials? _credentials;
    private readonly Uri _baseUri;
    private readonly Action<string>? _log;
    private ConnectorServiceDirectory? _directory;

    /// <summary>
    /// Welche Dienste in diesem Durchlauf schon geantwortet haben, und wie oft. Die Zaehler ist die
    /// Grundlage der Fehlererklaerung: der Aufrufkontext ist bei allen Diensten identisch, und ein
    /// Dienst, der damit schon geantwortet hat, widerlegt den Verdacht gegen denselben Kontext.
    /// </summary>
    private readonly Dictionary<string, int> _okByService = new(StringComparer.OrdinalIgnoreCase);

    public string? LastServerThumbprint { get; private set; }

    /// <summary>Zuletzt gemessene Abweichung der Konnektor-Uhr (HTTP-Date-Header), siehe <see cref="ConnectorClock"/>.</summary>
    public TimeSpan? ClockOffset { get; private set; }
    public string? LastServerSubject { get; private set; }
    public DateTime? LastServerNotAfter { get; private set; }

    /// <summary>Zuletzt gesendeter XML-Rumpf – nur für Diagnoseberichte und Log interessant.</summary>
    public string LastRequestXml { get; private set; } = "";
    public string LastResponseXml { get; private set; } = "";

    /// <summary>Die eingestellte Benutzer-ID, auch wenn sie gerade weggelassen wird.</summary>
    private readonly string _configuredUserId;

    public ConnectorClient(ConnectorSettings settings, Action<string>? log = null, IConnectorHttpTransport? transport = null)
    {
        _settings = settings.Clone();
        _log = log;
        _baseUri = ConnectorProtocol.BuildBaseUri(_settings.Host, _settings.UseTls);

        _configuredUserId = _settings.UserId;
        if (ConnectorCapabilities.GetOmitUserId(ConnectorCapabilities.OmitKey(_settings.Host, _configuredUserId)) == true)
            _settings.UserId = "";

        if (!string.IsNullOrWhiteSpace(_settings.ClientCertificatePath))
        {
            _credentials = LoadClientCertificate(
                _settings.ClientCertificatePath,
                SecretProtector.Unprotect(_settings.ClientCertificatePasswordProtected));
        }

        _transport = transport ?? new ConnectorHttpTransport(
            () => _settings.PinnedServerThumbprint,
            _credentials,
            allowTls13: true,
            userAgent: $"TILageMonitor/{UpdateService.CurrentVersion}");
    }

    public ConnectorClientCredentials? ClientCertificate => _credentials;

    public static ConnectorClientCredentials LoadClientCertificate(string path, string? password)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(LocalizationService.Pick($"Zertifikatsdatei nicht gefunden: {path}", $"Certificate file not found: {path}"));

        try
        {
            return ConnectorClientCredentials.Load(File.ReadAllBytes(path), password ?? "");
        }
        catch (ConnectorCredentialException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ConnectorCredentialException(
                LocalizationService.Pick(
                    $"Das Client-Zertifikat konnte nicht geöffnet werden: {ex.Message}",
                    $"The client certificate could not be opened: {ex.Message}"), ex);
        }
    }

    public bool SupportsEnableDisablePin => _directory?.SupportsEnableDisablePin == true;

        // ------------------------------------------------------------------
    // Transport
    // ------------------------------------------------------------------

    private async Task<string> SendAsync(Uri uri, string method, byte[]? body, string? contentType, IReadOnlyDictionary<string, string>? extraHeaders, TimeSpan timeout, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (extraHeaders is not null)
        {
            foreach (var kv in extraHeaders)
                headers[kv.Key] = kv.Value;
        }

        var basicPassword = "";
        try
        {
            basicPassword = SecretProtector.Unprotect(_settings.BasicAuthPasswordProtected);
        }
        catch (SecretProtectorException)
        {
            if (!string.IsNullOrWhiteSpace(_settings.BasicAuthUser))
                throw;
        }
        if (!string.IsNullOrWhiteSpace(_settings.BasicAuthUser))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_settings.BasicAuthUser}:{basicPassword}"));
            headers["Authorization"] = "Basic " + token;
        }

        try
        {
            var sentUtc = DateTimeOffset.UtcNow;
            var response = await _transport.SendAsync(
                new ConnectorHttpRequest(method, uri, headers, body, contentType), timeout, ct);
            var receivedUtc = DateTimeOffset.UtcNow;
            RememberServerCertificate();
            if (response.Headers.TryGetValue("Date", out var dateHeader)
                && ConnectorClock.OffsetFromDateHeader(dateHeader, sentUtc, receivedUtc) is { } offset)
                ClockOffset = offset;
            var text = response.BodyText;
            LastResponseXml = text;

            // SOAP-Faults kommen mit HTTP 500 – Body trotzdem auswerten.
            if (response.StatusCode is < 200 or >= 300 && !text.Contains("Envelope", StringComparison.Ordinal))
            {
                var hint = response.StatusCode switch
                {
                    401 => LocalizationService.Pick(" – Authentifizierung abgelehnt (Client-Zertifikat bzw. Benutzer/Passwort prüfen).",
                        " – authentication rejected (check client certificate or user/password)."),
                    403 => LocalizationService.Pick(" – Zugriff verweigert (Clientsystem im Konnektor freigeschaltet?).",
                        " – access denied (is the client system enabled in the connector?)."),
                    404 => LocalizationService.Pick(" – Endpunkt nicht gefunden.", " – endpoint not found."),
                    _ => ""
                };
                throw new HttpRequestException($"HTTP {response.StatusCode} {response.Reason}{hint}", null, (System.Net.HttpStatusCode)response.StatusCode);
            }
            return text;
        }
        catch (ConnectorTlsException ex) when (ex.PinFailure && _transport.LastServerCertificate is { } cert)
        {
            RememberServerCertificate();
            throw new ConnectorUntrustedServerException(cert.Sha256Fingerprint, cert.Subject, cert.NotAfter);
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or ConnectorTlsException)
        {
            // gemILF_PS TIP1-A_4967: bei Verbindungsproblemen SDS-Cache erneuern.
            _directory = null;
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log?.Invoke($"{uri.AbsolutePath} → Zeitüberschreitung nach {timeout.TotalSeconds:0} s");
            _directory = null;
            throw new TimeoutException(LocalizationService.Pick(
                $"Zeitüberschreitung: Der Konnektor hat nicht innerhalb von {timeout.TotalSeconds:0} Sekunden geantwortet.",
                $"Timeout: the connector did not answer within {timeout.TotalSeconds:0} seconds."));
        }
    }

    private void RememberServerCertificate()
    {
        if (_transport.LastServerCertificate is not { } cert)
            return;
        LastServerThumbprint = cert.Sha256Fingerprint;
        LastServerSubject = cert.Subject;
        LastServerNotAfter = cert.NotAfter;
    }

    private async Task<XElement> PostAsync(string serviceName, string operation, XNamespace serviceNs, Uri endpoint, string envelope, TimeSpan? timeout, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(envelope);
        var headers = new Dictionary<string, string>
        {
            ["SOAPAction"] = $"\"{ConnectorProtocol.SoapAction(serviceNs, operation)}\""
        };
        _log?.Invoke($"{serviceName}.{operation}");
        var body = await SendAsync(endpoint, "POST", bytes, "text/xml; charset=utf-8", headers, timeout ?? DefaultTimeout, ct);
        LastResponseXml = body;
        return ConnectorProtocol.ParseBody(body);
    }

    private Task<XElement> CallAsync(string serviceName, string defaultNs, Func<XNamespace, XElement> build, string operation, TimeSpan? timeout, CancellationToken ct) =>
        CallWithUserIdRecoveryAsync(serviceName, defaultNs, build, operation, timeout, ct);

    private async Task<XElement> CallCoreAsync(string serviceName, string defaultNs, Func<XNamespace, XElement> build, string operation, TimeSpan? timeout, CancellationToken ct)
    {
        var directory = await GetServiceDirectoryAsync(ct);
        var service = directory.Find(serviceName)
                      ?? throw new ConnectorFaultException(LocalizationService.Pick($"Der Konnektor bietet den Dienst {serviceName} nicht an.", $"The connector does not offer the service {serviceName}."));
        var endpoint = ConnectorProtocol.ResolveEndpoint(service.EndpointFor(_baseUri.Scheme == Uri.UriSchemeHttps), _baseUri, _settings.RewriteEndpointHost);

        // Namespace-Varianten der Reihe nach durchprobieren. Das ist ein echter Test, kein Raten:
        // antwortet der Konnektor mit einer Variante, hoeren wir sofort auf. Scheitern alle, ist
        // der Namespace als Ursache widerlegt und die Diagnose oben muss das benennen koennen.
        var candidates = ConnectorProtocol.NamespaceCandidates(service.TargetNamespace, defaultNs);
        ConnectorFaultException? last = null;

        foreach (var candidate in candidates)
        {
            XNamespace ns = candidate;
            var envelope = ConnectorProtocol.Envelope(build(ns));
            LastRequestXml = envelope;
            try
            {
                var answer = await PostAsync(serviceName, operation, ns, endpoint, envelope, timeout, ct);
                _okByService[serviceName] = _okByService.TryGetValue(serviceName, out var done) ? done + 1 : 1;

                // Der Namespace, mit dem der EventService antwortet, ist auch derjenige, mit dem
                // ein Abo Erfolg haben kann. Merkt man sich ihn, braucht spaeter kein Topic mit
                // einer Version gebaut zu werden, die dieser Konnektor nicht kennt.
                if (string.Equals(serviceName, "EventService", StringComparison.OrdinalIgnoreCase))
                    ConnectorCapabilities.SetEventNamespace(_settings.Host, candidate);

                return answer;
            }
            catch (ConnectorFaultException ex) when (ConnectorProtocol.IsSyntaxError(ex))
            {
                last = ex;
                _log?.Invoke($"{serviceName}.{operation}: abgelehnt ({ex.Code ?? "4000"}), Namespace {candidate}");
                if (candidates.Count > 1)
                    _log?.Invoke("  nächster Namespace-Versuch");
            }
        }

        // Eine einzige Rueckfallebene, klar protokolliert: eine unzulassige Benutzer-ID laesst
        // beim RISE-Konnektor saemtliche Aufrufe mit 4000 scheitern. Weglassen statt wechseln ist
        // hier die einzige Vernderung, die der Konnektor akzeptiert, und sie ist reversibel.
        if (last is not null && ConnectorProtocol.IsSyntaxError(last) && !string.IsNullOrWhiteSpace(_settings.UserId))
        {
            var saved = _settings.UserId;
            var keepOmitted = false;
            _settings.UserId = "";
            try
            {
                var answer = await PostAsync(serviceName, operation, candidates[0], endpoint,
                    ConnectorProtocol.Envelope(build(candidates[0])), timeout, ct);
                _log?.Invoke($"  Gelungen ohne Benutzer-ID \u201E{saved}\u201C – die akzeptiert dieser Konnektor nicht");
                ConnectorCapabilities.SetOmitUserId(ConnectorCapabilities.OmitKey(_settings.Host, saved), true);
                // Fuer den Rest dieses Durchlaufs ohne ID weiter – sonst scheitert jeder weitere
                // Aufruf erst mit 4000, bevor er ueber denselben Umweg doch gelingt.
                keepOmitted = true;
                _okByService[serviceName] = _okByService.TryGetValue(serviceName, out var done) ? done + 1 : 1;
                return answer;
            }
            catch (ConnectorFaultException)
            {
                // Ohne ID bleibt es auch abgelehnt – dann ist die ID die Ursache gewiss nicht.
            }
            finally
            {
                if (!keepOmitted)
                    _settings.UserId = saved;
            }
        }

        throw new ConnectorFaultException(DescribeFault(last!, serviceName, operation, endpoint, candidates), last!.Code);
    }

    /// <summary>
    /// Wird die Benutzer-ID gerade weggelassen und verlangt der Konnektor sie (4003, z. B. HBA-
    /// Operationen), einmal MIT ID wiederholen – und die Weglass-Annahme fuer diese ID verwerfen.
    /// </summary>
    private async Task<XElement> CallWithUserIdRecoveryAsync(string serviceName, string defaultNs, Func<XNamespace, XElement> build, string operation, TimeSpan? timeout, CancellationToken ct)
    {
        try
        {
            return await CallCoreAsync(serviceName, defaultNs, build, operation, timeout, ct);
        }
        catch (ConnectorFaultException ex) when (ex.Code == "4003" && string.IsNullOrWhiteSpace(_settings.UserId) && !string.IsNullOrWhiteSpace(_configuredUserId))
        {
            _log?.Invoke(LocalizationService.Pick("  4003 ohne Benutzer-ID – erneuter Versuch mit Benutzer-ID", "  4003 without user ID – retrying with user ID"));
            ConnectorCapabilities.SetOmitUserId(ConnectorCapabilities.OmitKey(_settings.Host, _configuredUserId), false);
            _settings.UserId = _configuredUserId;
            return await CallCoreAsync(serviceName, defaultNs, build, operation, timeout, ct);
        }
    }

    /// <summary>
    /// Erklaert einen zurueckgewiesenen Aufruf anhand dessen, was die Sitzung bisher gezeigt hat.
    /// Der entscheidende Unterschied zu frueheren Versionen: der Aufrufkontext wird nicht mehr
    /// pauschal beschuldigt, sondern gegen die erfolgreich beantworteten Dienste desselben
    /// Durchlaufs geprueft. Ist der Kontext woanders akzeptiert, faellt die Behauptung weg.
    /// </summary>
    private string DescribeFault(ConnectorFaultException fault, string serviceName, string operation, Uri endpoint, List<string> candidates)
    {
        var lines = new List<string> { fault.Message };
        var code = fault.Code ?? "";

        // 1. Kontext: nur anprangern, wenn er formal unzulaessig ist. Alles andere ist Spekulation.
        var contextProblems = ConnectorProtocol.ValidateContextIds(_settings);
        if (contextProblems is not null)
        {
            lines.Add(L("Der Aufrufkontext enthält unzulässige Angaben:\n", "The call context contains invalid values:\n") + contextProblems);
        }
        else if (_okByService.Count > 0)
        {
            var working = string.Join(", ", _okByService.OrderBy(k => k.Key).Select(k => $"{k.Key} ({_okByService[k.Key]}×)"));
            lines.Add(L($"Mit exakt demselben Aufrufkontext haben in diesem Durchlauf {working} geantwortet. " +
                        "Der Kontext kann die Ursache dieser Ablehnung deshalb nicht sein.",
                        $"With exactly the same call context, {working} answered in this run. " +
                        "The context therefore cannot be the cause of this rejection."));
        }
        else
        {
            var ctx = $"Mandant=\u201E{_settings.MandantId}\u201C, Clientsystem=\u201E{_settings.ClientSystemId}\u201C, " +
                      $"Arbeitsplatz=\u201E{_settings.WorkplaceId}\u201C" +
                      (string.IsNullOrWhiteSpace(_settings.UserId) ? "" : $", User=\u201E{_settings.UserId}\u201C");
            lines.Add(L("Noch kein anderer Dienst mit diesem Kontext befragt – Vergleiche fehlen. Kontext: ",
                        "No other service queried with this context yet – nothing to compare. Context: ") + ctx);
        }

        // 2. Namespace: nur ein Hinweis, wenn das Verzeichnis tatsaechlich den /WSDL/-Wert liefert.
        if (candidates.Count > 1)
            lines.Add(L("Getestete Namespaces: ", "Namespaces tried: ") + string.Join(", ", candidates));

        // 3. 4003: eine zwingende Angabe fehlt. Fuer Kartensitzungen des HBA ist das die Benutzer-ID.
        if (code.StartsWith("4003", StringComparison.Ordinal))
        {
            lines.Add(string.IsNullOrWhiteSpace(_settings.UserId)
                ? L("Diese Operation braucht eine Benutzer-ID im Aufrufkontext. Sie ist im Einstellfeld " +
                    "\u201EBenutzer-ID (optional)\u201C leer – bitte die in der Konnektor-Administration geführte " +
                    "Benutzer-ID des Arbeitsplatzes eintragen und erneut abrufen.",
                    "This operation needs a user ID in the call context. The field \u201EUser ID (optional)\u201C is empty – " +
                    "please enter the workplace user ID kept in the connector administration and retry.")
                : L($"Der Konnektor akzeptiert die eingetragene Benutzer-ID \u201E{_settings.UserId}\u201C für diese " +
                    "Operation nicht. Mit der Konnektor-Administration vergleichen, dort steht die gültige Schreibweise.",
                    $"The connector does not accept the user ID \u201E{_settings.UserId}\u201C for this operation. " +
                    "Compare with the connector administration for the valid spelling."));
        }

        // 4. 4000 auf genau einer Operation, obwohl derselbe Dienst sonst antwortet: dann liegt es
        // am Inhalt dieser Operation, nicht an Adresse, Namespace oder Kontext.
        else if (code.StartsWith("4000", StringComparison.Ordinal) && _okByService.TryGetValue(serviceName, out var passed))
        {
            lines.Add(L($"Der {serviceName} hat in diesem Durchlauf schon {passed} Mal geantwortet, nur {operation} wird abgelehnt. " +
                        "Adresse, Namespace und Aufrufkontext sind damit ausgeschlossen. Die Ablehnung hängt an den " +
                        "Angaben dieser einen Operation.",
                        $"{serviceName} already answered {passed} times in this run; only {operation} is rejected. " +
                        "Address, namespace and call context are therefore ruled out. The rejection depends on the " +
                        "content of this one operation."));
        }
        else if (code.StartsWith("4000", StringComparison.Ordinal))
        {
            lines.Add(L("Der Namespace wurde in allen Varianten probiert und jeweils gleich abgelehnt, " +
                        "er scheidet als Ursache damit aus. Übrig bleiben die Angaben in der Nachricht selbst.",
                        "All namespace variants were tried and rejected the same way, so the namespace is ruled out. " +
                        "What remains is the content of the message itself."));
        }

        lines.Add(L("Endpunkt: ", "Endpoint: ") + endpoint);
        lines.Add(L("Zuletzt gesendeter Request: ", "Last request sent: ") + ShortenXml(LastRequestXml));
        return string.Join("\n", lines);
    }

    private static string L(string de, string en) => LocalizationService.Pick(de, en);

    private static string ShortenXml(string xml) =>
        xml.Length <= 900 ? xml : xml[..900] + " …";

    // ------------------------------------------------------------------
    // Operationen
    // ------------------------------------------------------------------

    public async Task<ConnectorServiceDirectory> GetServiceDirectoryAsync(CancellationToken ct)
    {
        if (_directory is not null)
            return _directory;

        var xml = await SendAsync(new Uri(_baseUri, "connector.sds"), "GET", null, null, null, DefaultTimeout, ct);
        try
        {
            _directory = ConnectorProtocol.ParseServiceDirectory(xml);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ConnectorFaultException(LocalizationService.Pick($"connector.sds konnte nicht gelesen werden: {ex.Message}", $"connector.sds could not be read: {ex.Message}"));
        }
        _log?.Invoke($"Dienstverzeichnis: {_directory.Services.Count} Dienst-Versionen");
        return _directory;
    }

    /// <summary>
    /// Faelt der Mandanten-Radius bei einer Operation, die ihn schon einmal akzeptiert hat, ist
    /// das kein Grund, die Eigenart des Konnektors zu bestaetigen. Ein Konnektor, der auf "true"
    /// bestaendig mit 4000 antwortet, haette sonst bei jedem Poll zwei zusaetzliche Roundtrips.
    /// </summary>
    private async Task<List<ConnectorCard>> GetCardsAsync(CancellationToken ct)
    {
        foreach (var wide in ConnectorCapabilities.MandantWideAttempts(ConnectorCapabilities.GetMandantWide(_settings.Host)))
        {
            try
            {
                var cards = ConnectorProtocol.ParseCards(await CallAsync("EventService", ConnectorProtocol.DefaultEventServiceNs,
                    ns => ConnectorProtocol.GetCards(ns, _settings, wide), "GetCards", null, ct));
                ConnectorCapabilities.SetMandantWide(_settings.Host, wide);
                return cards;
            }
            catch (ConnectorFaultException ex) when (ConnectorProtocol.IsSyntaxError(ex))
            {
                // "mandant-wide=true" ist nach gemSpec_Kon zulaessig, wird aber von mancher
                // Firmware nicht angenommen. Der naechste Versuch bleibt, und nur der letzte
                // Ausschlag praegt das Geaechtnis.
                _log?.Invoke($"Karten: mandant-wide={wide.ToString().ToLowerInvariant()} abgelehnt" +
                             (wide ? " – zweiter Versuch ohne Mandanten-Radius" : ""));
            }
        }

        // Beide Varianten abgelehnt: das Geaechtnis unentschieden lassen, damit spaeter
        // erneut probiert wird, statt auf einer widerlegten Annahme stehen zu bleiben.
        ConnectorCapabilities.ForgetMandantWide(_settings.Host);
        return ConnectorProtocol.ParseCards(await CallAsync("EventService", ConnectorProtocol.DefaultEventServiceNs,
            ns => ConnectorProtocol.GetCards(ns, _settings, false), "GetCards", null, ct));
    }

    /// <summary>Dieselbe Rueckfallebene wie bei den Karten. Der Radius gilt fuer den Mandanten,
    /// nicht fuer die Operation, deshalb teilen sich beide dasselbe Geaechtnis.</summary>
    private async Task<List<ConnectorCardTerminal>> GetCardTerminalsAsync(CancellationToken ct)
    {
        foreach (var wide in ConnectorCapabilities.MandantWideAttempts(ConnectorCapabilities.GetMandantWide(_settings.Host)))
        {
            try
            {
                var terminals = ConnectorProtocol.ParseCardTerminals(await CallAsync("EventService", ConnectorProtocol.DefaultEventServiceNs,
                    ns => ConnectorProtocol.GetCardTerminals(ns, _settings, wide), "GetCardTerminals", null, ct));
                ConnectorCapabilities.SetMandantWide(_settings.Host, wide);
                return terminals;
            }
            catch (ConnectorFaultException ex) when (ConnectorProtocol.IsSyntaxError(ex))
            {
                _log?.Invoke($"Kartenterminals: mandant-wide={wide.ToString().ToLowerInvariant()} abgelehnt");
            }
        }

        ConnectorCapabilities.ForgetMandantWide(_settings.Host);
        return ConnectorProtocol.ParseCardTerminals(await CallAsync("EventService", ConnectorProtocol.DefaultEventServiceNs,
            ns => ConnectorProtocol.GetCardTerminals(ns, _settings, false), "GetCardTerminals", null, ct));
    }

    public async Task<ConnectorResourceInfo> GetResourceInformationAsync(CancellationToken ct) =>
        ConnectorProtocol.ParseResourceInformation(await CallAsync("EventService", ConnectorProtocol.DefaultEventServiceNs,
            ns => ConnectorProtocol.GetResourceInformation(ns, _settings), "GetResourceInformation", null, ct));

    // ------------------------------------------------------------------
    // Ereignisabos
    // ------------------------------------------------------------------

    /// <summary>Abo anlegen. Der Konnektor baut danach eine TCP-Verbindung zu eventTo auf.</summary>
    public async Task<(string SubscriptionId, DateTime? TerminationTime)> SubscribeAsync(string eventTo, string topic, CancellationToken ct)
    {
        var response = await CallAsync("EventService", ConnectorProtocol.DefaultEventServiceNs,
            ns => ConnectorProtocol.Subscribe(ns, _settings, eventTo, topic), "Subscribe", null, ct);
        var id = response.Elements().FirstOrDefault(e => e.Name.LocalName == "SubscriptionID")?.Value.Trim() ?? "";
        var termination = response.Elements().FirstOrDefault(e => e.Name.LocalName == "TerminationTime");
        DateTime? until = null;
        if (termination is not null && DateTime.TryParse(termination.Value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t))
            until = t.ToLocalTime();
        return (id, until);
    }

    public async Task<List<ConnectorSubscription>> GetSubscriptionsAsync(CancellationToken ct) =>
        ConnectorProtocol.ParseSubscriptions(await CallAsync("EventService", ConnectorProtocol.DefaultEventServiceNs,
            ns => ConnectorProtocol.GetSubscription(ns, _settings), "GetSubscription", null, ct));

    public async Task UnsubscribeAsync(string subscriptionId, CancellationToken ct) =>
        await CallAsync("EventService", ConnectorProtocol.DefaultEventServiceNs,
            ns => ConnectorProtocol.Unsubscribe(ns, _settings, subscriptionId), "Unsubscribe", null, ct);

    public async Task<List<(string SubscriptionId, DateTime? TerminationTime)>> RenewSubscriptionsAsync(IEnumerable<string> subscriptionIds, CancellationToken ct) =>
        ConnectorProtocol.ParseRenewals(await CallAsync("EventService", ConnectorProtocol.DefaultEventServiceNs,
            ns => ConnectorProtocol.RenewSubscriptions(ns, _settings, subscriptionIds), "RenewSubscriptions", null, ct));

    /// <summary>
    /// Liest alle vier nach CertRefEnum moeglichen Zertifikate einer Karte. Fehlende Refs
    /// sind normal (SMC-B fuehrt kein C.QES) und werden als leer, nicht als Fehler behandelt.
    /// </summary>
    public async Task<List<ConnectorCertificateInfo>> ReadAllCertificatesAsync(string cardHandle, CancellationToken ct)
    {
        var result = new List<ConnectorCertificateInfo>();
        foreach (var (reference, _) in ConnectorProtocol.KnownCertRefs)
        {
            try
            {
                var info = await ReadCardCertificateAsync(cardHandle, reference, ct);
                if (info is not null)
                    result.Add(info);
            }
            catch (ConnectorFaultException) when (reference != "C.AUT")
            {
                // Karte fuehrt diese Referenz nicht – genau das wollen wir wissen, nicht melden.
            }
        }
        return result;
    }

    public async Task<ConnectorPinState> GetPinStatusAsync(string cardHandle, string pinType, CancellationToken ct) =>
        ConnectorProtocol.ParsePinStatus(await CallAsync("CardService", ConnectorProtocol.DefaultCardServiceNs,
            ns => ConnectorProtocol.GetPinStatus(ns, _settings, cardHandle, pinType), "GetPinStatus", null, ct), pinType);

    /// <summary>
    /// VerifyPin, ChangePin, UnblockPin, EnablePin, DisablePin. Die PIN/PUK wird ausschließlich
    /// am Kartenterminal eingegeben; die App sieht sie nie.
    /// </summary>
    public async Task<ConnectorPinResult> PinOperationAsync(string operation, string cardHandle, string pinType, bool? setNewPin, CancellationToken ct) =>
        ConnectorProtocol.ParsePinResult(await CallAsync("CardService", ConnectorProtocol.DefaultCardServiceNs,
            ns => ConnectorProtocol.PinOperation(ns, operation, _settings, cardHandle, pinType, setNewPin), operation, PinOperationTimeout, ct));

    public async Task<ConnectorCertificateInfo?> ReadCardCertificateAsync(string cardHandle, string certRef, CancellationToken ct)
    {
        XElement response;
        try
        {
            response = await CallAsync("CertificateService", ConnectorProtocol.DefaultCertificateServiceNs,
                ns => ConnectorProtocol.ReadCardCertificate(ns, _settings, cardHandle, certRef), "ReadCardCertificate", null, ct);
        }
        catch (ConnectorFaultException)
        {
            // Reine ECC-Karten (G2.1+) liefern ohne explizites Crypt=ECC u. U. einen Fehler.
            response = await CallAsync("CertificateService", ConnectorProtocol.DefaultCertificateServiceNs,
                ns => ConnectorProtocol.ReadCardCertificate(ns, _settings, cardHandle, certRef, "ECC"), "ReadCardCertificate", null, ct);
        }

        var first = ConnectorProtocol.ParseCardCertificates(response).FirstOrDefault();
        return first.Base64 is null ? null : ConnectorProtocol.DescribeCertificate(string.IsNullOrEmpty(first.CertRef) ? certRef : first.CertRef, first.Base64);
    }

    public async Task<ConnectorVerificationResult> VerifyCertificateAsync(string base64Certificate, CancellationToken ct) =>
        ConnectorProtocol.ParseVerification(await CallAsync("CertificateService", ConnectorProtocol.DefaultCertificateServiceNs,
            ns => ConnectorProtocol.VerifyCertificate(ns, _settings, base64Certificate), "VerifyCertificate", TimeSpan.FromSeconds(60), ct));

    /// <summary>
    /// Probesignatur mit dem HBA (QES): holt eine Jobnummer und signiert einen kurzen Testtext
    /// per SignDocument. Der Konnektor verlangt dafuer die PIN.QES am Kartenterminal – erst die
    /// erfolgreiche Antwort zeigt, dass HBA, Zertifikat, PIN und Signaturdienst zusammenspielen.
    /// </summary>
    public async Task<ConnectorSignatureResult> QesTestSignatureAsync(string cardHandle, CancellationToken ct)
    {
        var directory = await GetServiceDirectoryAsync(ct);
        if (directory.Find("SignatureService") is null)
            throw new ConnectorFaultException(LocalizationService.Pick(
                "Der Konnektor bietet keinen Signaturdienst (SignatureService) an.",
                "The connector does not offer a signature service (SignatureService)."));

        var job = ConnectorProtocol.ParseJobNumber(await CallAsync("SignatureService", ConnectorProtocol.DefaultSignatureServiceNs,
            ns => ConnectorProtocol.GetJobNumber(ns, _settings), "GetJobNumber", null, ct));
        if (string.IsNullOrWhiteSpace(job))
            throw new ConnectorFaultException(LocalizationService.Pick(
                "Der Konnektor hat keine Jobnummer für die Signatur geliefert.",
                "The connector returned no job number for the signature."));

        var text = $"TI-Lage Monitor – QES-Probesignatur {DateTime.Now:yyyy-MM-dd HH:mm:ss}. " +
                   "Dieses Dokument dient ausschließlich dem Funktionstest und hat keinen rechtlichen Inhalt.";
        var shortText = LocalizationService.Pick("TI-Lage Monitor Signaturtest", "TI-Lage Monitor signature test");
        var response = await CallAsync("SignatureService", ConnectorProtocol.DefaultSignatureServiceNs,
            ns => ConnectorProtocol.SignDocument(ns, _settings, cardHandle, job, Encoding.UTF8.GetBytes(text), shortText),
            "SignDocument", PinOperationTimeout, ct);
        return ConnectorProtocol.ParseSignResult(response);
    }

    /// <summary>
    /// Liest alles Sinnvolle in einem Durchgang. Teilfehler (z. B. ein nicht unterstützter Dienst)
    /// landen in <see cref="ConnectorSnapshot.Warnings"/>, statt den ganzen Abruf abzubrechen.
    /// Nur ein Fehler beim Dienstverzeichnis ist fatal.
    /// </summary>
    public async Task<ConnectorSnapshot> GetSnapshotAsync(bool includeCertificates, CancellationToken ct)
    {
        var snapshot = new ConnectorSnapshot
        {
            Directory = await GetServiceDirectoryAsync(ct)
        };

        async Task Try(string label, Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                snapshot.Warnings.Add($"{label}: {ex.Message}");
                _log?.Invoke($"{label} fehlgeschlagen: {ex.Message}");
            }
        }

        await Try(LocalizationService.Pick("Betriebszustand", "Operating state") + " (GetResourceInformation)", async () => snapshot.Resource = await GetResourceInformationAsync(ct));
        await Try(LocalizationService.Pick("Kartenterminals", "Card terminals") + " (GetCardTerminals)", async () => snapshot.Terminals = await GetCardTerminalsAsync(ct));
        await Try(LocalizationService.Pick("Karten", "Cards") + " (GetCards)", async () => snapshot.Cards = await GetCardsAsync(ct));

        // Aktive Abos anzeigen: ein getaetigtes Abo, an das der Konnektor nicht mehr sendet,
        // ist eine typische Ursache dafuer, dass die App eine gesteckte Karte nicht merkt.
        await Try(LocalizationService.Pick("Ereignis-Abos", "Event subscriptions") + " (GetSubscription)", async () => snapshot.Subscriptions = await GetSubscriptionsAsync(ct));

        foreach (var card in snapshot.Cards.Where(c => c.SupportsPinManagement))
        {
            foreach (var pinType in card.PinTypes)
            {
                try
                {
                    card.PinStates.Add(await GetPinStatusAsync(card.CardHandle, pinType, ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    card.PinStates.Add(new ConnectorPinState { PinType = pinType, Error = ex.Message });
                }
            }

            if (includeCertificates)
            {
                await Try($"Zertifikat {card.DisplayName}", async () =>
                    card.AuthCertificate = await ReadCardCertificateAsync(card.CardHandle, "C.AUT", ct));
            }
        }

        snapshot.ClockOffset = ClockOffset;
        snapshot.ServerCertificateThumbprint = LastServerThumbprint;
        snapshot.ServerCertificateSubject = LastServerSubject;
        snapshot.ServerCertificateNotAfter = LastServerNotAfter;
        snapshot.RetrievedAt = DateTime.Now;
        return snapshot;
    }

    public void Dispose()
    {
        // Transport hält keine gepoolten Verbindungen über die Lebensdauer hinaus (Connection: close).
        // Der P12-Schlüssel bleibt nur im Arbeitsspeicher dieses Clients.
    }
}
