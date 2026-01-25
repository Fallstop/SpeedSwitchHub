using GAutoSwitch.Core.Interfaces;
using HidLibrary;

namespace GAutoSwitch.Hardware;

/// <summary>
/// Detects the connection state of the Logitech G Pro X 2 Lightspeed headset.
/// Uses 0x51 Battery Query command on 0xFFA0 interface - byte 8 indicates connection status.
/// </summary>
public class HeadsetStateService : IHeadsetStateService
{
    private const int LogitechVid = 0x046D;
    private const int GProX2Pid = 0x0AF7;
    private const int ReadTimeoutMs = 100;

    private HeadsetConnectionState _currentState = HeadsetConnectionState.Unknown;
    private string? _productName;
    private bool _isDongleConnected;
    private bool _isMonitoring;
    private CancellationTokenSource? _monitoringCts;
    private Task? _monitoringTask;

    // Cached device handle for faster repeated detection
    private Microsoft.Win32.SafeHandles.SafeFileHandle? _cachedHandle;
    private string? _cachedDevicePath;
    private int _cachedReportSize;
    private readonly object _handleLock = new();

    public HeadsetConnectionState CurrentState => _currentState;
    public string? ProductName => _productName;
    public bool IsDongleConnected => _isDongleConnected;
    public bool IsMonitoring => _isMonitoring;

    public event EventHandler<HeadsetStateChangedEventArgs>? StateChanged;

    public HeadsetConnectionState Detect(int listenDurationMs = 500)
    {
        var devices = HidDevices.Enumerate(LogitechVid)
            .Where(d => d.Attributes.ProductId == GProX2Pid)
            .ToList();

        if (devices.Count == 0)
        {
            _isDongleConnected = false;
            _productName = null;
            InvalidateCache();
            return UpdateState(HeadsetConnectionState.DongleNotFound);
        }

        _isDongleConnected = true;

        // Find 0xFFA0 interface for 0x51 protocol
        var extendedInterface = devices.FirstOrDefault(d =>
        {
            try
            {
                var caps = d.Capabilities;
                return (ushort)caps.UsagePage == LogitechAudioProtocol.ExtendedUsagePage &&
                       caps.OutputReportByteLength > 0;
            }
            catch
            {
                return false;
            }
        });

        if (extendedInterface == null)
        {
            return UpdateState(HeadsetConnectionState.Unknown);
        }

        return UpdateState(DetectVia0x51Command(extendedInterface));
    }

    /// <summary>
    /// Detects headset state using 0x51 Battery Query command.
    /// Response byte 8: 0x01 = online, 0x00 = offline.
    /// </summary>
    private HeadsetConnectionState DetectVia0x51Command(HidDevice device)
    {
        try
        {
            var devicePath = device.DevicePath;
            var reportSize = device.Capabilities.OutputReportByteLength;

            lock (_handleLock)
            {
                // Reuse cached handle if available and valid
                if (_cachedHandle == null || _cachedHandle.IsInvalid || _cachedHandle.IsClosed ||
                    _cachedDevicePath != devicePath)
                {
                    _cachedHandle?.Dispose();
                    _cachedHandle = NativeHid.OpenDevice(devicePath, writeAccess: true);
                    _cachedDevicePath = devicePath;
                    _cachedReportSize = reportSize;

                    if (_cachedHandle != null && !_cachedHandle.IsInvalid)
                    {
                        _productName = NativeHid.GetProductString(_cachedHandle) ?? "G Pro X 2 Lightspeed";
                    }
                }

                if (_cachedHandle == null || _cachedHandle.IsInvalid)
                {
                    return HeadsetConnectionState.Unknown;
                }

                var command = LogitechAudioProtocol.CreateCommand(
                    LogitechAudioProtocol.Commands.BatteryQuery,
                    _cachedReportSize);

                if (NativeHid.WriteOutputReport(_cachedHandle, command))
                {
                    var response = NativeHid.ReadFile(_cachedHandle, _cachedReportSize, ReadTimeoutMs);

                    if (LogitechAudioProtocol.IsDeviceOnline(response))
                    {
                        return HeadsetConnectionState.Online;
                    }
                    else
                    {
                        return HeadsetConnectionState.Offline;
                    }
                }
                else
                {
                    // Write failed - invalidate cache
                    InvalidateCacheLocked();
                }
            }
        }
        catch
        {
            InvalidateCache();
        }

        return HeadsetConnectionState.Unknown;
    }

    private void InvalidateCache()
    {
        lock (_handleLock)
        {
            InvalidateCacheLocked();
        }
    }

    private void InvalidateCacheLocked()
    {
        _cachedHandle?.Dispose();
        _cachedHandle = null;
        _cachedDevicePath = null;
    }

    public void StartMonitoring(int pollIntervalMs = 150)
    {
        if (_isMonitoring) return;

        _isMonitoring = true;
        _monitoringCts = new CancellationTokenSource();

        var actualInterval = Math.Max(pollIntervalMs, 100);

        _monitoringTask = Task.Run(async () =>
        {
            while (!_monitoringCts.Token.IsCancellationRequested)
            {
                try
                {
                    Detect();
                    await Task.Delay(actualInterval, _monitoringCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(actualInterval, _monitoringCts.Token);
                }
            }
        }, _monitoringCts.Token);
    }

    public void StopMonitoring()
    {
        if (!_isMonitoring) return;

        _monitoringCts?.Cancel();
        _monitoringTask?.Wait(1000);
        _monitoringCts?.Dispose();
        _monitoringCts = null;
        _monitoringTask = null;
        _isMonitoring = false;
    }

    private HeadsetConnectionState UpdateState(HeadsetConnectionState newState)
    {
        var previousState = _currentState;
        _currentState = newState;

        if (previousState != newState)
        {
            StateChanged?.Invoke(this, new HeadsetStateChangedEventArgs(previousState, newState));
        }

        return newState;
    }

    public void Dispose()
    {
        StopMonitoring();
        InvalidateCache();
    }
}
