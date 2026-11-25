using System.Security.Cryptography;
using System.Text;

namespace TILageMonitor;

/// <summary>Ein Eintrag im Ablaufkalender.</summary>
public sealed record ConnectorCalendarEntry(string Title, string Owner, DateTime NotAfter);

/// <summary>
/// Baut eine iCalendar-Datei (RFC 5545) mit den Zertifikatsabläufen. Je Zertifikat ein
/// ganztägiger Termin am Ablauftag mit Erinnerungen 90, 30 und 7 Tage vorher – dieselben
/// Stufen wie die Warnungen der App. Die UID ist stabil (aus Inhaber, Art und Ablaufdatum),
/// damit ein erneuter Import in Outlook vorhandene Termine aktualisiert statt verdoppelt.
/// </summary>
public static class ConnectorCalendarExport
{
    public static string Build(IEnumerable<ConnectorCalendarEntry> entries, DateTime nowUtc)
    {
        var sb = new StringBuilder();
        void Line(string text) => AppendFolded(sb, text);

        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//TI-Lage Monitor//Konnektor-Zertifikate//DE");
        Line("CALSCALE:GREGORIAN");
        Line("METHOD:PUBLISH");
        Line("X-WR-CALNAME:" + Escape(L("TI-Zertifikate", "TI certificates")));

        var stamp = nowUtc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.OrderBy(e => e.NotAfter))
        {
            var day = entry.NotAfter.Date;
            var uid = Uid(entry);
            if (!seen.Add(uid))
                continue;

            var summary = string.IsNullOrWhiteSpace(entry.Owner)
                ? L($"Zertifikat läuft ab: {entry.Title}", $"Certificate expires: {entry.Title}")
                : L($"Zertifikat läuft ab: {entry.Title} ({entry.Owner})", $"Certificate expires: {entry.Title} ({entry.Owner})");
            var description = L(
                $"{entry.Title} von {entry.Owner} ist gültig bis {entry.NotAfter:dd.MM.yyyy HH:mm}. " +
                "Rechtzeitig eine Folgekarte bzw. ein neues Zertifikat beim Kartenherausgeber beantragen.\n\n" +
                "Erzeugt vom TI-Lage Monitor.",
                $"{entry.Title} of {entry.Owner} is valid until {entry.NotAfter:yyyy-MM-dd HH:mm}. " +
                "Order a follow-up card or certificate from the card issuer in time.\n\n" +
                "Created by TI-Lage Monitor.");

            Line("BEGIN:VEVENT");
            Line("UID:" + uid);
            Line("DTSTAMP:" + stamp);
            Line("DTSTART;VALUE=DATE:" + day.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
            Line("DTEND;VALUE=DATE:" + day.AddDays(1).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
            Line("SUMMARY:" + Escape(summary));
            Line("DESCRIPTION:" + Escape(description));
            Line("CATEGORIES:" + Escape(L("TI,Zertifikat", "TI,Certificate")));
            Line("TRANSP:TRANSPARENT");
            foreach (var days in ConnectorCertificateExpiry.Thresholds)
            {
                Line("BEGIN:VALARM");
                Line("ACTION:DISPLAY");
                Line("DESCRIPTION:" + Escape(L($"{summary} – noch {days} Tage", $"{summary} – {days} days left")));
                Line($"TRIGGER:-P{days}D");
                Line("END:VALARM");
            }
            Line("END:VEVENT");
        }

        Line("END:VCALENDAR");
        return sb.ToString();
    }

    /// <summary>Stabile UID: gleicher Inhaber, gleiche Art, gleiches Ablaufdatum = derselbe Termin.</summary>
    internal static string Uid(ConnectorCalendarEntry entry)
    {
        var key = $"{entry.Owner.Trim()}|{entry.Title.Trim()}|{entry.NotAfter.Date:yyyy-MM-dd}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
        return hash + "@tilagemonitor";
    }

    /// <summary>TEXT-Escaping nach RFC 5545 §3.3.11.</summary>
    internal static string Escape(string value) =>
        value.Replace("\\", "\\\\")
             .Replace(";", "\\;")
             .Replace(",", "\\,")
             .Replace("\r\n", "\\n")
             .Replace("\n", "\\n")
             .Replace("\r", "\\n");

    /// <summary>Zeilen über 75 Oktette falten (RFC 5545 §3.1), ohne UTF-8-Zeichen zu zerteilen.</summary>
    internal static void AppendFolded(StringBuilder sb, string line)
    {
        var octets = 0;
        var first = true;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            var limit = first ? 75 : 74; // Folgezeilen beginnen mit einem Leerzeichen
            if (octets + size > limit)
            {
                sb.Append("\r\n ");
                octets = 0;
                first = false;
            }
            sb.Append(rune.ToString());
            octets += size;
        }
        sb.Append("\r\n");
    }

    private static string L(string de, string en) => LocalizationService.Pick(de, en);
}
