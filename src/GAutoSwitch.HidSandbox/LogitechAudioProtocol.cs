namespace GAutoSwitch.HidSandbox;

/// <summary>
/// Logitech Audio Protocol helpers for G Pro X 2 Lightspeed.
/// Based on reverse-engineering from HeadsetControl project.
///
/// The G Pro X 2 uses a proprietary protocol on the 0xFFA0 interface:
/// - Command prefix: 0x51
/// - Format: [0x51][Length][0x00][0x03][Params...]
/// </summary>
public static class LogitechAudioProtocol
{
    /// <summary>Report size for 0xFFA0 interface (64 bytes)</summary>
    public const int ReportSize = 64;

    /// <summary>Command prefix for Logitech Audio Protocol</summary>
    public const byte CommandPrefix = 0x51;

    /// <summary>Usage page for the extended protocol interface</summary>
    public const ushort ExtendedUsagePage = 0xFFA0;

    /// <summary>Usage page for the audio control interface</summary>
    public const ushort AudioUsagePage = 0xFF13;

    /// <summary>
    /// Known commands discovered from HeadsetControl Issue #314.
    /// Format: [0x51][Length][0x00][0x03][CommandType][0x00][SubLen][0x00][SubType][CommandId][Value...]
    /// </summary>
    public static class Commands
    {
        /// <summary>
        /// Sidetone command: 0x51, 0x0a, 0x00, 0x03, 0x1b, 0x00, 0x05, 0x00, 0x07, 0x1b, 0x01, [value]
        /// Value: 0x00-0x64 (0-100%)
        /// </summary>
        public static byte[] Sidetone(byte level) => new byte[]
        {
            0x51, 0x0a, 0x00, 0x03, 0x1b, 0x00, 0x05, 0x00, 0x07, 0x1b, 0x01, level
        };

        /// <summary>
        /// Microphone Noise Reduction: 0x51, 0x09, 0x00, 0x03, 0x1c, 0x00, 0x04, 0x00, 0x08, 0x1c, [value]
        /// Value: 0x00 (off), 0x01 (on)
        /// </summary>
        public static byte[] MicNoiseReduction(bool enabled) => new byte[]
        {
            0x51, 0x09, 0x00, 0x03, 0x1c, 0x00, 0x04, 0x00, 0x08, 0x1c, (byte)(enabled ? 0x01 : 0x00)
        };

        /// <summary>
        /// Auto-off timer: 0x51, 0x09, 0x00, 0x03, 0x1c, 0x00, 0x04, 0x00, 0x06, 0x1d, [value]
        /// Value: minutes (0 = disabled)
        /// </summary>
        public static byte[] AutoOff(byte minutes) => new byte[]
        {
            0x51, 0x09, 0x00, 0x03, 0x1c, 0x00, 0x04, 0x00, 0x06, 0x1d, minutes
        };

        /// <summary>
        /// Experimental status query commands to discover battery/connection state.
        /// These are educated guesses based on the command pattern.
        /// </summary>
        public static byte[][] ExperimentalStatusQueries => new[]
        {
            // Try querying with different command types
            new byte[] { 0x51, 0x04, 0x00, 0x03, 0x00, 0x00 },           // Minimal query
            new byte[] { 0x51, 0x05, 0x00, 0x03, 0x01, 0x00, 0x00 },     // Status type 0x01
            new byte[] { 0x51, 0x05, 0x00, 0x03, 0x04, 0x00, 0x00 },     // Battery type 0x04
            new byte[] { 0x51, 0x05, 0x00, 0x03, 0x05, 0x00, 0x00 },     // Power type 0x05
            new byte[] { 0x51, 0x06, 0x00, 0x03, 0x1a, 0x00, 0x01, 0x00 }, // Query 0x1a
            new byte[] { 0x51, 0x06, 0x00, 0x03, 0x1b, 0x00, 0x01, 0x00 }, // Query 0x1b (sidetone related)
            new byte[] { 0x51, 0x06, 0x00, 0x03, 0x1c, 0x00, 0x01, 0x00 }, // Query 0x1c (settings related)

            // Try HID++ style ping on this interface (may work on some devices)
            new byte[] { 0x10, 0x01, 0x00, 0x07, 0x00, 0x00, 0x00 },     // HID++ short ping
            new byte[] { 0x11, 0x01, 0x00, 0x07, 0x00, 0x00, 0x00 },     // HID++ long ping
        };
    }

    /// <summary>
    /// Creates a command buffer with proper padding for the 64-byte report.
    /// </summary>
    public static byte[] CreateCommand(byte[] command)
    {
        var buffer = new byte[ReportSize];
        Array.Copy(command, 0, buffer, 0, Math.Min(command.Length, buffer.Length));
        return buffer;
    }

    /// <summary>
    /// Parses a response to check if it's a valid acknowledgment.
    /// </summary>
    public static bool IsValidResponse(byte[]? response)
    {
        if (response == null || response.Length < 4)
            return false;

        // A valid response typically echoes the command prefix
        // or has a specific acknowledgment pattern
        return response[0] == CommandPrefix || response[0] == 0x52; // 0x52 might be response prefix
    }

    /// <summary>
    /// Checks if the response indicates an error (device unavailable, etc.)
    /// </summary>
    public static bool IsErrorResponse(byte[]? response)
    {
        if (response == null || response.Length < 2)
            return true;

        // Check for common error patterns
        // HID++ error responses start with 0x8F
        if (response[0] == 0x8F)
            return true;

        // Check if response is all zeros (no device response)
        return response.All(b => b == 0);
    }

    /// <summary>
    /// Extracts any meaningful data from a response.
    /// </summary>
    public static string AnalyzeResponse(byte[] response)
    {
        if (response == null || response.Length == 0)
            return "No response";

        if (response.All(b => b == 0))
            return "Empty response (all zeros) - device may be offline";

        var nonZeroBytes = response.TakeWhile((b, i) => i < 16 || b != 0).ToArray();
        var hex = BitConverter.ToString(nonZeroBytes);

        // Try to identify known response patterns
        if (response[0] == 0x51)
            return $"Command echo: {hex} (likely ACK)";
        if (response[0] == 0x52)
            return $"Response: {hex}";
        if (response[0] == 0x8F)
            return $"Error response: {hex}";

        return $"Unknown pattern: {hex}";
    }

    /// <summary>
    /// Parses a battery query response.
    /// Response format: 51-08-00-03-04-[status]-[battery]-00-00-[charging]
    /// </summary>
    public static (bool Valid, byte Status, byte BatteryLevel, bool IsCharging) ParseBatteryResponse(byte[]? response)
    {
        if (response == null || response.Length < 10)
            return (false, 0, 0, false);

        // Check it's a battery response (starts with 51-08-00-03-04)
        if (response[0] != 0x51 || response[1] != 0x08 || response[4] != 0x04)
            return (false, 0, 0, false);

        byte status = response[5];      // 0x03 seems to be "connected"
        byte battery = response[6];      // Battery level (0x7A = 122 = maybe %)
        byte charging = response[9];     // 0x01 = charging?

        return (true, status, battery, charging == 0x01);
    }

    /// <summary>
    /// Parses a status query response to determine if headset is truly online.
    /// Response format: 51-05-00-[status]-03-00-04
    /// When status byte is 0xFF, device may be offline/disconnected.
    /// </summary>
    public static (bool Valid, bool IsOnline, byte StatusByte) ParseStatusResponse(byte[]? response)
    {
        if (response == null || response.Length < 7)
            return (false, false, 0);

        // Check it's a status response (starts with 51-05-00)
        if (response[0] != 0x51 || response[1] != 0x05)
            return (false, false, 0);

        byte statusByte = response[3];

        // 0xFF in status position might indicate "no device" / error
        // Need to verify this by comparing online vs offline responses
        bool isOnline = statusByte != 0xFF;

        return (true, isOnline, statusByte);
    }

    /// <summary>
    /// Detailed analysis of a response for debugging.
    /// </summary>
    public static void PrintDetailedAnalysis(byte[] response, string commandName)
    {
        if (response == null || response.Length < 6)
        {
            Console.WriteLine($"      [{commandName}] Invalid/no response");
            return;
        }

        Console.WriteLine($"      [{commandName}] Detailed Analysis:");
        Console.WriteLine($"        Byte 0 (Prefix):  0x{response[0]:X2}");
        Console.WriteLine($"        Byte 1 (Length?): 0x{response[1]:X2} ({response[1]})");
        Console.WriteLine($"        Byte 2:           0x{response[2]:X2}");
        Console.WriteLine($"        Byte 3 (Status?): 0x{response[3]:X2} {(response[3] == 0xFF ? "<-- FF might indicate OFFLINE" : "")}");
        Console.WriteLine($"        Byte 4:           0x{response[4]:X2}");
        Console.WriteLine($"        Byte 5:           0x{response[5]:X2}");

        if (response.Length > 6)
            Console.WriteLine($"        Byte 6 (Data?):   0x{response[6]:X2} ({response[6]})");
        if (response.Length > 9)
            Console.WriteLine($"        Byte 9:           0x{response[9]:X2}");
    }
}
