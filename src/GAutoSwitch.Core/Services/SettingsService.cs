using System.Diagnostics;
using System.Text.Json;
using GAutoSwitch.Core.Interfaces;
using GAutoSwitch.Core.Models;

namespace GAutoSwitch.Core.Services;

/// <summary>
/// Persists application settings to a JSON file in AppData.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public AppSettings Settings { get; private set; } = new();

    public SettingsService()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appDataPath, "GAutoSwitch");
        Directory.CreateDirectory(appFolder);
        _settingsPath = Path.Combine(appFolder, "settings.json");
    }

    public async Task LoadAsync()
    {
        Debug.WriteLine($"[SettingsService] Loading settings from: {_settingsPath}");

        if (!File.Exists(_settingsPath))
        {
            Debug.WriteLine("[SettingsService] Settings file not found, using defaults");
            Settings = new AppSettings();
            return;
        }

        try
        {
            await using var stream = File.OpenRead(_settingsPath);
            Settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions)
                       ?? new AppSettings();

            Debug.WriteLine($"[SettingsService] Loaded - WirelessId: {Settings.WirelessDeviceId ?? "(null)"}");
            Debug.WriteLine($"[SettingsService] Loaded - WiredId: {Settings.WiredDeviceId ?? "(null)"}");
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[SettingsService] JSON parse error: {ex.Message}");
            Settings = new AppSettings();
        }
    }

    public async Task SaveAsync()
    {
        await using var stream = File.Create(_settingsPath);
        await JsonSerializer.SerializeAsync(stream, Settings, JsonOptions);
    }
}
