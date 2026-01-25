using GAutoSwitch.Core.Interfaces;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace GAutoSwitch.UI.ViewModels;

/// <summary>
/// ViewModel for displaying the headset connection state in the UI.
/// </summary>
public partial class HeadsetStateViewModel : ObservableObject, IDisposable
{
    private readonly IHeadsetStateService _headsetStateService;
    private readonly DispatcherQueue? _dispatcherQueue;
    private bool _disposed;

    // Status colors matching WinUI design system
    private static readonly Color OnlineColor = Color.FromArgb(255, 15, 123, 15);     // Green #0F7B0F
    private static readonly Color OfflineColor = Color.FromArgb(255, 157, 157, 157);  // Gray
    private static readonly Color DongleNotFoundColor = Color.FromArgb(255, 196, 43, 28); // Red #C42B1C
    private static readonly Color UnknownColor = Color.FromArgb(255, 157, 93, 0);     // Yellow/Orange #9D5D00

    [ObservableProperty]
    private HeadsetConnectionState _connectionState = HeadsetConnectionState.Unknown;

    [ObservableProperty]
    private string _statusText = "Detecting...";

    [ObservableProperty]
    private SolidColorBrush _statusColor = new(UnknownColor);

    [ObservableProperty]
    private SolidColorBrush _statusDotColor = new(UnknownColor);

    [ObservableProperty]
    private bool _isOnline;

    [ObservableProperty]
    private string _productName = "PRO X 2 LIGHTSPEED";

    [ObservableProperty]
    private string _statusIcon = "\uE7F5"; // Headphone icon by default

    [ObservableProperty]
    private bool _isSwitching;

    [ObservableProperty]
    private string _switchingText = "";

    // Transition color (blue accent)
    private static readonly Color SwitchingColor = Color.FromArgb(255, 0, 120, 212); // Blue #0078D4

    public HeadsetStateViewModel(IHeadsetStateService headsetStateService)
    {
        _headsetStateService = headsetStateService;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _headsetStateService.StateChanged += OnStateChanged;

        // Initialize with current state
        UpdateFromState(_headsetStateService.CurrentState);
    }

    /// <summary>
    /// Starts monitoring the headset state.
    /// </summary>
    public void StartMonitoring()
    {
        if (!_headsetStateService.IsMonitoring)
        {
            _headsetStateService.StartMonitoring();
        }
    }

    /// <summary>
    /// Stops monitoring the headset state.
    /// </summary>
    public void StopMonitoring()
    {
        _headsetStateService.StopMonitoring();
    }

    private void OnStateChanged(object? sender, HeadsetStateChangedEventArgs e)
    {
        // Marshal to UI thread
        if (_dispatcherQueue != null)
        {
            _dispatcherQueue.TryEnqueue(() => HandleStateTransition(e.PreviousState, e.NewState));
        }
        else
        {
            HandleStateTransition(e.PreviousState, e.NewState);
        }
    }

    private async void HandleStateTransition(HeadsetConnectionState previousState, HeadsetConnectionState newState)
    {
        // Only show transition for Online <-> Offline switches (actual device switching)
        bool isDeviceSwitch = (previousState == HeadsetConnectionState.Online && newState == HeadsetConnectionState.Offline) ||
                              (previousState == HeadsetConnectionState.Offline && newState == HeadsetConnectionState.Online);

        if (isDeviceSwitch)
        {
            // Show immediate transition feedback
            IsSwitching = true;
            SwitchingText = newState == HeadsetConnectionState.Online
                ? "Switching to wireless..."
                : "Switching to wired...";

            // Update visual to show transitioning state
            StatusText = SwitchingText;
            StatusColor = new SolidColorBrush(SwitchingColor);
            StatusDotColor = new SolidColorBrush(SwitchingColor);
            StatusIcon = "\uE895"; // Sync icon
            ConnectionState = newState;
            IsOnline = newState == HeadsetConnectionState.Online;

            // Brief delay to show transition, then update to final state
            await Task.Delay(800);

            IsSwitching = false;
            SwitchingText = "";
        }

        // Update to final state
        UpdateFromState(newState);
    }

    private void UpdateFromState(HeadsetConnectionState state)
    {
        ConnectionState = state;

        // Update product name from service if available
        if (!string.IsNullOrEmpty(_headsetStateService.ProductName))
        {
            ProductName = _headsetStateService.ProductName;
        }

        // Update visual properties based on state
        switch (state)
        {
            case HeadsetConnectionState.Online:
                StatusText = "Connected";
                StatusColor = new SolidColorBrush(OnlineColor);
                StatusDotColor = new SolidColorBrush(OnlineColor);
                StatusIcon = "\uE7F6"; // Headphones with check
                IsOnline = true;
                break;

            case HeadsetConnectionState.Offline:
                StatusText = "Disconnected";
                StatusColor = new SolidColorBrush(OfflineColor);
                StatusDotColor = new SolidColorBrush(OfflineColor);
                StatusIcon = "\uE7F5"; // Headphones
                IsOnline = false;
                break;

            case HeadsetConnectionState.DongleNotFound:
                StatusText = "Dongle Not Found";
                StatusColor = new SolidColorBrush(DongleNotFoundColor);
                StatusDotColor = new SolidColorBrush(DongleNotFoundColor);
                StatusIcon = "\uE8D8"; // USB icon
                IsOnline = false;
                break;

            case HeadsetConnectionState.Unknown:
            default:
                StatusText = "Detecting...";
                StatusColor = new SolidColorBrush(UnknownColor);
                StatusDotColor = new SolidColorBrush(UnknownColor);
                StatusIcon = "\uE9CE"; // Question mark
                IsOnline = false;
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _headsetStateService.StateChanged -= OnStateChanged;
        StopMonitoring();
        _disposed = true;
    }
}
