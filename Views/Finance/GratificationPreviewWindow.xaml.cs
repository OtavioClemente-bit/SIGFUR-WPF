using System.Windows;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Finance;

public partial class GratificationPreviewWindow : Window
{
    private readonly BulletinRenderResult _render;
    private readonly IReadOnlyList<MilitaryRecord> _military;
    private readonly GratificationSettings _settings;
    private readonly BulletinService _bulletinService;

    public GratificationPreviewWindow(BulletinRenderResult render, IReadOnlyList<MilitaryRecord> military, GratificationSettings settings)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _render = render;
        _military = military;
        _settings = settings;
        _bulletinService = new BulletinService(App.Paths, App.Json, App.Log);
        PreviewViewer.Document = _bulletinService.BuildDocument(render);
        SubjectBox.Text = string.IsNullOrWhiteSpace(settings.SisbolSubject) ? "Gratificação de Representação" : settings.SisbolSubject;
        CodeBox.Text = settings.SisbolSpecificCode;
        StatusText.Text = $"{military.Count} militar(es). Mesma renderização dos boletins do SIGFUR: Times New Roman 10 pt, justificado, com nome de guerra em negrito real.";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        BulletinService.CopyForWord(PreviewViewer.Document, _render.Text);
        StatusText.Text = "Texto copiado com formatação para Word e SisBol.";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Salvar texto do boletim", Filter = "Arquivo de texto|*.txt", FileName = $"boletim_gratificacao_{DateTime.Now:yyyyMMdd}.txt" };
        if (dialog.ShowDialog(this) != true) return;
        await File.WriteAllTextAsync(dialog.FileName, _render.Text, System.Text.Encoding.UTF8);
        StatusText.Text = $"Texto salvo: {dialog.FileName}";
    }


    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        _settings.SisbolSubject = string.IsNullOrWhiteSpace(SubjectBox.Text) ? "Gratificação de Representação" : SubjectBox.Text.Trim();
        _settings.SisbolSpecificCode = CodeBox.Text.Trim();
        if (!App.Sisbol.IsReady)
        {
            SigfurDialog.Show(this,
                "O SisBol não está preparado. Vá na janela principal, clique em ‘Preparar SisBol’, conclua o login/captcha e valide a sessão.",
                "SisBol não preparado", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText.Text = "SisBol não preparado. Prepare na janela principal antes de enviar.";
            return;
        }
        try
        {
            StatusText.Text = "Enviando ao SisBol…";
            var sisbolSubject = string.IsNullOrWhiteSpace(_settings.SisbolSpecificCode)
                ? _settings.SisbolSubject
                : $"{_settings.SisbolSpecificCode.Trim()} - {_settings.SisbolSubject}";
            await App.Sisbol.SendAsync(
                _render.Text,
                _military,
                sisbolSubject,
                IncludeConsequencesCheck.IsChecked == true,
                ConsequencesTextBox.Text);
            await App.Gratifications.SaveSettingsAsync(_settings);
            StatusText.Text = "Matéria incluída no SisBol com sucesso.";
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha ao enviar Gratificação ao SisBol.", ex);
            SigfurDialog.Show(this, ex.Message, "Enviar ao SisBol", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Falha no envio. A sessão foi mantida para conferência.";
        }
    }
}
