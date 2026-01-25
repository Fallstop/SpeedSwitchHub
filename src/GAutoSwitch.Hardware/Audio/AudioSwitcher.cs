using System.Diagnostics;
using System.Runtime.InteropServices;
using GAutoSwitch.Core.Interfaces;

namespace GAutoSwitch.Hardware.Audio;

/// <summary>
/// Implementation of IAudioSwitcher that uses undocumented Windows COM interfaces
/// to change the default audio device and migrate active audio sessions.
/// </summary>
public sealed class AudioSwitcher : IAudioSwitcher
{
    private readonly AudioPolicyConfigInterop.IPolicyConfigWin10? _policyConfigWin10;
    private readonly AudioPolicyConfigInterop.IPolicyConfig? _policyConfig;
    private readonly AudioPolicyConfigInterop.IPolicyConfigVista? _policyConfigVista;
    private readonly bool _isSupported;

    public AudioSwitcher()
    {
        // Try Win10 interface first (supports per-process routing), then fall back to legacy interfaces
        _policyConfigWin10 = AudioPolicyConfigInterop.CreatePolicyConfigWin10();
        _policyConfig = AudioPolicyConfigInterop.CreatePolicyConfig();

        if (_policyConfigWin10 != null || _policyConfig != null)
        {
            _isSupported = true;
        }
        else
        {
            _policyConfigVista = AudioPolicyConfigInterop.CreatePolicyConfigVista();
            _isSupported = _policyConfigVista != null;
        }
    }

    /// <inheritdoc />
    public bool IsSupported => _isSupported;

    /// <inheritdoc />
    public bool SetDefaultDevice(string deviceId, EDataFlow flow = EDataFlow.Render)
    {
        if (!_isSupported || string.IsNullOrEmpty(deviceId))
            return false;

        try
        {
            // The device ID from WinRT enumeration needs to be converted to MMDevice format
            string mmDeviceId = ConvertToMMDeviceId(deviceId);
            Debug.WriteLine($"[AudioSwitcher] SetDefaultDevice: flow={flow}, original={deviceId}");
            Debug.WriteLine($"[AudioSwitcher] SetDefaultDevice: converted={mmDeviceId}");

            // Set default endpoint for all roles
            bool success = true;
            foreach (ERole role in Enum.GetValues<ERole>())
            {
                int hr;
                if (_policyConfig != null)
                {
                    hr = _policyConfig.SetDefaultEndpoint(mmDeviceId, role);
                }
                else if (_policyConfigVista != null)
                {
                    hr = _policyConfigVista.SetDefaultEndpoint(mmDeviceId, role);
                }
                else
                {
                    return false;
                }

                if (hr != 0)
                {
                    Debug.WriteLine($"SetDefaultEndpoint failed for role {role}: HRESULT 0x{hr:X8}");
                    success = false;
                }
            }

            return success;
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"COM exception in SetDefaultDevice: 0x{ex.HResult:X8}");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Exception in SetDefaultDevice: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc />
    public int MigrateActiveSessions(string toDeviceId, EDataFlow flow = EDataFlow.Render)
    {
        if (!_isSupported || string.IsNullOrEmpty(toDeviceId))
            return 0;

        if (_policyConfigWin10 == null)
        {
            Debug.WriteLine("[AudioSwitcher] Win10 interface not available, cannot migrate sessions");
            return 0;
        }

        try
        {
            // Get all active session process IDs
            var processIds = AudioSessionInterop.GetActiveSessionProcessIds(flow);
            if (processIds.Count == 0)
                return 0;

            string mmDeviceId = ConvertToMMDeviceId(toDeviceId);
            int migratedCount = 0;

            // Use SetPersistedDefaultAudioEndpoint to migrate each process to the new device
            foreach (var pid in processIds)
            {
                try
                {
                    var process = Process.GetProcessById((int)pid);
                    Debug.WriteLine($"[AudioSwitcher] Migrating PID {pid} ({process.ProcessName})");

                    // Set for all roles (Console=0, Multimedia=1, Communications=2)
                    for (int role = 0; role <= 2; role++)
                    {
                        int hr = _policyConfigWin10.SetPersistedDefaultAudioEndpoint(
                            pid,
                            (int)flow,
                            role,
                            mmDeviceId);

                        if (hr != 0)
                            Debug.WriteLine($"[AudioSwitcher] Role {role} failed: 0x{hr:X8}");
                    }
                    migratedCount++;
                }
                catch (ArgumentException)
                {
                    // Process no longer exists
                    Debug.WriteLine($"[AudioSwitcher] PID {pid} no longer exists");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AudioSwitcher] Failed to migrate PID {pid}: {ex.Message}");
                }
            }

            Debug.WriteLine($"[AudioSwitcher] Migrated {migratedCount} session(s)");
            return migratedCount;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Exception in MigrateActiveSessions: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Converts a WinRT device ID to MMDevice ID format.
    /// WinRT IDs look like: \\?\SWD#MMDEVAPI#{0.0.0.00000000}.{guid}#...
    /// MMDevice IDs look like: {0.0.0.00000000}.{guid}
    /// </summary>
    private static string ConvertToMMDeviceId(string deviceId)
    {
        Debug.WriteLine($"[AudioSwitcher] ConvertToMMDeviceId input: {deviceId}");

        // If it's already in MMDevice format, return as-is
        if (deviceId.StartsWith("{"))
        {
            Debug.WriteLine($"[AudioSwitcher] Already in MMDevice format");
            return deviceId;
        }

        // Extract the MMDevice ID from the WinRT format
        // Look for any pattern like {digit.digit.digit.digits}.{guid}
        // Render devices: {0.0.0.00000000}.{guid}
        // Capture devices: {0.0.1.00000000}.{guid} or similar
        int startIndex = -1;

        // Find any opening brace followed by a digit
        for (int i = 0; i < deviceId.Length - 1; i++)
        {
            if (deviceId[i] == '{' && char.IsDigit(deviceId[i + 1]))
            {
                startIndex = i;
                break;
            }
        }

        if (startIndex >= 0)
        {
            // Find the end of the device ID (next # or end of string)
            int endIndex = deviceId.IndexOf('#', startIndex);
            if (endIndex < 0)
                endIndex = deviceId.Length;

            string result = deviceId.Substring(startIndex, endIndex - startIndex);
            Debug.WriteLine($"[AudioSwitcher] Converted to: {result}");
            return result;
        }

        // Return the original ID if we can't parse it
        Debug.WriteLine($"[AudioSwitcher] Could not convert, returning original");
        return deviceId;
    }
}
