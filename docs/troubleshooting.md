# Troubleshooting

## Sign-in

**"None of the Tablo(s) on the account answered on this network."**
The credentials were fine — the cloud accepted them and told us where your DVR is — but nothing
answered at that address. The server has to be on the same network as the DVR. Check the DVR is
powered up, and that the machine running this can reach port 8887 on it:

```bash
curl -s -A Tablo-FAST/1.7.0 http://<dvr-ip>:8887/server/info
```

(The `-A` is not optional. The firmware answers 403 to a request with no `User-Agent` at all.)

**The sign-in succeeds and bounces straight back to the sign-in page.**
The session cookie is not being kept. Almost always `TABLOWEB_COOKIE_SECURE=1` on a site served
over plain http — a cookie marked Secure cannot be set over http, so the browser silently drops
it. Unset it, or put TLS in front.

**Everybody is signed out after a restart, and the server has forgotten the DVR.**
The config directory did not survive. In Docker that means `/config` was not mounted; the
credentials file is encrypted with data-protection keys kept beside it, so losing the directory
loses both. Mount it and sign in once more.

**"Saved credentials could not be read" in the log.**
The credentials file outlived its keys — usually a `/config` that was partly restored, or copied
between machines. Sign in again and it is rewritten.

## Channels that will not play

**"The Tablo could not get a signal on this channel."**
The DVR answered `503 no_signal_lock`. It is the tuner and the aerial talking, not this site: the
other channels from the same transmitter will be failing too. The app retries up to three times
before reporting it, so a channel that fails intermittently usually just starts a little slowly.

Two things are worth knowing before blaming your aerial:

- **A Tablo degrades with uptime and load, and a power cycle brings channels back.** A box that
  had been up for a long time refused fourteen channels, grouped so neatly by transmitter that it
  looked exactly like a reception fault. After a power cycle, all but six of them tuned fine.
- **How fast it refuses tells you which it is.** A refusal in about a second is a genuine one. A
  refusal that takes ~40 seconds means the device is struggling, and a power cycle is worth more
  than an aerial rotator.

**Do not scan every channel to find out which ones work.** Each failing channel holds a tuner for
about 40 seconds, and a 50-channel sweep has been enough to wedge a DVR completely — pinging,
port 8887 accepting connections, and HTTP answering nothing for 25 minutes, needing a power
cycle. Test a handful, several seconds apart.

## The DVR stops answering everything

Ping works, port 8887 accepts connections, and no HTTP request ever completes. This is the
overload state, and it is not the network. What provokes it:

- Restarting this app twice in quick succession — each start kicks off a full guide load, which
  is hundreds of calls.
- Anything else on the account doing heavy work at the same time.
- Tuner scans, as above.

**It usually recovers on its own in about ten minutes.** Wait before walking to the DVR to pull
its power. And when deploying, deploy once and leave it.

## The guide

**The Guide tab shows a progress bar for a long time.**
Expected on a first start: 12,000 to 20,000 airings over hundreds of batch calls, which takes minutes on
a busy box. It is loaded once in the background and cached for six hours, so it only happens
again after a restart. The rest of the site works while it runs.

**Recording space shows as nothing.**
`/server/harddrives` returned something unreadable — usually because the device was busy rather
than because the endpoint is missing. A failed read is retried after 30 seconds rather than being
cached, so it heals itself; the log line records what the device actually said.

## Playback

**"Transcoding failed to start."**
ffmpeg died immediately, and its own last line is in the message. Common causes: no ffmpeg on
`PATH` (`TABLOWEB_FFMPEG` points at the wrong place), or a hardware encoder that passed its
startup probe but cannot cope with the real stream. Set `TABLOWEB_ENCODER=cpu` to confirm which.

**Video plays but the machine is on its knees.**
Software encoding, most likely. `docker compose logs | grep -i encoding` says which encoder was
chosen and, when a GPU was rejected, why. If there is no GPU to be had, see
[encoding.md](encoding.md#sizing-without-a-gpu) — `TABLOWEB_MAX_HEIGHT=480` is the biggest single
win.

**Everyone gets the low-bitrate stream.**
The app thinks they are all remote. Behind a reverse proxy this means `X-Forwarded-For` is not
being passed through, so every viewer appears to come from the proxy. Add it (see
[docker.md](docker.md#putting-it-on-the-internet)), or list the proxy's own network in
`TABLOWEB_LAN_NETWORKS`.

**A stream ends by itself after about a minute.**
Sessions are reaped 75 seconds after the last segment request, which is how a tuner gets released
when a tab is closed. If it happens while someone is watching, the browser stopped fetching —
look for a stalled player rather than a server problem.

**Someone else's stream stopped when I started mine.**
`TABLOWEB_MAX_STREAMS` (default 3) was reached, and the least recently watched session was
dropped to make room. Raise it if the machine and the DVR's tuner count can take it.

## Multi-view

**"Fewer than two of the chosen channels would tune."**
Every pane is a tuner, and the DVR has four. Something else is using them — a recording in
progress, most often, which multi-view deliberately does not interrupt. Check what is recording,
or pick fewer panes. If channels from one transmitter all fail while others tune, that is
reception rather than tuners: see [channels that will not play](#channels-that-will-not-play).

**The picture starts muted.**
By design. Mobile browsers refuse to autoplay video with sound — `play()` is simply rejected and
the picture never appears — so the mosaic starts muted and the first tap, click or arrow key
lifts it. Tapping a pane button both unmutes and points the sound at that pane.

**It buffers every few seconds.**
The transcode is not holding realtime. Look at the network before the CPU: four panes pull around
45 Mbps in bursts, so a DVR on a 100 Mbps link — or one on weak Wi-Fi — starves the transcoder
while ffmpeg sits idle. `ffmpeg` using well under a core per pane while the picture stalls is the
tell. If it really is the machine, `TABLOWEB_MOSAIC_LOWRES=1` makes decoding about four times
cheaper at a visibly softer picture, and three panes cost noticeably less than four.

**The yellow border lags the sound.**
It is drawn into the video on the server, so it arrives with the picture — a second or so behind
a switch the player made instantly. It cannot be made exact without drawing it in the page, and
the page is not on screen in full screen. Segments are already short to keep the gap small.

**The sound will not move between panes.**
The panes are audio renditions of one stream, so switching needs a player that can change audio
track mid-stream. Every browser tested does; if yours does not, the picture keeps playing with
the first pane's audio.

## Logs

```bash
docker compose logs -f          # Docker
journalctl -u tabloweb -f       # systemd
```

Framework noise is filtered out, so what is left is the app's own account of what it is doing:
which DVR it connected to, which encoder it chose and why, every stream that starts and stops,
and every guide load.
