using System.Windows;

namespace RiftPen;

public partial class App : Application
{
    Mutex? mutex;
    TrayIcon? tray;
    bool selfTest;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length >= 2 && e.Args[0] == "--track-test")
        {
            selfTest = true;
            var folder = e.Args[1];
            double at = e.Args.Length >= 3 && double.TryParse(e.Args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 360;
            _ = Dispatcher.InvokeAsync(async () =>
            {
                try { await SelfTest.RunTrackTest(folder, at); }
                catch (Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "tracktest_crash.txt"), ex.ToString()); }
                Shutdown();
            });
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0] == "--board-test-offline")
        {
            selfTest = true;
            var folder = e.Args[1];
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Dispatcher.InvokeAsync(async () =>
            {
                try { await SelfTest.RunBoardTestOffline(folder); }
                catch (Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "boardtest_crash.txt"), ex.ToString()); }
                Shutdown();
            });
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0] == "--board-test")
        {
            selfTest = true;
            var folder = e.Args[1];
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Dispatcher.InvokeAsync(async () =>
            {
                try { await SelfTest.RunBoardTest(folder); }
                catch (Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "boardtest_crash.txt"), ex.ToString()); }
                Shutdown();
            });
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0] == "--game-test")
        {
            selfTest = true;
            var folder = e.Args[1];
            _ = Dispatcher.InvokeAsync(async () =>
            {
                try { await SelfTest.RunGameTest(folder); }
                catch (Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "gametest_crash.txt"), ex.ToString()); }
                Shutdown();
            });
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0] == "--selftest")
        {
            selfTest = true;
            try { SelfTest.Run(e.Args[1]); }
            catch (Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(e.Args[1], "selftest_crash.txt"), ex.ToString()); }
            Shutdown();
            return;
        }

        mutex = new Mutex(true, "RiftPen.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("RiftPen is already running.", "RiftPen");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            AppSettings.LogError(args.Exception);
            args.Handled = true;
        };

        var S = AppState.I;
        S.LoadAutosave();
        S.AutosaveEnabled = true;
        S.SettingsAutosave = true;

        // Shortcuts: a low-level hook, because League blocks normal Windows hotkeys while it's focused.
        S.Hook = new InputHook(Dispatcher);
        S.Hook.ActionTriggered += S.RunAction;
        S.ApplyBindings();
        S.Hook.Start();
        if (!S.Hook.Installed)
            S.HotkeyFailures.Add("Windows refused the keyboard hook, so shortcuts only work while RiftPen is focused.");

        var overlay = new OverlayWindow();
        overlay.Show();

        var toolbar = new ToolbarWindow(overlay) { Owner = overlay };
        toolbar.Show();

        var timeline = new TimelineWindow { Owner = overlay };
        CaptureGuard.Guard(toolbar);
        CaptureGuard.Guard(timeline);
        timeline.OpenNotesRequested += toolbar.OpenNotes;
        overlay.Timeline = timeline;

        S.UiRequest += what =>
        {
            switch (what)
            {
                case "screenshot": overlay.TakeScreenshot(toolbar); break;
                case "toggleToolbar": toolbar.ToggleVisible(); break;
                case "notes": toolbar.OpenNotes(); break;
                case "board": RiftBoardWindow.Open(); break;
                case "ping": overlay.Surface.PingAtCursor(); break;
                case "detectMinimap": overlay.DetectMinimap(automatic: false); break;
                case "minimapPicked": toolbar.OpenSettings(SettingsWindow.ReplayTab); break;
            }
        };

        tray = new TrayIcon(
            toggleToolbar: toolbar.ToggleVisible,
            toggleDraw: () => S.DrawMode = !S.DrawMode,
            openSettings: () => toolbar.OpenSettings(0),
            quit: Shutdown,
            toggleCapture: () => S.RunAction("hideFromCapture", true),
            captureHidden: () => S.Settings.HideUiFromCapture);

        // First run of the real app: add RiftPen to the Start menu (keeps it pointing here if the exe moved).
        if (!e.Args.Contains("--demo") && !StartMenu.IsDevBuild && (!S.Settings.StartMenuShortcutDone || StartMenu.Exists))
        {
            try
            {
                StartMenu.Create();
                S.Settings.StartMenuShortcutDone = true;
            }
            catch (Exception ex) { AppSettings.LogError(ex); }
        }

        if (e.Args.Contains("--demo"))
        {
            S.AutosaveEnabled = false; // test drawings shouldn't overwrite real ones
            SelfTest.AddDemoItems();
        }

        S.Replay.Start();
        RiftSession.I.Start(Dispatcher);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var S = AppState.I;
        tray?.Dispose();
        S.Hook?.Dispose();
        if (!selfTest)
        {
            S.SaveNow();
            S.Settings.Save();
        }
        if (!selfTest) RiftSession.I.Stop();
        S.Replay.Dispose();
        mutex?.Dispose();
        base.OnExit(e);
    }
}
