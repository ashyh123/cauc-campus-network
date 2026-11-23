using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Win32;

namespace CAUCCampusNetwork.Services;

public sealed class LaunchAtLoginManager : INotifyPropertyChanged
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CAUCCampusNetwork";

    private bool _isEnabled;
    private string? _statusHint;

    public event PropertyChangedEventHandler? PropertyChanged;

    public LaunchAtLoginManager()
    {
        Refresh();
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled != value)
            {
                SetEnabled(value);
            }
        }
    }

    public string? StatusHint
    {
        get => _statusHint;
        private set
        {
            if (!string.Equals(_statusHint, value, StringComparison.Ordinal))
            {
                _statusHint = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasStatusHint));
            }
        }
    }

    public bool HasStatusHint => !string.IsNullOrEmpty(StatusHint);

    public void Refresh(bool preservingHint = false)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var rawValue = key?.GetValue(ValueName) as string;
            var currentExe = Environment.ProcessPath;

            bool registered = false;
            if (!string.IsNullOrWhiteSpace(rawValue) && !string.IsNullOrWhiteSpace(currentExe))
            {
                var configuredPath = ExtractExecutablePath(rawValue);
                registered = string.Equals(configuredPath, currentExe, StringComparison.OrdinalIgnoreCase);
            }

            SetIsEnabledBacking(registered);
            if (!preservingHint)
            {
                StatusHint = null;
            }
        }
        catch
        {
            SetIsEnabledBacking(false);
            if (!preservingHint)
            {
                StatusHint = "无法读取开机自启配置";
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            if (key is null)
            {
                throw new InvalidOperationException("Unable to open HKCU Run key.");
            }

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    throw new InvalidOperationException("ProcessPath unavailable.");
                }

                key.SetValue(ValueName, $"\"{exePath}\" --autostart", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            StatusHint = null;
            Refresh();
        }
        catch
        {
            StatusHint = enabled
                ? "开启自启失败，请检查权限或安全软件拦截"
                : "无法取消开机自启";
            Refresh(preservingHint: true);
        }
    }

    private void SetIsEnabledBacking(bool value)
    {
        if (_isEnabled != value)
        {
            _isEnabled = value;
            OnPropertyChanged(nameof(IsEnabled));
        }
    }

    private static string ExtractExecutablePath(string commandLine)
    {
        var trimmed = commandLine.Trim();
        if (trimmed.StartsWith('"'))
        {
            int closing = trimmed.IndexOf('"', 1);
            if (closing > 1)
            {
                return trimmed[1..closing];
            }
        }

        int firstSpace = trimmed.IndexOf(' ');
        return firstSpace > 0 ? trimmed[..firstSpace] : trimmed;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
