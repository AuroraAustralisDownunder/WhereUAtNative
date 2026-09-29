namespace WhereUAtNative.Views;

public partial class LoginPage : ContentPage
{
    public LoginPage()
    {
        InitializeComponent();
        
        // This links the UI to your logic
        BindingContext = new ViewModels.LoginViewModel();
    }
}