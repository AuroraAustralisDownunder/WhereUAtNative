namespace WhereUAtNative.Services;

/// <summary>
/// Privacy-first family groups + live locations via Firebase Realtime Database REST.
/// Location is only written while the user has Share my location ON.
/// </summary>
public interface IFamilyService
{
    string? CurrentFamilyId { get; }

    event EventHandler? FamilyChanged;

    Task RefreshMembershipAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a family and returns the 6-char invite code, or an error message.</summary>
    Task<(string? Code, string? Error)> CreateFamilyAsync(CancellationToken cancellationToken = default);

    /// <summary>Joins an existing family by invite code.</summary>
    Task<string?> JoinFamilyAsync(string code, CancellationToken cancellationToken = default);

    Task<string?> LeaveFamilyAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FamilyMemberInfo>> GetMembersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads own location when sharing is enabled and user is in a family.
    /// No-op otherwise. Never throws to callers.
    /// </summary>
    Task PublishLocationAsync(double lat, double lon, CancellationToken cancellationToken = default);

    /// <summary>Removes own shared location (sharing off / leave / sign-out).</summary>
    Task ClearPublishedLocationAsync(CancellationToken cancellationToken = default);

    /// <summary>Locations of members currently sharing (excludes self if desired by caller).</summary>
    Task<IReadOnlyList<FamilyLocation>> GetFamilyLocationsAsync(CancellationToken cancellationToken = default);
}
