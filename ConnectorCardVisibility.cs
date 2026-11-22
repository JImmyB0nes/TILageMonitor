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
}
