using System.Globalization;
using WhereUAtNative.ViewModels;

namespace WhereUAtNative.Views;

public partial class MapPage : ContentPage
{
    private readonly MapViewModel _viewModel;
    private bool _mapReady;
    private string? _cachedHtml;

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
        await EnsureMapLoadedAsync();
        await _viewModel.OnAppearingAsync();
        await SyncPinFromViewModelAsync();
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
            or nameof(MapViewModel.PinLongitude)
            or nameof(MapViewModel.StatusMessage))
        {
            MainThread.BeginInvokeOnMainThread(async () => await SyncPinFromViewModelAsync());
        }
    }

    private async void OnMapWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        _mapReady = e.Result == WebNavigationResult.Success;
        if (_mapReady)
            await SyncPinFromViewModelAsync();
    }

    private async Task EnsureMapLoadedAsync()
    {
        if (MapWebView.Source is not null && _cachedHtml is not null)
            return;

        try
        {
            _cachedHtml ??= await LoadMapHtmlAsync();
            MapWebView.Source = new HtmlWebViewSource { Html = _cachedHtml };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load map.html: {ex.Message}");
            MapWebView.Source = new HtmlWebViewSource
            {
                Html = "<html><body style='font-family:sans-serif;padding:24px;color:#555'>Map assets failed to load.</body></html>"
            };
        }
    }

    private static async Task<string> LoadMapHtmlAsync()
    {
        await using var stream = await FileSystem.OpenAppPackageFileAsync("map.html");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private async Task SyncPinFromViewModelAsync()
    {
        if (!_mapReady)
            return;

        try
        {
            if (!_viewModel.HasPin)
            {
                var msg = EscapeJs(_viewModel.StatusMessage);
                await MapWebView.EvaluateJavaScriptAsync($"clearPin('{msg}')");
                return;
            }

            var lat = _viewModel.PinLatitude.ToString(CultureInfo.InvariantCulture);
            var lon = _viewModel.PinLongitude.ToString(CultureInfo.InvariantCulture);
            await MapWebView.EvaluateJavaScriptAsync($"setPin({lat}, {lon})");
        }
        catch
        {
            // WebView may not be ready yet — ignore.
        }
    }

    private static string EscapeJs(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }
}
