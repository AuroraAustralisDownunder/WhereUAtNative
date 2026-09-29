using WhereUAtNative.Services;
using WhereUAtNative.Views;

namespace WhereUAtNative;

public partial class AppShell : Shell
{
    private readonly IAuthService _authService;
    private bool _startupNavigationDone;

    public AppShell(IAuthService authService, LoginPage loginPage, MapPage mapPage)
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
            Title = "Map",
            Route = "MapPage",
            Content = mapPage,
            FlyoutItemIsVisible = false
        });

        // Settings is pushed onto the navigation stack from the map toolbar.
        Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));

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
                await GoToAsync("//MapPage");
            else
                await GoToAsync("//LoginPage");
        }
        catch
        {
            await GoToAsync("//LoginPage");
        }
    }
}
