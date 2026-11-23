using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace CAUCCampusNetwork.Services;

public static class SystemThemeHelper
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsAppDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, writable: false);
            if (key?.GetValue("AppsUseLightTheme") is int value)
            {
                return value == 0;
            }
        }
        catch
        {
            // Fall back to light mode when registry is inaccessible.
        }

        return false;
    }

    public static bool IsSystemTaskbarDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, writable: false);
            if (key?.GetValue("SystemUsesLightTheme") is int value)
            {
                return value == 0;
            }
        }
        catch
        {
            // Windows 10/11 taskbar defaults to dark theme.
        }

        return true;
    }

    public static void ApplyThemeResources(ResourceDictionary resources, bool isDark, bool isAcrylicActive)
    {
        if (isDark)
        {
            SetBrush(resources, "PopoverBackgroundBrush", isAcrylicActive
                ? Color.FromArgb(0xB8, 0x1E, 0x20, 0x24)
                : Color.FromRgb(0x1E, 0x20, 0x24));
            SetBrush(resources, "PopoverBorderBrush", Color.FromArgb(0x36, 0xFF, 0xFF, 0xFF));
            SetBrush(resources, "PrimaryTextBrush", Color.FromRgb(0xF3, 0xF5, 0xF8));
            SetBrush(resources, "SecondaryTextBrush", Color.FromRgb(0x9E, 0xA6, 0xB2));
            SetBrush(resources, "SubtleCardBackgroundBrush", Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
            SetBrush(resources, "SubtleCardBorderBrush", Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
            SetBrush(resources, "DividerBrush", Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));
            SetBrush(resources, "PillBackgroundBrush", Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
            SetBrush(resources, "HoverFillBrush", Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
            SetBrush(resources, "IconButtonFillBrush", Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            SetBrush(resources, "IconButtonHoverBrush", Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
            SetBrush(resources, "ToggleTrackOffBrush", Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            SetBrush(resources, "OnlineTextBrush", Color.FromRgb(0x4A, 0xDE, 0x80));
            SetBrush(resources, "OnlineFillBrush", Color.FromArgb(0x24, 0x22, 0xC5, 0x5E));
            SetBrush(resources, "OnlineBorderBrush", Color.FromArgb(0x52, 0x22, 0xC5, 0x5E));
            SetBrush(resources, "OnlineHoverFillBrush", Color.FromArgb(0x30, 0x22, 0xC5, 0x5E));
            SetBrush(resources, "OfflineTextBrush", Color.FromRgb(0xFB, 0x92, 0x3C));
            SetBrush(resources, "OfflineFillBrush", Color.FromArgb(0x24, 0xF9, 0x73, 0x16));
            SetBrush(resources, "OfflineBorderBrush", Color.FromArgb(0x52, 0xF9, 0x73, 0x16));
            SetBrush(resources, "AccentBrush", Color.FromRgb(0x0A, 0x84, 0xFF));
            SetBrush(resources, "AccentBottomBrush", Color.FromRgb(0x00, 0x70, 0xE0));
            SetBrush(resources, "AccentSoftFillBrush", Color.FromArgb(0x24, 0x0A, 0x84, 0xFF));
            SetBrush(resources, "AccentSoftBorderBrush", Color.FromArgb(0x48, 0x0A, 0x84, 0xFF));
            SetBrush(resources, "WarningTextBrush", Color.FromRgb(0xFB, 0x92, 0x3C));
        }
        else
        {
            SetBrush(resources, "PopoverBackgroundBrush", isAcrylicActive
                ? Color.FromArgb(0xB8, 0xF9, 0xFA, 0xFC)
                : Color.FromRgb(0xF8, 0xF9, 0xFB));
            SetBrush(resources, "PopoverBorderBrush", Color.FromArgb(0x22, 0x00, 0x00, 0x00));
            SetBrush(resources, "PrimaryTextBrush", Color.FromRgb(0x16, 0x19, 0x1E));
            SetBrush(resources, "SecondaryTextBrush", Color.FromRgb(0x64, 0x6C, 0x79));
            SetBrush(resources, "SubtleCardBackgroundBrush", Color.FromArgb(0x0D, 0x00, 0x00, 0x00));
            SetBrush(resources, "SubtleCardBorderBrush", Color.FromArgb(0x18, 0x00, 0x00, 0x00));
            SetBrush(resources, "DividerBrush", Color.FromArgb(0x14, 0x00, 0x00, 0x00));
            SetBrush(resources, "PillBackgroundBrush", Color.FromArgb(0x10, 0x00, 0x00, 0x00));
            SetBrush(resources, "HoverFillBrush", Color.FromArgb(0x16, 0x00, 0x00, 0x00));
            SetBrush(resources, "IconButtonFillBrush", Color.FromArgb(0x0E, 0x00, 0x00, 0x00));
            SetBrush(resources, "IconButtonHoverBrush", Color.FromArgb(0x1C, 0x00, 0x00, 0x00));
            SetBrush(resources, "ToggleTrackOffBrush", Color.FromArgb(0x30, 0x00, 0x00, 0x00));
            SetBrush(resources, "OnlineTextBrush", Color.FromRgb(0x15, 0x80, 0x3D));
            SetBrush(resources, "OnlineFillBrush", Color.FromArgb(0x1C, 0x22, 0xC5, 0x5E));
            SetBrush(resources, "OnlineBorderBrush", Color.FromArgb(0x4A, 0x16, 0xA3, 0x4A));
            SetBrush(resources, "OnlineHoverFillBrush", Color.FromArgb(0x26, 0x22, 0xC5, 0x5E));
            SetBrush(resources, "OfflineTextBrush", Color.FromRgb(0xC2, 0x41, 0x0C));
            SetBrush(resources, "OfflineFillBrush", Color.FromArgb(0x1C, 0xF9, 0x73, 0x16));
            SetBrush(resources, "OfflineBorderBrush", Color.FromArgb(0x4A, 0xEA, 0x58, 0x0C));
            SetBrush(resources, "AccentBrush", Color.FromRgb(0x00, 0x78, 0xD4));
            SetBrush(resources, "AccentBottomBrush", Color.FromRgb(0x00, 0x62, 0xB8));
            SetBrush(resources, "AccentSoftFillBrush", Color.FromArgb(0x1B, 0x00, 0x78, 0xD4));
            SetBrush(resources, "AccentSoftBorderBrush", Color.FromArgb(0x3C, 0x00, 0x78, 0xD4));
            SetBrush(resources, "WarningTextBrush", Color.FromRgb(0xD9, 0x77, 0x06));
        }
    }

    private static void SetBrush(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }
}

public static class DwmHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwcpRound = 2;
    private const int DwmsbtTransientWindow = 3; // Acrylic

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins pMarInset);

    public static bool ApplyWindowBackdrop(Window window, bool isDark, bool enableAcrylic)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        int darkValue = isDark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkValue, sizeof(int));

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            int corner = DwmwcpRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        }

        if (!enableAcrylic || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            return false;
        }

        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
        {
            target.BackgroundColor = Colors.Transparent;
        }

        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        int extendHr = DwmExtendFrameIntoClientArea(hwnd, ref margins);
        if (extendHr < 0)
        {
            return false;
        }

        int backdrop = DwmsbtTransientWindow;
        int backdropHr = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
        return backdropHr == 0;
    }
}
