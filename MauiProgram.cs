using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.LifecycleEvents;
using WhereUAtNative.Services;
using WhereUAtNative.ViewModels;
using WhereUAtNative.Views;
#if IOS
using Plugin.Firebase.Core.Platforms.iOS;
#elif ANDROID
using Plugin.Firebase.Core.Platforms.Android;
#endif

namespace WhereUAtNative;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        RegisterFirebaseServices(builder);
        RegisterAppServices(builder);

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();
        // Start forwarding opt-in GPS → family RTDB (no-op until sharing + family).
        app.Services.GetRequiredService<LocationSyncService>().Start();
        return app;
    }

    private static void RegisterAppServices(MauiAppBuilder builder)
    {
        builder.Services.AddSingleton<IAuthService, FirebaseAuthService>();
        builder.Services.AddSingleton<ILocationService, LocationService>();
        builder.Services.AddSingleton<IFamilyService, FirebaseFamilyService>();
        builder.Services.AddSingleton<LocationSyncService>();

        builder.Services.AddTransient<LoginViewModel>();
        builder.Services.AddTransient<MapViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();

        // Shell root pages are created once and kept for the app session.
        builder.Services.AddSingleton<LoginPage>();
        builder.Services.AddSingleton<MapPage>();
        // Settings is pushed via Routing.RegisterRoute — resolve from DI.
        builder.Services.AddTransient<SettingsPage>();

        builder.Services.AddSingleton<AppShell>();
    }

    private static void RegisterFirebaseServices(MauiAppBuilder builder)
    {
        builder.ConfigureLifecycleEvents(events =>
        {
#if IOS
            events.AddiOS(iOS => iOS.WillFinishLaunching((app, dict) =>
            {
                CrossFirebase.Initialize();
                return true;
            }));
#elif ANDROID
            events.AddAndroid(android => android.OnCreate((activity, state) =>
                CrossFirebase.Initialize(activity, () => Platform.CurrentActivity)));
#endif
        });
    }
}
