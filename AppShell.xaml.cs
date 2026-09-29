using WhereUAtNative.Services;
using WhereUAtNative.Views;

namespace WhereUAtNative;

public partial class AppShell : Shell
{
    private readonly IAuthService _authService;
    private bool _startupNavigationDone;

    public AppShell(IAuthService authService, LoginPage loginPage, HomePage homePage)
    {
        _authService = authService;
        InitializeComponent();

        // Resolve pages from DI so ViewModels are injected (Shell DataTemplate would not).
        Items.Add(new ShellContent
        {
            Title = "Login",
            Route = "LoginPage",
            Content = loginPage,
            FlyoutItemIsVisible = false
        });

        Items.Add(new ShellContent
        {
            Title = "Home",
            Route = "HomePage",
            Content = homePage,
            FlyoutItemIsVisible = false
        });

        Loaded += OnShellLoaded;
    }

    private async void OnShellLoaded(object? sender, EventArgs e)
    {
        if (_startupNavigationDone)
            return;

        _startupNavigationDone = true;

        try
        {
            if (_authService.IsSignedIn)
                await GoToAsync("//HomePage");
            else
                await GoToAsync("//LoginPage");
        }
        catch
        {
            await GoToAsync("//LoginPage");
        }
    }
}
