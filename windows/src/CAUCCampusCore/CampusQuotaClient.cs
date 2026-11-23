using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace CAUCCampusCore;

public sealed record PhysicalInterfaceCandidate(
    string Id,
    string Name,
    int IPv4InterfaceIndex,
    IPAddress LocalIPv4
);

public sealed class CampusQuotaClient : IDisposable
{
    private const int IpUnicastIfSocketOption = 31;
    private const int MaxResponseBytes = 65_536;
    private static readonly IPAddress GatewayIP = IPAddress.Parse("192.168.100.200");
    private static ReadOnlySpan<byte> CrlfSeparator => "\r\n\r\n"u8;
    private static ReadOnlySpan<byte> LfSeparator => "\n\n"u8;

    private static readonly string[] VirtualAdapterKeywords =
    [
        "wintun",
        "tun",
        "tap",
        "wireguard",
        "tailscale",
        "zerotier",
        "clash",
        "mihomo",
        "sing-box",
        "singbox",
        "v2ray",
        "xray",
        "openvpn",
        "nordlynx",
        "warp",
        "hyper-v",
        "vethernet",
        "vmware",
        "virtualbox",
        "wsl",
        "loopback",
        "bluetooth",
        "npcap",
        "teredo",
        "isatap",
        "pseudo",
    ];

    private readonly object _memoryLock = new();
    private readonly HttpClient _httpClient;
    private string? _lastSuccessfulAdapterId;

    public CampusQuotaClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            Proxy = null,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            PooledConnectionLifetime = TimeSpan.FromSeconds(15),
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(2.5),
        };
        _httpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true,
        };
    }

    public async Task<PortalQueryResult> FetchAsync(CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return PortalQueryResult.Unreachable;
        }

        int token = Random.Shared.Next(1000, 1_000_000);
        string path = $"/drcom/chkstatus?callback=dr1002&jsVersion=4.1.3&v={token}&lang=zh";
        string urlString = $"{CampusEndpoints.ChkstatusBase}?callback=dr1002&jsVersion=4.1.3&v={token}&lang=zh";

        string? preferredAdapterId;
        lock (_memoryLock)
        {
            preferredAdapterId = _lastSuccessfulAdapterId;
        }

        var collected = GetPhysicalInterfaceCandidates();
        var ordered = preferredAdapterId is not null
            ? collected
                .Where(c => string.Equals(c.Id, preferredAdapterId, StringComparison.Ordinal))
                .Concat(collected.Where(c => !string.Equals(c.Id, preferredAdapterId, StringComparison.Ordinal)))
                .ToList()
            : collected;

        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var racers = new List<Task<(PortalQueryResult Result, string? AdapterId)>>(ordered.Count + 1);

        foreach (var candidate in ordered)
        {
            racers.Add(FetchDirectWithAdapterTagAsync(candidate, path, raceCts.Token));
        }

        racers.Add(FetchViaHttpClientWithTagAsync(urlString, raceCts.Token));

        while (racers.Count > 0)
        {
            var completed = await Task.WhenAny(racers).ConfigureAwait(false);
            racers.Remove(completed);

            var (result, adapterId) = await completed.ConfigureAwait(false);
            if (result.Kind is PortalQueryKind.Online or PortalQueryKind.Offline)
            {
                if (adapterId is not null)
                {
                    lock (_memoryLock)
                    {
                        _lastSuccessfulAdapterId = adapterId;
                    }
                }

                await raceCts.CancelAsync().ConfigureAwait(false);
                await ObserveRemainingAsync(racers).ConfigureAwait(false);
                return result;
            }
        }

        return PortalQueryResult.Unreachable;
    }

    public static IReadOnlyList<PhysicalInterfaceCandidate> GetPhysicalInterfaceCandidates()
    {
        var results = new List<PhysicalInterfaceCandidate>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || !IsPhysicalAdapter(ni))
                {
                    continue;
                }

                if (!ni.Supports(NetworkInterfaceComponent.IPv4))
                {
                    continue;
                }

                IPInterfaceProperties ipProps;
                IPv4InterfaceProperties? ipv4Props;
                try
                {
                    ipProps = ni.GetIPProperties();
                    ipv4Props = ipProps.GetIPv4Properties();
                }
                catch
                {
                    continue;
                }

                if (ipv4Props is null || ipv4Props.Index <= 0)
                {
                    continue;
                }

                IPAddress? localIpv4 = null;
                foreach (var unicast in ipProps.UnicastAddresses)
                {
                    var addr = unicast.Address;
                    if (addr.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(addr))
                    {
                        continue;
                    }

                    Span<byte> bytes = stackalloc byte[4];
                    if (addr.TryWriteBytes(bytes, out int written) && written == 4 && bytes[0] == 169 && bytes[1] == 254)
                    {
                        continue;
                    }

                    localIpv4 = addr;
                    break;
                }

                if (localIpv4 is null)
                {
                    continue;
                }

                results.Add(new PhysicalInterfaceCandidate(ni.Id, ni.Name, ipv4Props.Index, localIpv4));
            }
        }
        catch
        {
            // Ignore adapter enumeration failures and fall back to HttpClient.
        }

        return results;
    }

    public static bool IsPhysicalAdapter(NetworkInterface ni)
    {
        bool supportedType = ni.NetworkInterfaceType is NetworkInterfaceType.Wireless80211
            or NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.Ethernet3Megabit;

        return supportedType && !IsVirtualOrTunnelAdapter(ni.Name, ni.Description);
    }

    public static bool IsVirtualOrTunnelAdapter(string name, string description)
    {
        foreach (var keyword in VirtualAdapterKeywords)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || description.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<(PortalQueryResult Result, string? AdapterId)> FetchDirectWithAdapterTagAsync(
        PhysicalInterfaceCandidate candidate,
        string path,
        CancellationToken ct)
    {
        var result = await DirectGatewayFetchAsync(candidate, path, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        return (result, candidate.Id);
    }

    private async Task<(PortalQueryResult Result, string? AdapterId)> FetchViaHttpClientWithTagAsync(
        string urlString,
        CancellationToken ct)
    {
        var result = await FetchViaHttpClientAsync(urlString, ct).ConfigureAwait(false);
        return (result, null);
    }

    private static async Task<PortalQueryResult> DirectGatewayFetchAsync(
        PhysicalInterfaceCandidate candidate,
        string path,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var token = timeoutCts.Token;

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    socket.SetSocketOption(
                        SocketOptionLevel.IP,
                        (SocketOptionName)IpUnicastIfSocketOption,
                        IPAddress.HostToNetworkOrder(candidate.IPv4InterfaceIndex)
                    );
                }
                catch
                {
                    // Continue with source IP binding if IP_UNICAST_IF is unavailable.
                }
            }

            socket.Bind(new IPEndPoint(candidate.LocalIPv4, 0));
            await socket.ConnectAsync(new IPEndPoint(GatewayIP, 80), token).ConfigureAwait(false);

            var rawRequest = $"GET {path} HTTP/1.1\r\nHost: 192.168.100.200\r\nUser-Agent: CAUCCampusNetwork/2.0\r\nConnection: close\r\n\r\n";
            await socket.SendAsync(Encoding.UTF8.GetBytes(rawRequest), SocketFlags.None, token).ConfigureAwait(false);

            var buffer = new byte[MaxResponseBytes];
            int totalRead = 0;

            while (totalRead < MaxResponseBytes)
            {
                int chunkMax = Math.Min(4096, MaxResponseBytes - totalRead);
                int read = await socket.ReceiveAsync(buffer.AsMemory(totalRead, chunkMax), SocketFlags.None, token).ConfigureAwait(false);

                if (read == 0)
                {
                    return CampusQuotaParser.ParseHTTPResponse(buffer.AsSpan(0, totalRead)) ?? PortalQueryResult.Unreachable;
                }

                totalRead += read;
                if (TryIncrementalParse(buffer, totalRead) is { } parsed)
                {
                    return parsed;
                }
            }

            return CampusQuotaParser.ParseHTTPResponse(buffer.AsSpan(0, totalRead)) ?? PortalQueryResult.Unreachable;
        }
        catch
        {
            return PortalQueryResult.Unreachable;
        }
    }

    private async Task<PortalQueryResult> FetchViaHttpClientAsync(string urlString, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return PortalQueryResult.Unreachable;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, urlString);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return PortalQueryResult.Unreachable;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return CampusQuotaParser.Parse(bytes) ?? PortalQueryResult.Unreachable;
        }
        catch
        {
            return PortalQueryResult.Unreachable;
        }
    }

    private static PortalQueryResult? TryIncrementalParse(byte[] buffer, int length)
    {
        var span = buffer.AsSpan(0, length);
        if (!ShouldAttemptIncrementalParse(span))
        {
            return null;
        }

        var parsed = CampusQuotaParser.ParseHTTPResponse(span);
        return parsed is { Kind: PortalQueryKind.Online or PortalQueryKind.Offline } ? parsed : null;
    }

    private static bool ShouldAttemptIncrementalParse(ReadOnlySpan<byte> buffer)
    {
        int sepLen;
        int sepIdx = buffer.IndexOf(CrlfSeparator);
        if (sepIdx >= 0)
        {
            sepLen = 4;
        }
        else
        {
            sepIdx = buffer.IndexOf(LfSeparator);
            if (sepIdx < 0)
            {
                return false;
            }
            sepLen = 2;
        }

        return buffer[(sepIdx + sepLen)..].Contains((byte)'}');
    }

    private static async Task ObserveRemainingAsync(IEnumerable<Task<(PortalQueryResult Result, string? AdapterId)>> tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // Ignored after winner already returned.
            }
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
