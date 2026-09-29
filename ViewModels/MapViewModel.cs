using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.Maui.Devices.Sensors;
using WhereUAtNative.Services;

namespace WhereUAtNative.ViewModels;

public class MapViewModel : INotifyPropertyChanged
{
    private readonly ILocationService _locationService;

    private string _statusMessage = "Turn on location sharing to see yourself on the map.";
    private bool _hasPin;
    private double _pinLatitude;
    private double _pinLongitude;

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public bool HasPin
    {
        get => _hasPin;
        set
        {
            _hasPin = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowEmptyMessage));
        }
    }

    public bool ShowEmptyMessage => !HasPin;

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

    public ICommand GoHomeCommand { get; }

    public MapViewModel(ILocationService locationService)
    {
        _locationService = locationService;
        GoHomeCommand = new Command(async () =>
        {
            try { await Shell.Current.GoToAsync("//HomePage"); }
            catch { /* ignore */ }
        });
    }

    public async Task OnAppearingAsync()
    {
        _locationService.PositionChanged -= OnPositionChanged;
        _locationService.PositionChanged += OnPositionChanged;

        if (!_locationService.IsSharingEnabled)
        {
            ClearPin("Turn on location sharing to see yourself on the map.");
            return;
        }

        StatusMessage = "Getting your position…";
        var location = _locationService.LastKnownLocation ?? await _locationService.GetCurrentAsync();
        ApplyLocation(location);
    }

    public void OnDisappearing()
    {
        _locationService.PositionChanged -= OnPositionChanged;
    }

    private void OnPositionChanged(object? sender, Location? location)
    {
        MainThread.BeginInvokeOnMainThread(() => ApplyLocation(location));
    }

    private void ApplyLocation(Location? location)
    {
        if (!_locationService.IsSharingEnabled)
        {
            ClearPin("Turn on location sharing to see yourself on the map.");
            return;
        }

        if (location is null)
        {
            ClearPin("Waiting for GPS…");
            return;
        }

        PinLatitude = location.Latitude;
        PinLongitude = location.Longitude;
        HasPin = true;
        var lat = Math.Round(location.Latitude, 4);
        var lon = Math.Round(location.Longitude, 4);
        StatusMessage = $"You — {lat:0.0000}, {lon:0.0000}";
    }

    private void ClearPin(string message)
    {
        HasPin = false;
        StatusMessage = message;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
