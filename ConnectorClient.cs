using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
    private readonly HttpClient _http;
    private readonly X509Certificate2? _clientCertificate;
    private readonly X509Certificate2Collection? _clientCertificateBag;
    private readonly Uri _baseUri;
    private readonly Action<string>? _log;
    private ConnectorServiceDirectory? _directory;

    public string? LastServerThumbprint { get; private set; }
    public string? LastServerSubject { get; private set; }
    public DateTime? LastServerNotAfter { get; private set; }
    private bool _lastServerRejected;

    /// <summary>Zuletzt gesendeter XML-Rumpf – nur für Diagnoseberichte und Log interessant.</summary>
    public string LastRequestXml { get; private set; } = "";
    public string LastResponseXml { get; private set; } = "";

    public ConnectorClient(ConnectorSettings settings, Action<string>? log = null)
    {
        _settings = settings.Clone();
        _log = log;
        _baseUri = ConnectorProtocol.BuildBaseUri(_settings.Host, _settings.UseTls);

        if (!string.IsNullOrWhiteSpace(_settings.ClientCertificatePath))
        {
            (_clientCertificate, _clientCertificateBag) = LoadClientCertificate(
                _settings.ClientCertificatePath,
                SecretProtector.Unprotect(_settings.ClientCertificatePasswordProtected));
        }

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            UseProxy = false, // Konnektor ist immer im lokalen Netz
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = ValidateServerCertificate,
                // Zertifikat immer anbieten – Windows würde es sonst ggf. wegfiltern, wenn die
                // CA-Liste des Konnektors nicht zum Aussteller des Client-Zertifikats passt.
                LocalCertificateSelectionCallback = (_, _, _, _, _) => _clientCertificate!
            }
        };
        if (_clientCertificate is not null)
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { _clientCertificate };
        else
            handler.SslOptions.LocalCertificateSelectionCallback = null;

        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        var basicPassword = SecretProtector.Unprotect(_settings.BasicAuthPasswordProtected);
        if (!string.IsNullOrWhiteSpace(_settings.BasicAuthUser))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_settings.BasicAuthUser}:{basicPassword}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"TILageMonitor/{UpdateService.CurrentVersion}");
    }

    public X509Certificate2? ClientCertificate => _clientCertificate;

    public static (X509Certificate2 Certificate, X509Certificate2Collection Bag) LoadClientCertificate(string path, string? password)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Zertifikatsdatei nicht gefunden: {path}");

        X509Certificate2Collection bag;
        try
        {
            // UserKeySet: Schannel benötigt einen (temporär) persistierten Schlüssel; EphemeralKeySet
            // funktioniert unter Windows nicht für TLS-Client-Authentifizierung.
            bag = X509CertificateLoader.LoadPkcs12CollectionFromFile(
                path,
                string.IsNullOrEmpty(password) ? null : password,
                X509KeyStorageFlags.UserKeySet);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"Das Client-Zertifikat konnte nicht geöffnet werden (Passwort falsch oder Datei beschädigt): {ex.Message}", ex);
        }

        var withKey = bag.FirstOrDefault(c => c.HasPrivateKey)
                      ?? throw new InvalidOperationException("Die .p12-Datei enthält kein Zertifikat mit privatem Schlüssel.");
        return (withKey, bag);
    }

    private bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null)
            return false;

        using var cert = new X509Certificate2(certificate);
        var thumbprint = Convert.ToHexString(SHA256.HashData(cert.RawData));
        LastServerThumbprint = thumbprint;
        LastServerSubject = cert.Subject;
        LastServerNotAfter = cert.NotAfter;

        var pinned = _settings.PinnedServerThumbprint;
        var accepted = !string.IsNullOrWhiteSpace(pinned)
            ? string.Equals(pinned, thumbprint, StringComparison.OrdinalIgnoreCase)
            : errors == SslPolicyErrors.None;
        _lastServerRejected = !accepted;
        return accepted;
    }

    // ------------------------------------------------------------------
    // Transport
    // ------------------------------------------------------------------

    private async Task<string> SendAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            _lastServerRejected = false;
            using var response = await _http.SendAsync(request, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            _log?.Invoke($"{request.Method} {request.RequestUri?.AbsolutePath} → {(int)response.StatusCode} ({sw.ElapsedMilliseconds} ms)");

            // SOAP-Faults kommen mit HTTP 500 – Body trotzdem auswerten.
            if (!response.IsSuccessStatusCode && !body.Contains("Envelope", StringComparison.Ordinal))
            {
                var hint = (int)response.StatusCode switch
                {
                    401 => " – Authentifizierung abgelehnt (Client-Zertifikat bzw. Benutzer/Passwort prüfen).",
                    403 => " – Zugriff verweigert (Clientsystem im Konnektor freigeschaltet?).",
                    404 => " – Endpunkt nicht gefunden.",
                    _ => ""
                };
                throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}{hint}");
            }
            return body;
        }
        catch (HttpRequestException) when (_lastServerRejected && LastServerThumbprint is not null)
        {
            throw new ConnectorUntrustedServerException(LastServerThumbprint, LastServerSubject ?? "", LastServerNotAfter ?? DateTime.MinValue);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log?.Invoke($"{request.RequestUri?.AbsolutePath} → Zeitüberschreitung nach {timeout.TotalSeconds:0} s");
            throw new TimeoutException($"Zeitüberschreitung: Der Konnektor hat nicht innerhalb von {timeout.TotalSeconds:0} Sekunden geantwortet.");
        }
    }

    private async Task<XElement> CallAsync(string serviceName, string defaultNs, Func<XNamespace, XElement> build, string operation, TimeSpan? timeout, CancellationToken ct)
    {
        var directory = await GetServiceDirectoryAsync(ct);
        var service = directory.Find(serviceName)
                      ?? throw new ConnectorFaultException($"Der Konnektor bietet den Dienst {serviceName} nicht an.");
        XNamespace ns = string.IsNullOrWhiteSpace(service.TargetNamespace) ? defaultNs : service.TargetNamespace;
        var endpoint = ConnectorProtocol.ResolveEndpoint(service.Endpoint, _baseUri, _settings.RewriteEndpointHost);

        var envelope = ConnectorProtocol.Envelope(build(ns));
        LastRequestXml = envelope;
        try
        {
            return await PostAsync(serviceName, operation, ns, endpoint, envelope, timeout, ct);
        }
        catch (ConnectorFaultException ex) when (ConnectorProtocol.IsSyntaxError(ex))
        {
            // RISE-Konnektoren quittieren unzulässige Eingaben pauschal mit "4000 Syntaxfehler".
            // Zwei harmlose Varianten, die wir testweise durchspielen, damit der Nutzer nicht
            // raten muss: Aufruf ohne Benutzer-ID, und (für Karten/Terminals) ohne Mandanten-Radius.
            _log?.Invoke($"{serviceName}.{operation}: Syntaxfehler – 4000, prüfe Aufrufkontext und Mandanten-Radius");
            _log?.Invoke($"  gesendeter Request: {ShortenXml(envelope)}");

            if (!string.IsNullOrWhiteSpace(_settings.UserId))
            {
                var saved = _settings.UserId;
                _settings.UserId = "";
                try
                {
                    var retried = await PostAsync(serviceName, operation, ns, endpoint,
                        ConnectorProtocol.Envelope(build(ns)), timeout, ct);
                    _log?.Invoke($"  Erfolg ohne Benutzer-ID \u201E{saved}\u201C – diese ID akzeptiert der Konnektor nicht");
                    return retried;
                }
                finally
                {
                    _settings.UserId = saved;
                }
            }

            // Alle drei EventService-Operationen scheitern gleich, wenn der Konnektor eine
            // ID im Aufrufkontext ablehnt – deshalb beide Werte für den Vergleich mit der
            // Konnektor-Administration mitliefern.
            throw new ConnectorFaultException(
                $"{ex.Message}\nAufrufkontext: Mandant=\u201E{_settings.MandantId}\u201C, Clientsystem=\u201E{_settings.ClientSystemId}\u201C, " +
                $"Arbeitsplatz=\u201E{_settings.WorkplaceId}\u201C{(string.IsNullOrWhiteSpace(_settings.UserId) ? "" : $", Benutzer=\u201E{_settings.UserId}\u201C")}\n" +
                "Bitte exakt die Werte aus der Konnektor-Administration verwenden (nur A-Z, a-z, 0-9, - und _).\n" +
                $"Gesendeter Request: {ShortenXml(envelope)}", ex.Code);
        }
    }

    private static string ShortenXml(string xml) =>
        xml.Length <= 900 ? xml : xml[..900] + " …";

    private async Task<XElement> PostAsync(string serviceName, string operation, XNamespace serviceNs, Uri endpoint, string envelope, TimeSpan? timeout, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "text/xml")
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{ConnectorProtocol.SoapAction(serviceNs, operation)}\"");
        _log?.Invoke($"{serviceName}.{operation}");
        var body = await SendAsync(request, timeout ?? DefaultTimeout, ct);
        LastResponseXml = body;
        return ConnectorProtocol.ParseBody(body);
    }

    // ------------------------------------------------------------------
    // Operationen
    // ------------------------------------------------------------------

    public async Task<ConnectorServiceDirectory> GetServiceDirectoryAsync(CancellationToken ct)
    {
        if (_directory is not null)
            return _directory;

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, "connector.sds"));
        var xml = await SendAsync(request, DefaultTimeout, ct);
        try
        {
            _directory = ConnectorProtocol.ParseServiceDirectory(xml);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ConnectorFaultException($"connector.sds konnte nicht gelesen werden: {ex.Message}");
        }
        _log?.Invoke($"Dienstverzeichnis: {_directory.Services.Count} Dienst-Versionen");
        return _directory;
    }

    public async Task<List<ConnectorCard>> GetCardsAsync(CancellationToken ct)
    {
        try
        {
            return ConnectorProtocol.ParseCards(await CallAsync("EventService", ConnectorProtocol.DefaultEventServiceNs,
                ns => ConnectorProtocol.GetCards(ns, _settings), "GetCards", null, ct));
        }
        catch (ConnectorFaultException ex) when (ConnectorProtocol.IsSyntaxError(ex))
        {
            // "mandant-wide=true" ist nach gemSpec_Kon zulässig, wird aber von mannger Firmware
            // nicht angenommen. Zweiter Versuch nur für das eingestellte Mandantenpaar.
            _log?.Invoke("Karten: zweiter Versuch ohne Mandanten-Radius (mandant-wide=false)");
            return ConnectorProtocol.ParseCards(await CallAsync("EventService", ConnectorProtocol.DefaultEventServiceNs,
                ns => ConnectorProtocol.GetCards(ns, _settings, mandantWide: false), "GetCards", null, ct));
        }
    }

    public async Task<List<ConnectorCardTerminal>> GetCardTerminalsAsync(CancellationToken ct)
    {
        try
        {
            return ConnectorProtocol.ParseCardTerminals(await CallAsync("EventService", ConnectorProtocol.DefaultEventServiceNs,
                ns => ConnectorProtocol.GetCardTerminals(ns, _settings), "GetCardTerminals", null, ct));
        }
        catch (ConnectorFaultException ex) when (ConnectorProtocol.IsSyntaxError(ex))
        {
            _log?.Invoke("Kartenterminals: zweiter Versuch ohne Mandanten-Radius (mandant-wide=false)");
            return ConnectorProtocol.ParseCardTerminals(await CallAsync("EventService", ConnectorProtocol.DefaultEventServiceNs,
                ns => ConnectorProtocol.GetCardTerminals(ns, _settings, mandantWide: false), "GetCardTerminals", null, ct));
        }
    }

    public async Task<ConnectorResourceInfo> GetResourceInformationAsync(CancellationToken ct) =>
        ConnectorProtocol.ParseResourceInformation(await CallAsync("EventService", ConnectorProtocol.DefaultEventServiceNs,
            ns => ConnectorProtocol.GetResourceInformation(ns, _settings), "GetResourceInformation", null, ct));

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

        await Try("Betriebszustand (GetResourceInformation)", async () => snapshot.Resource = await GetResourceInformationAsync(ct));
        await Try("Kartenterminals (GetCardTerminals)", async () => snapshot.Terminals = await GetCardTerminalsAsync(ct));
        await Try("Karten (GetCards)", async () => snapshot.Cards = await GetCardsAsync(ct));

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

        snapshot.ServerCertificateThumbprint = LastServerThumbprint;
        snapshot.ServerCertificateSubject = LastServerSubject;
        snapshot.ServerCertificateNotAfter = LastServerNotAfter;
        snapshot.RetrievedAt = DateTime.Now;
        return snapshot;
    }

    public void Dispose()
    {
        _http.Dispose();
        if (_clientCertificateBag is not null)
        {
            foreach (var cert in _clientCertificateBag)
                cert.Dispose();
        }
    }
}
