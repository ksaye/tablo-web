# Changelog

## 2026-09-18 — Multi-view starts in about four seconds instead of sixteen

- **Every pane now opens at the live edge, and is not over-analysed first** (`MosaicManager.cs`):
  ffmpeg opens an HLS input three segments back by default and reads five seconds of it before it
  will write anything, and with a pane doing that per channel it was nearly all of the wait before
  a multi-view appeared. Measured on the same two antenna channels, Windows 11: the start call went
  from **12.1s to 3.8s**, and a playable stream from **16.5s to 4.5s**. Two free-streaming panes
  went from 15.4s to 9.6s (their CDN publishes six-second segments, so they can only start so fast).
  It also leaves the picture nearer to live, which shortens the lag before the yellow
  active-audio box catches up with an arrow-key press.

## 2026-09-17 — A log file on Windows

- **The Windows service now writes a log** (`FileLog.cs`) to `logs\tabloweb-<date>.log` beside the
  program, one file a day and the last seven kept, removed on uninstall. A Windows service's
  console goes nowhere, so until now a Windows install had no way to find out why a multi-view had
  stopped or a stream had failed — the first real multi-view failure on a Fire TV had to be chased
  by running the service by hand in a console. Off with `TABLOWEB_LOG=0`; elsewhere with
  `TABLOWEB_LOG_DIR`. Linux and Docker are unchanged (journald and `docker logs` already keep it).

## 2026-09-17 — Network discovery, and Windows Firewall rules in the MSI

- **Network discovery** (`Discovery.cs`): the server answers a `TABLOWEB_DISCOVER` UDP broadcast on
  port 8788 with its site port, version, the Tablo it is connected to, and `multiview` as a
  feature. The [Tablo for Fire TV](https://github.com/ksaye/tablo-firetv) app uses it to find a
  server and offer multi-view. Anything that is not a discovery query is ignored. Off with
  `TABLOWEB_DISCOVERY=0`; port with `TABLOWEB_DISCOVERY_PORT`. Docker needs host networking for it.
- **The MSI now adds Windows Firewall rules** for TCP 8787 and UDP 8788, scoped to the local
  subnet on all profiles, and removes them on uninstall. Before this a Windows install was
  reachable only from the machine itself: a service gets no firewall prompt. The release workflow
  adds `WixToolset.Firewall.wixext`.
- Verified: a broadcast query from another machine is answered and junk is ignored (Linux), and on
  Windows 11 the MSI installs the service and both rules, the site answers from the LAN, discovery
  answers a broadcast, and uninstall removes the rules.
- **Windows fetches its own ffmpeg** (`FfmpegSetup.cs`): with no `TABLOWEB_FFMPEG` and nothing on
  `PATH`, the service downloads ffmpeg 8.1.2 essentials (pinned, SHA-256 checked) beside
  `TabloWeb.exe` in the background after start-up; playback says so until it is ready. The MSI
  removes that folder on uninstall (`util:RemoveFolderEx`, found through an `InstallFolder`
  registry value) but not on upgrade. Off with `TABLOWEB_FFMPEG_DOWNLOAD=0`. Verified on Windows 11:
  fresh install → ffmpeg ready in 15s; upgrade kept it; uninstall removed it.
- **ffmpeg 9.0 halves multi-view speed.** Measured on the Windows VM with two antenna channels and
  the mosaic's exact command: 9.0.1 0.47×, while 6.1.1 / 7.1.1 / 8.1.2 held 1.05×; without the
  panes' audio 9.0.1 kept up. A startup warning now flags ffmpeg 9+. Through the service with 8.1.2,
  a two-channel multi-view held exactly 1.0× over two minutes.
- **Free streaming channels in multi-view on ffmpeg 7.1+**: they failed to start ("Error binding
  filtergraph") because newer ffmpeg rejects the CDNs' extension-less ad segment URLs.
  `-extension_picky 0` is now added to FAST inputs when the ffmpeg supports it (6.1 does not, and
  does not need it). They now start and composite. **Still open:** a two-FAST multi-view on Windows
  stalled after about a minute in one two-minute run, although each channel alone, and the pair
  composited by hand, keep up.

## 2026-09-15 — Windows measured, MSI build fixed, mosaic bug found

- **v1.1.0's MSI is now real**, built and installed end to end on a real Windows machine (the
  win11 build VM), not just compiled: install, a major-upgrade from 1.1.0→1.1.1, and uninstall
  all verified. Along the way, fixed two installer bugs from the first attempt at this — a
  duplicate `<Compile Include="Product.wxs">` that made WiX see two competing entry points, and
  a `WixToolset.Sdk` MSBuild directory-harvest item (`HarvestDirectory`) that turned out not to
  exist in 5.0.2 and silently did nothing. `installer/Product.wxs` now harvests `wwwroot` with
  the compiler-level `<Files Include="...\wwwroot\**"/>`, which does work, and the release
  workflow builds with the plain `wix build` CLI instead of an MSBuild project.
- **Measured real performance on Windows** against the live Tablo, software encoding (the build
  VM has no GPU): see [Windows](docs/encoding.md#windows) — roughly 130% of a core / 1.75 Mbps
  for one live channel, versus the documented Linux figure of 217% of a core for the same
  channel and encoder on the same physical CPU model. Caveated appropriately: live content varies
  shot to shot, and the comparison wasn't under matched load.
- **Found a real bug, not yet fixed**: FAST channels can fail to open under a newer ffmpeg — some
  partner CDNs' ad-stitched segment URLs trip ffmpeg's `allowed_segment_extensions` HLS-demuxer
  safety check (added after the `6.1.1` this project documents using). Reproduced with ffmpeg
  9.0.1 against three different FAST channels; antenna channels are unaffected. Written up in
  [docs/encoding.md](docs/encoding.md#windows) since that's where it was found — likely fix is
  `-allowed_extensions ALL` on FAST-channel ffmpeg inputs.

## 2026-09-15 — MSI installer, Windows Service, and an in-app update checker

- **Windows installer** (`installer/Product.wxs`, WiX v5): installs TabloWeb into
  `C:\Program Files\TabloWeb`, registers it as a Windows Service (`LocalSystem`, `Start=auto`) so
  it survives a reboot with no console window, and drops an *Open TabloWeb* Start Menu shortcut.
  Built from the same self-contained win-x64 publish as the plain zip; `TabloWeb.exe`'s file
  component also carries the `ServiceInstall`/`ServiceControl` pair so a `/qn` install can stop,
  replace and restart the running service unattended. `MajorUpgrade` means a newer MSI upgrades
  an existing install in place rather than sitting beside it.
- **`builder.Host.UseWindowsService()`** in `Program.cs` — a no-op everywhere except when the
  Service Control Manager launches the exe, where it swaps in a lifetime that reports status back
  to the SCM instead of writing to a console nobody can see.
- **In-app update checker** (`UpdateChecker.cs`): polls `GET /repos/ksaye/tablo-web/releases/latest`
  once a minute after startup and then every `TABLOWEB_UPDATE_CHECK_HOURS` (default 24), compares
  the release tag against the version baked into the exe, and — only when a `.msi` asset is newer
  — shows a banner with an *Update now* button. Clicking it downloads that MSI to `%TEMP%` and
  launches `msiexec /qn`, which is what actually restarts the service; the endpoint therefore
  returns before the install finishes on purpose. Off by default everywhere except the Windows
  install (Docker updates via image pulls, a Linux checkout via `git pull` — neither wants a
  background job phoning GitHub). See [Updates](docs/configuration.md#updates).
- `GET /api/update` / `POST /api/update/install` — both already covered by the existing `/api/*`
  401 gate in `Login.cs`, no changes needed there.

## 2026-09-15 — Windows release

Tagged v1.0.0 and published a self-contained win-x64 build (single `TabloWeb.exe`, no .NET
install needed) as a GitHub release, alongside the existing Docker path. ffmpeg is still required
and not bundled — install it separately and put it on PATH, or point `TABLOWEB_FFMPEG` at it.

## 2026-09-13 — Multi-view

Watch 2–4 live channels at once, tiled into a single picture.

- `MosaicManager` composites the channels **server-side**: one ffmpeg pulls the live playlists,
  lays them on a 1080p canvas and emits one H.264 HLS stream whose audio renditions are the
  individual panes. The browser therefore decodes one video rather than four, and moving the
  sound between panes is an audio-track switch that costs nothing.
- A yellow border marks the pane the sound is coming from. It is drawn into the video — in full
  screen the picture is all there is — and repositioned by a runtime command written to the
  running ffmpeg's stdin, so no restart and no retune.
- `POST /api/mosaic`, `GET /api/mosaic` (rejoin a running session), `GET /api/mosaic/{id}`,
  `POST /api/mosaic/{id}/audio`, `POST /api/mosaic/{id}/stop`, and `GET /mosaic/{id}/{file}` for
  the segments. One session at a time, reaped 90s after the last segment request.
- A **Multi-view** tab picks the channels. The sound follows a pane you choose or cycles on a
  timer (45s by default); the arrow keys work too — left/right step and pin, up/down hand it back
  to the cycle.
- New settings: `TABLOWEB_MOSAIC_DIR`, `TABLOWEB_MOSAIC_DEINT`, `TABLOWEB_MOSAIC_LOWRES`,
  `TABLOWEB_MOSAIC_BOX_THICKNESS`. See [docs/configuration.md](docs/configuration.md).
- `docs/images/multi-view.png` — four channels running at once, so the README shows the thing
  rather than describing it, plus `multi-view-picker.png` and `multi-view-player.png` for how the
  channels are chosen and what the controls look like while it runs.
- Re-shot `guide.png`, `live-tv.png` and `recordings.png`: their tab bars predated the
  **Multi-view** tab, so the first thing anyone saw in the README contradicted the feature list.

- **Tap a pane to move the sound to it.** In full screen the button bar was outside the element
  that goes full screen, so a phone had no way to change the audio pane at all — and the overlay
  hint has always said "tap a channel for sound". The pane layout is mirrored from the server's,
  since the composite arrives as one flat picture with nothing in the DOM to hit-test.
- **The pane bar now follows the picture into full screen**, floating along the bottom, so there
  is a visible control there and not just a gesture — and the auto-cycle can be re-armed without
  leaving full screen.
- **Verified in the shipped Docker image**, software encoding, four streaming channels: the
  mosaic starts, plays 1920x1080 with four audio renditions, and holds exactly realtime (30.0s of
  video per 30s of wall clock) at about six cores. The measured cost is now in
  [docs/configuration.md](docs/configuration.md).
- **The browser's own video controls are switched off while a mosaic plays.** On a touchscreen
  their shadow DOM swallows the tap (the first tap only reveals the control bar), so tapping a
  pane did nothing in full screen — reported on an Android phone, and reproduced with a real touch
  tap where a mouse click had worked fine. They have nothing to offer a live mosaic anyway.

Carried over with it, from running this in anger:

- **Mosaics start muted.** Mobile browsers reject autoplay with sound outright, leaving the
  picture stuck behind the tuning overlay; the first tap or key lifts the mute.
- **Deinterlace every pane, always.** Skipping it when decoding at reduced resolution looked
  fine on a phone and juddered badly on a television.
- **Full-resolution decode by default.** Half-resolution MPEG-2 decode is much cheaper but
  visibly soft on a big screen, and full-res holds realtime; `TABLOWEB_MOSAIC_LOWRES` remains as
  the escape hatch.
- **Short segments and a small live-edge cushion for the mosaic only**, which is what keeps the
  burned-in yellow border close behind the (instant) audio switch.
- The encoder arguments are now shared with normal playback rather than duplicated, so NVENC,
  VA-API and software all behave the same way in both.

## Before this

The repository history is the record — `git log`. This file starts here.
