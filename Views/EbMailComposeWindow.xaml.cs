using System.Collections.ObjectModel;
using System.Net.Mail;
using System.Text.RegularExpressions;
using System.Windows;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views;

public partial class EbMailComposeWindow : Window
{
    private readonly ObservableCollection<EbMailDocument> _documents;
    private readonly EbMailAutomationService _automation = new(App.Paths, App.Log);
    private EbMailSettings _settings = new();
    private CancellationTokenSource? _cancellation;
    private bool _busy;

    public EbMailComposeWindow(IEnumerable<EbMailDocument> documents)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _documents = new ObservableCollection<EbMailDocument>(documents
            .Where(x => x.HasAnyAttachment)
            .DistinctBy(x => string.Join("|", x.Reference, x.ResolvedOriginalPath, x.ResolvedSignedPath), StringComparer.OrdinalIgnoreCase));
        DocumentsGrid.ItemsSource = _documents;
        UpdateDocumentSummary();
        Loaded += async (_, _) => await LoadSettingsAsync();
    }

    private async Task LoadSettingsAsync()
    {
        _settings = await App.Json.LoadAsync<EbMailSettings>(App.Paths.EbMailSettingsFile) ?? new EbMailSettings();
        _settings.Presets ??= [];
        RefreshPresets();
        var selected = _settings.Presets.FirstOrDefault(x => x.Id == _settings.LastPresetId) ?? _settings.Presets.FirstOrDefault();
        if (selected is not null) PresetBox.SelectedItem = selected;
        else ApplyPreset(new EbMailRecipientPreset { Name = "Novo grupo" });
        AutoSendCheck.IsChecked = _settings.SendAutomatically;
        UpdateTemplatePreview();
    }

    private void RefreshPresets(EbMailRecipientPreset? selected = null)
    {
        PresetBox.ItemsSource = null;
        PresetBox.ItemsSource = _settings.Presets.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (selected is not null) PresetBox.SelectedItem = PresetBox.Items.Cast<EbMailRecipientPreset>().FirstOrDefault(x => x.Id == selected.Id);
    }

    private void PresetBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PresetBox.SelectedItem is EbMailRecipientPreset preset) ApplyPreset(preset);
    }

    private void NewPreset_Click(object sender, RoutedEventArgs e)
    {
        PresetBox.SelectedItem = null;
        ApplyPreset(new EbMailRecipientPreset { Name = "Novo grupo" });
        SavePresetCheck.IsChecked = true;
        ToBox.Focus();
    }

    private async void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        var invalid = InvalidAddresses(ToBox.Text).Concat(InvalidAddresses(CcBox.Text)).Concat(InvalidAddresses(BccBox.Text)).Distinct().ToList();
        if (string.IsNullOrWhiteSpace(ToBox.Text) || invalid.Count > 0)
        {
            var message = string.IsNullOrWhiteSpace(ToBox.Text)
                ? "Informe ao menos um destinatário no campo Para."
                : "Confira os endereços: " + string.Join(", ", invalid);
            SigfurDialog.Show(this, message, "Destinatários do EBMail", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var saved = UpsertCurrentPreset();
        _settings.LastPresetId = saved.Id;
        _settings.ReuseProtectedBrowserSession = true;
        await App.Json.SaveAsync(App.Paths.EbMailSettingsFile, _settings);
        RefreshPresets(saved);
        SavePresetCheck.IsChecked = true;
        SigfurDialog.Show(this, $"O grupo '{saved.Name}' foi salvo e será carregado automaticamente.", "Destinatários do EBMail", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OpenSession_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        OpenSessionButton.IsEnabled = false;
        ProgressStageText.Text = "LOGIN";
        ProgressMessageText.Text = "Abrindo o perfil protegido do EBMail…";
        try
        {
            _settings.ReuseProtectedBrowserSession = true;
            await App.Json.SaveAsync(App.Paths.EbMailSettingsFile, _settings);
            await _automation.OpenSessionAsync();
            ProgressMessageText.Text = "EBMail aberto. Faça o login, aceite salvar a senha no navegador e marque o dispositivo como confiável.";
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha ao abrir a sessão do EBMail.", ex);
            SigfurDialog.Show(this, "Não foi possível abrir o EBMail.\n\n" + ex.Message, "EBMail", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            OpenSessionButton.IsEnabled = true;
        }
    }

    private void ApplyPreset(EbMailRecipientPreset preset)
    {
        PresetNameBox.Text = preset.Name;
        ToBox.Text = preset.To;
        CcBox.Text = preset.Cc;
        BccBox.Text = preset.Bcc;
        SubjectBox.Text = preset.SubjectTemplate;
        BodyBox.Text = preset.BodyTemplate;
        UpdateTemplatePreview();
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (PresetBox.SelectedItem is not EbMailRecipientPreset preset) return;
        if (SigfurDialog.Show(this, $"Excluir o grupo de destinatários '{preset.Name}'?", "EBMail", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _settings.Presets.RemoveAll(x => x.Id == preset.Id);
        _settings.LastPresetId = string.Empty;
        RefreshPresets();
        ApplyPreset(new EbMailRecipientPreset { Name = "Novo grupo" });
        _ = App.Json.SaveAsync(App.Paths.EbMailSettingsFile, _settings);
    }

    private void RemoveDocument_Click(object sender, RoutedEventArgs e)
    {
        foreach (var document in DocumentsGrid.SelectedItems.Cast<EbMailDocument>().ToList()) _documents.Remove(document);
        UpdateDocumentSummary();
        UpdateTemplatePreview();
    }

    private void UpdateDocumentSummary()
    {
        var files = _documents.Sum(document => document.SelectedFilePaths.Count);
        DocumentCountText.Text = $"{_documents.Count} documento(s) selecionado(s) • {files} arquivo(s) para anexar";
    }

    private void AttachmentMode_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateDocumentSummary();
        UpdateTemplatePreview();
    }

    private void TemplateBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateTemplatePreview();

    private void UpdateTemplatePreview()
    {
        if (SubjectPreviewText is null) return;
        SubjectPreviewText.Text = "Prévia: " + ExpandTemplate(SubjectBox?.Text ?? string.Empty);
    }

    private string ExpandTemplate(string template)
    {
        var kinds = _documents.Select(x => x.Kind).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var type = kinds.Count switch { 0 => "documentos", 1 => kinds[0], _ => "BIs e ADTs" };
        var references = string.Join(", ", _documents.Select(x => x.Reference).Where(x => !string.IsNullOrWhiteSpace(x) && x != "—"));
        var list = string.Join(Environment.NewLine, _documents.Select(x => $"• {x.DisplayReference} — {x.SignatureText}"));
        return (template ?? string.Empty)
            .Replace("{TIPO}", type, StringComparison.OrdinalIgnoreCase)
            .Replace("{REFERENCIAS}", references, StringComparison.OrdinalIgnoreCase)
            .Replace("{QUANTIDADE}", _documents.Count.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{LISTA_DOCUMENTOS}", list, StringComparison.OrdinalIgnoreCase)
            .Replace("{DATA}", DateTime.Today.ToString("dd/MM/yyyy"), StringComparison.OrdinalIgnoreCase);
    }

    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_documents.Count == 0)
        {
            SigfurDialog.Show(this, "Selecione ao menos um BI ou ADT.", "EBMail", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var invalid = InvalidAddresses(ToBox.Text).Concat(InvalidAddresses(CcBox.Text)).Concat(InvalidAddresses(BccBox.Text)).Distinct().ToList();
        if (string.IsNullOrWhiteSpace(ToBox.Text) || invalid.Count > 0)
        {
            var message = string.IsNullOrWhiteSpace(ToBox.Text) ? "Informe ao menos um destinatário no campo Para." : "Confira os endereços: " + string.Join(", ", invalid);
            SigfurDialog.Show(this, message, "Destinatários do EBMail", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var autoSend = AutoSendCheck.IsChecked == true;
        if (autoSend)
        {
            var recipients = string.Join("; ", SplitAddresses(ToBox.Text));
            var attachmentCount = _documents.Sum(document => document.SelectedFilePaths.Count);
            if (SigfurDialog.Show(this,
                    $"O SIGFUR preparará e enviará a mensagem pelo EBMail.\n\nPara: {recipients}\nAssunto: {ExpandTemplate(SubjectBox.Text)}\nAnexos: {attachmentCount}\n\nConfirma o envio automático?",
                    "Confirmar envio pelo EBMail", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        }

        await SaveSettingsAsync();
        var request = new EbMailComposeRequest
        {
            To = ToBox.Text,
            Cc = CcBox.Text,
            Bcc = BccBox.Text,
            Subject = ExpandTemplate(SubjectBox.Text),
            Body = ExpandTemplate(BodyBox.Text),
            SendAutomatically = autoSend,
            Documents = _documents.ToList()
        };

        SetBusy(true);
        _cancellation = new CancellationTokenSource();
        var progress = new Progress<EbMailProgress>(item =>
        {
            ProgressStageText.Text = item.Stage;
            ProgressMessageText.Text = item.Message;
            ProgressBar.Value = item.Percent;
        });
        try
        {
            var result = await _automation.PrepareAsync(request, progress, _cancellation.Token);
            ProgressBar.Value = 100;
            ProgressStageText.Text = result.Sent ? "ENVIADO" : "PRONTO PARA REVISÃO";
            ProgressMessageText.Text = result.Message;
            SigfurDialog.Show(this, result.Message, "EBMail", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            ProgressStageText.Text = "CANCELADO";
            ProgressMessageText.Text = "A preparação foi cancelada. Nenhuma mensagem foi enviada pelo SIGFUR.";
            SetBusy(false);
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha ao preparar mensagem no EBMail.", ex);
            ProgressStageText.Text = "FALHA";
            ProgressMessageText.Text = ex.Message;
            SigfurDialog.Show(this, "Não foi possível preparar a mensagem.\n\n" + ex.Message, "EBMail", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetBusy(false);
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private async Task SaveSettingsAsync()
    {
        EbMailRecipientPreset? saved = null;
        if (SavePresetCheck.IsChecked == true)
        {
            saved = UpsertCurrentPreset();
            _settings.LastPresetId = saved.Id;
        }
        else if (PresetBox.SelectedItem is EbMailRecipientPreset selected)
        {
            _settings.LastPresetId = selected.Id;
        }
        _settings.SendAutomatically = false;
        _settings.ReuseProtectedBrowserSession = true;
        await App.Json.SaveAsync(App.Paths.EbMailSettingsFile, _settings);
        if (saved is not null) RefreshPresets(saved);
    }

    private EbMailRecipientPreset UpsertCurrentPreset()
    {
        var name = PresetNameBox.Text.Trim();
        if (name.Length == 0 || name.Equals("Novo grupo", StringComparison.OrdinalIgnoreCase))
            name = "Destinatários frequentes";

        var saved = PresetBox.SelectedItem as EbMailRecipientPreset;
        if (saved is null || !_settings.Presets.Contains(saved))
            saved = _settings.Presets.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (saved is null)
        {
            saved = new EbMailRecipientPreset();
            _settings.Presets.Add(saved);
        }

        saved.Name = name;
        saved.To = ToBox.Text.Trim();
        saved.Cc = CcBox.Text.Trim();
        saved.Bcc = BccBox.Text.Trim();
        saved.SubjectTemplate = SubjectBox.Text;
        saved.BodyTemplate = BodyBox.Text;
        return saved;
    }

    private static IEnumerable<string> InvalidAddresses(string raw)
        => SplitAddresses(raw).Where(value => !MailAddress.TryCreate(value, out _));

    private static List<string> SplitAddresses(string raw)
        => Regex.Split(raw ?? string.Empty, @"[;,\r\n]+")
            .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void SetBusy(bool busy)
    {
        _busy = busy;
        FormPanel.IsEnabled = !busy;
        DocumentsGrid.IsEnabled = !busy;
        PresetBox.IsEnabled = !busy;
        PrepareButton.IsEnabled = !busy;
        CancelButton.Content = busy ? "Cancelar preparação" : "Cancelar";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { _cancellation?.Cancel(); return; }
        DialogResult = false;
    }
}
