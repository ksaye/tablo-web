# The Tablo 4th-generation protocol

Notes on how the 4th-generation Tablo API works, written down because there is no public
documentation for it. Everything here was worked out by watching the official apps, and verified
against a **Tablo 4G QUAD on firmware 2.2.58**. The implementation is
[`src/TabloWeb.Core/Services/TabloClient.cs`](../src/TabloWeb.Core/Services/TabloClient.cs).

This is the 4th generation only — the earlier Tablos speak something entirely different.

There are two halves: a cloud service that authenticates you, and the DVR itself, which is on
your network and does all the real work.

## 1. Cloud login

```
POST https://lighthousetv.ewscloud.com/api/v2/login/
{"email": "...", "password": "..."}
→ {"token_type": "Bearer", "access_token": "…"}
```

A failure comes back as `200` with a `code` and a `message` rather than an HTTP error, so check
the body rather than the status.

```
GET /api/v2/account/          Authorization: Bearer …
→ { profiles: [{identifier, name}], devices: [{name, serverId, url}] }
```

`url` is the DVR's address **on your network** — `http://192.168.x.x:8887`. The cloud is
effectively a directory service: it tells you where your DVR is and gives you a token to talk to
it, and after that everything is local.

Accounts keep listing hardware that was returned or replaced, so probe each device before
trusting it (see below) rather than assuming the first one is real.

```
POST /api/v2/account/select/  Authorization: Bearer …
{"pid": "<profile identifier>", "sid": "<serverId>"}
→ {"token": "…"}
```

That token — the *Lighthouse* token — is what the DVR itself wants. It expires; a valid request
suddenly returning 401 or 403 means log in again.

## 2. Talking to the DVR

Everything else is HTTP on port **8887** on your own network, and every request is signed.

```
Date:          Wed, 16 Jul 2025 14:03:22 GMT
User-Agent:    Tablo-FAST/1.7.0 (Mobile; iPhone; iOS 18.4)
Lighthouse:    <token from /account/select/>
Authorization: tablo:<device key>:<hmac>
```

The signature is HMAC-MD5, hex, lower case, over four lines:

```
METHOD \n PATH \n md5(body, or "" when there is none) \n Date
```

keyed with a fixed string. Both that key and the device key in the `Authorization` header are
constants shared by all the Tablo apps — they identify *the app*, not you. The per-account gate
is the Lighthouse token layered on top.

```
HashKey    6l8jU5N43cEilqItmT3U2M2PFM3qPziilXqau9ys
DeviceKey  ljpg6ZkwShVv8aI12E2LP55Ep8vq1uYDPvX0DdTB
```

Three things will bite you here:

- **The `Date` header is part of the signature.** Rebuild it, and the signature, on every retry.
- **The body must actually be sent** for anything signed over `md5(body)` — a `POST` whose body
  you dropped is a 401, not an empty request.
- **A missing `User-Agent` is a 403**, even on endpoints that need no authentication at all. .NET's
  `HttpClient` sends none by default, which makes a healthy DVR look like a dead one.

### Probing

```
GET /server/info        (no authentication needed, User-Agent still required)
→ {server_id, name, version, model: {name, tuners, …}}
```

Any answer at all means the box is alive. Some endpoint-security products refuse an unknown
program's *first* connection while they evaluate it, so retry a couple of times before concluding
a device is offline.

### Listing things

The pattern throughout is **paths, then a batch resolve**:

```
GET  /guide/channels        → ["/guide/channels/123", …]
GET  /guide/airings         → ["/guide/airings/456", …]   (12,000-20,000 of them)
GET  /recordings/airings    → ["/recordings/airings/789", …]

POST /batch  ["path", "path", …]   (50 at a time)
→ {"path": {…object…}, …}
```

The guide is the expensive one: 12,000 to 20,000 airings over hundreds of batch calls, and several
minutes when the box is busy. Keep concurrency modest — four in flight is comfortable, more and
the device starts dropping connections — and cache the result for hours, not minutes. A partial
guide is better than none, so let a failed batch fail alone rather than aborting the lot.

### Watching

```
POST /guide/channels/123/watch          → {playlist_url: "http://…/…m3u8", …}
POST /recordings/airings/789/watch      → same
```

The playlist is HLS, and the payload inside it is **MPEG-2 video with AC3 audio**, so it is not
something a browser can play. The request needs the same `User-Agent`, or you get a 403.

**Starting a live stream ties up a tuner for as long as something keeps fetching segments.** There
is no explicit "stop" — stop asking and the DVR eventually releases it. Anything long-lived
should reap idle sessions itself.

A tuner that cannot lock the signal answers:

```
503 {"code":"playback_failed","details":"no_signal_lock"}
```

**How long that takes is the useful signal.** A refusal in about a second is a genuine one —
retrying it just repeats the answer. A refusal that takes ~40 seconds means the tuner really did
try, and often means the device itself is unwell rather than the channel being unreceivable. See
[troubleshooting.md](troubleshooting.md).

### Scheduling and other writes

```
PATCH /guide/airings/456          {"scheduled": true}
PATCH /guide/series/12            {"schedule": {"rule": "all" | "new" | "none"}}
PATCH /recordings/airings/789     {"user_info": {"protected": true}}
DELETE /recordings/airings/789
```

(Implemented in `TabloClient`, though this site does not use them — it is read-only.)

### Odds and ends

```
GET /server/harddrives    → recording storage; the shape varies by firmware, so walk it
                            looking for size/free numbers rather than binding a model
GET /server/tuners        → do not trust `in_use`. It has been observed reading "4 of 8"
                            with nothing playing and with three streams running alike
GET /images/<id>          → snapshot artwork, unauthenticated
```

## Rate of fire

The DVR is a small appliance with a small appliance's patience. What it does when overloaded is
not to return errors — it stops answering HTTP entirely while still accepting TCP connections and
replying to pings, which makes it look like a network problem for the fifteen minutes or so it
takes to recover.

Two things reliably provoke it: a full guide load twice in quick succession, and scanning many
channels for signal in a row (each failure holds a tuner for ~40 seconds). Space out anything
that touches tuners by several seconds, and treat the guide as something you load once a day.
