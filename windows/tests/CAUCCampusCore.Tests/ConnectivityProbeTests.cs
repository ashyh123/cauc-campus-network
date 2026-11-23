using System.Net;
using System.Net.Sockets;
using System.Text;
using CAUCCampusCore;
using Xunit;

namespace CAUCCampusCore.Tests;

public sealed class ConnectivityProbeTests
{
    [Fact]
    public async Task ProbesRequestedIpFamilyAndRejectsHttpRedirects()
    {
        var prober = new ConnectivityProber(TimeSpan.FromMilliseconds(800));

        await using var v4Server = LocalHttpProbeServer.TryStart(IPAddress.Loopback);
        if (v4Server is not null)
        {
            var v4Base = $"http://127.0.0.1:{v4Server.Port}";
            Assert.True(await prober.ProbeSingleAsync(IPFamily.IPv4, new Uri($"{v4Base}/ok")));
            Assert.False(await prober.ProbeSingleAsync(IPFamily.IPv6, new Uri($"{v4Base}/ok")));
            Assert.False(await prober.ProbeSingleAsync(IPFamily.IPv4, new Uri($"{v4Base}/portal")));
        }

        await using var v6Server = LocalHttpProbeServer.TryStart(IPAddress.IPv6Loopback);
        if (v6Server is not null)
        {
            var v6Base = $"http://[::1]:{v6Server.Port}";
            Assert.True(await prober.ProbeSingleAsync(IPFamily.IPv6, new Uri($"{v6Base}/ok")));
            Assert.False(await prober.ProbeSingleAsync(IPFamily.IPv4, new Uri($"{v6Base}/ok")));
            Assert.False(await prober.ProbeSingleAsync(IPFamily.IPv6, new Uri($"{v6Base}/portal")));
        }

        Assert.True(v4Server is not null || v6Server is not null);
    }

    private sealed class LocalHttpProbeServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        public int Port { get; }

        private LocalHttpProbeServer(TcpListener listener, int port)
        {
            _listener = listener;
            Port = port;
            _acceptLoop = AcceptLoopAsync();
        }

        public static LocalHttpProbeServer? TryStart(IPAddress bindAddress)
        {
            try
            {
                var listener = new TcpListener(bindAddress, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                return new LocalHttpProbeServer(listener, port);
            }
            catch
            {
                return null;
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    break;
                }

                _ = HandleClientAsync(client);
            }
        }

        private static async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    await using var stream = client.GetStream();
                    var buffer = new byte[2048];
                    int read = await stream.ReadAsync(buffer);
                    var req = Encoding.UTF8.GetString(buffer, 0, read);
                    var firstLine = req.Split("\r\n", 2)[0];

                    string response = firstLine.Contains(" /portal ", StringComparison.Ordinal)
                        ? "HTTP/1.1 302 Found\r\nLocation: /ok\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                        : "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n";

                    var responseBytes = Encoding.UTF8.GetBytes(response);
                    await stream.WriteAsync(responseBytes);
                    await stream.FlushAsync();
                }
                catch
                {
                    // Ignore client disconnect during teardown.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try
            {
                await _acceptLoop;
            }
            catch
            {
                // Ignore accept cancellation.
            }
            _cts.Dispose();
        }
    }
}
