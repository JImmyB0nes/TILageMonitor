using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace TILageMonitor;

/// <summary>TLS failure with a readable DE/EN message (includes the TLS alert description).</summary>
public sealed class ConnectorTlsException : Exception
{
    public short? Alert { get; }
    public bool PinFailure { get; }

    public ConnectorTlsException(string message, short? alert = null, bool pinFailure = false, Exception? inner = null)
        : base(message, inner)
    {
        Alert = alert;
        PinFailure = pinFailure;
    }
}

/// <summary>
/// BouncyCastle TLS client for the Konnektor: TLS 1.2 (optionally 1.3) with the gematik
/// TR-03116-3 cipher suites, brainpool + NIST groups and signature schemes. Server trust is
/// decided only by the pinned SHA-256 fingerprint; client auth uses RSA or ECDSA keys from P12.
/// </summary>
public sealed class ConnectorTlsClient : DefaultTlsClient
{
    public static readonly int[] Tls12CipherSuites =
    [
        CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
        CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
        CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
        CipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
        CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256,
        CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA384,
        CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA256,
        CipherSuite.TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA384
    ];

    public static readonly int[] Tls13CipherSuites =
    [
        CipherSuite.TLS_AES_128_GCM_SHA256,
        CipherSuite.TLS_AES_256_GCM_SHA384
    ];

    public static readonly int[] Groups =
    [
        NamedGroup.brainpoolP256r1,       // 26 (TLS 1.2)
        NamedGroup.brainpoolP384r1,       // 27
        NamedGroup.brainpoolP512r1,       // 28
        NamedGroup.secp256r1,
        NamedGroup.secp384r1,
        NamedGroup.brainpoolP256r1tls13,  // 31 (TLS 1.3)
        NamedGroup.brainpoolP384r1tls13,
        NamedGroup.brainpoolP512r1tls13
    ];

    public static readonly int[] SignatureSchemes =
    [
        SignatureScheme.ecdsa_brainpoolP256r1tls13_sha256,
        SignatureScheme.ecdsa_brainpoolP384r1tls13_sha384,
        SignatureScheme.ecdsa_brainpoolP512r1tls13_sha512,
        SignatureScheme.ecdsa_secp256r1_sha256,   // = (sha256, ecdsa) in TLS 1.2
        SignatureScheme.ecdsa_secp384r1_sha384,   // = (sha384, ecdsa) in TLS 1.2
        SignatureScheme.rsa_pss_rsae_sha256,
        SignatureScheme.rsa_pss_rsae_sha384,
        SignatureScheme.rsa_pkcs1_sha256,
        SignatureScheme.rsa_pkcs1_sha384
    ];

    private readonly string? _pinnedFingerprint;
    private readonly ConnectorClientCredentials? _credentials;
    private readonly bool _allowTls13;
    private readonly string? _sniHost;

    public ConnectorServerCertificate? ServerCertificate { get; private set; }
    public PinEvaluation Pin { get; private set; } = PinEvaluation.NotPinned;

    public ConnectorTlsClient(string? pinnedFingerprint, ConnectorClientCredentials? credentials, bool allowTls13 = true, string? sniHost = null)
        : base(new BcTlsCrypto(new SecureRandom()))
    {
        _pinnedFingerprint = pinnedFingerprint;
        _credentials = credentials;
        _allowTls13 = allowTls13;
        _sniHost = sniHost;
    }

    protected override ProtocolVersion[] GetSupportedVersions() =>
        _allowTls13
            ? ProtocolVersion.TLSv13.DownTo(ProtocolVersion.TLSv12)
            : ProtocolVersion.TLSv12.Only();

    protected override int[] GetSupportedCipherSuites() =>
        TlsUtilities.GetSupportedCipherSuites(Crypto,
            _allowTls13 ? Tls13CipherSuites.Concat(Tls12CipherSuites).ToArray() : Tls12CipherSuites);

    protected override IList<int> GetSupportedGroups(IList<int> namedGroupRoles) =>
        Groups.Where(g => Crypto.HasNamedGroup(g)).ToList();

    protected override IList<SignatureAndHashAlgorithm> GetSupportedSignatureAlgorithms() =>
        SignatureSchemes.Select(SignatureScheme.GetSignatureAndHashAlgorithm).ToList();

    protected override IList<ServerName>? GetSniServerNames()
    {
        if (string.IsNullOrWhiteSpace(_sniHost) || System.Net.IPAddress.TryParse(_sniHost, out _))
            return null;
        return new List<ServerName> { new(NameType.host_name, System.Text.Encoding.ASCII.GetBytes(_sniHost)) };
    }

    public override TlsAuthentication GetAuthentication() => new Authentication(this);

    private sealed class Authentication : TlsAuthentication
    {
        private readonly ConnectorTlsClient _owner;

        public Authentication(ConnectorTlsClient owner) => _owner = owner;

        public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
        {
            var chain = serverCertificate.Certificate;
            if (chain is null || chain.IsEmpty)
                throw new TlsFatalAlert(AlertDescription.bad_certificate);

            var der = chain.GetCertificateAt(0).GetEncoded();
            var x509 = new Org.BouncyCastle.X509.X509CertificateParser().ReadCertificate(der);
            _owner.ServerCertificate = new ConnectorServerCertificate(
                CertificatePinning.Fingerprint(der),
                x509.SubjectDN.ToString(),
                x509.IssuerDN.ToString(),
                x509.NotBefore.ToLocalTime(),
                x509.NotAfter.ToLocalTime());
            _owner.Pin = CertificatePinning.Evaluate(_owner.ServerCertificate.Sha256Fingerprint, _owner._pinnedFingerprint);

            // Strict: only a matching pin proceeds. NotPinned stops before any application data is sent.
            if (_owner.Pin != PinEvaluation.Match)
                throw new TlsFatalAlert(AlertDescription.bad_certificate);
        }

        public TlsCredentials? GetClientCredentials(CertificateRequest certificateRequest)
        {
            var creds = _owner._credentials;
            if (creds is null)
                return null;

            var context = _owner.m_context;
            var tls13 = TlsUtilities.IsTlsV13(context.ServerVersion);
            var offered = certificateRequest.SupportedSignatureAlgorithms;
            var sigAlg = SelectSignatureAlgorithm(creds.KeyType, tls13, offered)
                         ?? throw new TlsFatalAlert(AlertDescription.handshake_failure);

            var crypto = (BcTlsCrypto)_owner.Crypto;
            var tlsCerts = creds.ChainDer.Select(d => (TlsCertificate)new BcTlsCertificate(crypto, d)).ToArray();
            var certificate = tls13
                ? new Certificate(certificateRequest.GetCertificateRequestContext(),
                    tlsCerts.Select(c => new CertificateEntry(c, null)).ToArray())
                : new Certificate(tlsCerts);

            return new BcDefaultTlsCredentialedSigner(new TlsCryptoParameters(context), crypto, creds.PrivateKey, certificate, sigAlg);
        }
    }

    /// <summary>Picks the client signature algorithm for the key type from what the server offered.</summary>
    public static SignatureAndHashAlgorithm? SelectSignatureAlgorithm(
        ConnectorKeyType keyType, bool tls13, IList<SignatureAndHashAlgorithm>? offered)
    {
        int[] preferred = (keyType, tls13) switch
        {
            (ConnectorKeyType.Rsa, true) => [SignatureScheme.rsa_pss_rsae_sha256, SignatureScheme.rsa_pss_rsae_sha384],
            (ConnectorKeyType.Rsa, false) => [SignatureScheme.rsa_pkcs1_sha256, SignatureScheme.rsa_pkcs1_sha384, SignatureScheme.rsa_pss_rsae_sha256, SignatureScheme.rsa_pss_rsae_sha384],
            (ConnectorKeyType.EcBrainpoolP256r1, true) => [SignatureScheme.ecdsa_brainpoolP256r1tls13_sha256],
            (ConnectorKeyType.EcBrainpoolP384r1, true) => [SignatureScheme.ecdsa_brainpoolP384r1tls13_sha384],
            (ConnectorKeyType.EcBrainpoolP512r1, true) => [SignatureScheme.ecdsa_brainpoolP512r1tls13_sha512],
            (ConnectorKeyType.EcSecp256r1, true) => [SignatureScheme.ecdsa_secp256r1_sha256],
            (ConnectorKeyType.EcSecp384r1, true) => [SignatureScheme.ecdsa_secp384r1_sha384],
            // TLS 1.2: ECDSA hash is independent of the curve; prefer the curve-matching size.
            (ConnectorKeyType.EcSecp384r1 or ConnectorKeyType.EcBrainpoolP384r1 or ConnectorKeyType.EcBrainpoolP512r1, false)
                => [SignatureScheme.ecdsa_secp384r1_sha384, SignatureScheme.ecdsa_secp256r1_sha256],
            _ => [SignatureScheme.ecdsa_secp256r1_sha256, SignatureScheme.ecdsa_secp384r1_sha384]
        };

        foreach (var scheme in preferred)
        {
            var alg = SignatureScheme.GetSignatureAndHashAlgorithm(scheme);
            if (offered is null || offered.Count == 0 || offered.Contains(alg))
                return alg;
        }

        return null;
    }

    /// <summary>Human readable message for BC TLS exceptions.</summary>
    public static ConnectorTlsException Translate(Exception ex, ConnectorTlsClient client)
    {
        if (client.Pin != PinEvaluation.Match && client.ServerCertificate is not null)
        {
            return new ConnectorTlsException(client.Pin == PinEvaluation.NotPinned
                    ? LocalizationService.Pick("Konnektor-Zertifikat ist noch nicht vertraut.", "Connector certificate is not trusted yet.")
                    : LocalizationService.Pick("Konnektor-Zertifikat passt nicht zum vertrauten Fingerprint.", "Connector certificate does not match the trusted fingerprint."),
                AlertDescription.bad_certificate, pinFailure: true, inner: ex);
        }

        return ex switch
        {
            TlsFatalAlertReceived received => new ConnectorTlsException(
                LocalizationService.Pick($"TLS-Handshake vom Konnektor abgelehnt: {AlertDescription.GetText(received.AlertDescription)}",
                    $"TLS handshake rejected by connector: {AlertDescription.GetText(received.AlertDescription)}")
                + Hint(received.AlertDescription), received.AlertDescription, inner: ex),
            TlsFatalAlert local => new ConnectorTlsException(
                LocalizationService.Pick($"TLS-Handshake fehlgeschlagen: {AlertDescription.GetText(local.AlertDescription)}",
                    $"TLS handshake failed: {AlertDescription.GetText(local.AlertDescription)}")
                + (string.IsNullOrWhiteSpace(local.Message) ? "" : $" ({local.Message})"), local.AlertDescription, inner: ex),
            _ => new ConnectorTlsException(LocalizationService.Pick("TLS-Fehler: ", "TLS error: ") + ex.Message, inner: ex)
        };
    }

    private static string Hint(short alert) => alert switch
    {
        AlertDescription.bad_certificate or AlertDescription.certificate_unknown or AlertDescription.unknown_ca or AlertDescription.certificate_required
            => LocalizationService.Pick(" – Clientzertifikat fehlt oder ist im Konnektor nicht hinterlegt.", " – client certificate missing or not registered in the connector."),
        AlertDescription.handshake_failure or AlertDescription.insufficient_security
            => LocalizationService.Pick(" – keine gemeinsame Cipher-Suite/Kurve.", " – no common cipher suite/curve."),
        _ => ""
    };
}

/// <summary>
/// TLS-Server für die CETP-Ereignissenke (gemSpec_Kon CETP1 / TIP1-A_4595):
/// der Konnektor ist TLS-Client, die App ist TLS-Server. Dasselbe P12 wie an der SOAP-Seite.
/// Präsentiert der Konnektor ein Clientzertifikat, wird es gegen den gespeicherten Fingerprint geprüft.
/// </summary>
public sealed class ConnectorTlsServer : DefaultTlsServer
{
    private readonly ConnectorClientCredentials _credentials;
    private readonly string? _pinnedKonnektorFingerprint;
    private readonly bool _requireClientCertificate;

    public ConnectorServerCertificate? PeerCertificate { get; private set; }
    public PinEvaluation PeerPin { get; private set; } = PinEvaluation.NotPinned;

    public ConnectorTlsServer(
        ConnectorClientCredentials credentials,
        string? pinnedKonnektorFingerprint,
        bool requireClientCertificate = false)
        : base(new BcTlsCrypto(new SecureRandom()))
    {
        _credentials = credentials;
        _pinnedKonnektorFingerprint = pinnedKonnektorFingerprint;
        _requireClientCertificate = requireClientCertificate;
    }

    protected override ProtocolVersion[] GetSupportedVersions() =>
        ProtocolVersion.TLSv13.DownTo(ProtocolVersion.TLSv12);

    protected override int[] GetSupportedCipherSuites() =>
        TlsUtilities.GetSupportedCipherSuites(Crypto, CipherSuitesFor(_credentials.KeyType));

    /// <summary>
    /// TLS 1.2 bindet die Suite an den Schluesseltyp des Serverzertifikats: Mit einem EC-Schluessel
    /// (RISE: brainpoolP256r1) duerfen nur ECDHE_ECDSA-Suites ausgewaehlt werden, mit RSA nur
    /// ECDHE_RSA. Wird beides angeboten und bevorzugt der Konnektor die "andere" Familie, scheitert
    /// der Handshake (illegal_parameter) – und kein Ereignis kommt an. TLS 1.3 ist unabhaengig davon.
    /// </summary>
    public static int[] CipherSuitesFor(ConnectorKeyType keyType)
    {
        var rsa = keyType == ConnectorKeyType.Rsa;
        var tls12 = ConnectorTlsClient.Tls12CipherSuites.Where(cs => rsa
            ? TlsUtilities.GetKeyExchangeAlgorithm(cs) == KeyExchangeAlgorithm.ECDHE_RSA
            : TlsUtilities.GetKeyExchangeAlgorithm(cs) == KeyExchangeAlgorithm.ECDHE_ECDSA);
        return ConnectorTlsClient.Tls13CipherSuites.Concat(tls12).ToArray();
    }

    public override int[] GetSupportedGroups() =>
        ConnectorTlsClient.Groups.Where(g => Crypto.HasNamedGroup(g)).ToArray();

    public override TlsCredentials GetCredentials()
    {
        var tls13 = TlsUtilities.IsTlsV13(m_context.ServerVersion);
        var sigAlg = ConnectorTlsClient.SelectSignatureAlgorithm(_credentials.KeyType, tls13, m_context.SecurityParameters.ClientSigAlgs)
                     ?? throw new TlsFatalAlert(AlertDescription.handshake_failure);
        var crypto = (BcTlsCrypto)Crypto;
        var tlsCerts = _credentials.ChainDer.Select(d => (TlsCertificate)new BcTlsCertificate(crypto, d)).ToArray();
        var certificate = tls13
            ? new Certificate(TlsUtilities.EmptyBytes, tlsCerts.Select(c => new CertificateEntry(c, null)).ToArray())
            : new Certificate(tlsCerts);
        return new BcDefaultTlsCredentialedSigner(new TlsCryptoParameters(m_context), crypto, _credentials.PrivateKey, certificate, sigAlg);
    }

    public override CertificateRequest GetCertificateRequest()
    {
        var algs = ConnectorTlsClient.SignatureSchemes.Select(SignatureScheme.GetSignatureAndHashAlgorithm).ToList();
        return TlsUtilities.IsTlsV13(m_context.ServerVersion)
            ? new CertificateRequest(TlsUtilities.EmptyBytes, algs, null, null)
            : new CertificateRequest([ClientCertificateType.ecdsa_sign, ClientCertificateType.rsa_sign], algs, null);
    }

    public override void NotifyClientCertificate(Certificate clientCertificate)
    {
        if (clientCertificate is null || clientCertificate.IsEmpty)
        {
            if (_requireClientCertificate)
                throw new TlsFatalAlert(AlertDescription.certificate_required);
            PeerPin = PinEvaluation.NotPinned;
            return;
        }

        var der = clientCertificate.GetCertificateAt(0).GetEncoded();
        var x509 = new Org.BouncyCastle.X509.X509CertificateParser().ReadCertificate(der);
        PeerCertificate = new ConnectorServerCertificate(
            CertificatePinning.Fingerprint(der),
            x509.SubjectDN.ToString(),
            x509.IssuerDN.ToString(),
            x509.NotBefore.ToLocalTime(),
            x509.NotAfter.ToLocalTime());
        PeerPin = CertificatePinning.Evaluate(PeerCertificate.Sha256Fingerprint, _pinnedKonnektorFingerprint);

        // Ist ein Pin hinterlegt, muss das Konnektor-Zertifikat passen. Ohne Pin (erster Start)
        // lassen wir die Zustellung zu – der SOAP-Pfad fragt das Vertrauen separat.
        if (!string.IsNullOrWhiteSpace(_pinnedKonnektorFingerprint) && PeerPin != PinEvaluation.Match)
            throw new TlsFatalAlert(AlertDescription.bad_certificate);
    }
}
