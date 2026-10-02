using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Storage;

namespace WhereUAtNative.Services;

/// <summary>
/// Opt-in location. Preference defaults to false. Server upload handled by LocationSyncService when in a family.
/// Uses StartListeningForeground (Medium) plus a simple one-shot ladder — Best accuracy was starving
/// indoor/network fixes and racing the listener on Android (regression after v0.1.1 Medium path).
/// Session restore must call ResumeSharingIfEnabledAsync (listener-first) — raw GetCurrentAsync on a
/// cold process with preference=true raced one-shots against a just-started listener and hung GPS.
/// StartListeningSafeAsync trusts Geolocation.IsListeningForeground (not a stale _listening bool)
/// so OEM silent stops can restart; IsEnabled=false is a soft warning, not a hard abort.
/// </summary>
public sealed class LocationService : ILocationService
{
    public const string SharingPreferenceKey = "location_sharing_enabled";

    /// <summary>Accept a last-known fix younger than this without waiting for a fresh GPS sample.</summary>
    private static readonly TimeSpan LastKnownMaxAge = TimeSpan.FromMinutes(30);

    /// <summary>Skip starting another fresh request if we already accepted a fix this recently.</summary>
    private static readonly TimeSpan MinFreshInterval = TimeSpan.FromSeconds(4);

    private Location? _lastKnown;
    private string? _lastFailureHint;
    private DateTimeOffset _lastAcceptUtc = DateTimeOffset.MinValue;
    private int _refineInFlight;
    private int _oneShotInFlight;
    private bool _listening;
    private int _attemptCount;
    private TaskCompletionSource<Location?>? _firstFixTcs;

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
                // Soft warning only — some OEMs lie about IsEnabled. Still attempt a fix;
                // FeatureNotEnabledException / failed ladder will surface a clear hint.
                _lastFailureHint = "Device location may be off — turn on GPS/Location if no fix arrives.";
            }

            SetSharingEnabled(true);
            _attemptCount = 0;
            if (IsDeviceLocationEnabled())
                _lastFailureHint = "Sharing on — waiting for GPS fix…";
            else
                _lastFailureHint = "Device location may be off — turn on GPS/Location if no fix arrives.";

            // Continuous updates are the reliable path on Android; one-shot alone often times out.
            await StartListeningSafeAsync();

            // Brief wait for the listener's first sample before heavy one-shots.
            var fromListener = await WaitForFirstListenerFixAsync(TimeSpan.FromSeconds(3));
            if (fromListener is not null)
                return (true, null);

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
        FailFirstFixWait();
    }

    /// <summary>
    /// Session restore: preference may still say sharing is ON while in-memory listener
    /// state is cold. Mirror EnableSharingAsync (permission + listener + brief wait) so we
    /// do not immediately race StartListeningForeground with one-shot GetLocationAsync —
    /// that race is the cold-open GPS hang/retry loop. Fresh login + pin tap uses
    /// EnableSharingAsync and works; restore must take the same init path.
    /// </summary>
    public async Task ResumeSharingIfEnabledAsync()
    {
        if (!IsSharingEnabled)
            return;

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
                return;
            }

            if (!IsDeviceLocationEnabled())
            {
                // Keep preference and still attempt — OEM IsEnabled false-negatives were
                // aborting resume and leaving the FAB green with no listener.
                _lastFailureHint = "Device location may be off — turn on GPS/Location if no fix arrives.";
            }

            // Already have a fresh in-memory fix from this process — keep listening, skip ladder.
            if (_lastKnown is not null && DateTimeOffset.UtcNow - _lastAcceptUtc < MinFreshInterval)
            {
                await StartListeningSafeAsync();
                return;
            }

            _attemptCount = 0;
            if (IsDeviceLocationEnabled())
                _lastFailureHint = "Sharing on — waiting for GPS fix…";
            else
                _lastFailureHint = "Device location may be off — turn on GPS/Location if no fix arrives.";

            await StartListeningSafeAsync();

            var fromListener = await WaitForFirstListenerFixAsync(TimeSpan.FromSeconds(3));
            if (fromListener is not null)
                return;

            // Listener had a head start; one-shots are now a fallback (same as EnableSharingAsync).
            _ = await GetCurrentAsync();
        }
        catch (Exception)
        {
            _lastFailureHint ??= "Unable to resume location sharing — tap the pin or retry.";
        }
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

            // Do not hard-return on IsEnabled=false — OEMs false-negative; let one-shots
            // throw FeatureNotEnabledException if location is truly off.
            if (!IsDeviceLocationEnabled())
                _lastFailureHint = "Device location may be off — turn on GPS/Location if no fix arrives.";

            // Track whether this call is the one that starts the listener — one-shots
            // immediately after StartListeningForeground race the fused provider on Android.
            var wasListening = Geolocation.Default.IsListeningForeground;
            await StartListeningSafeAsync();
            var startedNow = !wasListening && (_listening || Geolocation.Default.IsListeningForeground);

            if (_lastKnown is not null && DateTimeOffset.UtcNow - _lastAcceptUtc < MinFreshInterval)
                return _lastKnown;

            var cached = await TryGetFreshLastKnownAsync(cancellationToken);
            if (cached is not null)
            {
                AcceptFix(cached);
                MaybeRefine(cancellationToken);
                return cached;
            }

            // Brief listener head-start before one-shots (same idea as EnableSharingAsync).
            if (startedNow)
            {
                var fromListener = await WaitForFirstListenerFixAsync(TimeSpan.FromSeconds(2));
                if (fromListener is not null)
                    return fromListener;
            }

            Interlocked.Increment(ref _attemptCount);
            _lastFailureHint = $"Sharing on — waiting for GPS (try {_attemptCount})…";

            // Avoid stacking parallel one-shots (Android fused provider often fails when doubled).
            if (Interlocked.CompareExchange(ref _oneShotInFlight, 1, 0) != 0)
                return _lastKnown;

            try
            {
                // Restore v0.1.1-style Medium-first ladder. Best accuracy often waits forever
                // indoors and races StartListeningForeground on Android.
                Location? fresh =
                    await RequestFixAsync(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(12), cancellationToken)
                    ?? await RequestFixAsync(GeolocationAccuracy.Low, TimeSpan.FromSeconds(12), cancellationToken)
                    ?? await RequestFixAsync(GeolocationAccuracy.Lowest, TimeSpan.FromSeconds(10), cancellationToken);

                // Only try High if still nothing and not already listening (listener will deliver).
                if (fresh is null && !(_listening || Geolocation.Default.IsListeningForeground))
                {
                    fresh = await RequestFixAsync(GeolocationAccuracy.High, TimeSpan.FromSeconds(10), cancellationToken);
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
        // Trust the platform flag. A stale _listening=true while the fused provider
        // already stopped would previously early-return and never restart — permanent
        // "waiting for GPS" after an OEM silent stop / app resume race.
        if (Geolocation.Default.IsListeningForeground)
        {
            _listening = true;
            if (_firstFixTcs is null || _firstFixTcs.Task.IsCompleted)
                _firstFixTcs = new TaskCompletionSource<Location?>(TaskCreationOptions.RunContinuationsAsynchronously);
            return;
        }

        _listening = false;

        try
        {
            Geolocation.Default.LocationChanged -= OnNativeLocationChanged;
            Geolocation.Default.LocationChanged += OnNativeLocationChanged;
            Geolocation.Default.ListeningFailed -= OnListeningFailed;
            Geolocation.Default.ListeningFailed += OnListeningFailed;

            // Medium matches the working v0.1.1 one-shot path; Best starved network fixes.
            var request = new GeolocationListeningRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(3));
            _firstFixTcs = new TaskCompletionSource<Location?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = await Geolocation.Default.StartListeningForegroundAsync(request);
            _listening = started || Geolocation.Default.IsListeningForeground;
            if (!_listening)
            {
                _lastFailureHint ??= "Could not start GPS listener — retrying one-shot…";
                FailFirstFixWait();
            }
        }
        catch (FeatureNotEnabledException)
        {
            _lastFailureHint = "Device location is off — turn on GPS/Location in system Settings.";
            _listening = false;
            FailFirstFixWait();
        }
        catch (PermissionException)
        {
            _lastFailureHint = "Location permission was revoked.";
            _listening = false;
            FailFirstFixWait();
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
            FailFirstFixWait();
        }
    }

    private async Task<Location?> WaitForFirstListenerFixAsync(TimeSpan timeout)
    {
        var tcs = _firstFixTcs;
        if (tcs is null)
            return _lastKnown;

        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
            if (completed == tcs.Task)
                return await tcs.Task;
        }
        catch
        {
            // ignore
        }

        return _lastKnown;
    }

    private void FailFirstFixWait()
    {
        try { _firstFixTcs?.TrySetResult(null); } catch { /* ignore */ }
        _firstFixTcs = null;
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
            FailFirstFixWait();
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
        FailFirstFixWait();

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
        try { _firstFixTcs?.TrySetResult(location); } catch { /* ignore */ }
        PositionChanged?.Invoke(this, location);
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static bool IsDeviceLocationEnabled()
    {
        // Some OEMs report IsEnabled=false while fused/network providers still deliver.
        // Callers should prefer attempting a fix and handling FeatureNotEnabledException
        // over hard-blocking on this flag alone.
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
