using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace TabloWeb;

/// <summary>
/// Polls GitHub for a newer release than the one currently running, and — when the web UI asks
/// for it — downloads the Windows installer and hands it to msiexec. This is notify-and-ask, not
/// silent: nothing here installs anything until a human clicks "Update now".
///
/// Off by default anywhere that isn't the Windows Service install — a Docker deployment updates
/// by pulling a new image, and a Linux `dotnet run` checkout updates with `git pull`. Neither of
/// those wants a background job phoning GitHub on their behalf.
/// </summary>
public sealed class UpdateChecker(IHttpClientFactory factory, ILogger<UpdateChecker> log) : BackgroundService
{
    public static bool Enabled => Environment.GetEnvironmentVariable("TABLOWEB_UPDATE_CHECK") switch
    {
        "1" or "true" => true,
        "0" or "false" => false,
        _ => OperatingSystem.IsWindows()
    };

    public static string Repo =>
        Environment.GetEnvironmentVariable("TABLOWEB_UPDATE_REPO") ?? "ksaye/tablo-web";

    private static TimeSpan Interval => TimeSpan.FromHours(
        double.TryParse(Environment.GetEnvironmentVariable("TABLOWEB_UPDATE_CHECK_HOURS"), out var h) && h > 0
            ? h : 24);

    /// <summary>The version baked in at build time (see TabloWeb.csproj's &lt;Version&gt;).</summary>
    public static readonly Version CurrentVersion = Normalize(
        typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0));

    public UpdateState State { get; } = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            log.LogInformation(
                "Update checks are off (set TABLOWEB_UPDATE_CHECK=1 to turn them on; they default " +
                "on for the Windows install and off everywhere else).");
            return;
        }

        // Let the app finish warming up before spending a call on GitHub.
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckOnceAsync(stoppingToken);
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task CheckOnceAsync(CancellationToken ct)
    {
        try
        {
            var client = factory.CreateClient("github");
            var release = await client.GetFromJsonAsync<GitHubRelease>(
                $"https://api.github.com/repos/{Repo}/releases/latest", ct);

            var tag = release?.TagName?.TrimStart('v', 'V');
            if (tag is null || !Version.TryParse(tag, out var latestRaw))
            {
                State.Error = "Could not read the latest release from GitHub.";
                return;
            }

            var latest = Normalize(latestRaw);
            var asset = release!.Assets?.FirstOrDefault(a =>
                a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));

            State.LatestVersion = latest;
            State.ReleaseUrl = release.HtmlUrl;
            State.AssetName = asset?.Name;
            State.AssetUrl = asset?.BrowserDownloadUrl;
            State.AssetSize = asset?.Size ?? 0;
            State.CheckedUtc = DateTime.UtcNow;
            State.Error = null;
            State.Available = latest > CurrentVersion && asset is not null;

            log.LogInformation("Update check: running {Current}, latest on GitHub is {Latest}{Note}",
                CurrentVersion, latest, State.Available ? " (update available)" : "");
        }
        catch (Exception ex)
        {
            State.Error = ex.Message;
            log.LogWarning(ex, "Update check against GitHub failed");
        }
    }

    /// <summary>
    /// Downloads the MSI and launches msiexec for a silent, unattended install. Deliberately not
    /// awaited by the caller: the MSI's own ServiceControl stops TabloWeb, replaces the files and
    /// starts it again, which normally kills the very process running this method partway
    /// through — that is success, not a bug, and there is no response left to send by then.
    /// </summary>
    public async Task InstallAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("The installer only exists for Windows.");
        if (State.AssetUrl is null)
            throw new InvalidOperationException("No installer is available to download yet.");

        State.Installing = true;
        State.InstallError = null;
        try
        {
            var client = factory.CreateClient("github");
            var bytes = await client.GetByteArrayAsync(State.AssetUrl, ct);

            var dir = Path.Combine(Path.GetTempPath(), "TabloWebUpdate");
            Directory.CreateDirectory(dir);
            var msiPath = Path.Combine(dir, State.AssetName ?? "TabloWeb-update.msi");
            await File.WriteAllBytesAsync(msiPath, bytes, ct);

            var psi = new ProcessStartInfo("msiexec.exe") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("/i");
            psi.ArgumentList.Add(msiPath);
            psi.ArgumentList.Add("/qn");
            psi.ArgumentList.Add("/norestart");
            psi.ArgumentList.Add("/l*v");
            psi.ArgumentList.Add(Path.Combine(dir, "install.log"));

            log.LogWarning("Launching msiexec to install {Path} — the service will restart shortly", msiPath);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            State.Installing = false;
            State.InstallError = ex.Message;
            log.LogWarning(ex, "Could not start the update install");
            throw;
        }
    }

    /// <summary>
    /// .NET pads a missing Build/Revision with -1, not 0, so `Version.Parse("1.2")` sorts
    /// *below* `1.2.0.0` instead of equal to it. Every version compared here is a release tag or
    /// an assembly version, never anything with a real use for that distinction, so flatten both
    /// to Major.Minor.Build before comparing.
    /// </summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}

public sealed class UpdateState
{
    public Version? LatestVersion { get; set; }
    public string? ReleaseUrl { get; set; }
    public string? AssetName { get; set; }
    public string? AssetUrl { get; set; }
    public long AssetSize { get; set; }
    public DateTime? CheckedUtc { get; set; }
    public bool Available { get; set; }
    public string? Error { get; set; }
    public bool Installing { get; set; }
    public string? InstallError { get; set; }
}

file sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
}

file sealed class GitHubAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}
