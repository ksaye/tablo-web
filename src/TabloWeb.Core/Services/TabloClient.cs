using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TabloWeb.Models;

namespace TabloWeb.Services;

/// <summary>
/// Talks to the Tablo cloud (lighthousetv.ewscloud.com) to authenticate, then to the
/// local Gen-4 device on port 8887 with HMAC-MD5 signed requests. Protocol verified
/// live against a Tablo 4G QUAD (firmware 2.2.58).
/// </summary>
public sealed class TabloClient
{
    private const string LighthouseHost = "https://lighthousetv.ewscloud.com";
    // Shared client keys the Gen-4 apps sign with. Not per-account secrets — they are the
    // app's identity; the per-account gate is the Lighthouse token added on top.
    private const string HashKey = "6l8jU5N43cEilqItmT3U2M2PFM3qPziilXqau9ys";
    private const string DeviceKey = "ljpg6ZkwShVv8aI12E2LP55Ep8vq1uYDPvX0DdTB";
    private const string DeviceUserAgent = "Tablo-FAST/1.7.0 (Mobile; iPhone; iOS 18.4)";
    private const string CloudUserAgent = "Tablo-FAST/2.0.0 (Mobile; iPhone; iOS 16.6)";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // Bypass any system HTTP proxy: Tablo devices are always LAN-local, and the cloud is
    // reachable directly on a normal home network. A configured proxy would route the LAN
    // device IP to the proxy and fail (cloud login works, every device call 000s).
    private static HttpClient NoProxyClient(int timeoutSeconds) =>
        new(new SocketsHttpHandler { UseProxy = false, Proxy = null })
        { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

    private readonly HttpClient _http = NoProxyClient(30);
    // Short-timeout client just for probing device reachability at startup.
    private static readonly HttpClient _probe = NoProxyClient(6);

    private string? _cloudAuthorization;   // "Bearer xxx"
    private string? _lighthouse;           // per device+profile token

    public AccountResponse? Account { get; private set; }
    public TabloDevice? Device { get; private set; }
    public ServerInfo? ServerInfo { get; private set; }

    private string DeviceBase => Device?.Url.TrimEnd('/')
        ?? throw new InvalidOperationException("No device selected.");

    /// <summary>
    /// Is this device actually reachable on the LAN? On port 8887 /server/info answers
    /// unauthenticated, so any HTTP response means the box is online. Used to skip dead
    /// devices that are still listed on the account (e.g. returned/defective units).
    /// </summary>
    public static async Task<ServerInfo?> ProbeAsync(TabloDevice device, CancellationToken ct = default)
    {
        var url = device.Url.TrimEnd('/') + "/server/info";
        // Retry a few times: some endpoint-security products refuse an unknown app's first
        // connection while they evaluate it, so a single failure doesn't mean "offline".
        for (int i = 0; i < 4; i++)
        {
            try
            {
                // The User-Agent is not optional. /server/info needs no authentication, but
                // firmware 2.2.58 answers 403 to a request that carries no User-Agent at all —
                // and HttpClient sends none by default. Without this the probe reports every
                // device as offline and the app says "No Tablo responding" on a healthy LAN.
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", DeviceUserAgent);
                req.Headers.Accept.ParseAdd("*/*");
                using var res = await _probe.SendAsync(req, ct);
                if (res.IsSuccessStatusCode)
                    return JsonSerializer.Deserialize<ServerInfo>(await res.Content.ReadAsStringAsync(ct), Json);
            }
            catch { /* connection refused / timeout / DNS — try again */ }
            if (i < 3) await Task.Delay(350, ct);
        }
        return null;
    }

    // ---------------------------------------------------------------- auth

    /// <summary>Log in and load the account (profiles + devices). Does not pick a device.</summary>
    public async Task LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { password, email });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{LighthouseHost}/api/v2/login/")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("User-Agent", CloudUserAgent);
        req.Headers.Accept.ParseAdd("*/*");

        var login = await SendJsonAsync<LoginResponse>(req, ct);
        if (login is null || login.Code is not null || string.IsNullOrEmpty(login.AccessToken))
            throw new TabloAuthException(login?.Message ?? "Login was not accepted.");

        _cloudAuthorization = $"{login.TokenType} {login.AccessToken}";
        Account = await GetAccountAsync(ct)
                  ?? throw new TabloAuthException("Could not load account details.");
    }

    private async Task<AccountResponse?> GetAccountAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{LighthouseHost}/api/v2/account/");
        req.Headers.TryAddWithoutValidation("User-Agent", CloudUserAgent);
        req.Headers.TryAddWithoutValidation("Authorization", _cloudAuthorization);
        req.Headers.Accept.ParseAdd("*/*");
        return await SendJsonAsync<AccountResponse>(req, ct);
    }

    /// <summary>Select profile+device and mint the Lighthouse token, then read /server/info.</summary>
    public async Task SelectDeviceAsync(Profile profile, TabloDevice device, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { pid = profile.Identifier, sid = device.ServerId });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{LighthouseHost}/api/v2/account/select/")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("User-Agent", CloudUserAgent);
        req.Headers.TryAddWithoutValidation("Authorization", _cloudAuthorization);
        req.Headers.Accept.ParseAdd("*/*");

        var sel = await SendJsonAsync<SelectResponse>(req, ct);
        if (sel?.Token is null) throw new TabloAuthException("Account token was not returned.");

        _lighthouse = sel.Token;
        Device = device;
        ServerInfo = await DeviceGetAsync<ServerInfo>("/server/info", ct);
    }

    // -------------------------------------------------------------- signing

    private static string DeviceDate() =>
        DateTime.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture);

    private static string Md5Hex(string s)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string DeviceAuth(string method, string path, string msg, string date)
    {
        var body = string.IsNullOrEmpty(msg) ? "" : Md5Hex(msg);
        var full = $"{method}\n{path}\n{body}\n{date}";
        var key = Encoding.UTF8.GetBytes(HashKey);
        var mac = new HMACMD5(key).ComputeHash(Encoding.UTF8.GetBytes(full));
        return $"tablo:{DeviceKey}:{Convert.ToHexString(mac).ToLowerInvariant()}";
    }

    private HttpRequestMessage BuildDeviceRequest(HttpMethod method, string path, string? jsonBody)
    {
        var date = DeviceDate();
        var msg = jsonBody ?? "";
        var req = new HttpRequestMessage(method, DeviceBase + path);
        // Attach the body for any method that carries one (POST/PATCH/PUT). The auth header
        // is signed over md5(body), so the body MUST actually be sent or the device 401s.
        if (jsonBody is not null && method != HttpMethod.Get && method != HttpMethod.Delete)
            req.Content = new StringContent(msg, Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("Date", date);
        req.Headers.TryAddWithoutValidation("User-Agent", DeviceUserAgent);
        req.Headers.Accept.ParseAdd("*/*");
        req.Headers.TryAddWithoutValidation("Authorization", DeviceAuth(method.Method, path, msg, date));
        req.Headers.TryAddWithoutValidation("Lighthouse", _lighthouse);
        return req;
    }

    /// <summary>
    /// Send a signed device request, rebuilding it (fresh Date + signature) on each try, and
    /// retry transient connection failures. Some endpoint-security products refuse the FIRST
    /// connection from an unknown app while they evaluate it, then allow retries — so a single
    /// "connection refused" shouldn't fail the operation.
    /// </summary>
    private async Task<HttpResponseMessage> SendDeviceAsync(
        HttpMethod method, string path, string? jsonBody, CancellationToken ct, int attempts = 5)
    {
        for (int i = 0; ; i++)
        {
            var req = BuildDeviceRequest(method, path, jsonBody);
            try
            {
                return await _http.SendAsync(req, ct);
            }
            catch (HttpRequestException) when (i < attempts - 1)
            {
                req.Dispose();
                await Task.Delay(300, ct);
            }
            catch (TaskCanceledException) when (i < attempts - 1 && !ct.IsCancellationRequested)
            {
                req.Dispose();   // per-request timeout, not a caller cancel
                await Task.Delay(300, ct);
            }
        }
    }

    private async Task<T?> DeviceGetAsync<T>(string path, CancellationToken ct)
    {
        using var res = await SendDeviceAsync(HttpMethod.Get, path, null, ct);
        return await ReadJsonAsync<T>(res, ct);
    }

    private async Task<string> DeviceGetRawAsync(string path, CancellationToken ct)
    {
        using var res = await SendDeviceAsync(HttpMethod.Get, path, null, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    private async Task<T?> DevicePostAsync<T>(string path, string jsonBody, CancellationToken ct)
    {
        using var res = await SendDeviceAsync(HttpMethod.Post, path, jsonBody, ct);
        return await ReadJsonAsync<T>(res, ct);
    }

    // -------------------------------------------------------------- content

    /// <summary>List recording airing paths (individual recorded programs).</summary>
    public async Task<List<string>> GetRecordingAiringPathsAsync(CancellationToken ct = default) =>
        await DeviceGetAsync<List<string>>("/recordings/airings", ct) ?? new();

    /// <summary>Resolve a set of paths to their detail objects via /batch.</summary>
    public async Task<Dictionary<string, JsonElement>> BatchAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var list = paths.ToList();
        if (list.Count == 0) return new();
        var result = new Dictionary<string, JsonElement>();
        // Device caps batch size; chunk to be safe.
        foreach (var chunk in Chunk(list, 50))
        {
            var json = JsonSerializer.Serialize(chunk);
            var doc = await DevicePostAsync<JsonElement>("/batch", json, ct);
            if (doc is { ValueKind: JsonValueKind.Object } el)
                foreach (var prop in el.EnumerateObject())
                    result[prop.Name] = prop.Value.Clone();
        }
        return result;
    }

    public async Task<List<RecordingAiring>> GetRecordingsAsync(CancellationToken ct = default)
    {
        var paths = await GetRecordingAiringPathsAsync(ct);
        var batch = await BatchAsync(paths, ct);
        var recs = new List<RecordingAiring>();
        foreach (var kv in batch)
        {
            try
            {
                var r = kv.Value.Deserialize<RecordingAiring>(Json);
                if (r is not null) recs.Add(r);
            }
            catch { /* skip malformed */ }
        }
        return recs
            .OrderByDescending(r => ParseDate(r.AiringDetails.Datetime))
            .ToList();
    }

    public async Task<List<GuideChannelWrap>> GetChannelsAsync(CancellationToken ct = default)
    {
        var paths = await DeviceGetAsync<List<string>>("/guide/channels", ct) ?? new();
        var batch = await BatchAsync(paths, ct);
        var chans = new List<GuideChannelWrap>();
        foreach (var kv in batch)
        {
            try
            {
                var c = kv.Value.Deserialize<GuideChannelWrap>(Json);
                if (c is not null) chans.Add(c);
            }
            catch { /* skip */ }
        }
        return chans
            .OrderBy(c => c.Channel.Major).ThenBy(c => c.Channel.Minor)
            .ToList();
    }

    // ---- Guide + scheduling ----

    /// <summary>
    /// Load the full guide (all airings, ~14 days) and resolve details via concurrent /batch
    /// calls (device caps each batch at 50). Reports progress 0..1. Cached per instance.
    /// </summary>
    public async Task<List<GuideAiring>> GetGuideAiringsAsync(
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var paths = await DeviceGetAsync<List<string>>("/guide/airings", ct) ?? new();
        var chunks = Chunk(paths, 50).ToList();
        var results = new List<GuideAiring>(paths.Count);
        var done = 0;
        var sync = new object();

        // Keep concurrency modest — the device is a small appliance and drops connections
        // if hit too hard. Retry a failed batch a couple of times, and never let one bad
        // batch abort the whole guide (partial guide is better than none).
        using var gate = new SemaphoreSlim(4);
        var tasks = chunks.Select(async chunk =>
        {
            await gate.WaitAsync(ct);
            try
            {
                JsonElement? doc = null;
                var json = JsonSerializer.Serialize(chunk);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try { doc = await DevicePostAsync<JsonElement>("/batch", json, ct); break; }
                    catch when (attempt < 2) { await Task.Delay(200 * (attempt + 1), ct); }
                    catch { doc = null; }   // give up on this batch
                }

                var local = new List<GuideAiring>();
                if (doc is { ValueKind: JsonValueKind.Object } el)
                    foreach (var prop in el.EnumerateObject())
                    {
                        try
                        {
                            var a = prop.Value.Deserialize<GuideAiring>(Json);
                            if (a is not null) { a.Path = prop.Name; local.Add(a); }
                        }
                        catch { /* skip */ }
                    }
                lock (sync)
                {
                    results.AddRange(local);
                    done++;
                    progress?.Report((double)done / chunks.Count);
                }
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        return results;
    }

    /// <summary>Schedule or unschedule a single guide airing.</summary>
    public async Task ScheduleAiringAsync(string airingPath, bool scheduled, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { scheduled });
        using var res = await SendDeviceAsync(HttpMethod.Patch, airingPath, body, ct);
        if (!res.IsSuccessStatusCode)
            throw new TabloHttpException((int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Set a series recording rule: "all", "new", or "none".</summary>
    public async Task SetSeriesRuleAsync(string seriesPath, string rule, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { schedule = new { rule } });
        using var res = await SendDeviceAsync(HttpMethod.Patch, seriesPath, body, ct);
        if (!res.IsSuccessStatusCode)
            throw new TabloHttpException((int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Read a single series object (for its current rule).</summary>
    public async Task<GuideSeries?> GetSeriesAsync(string seriesPath, CancellationToken ct = default)
    {
        var batch = await BatchAsync(new[] { seriesPath }, ct);
        return batch.TryGetValue(seriesPath, out var el) ? el.Deserialize<GuideSeries>(Json) : null;
    }

    /// <summary>Ask the device to start a stream. Works for both recording paths and channel paths.</summary>
    public Task<WatchResponse?> WatchAsync(string path, CancellationToken ct = default) =>
        DevicePostAsync<WatchResponse>(path.TrimEnd('/') + "/watch", "", ct);

    /// <summary>Delete a recording (path like /recordings/series/episodes/NNN).</summary>
    public async Task DeleteRecordingAsync(string path, CancellationToken ct = default)
    {
        using var res = await SendDeviceAsync(HttpMethod.Delete, path, null, ct);
        res.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Mark a recording protected (Tablo's "keep") so nothing — including the autopick
    /// service — will delete it. Protected recordings are permanently out of the
    /// autopicker's control.
    /// </summary>
    public async Task SetProtectedAsync(string recordingPath, bool isProtected, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { user_info = new { @protected = isProtected } });
        using var res = await SendDeviceAsync(HttpMethod.Patch, recordingPath, body, ct);
        if (!res.IsSuccessStatusCode)
            throw new TabloHttpException((int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
    }

    // -------------------------------------------------------------- storage

    /// <summary>
    /// Total/free bytes on the device's recording storage, read from /server/harddrives.
    ///
    /// The exact JSON shape of that endpoint isn't documented and differs across firmware,
    /// so rather than binding to a fixed model we walk the response and pick up the first
    /// plausible capacity/free numbers we find (see <see cref="ScanStorage"/>). Returns null
    /// if the endpoint is missing or nothing recognisable came back — callers must fall back
    /// to a configured capacity in that case.
    /// </summary>
    public async Task<StorageInfo?> GetStorageAsync(CancellationToken ct = default)
    {
        JsonElement doc;
        try
        {
            using var res = await SendDeviceAsync(HttpMethod.Get, "/server/harddrives", null, ct);
            if (!res.IsSuccessStatusCode) return null;
            var text = await res.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(text)) return null;
            doc = JsonDocument.Parse(text).RootElement.Clone();
        }
        catch { return null; }

        long total = 0, free = 0;
        ScanStorage(doc, ref total, ref free);
        return total > 0 ? new StorageInfo(total, free, doc) : null;
    }

    /// <summary>
    /// Recursively look for size/free numbers under any of the names firmware has been seen
    /// to use. Values are summed so a multi-drive box reports its whole pool.
    /// </summary>
    private static void ScanStorage(JsonElement el, ref long total, ref long free)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    var name = p.Name.ToLowerInvariant();
                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var n))
                    {
                        if (name is "size" or "total" or "capacity" or "total_size" or "size_bytes") total += n;
                        else if (name is "free" or "available" or "remaining" or "free_size" or "free_bytes") free += n;
                    }
                    else ScanStorage(p.Value, ref total, ref free);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) ScanStorage(item, ref total, ref free);
                break;
        }
    }

    /// <summary>
    /// Signed GET returning the raw status and body. For diagnostics: when a typed call comes
    /// back empty it is the only way to see whether the device refused, answered oddly, or
    /// simply returned a shape we don't parse.
    /// </summary>
    public async Task<(int Status, string Body)> DeviceRawGetAsync(string path, CancellationToken ct = default)
    {
        try
        {
            using var res = await SendDeviceAsync(HttpMethod.Get, path, null, ct);
            return ((int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            return (0, ex.Message);
        }
    }

    /// <summary>Snapshot image for a recording — served unauthenticated on 8887 at /images/{id}.</summary>
    public string SnapshotUrl(long imageId) => $"{DeviceBase}/images/{imageId}";

    // -------------------------------------------------------------- helpers

    private async Task<T?> SendJsonAsync<T>(HttpRequestMessage req, CancellationToken ct)
    {
        using var res = await _http.SendAsync(req, ct);
        return await ReadJsonAsync<T>(res, ct);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new TabloHttpException((int)res.StatusCode, text);
        if (string.IsNullOrWhiteSpace(text)) return default;
        return JsonSerializer.Deserialize<T>(text, Json);
    }

    private static IEnumerable<List<T>> Chunk<T>(List<T> src, int size)
    {
        for (int i = 0; i < src.Count; i += size)
            yield return src.GetRange(i, Math.Min(size, src.Count - i));
    }

    public static DateTime ParseDate(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d : DateTime.MinValue;
}

public class TabloAuthException(string message) : Exception(message);

public sealed class TabloHttpException(int status, string body)
    : Exception($"Device/cloud returned HTTP {status}: {Trim(body)}")
{
    public int Status { get; } = status;
    private static string Trim(string b) => b.Length > 200 ? b[..200] : b;
}
