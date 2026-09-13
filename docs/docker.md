# Running in Docker

```bash
docker compose up -d
docker compose logs -f
```

Then open <http://localhost:8787>. The first page is the sign-in; use your Tablo account.

The image is built from the `Dockerfile` here — a two-stage build that publishes the app with the
.NET SDK and runs it on the ASP.NET runtime image, with Debian's ffmpeg installed alongside.

## What to mount

| Path | Why |
|---|---|
| `/config` | The encrypted Tablo credentials and the data-protection keys. **Mount this.** Without it, every `docker compose up --build` signs everyone out and forgets the DVR. |
| `/var/tmp/tabloweb-stream` | Transcoded segments. Fine inside the container; mount a disk if the container filesystem is small. Allow a few GB per concurrent viewer — a recording keeps every segment it has transcoded so that seeking works. |
| `/var/tmp/tabloweb-mosaic` | Multi-view's tiled output. Nothing to mount in practice: it is a 40-second sliding window, so it stays small. |

Do not put the stream directory on tmpfs unless you have RAM to spare: a two-hour recording at
3 Mbps is about 2.7 GB, and it is all held while someone is watching.

## Networking

The default is a normal bridge network with port 8787 published. The DVR is found through Tablo's
cloud API, which hands back its address on your network, so outbound NAT is all the container
needs — there is no discovery protocol to worry about and no reason to reach for `host` mode.

## NVIDIA

Needs the NVIDIA driver on the host and the
[NVIDIA Container Toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/latest/install-guide.html).

```bash
docker compose -f docker-compose.yml -f docker-compose.nvidia.yml up -d
```

The overlay asks for the `video` capability as well as `compute`. That is the one that carries
NVENC — without it the card appears inside the container but the encoder does not exist, which
looks exactly like a missing driver.

Confirm it took:

```bash
docker compose logs | grep -i encoding
# → Encoding on the GPU (h264_nvenc, CUDA_VISIBLE_DEVICES=…)
nvidia-smi     # while something is playing: an ffmpeg using the encoder
```

With more than one card, set `TABLOWEB_GPU` to a UUID from `nvidia-smi -L`. Not an index: CUDA
orders devices by capability by default, so an index can select a different card from the one you
meant, and quietly take an encoder away from whatever else was using it.

## Intel Quick Sync and AMD (VA-API)

```bash
docker compose -f docker-compose.yml -f docker-compose.vaapi.yml up -d
```

This only needs `/dev/dri` passed through. If `renderD128` is not the right node — a machine with
integrated *and* discrete graphics usually has `renderD129` too — set `TABLOWEB_VAAPI_DEVICE`.
`vainfo --display drm --device /dev/dri/renderD128` on the host tells you which is which.

The container runs as root, which can open the render node. If you run it as a normal user
instead, that user needs the host's `render` group by numeric gid (`group_add: ["993"]` or
whatever `getent group render` says) — group *names* inside the container mean nothing.

## Putting it on the internet

The sign-in is the only thing between the internet and your recordings. If you expose this:

- **Terminate TLS in front of it** and set `TABLOWEB_COOKIE_SECURE=1`.
- **Pass through `X-Forwarded-For` and `X-Forwarded-Proto`.** The app honours both. Without the
  first, every viewer looks local and gets the high-bitrate profile through your upload link;
  without the second, the app cannot tell that the browser is on https.
- **Turn off proxy buffering and raise the timeouts.** This is carrying HLS segments, and a fresh
  transcode takes several seconds to produce its first playlist.

An nginx location that does all of that:

```nginx
location / {
    proxy_pass http://127.0.0.1:8787;
    proxy_http_version 1.1;
    proxy_set_header Host              $host;
    proxy_set_header X-Real-IP         $remote_addr;
    proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_buffering off;
    proxy_read_timeout 300s;
    proxy_send_timeout 300s;
}
```

Consider also whether the internet needs to reach it at all: a VPN back into the house (Tailscale,
WireGuard, headscale) gives the same result with nothing exposed, and addresses in
`100.64.0.0/10` are already treated as local, so VPN viewers get full quality.

## Updating

```bash
git pull
docker compose up -d --build
```

Sessions survive if `/config` is mounted. Anything that was playing stops — the container is
replaced, and with it every ffmpeg.

**Restart once and leave it.** Each start kicks off a full guide load in the background, which is
hundreds of calls to a small appliance. Doing that twice in quick succession has been enough to
make a Tablo stop answering altogether for ten minutes.

## Health

`/healthz` answers `{"ok":true}` as soon as the web server is up, without asking the DVR
anything. That is deliberate: a Tablo that is unreachable, or still loading its guide, is not the
container being unhealthy, and a health check that fails then would restart the app into exactly
the loop it should be avoiding.
