using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TILageMonitor;

/// <summary>Konnektor-Verlauf (14 Tage) im Konnektor-Fenster.</summary>
public partial class ConnectorWindow
{
    private DateTime? _historySelectedDay;

    private static string LaneLabel(ConnectorHistoryLane lane) => lane switch
    {
        ConnectorHistoryLane.Reachable => L("Konnektor erreichbar", "Connector reachable"),
        ConnectorHistoryLane.VpnTi => L("VPN TI", "VPN TI"),
        ConnectorHistoryLane.VpnSis => L("VPN SIS", "VPN SIS"),
        ConnectorHistoryLane.SmcB => L("SMC-B gesteckt", "SMC-B inserted"),
        ConnectorHistoryLane.Hba => L("HBA gesteckt", "HBA inserted"),
        _ => lane.ToString()
    };

    /// <summary>
    /// Farbe je Zustand. Beim HBA ist "nicht gesteckt" kein Fehler (viele Praxen stecken ihn
    /// nur zum Signieren) – deshalb neutral statt rot.
    /// </summary>
    private static string StateBrushKey(ConnectorHistoryLane lane, ConnectorHistoryState state) => state switch
    {
        ConnectorHistoryState.Ok => "StatusOk",
        ConnectorHistoryState.Partial => lane == ConnectorHistoryLane.Hba ? "StatusMaintenance" : "StatusPartial",
        ConnectorHistoryState.Down => lane == ConnectorHistoryLane.Hba ? "ChipBackground" : "StatusOutage",
        _ => "StatusEmpty"
    };

    private static string StateText(ConnectorHistoryLane lane, ConnectorHistoryCell cell)
    {
        var cardLane = lane is ConnectorHistoryLane.SmcB or ConnectorHistoryLane.Hba;
        var text = cell.State switch
        {
            ConnectorHistoryState.Ok => cardLane ? L("gesteckt", "inserted") : lane == ConnectorHistoryLane.Reachable ? L("erreichbar", "reachable") : L("online", "online"),
            ConnectorHistoryState.Partial => cardLane ? L("zeitweise gesteckt", "partly inserted") : L("zeitweise gestört", "partly disrupted"),
            ConnectorHistoryState.Down => cardLane ? L("nicht gesteckt", "not inserted") : lane == ConnectorHistoryLane.Reachable ? L("nicht erreichbar", "unreachable") : L("offline", "offline"),
            _ => L("keine Daten", "no data")
        };
        return cell.Percent is { } pct && cell.State == ConnectorHistoryState.Partial ? $"{text} ({pct} %)" : text;
    }

    private void RenderHistory()
    {
        if (HistoryGrid is null)
            return;

        var file = ConnectorHistoryStore.Load();
        var today = DateTime.Now.Date;
        var days = ConnectorHistoryTimeline.Build(file, today);
        var any = days.Any(d => d.CoveredHours > 0);
        HistoryEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;

        var covered = days.Sum(d => d.CoveredHours);
        var problems = days.Sum(d => d.ProblemHours);
        HistorySummary.Text = any
            ? L($"{covered} Stunden erfasst · {problems} Stunden mit Störung (nicht erreichbar oder VPN TI getrennt)",
                $"{covered} hours recorded · {problems} hours with disruption (unreachable or VPN TI down)")
            : "";

        HistoryGrid.Children.Clear();
        HistoryGrid.RowDefinitions.Clear();
        HistoryGrid.ColumnDefinitions.Clear();
        HistoryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        foreach (var _ in days)
            HistoryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

        // Kopfzeile: Wochentag + Tag
        HistoryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < days.Count; i++)
        {
            var label = new TextBlock
            {
                Text = days[i].Date.ToString(LocalizationService.IsGerman ? "dd.MM." : "MM/dd", LocalizationService.DisplayCulture),
                FontSize = 10,
                Foreground = Brush("TextMuted"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            };
            Grid.SetRow(label, 0);
            Grid.SetColumn(label, i + 1);
            HistoryGrid.Children.Add(label);
        }

        var lanes = ConnectorHistoryTimeline.Lanes;
        for (var r = 0; r < lanes.Length; r++)
        {
            var lane = lanes[r];
            HistoryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock
            {
                Text = LaneLabel(lane),
                FontSize = 12,
                Foreground = Brush("TextMain"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 3, 10, 3)
            };
            Grid.SetRow(name, r + 1);
            Grid.SetColumn(name, 0);
            HistoryGrid.Children.Add(name);

            for (var i = 0; i < days.Count; i++)
            {
                var day = days[i];
                var cell = day.Summary[lane];
                var selected = _historySelectedDay == day.Date;
                var tile = new Border
                {
                    Width = 26,
                    Height = 22,
                    CornerRadius = new CornerRadius(5),
                    Background = Brush(StateBrushKey(lane, cell.State)),
                    BorderBrush = selected ? Brush("TextMain") : Brush("BorderSubtle"),
                    BorderThickness = new Thickness(selected ? 2 : 1),
                    Margin = new Thickness(4, 3, 4, 3),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = $"{day.Date.ToString("D", LocalizationService.DisplayCulture)}\n{LaneLabel(lane)}: {StateText(lane, cell)}",
                    Tag = day.Date
                };
                tile.MouseLeftButtonUp += HistoryTile_Click;
                Grid.SetRow(tile, r + 1);
                Grid.SetColumn(tile, i + 1);
                HistoryGrid.Children.Add(tile);
            }
        }

        HistoryLegend.Children.Clear();
        void Legend(string key, string text)
        {
            var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 4) };
            panel.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = Brush(key), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = Brush("TextMuted"), VerticalAlignment = VerticalAlignment.Center });
            HistoryLegend.Children.Add(panel);
        }
        Legend("StatusOk", L("in Ordnung / gesteckt", "OK / inserted"));
        Legend("StatusPartial", L("zeitweise", "partly"));
        Legend("StatusOutage", L("gestört / nicht gesteckt", "disrupted / not inserted"));
        Legend("ChipBackground", L("HBA nicht gesteckt", "HBA not inserted"));
        Legend("StatusEmpty", L("keine Daten", "no data"));

        var selectedDay = days.FirstOrDefault(d => d.Date == _historySelectedDay) ?? days.LastOrDefault();
        RenderHistoryHours(selectedDay);
    }

    private void HistoryTile_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DateTime date })
        {
            _historySelectedDay = date;
            RenderHistory();
        }
    }

    private void RenderHistoryHours(ConnectorHistoryDay? day)
    {
        HistoryHoursGrid.Children.Clear();
        HistoryHoursGrid.RowDefinitions.Clear();
        HistoryHoursGrid.ColumnDefinitions.Clear();
        if (day is null)
        {
            HistoryDayTitle.Text = L("Stunden", "Hours");
            return;
        }

        HistoryDayTitle.Text = L($"Stunden am {day.Date.ToString("dddd, dd.MM.yyyy", LocalizationService.DisplayCulture)}",
                                 $"Hours on {day.Date.ToString("dddd, yyyy-MM-dd", LocalizationService.DisplayCulture)}");

        HistoryHoursGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        for (var h = 0; h < 24; h++)
            HistoryHoursGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });

        HistoryHoursGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var h = 0; h < 24; h += 3)
        {
            var label = new TextBlock { Text = h.ToString("00"), FontSize = 10, Foreground = Brush("TextMuted"), Margin = new Thickness(2, 0, 0, 4) };
            Grid.SetRow(label, 0);
            Grid.SetColumn(label, h + 1);
            Grid.SetColumnSpan(label, 3);
            HistoryHoursGrid.Children.Add(label);
        }

        var lanes = ConnectorHistoryTimeline.Lanes;
        for (var r = 0; r < lanes.Length; r++)
        {
            var lane = lanes[r];
            HistoryHoursGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = LaneLabel(lane), FontSize = 12, Foreground = Brush("TextMain"), Margin = new Thickness(0, 2, 10, 2), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(name, r + 1);
            HistoryHoursGrid.Children.Add(name);
            for (var h = 0; h < 24; h++)
            {
                var cell = day.Hours[lane][h];
                var block = new Border
                {
                    Height = 16,
                    CornerRadius = new CornerRadius(3),
                    Background = Brush(StateBrushKey(lane, cell.State)),
                    Margin = new Thickness(1, 2, 1, 2),
                    ToolTip = $"{h:00}:00–{h:00}:59 · {LaneLabel(lane)}: {StateText(lane, cell)}"
                };
                Grid.SetRow(block, r + 1);
                Grid.SetColumn(block, h + 1);
                HistoryHoursGrid.Children.Add(block);
            }
        }
    }
}
