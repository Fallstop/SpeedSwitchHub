using System.Diagnostics;
using System.IO;
using GAutoSwitch.Core.Interfaces;
using GAutoSwitch.Core.Services;
using GAutoSwitch.Hardware;
using GAutoSwitch.Hardware.Audio;
using GAutoSwitch.UI.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Composition.SystemBackdrops;
using Squirrel;

namespace GAutoSwitch.UI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
private Window? _window;
    private TrayIconService? _trayIconService;
    private bool _isExiting;

    /// <summary>
    /// Shared audio device service instance.
    /// </summary>
    public static IAudioDeviceService AudioDeviceService { get; } = new AudioDeviceService();

    /// <summary>
    /// Shared settings service instance.
    /// </summary>
    public static ISettingsService SettingsService { get; } = new SettingsService();

    /// <summary>
    /// Shared startup service instance.
    /// </summary>
    public static IStartupService StartupService { get; } = new StartupService();

    /// <summary>
    /// Shared headset state service instance for detecting headphone connection status.
    /// </summary>
    public static IHeadsetStateService HeadsetStateService { get; } = new HeadsetStateService();

    /// <summary>
    /// Shared auto-switch service instance for orchestrating audio device switching.
    /// </summary>
    public static IAutoSwitchService AutoSwitchService { get; private set; } = null!;

    /// <summary>
    /// Shared audio proxy service instance for managing the low-latency audio proxy.
    /// </summary>
    public static IAudioProxyService AudioProxyService { get; } = new AudioProxyService();

    /// <summary>
    /// Gets the main window instance.
    /// </summary>
    public static Window? MainWindow { get; private set; }

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        // Handle Squirrel events BEFORE InitializeComponent
        SquirrelAwareApp.HandleEvents(
            onInitialInstall: OnAppInstall,
            onAppUpdate: OnAppUpdate,
            onAppUninstall: OnAppUninstall,
            onEveryRun: OnEveryRun
        );

#if DEBUG
        // Enable Debug.WriteLine output to Rider/VS console
        Trace.Listeners.Add(new ConsoleTraceListener());
#endif
        this.InitializeComponent();
    }

    private static void OnAppInstall(SemanticVersion version, IAppTools tools)
    {
        // Create Start Menu and Desktop shortcuts
        tools.CreateShortcutForThisExe(ShortcutLocation.StartMenu | ShortcutLocation.Desktop);
    }

    private static void OnAppUpdate(SemanticVersion version, IAppTools tools)
    {
        // Update shortcuts to point to new version
        tools.CreateShortcutForThisExe(ShortcutLocation.StartMenu | ShortcutLocation.Desktop);
    }

    private static void OnAppUninstall(SemanticVersion version, IAppTools tools)
    {
        // Remove all shortcuts
        tools.RemoveShortcutForThisExe(ShortcutLocation.StartMenu | ShortcutLocation.Desktop);
    }

    private static void OnEveryRun(SemanticVersion version, IAppTools tools, bool firstRun)
    {
        // Update shortcuts on every run (ensures they're always current)
        tools.SetProcessAppUserModelId();

        if (firstRun)
        {
            Debug.WriteLine("G-AutoSwitch: First run after installation");
        }
    }

    /// <summary>
    /// Invoked when the application is launched normally by the end user.
    /// </summary>
    protected override async void OnLaunched(LaunchActivatedEventArgs e)
    {
        // Load settings first
        await SettingsService.LoadAsync();

        // Initialize auto-switch service (with audio proxy integration)
        AutoSwitchService = new AutoSwitchService(
            HeadsetStateService,
            AudioDeviceService,
            SettingsService,
            AudioProxyService);
        AutoSwitchService.IsEnabled = SettingsService.Settings.AutoSwitchEnabled;
        AutoSwitchService.Start();

        // Auto-start audio proxy if enabled
        if (SettingsService.Settings.UseAudioProxy)
        {
            _ = StartAudioProxyAsync();
        }

        // Check for --minimized or --silent command-line args
        var args = Environment.GetCommandLineArgs();
        var startMinimized = args.Contains("--minimized") || args.Contains("--silent")
                             || SettingsService.Settings.StartMinimized;

        _window ??= new Window();
        MainWindow = _window;

        // Set window title
        _window.Title = "G-AutoSwitch";

        // Set window icon for taskbar
        _window.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));

        // Enable Mica backdrop
        _window.SystemBackdrop = new MicaBackdrop();

        // Configure custom titlebar
        var titleBar = _window.AppWindow.TitleBar;
        titleBar.ExtendsContentIntoTitleBar = true;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        // Subscribe to window closing to minimize to tray instead
        _window.AppWindow.Closing += OnWindowClosing;

        if (_window.Content is not Frame rootFrame)
        {
            rootFrame = new Frame();
            rootFrame.NavigationFailed += OnNavigationFailed;
            _window.Content = rootFrame;
        }

        _ = rootFrame.Navigate(typeof(ShellPage), e.Arguments);

        // Initialize tray icon with dependencies for dynamic state updates
        _trayIconService = new TrayIconService(AutoSwitchService, SettingsService, AudioDeviceService);
        _trayIconService.ShowWindowRequested += OnShowWindowRequested;
        _trayIconService.ExitRequested += OnExitRequested;
        _trayIconService.Initialize(_window);

        // Only show window if not starting minimized
        if (!startMinimized)
        {
            _window.Activate();
        }
    }

    /// <summary>
    /// Starts the audio proxy with the configured wireless/wired output device.
    /// </summary>
    private static async Task StartAudioProxyAsync()
    {
        var settings = SettingsService.Settings;
        var headsetState = HeadsetStateService.CurrentState;

        // Determine speaker output based on headset state (use configured wireless/wired device)
        string? speakerOutputDeviceId = null;
        if (headsetState == HeadsetConnectionState.Online && !string.IsNullOrEmpty(settings.WirelessDeviceId))
        {
            speakerOutputDeviceId = settings.WirelessDeviceId;
            Debug.WriteLine($"[App] Using wireless speaker device for proxy output");
        }
        else if (!string.IsNullOrEmpty(settings.WiredDeviceId))
        {
            speakerOutputDeviceId = settings.WiredDeviceId;
            Debug.WriteLine($"[App] Using wired speaker device for proxy output");
        }
        else
        {
            // Fallback to default device only if no configured devices
            var outputDevice = AudioDeviceService.GetDefaultDevice();
            if (outputDevice != null)
            {
                speakerOutputDeviceId = outputDevice.Id;
                Debug.WriteLine($"[App] No configured devices, using default: {outputDevice.Name}");
            }
        }

        if (string.IsNullOrEmpty(speakerOutputDeviceId))
        {
            Debug.WriteLine("[App] Cannot auto-start audio proxy: no output device configured or found");
            return;
        }

        // Determine microphone input if mic proxy is enabled
        string? micInputDeviceId = null;
        if (settings.UseMicProxy && settings.MicProxyAutoStart && AudioProxyService.IsVBCableInputInstalled)
        {
            // Use wireless/wired mic based on current headset state
            if (headsetState == HeadsetConnectionState.Online && !string.IsNullOrEmpty(settings.WirelessMicrophoneId))
            {
                micInputDeviceId = settings.WirelessMicrophoneId;
            }
            else if (headsetState == HeadsetConnectionState.Offline && !string.IsNullOrEmpty(settings.WiredMicrophoneId))
            {
                micInputDeviceId = settings.WiredMicrophoneId;
            }
            else
            {
                // Fallback to configured mic proxy input device
                micInputDeviceId = settings.MicProxyInputDeviceId;
            }
        }

        Debug.WriteLine($"[App] Auto-starting audio proxy with output: {speakerOutputDeviceId}");
        if (!string.IsNullOrEmpty(micInputDeviceId))
        {
            Debug.WriteLine($"[App] Also starting mic proxy with input: {micInputDeviceId}");
        }

        await AudioProxyService.StartAsync(speakerOutputDeviceId, micInputDeviceId);
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // If we're exiting, allow the window to close
        if (_isExiting) return;

        // Otherwise, hide the window instead of closing
        args.Cancel = true;
        HideMainWindow();
    }

    private void OnShowWindowRequested(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        ExitApplication();
    }

    /// <summary>
    /// Shows the main window.
    /// </summary>
    public static void ShowMainWindow()
    {
        if (MainWindow == null) return;

        MainWindow.Activate();

        // Restore if minimized
        var presenter = MainWindow.AppWindow.Presenter as OverlappedPresenter;
        if (presenter?.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }
    }

    /// <summary>
    /// Hides the main window to tray.
    /// </summary>
    public static void HideMainWindow()
    {
        MainWindow?.AppWindow.Hide();
    }

    /// <summary>
    /// Exits the application completely.
    /// </summary>
    public void ExitApplication()
    {
        _isExiting = true;

        // Dispose tray icon
        if (_trayIconService != null)
        {
            _trayIconService.ShowWindowRequested -= OnShowWindowRequested;
            _trayIconService.ExitRequested -= OnExitRequested;
            _trayIconService.Dispose();
            _trayIconService = null;
        }

        // Dispose auto-switch service
        AutoSwitchService?.Dispose();

        // Dispose audio proxy service
        AudioProxyService?.Dispose();

        // Dispose headset state service
        HeadsetStateService.Dispose();

        // Close the window
        _window?.Close();

        // Exit the application
        Exit();
    }

    /// <summary>
    /// Invoked when Navigation to a certain page fails.
    /// </summary>
    private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
    {
        throw new InvalidOperationException($"Failed to load Page {e.SourcePageType.FullName}");
    }
}
