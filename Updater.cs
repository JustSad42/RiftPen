using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace RiftPen;

/// <summary>
/// Keeps RiftPen up to date from GitHub releases (github.com/JustSad42/RiftPen). A release carries RiftPen.exe and
/// RiftPen.exe.sha256. A newer version is downloaded next to the running exe and checked against its SHA-256; it is
/// swapped in when RiftPen quits, or straight away with "Restart to update".
/// </summary>
public static class Updater
{
    public const string Repo = "JustSad42/RiftPen";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    static Updater()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("RiftPen-updater");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static Version Current { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Math.Max(0, Current.Build)}";

    /// <summary>Set when a verified new version is downloaded and waiting to be swapped in.</summary>
    public static Version? Ready { get; private set; }
    /// <summary>What's new in the version that's ready (the release notes).</summary>
    public static string ReadyNotes { get; private set; } = "";
    public static string Status { get; private set; } = "";
    public static event Action? StatusChanged;

    static string ExePath => Environment.ProcessPath ?? "";
    static string NewPath => ExePath + ".new";
    static string OldPath => ExePath + ".old";
    static bool busy;

    /// <summary>Dev builds (run from bin\) never update themselves.</summary>
    public static bool CanSelfUpdate =>
        ExePath.EndsWith("RiftPen.exe", StringComparison.OrdinalIgnoreCase) && !StartMenu.IsDevBuild;

    static void Set(string status) { Status = status; StatusChanged?.Invoke(); }

    static System.Windows.Threading.DispatcherTimer? timer;

    /// <summary>Checks 30 seconds after start-up, then every 6 hours, while automatic updates are on.</summary>
    public static void StartAutomatic()
    {
        CleanUp();
        timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += async (_, _) =>
        {
            timer.Interval = TimeSpan.FromHours(6);
            if (AppState.I.Settings.AutoUpdate) await CheckAsync();
        };
        timer.Start();
    }

    /// <summary>Looks for a newer release and downloads it. Safe to call often; does nothing while busy.</summary>
    public static async Task CheckAsync(bool manual = false)
    {
        if (busy) return;
        if (Ready != null) { Set($"Version {Ready.ToString(3)} is ready: it installs when you quit RiftPen, or use Restart to update."); return; }
        busy = true;
        try
        {
            Set("Checking for updates…");
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) { Set("Couldn't read the latest version."); return; }
            latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
            var current = new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build));
            if (latest <= current) { Set($"You have the latest version ({CurrentText})."); return; }

            string? exeUrl = null, shaUrl = null, digest = null;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString();
                var url = a.GetProperty("browser_download_url").GetString();
                if (name == "RiftPen.exe")
                {
                    exeUrl = url;
                    if (a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String) digest = d.GetString();
                }
                else if (name == "RiftPen.exe.sha256") shaUrl = url;
            }
            if (exeUrl == null) { Set($"Version {latest.ToString(3)} is out, but its download isn't ready yet."); return; }
            if (!CanSelfUpdate) { Set($"Version {latest.ToString(3)} is available on GitHub (this copy can't update itself)."); return; }

            // Expected hash: the .sha256 file, else GitHub's own digest. Never install an unverified file.
            string? expected = null;
            if (shaUrl != null) expected = (await Http.GetStringAsync(shaUrl)).Trim().Split(' ', '\t', '\n')[0];
            else if (digest?.StartsWith("sha256:") == true) expected = digest[7..];
            if (string.IsNullOrEmpty(expected)) { Set($"Version {latest.ToString(3)} has no checksum, so it wasn't installed."); return; }

            Set($"Downloading version {latest.ToString(3)}…");
            var bytes = await Http.GetByteArrayAsync(exeUrl);
            var actual = Convert.ToHexString(SHA256.HashData(bytes));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                Set("The download didn't match its checksum, so it wasn't installed.");
                return;
            }
            await File.WriteAllBytesAsync(NewPath, bytes);
            Ready = latest;
            ReadyNotes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            Set($"Version {latest.ToString(3)} is ready: it installs when you quit RiftPen, or use Restart to update.");
            AppState.I.Say($"RiftPen {latest.ToString(3)} downloaded. It installs when you quit, or right-click the tray icon → Restart to update.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Set("No releases published yet.");
        }
        catch (Exception ex)
        {
            Set(manual ? "Couldn't check for updates: " + ex.Message : "Couldn't check for updates.");
        }
        finally { busy = false; }
    }

    /// <summary>Puts the downloaded version in place of the running exe (Windows allows renaming a running exe).</summary>
    static bool Swap()
    {
        if (Ready == null || !File.Exists(NewPath)) return false;
        try
        {
            if (File.Exists(OldPath)) File.Delete(OldPath);
            File.Move(ExePath, OldPath);
            File.Move(NewPath, ExePath);
            return true;
        }
        catch (Exception ex)
        {
            try { if (!File.Exists(ExePath) && File.Exists(OldPath)) File.Move(OldPath, ExePath); } catch { }
            AppSettings.LogError(ex);
            Set("Couldn't install the update (is the folder read-only?).");
            Ready = null;
            return false;
        }
    }

    /// <summary>Called when RiftPen quits: the next start runs the new version.</summary>
    public static void ApplyOnExit()
    {
        if (Ready != null) Swap();
    }

    /// <summary>Swaps in the downloaded version and restarts. Returns false if it couldn't.</summary>
    public static bool ApplyAndRestart()
    {
        if (!Swap()) return false;
        Ready = null; // already in place; don't swap again on exit
        Process.Start(new ProcessStartInfo(ExePath, $"--updated --wait-for {Environment.ProcessId}") { UseShellExecute = false });
        System.Windows.Application.Current.Shutdown();
        return true;
    }

    /// <summary>Removes the previous version left behind by an update (it may still be closing).</summary>
    public static void CleanUp()
    {
        Task.Run(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    if (File.Exists(OldPath)) File.Delete(OldPath);
                    if (File.Exists(NewPath) && Ready == null) File.Delete(NewPath);
                    return;
                }
                catch { Thread.Sleep(500); }
            }
        });
    }

    /// <summary>`--wait-for &lt;pid&gt;`: the copy that just updated waits for the old one to close first.</summary>
    public static void WaitForPrevious(string[] args)
    {
        int i = Array.IndexOf(args, "--wait-for");
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out var pid)) return;
        try { using var p = Process.GetProcessById(pid); p.WaitForExit(15000); }
        catch { /* already gone */ }
    }
}
