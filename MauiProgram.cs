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
        // Install global handlers before DI / Firebase so early faults are captured.
        CrashLogBootstrap.InstallEarlyHandlers();

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Remove Material underline on Android Entry (login/password look boxed, not underscored).
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("NoUnderline", (handler, view) =>
        {
#if ANDROID
            handler.PlatformView.BackgroundTintList =
                Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
#endif
        });

        RegisterFirebaseServices(builder);
        RegisterAppServices(builder);

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();
        CrashLogBootstrap.BindService(app.Services);

        try
        {
            // Start forwarding opt-in GPS → family RTDB (no-op until sharing + family).
            app.Services.GetRequiredService<LocationSyncService>().Start();
        }
        catch (Exception ex)
        {
            // Preferences / DI must not kill process during CreateMauiApp.
            try { app.Services.GetService<ICrashLogService>()?.LogError("LocationSyncService.Start failed at launch", ex); }
            catch { /* ignore */ }
        }
        return app;
    }

    private static void RegisterAppServices(MauiAppBuilder builder)
    {
        builder.Services.AddSingleton<ICrashLogService, CrashLogService>();
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
                try
                {
                    CrossFirebase.Initialize();
                }
                catch (Exception ex)
                {
                    // Missing plist / double-init must not abort launch.
                    CrashLogBootstrap.LogEarly("Firebase iOS init failed", ex);
                }
                return true;
            }));
#elif ANDROID
            events.AddAndroid(android => android.OnCreate((activity, state) =>
            {
                try
                {
                    CrossFirebase.Initialize(activity, () => Platform.CurrentActivity!);
                }
                catch (Exception ex)
                {
                    // Missing google-services / double-init must not abort launch.
                    CrashLogBootstrap.LogEarly("Firebase Android init failed", ex);
                }
            }));
#endif
        });
    }
}

/// <summary>
/// Static early hook so we can log before the DI container is ready.
/// Once the app builds, BindService swaps to the real singleton.
/// </summary>
internal static class CrashLogBootstrap
{
    private static ICrashLogService? _service;
    private static readonly CrashLogService Early = new();
    private static bool _handlersInstalled;

    public static void InstallEarlyHandlers()
    {
        if (_handlersInstalled)
            return;
        _handlersInstalled = true;

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

#if ANDROID
        Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += OnAndroidUnhandledException;
#endif
    }

    public static void BindService(IServiceProvider services)
    {
        try
        {
            _service = services.GetService<ICrashLogService>();
        }
        catch
        {
            // keep early instance
        }
    }

    public static void LogEarly(string message, Exception? ex = null)
    {
        try
        {
            (_service ?? Early).LogError(message, ex);
        }
        catch
        {
            // never throw from logging
        }
    }

    private static ICrashLogService Sink => _service ?? Early;

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var ex = e.ExceptionObject as Exception;
            Sink.LogFatal(
                e.IsTerminating ? "AppDomain.UnhandledException (terminating)" : "AppDomain.UnhandledException",
                ex);
        }
        catch { /* ignore */ }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            Sink.LogError("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        }
        catch { /* ignore */ }
    }

#if ANDROID
    private static void OnAndroidUnhandledException(object? sender, Android.Runtime.RaiseThrowableEventArgs e)
    {
        try
        {
            Sink.LogFatal("Android.UnhandledExceptionRaiser", e.Exception);
            // Do not set e.Handled — let the platform still surface the crash after we log.
        }
        catch { /* ignore */ }
    }
#endif
}
