using GAutoSwitch.Core.Models;

namespace GAutoSwitch.Core.Interfaces;

/// <summary>
/// Service for persisting application settings.
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// Gets the current settings.
    /// </summary>
    AppSettings Settings { get; }

    /// <summary>
    /// Loads settings from disk.
    /// </summary>
    Task LoadAsync();

    /// <summary>
    /// Saves the current settings to disk.
    /// </summary>
    Task SaveAsync();
}
