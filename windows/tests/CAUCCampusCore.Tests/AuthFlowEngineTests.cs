using CAUCCampusCore;
using Xunit;

namespace CAUCCampusCore.Tests;

public sealed class AuthFlowEngineTests
{
    [Fact]
    public async Task SkipsPortalWhenAlreadyDualStackOnline()
    {
        var events = new List<FlowEvent>();
        var quota = new CampusQuota
        {
            AccountID = "202400001",
            BalanceYuan = 25.98,
            RemainingKB = 53_227_520,
        };

        var services = new FlowServices(
            CheckAsync: (_, _) => Task.FromResult(new ConnectivityStatus(IPv4: true, IPv6: true)),
            FetchQuotaAsync: _ => Task.FromResult(PortalQueryResult.Online(quota))
        );

        await AuthFlowEngine.RunNetworkFlowAsync(true, services, evt =>
        {
            events.Add(evt);
            return Task.CompletedTask;
        });

        Assert.Single(events);
        Assert.True(events[0].Done);
        Assert.False(events[0].OpenPortal);
        Assert.True(events[0].ClosePortal);
        Assert.False(events[0].TriggerIPv6);
        Assert.Equal(new ConnectivityStatus(IPv4: true, IPv6: true), events[0].Status);
        Assert.Equal(PortalQueryResult.Online(quota), events[0].PortalResult);
    }

    [Fact]
    public async Task OpensPortalTriggersIpv6OnceEnablesFallbackAndClosesPortalOnSuccess()
    {
        var sequence = new[]
        {
            new ConnectivityStatus(IPv4: false, IPv6: false),
            new ConnectivityStatus(IPv4: true, IPv6: false),
            new ConnectivityStatus(IPv4: true, IPv6: false),
            new ConnectivityStatus(IPv4: true, IPv6: true),
        };

        int checkIndex = 0;
        int quotaCalls = 0;
        var fallbacks = new List<bool>();
        var events = new List<FlowEvent>();
        var finalQuota = new CampusQuota
        {
            AccountID = "202400001",
            BalanceYuan = 20.0,
            RemainingKB = 10_485_760,
        };

        var services = new FlowServices(
            CheckAsync: (fallback, _) =>
            {
                fallbacks.Add(fallback);
                var status = sequence[Math.Min(checkIndex, sequence.Length - 1)];
                checkIndex++;
                return Task.FromResult(status);
            },
            FetchQuotaAsync: _ =>
            {
                quotaCalls++;
                return Task.FromResult(quotaCalls == 1
                    ? PortalQueryResult.Offline
                    : PortalQueryResult.Online(finalQuota));
            },
            PollInterval: TimeSpan.FromMilliseconds(5),
            Timeout: TimeSpan.FromSeconds(5)
        );

        await AuthFlowEngine.RunNetworkFlowAsync(true, services, evt =>
        {
            events.Add(evt);
            return Task.CompletedTask;
        });

        Assert.Equal([false, false, true, true], fallbacks);
        Assert.Equal(1, events.Count(e => e.OpenPortal));
        Assert.Equal(1, events.Count(e => e.TriggerIPv6));
        Assert.Equal(1, events.Count(e => e.ClosePortal));
        Assert.True(events[^1].Done);
        Assert.Equal(new ConnectivityStatus(IPv4: true, IPv6: true), events[^1].Status);
        Assert.Equal(PortalQueryResult.Online(finalQuota), events[^1].PortalResult);
    }

    [Fact]
    public async Task RefreshWithOfflineCampusPortalReportsPendingLoginMessage()
    {
        var events = new List<FlowEvent>();
        var services = new FlowServices(
            CheckAsync: (_, _) => Task.FromResult(new ConnectivityStatus(IPv4: false, IPv6: false)),
            FetchQuotaAsync: _ => Task.FromResult(PortalQueryResult.Offline)
        );

        await AuthFlowEngine.RunNetworkFlowAsync(false, services, evt =>
        {
            events.Add(evt);
            return Task.CompletedTask;
        });

        Assert.Single(events);
        Assert.False(events[0].OpenPortal);
        Assert.True(events[0].Done);
        Assert.Equal(PortalQueryResult.Offline, events[0].PortalResult);
        Assert.Equal("已连接校园网，待认证登录", events[0].Message);
    }

    [Fact]
    public async Task CancellingTokenDuringAuthPollingStopsPromptly()
    {
        using var cts = new CancellationTokenSource();
        var events = new List<FlowEvent>();

        var services = new FlowServices(
            CheckAsync: (_, _) => Task.FromResult(new ConnectivityStatus(IPv4: false, IPv6: false)),
            FetchQuotaAsync: _ => Task.FromResult(PortalQueryResult.Offline),
            PollInterval: TimeSpan.FromSeconds(10),
            Timeout: TimeSpan.FromSeconds(55)
        );

        var runTask = AuthFlowEngine.RunNetworkFlowAsync(true, services, evt =>
        {
            events.Add(evt);
            if (evt.OpenPortal)
            {
                cts.Cancel();
            }
            return Task.CompletedTask;
        }, cts.Token);

        var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(runTask, completed);
        await runTask;
        Assert.Single(events);
        Assert.True(events[0].OpenPortal);
    }
}
