using System.Windows;
using System.Windows.Controls;
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
    private List<SigfurProfileConfig> _localProfiles = [];

    public SigfurProfileSession? Session { get; private set; }

    public StartupLoginWindow(ProfileService profileService, SigfurBackupService backupService, SigfurRestoreService restoreService, UiStatePersistenceService? uiState = null)
    {
        InitializeComponent();
        _profileService = profileService;
        _backupService = backupService;
        _restoreService = restoreService;
        _uiState = uiState;
        _uiState?.Attach(this, "SIGFUR.StartupLoginWindow");
        Loaded += StartupLoginWindow_Loaded;
    }

    private async void StartupLoginWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await ReloadProfilesAsync();
        FooterTextBlock.Text = $"Pasta local atual: {AppPaths.GetDefaultDataDirectory()}";
    }

    private async Task ReloadProfilesAsync()
    {
        _localProfiles = await _profileService.LoadLocalProfilesAsync();
        ProfilesComboBox.ItemsSource = _localProfiles;
        if (_localProfiles.Count > 0) ProfilesComboBox.SelectedIndex = 0;
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
            var info = await _backupService.GetLastBackupInfoAsync(profile.SyncFolderPath);
            FooterTextBlock.Text =
                $"Pasta local: {profile.LocalDataPath}\n" +
                $"Pasta de sincronização: {profile.SyncFolderPath}\n" +
                $"Último backup: {(info is null ? "não encontrado" : info.Display)}";
        }
        else
        {
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
        MessageBox.Show(this,
            "Você entrou sem login. O SIGFUR usará apenas os dados deste computador.",
            "SIGFUR - Entrar sem login",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        DialogResult = true;
    }

    private async void EnterProfileButton_Click(object sender, RoutedEventArgs e) => await EnterProfileAsync(restoreBackup: false);

    private async void EnterAndRestoreButton_Click(object sender, RoutedEventArgs e) => await EnterProfileAsync(restoreBackup: true);

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
            var session = await _profileService.ValidateLocalProfileAsync(profile, ProfilePasswordBox.Password);

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

    private void SetBusy(bool busy, string status)
    {
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
        StatusTextBlock.Text = "Status: " + status;
        Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
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
