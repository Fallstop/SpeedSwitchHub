namespace GAutoSwitch.UI.Views;

/// <summary>
/// Settings page for configuring audio device switching.
/// </summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }
    public HeadsetStateViewModel HeadsetViewModel { get; }

    public SettingsPage()
    {
        // Get services from App
        var audioService = App.AudioDeviceService;
        var settingsService = App.SettingsService;
        var startupService = App.StartupService;
        var headsetStateService = App.HeadsetStateService;

        ViewModel = new SettingsViewModel(audioService, settingsService, startupService, headsetStateService);
        HeadsetViewModel = new HeadsetStateViewModel(headsetStateService);

        this.InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();

        // Start monitoring headset state
        HeadsetViewModel.StartMonitoring();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        // Stop monitoring and clean up
        HeadsetViewModel.StopMonitoring();
        HeadsetViewModel.Dispose();
    }
}
