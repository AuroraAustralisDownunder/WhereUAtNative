using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using WhereUAtNative.Services;

namespace WhereUAtNative.ViewModels;

public class HomeViewModel : INotifyPropertyChanged
{
    private readonly IAuthService _authService;
    private string _welcomeText = "Signed in";
    private bool _isBusy;

    public string WelcomeText
    {
        get => _welcomeText;
        set { _welcomeText = value; OnPropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotBusy)); }
    }

    public bool IsNotBusy => !IsBusy;

    public ICommand SignOutCommand { get; }

    public HomeViewModel(IAuthService authService)
    {
        _authService = authService;
        SignOutCommand = new Command(async () => await SignOutAsync(), () => !IsBusy);
        RefreshWelcome();
    }

    public void RefreshWelcome()
    {
        var email = _authService.CurrentUserEmail;
        WelcomeText = string.IsNullOrWhiteSpace(email)
            ? "Signed in"
            : $"Welcome, {email}";
    }

    private async Task SignOutAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            ((Command)SignOutCommand).ChangeCanExecute();
            await _authService.SignOutAsync();
            await Shell.Current.GoToAsync("//LoginPage");
        }
        finally
        {
            IsBusy = false;
            ((Command)SignOutCommand).ChangeCanExecute();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
