namespace GAutoSwitch.Core.Interfaces;

/// <summary>
/// Service for managing Windows startup registration.
/// </summary>
public interface IStartupService
{
    /// <summary>
    /// Gets whether the application is registered to start with Windows.
    /// </summary>
    bool IsStartupEnabled { get; }

    /// <summary>
    /// Registers the application to start with Windows.
    /// </summary>
    void EnableStartup();

    /// <summary>
    /// Removes the application from Windows startup.
    /// </summary>
    void DisableStartup();
}
