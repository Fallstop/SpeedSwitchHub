using System.Collections.ObjectModel;
using GAutoSwitch.Core.Interfaces;
using GAutoSwitch.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace GAutoSwitch.UI.ViewModels;

/// <summary>
/// ViewModel for the Audio Proxy settings page.
/// </summary>
public partial class AudioProxyViewModel : ObservableObject, IDisposable
{
    private readonly IAudioProxyService _proxyService;
    private readonly IAudioDeviceService _audioDeviceService;
    private readonly ISettingsService _settingsService;
    private readonly IHeadsetStateService? _headsetStateService;
    private readonly DispatcherQueue? _dispatcherQueue;
    private bool _disposed;
    private bool _isInitializing;

    // Status colors
    private static readonly Color RunningColor = Color.FromArgb(255, 15, 123, 15);     // Green
    private static readonly Color StoppedColor = Color.FromArgb(255, 157, 157, 157);   // Gray
    private static readonly Color WarningColor = Color.FromArgb(255, 157, 93, 0);      // Orange
    private static readonly Color ErrorColor = Color.FromArgb(255, 196, 43, 28);       // Red

    /// <summary>
    /// Collection of available virtual audio devices (for speaker proxy).
    /// </summary>
    public ObservableCollection<AudioDevice> VirtualDevices { get; } = [];

    /// <summary>
    /// Collection of available playback devices for output.
    /// </summary>
    public ObservableCollection<AudioDevice> PlaybackDevices { get; } = [];

    /// <summary>
    /// Collection of available microphone devices (for mic proxy input).
    /// </summary>
    public ObservableCollection<AudioDevice> MicrophoneDevices { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _useAudioProxy;

    [ObservableProperty]
    private int _proxyBufferMs = 10;

    [ObservableProperty]
    private AudioDevice? _selectedVirtualDevice;

    [ObservableProperty]
    private bool _isProxyRunning;

    [ObservableProperty]
    private bool _isVBCableInstalled;

    [ObservableProperty]
    private string _currentOutputDevice = "None";

    [ObservableProperty]
    private string _statusTitle = "Stopped";

    [ObservableProperty]
    private string _statusDescription = "Audio proxy is not running";

    [ObservableProperty]
    private string _statusText = "Stopped";

    [ObservableProperty]
    private string _statusIcon = "\uE769"; // Speaker icon

    [ObservableProperty]
    private SolidColorBrush _statusColor = new(StoppedColor);

    [ObservableProperty]
    private string _bufferDisplayText = "10 ms";

    [ObservableProperty]
    private bool _showVBCableWarning;

    [ObservableProperty]
    private bool _canEnableProxy;

    [ObservableProperty]
    private bool _canStartProxy;

    [ObservableProperty]
    private bool _canStopProxy;

    [ObservableProperty]
    private string? _errorMessage;

    // ========================================
    // Microphone Proxy Properties
    // ========================================

    [ObservableProperty]
    private bool _useMicProxy;

    [ObservableProperty]
    private bool _micProxyAutoStart = true;

    [ObservableProperty]
    private AudioDevice? _selectedMicrophoneDevice;

    [ObservableProperty]
    private bool _isMicProxyRunning;

    [ObservableProperty]
    private bool _isVBCableInputInstalled;

    [ObservableProperty]
    private bool _showVBCableInputWarning;

    [ObservableProperty]
    private bool _canEnableMicProxy;

    [ObservableProperty]
    private string _micStatusTitle = "Stopped";

    [ObservableProperty]
    private string _micStatusDescription = "Microphone proxy is not running";

    [ObservableProperty]
    private string _micStatusText = "Stopped";

    [ObservableProperty]
    private string _micStatusIcon = "\uE720"; // Microphone icon

    [ObservableProperty]
    private SolidColorBrush _micStatusColor = new(StoppedColor);

    [ObservableProperty]
    private string? _micErrorMessage;

    public AudioProxyViewModel(
        IAudioProxyService proxyService,
        IAudioDeviceService audioDeviceService,
        ISettingsService settingsService,
        IHeadsetStateService? headsetStateService = null)
    {
        _proxyService = proxyService;
        _audioDeviceService = audioDeviceService;
        _settingsService = settingsService;
        _headsetStateService = headsetStateService;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _proxyService.StatusChanged += OnProxyStatusChanged;
        _proxyService.MicStatusChanged += OnMicStatusChanged;
    }

    public async Task InitializeAsync()
    {
        IsLoading = true;
        _isInitializing = true;

        try
        {
            await _settingsService.LoadAsync();
            LoadSettingsToViewModel();
            RefreshDevices();
            RefreshVBCableStatus();
            RefreshVBCableInputStatus();
            UpdateStatusDisplay();
            UpdateMicStatusDisplay();
            UpdateCanProperties();
        }
        finally
        {
            _isInitializing = false;
            IsLoading = false;
        }
    }

    private void LoadSettingsToViewModel()
    {
        var settings = _settingsService.Settings;
        UseAudioProxy = settings.UseAudioProxy;
        ProxyBufferMs = settings.ProxyBufferMs;
        UseMicProxy = settings.UseMicProxy;
        MicProxyAutoStart = settings.MicProxyAutoStart;
        UpdateBufferDisplayText();
    }

    private void RefreshDevices()
    {
        var playbackDevices = _audioDeviceService.GetPlaybackDevices();
        var recordingDevices = _audioDeviceService.GetCaptureDevices();

        PlaybackDevices.Clear();
        VirtualDevices.Clear();
        MicrophoneDevices.Clear();

        foreach (var device in playbackDevices)
        {
            PlaybackDevices.Add(device);

            // Check if this is a virtual audio device (VB-Cable, etc.)
            if (device.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
                device.Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
                device.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
            {
                VirtualDevices.Add(device);
            }
        }

        // Add microphones (recording devices)
        foreach (var device in recordingDevices)
        {
            // Exclude virtual devices from mic list - we want physical mics only
            bool isVirtual = device.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
                             device.Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
                             device.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                             device.Name.Contains("Stereo Mix", StringComparison.OrdinalIgnoreCase);
            if (!isVirtual)
            {
                MicrophoneDevices.Add(device);
            }
        }

        // Restore selected virtual device from settings
        var settings = _settingsService.Settings;
        if (!string.IsNullOrEmpty(settings.ProxyInputDeviceId))
        {
            SelectedVirtualDevice = VirtualDevices.FirstOrDefault(d => d.Id == settings.ProxyInputDeviceId);
        }

        // Restore selected microphone from settings
        if (!string.IsNullOrEmpty(settings.MicProxyInputDeviceId))
        {
            SelectedMicrophoneDevice = MicrophoneDevices.FirstOrDefault(d => d.Id == settings.MicProxyInputDeviceId);
        }
    }

    [RelayCommand]
    private void RefreshVBCable()
    {
        RefreshVBCableStatus();
        RefreshVBCableInputStatus();
        RefreshDevices();
    }

    private void RefreshVBCableStatus()
    {
        _proxyService.RefreshVBCableStatus();
        IsVBCableInstalled = _proxyService.IsVBCableInstalled;
        ShowVBCableWarning = !IsVBCableInstalled;
        UpdateCanProperties();
    }

    private void RefreshVBCableInputStatus()
    {
        _proxyService.RefreshVBCableInputStatus();
        IsVBCableInputInstalled = _proxyService.IsVBCableInputInstalled;
        ShowVBCableInputWarning = !IsVBCableInputInstalled && UseMicProxy;
        UpdateCanProperties();
    }

    [RelayCommand]
    private async Task StartProxyAsync()
    {
        if (!CanStartProxy)
            return;

        ErrorMessage = null;
        MicErrorMessage = null;

        // Get the configured wireless/wired output device based on headset state
        var speakerOutputDeviceId = GetCurrentSpeakerDeviceId();
        if (string.IsNullOrEmpty(speakerOutputDeviceId))
        {
            ErrorMessage = "No speaker output device configured. Please configure wireless/wired devices in settings.";
            return;
        }

        // Determine if we should start mic proxy too
        string? micInputDeviceId = null;
        if (UseMicProxy && MicProxyAutoStart && IsVBCableInputInstalled)
        {
            micInputDeviceId = GetCurrentMicrophoneId();
        }

        var success = await _proxyService.StartAsync(speakerOutputDeviceId, micInputDeviceId);
        if (!success)
        {
            var status = _proxyService.GetStatus();
            ErrorMessage = status.ErrorMessage ?? "Failed to start audio proxy";
        }
    }

    /// <summary>
    /// Gets the appropriate speaker device ID based on the current headset state.
    /// </summary>
    private string? GetCurrentSpeakerDeviceId()
    {
        var settings = _settingsService.Settings;

        if (_headsetStateService != null)
        {
            var headsetState = _headsetStateService.CurrentState;

            // Use wireless speaker when headset is online, wired when offline
            if (headsetState == HeadsetConnectionState.Online && !string.IsNullOrEmpty(settings.WirelessDeviceId))
            {
                return settings.WirelessDeviceId;
            }
            else if (!string.IsNullOrEmpty(settings.WiredDeviceId))
            {
                return settings.WiredDeviceId;
            }
        }

        // Fallback to wired device if available
        if (!string.IsNullOrEmpty(settings.WiredDeviceId))
        {
            return settings.WiredDeviceId;
        }

        // Last resort: use default device
        var outputDevice = _audioDeviceService.GetDefaultDevice();
        return outputDevice?.Id;
    }

    /// <summary>
    /// Gets the appropriate microphone ID based on the current headset state.
    /// Falls back to the selected microphone if no configured wireless/wired mic is available.
    /// </summary>
    private string? GetCurrentMicrophoneId()
    {
        var settings = _settingsService.Settings;

        // Check if we have headset state service and configured microphones
        if (_headsetStateService != null)
        {
            var headsetState = _headsetStateService.CurrentState;

            // Use wireless mic when headset is online, wired mic when offline
            if (headsetState == HeadsetConnectionState.Online && !string.IsNullOrEmpty(settings.WirelessMicrophoneId))
            {
                return settings.WirelessMicrophoneId;
            }
            else if (headsetState == HeadsetConnectionState.Offline && !string.IsNullOrEmpty(settings.WiredMicrophoneId))
            {
                return settings.WiredMicrophoneId;
            }
        }

        // Fallback to the manually selected microphone
        return SelectedMicrophoneDevice?.Id;
    }

    [RelayCommand]
    private async Task StopProxyAsync()
    {
        if (!CanStopProxy)
            return;

        ErrorMessage = null;
        MicErrorMessage = null;
        await _proxyService.StopAsync();
    }

    [RelayCommand]
    private async Task ToggleMicProxyAsync()
    {
        if (!IsProxyRunning)
        {
            MicErrorMessage = "Start the audio proxy first";
            return;
        }

        MicErrorMessage = null;
        var newState = !IsMicProxyRunning;

        var success = await _proxyService.SetMicProxyEnabledAsync(newState);
        if (!success)
        {
            MicErrorMessage = $"Failed to {(newState ? "enable" : "disable")} mic proxy";
        }
    }

    [RelayCommand]
    private async Task ChangeMicInputDeviceAsync()
    {
        if (!IsProxyRunning || SelectedMicrophoneDevice == null)
            return;

        MicErrorMessage = null;
        var success = await _proxyService.SetMicInputDeviceAsync(SelectedMicrophoneDevice.Id);
        if (!success)
        {
            MicErrorMessage = "Failed to change microphone input device";
        }
    }

    private void OnProxyStatusChanged(object? sender, ProxyStatusEventArgs e)
    {
        // Marshal to UI thread
        if (_dispatcherQueue != null)
        {
            _dispatcherQueue.TryEnqueue(() => HandleStatusChange(e.Status));
        }
        else
        {
            HandleStatusChange(e.Status);
        }
    }

    private void OnMicStatusChanged(object? sender, MicProxyStatusEventArgs e)
    {
        // Marshal to UI thread
        if (_dispatcherQueue != null)
        {
            _dispatcherQueue.TryEnqueue(() => HandleMicStatusChange(e));
        }
        else
        {
            HandleMicStatusChange(e);
        }
    }

    private void HandleStatusChange(ProxyStatus status)
    {
        IsProxyRunning = status.IsRunning;

        if (!string.IsNullOrEmpty(status.OutputDeviceId))
        {
            // Try to find the device name
            var device = PlaybackDevices.FirstOrDefault(d => d.Id == status.OutputDeviceId);
            CurrentOutputDevice = device?.Name ?? status.OutputDeviceId;
        }
        else
        {
            CurrentOutputDevice = "None";
        }

        if (!string.IsNullOrEmpty(status.ErrorMessage))
        {
            ErrorMessage = status.ErrorMessage;
        }

        // Update mic status from proxy status
        if (status.MicEnabled.HasValue)
        {
            IsMicProxyRunning = status.MicEnabled.Value;
        }

        UpdateStatusDisplay();
        UpdateMicStatusDisplay();
        UpdateCanProperties();
    }

    private void HandleMicStatusChange(MicProxyStatusEventArgs e)
    {
        IsMicProxyRunning = e.IsEnabled;

        if (!string.IsNullOrEmpty(e.ErrorMessage))
        {
            MicErrorMessage = e.ErrorMessage;
        }

        UpdateMicStatusDisplay();
        UpdateCanProperties();
    }

    private void UpdateStatusDisplay()
    {
        var status = _proxyService.GetStatus();

        // Update IsProxyRunning from the actual status (important for initialization)
        IsProxyRunning = status.IsRunning;
        IsMicProxyRunning = status.MicEnabled ?? false;

        // Update current output device from status
        if (!string.IsNullOrEmpty(status.OutputDeviceId))
        {
            var device = PlaybackDevices.FirstOrDefault(d => d.Id == status.OutputDeviceId);
            CurrentOutputDevice = device?.Name ?? status.OutputDeviceId;
        }

        if (status.IsRunning)
        {
            StatusTitle = "Running";
            StatusDescription = $"Forwarding audio to {CurrentOutputDevice}";
            StatusText = "Running";
            StatusIcon = "\uE768"; // Volume icon
            StatusColor = new SolidColorBrush(RunningColor);
        }
        else if (!string.IsNullOrEmpty(status.ErrorMessage))
        {
            StatusTitle = "Error";
            StatusDescription = status.ErrorMessage;
            StatusText = "Error";
            StatusIcon = "\uE783"; // Error icon
            StatusColor = new SolidColorBrush(ErrorColor);
        }
        else if (!IsVBCableInstalled)
        {
            StatusTitle = "VB-Cable Required";
            StatusDescription = "Install VB-Cable to use the audio proxy";
            StatusText = "Not Available";
            StatusIcon = "\uE7BA"; // Warning icon
            StatusColor = new SolidColorBrush(WarningColor);
        }
        else
        {
            StatusTitle = "Stopped";
            StatusDescription = "Audio proxy is not running";
            StatusText = "Stopped";
            StatusIcon = "\uE769"; // Mute icon
            StatusColor = new SolidColorBrush(StoppedColor);
        }
    }

    private void UpdateCanProperties()
    {
        CanEnableProxy = IsVBCableInstalled;
        CanStartProxy = IsVBCableInstalled && !IsProxyRunning;
        CanStopProxy = IsProxyRunning;
        CanEnableMicProxy = IsVBCableInputInstalled && IsProxyRunning;
    }

    private void UpdateMicStatusDisplay()
    {
        if (!IsProxyRunning)
        {
            MicStatusTitle = "Stopped";
            MicStatusDescription = "Start the audio proxy to enable mic proxy";
            MicStatusText = "Stopped";
            MicStatusIcon = "\uE720"; // Microphone icon
            MicStatusColor = new SolidColorBrush(StoppedColor);
        }
        else if (!IsVBCableInputInstalled)
        {
            MicStatusTitle = "VB-Cable Required";
            MicStatusDescription = "A second virtual audio device is needed for mic proxy";
            MicStatusText = "Not Available";
            MicStatusIcon = "\uE7BA"; // Warning icon
            MicStatusColor = new SolidColorBrush(WarningColor);
        }
        else if (IsMicProxyRunning)
        {
            var micName = SelectedMicrophoneDevice?.Name ?? "Unknown";
            MicStatusTitle = "Running";
            MicStatusDescription = $"Forwarding {micName} to virtual device";
            MicStatusText = "Running";
            MicStatusIcon = "\uE720"; // Microphone icon
            MicStatusColor = new SolidColorBrush(RunningColor);
        }
        else if (!string.IsNullOrEmpty(MicErrorMessage))
        {
            MicStatusTitle = "Error";
            MicStatusDescription = MicErrorMessage;
            MicStatusText = "Error";
            MicStatusIcon = "\uE783"; // Error icon
            MicStatusColor = new SolidColorBrush(ErrorColor);
        }
        else
        {
            MicStatusTitle = "Disabled";
            MicStatusDescription = "Mic proxy is available but disabled";
            MicStatusText = "Disabled";
            MicStatusIcon = "\uE720"; // Microphone icon
            MicStatusColor = new SolidColorBrush(StoppedColor);
        }
    }

    private void UpdateBufferDisplayText()
    {
        BufferDisplayText = $"{ProxyBufferMs} ms";
    }

    partial void OnUseAudioProxyChanged(bool value)
    {
        if (_isInitializing) return;

        _settingsService.Settings.UseAudioProxy = value;
        _ = _settingsService.SaveAsync();
    }

    partial void OnProxyBufferMsChanged(int value)
    {
        UpdateBufferDisplayText();

        if (_isInitializing) return;

        _settingsService.Settings.ProxyBufferMs = value;
        _ = _settingsService.SaveAsync();
    }

    partial void OnSelectedVirtualDeviceChanged(AudioDevice? value)
    {
        if (_isInitializing) return;

        _settingsService.Settings.ProxyInputDeviceId = value?.Id;
        _ = _settingsService.SaveAsync();
    }

    partial void OnUseMicProxyChanged(bool value)
    {
        if (_isInitializing) return;

        _settingsService.Settings.UseMicProxy = value;
        _ = _settingsService.SaveAsync();

        // Update warning visibility
        ShowVBCableInputWarning = !IsVBCableInputInstalled && value;
        UpdateCanProperties();
    }

    partial void OnMicProxyAutoStartChanged(bool value)
    {
        if (_isInitializing) return;

        _settingsService.Settings.MicProxyAutoStart = value;
        _ = _settingsService.SaveAsync();
    }

    partial void OnSelectedMicrophoneDeviceChanged(AudioDevice? value)
    {
        if (_isInitializing) return;

        _settingsService.Settings.MicProxyInputDeviceId = value?.Id;
        _ = _settingsService.SaveAsync();

        // If proxy is running, update mic input device
        if (IsProxyRunning && IsMicProxyRunning && value != null)
        {
            _ = ChangeMicInputDeviceAsync();
        }
    }

    public void Cleanup()
    {
        _proxyService.StatusChanged -= OnProxyStatusChanged;
        _proxyService.MicStatusChanged -= OnMicStatusChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Cleanup();
    }
}
