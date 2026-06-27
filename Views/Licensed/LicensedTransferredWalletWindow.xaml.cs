using Microsoft.Win32;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views.Military;

namespace SIGFUR.Wpf.Views.Licensed;

public partial class LicensedTransferredWalletWindow : Window
{
    private readonly LicensedTransferredRepository _repository;
    private readonly PaystubService _paystubs;
    private LicensedTransferredRecord _record;
    public bool Changed { get; private set; }
    public bool Restored { get; private set; }

    public LicensedTransferredWalletWindow(LicensedTransferredRepository repository, PaystubService paystubs, LicensedTransferredRecord record, int initialTab = 0)
    {
        InitializeComponent(); App.UiState.Attach(this);
        _repository = repository; _paystubs = paystubs; _record = record.Clone(); Tabs.SelectedIndex = Math.Clamp(initialTab, 0, 4);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await ReloadAsync();
    private async Task ReloadAsync()
    {
        var current = await _repository.GetByIdAsync(_record.Id);
        if (current is not null) _record = current;
        RankText.Text = _record.ShortRank;
        NameText.FullName = _record.Name; NameText.WarName = _record.WarName;
        IdentityText.Text = $"CPF {_record.FormattedCpf}  •  PREC-CP {_record.FormattedPrecCp}";
        SituationText.Text = $"{_record.Reason}  •  Destino: {_record.Destination}  •  {_record.StatusText}";
        IdentificationText.Text = $"Nome de Guerra: {_record.WarName}\nIDT: {_record.MilitaryId}\nAno de formação: {_record.FormationYear}\nMotivo: {_record.Reason}\nDestino/OM: {_record.Destination}";
        ContactText.Text = $"Telefone: {_record.Phone}\nE-mail: {_record.Email}\nEscolaridade: {_record.Education}\nEndereço: {_record.Address}\nCEP: {_record.ZipCode}";
        BankDateText.Text = $"Banco: {_record.Bank}\nAgência: {_record.Agency}\nConta: {_record.Account}\nNascimento: {_record.BirthDate}\nData de praça: {_record.EnlistmentDate}";
        BenefitsText.Text = $"Pré-escolar: {_record.ReceivesPreSchool} — {_record.PreSchoolValue}\nAuxílio-Transporte: {_record.ReceivesTransportAid} — {_record.TransportAidValue}\nPNR: {_record.HasPnr}\nPensão alimentícia: {_record.Alimony} — {_record.AlimonyValue}";
        LoadPhoto();
        var military = _record.ToMilitaryRecord();
        var paystubList = await _paystubs.FindForMilitaryAsync(military); PaystubGrid.ItemsSource = paystubList;
        FinancialStatementGrid.ItemsSource = await _paystubs.FindFinancialStatementsForMilitaryAsync(military);
        DocumentGrid.ItemsSource = await _repository.GetDocumentsAsync(_record.Id);
        var fares = await _repository.GetTransportFaresAsync(_record.Id); FareGrid.ItemsSource = fares;
        var salary = await _repository.GetSalaryAsync(_record.Rank);
        TransportSummaryText.Text = $"Soldo de referência: {salary.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"))}\nDias úteis preservados: {_record.TransportWorkingDays?.ToString() ?? "—"}\nTotal bruto preservado: {(_record.TransportGrossTotal?.ToString("C2", CultureInfo.GetCultureInfo("pt-BR")) ?? "—")}\nTarifas de ônibus encontradas: {fares.Count}\nBase do cálculo: {_record.TransportBaseTimestamp}";
        StatusText.Text = "Carteira atualizada.";
    }

    private void LoadPhoto()
    {
        PhotoImage.Source = null;
        try
        {
            if (string.IsNullOrWhiteSpace(_record.PhotoPath) || !File.Exists(_record.PhotoPath)) return;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 300;
            image.UriSource = new Uri(_record.PhotoPath);
            image.EndInit();
            image.Freeze();
            PhotoImage.Source = image;
        }
        catch { }
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        var editor = new LicensedTransferredEditorWindow(_repository, _record) { Owner = this };
        if (editor.ShowDialog() == true) { Changed = true; await ReloadAsync(); }
    }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (SigfurDialog.Show(this, $"Restaurar {_record.ShortRank} {_record.Name} para a lista principal?\n\nDocumentos, certidões e tarifas de AT permanecerão vinculados.", "Restaurar militar", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await _repository.RestoreToActiveAsync(_record); Restored = true; Changed = true; DialogResult = true; }
        catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "Restaurar", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private async void DownloadPaystub_Click(object sender, RoutedEventArgs e)
    {
        var person = new CpexPaystubPerson(_record.Name, _record.Cpf, _record.ShortRank, _record.Id, _record.MilitaryId, _record.PrecCp);
        var window = new CpexPaystubDownloadWindow(App.CpexPaystubs, [person]) { Owner = this };
        window.ShowDialog(); if (window.DownloadedAny) { _paystubs.InvalidateCache(); await ReloadAsync(); }
    }
    private void OpenPaystub_Click(object sender, RoutedEventArgs e) => OpenSelectedPaystub();
    private void PaystubGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedPaystub();
    private void OpenSelectedPaystub() { if (PaystubGrid.SelectedItem is PaystubFileRecord p && File.Exists(p.Path)) ShellService.OpenPath(p.Path); }
    private void OpenPaystubFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = PersonDocumentStorageService.PrepareRegisteredFolder(
            App.Paths, PersonDocumentStorageService.ResolveConfiguredRoot(App.Paths),
            _record.Rank, _record.Name, _record.Cpf, _record.PrecCp);
        Directory.CreateDirectory(folder); ShellService.OpenPath(folder);
    }
    private async void CaptureFinancialStatement_Click(object sender, RoutedEventArgs e)
    {
        var prompt = new TextPromptWindow("Ficha Financeira", "Informe o ano da ficha que será baixada no portal do CPEx.", DateTime.Now.Year.ToString()) { Owner = this };
        if (prompt.ShowDialog() != true) return;
        if (!int.TryParse(prompt.Value, out var year) || year is < 2000 or > 2200)
        {
            StatusText.Text = "Informe um ano válido.";
            return;
        }
        try
        {
            Clipboard.SetText(MilitaryFormatting.Digits(_record.Cpf));
            var progress = new Progress<string>(message => StatusText.Text = message);
            var saved = await App.FinancialStatements.CaptureAsync(_record.ToMilitaryRecord(), year, progress);
            _paystubs.InvalidateCache();
            Changed = true;
            await ReloadAsync();
            ShellService.OpenPath(saved);
        }
        catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "Ficha Financeira", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private async void RefreshFinancialStatements_Click(object sender, RoutedEventArgs e)
    {
        _paystubs.InvalidateCache();
        FinancialStatementGrid.ItemsSource = await _paystubs.FindFinancialStatementsForMilitaryAsync(_record.ToMilitaryRecord());
        StatusText.Text = "Fichas financeiras atualizadas.";
    }
    private void OpenFinancialStatement_Click(object sender, RoutedEventArgs e) => OpenSelectedFinancialStatement();
    private void FinancialStatementGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedFinancialStatement();
    private void OpenSelectedFinancialStatement()
    {
        if (FinancialStatementGrid.SelectedItem is PaystubFileRecord item && File.Exists(item.Path)) ShellService.OpenPath(item.Path);
    }
    private void OpenFinancialStatementFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = FinancialStatementGrid.SelectedItem is PaystubFileRecord item
            ? Path.GetDirectoryName(item.Path)
            : PersonDocumentStorageService.PrepareRegisteredFolder(
                App.Paths, PersonDocumentStorageService.ResolveConfiguredRoot(App.Paths),
                _record.Rank, _record.Name, _record.Cpf, _record.PrecCp);
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        ShellService.OpenPath(folder);
    }
    private async void DeletePaystub_Click(object sender, RoutedEventArgs e)
    {
        if (PaystubGrid.SelectedItem is not PaystubFileRecord paystub || !File.Exists(paystub.Path))
        {
            StatusText.Text = "Selecione um contracheque salvo.";
            return;
        }
        if (SigfurDialog.Show(this,
                $"Excluir definitivamente este PDF?\n\n{paystub.FileName}",
                "Excluir contracheque", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            File.Delete(paystub.Path);
            _paystubs.InvalidateCache();
            Changed = true;
            await ReloadAsync();
            StatusText.Text = "Contracheque excluído.";
        }
        catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "Excluir contracheque", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void AddDocument_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Adicionar documento à carteira", Filter = "Documentos e imagens|*.pdf;*.png;*.jpg;*.jpeg;*.webp;*.doc;*.docx|Todos os arquivos|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        var details = new DocumentDetailsWindow(dialog.FileName) { Owner = this };
        if (details.ShowDialog() != true) return;
        try
        {
            var observation = string.Empty;
            var keysJson = string.Empty;
            Dictionary<string, string>? keys = null;
            if (details.RunCertificateOcr)
            {
                var processed = await ProcessCertificateOcrAsync(dialog.FileName);
                if (processed is null) return;
                observation = processed.Value.Observation;
                keysJson = processed.Value.KeysJson;
                keys = processed.Value.Keys;
            }
            await _repository.AddDocumentAsync(_record, dialog.FileName, details.DocumentType, details.DocumentTitle, observation, keysJson);
            if (keys is not null)
            {
                try { await App.CertificateOcr.SaveGlobalKeysAsync(keys, _record.ToMilitaryRecord()); }
                catch (Exception keyEx) { await App.Log.WriteAsync("Certidão LT salva, mas houve falha ao atualizar chaves globais.", keyEx); }
            }
            Changed = true;
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            var savePlain = details.RunCertificateOcr && SigfurDialog.Show(this,
                "Não foi possível concluir a leitura automática da certidão.\n\n" + ex.Message +
                "\n\nDeseja salvar o documento sem as chaves OCR?",
                "SIGFUR — Certidão", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
            if (savePlain)
            {
                try { await _repository.AddDocumentAsync(_record, dialog.FileName, details.DocumentType, details.DocumentTitle); Changed = true; await ReloadAsync(); }
                catch (Exception saveEx) { SigfurDialog.Show(this, saveEx.Message, "Documento", MessageBoxButton.OK, MessageBoxImage.Error); }
            }
            else SigfurDialog.Show(this, ex.Message, "Documento", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenDocumentFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = DocumentGrid.SelectedItem is MilitaryDocumentRecord document && !string.IsNullOrWhiteSpace(document.Path)
            ? Path.GetDirectoryName(document.Path)
            : Path.Combine(App.Paths.MilitaryDocumentsDirectory, $"{_record.Id:000000}_{Safe(_record.Name)}");
        if (string.IsNullOrWhiteSpace(path)) return;
        Directory.CreateDirectory(path);
        ShellService.OpenPath(path);
    }

    private void PhotoImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2 || string.IsNullOrWhiteSpace(_record.PhotoPath) || !File.Exists(_record.PhotoPath)) return;
        ShellService.OpenPath(_record.PhotoPath);
        e.Handled = true;
    }

    private async Task<(string Observation, string KeysJson, Dictionary<string, string> Keys)?> ProcessCertificateOcrAsync(string filePath)
    {
        StatusText.Text = "Lendo a certidão e identificando os campos...";
        var military = _record.ToMilitaryRecord();
        var result = await App.CertificateOcr.ReadAsync(filePath, military);
        var review = new CertificateOcrReviewWindow(result) { Owner = this };
        if (review.ShowDialog() != true) return null;
        var keys = CertificateOcrService.ExpandAliases(review.Values, military);
        var json = JsonSerializer.Serialize(keys, new JsonSerializerOptions { WriteIndented = true });
        return ($"Certidão lida e conferida pelo OCR em {DateTime.Now:dd/MM/yyyy HH:mm}.", json, keys);
    }

    private async void ReprocessOcr_Click(object sender, RoutedEventArgs e)
    {
        if (DocumentGrid.SelectedItem is not MilitaryDocumentRecord document)
        {
            StatusText.Text = "Selecione uma certidão.";
            return;
        }
        if (!document.Type.Equals("CERTIDAO_NASCIMENTO", StringComparison.OrdinalIgnoreCase))
        {
            SigfurDialog.Show(this, "Selecione um documento cadastrado como Certidão de nascimento.", "OCR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!document.Exists || !CertificateOcrService.SupportsFile(document.Path))
        {
            SigfurDialog.Show(this, "O arquivo não foi encontrado ou não é compatível com OCR.", "OCR", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var processed = await ProcessCertificateOcrAsync(document.Path);
            if (processed is null) return;
            await App.MilitaryRepository.UpdateDocumentOcrAsync(document.Id, processed.Value.Observation, processed.Value.KeysJson);
            await App.CertificateOcr.SaveGlobalKeysAsync(processed.Value.Keys, _record.ToMilitaryRecord());
            Changed = true;
            await ReloadAsync();
            StatusText.Text = "Certidão reprocessada e chaves do Boletim atualizadas.";
        }
        catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "OCR da certidão", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void OpenDocument_Click(object sender, RoutedEventArgs e) => OpenSelectedDocument();
    private void DocumentGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedDocument();
    private void OpenSelectedDocument() { if (DocumentGrid.SelectedItem is MilitaryDocumentRecord d && d.Exists) ShellService.OpenPath(d.Path); }
    private async void RemoveDocument_Click(object sender, RoutedEventArgs e)
    {
        if (DocumentGrid.SelectedItem is not MilitaryDocumentRecord d) return;
        if (SigfurDialog.Show(this, "Remover o registro deste documento? O arquivo físico será preservado.", "Remover documento", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await _repository.RemoveDocumentAsync(d, false); Changed = true; await ReloadAsync();
    }
    private void InformationText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2 || sender is not System.Windows.Controls.TextBlock text || string.IsNullOrWhiteSpace(text.Text)) return;
        Clipboard.SetText(text.Text);
        StatusText.Text = "Informações copiadas para a área de transferência.";
        e.Handled = true;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText($"{_record.ShortRank} {_record.Name}\nNome de Guerra: {_record.WarName}\nCPF: {_record.FormattedCpf}\nPREC-CP: {_record.FormattedPrecCp}\nIDT: {_record.MilitaryId}\nTelefone: {_record.Phone}\nE-mail: {_record.Email}\nMotivo: {_record.Reason}\nDestino: {_record.Destination}");
        StatusText.Text = "Dados copiados.";
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Exportar carteira completa", Filter = "Arquivo ZIP|*.zip", FileName = $"Carteira_{Safe(_record.Name)}.zip" };
        if (dialog.ShowDialog(this) != true) return;
        try { var paystubs = (await _paystubs.FindForMilitaryAsync(_record.ToMilitaryRecord())).ToList(); await _repository.ExportWalletAsync(_record, dialog.FileName, paystubs); StatusText.Text = "Carteira completa exportada."; }
        catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "Exportar carteira", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private static string Safe(string value) => string.Join("_", value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Replace(' ', '_');
}
