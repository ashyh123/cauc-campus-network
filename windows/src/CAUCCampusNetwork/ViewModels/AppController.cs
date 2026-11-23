using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CAUCCampusCore;
using CAUCCampusNetwork.Services;
using Microsoft.Win32;

namespace CAUCCampusNetwork.ViewModels;

public enum StackCardState
{
    Online,
    Offline,
    Checking,
    Unknown,
}

public enum OverallStatusKind
{
    DualStackOnline,
    PartialOrPending,
    Busy,
    Idle,
}

public enum PrimaryButtonKind
{
    Accent,
    Online,
    Busy,
}

public sealed class AppController : INotifyPropertyChanged, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ConnectivityProber _prober = new();
    private readonly CampusQuotaClient _quotaClient = new();
    private readonly QuotaFetchCoordinator _quotaCoordinator;
    private readonly PortalWindowController _portalController;
    private readonly NetworkPathMonitor _pathMonitor;
    private readonly string _cacheFilePath;

    private ConnectivityStatus? _connectivity;
    private PortalQueryResult _portalResult = PortalQueryResult.Unreachable;
    private CachedCampusQuota? _cachedQuota;
    private string _message = "正在检测网络状态…";
    private bool _isBusy;
    private bool _isAuthenticating;
    private string? _lastCheckedTime;

    private string _currentPathSignature;
    private CancellationTokenSource? _pathDebounceCts;
    private CancellationTokenSource? _flowCts;
    private ulong _generation;
    private DateTimeOffset? _lastRefreshDate;
    private bool _disposed;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? QuitRequested;

    public LaunchAtLoginManager LaunchAtLogin { get; }

    public AppController(
        Dispatcher dispatcher,
        LaunchAtLoginManager launchAtLogin,
        PortalWindowController portalController,
        NetworkPathMonitor pathMonitor)
    {
        _dispatcher = dispatcher;
        LaunchAtLogin = launchAtLogin;
        _portalController = portalController;
        _pathMonitor = pathMonitor;
        _currentPathSignature = pathMonitor.CurrentSignature;

        _quotaCoordinator = new QuotaFetchCoordinator(
            freshTTL: TimeSpan.FromSeconds(60),
            unreachableCooldown: TimeSpan.FromSeconds(60),
            fetcher: ct => _quotaClient.FetchAsync(ct)
        );

        _cacheFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CAUCCampusNetwork",
            "cached-quota.json"
        );

        LoadCachedQuota();

        _portalController.UserClosedPortal += HandlePortalClosedByUser;
        _pathMonitor.PathSignatureChanged += OnPathSignatureChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        Refresh(force: true);
    }

    public ConnectivityStatus? Connectivity
    {
        get => _connectivity;
        private set
        {
            if (_connectivity != value)
            {
                _connectivity = value;
                NotifyStatusRelatedProperties();
            }
        }
    }

    public PortalQueryResult PortalResult
    {
        get => _portalResult;
        private set
        {
            if (_portalResult != value)
            {
                _portalResult = value;
                NotifyQuotaRelatedProperties();
                NotifyStatusRelatedProperties();
            }
        }
    }

    public CachedCampusQuota? CachedQuota
    {
        get => _cachedQuota;
        private set
        {
            if (_cachedQuota != value)
            {
                _cachedQuota = value;
                NotifyQuotaRelatedProperties();
            }
        }
    }

    public string Message
    {
        get => _message;
        private set
        {
            if (!string.Equals(_message, value, StringComparison.Ordinal))
            {
                _message = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TrayTooltip));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy != value)
            {
                _isBusy = value;
                OnPropertyChanged();
                NotifyStatusRelatedProperties();
                NotifyQuotaRelatedProperties();
            }
        }
    }

    public bool IsAuthenticating
    {
        get => _isAuthenticating;
        private set
        {
            if (_isAuthenticating != value)
            {
                _isAuthenticating = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PrimaryActionText));
            }
        }
    }

    public string? LastCheckedTime
    {
        get => _lastCheckedTime;
        private set
        {
            if (!string.Equals(_lastCheckedTime, value, StringComparison.Ordinal))
            {
                _lastCheckedTime = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasLastCheckedTime));
            }
        }
    }

    public bool HasLastCheckedTime => !string.IsNullOrEmpty(LastCheckedTime);

    public CampusQuota? Quota => _portalResult.Kind == PortalQueryKind.Online ? _portalResult.Quota : null;

    public CampusQuota? DisplayedQuota => Quota ?? _cachedQuota?.Quota;

    public bool HasDisplayedQuota => DisplayedQuota is not null;

    public bool IsShowingCachedQuota => _portalResult.Kind == PortalQueryKind.Unreachable && _cachedQuota is not null;

    public string? CachedQuotaTimeText
    {
        get
        {
            if (_cachedQuota is not { } cached)
            {
                return null;
            }

            var local = cached.UpdatedAt.ToLocalTime();
            var today = DateTimeOffset.Now.Date;
            return local.Date == today
                ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
                : local.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
    }

    public string? CachedQuotaHintText =>
        IsShowingCachedQuota && CachedQuotaTimeText is { } timeText
            ? $"网关暂不可达 · 上次同步 {timeText}"
            : null;

    public bool HasCachedQuotaHint => !string.IsNullOrEmpty(CachedQuotaHintText);

    public bool ShowQuotaUnreachableHint =>
        _portalResult.Kind == PortalQueryKind.Unreachable
        && _cachedQuota is null
        && !_isBusy
        && _connectivity is not null;

    public string QuotaAccountIdText => DisplayedQuota?.AccountID ?? string.Empty;
    public bool HasQuotaAccountId => !string.IsNullOrEmpty(DisplayedQuota?.AccountID);
    public string QuotaRemainingText => DisplayedQuota?.RemainingText ?? "--";
    public bool IsLowRemainingFlow => DisplayedQuota?.IsLowRemainingFlow == true;
    public string QuotaBalanceText => DisplayedQuota?.BalanceText ?? "--";
    public bool IsLowBalance => DisplayedQuota?.IsLowBalance == true;
    public bool HasUsedText => !string.IsNullOrEmpty(DisplayedQuota?.UsedText);
    public string UsedDisplayText => DisplayedQuota?.UsedText is { } used ? $"累计已用 {used}" : string.Empty;
    public bool HasIPv6Used => (DisplayedQuota?.IPv6UsedKB ?? 0) > 0 && !string.IsNullOrEmpty(DisplayedQuota?.IPv6UsedText);
    public string IPv6UsedDisplayText => DisplayedQuota?.IPv6UsedText is { } used ? $"IPv6 免计 {used}" : string.Empty;
    public bool ShowQuotaDetailsRow => HasUsedText || HasIPv6Used;
    public bool ShowQuotaBottomSection => ShowQuotaDetailsRow || HasCachedQuotaHint;

    public StackCardState IPv4State => ResolveStackState(_connectivity?.IPv4);
    public StackCardState IPv6State => ResolveStackState(_connectivity?.IPv6);
    public string IPv4Subtitle => StackSubtitle(IPv4State);
    public string IPv6Subtitle => StackSubtitle(IPv6State);

    public bool IsDualStackOnline => _connectivity?.IsDualStackOnline == true;

    public OverallStatusKind OverallStatus
    {
        get
        {
            if (_connectivity is { } status)
            {
                if (status.IsDualStackOnline)
                {
                    return OverallStatusKind.DualStackOnline;
                }
                if (status.IPv4 || status.IPv6 || _portalResult.Kind == PortalQueryKind.Offline)
                {
                    return OverallStatusKind.PartialOrPending;
                }
                return OverallStatusKind.Idle;
            }

            return _isBusy ? OverallStatusKind.Busy : OverallStatusKind.Idle;
        }
    }

    public PrimaryButtonKind PrimaryButtonStyleKind
    {
        get
        {
            if (_isBusy)
            {
                return PrimaryButtonKind.Busy;
            }
            if (IsDualStackOnline)
            {
                return PrimaryButtonKind.Online;
            }
            return PrimaryButtonKind.Accent;
        }
    }

    public string PrimaryActionText
    {
        get
        {
            if (_isBusy)
            {
                return _isAuthenticating ? "正在等待认证…" : "正在检测网络…";
            }
            if (IsDualStackOnline)
            {
                return "双栈已连通 · 重新检测";
            }
            return "开始认证";
        }
    }

    public string RefreshButtonHelpText => _isBusy ? "停止检测" : "重新检测";

    public TrayIconVisualState TrayIconState
    {
        get
        {
            if (_isBusy && _connectivity is null)
            {
                return TrayIconVisualState.BusyInitial;
            }
            if (_connectivity?.IsDualStackOnline == true)
            {
                return TrayIconVisualState.DualStackOnline;
            }
            if (_connectivity is { } c && (c.IPv4 || c.IPv6))
            {
                return TrayIconVisualState.Partial;
            }
            if (_connectivity is not null)
            {
                return TrayIconVisualState.Offline;
            }
            return TrayIconVisualState.Partial;
        }
    }

    public string TrayTooltip
    {
        get
        {
            string raw = string.IsNullOrWhiteSpace(_message)
                ? "CAUC 校园网"
                : $"CAUC 校园网 - {_message}";
            return raw.Length <= 63 ? raw : raw[..60] + "…";
        }
    }

    public void HandlePopoverOpened()
    {
        LaunchAtLogin.Refresh();
        if (_isBusy)
        {
            return;
        }

        if (_lastRefreshDate is { } lastRefresh
            && (DateTimeOffset.UtcNow - lastRefresh) < TimeSpan.FromSeconds(3))
        {
            return;
        }

        Refresh(force: true, forceQuota: false);
    }

    public void Refresh(bool force = true, bool? forceQuota = null)
    {
        if (!force && _isBusy)
        {
            return;
        }

        bool resolvedForceQuota = forceQuota ?? force;
        StartFlow(authenticate: false, forceQuota: resolvedForceQuota);
    }

    public void StartAuthentication()
    {
        if (_isBusy)
        {
            return;
        }

        StartFlow(authenticate: true, forceQuota: true);
    }

    public void StopChecking()
    {
        CancelActiveFlow("已停止检测");
    }

    public void OpenPortalManually()
    {
        _portalController.OpenPortal();
    }

    public void OpenWiFiSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:network-wifi")
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:network")
                {
                    UseShellExecute = true,
                });
            }
            catch
            {
                // Ignore if Windows settings URI cannot be launched.
            }
        }
    }

    public void Quit()
    {
        QuitRequested?.Invoke();
    }

    private void HandlePortalClosedByUser()
    {
        if (!_isAuthenticating)
        {
            return;
        }

        CancelActiveFlow("已关闭认证窗口");
    }

    private void CancelActiveFlow(string message)
    {
        _pathDebounceCts?.Cancel();
        _pathDebounceCts?.Dispose();
        _pathDebounceCts = null;

        _flowCts?.Cancel();
        _flowCts?.Dispose();
        _flowCts = null;

        _generation++;
        _portalController.StopTrigger();
        IsBusy = false;
        IsAuthenticating = false;
        Message = message;
    }

    private void OnPathSignatureChanged(string newSignature)
    {
        _dispatcher.BeginInvoke(() => HandlePathUpdate(newSignature));
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (!_disposed)
                {
                    Refresh(force: true);
                }
            });
        }
    }

    private void HandlePathUpdate(string newSignature)
    {
        if (_disposed || string.Equals(newSignature, _currentPathSignature, StringComparison.Ordinal))
        {
            return;
        }

        _currentPathSignature = newSignature;
        _pathDebounceCts?.Cancel();
        _pathDebounceCts?.Dispose();

        var cts = new CancellationTokenSource();
        _pathDebounceCts = cts;
        _ = DebouncePathRefreshAsync(cts.Token);
    }

    private async Task DebouncePathRefreshAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_disposed || ct.IsCancellationRequested || _isAuthenticating)
        {
            return;
        }

        _pathDebounceCts?.Dispose();
        _pathDebounceCts = null;
        Refresh(force: true, forceQuota: false);
    }

    private void StartFlow(bool authenticate, bool forceQuota)
    {
        if (_disposed)
        {
            return;
        }

        _pathDebounceCts?.Cancel();
        _pathDebounceCts?.Dispose();
        _pathDebounceCts = null;

        _flowCts?.Cancel();
        _flowCts?.Dispose();
        var cts = new CancellationTokenSource();
        _flowCts = cts;

        _portalController.StopTrigger();

        _generation++;
        ulong activeGeneration = _generation;
        _lastRefreshDate = DateTimeOffset.UtcNow;
        IsBusy = true;
        IsAuthenticating = authenticate;
        Message = authenticate ? "正在检查当前网络并准备认证…" : "正在检测 IPv4 与 IPv6…";

        string pathSignature = _currentPathSignature;
        bool useForce = authenticate || forceQuota;

        var services = new FlowServices(
            CheckAsync: (fallback, token) => _prober.CheckAsync(fallback, token),
            FetchQuotaAsync: token => _quotaCoordinator.FetchAsync(
                useForce ? QuotaFetchMode.Force(pathSignature) : QuotaFetchMode.Smart(pathSignature),
                ct: token
            ),
            PollInterval: TimeSpan.FromSeconds(2),
            Timeout: TimeSpan.FromSeconds(55)
        );

        _ = RunFlowOnBackgroundAsync(authenticate, services, activeGeneration, cts.Token);
    }

    private async Task RunFlowOnBackgroundAsync(
        bool authenticate,
        FlowServices services,
        ulong activeGeneration,
        CancellationToken ct)
    {
        try
        {
            await AuthFlowEngine.RunNetworkFlowAsync(
                authenticate,
                services,
                evt => _dispatcher.InvokeAsync(() => ApplyEvent(evt, activeGeneration)).Task,
                ct
            );
        }
        catch (OperationCanceledException)
        {
            // Flow cancelled explicitly.
        }
    }

    private void ApplyEvent(FlowEvent evt, ulong generation)
    {
        if (_disposed || generation != _generation)
        {
            return;
        }

        if (evt.Status is { } status)
        {
            Connectivity = status;
            var nowLocal = DateTimeOffset.Now;
            LastCheckedTime = nowLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            _lastRefreshDate = DateTimeOffset.UtcNow;
        }

        if (evt.PortalResult is { } portal)
        {
            PortalResult = portal;
            switch (portal.Kind)
            {
                case PortalQueryKind.Online when portal.Quota is not null:
                    var cached = new CachedCampusQuota(portal.Quota, DateTimeOffset.UtcNow);
                    CachedQuota = cached;
                    SaveCachedQuota(cached);
                    break;
                case PortalQueryKind.Offline:
                    CachedQuota = null;
                    DeleteCachedQuotaFile();
                    break;
                case PortalQueryKind.Unreachable:
                    break;
            }
        }

        if (evt.Message is not null)
        {
            Message = evt.Message;
        }

        if (evt.OpenPortal)
        {
            _portalController.OpenPortal();
        }

        if (evt.TriggerIPv6)
        {
            _portalController.TriggerIPv6();
        }

        if (evt.ClosePortal)
        {
            _portalController.ClosePortal();
        }

        if (evt.Done)
        {
            IsBusy = false;
            IsAuthenticating = false;
            _portalController.StopTrigger();
            _flowCts?.Dispose();
            _flowCts = null;
        }
    }

    private StackCardState ResolveStackState(bool? reachable)
    {
        if (reachable is { } value)
        {
            return value ? StackCardState.Online : StackCardState.Offline;
        }

        return _isBusy ? StackCardState.Checking : StackCardState.Unknown;
    }

    private static string StackSubtitle(StackCardState state) => state switch
    {
        StackCardState.Online => "公网可达",
        StackCardState.Offline => "未连通",
        StackCardState.Checking => "检测中…",
        _ => "待检测",
    };

    private void LoadCachedQuota()
    {
        try
        {
            if (!File.Exists(_cacheFilePath))
            {
                return;
            }

            var json = File.ReadAllText(_cacheFilePath);
            var decoded = JsonSerializer.Deserialize<CachedCampusQuota>(json);
            if (decoded is not null && (DateTimeOffset.UtcNow - decoded.UpdatedAt) <= TimeSpan.FromDays(7))
            {
                _cachedQuota = decoded;
            }
            else
            {
                DeleteCachedQuotaFile();
            }
        }
        catch
        {
            DeleteCachedQuotaFile();
        }
    }

    private void SaveCachedQuota(CachedCampusQuota cached)
    {
        try
        {
            var dir = Path.GetDirectoryName(_cacheFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(cached);
            File.WriteAllText(_cacheFilePath, json);
        }
        catch
        {
            // Ignore cache persistence errors.
        }
    }

    private void DeleteCachedQuotaFile()
    {
        try
        {
            if (File.Exists(_cacheFilePath))
            {
                File.Delete(_cacheFilePath);
            }
        }
        catch
        {
            // Ignore cache deletion errors.
        }
    }

    private void NotifyStatusRelatedProperties()
    {
        OnPropertyChanged(nameof(Connectivity));
        OnPropertyChanged(nameof(IPv4State));
        OnPropertyChanged(nameof(IPv6State));
        OnPropertyChanged(nameof(IPv4Subtitle));
        OnPropertyChanged(nameof(IPv6Subtitle));
        OnPropertyChanged(nameof(IsDualStackOnline));
        OnPropertyChanged(nameof(OverallStatus));
        OnPropertyChanged(nameof(PrimaryButtonStyleKind));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(RefreshButtonHelpText));
        OnPropertyChanged(nameof(TrayIconState));
        OnPropertyChanged(nameof(TrayTooltip));
    }

    private void NotifyQuotaRelatedProperties()
    {
        OnPropertyChanged(nameof(PortalResult));
        OnPropertyChanged(nameof(CachedQuota));
        OnPropertyChanged(nameof(DisplayedQuota));
        OnPropertyChanged(nameof(HasDisplayedQuota));
        OnPropertyChanged(nameof(IsShowingCachedQuota));
        OnPropertyChanged(nameof(CachedQuotaTimeText));
        OnPropertyChanged(nameof(CachedQuotaHintText));
        OnPropertyChanged(nameof(HasCachedQuotaHint));
        OnPropertyChanged(nameof(ShowQuotaUnreachableHint));
        OnPropertyChanged(nameof(QuotaAccountIdText));
        OnPropertyChanged(nameof(HasQuotaAccountId));
        OnPropertyChanged(nameof(QuotaRemainingText));
        OnPropertyChanged(nameof(IsLowRemainingFlow));
        OnPropertyChanged(nameof(QuotaBalanceText));
        OnPropertyChanged(nameof(IsLowBalance));
        OnPropertyChanged(nameof(HasUsedText));
        OnPropertyChanged(nameof(UsedDisplayText));
        OnPropertyChanged(nameof(HasIPv6Used));
        OnPropertyChanged(nameof(IPv6UsedDisplayText));
        OnPropertyChanged(nameof(ShowQuotaDetailsRow));
        OnPropertyChanged(nameof(ShowQuotaBottomSection));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _pathMonitor.PathSignatureChanged -= OnPathSignatureChanged;
        _portalController.UserClosedPortal -= HandlePortalClosedByUser;

        _pathDebounceCts?.Cancel();
        _pathDebounceCts?.Dispose();
        _pathDebounceCts = null;

        _flowCts?.Cancel();
        _flowCts?.Dispose();
        _flowCts = null;

        _quotaClient.Dispose();
    }
}
