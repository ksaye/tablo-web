using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace TabloWeb;

/// <summary>
/// Finds ffmpeg, and on Windows fetches it when there is none.
///
/// Linux and Docker installs get ffmpeg from the system package manager (the Dockerfile installs
/// it). A Windows install has no package manager a service can use, and asking people to install
/// ffmpeg by hand was the one manual step left. So when nothing is configured and nothing is on
/// PATH, the Windows service downloads a pinned, known-good build into its own install folder.
///
/// It is downloaded rather than put inside the MSI: the ffmpeg build is GPL-licensed and ~110 MB,
/// and fetching the published archive from its author keeps this project's installer small and
/// its licensing simple. It is done in the background after start-up, not before, because Windows
/// gives a service only ~30 seconds to report that it has started.
///
/// The build is pinned on purpose. ffmpeg 9.0 halves multi-view speed when the panes carry audio
/// (measured on Windows 11: 0.47x with 9.0.1 against 1.05x with 6.1.1, 7.1.1 and 8.1.2, same
/// streams, same command), so "latest" is not safe to follow automatically.
/// </summary>
public static class FfmpegSetup
{
    public const string PinnedVersion = "8.1.2";

    private const string PinnedUrl =
        "https://github.com/GyanD/codexffmpeg/releases/download/8.1.2/ffmpeg-8.1.2-essentials_build.zip";

    // SHA-256 of the archive above, as published by GitHub for the release asset.
    private const string PinnedSha256 = "db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec";

    /// <summary>Where a downloaded ffmpeg lives: an `ffmpeg` folder beside TabloWeb.exe.</summary>
    public static string DownloadedDir => Path.Combine(AppContext.BaseDirectory, "ffmpeg");
    private static string DownloadedExe => Path.Combine(DownloadedDir, "bin", "ffmpeg.exe");

    /// <summary>
    /// Downloading is for Windows installs that have no ffmpeg of their own. An explicit
    /// TABLOWEB_FFMPEG always wins, and TABLOWEB_FFMPEG_DOWNLOAD=0 turns it off.
    /// </summary>
    public static bool DownloadAllowed =>
        OperatingSystem.IsWindows()
        && Environment.GetEnvironmentVariable("TABLOWEB_FFMPEG") is null
        && Environment.GetEnvironmentVariable("TABLOWEB_FFMPEG_DOWNLOAD") is not ("0" or "false");

    /// <summary>
    /// The ffmpeg to use, or null when there is none: TABLOWEB_FFMPEG if set, then one on PATH,
    /// then a previously downloaded one.
    /// </summary>
    public static string? Locate()
    {
        if (Environment.GetEnvironmentVariable("TABLOWEB_FFMPEG") is { Length: > 0 } configured)
            return configured;
        if (Runs("ffmpeg")) return "ffmpeg";
        if (File.Exists(DownloadedExe) && Runs(DownloadedExe)) return DownloadedExe;
        return null;
    }

    /// <summary>The major version of an ffmpeg, or null when it cannot be told (a git build, say).</summary>
    public static int? MajorVersion(string ffmpeg)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg, "-hide_banner -version")
            { RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var first = p.StandardOutput.ReadLine() ?? "";
            p.WaitForExit(10_000);
            // "ffmpeg version 8.1.2-essentials_build-www.gyan.dev Copyright ..."
            var parts = first.Split(' ');
            var i = Array.IndexOf(parts, "version");
            if (i < 0 || i + 1 >= parts.Length) return null;
            var digits = new string(parts[i + 1].TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out var major) ? major : null;
        }
        catch { return null; }
    }

    private static bool Runs(string ffmpeg)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg, "-hide_banner -version")
            { RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            return p.WaitForExit(10_000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Download, verify and unpack the pinned build. Returns the path to ffmpeg.exe. Throws on any
    /// failure, leaving no half-unpacked folder behind.
    /// </summary>
    public static async Task<string> DownloadAsync(ILogger log, CancellationToken ct)
    {
        var root = AppContext.BaseDirectory;
        var zip = Path.Combine(root, "ffmpeg-download.zip");
        var staging = Path.Combine(root, "ffmpeg-staging");

        log.LogInformation("No ffmpeg found; downloading ffmpeg {Version} (about 110 MB) from {Url}",
            PinnedVersion, PinnedUrl);

        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"TabloWeb/{UpdateChecker.CurrentVersion}");
                using var res = await http.GetAsync(PinnedUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();
                await using var source = await res.Content.ReadAsStreamAsync(ct);
                await using var file = File.Create(zip);
                await source.CopyToAsync(file, ct);
            }

            string actual;
            await using (var file = File.OpenRead(zip))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
            if (actual != PinnedSha256)
                throw new InvalidOperationException(
                    $"The downloaded ffmpeg did not match its published checksum (got {actual}); it was discarded.");

            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            ZipFile.ExtractToDirectory(zip, staging);

            // The archive holds a single top-level folder, ffmpeg-<version>-essentials_build.
            var unpacked = Directory.GetDirectories(staging).Single();
            if (Directory.Exists(DownloadedDir)) Directory.Delete(DownloadedDir, recursive: true);
            Directory.Move(unpacked, DownloadedDir);

            if (!Runs(DownloadedExe))
                throw new InvalidOperationException("The downloaded ffmpeg would not run.");

            log.LogInformation("ffmpeg {Version} installed at {Path}", PinnedVersion, DownloadedExe);
            return DownloadedExe;
        }
        finally
        {
            try { if (File.Exists(zip)) File.Delete(zip); } catch { /* best effort */ }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
        }
    }
}
