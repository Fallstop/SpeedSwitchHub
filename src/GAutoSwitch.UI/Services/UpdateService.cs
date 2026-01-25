using System.Diagnostics;
using Squirrel;
using Squirrel.Sources;

namespace GAutoSwitch.UI.Services;

/// <summary>
/// Manages application updates using Squirrel.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly string? _updateUrl;
    private UpdateManager? _updateManager;
    private bool _disposed;

    /// <summary>
    /// Gets or sets the URL where updates are hosted (GitHub repo URL or file path).
    /// </summary>
    public string? UpdateUrl
    {
        get => _updateUrl;
        init => _updateUrl = value;
    }

    /// <summary>
    /// Gets a value indicating whether an update is available.
    /// </summary>
    public bool IsUpdateAvailable { get; private set; }

    /// <summary>
    /// Gets the latest available version, if an update check has been performed.
    /// </summary>
    public SemanticVersion? LatestVersion { get; private set; }

    /// <summary>
    /// Occurs when update progress changes during download.
    /// </summary>
    public event EventHandler<int>? DownloadProgress;

    /// <summary>
    /// Checks for available updates.
    /// </summary>
    /// <returns>True if an update is available, false otherwise.</returns>
    public async Task<bool> CheckForUpdatesAsync()
    {
        if (string.IsNullOrEmpty(_updateUrl))
        {
            Debug.WriteLine("UpdateService: No update URL configured");
            return false;
        }

        try
        {
            // Create update source - use GithubSource for GitHub releases
            IUpdateSource source = _updateUrl.Contains("github.com")
                ? new GithubSource(_updateUrl, null, false)
                : new SimpleWebSource(_updateUrl);

            _updateManager = new UpdateManager(source);
            var updateInfo = await _updateManager.CheckForUpdate();

            if (updateInfo?.ReleasesToApply?.Count > 0)
            {
                IsUpdateAvailable = true;
                LatestVersion = updateInfo.FutureReleaseEntry?.Version;
                Debug.WriteLine($"UpdateService: Update available - {LatestVersion}");
                return true;
            }

            IsUpdateAvailable = false;
            Debug.WriteLine("UpdateService: No updates available");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UpdateService: Error checking for updates - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Downloads and applies available updates.
    /// </summary>
    /// <returns>True if updates were applied successfully, false otherwise.</returns>
    public async Task<bool> DownloadAndApplyUpdatesAsync()
    {
        if (_updateManager == null)
        {
            Debug.WriteLine("UpdateService: Must call CheckForUpdatesAsync first");
            return false;
        }

        try
        {
            var updateInfo = await _updateManager.CheckForUpdate();
            if (updateInfo?.ReleasesToApply?.Count > 0)
            {
                Debug.WriteLine("UpdateService: Downloading updates...");
                await _updateManager.DownloadReleases(updateInfo.ReleasesToApply, progress =>
                {
                    DownloadProgress?.Invoke(this, progress);
                });

                Debug.WriteLine("UpdateService: Applying updates...");
                await _updateManager.ApplyReleases(updateInfo);

                Debug.WriteLine("UpdateService: Updates applied successfully");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UpdateService: Error applying updates - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Restarts the application to apply updates.
    /// </summary>
    public static void RestartApp()
    {
        UpdateManager.RestartApp();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _updateManager?.Dispose();
        _updateManager = null;
    }
}
