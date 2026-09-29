namespace WhereUAtNative.Services;

/// <summary>
/// Abstraction over Firebase Auth. Never logs credentials.
/// </summary>
public interface IAuthService
{
    bool IsSignedIn { get; }
    string? CurrentUserEmail { get; }
    string? CurrentUserId { get; }

    /// <summary>Email local-part, or "You" if unavailable.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Signs in with email/password. Returns null on success, or a user-safe error message on failure.
    /// </summary>
    Task<string?> SignInAsync(string email, string password);

    Task SignOutAsync();

    /// <summary>
    /// Firebase ID token for Realtime Database / REST. Null if signed out or refresh fails.
    /// </summary>
    Task<string?> GetIdTokenAsync(bool forceRefresh = false);
}
