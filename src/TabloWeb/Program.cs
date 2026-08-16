using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using TabloWeb;

// ---------------------------------------------------------------------------------------------
// Tablo web — a browser front end for the Tablo 4th-generation DVR.
//
// Shows the 14-day guide, everything recorded, and what is on live, and plays any of it in the
// browser by transcoding through ffmpeg (see StreamManager for why that is unavoidable).
//
// You sign in with the Tablo account itself (see Login.cs); that same sign-in is what connects
// the server to the DVR. The gate covers the page, the artwork and the video segments alike.
// ---------------------------------------------------------------------------------------------

LoadDotEnv();

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("TABLOWEB_URLS") ?? "http://0.0.0.0:8787");
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "yyyy-MM-dd HH:mm:ss "; });
// Every open tab polls status, and each poll is four framework log lines. Keep the journal to
// our own messages and anything that actually went wrong.
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
// One guide load is hundreds of device calls, and HttpClient narrates every one of them at Info.
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);
// Data protection warns on every start that the keys are not themselves encrypted. On Linux
// there is nothing to encrypt them with; the config directory is kept owner-only instead.
builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error);

builder.Services.AddSingleton<TabloSession>();
builder.Services.AddSingleton<StreamManager>();
builder.Services.AddHostedService<Warmer>();
builder.Services.AddHttpClient("device").ConfigurePrimaryHttpMessageHandler(
    // Snapshot images come from a raw LAN IP; a system proxy would refuse to route it.
    () => new SocketsHttpHandler { UseProxy = false, Proxy = null });
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.AddTabloLogin();

var app = builder.Build();
app.UseForwardedHeaders();
app.UseTabloLogin();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapLoginEndpoints();

CredentialStore.Use(app.Services.GetRequiredService<IDataProtectionProvider>());

var tablo = app.Services.GetRequiredService<TabloSession>();
var streams = app.Services.GetRequiredService<StreamManager>();

// Credentials from the environment or from a previous sign-in, so a restart reconnects and
// starts warming the guide without waiting for a browser.
tablo.Restore();

// ------------------------------------------------------------------------------------ status

app.MapGet("/api/status", async (CancellationToken ct) =>
{
    // Storage is a device call, so a box that has gone away must not make the status endpoint
    // hang or throw — the UI relies on this to explain that very situation.
    StorageBox? storage = null;
    if (tablo.Connected)
    {
        try { storage = await tablo.StorageAsync(ct); } catch { /* reported as zero below */ }
    }

    return new StatusDto(
        tablo.Connected, tablo.State,
        tablo.Device?.Name, tablo.Device?.Host,
        tablo.ServerInfo?.Model.Name, tablo.ServerInfo?.Version,
        tablo.ServerInfo?.Model.Tuners ?? 0,
        tablo.GuideReady, tablo.GuideProgress,
        storage?.Info?.TotalBytes ?? 0, storage?.Info?.FreeBytes ?? 0,
        streams.Sessions.Count,
        tablo.NeedsCredentials);
});

// ---------------------------------------------------------------------------------- content

app.MapGet("/api/channels", async (bool? refresh, CancellationToken ct) =>
    (await tablo.ChannelsAsync(refresh == true, ct)).Select(ChannelDto.From).ToList());

app.MapGet("/api/recordings", async (bool? refresh, CancellationToken ct) =>
    (await tablo.RecordingsAsync(refresh == true, ct)).Select(RecordingDto.From).ToList());

// The guide is the expensive one: ~19k airings, minutes to load on a busy box. It is loaded
// once in the background and served from memory, so rather than block a page load we answer
// 202 with progress until it is there.
app.MapGet("/api/guide", async (HttpContext http, string? start, double? hours, bool? refresh, CancellationToken ct) =>
{
    if (!tablo.GuideReady && refresh != true)
        return Results.Json(new { loading = true, progress = tablo.GuideProgress ?? 0, state = tablo.State },
            statusCode: StatusCodes.Status202Accepted);

    var from = DateTime.TryParse(start, null,
        System.Globalization.DateTimeStyles.AdjustToUniversal |
        System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
        ? parsed : DateTime.UtcNow;
    var to = from.AddHours(hours is > 0 and <= 48 ? hours.Value : 4);

    var airings = await tablo.GuideAsync(refresh == true, ct);
    var window = airings
        .Select(AiringDto.From)
        .Where(a => a is not null && a.StartUtc < to && a.EndUtc > from)
        .Select(a => a!)
        .OrderBy(a => a.StartUtc)
        .ToList();

    return Results.Ok(new
    {
        loading = false,
        start = from,
        end = to,
        channels = (await tablo.ChannelsAsync(false, ct)).Select(ChannelDto.From).ToList(),
        airings = window
    });
});

// What is on right now, per channel. Derived from the same cached guide, so it costs nothing.
app.MapGet("/api/now", async (CancellationToken ct) =>
{
    var channels = (await tablo.ChannelsAsync(false, ct)).Select(ChannelDto.From).ToList();
    if (!tablo.GuideReady)
        return Results.Ok(channels.Select(c => new NowDto(c, null, 0)).ToList());

    var now = DateTime.UtcNow;
    var byChannel = (await tablo.GuideAsync(false, ct))
        .Select(AiringDto.From)
        .Where(a => a is not null && a!.StartUtc <= now && a.EndUtc > now)
        .GroupBy(a => a!.ChannelPath)
        .ToDictionary(g => g.Key, g => g.First()!);

    return Results.Ok(channels.Select(c =>
    {
        byChannel.TryGetValue(c.Path, out var airing);
        var progress = airing is { DurationSeconds: > 0 }
            ? Math.Clamp((now - airing.StartUtc).TotalSeconds / airing.DurationSeconds, 0, 1)
            : 0;
        return new NowDto(c, airing, progress);
    }).ToList());
});

// Snapshot images are served unauthenticated by the device, but proxying them keeps the page
// working from anywhere the site itself is reachable — including behind nginx later, where the
// browser may have no route to the Tablo at all.
app.MapGet("/api/image/{id:long}", async (long id, IHttpClientFactory factory, CancellationToken ct) =>
{
    if (!tablo.Connected) return Results.NotFound();
    var client = factory.CreateClient("device");
    var url = (await tablo.ClientAsync(ct)).SnapshotUrl(id);

    using var req = new HttpRequestMessage(HttpMethod.Get, url);
    req.Headers.TryAddWithoutValidation("User-Agent", "Tablo-FAST/1.7.0 (Mobile; iPhone; iOS 18.4)");
    using var res = await client.SendAsync(req, ct);
    if (!res.IsSuccessStatusCode) return Results.NotFound();

    var bytes = await res.Content.ReadAsByteArrayAsync(ct);
    return Results.File(bytes, res.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
});

// ---------------------------------------------------------------------------------- playback

app.MapPost("/api/play", async (HttpContext http, PlayRequest request, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Path))
        return Results.BadRequest(new { error = "No program was given." });
    try
    {
        // Encode for the road the video has to travel: full quality inside the house, a lower
        // bitrate when it is going out through the uplink.
        var remote = Caller.IsRemote(http);
        var session = await streams.StartAsync(request.Path, request.Live, request.Position ?? 0, remote, ct);
        return Results.Ok(new PlayDto(
            session.Id, $"/stream/{session.Id}/index.m3u8",
            session.Live, session.OffsetSeconds, request.Duration ?? 0));
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Play failed for {Path}", request.Path);
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/stop/{id}", (string id) => { streams.Stop(id); return Results.Ok(); });

// The page polls this so it can tell "still transcoding" apart from "the transcoder died",
// and so it knows when a recording has been fully transcoded and is freely seekable.
app.MapGet("/api/session/{id}", (string id) =>
{
    var s = streams.Get(id);
    return s is null
        ? Results.NotFound(new { error = "That playback session has ended." })
        : Results.Ok(new
        {
            id = s.Id, live = s.Live, offset = s.OffsetSeconds,
            finished = s.Finished, exitCode = s.ExitCode, lastError = s.LastError
        });
});

// Serve the transcoder's output. Each fetch is what keeps the session (and, for live, the
// tuner) alive — stop asking for segments and the reaper shuts it down.
app.MapGet("/stream/{id}/{file}", (string id, string file) =>
{
    var session = streams.Get(id);
    if (session is null) return Results.NotFound();

    // Only ever serve the plain names ffmpeg writes; never anything a caller composed.
    if (file is not "index.m3u8" && !(file.StartsWith('s') && file.EndsWith(".ts") && file.Length == 9))
        return Results.NotFound();

    var path = Path.Combine(session.Dir, file);
    if (!File.Exists(path)) return Results.NotFound();
    session.Touch();

    var type = file.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl" : "video/mp2t";
    return Results.File(File.OpenRead(path), type, enableRangeProcessing: !file.EndsWith(".m3u8"));
});

app.Run();

// ------------------------------------------------------------------------------------ helpers

/// <summary>
/// Read a .env sitting next to the binary when the variables aren't already in the environment.
/// Under systemd EnvironmentFile does this; this is what makes `dotnet run` work by hand.
/// </summary>
static void LoadDotEnv()
{
    var file = Path.Combine(AppContext.BaseDirectory, ".env");
    if (!File.Exists(file)) return;
    foreach (var raw in File.ReadAllLines(file))
    {
        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#')) continue;
        var eq = line.IndexOf('=');
        if (eq <= 0) continue;
        var key = line[..eq].Trim();
        var value = line[(eq + 1)..].Trim().Trim('"');
        if (Environment.GetEnvironmentVariable(key) is null)
            Environment.SetEnvironmentVariable(key, value);
    }
}

public sealed record PlayRequest(string Path, bool Live, double? Position, int? Duration);

/// <summary>Loads the guide and recordings in the background so the first visitor isn't the one who waits.</summary>
public sealed class Warmer(TabloSession session) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => session.WarmAsync(stoppingToken);
}
