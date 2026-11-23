using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CAUCCampusNetwork.Services;
using CAUCCampusNetwork.ViewModels;
using Forms = System.Windows.Forms;

namespace CAUCCampusNetwork.Views;

public partial class TrayPopoverWindow : Window
{
    private readonly AppController _controller;
    private bool _backdropInitialized;
    private bool _isAcrylicActive;
    private bool _hasAnchoredX;
    private double _anchoredLeftDip;

    public DateTimeOffset LastClosedAt { get; private set; } = DateTimeOffset.MinValue;

    public TrayPopoverWindow(AppController controller)
    {
        _controller = controller;
        DataContext = controller;

        InitializeComponent();

        SourceInitialized += OnSourceInitialized;
        Deactivated += OnWindowDeactivated;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        SizeChanged += OnWindowSizeChanged;
    }

    public bool IsAcrylicActive => _isAcrylicActive;

    public void RefreshBackdropTheme(bool isDark)
    {
        if (_backdropInitialized)
        {
            _isAcrylicActive = DwmHelper.ApplyWindowBackdrop(this, isDark, enableAcrylic: true);
        }
        else
        {
            _isAcrylicActive = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
        }

        ApplyCardCornerAndWindowBackground();
    }

    public void ShowPopover(bool anchorToCursor)
    {
        bool isDark = SystemThemeHelper.IsAppDarkMode();
        _isAcrylicActive = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
        SystemThemeHelper.ApplyThemeResources(Application.Current.Resources, isDark, _isAcrylicActive);
        ApplyCardCornerAndWindowBackground();

        _hasAnchoredX = false;
        RootCard.Opacity = 0;

        if (!IsVisible)
        {
            PositionNearTray(anchorToCursor);
            Show();
        }

        UpdateLayout();
        PositionNearTray(anchorToCursor);
        Activate();
        Focus();

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        RootCard.BeginAnimation(OpacityProperty, fadeIn);

        _controller.HandlePopoverOpened();
    }

    public void HidePopover()
    {
        if (!IsVisible)
        {
            return;
        }

        LastClosedAt = DateTimeOffset.UtcNow;
        RootCard.BeginAnimation(OpacityProperty, null);
        Hide();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _backdropInitialized = true;
        bool isDark = SystemThemeHelper.IsAppDarkMode();
        _isAcrylicActive = DwmHelper.ApplyWindowBackdrop(this, isDark, enableAcrylic: true);
        SystemThemeHelper.ApplyThemeResources(Application.Current.Resources, isDark, _isAcrylicActive);
        ApplyCardCornerAndWindowBackground();
    }

    private void ApplyCardCornerAndWindowBackground()
    {
        if (_isAcrylicActive)
        {
            Background = Brushes.Transparent;
            RootCard.CornerRadius = new CornerRadius(8);
        }
        else
        {
            Background = TryFindResource("PopoverBackgroundBrush") as Brush ?? Brushes.White;
            RootCard.CornerRadius = new CornerRadius(0);
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        HidePopover();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HidePopover();
        }
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsVisible && _hasAnchoredX)
        {
            PositionNearTray(anchorToCursor: false, reuseAnchoredX: true);
        }
    }

    private void PositionNearTray(bool anchorToCursor, bool reuseAnchoredX = false)
    {
        var cursorPx = Forms.Control.MousePosition;
        var screen = Forms.Screen.FromPoint(cursorPx);
        var boundsPx = screen.Bounds;
        var workPx = screen.WorkingArea;

        GetDpiScale(out double scaleX, out double scaleY);

        double workLeft = workPx.Left / scaleX;
        double workTop = workPx.Top / scaleY;
        double workRight = workPx.Right / scaleX;
        double workBottom = workPx.Bottom / scaleY;

        double cardWidth = ActualWidth > 1 ? ActualWidth : Width;
        double cardHeight = ActualHeight > 1 ? ActualHeight : 340;
        const double margin = 10;

        double targetLeft;
        if (reuseAnchoredX && _hasAnchoredX)
        {
            targetLeft = _anchoredLeftDip;
        }
        else if (anchorToCursor)
        {
            double cursorDipX = cursorPx.X / scaleX;
            targetLeft = cursorDipX - cardWidth / 2.0;
        }
        else
        {
            targetLeft = workRight - cardWidth - margin;
        }

        targetLeft = Math.Clamp(targetLeft, workLeft + 8, Math.Max(workLeft + 8, workRight - cardWidth - 8));
        _anchoredLeftDip = targetLeft;
        _hasAnchoredX = true;

        double targetTop;
        if (workPx.Top > boundsPx.Top)
        {
            // Top taskbar
            targetTop = workTop + margin;
        }
        else if (workPx.Left > boundsPx.Left)
        {
            // Left taskbar
            if (!reuseAnchoredX)
            {
                _anchoredLeftDip = workLeft + margin;
                targetLeft = _anchoredLeftDip;
            }
            targetTop = workBottom - cardHeight - margin;
        }
        else if (workPx.Right < boundsPx.Right)
        {
            // Right taskbar
            if (!reuseAnchoredX)
            {
                _anchoredLeftDip = workRight - cardWidth - margin;
                targetLeft = _anchoredLeftDip;
            }
            targetTop = workBottom - cardHeight - margin;
        }
        else
        {
            // Bottom taskbar (standard Windows 10/11)
            targetTop = workBottom - cardHeight - margin;
        }

        targetTop = Math.Clamp(targetTop, workTop + 8, Math.Max(workTop + 8, workBottom - cardHeight - 8));

        Left = Math.Round(targetLeft);
        Top = Math.Round(targetTop);
    }

    private void GetDpiScale(out double scaleX, out double scaleY)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is { } target)
        {
            var m = target.TransformToDevice;
            scaleX = m.M11 > 0 ? m.M11 : 1.0;
            scaleY = m.M22 > 0 ? m.M22 : 1.0;
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
    }

    private void OnRefreshOrStopClick(object sender, RoutedEventArgs e)
    {
        if (_controller.IsBusy)
        {
            _controller.StopChecking();
        }
        else
        {
            _controller.Refresh(force: true);
        }
    }

    private void OnPrimaryActionClick(object sender, RoutedEventArgs e)
    {
        if (_controller.IsBusy)
        {
            _controller.StopChecking();
        }
        else if (_controller.IsDualStackOnline)
        {
            _controller.Refresh(force: true);
        }
        else
        {
            _controller.StartAuthentication();
            HidePopover();
        }
    }

    private void OnOpenPortalClick(object sender, RoutedEventArgs e)
    {
        _controller.OpenPortalManually();
        HidePopover();
    }

    private void OnOpenWiFiSettingsClick(object sender, RoutedEventArgs e)
    {
        _controller.OpenWiFiSettings();
        HidePopover();
    }

    private void OnQuitClick(object sender, RoutedEventArgs e)
    {
        HidePopover();
        _controller.Quit();
    }
}
