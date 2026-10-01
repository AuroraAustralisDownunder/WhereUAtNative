using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using WhereUAtNative.Services;

namespace WhereUAtNative.ViewModels;

/// <summary>
/// Family, privacy, account, coordinates, and sign-out — map stays clean.
/// Location sharing primary control lives on the Map pin FAB.
/// </summary>
public class SettingsViewModel : INotifyPropertyChanged
{
    private readonly IAuthService _authService;
    private readonly ILocationService _locationService;
    private readonly IFamilyService _familyService;
    private readonly ICrashLogService _crashLog;

    private string _accountEmail = "Signed in";
    private bool _isBusy;
    private string _locationStatusText = "Off";
    private string? _familyCode;
    private string _familyStatusText = "Not in a family yet.";
    private string? _familyHint;
    private string _joinCodeInput = string.Empty;
    private bool _isInFamily;
    private string? _crashLogHint;

    public string AccountEmail
    {
        get => _accountEmail;
        set { _accountEmail = value; OnPropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNotBusy));
            ((Command)CreateFamilyCommand).ChangeCanExecute();
            ((Command)JoinFamilyCommand).ChangeCanExecute();
            ((Command)LeaveFamilyCommand).ChangeCanExecute();
            ((Command)CloseCommand).ChangeCanExecute();
            ((Command)ShareCrashLogCommand).ChangeCanExecute();
            ((Command)CopyCrashLogCommand).ChangeCanExecute();
            ((Command)ClearCrashLogCommand).ChangeCanExecute();
            ((Command)SignOutCommand).ChangeCanExecute();
        }
    }

    public bool IsNotBusy => !IsBusy;

    public string LocationStatusText
    {
        get => _locationStatusText;
        set { _locationStatusText = value; OnPropertyChanged(); }
    }

    public string PrivacyReminder { get; } =
        "Location is off by default. Your position is uploaded only while Share my location is ON and you belong to a family — and only to that family’s private location node. Use the green pin FAB on the map to toggle sharing.";

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

    public string? CrashLogHint
    {
        get => _crashLogHint;
        set
        {
            _crashLogHint = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCrashLogHintVisible));
        }
    }

    public bool IsCrashLogHintVisible => !string.IsNullOrWhiteSpace(CrashLogHint);


    public string JoinCodeInput
    {
        get => _joinCodeInput;
        set { _joinCodeInput = value; OnPropertyChanged(); }
    }

    public ObservableCollection<string> MemberLabels { get; } = new();

    public ICommand CreateFamilyCommand { get; }
    public ICommand JoinFamilyCommand { get; }
    public ICommand LeaveFamilyCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand ShareCrashLogCommand { get; }
    public ICommand CopyCrashLogCommand { get; }
    public ICommand ClearCrashLogCommand { get; }
    public ICommand SignOutCommand { get; }

    public SettingsViewModel(IAuthService authService, ILocationService locationService, IFamilyService familyService, ICrashLogService crashLog)
    {
        _authService = authService;
        _locationService = locationService;
        _familyService = familyService;
        _crashLog = crashLog;
        CreateFamilyCommand = new Command(async () => await CreateFamilyAsync(), () => !IsBusy);
        JoinFamilyCommand = new Command(async () => await JoinFamilyAsync(), () => !IsBusy);
        LeaveFamilyCommand = new Command(async () => await LeaveFamilyAsync(), () => !IsBusy);
        CloseCommand = new Command(async () => await CloseAsync(), () => !IsBusy);
        ShareCrashLogCommand = new Command(async () => await ShareCrashLogAsync(), () => !IsBusy);
        CopyCrashLogCommand = new Command(async () => await CopyCrashLogAsync(), () => !IsBusy);
        ClearCrashLogCommand = new Command(async () => await ClearCrashLogAsync(), () => !IsBusy);
        SignOutCommand = new Command(async () => await SignOutAsync(), () => !IsBusy);
        ApplyFamilyStateFromService();
        UpdateLocationStatus();
    }

    public async Task OnAppearingAsync()
    {
        RefreshAccount();
        UpdateLocationStatus();
        await _familyService.RefreshMembershipAsync();
        await RefreshFamilyUiAsync();
    }

    private void RefreshAccount()
    {
        var email = _authService.CurrentUserEmail;
        AccountEmail = string.IsNullOrWhiteSpace(email) ? "Signed in" : email;
    }

    private void UpdateLocationStatus()
    {
        if (!_locationService.IsSharingEnabled)
        {
            LocationStatusText = "Sharing off — tap the pin FAB on the map to share. Coordinates appear here while sharing.";
            return;
        }

        var location = _locationService.LastKnownLocation;
        if (location is null)
        {
            LocationStatusText = "Sharing on — waiting for GPS…";
            return;
        }

        var lat = Math.Round(location.Latitude, 4);
        var lon = Math.Round(location.Longitude, 4);
        LocationStatusText = $"Sharing on — {lat:0.0000}, {lon:0.0000}";
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


    private async Task ShareCrashLogAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            CrashLogHint = null;
            var text = await _crashLog.ReadTailAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                CrashLogHint = "No crash log entries yet.";
                return;
            }

            var filePath = Path.Combine(FileSystem.CacheDirectory, "whereuat-crash.log");
            await File.WriteAllTextAsync(filePath, text);
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "Where U At crash log",
                File = new ShareFile(filePath)
            });
            CrashLogHint = "Share sheet opened — paste or send to the developer.";
        }
        catch (Exception ex)
        {
            _crashLog.LogError("ShareCrashLog failed", ex);
            CrashLogHint = "Could not share crash log.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CopyCrashLogAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            CrashLogHint = null;
            var text = await _crashLog.ReadTailAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                CrashLogHint = "No crash log entries yet.";
                return;
            }

            await Clipboard.Default.SetTextAsync(text);
            CrashLogHint = "Crash log copied to clipboard.";
        }
        catch (Exception ex)
        {
            _crashLog.LogError("CopyCrashLog failed", ex);
            CrashLogHint = "Could not copy crash log.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ClearCrashLogAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            await _crashLog.ClearAsync();
            CrashLogHint = "Crash log cleared.";
        }
        catch (Exception ex)
        {
            CrashLogHint = "Could not clear crash log.";
            try { _crashLog.LogError("ClearCrashLog failed", ex); } catch { /* ignore */ }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SignOutAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            if (_locationService.IsSharingEnabled)
                await _locationService.DisableSharingAsync();
            await _familyService.ClearPublishedLocationAsync();
            UpdateLocationStatus();

            await _authService.SignOutAsync();
            await Shell.Current.GoToAsync("//LoginPage");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CloseAsync()
    {
        try
        {
            await Shell.Current.GoToAsync("..");
        }
        catch
        {
            try { await Shell.Current.GoToAsync("//MapPage"); }
            catch { /* ignore */ }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
