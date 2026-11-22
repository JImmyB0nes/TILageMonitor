using System.Security.Cryptography;

namespace TILageMonitor;

public enum PinEvaluation
{
    /// <summary>Noch kein Fingerprint gespeichert – der Benutzer muss zuerst vertrauen.</summary>
    NotPinned,
    Match,
    /// <summary>Anderes Zertifikat als das vertraute – möglicher MITM oder Konnektor-Neuausstellung.</summary>
    Mismatch
}

/// <summary>SHA-256-Fingerprint des Konnektor-Zertifikats (Leaf, DER), das die App gesehen hat.</summary>
public sealed record ConnectorServerCertificate(
    string Sha256Fingerprint,
    string Subject,
    string Issuer,
    DateTime NotBefore,
    DateTime NotAfter);

/// <summary>
/// Trust-on-first-use für das Konnektor-TLS-Zertifikat (SHA-256 über das Leaf-DER).
/// Die TI-PKI ist Windows fremd; der Pin ersetzt die Kettenprüfung – niemals „jedes Zertifikat akzeptieren“.
/// </summary>
public static class CertificatePinning
{
    /// <summary>64 hex uppercase without separators – matches historically stored pins (ToHexString).</summary>
    public static string Fingerprint(byte[] derCertificate)
    {
        ArgumentNullException.ThrowIfNull(derCertificate);
        return Convert.ToHexString(SHA256.HashData(derCertificate));
    }

    /// <summary>Normalisiert „aa:bb cc-DD“ und ungetrenntes Hex zu 64 Zeichen A–F.</summary>
    public static string Normalize(string? fingerprint) =>
        new((fingerprint ?? "").Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());

    public static string FormatDisplay(string? fingerprint)
    {
        var hex = Normalize(fingerprint);
        if (hex.Length != 64)
            return fingerprint ?? "";
        return string.Join(":", Enumerable.Range(0, 32).Select(i => hex.Substring(i * 2, 2)));
    }

    public static PinEvaluation Evaluate(string? actualFingerprint, string? pinnedFingerprint)
    {
        var pinned = Normalize(pinnedFingerprint);
        if (pinned.Length == 0)
            return PinEvaluation.NotPinned;
        var actual = Normalize(actualFingerprint);
        if (actual.Length != 64 || pinned.Length != 64)
            return PinEvaluation.Mismatch;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(actual),
            System.Text.Encoding.ASCII.GetBytes(pinned))
            ? PinEvaluation.Match
            : PinEvaluation.Mismatch;
    }
}
