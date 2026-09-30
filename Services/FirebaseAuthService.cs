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

    public string DisplayName
    {
        get
        {
            try
            {
                var user = CrossFirebaseAuth.Current.CurrentUser;
                if (user is null)
                    return "You";

                if (!string.IsNullOrWhiteSpace(user.DisplayName))
                    return user.DisplayName!.Trim();

                var email = user.Email;
                if (!string.IsNullOrWhiteSpace(email))
                {
                    var at = email.IndexOf('@');
                    return at > 0 ? email[..at] : email;
                }
            }
            catch
            {
                // fall through
            }

            return "You";
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
        try
        {
            await CrossFirebaseAuth.Current.SignOutAsync();
        }
        catch
        {
            // Native sign-out can fail if Firebase never initialized — treat as signed out.
        }
    }

    public async Task<string?> GetIdTokenAsync(bool forceRefresh = false)
    {
        try
        {
            var user = CrossFirebaseAuth.Current.CurrentUser;
            if (user is null)
                return null;

            var result = await user.GetIdTokenResultAsync(forceRefresh);
            return result?.Token;
        }
        catch
        {
            return null;
        }
    }
}
