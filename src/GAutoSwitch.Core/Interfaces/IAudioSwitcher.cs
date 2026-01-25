namespace GAutoSwitch.Core.Interfaces;

/// <summary>
/// Specifies the data flow direction for audio devices.
/// </summary>
public enum EDataFlow
{
    /// <summary>Audio rendering (playback) endpoint.</summary>
    Render = 0,
    /// <summary>Audio capture (recording) endpoint.</summary>
    Capture = 1
}

/// <summary>
/// Specifies the role of an audio endpoint.
/// </summary>
public enum ERole
{
    /// <summary>Games, system notification sounds, and voice commands.</summary>
    Console = 0,
    /// <summary>Music, movies, narration, and live music recording.</summary>
    Multimedia = 1,
    /// <summary>Voice communications (talking to another person).</summary>
    Communications = 2
}

/// <summary>
/// Interface for switching the default audio device and migrating active audio sessions.
/// Uses undocumented Windows COM interfaces (IPolicyConfig) for aggressive audio switching.
/// </summary>
public interface IAudioSwitcher
{
    /// <summary>
    /// Sets the default audio endpoint for the specified flow and all roles.
    /// </summary>
    /// <param name="deviceId">The device ID (MMDevice ID format).</param>
    /// <param name="flow">The data flow direction (Render or Capture).</param>
    /// <returns>True if successful, false otherwise.</returns>
    bool SetDefaultDevice(string deviceId, EDataFlow flow = EDataFlow.Render);

    /// <summary>
    /// Migrates all active audio sessions to the specified device.
    /// This allows audio to move without requiring applications to restart.
    /// </summary>
    /// <param name="toDeviceId">The target device ID.</param>
    /// <param name="flow">The data flow direction.</param>
    /// <returns>The number of sessions successfully migrated.</returns>
    int MigrateActiveSessions(string toDeviceId, EDataFlow flow = EDataFlow.Render);

    /// <summary>
    /// Gets whether aggressive audio switching is supported on this system.
    /// Returns false if required COM interfaces are unavailable.
    /// </summary>
    bool IsSupported { get; }
}
