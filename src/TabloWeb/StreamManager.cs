using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using TabloWeb.Models;
using TabloWeb.Services;

namespace TabloWeb;

/// <summary>
/// Turns a Tablo stream into something a browser can actually play.
///
/// The Tablo serves HLS, but the payload is broadcast MPEG-2 video with AC3 audio — no browser
/// decodes either. So every playback request spawns an ffmpeg that pulls the Tablo playlist and
/// writes a fresh H.264/AAC HLS tree to disk, which we then serve.
///
/// Sessions are reaped when nobody has fetched a segment for a while. That matters for more
/// than tidiness: a live stream holds a tuner for as long as something keeps pulling segments,
/// so an abandoned session would keep a tuner busy until the box was rebooted.
/// </summary>
public sealed class StreamManager : IDisposable
{
    private const string TabloUserAgent = "Tablo-FAST/1.7.0 (Mobile; iPhone; iOS 18.4)";

    /// <summary>Give up on a session nobody is watching. Long enough to survive a buffering stall.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(75);

    /// <summary>
    /// Concurrent transcodes. The box has four tuners, and each transcode is roughly a core;
    /// the real reason to cap it is that every live session ties up a tuner.
    /// </summary>
    private static readonly int MaxSessions =
        int.TryParse(Environment.GetEnvironmentVariable("TABLOWEB_MAX_STREAMS"), out var n) ? n : 3;

    private readonly ConcurrentDictionary<string, StreamSession> _sessions = new();
    private readonly ILogger<StreamManager> _log;
    private readonly TabloSession _tablo;
    private readonly string _root;
    private readonly string _ffmpeg;
    private readonly bool _hasReadrate;
    private readonly Timer _reaper;

    /// <summary>Which video encoder the transcodes actually use, decided once at startup.</summary>
    private readonly Encoder _encoder;

    private bool Nvenc => _encoder == Encoder.Nvenc;

    /// <summary>
    /// Decode and deinterlace on the GPU as well, rather than only encoding there. Cheaper again
    /// (about 9% of a core versus 30% on a 720p60 channel) but it puts broadcast MPEG-2 through
    /// nvdec, which is less forgiving of a glitchy over-the-air signal than the software decoder.
    /// NVIDIA only, and off unless asked for.
    /// </summary>
    private readonly bool _hwDecode =
        Environment.GetEnvironmentVariable("TABLOWEB_HWDECODE") is "1" or "true";

    /// <summary>
    /// Which NVIDIA GPU to use, as a CUDA_VISIBLE_DEVICES value — a UUID is worth the typing,
    /// because an index silently points somewhere else the day a card is added or moved.
    /// </summary>
    private readonly string? _gpu = Environment.GetEnvironmentVariable("TABLOWEB_GPU");

    /// <summary>The VA-API render node, for Intel Quick Sync and AMD.</summary>
    private readonly string _vaapiDevice =
        Env("TABLOWEB_VAAPI_DEVICE", "/dev/dri/renderD128");

    /// <summary>
    /// Cap the picture height, e.g. 480 to send 720p and 1080i channels out at DVD size. On a
    /// machine with no usable GPU this is the biggest single saving there is — software encoding
    /// cost falls roughly with the pixel count — and on a phone nobody can tell.
    /// </summary>
    private readonly int _maxHeight =
        int.TryParse(Environment.GetEnvironmentVariable("TABLOWEB_MAX_HEIGHT"), out var h) && h > 0 ? h : 0;

    /// <summary>
    /// x264 speed/quality trade. Slower presets look better at the same bitrate and cost more CPU;
    /// on a weak box `superfast` or `ultrafast` is what makes software encoding keep up at all.
    /// </summary>
    private readonly string _x264Preset = Env("TABLOWEB_X264_PRESET", "veryfast");

    /// <summary>Cores each software transcode may use. 0 lets ffmpeg decide.</summary>
    private readonly string _threads = Env("TABLOWEB_THREADS", "0");

    private enum Encoder { Software, Nvenc, Vaapi }

    /// <summary>
    /// On the local network (or a VPN into it, which is the same house by another road) there is
    /// far more bandwidth than a broadcast channel needs, so the picture should be as close to
    /// the original as the encoder can manage. Measured ~3.2 Mbps average on a 720p60 channel
    /// with NVENC at these settings.
    /// </summary>
    public static readonly Quality LocalQuality =
        new(Cq: Env("TABLOWEB_CQ", "30"), Crf: Env("TABLOWEB_CRF", "22"),
            MaxRate: Env("TABLOWEB_MAXRATE", "6M"), BufSize: "12M", AudioRate: "160k");

    /// <summary>
    /// Off the network the stream has to climb the house upload link, which on a domestic
    /// connection is a fraction of the download speed and shared with everything else. Roughly
    /// half the bitrate: still fine on a laptop or a phone, and three of these fit comfortably
    /// where three local ones would not.
    /// </summary>
    public static readonly Quality RemoteQuality =
        new(Cq: Env("TABLOWEB_REMOTE_CQ", "34"), Crf: Env("TABLOWEB_REMOTE_CRF", "26"),
            MaxRate: Env("TABLOWEB_REMOTE_MAXRATE", "2500k"), BufSize: "5M", AudioRate: "128k");

    private static string Env(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

    public StreamManager(TabloSession tablo, ILogger<StreamManager> log, IHostEnvironment env)
    {
        _tablo = tablo;
        _log = log;
        _ffmpeg = Environment.GetEnvironmentVariable("TABLOWEB_FFMPEG") ?? "ffmpeg";
        _root = Environment.GetEnvironmentVariable("TABLOWEB_STREAM_DIR")
                ?? Path.Combine(env.ContentRootPath, "stream");

        // Anything left here belongs to a previous run whose ffmpegs died with it.
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) { _log.LogWarning("Could not clear {Root}: {Message}", _root, ex.Message); }
        Directory.CreateDirectory(_root);

        _hasReadrate = DetectReadrate();
        _encoder = DetectEncoder();
        _reaper = new Timer(_ => Reap(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Point CUDA at the card we want. The device order is pinned to the PCI bus so "device 0"
    /// means the card in the first slot rather than whichever CUDA rates fastest — on a machine
    /// with a second, busier GPU that distinction is the whole point.
    /// </summary>
    private void ApplyGpuEnvironment(ProcessStartInfo psi)
    {
        psi.Environment["CUDA_DEVICE_ORDER"] = "PCI_BUS_ID";
        if (!string.IsNullOrWhiteSpace(_gpu)) psi.Environment["CUDA_VISIBLE_DEVICES"] = _gpu;
    }

    /// <summary>
    /// Pick the video encoder, once, at startup.
    ///
    /// Software x264 costs about two cores for one 720p60 channel; a GPU costs a rounding error.
    /// But a GPU is not something to assume: TABLOWEB_ENCODER chooses between
    ///   auto   try NVIDIA, then VA-API, then fall back to software (the default)
    ///   cpu    software only — the right answer on any machine without a usable GPU
    ///   nvenc  NVIDIA NVENC
    ///   vaapi  VA-API, which is Intel Quick Sync and AMD on Linux
    ///
    /// Each candidate is decided by actually encoding a few frames. An encoder appearing in
    /// `ffmpeg -encoders` says nothing about a driver being loaded, a card being reachable from
    /// this process, or a render node being readable inside a container.
    /// </summary>
    private Encoder DetectEncoder()
    {
        var choice = (Environment.GetEnvironmentVariable("TABLOWEB_ENCODER") ?? "auto").ToLowerInvariant();

        switch (choice)
        {
            case "cpu" or "x264" or "libx264" or "software":
                _log.LogInformation("Encoding in software (libx264, preset {Preset}) by configuration", _x264Preset);
                return Encoder.Software;

            case "nvenc" or "nvidia" or "cuda" or "gpu":
                if (TestEncode(Encoder.Nvenc, out var nvencError)) return Announce(Encoder.Nvenc);
                _log.LogError("TABLOWEB_ENCODER={Choice} but the test encode failed; using libx264 instead: {Error}",
                    choice, nvencError);
                return Encoder.Software;

            case "vaapi" or "qsv" or "intel" or "amd":
                if (TestEncode(Encoder.Vaapi, out var vaapiError)) return Announce(Encoder.Vaapi);
                _log.LogError("TABLOWEB_ENCODER={Choice} but the test encode failed; using libx264 instead: {Error}",
                    choice, vaapiError);
                return Encoder.Software;

            default:
                if (TestEncode(Encoder.Nvenc, out var whyNotNvenc)) return Announce(Encoder.Nvenc);
                if (TestEncode(Encoder.Vaapi, out var whyNotVaapi)) return Announce(Encoder.Vaapi);
                _log.LogInformation(
                    "No usable GPU encoder; encoding in software (libx264, preset {Preset}). NVENC: {Nvenc} VA-API: {Vaapi}",
                    _x264Preset, whyNotNvenc, whyNotVaapi);
                return Encoder.Software;
        }
    }

    private Encoder Announce(Encoder encoder)
    {
        if (encoder == Encoder.Nvenc)
            _log.LogInformation("Encoding on the GPU (h264_nvenc, CUDA_VISIBLE_DEVICES={Gpu}){Decode}",
                _gpu ?? "PCI device 0", _hwDecode ? ", decoding on the GPU too" : "");
        else
            _log.LogInformation("Encoding on the GPU (h264_vaapi via {Device})", _vaapiDevice);
        return encoder;
    }

    /// <summary>Encode a second of colour bars and see whether it comes out.</summary>
    private bool TestEncode(Encoder encoder, out string error)
    {
        error = "";
        try
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
            if (encoder == Encoder.Vaapi) args.AddRange(["-vaapi_device", _vaapiDevice]);
            args.AddRange(["-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30", "-frames:v", "30"]);
            args.AddRange(encoder == Encoder.Nvenc
                ? ["-c:v", "h264_nvenc", "-gpu", "0"]
                : ["-vf", "format=nv12,hwupload", "-c:v", "h264_vaapi"]);
            args.AddRange(["-f", "null", "-"]);

            var psi = new ProcessStartInfo(_ffmpeg)
            { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (encoder == Encoder.Nvenc) ApplyGpuEnvironment(psi);

            using var p = Process.Start(psi)!;
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);

            if (p.HasExited && p.ExitCode == 0) return true;
            error = FirstLine(stderr);
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string FirstLine(string text)
    {
        var line = text.Trim().Split('\n').FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(line) ? "no output" : line;
    }

    /// <summary>
    /// Older ffmpeg builds have no -readrate, and an unknown option is a hard startup failure.
    /// We only use it to stop a recording transcode running away at full speed, so it is safe
    /// to do without.
    /// </summary>
    private bool DetectReadrate()
    {
        try
        {
            var psi = new ProcessStartInfo(_ffmpeg, "-hide_banner -h full")
            { RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            var found = text.Contains("-readrate ");
            _log.LogInformation("ffmpeg at {Path}; -readrate {State}", _ffmpeg, found ? "supported" : "unavailable");
            return found;
        }
        catch (Exception ex)
        {
            _log.LogError("Could not run ffmpeg ({Path}): {Message}. Playback will not work.", _ffmpeg, ex.Message);
            return false;
        }
    }

    public StreamSession? Get(string id) => _sessions.TryGetValue(id, out var s) ? s : null;

    public IReadOnlyCollection<StreamSession> Sessions => _sessions.Values.ToList();

    // ------------------------------------------------------------------ start

    /// <summary>
    /// Ask the Tablo to start streaming <paramref name="path"/> (a recording or a channel),
    /// then start transcoding it. Returns once the output playlist exists, so the caller can
    /// hand the URL straight to the browser.
    /// </summary>
    /// <param name="offsetSeconds">Where to start within a recording. Ignored for live.</param>
    /// <param name="remote">Viewer is out on the internet, so encode for the uplink.</param>
    public async Task<StreamSession> StartAsync(string path, bool live, double offsetSeconds,
        bool remote, CancellationToken ct)
    {
        EvictForCapacity();

        var watch = await TuneAsync(path, ct);

        var session = new StreamSession
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            TabloPath = path,
            Live = live,
            Remote = remote,
            OffsetSeconds = live ? 0 : Math.Max(0, offsetSeconds),
            Dir = Path.Combine(_root, Guid.NewGuid().ToString("n")[..12]),
            SourceUrl = watch.PlaylistUrl!
        };
        Directory.CreateDirectory(session.Dir);

        StartFfmpeg(session);
        _sessions[session.Id] = session;

        try
        {
            await WaitForPlaylistAsync(session, ct);
        }
        catch
        {
            Stop(session.Id);
            throw;
        }

        _log.LogInformation("Stream {Id} started: {Path} ({Kind}, offset {Offset:F0}s, {Where} quality)",
            session.Id, path, live ? "live" : "recording", session.OffsetSeconds,
            remote ? "off-network" : "local");
        return session;
    }

    /// <summary>
    /// Ask the Tablo to start streaming, retrying when the tuner fails to lock the signal.
    ///
    /// Tuning is not deterministic. A station that plays perfectly can refuse once and then
    /// work a second later — 8.8 WFAA-HD did exactly that: six refusals in a row one morning,
    /// then 10 for 10 an hour afterwards with nothing changed. Retrying turns most of those
    /// into a slightly slow start rather than an error in the viewer's face.
    ///
    /// A station that genuinely is not being received fails every attempt, and still gives up
    /// in a few seconds with an explanation the viewer can act on.
    /// </summary>
    private async Task<WatchResponse> TuneAsync(string path, CancellationToken ct)
    {
        // Only a *fast* refusal is worth retrying. When the tuner genuinely tries and fails to
        // lock, the device thinks about it for ~40 seconds first; retrying that just makes the
        // viewer wait two minutes for the same answer. A transient refusal comes back in a few
        // seconds, and those are the ones that succeed on the next go.
        var retryIfFasterThan = TimeSpan.FromSeconds(15);
        const int attempts = 3;

        for (var attempt = 1; ; attempt++)
        {
            var started = DateTime.UtcNow;
            try
            {
                var watch = await _tablo.WithRetryAsync(c => c.WatchAsync(path, ct), ct)
                            ?? throw new InvalidOperationException("The Tablo did not answer the play request.");
                if (string.IsNullOrWhiteSpace(watch.PlaylistUrl))
                    throw new InvalidOperationException("The Tablo returned no playlist for this program.");

                if (attempt > 1) _log.LogInformation("Signal locked on {Path} at attempt {Attempt}", path, attempt);
                return watch;
            }
            catch (TabloHttpException ex) when (IsNoSignalLock(ex))
            {
                var took = DateTime.UtcNow - started;
                if (attempt >= attempts || took > retryIfFasterThan)
                {
                    _log.LogWarning("No signal lock on {Path} after {Attempts} attempt(s), last took {Took:F0}s",
                        path, attempt, took.TotalSeconds);
                    throw new InvalidOperationException(
                        "The Tablo could not get a signal on this channel. That is reception at "
                        + "the antenna rather than a problem with this site — the other channels "
                        + "from the same transmitter will be failing too.");
                }
                _log.LogInformation("No signal lock on {Path} after {Took:F1}s (attempt {Attempt}); retrying",
                    path, took.TotalSeconds, attempt);
                await Task.Delay(1500, ct);
            }
        }
    }

    /// <summary>The device reports a tuner that could not lock as a 503 with this detail code.</summary>
    private static bool IsNoSignalLock(TabloHttpException ex) =>
        ex.Status == 503 && ex.Message.Contains("no_signal_lock", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The filter chain: deinterlace, optionally shrink, and get the frames wherever the encoder
    /// needs them. Broadcast is usually interlaced, and yadif's mode 0:-1:1 only touches the
    /// frames that say they are — progressive channels pass through untouched.
    /// </summary>
    private string VideoFilters()
    {
        // Frames stay on the GPU end to end, so both filters have to be the CUDA ones. scale_cuda
        // takes no expressions, so a height cap here is absolute: an SD channel would be scaled
        // *up* to it. That is the trade for hardware decoding.
        if (Nvenc && _hwDecode)
            return _maxHeight > 0 ? $"yadif_cuda=0:-1:1,scale_cuda=-2:{_maxHeight}" : "yadif_cuda=0:-1:1";

        var chain = "yadif=0:-1:1";
        // Only ever downwards: scaling a 480i channel up to 720 would cost more and show nothing.
        // The comma inside min() has to be escaped or ffmpeg reads it as the next filter.
        if (_maxHeight > 0) chain += $",scale=-2:min(ih\\,{_maxHeight})";
        // VA-API encodes from surfaces in the GPU's own memory, so the last step is to put them there.
        if (_encoder == Encoder.Vaapi) chain += ",format=nv12,hwupload";
        return chain;
    }

    private void StartFfmpeg(StreamSession session)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-nostdin",
            // Without a User-Agent the Tablo answers 403, and ffmpeg's default is fine but we
            // may as well look like the app the protocol was learned from.
            "-user_agent", TabloUserAgent
        };

        // Decode on the GPU too, when asked. Must come before -i, and only makes sense when the
        // encoder is also on the GPU — otherwise every frame would be downloaded again.
        if (Nvenc && _hwDecode)
            args.AddRange(["-hwaccel", "cuda", "-hwaccel_output_format", "cuda", "-c:v", "mpeg2_cuvid"]);

        // VA-API needs the render node opened up front. Decoding stays in software: broadcast
        // MPEG-2 off an aerial is often ragged, and the software decoder is the forgiving one.
        if (_encoder == Encoder.Vaapi)
            args.AddRange(["-vaapi_device", _vaapiDevice]);

        // Input seek: cheap, and the only way to start a recording part-way through, since we
        // transcode from the beginning of whatever we read.
        if (!session.Live && session.OffsetSeconds > 0)
            args.AddRange(["-ss", session.OffsetSeconds.ToString("F3")]);

        // A recording would otherwise transcode as fast as the CPU allows, burning cores and
        // disk to build hours of video nobody may watch. Three times realtime keeps a healthy
        // buffer ahead of the viewer at a fraction of the cost.
        if (!session.Live && _hasReadrate)
            args.AddRange(["-readrate", "3.0"]);

        args.AddRange([
            "-i", session.SourceUrl,
            "-map", "0:v:0", "-map", "0:a:0", "-sn", "-dn",
            "-vf", VideoFilters()
        ]);

        // Quality is chosen by where the viewer is. On the LAN there is gigabit to spare and the
        // picture should be as good as the broadcast. Off the network it has to fit through the
        // house uplink — measured at about 17 Mbps up — shared with three concurrent streams and
        // whatever else is uploading, so a stream that looks the same but costs half as much is
        // the right trade.
        var quality = session.Remote ? RemoteQuality : LocalQuality;

        // A fixed GOP with scene-cut detection off keeps every segment starting on a keyframe
        // and independently decodable, which is what makes seeking work.
        if (_encoder == Encoder.Vaapi)
            // VA-API drivers vary a lot in what they accept. Constant-quality with a rate cap is
            // the combination Intel and AMD both handle; B-frames are left out because some AMD
            // drivers refuse them outright.
            args.AddRange([
                "-c:v", "h264_vaapi", "-rc_mode", "CQP", "-qp", quality.Crf,
                "-maxrate", quality.MaxRate, "-bufsize", quality.BufSize, "-g", "120"
            ]);
        else if (Nvenc)
            // NVENC's cq is a much gentler dial than x264's crf — cq 23 with a 6M cap sat at
            // 6 Mbps flat, nearly triple what libx264 produced for the same picture. cq 30
            // measures at ~3.2 Mbps average on a 720p60 channel, letting quality drive the
            // bitrate while maxrate still catches the spikes. `-b:v 0` is what makes the
            // quality target apply at all.
            args.AddRange([
                "-c:v", "h264_nvenc", "-gpu", "0", "-preset", "p5", "-tune", "hq",
                "-rc", "vbr", "-cq", quality.Cq, "-b:v", "0",
                "-maxrate", quality.MaxRate, "-bufsize", quality.BufSize,
                "-profile:v", "high", "-g", "120", "-bf", "2"
            ]);
        else
            args.AddRange([
                "-c:v", "libx264", "-preset", _x264Preset, "-crf", quality.Crf,
                "-maxrate", quality.MaxRate, "-bufsize", quality.BufSize,
                "-threads", _threads,
                "-g", "120", "-keyint_min", "120", "-sc_threshold", "0"
            ]);

        // Forcing a pixel format would pull frames the GPU already holds back into system memory,
        // undoing the point of keeping them there; the software path wants it pinned for browser
        // compatibility.
        if (_encoder == Encoder.Software || (Nvenc && !_hwDecode))
            args.AddRange(["-pix_fmt", "yuv420p"]);

        args.AddRange([
            "-c:a", "aac", "-ac", "2", "-b:a", quality.AudioRate,
            "-f", "hls", "-hls_time", "4", "-hls_segment_type", "mpegts",
            "-hls_segment_filename", Path.Combine(session.Dir, "s%05d.ts")
        ]);

        if (session.Live)
            // A sliding window: old segments are deleted as they age out, so a channel left
            // playing all day doesn't fill the disk.
            args.AddRange([
                "-hls_list_size", "6", "-hls_delete_threshold", "3",
                "-hls_flags", "delete_segments+independent_segments+omit_endlist+temp_file"
            ]);
        else
            // Keep every segment produced so far and mark the playlist as an event, which is
            // how a player knows it may seek back through what has already been transcoded.
            args.AddRange([
                "-hls_playlist_type", "event",
                "-hls_flags", "independent_segments+temp_file"
            ]);

        args.Add(Path.Combine(session.Dir, "index.m3u8"));

        var psi = new ProcessStartInfo(_ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (Nvenc) ApplyGpuEnvironment(psi);

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr)
            {
                // Keep only the tail — enough to explain a failure without unbounded growth.
                stderr.AppendLine(e.Data);
                if (stderr.Length > 8000) stderr.Remove(0, stderr.Length - 4000);
            }
            session.LastError = e.Data;
        };
        proc.Exited += (_, _) =>
        {
            session.ExitCode = proc.HasExited ? proc.ExitCode : null;
            lock (stderr) session.Log = stderr.ToString();
            _log.LogInformation("Stream {Id} ffmpeg exited ({Code})", session.Id, session.ExitCode);
        };

        proc.Start();
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();
        session.Process = proc;
        session.Touch();
    }

    /// <summary>
    /// Wait for ffmpeg to publish its first playlist. Transcoding takes a few seconds to get
    /// going; if ffmpeg dies in the meantime its own error is far more useful than a timeout.
    /// </summary>
    private static async Task WaitForPlaylistAsync(StreamSession session, CancellationToken ct)
    {
        var playlist = Path.Combine(session.Dir, "index.m3u8");
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(playlist) && new FileInfo(playlist).Length > 0) return;
            if (session.Process is { HasExited: true })
                throw new InvalidOperationException(
                    "Transcoding failed to start. " + (session.LastError ?? "ffmpeg exited immediately."));
            await Task.Delay(250, ct);
        }
        throw new TimeoutException("Timed out waiting for the transcoder to produce video.");
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>
    /// Make room before starting another transcode by dropping the least recently watched
    /// session. Without this a few stale tabs could hold every tuner.
    /// </summary>
    private void EvictForCapacity()
    {
        while (_sessions.Count >= MaxSessions)
        {
            var oldest = _sessions.Values.OrderBy(s => s.LastAccessUtc).FirstOrDefault();
            if (oldest is null) return;
            _log.LogInformation("Stream limit reached; stopping least-recent session {Id}", oldest.Id);
            Stop(oldest.Id);
        }
    }

    public void Stop(string id)
    {
        if (!_sessions.TryRemove(id, out var session)) return;
        session.Kill();
        try
        {
            if (Directory.Exists(session.Dir)) Directory.Delete(session.Dir, recursive: true);
        }
        catch (Exception ex) { _log.LogWarning("Could not remove {Dir}: {Message}", session.Dir, ex.Message); }
    }

    private void Reap()
    {
        foreach (var session in _sessions.Values)
        {
            var idle = DateTime.UtcNow - session.LastAccessUtc;
            if (idle < IdleTimeout) continue;
            _log.LogInformation("Stream {Id} idle for {Idle:F0}s; stopping", session.Id, idle.TotalSeconds);
            Stop(session.Id);
        }
    }

    public void Dispose()
    {
        _reaper.Dispose();
        foreach (var id in _sessions.Keys.ToList()) Stop(id);
    }
}

/// <summary>Encoder settings for one class of viewer. See the profiles on StreamManager.</summary>
public sealed record Quality(string Cq, string Crf, string MaxRate, string BufSize, string AudioRate);

public sealed class StreamSession
{
    public required string Id { get; init; }
    public required string TabloPath { get; init; }
    public required string Dir { get; init; }
    public required string SourceUrl { get; init; }
    public bool Live { get; init; }

    /// <summary>
    /// The viewer is out on the internet rather than on the LAN or the VPN, so this transcode is
    /// leaving the house through the uplink and is encoded at a lower bitrate to suit it.
    /// </summary>
    public bool Remote { get; init; }

    /// <summary>Seconds into the recording that this transcode starts at; playback time is offset + player time.</summary>
    public double OffsetSeconds { get; init; }

    public Process? Process { get; set; }
    public int? ExitCode { get; set; }
    public string? LastError { get; set; }
    public string? Log { get; set; }

    public DateTime LastAccessUtc { get; private set; } = DateTime.UtcNow;
    public void Touch() => LastAccessUtc = DateTime.UtcNow;

    /// <summary>The transcoder has finished — for a recording that means the whole thing is available.</summary>
    public bool Finished => Process is { HasExited: true };

    public void Kill()
    {
        try
        {
            if (Process is { HasExited: false }) Process.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }
        Process?.Dispose();
    }
}
