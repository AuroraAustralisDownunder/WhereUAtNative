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

    private string _statusMessage = "Turn on location sharing or join a family to see the map.";
    private bool _hasSelfPin;
    private double _pinLatitude;
    private double _pinLongitude;
    private int _familyMarkerCount;
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
            OnPropertyChanged(nameof(ShowEmptyMessage));
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
            OnPropertyChanged(nameof(ShowEmptyMessage));
        }
    }

    public bool HasMapContent => HasSelfPin || FamilyMarkerCount > 0;

    public bool ShowEmptyMessage => !HasMapContent;

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

    /// <summary>Raised when family marker set changes so the page can sync the WebView.</summary>
    public event EventHandler? MarkersChanged;

    public ICommand GoHomeCommand { get; }

    public MapViewModel(ILocationService locationService, IFamilyService familyService, IAuthService authService)
    {
        _locationService = locationService;
        _familyService = familyService;
        _authService = authService;
        GoHomeCommand = new Command(async () =>
        {
            try { await Shell.Current.GoToAsync("//HomePage"); }
            catch { /* ignore */ }
        });
    }

    public async Task OnAppearingAsync()
    {
        _isPageVisible = true;
        _locationService.PositionChanged -= OnPositionChanged;
        _locationService.PositionChanged += OnPositionChanged;

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

    public void OnDisappearing()
    {
        _isPageVisible = false;
        _locationService.PositionChanged -= OnPositionChanged;
        StopFamilyPoll();
    }

    private void OnPositionChanged(object? sender, Location? location)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
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
            // Keep previous self pin if we had one; only clear when sharing is off.
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
                // Self is rendered via setPin / id "self" — skip duplicate uid marker.
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
                StatusMessage = "No live locations yet. Turn on Share my location, or wait for family.";
            else
                StatusMessage = "Turn on location sharing or join a family to see the map.";
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

        // Poll even without a family so joining mid-session still works after RefreshMembership.
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
                MainThread.BeginInvokeOnMainThread(UpdateStatusMessage);
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
