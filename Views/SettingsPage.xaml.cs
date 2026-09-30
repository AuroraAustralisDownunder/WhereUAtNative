using WhereUAtNative.ViewModels;

namespace WhereUAtNative.Views;

public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _viewModel;

    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            await _viewModel.OnAppearingAsync();
        }
        catch
        {
            // async void — family/token errors must not crash Settings.
        }
    }
}
