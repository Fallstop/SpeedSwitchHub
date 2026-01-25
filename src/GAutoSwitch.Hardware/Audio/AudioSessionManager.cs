using System.Runtime.InteropServices;
using GAutoSwitch.Core.Interfaces;

namespace GAutoSwitch.Hardware.Audio;

/// <summary>
/// COM interop definitions for audio session management.
/// Used to enumerate active audio sessions and get their process IDs.
/// </summary>
public static class AudioSessionInterop
{
    // IMMDeviceEnumerator - enumerates audio endpoints
    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(
            EDataFlow dataFlow,
            uint stateMask,
            out IMMDeviceCollection devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(
            EDataFlow dataFlow,
            ERole role,
            out IMMDevice device);

        [PreserveSig]
        int GetDevice(
            [MarshalAs(UnmanagedType.LPWStr)] string id,
            out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    // IMMDeviceCollection - collection of audio devices
    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice device);
    }

    // IMMDevice - represents a single audio endpoint
    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid iid,
            uint clsCtx,
            IntPtr activationParams,
            out IntPtr iface);

        [PreserveSig]
        int OpenPropertyStore(uint stgmAccess, out IntPtr properties);

        [PreserveSig]
        int GetId(out IntPtr id);

        [PreserveSig]
        int GetState(out uint state);
    }

    // IAudioSessionManager2 - manages audio sessions
    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        // IAudioSessionManager methods
        [PreserveSig]
        int GetAudioSessionControl(IntPtr audioSessionGuid, uint flags, out IntPtr sessionControl);

        [PreserveSig]
        int GetSimpleAudioVolume(IntPtr audioSessionGuid, uint flags, out IntPtr simpleAudioVolume);

        // IAudioSessionManager2 methods
        [PreserveSig]
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);

        [PreserveSig]
        int RegisterSessionNotification(IntPtr sessionNotification);

        [PreserveSig]
        int UnregisterSessionNotification(IntPtr sessionNotification);

        [PreserveSig]
        int RegisterDuckNotification(
            [MarshalAs(UnmanagedType.LPWStr)] string sessionId,
            IntPtr duckNotification);

        [PreserveSig]
        int UnregisterDuckNotification(IntPtr duckNotification);
    }

    // IAudioSessionEnumerator - enumerates audio sessions
    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        [PreserveSig]
        int GetCount(out int count);

        [PreserveSig]
        int GetSession(int index, out IAudioSessionControl session);
    }

    // IAudioSessionControl - base interface for session control
    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl
    {
        [PreserveSig]
        int QueryInterface(ref Guid riid, out IntPtr ppvObject);

        [PreserveSig]
        int GetState(out AudioSessionState state);

        [PreserveSig]
        int GetDisplayName(out IntPtr displayName);

        [PreserveSig]
        int SetDisplayName(
            [MarshalAs(UnmanagedType.LPWStr)] string displayName,
            ref Guid eventContext);

        [PreserveSig]
        int GetIconPath(out IntPtr iconPath);

        [PreserveSig]
        int SetIconPath(
            [MarshalAs(UnmanagedType.LPWStr)] string iconPath,
            ref Guid eventContext);

        [PreserveSig]
        int GetGroupingParam(out Guid groupingParam);

        [PreserveSig]
        int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);

        [PreserveSig]
        int RegisterAudioSessionNotification(IntPtr notification);

        [PreserveSig]
        int UnregisterAudioSessionNotification(IntPtr notification);
    }

    // IAudioSessionControl2 - extended interface with process ID
    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        // IAudioSessionControl methods
        [PreserveSig]
        int GetState(out AudioSessionState state);

        [PreserveSig]
        int GetDisplayName(out IntPtr displayName);

        [PreserveSig]
        int SetDisplayName(
            [MarshalAs(UnmanagedType.LPWStr)] string displayName,
            ref Guid eventContext);

        [PreserveSig]
        int GetIconPath(out IntPtr iconPath);

        [PreserveSig]
        int SetIconPath(
            [MarshalAs(UnmanagedType.LPWStr)] string iconPath,
            ref Guid eventContext);

        [PreserveSig]
        int GetGroupingParam(out Guid groupingParam);

        [PreserveSig]
        int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);

        [PreserveSig]
        int RegisterAudioSessionNotification(IntPtr notification);

        [PreserveSig]
        int UnregisterAudioSessionNotification(IntPtr notification);

        // IAudioSessionControl2 methods
        [PreserveSig]
        int GetSessionIdentifier(out IntPtr sessionId);

        [PreserveSig]
        int GetSessionInstanceIdentifier(out IntPtr sessionInstanceId);

        [PreserveSig]
        int GetProcessId(out uint processId);

        [PreserveSig]
        int IsSystemSoundsSession();

        [PreserveSig]
        int SetDuckingPreference(int optOut);
    }

    internal enum AudioSessionState
    {
        Inactive = 0,
        Active = 1,
        Expired = 2
    }

    // MMDeviceEnumerator CoClass
    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator { }

    // GUIDs for interface activation
    internal static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    private const uint CLSCTX_ALL = 0x17;
    private const uint DEVICE_STATE_ACTIVE = 0x00000001;

    /// <summary>
    /// Gets the MMDevice ID of the default audio endpoint.
    /// </summary>
    public static string? GetDefaultDeviceId(EDataFlow flow = EDataFlow.Render, ERole role = ERole.Multimedia)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            int hr = enumerator.GetDefaultAudioEndpoint(flow, role, out var device);
            if (hr != 0 || device == null)
                return null;

            hr = device.GetId(out var idPtr);
            if (hr != 0 || idPtr == IntPtr.Zero)
                return null;

            string id = Marshal.PtrToStringUni(idPtr) ?? string.Empty;
            Marshal.FreeCoTaskMem(idPtr);
            return id;
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the process IDs of all active audio sessions for the specified data flow.
    /// </summary>
    public static List<uint> GetActiveSessionProcessIds(EDataFlow flow = EDataFlow.Render)
    {
        var processIds = new List<uint>();

        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();

            int hr = enumerator.EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, out var devices);
            if (hr != 0) return processIds;

            hr = devices.GetCount(out uint deviceCount);
            if (hr != 0) return processIds;

            for (uint i = 0; i < deviceCount; i++)
            {
                hr = devices.Item(i, out var device);
                if (hr != 0) continue;

                var sessionPids = GetSessionProcessIdsForDevice(device);
                processIds.AddRange(sessionPids);
            }
        }
        catch (COMException)
        {
            // COM error - return what we have
        }

        return processIds.Distinct().ToList();
    }

    private static List<uint> GetSessionProcessIdsForDevice(IMMDevice device)
    {
        var processIds = new List<uint>();

        try
        {
            var iidSessionManager = IID_IAudioSessionManager2;
            int hr = device.Activate(ref iidSessionManager, CLSCTX_ALL, IntPtr.Zero, out var sessionManagerPtr);
            if (hr != 0 || sessionManagerPtr == IntPtr.Zero) return processIds;

            var sessionManager = (IAudioSessionManager2)Marshal.GetObjectForIUnknown(sessionManagerPtr);
            Marshal.Release(sessionManagerPtr);

            hr = sessionManager.GetSessionEnumerator(out var sessionEnumerator);
            if (hr != 0) return processIds;

            hr = sessionEnumerator.GetCount(out int sessionCount);
            if (hr != 0) return processIds;

            for (int j = 0; j < sessionCount; j++)
            {
                hr = sessionEnumerator.GetSession(j, out var sessionControl);
                if (hr != 0) continue;

                // Query for IAudioSessionControl2
                var iid = new Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D");
                hr = sessionControl.QueryInterface(ref iid, out var sessionControl2Ptr);
                if (hr != 0 || sessionControl2Ptr == IntPtr.Zero) continue;

                var sessionControl2 = (IAudioSessionControl2)Marshal.GetObjectForIUnknown(sessionControl2Ptr);
                Marshal.Release(sessionControl2Ptr);

                hr = sessionControl2.GetState(out var state);
                if (hr != 0) continue;

                // Only include active sessions
                if (state == AudioSessionState.Active)
                {
                    hr = sessionControl2.GetProcessId(out uint pid);
                    if (hr == 0 && pid != 0)
                    {
                        processIds.Add(pid);
                    }
                }
            }
        }
        catch (COMException)
        {
            // COM error - return what we have
        }

        return processIds;
    }
}
