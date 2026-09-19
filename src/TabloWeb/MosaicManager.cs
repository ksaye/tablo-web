using System.Diagnostics;
using System.Text;
using TabloWeb.Services;

namespace TabloWeb;

/// <summary>
/// Multi-view: several live channels tiled into ONE video stream, server-side.
///
/// Compositing here rather than in the client is deliberate. Four &lt;video&gt; elements playing
/// four transcodes means four decoders, four network streams and four sets of clock drift, and a
/// TV-stick browser cannot do it at all. Instead a single ffmpeg pulls 2-4 Tablo live playlists,
/// lays them out on a 1080p canvas with <c>overlay</c>, and emits one H.264 HLS stream that
/// carries every pane's audio as a separate selectable rendition. The client plays that one
/// stream through the same path live TV already uses, and switches audio track to move the sound
/// between panes — which costs nothing, because no re-encode is involved.
///
/// One session at a time: there is one couch. Starting a mosaic stops the previous one and any
/// plain live <see cref="StreamManager"/> session, because a mosaic wants all the tuners.
/// Tuning is "always allow, never yield": a recording already holding a tuner just means that
/// pane fails to lock and shows black.
/// </summary>
public sealed class MosaicManager : IDisposable
{
    private const string TabloUserAgent = "Tablo-FAST/1.7.0 (Mobile; iPhone; iOS 18.4)";

    /// <summary>Canvas the panes are laid out on. Each 2×2 pane is then 960×540, a downscale from
    /// 720p60/1080i broadcast.</summary>
    private const int CanvasW = 1920, CanvasH = 1080;

    /// <summary>Output frame rate. Every pane is resampled to it, so the mixed 60p/30i sources
    /// share one timeline — without that the overlay chain runs at whichever input arrives.</summary>
    private const int Fps = 30;

    /// <summary>Segment length. 2s rather than the 4s live TV uses, because the "which pane has
    /// the sound" marker is burned into the video (see <see cref="SetAudioBox"/>) and therefore
    /// lags the instant client-side audio switch by however far behind live the player is.</summary>
    private const int SegmentSeconds = 2;

    /// <summary>Drop the mosaic when nobody has fetched a segment for this long — that frees the
    /// tuners. Same reasoning as StreamManager.IdleTimeout.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(90);

    private readonly TabloSession _tablo;
    private readonly StreamManager _streams;
    private readonly ILogger<MosaicManager> _log;
    private readonly string _root;
    private readonly Timer _reaper;
    private readonly SemaphoreSlim _startGate = new(1, 1);

    private MosaicSession? _current;

    public MosaicManager(TabloSession tablo, StreamManager streams, ILogger<MosaicManager> log, IHostEnvironment env)
    {
        _tablo = tablo;
        _streams = streams;
        _log = log;
        _root = Environment.GetEnvironmentVariable("TABLOWEB_MOSAIC_DIR")
                ?? Path.Combine(env.ContentRootPath, "mosaic");
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) { _log.LogWarning("Could not clear {Root}: {Message}", _root, ex.Message); }
        Directory.CreateDirectory(_root);

        _reaper = new Timer(_ => Reap(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public MosaicSession? Current => _current is { Alive: true } s ? s : null;

    public MosaicSession? Get(string id) => _current is { } s && s.Id == id ? s : null;

    // ------------------------------------------------------------------ start

    /// <summary>Start (or replace) the mosaic. Pane order follows <paramref name="channelPaths"/>.</summary>
    public async Task<MosaicSession> StartAsync(
        IReadOnlyList<string> channelPaths, bool remote, CancellationToken ct)
    {
        if (channelPaths.Count is < 2 or > 4)
            throw new ArgumentException("Multi-view needs between 2 and 4 channels.");

        _streams.EnsureFfmpeg();
        await _startGate.WaitAsync(ct);
        try
        {
            StopInternal();

            // A mosaic wants every tuner; a plain live web session would just lose the race for
            // one. Recordings are left alone on purpose (see the class summary).
            foreach (var live in _streams.Sessions.Where(s => s.Live).ToList())
            {
                _log.LogInformation("Stopping live stream {Id} to free a tuner for the mosaic", live.Id);
                _streams.Stop(live.Id);
            }

            // Resolve each channel to a playlist URL. Antenna paths tune a physical tuner here;
            // FAST paths return a CDN URL and tune nothing. One failing to tune is not fatal —
            // that pane shows black and the rest still play.
            var sources = new List<string>();
            var labels = new List<string>();
            foreach (var path in channelPaths)
            {
                try
                {
                    var watch = await _tablo.WithRetryAsync(c => c.WatchAsync(path, ct), ct);
                    if (string.IsNullOrWhiteSpace(watch?.PlaylistUrl))
                        throw new InvalidOperationException("no playlist");
                    sources.Add(watch!.PlaylistUrl!);
                    labels.Add(path);
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Mosaic pane {Path} would not tune ({Message}); skipping it", path, ex.Message);
                }
            }

            if (sources.Count < 2)
                throw new InvalidOperationException(
                    "Fewer than two of the chosen channels would tune. The others from the same "
                    + "transmitter are probably failing too — that is reception at the antenna.");

            var session = new MosaicSession
            {
                Id = Guid.NewGuid().ToString("n")[..12],
                Dir = Path.Combine(_root, Guid.NewGuid().ToString("n")[..12]),
                Channels = labels,
                Remote = remote
            };
            Directory.CreateDirectory(session.Dir);

            StartFfmpeg(session, sources);
            _current = session;

            try
            {
                await WaitForMasterAsync(session, ct);
            }
            catch
            {
                StopInternal();
                throw;
            }

            _log.LogInformation("Mosaic {Id} started: {Count} panes ({Where})",
                session.Id, sources.Count, remote ? "off-network" : "local");
            return session;
        }
        finally { _startGate.Release(); }
    }

    private void StartFfmpeg(MosaicSession session, IReadOnlyList<string> sources)
    {
        var encoder = _streams.Encoder;
        var quality = session.Remote ? StreamManager.RemoteQuality : StreamManager.LocalQuality;

        // NOT -nostdin: the "active audio" yellow box is a single drawbox filter repositioned at
        // runtime by writing `c` commands to ffmpeg's stdin (see SetAudioBox) — no restart, no
        // extra library. ffmpeg only reads stdin when -nostdin is absent.
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-fflags", "+genpts" };

        // VA-API needs its render node opened before the inputs. Decoding stays in software for
        // the same reason live TV does it that way: broadcast MPEG-2 off an aerial is ragged, and
        // the software decoder is the forgiving one.
        if (encoder == VideoEncoder.Vaapi)
            args.AddRange(["-vaapi_device", _streams.VaapiDevice]);

        // `-lowres` decodes MPEG-2 at half (1) or quarter (2) linear resolution — much cheaper,
        // but it visibly softens the picture on a big TV, so it is OFF by default. Full-res
        // decode holds realtime on a modest box (measured ~1.0x with 3-4 panes);
        // TABLOWEB_MOSAIC_LOWRES=1 is the escape hatch if a loaded machine starts buffering.
        // MPEG-2 only — the H.264 decoder a FAST channel needs has no -lowres.
        var lowres = Environment.GetEnvironmentVariable("TABLOWEB_MOSAIC_LOWRES") is { Length: > 0 } lr
                     && int.TryParse(lr, out var lrv) ? Math.Clamp(lrv, 0, 2) : 0;

        for (var i = 0; i < sources.Count; i++)
        {
            var isFast = i < session.Channels.Count && TabloClient.IsFast(session.Channels[i]);
            // Open each pane at the live edge rather than ffmpeg's default of three segments back:
            // fetching those three, per pane, was most of the wait before a multi-view appeared,
            // and it left the picture further behind live than it needed to be.
            //
            // The analysis budget is deliberately left alone. Capping it (1s/1MB) looked like a
            // further second or two saved, but broadcast MPEG-2 then did not always get recognised
            // in time and those panes composited as black rectangles — a faster multi-view of
            // nothing.
            args.AddRange(["-user_agent", TabloUserAgent, "-thread_queue_size", "1024",
                "-live_start_index", "-1"]);
            // Free streaming CDNs serve ad segments with extension-less URLs; see ExtensionPickyOption.
            if (isFast && _streams.ExtensionPickyOption) args.AddRange(["-extension_picky", "0"]);
            if (lowres > 0 && !isFast) args.AddRange(["-lowres", lowres.ToString()]);
            // A Tablo playlist is HTTP; a FAST CDN URL is too. Both take the same options.
            args.AddRange(["-i", sources[i]]);
        }

        var rects = Rects(sources.Count);
        session.Rects = rects;
        session.BoxThickness = int.TryParse(
            Environment.GetEnvironmentVariable("TABLOWEB_MOSAIC_BOX_THICKNESS"), out var bt) ? bt : 8;

        // Deinterlace, always. Skipping it when -lowres is on (an earlier idea, on the theory that
        // a half-res decode hides the combing) left visible motion judder on the 1080i channels
        // once it was on a real television rather than a phone. yadif's `deint=1` only touches
        // frames flagged interlaced, so the 720p60 channels pass through untouched.
        var deinterlace = Environment.GetEnvironmentVariable("TABLOWEB_MOSAIC_DEINT") is not "0";
        args.AddRange(["-filter_complex",
            BuildFilter(rects, deinterlace, session.BoxThickness, encoder == VideoEncoder.Vaapi)]);

        args.AddRange(["-map", "[v]"]);
        for (var i = 0; i < sources.Count; i++)
        {
            // '?' — a pane that tuned but has no audio stream must not fail the whole mux.
            args.AddRange(["-map", $"{i}:a:0?"]);
        }

        // Shorter GOP than the single-stream profile so the segments can be 2s, which halves how
        // far the yellow active-audio box lags a pane change.
        args.AddRange(StreamManager.VideoEncoderArgs(encoder, quality, gopSize: Fps * SegmentSeconds));
        args.AddRange(["-r", Fps.ToString()]);
        // VA-API is encoding from surfaces already in GPU memory; pinning a pixel format would
        // drag them back out again.
        if (encoder != VideoEncoder.Vaapi) args.AddRange(["-pix_fmt", "yuv420p"]);
        args.AddRange(["-c:a", "aac", "-ac", "2", "-b:a", "128k"]);

        // One video variant, each pane's audio as an alternative rendition in the same group.
        // No name: — that would be substituted for %v and give the playlists odd filenames; the
        // client labels the panes from the channel list anyway. ffmpeg then names them by index:
        // index-0.m3u8 is the video, index-1..N.m3u8 the audio.
        var streamMap = new StringBuilder("v:0,agroup:aud");
        for (var i = 0; i < sources.Count; i++)
            streamMap.Append($" a:{i},agroup:aud{(i == 0 ? ",default:yes" : "")}");

        args.AddRange([
            "-f", "hls", "-hls_time", SegmentSeconds.ToString(), "-hls_segment_type", "mpegts",
            // 20 short segments still gives a 40s window, so a brief dip below realtime does not
            // starve the player at the live edge.
            "-hls_list_size", "20", "-hls_delete_threshold", "12",
            "-hls_flags", "delete_segments+independent_segments+omit_endlist+temp_file",
            "-master_pl_name", "master.m3u8",
            "-var_stream_map", streamMap.ToString(),
            "-hls_segment_filename", Path.Combine(session.Dir, "index-%v-s%05d.ts"),
            Path.Combine(session.Dir, "index-%v.m3u8")
        ]);

        var psi = new ProcessStartInfo(_streams.FfmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,   // used to reposition the active-audio box at runtime
            UseShellExecute = false
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        _streams.PrepareGpu(psi);

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr)
            {
                stderr.AppendLine(e.Data);
                if (stderr.Length > 8000) stderr.Remove(0, stderr.Length - 4000);
            }
            session.LastError = e.Data;
        };
        proc.Exited += (_, _) =>
        {
            lock (stderr) session.Log = stderr.ToString();
            _log.LogInformation("Mosaic {Id} ffmpeg exited ({Code})",
                session.Id, proc.HasExited ? proc.ExitCode : (int?)null);
        };

        proc.Start();
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();
        proc.StandardInput.AutoFlush = true;
        session.Process = proc;
        session.Touch();
    }

    /// <summary>
    /// Move the yellow "this pane has the sound" box onto <paramref name="pane"/>. Done by
    /// writing `c` (send-command) lines to the running ffmpeg's stdin — the drawbox filter takes
    /// x/y/w/h as runtime commands, so nothing restarts and no tuner is retuned. Called on every
    /// audio-pane change, including each step of the auto-cycle.
    ///
    /// It has to be burned into the video rather than drawn in the page, because in full screen
    /// the picture is the only thing on screen.
    /// </summary>
    public void SetAudioBox(string id, int pane)
    {
        if (Get(id) is not { } s || s.Rects is not { } rects || pane < 0 || pane >= rects.Length) return;

        var t = s.BoxThickness;
        var (x, y, w, h) = rects[pane];
        // Inset by the thickness so the whole border is inside the pane (edges at the canvas
        // boundary would otherwise be half-clipped).
        (x, y, w, h) = (x + t, y + t, w - 2 * t, h - 2 * t);

        lock (s.StdinLock)
        {
            try
            {
                var stdin = s.Process?.StandardInput;
                if (stdin is null || s.Process!.HasExited) return;
                stdin.Write($"cdrawbox -1 x {x}\n");
                stdin.Write($"cdrawbox -1 y {y}\n");
                stdin.Write($"cdrawbox -1 w {w}\n");
                stdin.Write($"cdrawbox -1 h {h}\n");
            }
            catch (Exception ex) { _log.LogDebug("Mosaic box move failed: {Message}", ex.Message); }
        }
    }

    /// <summary>
    /// The overlay chain that lays the panes out. A black background source keeps the geometry
    /// simple and forgiving: a pane that dies (eof_action=pass) or stalls (repeatlast, the
    /// default) just leaves its last frame or black, and the rest of the mosaic carries on.
    /// </summary>
    private static string BuildFilter(
        (int X, int Y, int W, int H)[] rects, bool deinterlace, int boxThickness, bool vaapi)
    {
        var n = rects.Length;
        var sb = new StringBuilder();
        sb.Append($"color=c=black:s={CanvasW}x{CanvasH}:r={Fps}[bg];");

        for (var i = 0; i < n; i++)
        {
            var (_, _, w, h) = rects[i];
            // decrease + pad: never stretch an odd-aspect source; letterbox it inside its cell.
            sb.Append($"[{i}:v]{(deinterlace ? "yadif=0:-1:1," : "")}fps={Fps},")
              .Append($"scale={w}:{h}:force_original_aspect_ratio=decrease,")
              .Append($"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1[p{i}];");
        }

        var prev = "bg";
        for (var i = 0; i < n; i++)
        {
            var (x, y, _, _) = rects[i];
            var outLabel = i == n - 1 ? "vc" : $"o{i}";
            sb.Append($"[{prev}][p{i}]overlay={x}:{y}:eof_action=pass[{outLabel}];");
            prev = outLabel;
        }

        // The active-audio marker: one yellow box, starting on pane 0 (the default audio track).
        // SetAudioBox repositions it at runtime via ffmpeg stdin commands.
        var (bx, by, bw, bh) = rects[0];
        var t = boxThickness;
        sb.Append($"[vc]drawbox=x={bx + t}:y={by + t}:w={bw - 2 * t}:h={bh - 2 * t}:")
          .Append($"color=yellow:thickness={t}")
          // VA-API encodes from surfaces in the GPU's own memory, so the last step is to put the
          // finished composite there. Everything above it is software — drawbox included, which
          // is what makes the runtime box move possible.
          .Append(vaapi ? ",format=nv12,hwupload[v]" : "[v]");

        return sb.ToString();
    }

    /// <summary>(x, y, w, h) for each pane on the canvas. Two panes sit as a centred band rather
    /// than full-height halves, so neither is cropped to a letterbox sliver.</summary>
    private static (int X, int Y, int W, int H)[] Rects(int n)
    {
        int halfW = CanvasW / 2, halfH = CanvasH / 2;

        return n switch
        {
            2 => [(0, halfH / 2, halfW, halfH), (halfW, halfH / 2, halfW, halfH)],
            3 => [(0, 0, halfW, halfH), (halfW, 0, halfW, halfH), (halfW / 2, halfH, halfW, halfH)],
            _ => [(0, 0, halfW, halfH), (halfW, 0, halfW, halfH),
                  (0, halfH, halfW, halfH), (halfW, halfH, halfW, halfH)],
        };
    }

    private static async Task WaitForMasterAsync(MosaicSession session, CancellationToken ct)
    {
        var master = Path.Combine(session.Dir, "master.m3u8");
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(master) && new FileInfo(master).Length > 0)
            {
                // The master can be written before any media playlist has a segment; give the
                // first video playlist a moment so the client's first fetch is not a 404 storm.
                if (File.Exists(Path.Combine(session.Dir, "index-0.m3u8"))) return;
            }
            if (session.Process is { HasExited: true })
                throw new InvalidOperationException(
                    "The mosaic transcoder stopped on startup. "
                    + (session.LastError ?? "ffmpeg exited immediately."));
            await Task.Delay(250, ct);
        }
        throw new TimeoutException("Timed out waiting for the mosaic transcoder to produce video.");
    }

    // ------------------------------------------------------------------ lifecycle

    public void Stop(string id)
    {
        if (_current?.Id == id) StopInternal();
    }

    private void StopInternal()
    {
        var s = _current;
        _current = null;
        if (s is null) return;
        s.Kill();
        try
        {
            if (Directory.Exists(s.Dir)) Directory.Delete(s.Dir, recursive: true);
        }
        catch (Exception ex) { _log.LogWarning("Could not remove {Dir}: {Message}", s.Dir, ex.Message); }
    }

    private void Reap()
    {
        var s = _current;
        if (s is null) return;
        if (!s.Alive)
        {
            _log.LogInformation("Mosaic {Id} transcoder gone; clearing up", s.Id);
            StopInternal();
            return;
        }
        if (DateTime.UtcNow - s.LastAccessUtc > IdleTimeout)
        {
            _log.LogInformation("Mosaic {Id} idle for >{Secs}s; stopping and freeing the tuners",
                s.Id, IdleTimeout.TotalSeconds);
            StopInternal();
        }
    }

    public void Dispose()
    {
        _reaper.Dispose();
        StopInternal();
    }
}

public sealed class MosaicSession
{
    public required string Id { get; init; }
    public required string Dir { get; init; }
    public required IReadOnlyList<string> Channels { get; init; }
    public bool Remote { get; init; }

    public Process? Process { get; set; }
    public string? LastError { get; set; }
    public string? Log { get; set; }

    /// <summary>Pane rectangles, in pane order. Set once ffmpeg starts; used by
    /// <see cref="MosaicManager.SetAudioBox"/> to place the yellow marker.</summary>
    public (int X, int Y, int W, int H)[]? Rects { get; set; }
    public int BoxThickness { get; set; } = 8;

    /// <summary>Serialises the `c` command writes to ffmpeg's stdin.</summary>
    public object StdinLock { get; } = new();

    public DateTime LastAccessUtc { get; private set; } = DateTime.UtcNow;
    public void Touch() => LastAccessUtc = DateTime.UtcNow;

    public bool Alive => Process is { HasExited: false };
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
