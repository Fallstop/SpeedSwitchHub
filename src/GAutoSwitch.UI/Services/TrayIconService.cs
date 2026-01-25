using CommunityToolkit.Mvvm.Input;
using GAutoSwitch.Core.Interfaces;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GAutoSwitch.UI.Services;

/// <summary>
/// Represents the visual state of the tray icon.
/// </summary>
public enum TrayIconState
{
    Wireless,
    Wired,
    Error,
    Unknown
}

/// <summary>
/// Manages the system tray icon and its interactions.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly IAutoSwitchService _autoSwitchService;
    private readonly ISettingsService _settingsService;
    private readonly IAudioDeviceService _audioDeviceService;

    private TaskbarIcon? _taskbarIcon;
    private MenuFlyoutItem? _toggleAutoSwitchItem;
    private MenuFlyoutSubItem? _debugSubMenu;
    private DispatcherQueue? _dispatcherQueue;
    private bool _disposed;

    // Preloaded icons for each state
    private BitmapImage? _iconWireless;
    private BitmapImage? _iconWired;
    private BitmapImage? _iconError;
    private BitmapImage? _iconUnknown;

    public event EventHandler? ShowWindowRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService(IAutoSwitchService autoSwitchService, ISettingsService settingsService, IAudioDeviceService audioDeviceService)
    {
        _autoSwitchService = autoSwitchService;
        _settingsService = settingsService;
        _audioDeviceService = audioDeviceService;
    }

    public void Initialize(Window window)
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        // Preload all icon variants
        PreloadIcons();

        _taskbarIcon = new TaskbarIcon
        {
            ToolTipText = GetTooltipText(),
            IconSource = GetIconForCurrentState(),
            LeftClickCommand = new RelayCommand(OnShowWindow),
            DoubleClickCommand = new RelayCommand(OnShowWindow),
            NoLeftClickDelay = true,
            ContextMenuMode = ContextMenuMode.SecondWindow
        };

        // Set up context menu
        var menuFlyout = new MenuFlyout
        {
            AreOpenCloseAnimationsEnabled = false
        };

        // Add toggle auto-switch item at top
        _toggleAutoSwitchItem = new MenuFlyoutItem
        {
            Text = _autoSwitchService.IsEnabled ? "Disable Auto-Switch" : "Enable Auto-Switch",
            Command = new RelayCommand(OnToggleAutoSwitch)
        };
        menuFlyout.Items.Add(_toggleAutoSwitchItem);

        // Add Force Apply item
        var forceApplyItem = new MenuFlyoutItem
        {
            Text = "Force Apply",
            Command = new RelayCommand(OnForceApply)
        };
        menuFlyout.Items.Add(forceApplyItem);

        menuFlyout.Items.Add(new MenuFlyoutSeparator());

        // Add debug submenu
        _debugSubMenu = new MenuFlyoutSubItem
        {
            Text = "Debug Info"
        };
        UpdateDebugSubMenu();
        menuFlyout.Items.Add(_debugSubMenu);

        menuFlyout.Items.Add(new MenuFlyoutSeparator());

        var showItem = new MenuFlyoutItem
        {
            Text = "Open Settings",
            Command = new RelayCommand(OnShowWindow)
        };
        menuFlyout.Items.Add(showItem);

        menuFlyout.Items.Add(new MenuFlyoutSeparator());

        var exitItem = new MenuFlyoutItem
        {
            Text = "Exit",
            Command = new RelayCommand(OnExit)
        };
        menuFlyout.Items.Add(exitItem);

        _taskbarIcon.ContextFlyout = menuFlyout;

        // Subscribe to state change events
        _autoSwitchService.StateChanged += OnAutoSwitchStateChanged;
        _autoSwitchService.IsEnabledChanged += OnIsEnabledChanged;
        _autoSwitchService.ConfiguredDeviceStateChanged += OnConfiguredDeviceStateChanged;

        // Force creation of the tray icon
        _taskbarIcon.ForceCreate();
    }

    private void PreloadIcons()
    {
        // Preload icons - using app.ico as fallback for now until proper icons are created
        // These will be replaced with proper state-specific icons
        try
        {
            _iconWireless = new BitmapImage(new Uri("ms-appx:///Assets/TrayIcons/tray-wireless.ico"));
        }
        catch
        {
            _iconWireless = new BitmapImage(new Uri("ms-appx:///Assets/app.ico"));
        }

        try
        {
            _iconWired = new BitmapImage(new Uri("ms-appx:///Assets/TrayIcons/tray-wired.ico"));
        }
        catch
        {
            _iconWired = new BitmapImage(new Uri("ms-appx:///Assets/app.ico"));
        }

        try
        {
            _iconError = new BitmapImage(new Uri("ms-appx:///Assets/TrayIcons/tray-error.ico"));
        }
        catch
        {
            _iconError = new BitmapImage(new Uri("ms-appx:///Assets/app.ico"));
        }

        try
        {
            _iconUnknown = new BitmapImage(new Uri("ms-appx:///Assets/TrayIcons/tray-unknown.ico"));
        }
        catch
        {
            _iconUnknown = new BitmapImage(new Uri("ms-appx:///Assets/app.ico"));
        }
    }

    private void OnAutoSwitchStateChanged(object? sender, AutoSwitchStateChangedEventArgs e)
    {
        UpdateTrayIcon();
    }

    private void OnIsEnabledChanged(object? sender, bool e)
    {
        UpdateTrayIcon();
        UpdateToggleMenuItemText();
    }

    private void OnConfiguredDeviceStateChanged(object? sender, bool e)
    {
        UpdateTrayIcon();
    }

    private void UpdateTrayIcon()
    {
        if (_taskbarIcon == null) return;

        _dispatcherQueue?.TryEnqueue(() =>
        {
            if (_taskbarIcon == null) return;

            _taskbarIcon.IconSource = GetIconForCurrentState();
            _taskbarIcon.ToolTipText = GetTooltipText();
            UpdateDebugSubMenu();
        });
    }

    private void UpdateToggleMenuItemText()
    {
        if (_toggleAutoSwitchItem == null) return;

        _dispatcherQueue?.TryEnqueue(() =>
        {
            if (_toggleAutoSwitchItem == null) return;

            _toggleAutoSwitchItem.Text = _autoSwitchService.IsEnabled
                ? "Disable Auto-Switch"
                : "Enable Auto-Switch";
        });
    }

    private TrayIconState GetCurrentIconState()
    {
        return _autoSwitchService.CurrentState switch
        {
            AutoSwitchState.WirelessConnected => TrayIconState.Wireless,
            AutoSwitchState.WirelessDisconnected => TrayIconState.Wired,
            AutoSwitchState.DongleNotFound => TrayIconState.Error,
            AutoSwitchState.Stopped => TrayIconState.Unknown,
            AutoSwitchState.Unknown => TrayIconState.Unknown,
            AutoSwitchState.Transitioning => TrayIconState.Unknown,
            _ => TrayIconState.Unknown
        };
    }

    private BitmapImage GetIconForCurrentState()
    {
        var state = GetCurrentIconState();
        return state switch
        {
            TrayIconState.Wireless => _iconWireless!,
            TrayIconState.Wired => _iconWired!,
            TrayIconState.Error => _iconError!,
            TrayIconState.Unknown => _iconUnknown!,
            _ => _iconUnknown!
        };
    }

    private string GetTooltipText()
    {
        var baseText = _autoSwitchService.CurrentState switch
        {
            AutoSwitchState.WirelessConnected => "G-AutoSwitch - Wireless",
            AutoSwitchState.WirelessDisconnected => "G-AutoSwitch - Wired",
            AutoSwitchState.DongleNotFound => "G-AutoSwitch - Dongle Not Found",
            AutoSwitchState.Stopped => "G-AutoSwitch - Stopped",
            AutoSwitchState.Unknown => "G-AutoSwitch - Unknown State",
            AutoSwitchState.Transitioning => "G-AutoSwitch - Switching...",
            _ => "G-AutoSwitch"
        };

        // Add suffix based on enabled state and configured device state
        // Only add suffix for connection states (not error/stopped/unknown)
        if (_autoSwitchService.CurrentState == AutoSwitchState.WirelessConnected ||
            _autoSwitchService.CurrentState == AutoSwitchState.WirelessDisconnected)
        {
            if (!_autoSwitchService.IsEnabled)
            {
                return baseText + " (Disabled)";
            }

            if (!_autoSwitchService.IsUsingConfiguredDevice)
            {
                return baseText + " (Inactive)";
            }
        }

        return baseText;
    }

    private void OnToggleAutoSwitch()
    {
        _autoSwitchService.IsEnabled = !_autoSwitchService.IsEnabled;

        // Save the setting
        _settingsService.Settings.AutoSwitchEnabled = _autoSwitchService.IsEnabled;
        _ = _settingsService.SaveAsync();
    }

    private void OnForceApply()
    {
        _autoSwitchService.ForceApply();
    }

    private void UpdateDebugSubMenu()
    {
        if (_debugSubMenu == null) return;

        _debugSubMenu.Items.Clear();

        var settings = _settingsService.Settings;
        const double minWidth = 350;

        // Connection state section
        var stateHeader = new MenuFlyoutItem { Text = "── Connection State ──", IsEnabled = false, MinWidth = minWidth };
        _debugSubMenu.Items.Add(stateHeader);

        var currentState = new MenuFlyoutItem
        {
            Text = $"State: {_autoSwitchService.CurrentState}",
            IsEnabled = false,
            MinWidth = minWidth
        };
        _debugSubMenu.Items.Add(currentState);

        var enabledState = new MenuFlyoutItem
        {
            Text = $"Enabled: {_autoSwitchService.IsEnabled}",
            IsEnabled = false,
            MinWidth = minWidth
        };
        _debugSubMenu.Items.Add(enabledState);

        var usingConfigured = new MenuFlyoutItem
        {
            Text = $"Using Configured: {_autoSwitchService.IsUsingConfiguredDevice}",
            IsEnabled = false,
            MinWidth = minWidth
        };
        _debugSubMenu.Items.Add(usingConfigured);

        _debugSubMenu.Items.Add(new MenuFlyoutSeparator());

        // Current settings section
        var currentHeader = new MenuFlyoutItem { Text = "── Current Devices ──", IsEnabled = false, MinWidth = minWidth };
        _debugSubMenu.Items.Add(currentHeader);

        var currentPlayback = _audioDeviceService.GetDefaultDevice();
        var currentPlaybackItem = new MenuFlyoutItem
        {
            Text = $"Speaker: {currentPlayback?.Name ?? "(none)"}",
            IsEnabled = false,
            MinWidth = minWidth
        };
        _debugSubMenu.Items.Add(currentPlaybackItem);

        var currentCapture = _audioDeviceService.GetDefaultCaptureDevice();
        var currentCaptureItem = new MenuFlyoutItem
        {
            Text = $"Mic: {currentCapture?.Name ?? "(none)"}",
            IsEnabled = false,
            MinWidth = minWidth
        };
        _debugSubMenu.Items.Add(currentCaptureItem);

        _debugSubMenu.Items.Add(new MenuFlyoutSeparator());

        // Goal settings section
        var goalHeader = new MenuFlyoutItem { Text = "── Configured Devices ──", IsEnabled = false, MinWidth = minWidth };
        _debugSubMenu.Items.Add(goalHeader);

        var wirelessSpeaker = new MenuFlyoutItem
        {
            Text = $"Wireless: {settings.WirelessDeviceName ?? "(not set)"}",
            IsEnabled = false,
            MinWidth = minWidth
        };
        _debugSubMenu.Items.Add(wirelessSpeaker);

        var wiredSpeaker = new MenuFlyoutItem
        {
            Text = $"Wired: {settings.WiredDeviceName ?? "(not set)"}",
            IsEnabled = false,
            MinWidth = minWidth
        };
        _debugSubMenu.Items.Add(wiredSpeaker);

        var wirelessMic = new MenuFlyoutItem
        {
            Text = $"Wireless Mic: {settings.WirelessMicrophoneName ?? "(not set)"}",
            IsEnabled = false,
            MinWidth = minWidth
        };
        _debugSubMenu.Items.Add(wirelessMic);

        var wiredMic = new MenuFlyoutItem
        {
            Text = $"Wired Mic: {settings.WiredMicrophoneName ?? "(not set)"}",
            IsEnabled = false,
            MinWidth = minWidth
        };
        _debugSubMenu.Items.Add(wiredMic);

        // Last switch info
        if (_autoSwitchService.LastSwitchTime.HasValue)
        {
            _debugSubMenu.Items.Add(new MenuFlyoutSeparator());

            var lastSwitchHeader = new MenuFlyoutItem { Text = "── Last Switch ──", IsEnabled = false, MinWidth = minWidth };
            _debugSubMenu.Items.Add(lastSwitchHeader);

            var lastSwitchTime = new MenuFlyoutItem
            {
                Text = $"Time: {_autoSwitchService.LastSwitchTime:HH:mm:ss}",
                IsEnabled = false,
                MinWidth = minWidth
            };
            _debugSubMenu.Items.Add(lastSwitchTime);

            var lastSwitchDesc = new MenuFlyoutItem
            {
                Text = $"{_autoSwitchService.LastSwitchDescription ?? "(none)"}",
                IsEnabled = false,
                MinWidth = minWidth
            };
            _debugSubMenu.Items.Add(lastSwitchDesc);
        }
    }

    private void OnShowWindow()
    {
        ShowWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnExit()
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unsubscribe from events
        _autoSwitchService.StateChanged -= OnAutoSwitchStateChanged;
        _autoSwitchService.IsEnabledChanged -= OnIsEnabledChanged;
        _autoSwitchService.ConfiguredDeviceStateChanged -= OnConfiguredDeviceStateChanged;

        _taskbarIcon?.Dispose();
        _taskbarIcon = null;
    }
}
