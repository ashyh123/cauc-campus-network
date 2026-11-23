using System;
using System.Windows;
using CAUCCampusNetwork.Services;

namespace CAUCCampusNetwork.Views;

public partial class PortalWindow : Window
{
    public PortalWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    public void ApplyTheme(bool isDark)
    {
        DwmHelper.ApplyWindowBackdrop(this, isDark, enableAcrylic: false);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyTheme(SystemThemeHelper.IsAppDarkMode());
    }
}
