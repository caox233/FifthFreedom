using System.Windows;
using System.Windows.Input;
using SplinterCellCNLauncher.Services;

namespace SplinterCellCNLauncher;
public partial class MainWindow
{
    private readonly CredentialStore _credentialStore=new();
    private bool _restoringPassword;
    private string? _autoPasswordScope;
    private CredentialEnvironmentContext _credentialEnvironment = CredentialEnvironmentContext.Lan;
    private CredentialIdentity CurrentCredentialIdentity(string server, string username)
        => _credentialEnvironment.CreateIdentity(server, username);
    private bool CanUseCapturedPassword(string? scope,string server,string username, CredentialEnvironmentContext? capturedEnvironment = null)
    {
        if (capturedEnvironment is not null && !capturedEnvironment.Equals(_credentialEnvironment)) return false;
        if(scope==null)return true; // Manual input is usable only in its captured environment.
        try{return scope==CurrentCredentialIdentity(server,username).ScopeValue;}catch(ArgumentException){return false;}
    }
    // This changes credential ownership only. Public networking/UI remains
    // disabled until its separate adapter and trust gates are implemented.
    private void SetCredentialEnvironment(CredentialEnvironmentContext environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        Dispatcher.VerifyAccess();
        if (_credentialEnvironment.Equals(environment)) return;
        if (_isBusy || _isGameRunning || _isGameStarting || _toolsOperation || _closingBackgroundTasks || _gameLaunchService.SessionId.HasValue)
            throw new InvalidOperationException("CREDENTIAL_ENVIRONMENT_BUSY");
        txtPassword.Password = "";
        _autoPasswordScope = null;
        ClearRecoveryFields();
        _recoveryServer = _recoveryUser = _recoveryBind = "";
        _recoveryCredentialIdentity = null;
        _credentialEnvironment = environment;
    }
    private void PasswordBox_PasswordChanged(object sender,RoutedEventArgs e)
    {
        if(!_restoringPassword)_autoPasswordScope=null;
    }
    private void RememberedIdentity_LostKeyboardFocus(object sender,KeyboardFocusChangedEventArgs e) => RestoreRememberedPassword();
    private void ClearAutofillForChangedIdentity()
    {
        if(_autoPasswordScope==null || txtPassword==null)return;
        string? scope=null;
        try{scope=CurrentCredentialIdentity(txtServer.Text,txtUsername.Text).ScopeValue;}catch(ArgumentException){}
        if(_autoPasswordScope!=scope){txtPassword.Password="";_autoPasswordScope=null;}
    }
    private void RestoreRememberedPassword()
    {
        if(App.PreviewMode || chkRememberPassword==null || txtPassword==null)return;
        ClearAutofillForChangedIdentity();
        if(chkRememberPassword.IsChecked!=true || txtPassword.Password.Length!=0)return;
        try {
            var identity = CurrentCredentialIdentity(txtServer.Text,txtUsername.Text);
            if (!_credentialStore.TryLoad(identity, out string? password)) return;
            string scope = identity.ScopeValue;
            _restoringPassword=true;
            try{txtPassword.Password=password;_autoPasswordScope=scope;}
            finally{_restoringPassword=false;}
        } catch(ArgumentException) { }
          catch {LogService.Error("Remembered password unavailable; re-entry required. No credential content logged.");}
    }
    private async void RememberPassword_Changed(object sender,RoutedEventArgs e)
    {
        if(!IsLoaded || App.PreviewMode)return;
        try {
            SaveSettingsFromUi();
            if(chkRememberPassword.IsChecked!=true){_credentialStore.Clear();_autoPasswordScope=null;}
            else RestoreRememberedPassword();
        } catch {await ShowInfoDialogAsync(L("记住密码","Remember Password"),L("无法保存偏好或清理已保存密码，请检查启动器目录写权限。","Could not save the preference or clear saved passwords. Check folder write permission."));}
    }
    private async Task RememberSuccessfulLoginAsync(string server,string username,string password, CredentialEnvironmentContext authenticatedEnvironment)
    {
        if(chkRememberPassword.IsChecked!=true)return;
        bool credentialSaved=false;
        try
        {
            // Persist to the namespace captured for this authentication, never
            // to a different context selected while asynchronous work completed.
            _credentialStore.Save(authenticatedEnvironment.CreateIdentity(server,username),password);
            credentialSaved=true;
            SaveSettingsFromUi();
        }
        catch
        {
            await ShowInfoDialogAsync(L("记住密码","Remember Password"),credentialSaved
                ? L("密码已加密保存，但偏好设置未能保存；不影响本次启动，请稍后重试。","The password was saved securely, but preferences could not be saved. This launch can continue; please retry later.")
                : L("本次登录成功，但密码未能加密保存；不影响本次启动。","Login succeeded, but the password could not be saved securely. This launch can continue."));
        }
    }
}
