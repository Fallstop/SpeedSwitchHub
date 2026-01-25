using System.Runtime.InteropServices;
using HidLibrary;

namespace GAutoSwitch.HidSandbox;

/// <summary>
/// Detects the connection state of the Logitech G Pro X 2 Lightspeed headset.
/// Uses Windows Core Audio API as primary detection (works with G Hub running).
/// Falls back to HID detection if audio API is unavailable.
/// </summary>
public class HeadsetDetector
{
    private const int LogitechVid = 0x046D;
    private const int GProX2Pid = 0x0AF7;
    private const ushort LogitechAudioUsagePage = 0xFF13;

    public enum HeadsetState
    {
        /// <summary>The dongle is not connected to the PC.</summary>
        DongleNotFound,

        /// <summary>The dongle is connected but the headset is powered off (wireless offline).</summary>
        HeadsetOffline,

        /// <summary>The headset is connected and communicating wirelessly.</summary>
        HeadsetOnline,

        /// <summary>Unable to determine state (G Hub blocking or other issue).</summary>
        Unknown
    }

    public record DetectionResult(
        HeadsetState State,
        string? DevicePath,
        string? ProductName,
        int DataPacketsReceived,
        string DiagnosticInfo
    );

    /// <summary>
    /// Detects the current state of the G Pro X 2 Lightspeed headset.
    /// Uses 0x51 protocol on 0xFFA0 interface as primary detection method.
    /// </summary>
    public static DetectionResult DetectHeadsetState(int listenDurationMs = 500)
    {
        var diagnostics = new System.Text.StringBuilder();

        // Primary Method: Use 0x51 protocol Battery Query on 0xFFA0 interface
        // This reliably detects actual headset connection state
        var (protocolState, batteryLevel) = DetectVia0x51Protocol();
        diagnostics.AppendLine($"0x51 Protocol Detection: {protocolState}");

        if (protocolState == HeadsetState.HeadsetOnline)
        {
            diagnostics.AppendLine($"Headset ONLINE (Battery: {batteryLevel}%)");
            return new DetectionResult(
                HeadsetState.HeadsetOnline,
                null,
                "PRO X 2 LIGHTSPEED",
                -1,
                diagnostics.ToString()
            );
        }
        else if (protocolState == HeadsetState.HeadsetOffline)
        {
            diagnostics.AppendLine("Headset OFFLINE (0x51 protocol confirmed)");
            return new DetectionResult(
                HeadsetState.HeadsetOffline,
                null,
                "PRO X 2 LIGHTSPEED",
                0,
                diagnostics.ToString()
            );
        }
        else if (protocolState == HeadsetState.DongleNotFound)
        {
            diagnostics.AppendLine("USB dongle not found");
            return new DetectionResult(
                HeadsetState.DongleNotFound,
                null,
                null,
                0,
                diagnostics.ToString()
            );
        }

        // Fallback: Try audio endpoint check if 0x51 protocol failed
        diagnostics.AppendLine("0x51 protocol unavailable, falling back to audio endpoint check...");
        var (audioState, endpointName) = CheckAudioEndpointState();
        diagnostics.AppendLine($"Audio Endpoint Check: {audioState}");

        if (audioState == AudioEndpointState.NotPresent || audioState == AudioEndpointState.Unplugged)
        {
            return new DetectionResult(
                HeadsetState.HeadsetOffline,
                null,
                endpointName ?? "PRO X 2 LIGHTSPEED",
                0,
                diagnostics.ToString()
            );
        }
        else if (audioState == AudioEndpointState.NotFound)
        {
            var donglePresent = CheckDonglePresent();
            if (!donglePresent)
            {
                return new DetectionResult(
                    HeadsetState.DongleNotFound,
                    null,
                    null,
                    0,
                    diagnostics.ToString()
                );
            }
        }

        // Last resort: HID packet detection
        diagnostics.AppendLine("Falling back to HID packet detection...");
        return DetectViaHid(listenDurationMs, diagnostics);
    }

    private const ushort LogitechAudioUsagePage2 = 0xFFA0;

    /// <summary>
    /// Detects headset state using the 0x51 Logitech Audio Protocol.
    /// Sends Battery Query command and checks Byte 8 for connection status.
    /// </summary>
    private static (HeadsetState State, int BatteryLevel) DetectVia0x51Protocol()
    {
        // Find G Pro X 2 devices
        var devices = HidDevices.Enumerate(LogitechVid)
            .Where(d => d.Attributes.ProductId == GProX2Pid)
            .ToList();

        if (devices.Count == 0)
        {
            return (HeadsetState.DongleNotFound, 0);
        }

        // Find the 0xFFA0 interface
        var extendedInterface = devices.FirstOrDefault(d =>
        {
            try
            {
                var caps = d.Capabilities;
                return (ushort)caps.UsagePage == LogitechAudioUsagePage2 &&
                       caps.OutputReportByteLength > 0;
            }
            catch { return false; }
        });

        if (extendedInterface == null)
        {
            return (HeadsetState.Unknown, 0);
        }

        var devicePath = extendedInterface.DevicePath;
        var reportSize = extendedInterface.Capabilities.InputReportByteLength;

        using var handle = NativeHid.OpenDevice(devicePath, writeAccess: true);
        if (handle == null || handle.IsInvalid)
        {
            return (HeadsetState.Unknown, 0);
        }

        // Send Battery Query: 51-05-00-03-04-00-00
        var batteryQuery = new byte[64];
        batteryQuery[0] = 0x51;
        batteryQuery[1] = 0x05;
        batteryQuery[2] = 0x00;
        batteryQuery[3] = 0x03;
        batteryQuery[4] = 0x04;
        batteryQuery[5] = 0x00;
        batteryQuery[6] = 0x00;

        if (!NativeHid.WriteOutputReport(handle, batteryQuery))
        {
            return (HeadsetState.Unknown, 0);
        }

        // Read response
        var response = NativeHid.ReadFile(handle, reportSize, 300);
        if (response == null || response.Length < 10)
        {
            return (HeadsetState.Unknown, 0);
        }

        // Check response format: 51-08-00-03-04-XX-[battery]-00-[connected]-XX
        if (response[0] != 0x51 || response[4] != 0x04)
        {
            return (HeadsetState.Unknown, 0);
        }

        byte connectedByte = response[8]; // 0x01 = CONNECTED, 0x00 = DISCONNECTED
        byte batteryLevel = response[6];  // Battery percentage

        if (connectedByte == 0x01)
        {
            return (HeadsetState.HeadsetOnline, batteryLevel);
        }
        else
        {
            return (HeadsetState.HeadsetOffline, 0);
        }
    }

    private enum AudioEndpointState { Active, Disabled, NotPresent, Unplugged, NotFound, Error }

    private static (AudioEndpointState State, string? Name) CheckAudioEndpointState()
    {
        // Use the working AudioEndpointChecker
        var result = AudioEndpointChecker.GetProX2EndpointState();

        if (result == null)
        {
            return (AudioEndpointState.NotFound, null);
        }

        var state = result.State switch
        {
            AudioEndpointChecker.EndpointState.Active => AudioEndpointState.Active,
            AudioEndpointChecker.EndpointState.Disabled => AudioEndpointState.Disabled,
            AudioEndpointChecker.EndpointState.NotPresent => AudioEndpointState.NotPresent,
            AudioEndpointChecker.EndpointState.Unplugged => AudioEndpointState.Unplugged,
            _ => AudioEndpointState.Error
        };

        return (state, result.Name);
    }

    private static bool CheckDonglePresent()
    {
        return HidDevices.Enumerate(LogitechVid)
            .Any(d => d.Attributes.ProductId == GProX2Pid);
    }

    private static DetectionResult DetectViaHid(int listenDurationMs, System.Text.StringBuilder diagnostics)
    {
        var devices = HidDevices.Enumerate(LogitechVid)
            .Where(d => d.Attributes.ProductId == GProX2Pid)
            .ToList();

        if (devices.Count == 0)
        {
            return new DetectionResult(
                HeadsetState.DongleNotFound,
                null,
                null,
                0,
                diagnostics.ToString() + "\nNo Logitech G Pro X 2 dongle found."
            );
        }

        var audioInterface = devices.FirstOrDefault(d =>
        {
            try
            {
                var caps = d.Capabilities;
                return (ushort)caps.UsagePage == LogitechAudioUsagePage && caps.InputReportByteLength > 0;
            }
            catch { return false; }
        });

        if (audioInterface == null)
        {
            return new DetectionResult(
                HeadsetState.Unknown,
                null,
                null,
                0,
                diagnostics.ToString() + "\nAudio interface not accessible."
            );
        }

        var devicePath = audioInterface.DevicePath;
        string? productName = null;
        int dataPackets = 0;

        using (var handle = NativeHid.OpenDevice(devicePath, writeAccess: false))
        {
            if (handle != null && !handle.IsInvalid)
            {
                productName = NativeHid.GetProductString(handle) ?? "G Pro X 2 Lightspeed";
                int reportSize = audioInterface.Capabilities.InputReportByteLength;
                var startTime = DateTime.Now;

                while ((DateTime.Now - startTime).TotalMilliseconds < listenDurationMs)
                {
                    var data = NativeHid.ReadFile(handle, reportSize, 50);
                    if (data != null && data.Length > 0) dataPackets++;
                }
            }
        }

        diagnostics.AppendLine($"HID packets received: {dataPackets}");

        return new DetectionResult(
            dataPackets > 0 ? HeadsetState.HeadsetOnline : HeadsetState.HeadsetOffline,
            devicePath,
            productName,
            dataPackets,
            diagnostics.ToString()
        );
    }


    /// <summary>
    /// Continuously monitors the headset state and invokes a callback on state change.
    /// </summary>
    public static async Task MonitorAsync(
        Action<HeadsetState> onStateChange,
        CancellationToken cancellationToken,
        int pollIntervalMs = 2000)
    {
        HeadsetState lastState = HeadsetState.Unknown;

        while (!cancellationToken.IsCancellationRequested)
        {
            var result = DetectHeadsetState(500);

            if (result.State != lastState)
            {
                lastState = result.State;
                onStateChange(result.State);
            }

            try
            {
                await Task.Delay(pollIntervalMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
