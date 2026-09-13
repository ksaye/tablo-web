# Changelog

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
  rather than describing it.

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
