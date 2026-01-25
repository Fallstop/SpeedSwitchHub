namespace GAutoSwitch.Core.Interfaces;

/// <summary>
/// Status information for the audio proxy.
/// </summary>
public sealed class ProxyStatus
{
    /// <summary>
    /// Whether the proxy process is running.
    /// </summary>
    public bool IsRunning { get; init; }

    /// <summary>
    /// The current output device ID.
    /// </summary>
    public string? OutputDeviceId { get; init; }

    /// <summary>
    /// Error message if the proxy failed to start or encountered an error.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// The process ID of the proxy if running.
    /// </summary>
    public int? ProcessId { get; init; }

    /// <summary>
    /// Whether the microphone proxy is enabled (if configured).
    /// </summary>
    public bool? MicEnabled { get; init; }

    /// <summary>
    /// The current microphone input device ID (if configured).
    /// </summary>
    public string? MicInputDeviceId { get; init; }
}

/// <summary>
/// Event args for proxy status changes.
/// </summary>
public sealed class ProxyStatusEventArgs : EventArgs
{
    public ProxyStatus Status { get; }

    public ProxyStatusEventArgs(ProxyStatus status)
    {
        Status = status;
    }
}

/// <summary>
/// Event args for mic proxy status changes.
/// </summary>
public sealed class MicProxyStatusEventArgs : EventArgs
{
    public bool IsEnabled { get; }
    public string? InputDeviceId { get; }
    public string? ErrorMessage { get; }

    public MicProxyStatusEventArgs(bool isEnabled, string? inputDeviceId, string? errorMessage = null)
    {
        IsEnabled = isEnabled;
        InputDeviceId = inputDeviceId;
        ErrorMessage = errorMessage;
    }
}

/// <summary>
/// Service for managing the low-latency audio proxy.
/// The proxy captures audio from a virtual device (e.g., VB-Cable) and forwards
/// it to the actual output device, allowing seamless device switching for games
/// that don't support Windows audio device switching.
/// </summary>
public interface IAudioProxyService : IDisposable
{
    /// <summary>
    /// Whether the proxy process is currently running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Whether VB-Cable Output (or a compatible virtual audio device) is installed for speaker proxy.
    /// </summary>
    bool IsVBCableInstalled { get; }

    /// <summary>
    /// The device ID of the detected virtual audio output device (VB-Cable Output) for speaker proxy.
    /// </summary>
    string? VBCableDeviceId { get; }

    /// <summary>
    /// Whether VB-Cable Input (for microphone proxy) is installed.
    /// </summary>
    bool IsVBCableInputInstalled { get; }

    /// <summary>
    /// The device ID of the detected virtual audio input device (VB-Cable Input) for mic proxy.
    /// </summary>
    string? VBCableInputDeviceId { get; }

    /// <summary>
    /// Whether the microphone proxy is currently enabled.
    /// </summary>
    bool IsMicProxyEnabled { get; }

    /// <summary>
    /// Event raised when the proxy status changes.
    /// </summary>
    event EventHandler<ProxyStatusEventArgs>? StatusChanged;

    /// <summary>
    /// Event raised when the microphone proxy status changes.
    /// </summary>
    event EventHandler<MicProxyStatusEventArgs>? MicStatusChanged;

    /// <summary>
    /// Starts the audio proxy, capturing from the virtual device and outputting
    /// to the specified device.
    /// </summary>
    /// <param name="outputDeviceId">The ID of the output device to play audio to.</param>
    /// <returns>True if the proxy started successfully.</returns>
    Task<bool> StartAsync(string outputDeviceId);

    /// <summary>
    /// Starts the audio proxy with optional microphone proxy support.
    /// </summary>
    /// <param name="speakerOutputDeviceId">The ID of the output device for speaker audio.</param>
    /// <param name="micInputDeviceId">The ID of the physical microphone to capture (optional).</param>
    /// <returns>True if the proxy started successfully.</returns>
    Task<bool> StartAsync(string speakerOutputDeviceId, string? micInputDeviceId);

    /// <summary>
    /// Stops the audio proxy.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Changes the speaker output device without stopping the proxy.
    /// This is the key feature that allows seamless device switching.
    /// </summary>
    /// <param name="deviceId">The new output device ID.</param>
    /// <returns>True if the device was changed successfully.</returns>
    Task<bool> SetOutputDeviceAsync(string deviceId);

    /// <summary>
    /// Enables or disables the microphone proxy at runtime.
    /// </summary>
    /// <param name="enabled">Whether to enable the mic proxy.</param>
    /// <returns>True if the command was sent successfully.</returns>
    Task<bool> SetMicProxyEnabledAsync(bool enabled);

    /// <summary>
    /// Changes the microphone input device (hot-swap) without stopping the proxy.
    /// </summary>
    /// <param name="deviceId">The new microphone device ID.</param>
    /// <returns>True if the device was changed successfully.</returns>
    Task<bool> SetMicInputDeviceAsync(string deviceId);

    /// <summary>
    /// Gets the current status of the proxy.
    /// </summary>
    ProxyStatus GetStatus();

    /// <summary>
    /// Refreshes the VB-Cable Output detection status (for speaker proxy).
    /// </summary>
    void RefreshVBCableStatus();

    /// <summary>
    /// Refreshes the VB-Cable Input detection status (for mic proxy).
    /// </summary>
    void RefreshVBCableInputStatus();
}
