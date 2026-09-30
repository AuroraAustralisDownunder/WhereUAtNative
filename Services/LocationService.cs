using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Storage;

namespace WhereUAtNative.Services;

/// <summary>
/// Opt-in location. Preference defaults to false. Server upload handled by LocationSyncService when in a family.
/// </summary>
public sealed class LocationService : ILocationService
{
    public const string SharingPreferenceKey = "location_sharing_enabled";

    /// <summary>Accept a last-known fix younger than this without waiting for a fresh GPS sample.</summary>
    private static readonly TimeSpan LastKnownMaxAge = TimeSpan.FromMinutes(2);

    /// <summary>Skip starting another fresh request if we already accepted a fix this recently.</summary>
    private static readonly TimeSpan MinFreshInterval = TimeSpan.FromSeconds(8);

    private Location? _lastKnown;
    private string? _lastFailureHint;
    private DateTimeOffset _lastAcceptUtc = DateTimeOffset.MinValue;
    private int _refineInFlight;

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
            _lastFailureHint = null;

            var location = await GetCurrentAsync();
            if (location is null)
                return (true, _lastFailureHint ?? "Getting GPS…");

            return (true, null);
        }
        catch (Exception)
        {
            SetSharingEnabled(false);
            ClearLastKnown();
            _lastFailureHint = "Unable to enable location sharing right now. Please try again.";
            // Do not log precise location or crash.
            return (false, _lastFailureHint);
        }
    }

    public Task DisableSharingAsync()
    {
        SetSharingEnabled(false);
        ClearLastKnown();
        _lastFailureHint = null;
        return Task.CompletedTask;
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

            // Already have a very recent in-memory fix (e.g. poll loop) — return it and optionally refine.
            if (_lastKnown is not null && DateTimeOffset.UtcNow - _lastAcceptUtc < MinFreshInterval)
            {
                MaybeRefine(cancellationToken);
                return _lastKnown;
            }

            // Fast path: platform last-known so the pin appears immediately on cold start.
            var cached = await TryGetFreshLastKnownAsync(cancellationToken);
            if (cached is not null)
            {
                AcceptFix(cached);
                MaybeRefine(cancellationToken);
                return cached;
            }

            var fresh = await RequestFixAsync(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(12), cancellationToken);
            if (fresh is null)
            {
                // Indoor / cold start: Medium often times out — fall back to Low (network/wifi).
                _lastFailureHint = "GPS slow — trying network location…";
                fresh = await RequestFixAsync(GeolocationAccuracy.Low, TimeSpan.FromSeconds(15), cancellationToken);
            }

            if (fresh is not null)
            {
                AcceptFix(fresh);
                return fresh;
            }

            _lastFailureHint = "Still getting GPS… move near a window or wait a moment.";
            return _lastKnown; // may still be null
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
            // Swallow — never crash the UI for a GPS miss.
            _lastFailureHint ??= "Unable to read GPS right now — retrying…";
            return _lastKnown;
        }
    }

    private void MaybeRefine(CancellationToken cancellationToken)
    {
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
            var request = new GeolocationRequest(accuracy, timeout);
            return await Geolocation.Default.GetLocationAsync(request, cancellationToken);
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
        _lastKnown = location;
        _lastAcceptUtc = DateTimeOffset.UtcNow;
        _lastFailureHint = null;
        PositionChanged?.Invoke(this, location);
    }

    private static bool IsDeviceLocationEnabled()
    {
        try
        {
            return Geolocation.Default.IsEnabled;
        }
        catch
        {
            // Older / stub platforms — assume enabled and let GetLocationAsync decide.
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
