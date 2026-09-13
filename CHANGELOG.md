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
