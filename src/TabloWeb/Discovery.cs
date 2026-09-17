using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TabloWeb;

/// <summary>
/// Lets apps on the local network find this server without anyone typing an address.
///
/// An app (the Tablo for Fire TV app, for one) broadcasts <see cref="Query"/> to UDP
/// <see cref="Port"/>; this answers the sender directly with a small JSON description: where the
/// site is, which Tablo it is connected to, and what it can do. The Fire TV app uses it to offer
/// multi-view, which needs this server's compositor.
///
/// Question-and-answer rather than periodic announcements: an app gets an answer the moment it
/// asks, instead of waiting out an announcement interval, and many TV devices drop broadcast
/// traffic they are not actively waiting for.
///
/// Nothing sensitive is in the answer — the same things the sign-in page shows. It only works on
/// the local network (broadcasts do not cross routers), and in Docker only with host networking.
/// Set TABLOWEB_DISCOVERY=0 to turn it off.
/// </summary>
public sealed class Discovery(TabloSession tablo, ILogger<Discovery> log) : BackgroundService
{
    public const string Query = "TABLOWEB_DISCOVER";

    public static int Port =>
        int.TryParse(Environment.GetEnvironmentVariable("TABLOWEB_DISCOVERY_PORT"), out var p) && p is > 0 and < 65536
            ? p : 8788;

    public static bool Enabled =>
        Environment.GetEnvironmentVariable("TABLOWEB_DISCOVERY") is not ("0" or "false");

    /// <summary>The port the site itself listens on, from the first http address in TABLOWEB_URLS.</summary>
    private static int SitePort
    {
        get
        {
            var urls = Environment.GetEnvironmentVariable("TABLOWEB_URLS") ?? "http://0.0.0.0:8787";
            foreach (var raw in urls.Split(';', ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var text = raw.Replace("://+", "://0.0.0.0").Replace("://*", "://0.0.0.0");
                if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == "http") return uri.Port;
            }
            return 8787;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            log.LogInformation("Network discovery is off (TABLOWEB_DISCOVERY=0)");
            return;
        }

        UdpClient udp;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
        }
        catch (Exception ex)
        {
            // Another copy running, or the port taken: the site works fine without discovery.
            log.LogWarning("Network discovery is unavailable: could not listen on UDP {Port} ({Message})",
                Port, ex.Message);
            return;
        }

        log.LogInformation("Answering network discovery on UDP {Port}", Port);
        using (udp)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                UdpReceiveResult request;
                try { request = await udp.ReceiveAsync(stoppingToken); }
                catch (OperationCanceledException) { return; }
                catch (SocketException) { continue; }

                // Ignore anything that is not a discovery query; never answer arbitrary traffic.
                if (request.Buffer.Length > 256 ||
                    !Encoding.UTF8.GetString(request.Buffer).StartsWith(Query, StringComparison.Ordinal))
                    continue;

                try
                {
                    var answer = JsonSerializer.SerializeToUtf8Bytes(Describe());
                    await udp.SendAsync(answer, request.RemoteEndPoint, stoppingToken);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    log.LogDebug("Could not answer discovery from {Sender}: {Message}", request.RemoteEndPoint, ex.Message);
                }
            }
        }
    }

    private object Describe() => new
    {
        service = "tablo-web",
        version = UpdateChecker.CurrentVersion.ToString(3),
        port = SitePort,
        // Which DVR this server talks to, so an app can ignore a server connected to someone else's.
        serverId = tablo.Device?.ServerId,
        device = tablo.Device?.Name,
        connected = tablo.Connected,
        loginRequired = !Login.NoLogin,
        features = new[] { "multiview" }
    };
}
