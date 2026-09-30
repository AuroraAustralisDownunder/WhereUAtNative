using System.Globalization;
using WhereUAtNative.ViewModels;

namespace WhereUAtNative.Views;

public partial class MapPage : ContentPage
{
    private readonly MapViewModel _viewModel;
    private bool _mapReady;
    private string? _cachedHtml;
    private readonly HashSet<string> _renderedFamilyIds = new(StringComparer.Ordinal);
    private int _syncGate;

    public MapPage(MapViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.MarkersChanged += OnMarkersChanged;
        _viewModel.SelfPinChanged += OnSelfPinChanged;
        MapWebView.Navigating += OnMapWebViewNavigating;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            await EnsureMapLoadedAsync();
            await _viewModel.OnAppearingAsync();
            await EnsureBridgeAndSyncAsync();
        }
        catch
        {
            // async void — never crash the first map frame (auth/token/WebView races).
        }
    }

    protected override void OnDisappearing()
    {
        _viewModel.OnDisappearing();
        base.OnDisappearing();
    }

    private void OnMarkersChanged(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () => await SyncFamilyMarkersAsync());
    }

    private void OnSelfPinChanged(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () => await SyncSelfPinAsync(forceCenter: true));
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MapViewModel.HasSelfPin)
            or nameof(MapViewModel.PinLatitude)
            or nameof(MapViewModel.PinLongitude)
            or nameof(MapViewModel.HasMapContent)
            or nameof(MapViewModel.StatusMessage)
            or nameof(MapViewModel.ShowEmptyOverlay))
        {
            MainThread.BeginInvokeOnMainThread(async () => await SyncMapFromViewModelAsync());
        }
    }

    private async void OnMapWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        _mapReady = e.Result == WebNavigationResult.Success;
        if (_mapReady)
            await EnsureBridgeAndSyncAsync();
    }

    /// <summary>
    /// Map loads local HTML only (HtmlWebViewSource). Block top-level http(s) navigations
    /// (e.g. attribution clicks). Leaflet/OSM CDN + tiles are subresources, not navigations.
    /// </summary>
    private void OnMapWebViewNavigating(object? sender, WebNavigatingEventArgs e)
    {
        var url = e.Url ?? string.Empty;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
        }
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
        catch (Exception)
        {
            System.Diagnostics.Debug.WriteLine("Failed to load map.html from app package.");
            MapWebView.Source = new HtmlWebViewSource
            {
                Html = "<html><body style='font-family:sans-serif;padding:24px;color:#555;background:#1C1C1E'>Map assets failed to load.</body></html>"
            };
        }
    }

    private static async Task<string> LoadMapHtmlAsync()
    {
        await using var stream = await FileSystem.OpenAppPackageFileAsync("map.html");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    /// <summary>
    /// Wait briefly for window.setPin to exist (Leaflet CDN + inline init), then sync markers.
    /// </summary>
    private async Task EnsureBridgeAndSyncAsync()
    {
        if (!_mapReady)
            return;

        for (var i = 0; i < 20; i++)
        {
            try
            {
                var ready = await MapWebView.EvaluateJavaScriptAsync(
                    "(function(){ return (typeof window.setPin === 'function' && typeof window.upsertUser === 'function') ? '1' : '0'; })()");
                if (ready is not null && ready.Contains('1'))
                {
                    await SyncMapFromViewModelAsync();
                    return;
                }
            }
            catch
            {
                // WebView not ready
            }

            await Task.Delay(100);
        }

        // Last attempt even if bridge check failed — Sync catches errors.
        await SyncMapFromViewModelAsync();
    }

    private async Task SyncMapFromViewModelAsync()
    {
        if (!_mapReady)
            return;

        if (Interlocked.CompareExchange(ref _syncGate, 1, 0) != 0)
            return;

        try
        {
            if (!_viewModel.HasMapContent)
            {
                var msg = EscapeJs(_viewModel.StatusMessage);
                await MapWebView.EvaluateJavaScriptAsync($"clearPin('{msg}')");
                _renderedFamilyIds.Clear();
                return;
            }

            await SyncSelfPinAsync(forceCenter: false);
            await SyncFamilyMarkersAsync();
        }
        catch
        {
            // WebView may not be ready yet — ignore.
        }
        finally
        {
            Interlocked.Exchange(ref _syncGate, 0);
        }
    }

    private async Task SyncSelfPinAsync(bool forceCenter)
    {
        if (!_mapReady)
            return;

        try
        {
            if (_viewModel.HasSelfPin)
            {
                var lat = _viewModel.PinLatitude.ToString(CultureInfo.InvariantCulture);
                var lon = _viewModel.PinLongitude.ToString(CultureInfo.InvariantCulture);
                var centerFlag = forceCenter ? "true" : "false";
                await MapWebView.EvaluateJavaScriptAsync($"setPin({lat}, {lon}, {centerFlag})");
            }
            else if (_viewModel.HasMapContent)
            {
                await MapWebView.EvaluateJavaScriptAsync("removeUser('self')");
            }
        }
        catch
        {
            // ignore WebView race
        }
    }

    private async Task SyncFamilyMarkersAsync()
    {
        if (!_mapReady)
            return;

        try
        {
            var current = _viewModel.FamilyMarkers;
            var nextIds = new HashSet<string>(current.Keys, StringComparer.Ordinal);

            foreach (var stale in _renderedFamilyIds.Where(id => !nextIds.Contains(id)).ToList())
            {
                await MapWebView.EvaluateJavaScriptAsync($"removeUser('{EscapeJs(stale)}')");
                _renderedFamilyIds.Remove(stale);
            }

            foreach (var (id, (lat, lon, label)) in current)
            {
                var latS = lat.ToString(CultureInfo.InvariantCulture);
                var lonS = lon.ToString(CultureInfo.InvariantCulture);
                var labelS = EscapeJs(label);
                var idS = EscapeJs(id);
                await MapWebView.EvaluateJavaScriptAsync($"upsertUser('{idS}', {latS}, {lonS}, '{labelS}')");
                _renderedFamilyIds.Add(id);
            }
        }
        catch
        {
            // ignore WebView race
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
