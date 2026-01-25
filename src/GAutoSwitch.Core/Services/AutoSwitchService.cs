using System.Diagnostics;
using GAutoSwitch.Core.Interfaces;

namespace GAutoSwitch.Core.Services;

/// <summary>
/// Orchestrates automatic audio device switching based on headset state changes.
/// </summary>
public class AutoSwitchService : IAutoSwitchService
{
    private readonly IHeadsetStateService _headsetStateService;
    private readonly IAudioDeviceService _audioDeviceService;
    private readonly ISettingsService _settingsService;

    private AutoSwitchState _currentState = AutoSwitchState.Stopped;
    private bool _isEnabled = true;
    private bool _isUsingConfiguredDevice = true;
    private bool _isDisposed;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled != value)
            {
                _isEnabled = value;
                IsEnabledChanged?.Invoke(this, value);
            }
        }
    }

    public bool IsUsingConfiguredDevice => _isUsingConfiguredDevice;

    public AutoSwitchState CurrentState => _currentState;
    public bool IsTransitioning => false;
    public DateTime? LastSwitchTime { get; private set; }
    public string? LastSwitchDescription { get; private set; }

    public event EventHandler<AudioSwitchedEventArgs>? AudioSwitched;
    public event EventHandler<AutoSwitchStateChangedEventArgs>? StateChanged;
    public event EventHandler<bool>? IsEnabledChanged;
    public event EventHandler<bool>? ConfiguredDeviceStateChanged;

    public AutoSwitchService(
        IHeadsetStateService headsetStateService,
        IAudioDeviceService audioDeviceService,
        ISettingsService settingsService)
    {
        _headsetStateService = headsetStateService;
        _audioDeviceService = audioDeviceService;
        _settingsService = settingsService;
    }

    public void Start()
    {
        if (_currentState != AutoSwitchState.Stopped)
            return;

        Debug.WriteLine("[AutoSwitchService] Starting...");

        // Subscribe to headset state changes
        _headsetStateService.StateChanged += OnHeadsetStateChanged;

        // Subscribe to audio device changes to recheck configured device state
        _audioDeviceService.DevicesChanged += OnDevicesChanged;

        // Set initial state based on current headset state
        UpdateStateFromHeadset(_headsetStateService.CurrentState);

        // Check initial configured device state
        CheckConfiguredDeviceState();

        // Start headset monitoring if not already running
        if (!_headsetStateService.IsMonitoring)
        {
            _headsetStateService.StartMonitoring();
        }

        Debug.WriteLine($"[AutoSwitchService] Started. Initial state: {_currentState}");
    }

    public void Stop()
    {
        if (_currentState == AutoSwitchState.Stopped)
            return;

        Debug.WriteLine("[AutoSwitchService] Stopping...");

        _headsetStateService.StateChanged -= OnHeadsetStateChanged;
        _audioDeviceService.DevicesChanged -= OnDevicesChanged;

        SetState(AutoSwitchState.Stopped);
    }

    public void ForceApply()
    {
        Debug.WriteLine("[AutoSwitchService] Force apply requested");

        var headsetState = _headsetStateService.CurrentState;

        switch (headsetState)
        {
            case HeadsetConnectionState.Offline:
                ForceSwitch(SwitchDirection.ToWired);
                break;

            case HeadsetConnectionState.Online:
                ForceSwitch(SwitchDirection.ToWireless);
                break;

            default:
                Debug.WriteLine($"[AutoSwitchService] Cannot force apply in state: {headsetState}");
                break;
        }
    }

    private void ForceSwitch(SwitchDirection direction)
    {
        Debug.WriteLine($"[AutoSwitchService] Force switching: {direction}");

        var settings = _settingsService.Settings;
        var wirelessId = settings.WirelessDeviceId;
        var wiredId = settings.WiredDeviceId;

        if (string.IsNullOrEmpty(wirelessId) || string.IsNullOrEmpty(wiredId))
        {
            Debug.WriteLine("[AutoSwitchService] Cannot force switch: devices not configured");
            return;
        }

        string targetId = direction == SwitchDirection.ToWireless ? wirelessId : wiredId;
        var targetDevice = _audioDeviceService.GetDeviceById(targetId);
        string targetName = targetDevice?.Name ?? (direction == SwitchDirection.ToWireless ? "Wireless Device" : "Wired Device");

        bool speakerSuccess = _audioDeviceService.SetDefaultDevice(targetId);

        string? targetMicId = direction == SwitchDirection.ToWireless
            ? settings.WirelessMicrophoneId
            : settings.WiredMicrophoneId;

        bool micSuccess = true;
        string? micName = null;
        if (!string.IsNullOrEmpty(targetMicId))
        {
            micSuccess = _audioDeviceService.SetDefaultCaptureDevice(targetMicId);
            var micDevice = _audioDeviceService.GetDeviceById(targetMicId);
            micName = micDevice?.Name;
        }

        bool success = speakerSuccess && micSuccess;

        LastSwitchTime = DateTime.Now;
        LastSwitchDescription = success
            ? $"Force switched to {targetName}" + (micName != null ? $" + {micName}" : "")
            : $"Failed to force switch to {targetName}";

        Debug.WriteLine($"[AutoSwitchService] Force switch result: {LastSwitchDescription}");

        CheckConfiguredDeviceState();
        AudioSwitched?.Invoke(this, new AudioSwitchedEventArgs(direction, targetName, success));
    }

    private void OnHeadsetStateChanged(object? sender, HeadsetStateChangedEventArgs e)
    {
        Debug.WriteLine($"[AutoSwitchService] Headset state changed: {e.PreviousState} -> {e.NewState}");

        if (!_isEnabled)
        {
            Debug.WriteLine("[AutoSwitchService] Auto-switch disabled, ignoring state change");
            UpdateStateFromHeadset(e.NewState);
            return;
        }

        switch (e.NewState)
        {
            case HeadsetConnectionState.Offline:
                ExecuteSwitch(SwitchDirection.ToWired);
                break;

            case HeadsetConnectionState.Online:
                ExecuteSwitch(SwitchDirection.ToWireless);
                break;

            case HeadsetConnectionState.DongleNotFound:
                SetState(AutoSwitchState.DongleNotFound);
                break;

            case HeadsetConnectionState.Unknown:
                SetState(AutoSwitchState.Unknown);
                break;
        }
    }

    private void UpdateStateFromHeadset(HeadsetConnectionState headsetState)
    {
        var newState = headsetState switch
        {
            HeadsetConnectionState.Online => AutoSwitchState.WirelessConnected,
            HeadsetConnectionState.Offline => AutoSwitchState.WirelessDisconnected,
            HeadsetConnectionState.DongleNotFound => AutoSwitchState.DongleNotFound,
            _ => AutoSwitchState.Unknown
        };

        SetState(newState);
    }

    private void ExecuteSwitch(SwitchDirection direction)
    {
        Debug.WriteLine($"[AutoSwitchService] Executing switch: {direction}");

        var settings = _settingsService.Settings;
        var wirelessId = settings.WirelessDeviceId;
        var wiredId = settings.WiredDeviceId;

        Debug.WriteLine($"[AutoSwitchService] Configured wireless: {wirelessId ?? "(null)"}");
        Debug.WriteLine($"[AutoSwitchService] Configured wired: {wiredId ?? "(null)"}");

        // Check if we have configured devices
        if (string.IsNullOrEmpty(wirelessId) || string.IsNullOrEmpty(wiredId))
        {
            Debug.WriteLine("[AutoSwitchService] Cannot switch: devices not configured");
            UpdateStateFromHeadset(_headsetStateService.CurrentState);
            return;
        }

        // Get current default device
        var currentDefault = _audioDeviceService.GetDefaultDevice();
        if (currentDefault == null)
        {
            Debug.WriteLine("[AutoSwitchService] Cannot switch: no default device found");
            Debug.WriteLine($"[AutoSwitchService] Available devices: {_audioDeviceService.GetPlaybackDevices().Count}");
            UpdateStateFromHeadset(_headsetStateService.CurrentState);
            return;
        }

        Debug.WriteLine($"[AutoSwitchService] Current default: {currentDefault.Name} ({currentDefault.Id})");

        // Config matching rule: only switch if current device matches one of our configured pair
        bool isCurrentlyWireless = currentDefault.Id == wirelessId;
        bool isCurrentlyWired = currentDefault.Id == wiredId;

        if (!isCurrentlyWireless && !isCurrentlyWired)
        {
            Debug.WriteLine($"[AutoSwitchService] Skipping switch: current device ({currentDefault.Name}) is not in configured pair");
            UpdateStateFromHeadset(_headsetStateService.CurrentState);
            return;
        }

        // Determine target device
        string targetId;
        string targetName;

        if (direction == SwitchDirection.ToWireless && isCurrentlyWired)
        {
            targetId = wirelessId;
            var targetDevice = _audioDeviceService.GetDeviceById(wirelessId);
            targetName = targetDevice?.Name ?? "Wireless Device";
        }
        else if (direction == SwitchDirection.ToWired && isCurrentlyWireless)
        {
            targetId = wiredId;
            var targetDevice = _audioDeviceService.GetDeviceById(wiredId);
            targetName = targetDevice?.Name ?? "Wired Device";
        }
        else
        {
            Debug.WriteLine($"[AutoSwitchService] No switch needed: direction={direction}, isWireless={isCurrentlyWireless}, isWired={isCurrentlyWired}");
            UpdateStateFromHeadset(_headsetStateService.CurrentState);
            return;
        }

        // Execute the speaker switch
        bool speakerSuccess = _audioDeviceService.SetDefaultDevice(targetId);

        // Also switch the microphone if configured
        string? targetMicId = direction == SwitchDirection.ToWireless
            ? settings.WirelessMicrophoneId
            : settings.WiredMicrophoneId;

        bool micSuccess = true;
        string? micName = null;
        if (!string.IsNullOrEmpty(targetMicId))
        {
            micSuccess = _audioDeviceService.SetDefaultCaptureDevice(targetMicId);
            var micDevice = _audioDeviceService.GetDeviceById(targetMicId);
            micName = micDevice?.Name;
            Debug.WriteLine($"[AutoSwitchService] Microphone switch to '{micName ?? targetMicId}': {(micSuccess ? "success" : "failed")}");
        }

        bool success = speakerSuccess && micSuccess;

        LastSwitchTime = DateTime.Now;
        LastSwitchDescription = success
            ? $"Switched to {targetName}" + (micName != null ? $" + {micName}" : "")
            : $"Failed to switch to {targetName}";

        Debug.WriteLine($"[AutoSwitchService] Switch result: {LastSwitchDescription}");

        // Update state based on headset
        UpdateStateFromHeadset(_headsetStateService.CurrentState);

        // Recheck configured device state after switch
        CheckConfiguredDeviceState();

        // Raise event
        AudioSwitched?.Invoke(this, new AudioSwitchedEventArgs(direction, targetName, success));
    }

    private void SetState(AutoSwitchState newState)
    {
        if (_currentState == newState)
            return;

        var previousState = _currentState;
        _currentState = newState;

        Debug.WriteLine($"[AutoSwitchService] State changed: {previousState} -> {newState}");
        StateChanged?.Invoke(this, new AutoSwitchStateChangedEventArgs(previousState, newState));
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        // Recheck if we're using a configured device when device list changes
        CheckConfiguredDeviceState();
    }

    private void CheckConfiguredDeviceState()
    {
        var settings = _settingsService.Settings;
        var wirelessId = settings.WirelessDeviceId;
        var wiredId = settings.WiredDeviceId;

        // If devices aren't configured, consider us as using configured device (nothing to check against)
        if (string.IsNullOrEmpty(wirelessId) || string.IsNullOrEmpty(wiredId))
        {
            SetUsingConfiguredDevice(true);
            return;
        }

        var currentDefault = _audioDeviceService.GetDefaultDevice();
        if (currentDefault == null)
        {
            SetUsingConfiguredDevice(false);
            return;
        }

        bool isUsingConfigured = currentDefault.Id == wirelessId || currentDefault.Id == wiredId;
        SetUsingConfiguredDevice(isUsingConfigured);
    }

    private void SetUsingConfiguredDevice(bool value)
    {
        if (_isUsingConfiguredDevice != value)
        {
            _isUsingConfiguredDevice = value;
            Debug.WriteLine($"[AutoSwitchService] IsUsingConfiguredDevice changed: {value}");
            ConfiguredDeviceStateChanged?.Invoke(this, value);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        Stop();
        _isDisposed = true;
    }
}
