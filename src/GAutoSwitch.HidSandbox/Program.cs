using System.Text;
using System.Runtime.InteropServices;
using HidLibrary;

namespace GAutoSwitch.HidSandbox;

/// <summary>
/// HID++ 2.0 Sandbox for probing Logitech G Pro X 2 Lightspeed dongle.
/// This console app tests communication with the USB receiver to detect
/// whether the headset is connected (wireless mode) or offline (wired mode).
/// </summary>
public static class Program
{
    // Logitech Vendor ID
    private const int LogitechVid = 0x046D;

    // G Pro X 2 Lightspeed specific PID
    private const int GProX2Pid = 0x0AF7;

    // Logitech Audio device usage pages
    private const ushort LogitechAudioUsagePage = 0xFF13;
    private const ushort LogitechAudioUsagePage2 = 0xFFA0;

    // Known PIDs for G Pro X 2 Lightspeed receiver
    private static readonly int[] KnownReceiverPids = [
        0x0AF7, // G Pro X 2 Lightspeed (confirmed!)
        0x0AFE, // G Pro X 2 Lightspeed (alternate)
        0x0B00, // Alternate PID
    ];

    // HID++ Report IDs
    private const byte ShortReportId = 0x10;  // 7 bytes payload
    private const byte LongReportId = 0x11;   // 20 bytes payload

    // Device indices
    private const byte ReceiverIndex = 0xFF;  // Receiver itself
    private const byte DeviceIndex1 = 0x01;   // First paired device (headset)

    // HID++ Feature IDs
    private const ushort FeatureRoot = 0x0000;           // IRoot - feature discovery
    private const ushort FeatureDeviceInfo = 0x0003;     // IDeviceInfo
    private const ushort FeatureBatteryUnified = 0x1000; // Battery Unified
    private const ushort FeatureBatteryV1 = 0x1001;      // Battery v1
    private const ushort FeatureWirelessStatus = 0x1D4B; // Wireless device status

    // Software ID (arbitrary, identifies our application in responses)
    private const byte SoftwareId = 0x07;

    public static async Task Main(string[] args)
    {
        Console.WriteLine("==============================================");
        Console.WriteLine("  G-AutoSwitch HID++ Sandbox");
        Console.WriteLine("  Logitech G Pro X 2 Lightspeed Detector");
        Console.WriteLine("==============================================\n");

        // Check for simple detection mode
        if (args.Contains("--detect") || args.Contains("-d"))
        {
            await RunSimpleDetection();
            return;
        }

        // Check for monitor mode
        if (args.Contains("--monitor") || args.Contains("-m"))
        {
            await RunMonitorMode();
            return;
        }

        // Check for 0x51 protocol test mode
        if (args.Contains("--0x51") || args.Contains("--protocol"))
        {
            await Test0x51Protocol();
            return;
        }

        // Default: Full diagnostic mode
        await RunFullDiagnostics();
    }

    /// <summary>
    /// Dedicated test for the 0x51 Logitech Audio Protocol on 0xFFA0 interface.
    /// </summary>
    private static async Task Test0x51Protocol()
    {
        Console.WriteLine("[0x51 Protocol Test Mode]\n");
        Console.WriteLine("Testing Logitech Audio Protocol on 0xFFA0 interface...\n");

        // Find the G Pro X 2 device
        var devices = HidDevices.Enumerate(LogitechVid)
            .Where(d => d.Attributes.ProductId == GProX2Pid)
            .ToList();

        if (devices.Count == 0)
        {
            Console.WriteLine("ERROR: G Pro X 2 dongle not found!");
            return;
        }

        Console.WriteLine($"Found {devices.Count} G Pro X 2 interface(s)\n");

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
            Console.WriteLine("ERROR: 0xFFA0 interface not found!");
            Console.WriteLine("Available interfaces:");
            foreach (var d in devices)
            {
                try
                {
                    var caps = d.Capabilities;
                    Console.WriteLine($"  UsagePage: 0x{caps.UsagePage:X4}, Output: {caps.OutputReportByteLength} bytes");
                }
                catch { }
            }
            return;
        }

        var caps2 = extendedInterface.Capabilities;
        Console.WriteLine($"Found 0xFFA0 interface:");
        Console.WriteLine($"  Output Report Size: {caps2.OutputReportByteLength} bytes");
        Console.WriteLine($"  Input Report Size: {caps2.InputReportByteLength} bytes\n");

        var devicePath = extendedInterface.DevicePath;

        // Open with write access using native API
        Console.WriteLine("Opening device with native API...\n");
        using var handle = NativeHid.OpenDevice(devicePath, writeAccess: true);

        if (handle == null || handle.IsInvalid)
        {
            Console.WriteLine("ERROR: Could not open device with write access.");
            Console.WriteLine("G Hub may have exclusive access.\n");

            // Try read-only for data listening
            Console.WriteLine("Trying read-only access for data monitoring...\n");
            using var readHandle = NativeHid.OpenDevice(devicePath, writeAccess: false);
            if (readHandle != null && !readHandle.IsInvalid)
            {
                Console.WriteLine("Read-only access successful. Listening for data (5 seconds)...\n");
                int packets = 0;
                var start = DateTime.Now;
                while ((DateTime.Now - start).TotalSeconds < 5)
                {
                    var data = NativeHid.ReadFile(readHandle, caps2.InputReportByteLength, 100);
                    if (data != null && data.Any(b => b != 0))
                    {
                        packets++;
                        Console.WriteLine($"  [{packets}] {BitConverter.ToString(data.Take(16).ToArray())}...");
                        if (packets >= 20) break;
                    }
                }
                Console.WriteLine($"\nReceived {packets} data packets");
                Console.WriteLine(packets > 0 ? "HEADSET IS ONLINE" : "HEADSET IS OFFLINE (or no data flow)");
            }
            return;
        }

        var productName = NativeHid.GetProductString(handle) ?? "(unknown)";
        Console.WriteLine($"Device: {productName}\n");

        // Test 0x51 commands
        Console.WriteLine("=== Testing 0x51 Commands ===\n");

        var testCommands = new (string Name, byte[] Data)[]
        {
            ("Connection Check (0x1a)", new byte[] { 0x51, 0x06, 0x00, 0x03, 0x1a, 0x00, 0x01, 0x00 }),
            ("Battery Query", new byte[] { 0x51, 0x05, 0x00, 0x03, 0x04, 0x00, 0x00 }),
            ("Power Query", new byte[] { 0x51, 0x05, 0x00, 0x03, 0x05, 0x00, 0x00 }),
            ("Sidetone Read", LogitechAudioProtocol.Commands.Sidetone(0x00)),
            ("Query 0x1b", new byte[] { 0x51, 0x06, 0x00, 0x03, 0x1b, 0x00, 0x01, 0x00 }),
        };

        int successfulWrites = 0;
        int responses = 0;

        byte[]? connectionResponse = null;
        byte[]? batteryResponse = null;

        foreach (var (name, cmd) in testCommands)
        {
            var report = LogitechAudioProtocol.CreateCommand(cmd);

            Console.WriteLine($"[{name}]");
            Console.WriteLine($"  TX: {BitConverter.ToString(cmd)}");

            if (NativeHid.WriteOutputReport(handle, report))
            {
                successfulWrites++;
                Console.WriteLine("  Write: OK");

                // Try to read response
                var response = NativeHid.ReadFile(handle, caps2.InputReportByteLength, 300);
                if (response != null && response.Any(b => b != 0))
                {
                    responses++;
                    Console.WriteLine($"  RX: {BitConverter.ToString(response.Take(20).ToArray())}...");

                    // Detailed byte analysis
                    LogitechAudioProtocol.PrintDetailedAnalysis(response, name);

                    // Save key responses for final analysis
                    if (name.Contains("Connection"))
                        connectionResponse = response;
                    if (name.Contains("Battery"))
                        batteryResponse = response;
                }
                else
                {
                    Console.WriteLine("  RX: (no response/timeout)");
                }
            }
            else
            {
                Console.WriteLine("  Write: FAILED");
            }
            Console.WriteLine();
        }

        // Final interpretation
        Console.WriteLine("=== Response Interpretation ===\n");

        bool connectionOnline = false;
        bool batteryOnline = false;

        if (connectionResponse != null && connectionResponse.Length >= 4)
        {
            byte statusByte = connectionResponse[3];
            connectionOnline = statusByte != 0xFF;
            Console.WriteLine($"  Connection Check (0x1a):");
            Console.WriteLine($"    Byte 3 (Status): 0x{statusByte:X2}");
            Console.WriteLine($"    Result: {(connectionOnline ? "ONLINE (0x03)" : "OFFLINE (0xFF)")}");
        }

        if (batteryResponse != null && batteryResponse.Length >= 9)
        {
            byte statusByte = batteryResponse[3];
            byte battery = batteryResponse[6];
            byte connectedByte = batteryResponse[8];  // Byte 8 is the key! 0x00=OFF, 0x01=ON
            batteryOnline = connectedByte == 0x01;

            Console.WriteLine($"  Battery Response:");
            Console.WriteLine($"    Byte 3 (Status): 0x{statusByte:X2}");
            Console.WriteLine($"    Byte 6 (Battery): {battery} (0x{battery:X2})");
            Console.WriteLine($"    Byte 8 (Connected): 0x{connectedByte:X2} {(connectedByte == 0x01 ? "= CONNECTED" : "= DISCONNECTED")}");
            Console.WriteLine($"    Result: {(batteryOnline ? "ONLINE" : "OFFLINE")}");
        }

        // Summary
        Console.WriteLine("\n=== Summary ===\n");
        Console.WriteLine($"  Commands sent: {testCommands.Length}");
        Console.WriteLine($"  Successful writes: {successfulWrites}");
        Console.WriteLine($"  Responses received: {responses}");

        // Determine final state - prefer battery byte 7 as it's most reliable
        bool headsetOnline = batteryOnline || connectionOnline;

        Console.WriteLine($"\n  *** DETECTION RESULT ***");
        if (headsetOnline)
        {
            Console.WriteLine($"  --> HEADSET IS ONLINE (wireless mode active)");
            Console.WriteLine($"      Connection check: {(connectionOnline ? "ONLINE" : "offline")}");
            Console.WriteLine($"      Battery byte 7:   {(batteryOnline ? "CONNECTED" : "disconnected")}");
        }
        else
        {
            Console.WriteLine($"  --> HEADSET IS OFFLINE (powered off or wired mode)");
        }
    }

    private static async Task RunSimpleDetection()
    {
        Console.WriteLine("[Simple Detection Mode]\n");

        // First, check audio endpoint state (works even when G Hub is running)
        AudioEndpointChecker.CheckEndpoints();

        // Then try HID detection
        Console.WriteLine("\n[HID Detection]\n");
        var result = HeadsetDetector.DetectHeadsetState(1000);

        Console.WriteLine($"HID State: {result.State}");
        Console.WriteLine($"Product: {result.ProductName ?? "N/A"}");
        Console.WriteLine($"Data Packets: {result.DataPacketsReceived}");
        Console.WriteLine($"\nDiagnostics:\n{result.DiagnosticInfo}");

        Console.WriteLine("\n==============================================");
        Console.WriteLine($"  RESULT: {GetStateDescription(result.State)}");
        Console.WriteLine("==============================================");
    }

    private static async Task RunMonitorMode()
    {
        Console.WriteLine("[Monitor Mode - Press Ctrl+C to exit]\n");

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // Initial detection
        var initial = HeadsetDetector.DetectHeadsetState(1000);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Initial state: {initial.State}");
        Console.WriteLine($"  {GetStateDescription(initial.State)}\n");

        // Monitor for changes
        await HeadsetDetector.MonitorAsync(
            state =>
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] STATE CHANGED: {state}");
                Console.WriteLine($"  {GetStateDescription(state)}\n");
            },
            cts.Token,
            pollIntervalMs: 2000
        );

        Console.WriteLine("\nMonitoring stopped.");
    }

    private static string GetStateDescription(HeadsetDetector.HeadsetState state) => state switch
    {
        HeadsetDetector.HeadsetState.DongleNotFound => "USB dongle not detected. Plug in the receiver.",
        HeadsetDetector.HeadsetState.HeadsetOffline => "Headset is OFFLINE (powered off or using 3.5mm wired mode)",
        HeadsetDetector.HeadsetState.HeadsetOnline => "Headset is ONLINE (wireless mode active)",
        HeadsetDetector.HeadsetState.Unknown => "Unable to determine state (G Hub may have exclusive access)",
        _ => "Unknown state"
    };

    private static async Task RunFullDiagnostics()
    {
        // Step 1: Enumerate all Logitech HID devices
        Console.WriteLine("[STEP 1] Enumerating Logitech HID devices...\n");
        var logitechDevices = EnumerateLogitechDevices();

        if (logitechDevices.Count == 0)
        {
            Console.WriteLine("ERROR: No Logitech HID devices found!");
            Console.WriteLine("Make sure the USB dongle is plugged in.");
            return;
        }

        // Step 2: Find G Pro X 2 specifically
        Console.WriteLine("\n[STEP 2] Looking for G Pro X 2 Lightspeed (PID 0x0AF7)...\n");
        var gProX2Devices = FindGProX2Devices(logitechDevices);

        if (gProX2Devices.Count > 0)
        {
            Console.WriteLine($"  Found {gProX2Devices.Count} G Pro X 2 interface(s)!\n");

            // Step 3: Probe the G Pro X 2 specifically
            Console.WriteLine("\n[STEP 3] Probing G Pro X 2 interfaces...\n");

            foreach (var (devicePath, _) in gProX2Devices)
            {
                await ProbeGProX2(devicePath);
            }
        }
        else
        {
            Console.WriteLine("  G Pro X 2 not found. Looking for other HID++ devices...\n");

            // Fallback to generic HID++ detection
            var hidppDevices = FindHidPlusPlusDevices(logitechDevices);

            if (hidppDevices.Count == 0)
            {
                Console.WriteLine("ERROR: No HID++ capable devices found!");
                return;
            }

            foreach (var devicePath in hidppDevices)
            {
                await ProbeDevice(devicePath);
            }
        }

        // Step 4: Run the clean detector
        Console.WriteLine("\n==============================================");
        Console.WriteLine("  FINAL DETECTION RESULT");
        Console.WriteLine("==============================================\n");

        var result = HeadsetDetector.DetectHeadsetState(1000);
        Console.WriteLine($"  State: {result.State}");
        Console.WriteLine($"  {GetStateDescription(result.State)}");

        Console.WriteLine("\n==============================================");
        Console.WriteLine("  Sandbox Complete");
        Console.WriteLine("==============================================");
    }

    private static List<(string Path, HidDevice Device)> FindGProX2Devices(List<HidDevice> devices)
    {
        var gProX2 = new List<(string, HidDevice)>();

        foreach (var device in devices)
        {
            if (device.Attributes.ProductId == GProX2Pid)
            {
                try
                {
                    var caps = device.Capabilities;
                    Console.WriteLine($"  [G Pro X 2] Interface found:");
                    Console.WriteLine($"    Usage Page: 0x{caps.UsagePage:X4}  Usage: 0x{caps.Usage:X4}");
                    Console.WriteLine($"    Input: {caps.InputReportByteLength} bytes, Output: {caps.OutputReportByteLength} bytes");
                    Console.WriteLine($"    Path: ...{device.DevicePath[^50..]}");

                    // We want interfaces with output capability for communication
                    if (caps.OutputReportByteLength > 0)
                    {
                        gProX2.Add((device.DevicePath, device));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    (Error reading caps: {ex.Message})");
                }
            }
        }

        return gProX2;
    }

    private static async Task ProbeGProX2(string devicePath)
    {
        Console.WriteLine($"\n--- Probing G Pro X 2 Interface ---");
        Console.WriteLine($"Path: ...{devicePath[^50..]}\n");

        // First, try using native Windows HID API
        Console.WriteLine("  [Trying Native Windows HID API]");

        using (var nativeHandle = NativeHid.OpenDevice(devicePath, writeAccess: true))
        {
            if (nativeHandle != null && !nativeHandle.IsInvalid)
            {
                Console.WriteLine("  Native handle opened successfully!\n");

                // Get device strings
                var product = NativeHid.GetProductString(nativeHandle);
                var manufacturer = NativeHid.GetManufacturerString(nativeHandle);
                Console.WriteLine($"  Product: {product ?? "(unknown)"}");
                Console.WriteLine($"  Manufacturer: {manufacturer ?? "(unknown)"}\n");

                // Try native approach
                await ProbeWithNativeApi(devicePath, nativeHandle);
            }
            else
            {
                Console.WriteLine("  Native API failed to open device.\n");
            }
        }

        // Also try HidLibrary for comparison
        Console.WriteLine("\n  [Trying HidLibrary API]");
        var device = HidDevices.GetDevice(devicePath);

        try
        {
            // Try opening with shared access
            device.OpenDevice(DeviceMode.NonOverlapped, DeviceMode.NonOverlapped, ShareMode.ShareRead | ShareMode.ShareWrite);

            if (!device.IsOpen)
            {
                Console.WriteLine("  HidLibrary could not open device with shared access.\n");
                return;
            }

            Console.WriteLine("  HidLibrary opened device!\n");

            var caps = device.Capabilities;
            Console.WriteLine($"  Report sizes - Input: {caps.InputReportByteLength}, Output: {caps.OutputReportByteLength}");
            Console.WriteLine($"  Usage Page: 0x{caps.UsagePage:X4}");

            // Determine protocol based on usage page
            if ((ushort)caps.UsagePage == LogitechAudioUsagePage)
            {
                Console.WriteLine("\n  Detected Logitech Audio Protocol (0xFF13)");
                await ProbeLogitechAudioProtocol(device, caps);
            }
            else if ((ushort)caps.UsagePage == LogitechAudioUsagePage2)
            {
                Console.WriteLine("\n  Detected Logitech Extended Protocol (0xFFA0)");
                await ProbeLogitechExtendedProtocol(device, caps);
            }
            else if ((ushort)caps.UsagePage >= 0xFF00)
            {
                Console.WriteLine("\n  Detected vendor-specific protocol, trying HID++...");
                await ProbeHidPlusPlus(device, caps);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            device.CloseDevice();
            Console.WriteLine("\n  Device closed.\n");
        }
    }

    private static async Task ProbeWithNativeApi(string devicePath, Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        // Determine interface type from path or report size
        bool is0xFFA0 = devicePath.Contains("col03") || devicePath.Contains("&col03");
        int reportSize = is0xFFA0 ? 64 : 62;

        Console.WriteLine($"  Interface: {(is0xFFA0 ? "0xFFA0 (Extended)" : "0xFF13 (Audio)")}");
        Console.WriteLine($"  Report size: {reportSize} bytes\n");

        // If this is the 0xFFA0 interface, prioritize the 0x51 command format
        if (is0xFFA0)
        {
            Console.WriteLine("  [Priority: 0x51 Command Format (HeadsetControl discovery)]\n");

            // Try the known working 0x51 commands
            var commands0x51 = new (string Name, byte[] Data)[]
            {
                ("Minimal 0x51 query", new byte[] { 0x51, 0x04, 0x00, 0x03, 0x00, 0x00 }),
                ("Status query 0x01", new byte[] { 0x51, 0x05, 0x00, 0x03, 0x01, 0x00, 0x00 }),
                ("Battery query 0x04", new byte[] { 0x51, 0x05, 0x00, 0x03, 0x04, 0x00, 0x00 }),
                ("Sidetone read", LogitechAudioProtocol.Commands.Sidetone(0x00)),
            };

            foreach (var (name, cmd) in commands0x51)
            {
                var report = new byte[reportSize];
                Array.Copy(cmd, 0, report, 0, Math.Min(cmd.Length, report.Length));

                Console.Write($"    {name}: ");
                bool success = NativeHid.WriteOutputReport(handle, report);

                if (success)
                {
                    Console.WriteLine("WRITE OK!");
                    var response = NativeHid.ReadFile(handle, reportSize, 200);
                    if (response != null && !response.All(b => b == 0))
                    {
                        Console.WriteLine($"      RX: {BitConverter.ToString(response.Take(16).ToArray())}...");
                        Console.WriteLine($"      Analysis: {LogitechAudioProtocol.AnalyzeResponse(response)}");
                    }
                    else
                    {
                        Console.WriteLine("      No response data");
                    }
                }
                else
                {
                    Console.WriteLine("write failed");
                }
            }
        }

        // Try legacy approaches as fallback
        Console.WriteLine("\n  [Fallback: Legacy command formats]\n");

        // Approach 1: Try without report ID (some devices expect data starting at byte 0)
        Console.WriteLine("  [Approach 1: No Report ID prefix]");
        {
            var report = new byte[reportSize - 1]; // One byte shorter, no report ID
            report[0] = 0x01; // Device index
            report[1] = 0x00; // Feature index (IRoot)
            report[2] = 0x07; // Function | SoftwareId

            Console.WriteLine($"    TX ({report.Length} bytes): {BitConverter.ToString(report.Take(10).ToArray())}...");
            bool success = NativeHid.WriteOutputReport(handle, report);
            Console.WriteLine($"    Result: {(success ? "OK" : "Failed")}");
        }

        // Approach 2: Try exact size with different report IDs
        Console.WriteLine("\n  [Approach 2: Try various report IDs with exact size]");
        foreach (byte reportId in new byte[] { 0x00, 0x01, 0x02, 0x20, 0x21, 0x22 })
        {
            var report = new byte[reportSize];
            report[0] = reportId;
            report[1] = 0x01; // Might be length or device index
            report[2] = 0x00;
            report[3] = 0x00;

            Console.Write($"    ReportID 0x{reportId:X2}: ");
            bool success = NativeHid.WriteOutputReport(handle, report);
            Console.WriteLine(success ? "OK!" : "Failed");

            if (success) break;
        }

        // Approach 3: Standard audio protocol patterns
        Console.WriteLine("\n  [Approach 3: Standard Audio Protocol patterns]");

        var audioPatterns = new (string Name, byte[] Data)[]
        {
            ("Length-prefixed query", new byte[] { 0x04, 0x00, 0x01, 0x00, 0x00 }),
            ("Type/SubType format", new byte[] { 0xFF, 0x01, 0x00, 0x00, 0x00 }),
            ("Battery request", new byte[] { 0x11, 0xFF, 0x04, 0x00, 0x00 }),
            ("Status request", new byte[] { 0x11, 0xFF, 0x06, 0x00, 0x00 }),
        };

        foreach (var (name, pattern) in audioPatterns)
        {
            var report = new byte[reportSize];
            Array.Copy(pattern, 0, report, 0, Math.Min(pattern.Length, report.Length));

            Console.Write($"    {name}: ");
            bool success = NativeHid.WriteOutputReport(handle, report);
            Console.WriteLine(success ? "OK!" : "Failed");

            if (success)
            {
                var response = NativeHid.GetInputReport(handle, reportSize);
                if (response != null)
                {
                    Console.WriteLine($"      RX: {BitConverter.ToString(response.Take(16).ToArray())}...");
                }
                break;
            }
        }

        // Try to get feature reports (might reveal device state)
        Console.WriteLine("\n  [Trying Feature Reports]");
        bool anyFeature = false;
        for (byte reportId = 0; reportId <= 0x22; reportId++)
        {
            var feature = NativeHid.GetFeatureReport(handle, reportId, reportSize);
            if (feature != null)
            {
                anyFeature = true;
                Console.WriteLine($"    Feature Report 0x{reportId:X2}: {BitConverter.ToString(feature.Take(20).ToArray())}...");
            }
        }
        if (!anyFeature)
        {
            Console.WriteLine("    No feature reports available");
        }

        // The key insight: if we can't write but can read device strings,
        // we might be able to infer state from other means
        Console.WriteLine("\n  [Alternative: Check device presence via enumeration]");
        Console.WriteLine("    Device enumerated and identified = dongle is present");
        Console.WriteLine("    Unable to communicate = either G Hub blocking OR headset offline");
        Console.WriteLine("\n    Note: When headset is ONLINE, G Hub typically streams data.");
        Console.WriteLine("    When headset is OFFLINE, there's no data to intercept.");

        // Try direct read to see if there's any data waiting
        Console.WriteLine("\n  [Checking for incoming data (500ms)]");
        int dataPackets = 0;
        var startTime = DateTime.Now;
        while ((DateTime.Now - startTime).TotalMilliseconds < 500)
        {
            var incoming = NativeHid.ReadFile(handle, reportSize, 50);
            if (incoming != null)
            {
                dataPackets++;
                Console.WriteLine($"    Packet {dataPackets}: {BitConverter.ToString(incoming.Take(16).ToArray())}...");
                if (dataPackets >= 5) break; // Limit output
            }
        }

        if (dataPackets > 0)
        {
            Console.WriteLine($"    Received {dataPackets} packet(s)");
            Console.WriteLine("    --> HEADSET IS ONLINE (wireless mode active)");
        }
        else
        {
            Console.WriteLine("    No data received");
            Console.WriteLine("    --> HEADSET LIKELY OFFLINE (or G Hub consuming all data)");
        }
    }

    private static async Task ProbeLogitechAudioProtocol(HidDevice device, HidDeviceCapabilities caps)
    {
        // The 0xFF13 usage page appears to be for Logitech gaming headset control
        // Report format may be: [ReportID][Command][SubCommand][Data...]

        Console.WriteLine("\n  Attempting to probe Logitech Audio interface...");

        // First, try to send commands and get responses
        if (caps.OutputReportByteLength > 0)
        {
            Console.WriteLine("\n  [A] Sending probe commands...");

            // Based on reverse engineering of Logitech audio devices:
            // The protocol typically uses:
            // Byte 0: Report ID (often 0x11 or 0x21 for long reports)
            // Byte 1: Device index or command class
            // Byte 2: Command/Function ID
            // Bytes 3+: Parameters

            var queryCommands = new (string Name, byte[] Data)[]
            {
                // HID++ style commands (some audio devices support these)
                ("HID++ IRoot Query", new byte[] { 0x11, 0x01, 0x00, 0x07, 0x00, 0x00 }),
                ("HID++ Battery Query", new byte[] { 0x11, 0x01, 0x05, 0x07, 0x00, 0x00 }),

                // Logitech audio-specific commands (0xFF13 protocol)
                ("Audio Protocol Ping", new byte[] { 0x21, 0x00, 0xFF, 0x00, 0x00, 0x00 }),
                ("Audio Protocol Status", new byte[] { 0x21, 0x01, 0x00, 0x00, 0x00, 0x00 }),
                ("Battery Status Query", new byte[] { 0x21, 0x04, 0x00, 0x00, 0x00, 0x00 }),
                ("Device Info Query", new byte[] { 0x21, 0x06, 0x00, 0x00, 0x00, 0x00 }),

                // Alternative patterns
                ("Alt Ping 1", new byte[] { 0x00, 0x11, 0x01, 0x00, 0x07, 0x00, 0x00 }),
                ("Alt Ping 2", new byte[] { 0x00, 0xFF, 0x01, 0x00, 0x00, 0x00, 0x00 }),
            };

            foreach (var (name, cmd) in queryCommands)
            {
                var report = new byte[caps.OutputReportByteLength];
                Array.Copy(cmd, 0, report, 0, Math.Min(cmd.Length, report.Length));

                Console.WriteLine($"\n      [{name}]");
                Console.WriteLine($"      TX: {BitConverter.ToString(report.Take(10).ToArray())}...");

                // Try writing
                bool writeSuccess = TryWriteReport(device, report);

                if (writeSuccess)
                {
                    Console.WriteLine("      Write OK! Waiting for response (200ms)...");

                    // Try to read response with short timeout
                    var response = await ReadWithTimeoutNonBlocking(device, TimeSpan.FromMilliseconds(200));
                    if (response != null && response.Length > 0)
                    {
                        Console.WriteLine($"      RX: {BitConverter.ToString(response.Take(20).ToArray())}{(response.Length > 20 ? "..." : "")}");
                        AnalyzeResponse(response);
                    }
                    else
                    {
                        Console.WriteLine("      No response (device offline or command not recognized)");
                    }
                }
                else
                {
                    Console.WriteLine("      Write FAILED");
                }
            }
        }

        // Try passive listening (quick check)
        Console.WriteLine("\n  [B] Quick listen for unsolicited data (500ms)...");
        int packetsReceived = 0;
        var listenStart = DateTime.Now;

        while ((DateTime.Now - listenStart).TotalMilliseconds < 500)
        {
            var data = await ReadWithTimeoutNonBlocking(device, TimeSpan.FromMilliseconds(50));
            if (data != null && data.Length > 0)
            {
                packetsReceived++;
                Console.WriteLine($"      RX[{packetsReceived}]: {BitConverter.ToString(data.Take(16).ToArray())}...");
            }
        }

        // Final analysis
        Console.WriteLine("\n  [ANALYSIS]");
        Console.WriteLine($"  Packets received during listen: {packetsReceived}");

        if (packetsReceived > 0)
        {
            Console.WriteLine("  --> HEADSET ONLINE (receiving active data stream)");
        }
        else
        {
            Console.WriteLine("  --> HEADSET LIKELY OFFLINE (no data received)");
            Console.WriteLine("      This indicates the headset is powered off or in wired mode.");
        }
    }

    private static async Task<byte[]?> ReadWithTimeoutNonBlocking(HidDevice device, TimeSpan timeout)
    {
        var cts = new CancellationTokenSource();
        cts.CancelAfter(timeout);

        try
        {
            return await Task.Run(() =>
            {
                var result = device.Read((int)timeout.TotalMilliseconds);
                if (result.Status == HidDeviceData.ReadStatus.Success)
                    return result.Data;
                return null;
            }, cts.Token);
        }
        catch
        {
            return null;
        }
    }

    private static async Task ProbeLogitechExtendedProtocol(HidDevice device, HidDeviceCapabilities caps)
    {
        Console.WriteLine("\n  This interface uses 64-byte reports (extended protocol)");
        Console.WriteLine("  Testing Logitech Audio Protocol (0x51 commands) from HeadsetControl...\n");

        // First, try the known 0x51 command format discovered from HeadsetControl
        Console.WriteLine("  [A] Testing 0x51 Command Format (HeadsetControl discovery)\n");

        var knownCommands = new (string Name, byte[] Data)[]
        {
            ("Sidetone Query (read current)", LogitechAudioProtocol.Commands.Sidetone(0x00)),
            ("Auto-off Query", LogitechAudioProtocol.Commands.AutoOff(0x00)),
        };

        foreach (var (name, cmd) in knownCommands)
        {
            var report = LogitechAudioProtocol.CreateCommand(cmd);
            Console.WriteLine($"      [{name}]");
            Console.WriteLine($"      TX: {BitConverter.ToString(cmd)}");

            if (TryWriteReport(device, report))
            {
                Console.WriteLine("      Write OK!");
                var response = await ReadWithTimeoutNonBlocking(device, TimeSpan.FromMilliseconds(300));
                if (response != null && response.Length > 0)
                {
                    Console.WriteLine($"      RX: {LogitechAudioProtocol.AnalyzeResponse(response)}");
                    Console.WriteLine($"      Raw: {BitConverter.ToString(response.Take(16).ToArray())}...");
                }
                else
                {
                    Console.WriteLine("      No response (timeout)");
                }
            }
            else
            {
                Console.WriteLine("      Write FAILED");
            }
            Console.WriteLine();
        }

        // Try experimental status queries
        Console.WriteLine("  [B] Experimental Status Queries\n");

        foreach (var cmd in LogitechAudioProtocol.Commands.ExperimentalStatusQueries)
        {
            var report = LogitechAudioProtocol.CreateCommand(cmd);
            Console.Write($"      {BitConverter.ToString(cmd)}: ");

            if (TryWriteReport(device, report))
            {
                var response = await ReadWithTimeoutNonBlocking(device, TimeSpan.FromMilliseconds(200));
                if (response != null && !response.All(b => b == 0))
                {
                    Console.WriteLine($"RESPONSE!");
                    Console.WriteLine($"        {LogitechAudioProtocol.AnalyzeResponse(response)}");
                }
                else
                {
                    Console.WriteLine("no response");
                }
            }
            else
            {
                Console.WriteLine("write failed");
            }
        }

        // Also try legacy HID++ commands for comparison
        Console.WriteLine("\n  [C] Legacy HID++ Commands (for comparison)\n");

        var hidppCommands = new (string Name, byte[] Data)[]
        {
            ("HID++ Root Query", new byte[] { 0x11, 0x01, 0x00, 0x07, 0x00, 0x00, 0x00 }),
            ("HID++ Ping Device", new byte[] { 0x10, 0x01, 0x00, 0xE7, 0x00, 0x00, 0x00 }),
            ("Extended Status", new byte[] { 0x12, 0x01, 0x00, 0x07, 0x00, 0x00, 0x00 }),
        };

        foreach (var (name, cmd) in hidppCommands)
        {
            var report = new byte[caps.OutputReportByteLength];
            Array.Copy(cmd, 0, report, 0, Math.Min(cmd.Length, report.Length));

            Console.Write($"      [{name}]: ");

            if (TryWriteReport(device, report))
            {
                var response = await ReadWithTimeoutNonBlocking(device, TimeSpan.FromMilliseconds(200));
                if (response != null && !response.All(b => b == 0))
                {
                    Console.WriteLine($"RESPONSE: {BitConverter.ToString(response.Take(10).ToArray())}...");
                }
                else
                {
                    Console.WriteLine("no response");
                }
            }
            else
            {
                Console.WriteLine("write failed");
            }
        }

        // Listen for any unsolicited data
        Console.WriteLine("\n  [D] Listening for unsolicited data (1 second)...\n");
        int packetsReceived = 0;
        var listenStart = DateTime.Now;

        while ((DateTime.Now - listenStart).TotalMilliseconds < 1000)
        {
            var data = await ReadWithTimeoutNonBlocking(device, TimeSpan.FromMilliseconds(50));
            if (data != null && data.Length > 0 && !data.All(b => b == 0))
            {
                packetsReceived++;
                Console.WriteLine($"      Packet {packetsReceived}: {BitConverter.ToString(data.Take(16).ToArray())}...");
                if (packetsReceived >= 10) break;
            }
        }

        Console.WriteLine($"\n      Received {packetsReceived} data packet(s)");
        if (packetsReceived > 0)
        {
            Console.WriteLine("      --> HEADSET IS ONLINE (active data stream detected)");
        }
        else
        {
            Console.WriteLine("      --> No data received (headset may be offline or G Hub consuming all data)");
        }
    }

    private static async Task ProbeHidPlusPlus(HidDevice device, HidDeviceCapabilities caps)
    {
        var outputLen = caps.OutputReportByteLength;

        Console.WriteLine($"\n  Using HID++ protocol (output report size: {outputLen})");

        // Determine report type
        bool useLong = outputLen >= 20;
        byte reportId = useLong ? LongReportId : ShortReportId;
        int payloadLen = Math.Min(outputLen, useLong ? 20 : 7);

        var report = new byte[outputLen];
        report[0] = DeviceIndex1;
        report[1] = 0x00; // IRoot
        report[2] = (byte)(0x00 | SoftwareId);
        report[3] = 0x00; // Feature 0x0000 (IRoot)
        report[4] = 0x00;

        Console.WriteLine($"    TX: [{reportId:X2}] {BitConverter.ToString(report.Take(7).ToArray())}");

        bool writeSuccess = device.Write(report, reportId);

        if (!writeSuccess)
        {
            writeSuccess = TryWriteReport(device, report);
        }

        if (writeSuccess)
        {
            Console.WriteLine("    Write succeeded!");
            var response = await ReadWithTimeout(device, TimeSpan.FromMilliseconds(500));
            if (response != null)
            {
                Console.WriteLine($"    RX: {BitConverter.ToString(response)}");
            }
        }
        else
        {
            Console.WriteLine("    Write failed - G Hub has exclusive write access");
        }
    }

    private static bool TryWriteReport(HidDevice device, byte[] data)
    {
        // Method 1: Standard Write
        if (device.Write(data))
            return true;

        // Method 2: Write with report ID 0
        if (device.Write(data, 0x00))
            return true;

        // Method 3: WriteReport
        var report = new HidReport(data.Length) { Data = data };
        if (device.WriteReport(report))
            return true;

        return false;
    }

    private static void AnalyzeResponse(byte[] response)
    {
        if (response.Length < 2) return;

        // Check for common patterns
        if (response[0] == 0x8F)
        {
            Console.WriteLine("      -> Error response detected");
            if (response.Length > 4)
            {
                byte errorCode = response[4];
                string errorMsg = errorCode switch
                {
                    0x01 => "Device unavailable/offline",
                    0x02 => "Invalid device index",
                    0x03 => "Busy",
                    0x04 => "Unsupported",
                    0x05 => "Invalid argument",
                    _ => $"Unknown error 0x{errorCode:X2}"
                };
                Console.WriteLine($"      -> Error: {errorMsg}");

                if (errorCode == 0x01)
                {
                    Console.WriteLine("\n  *** HEADSET OFFLINE CONFIRMED ***");
                }
            }
        }
    }

    private static List<HidDevice> EnumerateLogitechDevices()
    {
        var devices = HidDevices.Enumerate(LogitechVid).ToList();

        Console.WriteLine($"Found {devices.Count} Logitech HID device(s):\n");

        foreach (var device in devices)
        {
            var pid = device.Attributes.ProductId;
            var vidHex = device.Attributes.VendorId.ToString("X4");
            var pidHex = pid.ToString("X4");

            Console.WriteLine($"  VID: 0x{vidHex}  PID: 0x{pidHex}");
            Console.WriteLine($"  Path: {device.DevicePath}");

            // Try to get capabilities
            try
            {
                var caps = device.Capabilities;
                Console.WriteLine($"  Usage Page: 0x{caps.UsagePage:X4}  Usage: 0x{caps.Usage:X4}");
                Console.WriteLine($"  Input Report Length: {caps.InputReportByteLength}");
                Console.WriteLine($"  Output Report Length: {caps.OutputReportByteLength}");
                Console.WriteLine($"  Feature Report Length: {caps.FeatureReportByteLength}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  (Could not read capabilities: {ex.Message})");
            }
            Console.WriteLine();
        }

        return devices;
    }

    private static List<string> FindHidPlusPlusDevices(List<HidDevice> devices)
    {
        var hidppPaths = new List<string>();

        foreach (var device in devices)
        {
            try
            {
                var caps = device.Capabilities;

                // HID++ devices use vendor-specific usage page (0xFF00 or similar)
                // and have specific input/output report lengths:
                // - Short HID++: 7 bytes (+ 1 byte report ID = 8 bytes)
                // - Long HID++: 20 bytes (+ 1 byte report ID = 21 bytes)

                bool isShortHidpp = caps.InputReportByteLength == 7 || caps.InputReportByteLength == 8;
                bool isLongHidpp = caps.InputReportByteLength == 20 || caps.InputReportByteLength == 21;
                bool isVeryLongHidpp = caps.InputReportByteLength == 64 || caps.InputReportByteLength == 65;

                // Also check usage page - Logitech uses 0xFF00 (vendor-specific)
                bool isVendorPage = (ushort)caps.UsagePage >= 0xFF00;

                if ((isShortHidpp || isLongHidpp || isVeryLongHidpp) && isVendorPage)
                {
                    Console.WriteLine($"  [HID++] Found: PID 0x{device.Attributes.ProductId:X4}");
                    Console.WriteLine($"          Report size: {caps.InputReportByteLength} bytes");
                    hidppPaths.Add(device.DevicePath);
                }
            }
            catch
            {
                // Skip devices we can't query
            }
        }

        return hidppPaths;
    }

    private static async Task ProbeDevice(string devicePath)
    {
        Console.WriteLine($"--- Probing device: {TruncatePath(devicePath)} ---\n");

        var device = HidDevices.GetDevice(devicePath);

        try
        {
            // Attempt to open with sharing (G Hub may have the device open)
            device.OpenDevice(DeviceMode.NonOverlapped, DeviceMode.NonOverlapped, ShareMode.ShareRead | ShareMode.ShareWrite);

            if (!device.IsOpen)
            {
                Console.WriteLine("  ERROR: Could not open device (G Hub may have exclusive access)");
                Console.WriteLine("  Tip: Try closing G Hub temporarily for testing.\n");
                return;
            }

            Console.WriteLine("  Device opened successfully (shared mode)\n");

            // Get device capabilities to determine report sizes
            var caps = device.Capabilities;
            var inputLen = caps.InputReportByteLength;
            var outputLen = caps.OutputReportByteLength;

            Console.WriteLine($"  Report sizes - Input: {inputLen}, Output: {outputLen}");

            // Determine which HID++ mode to use
            bool useShort = outputLen >= 7 && outputLen <= 8;
            bool useLong = outputLen >= 20 && outputLen <= 21;

            // Step A: Try to ping the receiver
            Console.WriteLine("\n  [A] Pinging receiver (IRoot feature discovery)...");
            var rootResult = await QueryFeatureIndex(device, DeviceIndex1, FeatureRoot, outputLen);

            if (rootResult.Success)
            {
                Console.WriteLine($"      Receiver responded! IRoot is at index 0x{rootResult.FeatureIndex:X2}");

                // Step B: Discover Battery feature
                Console.WriteLine("\n  [B] Discovering Battery feature...");
                var batteryResult = await QueryFeatureIndex(device, DeviceIndex1, FeatureBatteryUnified, outputLen);

                if (!batteryResult.Success)
                {
                    Console.WriteLine("      Battery Unified (0x1000) not found, trying Battery v1 (0x1001)...");
                    batteryResult = await QueryFeatureIndex(device, DeviceIndex1, FeatureBatteryV1, outputLen);
                }

                if (batteryResult.Success)
                {
                    Console.WriteLine($"      Battery feature found at index 0x{batteryResult.FeatureIndex:X2}");

                    // Step C: Query battery level
                    Console.WriteLine("\n  [C] Querying battery status...");
                    await QueryBatteryStatus(device, DeviceIndex1, batteryResult.FeatureIndex, outputLen);
                }
                else
                {
                    Console.WriteLine("      Battery feature not found - device may be offline");
                }
            }
            else
            {
                Console.WriteLine($"      No response from device (Error: {rootResult.ErrorCode:X2})");

                if (rootResult.ErrorCode == 0x01)
                {
                    Console.WriteLine("\n  *** HEADSET OFFLINE ***");
                    Console.WriteLine("  The receiver reports the device is not connected.");
                    Console.WriteLine("  This indicates WIRED MODE (headset powered off or using 3.5mm cable).");
                }
                else if (rootResult.ErrorCode == 0x02)
                {
                    Console.WriteLine("      Error: Invalid device index");
                }
                else if (rootResult.ErrorCode == 0xFF)
                {
                    Console.WriteLine("      No response received (timeout or G Hub blocking)");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Exception: {ex.Message}");
        }
        finally
        {
            device.CloseDevice();
            Console.WriteLine("\n  Device closed.\n");
        }
    }

    private record FeatureQueryResult(bool Success, byte FeatureIndex, byte ErrorCode);

    private static async Task<FeatureQueryResult> QueryFeatureIndex(
        HidDevice device, byte deviceIndex, ushort featureId, int outputReportLen)
    {
        // IRoot::GetFeatureIndex (function 0x00)
        // Send: [DeviceIndex][0x00][0x00 | SoftwareId][FeatureId_High][FeatureId_Low]

        bool useLong = outputReportLen >= 20;
        byte reportId = useLong ? LongReportId : ShortReportId;
        int payloadLen = useLong ? 20 : 7;

        var report = new byte[payloadLen];
        report[0] = deviceIndex;
        report[1] = 0x00; // IRoot feature index is always 0x00
        report[2] = (byte)(0x00 | SoftwareId); // Function 0x00 (GetFeatureIndex) | SoftwareId
        report[3] = (byte)(featureId >> 8);    // Feature ID high byte
        report[4] = (byte)(featureId & 0xFF);  // Feature ID low byte

        Console.WriteLine($"      TX: [{reportId:X2}] {BitConverter.ToString(report)}");

        // Write the report
        var writeResult = device.Write(report, reportId);

        if (!writeResult)
        {
            Console.WriteLine("      Write failed!");
            return new FeatureQueryResult(false, 0, 0xFF);
        }

        // Read response with timeout
        var response = await ReadWithTimeout(device, TimeSpan.FromMilliseconds(500));

        if (response == null)
        {
            Console.WriteLine("      No response (timeout)");
            return new FeatureQueryResult(false, 0, 0xFF);
        }

        Console.WriteLine($"      RX: {BitConverter.ToString(response)}");

        // Check for error response (report ID 0x8F)
        if (response.Length > 0 && response[0] == 0x8F)
        {
            byte errorCode = response.Length > 4 ? response[4] : (byte)0xFF;
            return new FeatureQueryResult(false, 0, errorCode);
        }

        // Parse successful response
        // Response: [DeviceIndex][0x00][0x00|SoftwareId][FeatureIndex][FeatureType][Reserved]
        if (response.Length >= 5 && response[1] == 0x00)
        {
            byte featureIndex = response[3];
            return new FeatureQueryResult(true, featureIndex, 0);
        }

        return new FeatureQueryResult(false, 0, 0xFF);
    }

    private static async Task QueryBatteryStatus(HidDevice device, byte deviceIndex, byte batteryFeatureIndex, int outputReportLen)
    {
        // Battery::GetStatus (function 0x00)
        bool useLong = outputReportLen >= 20;
        byte reportId = useLong ? LongReportId : ShortReportId;
        int payloadLen = useLong ? 20 : 7;

        var report = new byte[payloadLen];
        report[0] = deviceIndex;
        report[1] = batteryFeatureIndex;
        report[2] = (byte)(0x00 | SoftwareId); // Function 0x00 (GetStatus) | SoftwareId

        Console.WriteLine($"      TX: [{reportId:X2}] {BitConverter.ToString(report)}");

        var writeResult = device.Write(report, reportId);

        if (!writeResult)
        {
            Console.WriteLine("      Write failed!");
            return;
        }

        var response = await ReadWithTimeout(device, TimeSpan.FromMilliseconds(500));

        if (response == null)
        {
            Console.WriteLine("      No response (timeout) - headset may be offline");
            return;
        }

        Console.WriteLine($"      RX: {BitConverter.ToString(response)}");

        // Check for error
        if (response.Length > 0 && response[0] == 0x8F)
        {
            byte errorCode = response.Length > 4 ? response[4] : (byte)0xFF;
            Console.WriteLine($"      Error response: 0x{errorCode:X2}");

            if (errorCode == 0x01)
            {
                Console.WriteLine("\n  *** HEADSET OFFLINE ***");
                Console.WriteLine("  Device is not responding - likely in WIRED MODE.");
            }
            return;
        }

        // Parse battery response
        // Response format varies by battery feature version
        if (response.Length >= 6)
        {
            byte level = response[3];
            byte nextLevel = response[4];
            byte status = response[5];

            Console.WriteLine($"\n  *** HEADSET ONLINE (WIRELESS MODE) ***");
            Console.WriteLine($"  Battery Level: {level}%");
            Console.WriteLine($"  Charging Status: 0x{status:X2}");

            string statusText = status switch
            {
                0x00 => "Discharging",
                0x01 => "Charging",
                0x02 => "Almost full",
                0x03 => "Fully charged",
                0x04 => "Slow charging",
                0x05 => "Invalid battery",
                0x06 => "Thermal error",
                _ => $"Unknown (0x{status:X2})"
            };
            Console.WriteLine($"  Status: {statusText}");
        }
    }

    private static async Task<byte[]?> ReadWithTimeout(HidDevice device, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            var readTask = Task.Run(() =>
            {
                var data = device.Read((int)timeout.TotalMilliseconds);
                return data.Status == HidDeviceData.ReadStatus.Success ? data.Data : null;
            });

            return await readTask;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static string TruncatePath(string path)
    {
        if (path.Length <= 60) return path;
        return path[..30] + "..." + path[^27..];
    }
}
