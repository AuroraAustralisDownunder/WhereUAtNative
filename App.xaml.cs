namespace WhereUAtNative;

public partial class App : Application
{
    private readonly AppShell _shell;

    public App(AppShell shell)
    {
        InitializeComponent();
        _shell = shell;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        try
        {
            return new Window(_shell);
        }
        catch
        {
            // Last-resort window so a DI/shell fault does not kill the process before UI.
            return new Window(new ContentPage
            {
                BackgroundColor = Color.FromArgb("#1C1C1E"),
                Content = new Label
                {
                    Text = "Where U At failed to start. Force-stop the app and open again.",
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
