namespace GAutoSwitch.Hardware;

/// <summary>
/// Logitech Audio Protocol helpers for G Pro X 2 Lightspeed.
/// Based on reverse-engineering from HeadsetControl project.
///
/// The G Pro X 2 uses a proprietary protocol on the 0xFFA0 interface:
/// - Command prefix: 0x51
/// - Format: [0x51][Length][0x00][0x03][Params...]
/// </summary>
internal static class LogitechAudioProtocol
{
    /// <summary>Report size for 0xFFA0 interface (64 bytes)</summary>
    public const int ExtendedReportSize = 64;

    /// <summary>Report size for 0xFF13 interface (62 bytes)</summary>
    public const int AudioReportSize = 62;

    /// <summary>Command prefix for Logitech Audio Protocol</summary>
    public const byte CommandPrefix = 0x51;

    /// <summary>Usage page for the extended protocol interface</summary>
    public const ushort ExtendedUsagePage = 0xFFA0;

    /// <summary>Usage page for the audio control interface</summary>
    public const ushort AudioUsagePage = 0xFF13;

    /// <summary>
    /// Known commands discovered from HeadsetControl Issue #314 and testing.
    /// </summary>
    public static class Commands
    {
        /// <summary>
        /// Connection check query (0x1a) - byte 3 of response is 0xFF when offline, 0x03 when online.
        /// </summary>
        public static readonly byte[] ConnectionCheck = { 0x51, 0x06, 0x00, 0x03, 0x1a, 0x00, 0x01, 0x00 };

        /// <summary>
        /// Battery/power status query - byte 7 of response is 0x00 when offline, 0x01 when online.
        /// </summary>
        public static readonly byte[] BatteryQuery = { 0x51, 0x05, 0x00, 0x03, 0x04, 0x00, 0x00 };
    }

    /// <summary>
    /// Creates a command buffer with proper padding for the interface report size.
    /// </summary>
    public static byte[] CreateCommand(byte[] command, int reportSize)
    {
        var buffer = new byte[reportSize];
        Array.Copy(command, 0, buffer, 0, Math.Min(command.Length, buffer.Length));
        return buffer;
    }

    /// <summary>
    /// Checks if the response indicates the device is responsive (online).
    /// Based on testing:
    /// - For ConnectionCheck (0x1a): byte 3 is 0xFF when offline, 0x03 when online
    /// - For BatteryQuery: byte 7 is 0x00 when offline, 0x01 when online (most reliable)
    /// </summary>
    public static bool IsDeviceOnline(byte[]? response)
    {
        if (response == null || response.Length < 4)
            return false;

        // Check for 0x51 response prefix
        if (response[0] != CommandPrefix)
            return false;

        // For battery response (length byte = 0x08), check byte 8 (connected indicator)
        if (response[1] == 0x08 && response.Length >= 9)
        {
            return response[8] == 0x01;  // 0x01 = connected, 0x00 = disconnected
        }

        // For connection check response, check byte 3
        // 0xFF = device not present / offline
        // 0x03 = device online
        byte statusByte = response[3];
        return statusByte != 0xFF;
    }

    /// <summary>
    /// Checks if the response indicates an error (device unavailable).
    /// </summary>
    public static bool IsErrorResponse(byte[]? response)
    {
        if (response == null || response.Length < 2)
            return true;

        // HID++ error responses start with 0x8F
        if (response[0] == 0x8F)
            return true;

        // All zeros typically means no response
        if (response.All(b => b == 0))
            return true;

        // 0xFF in status byte position indicates offline
        if (response.Length >= 4 && response[0] == CommandPrefix && response[3] == 0xFF)
            return true;

        return false;
    }
}
