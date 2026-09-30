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
    private CancellationTokenSource? _reinjectCts;

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
            // Do not require Navigated alone — probe Leaflet bridge and reinject until confirmed.
            await EnsureBridgeAndSyncAsync(forceSelfCenter: true);
            StartReinjectLoop();
        }
        catch
        {
            // async void — never crash the first map frame (auth/token/WebView races).
        }
    }

    protected override void OnDisappearing()
    {
        StopReinjectLoop();
        _viewModel.OnDisappearing();
        base.OnDisappearing();
    }

    private void OnMarkersChanged(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () => await SyncFamilyMarkersAsync());
    }

    private void OnSelfPinChanged(object? sender, EventArgs e)
    {
        // Only SyncSelfPinAsync may NotifySelfPinOnMap — and only after isSelfCentered confirms.
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            // First fix + continuous lock-follow updates always try to center until locked@16.
            await EnsureBridgeAndSyncAsync(forceSelfCenter: !_centeredOnSelf || _viewModel.AwaitingMapCenter);
            if (_viewModel.HasSelfPin && !_centeredOnSelf)
                StartReinjectLoop();
        });
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Do NOT sync on StatusMessage / ShowEmptyOverlay — that legacy trigger called clearPin
        // on every "waiting for GPS…" rewrite and raced with setPin (v0.1.1 WebView empty-state).
        if (e.PropertyName is nameof(MapViewModel.HasSelfPin)
            or nameof(MapViewModel.PinLatitude)
            or nameof(MapViewModel.PinLongitude)
            or nameof(MapViewModel.HasMapContent)
            or nameof(MapViewModel.AwaitingMapCenter))
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                await SyncMapFromViewModelAsync(forceSelfCenter: !_centeredOnSelf || _viewModel.AwaitingMapCenter);
                if (_viewModel.HasSelfPin && !_centeredOnSelf)
                    StartReinjectLoop();
            });
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
                // WebView not ready — queue current self pin if any.
                QueuePendingFromViewModel(forceSelfCenter);
            }

            await Task.Delay(100);
        }

        // Last attempt even if bridge check failed — Sync catches errors.
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

            await SyncSelfPinAsync(forceCenter: forceSelfCenter || !_centeredOnSelf);
            await SyncFamilyMarkersAsync();
        }
        catch
        {
            // WebView may not be ready yet — queue for later.
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
                // Always force center until we've successfully lock-followed at zoom 16.
                var shouldCenter = forceCenter || !_centeredOnSelf || _viewModel.AwaitingMapCenter;
                var centerFlag = shouldCenter ? "true" : "false";
                var gen = Interlocked.Increment(ref _selfInjectGeneration);

                // IIFE + explicit window.setPin so Android WebView always executes and returns a status.
                var inject = await EvalAsync(
                    "(function(){ try {" +
                    " if (typeof window.setPin !== 'function') return 'NOFN';" +
                    $" var r = window.setPin({latS}, {lonS}, {centerFlag});" +
                    " if (typeof window.lockUser === 'function' && " + (shouldCenter ? "true" : "false") + ") window.lockUser('self');" +
                    " var ok = (typeof window.hasUser === 'function' && window.hasUser('self'));" +
                    " var cen = (typeof window.isSelfCentered === 'function' && window.isSelfCentered() === '1');" +
                    " return ok ? (cen ? 'OKC:' + String(r) : 'OKZ:' + String(r)) : ('MISS:' + String(r));" +
                    " } catch(e) { return 'ERR:' + String(e); } })()");

                if (inject is null || inject.Contains("NOFN", StringComparison.OrdinalIgnoreCase))
                {
                    // Bridge not ready — queue and retry via reinject loop.
                    _pendingLat = lat;
                    _pendingLon = lon;
                    _pendingForceCenter = true;
                    return;
                }

                var pinOk = inject.Contains("OK", StringComparison.OrdinalIgnoreCase);
                var centeredOk = inject.Contains("OKC", StringComparison.OrdinalIgnoreCase);

                // Give Leaflet layout/flyTo a beat, then re-check isSelfCentered.
                if (pinOk && shouldCenter && !centeredOk)
                {
                    await Task.Delay(200);
                    if (gen != _selfInjectGeneration)
                        return;
                    centeredOk = await ProbeSelfCenteredAsync();
                    if (!centeredOk)
                    {
                        // Hard re-inject with forceCenter + lockUser.
                        inject = await EvalAsync(
                            "(function(){ try {" +
                            " if (typeof window.setPin !== 'function') return 'NOFN';" +
                            $" window.setPin({latS}, {lonS}, true);" +
                            " if (typeof window.lockUser === 'function') window.lockUser('self');" +
                            " var ok = (typeof window.hasUser === 'function' && window.hasUser('self'));" +
                            " var cen = (typeof window.isSelfCentered === 'function' && window.isSelfCentered() === '1');" +
                            " return ok ? (cen ? 'OKC' : 'OKZ') : 'MISS';" +
                            " } catch(e) { return 'ERR'; } })()");
                        pinOk = inject is not null && inject.Contains("OK", StringComparison.OrdinalIgnoreCase);
                        centeredOk = inject is not null && inject.Contains("OKC", StringComparison.OrdinalIgnoreCase);
                    }
                }

                // One more delayed probe — flyTo retries in map.html land at 120/450/900ms.
                if (pinOk && shouldCenter && !centeredOk)
                {
                    await Task.Delay(500);
                    if (gen != _selfInjectGeneration)
                        return;
                    centeredOk = await ProbeSelfCenteredAsync();
                }

                if (pinOk)
                {
                    _pendingLat = null;
                    _pendingLon = null;
                    _bridgeReady = true;

                    if (centeredOk || !shouldCenter)
                    {
                        if (shouldCenter || centeredOk)
                            _centeredOnSelf = true;
                        _pendingForceCenter = false;
                        _viewModel.NotifySelfPinOnMap();
                        if (_centeredOnSelf)
                            StopReinjectLoop();
                    }
                    else
                    {
                        // Pin exists but still world/country view — keep reinjecting.
                        _centeredOnSelf = false;
                        _pendingForceCenter = true;
                    }
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

    private async Task<bool> ProbeSelfCenteredAsync()
    {
        try
        {
            var r = await EvalAsync(
                "(function(){ try { return (typeof window.isSelfCentered === 'function' && window.isSelfCentered() === '1') ? '1' : '0'; } catch(e) { return '0'; } })()");
            return IsJsTruthy(r);
        }
        catch
        {
            return false;
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
        }
        catch
        {
            // ignore WebView race
        }
    }

    /// <summary>
    /// Keep retrying setPin+flyTo until isSelfCentered (lock + zoom≥14 near self), or sharing stops.
    /// Covers WebView-not-ready and zero-size first-paint races that left the map on a default country.
    /// </summary>
    private void StartReinjectLoop()
    {
        if (!_viewModel.HasSelfPin || _centeredOnSelf)
            return;

        StopReinjectLoop();
        var cts = new CancellationTokenSource();
        _reinjectCts = cts;
        _ = RunReinjectLoopAsync(cts.Token);
    }

    private void StopReinjectLoop()
    {
        try
        {
            _reinjectCts?.Cancel();
            _reinjectCts?.Dispose();
        }
        catch
        {
            // ignore
        }
        finally
        {
            _reinjectCts = null;
        }
    }

    private async Task RunReinjectLoopAsync(CancellationToken token)
    {
        try
        {
            for (var attempt = 0; attempt < 40 && !token.IsCancellationRequested; attempt++)
            {
                if (!_viewModel.HasSelfPin)
                    return;
                if (_centeredOnSelf)
                    return;

                // Sharing turned off while we were retrying — drop queue.
                if (!_viewModel.HasSelfPin)
                {
                    _pendingLat = _pendingLon = null;
                    _pendingForceCenter = false;
                    return;
                }

                await EnsureBridgeAndSyncAsync(forceSelfCenter: true);

                if (_centeredOnSelf)
                    return;

                // Extra hard setView via JS if pin exists but zoom still low.
                if (_viewModel.HasSelfPin)
                {
                    var latS = _viewModel.PinLatitude.ToString(CultureInfo.InvariantCulture);
                    var lonS = _viewModel.PinLongitude.ToString(CultureInfo.InvariantCulture);
                    await EvalAsync(
                        "(function(){ try {" +
                        $" if (typeof window.setPin === 'function') window.setPin({latS}, {lonS}, true);" +
                        " if (typeof window.lockUser === 'function') window.lockUser('self');" +
                        " } catch(e) {} })()");
                    await Task.Delay(300, token);
                    if (await ProbeSelfCenteredAsync())
                    {
                        _centeredOnSelf = true;
                        _pendingForceCenter = false;
                        _viewModel.NotifySelfPinOnMap();
                        return;
                    }
                }

                await Task.Delay(400, token);
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        catch
        {
            // ignore
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
        // MAUI may wrap results in quotes: "1" / \"1\" / 1 / true
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
