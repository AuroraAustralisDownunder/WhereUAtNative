using WhereUAtNative.Services;
using WhereUAtNative.Views;

namespace WhereUAtNative;

public partial class AppShell : Shell
{
    private readonly IAuthService _authService;
    private readonly ICrashLogService _crashLog;
    private bool _startupNavigationDone;

    public AppShell(IAuthService authService, ICrashLogService crashLog, LoginPage loginPage, MapPage mapPage)
    {
        _authService = authService;
        _crashLog = crashLog;
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
        // Type is registered in DI; Shell resolves via the service provider when possible.
        Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));

        Loaded += OnShellLoaded;
    }

    /// <summary>
    /// First-frame routing. async void must NEVER throw — nested try/catch so a failed
    /// Map navigation or stale Firebase session falls back to Login without crashing.
    /// Caught failures are written to the on-device crash log (not rethrown).
    /// </summary>
    private async void OnShellLoaded(object? sender, EventArgs e)
    {
        if (_startupNavigationDone)
            return;

        _startupNavigationDone = true;

        try
        {
            var signedIn = false;
            try
            {
                signedIn = _authService.IsSignedIn;
            }
            catch (Exception ex)
            {
                // Firebase not ready / token missing / native NRE — treat as logged out.
                _crashLog.LogError("Startup: IsSignedIn check failed", ex);
                signedIn = false;
            }

            if (signedIn)
            {
                try
                {
                    await GoToAsync("//MapPage");
                    return;
                }
                catch (Exception ex)
                {
                    // Navigation race or page already attached — fall through to Login.
                    _crashLog.LogError("Startup: GoTo MapPage failed", ex);
                }
            }

            try
            {
                await GoToAsync("//LoginPage");
            }
            catch (Exception ex)
            {
                // Already on Login or Shell not ready — stay put; never crash.
                _crashLog.LogError("Startup: GoTo LoginPage failed", ex);
            }
        }
        catch (Exception ex)
        {
            // Absolute last resort for async void handlers.
            _crashLog.LogError("Startup: OnShellLoaded unexpected failure", ex);
        }
    }
}
