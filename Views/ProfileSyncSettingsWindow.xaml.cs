using System.Windows;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views;

public partial class ProfileSyncSettingsWindow : Window
{
    private readonly SigfurProfileSession _session;
    private readonly ProfileService _profileService;
    private readonly SigfurBackupService _backupService;
    private readonly SigfurRestoreService _restoreService;

    public ProfileSyncSettingsWindow(SigfurProfileSession session, ProfileService profileService, SigfurBackupService backupService, SigfurRestoreService restoreService)
    {
        InitializeComponent();
        _session = session;
        _profileService = profileService;
        _backupService = backupService;
        _restoreService = restoreService;
        try { App.UiState?.Attach(this, "SIGFUR.ProfileSyncSettingsWindow"); } catch { }
        Loaded += ProfileSyncSettingsWindow_Loaded;
    }

    private async void ProfileSyncSettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadProfileAsync();
    }

    private async Task LoadProfileAsync()
    {
        if (!_session.IsProfileMode || _session.Config is null || _session.Profile is null)
        {
            ProfileNameTextBox.Text = "Sem login";
            LocalPathTextBox.Text = App.Paths.DataDirectory;
            SyncPathTextBox.Text = string.Empty;
            BackupOnCloseCheckBox.IsChecked = false;
            SafetyBackupCheckBox.IsChecked = true;
            LastBackupTextBlock.Text = "Você entrou sem login. Nenhum backup automático será feito.";
            StatusTextBlock.Text = "Modo sem login preservado: o SIGFUR usa apenas os dados deste computador.";
            SetProfileButtonsEnabled(false);
            return;
        }

        ProfileNameTextBox.Text = _session.Profile.ProfileName;
        LocalPathTextBox.Text = _session.Config.LocalDataPath;
        SyncPathTextBox.Text = _session.Config.SyncFolderPath;
        BackupOnCloseCheckBox.IsChecked = _session.Config.BackupOnClose;
        SafetyBackupCheckBox.IsChecked = _session.Config.CreateSafetyBackupBeforeRestore;
        await RefreshLastBackupTextAsync();
        StatusTextBlock.Text = "Perfil ativo carregado.";
    }

    private async Task RefreshLastBackupTextAsync()
    {
        var info = string.IsNullOrWhiteSpace(SyncPathTextBox.Text) ? null : await _backupService.GetLastBackupInfoAsync(SyncPathTextBox.Text);
        LastBackupTextBlock.Text = info is null
            ? "Último backup sincronizado: não encontrado."
            : "Último backup sincronizado: " + info.Display;
    }

    private void SetProfileButtonsEnabled(bool enabled)
    {
        SyncPathTextBox.IsEnabled = enabled;
        BackupOnCloseCheckBox.IsEnabled = enabled;
        SafetyBackupCheckBox.IsEnabled = enabled;
    }

    private async void SaveNow_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProfileMode()) return;
        try
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            StatusTextBlock.Text = "Criando backup manual...";
            await SaveSettingsInternalAsync();
            await _backupService.CreateBackupAsync(_session);
            await RefreshLastBackupTextAsync();
            MessageBox.Show(this, "Backup criado com sucesso. Seus dados foram salvos na pasta de sincronização.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "SIGFUR - Backup", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Cursor = null;
            StatusTextBlock.Text = "Pronto.";
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProfileMode()) return;
        var confirm = MessageBox.Show(this,
            "A restauração vai substituir a pasta local de dados pelo último backup sincronizado.\n\nSerá criado backup de segurança antes de restaurar. Deseja continuar?",
            "SIGFUR - Restaurar último backup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            await SaveSettingsInternalAsync();
            if (_session.Config is not null && await _restoreService.HasLocalDataNewerThanBackupAsync(_session.Config.LocalDataPath, _session.Config.SyncFolderPath))
            {
                var conflict = MessageBox.Show(this,
                    "Existem dados locais possivelmente mais recentes. Deseja usar o backup da sincronização mesmo assim?",
                    "SIGFUR - Conflito de dados",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (conflict != MessageBoxResult.Yes) return;
            }
            StatusTextBlock.Text = "Restaurando último backup...";
            await _restoreService.RestoreLatestBackupAsync(_session, _session.Config?.CreateSafetyBackupBeforeRestore != false);
            MessageBox.Show(this, "Dados restaurados com sucesso. Reinicie o SIGFUR para garantir que todos os módulos usem os dados restaurados.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "SIGFUR - Restauração", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Cursor = null;
            StatusTextBlock.Text = "Pronto.";
        }
    }

    private async void SafetyBackup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await _restoreService.CreateSafetyBackupAsync(LocalPathTextBox.Text);
            MessageBox.Show(this, string.IsNullOrWhiteSpace(path) ? "Não havia dados locais para copiar." : $"Backup de segurança criado em:\n{path}", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "SIGFUR - Backup de segurança", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Integrity_Click(object sender, RoutedEventArgs e)
    {
        var backup = Path.Combine(SyncPathTextBox.Text ?? string.Empty, "ultimo.sigfurbak");
        var ok = false;
        try { SigfurBackupService.ValidateBackup(backup); ok = true; } catch { }
        MessageBox.Show(this,
            ok ? "Integridade conferida: o último backup pôde ser aberto e validado." : "Não foi possível validar o último backup.",
            "SIGFUR - Integridade",
            MessageBoxButton.OK,
            ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async void ChooseSync_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProfileMode()) return;
        var dialog = new OpenFolderDialog
        {
            Title = "Escolha a pasta de sincronização",
            Multiselect = false,
            InitialDirectory = Directory.Exists(SyncPathTextBox.Text) ? SyncPathTextBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) == true)
        {
            SyncPathTextBox.Text = dialog.FolderName;
            await RefreshLastBackupTextAsync();
        }
    }

    private void OpenSync_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(SyncPathTextBox.Text)) ShellService.OpenPath(SyncPathTextBox.Text);
    }

    private void OpenLocal_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(LocalPathTextBox.Text)) ShellService.OpenPath(LocalPathTextBox.Text);
    }

    private async void DisableBackupOnClose_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProfileMode()) return;
        BackupOnCloseCheckBox.IsChecked = false;
        await SaveSettingsInternalAsync();
        MessageBox.Show(this, "Backup ao fechar desativado para este perfil.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProfileMode()) { Close(); return; }
        try
        {
            await SaveSettingsInternalAsync();
            MessageBox.Show(this, "Configurações de Perfil, Backup e Sincronização salvas.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "SIGFUR - Configurações", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SaveSettingsInternalAsync()
    {
        if (_session.Config is null) return;
        if (string.IsNullOrWhiteSpace(SyncPathTextBox.Text)) throw new InvalidOperationException("Informe a pasta de sincronização.");
        Directory.CreateDirectory(SyncPathTextBox.Text);
        _session.Config.SyncFolderPath = Path.GetFullPath(SyncPathTextBox.Text.Trim().Trim('"'));
        _session.Config.BackupOnClose = BackupOnCloseCheckBox.IsChecked == true;
        _session.Config.CreateSafetyBackupBeforeRestore = SafetyBackupCheckBox.IsChecked != false;
        await _profileService.UpdateLocalProfileAsync(_session.Config);
    }

    private bool EnsureProfileMode()
    {
        if (_session.IsProfileMode) return true;
        MessageBox.Show(this, "Você entrou sem login. Para usar backup/sincronização, crie ou entre com um Perfil SIGFUR na próxima abertura.", "SIGFUR - Modo sem login", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
