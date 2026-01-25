using System.Diagnostics;
using GAutoSwitch.Core.Interfaces;
using GAutoSwitch.Core.Models;
using GAutoSwitch.Hardware.Audio;
using Windows.Devices.Enumeration;

namespace GAutoSwitch.UI.Services;

/// <summary>
/// Implementation for audio device enumeration using Windows.Devices.Enumeration.
/// Will be enhanced with COM interop implementation in Stage 4.
/// </summary>
public sealed class AudioDeviceService : IAudioDeviceService, IDisposable
{
    // AudioRender GUID for playback devices
    private const string PlaybackSelector = "System.Devices.InterfaceClassGuid:=\"{E6327CAD-DCEC-4949-AE8A-991E976A79D2}\"";
    // AudioCapture GUID for capture devices (microphones)
    private const string CaptureSelector = "System.Devices.InterfaceClassGuid:=\"{2EEF81BE-33FA-4800-9670-1CD474972C3F}\"";

    private List<AudioDevice> _playbackDevices = [];
    private List<AudioDevice> _captureDevices = [];
    private DeviceWatcher? _playbackWatcher;
    private DeviceWatcher? _captureWatcher;
    private bool _disposed;

    public event EventHandler? DevicesChanged;

    public AudioDeviceService()
    {
        RefreshDevices();
        SetupDeviceWatchers();
    }

    public IReadOnlyList<AudioDevice> GetPlaybackDevices() => _playbackDevices.AsReadOnly();

    public IReadOnlyList<AudioDevice> GetCaptureDevices() => _captureDevices.AsReadOnly();

    public AudioDevice? GetDeviceById(string deviceId) =>
        _playbackDevices.FirstOrDefault(d => d.Id == deviceId) ??
        _captureDevices.FirstOrDefault(d => d.Id == deviceId);

    public AudioDevice? GetDefaultDevice() =>
        _playbackDevices.FirstOrDefault(d => d.IsDefault);

    public AudioDevice? GetDefaultCaptureDevice() =>
        _captureDevices.FirstOrDefault(d => d.IsDefault);

    public bool SetDefaultDevice(string deviceId)
    {
        try
        {
            var switcher = new AudioSwitcher();
            if (!switcher.IsSupported)
            {
                Debug.WriteLine("AudioSwitcher: COM interfaces not supported on this system");
                return false;
            }

            bool success = switcher.SetDefaultDevice(deviceId);
            if (success)
            {
                int migrated = switcher.MigrateActiveSessions(deviceId);
                Debug.WriteLine($"AudioSwitcher: Migrated {migrated} active session(s)");
                RefreshDevices();
            }

            return success;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SetDefaultDevice failed: {ex.Message}");
            return false;
        }
    }

    public bool SetDefaultCaptureDevice(string deviceId)
    {
        try
        {
            var switcher = new AudioSwitcher();
            if (!switcher.IsSupported)
            {
                Debug.WriteLine("AudioSwitcher: COM interfaces not supported on this system");
                return false;
            }

            bool success = switcher.SetDefaultDevice(deviceId, EDataFlow.Capture);
            if (success)
            {
                int migrated = switcher.MigrateActiveSessions(deviceId, EDataFlow.Capture);
                Debug.WriteLine($"AudioSwitcher: Migrated {migrated} active capture session(s)");
                RefreshDevices();
            }

            return success;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SetDefaultCaptureDevice failed: {ex.Message}");
            return false;
        }
    }

    private void SetupDeviceWatchers()
    {
        try
        {
            _playbackWatcher = DeviceInformation.CreateWatcher(PlaybackSelector);
            _playbackWatcher.Added += OnDeviceChanged;
            _playbackWatcher.Removed += OnDeviceChanged;
            _playbackWatcher.Updated += OnDeviceChanged;
            _playbackWatcher.Start();

            _captureWatcher = DeviceInformation.CreateWatcher(CaptureSelector);
            _captureWatcher.Added += OnDeviceChanged;
            _captureWatcher.Removed += OnDeviceChanged;
            _captureWatcher.Updated += OnDeviceChanged;
            _captureWatcher.Start();
        }
        catch
        {
            // If watchers fail to initialize, fall back to manual refresh
        }
    }

    private void OnDeviceChanged(DeviceWatcher sender, object args)
    {
        RaiseDevicesChanged();
    }

    private void RefreshDevices()
    {
        try
        {
            // Enumerate to local variables first
            var playbackDevices = EnumerateDevices(PlaybackSelector, AudioDeviceType.Playback);
            var captureDevices = EnumerateDevices(CaptureSelector, AudioDeviceType.Capture);

            Debug.WriteLine($"[AudioDeviceService] Enumerated {playbackDevices.Count} playback, {captureDevices.Count} capture devices");

            // Mark defaults BEFORE assigning to instance fields
            MarkDefaultDevice(playbackDevices, EDataFlow.Render);
            MarkDefaultDevice(captureDevices, EDataFlow.Capture);

            // Atomically update instance fields after defaults are marked
            _playbackDevices = playbackDevices;
            _captureDevices = captureDevices;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioDeviceService] RefreshDevices failed: {ex.Message}");
            _playbackDevices = [];
            _captureDevices = [];
        }
    }

    private static void MarkDefaultDevice(List<AudioDevice> devices, EDataFlow flow)
    {
        // Reset all to non-default first
        foreach (var device in devices)
            device.IsDefault = false;

        // Get the actual default from Windows
        string? defaultId = AudioSessionInterop.GetDefaultDeviceId(flow);
        Debug.WriteLine($"[AudioDeviceService] Default {flow} device ID: {defaultId ?? "(null)"}");

        if (string.IsNullOrEmpty(defaultId))
            return;

        // Find matching device - the WinRT ID contains the MMDevice ID
        var defaultDevice = devices.FirstOrDefault(d =>
            d.Id.Contains(defaultId, StringComparison.OrdinalIgnoreCase));

        if (defaultDevice != null)
        {
            defaultDevice.IsDefault = true;
            Debug.WriteLine($"[AudioDeviceService] Marked default: {defaultDevice.Name}");
        }
        else
        {
            Debug.WriteLine($"[AudioDeviceService] WARNING: Could not find device matching default ID");
        }
    }

    private static List<AudioDevice> EnumerateDevices(string selector, AudioDeviceType deviceType)
    {
        var devices = new List<AudioDevice>();

        try
        {
            var task = DeviceInformation.FindAllAsync(selector).AsTask();
            task.Wait();
            var deviceInfos = task.Result;

            foreach (var deviceInfo in deviceInfos)
            {
                devices.Add(new AudioDevice
                {
                    Id = deviceInfo.Id,
                    Name = deviceInfo.Name,
                    IsActive = deviceInfo.IsEnabled,
                    IsDefault = false, // Will be determined by CoreAudio later
                    DeviceType = deviceType
                });
            }
        }
        catch
        {
            // Fallback: return empty list
        }

        return devices;
    }

    public void RaiseDevicesChanged()
    {
        RefreshDevices();
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_playbackWatcher != null)
        {
            _playbackWatcher.Added -= OnDeviceChanged;
            _playbackWatcher.Removed -= OnDeviceChanged;
            _playbackWatcher.Updated -= OnDeviceChanged;
            _playbackWatcher.Stop();
        }

        if (_captureWatcher != null)
        {
            _captureWatcher.Added -= OnDeviceChanged;
            _captureWatcher.Removed -= OnDeviceChanged;
            _captureWatcher.Updated -= OnDeviceChanged;
            _captureWatcher.Stop();
        }
    }
}
