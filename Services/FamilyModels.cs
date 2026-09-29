namespace WhereUAtNative.Services;

public sealed class FamilyMemberInfo
{
    public required string Uid { get; init; }
    public required string DisplayName { get; init; }
}

public sealed class FamilyLocation
{
    public required string Uid { get; init; }
    public required string DisplayName { get; init; }
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public bool Sharing { get; init; } = true;
    public long UpdatedAt { get; init; }
}

public sealed class FamilyState
{
    public string? FamilyId { get; init; }
    public IReadOnlyList<FamilyMemberInfo> Members { get; init; } = Array.Empty<FamilyMemberInfo>();
}
