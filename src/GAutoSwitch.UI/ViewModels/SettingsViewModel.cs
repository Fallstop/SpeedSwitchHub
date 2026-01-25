using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using GAutoSwitch.Core.Interfaces;
using GAutoSwitch.Core.Models;
using Microsoft.UI.Dispatching;

namespace GAutoSwitch.UI.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IAudioDeviceService _audioDeviceService;
    private readonly ISettingsService _settingsService;
    private readonly IStartupService _startupService;
    private readonly IHeadsetStateService _headsetStateService;
    private readonly IAutoSwitchService _autoSwitchService;
    private readonly DispatcherQueue? _dispatcherQueue;

    public ObservableCollection<AudioDevice> PlaybackDevices { get; } = [];
    public ObservableCollection<AudioDevice> CaptureDevices { get; } = [];

    [ObservableProperty]
    private AudioDevice? _selectedWirelessSpeaker;

    [ObservableProperty]
    private AudioDevice? _selectedWiredSpeaker;

    [ObservableProperty]
    private AudioDevice? _selectedWirelessMicrophone;

    [ObservableProperty]
    private AudioDevice? _selectedWiredMicrophone;

    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private bool _launchOnStartup;

    [ObservableProperty]
    private bool _isLoading;

    private bool _isRefreshingDevices;

    [ObservableProperty]
    private string _currentDefaultSpeaker = "None";

    [ObservableProperty]
    private string _currentDefaultMicrophone = "None";

    [ObservableProperty]
    private bool _isUsingConfiguredDevice = true;

    [ObservableProperty]
    private string _expectedSpeaker = "";

    [ObservableProperty]
    private string _expectedMicrophone = "";

    [ObservableProperty]
    private bool _hasDeviceOverride;

    [ObservableProperty]
    private bool _autoSwitchEnabled = true;

    [ObservableProperty]
    private string _matchStatusText = "Matches config";

    [ObservableProperty]
    private string _matchStatusIcon = "\uE73E"; // Checkmark

    [ObservableProperty]
    private bool _isSwitchingDevices;

    [ObservableProperty]
    private string _switchingDevicesText = "";

    public SettingsViewModel(
        IAudioDeviceService audioDeviceService,
        ISettingsService settingsService,
        IStartupService startupService,
        IHeadsetStateService headsetStateService)
    {
        _audioDeviceService = audioDeviceService;
        _settingsService = settingsService;
        _startupService = startupService;
        _headsetStateService = headsetStateService;
        _autoSwitchService = App.AutoSwitchService;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _audioDeviceService.DevicesChanged += OnDevicesChanged;
        _autoSwitchService.ConfiguredDeviceStateChanged += OnConfiguredDeviceStateChanged;
        _headsetStateService.StateChanged += OnHeadsetStateChanged;
    }

    public async Task InitializeAsync()
    {
        IsLoading = true;
        try
        {
            await _settingsService.LoadAsync();
            LoadSettingsToViewModel();
            RefreshDevices();
            UpdateOverrideState();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void LoadSettingsToViewModel()
    {
        var settings = _settingsService.Settings;
        StartMinimized = settings.StartMinimized;
        // Sync with actual registry state
        LaunchOnStartup = _startupService.IsStartupEnabled;
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        // DevicesChanged may fire from a background thread, so dispatch to UI
        if (_dispatcherQueue != null)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                RefreshDevices();
                UpdateOverrideState();
            });
        }
        else
        {
            RefreshDevices();
            UpdateOverrideState();
        }
    }

    private void OnConfiguredDeviceStateChanged(object? sender, bool isUsingConfigured)
    {
        if (_dispatcherQueue != null)
        {
            _dispatcherQueue.TryEnqueue(UpdateOverrideState);
        }
        else
        {
            UpdateOverrideState();
        }
    }

    private void OnHeadsetStateChanged(object? sender, HeadsetStateChangedEventArgs e)
    {
        // Check if this is a real device switch (Online <-> Offline)
        bool isDeviceSwitch = (e.PreviousState == HeadsetConnectionState.Online && e.NewState == HeadsetConnectionState.Offline) ||
                              (e.PreviousState == HeadsetConnectionState.Offline && e.NewState == HeadsetConnectionState.Online);

        if (_dispatcherQueue != null)
        {
            _dispatcherQueue.TryEnqueue(() => HandleHeadsetStateChange(e.NewState, isDeviceSwitch));
        }
        else
        {
            HandleHeadsetStateChange(e.NewState, isDeviceSwitch);
        }
    }

    private async void HandleHeadsetStateChange(HeadsetConnectionState newState, bool isDeviceSwitch)
    {
        if (isDeviceSwitch)
        {
            // Show immediate switching feedback
            IsSwitchingDevices = true;
            SwitchingDevicesText = newState == HeadsetConnectionState.Online
                ? "Switching to wireless devices..."
                : "Switching to wired devices...";

            // Show "Switching..." in the current defaults display
            CurrentDefaultSpeaker = SwitchingDevicesText;
            CurrentDefaultMicrophone = "...";
            MatchStatusText = "Switching...";
            MatchStatusIcon = "\uE895"; // Sync icon

            // Wait for Windows to complete the switch
            await Task.Delay(1200);

            IsSwitchingDevices = false;
            SwitchingDevicesText = "";
        }

        UpdateOverrideState();
    }

    private void UpdateOverrideState()
    {
        var headsetState = _headsetStateService.CurrentState;
        bool isWireless = headsetState == HeadsetConnectionState.Online;

        // Determine expected devices based on headset state
        AudioDevice? expectedSpeaker = isWireless ? SelectedWirelessSpeaker : SelectedWiredSpeaker;
        AudioDevice? expectedMic = isWireless ? SelectedWirelessMicrophone : SelectedWiredMicrophone;

        ExpectedSpeaker = expectedSpeaker?.Name ?? "Not configured";
        ExpectedMicrophone = expectedMic?.Name ?? "Not configured";

        // Check if current Windows defaults match expected
        var currentSpeaker = _audioDeviceService.GetDefaultDevice();
        var currentMic = _audioDeviceService.GetDefaultCaptureDevice();

        bool speakerMatches = expectedSpeaker == null || currentSpeaker?.Id == expectedSpeaker.Id;
        bool micMatches = expectedMic == null || currentMic?.Id == expectedMic.Id;

        IsUsingConfiguredDevice = speakerMatches && micMatches;
        HasDeviceOverride = !IsUsingConfiguredDevice && (expectedSpeaker != null || expectedMic != null);

        // Update match status display
        if (IsUsingConfiguredDevice)
        {
            MatchStatusText = "Matches config";
            MatchStatusIcon = "\uE73E"; // Checkmark
        }
        else
        {
            MatchStatusText = "Override active";
            MatchStatusIcon = "\uE7BA"; // Warning
        }

        // Update current defaults display
        UpdateCurrentDefaults();

        // Update auto-switch enabled state
        AutoSwitchEnabled = _autoSwitchService.IsEnabled;
    }

    private void RefreshDevices()
    {
        _isRefreshingDevices = true;
        try
        {
            var playbackDevices = _audioDeviceService.GetPlaybackDevices();
            var captureDevices = _audioDeviceService.GetCaptureDevices();
            var settings = _settingsService.Settings;

            PlaybackDevices.Clear();
            foreach (var device in playbackDevices)
            {
                PlaybackDevices.Add(device);
            }

            CaptureDevices.Clear();
            foreach (var device in captureDevices)
            {
                CaptureDevices.Add(device);
            }

            // Restore selected devices using smart matching (each device matched independently)
            SelectedWirelessSpeaker = FindDevice(PlaybackDevices,
                settings.WirelessDeviceId, settings.WirelessDeviceName);

            SelectedWiredSpeaker = FindDevice(PlaybackDevices,
                settings.WiredDeviceId, settings.WiredDeviceName);

            SelectedWirelessMicrophone = FindDevice(CaptureDevices,
                settings.WirelessMicrophoneId, settings.WirelessMicrophoneName);

            SelectedWiredMicrophone = FindDevice(CaptureDevices,
                settings.WiredMicrophoneId, settings.WiredMicrophoneName);

            // Update current Windows defaults display
            UpdateCurrentDefaults();
        }
        finally
        {
            _isRefreshingDevices = false;
        }
    }

    private void UpdateCurrentDefaults()
    {
        var defaultSpeaker = _audioDeviceService.GetDefaultDevice();
        var defaultMic = _audioDeviceService.GetDefaultCaptureDevice();

        CurrentDefaultSpeaker = defaultSpeaker?.Name ?? "None";
        CurrentDefaultMicrophone = defaultMic?.Name ?? "None";
    }

    private static AudioDevice? FindDevice(IEnumerable<AudioDevice> devices, string? savedId, string? savedName)
    {
        Debug.WriteLine($"[FindDevice] Looking for savedId={savedId ?? "(null)"}, savedName={savedName ?? "(null)"}");

        if (string.IsNullOrEmpty(savedId) && string.IsNullOrEmpty(savedName))
        {
            Debug.WriteLine("[FindDevice] No saved ID or name, returning null");
            return null;
        }

        var deviceList = devices.ToList();
        Debug.WriteLine($"[FindDevice] Available devices ({deviceList.Count}):");
        foreach (var d in deviceList)
            Debug.WriteLine($"[FindDevice]   - {d.Name} | {d.Id}");

        // Strategy 1: Exact ID match (fastest, most reliable)
        if (!string.IsNullOrEmpty(savedId))
        {
            var exactMatch = deviceList.FirstOrDefault(d => d.Id == savedId);
            if (exactMatch != null)
            {
                Debug.WriteLine($"[FindDevice] Strategy 1 SUCCESS: Exact ID match");
                return exactMatch;
            }
            Debug.WriteLine("[FindDevice] Strategy 1 FAILED: No exact ID match");
        }

        // Strategy 2: GUID portion match (handles path changes)
        // Device IDs look like: \\?\SWD#MMDEVAPI#{0.0.0.00000000}.{GUID}#...
        if (!string.IsNullOrEmpty(savedId))
        {
            var savedGuid = ExtractDeviceGuid(savedId);
            Debug.WriteLine($"[FindDevice] Strategy 2: Extracted GUID = {savedGuid ?? "(null)"}");

            if (!string.IsNullOrEmpty(savedGuid))
            {
                var guidMatch = deviceList.FirstOrDefault(d =>
                    ExtractDeviceGuid(d.Id) == savedGuid);
                if (guidMatch != null)
                {
                    Debug.WriteLine($"[FindDevice] Strategy 2 SUCCESS: GUID match -> {guidMatch.Name}");
                    return guidMatch;
                }
                Debug.WriteLine("[FindDevice] Strategy 2 FAILED: No GUID match");
            }
        }

        // Strategy 3: Name match (fallback for completely changed IDs)
        if (!string.IsNullOrEmpty(savedName))
        {
            var nameMatch = deviceList.FirstOrDefault(d =>
                d.Name.Equals(savedName, StringComparison.OrdinalIgnoreCase));
            if (nameMatch != null)
            {
                Debug.WriteLine($"[FindDevice] Strategy 3 SUCCESS: Name match");
                return nameMatch;
            }
            Debug.WriteLine($"[FindDevice] Strategy 3 FAILED: No name match for '{savedName}'");
        }

        Debug.WriteLine("[FindDevice] All strategies failed, returning null");
        return null;
    }

    private static string? ExtractDeviceGuid(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return null;

        // Extract GUID like {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}
        var match = Regex.Match(
            deviceId,
            @"\{[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}\}",
            RegexOptions.IgnoreCase);

        return match.Success ? match.Value : null;
    }

    partial void OnSelectedWirelessSpeakerChanged(AudioDevice? value)
    {
        if (_isRefreshingDevices) return;
        _settingsService.Settings.WirelessDeviceId = value?.Id;
        _settingsService.Settings.WirelessDeviceName = value?.Name;
        _ = _settingsService.SaveAsync();
    }

    partial void OnSelectedWiredSpeakerChanged(AudioDevice? value)
    {
        if (_isRefreshingDevices) return;
        _settingsService.Settings.WiredDeviceId = value?.Id;
        _settingsService.Settings.WiredDeviceName = value?.Name;
        _ = _settingsService.SaveAsync();
    }

    partial void OnSelectedWirelessMicrophoneChanged(AudioDevice? value)
    {
        if (_isRefreshingDevices) return;
        _settingsService.Settings.WirelessMicrophoneId = value?.Id;
        _settingsService.Settings.WirelessMicrophoneName = value?.Name;
        _ = _settingsService.SaveAsync();
    }

    partial void OnSelectedWiredMicrophoneChanged(AudioDevice? value)
    {
        if (_isRefreshingDevices) return;
        _settingsService.Settings.WiredMicrophoneId = value?.Id;
        _settingsService.Settings.WiredMicrophoneName = value?.Name;
        _ = _settingsService.SaveAsync();
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        _settingsService.Settings.StartMinimized = value;
        _ = _settingsService.SaveAsync();
    }

    partial void OnLaunchOnStartupChanged(bool value)
    {
        _settingsService.Settings.LaunchOnStartup = value;
        _ = _settingsService.SaveAsync();

        // Update Windows startup registration
        if (value)
        {
            _startupService.EnableStartup();
        }
        else
        {
            _startupService.DisableStartup();
        }
    }

    [RelayCommand]
    private void SyncToCurrentOutput()
    {
        var headsetState = _headsetStateService.CurrentState;
        bool isWireless = headsetState == HeadsetConnectionState.Online;

        AudioDevice? targetSpeaker = isWireless ? SelectedWirelessSpeaker : SelectedWiredSpeaker;
        AudioDevice? targetMic = isWireless ? SelectedWirelessMicrophone : SelectedWiredMicrophone;

        var results = new List<string>();
        bool anySuccess = false;

        // Apply speaker setting
        if (targetSpeaker != null)
        {
            if (_audioDeviceService.SetDefaultDevice(targetSpeaker.Id))
            {
                results.Add($"Speaker: {targetSpeaker.Name}");
                anySuccess = true;
            }
            else
            {
                results.Add("Speaker: failed");
            }
        }

        // Apply microphone setting
        if (targetMic != null)
        {
            if (_audioDeviceService.SetDefaultCaptureDevice(targetMic.Id))
            {
                results.Add($"Mic: {targetMic.Name}");
                anySuccess = true;
            }
            else
            {
                results.Add("Mic: failed");
            }
        }

        if (anySuccess)
        {
            UpdateCurrentDefaults();
            UpdateOverrideState();
        }
    }
}
