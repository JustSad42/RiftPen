using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RiftPen;

public partial class ChampionRangesWindow : Window
{
    static readonly Dictionary<string, string> SlotColors = new()
    {
        ["Auto"] = "#FFFFFF", ["Q"] = "#32D7FF", ["W"] = "#34C759", ["E"] = "#FF9500", ["R"] = "#FF3B30",
    };

    readonly AppState S = AppState.I;
    List<ChampionInfo> all = new();
    ChampionInfo? champ;
    readonly List<(CheckBox Box, ChampionRange Range)> checks = new();

    public ChampionRangesWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            try
            {
                all = await DataDragon.GetChampionsAsync();
                ChampList.ItemsSource = all;
                ChampTitle.Text = "Pick a champion";
                Status.Text = $"Patch {await DataDragon.GetVersionAsync()}";
                Search.Focus();
            }
            catch (Exception ex)
            {
                ChampTitle.Text = "Couldn't reach Data Dragon";
                Status.Text = ex.Message;
            }
        };
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = Search.Text.Trim();
        ChampList.ItemsSource = q.Length == 0 ? all : all.Where(c => c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (ChampList.Items.Count == 1) ChampList.SelectedIndex = 0;
    }

    async void ChampList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChampList.SelectedItem is not ChampionInfo c) return;
        champ = c;
        ChampTitle.Text = c.Name;
        RangeChecks.Children.Clear();
        checks.Clear();
        AddBtn.IsEnabled = false;
        try
        {
            var ranges = await DataDragon.GetRangesAsync(c.Id);
            if (champ != c) return; // picked another champion meanwhile
            foreach (var r in ranges)
            {
                var color = (Color)ColorConverter.ConvertFromString(SlotColors.GetValueOrDefault(r.Slot, "#FFFFFF"));
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new Border { Width = 26, Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 8, 0),
                    Child = new TextBlock { Text = r.Slot == "Auto" ? "AA" : r.Slot, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.Black } });
                row.Children.Add(new TextBlock { Text = $"{r.SpellName}  ·  {r.Radius:0}", FontWeight = FontWeights.SemiBold });
                if (r.Detail.Length > 0) row.Children.Add(new TextBlock { Text = $"   ({r.Detail} by rank)", Foreground = Brushes.Gray });
                var box = new CheckBox { Content = row, IsChecked = r.Slot != "Auto" || ranges.Count == 1, Margin = new Thickness(0, 4, 0, 4) };
                RangeChecks.Children.Add(box);
                checks.Add((box, r));
            }
            if (checks.Count == 0) RangeChecks.Children.Add(new TextBlock { Text = "No usable ranges listed for this champion.", Foreground = Brushes.Gray });
            AddBtn.IsEnabled = checks.Count > 0;
        }
        catch (Exception ex)
        {
            Status.Text = "Couldn't load: " + ex.Message;
        }
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        if (champ == null) return;
        int firstIndex = -1, added = 0;
        foreach (var (box, r) in checks)
        {
            if (box.IsChecked != true) continue;
            string name = r.Slot == "Auto" ? $"{champ.Name} auto" : $"{champ.Name} {r.Slot} {r.SpellName}";
            var existing = S.Settings.RangePresets.FirstOrDefault(p => p.Name == name);
            if (existing != null) { existing.Radius = r.Radius; continue; }
            var preset = new RangePreset { Name = name, Radius = r.Radius, Color = SlotColors.GetValueOrDefault(r.Slot, "#FFFFFF") };
            S.Settings.RangePresets.Add(preset);
            if (firstIndex < 0) firstIndex = S.Settings.RangePresets.Count - 1;
            added++;
        }
        S.Settings.EnsureHotkeys();
        S.Settings.Save();
        S.ApplyBindings();
        if (firstIndex >= 0) S.RangeIndex = firstIndex;
        S.NotifyChanged();
        Status.Text = added > 0 ? $"Added {added}. Pick them from the range button." : "Those were already there (sizes updated).";
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
