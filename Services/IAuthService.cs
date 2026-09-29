namespace WhereUAtNative.Services;

/// <summary>
/// Abstraction over Firebase Auth. Never logs credentials.
/// </summary>
public interface IAuthService
{
    bool IsSignedIn { get; }
    string? CurrentUserEmail { get; }
    string? CurrentUserId { get; }

    /// <summary>
    /// Signs in with email/password. Returns null on success, or a user-safe error message on failure.
    /// </summary>
    Task<string?> SignInAsync(string email, string password);

    Task SignOutAsync();
}
