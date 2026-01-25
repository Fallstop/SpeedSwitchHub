namespace GAutoSwitch.Core.Interfaces;

/// <summary>
/// Service that orchestrates automatic audio device switching based on headset state changes.
/// </summary>
public interface IAutoSwitchService : IDisposable
{
    /// <summary>
    /// Gets or sets whether auto-switching is enabled.
    /// </summary>
    bool IsEnabled { get; set; }

    /// <summary>
    /// Gets the current state of the auto-switch service.
    /// </summary>
    AutoSwitchState CurrentState { get; }

    /// <summary>
    /// Gets whether a switch is currently being debounced.
    /// </summary>
    bool IsTransitioning { get; }

    /// <summary>
    /// Gets the timestamp of the last successful audio switch.
    /// </summary>
    DateTime? LastSwitchTime { get; }

    /// <summary>
    /// Gets a description of the last audio switch action.
    /// </summary>
    string? LastSwitchDescription { get; }

    /// <summary>
    /// Gets whether the current default device is in the configured wireless/wired pair.
    /// </summary>
    bool IsUsingConfiguredDevice { get; }

    /// <summary>
    /// Starts the auto-switch service and begins monitoring headset state.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops the auto-switch service.
    /// </summary>
    void Stop();

    /// <summary>
    /// Forces an immediate switch based on current headset state, ignoring normal conditions.
    /// </summary>
    void ForceApply();

    /// <summary>
    /// Raised when an audio device switch is performed.
    /// </summary>
    event EventHandler<AudioSwitchedEventArgs>? AudioSwitched;

    /// <summary>
    /// Raised when the auto-switch state changes.
    /// </summary>
    event EventHandler<AutoSwitchStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Raised when IsEnabled changes.
    /// </summary>
    event EventHandler<bool>? IsEnabledChanged;

    /// <summary>
    /// Raised when IsUsingConfiguredDevice changes.
    /// </summary>
    event EventHandler<bool>? ConfiguredDeviceStateChanged;
}

/// <summary>
/// Represents the current state of the auto-switch service.
/// </summary>
public enum AutoSwitchState
{
    /// <summary>The service is stopped.</summary>
    Stopped,

    /// <summary>The wireless headset is connected and active.</summary>
    WirelessConnected,

    /// <summary>The wireless headset is disconnected, using wired fallback.</summary>
    WirelessDisconnected,

    /// <summary>A state transition is in progress (debouncing).</summary>
    Transitioning,

    /// <summary>The USB dongle is not connected.</summary>
    DongleNotFound,

    /// <summary>Unable to determine state.</summary>
    Unknown
}

/// <summary>
/// Represents the direction of an audio device switch.
/// </summary>
public enum SwitchDirection
{
    /// <summary>Switching to the wireless audio device.</summary>
    ToWireless,

    /// <summary>Switching to the wired audio device.</summary>
    ToWired
}

/// <summary>
/// Event arguments for audio switch events.
/// </summary>
public class AudioSwitchedEventArgs : EventArgs
{
    /// <summary>
    /// Gets the direction of the switch.
    /// </summary>
    public SwitchDirection Direction { get; }

    /// <summary>
    /// Gets the name of the device that was switched to.
    /// </summary>
    public string DeviceName { get; }

    /// <summary>
    /// Gets whether the switch was successful.
    /// </summary>
    public bool Success { get; }

    /// <summary>
    /// Gets the timestamp of the switch.
    /// </summary>
    public DateTime Timestamp { get; }

    public AudioSwitchedEventArgs(SwitchDirection direction, string deviceName, bool success)
    {
        Direction = direction;
        DeviceName = deviceName;
        Success = success;
        Timestamp = DateTime.Now;
    }
}

/// <summary>
/// Event arguments for auto-switch state change events.
/// </summary>
public class AutoSwitchStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// Gets the previous state.
    /// </summary>
    public AutoSwitchState PreviousState { get; }

    /// <summary>
    /// Gets the new state.
    /// </summary>
    public AutoSwitchState NewState { get; }

    /// <summary>
    /// Gets the timestamp of the state change.
    /// </summary>
    public DateTime Timestamp { get; }

    public AutoSwitchStateChangedEventArgs(AutoSwitchState previousState, AutoSwitchState newState)
    {
        PreviousState = previousState;
        NewState = newState;
        Timestamp = DateTime.Now;
    }
}
