using WhereUAtNative.Services;
using WhereUAtNative.ViewModels;

namespace WhereUAtNative.Views;

public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _viewModel;
    private readonly ICrashLogService? _crashLog;

    public SettingsPage(SettingsViewModel viewModel, ICrashLogService? crashLog = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _crashLog = crashLog;
        BindingContext = _viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            await _viewModel.OnAppearingAsync();
        }
        catch (Exception ex)
        {
            // async void — family/token errors must not crash Settings.
            try { _crashLog?.LogError("Settings.OnAppearing failed", ex); } catch { /* ignore */ }
        }
    }
}
