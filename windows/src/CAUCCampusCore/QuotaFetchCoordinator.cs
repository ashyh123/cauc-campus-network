namespace CAUCCampusCore;

public abstract record QuotaFetchMode(string PathSignature)
{
    public sealed record SmartMode(string PathSignature) : QuotaFetchMode(PathSignature);
    public sealed record ForceMode(string PathSignature) : QuotaFetchMode(PathSignature);

    public static QuotaFetchMode Smart(string pathSignature) => new SmartMode(pathSignature);
    public static QuotaFetchMode Force(string pathSignature) => new ForceMode(pathSignature);
}

public sealed class QuotaFetchCoordinator
{
    private readonly object _gate = new();
    private readonly TimeSpan _freshTTL;
    private readonly TimeSpan _unreachableCooldown;
    private readonly Func<CancellationToken, Task<PortalQueryResult>> _fetcher;

    private string? _lastPathSignature;
    private PortalQueryResult? _lastResult;
    private DateTimeOffset? _lastFetchedAt;
    private ulong _generation;

    public QuotaFetchCoordinator(
        TimeSpan? freshTTL = null,
        TimeSpan? unreachableCooldown = null,
        Func<CancellationToken, Task<PortalQueryResult>>? fetcher = null)
    {
        _freshTTL = freshTTL ?? TimeSpan.FromSeconds(60);
        _unreachableCooldown = unreachableCooldown ?? TimeSpan.FromSeconds(60);
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
    }

    public async Task<PortalQueryResult> FetchAsync(
        QuotaFetchMode mode,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        ulong fetchGeneration;

        lock (_gate)
        {
            if (mode is QuotaFetchMode.SmartMode smart
                && string.Equals(smart.PathSignature, _lastPathSignature, StringComparison.Ordinal)
                && _lastFetchedAt is { } lastFetchedAt
                && _lastResult is { } lastResult)
            {
                var elapsed = timestamp - lastFetchedAt;
                switch (lastResult.Kind)
                {
                    case PortalQueryKind.Online when elapsed < _freshTTL:
                        return lastResult;
                    case PortalQueryKind.Unreachable when elapsed < _unreachableCooldown:
                        return PortalQueryResult.Unreachable;
                    case PortalQueryKind.Offline:
                        break;
                }
            }

            _generation++;
            fetchGeneration = _generation;
        }

        PortalQueryResult result;
        try
        {
            result = await _fetcher(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return PortalQueryResult.Unreachable;
        }

        lock (_gate)
        {
            if (_generation == fetchGeneration
                && !(ct.IsCancellationRequested && result.Kind == PortalQueryKind.Unreachable))
            {
                _lastPathSignature = mode.PathSignature;
                _lastResult = result;
                _lastFetchedAt = timestamp;
            }
        }

        return result;
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _lastPathSignature = null;
            _lastResult = null;
            _lastFetchedAt = null;
        }
    }
}
