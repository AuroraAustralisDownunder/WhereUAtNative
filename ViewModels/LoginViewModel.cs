using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using WhereUAtNative.Services;

namespace WhereUAtNative.ViewModels;

public class LoginViewModel : INotifyPropertyChanged
{
    private readonly IAuthService _authService;

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

    private string? _statusMessage;
    public string? StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    private bool _isStatusVisible;
    public bool IsStatusVisible
    {
        get => _isStatusVisible;
        set { _isStatusVisible = value; OnPropertyChanged(); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNotBusy));
            ((Command)LoginCommand).ChangeCanExecute();
        }
    }

    public bool IsNotBusy => !IsBusy;

    public ICommand LoginCommand { get; }

    public LoginViewModel(IAuthService authService)
    {
        _authService = authService;
        LoginCommand = new Command(async () => await PerformLoginAsync(), () => !IsBusy);
    }

    private async Task PerformLoginAsync()
    {
        if (IsBusy)
            return;

        IsErrorVisible = false;
        IsStatusVisible = false;
        ErrorMessage = null;
        StatusMessage = null;

        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Please enter an email and password.";
            IsErrorVisible = true;
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Signing in…";
            IsStatusVisible = true;

            var error = await _authService.SignInAsync(Email, Password);
            if (error is not null)
            {
                IsStatusVisible = false;
                StatusMessage = null;
                ErrorMessage = error;
                IsErrorVisible = true;
                return;
            }

            Password = string.Empty;
            IsStatusVisible = false;
            StatusMessage = null;

            // Replace stack so Back does not return to login.
            await Shell.Current.GoToAsync("//MapPage");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
