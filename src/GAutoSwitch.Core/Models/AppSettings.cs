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

    // ========================================
    // Optional Audio Proxy Settings
    // (for games that don't support device switching)
    // ========================================

    /// <summary>
    /// Whether to use the audio proxy feature.
    /// When enabled, audio is routed through a virtual device (VB-Cable)
    /// which allows seamless device switching for stubborn games.
    /// </summary>
    public bool UseAudioProxy { get; set; } = false;

    /// <summary>
    /// The device ID of the virtual audio input device (e.g., VB-Cable).
    /// If null, the service will auto-detect VB-Cable.
    /// </summary>
    public string? ProxyInputDeviceId { get; set; }

    /// <summary>
    /// Buffer size in milliseconds for the audio proxy.
    /// Lower values = less latency but more CPU usage.
    /// Recommended: 10ms (default) for gaming.
    /// </summary>
    public int ProxyBufferMs { get; set; } = 10;

    // ========================================
    // Microphone Proxy Settings
    // ========================================

    /// <summary>
    /// Whether to use the microphone proxy feature.
    /// When enabled, microphone audio is captured from a physical mic and
    /// forwarded to a virtual device (VB-Cable Input) for apps to capture.
    /// </summary>
    public bool UseMicProxy { get; set; } = false;

    /// <summary>
    /// The device ID of the physical microphone to capture audio from.
    /// </summary>
    public string? MicProxyInputDeviceId { get; set; }

    /// <summary>
    /// Whether to auto-start the mic proxy when the speaker proxy starts.
    /// </summary>
    public bool MicProxyAutoStart { get; set; } = true;
}
