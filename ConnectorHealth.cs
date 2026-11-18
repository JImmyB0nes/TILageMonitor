namespace TILageMonitor;

public sealed record ConnectorAlert(string Key, string Title, string Body, ToastUrgency Urgency);

/// <summary>Bewertet einen Konnektor-Datenstand und leitet Warnungen ab (I/O-frei, testbar).</summary>
public static class ConnectorHealth
{
    public static List<ConnectorAlert> Evaluate(ConnectorSnapshot snapshot, int certificateWarningDays, DateTime now)
    {
        var alerts = new List<ConnectorAlert>();

        if (snapshot.Resource?.IsTiOnline == false)
        {
            alerts.Add(new ConnectorAlert(
                "vpn-ti",
                L("Konnektor: TI-Verbindung getrennt", "Connector: TI connection lost"),
                L("Der VPN-Zugang des Konnektors zur Telematikinfrastruktur ist offline.", "The connector's VPN access to the telematics infrastructure is offline."),
                ToastUrgency.Error));
        }

        foreach (var error in snapshot.Resource?.ActiveErrors ?? [])
        {
            var urgency = error.Severity.Equals("Fatal", StringComparison.OrdinalIgnoreCase) ||
                          error.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)
                ? ToastUrgency.Error
                : ToastUrgency.Warning;
            alerts.Add(new ConnectorAlert(
                "error-" + error.Condition,
                L("Konnektor meldet Fehlerzustand", "Connector reports an error state"),
                $"{error.Condition} ({error.Severity}, {error.Type})",
                urgency));
        }

        foreach (var terminal in snapshot.Terminals.Where(t => t.Connected == false))
        {
            alerts.Add(new ConnectorAlert(
                "ct-" + terminal.CtId,
                L("Kartenterminal nicht verbunden", "Card terminal not connected"),
                L($"{Label(terminal)} ist vom Konnektor getrennt.", $"{Label(terminal)} is disconnected from the connector."),
                ToastUrgency.Warning));
        }

        // Nur wenn GetCards erfolgreich war, kann eine fehlende SMC-B sicher erkannt werden.
        var cardsKnown = !snapshot.Warnings.Any(w => w.Contains("GetCards", StringComparison.Ordinal));
        if (cardsKnown && !snapshot.Cards.Any(c => c.IsSmcB))
        {
            alerts.Add(new ConnectorAlert(
                "no-smcb",
                L("Keine SMC-B gesteckt", "No SMC-B inserted"),
                L("Der Konnektor meldet für diesen Mandanten keine SMC-B. TI-Anwendungen funktionieren ohne Praxisausweis nicht.", "The connector reports no SMC-B for this tenant. TI applications do not work without an institution card."),
                ToastUrgency.Warning));
        }

        foreach (var card in snapshot.Cards.Where(c => c.SupportsPinManagement))
        {
            var id = string.IsNullOrWhiteSpace(card.Iccsn) ? card.CardHandle : card.Iccsn;

            foreach (var pin in card.PinStates.Where(p => p.Error is null))
            {
                if (pin.IsBlocked)
                {
                    alerts.Add(new ConnectorAlert(
                        $"pin-blocked-{id}-{pin.PinType}",
                        L($"{card.DisplayName}: PIN gesperrt", $"{card.DisplayName}: PIN blocked"),
                        L($"{pin.PinType} ist gesperrt und muss mit der PUK entsperrt werden.", $"{pin.PinType} is blocked and must be unblocked with the PUK."),
                        ToastUrgency.Error));
                }
                else if (pin.IsTransportPin)
                {
                    alerts.Add(new ConnectorAlert(
                        $"pin-transport-{id}-{pin.PinType}",
                        L($"{card.DisplayName}: Transport-PIN aktiv", $"{card.DisplayName}: transport PIN active"),
                        L($"Für {pin.PinType} muss zuerst eine eigene PIN gesetzt werden (PIN ändern).", $"A personal PIN must be set for {pin.PinType} first (change PIN)."),
                        ToastUrgency.Warning));
                }
                else if (card.IsSmcB && pin.PinType == "PIN.SMC" && !pin.IsVerified)
                {
                    alerts.Add(new ConnectorAlert(
                        $"smcb-unverified-{id}",
                        L("SMC-B nicht freigeschaltet", "SMC-B not unlocked"),
                        L($"{card.DisplayName} ist nicht PIN-verifiziert{LeftTries(pin)}. eRezept, ePA, KIM und VSDM benötigen eine freigeschaltete SMC-B.", $"{card.DisplayName} is not PIN-verified{LeftTries(pin)}. eRezept, ePA, KIM and VSDM require an unlocked SMC-B."),
                        ToastUrgency.Warning));
                }
            }

            var expiry = card.AuthCertificate?.NotAfter ?? card.CertificateExpirationDate;
            if (expiry is { } notAfter)
            {
                var days = (int)Math.Floor((notAfter - now).TotalDays);
                if (days < 0)
                {
                    alerts.Add(new ConnectorAlert(
                        $"cert-expired-{id}",
                        L($"{card.DisplayName}: Zertifikat abgelaufen", $"{card.DisplayName}: certificate expired"),
                        L($"Das Kartenzertifikat ist seit {notAfter:dd.MM.yyyy} abgelaufen. Bitte Folgekarte bestellen und stecken.", $"The card certificate expired on {notAfter:yyyy-MM-dd}. Please order and insert a replacement card."),
                        ToastUrgency.Error));
                }
                else if (days <= certificateWarningDays)
                {
                    alerts.Add(new ConnectorAlert(
                        $"cert-expiring-{id}",
                        L($"{card.DisplayName}: Zertifikat läuft ab", $"{card.DisplayName}: certificate expiring"),
                        L($"Das Kartenzertifikat läuft am {notAfter:dd.MM.yyyy} ab (noch {days} Tage).", $"The card certificate expires on {notAfter:yyyy-MM-dd} ({days} days left)."),
                        days <= 14 ? ToastUrgency.Error : ToastUrgency.Warning));
                }
            }
        }

        return alerts;
    }

    public static ToastUrgency? Worst(IEnumerable<ConnectorAlert> alerts)
    {
        ToastUrgency? worst = null;
        foreach (var alert in alerts)
        {
            if (worst is null || alert.Urgency > worst)
                worst = alert.Urgency;
        }
        return worst;
    }

    private static string Label(ConnectorCardTerminal terminal) =>
        string.IsNullOrWhiteSpace(terminal.Name) ? terminal.CtId : $"{terminal.Name} ({terminal.CtId})";

    private static string LeftTries(ConnectorPinState pin) =>
        pin.LeftTries is { } tries ? L($" (verbleibende Versuche: {tries})", $" ({tries} tries left)") : "";

    private static string L(string de, string en) => LocalizationService.Pick(de, en);
}
