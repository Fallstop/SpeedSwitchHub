using GAutoSwitch.UI.ViewModels;

namespace GAutoSwitch.UI.Views;

/// <summary>
/// Page for configuring and controlling the audio proxy feature.
/// </summary>
public sealed partial class AudioProxyPage : Page
{
    public AudioProxyViewModel ViewModel { get; }

    public AudioProxyPage()
    {
        // Get services from App
        var proxyService = App.AudioProxyService;
        var audioService = App.AudioDeviceService;
        var settingsService = App.SettingsService;
        var headsetStateService = App.HeadsetStateService;

        ViewModel = new AudioProxyViewModel(proxyService, audioService, settingsService, headsetStateService);

        this.InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Cleanup();
    }
}
