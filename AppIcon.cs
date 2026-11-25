using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TILageMonitor;

/// <summary>
/// Linien-Symbol auf einem 24er-Raster. Ersetzt die Segoe-Fluent-Glyphen dort, wo sie wie
/// Clipart wirkten: gleiche Strichstaerke, runde Enden, unabhaengig von der installierten
/// Schrift. Angesprochen wird es weiter ueber den Fluent-Code (Glyph="&#xE8C7;"), damit
/// Aufrufer und XAML nicht umdenken muessen – unbekannte Codes zeichnen nichts.
/// </summary>
public sealed class AppIcon : Shape
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(AppIcon),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(AppIcon),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure,
            (d, _) => ((AppIcon)d).ApplyDefaultThickness()));

    public AppIcon() => ApplyDefaultThickness();

    /// <summary>Strichstaerke folgt der Groesse, solange niemand sie ausdruecklich setzt.</summary>
    private void ApplyDefaultThickness()
    {
        if (ReadLocalValue(StrokeThicknessProperty) == DependencyProperty.UnsetValue)
            SetCurrentValue(StrokeThicknessProperty, Math.Clamp(Size / 11.0, 1.25, 2.25));
    }

    static AppIcon()
    {
        StrokeStartLineCapProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(PenLineCap.Round));
        StrokeEndLineCapProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(PenLineCap.Round));
        StrokeLineJoinProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(PenLineJoin.Round));
        StretchProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(Stretch.None));
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(false));
        HorizontalAlignmentProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(System.Windows.HorizontalAlignment.Center));
        VerticalAlignmentProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(VerticalAlignment.Center));
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>Kantenlaenge in DIP. Die Strichstaerke waechst mit, bleibt aber zwischen 1,25 und 2,25.</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    protected override Geometry DefiningGeometry
    {
        get
        {
            if (!Paths.TryGetValue(Glyph ?? "", out var data))
                return Geometry.Empty;
            var geometry = Geometry.Parse(data).Clone();
            var scale = Size / 24.0;
            geometry.Transform = new ScaleTransform(scale, scale);
            return geometry;
        }
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size constraint) => new(Size, Size);

    protected override System.Windows.Size ArrangeOverride(System.Windows.Size finalSize) => new(Size, Size);

    /// <summary>True, wenn fuer den Fluent-Code ein eigenes Symbol existiert.</summary>
    public static bool Has(string glyph) => Paths.ContainsKey(glyph);

    /// <summary>Symbol fuer Code-Behind: Linien-Symbol, falls vorhanden, sonst die Fluent-Glyphe.</summary>
    public static FrameworkElement Create(string glyph, double size, System.Windows.Media.Brush brush)
    {
        if (Has(glyph))
            return new AppIcon { Glyph = glyph, Size = size, Stroke = brush };
        return new System.Windows.Controls.TextBlock
        {
            Text = glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = size,
            Foreground = brush,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    // Alle Pfade auf 24×24, Linien ohne Fuellung. "h0.01" ergibt mit runden Enden einen Punkt.
    // Netzgeraet mit Statusleuchten, darunter die Leitung ins Praxisnetz.
    private const string Connector =
        "M4,4 H20 A2,2 0 0 1 22,6 V13 A2,2 0 0 1 20,15 H4 A2,2 0 0 1 2,13 V6 A2,2 0 0 1 4,4 Z " +
        "M6,9.5 h0.01 M9,9.5 h0.01 M13,9.5 H18 " +
        "M12,15 V19 M5,19 H19 M5,19 h0.01 M19,19 h0.01";
    private const string Card =
        "M4,5 H20 A2,2 0 0 1 22,7 V17 A2,2 0 0 1 20,19 H4 A2,2 0 0 1 2,17 V7 A2,2 0 0 1 4,5 Z " +
        "M6.5,9 H10.5 A0.5,0.5 0 0 1 11,9.5 V12.5 A0.5,0.5 0 0 1 10.5,13 H6.5 A0.5,0.5 0 0 1 6,12.5 V9.5 A0.5,0.5 0 0 1 6.5,9 Z " +
        "M14,10 H18 M14,13 H16.5 M6,16 H11";
    private const string Terminal =
        "M7,2 H17 A2,2 0 0 1 19,4 V20 A2,2 0 0 1 17,22 H7 A2,2 0 0 1 5,20 V4 A2,2 0 0 1 7,2 Z " +
        "M8.5,5.5 H15.5 V9.5 H8.5 Z " +
        "M9,13.5 h0.01 M12,13.5 h0.01 M15,13.5 h0.01 M9,16.5 h0.01 M12,16.5 h0.01 M15,16.5 h0.01 M10,19 H14";
    private const string Certificate =
        "M12,21 H6 A2,2 0 0 1 4,19 V5 A2,2 0 0 1 6,3 H18 A2,2 0 0 1 20,5 V10 " +
        "M8,8 H16 M8,12 H12 " +
        "M20,15.5 A3,3 0 1 1 14,15.5 A3,3 0 1 1 20,15.5 Z M15.3,18.2 L15,22 L17,21 L19,22 L18.7,18.2";
    private const string Overview =
        "M4,3 H9 A1,1 0 0 1 10,4 V11 A1,1 0 0 1 9,12 H4 A1,1 0 0 1 3,11 V4 A1,1 0 0 1 4,3 Z " +
        "M15,3 H20 A1,1 0 0 1 21,4 V7 A1,1 0 0 1 20,8 H15 A1,1 0 0 1 14,7 V4 A1,1 0 0 1 15,3 Z " +
        "M15,12 H20 A1,1 0 0 1 21,13 V20 A1,1 0 0 1 20,21 H15 A1,1 0 0 1 14,20 V13 A1,1 0 0 1 15,12 Z " +
        "M4,16 H9 A1,1 0 0 1 10,17 V20 A1,1 0 0 1 9,21 H4 A1,1 0 0 1 3,20 V17 A1,1 0 0 1 4,16 Z";
    private const string Bell =
        "M6,8 A6,6 0 0 1 18,8 C18,15 21,17 21,17 H3 C3,17 6,15 6,8 Z M10.3,21 A1.94,1.94 0 0 0 13.7,21";
    private const string History =
        "M3,12 A9,9 0 1 0 5.6,5.6 L3,8.2 M3,3.5 V8.2 H7.7 M12,7 V12 L15.5,14";
    private const string Pulse = "M22,12 H18 L15,20 L9,4 L6,12 H2";
    private const string Sliders =
        "M4,21 V14 M4,10 V3 M12,21 V12 M12,8 V3 M20,21 V16 M20,12 V3 M2,14 H6 M10,8 H14 M18,16 H22";
    private const string Check = "M20,6 L9,17 L4,12";
    private const string Cross = "M18,6 L6,18 M6,6 L18,18";
    private const string Warning =
        "M10.3,3.9 L1.8,18 A2,2 0 0 0 3.5,21 H20.5 A2,2 0 0 0 22.2,18 L13.7,3.9 A2,2 0 0 0 10.3,3.9 Z M12,9 V13 M12,17 h0.01";
    private const string ErrorCircle =
        "M12,2 A10,10 0 1 1 12,22 A10,10 0 1 1 12,2 Z M15,9 L9,15 M9,9 L15,15";
    private const string Info =
        "M12,2 A10,10 0 1 1 12,22 A10,10 0 1 1 12,2 Z M12,16 V12 M12,8 h0.01";
    private const string Key =
        "M7.5,10 A5.5,5.5 0 1 1 7.5,21 A5.5,5.5 0 1 1 7.5,10 Z M11.4,11.6 L21,2 M16,7 L19,10 M18.5,4.5 L21,6.5";
    private const string Refresh =
        "M21,12 A9,9 0 1 1 18.4,5.6 L21,8 M21,3 V8 H16";
    private const string Copy =
        "M10,8 H18 A2,2 0 0 1 20,10 V18 A2,2 0 0 1 18,20 H10 A2,2 0 0 1 8,18 V10 A2,2 0 0 1 10,8 Z M4,16 V6 A2,2 0 0 1 6,4 H16";
    private const string Clock =
        "M12,2 A10,10 0 1 1 12,22 A10,10 0 1 1 12,2 Z M12,6.5 V12 L15.5,14";
    private const string Lock =
        "M5,11 H19 A2,2 0 0 1 21,13 V19 A2,2 0 0 1 19,21 H5 A2,2 0 0 1 3,19 V13 A2,2 0 0 1 5,11 Z M7,11 V7 A5,5 0 0 1 17,7 V11";
    private const string Calendar =
        "M5,4 H19 A2,2 0 0 1 21,6 V19 A2,2 0 0 1 19,21 H5 A2,2 0 0 1 3,19 V6 A2,2 0 0 1 5,4 Z M16,2 V6 M8,2 V6 M3,10 H21";
    private const string Globe =
        "M12,2 A10,10 0 1 1 12,22 A10,10 0 1 1 12,2 Z M2,12 H22 M12,2 A15.3,15.3 0 0 1 12,22 A15.3,15.3 0 0 1 12,2 Z";
    private const string Person =
        "M12,3 A4,4 0 1 1 12,11 A4,4 0 1 1 12,3 Z M4,21 V20 A6,6 0 0 1 10,14 H14 A6,6 0 0 1 20,20 V21";
    private const string Shield =
        "M12,22 C12,22 20,18 20,12 V5 L12,2 L4,5 V12 C4,18 12,22 12,22 Z M9,12 L11,14 L15,10";
    private const string House =
        "M3,10 L12,3 L21,10 V20 A1,1 0 0 1 20,21 H4 A1,1 0 0 1 3,20 Z M9.5,21 V14.5 H14.5 V21";
    private const string Folder =
        "M20,20 A2,2 0 0 0 22,18 V8 A2,2 0 0 0 20,6 H12.1 A2,2 0 0 1 10.4,5.1 L9.6,3.9 A2,2 0 0 0 7.9,3 H4 A2,2 0 0 0 2,5 V18 A2,2 0 0 0 4,20 Z";
    private const string Save =
        "M5,3 H16 L21,8 V19 A2,2 0 0 1 19,21 H5 A2,2 0 0 1 3,19 V5 A2,2 0 0 1 5,3 Z M7,3 V8 H15 M7,21 V14 H17 V21";
    private const string ListCheck =
        "M11,6 H21 M11,12 H21 M11,18 H21 M3,6 L4.5,7.5 L7.5,4.5 M3,12 L4.5,13.5 L7.5,10.5 M3,18 L4.5,19.5 L7.5,16.5";
    private const string Trash =
        "M3,6 H21 M19,6 V20 A2,2 0 0 1 17,22 H7 A2,2 0 0 1 5,20 V6 M8,6 V4 A2,2 0 0 1 10,2 H14 A2,2 0 0 1 16,4 V6 M10,11 V17 M14,11 V17";
    private const string Chevron = "M6,9 L12,15 L18,9";
    private const string Pen =
        "M17,3 A2.85,2.85 0 1 1 21,7 L7.5,20.5 L2,22 L3.5,16.5 Z";

    private static readonly Dictionary<string, string> Paths = new()
    {
        ["\uE703"] = Connector,   // Verbindung
        ["\uE968"] = Connector,   // Netzwerkturm (Hauptfenster)
        ["\uE950"] = Connector,   // Geraeteinfo
        ["\uE8C7"] = Card,
        ["\uE7F4"] = Terminal,
        ["\uEB95"] = Certificate,
        ["\uE80F"] = Overview,
        ["\uEA8F"] = Bell,
        ["\uE81C"] = History,
        ["\uE9D9"] = Pulse,
        ["\uE713"] = Sliders,
        ["\uE73E"] = Check,
        ["\uE711"] = Cross,
        ["\uE7BA"] = Warning,
        ["\uEA39"] = ErrorCircle,
        ["\uE946"] = Info,
        ["\uE8D7"] = Key,
        ["\uE72C"] = Refresh,
        ["\uE8C8"] = Copy,
        ["\uE823"] = Clock,
        ["\uE72E"] = Lock,
        ["\uE787"] = Calendar,
        ["\uE774"] = Globe,
        ["\uE77B"] = Person,
        ["\uEA18"] = Shield,
        ["home"] = House,
        ["\uE838"] = Folder,
        ["\uE74E"] = Save,
        ["\uE9D5"] = ListCheck,
        ["\uE74D"] = Trash,
        ["\uE70D"] = Chevron,
        ["\uE70F"] = Pen,
        ["\uE71D"] = Overview,  // Dienstverzeichnis
        ["\uE756"] = ListCheck, // Ablauf des Abrufs
        ["\uE9E9"] = Sliders,   // Verhalten
        ["\uE90F"] = Sliders,   // Erweitert
        ["\uE777"] = Refresh,   // Vertrauen zuruecksetzen
        ["\uE8F1"] = Bell,      // Abos
    };

    /// <summary>Haus-Symbol fuer die Home-Assistant-Anbindung (kein eigener Fluent-Code).</summary>
    public const string HomeGlyph = "home";
}
