using TabloWeb.Models;
using TabloWeb.Services;

namespace TabloWeb;

/// <summary>
/// Owns the single connection to the Tablo and everything cached from it.
///
/// The Tablo is a small appliance and it is shared: the phone apps and anything else on the
/// account talk to the same box, and a full guide load is tens of thousands of airings across
/// hundreds of batch calls. Hammering it makes it refuse connections outright, so this class is
/// deliberately stingy — one connection, one in-flight load of any given thing, and generous
/// cache lifetimes. Every browser that opens the site is served from memory.
/// </summary>
public sealed class TabloSession(ILogger<TabloSession> log)
{
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private TabloClient? _client;
    private Credentials? _credentials;

    /// <summary>Human-readable connection state, surfaced to the UI so a cold start explains itself.</summary>
    public string State { get; private set; } = "Starting up…";
    public bool Connected => _client is not null;
    public ServerInfo? ServerInfo => _client?.ServerInfo;
    public TabloDevice? Device => _client?.Device;

    /// <summary>Nobody has signed in and there is nothing saved, so there is no DVR to talk to yet.</summary>
    public bool NeedsCredentials => _credentials is null;
    public string? AccountEmail => _credentials?.Email;

    private readonly Cache<List<GuideChannelWrap>> _channels = new(TimeSpan.FromHours(6));
    private readonly Cache<List<RecordingAiring>> _recordings = new(TimeSpan.FromMinutes(2));
    private readonly Cache<List<GuideAiring>> _guide = new(TimeSpan.FromHours(6));
    private readonly Cache<StorageBox> _storage = new(TimeSpan.FromMinutes(5));

    /// <summary>Progress of a guide load in flight, 0..1, or null when not loading.</summary>
    public double? GuideProgress { get; private set; }

    // ------------------------------------------------------------------ credentials

    /// <summary>
    /// Take whatever credentials are available without a human: the environment first (which is
    /// how a container is configured), then whatever a previous sign-in chose to remember. Either
    /// way the guide starts warming immediately instead of waiting for the first visitor.
    /// </summary>
    public void Restore()
    {
        var email = Environment.GetEnvironmentVariable("TABLO_EMAIL");
        var password = Environment.GetEnvironmentVariable("TABLO_PASSWORD");

        if (!string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(password))
        {
            _credentials = new Credentials(email.Trim(), password,
                Environment.GetEnvironmentVariable("TABLO_SERVER_ID"));
            log.LogInformation("Using the Tablo credentials from the environment ({Email})", _credentials.Email);
            return;
        }

        if (CredentialStore.Load(log) is { } saved)
        {
            _credentials = saved;
            log.LogInformation("Using the saved Tablo credentials ({Email})", saved.Email);
            return;
        }

        State = "Waiting for a Tablo sign-in.";
        log.LogInformation("No Tablo credentials yet — sign in through the site to connect.");
    }

    /// <summary>
    /// Check credentials against the Tablo cloud and, unless we are already connected with them,
    /// connect the server to the DVR.
    ///
    /// The cloud login is the real authentication: signing in to this site *is* proving you can
    /// sign in to the Tablo account. When the account owns more than one DVR and none was chosen,
    /// this returns the list instead of guessing.
    /// </summary>
    public async Task<SignInResult> SignInAsync(Credentials credentials, CancellationToken ct)
    {
        var client = new TabloClient();
        await client.LoginAsync(credentials.Email, credentials.Password, ct);

        // Already serving this account and this box? Then the caller has proved who they are and
        // there is nothing to rebuild — leave the live connection and its caches alone. Rebuilding
        // would throw away a guide that took minutes to load, and reload it off a busy device.
        if (_client is { Device: not null } live &&
            string.Equals(_credentials?.Email, credentials.Email, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(credentials.ServerId) || credentials.ServerId == live.Device.ServerId))
        {
            _credentials = _credentials! with { Password = credentials.Password };
            return new SignInResult(null, live.Device.ServerId, live.Device.Name);
        }

        var device = await ChooseDeviceAsync(client, credentials.ServerId, ct);
        if (device is null)
        {
            // Several reachable devices and no choice made — hand back the menu.
            var reachable = await ReachableAsync(client, ct);
            return new SignInResult(
                reachable.Select(d => new DeviceOption(d.ServerId, d.Name, d.Host)).ToList(), null, null);
        }

        await client.SelectDeviceAsync(client.Account!.Profiles.First(), device, ct);

        _credentials = credentials with { ServerId = device.ServerId };
        Install(client, device);
        return new SignInResult(null, device.ServerId, device.Name);
    }

    /// <summary>Forget the connection and the credentials — the site goes back to asking for a sign-in.</summary>
    public void Disconnect()
    {
        _client = null;
        _credentials = null;
        _channels.Clear();
        _recordings.Clear();
        _guide.Clear();
        _storage.Clear();
        State = "Waiting for a Tablo sign-in.";
    }

    private void Install(TabloClient client, TabloDevice device)
    {
        var replacing = _client is not null;
        _client = client;
        if (replacing)
        {
            // A different box means everything cached describes the wrong DVR.
            _channels.Clear();
            _recordings.Clear();
            _guide.Clear();
            _storage.Clear();
        }
        State = $"Connected to {device.Display}";
        log.LogInformation("Connected to {Device} ({Model})", device.Display, client.ServerInfo?.Model.Name);
    }

    // ------------------------------------------------------------------ connection

    /// <summary>
    /// Get a connected client, connecting on first use. Concurrent callers share one attempt
    /// rather than each starting their own login.
    /// </summary>
    public async Task<TabloClient> ClientAsync(CancellationToken ct = default)
    {
        if (_client is { } ready) return ready;

        await _connectGate.WaitAsync(ct);
        try
        {
            if (_client is { } racedIn) return racedIn;
            _client = await ConnectAsync(ct);
            return _client;
        }
        finally { _connectGate.Release(); }
    }

    private async Task<TabloClient> ConnectAsync(CancellationToken ct)
    {
        if (_credentials is not { } credentials)
        {
            State = "Waiting for a Tablo sign-in.";
            throw new InvalidOperationException("Nobody has signed in to a Tablo account yet.");
        }

        try
        {
            State = "Signing in to the Tablo account…";
            var client = new TabloClient();
            await client.LoginAsync(credentials.Email, credentials.Password, ct);

            State = "Looking for a Tablo on the network…";
            var device = await ChooseDeviceAsync(client, credentials.ServerId, ct)
                         ?? (await ReachableAsync(client, ct)).First();
            await client.SelectDeviceAsync(client.Account!.Profiles.First(), device, ct);

            State = $"Connected to {device.Display}";
            log.LogInformation("Connected to {Device} ({Model})", device.Display, client.ServerInfo?.Model.Name);
            return client;
        }
        catch (Exception ex)
        {
            State = "Not connected: " + ex.Message;
            log.LogWarning(ex, "Connect failed");
            throw;
        }
    }

    /// <summary>
    /// Which DVR to talk to. Returns null when the account has several that answer and none was
    /// asked for, so the caller can offer the choice rather than picking one at random.
    /// </summary>
    private static async Task<TabloDevice?> ChooseDeviceAsync(TabloClient client, string? serverId, CancellationToken ct)
    {
        var reachable = await ReachableAsync(client, ct);
        if (!string.IsNullOrWhiteSpace(serverId))
            return reachable.FirstOrDefault(d => d.ServerId == serverId)
                   ?? throw new InvalidOperationException(
                       "The Tablo that was chosen is not answering on this network.");
        return reachable.Count == 1 ? reachable[0] : null;
    }

    /// <summary>
    /// The devices on the account that actually answer. An account keeps listing units that were
    /// returned or replaced, so probe rather than trusting the order the cloud gives them in.
    /// </summary>
    private static async Task<List<TabloDevice>> ReachableAsync(TabloClient client, CancellationToken ct)
    {
        var devices = client.Account!.Devices;
        var probes = await Task.WhenAll(devices.Select(async d =>
            (device: d, info: await TabloClient.ProbeAsync(d, ct))));
        var reachable = probes.Where(p => p.info is not null).Select(p => p.device).ToList();

        if (reachable.Count == 0)
            throw new InvalidOperationException(devices.Count == 0
                ? "That account has no Tablo on it."
                : $"None of the {devices.Count} Tablo(s) on the account answered on this network. " +
                  "This server has to be on the same network as the DVR.");
        return reachable;
    }

    /// <summary>
    /// Drop the connection so the next call signs in again. The Lighthouse token does expire,
    /// and the failure mode is a 401 on an otherwise valid request.
    /// </summary>
    public void Invalidate()
    {
        _client = null;
        State = "Reconnecting…";
    }

    /// <summary>
    /// Run a device call, signing in again once if the token has expired. Anything else
    /// propagates — a caller that gets an error should see the real one.
    /// </summary>
    public async Task<T> WithRetryAsync<T>(Func<TabloClient, Task<T>> work, CancellationToken ct = default)
    {
        try
        {
            return await work(await ClientAsync(ct));
        }
        catch (TabloHttpException ex) when (ex.Status is 401 or 403)
        {
            log.LogInformation("Device returned {Status}; re-authenticating", ex.Status);
            Invalidate();
            return await work(await ClientAsync(ct));
        }
    }

    // ------------------------------------------------------------------ cached content

    public Task<List<GuideChannelWrap>> ChannelsAsync(bool force = false, CancellationToken ct = default) =>
        _channels.GetAsync(force, () => WithRetryAsync(c => c.GetChannelsAsync(ct), ct));

    public Task<List<RecordingAiring>> RecordingsAsync(bool force = false, CancellationToken ct = default) =>
        _recordings.GetAsync(force, () => WithRetryAsync(c => c.GetRecordingsAsync(ct), ct));

    public Task<List<GuideAiring>> GuideAsync(bool force = false, CancellationToken ct = default) =>
        _guide.GetAsync(force, async () =>
        {
            var progress = new Progress<double>(p => GuideProgress = p);
            GuideProgress = 0;
            try
            {
                var airings = await WithRetryAsync(c => c.GetGuideAiringsAsync(progress, ct), ct);
                log.LogInformation("Guide loaded: {Count} airings", airings.Count);
                return airings;
            }
            finally { GuideProgress = null; }
        });

    /// <summary>
    /// Recording space. Wrapped because the cache holds reference types and the endpoint may
    /// legitimately have nothing to report on some firmware.
    /// </summary>
    public Task<StorageBox> StorageAsync(CancellationToken ct = default) =>
        _storage.GetAsync(false, async () =>
        {
            var info = await WithRetryAsync(c => c.GetStorageAsync(ct), ct);
            if (info is null)
            {
                // GetStorageAsync swallows everything and returns null, which used to leave the
                // page reporting zero bytes with nothing in the log to explain it.
                var raw = await WithRetryAsync(c => c.DeviceRawGetAsync("/server/harddrives", ct), ct);
                log.LogWarning("No storage figures: /server/harddrives answered {Status}: {Body}",
                    raw.Status, raw.Body.Length > 300 ? raw.Body[..300] : raw.Body);
            }
            return new StorageBox(info?.Normalized());
        }, box => box.Info is not null);

    /// <summary>True while the guide has never finished loading — the UI shows a progress bar.</summary>
    public bool GuideReady => _channels.HasValue && _guide.HasValue;

    /// <summary>
    /// Warm the caches in the background so the first visitor doesn't wait out a full guide load.
    /// Failures are logged and retried on the next pass, never fatal.
    /// </summary>
    public async Task WarmAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Nothing to warm until somebody has signed in. Wait quietly rather than logging a
            // failure a minute forever on a fresh install.
            if (!NeedsCredentials)
            {
                try
                {
                    await ChannelsAsync(ct: ct);
                    await RecordingsAsync(ct: ct);
                    await GuideAsync(ct: ct);
                }
                // Only a real shutdown ends the loop. An HttpClient timeout also surfaces as a
                // TaskCanceledException, and treating that as shutdown silently killed the warmer —
                // leaving the guide, which nothing else loads, spinning forever in the browser.
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    log.LogWarning("Warm-up failed ({Message}); will retry", ex.Message);
                }
            }

            // Poll often enough to notice an expired cache promptly, but the caches themselves
            // decide when real work happens.
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
            catch (OperationCanceledException) { return; }
        }

        log.LogInformation("Warm-up loop stopped");
    }
}

public sealed record StorageBox(StorageInfo? Info);

/// <summary>
/// The outcome of a sign-in: either the DVR that is now connected, or — when the account has
/// several and none was chosen — the list to choose from.
/// </summary>
public sealed record SignInResult(List<DeviceOption>? Devices, string? ServerId, string? DeviceName);

public sealed record DeviceOption(string ServerId, string Name, string Host);

/// <summary>
/// A value with a lifetime, refreshed by at most one caller at a time. While a refresh is in
/// flight other callers get the stale value if there is one, so a slow guide reload never
/// stalls a page load.
/// </summary>
internal sealed class Cache<T>(TimeSpan lifetime) where T : class
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private T? _value;
    private DateTime _loadedUtc = DateTime.MinValue;

    public bool HasValue => _value is not null;
    public bool Fresh => _value is not null && DateTime.UtcNow - _loadedUtc < lifetime;

    /// <summary>How soon to try again after a load that came back empty.</summary>
    private static readonly TimeSpan RetrySoon = TimeSpan.FromSeconds(30);

    /// <param name="valid">
    /// Optional test for "this load actually worked". A value that fails it is still returned,
    /// but expires in seconds rather than being trusted for the full lifetime — which is what
    /// a device call that lost a race with the guide load deserves.
    /// </param>
    public async Task<T> GetAsync(bool force, Func<Task<T>> load, Func<T, bool>? valid = null)
    {
        if (!force && Fresh) return _value!;

        // Someone else is already refreshing. If we have anything at all, hand back the stale
        // copy instead of queueing — a page load must never wait out a guide reload.
        if (!_gate.Wait(0))
        {
            if (_value is not null && !force) return _value;
            await _gate.WaitAsync();
        }

        try
        {
            if (!force && Fresh) return _value!;
            _value = await load();
            _loadedUtc = valid is null || valid(_value)
                ? DateTime.UtcNow
                : DateTime.UtcNow - lifetime + RetrySoon;
            return _value;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Throw the value away — the DVR it described is no longer the one we talk to.</summary>
    public void Clear()
    {
        _value = null;
        _loadedUtc = DateTime.MinValue;
    }
}
