using GAutoSwitch.Core.Interfaces;
using Microsoft.Win32;

namespace GAutoSwitch.Core.Services;

/// <summary>
/// Manages Windows startup registration via the registry.
/// </summary>
public sealed class StartupService : IStartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "GAutoSwitch";

    public bool IsStartupEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(AppName) != null;
        }
    }

    public void EnableStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        var exePath = Environment.ProcessPath;
        if (exePath != null)
        {
            key?.SetValue(AppName, $"\"{exePath}\" --minimized");
        }
    }

    public void DisableStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        key?.DeleteValue(AppName, false);
    }
}
