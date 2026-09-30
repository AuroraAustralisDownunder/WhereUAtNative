using WhereUAtNative.Services;

namespace WhereUAtNative;

public partial class App : Application
{
    private readonly AppShell _shell;
    private readonly ICrashLogService? _crashLog;

    public App(AppShell shell, ICrashLogService? crashLog = null)
    {
        InitializeComponent();
        _shell = shell;
        _crashLog = crashLog;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        try
        {
            return new Window(_shell);
        }
        catch (Exception ex)
        {
            try { _crashLog?.LogFatal("App.CreateWindow failed — showing fallback page", ex); }
            catch { /* ignore */ }

            // Last-resort window so a DI/shell fault does not kill the process before UI.
            return new Window(new ContentPage
            {
                BackgroundColor = Color.FromArgb("#1C1C1E"),
                Content = new Label
                {
                    Text = "Where U At failed to start. Force-stop the app and open again. If it keeps failing, Settings → Share crash log (after a successful launch) or clear app data.",
                    TextColor = Colors.White,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    Margin = new Thickness(24),
                    HorizontalTextAlignment = TextAlignment.Center
                }
            });
        }
    }
}
