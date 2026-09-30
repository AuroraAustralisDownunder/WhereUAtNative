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
    private bool _centeredOnSelf;
    private int _selfInjectGeneration;

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
            _centeredOnSelf = false;
            await EnsureMapLoadedAsync();
            await _viewModel.OnAppearingAsync();
            await EnsureBridgeAndSyncAsync(forceSelfCenter: true);
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
        // Always force-center on dedicated self-pin events (first fix + follow updates).
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await SyncSelfPinAsync(forceCenter: true);
            if (_viewModel.HasSelfPin)
                _viewModel.NotifySelfPinOnMap();
        });
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Do NOT sync on StatusMessage / ShowEmptyOverlay — that legacy trigger called clearPin
        // on every "waiting for GPS…" rewrite and raced with setPin (v0.1.1 WebView empty-state).
        if (e.PropertyName is nameof(MapViewModel.HasSelfPin)
            or nameof(MapViewModel.PinLatitude)
            or nameof(MapViewModel.PinLongitude)
            or nameof(MapViewModel.HasMapContent))
        {
            MainThread.BeginInvokeOnMainThread(async () => await SyncMapFromViewModelAsync());
        }
    }

    private async void OnMapWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        _mapReady = e.Result == WebNavigationResult.Success;
        _centeredOnSelf = false;
        if (_mapReady)
            await EnsureBridgeAndSyncAsync(forceSelfCenter: true);
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
    private async Task EnsureBridgeAndSyncAsync(bool forceSelfCenter = false)
    {
        if (!_mapReady)
            return;

        for (var i = 0; i < 30; i++)
        {
            try
            {
                var ready = await MapWebView.EvaluateJavaScriptAsync(
                    "(function(){ try { return (typeof window.setPin === 'function' && typeof window.upsertUser === 'function' && typeof L !== 'undefined') ? '1' : '0'; } catch(e) { return '0'; } })()");
                if (ready is not null && ready.Contains('1'))
                {
                    await SyncMapFromViewModelAsync(forceSelfCenter: forceSelfCenter);
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
        await SyncMapFromViewModelAsync(forceSelfCenter: forceSelfCenter);
    }

    private async Task SyncMapFromViewModelAsync(bool forceSelfCenter = false)
    {
        if (!_mapReady)
            return;

        if (Interlocked.CompareExchange(ref _syncGate, 1, 0) != 0)
        {
            // A sync is in flight — still push self pin without taking the gate so we never
            // drop a fix behind a clearPin/status race.
            if (_viewModel.HasSelfPin)
                await SyncSelfPinAsync(forceCenter: forceSelfCenter || !_centeredOnSelf);
            return;
        }

        try
        {
            // Re-read after acquiring the gate — StatusMessage used to trigger clearPin with a
            // stale !HasMapContent snapshot and wipe a pin set concurrently via SelfPinChanged.
            if (!_viewModel.HasMapContent)
            {
                await EvalAsync("clearPin('')");
                _renderedFamilyIds.Clear();
                _centeredOnSelf = false;
                return;
            }

            await SyncSelfPinAsync(forceCenter: forceSelfCenter || !_centeredOnSelf);
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
                // Always force center until we've successfully locked onto self once this session,
                // restoring v0.1.1 lock-follow UX on first fix.
                var shouldCenter = forceCenter || !_centeredOnSelf;
                var centerFlag = shouldCenter ? "true" : "false";
                var gen = Interlocked.Increment(ref _selfInjectGeneration);

                await EvalAsync($"setPin({lat}, {lon}, {centerFlag})");

                // Verify the JS bridge actually created the self marker; retry once if not.
                var hasSelf = await EvalAsync(
                    "(function(){ try { return (typeof window.hasUser === 'function' && window.hasUser('self')) ? '1' : '0'; } catch(e) { return '0'; } })()");
                if (hasSelf is null || !hasSelf.Contains('1'))
                {
                    await Task.Delay(150);
                    if (gen == _selfInjectGeneration)
                    {
                        await EvalAsync($"setPin({lat}, {lon}, true)");
                        hasSelf = await EvalAsync(
                            "(function(){ try { return (typeof window.hasUser === 'function' && window.hasUser('self')) ? '1' : '0'; } catch(e) { return '0'; } })()");
                    }
                }

                if (hasSelf is not null && hasSelf.Contains('1'))
                {
                    if (shouldCenter)
                        _centeredOnSelf = true;
                    _viewModel.NotifySelfPinOnMap();
                }
            }
            else if (_viewModel.HasMapContent)
            {
                await EvalAsync("removeUser('self')");
                _centeredOnSelf = false;
            }
            else
            {
                _centeredOnSelf = false;
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
                await EvalAsync($"removeUser('{EscapeJs(stale)}')");
                _renderedFamilyIds.Remove(stale);
            }

            foreach (var (id, (lat, lon, label)) in current)
            {
                var latS = lat.ToString(CultureInfo.InvariantCulture);
                var lonS = lon.ToString(CultureInfo.InvariantCulture);
                var labelS = EscapeJs(label);
                var idS = EscapeJs(id);
                await EvalAsync($"upsertUser('{idS}', {latS}, {lonS}, '{labelS}')");
                _renderedFamilyIds.Add(id);
            }
        }
        catch
        {
            // ignore WebView race
        }
    }

    private async Task<string?> EvalAsync(string script)
    {
        try
        {
            return await MapWebView.EvaluateJavaScriptAsync(script);
        }
        catch
        {
            return null;
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
