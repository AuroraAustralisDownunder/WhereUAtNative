using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Plugin.Firebase.Auth;

namespace WhereUAtNative.ViewModels;

public class LoginViewModel : INotifyPropertyChanged
{
    private string? _email;
    public string? Email
    {
        get => _email;
        set { _email = value; OnPropertyChanged(); }
    }

    private string? _password;
    public string? Password
    {
        get => _password;
        set { _password = value; OnPropertyChanged(); }
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        set { _errorMessage = value; OnPropertyChanged(); }
    }

    private bool _isErrorVisible;
    public bool IsErrorVisible
    {
        get => _isErrorVisible;
        set { _isErrorVisible = value; OnPropertyChanged(); }
    }

    public ICommand LoginCommand { get; }

    public LoginViewModel()
    {
        LoginCommand = new Command(async () => await PerformLogin());
    }

    private async Task PerformLogin()
    {
        IsErrorVisible = false;

        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Please enter an email and password.";
            IsErrorVisible = true;
            return;
        }

        try
        {
            ErrorMessage = "Authenticating securely...";
            IsErrorVisible = true; 

            // This talks directly to the native Firebase SDKs we configured
            var authResult = await CrossFirebaseAuth.Current.SignInWithEmailAndPasswordAsync(Email, Password);

            // If it succeeds, we update the UI to show success
            ErrorMessage = "Success! Securing connection...";
            
            // NOTE: Our next step will be navigating away from this page to the Map view!
        }
        catch (Exception)
        {
            // We intentionally catch the raw exception here rather than displaying it.
            // This displays a secure, generic message if the login fails.
            ErrorMessage = "Login failed. Please verify your credentials.";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}