namespace TILageMonitor;

/// <summary>
/// Konnektor-Karte im Hauptfenster (I/O-frei, testbar). Ist ein Konnektor eingerichtet, steht
/// die Karte immer da – sie zeigt dann den Zustand, auch "nicht erreichbar". Ohne Einrichtung
/// ("Nicht eingerichtet") laesst sie sich per X ausblenden; das wird gespeichert und in den
/// Einstellungen wieder rueckgaengig gemacht. Einrichten bleibt ueber Einstellungen, Tray und
/// die Konnektor-Schaltflaeche erreichbar.
/// </summary>
public static class ConnectorCardVisibility
{
    /// <summary>Eingerichtet heisst: Anbindung aktiv und Aufrufkontext vollstaendig.</summary>
    public static bool IsSetUp(ConnectorSettings settings) => settings.Enabled && settings.IsConfigured;

    /// <summary>Karte anzeigen?</summary>
    public static bool ShouldShow(ConnectorSettings settings) =>
        IsSetUp(settings) || settings.ShowMainWindowCard;

    /// <summary>X nur im Ausgangszustand (nicht eingerichtet).</summary>
    public static bool ShowCloseButton(ConnectorSettings settings) => !IsSetUp(settings);

    /// <summary>Was die Karte (und die Konnektor-Schaltflaeche) zeigt.</summary>
    public enum State
    {
        NotSetUp,
        /// <summary>Eingerichtet, aber Hintergrundueberwachung aus – kein Datenstand zu erwarten.</summary>
        MonitoringOff,
        Querying,
        Unreachable,
        /// <summary>Konnektor erreichbar, lehnt aber ab (Konfiguration, Rechte, Zertifikat).</summary>
        Rejected,
        Ok
    }

    public static State Classify(ConnectorSettings settings, bool hasSnapshot, bool hasError, bool errorIsConfiguration)
    {
        if (!IsSetUp(settings))
            return State.NotSetUp;
        if (!settings.MonitoringEnabled)
            return State.MonitoringOff;
        if (hasError)
            return errorIsConfiguration ? State.Rejected : State.Unreachable;
        return hasSnapshot ? State.Ok : State.Querying;
    }

    /// <summary>"Erreichbar"-Chip: gruen/rot nur mit Befund, sonst neutral (kein falsches Rot).</summary>
    public static bool? Reachable(State state) => state switch
    {
        State.Ok or State.Rejected => true,
        State.Unreachable => false,
        _ => null
    };
}
