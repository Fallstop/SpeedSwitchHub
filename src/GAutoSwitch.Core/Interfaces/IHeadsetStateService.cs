namespace GAutoSwitch.Core.Interfaces;

/// <summary>
/// Service for detecting the connection state of the Logitech G Pro X 2 Lightspeed headset.
/// </summary>
public interface IHeadsetStateService : IDisposable
{
    /// <summary>
    /// Gets the current state of the headset.
    /// </summary>
    HeadsetConnectionState CurrentState { get; }

    /// <summary>
    /// Gets the name of the detected headset product, if available.
    /// </summary>
    string? ProductName { get; }

    /// <summary>
    /// Gets whether the USB dongle is connected to the PC.
    /// </summary>
    bool IsDongleConnected { get; }

    /// <summary>
    /// Performs a one-time detection of the headset state.
    /// </summary>
    /// <param name="listenDurationMs">Duration in milliseconds to listen for data from the headset.</param>
    /// <returns>The detected state.</returns>
    HeadsetConnectionState Detect(int listenDurationMs = 500);

    /// <summary>
    /// Starts continuous monitoring of the headset state.
    /// </summary>
    /// <param name="pollIntervalMs">Interval between state checks in milliseconds.</param>
    void StartMonitoring(int pollIntervalMs = 150);

    /// <summary>
    /// Stops continuous monitoring.
    /// </summary>
    void StopMonitoring();

    /// <summary>
    /// Gets whether monitoring is currently active.
    /// </summary>
    bool IsMonitoring { get; }

    /// <summary>
    /// Raised when the headset connection state changes.
    /// </summary>
    event EventHandler<HeadsetStateChangedEventArgs>? StateChanged;
}

/// <summary>
/// Represents the connection state of the wireless headset.
/// </summary>
public enum HeadsetConnectionState
{
    /// <summary>The USB dongle is not connected to the PC.</summary>
    DongleNotFound,

    /// <summary>The dongle is connected but the headset is powered off (assume wired mode).</summary>
    Offline,

    /// <summary>The headset is connected wirelessly and actively communicating.</summary>
    Online,

    /// <summary>Unable to determine state due to access issues or other errors.</summary>
    Unknown
}

/// <summary>
/// Event arguments for headset state change events.
/// </summary>
public class HeadsetStateChangedEventArgs : EventArgs
{
    public HeadsetConnectionState PreviousState { get; }
    public HeadsetConnectionState NewState { get; }
    public DateTime Timestamp { get; }

    public HeadsetStateChangedEventArgs(HeadsetConnectionState previousState, HeadsetConnectionState newState)
    {
        PreviousState = previousState;
        NewState = newState;
        Timestamp = DateTime.Now;
    }
}
