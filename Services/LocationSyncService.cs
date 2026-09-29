using Microsoft.Maui.Devices.Sensors;

namespace WhereUAtNative.Services;

/// <summary>
/// Forwards opt-in GPS fixes to the family location node. Foreground only.
/// </summary>
public sealed class LocationSyncService : IDisposable
{
    private readonly ILocationService _location;
    private readonly IFamilyService _family;
    private bool _started;

    public LocationSyncService(ILocationService location, IFamilyService family)
    {
        _location = location;
        _family = family;
    }

    public void Start()
    {
        if (_started)
            return;
        _started = true;
        _location.PositionChanged += OnPositionChanged;
        _family.FamilyChanged += OnFamilyChanged;
    }

    private void OnFamilyChanged(object? sender, EventArgs e)
    {
        // If we just joined/created and already have a fix, publish promptly.
        if (_location.IsSharingEnabled && _location.LastKnownLocation is { } loc)
            _ = _family.PublishLocationAsync(loc.Latitude, loc.Longitude);
        else if (!_location.IsSharingEnabled)
            _ = _family.ClearPublishedLocationAsync();
    }

    private void OnPositionChanged(object? sender, Location? location)
    {
        if (location is null || !_location.IsSharingEnabled)
        {
            _ = _family.ClearPublishedLocationAsync();
            return;
        }

        _ = _family.PublishLocationAsync(location.Latitude, location.Longitude);
    }

    public void Dispose()
    {
        if (!_started)
            return;
        _location.PositionChanged -= OnPositionChanged;
        _family.FamilyChanged -= OnFamilyChanged;
        _started = false;
    }
}
