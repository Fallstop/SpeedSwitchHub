using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GAutoSwitch.Core.Interfaces;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace GAutoSwitch.Hardware.Audio;

/// <summary>
/// Service for managing the low-latency audio proxy process.
/// </summary>
public sealed partial class AudioProxyService : IAudioProxyService
{
    private const string PipeName = "GAutoSwitchAudioProxy";
    private const string ProxyExecutableName = "audio-proxy.exe";
    private const string VBCableDeviceNamePattern = "CABLE";

    private Process? _proxyProcess;
    private string? _currentOutputDeviceId;
    private string? _currentMicInputDeviceId;
    private string? _vbCableRenderDeviceId;   // "CABLE Input" - render device for apps to play to
    private string? _vbCableCaptureDeviceId;  // "CABLE Output" - capture device for proxy to capture from
    private string? _vbCableInputDeviceId;
    private bool _isMicProxyEnabled;
    private bool _isDisposed;
    private readonly object _lock = new();

    /// <inheritdoc />
    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _proxyProcess != null && !_proxyProcess.HasExited;
            }
        }
    }

    /// <inheritdoc />
    public bool IsVBCableInstalled => _vbCableCaptureDeviceId != null;

    /// <inheritdoc />
    public string? VBCableDeviceId => _vbCableRenderDeviceId;

    /// <inheritdoc />
    public bool IsVBCableInputInstalled => _vbCableInputDeviceId != null;

    /// <inheritdoc />
    public string? VBCableInputDeviceId => _vbCableInputDeviceId;

    /// <inheritdoc />
    public bool IsMicProxyEnabled
    {
        get
        {
            lock (_lock)
            {
                return _isMicProxyEnabled;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<ProxyStatusEventArgs>? StatusChanged;

    /// <inheritdoc />
    public event EventHandler<MicProxyStatusEventArgs>? MicStatusChanged;

    public AudioProxyService()
    {
        RefreshVBCableStatus();
        RefreshVBCableInputStatus();
    }

    /// <inheritdoc />
    public void RefreshVBCableStatus()
    {
        _vbCableRenderDeviceId = DetectVBCableRenderDevice();
        _vbCableCaptureDeviceId = DetectVBCableCaptureDevice();
        Debug.WriteLine($"[AudioProxyService] VB-Cable Render (CABLE Input) detected: {_vbCableRenderDeviceId ?? "(not found)"}");
        Debug.WriteLine($"[AudioProxyService] VB-Cable Capture (CABLE Output) detected: {_vbCableCaptureDeviceId ?? "(not found)"}");
    }

    /// <inheritdoc />
    public void RefreshVBCableInputStatus()
    {
        _vbCableInputDeviceId = DetectVBCableInputDevice();
        Debug.WriteLine($"[AudioProxyService] VB-Cable Input detected: {_vbCableInputDeviceId ?? "(not found)"}");
    }

    /// <inheritdoc />
    public Task<bool> StartAsync(string outputDeviceId)
    {
        return StartAsync(outputDeviceId, null);
    }

    /// <inheritdoc />
    public async Task<bool> StartAsync(string speakerOutputDeviceId, string? micInputDeviceId)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(AudioProxyService));

        if (string.IsNullOrEmpty(_vbCableCaptureDeviceId))
        {
            Debug.WriteLine("[AudioProxyService] Cannot start: VB-Cable capture device (CABLE Output) not detected");
            RaiseStatusChanged(new ProxyStatus
            {
                IsRunning = false,
                ErrorMessage = "VB-Cable capture device (CABLE Output) not detected. Please install VB-Cable from https://vb-audio.com/Cable/"
            });
            return false;
        }

        lock (_lock)
        {
            if (_proxyProcess != null && !_proxyProcess.HasExited)
            {
                Debug.WriteLine("[AudioProxyService] Proxy already running");
                // Raise status event so UI knows the current state
                RaiseStatusChanged(new ProxyStatus
                {
                    IsRunning = true,
                    OutputDeviceId = _currentOutputDeviceId,
                    ProcessId = _proxyProcess.Id,
                    MicEnabled = _isMicProxyEnabled,
                    MicInputDeviceId = _currentMicInputDeviceId
                });
                return true;
            }
        }

        var proxyPath = FindProxyExecutable();
        if (proxyPath == null)
        {
            Debug.WriteLine("[AudioProxyService] Cannot start: proxy executable not found");
            RaiseStatusChanged(new ProxyStatus
            {
                IsRunning = false,
                ErrorMessage = "Audio proxy executable not found. Please build the audio-proxy project."
            });
            return false;
        }

        try
        {
            // Speaker input: capture from "CABLE Output" (the capture device)
            var speakerInputId = ConvertToMMDeviceId(_vbCableCaptureDeviceId!);
            var speakerOutputId = ConvertToMMDeviceId(speakerOutputDeviceId);

            // Build arguments using the new CLI format
            var arguments = $"--speaker-in \"{speakerInputId}\" --speaker-out \"{speakerOutputId}\"";

            // Add mic proxy arguments if configured
            bool micProxyConfigured = !string.IsNullOrEmpty(micInputDeviceId) && !string.IsNullOrEmpty(_vbCableInputDeviceId);
            if (micProxyConfigured)
            {
                var micIn = ConvertToMMDeviceId(micInputDeviceId!);
                var micOut = ConvertToMMDeviceId(_vbCableInputDeviceId!);
                arguments += $" --mic-in \"{micIn}\" --mic-out \"{micOut}\"";
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = proxyPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            Debug.WriteLine($"[AudioProxyService] Starting proxy: {startInfo.FileName} {startInfo.Arguments}");

            var process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Debug.WriteLine($"[AudioProxy] {e.Data}");
            };
            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Debug.WriteLine($"[AudioProxy ERROR] {e.Data}");
            };
            process.EnableRaisingEvents = true;
            process.Exited += OnProxyExited;

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            lock (_lock)
            {
                _proxyProcess = process;
                _currentOutputDeviceId = speakerOutputDeviceId;
                _currentMicInputDeviceId = micInputDeviceId;
                _isMicProxyEnabled = micProxyConfigured;
            }

            // Wait a bit for the process to initialize
            await Task.Delay(500);

            if (process.HasExited)
            {
                Debug.WriteLine($"[AudioProxyService] Proxy exited immediately with code {process.ExitCode}");
                RaiseStatusChanged(new ProxyStatus
                {
                    IsRunning = false,
                    ErrorMessage = $"Proxy failed to start (exit code {process.ExitCode})"
                });
                return false;
            }

            Debug.WriteLine($"[AudioProxyService] Proxy started with PID {process.Id}");
            RaiseStatusChanged(new ProxyStatus
            {
                IsRunning = true,
                OutputDeviceId = speakerOutputDeviceId,
                ProcessId = process.Id,
                MicEnabled = micProxyConfigured,
                MicInputDeviceId = micInputDeviceId
            });

            if (micProxyConfigured)
            {
                RaiseMicStatusChanged(true, micInputDeviceId);
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioProxyService] Failed to start proxy: {ex.Message}");
            RaiseStatusChanged(new ProxyStatus
            {
                IsRunning = false,
                ErrorMessage = ex.Message
            });
            return false;
        }
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        Process? process;
        lock (_lock)
        {
            process = _proxyProcess;
            _proxyProcess = null;
        }

        if (process == null || process.HasExited)
        {
            RaiseStatusChanged(new ProxyStatus { IsRunning = false });
            return;
        }

        try
        {
            // Try to stop gracefully via IPC
            var response = await SendCommandAsync(new IpcCommand { Command = "Stop" });
            if (response?.Success == true)
            {
                // Wait for process to exit
                if (!process.WaitForExit(3000))
                {
                    Debug.WriteLine("[AudioProxyService] Proxy didn't exit gracefully, killing...");
                    process.Kill();
                }
            }
            else
            {
                process.Kill();
            }
        }
        catch
        {
            try
            {
                process.Kill();
            }
            catch
            {
                // Process may have already exited
            }
        }
        finally
        {
            process.Dispose();
            RaiseStatusChanged(new ProxyStatus { IsRunning = false });
        }

        Debug.WriteLine("[AudioProxyService] Proxy stopped");
    }

    /// <inheritdoc />
    public async Task<bool> SetOutputDeviceAsync(string deviceId)
    {
        if (!IsRunning)
        {
            Debug.WriteLine("[AudioProxyService] Cannot set output device: proxy not running");
            return false;
        }

        var mmDeviceId = ConvertToMMDeviceId(deviceId);
        var command = new IpcCommand
        {
            Command = "SetOutput",
            Data = new SetOutputData { DeviceId = mmDeviceId }
        };

        var response = await SendCommandAsync(command);
        if (response?.Success == true)
        {
            lock (_lock)
            {
                _currentOutputDeviceId = deviceId;
            }

            Debug.WriteLine($"[AudioProxyService] Output device changed to: {deviceId}");
            RaiseStatusChanged(new ProxyStatus
            {
                IsRunning = true,
                OutputDeviceId = deviceId,
                ProcessId = _proxyProcess?.Id,
                MicEnabled = _isMicProxyEnabled,
                MicInputDeviceId = _currentMicInputDeviceId
            });
            return true;
        }

        Debug.WriteLine($"[AudioProxyService] Failed to change output device: {response?.Message}");
        return false;
    }

    /// <inheritdoc />
    public async Task<bool> SetMicProxyEnabledAsync(bool enabled)
    {
        if (!IsRunning)
        {
            Debug.WriteLine("[AudioProxyService] Cannot set mic enabled: proxy not running");
            return false;
        }

        var command = new IpcCommand
        {
            Command = "EnableMic",
            Data = new EnableMicData { Enabled = enabled }
        };

        var response = await SendCommandAsync(command);
        if (response?.Success == true)
        {
            lock (_lock)
            {
                _isMicProxyEnabled = enabled;
            }

            Debug.WriteLine($"[AudioProxyService] Mic proxy enabled: {enabled}");
            RaiseMicStatusChanged(enabled, _currentMicInputDeviceId);
            return true;
        }

        Debug.WriteLine($"[AudioProxyService] Failed to set mic enabled: {response?.Message}");
        return false;
    }

    /// <inheritdoc />
    public async Task<bool> SetMicInputDeviceAsync(string deviceId)
    {
        if (!IsRunning)
        {
            Debug.WriteLine("[AudioProxyService] Cannot set mic input device: proxy not running");
            return false;
        }

        var mmDeviceId = ConvertToMMDeviceId(deviceId);
        var command = new IpcCommand
        {
            Command = "SetMicInput",
            Data = new SetMicInputData { DeviceId = mmDeviceId }
        };

        var response = await SendCommandAsync(command);
        if (response?.Success == true)
        {
            lock (_lock)
            {
                _currentMicInputDeviceId = deviceId;
            }

            Debug.WriteLine($"[AudioProxyService] Mic input device changed to: {deviceId}");
            RaiseMicStatusChanged(_isMicProxyEnabled, deviceId);
            return true;
        }

        Debug.WriteLine($"[AudioProxyService] Failed to change mic input device: {response?.Message}");
        return false;
    }

    /// <inheritdoc />
    public ProxyStatus GetStatus()
    {
        lock (_lock)
        {
            if (_proxyProcess == null || _proxyProcess.HasExited)
            {
                return new ProxyStatus { IsRunning = false };
            }

            return new ProxyStatus
            {
                IsRunning = true,
                OutputDeviceId = _currentOutputDeviceId,
                ProcessId = _proxyProcess.Id,
                MicEnabled = _isMicProxyEnabled,
                MicInputDeviceId = _currentMicInputDeviceId
            };
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        StopAsync().GetAwaiter().GetResult();
    }

    private void OnProxyExited(object? sender, EventArgs e)
    {
        Debug.WriteLine("[AudioProxyService] Proxy process exited");
        lock (_lock)
        {
            _proxyProcess?.Dispose();
            _proxyProcess = null;
        }
        RaiseStatusChanged(new ProxyStatus { IsRunning = false });
    }

    private async Task<IpcResponse?> SendCommandAsync(IpcCommand command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);

            await pipe.ConnectAsync(1000);

            // Send command
            var json = JsonSerializer.Serialize(command, IpcJsonContext.Default.IpcCommand);
            var bytes = Encoding.UTF8.GetBytes(json);
            await pipe.WriteAsync(bytes);
            await pipe.FlushAsync();

            // Read response
            var buffer = new byte[4096];
            var bytesRead = await pipe.ReadAsync(buffer);
            var responseJson = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            return JsonSerializer.Deserialize(responseJson, IpcJsonContext.Default.IpcResponse);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioProxyService] IPC error: {ex.Message}");
            return null;
        }
    }

    private string? FindProxyExecutable()
    {
        // Look in several locations relative to the app and source directories
        var locations = new List<string>
        {
            // Next to the app executable
            Path.Combine(AppContext.BaseDirectory, ProxyExecutableName),
            // In an audio-proxy subfolder
            Path.Combine(AppContext.BaseDirectory, "audio-proxy", ProxyExecutableName),
            // Relative to bin output (Debug/Release builds)
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "audio-proxy", "target", "release", ProxyExecutableName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "audio-proxy", "target", "release", ProxyExecutableName),
        };

        // Also check relative to the solution root (for development)
        var solutionRoot = FindSolutionRoot(AppContext.BaseDirectory);
        if (solutionRoot != null)
        {
            locations.Add(Path.Combine(solutionRoot, "src", "audio-proxy", "target", "release", ProxyExecutableName));
        }

        foreach (var path in locations)
        {
            var fullPath = Path.GetFullPath(path);
            Debug.WriteLine($"[AudioProxyService] Checking: {fullPath}");
            if (File.Exists(fullPath))
            {
                Debug.WriteLine($"[AudioProxyService] Found proxy at: {fullPath}");
                return fullPath;
            }
        }

        Debug.WriteLine("[AudioProxyService] Proxy executable not found in any location");
        return null;
    }

    private static string? FindSolutionRoot(string startPath)
    {
        var dir = new DirectoryInfo(startPath);
        while (dir != null)
        {
            // Look for .sln file or src folder with audio-proxy
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "audio-proxy")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Detects VB-Cable render device ("CABLE Input" - the playback endpoint apps send audio to).
    /// This is returned as VBCableDeviceId for apps to configure as their output device.
    /// </summary>
    private string? DetectVBCableRenderDevice()
    {
        try
        {
            // Use WinRT to enumerate audio RENDER (playback) devices and find VB-Cable
            // We look for "CABLE Input" which is the playback device apps send audio to
            var selector = MediaDevice.GetAudioRenderSelector();
            var devices = DeviceInformation.FindAllAsync(selector).GetAwaiter().GetResult();

            foreach (var device in devices)
            {
                if (device.Name.Contains(VBCableDeviceNamePattern, StringComparison.OrdinalIgnoreCase) ||
                    device.Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine($"[AudioProxyService] Found VB-Cable render (CABLE Input): {device.Name} ({device.Id})");
                    return device.Id;
                }
            }

            // Also check for other virtual audio devices that might work
            foreach (var device in devices)
            {
                if (device.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                    device.Name.Contains("Audio", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine($"[AudioProxyService] Found virtual playback device: {device.Name} ({device.Id})");
                    return device.Id;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioProxyService] Error detecting VB-Cable render device: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Detects VB-Cable capture device ("CABLE Output" - the recording endpoint to capture audio from).
    /// This is what the proxy captures from to get audio that apps send to "CABLE Input".
    /// </summary>
    private string? DetectVBCableCaptureDevice()
    {
        try
        {
            // Use WinRT to enumerate audio CAPTURE (recording) devices and find VB-Cable
            // We look for "CABLE Output" which is the capture device that receives what apps play to "CABLE Input"
            var selector = MediaDevice.GetAudioCaptureSelector();
            var devices = DeviceInformation.FindAllAsync(selector).GetAwaiter().GetResult();

            foreach (var device in devices)
            {
                if (device.Name.Contains(VBCableDeviceNamePattern, StringComparison.OrdinalIgnoreCase) ||
                    device.Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine($"[AudioProxyService] Found VB-Cable capture (CABLE Output): {device.Name} ({device.Id})");
                    return device.Id;
                }
            }

            // Also check for other virtual audio devices that might work
            foreach (var device in devices)
            {
                if (device.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                    device.Name.Contains("Audio", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine($"[AudioProxyService] Found virtual capture device: {device.Name} ({device.Id})");
                    return device.Id;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioProxyService] Error detecting VB-Cable capture device: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Detects VB-Cable Input device (playback device for mic proxy output).
    /// The mic proxy renders to "CABLE Input" (playback), which apps capture from "CABLE Output" (recording).
    /// Note: This is the same device as VB-Cable Output for speaker proxy - both are "CABLE Input" playback.
    /// For mic proxy, we need a SECOND VB-Cable or different virtual device.
    /// </summary>
    private string? DetectVBCableInputDevice()
    {
        try
        {
            // For mic proxy, we need to render to a playback device
            // If user has VB-Cable A+B, we could use "CABLE-B Input" for mic
            // Otherwise, we look for any second virtual audio playback device
            var selector = MediaDevice.GetAudioRenderSelector();
            var devices = DeviceInformation.FindAllAsync(selector).GetAwaiter().GetResult();

            // First, look for VB-Cable B (if installed)
            foreach (var device in devices)
            {
                if (device.Name.Contains("CABLE-B", StringComparison.OrdinalIgnoreCase) ||
                    device.Name.Contains("VB-Cable B", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine($"[AudioProxyService] Found VB-Cable B Input (for mic): {device.Name} ({device.Id})");
                    return device.Id;
                }
            }

            // Then look for any other virtual audio device that's different from speaker proxy device
            foreach (var device in devices)
            {
                bool isVirtual = device.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                                 device.Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);
                bool isDifferentFromSpeaker = device.Id != _vbCableRenderDeviceId;

                if (isVirtual && isDifferentFromSpeaker)
                {
                    Debug.WriteLine($"[AudioProxyService] Found alternate virtual device for mic: {device.Name} ({device.Id})");
                    return device.Id;
                }
            }

            // Fallback: use the same VB-Cable device (user would need to configure apps differently)
            // This is not ideal but allows the feature to work with single VB-Cable
            if (!string.IsNullOrEmpty(_vbCableRenderDeviceId))
            {
                Debug.WriteLine("[AudioProxyService] Warning: Using same VB-Cable device for mic proxy. Consider installing VB-Cable A+B for separate channels.");
                return _vbCableRenderDeviceId;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioProxyService] Error detecting VB-Cable Input: {ex.Message}");
        }

        return null;
    }

    private static string ConvertToMMDeviceId(string deviceId)
    {
        // If it's already in MMDevice format, return as-is
        if (deviceId.StartsWith("{"))
            return deviceId;

        // Extract the MMDevice ID from the WinRT format
        for (int i = 0; i < deviceId.Length - 1; i++)
        {
            if (deviceId[i] == '{' && char.IsDigit(deviceId[i + 1]))
            {
                int endIndex = deviceId.IndexOf('#', i);
                if (endIndex < 0)
                    endIndex = deviceId.Length;
                return deviceId.Substring(i, endIndex - i);
            }
        }

        return deviceId;
    }

    private void RaiseStatusChanged(ProxyStatus status)
    {
        StatusChanged?.Invoke(this, new ProxyStatusEventArgs(status));
    }

    private void RaiseMicStatusChanged(bool enabled, string? deviceId, string? errorMessage = null)
    {
        MicStatusChanged?.Invoke(this, new MicProxyStatusEventArgs(enabled, deviceId, errorMessage));
    }

    // IPC command/response types
    private sealed class IpcCommand
    {
        [JsonPropertyName("command")]
        public string Command { get; set; } = "";

        [JsonPropertyName("data")]
        public object? Data { get; set; }
    }

    private sealed class SetOutputData
    {
        [JsonPropertyName("device_id")]
        public string DeviceId { get; set; } = "";
    }

    private sealed class SetMicInputData
    {
        [JsonPropertyName("device_id")]
        public string DeviceId { get; set; } = "";
    }

    private sealed class EnableMicData
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }
    }

    private sealed class IpcResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("running")]
        public bool? Running { get; set; }

        [JsonPropertyName("output_device")]
        public string? OutputDevice { get; set; }

        [JsonPropertyName("mic_enabled")]
        public bool? MicEnabled { get; set; }

        [JsonPropertyName("mic_input_device")]
        public string? MicInputDevice { get; set; }
    }

    [JsonSerializable(typeof(IpcCommand))]
    [JsonSerializable(typeof(IpcResponse))]
    private partial class IpcJsonContext : JsonSerializerContext
    {
    }
}
