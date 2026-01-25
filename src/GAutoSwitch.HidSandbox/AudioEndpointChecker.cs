using System.Runtime.InteropServices;

namespace GAutoSwitch.HidSandbox;

/// <summary>
/// Checks the state of audio endpoints using Windows Core Audio API.
/// This provides an alternative detection method when HID access is blocked.
/// </summary>
public static class AudioEndpointChecker
{
    // COM interfaces for Windows Core Audio
    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        int GetDevice(string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount(out uint count);
        int Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, out IntPtr iface);
        int OpenPropertyStore(uint stgmAccess, out IPropertyStore properties);
        int GetId(out IntPtr id);
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint count);
        int GetAt(uint index, out PropertyKey key);
        int GetValue(ref PropertyKey key, out PropVariant value);
        int SetValue(ref PropertyKey key, ref PropVariant value);
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pwszVal;
    }

    private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    // Device states
    private const uint DEVICE_STATE_ACTIVE = 0x00000001;
    private const uint DEVICE_STATE_DISABLED = 0x00000002;
    private const uint DEVICE_STATE_NOTPRESENT = 0x00000004;
    private const uint DEVICE_STATE_UNPLUGGED = 0x00000008;
    private const uint DEVICE_STATEMASK_ALL = 0x0000000F;

    // Property keys
    private static readonly PropertyKey PKEY_Device_FriendlyName = new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 14
    };

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    public record EndpointInfo(string Name, EndpointState State);

    public enum EndpointState { Active, Disabled, NotPresent, Unplugged, Unknown }

    /// <summary>
    /// Gets the state of the PRO X 2 LIGHTSPEED audio endpoint.
    /// </summary>
    public static EndpointInfo? GetProX2EndpointState()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.EnumAudioEndpoints(EDataFlow.eRender, DEVICE_STATEMASK_ALL, out var devices);
            devices.GetCount(out uint count);

            for (uint i = 0; i < count; i++)
            {
                devices.Item(i, out var device);
                device.GetState(out uint state);

                string name = "(Unknown)";
                try
                {
                    device.OpenPropertyStore(0, out var props);
                    var key = PKEY_Device_FriendlyName;
                    props.GetValue(ref key, out var value);
                    if (value.pwszVal != IntPtr.Zero)
                    {
                        name = Marshal.PtrToStringUni(value.pwszVal) ?? "(Unknown)";
                    }
                }
                catch { }

                if (name.Contains("PRO X 2", StringComparison.OrdinalIgnoreCase))
                {
                    var endpointState = state switch
                    {
                        DEVICE_STATE_ACTIVE => EndpointState.Active,
                        DEVICE_STATE_DISABLED => EndpointState.Disabled,
                        DEVICE_STATE_NOTPRESENT => EndpointState.NotPresent,
                        DEVICE_STATE_UNPLUGGED => EndpointState.Unplugged,
                        _ => EndpointState.Unknown
                    };
                    return new EndpointInfo(name, endpointState);
                }
            }
        }
        catch { }

        return null;
    }

    public static void CheckEndpoints()
    {
        Console.WriteLine("\n[Audio Endpoint Check via Core Audio API]\n");

        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();

            // Check both playback AND capture (microphone) endpoints
            Console.WriteLine("=== PLAYBACK Endpoints ===\n");
            CheckEndpointType(enumerator, EDataFlow.eRender);

            Console.WriteLine("\n=== CAPTURE (Microphone) Endpoints ===\n");
            CheckEndpointType(enumerator, EDataFlow.eCapture);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    private static void CheckEndpointType(IMMDeviceEnumerator enumerator, EDataFlow dataFlow)
    {
        enumerator.EnumAudioEndpoints(dataFlow, DEVICE_STATEMASK_ALL, out var devices);
        devices.GetCount(out uint count);

        for (uint i = 0; i < count; i++)
        {
            devices.Item(i, out var device);
            device.GetState(out uint state);
            device.GetId(out IntPtr idPtr);
            string id = Marshal.PtrToStringUni(idPtr) ?? "";
            Marshal.FreeCoTaskMem(idPtr);

            string name = "(Unknown)";
            try
            {
                device.OpenPropertyStore(0, out var props);
                var key = PKEY_Device_FriendlyName;
                props.GetValue(ref key, out var value);
                if (value.pwszVal != IntPtr.Zero)
                {
                    name = Marshal.PtrToStringUni(value.pwszVal) ?? "(Unknown)";
                }
            }
            catch { }

            bool isGProX2 = name.Contains("PRO X 2", StringComparison.OrdinalIgnoreCase) ||
                            id.Contains("0AF7", StringComparison.OrdinalIgnoreCase);

            string stateStr = state switch
            {
                DEVICE_STATE_ACTIVE => "ACTIVE",
                DEVICE_STATE_DISABLED => "DISABLED",
                DEVICE_STATE_NOTPRESENT => "NOT PRESENT",
                DEVICE_STATE_UNPLUGGED => "UNPLUGGED",
                _ => $"UNKNOWN (0x{state:X})"
            };

            if (isGProX2)
            {
                Console.WriteLine($"  *** {name}");
                Console.WriteLine($"      State: {stateStr}");

                if (state == DEVICE_STATE_ACTIVE)
                {
                    Console.WriteLine("      --> ENDPOINT ACTIVE");
                }
                else if (state == DEVICE_STATE_NOTPRESENT || state == DEVICE_STATE_UNPLUGGED)
                {
                    Console.WriteLine("      --> ENDPOINT NOT PRESENT/UNPLUGGED");
                }
                Console.WriteLine();
            }
        }
    }
}
