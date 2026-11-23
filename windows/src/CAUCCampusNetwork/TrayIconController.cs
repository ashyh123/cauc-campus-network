using System;
using System.ComponentModel;
using System.Windows;
using CAUCCampusNetwork.Services;
using CAUCCampusNetwork.ViewModels;
using CAUCCampusNetwork.Views;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace CAUCCampusNetwork;

public sealed class TrayIconController : IDisposable
{
    private readonly AppController _controller;
    private readonly TrayPopoverWindow _popover;
    private readonly PortalWindowController _portalController;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _contextMenu;
    private Drawing.Icon? _currentIcon;
    private TrayIconVisualState? _lastVisualState;
    private bool? _lastSystemDark;
    private bool _disposed;

    public TrayIconController(
        AppController controller,
        PortalWindowController portalController)
    {
        _controller = controller;
        _portalController = portalController;
        _popover = new TrayPopoverWindow(controller);

        _contextMenu = BuildContextMenu();
        _notifyIcon = new Forms.NotifyIcon
        {
            Visible = true,
            Text = controller.TrayTooltip,
            ContextMenuStrip = _contextMenu,
        };
        _notifyIcon.MouseUp += OnNotifyIconMouseUp;

        UpdateTrayIcon(force: true);

        _controller.PropertyChanged += OnControllerPropertyChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public void ShowPopover(bool anchorToCursor = false)
    {
        if (_disposed)
        {
            return;
        }

        _popover.ShowPopover(anchorToCursor);
    }

    private void TogglePopover()
    {
        if (_popover.IsVisible)
        {
            _popover.HidePopover();
            return;
        }

        if ((DateTimeOffset.UtcNow - _popover.LastClosedAt) < TimeSpan.FromMilliseconds(250))
        {
            return;
        }

        _popover.ShowPopover(anchorToCursor: true);
    }

    private void OnNotifyIconMouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
        {
            TogglePopover();
        }
    }

    private void OnControllerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(AppController.TrayIconState))
        {
            UpdateTrayIcon(force: false);
        }

        if (e.PropertyName is null or nameof(AppController.TrayTooltip))
        {
            _notifyIcon.Text = _controller.TrayTooltip;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_disposed)
                {
                    return;
                }

                bool appDark = SystemThemeHelper.IsAppDarkMode();
                SystemThemeHelper.ApplyThemeResources(
                    Application.Current.Resources,
                    appDark,
                    _popover.IsAcrylicActive
                );
                _popover.RefreshBackdropTheme(appDark);
                _portalController.UpdateTheme(appDark);
                UpdateTrayIcon(force: true);
            });
        }
    }

    private void UpdateTrayIcon(bool force)
    {
        var state = _controller.TrayIconState;
        bool isSystemDark = SystemThemeHelper.IsSystemTaskbarDarkMode();

        if (!force && _lastVisualState == state && _lastSystemDark == isSystemDark)
        {
            return;
        }

        _lastVisualState = state;
        _lastSystemDark = isSystemDark;

        var nextIcon = TrayIconGenerator.CreateIcon(state, isSystemDark);
        var previous = _currentIcon;
        _currentIcon = nextIcon;
        _notifyIcon.Icon = nextIcon;
        previous?.Dispose();
    }

    private Forms.ContextMenuStrip BuildContextMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开状态面板", null, (_, _) => ShowPopover(anchorToCursor: true));
        menu.Items.Add("重新检测", null, (_, _) => _controller.Refresh(force: true));
        menu.Items.Add("开始认证", null, (_, _) => _controller.StartAuthentication());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("打开认证页", null, (_, _) => _controller.OpenPortalManually());
        menu.Items.Add("Wi-Fi 设置", null, (_, _) => _controller.OpenWiFiSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) =>
        {
            _popover.HidePopover();
            _controller.Quit();
        });
        return menu;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _controller.PropertyChanged -= OnControllerPropertyChanged;
        _notifyIcon.MouseUp -= OnNotifyIconMouseUp;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();

        _currentIcon?.Dispose();
        _currentIcon = null;

        try
        {
            _popover.Close();
        }
        catch
        {
            // Ignore close errors during shutdown.
        }
    }
}
