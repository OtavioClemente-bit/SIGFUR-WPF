using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views;

public partial class StartupLoginWindow : Window
{
    private readonly ProfileService _profileService;
    private readonly SigfurBackupService _backupService;
    private readonly SigfurRestoreService _restoreService;
    private readonly UiStatePersistenceService? _uiState;
    private readonly Func<SigfurProfileConfig, Task>? _applyProfileTheme;
    private List<SigfurProfileConfig> _localProfiles = [];
    private bool _busy;
    private bool _passwordVisible;

    public SigfurProfileSession? Session { get; private set; }

    public StartupLoginWindow(ProfileService profileService, SigfurBackupService backupService, SigfurRestoreService restoreService,
        UiStatePersistenceService? uiState = null, Func<SigfurProfileConfig, Task>? applyProfileTheme = null)
    {
        InitializeComponent();
        _profileService = profileService;
        _backupService = backupService;
        _restoreService = restoreService;
        _uiState = uiState;
        _applyProfileTheme = applyProfileTheme;
        _uiState?.Attach(this, "SIGFUR.StartupLoginWindow");
        Loaded += StartupLoginWindow_Loaded;
    }

    private async void StartupLoginWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await ReloadProfilesAsync();
        if (_localProfiles.Count > 0) ProfilePasswordBox.Focus();
    }

    private async Task ReloadProfilesAsync()
    {
        var selectedName = (ProfilesComboBox.SelectedItem as SigfurProfileConfig)?.ProfileName;
        _localProfiles = await _profileService.LoadLocalProfilesAsync();
        ProfilesComboBox.ItemsSource = _localProfiles;
        ProfilesComboBox.SelectedItem = _localProfiles.FirstOrDefault(profile =>
            profile.ProfileName.Equals(selectedName, StringComparison.CurrentCultureIgnoreCase))
            ?? _localProfiles.OrderByDescending(profile => profile.LastUsedAt).FirstOrDefault();
        RemoveProfileButton.IsEnabled = _localProfiles.Count > 0;
        EnterProfileButton.IsEnabled = _localProfiles.Count > 0;
        EnterAndRestoreButton.IsEnabled = _localProfiles.Count > 0;
        StatusTextBlock.Text = _localProfiles.Count == 0
            ? "Status: nenhum Perfil SIGFUR local encontrado. Você pode entrar sem login ou criar/vincular um perfil."
            : $"Status: {_localProfiles.Count} perfil(is) local(is) encontrado(s).";
        await UpdateFooterForSelectedProfileAsync();
    }

    private async void ProfilesComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => await UpdateFooterForSelectedProfileAsync();

    private async Task UpdateFooterForSelectedProfileAsync()
    {
        if (ProfilesComboBox.SelectedItem is SigfurProfileConfig profile)
        {
            ClearPassword();
            var info = await _backupService.GetLastBackupInfoAsync(profile.SyncFolderPath);
            if (!ReferenceEquals(ProfilesComboBox.SelectedItem, profile)) return;
            if (_applyProfileTheme is not null)
            {
                await _applyProfileTheme(profile);
                if (!ReferenceEquals(ProfilesComboBox.SelectedItem, profile)) return;
            }
            SelectedProfileNameText.Text = profile.ProfileName;
            FooterTextBlock.Text =
                $"Pasta local: {profile.LocalDataPath}\n" +
                $"Pasta de sincronização: {profile.SyncFolderPath}\n" +
                $"Último backup: {(info is null ? "não encontrado" : info.Display)}";
        }
        else
        {
            SelectedProfileNameText.Text = "Nenhum perfil selecionado";
            FooterTextBlock.Text = $"Pasta local atual: {AppPaths.GetDefaultDataDirectory()}";
        }
    }

    private void NoLoginButton_Click(object sender, RoutedEventArgs e)
    {
        Session = new SigfurProfileSession
        {
            IsProfileMode = false,
            StatusMessage = "Você entrou sem login. O SIGFUR usará apenas os dados deste computador."
        };
        DialogResult = true;
    }

    private async void EnterProfileButton_Click(object sender, RoutedEventArgs e) => await EnterProfileAsync(restoreBackup: false);

    private async void EnterAndRestoreButton_Click(object sender, RoutedEventArgs e) => await EnterProfileAsync(restoreBackup: true);

    private async void RemoveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ProfilesComboBox.SelectedItem is not SigfurProfileConfig profile) return;
        var answer = SigfurDialog.Show(this,
            $"Remover o perfil “{profile.ProfileName}” deste computador?\n\n" +
            "Os dados locais, a pasta sincronizada e todos os backups serão preservados. " +
            "Você poderá usar ‘Conectar perfil existente’ para vinculá-lo novamente.",
            "SIGFUR — Remover perfil deste computador",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            SetBusy(true, $"Removendo o vínculo de {profile.ProfileName}...");
            var removed = await _profileService.RemoveLocalProfileAsync(profile);
            await ReloadProfilesAsync();
            StatusTextBlock.Text = removed
                ? $"Perfil “{profile.ProfileName}” removido deste computador. Nenhum dado foi apagado."
                : "O perfil selecionado já não estava vinculado a este computador.";
            if (_localProfiles.Count > 0) ProfilePasswordBox.Focus();
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "SIGFUR — Remover perfil", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false, "pronto para entrar.", preserveStatus: true); }
    }

    private async Task EnterProfileAsync(bool restoreBackup)
    {
        if (ProfilesComboBox.SelectedItem is not SigfurProfileConfig profile)
        {
            MessageBox.Show(this, "Nenhum Perfil SIGFUR foi selecionado.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            SetBusy(true, restoreBackup ? "Validando senha e preparando restauração..." : "Validando senha do perfil...");
            var session = await _profileService.ValidateLocalProfileAsync(profile, CurrentPassword);

            if (restoreBackup)
            {
                var last = await _backupService.GetLastBackupInfoAsync(profile.SyncFolderPath);
                if (last is null)
                {
                    MessageBox.Show(this, "Nenhum backup foi encontrado na pasta de sincronização escolhida.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (await _restoreService.HasLocalDataNewerThanBackupAsync(profile.LocalDataPath, profile.SyncFolderPath))
                {
                    var conflict = MessageBox.Show(this,
                        "Existem dados neste computador que parecem mais recentes do que o backup encontrado.\n\nDeseja usar o backup da sincronização mesmo assim?\n\nSerá criado backup de segurança antes de substituir.",
                        "SIGFUR - Conflito de dados",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (conflict != MessageBoxResult.Yes) return;
                }

                await _restoreService.RestoreLatestBackupAsync(session, profile.CreateSafetyBackupBeforeRestore);
                session.StatusMessage = "Dados restaurados com sucesso. O SIGFUR será aberto com os dados deste perfil.";
            }

            Session = session;
            DialogResult = true;
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Senha incorreta para este perfil.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning);
            FocusPasswordInput(selectAll: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "SIGFUR - Perfil e Backup", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, "pronto.");
        }
    }

    private async void CreateProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new CreateProfileWindow(_profileService, _backupService, _uiState) { Owner = this };
        if (window.ShowDialog() == true)
        {
            await ReloadProfilesAsync();
            ProfilesComboBox.SelectedItem = _localProfiles.FirstOrDefault(p => p.ProfileName.Equals(window.CreatedSession?.Profile?.ProfileName, StringComparison.CurrentCultureIgnoreCase));
            ProfilePasswordBox.Focus();
        }
    }

    private async void UseExistingProfileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFolderDialog { Title = "Escolha a pasta de sincronização do Perfil SIGFUR", Multiselect = false };
            if (dialog.ShowDialog(this) != true) return;

            SetBusy(true, "Procurando profile.sigfurprofile na pasta escolhida...");
            var profiles = await _profileService.LoadProfileFromSyncFolderAsync(dialog.FolderName);
            if (profiles.Count == 0)
            {
                MessageBox.Show(this, "Nenhum profile.sigfurprofile foi encontrado na pasta escolhida.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var linkWindow = new UseExistingProfileWindow(_profileService, _restoreService, profiles, dialog.FolderName, _uiState) { Owner = this };
            if (linkWindow.ShowDialog() == true)
            {
                await ReloadProfilesAsync();
                if (linkWindow.Session is not null)
                {
                    Session = linkWindow.Session;
                    DialogResult = true;
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "SIGFUR - Usar perfil existente", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, "pronto.");
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private string CurrentPassword => _passwordVisible ? ProfilePasswordTextBox.Text : ProfilePasswordBox.Password;

    private void TogglePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_passwordVisible)
        {
            ProfilePasswordBox.Password = ProfilePasswordTextBox.Text;
            ProfilePasswordTextBox.Clear();
            _passwordVisible = false;
            ProfilePasswordTextBox.Visibility = Visibility.Collapsed;
            ProfilePasswordBox.Visibility = Visibility.Visible;
            PasswordEyeSlash.Visibility = Visibility.Collapsed;
            TogglePasswordButton.ToolTip = "Mostrar senha";
            System.Windows.Automation.AutomationProperties.SetName(TogglePasswordButton, "Mostrar senha");
        }
        else
        {
            ProfilePasswordTextBox.Text = ProfilePasswordBox.Password;
            ProfilePasswordBox.Clear();
            _passwordVisible = true;
            ProfilePasswordBox.Visibility = Visibility.Collapsed;
            ProfilePasswordTextBox.Visibility = Visibility.Visible;
            PasswordEyeSlash.Visibility = Visibility.Visible;
            TogglePasswordButton.ToolTip = "Ocultar senha";
            System.Windows.Automation.AutomationProperties.SetName(TogglePasswordButton, "Ocultar senha");
        }

        FocusPasswordInput();
    }

    private void ClearPassword()
    {
        _passwordVisible = false;
        ProfilePasswordBox.Clear();
        ProfilePasswordTextBox.Clear();
        ProfilePasswordTextBox.Visibility = Visibility.Collapsed;
        ProfilePasswordBox.Visibility = Visibility.Visible;
        PasswordEyeSlash.Visibility = Visibility.Collapsed;
        TogglePasswordButton.ToolTip = "Mostrar senha";
        System.Windows.Automation.AutomationProperties.SetName(TogglePasswordButton, "Mostrar senha");
    }

    private void FocusPasswordInput(bool selectAll = false)
    {
        if (_passwordVisible)
        {
            ProfilePasswordTextBox.Focus();
            if (selectAll) ProfilePasswordTextBox.SelectAll();
            else ProfilePasswordTextBox.CaretIndex = ProfilePasswordTextBox.Text.Length;
        }
        else
        {
            ProfilePasswordBox.Focus();
            if (selectAll) ProfilePasswordBox.SelectAll();
        }
    }

    private void SetBusy(bool busy, string status, bool preserveStatus = false)
    {
        _busy = busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
        LoginContent.IsEnabled = !busy;
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!preserveStatus) StatusTextBlock.Text = "Status: " + status;
        Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_busy) { e.Handled = true; return; }
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
            return;
        }
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        if (ProfilesComboBox.IsDropDownOpen) return;
        if (Keyboard.FocusedElement is Button) return;
        if (ProfilesComboBox.SelectedItem is not SigfurProfileConfig) return;
        e.Handled = true;
        await EnterProfileAsync(restoreBackup: false);
    }


    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => KeepInsideWorkArea();

    private void Window_LocationChanged(object? sender, EventArgs e) => KeepInsideWorkArea();

    private void Window_StateChanged(object? sender, EventArgs e) => KeepInsideWorkArea();

    private void KeepInsideWorkArea()
    {
        if (!IsLoaded || WindowState != WindowState.Normal) return;
        try
        {
            var maxWidth = Math.Max(MinWidth, SystemParameters.WorkArea.Width - 24);
            var maxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 24);
            if (Width > maxWidth) Width = maxWidth;
            if (Height > maxHeight) Height = maxHeight;
            if (Left < SystemParameters.VirtualScreenLeft) Left = SystemParameters.VirtualScreenLeft;
            if (Top < SystemParameters.VirtualScreenTop) Top = SystemParameters.VirtualScreenTop;
        }
        catch { }
    }

}
