using System.IO;
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
        var startupCommand = GetStartupCommand();
        if (startupCommand != null)
        {
            key?.SetValue(AppName, startupCommand);
        }
    }

    public void DisableStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        key?.DeleteValue(AppName, false);
    }

    /// <summary>
    /// Gets the startup command, using Update.exe for Squirrel installations
    /// to ensure the correct version is launched after updates.
    /// </summary>
    private static string? GetStartupCommand()
    {
        var processPath = Environment.ProcessPath;
        if (processPath == null) return null;

        var processDir = Path.GetDirectoryName(processPath);
        var parentDir = processDir != null ? Path.GetDirectoryName(processDir) : null;

        // Check if this is a Squirrel installation by looking for Update.exe in parent directory
        if (parentDir != null)
        {
            var updateExe = Path.Combine(parentDir, "Update.exe");
            if (File.Exists(updateExe))
            {
                // Use Update.exe --processStart to launch the app
                // This ensures the correct version is launched after updates
                var exeName = Path.GetFileName(processPath);
                return $"\"{updateExe}\" --processStart \"{exeName}\" --process-start-args \"--minimized\"";
            }
        }

        // Fallback for non-Squirrel installations (development, portable, etc.)
        return $"\"{processPath}\" --minimized";
    }
}
