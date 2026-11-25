using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace TILageMonitor;

/// <summary>
/// Centralized UI localization. German is the default; every other system language
/// uses the English fallback so the application is never shown half-translated.
/// </summary>
public static class LocalizationService
{
    private static string _language = "system";

    public static bool IsGerman => _language switch
    {
        "de" => true,
        "en" => false,
        _ => IsGermanCulture(CultureInfo.CurrentUICulture)
    };

    public static CultureInfo DisplayCulture => _language switch
    {
        "de" => CultureInfo.GetCultureInfo("de-DE"),
        "en" => CultureInfo.GetCultureInfo("en-US"),
        _ => CultureInfo.CurrentUICulture
    };

    public static void Configure(string? language)
    {
        _language = language?.Trim().ToLowerInvariant() switch
        {
            "de" => "de",
            "en" => "en",
            _ => "system"
        };
    }

    /// <summary>Inline-Übersetzung für dynamische Texte (z. B. Konnektor-Meldungen mit Werten).</summary>
    public static string Pick(string german, string english) => IsGerman ? german : english;

    public static bool IsGermanCulture(CultureInfo? culture) =>
        string.Equals((culture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName, "de", StringComparison.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Über TI-Lage Monitor"] = "About TI-Lage Monitor",
        ["Über TI-Lage Monitor…"] = "About TI-Lage Monitor…",
        ["TI-Lage anzeigen"] = "Show TI status",
        ["Jetzt aktualisieren"] = "Refresh now",
        ["↻  Aktualisieren"] = "↻  Refresh",
        ["📈  Verlauf"] = "📈  History",
        ["TI-Status · 14 Tage"] = "TI Status · 14 Days",
        ["TI-Status · 14 Tage…"] = "TI Status · 14 Days…",
        ["⚙  Einstellungen"] = "⚙  Settings",
        ["Einstellungen…"] = "Settings…",
        ["Einstellungen öffnen"] = "Open settings",
        ["Einstellungen"] = "Settings",
        ["Überwachung der Telematikinfrastruktur und zentraler TI-Dienste."] = "Monitoring the German telematics infrastructure and central TI services.",
        ["Wird geladen …"] = "Loading …",
        ["Status wird ermittelt…"] = "Determining status…",
        ["Verbindung"] = "Connection",
        ["● Prüfe API …"] = "● Checking API …",
        ["Dienste"] = "Services",
        ["Aktuelle Meldungen"] = "Current messages",
        ["Keine aktuellen Meldungen"] = "No current messages",
        ["Aktuell liegen keine Störungen, Ursachen oder Einschränkungen vor. Der Status aktualisiert sich automatisch."] = "There are currently no outages, causes or restrictions. Status updates automatically.",
        ["Update installieren"] = "Install update",
        ["Setup herunterladen und starten"] = "Download and start setup",
        ["gematik TI-Status öffnen"] = "Open gematik TI status",
        ["Beenden"] = "Exit",
        ["Autostart: aus"] = "Autostart: off",
        ["Autostart: an"] = "Autostart: on",
        ["Benachrichtigungen: aus"] = "Notifications: off",
        ["Benachrichtigungen: an"] = "Notifications: on",
        ["Darstellung, Startverhalten, Updates und Benachrichtigungen"] = "Appearance, startup behavior, updates and notifications",
        ["Sprache"] = "Language",
        ["Sprache der Oberfläche auswählen"] = "Choose the interface language",
        ["Automatisch (Windows-Systemsprache)"] = "Automatic (Windows display language)",
        ["Deutsch"] = "German",
        ["Die Auswahl wird nach einem Neustart der App übernommen."] = "Your selection will take effect after restarting the app.",
        ["Die Auswahl wurde gespeichert und wird nach einem Neustart der App übernommen."] = "Your selection was saved and will take effect after restarting the app.",
        ["Jetzt neu starten"] = "Restart now",
        ["Die App konnte nicht neu gestartet werden. Bitte schließen und öffnen Sie sie manuell."] = "The app could not be restarted. Please close and reopen it manually.",
        ["Neustart nicht möglich"] = "Restart unavailable",
        ["Darstellung"] = "Appearance",
        ["Oberfläche der Anwendung"] = "Application appearance",
        ["Dunkelmodus"] = "Dark mode",
        ["Start"] = "Startup",
        ["Beim Anmelden automatisch starten (mit --tray)"] = "Start automatically when signing in (with --tray)",
        ["Autostart"] = "Autostart",
        ["Updates"] = "Updates",
        ["Beim Start wird automatisch nach einer neuen Version gesucht."] = "A new version is checked automatically at startup.",
        ["Updates automatisch installieren"] = "Install updates automatically",
        ["Neue Releases werden heruntergeladen, per SHA-256 geprüft und nach dem Beenden der App ohne Assistent installiert."] = "New releases are downloaded, verified with SHA-256 and installed after the app closes, without a wizard.",
        ["Aktuelle Version wird beim Start automatisch geprüft."] = "The current version is checked automatically at startup.",
        ["Nach Updates suchen"] = "Check for updates",
        ["Benachrichtigungen"] = "Notifications",
        ["Toast-Meldungen pro Dienst (Tray-Icon-Farbe bleibt unverändert)"] = "Toast notifications per service (tray icon color remains unchanged)",
        ["Schließen"] = "Close",
        ["Funktionen"] = "Features",
        ["• Ampel-Status im Infobereich (Systemtray)"] = "• Traffic-light status in the notification area (system tray)",
        ["• Windows-Toasts bei Störungen und Statuswechseln"] = "• Windows toasts for outages and status changes",
        ["• 14-Tage-Verlauf mit Tages-Kacheln und Ereignisse-Timeline"] = "• 14-day history with daily tiles and event timeline",
        ["• Offline-Cache bei API-Ausfall"] = "• Offline cache when the API is unavailable",
        ["• Autostart, Dunkelmodus, Benachrichtigungsfilter"] = "• Autostart, dark mode and notification filters",
        ["Kontakt / Support"] = "Contact / Support",
        ["Fragen oder Feedback zur Community-App?"] = "Questions or feedback about this community app?",
        ["Homepage öffnen"] = "Open homepage",
        ["GitHub-Projekt öffnen"] = "Open GitHub project",
        ["E-Mail schreiben"] = "Write email",
        ["Hinweis"] = "Notice",
        ["Kein offizielles Produkt der gematik GmbH. Datenquelle ist die öffentliche TI-Lage-API. Angaben ohne Gewähr."] = "Not an official product of gematik GmbH. Data comes from the public TI status API. No guarantee.",
        ["gematik Fachportal · TI-Status öffnen"] = "Open gematik portal · TI status",
        ["Tageskacheln in der Übersicht, Stunden-Zoom und Ereignisse als eigene Ansichten."] = "Daily tiles in the overview, hourly zoom and events as separate views.",
        ["Heute"] = "Today",
        ["Alles OK"] = "All OK",
        ["Schlechtester Status heute"] = "Worst status today",
        ["14 Tage"] = "14 days",
        ["0 auffällige Tage"] = "0 affected days",
        ["Tage mit Einschränkung, Störung oder Wartung"] = "Days with restrictions, outages or maintenance",
        ["Verfügbarkeit"] = "Availability",
        ["Lokal erfasste / API-gefüllte Stunden (ohne Zukunft)"] = "Locally recorded / API-filled hours (excluding the future)",
        ["Übersicht"] = "Overview",
        ["Stunden"] = "Hours",
        ["Ereignisse"] = "Events",
        ["Alle Dienste"] = "All services",
        ["Alle Dienste nebeneinander anzeigen"] = "Show all services side by side",
        ["Stabil · Alles ruhig"] = "Stable · All clear",
        ["In den letzten 14 Tagen gab es für diesen Fokus keine Einschränkungen oder Störungen."] = "There were no restrictions or outages for this focus in the last 14 days.",
        ["Tagesübersicht"] = "Daily overview",
        ["Große Tageskacheln — Farbe = schlechtester Status. Auswahl und Klick öffnen Stunden."] = "Large daily tiles — color shows the worst status. Select and click to open hours.",
        ["Noch keine Verlaufsdaten"] = "No history data yet",
        ["Der 14-Tage-Verlauf kombiniert gematik-API-Daten mit lokal erfassten Stunden. Sobald die App aktualisiert, füllen sich die Kacheln ruhig von selbst."] = "The 14-day history combines gematik API data with locally recorded hours. Tiles fill automatically as the app updates.",
        ["Bitte wählen Sie in der Übersicht einen Tag — oder es wird automatisch heute genutzt."] = "Please select a day in the overview — or today will be selected automatically.",
        ["← Zurück zur Übersicht"] = "← Back to overview",
        ["Stundenansicht (0–23)"] = "Hourly view (0–23)",
        ["Klick auf eine auffällige Stunde öffnet das passende Ereignis. Esc oder Zurück kehrt zur Übersicht."] = "Click an affected hour to open the matching event. Esc or Back returns to the overview.",
        ["Stunde"] = "Hour",
        ["Vollständige Timeline. Klick auf eine Karte öffnet die Stundenansicht für den Starttag."] = "Complete timeline. Click a card to open the hourly view for its start day.",
        ["Alles ruhig"] = "All clear",
        ["Im gewählten Fokus gibt es im 14-Tage-Fenster keine gemeldeten Ereignisse."] = "There are no reported events for the selected focus in the 14-day window.",
        ["Legende"] = "Legend",
        ["OK"] = "OK",
        ["Einschränkung"] = "Restriction",
        ["Wartung"] = "Maintenance",
        ["Störung"] = "Outage",
        ["keine Daten"] = "no data",
        ["kein Datenpunkt"] = "no data point",
        ["TI-Status: wird geladen…"] = "TI status: loading…",
        ["Die Update-Prüfung ist fehlgeschlagen."] = "The update check failed.",
        ["Teilausfall"] = "Partial outage",
        ["Verfügbar"] = "Available",
        ["Keine Daten"] = "No data",
        ["auffällige Tage"] = "affected days",
        ["physische Stunden mit Daten"] = "physical hours with data",
        ["Klick öffnet die Stundenansicht."] = "Click to open the hourly view.",
        ["wieder verfügbar"] = "available again",
        ["API nicht erreichbar"] = "API unavailable",
        ["Die gematik API antwortet nicht. Bitte später erneut versuchen."] = "The gematik API is not responding. Please try again later.",
        ["● Keine Verbindung"] = "● No connection",
        ["API nicht erreichbar · letzter Stand wird angezeigt."] = "API unavailable · showing the last known status.",
        ["TI-Status: API down"] = "TI status: API down",
        ["Die gematik API ist wiederholt nicht erreichbar."] = "The gematik API is repeatedly unavailable.",
        ["Wartung / Einschränkung"] = "Maintenance / restriction",
        ["Aktuelle Einschränkung."] = "Current restriction.",
        ["TI-Status: Änderung"] = "TI status: change",
        ["Automatisch erkannte Einschränkung · "] = "Automatically detected restriction · ",
        ["Mindestens ein TI-Dienst meldet einen Komplettausfall."] = "At least one TI service reports a full outage.",
        ["Mindestens ein TI-Dienst ist teilweise beeinträchtigt."] = "At least one TI service is partially affected.",
        ["Es läuft eine Wartung oder Einschränkung an einem TI-Dienst."] = "Maintenance or a restriction is in progress for a TI service.",
        ["TI-Status: Teilausfall"] = "TI status: partial outage",
        ["TI-Status: Beeinträchtigung"] = "TI status: degraded",
        ["Alle Dienste sind erreichbar und arbeiten einwandfrei."] = "All services are available and working correctly.",
        ["TI-Status: Alles wieder OK"] = "TI status: all clear again",
        ["Alle überwachten Dienste wieder normal."] = "All monitored services are normal again.",
        ["TI-Status: Wiederhergestellt"] = "TI status: restored",
        ["Wartung gemeldet"] = "Maintenance reported",
        ["Dienst wieder verfügbar"] = "Service available again",
        ["Updates prüfen"] = "Check updates",
        ["Der Installer wird gerade noch erstellt. Bitte in wenigen Minuten erneut prüfen."] = "The installer is still being created. Please check again in a few minutes.",
        ["Update wird vorbereitet"] = "Preparing update",
        ["Automatisches Update fehlgeschlagen"] = "Automatic update failed",
        ["Nächster Versuch frühestens in 6 Stunden."] = "The next attempt will be made in 6 hours at the earliest.",
        ["Update verfügbar"] = "Update available",
        ["Das automatische Update konnte nicht abgeschlossen werden. Die bisherige Version wurde wieder gestartet. Details stehen in %AppData%\\TILageMonitor\\update-install.log."] = "The automatic update could not be completed. The previous version was restarted. Details are in %AppData%\\TILageMonitor\\update-install.log.",
        ["Update fehlgeschlagen"] = "Update failed",
        ["Der Installer wird noch erstellt."] = "The installer is still being created.",
        ["wird vorbereitet"] = "is being prepared",
        ["Lade Update …"] = "Downloading update …",
        ["Erneut versuchen"] = "Try again",
        ["Keine Daten für diesen Dienst"] = "No data for this service",
        ["Für den gewählten Dienst gibt es in diesem Zeitraum keine Einträge. „Alle Dienste“ wählen oder einen anderen Chip tippen."] = "There are no entries for the selected service in this period. Select “All services” or choose another chip.",
        ["alle Dienste"] = "all services",
        ["In den letzten 14 Tagen blieb"] = "In the last 14 days",
        ["ohne Einschränkung oder Störung — ein ruhiges Bild."] = "without any restriction or outage — a quiet picture.",
        ["In den letzten 14 Tagen waren alle bekannten Tage über die Dienste hinweg ohne Einschränkung oder Störung."] = "Across all services, every known day in the last 14 days had no restriction or outage.",
        ["Schlechtester Status heute (alle Dienste)"] = "Worst status today (all services)",
        ["1 auffälliger Tag"] = "1 affected day",
        ["Noch keine bekannten Stunden für die Berechnung"] = "No known hours for the calculation yet",
        ["OK-Stunden:"] = "OK hours:",
        ["von"] = "of",
        ["inkl. API-gefüllte (ohne Zukunft)"] = "including API-filled hours (excluding the future)",
        ["nur lokal erfasste Stunden"] = "locally recorded hours only",
        ["Stundenansicht (0–23) — Klick auf auffällige Stunden öffnet das Ereignis; Esc zurück zur Übersicht."] = "Hourly view (0–23) — click an affected hour to open its event; Esc returns to the overview.",
        ["Ereignisse als Timeline — Esc kehrt zur Übersicht; Klick öffnet die Stundenansicht für den Starttag."] = "Events as a timeline — Esc returns to the overview; click to open the hourly view for the start day.",
        ["Fokus:"] = "Focus:",
        ["große Tageskacheln. Tag öffnet Stunden; Ereignisse über den Segment-Umschalter."] = "large daily tiles. A day opens its hours; events are available via the segment switcher.",
        ["Alle Dienste in kompakten Zeilen. Ein Dienst-Chip fokussiert die Tageskacheln; Ereignisse separat."] = "All services in compact rows. A service chip focuses the daily tiles; events are shown separately.",
        ["Kein Tag gewählt"] = "No day selected",
        ["kein Ereignis für diese Stunde"] = "no event for this hour",
        ["Einschr."] = "Restr.",
        ["Vollständige Störung im TI-Status — Stundenansicht für Details."] = "Full TI status outage — open the hourly view for details.",
        ["Einschränkung / Teilausfall — Zeitraum lokal dargestellt."] = "Restriction / partial outage — period shown locally.",
        ["Geplante oder laufende Wartung im erfassten Fenster."] = "Scheduled or ongoing maintenance in the captured window.",
        ["Ereignis aus dem TI-Status (lokal dargestellt)."] = "Event from TI status (shown locally).",
        ["Stundenansicht öffnen"] = "Open hourly view",
        ["Die Versionsnummer des Releases konnte nicht gelesen werden."] = "The release version could not be read.",
        ["Update-Prüfung fehlgeschlagen:"] = "Update check failed:",
        ["Download oder Start fehlgeschlagen:"] = "Download or launch failed:",
        ["Authenticode-Prüfung fehlgeschlagen:"] = "Authenticode check failed:",
        ["Starte Setup …"] = "Starting setup …",
        ["Setup gestartet"] = "Setup started",
        ["Keine Download-URL vorhanden."] = "No download URL is available.",
        ["Die Update-URL verweist nicht auf eine Setup.exe. Bitte die Release-Seite manuell öffnen."] = "The update URL does not point to a setup executable. Please open the release page manually.",
        ["Lade Setup herunter …"] = "Downloading setup …",
        ["Der Installer ist ungewöhnlich groß und wurde aus Sicherheitsgründen abgebrochen."] = "The installer is unusually large and was cancelled for security reasons.",
        ["Der Installer überschreitet das Größenlimit von 500 MB."] = "The installer exceeds the 500 MB size limit.",
        ["Prüfe Setup-Datei …"] = "Checking setup file …",
        ["Installiere Update …"] = "Installing update …",
        ["Das Update wird nach dem Beenden der App automatisch installiert und anschließend gestartet."] = "The update will be installed automatically after the app closes, then started again.",
        ["Setup wurde gestartet."] = "Setup was started.",
        ["Der Pfad der laufenden Anwendung konnte nicht bestimmt werden."] = "The path of the running application could not be determined.",
        ["Der Update-Starter konnte nicht gestartet werden."] = "The update launcher could not be started.",
        ["Die Prüfsumme des Updates fehlt."] = "The update checksum is missing.",
        ["Für den Installer wurde keine passende Prüfsumme gefunden."] = "No matching checksum was found for the installer.",
        ["Die Prüfsumme des Updates stimmt nicht überein."] = "The update checksum does not match.",
        ["Prüfsumme konnte nicht geprüft werden:"] = "Checksum could not be checked:",
        ["Datenstand gematik:"] = "gematik data as of:",
        ["● Offline · letzter Stand"] = "● Offline · last known status",
        ["Offline · Cache vom"] = "Offline · cache from",
        ["Offline · Cache"] = "Offline · cache",
        ["● API erreichbar"] = "● API available",
        ["Letzte Abfrage:"] = "Last checked:",
        ["Ursache ·"] = "Cause ·",
        ["Komplettausfall"] = "Full outage",
        ["Jetzt den Setup-Installer herunterladen und starten?"] = "Download and start the setup installer now?",
        ["Im Footer kannst du es installieren."] = "You can install it from the footer.",
        ["API wieder erreichbar"] = "API reachable again",
        ["API nicht erreichbar."] = "API unavailable.",
        ["TI-Status: Störung"] = "TI status: outage",
        ["TI-Status: OK"] = "TI status: OK",
        ["Prüfe GitHub-Release …"] = "Checking GitHub release …",
        ["gematik meldet Ausfall"] = "gematik reports an outage",
        ["gematik meldet Teilausfall"] = "gematik reports a partial outage",
        ["Dienste geändert"] = "services changed",
        ["neue Meldungen"] = "new messages",
        ["TI-Komponente"] = "TI component",
        ["Die Prüfsummen-URL ist nicht erlaubt."] = "The checksum URL is not allowed.",
        ["Update-Prüfung fehlgeschlagen."] = "Update check failed.",
        ["Zeitüberschreitung bei der Update-Prüfung. Bitte Internetverbindung oder Proxy prüfen und später erneut versuchen."] = "The update check timed out. Please check your internet connection or proxy and try again later.",
        ["Zeitüberschreitung beim Herunterladen des Updates. Bitte Internetverbindung oder Proxy prüfen und später erneut versuchen."] = "Downloading the update timed out. Please check your internet connection or proxy and try again later.",
        ["Unerwarteter Fehler:"] = "Unexpected error:",
        ["Ein unerwarteter Fehler ist aufgetreten. Die App läuft weiter."] = "An unexpected error occurred. The app keeps running.",
        ["Fehler"] = "Error",
        ["Exportieren"] = "Export",
        ["Störungsbericht als CSV exportieren"] = "Export incident report as CSV",
        ["Störungsbericht exportieren"] = "Export incident report",
        ["Störungsbericht gespeichert:"] = "Incident report saved:",
        ["Export fehlgeschlagen:"] = "Export failed:",
        ["Keine Ereignisse im angezeigten Zeitraum."] = "No events in the displayed range.",
        ["Benachrichtigungen aktiviert"] = "Notifications enabled",
        ["Pro Dienst"] = "Per service",
        ["Autor: Randy Carter / R.C.  ·  © 2026"] = "Author: Randy Carter / R.C.  ·  © 2026",
        ["Update"] = "Update",
        ["Version"] = "Version",
        ["ist verfügbar. Aktuell installiert:"] = "is available. Currently installed:",
        ["wurde gefunden. Der Installer wird noch erstellt."] = "was found. The installer is still being created.",
        ["Kompakte Zeilen je Dienst. Ein Dienst-Chip oben fokussiert die Tageskacheln; Ereignisse über den Segment-Umschalter."] = "Compact rows per service. A service chip above focuses the daily tiles; events are available via the segment switcher.",
        ["14 Tage API-Verlauf · {0} / {1} Stunden zusätzlich lokal erfasst"] = "14-day API history · {0} / {1} hours recorded locally",
        ["Stundenkacheln mit Daten"] = "hour tiles with data",
        ["In den letzten 14 Tagen blieb {0} ohne Einschränkung oder Störung — ein ruhiges Bild."] = "Over the last 14 days, {0} had no restriction or outage — a quiet picture.",
        ["Störung ·"] = "Outage ·",
        ["Konnektor…"] = "Connector…",
        ["Konnektor"] = "Connector",
        ["🔌  Konnektor"] = "🔌  Connector",
        ["Konnektor einrichten und auslesen"] = "Set up and read connector",
        ["Lokale TI-Anbindung: Konnektor-Status, Kartenterminals, Karten, PIN-Verwaltung und Zertifikate"] = "Local TI connection: connector status, card terminals, cards, PIN management and certificates",
        ["📋  Bericht kopieren"] = "📋  Copy report",
        ["Diagnosebericht in die Zwischenablage kopieren"] = "Copy diagnostic report to clipboard",
        ["Noch nicht verbunden"] = "Not connected yet",
        ["Zugangsdaten eintragen und „Speichern & verbinden“ wählen."] = "Enter the credentials and choose “Save & connect”.",
        ["Hinweise"] = "Notices",
        ["Werte aus der Konnektor-Administration (Infomodell: Mandant, Clientsystem, Arbeitsplatz). Das Client-Zertifikat (.p12) wird im Konnektor für dieses Clientsystem erzeugt bzw. hinterlegt."] = "Values from the connector administration (info model: tenant, client system, workplace). The client certificate (.p12) is created or registered in the connector for this client system.",
        ["Konnektor-Adresse"] = "Connector address",
        ["IP-Adresse oder Hostname, optional mit Port, z. B. 192.168.1.10 oder 10.0.0.5:8443"] = "IP address or host name, optionally with port, e.g. 192.168.1.10 or 10.0.0.5:8443",
        ["TLS (https)"] = "TLS (https)",
        ["Client-Zertifikat (.p12)"] = "Client certificate (.p12)",
        ["Durchsuchen…"] = "Browse…",
        ["Zertifikats-Passwort"] = "Certificate password",
        ["Leer lassen, um das gespeicherte Passwort zu behalten"] = "Leave empty to keep the saved password",
        ["Gespeichertes Passwort entfernen"] = "Remove saved password",
        ["Mandant-ID"] = "Tenant ID (MandantId)",
        ["Clientsystem-ID"] = "Client system ID",
        ["Arbeitsplatz-ID"] = "Workplace ID",
        ["Benutzer-ID (optional)"] = "User ID (optional)",
        ["Nur nötig für HBA-Operationen, wenn der Konnektor eine UserId verlangt"] = "Only needed for HBA operations if the connector requires a UserId",
        ["Erweitert"] = "Advanced",
        ["Basic-Auth Benutzer"] = "Basic auth user",
        ["Basic-Auth Passwort"] = "Basic auth password",
        ["Nur ausfüllen, wenn der Konnektor Benutzer/Passwort statt (oder zusätzlich zum) Zertifikat verlangt"] = "Only fill in if the connector requires user/password instead of (or in addition to) the certificate",
        ["Dienst-Endpunkte auf diese Adresse umleiten (empfohlen)"] = "Redirect service endpoints to this address (recommended)",
        ["connector.sds enthält oft interne Hostnamen, die vom Arbeitsplatz nicht auflösbar sind"] = "connector.sds often contains internal host names that cannot be resolved from the workstation",
        ["Konnektor-TLS-Zertifikat"] = "Connector TLS certificate",
        ["nicht gepinnt"] = "not pinned",
        ["Vertrauen zurücksetzen"] = "Reset trust",
        ["Konnektor-Anbindung aktiv"] = "Connector integration enabled",

        ["Darstellung, Startverhalten, Updates, Benachrichtigungen und Konnektor"] = "Appearance, startup behavior, updates, notifications and connector",
        ["Lokale TI-Anbindung: Überwachung, Ereignisse und Verlauf. Zugangsdaten bleiben im Konnektor-Fenster."] = "Local TI connection: monitoring, events and history. Credentials stay in the connector window.",
        ["Konnektor-Fenster öffnen"] = "Open connector window",
        ["Lokale TI-Anbindung einrichten und auslesen"] = "Set up and read the local TI connection",
        ["Öffnen"] = "Open",
        ["Einrichten"] = "Set up",
        ["Nicht eingerichtet"] = "Not set up",
        ["Erreichbar"] = "Reachable",
        ["Live-Ereignisse"] = "Live events",
        ["Zertifikate"] = "Certificates",
        ["Diagnose"] = "Diagnostics",
        ["Bitte warten …"] = "Please wait …",
        ["C.AUT, C.ENC, C.SIG und C.QES der gesteckten Karten. Ablaufdaten erscheinen farblich, bevor sie kritisch werden."] = "C.AUT, C.ENC, C.SIG and C.QES of the inserted cards. Expiry dates change color before they become critical.",
        ["Keine Zertifikate gelesen. Karte wählen und „Alle Zertifikate lesen“ verwenden."] = "No certificates read. Select a card and use “Read all certificates”.",
        ["Karte ein/aus und Terminals werden über CETP gemeldet, sobald ein Abo steht."] = "Card inserted/removed and terminals are reported via CETP once a subscription is active.",
        ["Noch keine Ereignisse empfangen."] = "No events received yet.",
        ["Status · Terminals · Karten · PIN · Zertifikate"] = "Status · terminals · cards · PIN · certificates",
        // Konnektor-Fenster (Neugestaltung)
        ["Ablaufkalender (.ics)"] = "Expiry calendar (.ics)",
        ["Alle Zertifikatsabläufe als Kalenderdatei (.ics) für Outlook bzw. den Kalender speichern – mit Erinnerungen 90, 30 und 7 Tage vorher"] = "Save all certificate expiry dates as a calendar file (.ics) for Outlook or your calendar – with reminders 90, 30 and 7 days before",
        ["Ab wie vielen Resttagen ein Zertifikat als bald ablaufend gilt."] = "Number of remaining days from which a certificate counts as expiring soon.",
        ["Ablauf des letzten Abrufs"] = "Log of the last query",
        ["Abstand zwischen zwei Abrufen im Hintergrund."] = "Time between two background queries.",
        ["Adresse des Konnektors und Client-Zertifikat dieses Arbeitsplatzes."] = "Connector address and client certificate of this workstation.",
        ["Aufrufkontext"] = "Call context",
        ["Aus connector.sds und GetResourceInformation"] = "From connector.sds and GetResourceInformation",
        ["Aus connector.sds – Name, Version und Erreichbarkeit der angebotenen Dienste"] = "From connector.sds – name, version and endpoints of the offered services",
        ["Bereiche"] = "Sections",
        ["Dienstverzeichnis"] = "Service directory",
        ["Dienstverzeichnis des Konnektors und Protokoll des letzten Abrufs."] = "Service directory of the connector and log of the last query.",
        ["Für Timeline und Störungsdauer – auch wenn die gematik-API nicht erreichbar ist"] = "For timeline and outage duration – even when the gematik API is unreachable",
        ["Genau die Werte aus der Konnektor-Administration verwenden. Erlaubt sind A-Z, a-z, 0-9, - und _, 1–64 Zeichen."] = "Use exactly the values from the connector administration. Allowed: A-Z, a-z, 0-9, - and _, 1–64 characters.",
        ["Gepinntes TLS-Zertifikat"] = "Pinned TLS certificate",
        ["Gesteckte Karten"] = "Inserted cards",
        ["IP oder Hostname, optional mit Port, z. B. 192.168.1.10 oder 10.0.0.5:8443"] = "IP or host name, optionally with port, e.g. 192.168.1.10 or 10.0.0.5:8443",
        ["Intervall"] = "Interval",
        ["Kartenterminals, ihre Verbindung und die gesteckten Karten."] = "Card terminals, their connection and the inserted cards.",
        ["Leer lassen, außer HBA-Operationen erfordern sie"] = "Leave empty unless HBA operations require it",
        ["Meldet Störungen auch bei geschlossenem Fenster."] = "Reports problems even while the window is closed.",
        ["Min."] = "min",
        ["Noch kein Client-Zertifikat geladen."] = "No client certificate loaded yet.",
        ["Nur A-Z, a-z, 0-9, - und _. Punkte oder Leerzeichen quittieren viele Konnektoren mit 4000 Syntaxfehler"] = "Only A-Z, a-z, 0-9, - and _. Many connectors reject dots or spaces with error 4000 (syntax error)",
        ["Nur, wenn der Konnektor Benutzer/Passwort statt eines Zertifikats verlangt"] = "Only if the connector requires user/password instead of a certificate",
        ["Passwort"] = "Password",
        ["SMC-B, HBA und eGK – PIN-Zustand, Zertifikate und Aktionen."] = "SMC-B, HBA and eGK – PIN state, certificates and actions.",
        ["Schaltet Abruf und Anzeige des Konnektors ein."] = "Turns connector queries and display on.",
        ["Tage"] = "days",
        ["Terminals"] = "Terminals",
        ["Verhalten"] = "Behavior",
        ["Warnung ab"] = "Warn from",
        ["Werte aus der Konnektor-Administration. Das Client-Zertifikat (.p12) wird im Konnektor für dieses Clientsystem erzeugt bzw. hinterlegt."] = "Values from the connector administration. The client certificate (.p12) is generated or stored in the connector for this client system.",
        ["Wie in der Konnektor-Administration hinterlegt"] = "As configured in the connector administration",
        ["Wie oft abgefragt wird und was die App im Hintergrund tut."] = "How often to query and what the app does in the background.",
        ["Zugang"] = "Access",
        ["Zugang und Aufrufkontext"] = "Access and call context",
        ["Zustand, gesteckte Karte und Eckdaten des Konnektors auf einen Blick."] = "Status, inserted card and key data of the connector at a glance.",
        ["Übertragung"] = "Transport",
        ["Bericht"] = "Report",
        ["Aktualisieren"] = "Refresh",
        ["VPN SIS"] = "VPN SIS",
        ["Zertifikatswarnung ab"] = "Certificate warning from",

        ["Im Hintergrund überwachen (Toasts)"] = "Monitor in background (toasts)",
        ["Ereignis-Abos (Karte rein/raus sofort, statt nur Pollen)"] = "Event subscriptions (card inserted/removed instantly, instead of polling only)",
        // Die XAML-Fassung laesst "statt nur Pollen" weg – ohne diese Zeile stand der Kasten
        // im Englischen deutsch da, weil die Uebersetzung nach dem vollen Text sucht.
        ["Ereignis-Abos (Karte rein/raus sofort)"] = "Event subscriptions (card inserted/removed instantly)",
        ["Karte"] = "Card",
        ["Ereignis-Abos"] = "Event subscriptions",
        ["Was der Konnektor derzeit an diese App meldet. Leere Liste heißt: es wird nur gepollt."] = "What the connector currently reports to this app. An empty list means: polling only.",
        ["Abos dieser App kündigen"] = "Cancel this app's subscriptions",
        ["Entfernt alle Einträge, die auf diesem Rechner zeigen. Hilft, wenn derselbe Lauscher nach Fehlversuchen mehrfach steht."] = "Removes all entries pointing at this machine. Useful when the same listener keeps being registered after failed attempts.",
        ["Konnektor-Verlauf lokal speichern (14 Tage)"] = "Store connector history locally (14 days)",
        ["Die App lauscht auf einem lokalen TCP-Port. Schlägt das Abo fehl, wird automatisch weiter gepollt."] = "The app listens on a local TCP port. If the subscription fails, polling continues automatically.",
        ["Für Timeline und Stördauer – auch dann, wenn die gematik-API nicht erreichbar ist"] = "For timeline and outage duration – even when the gematik API is unreachable",
        ["Intervall (Minuten)"] = "Interval (minutes)",
        ["Zertifikatswarnung ab (Tage)"] = "Certificate warning from (days)",
        ["Speichern & verbinden"] = "Save & connect",
        ["Nur testen"] = "Test only",
        ["Produktinformationen (connector.sds) und Betriebszustand (GetResourceInformation)"] = "Product information (connector.sds) and operating state (GetResourceInformation)",
        ["Karten"] = "Cards",
        ["SMC-B und HBA: PIN-Eingaben erfolgen ausschließlich am Kartenterminal – die App sieht keine PIN oder PUK."] = "SMC-B and HBA: PINs are entered exclusively on the card terminal – the app never sees a PIN or PUK.",
        ["Kartenterminals"] = "Card terminals",
        ["Dienstverzeichnis (connector.sds)"] = "Service directory (connector.sds)",
        ["Protokoll"] = "Log",
        ["• Konnektor-Anbindung: Status, Karten, PIN-Verwaltung"] = "• Connector integration: status, cards, PIN management",
        ["Einschränkung ·"] = "Restriction ·",

        // 3.1: Konnektor-Verlauf, Benachrichtigungen, Zertifikate
        ["Verlauf"] = "History",
        ["Konnektor-Verlauf (14 Tage)"] = "Connector history (14 days)",
        ["Erreichbarkeit, VPN zur TI und zum SIS sowie gesteckte Karten – lokal aufgezeichnet bei jedem Abruf. Kachel anklicken für die Stunden."] = "Reachability, VPN to TI and SIS and inserted cards – recorded locally on every poll. Click a tile for the hours.",
        ["Noch kein Verlauf aufgezeichnet. Der Verlauf füllt sich mit jedem Abruf, solange „Verlauf lokal speichern“ in den Einstellungen aktiv ist."] = "No history recorded yet. The history fills up with every poll as long as “Store history locally” is enabled in the settings.",
        ["Ablauf von SMC-B, HBA und gSMC-KT (Kartenterminal) sowie des Clientsystem-Zertifikats. Warnstufen: 90, 30 und 7 Tage vor Ablauf."] = "Expiry of SMC-B, HBA and gSMC-KT (card terminal) plus the client system certificate. Warning levels: 90, 30 and 7 days before expiry.",
        ["1883, mit TLS meist 8883"] = "1883, usually 8883 with TLS",
        ["Adresse"] = "Address",
        ["Benutzer"] = "User",
        ["Benutzer-ID (für HBA)"] = "User ID (for HBA)",
        ["Ein Home-Assistant-Benutzer oder ein Benutzer des Mosquitto-Add-ons"] = "A Home Assistant user or a user of the Mosquitto add-on",
        ["Entitäten"] = "Entities",
        ["Frei wählbar (z. B. Kürzel des Arztes). Nötig, wenn der Konnektor bei HBA-Abfragen Fehler 4003 meldet."] = "Freely chosen (e.g. the doctor's initials). Needed if the connector reports error 4003 for HBA requests.",
        ["Frei wählbare Kennung des Benutzers, z. B. das Kürzel des HBA-Inhabers. Wird nicht im Konnektor angelegt."] = "Freely chosen user identifier, e.g. the HBA holder's initials. Not created in the connector.",
        ["Gesteckte Karte"] = "Inserted card",
        ["Home Assistant"] = "Home Assistant",
        ["Home Assistant → Profil → Sicherheit → Langlebige Zugriffstoken. Leer lassen, um das gespeicherte zu behalten."] = "Home Assistant → Profile → Security → Long-lived access tokens. Leave empty to keep the stored one.",
        ["Hostname oder IP des Brokers, z. B. homeassistant.local (Mosquitto-Add-on)"] = "Host name or IP of the broker, e.g. homeassistant.local (Mosquitto add-on)",
        ["Je Balken eine Stunde – grün in Ordnung, gelb zeitweise, rot gestört."] = "One bar per hour – green fine, yellow partly, red down.",
        ["Letzte 24 Stunden"] = "Last 24 hours",
        ["Long-Lived Access Token"] = "Long-lived access token",
        ["MQTT (empfohlen)"] = "MQTT (recommended)",
        ["MQTT-Broker"] = "MQTT broker",
        ["Name der Praxis"] = "Practice name",
        ["Noch nichts gesendet."] = "Nothing sent yet.",
        ["Port"] = "Port",
        ["REST-API"] = "REST API",
        ["Schickt nach jedem Abruf Erreichbarkeit, VPN TI/SIS, Live-Ereignisse, Karten, Terminals und die Restlaufzeit von SMC-B, HBA, gSMC-KT und Clientsystem-Zertifikat als Sensoren an Home Assistant."] = "After every poll, sends reachability, VPN TI/SIS, live events, cards, terminals and the remaining validity of SMC-B, HBA, gSMC-KT and client system certificate to Home Assistant as sensors.",
        ["TLS"] = "TLS",
        ["Unterscheidet mehrere Konnektoren in Home Assistant. Leer: Inhaber der SMC-B."] = "Distinguishes several connectors in Home Assistant. Empty: holder of the SMC-B.",
        ["Verbindung testen"] = "Test connection",
        ["erreichbar"] = "reachable",
        ["z. B. http://homeassistant.local:8123 oder https://…ui.nabu.casa"] = "e.g. http://homeassistant.local:8123 or https://…ui.nabu.casa",
        ["Konnektor-Benachrichtigungen"] = "Connector notifications",
        ["Zertifikatsablauf melden (90, 30 und 7 Tage vorher)"] = "Notify certificate expiry (90, 30 and 7 days before)",
        ["PIN-Status melden (SMC-B nicht freigeschaltet, PIN gesperrt, letzter Versuch)"] = "Notify PIN status (SMC-B not unlocked, PIN blocked, last attempt)",
        ["Toasts bei Ereignissen"] = "Toasts for events",
        ["Karte entfernt"] = "Card removed",
        ["Karte gesteckt"] = "Card inserted",
        ["Terminal getrennt"] = "Terminal disconnected",
        ["Terminal verbunden"] = "Terminal connected",
        ["Karten:"] = "Cards:",
        ["Verlauf lokal speichern (14 Tage)"] = "Store history locally (14 days)",
        ["Konnektor-Karte ausblenden"] = "Hide connector card",
        ["Konnektor-Karte im Hauptfenster anzeigen"] = "Show connector card in main window",
        ["PIN-Verwaltung erlauben"] = "Allow PIN management",
        ["PIN verifizieren, ändern, mit PUK entsperren, aktivieren und deaktivieren im Konnektor-Fenster. Standardmäßig aus – die App liest dann nur."] = "Verify, change, unblock with PUK, enable and disable PINs in the connector window. Off by default – the app then only reads.",
        ["Gilt, solange kein Konnektor eingerichtet ist. Ein eingerichteter Konnektor wird immer angezeigt."] = "Applies while no connector is set up. A configured connector is always shown."
    };

    /// <summary>
    /// Exact catalog lookup only for UI chrome. Never substring-replaces free-form or
    /// API text (avoids corrupting words like <c>davon</c> via key <c>von</c>).
    /// </summary>
    public static string Translate(string? value, CultureInfo? culture = null)
    {
        if (string.IsNullOrEmpty(value) || (culture is null ? IsGerman : IsGermanCulture(culture)))
            return value ?? string.Empty;
        return English.TryGetValue(value, out var translated) ? translated : value;
    }

    /// <summary>
    /// Translate an AppRow status label. When AffectedFunctions are appended after
    /// " – ", only the leading status fragment is exact-matched; the API suffix stays raw.
    /// </summary>
    public static string TranslateAppDetail(string? value, CultureInfo? culture = null)
    {
        if (string.IsNullOrEmpty(value) || (culture is null ? IsGerman : IsGermanCulture(culture)))
            return value ?? string.Empty;

        const string sep = " – ";
        var idx = value.IndexOf(sep, StringComparison.Ordinal);
        if (idx < 0)
            return Translate(value, culture);

        return Translate(value[..idx], culture) + value[idx..];
    }

    /// <summary>
    /// Known update/error prefixes that may be followed by a dynamic exception suffix.
    /// Leading-prefix only — never mid-string replace.
    /// </summary>
    private static readonly string[] DynamicMessagePrefixes =
    [
        "Update-Prüfung fehlgeschlagen:",
        "Prüfsumme konnte nicht geprüft werden:",
        "Download oder Start fehlgeschlagen:",
        "Authenticode-Prüfung fehlgeschlagen:",
        "Unerwarteter Fehler:",
        "Störungsbericht gespeichert:",
        "Export fehlgeschlagen:"
    ];

    /// <summary>
    /// Exact match, or translate a known leading update/error prefix and keep the suffix.
    /// Use for updater messages — not for API/incident prose.
    /// </summary>
    public static string TranslateMessage(string? value, CultureInfo? culture = null)
    {
        if (string.IsNullOrEmpty(value) || (culture is null ? IsGerman : IsGermanCulture(culture)))
            return value ?? string.Empty;
        if (English.TryGetValue(value, out var exact))
            return exact;

        foreach (var prefix in DynamicMessagePrefixes.OrderByDescending(p => p.Length))
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal))
                return Translate(prefix, culture) + value[prefix.Length..];
        }

        return value;
    }

    public static void Apply(Window window)
    {
        if (window is null || IsGerman)
            return;
        window.Title = Translate(window.Title);
        ApplyElement(window);
    }

    private static void ApplyElement(DependencyObject element)
    {
        // A TextBlock with inline content can contain hyperlinks or dynamic Runs (for
        // example, the footer's clickable R.C. and version number). Assigning Text
        // would clear those inlines, so translate only plain TextBlocks.
        if (element is TextBlock textBlock && textBlock.Inlines.Count == 0)
            textBlock.Text = Translate(textBlock.Text);
        // Uebersetzt wird nur, was ein Text ist. Ein Content oder Header, der ein untergeordneter
        // Teil ist – eine Knopf mit Bild und Text, eine Klappe mit ueberschrift – wird beim
        // Durchlaufen der Kindkinder ohnehin richtig erwischt. Ihn ueber ToString() zurueckzu-
        // schreiben, waere schlimmer als nichts tun: aus dem StackPanel wird der String
        // "System.Windows.Controls.StackPanel", das Bild samt Beschriftung ist damit weg. Genau
        // das war der Fall, als die Kopfknopfe des Konnektor-Fensters von reiner Beschriftung auf
        // Vektor-Zeichen mit Text umgestellt wurden. Im Deutschen faellt es nicht auf, dort
        // fuehrt der fruehen Ausstieg zu keiner Zuweisung.
        if (element is System.Windows.Controls.Label label && label.Content is string labelText) label.Content = Translate(labelText);
        if (element is System.Windows.Controls.Button button && button.Content is string buttonContent) button.Content = Translate(buttonContent);
        if (element is System.Windows.Controls.CheckBox checkBox && checkBox.Content is string checkContent) checkBox.Content = Translate(checkContent);
        if (element is System.Windows.Controls.RadioButton radioButton && radioButton.Content is string radioContent) radioButton.Content = Translate(radioContent);
        if (element is HeaderedContentControl headered && headered.Header is string headerText) headered.Header = Translate(headerText);
        if (element is TabItem tab && tab.Header is string tabHeader) tab.Header = Translate(tabHeader);
        if (element is System.Windows.Controls.MenuItem menu && menu.Header is string menuHeader) menu.Header = Translate(menuHeader);
        if (element is FrameworkElement fe && fe.ToolTip is string tip) fe.ToolTip = Translate(tip);

        foreach (var child in LogicalTreeHelper.GetChildren(element))
            if (child is DependencyObject dependencyObject)
                ApplyElement(dependencyObject);
    }
}
