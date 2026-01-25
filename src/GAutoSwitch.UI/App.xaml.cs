using System.Diagnostics;
using System.IO;
using GAutoSwitch.Core.Interfaces;
using GAutoSwitch.Core.Services;
using GAutoSwitch.Hardware;
using GAutoSwitch.UI.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Composition.SystemBackdrops;

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
    /// Gets the main window instance.
    /// </summary>
    public static Window? MainWindow { get; private set; }

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
#if DEBUG
        // Enable Debug.WriteLine output to Rider/VS console
        Trace.Listeners.Add(new ConsoleTraceListener());
#endif
        this.InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched normally by the end user.
    /// </summary>
    protected override async void OnLaunched(LaunchActivatedEventArgs e)
    {
        // Load settings first
        await SettingsService.LoadAsync();

        // Initialize auto-switch service
        AutoSwitchService = new AutoSwitchService(
            HeadsetStateService,
            AudioDeviceService,
            SettingsService);
        AutoSwitchService.IsEnabled = SettingsService.Settings.AutoSwitchEnabled;
        AutoSwitchService.Start();

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
        _trayIconService = new TrayIconService(AutoSwitchService, SettingsService);
        _trayIconService.ShowWindowRequested += OnShowWindowRequested;
        _trayIconService.ExitRequested += OnExitRequested;
        _trayIconService.Initialize(_window);

        // Only show window if not starting minimized
        if (!startMinimized)
        {
            _window.Activate();
        }
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
