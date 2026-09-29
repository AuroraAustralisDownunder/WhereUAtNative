using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.Maui.Devices.Sensors;
using WhereUAtNative.Services;

namespace WhereUAtNative.ViewModels;

public class HomeViewModel : INotifyPropertyChanged
{
    private const int RefreshIntervalSeconds = 30;

    private readonly IAuthService _authService;
    private readonly ILocationService _locationService;

    private string _welcomeText = "Signed in";
    private bool _isBusy;
    private bool _isSharingEnabled;
    private bool _isToggling;
    private string _locationStatusText = "Off";
    private string? _locationHint;
    private CancellationTokenSource? _refreshCts;
    private bool _isPageVisible;

    public string WelcomeText
    {
        get => _welcomeText;
        set { _welcomeText = value; OnPropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNotBusy));
            ((Command)SignOutCommand).ChangeCanExecute();
        }
    }

    public bool IsNotBusy => !IsBusy;

    /// <summary>Two-way bound to the Share my location switch. Default off.</summary>
    public bool IsSharingEnabled
    {
        get => _isSharingEnabled;
        set
        {
            if (_isSharingEnabled == value || _isToggling)
                return;

            _ = ApplySharingToggleAsync(value);
        }
    }

    public string LocationStatusText
    {
        get => _locationStatusText;
        set { _locationStatusText = value; OnPropertyChanged(); }
    }

    public string? LocationHint
    {
        get => _locationHint;
        set
        {
            _locationHint = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLocationHintVisible));
        }
    }

    public bool IsLocationHintVisible => !string.IsNullOrWhiteSpace(LocationHint);

    public string PrivacyReminder { get; } =
        "Location is off by default. Nothing is uploaded in this build — sharing stays on this device until a later release.";

    public ICommand SignOutCommand { get; }
    public ICommand OpenMapCommand { get; }

    public HomeViewModel(IAuthService authService, ILocationService locationService)
    {
        _authService = authService;
        _locationService = locationService;
        SignOutCommand = new Command(async () => await SignOutAsync(), () => !IsBusy);
        OpenMapCommand = new Command(async () => await OpenMapAsync());

        // Reflect persisted opt-in without forcing a permission prompt until Appearing refresh.
        _isSharingEnabled = _locationService.IsSharingEnabled;
        UpdateStatusFromService(hint: null);
    }

    public void RefreshWelcome()
    {
        var email = _authService.CurrentUserEmail;
        WelcomeText = string.IsNullOrWhiteSpace(email)
            ? "Signed in"
            : $"Welcome, {email}";
    }

    public async Task OnAppearingAsync()
    {
        _isPageVisible = true;
        RefreshWelcome();

        // Sync toggle with preference (e.g. after Map page or permission change).
        SetSharingFlagWithoutSideEffects(_locationService.IsSharingEnabled);
        UpdateStatusFromService(LocationHint);

        if (_locationService.IsSharingEnabled)
        {
            await RefreshPositionAsync();
            StartRefreshLoop();
        }
        else
        {
            StopRefreshLoop();
        }
    }

    public void OnDisappearing()
    {
        _isPageVisible = false;
        StopRefreshLoop();
    }

    private async Task ApplySharingToggleAsync(bool enable)
    {
        _isToggling = true;
        try
        {
            // Optimistic UI so the Switch stays in sync with TwoWay binding.
            SetSharingFlagWithoutSideEffects(enable);

            if (enable)
            {
                LocationHint = null;
                LocationStatusText = "Requesting permission…";
                var (success, message) = await _locationService.EnableSharingAsync();
                SetSharingFlagWithoutSideEffects(_locationService.IsSharingEnabled);

                if (!success)
                {
                    LocationHint = message;
                    UpdateStatusFromService(message);
                    StopRefreshLoop();
                    return;
                }

                LocationHint = message; // e.g. Waiting for GPS…
                UpdateStatusFromService(message);
                StartRefreshLoop();
            }
            else
            {
                await _locationService.DisableSharingAsync();
                SetSharingFlagWithoutSideEffects(false);
                LocationHint = null;
                LocationStatusText = "Off";
                StopRefreshLoop();
            }
        }
        finally
        {
            _isToggling = false;
        }
    }

    private async Task RefreshPositionAsync()
    {
        if (!_locationService.IsSharingEnabled)
            return;

        var location = await _locationService.GetCurrentAsync();
        if (!_locationService.IsSharingEnabled)
        {
            SetSharingFlagWithoutSideEffects(false);
            LocationHint = "Permission needed";
            LocationStatusText = "Permission needed";
            StopRefreshLoop();
            return;
        }

        UpdateStatusFromService(location is null ? "Waiting for GPS…" : null);
    }

    private void UpdateStatusFromService(string? hint)
    {
        if (!_locationService.IsSharingEnabled)
        {
            LocationStatusText = "Off";
            if (!string.IsNullOrWhiteSpace(hint))
                LocationHint = hint;
            return;
        }

        var location = _locationService.LastKnownLocation;
        if (location is null)
        {
            LocationStatusText = string.IsNullOrWhiteSpace(hint) ? "On — Waiting for GPS…" : $"On — {hint}";
            LocationHint = hint;
            return;
        }

        LocationStatusText = $"On — {FormatRounded(location)}";
        LocationHint = hint;
    }

    /// <summary>Round to ~4 decimal places (~11 m) for privacy-friendly display. Never log raw coords.</summary>
    private static string FormatRounded(Location location)
    {
        var lat = Math.Round(location.Latitude, 4);
        var lon = Math.Round(location.Longitude, 4);
        return $"{lat:0.0000}, {lon:0.0000}";
    }

    private void SetSharingFlagWithoutSideEffects(bool value)
    {
        _isSharingEnabled = value;
        OnPropertyChanged(nameof(IsSharingEnabled));
    }

    private void StartRefreshLoop()
    {
        StopRefreshLoop();
        if (!_isPageVisible || !_locationService.IsSharingEnabled)
            return;

        _refreshCts = new CancellationTokenSource();
        var token = _refreshCts.Token;
        _ = RunRefreshLoopAsync(token);
    }

    private async Task RunRefreshLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(RefreshIntervalSeconds), token);
                if (token.IsCancellationRequested || !_isPageVisible || !_locationService.IsSharingEnabled)
                    break;

                await RefreshPositionAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on leave / disable.
        }
    }

    private void StopRefreshLoop()
    {
        try
        {
            _refreshCts?.Cancel();
            _refreshCts?.Dispose();
        }
        catch
        {
            // ignore
        }
        finally
        {
            _refreshCts = null;
        }
    }

    private async Task OpenMapAsync()
    {
        try
        {
            await Shell.Current.GoToAsync("//MapPage");
        }
        catch
        {
            // Navigation should not crash the home shell.
        }
    }

    private async Task SignOutAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            StopRefreshLoop();
            // Turn off sharing on sign-out so the next session starts privacy-safe.
            if (_locationService.IsSharingEnabled)
                await _locationService.DisableSharingAsync();
            SetSharingFlagWithoutSideEffects(false);
            LocationStatusText = "Off";
            LocationHint = null;

            await _authService.SignOutAsync();
            await Shell.Current.GoToAsync("//LoginPage");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
