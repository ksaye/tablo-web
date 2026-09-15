# Encoding

Every viewer costs one ffmpeg process. What that process costs depends almost entirely on whether
it is using a GPU, and this is the single decision that determines what hardware you need.

## Why transcoding is unavoidable

The Tablo serves HLS already, which makes it look as though a browser could just play it. It
cannot. The payload is what came off the aerial: **MPEG-2 video with AC3 audio**, usually
interlaced. No browser decodes either codec. So every playback request starts an ffmpeg that
pulls the Tablo playlist and writes out a fresh H.264/AAC HLS tree:

```
Tablo HLS ──▶ deinterlace (yadif, only frames marked interlaced)
          ──▶ optional downscale
          ──▶ H.264  (nvenc / vaapi / libx264)
          ──▶ AAC stereo
          ──▶ HLS on disk ──▶ browser
```

Live streams use a sliding window of six segments and run at 1x. Recordings keep every segment
so the viewer can seek back through what has been transcoded, and run at 3x realtime — enough to
stay comfortably ahead without burning cores rendering hours nobody will watch.

## The choice

`TABLOWEB_ENCODER` takes:

- **`auto`** (default) — try NVENC, then VA-API, then software.
- **`cpu`** — software only. The honest choice on a machine with no usable GPU: it skips two
  probes that are going to fail.
- **`nvenc`** — NVIDIA.
- **`vaapi`** — Intel Quick Sync or AMD on Linux.

Each is confirmed by actually encoding thirty frames of colour bars at startup. This matters more
than it sounds: `ffmpeg -encoders` will happily list `h264_nvenc` on a machine with no NVIDIA
driver, and inside a container the render node may exist but not be readable. A failed probe logs
one line explaining why and falls back to software, so a bad guess degrades rather than breaks.

## What it costs

Measured on one 720p59.94 channel — the expensive case, because of the frame rate — with an
NVIDIA Quadro P620:

| Configuration | CPU | Bitrate |
|---|---|---|
| `libx264 -preset veryfast` | **217% of a core** | 2.2 Mbps |
| `h264_nvenc`, software decode | **25%** | 3.7 Mbps |
| `h264_nvenc` + `mpeg2_cuvid` + `yadif_cuda` | **9%** | 2.9 Mbps |

The middle row is the default when NVENC is available: about eight times cheaper than software,
while keeping the *software* MPEG-2 decoder, which is noticeably more forgiving of a weak
over-the-air signal than nvdec is. `TABLOWEB_HWDECODE=1` moves decoding to the GPU as well — try
it, and turn it back off if a marginal channel starts breaking up.

VA-API has not been measured here, but sits in the same territory: a fixed-function encoder, a
few percent of a core, essentially free.

## Windows

Measured 2026-09-15 on the Windows Service install (v1.1.0), software encoding only — the build
VM used has no GPU, so NVENC/VA-API could not be compared on Windows.

| Test | Hardware | CPU | Bitrate |
|---|---|---|---|
| Live, 1 channel (4.1 KDFW, 720p59.94) | Xeon Silver 4210 @ 2.2GHz, 4 vCPUs | ~130% of a core (two 15s samples: 118%, 145%) | ~1.75 Mbps |
| Multi-view, 3 panes (4.1 KDFW + 5.1 KXAS + 8.8 WFAA, mixed 720p/1080i) | same | ~142% of a core | ~2.84 Mbps |

For context, the single-channel Linux number in the table above (`libx264 veryfast`, same
channel) is **217% of a core** — measured on the *same physical CPU model*: the Windows test ran
as a KVM guest on `home.saye.org`, which is itself a dual-socket Xeon Silver 4210. Take the
comparison as a data point, not a verdict — a live broadcast's content changes shot to shot and
`crf`/`veryfast` is content-adaptive, so both CPU and bitrate genuinely drift run to run even on
identical hardware; the Windows guest was also otherwise idle, while the Linux host was running
its full service fleet at the time the 217% figure was recorded. Re-measure before sizing a
Windows box around this.

**FAST (free streaming) channels can fail on a newer ffmpeg.** Some partner CDNs (Amagi, at
least) sign ad-stitched segment URLs with a trailing query string that confuses the HLS demuxer's
`allowed_segment_extensions` safety check, added in a recent ffmpeg version — it rejects the
segment as an unrecognized extension and the whole input fails to open (`Error binding
filtergraph inputs/outputs: Invalid argument` if it's one pane of a multi-view). Reproduced with
ffmpeg 9.0.1 (the current gyan.dev Windows *essentials* build) against three different FAST
channels; antenna channels and the Tablo's own HLS are unaffected — their segment URLs are plain.
The Linux deployment's documented `ffmpeg 6.1.1` predates this check, which is presumably why it
hasn't shown up there. Not yet fixed in code; the likely fix is passing `-allowed_extensions ALL`
on FAST-channel inputs. If FAST channels break after an ffmpeg upgrade, this is why.

## Sizing without a GPU

Roughly two cores per concurrent 720p60 viewer at `veryfast`. If that does not fit, in the order
worth trying:

1. **`TABLOWEB_MAX_HEIGHT=480`.** Encoding cost falls roughly with the pixel count, so capping
   720p and 1080i at 480p is close to a halving, and on a phone it is genuinely hard to see. Only
   ever scales down; an SD channel passes through untouched.
2. **`TABLOWEB_MAX_STREAMS=1` or `2`.** Fewer people at once, rather than everyone stuttering.
3. **`TABLOWEB_X264_PRESET=superfast`** or `ultrafast`. Cheaper, at a visibly higher bitrate for
   the same picture — which matters if anyone watches from outside the house.
4. **`TABLOWEB_THREADS`.** Leave it at `0` (ffmpeg decides) unless you are deliberately keeping
   one transcode from crowding out everything else on the machine.

## Tuning the bitrate

The two profiles — local and remote — are described in
[configuration.md](configuration.md#bitrate). Two things are worth knowing before turning the
dials:

**NVENC's `cq` is a much gentler dial than x264's `crf`.** The same number means a much higher
bitrate. `-cq 23` with a 6M cap sat at 6.2 Mbps flat, nearly triple what libx264 produced for a
comparable picture. Measured ladder on that 720p60 channel:

| Setting | Average |
|---|---|
| `cq 23`, uncapped | 7.5 Mbps |
| `cq 26`, maxrate 8M | 4.0 Mbps |
| `cq 28`, maxrate 6M | 4.7 Mbps |
| **`cq 30`, maxrate 6M** (default) | **3.2 Mbps** |
| `constqp 24` | 2.4 Mbps |

**`-b:v 0` is required with NVENC**, or the quality target is ignored entirely and you get
whatever the bitrate says. It is set for you; it is only worth knowing if you go editing
`StreamManager`.

## GOP and seeking

Every profile fixes the GOP at 120 frames with scene-cut detection off. That keeps each HLS
segment starting on a keyframe and independently decodable, which is what makes seeking work at
all. Seeking within the transcoded region is a normal seek; seeking past it restarts the
transcode at the new offset, which takes about four seconds.
