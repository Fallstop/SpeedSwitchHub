namespace GAutoSwitch.UI.Views;

/// <summary>
/// Shell page containing the NavigationView and main content frame.
/// </summary>
public sealed partial class ShellPage : Page
{
    public ShellPage()
    {
        this.InitializeComponent();
        this.Loaded += ShellPage_Loaded;
    }

    private void ShellPage_Loaded(object sender, RoutedEventArgs e)
    {
        // Set the custom titlebar drag region
        App.MainWindow?.SetTitleBar(AppTitleBar);

        // Select Settings by default and navigate
        NavView.SelectedItem = NavView.MenuItems[0];
        ContentFrame.Navigate(typeof(SettingsPage));
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item)
        {
            var tag = item.Tag?.ToString();
            switch (tag)
            {
                case "Settings":
                    ContentFrame.Navigate(typeof(SettingsPage));
                    break;
                case "AudioProxy":
                    ContentFrame.Navigate(typeof(AudioProxyPage));
                    break;
                case "About":
                    ContentFrame.Navigate(typeof(AboutPage));
                    break;
            }
        }
    }
}
