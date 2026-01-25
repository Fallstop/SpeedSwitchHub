using System.Runtime.InteropServices;
using GAutoSwitch.Core.Interfaces;

namespace GAutoSwitch.Hardware.Audio;

/// <summary>
/// COM interop definitions for the undocumented IPolicyConfig interface.
/// This interface allows setting the default audio endpoint programmatically.
/// </summary>
internal static class AudioPolicyConfigInterop
{
    // IPolicyConfig interface - used to set default audio endpoint
    // This is an undocumented Windows interface that works on Windows 7+
    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig]
        int GetMixFormat(string deviceId, IntPtr format);

        [PreserveSig]
        int GetDeviceFormat(string deviceId, int @default, IntPtr format);

        [PreserveSig]
        int ResetDeviceFormat(string deviceId);

        [PreserveSig]
        int SetDeviceFormat(string deviceId, IntPtr endpointFormat, IntPtr mixFormat);

        [PreserveSig]
        int GetProcessingPeriod(string deviceId, int @default, IntPtr defaultPeriod, IntPtr minimumPeriod);

        [PreserveSig]
        int SetProcessingPeriod(string deviceId, IntPtr period);

        [PreserveSig]
        int GetShareMode(string deviceId, IntPtr mode);

        [PreserveSig]
        int SetShareMode(string deviceId, IntPtr mode);

        [PreserveSig]
        int GetPropertyValue(string deviceId, ref PropertyKey key, IntPtr value);

        [PreserveSig]
        int SetPropertyValue(string deviceId, ref PropertyKey key, IntPtr value);

        [PreserveSig]
        int SetDefaultEndpoint(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            ERole role);

        [PreserveSig]
        int SetEndpointVisibility(string deviceId, int visible);
    }

    // IPolicyConfigVista - older interface for Vista/Win7, fallback if IPolicyConfig fails
    [ComImport]
    [Guid("568B9108-44BF-40B4-9006-86AFE5B5A620")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfigVista
    {
        [PreserveSig]
        int GetMixFormat(string deviceId, IntPtr format);

        [PreserveSig]
        int GetDeviceFormat(string deviceId, int @default, IntPtr format);

        [PreserveSig]
        int SetDeviceFormat(string deviceId, IntPtr endpointFormat, IntPtr mixFormat);

        [PreserveSig]
        int GetProcessingPeriod(string deviceId, int @default, IntPtr defaultPeriod, IntPtr minimumPeriod);

        [PreserveSig]
        int SetProcessingPeriod(string deviceId, IntPtr period);

        [PreserveSig]
        int GetShareMode(string deviceId, IntPtr mode);

        [PreserveSig]
        int SetShareMode(string deviceId, IntPtr mode);

        [PreserveSig]
        int GetPropertyValue(string deviceId, ref PropertyKey key, IntPtr value);

        [PreserveSig]
        int SetPropertyValue(string deviceId, ref PropertyKey key, IntPtr value);

        [PreserveSig]
        int SetDefaultEndpoint(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            ERole role);

        [PreserveSig]
        int SetEndpointVisibility(string deviceId, int visible);
    }

    // PolicyConfigClient - the COM class that implements IPolicyConfig
    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    internal class PolicyConfigClient { }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    /// <summary>
    /// Creates an instance of the IPolicyConfig interface.
    /// Returns null if the interface is not available on this system.
    /// </summary>
    public static IPolicyConfig? CreatePolicyConfig()
    {
        try
        {
            return (IPolicyConfig)new PolicyConfigClient();
        }
        catch (COMException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Creates an instance of the IPolicyConfigVista interface (fallback).
    /// Returns null if the interface is not available on this system.
    /// </summary>
    public static IPolicyConfigVista? CreatePolicyConfigVista()
    {
        try
        {
            return (IPolicyConfigVista)new PolicyConfigClient();
        }
        catch (COMException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
    }
}
