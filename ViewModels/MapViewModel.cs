using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.Maui.Devices.Sensors;
using WhereUAtNative.Services;

namespace WhereUAtNative.ViewModels;

public class MapViewModel : INotifyPropertyChanged
{
    private const int FamilyPollSeconds = 5;

    private readonly ILocationService _locationService;
    private readonly IFamilyService _familyService;
    private readonly IAuthService _authService;

    private string _statusMessage = "Tap the pin to share your location, or open Settings to join a family.";
    private bool _hasSelfPin;
    private double _pinLatitude;
    private double _pinLongitude;
    private int _familyMarkerCount;
    private bool _isSharingEnabled;
    private bool _isToggling;
    private bool _isBusy;
    private string? _toggleHint;
    private CancellationTokenSource? _pollCts;
    private bool _isPageVisible;

    /// <summary>Snapshot of family markers for the WebView (uid → lat/lon/label). Self uses id "self".</summary>
    public IReadOnlyDictionary<string, (double Lat, double Lon, string Label)> FamilyMarkers { get; private set; }
        = new Dictionary<string, (double, double, string)>();

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public bool HasSelfPin
    {
        get => _hasSelfPin;
        set
        {
            _hasSelfPin = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasMapContent));
            OnPropertyChanged(nameof(ShowEmptyOverlay));
        }
    }

    public int FamilyMarkerCount
    {
        get => _familyMarkerCount;
        private set
        {
            _familyMarkerCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasMapContent));
            OnPropertyChanged(nameof(ShowEmptyOverlay));
        }
    }

    public bool HasMapContent => HasSelfPin || FamilyMarkerCount > 0;

    /// <summary>Soft overlay on the live map when nothing to show yet.</summary>
    public bool ShowEmptyOverlay => !HasMapContent;

    public double PinLatitude
    {
        get => _pinLatitude;
        set { _pinLatitude = value; OnPropertyChanged(); }
    }

    public double PinLongitude
    {
        get => _pinLongitude;
        set { _pinLongitude = value; OnPropertyChanged(); }
    }

    public bool IsSharingEnabled
    {
        get => _isSharingEnabled;
        private set
        {
            if (_isSharingEnabled == value)
                return;
            _isSharingEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SharingFabColor));
            OnPropertyChanged(nameof(SharingFabTextColor));
            OnPropertyChanged(nameof(SharingFabLabel));
        }
    }

    /// <summary>Green when sharing on; muted grey when off.</summary>
    public Color SharingFabColor => IsSharingEnabled
        ? Color.FromArgb("#4CAF50")
        : Color.FromArgb("#636366");

    public Color SharingFabTextColor => Colors.White;

    public string SharingFabLabel => IsSharingEnabled ? "📍" : "📍";

    public string? ToggleHint
    {
        get => _toggleHint;
        set
        {
            _toggleHint = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsToggleHintVisible));
        }
    }

    public bool IsToggleHintVisible => !string.IsNullOrWhiteSpace(ToggleHint);

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            _isBusy = value;
            OnPropertyChanged();
            ((Command)SignOutCommand).ChangeCanExecute();
            ((Command)ToggleSharingCommand).ChangeCanExecute();
        }
    }

    /// <summary>Raised when family marker set changes so the page can sync the WebView.</summary>
    public event EventHandler? MarkersChanged;

    public ICommand ToggleSharingCommand { get; }
    public ICommand SignOutCommand { get; }
    public ICommand OpenSettingsCommand { get; }

    public MapViewModel(ILocationService locationService, IFamilyService familyService, IAuthService authService)
    {
        _locationService = locationService;
        _familyService = familyService;
        _authService = authService;
        ToggleSharingCommand = new Command(async () => await ToggleSharingAsync(), () => !IsBusy && !_isToggling);
        SignOutCommand = new Command(async () => await SignOutAsync(), () => !IsBusy);
        OpenSettingsCommand = new Command(async () => await OpenSettingsAsync());
        IsSharingEnabled = _locationService.IsSharingEnabled;
    }

    public async Task OnAppearingAsync()
    {
        _isPageVisible = true;
        try
        {
            _locationService.PositionChanged -= OnPositionChanged;
            _locationService.PositionChanged += OnPositionChanged;

            IsSharingEnabled = _locationService.IsSharingEnabled;

            await _familyService.RefreshMembershipAsync();

            if (_locationService.IsSharingEnabled)
            {
                StatusMessage = "Getting your position…";
                var location = _locationService.LastKnownLocation ?? await _locationService.GetCurrentAsync();
                ApplySelfLocation(location);
            }
            else
            {
                ClearSelfPin();
            }

            await RefreshFamilyMarkersAsync();
            UpdateStatusMessage();
            StartFamilyPoll();
        }
        catch
        {
            // Missing token / RTDB / GPS must not crash map appearance.
            try { UpdateStatusMessage(); } catch { /* ignore */ }
        }
    }

    public void OnDisappearing()
    {
        _isPageVisible = false;
        _locationService.PositionChanged -= OnPositionChanged;
        StopFamilyPoll();
    }

    private async Task ToggleSharingAsync()
    {
        if (_isToggling || IsBusy)
            return;

        _isToggling = true;
        ((Command)ToggleSharingCommand).ChangeCanExecute();
        try
        {
            if (_locationService.IsSharingEnabled)
            {
                await _locationService.DisableSharingAsync();
                await _familyService.ClearPublishedLocationAsync();
                IsSharingEnabled = false;
                ClearSelfPin();
                ToggleHint = null;
                UpdateStatusMessage();
            }
            else
            {
                ToggleHint = null;
                StatusMessage = "Requesting permission…";
                var (success, message) = await _locationService.EnableSharingAsync();
                IsSharingEnabled = _locationService.IsSharingEnabled;

                if (!success)
                {
                    ToggleHint = message;
                    UpdateStatusMessage();
                    return;
                }

                ToggleHint = message;
                var location = _locationService.LastKnownLocation ?? await _locationService.GetCurrentAsync();
                ApplySelfLocation(location);
                UpdateStatusMessage();
            }
        }
        finally
        {
            _isToggling = false;
            ((Command)ToggleSharingCommand).ChangeCanExecute();
        }
    }

    private async Task SignOutAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            StopFamilyPoll();
            if (_locationService.IsSharingEnabled)
                await _locationService.DisableSharingAsync();
            await _familyService.ClearPublishedLocationAsync();
            IsSharingEnabled = false;
            ClearSelfPin();
            ToggleHint = null;

            await _authService.SignOutAsync();
            await Shell.Current.GoToAsync("//LoginPage");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task OpenSettingsAsync()
    {
        try
        {
            await Shell.Current.GoToAsync("SettingsPage");
        }
        catch
        {
            // ignore navigation failures
        }
    }

    private void OnPositionChanged(object? sender, Location? location)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            IsSharingEnabled = _locationService.IsSharingEnabled;
            ApplySelfLocation(location);
            UpdateStatusMessage();
        });
    }

    private void ApplySelfLocation(Location? location)
    {
        if (!_locationService.IsSharingEnabled)
        {
            ClearSelfPin();
            return;
        }

        if (location is null)
        {
            if (!HasSelfPin)
                StatusMessage = "Waiting for GPS…";
            return;
        }

        PinLatitude = location.Latitude;
        PinLongitude = location.Longitude;
        HasSelfPin = true;
    }

    private void ClearSelfPin()
    {
        HasSelfPin = false;
    }

    private async Task RefreshFamilyMarkersAsync()
    {
        try
        {
            var selfUid = _authService.CurrentUserId;
            var locations = await _familyService.GetFamilyLocationsAsync();
            var next = new Dictionary<string, (double Lat, double Lon, string Label)>(StringComparer.Ordinal);

            foreach (var loc in locations)
            {
                if (!string.IsNullOrEmpty(selfUid) && loc.Uid == selfUid)
                    continue;

                next[loc.Uid] = (loc.Latitude, loc.Longitude, loc.DisplayName);
            }

            FamilyMarkers = next;
            FamilyMarkerCount = next.Count;
            MarkersChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Keep last snapshot on poll failure.
        }
    }

    private void UpdateStatusMessage()
    {
        if (!HasMapContent)
        {
            if (!string.IsNullOrEmpty(_familyService.CurrentFamilyId))
                StatusMessage = "No live locations yet. Tap the pin to share, or wait for family.";
            else
                StatusMessage = "Tap the pin to share your location, or open Settings to join a family.";
            return;
        }

        var parts = new List<string>();
        if (HasSelfPin)
        {
            var lat = Math.Round(PinLatitude, 4);
            var lon = Math.Round(PinLongitude, 4);
            parts.Add($"You — {lat:0.0000}, {lon:0.0000}");
        }

        if (FamilyMarkerCount > 0)
            parts.Add($"{FamilyMarkerCount} family sharing");

        if (!string.IsNullOrEmpty(_familyService.CurrentFamilyId))
            parts.Add($"code {_familyService.CurrentFamilyId}");

        StatusMessage = string.Join(" · ", parts);
    }

    private void StartFamilyPoll()
    {
        StopFamilyPoll();
        if (!_isPageVisible)
            return;

        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;
        _ = RunFamilyPollAsync(token);
    }

    private async Task RunFamilyPollAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(FamilyPollSeconds), token);
                if (token.IsCancellationRequested || !_isPageVisible)
                    break;

                await RefreshFamilyMarkersAsync();
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    IsSharingEnabled = _locationService.IsSharingEnabled;
                    UpdateStatusMessage();
                });
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }

    private void StopFamilyPoll()
    {
        try
        {
            _pollCts?.Cancel();
            _pollCts?.Dispose();
        }
        catch
        {
            // ignore
        }
        finally
        {
            _pollCts = null;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
