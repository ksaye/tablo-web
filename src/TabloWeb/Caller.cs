using System.Net;

namespace TabloWeb;

/// <summary>
/// Is this request coming from the local network, or in over the internet?
///
/// Used to pick the encoder bitrate. A stream that only crosses a home network can be as close
/// to the broadcast as the encoder manages; one leaving through a domestic upload link — often a
/// tenth of the download speed, and shared — should not be.
/// </summary>
public static class Caller
{
    /// <summary>
    /// The private ranges, plus 100.64/10. That range is carrier-grade NAT in general, but on a
    /// home server it is far more likely to be a Tailscale/headscale tailnet — the same household
    /// reached another way — so it counts as local. Replace the whole list with
    /// TABLOWEB_LAN_NETWORKS as comma-separated CIDRs.
    /// </summary>
    private static readonly (IPAddress Network, int Bits)[] Local = ParseNetworks(
        Environment.GetEnvironmentVariable("TABLOWEB_LAN_NETWORKS")
        ?? "10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,127.0.0.0/8,100.64.0.0/10,::1/128,fc00::/7,fe80::/10");

    public static bool IsRemote(HttpContext http)
    {
        // UseForwardedHeaders has already replaced this with the client address a proxy reported.
        var ip = http.Connection.RemoteIpAddress;
        if (ip is null) return true;   // unknown means assume the expensive case, not the cheap one
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return !Local.Any(n => InNetwork(ip, n.Network, n.Bits));
    }

    private static (IPAddress, int)[] ParseNetworks(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry =>
            {
                var slash = entry.IndexOf('/');
                var address = slash < 0 ? entry : entry[..slash];
                return IPAddress.TryParse(address, out var parsed)
                    ? (parsed, slash < 0 ? parsed.GetAddressBytes().Length * 8 : int.Parse(entry[(slash + 1)..]))
                    : ((IPAddress, int)?)null;
            })
            .Where(x => x is not null).Select(x => x!.Value).ToArray();

    private static bool InNetwork(IPAddress ip, IPAddress network, int bits)
    {
        var a = ip.GetAddressBytes();
        var b = network.GetAddressBytes();
        if (a.Length != b.Length) return false;   // v4 address against a v6 network, or the reverse

        for (var i = 0; bits > 0; i++, bits -= 8)
        {
            var mask = bits >= 8 ? (byte)0xFF : (byte)(0xFF << (8 - bits));
            if ((a[i] & mask) != (b[i] & mask)) return false;
        }
        return true;
    }
}
