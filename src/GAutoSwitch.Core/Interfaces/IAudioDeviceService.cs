using GAutoSwitch.Core.Models;

namespace GAutoSwitch.Core.Interfaces;

/// <summary>
/// Service for enumerating and managing audio devices.
/// </summary>
public interface IAudioDeviceService
{
    /// <summary>
    /// Gets all active audio playback devices (speakers/headphones).
    /// </summary>
    IReadOnlyList<AudioDevice> GetPlaybackDevices();

    /// <summary>
    /// Gets all active audio capture devices (microphones).
    /// </summary>
    IReadOnlyList<AudioDevice> GetCaptureDevices();

    /// <summary>
    /// Gets a specific device by its ID.
    /// </summary>
    AudioDevice? GetDeviceById(string deviceId);

    /// <summary>
    /// Gets the current default playback device.
    /// </summary>
    AudioDevice? GetDefaultDevice();

    /// <summary>
    /// Gets the current default capture device.
    /// </summary>
    AudioDevice? GetDefaultCaptureDevice();

    /// <summary>
    /// Sets the default playback device.
    /// </summary>
    bool SetDefaultDevice(string deviceId);

    /// <summary>
    /// Sets the default capture device.
    /// </summary>
    bool SetDefaultCaptureDevice(string deviceId);

    /// <summary>
    /// Raised when the list of available devices changes.
    /// </summary>
    event EventHandler? DevicesChanged;
}
