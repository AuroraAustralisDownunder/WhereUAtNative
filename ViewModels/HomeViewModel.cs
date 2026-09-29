using System.Collections.ObjectModel;
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
    private readonly IFamilyService _familyService;

    private string _welcomeText = "Signed in";
    private bool _isBusy;
    private bool _isSharingEnabled;
    private bool _isToggling;
    private string _locationStatusText = "Off";
    private string? _locationHint;
    private CancellationTokenSource? _refreshCts;
    private bool _isPageVisible;

    private string? _familyCode;
    private string _familyStatusText = "Not in a family yet.";
    private string? _familyHint;
    private string _joinCodeInput = string.Empty;
    private bool _isInFamily;

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
            ((Command)CreateFamilyCommand).ChangeCanExecute();
            ((Command)JoinFamilyCommand).ChangeCanExecute();
            ((Command)LeaveFamilyCommand).ChangeCanExecute();
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
        "Location is off by default. Your position is uploaded only while Share my location is ON and you belong to a family — and only to that family’s private location node.";

    public bool IsInFamily
    {
        get => _isInFamily;
        private set
        {
            _isInFamily = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNotInFamily));
        }
    }

    public bool IsNotInFamily => !IsInFamily;

    public string? FamilyCode
    {
        get => _familyCode;
        private set { _familyCode = value; OnPropertyChanged(); }
    }

    public string FamilyStatusText
    {
        get => _familyStatusText;
        set { _familyStatusText = value; OnPropertyChanged(); }
    }

    public string? FamilyHint
    {
        get => _familyHint;
        set
        {
            _familyHint = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFamilyHintVisible));
        }
    }

    public bool IsFamilyHintVisible => !string.IsNullOrWhiteSpace(FamilyHint);

    public string JoinCodeInput
    {
        get => _joinCodeInput;
        set { _joinCodeInput = value; OnPropertyChanged(); }
    }

    public ObservableCollection<string> MemberLabels { get; } = new();

    public ICommand SignOutCommand { get; }
    public ICommand OpenMapCommand { get; }
    public ICommand CreateFamilyCommand { get; }
    public ICommand JoinFamilyCommand { get; }
    public ICommand LeaveFamilyCommand { get; }

    public HomeViewModel(IAuthService authService, ILocationService locationService, IFamilyService familyService)
    {
        _authService = authService;
        _locationService = locationService;
        _familyService = familyService;
        SignOutCommand = new Command(async () => await SignOutAsync(), () => !IsBusy);
        OpenMapCommand = new Command(async () => await OpenMapAsync());
        CreateFamilyCommand = new Command(async () => await CreateFamilyAsync(), () => !IsBusy);
        JoinFamilyCommand = new Command(async () => await JoinFamilyAsync(), () => !IsBusy);
        LeaveFamilyCommand = new Command(async () => await LeaveFamilyAsync(), () => !IsBusy);

        // Reflect persisted opt-in without forcing a permission prompt until Appearing refresh.
        _isSharingEnabled = _locationService.IsSharingEnabled;
        UpdateStatusFromService(hint: null);
        ApplyFamilyStateFromService();
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

        await _familyService.RefreshMembershipAsync();
        await RefreshFamilyUiAsync();

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
                await _familyService.ClearPublishedLocationAsync();
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

    private void ApplyFamilyStateFromService()
    {
        FamilyCode = _familyService.CurrentFamilyId;
        IsInFamily = !string.IsNullOrEmpty(FamilyCode);
        FamilyStatusText = IsInFamily
            ? $"Family code: {FamilyCode}"
            : "Not in a family yet.";
    }

    private async Task RefreshFamilyUiAsync()
    {
        ApplyFamilyStateFromService();
        MemberLabels.Clear();

        if (!IsInFamily)
            return;

        var members = await _familyService.GetMembersAsync();
        var self = _authService.CurrentUserId;
        foreach (var m in members)
        {
            var label = m.Uid == self ? $"{m.DisplayName} (you)" : m.DisplayName;
            MemberLabels.Add(label);
        }

        FamilyStatusText = members.Count == 0
            ? $"Family code: {FamilyCode} — no members listed yet"
            : $"Family code: {FamilyCode} — {members.Count} member(s)";
    }

    private async Task CreateFamilyAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            FamilyHint = null;
            var (code, error) = await _familyService.CreateFamilyAsync();
            if (error is not null)
            {
                FamilyHint = error;
                return;
            }

            FamilyHint = $"Created! Share code {code} with family.";
            await RefreshFamilyUiAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task JoinFamilyAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            FamilyHint = null;
            var error = await _familyService.JoinFamilyAsync(JoinCodeInput);
            if (error is not null)
            {
                FamilyHint = error;
                return;
            }

            JoinCodeInput = string.Empty;
            FamilyHint = "Joined family.";
            await RefreshFamilyUiAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LeaveFamilyAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            FamilyHint = null;
            var error = await _familyService.LeaveFamilyAsync();
            if (error is not null)
                FamilyHint = error;
            else
                FamilyHint = "Left family.";
            await RefreshFamilyUiAsync();
        }
        finally
        {
            IsBusy = false;
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
            await _familyService.ClearPublishedLocationAsync();
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
