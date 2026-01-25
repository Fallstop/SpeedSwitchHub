using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GAutoSwitch.Hardware;

/// <summary>
/// Native Windows HID API for direct device communication.
/// </summary>
internal static class NativeHid
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

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
    private static extern bool ReadFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetOutputReport(
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

    public static SafeFileHandle? OpenDevice(string devicePath, bool writeAccess = false)
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
            0,
            IntPtr.Zero);

        return handle.IsInvalid ? null : handle;
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

    public static byte[]? ReadFile(SafeFileHandle handle, int size, int timeoutMs = 100)
    {
        var buffer = new byte[size];

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

    /// <summary>
    /// Writes an output report to the HID device using HidD_SetOutputReport.
    /// </summary>
    public static bool WriteOutputReport(SafeFileHandle handle, byte[] data)
    {
        return HidD_SetOutputReport(handle, data, (uint)data.Length);
    }

    /// <summary>
    /// Writes data directly to the HID device using WriteFile.
    /// </summary>
    public static bool WriteFileDirect(SafeFileHandle handle, byte[] data)
    {
        return WriteFile(handle, data, (uint)data.Length, out _, IntPtr.Zero);
    }
}
