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

    private Location? _lastKnown;

    public bool IsSharingEnabled
    {
        get
        {
            try { return Preferences.Default.Get(SharingPreferenceKey, false); }
            catch { return false; }
        }
    }

    public Location? LastKnownLocation => _lastKnown;

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
                return (false, "Permission needed — location sharing stays off until you allow access in Settings.");
            }

            SetSharingEnabled(true);

            var location = await GetCurrentAsync();
            if (location is null)
                return (true, "Waiting for GPS…");

            return (true, null);
        }
        catch (Exception)
        {
            SetSharingEnabled(false);
            ClearLastKnown();
            // Do not log precise location or crash.
            return (false, "Unable to enable location sharing right now. Please try again.");
        }
    }

    public Task DisableSharingAsync()
    {
        SetSharingEnabled(false);
        ClearLastKnown();
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
                return null;
            }

            var request = new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(12));
            var location = await Geolocation.Default.GetLocationAsync(request, cancellationToken);
            if (location is not null)
            {
                _lastKnown = location;
                PositionChanged?.Invoke(this, location);
            }

            return location;
        }
        catch (FeatureNotSupportedException)
        {
            return null;
        }
        catch (FeatureNotEnabledException)
        {
            return null;
        }
        catch (PermissionException)
        {
            SetSharingEnabled(false);
            ClearLastKnown();
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            // Swallow — never crash the UI for a GPS miss.
            return null;
        }
    }

    private static void SetSharingEnabled(bool enabled)
        => Preferences.Default.Set(SharingPreferenceKey, enabled);

    private void ClearLastKnown()
    {
        _lastKnown = null;
        PositionChanged?.Invoke(this, null);
    }
}
