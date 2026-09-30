using Microsoft.Maui.Devices.Sensors;

namespace WhereUAtNative.Services;

/// <summary>
/// Opt-in location sharing. Off by default; uploads only while Share my location is ON
/// and the user belongs to a family (via LocationSyncService → Realtime Database).
/// </summary>
public interface ILocationService
{
    /// <summary>True when the user has opted in locally (device Preferences). Default false.</summary>
    bool IsSharingEnabled { get; }

    /// <summary>Last successful fix while sharing is on; null otherwise.</summary>
    Location? LastKnownLocation { get; }

    /// <summary>Friendly hint after the latest failed/pending fix (no precise coords).</summary>
    string? LastFailureHint { get; }

    /// <summary>Raised when a new fix is obtained or when sharing is turned off (null).</summary>
    event EventHandler<Location?>? PositionChanged;

    /// <summary>
    /// Requests when-in-use permission and enables sharing.
    /// Returns (true, null) on success; (false, friendlyMessage) if permission denied or unavailable.
    /// Leaves sharing off on failure.
    /// </summary>
    Task<(bool Success, string? Message)> EnableSharingAsync();

    /// <summary>Disables sharing, clears last known position, and stops foreground listening.</summary>
    Task DisableSharingAsync();

    /// <summary>
    /// Cold-start / session-restore: if the sharing preference is already on, re-validate
    /// permission and start the foreground listener the same way as EnableSharingAsync
    /// (brief listener wait before one-shots). No-op when sharing is off. Turns sharing
    /// off if permission is missing.
    /// </summary>
    Task ResumeSharingIfEnabledAsync();

    /// <summary>
    /// Gets a current position when sharing is enabled. Returns null if sharing is off,
    /// permission is missing, GPS is unavailable, or the request fails. Never throws to callers.
    /// </summary>
    Task<Location?> GetCurrentAsync(CancellationToken cancellationToken = default);
}
