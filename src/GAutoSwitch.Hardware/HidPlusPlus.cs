namespace GAutoSwitch.Hardware;

/// <summary>
/// HID++ 2.0 protocol helpers for Logitech devices.
/// Note: The G Pro X 2 uses the Logitech Audio Protocol (0x51) on interface 0xFFA0,
/// not standard HID++. This class is provided for reference and future compatibility.
/// </summary>
internal static class HidPlusPlus
{
    /// <summary>Report ID for short HID++ messages (7 bytes payload)</summary>
    public const byte ShortReportId = 0x10;

    /// <summary>Report ID for long HID++ messages (20 bytes payload)</summary>
    public const byte LongReportId = 0x11;

    /// <summary>Report ID for very long HID++ messages (64 bytes payload)</summary>
    public const byte VeryLongReportId = 0x12;

    /// <summary>Error response report ID</summary>
    public const byte ErrorReportId = 0x8F;

    /// <summary>Device index for the receiver itself</summary>
    public const byte ReceiverIndex = 0xFF;

    /// <summary>Device index for first paired device</summary>
    public const byte DeviceIndex1 = 0x01;

    /// <summary>
    /// Standard HID++ features
    /// </summary>
    public static class Features
    {
        public const ushort IRoot = 0x0000;
        public const ushort IFeatureSet = 0x0001;
        public const ushort IFirmwareInfo = 0x0003;
        public const ushort DeviceName = 0x0005;
        public const ushort BatteryUnified = 0x1000;
        public const ushort BatteryVoltage = 0x1001;
        public const ushort WirelessStatus = 0x1D4B;
    }

    /// <summary>
    /// HID++ error codes
    /// </summary>
    public static class Errors
    {
        public const byte Success = 0x00;
        public const byte DeviceUnavailable = 0x01;
        public const byte InvalidDeviceIndex = 0x02;
        public const byte Busy = 0x03;
        public const byte Unsupported = 0x04;
        public const byte InvalidArgument = 0x05;
        public const byte InvalidAddress = 0x06;
        public const byte RequestFailed = 0x07;
        public const byte UnknownDevice = 0x08;
        public const byte InvalidFeatureIndex = 0x09;
        public const byte InvalidFunctionId = 0x0A;
    }

    /// <summary>
    /// Creates a short HID++ message.
    /// Format: [ReportID][DeviceIndex][FeatureIndex][(Function << 4) | SoftwareId][Params...]
    /// </summary>
    public static byte[] CreateShortMessage(byte deviceIndex, byte featureIndex, byte function, byte softwareId, params byte[] parameters)
    {
        var message = new byte[7];
        message[0] = deviceIndex;
        message[1] = featureIndex;
        message[2] = (byte)((function << 4) | (softwareId & 0x0F));

        for (int i = 0; i < Math.Min(parameters.Length, 4); i++)
        {
            message[3 + i] = parameters[i];
        }

        return message;
    }

    /// <summary>
    /// Creates a long HID++ message.
    /// </summary>
    public static byte[] CreateLongMessage(byte deviceIndex, byte featureIndex, byte function, byte softwareId, params byte[] parameters)
    {
        var message = new byte[20];
        message[0] = deviceIndex;
        message[1] = featureIndex;
        message[2] = (byte)((function << 4) | (softwareId & 0x0F));

        for (int i = 0; i < Math.Min(parameters.Length, 17); i++)
        {
            message[3 + i] = parameters[i];
        }

        return message;
    }

    /// <summary>
    /// Creates a ping message to check if a device is online.
    /// </summary>
    public static byte[] CreatePingMessage(byte deviceIndex = DeviceIndex1, byte softwareId = 0x07)
    {
        // Ping uses IRoot (feature 0x00), function 0x0E
        return CreateShortMessage(deviceIndex, 0x00, 0x0E, softwareId);
    }

    /// <summary>
    /// Parses an error response.
    /// </summary>
    public static (bool IsError, byte ErrorCode, string ErrorMessage) ParseResponse(byte[]? response)
    {
        if (response == null || response.Length < 5)
        {
            return (true, 0xFF, "No response");
        }

        if (response[0] == ErrorReportId)
        {
            byte errorCode = response.Length > 4 ? response[4] : (byte)0xFF;
            string errorMessage = errorCode switch
            {
                Errors.Success => "Success",
                Errors.DeviceUnavailable => "Device unavailable/offline",
                Errors.InvalidDeviceIndex => "Invalid device index",
                Errors.Busy => "Device busy",
                Errors.Unsupported => "Unsupported operation",
                Errors.InvalidArgument => "Invalid argument",
                Errors.InvalidAddress => "Invalid address",
                Errors.RequestFailed => "Request failed",
                Errors.UnknownDevice => "Unknown device",
                Errors.InvalidFeatureIndex => "Invalid feature index",
                Errors.InvalidFunctionId => "Invalid function ID",
                _ => $"Unknown error (0x{errorCode:X2})"
            };
            return (true, errorCode, errorMessage);
        }

        return (false, 0, "OK");
    }

    /// <summary>
    /// Checks if a response indicates the device is online.
    /// </summary>
    public static bool IsDeviceOnline(byte[]? response)
    {
        if (response == null || response.Length < 4)
            return false;

        // Not an error response = device responded = online
        if (response[0] != ErrorReportId)
            return true;

        // Check if error is device unavailable
        var (isError, errorCode, _) = ParseResponse(response);
        return !isError || errorCode != Errors.DeviceUnavailable;
    }
}
