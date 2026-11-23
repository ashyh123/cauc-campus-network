using System.Diagnostics;

namespace CAUCCampusCore;

public sealed record FlowEvent(
    ConnectivityStatus? Status = null,
    PortalQueryResult? PortalResult = null,
    string? Message = null,
    bool OpenPortal = false,
    bool ClosePortal = false,
    bool TriggerIPv6 = false,
    bool Done = false
);

public sealed record FlowServices(
    Func<bool, CancellationToken, Task<ConnectivityStatus>> CheckAsync,
    Func<CancellationToken, Task<PortalQueryResult>> FetchQuotaAsync,
    TimeSpan? PollInterval = null,
    TimeSpan? Timeout = null
)
{
    public TimeSpan EffectivePollInterval => PollInterval ?? TimeSpan.FromSeconds(2);
    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromSeconds(55);
}

public static class AuthFlowEngine
{
    public static async Task RunNetworkFlowAsync(
        bool authenticate,
        FlowServices services,
        Func<FlowEvent, Task> publish,
        CancellationToken ct = default)
    {
        var initialCheckTask = services.CheckAsync(false, ct);
        var initialQuotaTask = services.FetchQuotaAsync(ct);

        await Task.WhenAll(initialCheckTask, initialQuotaTask).ConfigureAwait(false);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        var currentStatus = await initialCheckTask;
        var initialPortal = await initialQuotaTask;

        if (!authenticate || currentStatus.IsDualStackOnline)
        {
            var summary = StatusSummary(currentStatus, initialPortal);
            await publish(new FlowEvent(
                Status: currentStatus,
                PortalResult: initialPortal,
                Message: summary,
                ClosePortal: authenticate && currentStatus.IsDualStackOnline,
                Done: true
            )).ConfigureAwait(false);
            return;
        }

        await publish(new FlowEvent(
            Status: currentStatus,
            PortalResult: initialPortal,
            Message: "请在认证窗口完成登录",
            OpenPortal: true
        )).ConfigureAwait(false);

        bool triggered = false;
        var stopwatch = Stopwatch.StartNew();
        var timeout = services.EffectiveTimeout;
        var pollInterval = services.EffectivePollInterval;

        while (!ct.IsCancellationRequested)
        {
            if (currentStatus.IPv4 && !currentStatus.IPv6 && !triggered)
            {
                triggered = true;
                await publish(new FlowEvent(
                    Message: "IPv4 已连通，正在激活并复检 IPv6…",
                    TriggerIPv6: true
                )).ConfigureAwait(false);
            }

            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var delay = remaining < pollInterval ? remaining : pollInterval;
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (stopwatch.Elapsed >= timeout)
            {
                break;
            }

            var nextStatus = await services.CheckAsync(triggered, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            currentStatus = nextStatus;

            if (currentStatus.IsDualStackOnline)
            {
                var freshQuota = await services.FetchQuotaAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                await publish(new FlowEvent(
                    Status: currentStatus,
                    PortalResult: freshQuota,
                    Message: "认证完成，IPv4 和 IPv6 均可访问公网",
                    ClosePortal: true,
                    Done: true
                )).ConfigureAwait(false);
                return;
            }

            await publish(new FlowEvent(Status: currentStatus)).ConfigureAwait(false);
        }

        if (ct.IsCancellationRequested)
        {
            return;
        }

        var timeoutQuota = await services.FetchQuotaAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        string timeoutMessage = (currentStatus.IPv4 && !currentStatus.IPv6)
            ? "IPv4 已连通，IPv6 仍未通过探测"
            : "等待认证超时，请确认登录结果";

        await publish(new FlowEvent(
            Status: currentStatus,
            PortalResult: timeoutQuota,
            Message: timeoutMessage,
            Done: true
        )).ConfigureAwait(false);
    }

    private static string StatusSummary(ConnectivityStatus status, PortalQueryResult portalResult)
    {
        return (status.IPv4, status.IPv6) switch
        {
            (true, true) => "IPv4 和 IPv6 均可访问公网",
            (true, false) => "IPv4 已连通，IPv6 暂不可达",
            (false, true) => "仅 IPv6 可访问公网",
            (false, false) => portalResult.Kind == PortalQueryKind.Offline
                ? "已连接校园网，待认证登录"
                : "暂未连通公网",
        };
    }
}
