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

    private TaskbarIcon? _taskbarIcon;
    private MenuFlyoutItem? _toggleAutoSwitchItem;
    private DispatcherQueue? _dispatcherQueue;
    private bool _disposed;

    // Preloaded icons for each state
    private BitmapImage? _iconWireless;
    private BitmapImage? _iconWired;
    private BitmapImage? _iconError;
    private BitmapImage? _iconUnknown;

    public event EventHandler? ShowWindowRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService(IAutoSwitchService autoSwitchService, ISettingsService settingsService)
    {
        _autoSwitchService = autoSwitchService;
        _settingsService = settingsService;
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
