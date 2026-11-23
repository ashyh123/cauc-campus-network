using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace CAUCCampusCore;

public enum IPFamily
{
    IPv4,
    IPv6,
}

public sealed class ConnectivityProber
{
    private readonly TimeSpan _timeout;

    public ConnectivityProber(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromMilliseconds(3500);
    }

    public async Task<ConnectivityStatus> CheckAsync(bool ustcFallback, CancellationToken ct = default)
    {
        var v6Targets = ustcFallback
            ? [.. CampusEndpoints.PublicTargets, CampusEndpoints.UstcIPv6Probe]
            : CampusEndpoints.PublicTargets;

        var v4Task = ProbeAsync(IPFamily.IPv4, CampusEndpoints.PublicTargets, ct);
        var v6Task = ProbeAsync(IPFamily.IPv6, v6Targets, ct);

        await Task.WhenAll(v4Task, v6Task).ConfigureAwait(false);
        return new ConnectivityStatus(IPv4: await v4Task, IPv6: await v6Task);
    }

    public async Task<bool> ProbeAsync(
        IPFamily family,
        IReadOnlyList<Uri> targets,
        CancellationToken ct = default)
    {
        if (targets.Count == 0 || ct.IsCancellationRequested)
        {
            return false;
        }

        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = targets
            .Select(target => ProbeSingleAsync(family, target, raceCts.Token))
            .ToList();

        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(finished);

            if (await finished.ConfigureAwait(false))
            {
                await raceCts.CancelAsync().ConfigureAwait(false);
                await ObserveRemainingAsync(pending).ConfigureAwait(false);
                return true;
            }
        }

        return false;
    }

    public async Task<bool> ProbeSingleAsync(IPFamily family, Uri uri, CancellationToken ct = default)
    {
        if (!TryBuildTarget(uri, out var target))
        {
            return false;
        }

        var addressFamily = family == IPFamily.IPv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
        if (IPAddress.TryParse(target.Host, out var literalIp))
        {
            if (!IsStrictFamily(literalIp, addressFamily))
            {
                return false;
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);
        var token = timeoutCts.Token;

        IPAddress[] candidates;
        if (literalIp is not null)
        {
            candidates = [literalIp];
        }
        else
        {
            try
            {
                var resolved = await Dns.GetHostAddressesAsync(target.Host, addressFamily, token).ConfigureAwait(false);
                candidates = resolved
                    .Where(ip => IsStrictFamily(ip, addressFamily))
                    .Take(3)
                    .ToArray();
            }
            catch
            {
                return false;
            }
        }

        if (candidates.Length == 0)
        {
            return false;
        }

        if (candidates.Length == 1)
        {
            return await ProbeAddressAsync(candidates[0], addressFamily, target, token).ConfigureAwait(false);
        }

        using var addrRaceCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var attempts = candidates
            .Select(ip => ProbeAddressAsync(ip, addressFamily, target, addrRaceCts.Token))
            .ToList();

        while (attempts.Count > 0)
        {
            var finished = await Task.WhenAny(attempts).ConfigureAwait(false);
            attempts.Remove(finished);
            if (await finished.ConfigureAwait(false))
            {
                await addrRaceCts.CancelAsync().ConfigureAwait(false);
                await ObserveRemainingAsync(attempts).ConfigureAwait(false);
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> ProbeAddressAsync(
        IPAddress address,
        AddressFamily family,
        ProbeTarget target,
        CancellationToken ct)
    {
        try
        {
            using var socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };

            await socket.ConnectAsync(new IPEndPoint(address, target.Port), ct).ConfigureAwait(false);

            if (!MatchesEndpointFamily(socket.RemoteEndPoint, family)
                || !MatchesEndpointFamily(socket.LocalEndPoint, family))
            {
                return false;
            }

            await using var networkStream = new NetworkStream(socket, ownsSocket: true);
            await using SslStream? sslStream = target.IsHttps
                ? new SslStream(networkStream, leaveInnerStreamOpen: false)
                : null;
            Stream transport = networkStream;

            if (sslStream is not null)
            {
                await sslStream.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = target.Host },
                    ct
                ).ConfigureAwait(false);
                transport = sslStream;
            }

            await transport.WriteAsync(target.RequestBytes, ct).ConfigureAwait(false);
            await transport.FlushAsync(ct).ConfigureAwait(false);

            var buffer = new byte[4096];
            int totalRead = 0;

            while (totalRead < buffer.Length)
            {
                int read = await transport.ReadAsync(buffer.AsMemory(totalRead), ct).ConfigureAwait(false);
                bool isEof = read == 0;
                totalRead += read;

                int? statusCode = ParseStatusCode(buffer.AsSpan(0, totalRead), isEof);
                if (statusCode is { } code)
                {
                    return code is >= 200 and < 300;
                }

                if (isEof)
                {
                    return false;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsStrictFamily(IPAddress address, AddressFamily expectedFamily)
    {
        if (expectedFamily == AddressFamily.InterNetwork)
        {
            return address.AddressFamily == AddressFamily.InterNetwork;
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv4MappedToIPv6;
    }

    private static bool MatchesEndpointFamily(EndPoint? endpoint, AddressFamily expectedFamily)
    {
        return endpoint is not IPEndPoint ipEndpoint || IsStrictFamily(ipEndpoint.Address, expectedFamily);
    }

    private static int? ParseStatusCode(ReadOnlySpan<byte> data, bool isEof)
    {
        int newlineIndex = data.IndexOf((byte)'\n');
        ReadOnlySpan<byte> lineBytes;
        if (newlineIndex >= 0)
        {
            lineBytes = data[..newlineIndex];
        }
        else if (isEof && !data.IsEmpty)
        {
            lineBytes = data;
        }
        else
        {
            return null;
        }

        var line = Encoding.UTF8.GetString(lineBytes).Trim();
        if (!line.StartsWith("HTTP/", StringComparison.Ordinal))
        {
            return -1;
        }

        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
        {
            return -1;
        }

        return code;
    }

    private static bool TryBuildTarget(Uri uri, out ProbeTarget target)
    {
        target = default;
        if (!uri.IsAbsoluteUri)
        {
            return false;
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        bool isHttps = scheme == "https";
        if (scheme != "http" && !isHttps)
        {
            return false;
        }

        var cleanHost = uri.DnsSafeHost.Trim('[', ']');
        if (string.IsNullOrEmpty(cleanHost))
        {
            return false;
        }

        int defaultPort = isHttps ? 443 : 80;
        int port = uri.IsDefaultPort ? defaultPort : uri.Port;
        if (port is <= 0 or > ushort.MaxValue)
        {
            return false;
        }

        var requestTarget = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
        var formattedHost = cleanHost.Contains(':', StringComparison.Ordinal) ? $"[{cleanHost}]" : cleanHost;
        var hostHeader = port == defaultPort ? formattedHost : $"{formattedHost}:{port}";
        var rawRequest = $"GET {requestTarget} HTTP/1.1\r\nHost: {hostHeader}\r\nUser-Agent: CAUCCampusNetwork/2.0\r\nConnection: close\r\n\r\n";

        target = new ProbeTarget(cleanHost, port, isHttps, Encoding.UTF8.GetBytes(rawRequest));
        return true;
    }

    private static async Task ObserveRemainingAsync(IEnumerable<Task<bool>> tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // Ignored after winner already completed.
            }
        }
    }

    private readonly record struct ProbeTarget(string Host, int Port, bool IsHttps, byte[] RequestBytes);
}
