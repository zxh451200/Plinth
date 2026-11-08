using System;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plinth.App.Localization;
using Plinth.App.Security;
using Plinth.App.Services;

namespace Plinth.App.ViewModels;

/// <summary>启动前的本机登录门禁。首次启用时设置密码；成功后由宿主创建主窗口。</summary>
public partial class LoginViewModel : ViewModelBase
{
    private readonly LocalLoginService _login;
    private readonly BrandingService _branding;
    private bool _completed;
    private string? _errorKey;

    public LoginViewModel(LocalLoginService login, BrandingService branding)
    {
        _login = login;
        _branding = branding;
        IsSetup = login.NeedsSetup;
        Username = IsSetup ? login.PrefillUsername : login.ReadUsername();
    }

    public event Action<bool>? Completed;

    public bool IsSetup { get; }
    public bool IsLogin => !IsSetup;
    public string ProductName => _branding.Current.ProductName;
    public IImage? Logo => _branding.Logo;
    public bool HasLogo => Logo is not null;
    public string WindowTitle => Loc.Format("login.windowTitle", ProductName);
    public string Heading => Loc.Get(IsSetup ? "login.setupTitle" : "login.title");
    public string Subtitle => Loc.Get(IsSetup ? "login.setupSubtitle" : "login.subtitle");
    public string SubmitText => Loc.Get(IsSetup ? "login.setupSubmit" : "login.submit");

    [ObservableProperty] private string username = "";
    [ObservableProperty] private string password = "";
    [ObservableProperty] private string confirmPassword = "";
    [ObservableProperty] private string errorMessage = "";
    [ObservableProperty] private bool isBusy;

    public bool HasError => ErrorMessage.Length > 0;

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (IsBusy || _completed) return;
        var name = Username.Trim();
        if (IsSetup)
        {
            var nameError = LocalLoginService.ValidateUsername(name);
            if (nameError is not null) { SetError(nameError); return; }
            var passwordError = LocalLoginService.ValidatePassword(Password);
            if (passwordError is not null) { SetError(passwordError); return; }
            if (!string.Equals(Password, ConfirmPassword, StringComparison.Ordinal))
            {
                SetError("login.error.mismatch");
                return;
            }
        }
        else if (name.Length == 0 || Password.Length == 0)
        {
            SetError("login.error.required");
            return;
        }

        IsBusy = true;
        _errorKey = null;
        ErrorMessage = "";
        try
        {
            if (IsSetup)
            {
                await _login.CreateCredentialAsync(name, Password);
                Complete(true);
            }
            else
            {
                var result = await _login.AuthenticateAsync(name, Password);
                if (result.Success) Complete(true);
                else SetError(result.ErrorKey ?? "login.error.invalid");
            }
        }
        catch (LocalLoginException ex) { SetError(ex.ErrorKey); }
        catch (Exception) { SetError("login.error.storage"); }
        finally
        {
            IsBusy = false;
            Password = "";
            ConfirmPassword = "";
        }
    }

    [RelayCommand]
    private void Exit() => Complete(false);

    public override void OnUiLanguageChanged()
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(SubmitText));
        if (_errorKey is not null) ErrorMessage = Loc.Get(_errorKey);
    }

    private void SetError(string key)
    {
        _errorKey = key;
        ErrorMessage = Loc.Get(key);
    }

    private void Complete(bool success)
    {
        if (_completed) return;
        _completed = true;
        Completed?.Invoke(success);
    }
}
