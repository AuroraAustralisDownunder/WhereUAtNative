using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
using WhereUAtNative.ViewModels;
using MapControl = Microsoft.Maui.Controls.Maps.Map;

namespace WhereUAtNative.Views;

public partial class MapPage : ContentPage
{
    private readonly MapViewModel _viewModel;
    private Pin? _userPin;

    public MapPage(MapViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.OnAppearingAsync();
        SyncPinFromViewModel();
    }

    protected override void OnDisappearing()
    {
        _viewModel.OnDisappearing();
        base.OnDisappearing();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MapViewModel.HasPin)
            or nameof(MapViewModel.PinLatitude)
            or nameof(MapViewModel.PinLongitude))
        {
            MainThread.BeginInvokeOnMainThread(SyncPinFromViewModel);
        }
    }

    private void SyncPinFromViewModel()
    {
        if (UserMap is not MapControl map)
            return;

        map.Pins.Clear();
        _userPin = null;

        if (!_viewModel.HasPin)
            return;

        var position = new Microsoft.Maui.Devices.Sensors.Location(_viewModel.PinLatitude, _viewModel.PinLongitude);
        _userPin = new Pin
        {
            Label = "You",
            Type = PinType.Place,
            Location = position
        };
        map.Pins.Add(_userPin);

        try
        {
            var span = MapSpan.FromCenterAndRadius(position, Distance.FromKilometers(1));
            map.MoveToRegion(span);
        }
        catch
        {
            // Map may not be ready yet (e.g. missing Android API key) — ignore.
        }
    }
}
