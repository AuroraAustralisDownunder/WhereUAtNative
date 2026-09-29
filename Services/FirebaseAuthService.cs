using Plugin.Firebase.Auth;

namespace WhereUAtNative.Services;

public sealed class FirebaseAuthService : IAuthService
{
    private const string GenericLoginFailure = "Login failed. Please verify your credentials.";

    public bool IsSignedIn
    {
        get
        {
            try
            {
                return CrossFirebaseAuth.Current.CurrentUser is not null;
            }
            catch
            {
                // Firebase may not be initialized yet on the first Android frame.
                return false;
            }
        }
    }

    public string? CurrentUserEmail
    {
        get
        {
            try
            {
                return CrossFirebaseAuth.Current.CurrentUser?.Email;
            }
            catch
            {
                return null;
            }
        }
    }

    public string? CurrentUserId
    {
        get
        {
            try
            {
                return CrossFirebaseAuth.Current.CurrentUser?.Uid;
            }
            catch
            {
                return null;
            }
        }
    }

    public async Task<string?> SignInAsync(string email, string password)
    {
        try
        {
            await CrossFirebaseAuth.Current.SignInWithEmailAndPasswordAsync(email, password);
            return null;
        }
        catch (Exception)
        {
            // Do not surface native exception details or credentials.
            return GenericLoginFailure;
        }
    }

    public async Task SignOutAsync()
    {
        await CrossFirebaseAuth.Current.SignOutAsync();
    }
}
