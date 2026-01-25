namespace GAutoSwitch.Core.Models;

/// <summary>
/// Application settings persisted to disk.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// The device ID of the wireless speaker (Logitech G Pro X 2 via USB dongle).
    /// </summary>
    public string? WirelessDeviceId { get; set; }

    /// <summary>
    /// The device name of the wireless speaker (for fallback matching).
    /// </summary>
    public string? WirelessDeviceName { get; set; }

    /// <summary>
    /// The device ID of the wired speaker (e.g., Realtek, external DAC).
    /// </summary>
    public string? WiredDeviceId { get; set; }

    /// <summary>
    /// The device name of the wired speaker (for fallback matching).
    /// </summary>
    public string? WiredDeviceName { get; set; }

    /// <summary>
    /// The device ID of the wireless microphone.
    /// </summary>
    public string? WirelessMicrophoneId { get; set; }

    /// <summary>
    /// The device name of the wireless microphone (for fallback matching).
    /// </summary>
    public string? WirelessMicrophoneName { get; set; }

    /// <summary>
    /// The device ID of the wired microphone.
    /// </summary>
    public string? WiredMicrophoneId { get; set; }

    /// <summary>
    /// The device name of the wired microphone (for fallback matching).
    /// </summary>
    public string? WiredMicrophoneName { get; set; }

    /// <summary>
    /// Whether to start the application minimized to tray.
    /// </summary>
    public bool StartMinimized { get; set; }

    /// <summary>
    /// Whether to launch on Windows startup.
    /// </summary>
    public bool LaunchOnStartup { get; set; }

    /// <summary>
    /// Whether automatic audio switching is enabled.
    /// </summary>
    public bool AutoSwitchEnabled { get; set; } = true;
}
