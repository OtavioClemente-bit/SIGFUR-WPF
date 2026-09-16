using System.Windows;
using System.Windows.Controls;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Military;

public partial class DocumentDetailsWindow : Window
{
    private bool _ready;
    private readonly bool _ocrFileSupported;
    public DocumentDetailsWindow(string filePath)
    {
        InitializeComponent();
        _ocrFileSupported = CertificateOcrService.SupportsFile(filePath);
        App.UiState.Attach(this);
        FileText.Text = Path.GetFileName(filePath);
        TitleBox.Text = Path.GetFileNameWithoutExtension(filePath);
        Loaded += (_, _) => { _ready = true; UpdateOcrVisibility(); };
    }
    public string DocumentType => (TypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "DOCUMENTO_DIVERSO";
    public string DocumentTitle => TitleBox.Text.Trim();
    public bool RunCertificateOcr => DocumentType == "CERTIDAO_NASCIMENTO" && _ocrFileSupported && OcrCheck.IsChecked == true;
    private void TypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_ready) UpdateOcrVisibility(); }
    private void UpdateOcrVisibility()
    {
        var certificate = DocumentType == "CERTIDAO_NASCIMENTO";
        OcrCard.Visibility = certificate ? Visibility.Visible : Visibility.Collapsed;
        OcrCheck.IsEnabled = _ocrFileSupported;
        OcrCheck.IsChecked = certificate && _ocrFileSupported;
        OcrCheck.Content = _ocrFileSupported
            ? "Ler a certidão automaticamente e criar as chaves do Boletim"
            : "OCR indisponível para este formato — use PDF ou imagem";
    }
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DocumentTitle)) { SigfurDialog.Show(this, "Informe o título do documento.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        DialogResult = true;
    }
}
