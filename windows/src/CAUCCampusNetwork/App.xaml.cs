using System;
using System.Linq;
using System.Threading;
using System.Windows;
using CAUCCampusNetwork.Services;
using CAUCCampusNetwork.ViewModels;

namespace CAUCCampusNetwork;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\CAUCCampusNetwork_SingleInstance";
    private const string ShowPopoverEventName = @"Local\CAUCCampusNetwork_ShowPopover";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showPopoverEvent;
    private RegisteredWaitHandle? _showPopoverWaitHandle;

    private LaunchAtLoginManager? _launchAtLogin;
    private PortalWindowController? _portalController;
    private NetworkPathMonitor? _pathMonitor;
    private AppController? _controller;
    private TrayIconController? _trayController;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                using var existingEvent = EventWaitHandle.OpenExisting(ShowPopoverEventName);
                existingEvent.Set();
            }
            catch
            {
                // Ignore if primary instance is exiting or event is unavailable.
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown(0);
            return;
        }

        _showPopoverEvent = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.AutoReset,
            name: ShowPopoverEventName
        );
        _showPopoverWaitHandle = ThreadPool.RegisterWaitForSingleObject(
            _showPopoverEvent,
            OnSecondaryInstanceSignaled,
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false
        );

        bool isDark = SystemThemeHelper.IsAppDarkMode();
        bool isAcrylicSupported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
        SystemThemeHelper.ApplyThemeResources(Resources, isDark, isAcrylicSupported);

        _launchAtLogin = new LaunchAtLoginManager();
        _portalController = new PortalWindowController();
        _pathMonitor = new NetworkPathMonitor();
        _controller = new AppController(Dispatcher, _launchAtLogin, _portalController, _pathMonitor);
        _controller.QuitRequested += OnQuitRequested;
        _trayController = new TrayIconController(_controller, _portalController);

        bool launchedAtLogin = e.Args.Any(arg =>
            string.Equals(arg, "--autostart", StringComparison.OrdinalIgnoreCase));

        if (!launchedAtLogin)
        {
            _trayController.ShowPopover(anchorToCursor: false);
        }
    }

    private void OnSecondaryInstanceSignaled(object? state, bool timedOut)
    {
        if (timedOut)
        {
            return;
        }

        Dispatcher.BeginInvoke(() => _trayController?.ShowPopover(anchorToCursor: false));
    }

    private void OnQuitRequested()
    {
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showPopoverWaitHandle?.Unregister(null);
        _showPopoverWaitHandle = null;

        _showPopoverEvent?.Dispose();
        _showPopoverEvent = null;

        if (_controller is not null)
        {
            _controller.QuitRequested -= OnQuitRequested;
        }

        _trayController?.Dispose();
        _trayController = null;

        _controller?.Dispose();
        _controller = null;

        _portalController?.Dispose();
        _portalController = null;

        _pathMonitor?.Dispose();
        _pathMonitor = null;

        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch
            {
                // Ignore if already released.
            }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }
}
