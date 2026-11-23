using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using CAUCCampusCore;
using CAUCCampusNetwork.Views;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CAUCCampusNetwork.Services;

public sealed class PortalWindowController : IDisposable
{
    private readonly string _userDataFolder;
    private PortalWindow? _window;
    private CoreWebView2Environment? _environment;
    private Task<CoreWebView2Environment>? _environmentTask;
    private bool _portalInitialized;
    private bool _triggerInitialized;
    private bool _isClosingProgrammatically;
    private bool _allowClose;
    private bool _disposed;
    private ulong _portalNavigationGeneration;
    private ulong _triggerGeneration;

    public event Action? UserClosedPortal;

    public PortalWindowController()
    {
        _userDataFolder = Path.Combine(
            Path.GetTempPath(),
            "CAUCCampusNetwork",
            $"wv2-{Environment.ProcessId}"
        );
    }

    public void OpenPortal()
    {
        if (_disposed)
        {
            return;
        }

        var window = EnsureWindow();
        window.Title = "校园网认证 · 192.168.100.200";
        window.ApplyTheme(SystemThemeHelper.IsAppDarkMode());

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Focus();

        ulong navGen = ++_portalNavigationGeneration;
        _ = NavigatePortalAsync(window, navGen);
    }

    public void ClosePortal()
    {
        _portalNavigationGeneration++;
        _isClosingProgrammatically = true;
        try
        {
            StopWebView(_window?.PortalWebView, _portalInitialized);
            if (_window is { IsVisible: true } window)
            {
                window.Hide();
            }
        }
        finally
        {
            _isClosingProgrammatically = false;
        }
    }

    public void TriggerIPv6()
    {
        if (_disposed || _window is null)
        {
            return;
        }

        ulong gen = ++_triggerGeneration;
        _ = NavigateTriggerAsync(_window, gen);
    }

    public void StopTrigger()
    {
        _triggerGeneration++;
        StopWebView(_window?.TriggerWebView, _triggerInitialized);
    }

    public void UpdateTheme(bool isDark)
    {
        _window?.ApplyTheme(isDark);
    }

    private PortalWindow EnsureWindow()
    {
        if (_window is not null)
        {
            return _window;
        }

        var window = new PortalWindow();
        window.Closing += OnWindowClosing;
        _window = window;
        return window;
    }

    private Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        if (_environment is not null)
        {
            return Task.FromResult(_environment);
        }

        if (_environmentTask is not null)
        {
            return _environmentTask;
        }

        Directory.CreateDirectory(_userDataFolder);
        _environmentTask = CreateEnvironmentCoreAsync();
        return _environmentTask;
    }

    private async Task<CoreWebView2Environment> CreateEnvironmentCoreAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: _userDataFolder
        );
        _environment = env;
        return env;
    }

    private async Task EnsurePortalWebViewAsync(PortalWindow window)
    {
        if (_portalInitialized)
        {
            return;
        }

        var env = await GetEnvironmentAsync();
        var options = env.CreateCoreWebView2ControllerOptions();
        options.IsInPrivateModeEnabled = true;

        await window.PortalWebView.EnsureCoreWebView2Async(env, options);
        if (_portalInitialized)
        {
            return;
        }

        _portalInitialized = true;
        var core = window.PortalWebView.CoreWebView2;
        core.NavigationStarting += OnPortalNavigationStarting;
        core.SourceChanged += OnPortalSourceChanged;
        core.NavigationCompleted += OnPortalNavigationCompleted;
    }

    private async Task EnsureTriggerWebViewAsync(PortalWindow window)
    {
        if (_triggerInitialized)
        {
            return;
        }

        var env = await GetEnvironmentAsync();
        var options = env.CreateCoreWebView2ControllerOptions();
        options.IsInPrivateModeEnabled = true;

        await window.TriggerWebView.EnsureCoreWebView2Async(env, options);
        _triggerInitialized = true;
    }

    private async Task NavigatePortalAsync(PortalWindow window, ulong navGen)
    {
        try
        {
            await EnsurePortalWebViewAsync(window);
            if (_disposed || navGen != _portalNavigationGeneration)
            {
                return;
            }

            window.PortalWebView.CoreWebView2.Navigate(CampusEndpoints.PortalUri.ToString());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            OpenFallbackBrowser();
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            if (!_disposed && navGen == _portalNavigationGeneration && _portalInitialized)
            {
                ShowLoadFailurePage(window.PortalWebView.CoreWebView2);
            }
            else if (!_portalInitialized)
            {
                OpenFallbackBrowser();
            }
        }
    }

    private async Task NavigateTriggerAsync(PortalWindow window, ulong triggerGen)
    {
        try
        {
            await EnsureTriggerWebViewAsync(window);
            if (_disposed || triggerGen != _triggerGeneration)
            {
                return;
            }

            window.TriggerWebView.CoreWebView2.Navigate(CampusEndpoints.IPv6TriggerUri.ToString());
        }
        catch
        {
            // Ignore background IPv6 trigger failures.
        }
    }

    private void OnPortalNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        UpdateWindowTitleForUrl(e.Uri);
    }

    private void OnPortalSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (sender is CoreWebView2 core)
        {
            UpdateWindowTitleForUrl(core.Source);
        }
    }

    private void OnPortalNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess || e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
        {
            return;
        }

        if (sender is CoreWebView2 core)
        {
            ShowLoadFailurePage(core);
        }
    }

    private void UpdateWindowTitleForUrl(string? rawUrl)
    {
        if (_window is null
            || string.IsNullOrWhiteSpace(rawUrl)
            || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            return;
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        if ((scheme == "http" || scheme == "https") && !string.IsNullOrEmpty(uri.Host))
        {
            _window.Title = $"校园网认证 · {scheme}://{uri.Host}";
        }
    }

    private void ShowLoadFailurePage(CoreWebView2 core)
    {
        if (_window is not null)
        {
            _window.Title = "校园网认证 · 页面加载失败";
        }

        const string html = """
            <!doctype html>
            <meta charset="utf-8">
            <meta name="color-scheme" content="light dark">
            <body style="font:15px/1.6 'Microsoft YaHei UI','Segoe UI',sans-serif;padding:40px;margin:0;color:#222;">
            <style>@media (prefers-color-scheme: dark){body{background:#1e1e1e;color:#e8e8e8;}a{color:#4cc2ff;}}</style>
            <h2 style="margin-top:0;">无法打开校园网认证页</h2>
            <p>请确认 Windows 已连接 CAUC 校园网，并尝试重新访问 <a href="http://192.168.100.200/">http://192.168.100.200/</a>。</p>
            </body>
            """;

        core.NavigateToString(html);
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        _portalNavigationGeneration++;
        StopWebView(_window?.PortalWebView, _portalInitialized);
        _window?.Hide();

        if (!_isClosingProgrammatically)
        {
            UserClosedPortal?.Invoke();
        }
    }

    private static void StopWebView(WebView2? webView, bool initialized)
    {
        if (!initialized || webView is null)
        {
            return;
        }

        try
        {
            webView.CoreWebView2?.Stop();
        }
        catch
        {
            // Ignore stop failures during teardown.
        }
    }

    private static void OpenFallbackBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo(CampusEndpoints.PortalUri.ToString())
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // Ignore shell open failure.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _allowClose = true;
        _portalNavigationGeneration++;
        _triggerGeneration++;

        if (_window is not null)
        {
            _window.Closing -= OnWindowClosing;
            if (_portalInitialized)
            {
                try
                {
                    var core = _window.PortalWebView.CoreWebView2;
                    core.NavigationStarting -= OnPortalNavigationStarting;
                    core.SourceChanged -= OnPortalSourceChanged;
                    core.NavigationCompleted -= OnPortalNavigationCompleted;
                }
                catch
                {
                    // Ignore event detach errors on exit.
                }
            }

            try
            {
                _window.PortalWebView.Dispose();
                _window.TriggerWebView.Dispose();
            }
            catch
            {
                // Ignore WebView2 disposal errors on exit.
            }

            try
            {
                _window.Close();
            }
            catch
            {
                // Ignore close failures on exit.
            }
            _window = null;
        }

        try
        {
            if (Directory.Exists(_userDataFolder))
            {
                Directory.Delete(_userDataFolder, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }
}
