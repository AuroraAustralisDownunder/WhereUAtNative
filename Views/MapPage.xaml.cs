using System.Globalization;
using WhereUAtNative.ViewModels;

namespace WhereUAtNative.Views;

public partial class MapPage : ContentPage
{
    private readonly MapViewModel _viewModel;
    private bool _navigatedOk;
    private bool _bridgeReady;
    private string? _cachedHtml;
    private readonly HashSet<string> _renderedFamilyIds = new(StringComparer.Ordinal);
    private int _syncGate;
    private bool _centeredOnSelf;
    private int _selfInjectGeneration;

    // Queue self pin when WebView/bridge is not ready yet (apply on Navigated / bridge probe).
    private double? _pendingLat;
    private double? _pendingLon;
    private bool _pendingForceCenter;

    public MapPage(MapViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.MarkersChanged += OnMarkersChanged;
        _viewModel.SelfPinChanged += OnSelfPinChanged;
        _viewModel.UnlockFollowRequested += OnUnlockFollowRequested;
        MapWebView.Navigating += OnMapWebViewNavigating;
    }

    private void OnUnlockFollowRequested(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await EvalAsync("(function(){ try { if (typeof window.unlockUser==='function') window.unlockUser(); return '1'; } catch(e) { return '0'; } })()");
                _viewModel.SetFollowLocked(false);
            }
            catch
            {
                // ignore WebView race
            }
        });
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            _centeredOnSelf = false;
            await EnsureMapLoadedAsync();

            // Cold session-restore used to await GPS (up to tens of seconds) BEFORE the
            // Leaflet bridge probe, so the restored map landed with sharing re-enabled
            // while the WebView was still loading CDN/init. Run bridge + VM in parallel;
            // then sync again once a fix may exist.
            var bridgeTask = EnsureBridgeAndSyncAsync(forceSelfCenter: true);
            var vmTask = _viewModel.OnAppearingAsync();
            await Task.WhenAll(bridgeTask, vmTask);
            await EnsureBridgeAndSyncAsync(forceSelfCenter: !_centeredOnSelf);
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
        // Force-center on dedicated self-pin events until we've locked once; confirm via hasUser.
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await EnsureBridgeAndSyncAsync(forceSelfCenter: !_centeredOnSelf);
            if (_viewModel.HasSelfPin && _centeredOnSelf)
                _viewModel.NotifySelfPinOnMap();
        });
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Do NOT sync on StatusMessage / ShowEmptyOverlay — that legacy trigger called clearPin
        // on every "waiting for GPS…" rewrite and raced with setPin (v0.1.1 WebView empty-state).
        // Do NOT sync on AwaitingMapCenter — v0.2.10's zoom-confirm loop starved GPS / UI thread.
        if (e.PropertyName is nameof(MapViewModel.HasSelfPin)
            or nameof(MapViewModel.PinLatitude)
            or nameof(MapViewModel.PinLongitude)
            or nameof(MapViewModel.HasMapContent))
        {
            MainThread.BeginInvokeOnMainThread(async () =>
                await SyncMapFromViewModelAsync(forceSelfCenter: !_centeredOnSelf));
        }
    }

    private async void OnMapWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        _navigatedOk = e.Result == WebNavigationResult.Success;
        _bridgeReady = false;
        _centeredOnSelf = false;
        if (_navigatedOk)
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
    /// Wait for window.setPin (Leaflet CDN + inline init), then sync markers.
    /// Works even when Navigated has not fired yet — probes EvaluateJavaScript directly.
    /// </summary>
    private async Task EnsureBridgeAndSyncAsync(bool forceSelfCenter = false)
    {
        if (_bridgeReady)
        {
            await SyncMapFromViewModelAsync(forceSelfCenter: forceSelfCenter);
            return;
        }

        for (var i = 0; i < 40; i++)
        {
            try
            {
                var ready = await EvalAsync(
                    "(function(){ try { return (typeof window.setPin === 'function' && typeof window.upsertUser === 'function' && typeof L !== 'undefined') ? '1' : '0'; } catch(e) { return '0'; } })()");
                if (IsJsTruthy(ready))
                {
                    _bridgeReady = true;
                    _navigatedOk = true;
                    await SyncMapFromViewModelAsync(forceSelfCenter: forceSelfCenter);
                    return;
                }
            }
            catch
            {
                QueuePendingFromViewModel(forceSelfCenter);
            }

            await Task.Delay(100);
        }

        await SyncMapFromViewModelAsync(forceSelfCenter: forceSelfCenter);
    }

    private void QueuePendingFromViewModel(bool forceCenter)
    {
        if (!_viewModel.HasSelfPin)
            return;
        _pendingLat = _viewModel.PinLatitude;
        _pendingLon = _viewModel.PinLongitude;
        _pendingForceCenter = forceCenter || !_centeredOnSelf || _pendingForceCenter;
    }

    private async Task SyncMapFromViewModelAsync(bool forceSelfCenter = false)
    {
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
                await EvalAsync("typeof clearPin==='function'&&clearPin('')");
                _renderedFamilyIds.Clear();
                _centeredOnSelf = false;
                _pendingLat = _pendingLon = null;
                _pendingForceCenter = false;
                return;
            }

            await SyncSelfPinAsync(forceCenter: forceSelfCenter || !_centeredOnSelf || _pendingForceCenter);
            await SyncFamilyMarkersAsync();
            await RefreshFollowLockStateAsync();
        }
        catch
        {
            QueuePendingFromViewModel(forceSelfCenter);
        }
        finally
        {
            Interlocked.Exchange(ref _syncGate, 0);
        }
    }

    private async Task SyncSelfPinAsync(bool forceCenter)
    {
        try
        {
            if (_viewModel.HasSelfPin)
            {
                var lat = _viewModel.PinLatitude;
                var lon = _viewModel.PinLongitude;
                var latS = lat.ToString(CultureInfo.InvariantCulture);
                var lonS = lon.ToString(CultureInfo.InvariantCulture);
                // Force center only until we've successfully put the self marker on the map once.
                // map.html setPin(force) does flyTo@16 + short layout retries — no C# reinject loop.
                var shouldCenter = forceCenter || !_centeredOnSelf || _pendingForceCenter;
                var centerFlag = shouldCenter ? "true" : "false";
                var gen = Interlocked.Increment(ref _selfInjectGeneration);

                var inject = await EvalAsync(
                    "(function(){ try {" +
                    " if (typeof window.setPin !== 'function') return 'NOFN';" +
                    $" var r = window.setPin({latS}, {lonS}, {centerFlag});" +
                    " var ok = (typeof window.hasUser === 'function' && window.hasUser('self'));" +
                    " return ok ? ('OK:' + String(r)) : ('MISS:' + String(r));" +
                    " } catch(e) { return 'ERR:' + String(e); } })()");

                if (inject is null || inject.Contains("NOFN", StringComparison.OrdinalIgnoreCase))
                {
                    _pendingLat = lat;
                    _pendingLon = lon;
                    _pendingForceCenter = true;
                    return;
                }

                var pinOk = inject.Contains("OK", StringComparison.OrdinalIgnoreCase);

                // One short retry if bridge raced — do NOT wait on zoom/isSelfCentered (v0.2.10 hang).
                if (!pinOk)
                {
                    await Task.Delay(150);
                    if (gen != _selfInjectGeneration)
                        return;
                    inject = await EvalAsync(
                        "(function(){ try {" +
                        " if (typeof window.setPin !== 'function') return 'NOFN';" +
                        $" window.setPin({latS}, {lonS}, true);" +
                        " var ok = (typeof window.hasUser === 'function' && window.hasUser('self'));" +
                        " return ok ? 'OK' : 'MISS';" +
                        " } catch(e) { return 'ERR'; } })()");
                    pinOk = inject is not null && inject.Contains("OK", StringComparison.OrdinalIgnoreCase);
                }

                if (pinOk)
                {
                    _pendingLat = null;
                    _pendingLon = null;
                    _pendingForceCenter = false;
                    _bridgeReady = true;
                    if (shouldCenter)
                        _centeredOnSelf = true;
                    _viewModel.NotifySelfPinOnMap();
                }
                else
                {
                    _pendingLat = lat;
                    _pendingLon = lon;
                    _pendingForceCenter = true;
                }
            }
            else if (_viewModel.HasMapContent)
            {
                await EvalAsync("(function(){ try { if (typeof window.removeUser==='function') window.removeUser('self'); } catch(e) {} })()");
                _centeredOnSelf = false;
                _pendingLat = _pendingLon = null;
                _pendingForceCenter = false;
            }
            else
            {
                _centeredOnSelf = false;
                _pendingLat = _pendingLon = null;
                _pendingForceCenter = false;
            }
        }
        catch
        {
            QueuePendingFromViewModel(forceCenter);
        }
    }

    private async Task RefreshFollowLockStateAsync()
    {
        try
        {
            var locked = await EvalAsync(
                "(function(){ try { return (typeof window.isLocked==='function' && window.isLocked()==='1') ? '1' : '0'; } catch(e) { return '0'; } })()");
            _viewModel.SetFollowLocked(IsJsTruthy(locked));
        }
        catch
        {
            // ignore
        }
    }

    private async Task SyncFamilyMarkersAsync()
    {
        try
        {
            var current = _viewModel.FamilyMarkers;
            var nextIds = new HashSet<string>(current.Keys, StringComparer.Ordinal);

            foreach (var stale in _renderedFamilyIds.Where(id => !nextIds.Contains(id)).ToList())
            {
                await EvalAsync($"(function(){{ try {{ window.removeUser('{EscapeJs(stale)}'); }} catch(e) {{}} }})()");
                _renderedFamilyIds.Remove(stale);
            }

            foreach (var (id, (lat, lon, label)) in current)
            {
                var latS = lat.ToString(CultureInfo.InvariantCulture);
                var lonS = lon.ToString(CultureInfo.InvariantCulture);
                var labelS = EscapeJs(label);
                var idS = EscapeJs(id);
                await EvalAsync($"(function(){{ try {{ window.upsertUser('{idS}', {latS}, {lonS}, '{labelS}'); }} catch(e) {{}} }})()");
                _renderedFamilyIds.Add(id);
            }

            await RefreshFollowLockStateAsync();
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

    private static bool IsJsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return value.Contains('1', StringComparison.Ordinal) ||
               value.Contains("true", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("OK", StringComparison.OrdinalIgnoreCase);
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
