using System.Windows;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views;

public partial class UseExistingProfileWindow : Window
{
    private readonly ProfileService _profileService;
    private readonly SigfurRestoreService _restoreService;
    private readonly string _syncFolder;
    private readonly UiStatePersistenceService? _uiState;

    public SigfurProfileSession? Session { get; private set; }

    public UseExistingProfileWindow(ProfileService profileService, SigfurRestoreService restoreService, IReadOnlyList<SigfurProfile> profiles, string syncFolder, UiStatePersistenceService? uiState = null)
    {
        InitializeComponent();
        _profileService = profileService;
        _restoreService = restoreService;
        _syncFolder = syncFolder;
        _uiState = uiState;
        _uiState?.Attach(this, "SIGFUR.UseExistingProfileWindow");
        ProfilesComboBox.ItemsSource = profiles;
        if (profiles.Count > 0) ProfilesComboBox.SelectedIndex = 0;
        LocalPathTextBox.Text = Path.Combine(AppPaths.GetDefaultDataDirectory(), "Perfis", profiles.FirstOrDefault()?.ProfileName ?? "Perfil");
    }

    private void BrowseLocal_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Escolha a pasta local dos dados",
            Multiselect = false,
            InitialDirectory = Directory.Exists(LocalPathTextBox.Text) ? LocalPathTextBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) == true) LocalPathTextBox.Text = dialog.FolderName;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesComboBox.SelectedItem is not SigfurProfile)
        {
            MessageBox.Show(this, "Nenhum perfil foi selecionado.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            StatusTextBlock.Text = "Validando senha e vinculando perfil...";
            var session = await _profileService.LinkExistingProfileAsync(_syncFolder, PasswordBox.Password, LocalPathTextBox.Text);
            if (session.Config is not null)
            {
                session.Config.BackupOnClose = BackupOnCloseCheckBox.IsChecked == true;
                session.Config.CreateSafetyBackupBeforeRestore = SafetyBackupCheckBox.IsChecked != false;
                await _profileService.UpdateLocalProfileAsync(session.Config);
            }

            var restored = false;
            if (RestoreCheckBox.IsChecked == true)
            {
                StatusTextBlock.Text = "Restaurando último backup...";
                await _restoreService.RestoreLatestBackupAsync(session, session.Config?.CreateSafetyBackupBeforeRestore != false);
                restored = true;
            }

            session.StatusMessage = restored
                ? "Dados restaurados com sucesso. O SIGFUR será aberto com os dados deste perfil."
                : "Perfil conectado com sucesso neste computador.";
            Session = session;
            MessageBox.Show(this, session.StatusMessage, "SIGFUR - Perfil existente", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Senha incorreta para este perfil.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning);
            Cursor = null;
            StatusTextBlock.Text = "Corrija a senha e tente novamente.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "SIGFUR - Usar perfil existente", MessageBoxButton.OK, MessageBoxImage.Error);
            Cursor = null;
            StatusTextBlock.Text = "Corrija os dados e tente novamente.";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
