using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Storage;

namespace WhereUAtNative.Services;

/// <summary>
/// Opt-in location. Preference defaults to false. Server upload handled by LocationSyncService when in a family.
/// Uses one-shot Geolocation plus StartListeningForeground so slow/cold GPS still surfaces a fix.
/// </summary>
public sealed class LocationService : ILocationService
{
    public const string SharingPreferenceKey = "location_sharing_enabled";

    /// <summary>Accept a last-known fix younger than this without waiting for a fresh GPS sample.</summary>
    private static readonly TimeSpan LastKnownMaxAge = TimeSpan.FromMinutes(30);

    /// <summary>Skip starting another fresh request if we already accepted a fix this recently.</summary>
    private static readonly TimeSpan MinFreshInterval = TimeSpan.FromSeconds(5);

    private Location? _lastKnown;
    private string? _lastFailureHint;
    private DateTimeOffset _lastAcceptUtc = DateTimeOffset.MinValue;
    private int _refineInFlight;
    private int _oneShotInFlight;
    private bool _listening;
    private int _attemptCount;

    public bool IsSharingEnabled
    {
        get
        {
            try { return Preferences.Default.Get(SharingPreferenceKey, false); }
            catch { return false; }
        }
    }

    public Location? LastKnownLocation => _lastKnown;

    /// <summary>Friendly hint after the latest failed/pending fix (no precise coords).</summary>
    public string? LastFailureHint => _lastFailureHint;

    public event EventHandler<Location?>? PositionChanged;

    public async Task<(bool Success, string? Message)> EnableSharingAsync()
    {
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (status != PermissionStatus.Granted)
                status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();

            if (status != PermissionStatus.Granted)
            {
                SetSharingEnabled(false);
                ClearLastKnown();
                _lastFailureHint = "Permission needed — allow location in system Settings, then tap the pin again.";
                return (false, _lastFailureHint);
            }

            if (!IsDeviceLocationEnabled())
            {
                SetSharingEnabled(false);
                ClearLastKnown();
                _lastFailureHint = "Device location is off — turn on GPS/Location in system Settings.";
                return (false, _lastFailureHint);
            }

            SetSharingEnabled(true);
            _attemptCount = 0;
            _lastFailureHint = "Sharing on — waiting for GPS fix…";

            // Continuous updates are the reliable path on Android; one-shot alone often times out.
            await StartListeningSafeAsync();

            var location = await GetCurrentAsync();
            if (location is null)
                return (true, _lastFailureHint ?? "Sharing on — waiting for GPS fix…");

            return (true, null);
        }
        catch (Exception)
        {
            await StopListeningSafeAsync();
            SetSharingEnabled(false);
            ClearLastKnown();
            _lastFailureHint = "Unable to enable location sharing right now. Please try again.";
            return (false, _lastFailureHint);
        }
    }

    public async Task DisableSharingAsync()
    {
        await StopListeningSafeAsync();
        SetSharingEnabled(false);
        ClearLastKnown();
        _lastFailureHint = null;
        _attemptCount = 0;
    }

    public async Task<Location?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSharingEnabled)
            return null;

        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (status != PermissionStatus.Granted)
            {
                await StopListeningSafeAsync();
                SetSharingEnabled(false);
                ClearLastKnown();
                _lastFailureHint = "Location permission was revoked.";
                return null;
            }

            if (!IsDeviceLocationEnabled())
            {
                _lastFailureHint = "Device location is off — turn on GPS/Location in system Settings.";
                return null;
            }

            await StartListeningSafeAsync();

            if (_lastKnown is not null && DateTimeOffset.UtcNow - _lastAcceptUtc < MinFreshInterval)
                return _lastKnown;

            var cached = await TryGetFreshLastKnownAsync(cancellationToken);
            if (cached is not null)
            {
                AcceptFix(cached);
                MaybeRefine(cancellationToken);
                return cached;
            }

            Interlocked.Increment(ref _attemptCount);
            _lastFailureHint = $"Sharing on — waiting for GPS (try {_attemptCount})…";

            // Avoid stacking parallel one-shots (Android fused provider often fails when doubled).
            if (Interlocked.CompareExchange(ref _oneShotInFlight, 1, 0) != 0)
                return _lastKnown;

            try
            {
                // When the foreground listener is running, keep one-shots short — the listener
                // will deliver the real fix. Without a listener, walk a fuller accuracy ladder.
                Location? fresh;
                if (_listening || Geolocation.Default.IsListeningForeground)
                {
                    fresh =
                        await RequestFixAsync(GeolocationAccuracy.Best, TimeSpan.FromSeconds(8), cancellationToken)
                        ?? await RequestFixAsync(GeolocationAccuracy.Low, TimeSpan.FromSeconds(8), cancellationToken);
                }
                else
                {
                    fresh =
                        await RequestFixAsync(GeolocationAccuracy.Best, TimeSpan.FromSeconds(12), cancellationToken)
                        ?? await RequestFixAsync(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(10), cancellationToken)
                        ?? await RequestFixAsync(GeolocationAccuracy.Low, TimeSpan.FromSeconds(12), cancellationToken)
                        ?? await RequestFixAsync(GeolocationAccuracy.Lowest, TimeSpan.FromSeconds(10), cancellationToken);
                }

                if (fresh is not null)
                {
                    AcceptFix(fresh);
                    return fresh;
                }

                // Listener may still AcceptFix asynchronously after we return.
                _lastFailureHint = _listening
                    ? $"Sharing on — GPS listening, no fix yet (try {_attemptCount}). Move near a window."
                    : $"Still getting GPS (try {_attemptCount})… move near a window or wait a moment.";
                return _lastKnown;
            }
            finally
            {
                Interlocked.Exchange(ref _oneShotInFlight, 0);
            }
        }
        catch (FeatureNotSupportedException)
        {
            _lastFailureHint = "This device does not support location.";
            return null;
        }
        catch (FeatureNotEnabledException)
        {
            _lastFailureHint = "Device location is off — turn on GPS/Location in system Settings.";
            return null;
        }
        catch (PermissionException)
        {
            await StopListeningSafeAsync();
            SetSharingEnabled(false);
            ClearLastKnown();
            _lastFailureHint = "Location permission was revoked.";
            return null;
        }
        catch (OperationCanceledException)
        {
            _lastFailureHint ??= "GPS request timed out — retrying…";
            return _lastKnown;
        }
        catch (Exception)
        {
            _lastFailureHint ??= "Unable to read GPS right now — retrying…";
            return _lastKnown;
        }
    }

    private async Task StartListeningSafeAsync()
    {
        if (_listening || Geolocation.Default.IsListeningForeground)
        {
            _listening = Geolocation.Default.IsListeningForeground;
            return;
        }

        try
        {
            Geolocation.Default.LocationChanged -= OnNativeLocationChanged;
            Geolocation.Default.LocationChanged += OnNativeLocationChanged;
            Geolocation.Default.ListeningFailed -= OnListeningFailed;
            Geolocation.Default.ListeningFailed += OnListeningFailed;

            var request = new GeolocationListeningRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(5));
            var started = await Geolocation.Default.StartListeningForegroundAsync(request);
            _listening = started || Geolocation.Default.IsListeningForeground;
            if (!_listening)
                _lastFailureHint ??= "Could not start GPS listener — retrying one-shot…";
        }
        catch (FeatureNotEnabledException)
        {
            _lastFailureHint = "Device location is off — turn on GPS/Location in system Settings.";
            _listening = false;
        }
        catch (PermissionException)
        {
            _lastFailureHint = "Location permission was revoked.";
            _listening = false;
        }
        catch (InvalidOperationException)
        {
            // Already listening (or platform quirk).
            _listening = Geolocation.Default.IsListeningForeground;
        }
        catch (Exception)
        {
            _listening = Geolocation.Default.IsListeningForeground;
            _lastFailureHint ??= "GPS listener unavailable — using one-shot requests.";
        }
    }

    private Task StopListeningSafeAsync()
    {
        try
        {
            Geolocation.Default.LocationChanged -= OnNativeLocationChanged;
            Geolocation.Default.ListeningFailed -= OnListeningFailed;
            if (Geolocation.Default.IsListeningForeground)
                Geolocation.Default.StopListeningForeground();
        }
        catch
        {
            // ignore
        }
        finally
        {
            _listening = false;
        }

        return Task.CompletedTask;
    }

    private void OnNativeLocationChanged(object? sender, GeolocationLocationChangedEventArgs e)
    {
        if (!IsSharingEnabled || e.Location is null)
            return;

        AcceptFix(e.Location);
    }

    private void OnListeningFailed(object? sender, GeolocationListeningFailedEventArgs e)
    {
        _listening = false;
        _lastFailureHint = e.Error switch
        {
            GeolocationError.Unauthorized => "Location permission was revoked.",
            GeolocationError.PositionUnavailable => "GPS unavailable — move near a window or check Location mode.",
            _ => "GPS listener failed — retrying…"
        };

        if (IsSharingEnabled)
            _ = RestartListeningSoonAsync();
    }

    private async Task RestartListeningSoonAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (IsSharingEnabled)
                await StartListeningSafeAsync();
        }
        catch
        {
            // ignore
        }
    }

    private void MaybeRefine(CancellationToken cancellationToken)
    {
        if (_listening || Geolocation.Default.IsListeningForeground)
            return;
        if (Interlocked.CompareExchange(ref _refineInFlight, 1, 0) != 0)
            return;

        _ = RefineThenClearFlagAsync(cancellationToken);
    }

    private async Task RefineThenClearFlagAsync(CancellationToken cancellationToken)
    {
        try
        {
            var fresh = await RequestFixAsync(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(12), cancellationToken);
            fresh ??= await RequestFixAsync(GeolocationAccuracy.Low, TimeSpan.FromSeconds(10), cancellationToken);
            if (fresh is not null && IsSharingEnabled)
                AcceptFix(fresh);
        }
        catch
        {
            // Background refine is best-effort.
        }
        finally
        {
            Interlocked.Exchange(ref _refineInFlight, 0);
        }
    }

    private async Task<Location?> TryGetFreshLastKnownAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var last = await Geolocation.Default.GetLastKnownLocationAsync();
            if (last is null)
                return null;

            var age = DateTimeOffset.UtcNow - last.Timestamp;
            if (age < TimeSpan.Zero)
                age = TimeSpan.Zero;
            if (age > LastKnownMaxAge)
                return null;

            return last;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<Location?> RequestFixAsync(
        GeolocationAccuracy accuracy,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            // Prefer the request's own timeout; only link caller cancel when it can fire.
            var request = new GeolocationRequest(accuracy, timeout);
            if (cancellationToken.CanBeCanceled)
                return await Geolocation.Default.GetLocationAsync(request, cancellationToken);
            return await Geolocation.Default.GetLocationAsync(request);
        }
        catch (FeatureNotEnabledException)
        {
            throw;
        }
        catch (PermissionException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void AcceptFix(Location location)
    {
        if (!IsFinite(location.Latitude) || !IsFinite(location.Longitude))
            return;

        // Ignore null-island / clearly invalid readings.
        if (Math.Abs(location.Latitude) < 0.0001 && Math.Abs(location.Longitude) < 0.0001)
            return;

        _lastKnown = location;
        _lastAcceptUtc = DateTimeOffset.UtcNow;
        _lastFailureHint = null;
        PositionChanged?.Invoke(this, location);
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static bool IsDeviceLocationEnabled()
    {
        try
        {
            return Geolocation.Default.IsEnabled;
        }
        catch
        {
            return true;
        }
    }

    private static void SetSharingEnabled(bool enabled)
        => Preferences.Default.Set(SharingPreferenceKey, enabled);

    private void ClearLastKnown()
    {
        _lastKnown = null;
        _lastAcceptUtc = DateTimeOffset.MinValue;
        PositionChanged?.Invoke(this, null);
    }
}
