using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace RiftPen;

public partial class SettingsWindow : Window
{
    public const int ShortcutsTab = 0, RangesTab = 1, ReplayTab = 2, GeneralTab = 3;

    static readonly (HotkeyScope Scope, string Label)[] ScopeChoices =
    {
        (HotkeyScope.Drawing, "While drawing"),
        (HotkeyScope.League, "In League"),
        (HotkeyScope.Everywhere, "Everywhere"),
    };

    readonly AppState S = AppState.I;
    readonly OverlayWindow overlay;
    readonly ToolbarWindow toolbar;
    readonly DispatcherTimer timer;
    string? capturingId;

    public SettingsWindow(OverlayWindow overlay, ToolbarWindow toolbar)
    {
        this.overlay = overlay;
        this.toolbar = toolbar;
        InitializeComponent();
        DataContext = S.Settings;

        AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((_, _) => Changed()));
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => Changed()));

        if (S.HotkeyFailures.Count > 0) HotkeyWarn.Text = string.Join("\n", S.HotkeyFailures);
        StartupBox.IsChecked = Startup.IsEnabled;
        BuildShortcuts();

        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) => UpdateLive();
        timer.Start();
        Closed += (_, _) =>
        {
            timer.Stop();
            S.Hook?.CancelCapture();
            S.Settings.Save();
        };
        UpdateLive();
    }

    public void SelectTab(int index) => Tabs.SelectedIndex = index;

    void Changed() => Dispatcher.BeginInvoke(() => S.NotifyChanged(), DispatcherPriority.Background);

    void RefreshBindings()
    {
        DataContext = null;
        DataContext = S.Settings;
    }

    void ShortcutsChanged()
    {
        S.Settings.Save();
        S.ApplyBindings();
        S.NotifyChanged();
        toolbar.BuildRangeList();
        BuildShortcuts();
    }

    // ---------------------------------------------------------------- shortcuts

    void BuildShortcuts()
    {
        ShortcutList.Children.Clear();
        string? group = null;
        foreach (var action in HotkeyCatalog.All(S.Settings))
        {
            if (action.Group != group)
            {
                group = action.Group;
                ShortcutList.Children.Add(new TextBlock { Text = group, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
            }

            var binding = S.Settings.Hotkeys[action.Id];
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });

            row.Children.Add(new TextBlock { Text = action.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

            bool capturing = capturingId == action.Id;
            var button = new Button
            {
                Content = capturing ? "Press a key…" : binding.Display,
                Padding = new Thickness(8, 3, 8, 3),
                FontWeight = capturing || binding.IsSet ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = binding.IsSet || capturing ? Brushes.Black : Brushes.Gray,
                Background = capturing ? new SolidColorBrush(Color.FromRgb(0xCF, 0xE3, 0xFF)) : null,
                ToolTip = "Click, then press the new shortcut",
            };
            string id = action.Id;
            button.Click += (_, _) => StartCapture(id, action.Name);
            Grid.SetColumn(button, 1);
            row.Children.Add(button);

            var scope = new ComboBox
            {
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ItemsSource = ScopeChoices.Select(c => c.Label).ToList(),
                SelectedIndex = Array.FindIndex(ScopeChoices, c => c.Scope == binding.EffectiveScope),
                ToolTip = "When this shortcut works",
            };
            scope.SelectionChanged += (_, _) =>
            {
                if (scope.SelectedIndex < 0) return;
                binding.SetScope(ScopeChoices[scope.SelectedIndex].Scope);
                S.Settings.Save();
                S.ApplyBindings();
                S.NotifyChanged();
            };
            Grid.SetColumn(scope, 2);
            row.Children.Add(scope);

            ShortcutList.Children.Add(row);
        }
    }

    void StartCapture(string id, string name)
    {
        if (S.Hook == null) return;
        capturingId = id;
        CaptureNote.Text = $"Press the new shortcut for \"{name}\". Esc cancels, Backspace removes it.";
        BuildShortcuts();
        S.Hook.BeginCapture(OnCaptured);
    }

    void OnCaptured(HotkeyBinding combo)
    {
        var id = capturingId;
        capturingId = null;
        CaptureNote.Text = "";
        if (id == null) { BuildShortcuts(); return; }

        var current = S.Settings.Hotkeys[id];
        bool plain = !combo.Ctrl && !combo.Alt && !combo.Shift && !combo.Win;
        if (plain && combo.Vk == 0x1B) { BuildShortcuts(); return; } // Esc: cancel
        if (plain && combo.Vk == 0x08) { current.Vk = 0; ShortcutsChanged(); return; } // Backspace: remove

        var removedFrom = new List<string>();
        var names = HotkeyCatalog.All(S.Settings).ToDictionary(a => a.Id, a => a.Name);
        foreach (var (otherId, other) in S.Settings.Hotkeys)
        {
            if (otherId == id || !other.IsSet || !other.SameCombo(combo)) continue;
            other.Vk = 0;
            if (names.TryGetValue(otherId, out var n)) removedFrom.Add(n);
        }

        current.Vk = combo.Vk;
        current.Ctrl = combo.Ctrl;
        current.Alt = combo.Alt;
        current.Shift = combo.Shift;
        current.Win = combo.Win;
        // A mouse button is only useful if it works before drawing starts.
        if (combo.Vk is 4 or 5 or 6 && current.EffectiveScope == HotkeyScope.Drawing) current.SetScope(HotkeyScope.League);

        ShortcutsChanged();
        if (removedFrom.Count > 0)
            CaptureNote.Text = $"{combo.Display} was moved here from: {string.Join(", ", removedFrom)}.";
    }

    void ResetShortcuts_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Put every shortcut back to its default?", "RiftPen", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        S.Settings.Hotkeys.Clear();
        S.Settings.EnsureHotkeys();
        ShortcutsChanged();
    }

    // ---------------------------------------------------------------- range circles

    void ChampionRanges_Click(object sender, RoutedEventArgs e) => toolbar.OpenChampionRanges();

    void RangeGrid_RowEditEnding(object? sender, DataGridRowEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var p in S.Settings.RangePresets)
            {
                p.Radius = Math.Clamp(p.Radius, 10, 20000);
                if (string.IsNullOrWhiteSpace(p.Name)) p.Name = "Range";
                try { ColorConverter.ConvertFromString(p.Color); } catch { p.Color = "#FFFFFF"; }
            }
            try { RangeGrid.Items.Refresh(); } catch { }
            ShortcutsChanged();
        }, DispatcherPriority.Background);
    }

    void AddRange_Click(object sender, RoutedEventArgs e)
    {
        var p = new RangePreset { Name = "New range", Radius = 500, Color = "#FFFFFF" };
        S.Settings.RangePresets.Add(p);
        ShortcutsChanged();
        RangeGrid.SelectedItem = p;
        RangeGrid.ScrollIntoView(p);
        RangeGrid.CurrentCell = new DataGridCellInfo(p, RangeGrid.Columns[1]);
        RangeGrid.BeginEdit();
    }

    void RemoveRange_Click(object sender, RoutedEventArgs e)
    {
        if (RangeGrid.SelectedItem is not RangePreset p) return;
        S.Settings.RangePresets.Remove(p);
        S.Settings.Hotkeys.Remove("range:" + p.Id);
        S.RangeIndex = Math.Min(S.RangeIndex, Math.Max(0, S.Settings.RangePresets.Count - 1));
        ShortcutsChanged();
    }

    void MoveRangeUp_Click(object sender, RoutedEventArgs e)
    {
        if (RangeGrid.SelectedItem is not RangePreset p) return;
        int i = S.Settings.RangePresets.IndexOf(p);
        if (i <= 0) return;
        S.Settings.RangePresets.Move(i, i - 1);
        RangeGrid.SelectedItem = p;
        ShortcutsChanged();
    }

    void ResetRanges_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Replace your range circles with the defaults?", "RiftPen", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        foreach (var p in S.Settings.RangePresets) S.Settings.Hotkeys.Remove("range:" + p.Id);
        S.Settings.RangePresets.Clear();
        foreach (var p in AppSettings.DefaultRanges()) S.Settings.RangePresets.Add(p);
        S.RangeIndex = 0;
        ShortcutsChanged();
    }

    // ---------------------------------------------------------------- replay

    void UpdateLive()
    {
        var c = S.Replay.Latest;
        var pb = S.Replay.Playback;
        StatusText.Text = c != null
            ? $"✔ Connected to the Replay API ({S.Replay.PollsPerSecond:0} camera updates per second)" +
              (pb != null && S.ReplayTime is double t ? $"  ·  replay at {AppState.FormatTime(t)} / {AppState.FormatTime(pb.Length)}" : "")
            : "✖ " + S.Replay.LastError;
        StatusText.Foreground = c != null ? Brushes.ForestGreen : Brushes.Firebrick;
        CamText.Text = c == null
            ? "(no camera: open a replay)"
            : $"mode {c.Mode}\n" +
              $"pos  x {c.Position.X,8:0}  y {c.Position.Y,8:0}  z {c.Position.Z,8:0}\n" +
              $"rot  x {c.RotX,8:0.0}  y {c.RotY,8:0.0}  z {c.RotZ,8:0.0}\n" +
              $"fov  {c.Fov:0.0}";
        MonitorText.Text = overlay.MonitorDescription;
        DurationText.Text = S.Settings.NoteDurationSec > 0 ? $"{S.Settings.NoteDurationSec:0} seconds" : "until the end";
        MinimapStatus.Text = S.Settings.MinimapRect is { Length: 4 } ? "✔ Minimap area set" : "Not set yet: open a replay first, then click this";
    }

    void DeleteThisReplay_Click(object sender, RoutedEventArgs e)
    {
        var key = S.ReplayKey;
        if (key == null) { MessageBox.Show(this, "Open the replay first.", "RiftPen"); return; }
        int n = S.DeleteReplayNotes(key);
        MessageBox.Show(this, $"Deleted {n} note{(n == 1 ? "" : "s")} for this replay. Undo brings them back.", "RiftPen");
    }

    void DeleteAllReplays_Click(object sender, RoutedEventArgs e)
    {
        int count = S.Items.Count(i => i.ReplayKey != null);
        if (count == 0) { MessageBox.Show(this, "There are no saved replay notes.", "RiftPen"); return; }
        if (MessageBox.Show(this, $"Delete all {count} replay notes across every replay?", "RiftPen", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        S.DeleteReplayNotes(null);
    }

    void PickMinimap_Click(object sender, RoutedEventArgs e)
    {
        Hide(); // comes back when picking is done
        S.StartMinimapPick();
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "game.cfg|game.cfg|All files|*.*", FileName = "game.cfg" };
        try { dlg.InitialDirectory = Path.GetDirectoryName(S.Settings.GameCfgPath); } catch { }
        if (dlg.ShowDialog(this) == true)
        {
            S.Settings.GameCfgPath = dlg.FileName;
            RefreshBindings();
        }
    }

    void EnableReplayApi_Click(object sender, RoutedEventArgs e)
    {
        var path = S.Settings.GameCfgPath;
        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"Couldn't find:\n{path}\n\nUse Browse… to pick League's Config\\game.cfg.", "RiftPen");
            return;
        }
        if (Process.GetProcessesByName("League of Legends").Length > 0)
        {
            MessageBox.Show(this, "Close the League game or replay window first. It rewrites game.cfg when it closes.\n(The League client can stay open.)", "RiftPen");
            return;
        }

        var lines = File.ReadAllLines(path).ToList();
        int idx = lines.FindIndex(l => l.TrimStart().StartsWith("EnableReplayApi", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && lines[idx].Replace(" ", "").Equals("EnableReplayApi=1", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "The Replay API is already on. Open a replay and the dot on the toolbar turns green.", "RiftPen");
            return;
        }

        if (idx >= 0) lines[idx] = "EnableReplayApi=1";
        else
        {
            int g = lines.FindIndex(l => l.Trim().Equals("[General]", StringComparison.OrdinalIgnoreCase));
            if (g < 0) { lines.Insert(0, "[General]"); g = 0; }
            lines.Insert(g + 1, "EnableReplayApi=1");
        }

        try
        {
            File.Copy(path, path + ".riftpen.bak", overwrite: true);
            File.WriteAllLines(path, lines);
            MessageBox.Show(this, "Done. The Replay API is on.\nA backup was saved as game.cfg.riftpen.bak.\n\nNow open a replay from the League client.", "RiftPen");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't write game.cfg:\n" + ex.Message, "RiftPen");
        }
    }

    void AutoCal_Click(object sender, RoutedEventArgs e)
    {
        var c = S.Replay.Latest;
        if (c == null)
        {
            MessageBox.Show(this, "Open a replay first. The camera readout above needs to show numbers.", "RiftPen");
            return;
        }
        var cal = S.Settings.Calibration;

        // Heuristic: in the default top-down view, yaw is a multiple of 90° and pitch is a steep downward angle.
        static bool NearRight(double a) { double m = Math.Abs(a) % 90; return m < 5 || m > 85; }
        static bool Steep(double a) => Math.Abs(a) is > 15 and < 85;
        cal.SwapYawPitch = Steep(c.RotX) && NearRight(c.RotY) && !Steep(c.RotY);

        double yawRaw = cal.SwapYawPitch ? c.RotY : c.RotX;
        double pitchRaw = cal.SwapYawPitch ? c.RotX : c.RotY;
        cal.InvertPitch = pitchRaw < 0;
        double yaw = cal.InvertYaw ? -yawRaw : yawRaw;
        cal.YawOffset = Math.Round(Normalize(-yaw), 1);

        S.Settings.ShowGrid = true;
        RefreshBindings();
        S.NotifyChanged();
        MessageBox.Show(this,
            $"Calibrated (yaw offset {cal.YawOffset:0.0}°, pitch {(cal.InvertPitch ? "inverted" : "normal")}{(cal.SwapYawPitch ? ", yaw/pitch swapped" : "")}).\n\n" +
            "The test grid is on now. Pan and zoom the camera: the grid should stay glued to the map. The blue fountain circle should sit on the blue fountain.",
            "RiftPen");
    }

    static double Normalize(double a) => ((a + 180) % 360 + 360) % 360 - 180;

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        S.Settings.Calibration = new CalibrationSettings();
        RefreshBindings();
        S.NotifyChanged();
    }

    // ---------------------------------------------------------------- general

    void Startup_Click(object sender, RoutedEventArgs e)
    {
        try { Startup.Set(StartupBox.IsChecked == true); }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't change startup: " + ex.Message, "RiftPen"); }
        StartupBox.IsChecked = Startup.IsEnabled;
    }

    void BrowseShots_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Where should screenshots go?" };
        try { if (Directory.Exists(S.Settings.ScreenshotFolder)) dlg.InitialDirectory = S.Settings.ScreenshotFolder; } catch { }
        if (dlg.ShowDialog(this) == true)
        {
            S.Settings.ScreenshotFolder = dlg.FolderName;
            RefreshBindings();
        }
    }

    void OpenShots_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(S.Settings.ScreenshotFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{S.Settings.ScreenshotFolder}\"") { UseShellExecute = true });
    }

    void NextMonitor_Click(object sender, RoutedEventArgs e)
    {
        overlay.NextMonitor();
        UpdateLive();
    }
}
