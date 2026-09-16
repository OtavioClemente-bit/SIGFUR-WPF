using System.Windows;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views;

public partial class CreateProfileWindow : Window
{
    private readonly ProfileService _profileService;
    private readonly SigfurBackupService _backupService;
    private readonly UiStatePersistenceService? _uiState;

    public SigfurProfileSession? CreatedSession { get; private set; }

    public CreateProfileWindow(ProfileService profileService, SigfurBackupService backupService, UiStatePersistenceService? uiState = null)
    {
        InitializeComponent();
        _profileService = profileService;
        _backupService = backupService;
        _uiState = uiState;
        _uiState?.Attach(this, "SIGFUR.CreateProfileWindow");
        LocalPathTextBox.Text = AppPaths.GetDefaultDataDirectory();
        SyncPathTextBox.Text = ProfileService.GetDefaultSyncFolder("MeuPerfil");
    }

    private void ProfileNameTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!SyncPathTextBox.IsKeyboardFocusWithin)
            SyncPathTextBox.Text = ProfileService.GetDefaultSyncFolder(ProfileNameTextBox.Text);
    }

    private void BrowseLocal_Click(object sender, RoutedEventArgs e) => PickFolder(LocalPathTextBox, "Escolha a pasta local dos dados do SIGFUR");
    private void BrowseSync_Click(object sender, RoutedEventArgs e) => PickFolder(SyncPathTextBox, "Escolha a pasta de sincronização do Perfil SIGFUR");

    private void PickFolder(System.Windows.Controls.TextBox target, string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
            InitialDirectory = Directory.Exists(target.Text) ? target.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FolderName;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ProfileNameTextBox.Text)) throw new InvalidOperationException("Informe o nome do perfil.");
            if (string.IsNullOrWhiteSpace(PasswordBox.Password)) throw new InvalidOperationException("Informe a senha.");
            if (!PasswordBox.Password.Equals(ConfirmPasswordBox.Password, StringComparison.Ordinal)) throw new InvalidOperationException("A confirmação da senha não confere.");

            EnsureDirectoryWithConfirmation(LocalPathTextBox.Text, "A pasta local dos dados não existe. Deseja criá-la?");
            EnsureDirectoryWithConfirmation(SyncPathTextBox.Text, "A pasta de sincronização não existe. Deseja criá-la?");

            Cursor = System.Windows.Input.Cursors.Wait;
            StatusTextBlock.Text = "Criando Perfil SIGFUR...";
            CreatedSession = await _profileService.CreateProfileAsync(
                ProfileNameTextBox.Text,
                PasswordBox.Password,
                LocalPathTextBox.Text,
                SyncPathTextBox.Text,
                BackupOnCloseCheckBox.IsChecked == true,
                SafetyBackupCheckBox.IsChecked != false);

            if (InitialBackupCheckBox.IsChecked == true)
            {
                StatusTextBlock.Text = "Criando backup inicial...";
                await _backupService.CreateBackupAsync(CreatedSession);
            }

            MessageBox.Show(this,
                "Perfil SIGFUR criado com sucesso. Seus dados foram salvos na pasta de sincronização.",
                "SIGFUR - Perfil criado",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "SIGFUR - Criar perfil", MessageBoxButton.OK, MessageBoxImage.Error);
            Cursor = null;
            StatusTextBlock.Text = "Corrija os dados e tente novamente.";
        }
    }

    private void EnsureDirectoryWithConfirmation(string path, string message)
    {
        path = Environment.ExpandEnvironmentVariables((path ?? string.Empty).Trim().Trim('"'));
        if (Directory.Exists(path)) return;
        var result = MessageBox.Show(this, message, "SIGFUR", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) throw new InvalidOperationException("Operação cancelada: pasta obrigatória não criada.");
        Directory.CreateDirectory(path);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
