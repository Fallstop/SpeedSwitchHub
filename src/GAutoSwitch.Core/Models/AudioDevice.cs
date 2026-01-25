namespace GAutoSwitch.Core.Models;

/// <summary>
/// Specifies the type of audio device.
/// </summary>
public enum AudioDeviceType
{
    Playback,
    Capture
}

/// <summary>
/// Represents an audio device in the system.
/// </summary>
public sealed class AudioDevice
{
    /// <summary>
    /// The unique device ID (GUID) used by Windows Audio APIs.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The friendly display name of the device (e.g., "Speakers (Realtek High Definition Audio)").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Indicates whether the device is currently active/enabled.
    /// </summary>
    public bool IsActive { get; init; }

    /// <summary>
    /// Indicates if this is the current default device for its type.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// The type of audio device (Playback or Capture).
    /// </summary>
    public AudioDeviceType DeviceType { get; init; }

    public override string ToString() => Name;

    public override bool Equals(object? obj) =>
        obj is AudioDevice other && Id == other.Id;

    public override int GetHashCode() => Id.GetHashCode();
}
