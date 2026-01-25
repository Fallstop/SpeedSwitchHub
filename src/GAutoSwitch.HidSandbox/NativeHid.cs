using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GAutoSwitch.HidSandbox;

/// <summary>
/// Native Windows HID API for direct device communication.
/// This bypasses HidLibrary for more direct control over the HID device.
/// </summary>
public static class NativeHid
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetOutputReport(
        SafeFileHandle HidDeviceObject,
        byte[] lpReportBuffer,
        uint ReportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetInputReport(
        SafeFileHandle HidDeviceObject,
        byte[] lpReportBuffer,
        uint ReportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(
        SafeFileHandle HidDeviceObject,
        byte[] lpReportBuffer,
        uint ReportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(
        SafeFileHandle HidDeviceObject,
        byte[] lpReportBuffer,
        uint ReportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetProductString(
        SafeFileHandle HidDeviceObject,
        byte[] Buffer,
        uint BufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetManufacturerString(
        SafeFileHandle HidDeviceObject,
        byte[] Buffer,
        uint BufferLength);

    public static SafeFileHandle? OpenDevice(string devicePath, bool writeAccess = true)
    {
        uint access = GENERIC_READ;
        if (writeAccess)
            access |= GENERIC_WRITE;

        var handle = CreateFile(
            devicePath,
            access,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0, // No overlapped for simplicity
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            Console.WriteLine($"    CreateFile failed with error: {error} (0x{error:X})");
            return null;
        }

        return handle;
    }

    public static string? GetProductString(SafeFileHandle handle)
    {
        var buffer = new byte[256];
        if (HidD_GetProductString(handle, buffer, (uint)buffer.Length))
        {
            return System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        }
        return null;
    }

    public static string? GetManufacturerString(SafeFileHandle handle)
    {
        var buffer = new byte[256];
        if (HidD_GetManufacturerString(handle, buffer, (uint)buffer.Length))
        {
            return System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        }
        return null;
    }

    public static bool WriteOutputReport(SafeFileHandle handle, byte[] data)
    {
        // HidD_SetOutputReport is synchronous and often works even when WriteFile fails
        bool result = HidD_SetOutputReport(handle, data, (uint)data.Length);
        if (!result)
        {
            int error = Marshal.GetLastWin32Error();
            Console.WriteLine($"    HidD_SetOutputReport failed: error {error} (0x{error:X})");
        }
        return result;
    }

    public static byte[]? GetInputReport(SafeFileHandle handle, int reportSize)
    {
        var buffer = new byte[reportSize];
        // Report ID goes in first byte (0 means report ID 0 or none)
        buffer[0] = 0x00;

        if (HidD_GetInputReport(handle, buffer, (uint)buffer.Length))
        {
            return buffer;
        }

        int error = Marshal.GetLastWin32Error();
        Console.WriteLine($"    HidD_GetInputReport failed: error {error} (0x{error:X})");
        return null;
    }

    public static byte[]? GetFeatureReport(SafeFileHandle handle, byte reportId, int reportSize)
    {
        var buffer = new byte[reportSize];
        buffer[0] = reportId;

        if (HidD_GetFeature(handle, buffer, (uint)buffer.Length))
        {
            return buffer;
        }

        int error = Marshal.GetLastWin32Error();
        if (error != 1) // Don't spam for "incorrect function" (no feature reports)
        {
            Console.WriteLine($"    HidD_GetFeature(0x{reportId:X2}) failed: error {error}");
        }
        return null;
    }

    public static bool SetFeatureReport(SafeFileHandle handle, byte[] data)
    {
        bool result = HidD_SetFeature(handle, data, (uint)data.Length);
        if (!result)
        {
            int error = Marshal.GetLastWin32Error();
            Console.WriteLine($"    HidD_SetFeature failed: error {error}");
        }
        return result;
    }

    public static bool WriteFile(SafeFileHandle handle, byte[] data)
    {
        bool result = WriteFile(handle, data, (uint)data.Length, out uint written, IntPtr.Zero);
        if (!result)
        {
            int error = Marshal.GetLastWin32Error();
            Console.WriteLine($"    WriteFile failed: error {error} (written: {written})");
        }
        return result;
    }

    public static byte[]? ReadFile(SafeFileHandle handle, int size, int timeoutMs = 100)
    {
        var buffer = new byte[size];

        // For non-overlapped reads, we need to be careful about blocking
        // Use a task with timeout
        var readTask = Task.Run(() =>
        {
            bool result = ReadFile(handle, buffer, (uint)buffer.Length, out uint bytesRead, IntPtr.Zero);
            if (result && bytesRead > 0)
                return buffer;
            return null;
        });

        if (readTask.Wait(timeoutMs))
        {
            return readTask.Result;
        }

        return null;
    }
}
