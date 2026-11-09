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

        S.UiRequest += what =>
        {
            switch (what)
            {
                case "screenshot": overlay.TakeScreenshot(toolbar); break;
                case "toggleToolbar": toolbar.ToggleVisible(); break;
                case "minimapPicked": toolbar.OpenSettings(SettingsWindow.ReplayTab); break;
            }
        };

        tray = new TrayIcon(
            toggleToolbar: toolbar.ToggleVisible,
            toggleDraw: () => S.DrawMode = !S.DrawMode,
            openSettings: () => toolbar.OpenSettings(0),
            quit: Shutdown);

        if (e.Args.Contains("--demo"))
        {
            S.AutosaveEnabled = false; // test drawings shouldn't overwrite real ones
            SelfTest.AddDemoItems();
        }

        S.Replay.Start();
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
        S.Replay.Dispose();
        mutex?.Dispose();
        base.OnExit(e);
    }
}
