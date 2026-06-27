using Microsoft.Win32;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.ViewModels.Military;
using SIGFUR.Wpf.Views.Licensed;

namespace SIGFUR.Wpf.Views.Military;

public partial class MilitaryWalletWindow : Window
{
    private readonly MilitaryRepository _repository;
    private readonly MilitaryWalletViewModel _vm;
    private readonly int _initialTab;
    private bool _loaded;
    private bool _suppressTabLoad;
    private bool _mentionsLoaded;
    private bool _paystubsLoaded;
    private bool _financialStatementsLoaded;

    public MilitaryWalletWindow(MilitaryRepository repository, PaystubService paystubs, MilitaryRecord military, int initialTab = 0)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _repository = repository;
        _vm = new MilitaryWalletViewModel(repository, paystubs, military);
        _initialTab = Math.Clamp(initialTab, 0, 7);
        DataContext = _vm;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            await _vm.LoadAsync(includePaystubs: false, includeMentions: false);
            _suppressTabLoad = true;
            WalletTabs.SelectedIndex = _initialTab;
            _suppressTabLoad = false;
            LoadPhoto(_vm.Military.PhotoPath);
            await LoadSelectedTabAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        var editor = new MilitaryEditorWindow(_repository, _vm.Military, App.MilitaryPreferences) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            await _vm.LoadAsync(includePaystubs: false, includeMentions: false);
            LoadPhoto(_vm.Military.PhotoPath);
        }
    }

    private void CopySummary_Click(object sender, RoutedEventArgs e)
    {
        var item = _vm.Military;
        var text = new StringBuilder()
            .AppendLine($"{item.ShortRank} {item.Name}")
            .AppendLine($"Nome de guerra: {item.WarName}")
            .AppendLine($"CPF: {item.FormattedCpf}")
            .AppendLine($"PREC-CP: {item.PrecCp}")
            .AppendLine($"IDT: {item.MilitaryId}")
            .AppendLine($"Telefone: {item.Phone}")
            .AppendLine($"E-mail: {item.Email}")
            .AppendLine($"Banco: {item.Bank}")
            .AppendLine($"Agência: {item.Agency}")
            .AppendLine($"Conta: {item.Account}")
            .AppendLine($"Endereço: {item.Address}")
            .ToString().Trim();
        Clipboard.SetText(text);
        _vm.StatusText = "Resumo copiado para a área de transferência.";
    }

    private async void AddDocument_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Selecionar documento", Filter = "Documentos e imagens|*.pdf;*.png;*.jpg;*.jpeg;*.webp;*.doc;*.docx|Todos os arquivos|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        var details = new DocumentDetailsWindow(dialog.FileName) { Owner = this };
        if (details.ShowDialog() != true) return;
        var documentSaved = false;
        var ocrCompleted = false;
        try
        {
            var observation = string.Empty;
            var keysJson = string.Empty;
            Dictionary<string, string>? certificateKeys = null;
            if (details.RunCertificateOcr)
            {
                var processed = await ProcessCertificateOcrAsync(dialog.FileName);
                if (processed is null) return;
                observation = processed.Value.Observation;
                keysJson = processed.Value.KeysJson;
                certificateKeys = processed.Value.Keys;
                ocrCompleted = true;
            }
            await _vm.AddDocumentAsync(dialog.FileName, details.DocumentType, details.DocumentTitle, observation, keysJson);
            documentSaved = true;

            // A carteira é a fonte de verdade. Só publica as chaves globais depois
            // que o documento e as chaves por militar forem efetivamente salvos.
            if (certificateKeys is not null)
            {
                try
                {
                    await App.CertificateOcr.SaveGlobalKeysAsync(certificateKeys, _vm.Military);
                    _vm.StatusText = "Certidão salva e chaves do Boletim atualizadas.";
                }
                catch (Exception keyEx)
                {
                    await App.Log.WriteAsync("Documento salvo, mas houve falha ao atualizar as chaves globais da certidão.", keyEx);
                    SigfurDialog.Show(this,
                        "A certidão foi salva corretamente na carteira e as chaves ficaram vinculadas ao militar.\n\n" +
                        "Não foi possível apenas atualizar o arquivo global de chaves neste momento. Você pode reprocessar a certidão depois.\n\n" + keyEx.Message,
                        "SIGFUR — Certidão salva com aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            // Nunca tenta salvar uma segunda cópia quando o documento já entrou na carteira.
            if (documentSaved) { ShowError(ex); return; }

            var saveWithoutOcr = details.RunCertificateOcr && !ocrCompleted && SigfurDialog.Show(this,
                "Não foi possível concluir a leitura automática da certidão.\n\n" + ex.Message +
                "\n\nDeseja salvar o documento na carteira sem as chaves OCR?",
                "SIGFUR — Leitura da certidão", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
            if (saveWithoutOcr)
            {
                try { await _vm.AddDocumentAsync(dialog.FileName, details.DocumentType, details.DocumentTitle); }
                catch (Exception saveEx) { ShowError(saveEx); }
            }
            else ShowError(ex);
        }
    }

    private async Task<(string Observation, string KeysJson, Dictionary<string, string> Keys)?> ProcessCertificateOcrAsync(string filePath)
    {
        _vm.IsBusy = true;
        _vm.StatusText = "Lendo a certidão e identificando os campos…";
        CertificateOcrResult result;
        try { result = await App.CertificateOcr.ReadAsync(filePath, _vm.Military); }
        finally { _vm.IsBusy = false; }

        var review = new CertificateOcrReviewWindow(result) { Owner = this };
        if (review.ShowDialog() != true) return null;
        var keys = CertificateOcrService.ExpandAliases(review.Values, _vm.Military);
        var json = JsonSerializer.Serialize(keys, new JsonSerializerOptions { WriteIndented = true });
        var observation = $"Certidão lida e conferida pelo OCR em {DateTime.Now:dd/MM/yyyy HH:mm}. Chaves vinculadas ao cadastro do militar.";
        return (observation, json, keys);
    }

    private async void ReprocessDocumentOcr_Click(object sender, RoutedEventArgs e)
    {
        var document = _vm.SelectedDocument;
        if (document is null) { Notify("Selecione uma certidão."); return; }
        if (!document.Type.Equals("CERTIDAO_NASCIMENTO", StringComparison.OrdinalIgnoreCase))
        {
            Notify("A leitura automática está disponível para documentos cadastrados como Certidão de nascimento.");
            return;
        }
        if (!File.Exists(document.Path)) { Notify("O arquivo da certidão não foi encontrado."); return; }
        if (!CertificateOcrService.SupportsFile(document.Path))
        {
            Notify("Este formato não pode ser lido pelo OCR. Use uma certidão em PDF ou imagem.");
            return;
        }
        try
        {
            var processed = await ProcessCertificateOcrAsync(document.Path);
            if (processed is null) return;
            await _vm.UpdateDocumentOcrAsync(document, processed.Value.Observation, processed.Value.KeysJson);
            try
            {
                await App.CertificateOcr.SaveGlobalKeysAsync(processed.Value.Keys, _vm.Military);
                _vm.StatusText = "Certidão reprocessada e chaves do Boletim atualizadas.";
            }
            catch (Exception keyEx)
            {
                await App.Log.WriteAsync("Certidão reprocessada, mas houve falha ao atualizar as chaves globais.", keyEx);
                SigfurDialog.Show(this,
                    "A leitura foi salva no documento e ficou vinculada ao militar, mas o arquivo global de chaves não pôde ser atualizado.\n\n" + keyEx.Message,
                    "SIGFUR — Reprocessamento concluído com aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            DocumentsGrid.Items.Refresh();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OpenDocument_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedDocument is null) { Notify("Selecione um documento."); return; }
        ShellService.OpenPath(_vm.SelectedDocument.Path);
    }

    private void OpenDocumentFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedDocument is null) { Notify("Selecione um documento."); return; }
        ShellService.RevealInExplorer(_vm.SelectedDocument.Path);
    }

    private async void RemoveDocument_Click(object sender, RoutedEventArgs e)
    {
        var document = _vm.SelectedDocument;
        if (document is null) { Notify("Selecione um documento."); return; }
        var result = SigfurDialog.Show(this,
            "Deseja remover somente o registro da carteira?\n\nEscolha Sim para remover também o arquivo físico, ou Não para manter o arquivo na pasta.",
            "Remover documento", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Cancel) return;
        try { await _vm.RemoveDocumentAsync(document, result == MessageBoxResult.Yes); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void DocumentsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is MilitaryDocumentRecord row)
            _vm.SelectedDocument = row;
        if (_vm.SelectedDocument is null || string.IsNullOrWhiteSpace(_vm.SelectedDocument.Path)) return;
        e.Handled = true;
        ShellService.OpenPath(_vm.SelectedDocument.Path);
        _vm.StatusText = $"Aberto: {Path.GetFileName(_vm.SelectedDocument.Path)}";
    }

    private void AddFare_Click(object sender, RoutedEventArgs e)
    {
        _vm.Transport.Fares.Add(new TransportFareRecord { Index = _vm.Transport.Fares.Count, Number = string.Empty, Name = string.Empty, Category = "Informado na carteira", Fare = 0 });
        FaresGrid.Items.Refresh();
    }

    private void RemoveFare_Click(object sender, RoutedEventArgs e)
    {
        if (FaresGrid.SelectedItem is not TransportFareRecord fare) { Notify("Selecione uma tarifa."); return; }
        _vm.Transport.Fares.Remove(fare);
        for (var index = 0; index < _vm.Transport.Fares.Count; index++) _vm.Transport.Fares[index].Index = index;
        FaresGrid.Items.Refresh();
    }

    private async void SaveFares_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WorkingDaysBox.Text, out var days) || days is < 0 or > 31) { Notify("Informe uma quantidade válida de dias úteis."); return; }
        try
        {
            FaresGrid.CommitEdit();
            FaresGrid.CommitEdit();
            await _vm.SaveFaresAsync(_vm.Transport.Fares.ToList(), days);
            Notify("Auxílio-Transporte recalculado e salvo com sucesso.");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void NewInterval_Click(object sender, RoutedEventArgs e)
    {
        var window = new ServiceIntervalWindow(new ServiceIntervalRecord { MilitaryId = _vm.Military.Id, Order = _vm.ServiceIntervals.Count }) { Owner = this };
        if (window.ShowDialog() == true)
        {
            try { await _vm.SaveIntervalAsync(window.Result); }
            catch (Exception ex) { ShowError(ex); }
        }
    }

    private async void EditInterval_Click(object sender, RoutedEventArgs e)
    {
        var interval = _vm.SelectedInterval;
        if (interval is null) { Notify("Selecione um intervalo."); return; }
        var window = new ServiceIntervalWindow(interval) { Owner = this };
        if (window.ShowDialog() == true)
        {
            try { await _vm.SaveIntervalAsync(window.Result); }
            catch (Exception ex) { ShowError(ex); }
        }
    }

    private async void DeleteInterval_Click(object sender, RoutedEventArgs e)
    {
        var interval = _vm.SelectedInterval;
        if (interval is null) { Notify("Selecione um intervalo."); return; }
        if (SigfurDialog.Show(this, "Excluir o intervalo selecionado?", "SIGFUR", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await _vm.DeleteIntervalAsync(interval); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void IntervalsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is ServiceIntervalRecord row)
            _vm.SelectedInterval = row;
        e.Handled = true;
        EditInterval_Click(sender, new RoutedEventArgs());
    }

    private async void RefreshPaystubs_Click(object sender, RoutedEventArgs e)
    {
        try { await _vm.LoadPaystubsAsync(); _paystubsLoaded = true; }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void DownloadPaystub_Click(object sender, RoutedEventArgs e)
    {
        var person = new CpexPaystubPerson(
            _vm.Military.Name,
            _vm.Military.Cpf,
            _vm.Military.ShortRank,
            _vm.Military.Id,
            _vm.Military.MilitaryId,
            _vm.Military.PrecCp);

        try
        {
            _vm.IsBusy = true;
            var settings = await App.CpexPaystubs.LoadSettingsAsync();
            var password = App.CpexPaystubs.ReadSavedPassword(settings);
            settings.Headless = true;
            settings.OpenAfterDownload = false;
            if (string.IsNullOrWhiteSpace(settings.Login) || string.IsNullOrWhiteSpace(password))
            {
                _vm.IsBusy = false;
                var downloader = new CpexPaystubDownloadWindow(App.CpexPaystubs, [person]) { Owner = this };
                downloader.ShowDialog();
                if (!downloader.DownloadedAny) return;
            }
            else
            {
                var progress = new Progress<CpexPaystubProgress>(p => _vm.StatusText = p.Message);
                var result = await App.CpexPaystubs.DownloadAsync([person], settings, password, progress);
                if (result.Downloaded == 0 && result.Failures.Count > 0)
                    SigfurDialog.Show(this, string.Join(Environment.NewLine, result.Failures), "Baixar contracheque", MessageBoxButton.OK, MessageBoxImage.Warning);
                else
                    _vm.StatusText = $"Contracheque baixado automaticamente: {result.Downloaded} arquivo(s).";
            }

            App.Paystubs.InvalidateCache();
            await _vm.LoadPaystubsAsync();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _vm.IsBusy = false; }
    }

    private void OpenPaystub_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedPaystub is null) { Notify("Selecione um contracheque."); return; }
        ShellService.OpenPath(_vm.SelectedPaystub.Path);
    }

    private void OpenPaystubFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedPaystub is not null && !string.IsNullOrWhiteSpace(_vm.SelectedPaystub.Path))
        {
            ShellService.RevealInExplorer(_vm.SelectedPaystub.Path);
            return;
        }

        var militaryFolder = PersonDocumentStorageService.PrepareRegisteredFolder(
            App.Paths, PersonDocumentStorageService.ResolveConfiguredRoot(App.Paths),
            _vm.Military.Rank, _vm.Military.Name, _vm.Military.Cpf, _vm.Military.PrecCp);
        Directory.CreateDirectory(militaryFolder);
        ShellService.OpenPath(militaryFolder);
        _vm.StatusText = "Pasta de contracheques do militar aberta.";
    }

    private async void DeletePaystub_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedPaystub is null || string.IsNullOrWhiteSpace(_vm.SelectedPaystub.Path) || !File.Exists(_vm.SelectedPaystub.Path))
        {
            Notify("Selecione um contracheque salvo para excluir.");
            return;
        }
        if (SigfurDialog.Show(this, $"Excluir definitivamente este contracheque?\n\n{_vm.SelectedPaystub.FileName}", "Excluir contracheque", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            File.Delete(_vm.SelectedPaystub.Path);
            App.Paystubs.InvalidateCache();
            await _vm.LoadPaystubsAsync();
            _vm.StatusText = "Contracheque excluído da carteira.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void PaystubsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is PaystubFileRecord row)
            _vm.SelectedPaystub = row;
        if (_vm.SelectedPaystub is null || string.IsNullOrWhiteSpace(_vm.SelectedPaystub.Path)) return;
        if (!File.Exists(_vm.SelectedPaystub.Path))
        {
            SigfurDialog.Show(this, "O arquivo selecionado não foi encontrado no disco.", "Contracheques", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        e.Handled = true;
        ShellService.OpenPath(_vm.SelectedPaystub.Path);
        _vm.StatusText = $"Aberto: {_vm.SelectedPaystub.FileName}";
    }

    private async void CaptureFinancialStatement_Click(object sender, RoutedEventArgs e)
    {
        var prompt = new TextPromptWindow("Ficha Financeira", "Informe o ano da ficha que será baixada no portal do CPEx.", DateTime.Now.Year.ToString()) { Owner = this };
        if (prompt.ShowDialog() != true) return;
        if (!int.TryParse(prompt.Value, out var year) || year is < 2000 or > 2200)
        {
            Notify("Informe um ano válido.");
            return;
        }

        try
        {
            Clipboard.SetText(MilitaryFormatting.Digits(_vm.Military.Cpf));
            _vm.IsBusy = true;
            var progress = new Progress<string>(message => _vm.StatusText = message);
            var saved = await App.FinancialStatements.CaptureAsync(_vm.Military, year, progress);
            App.Paystubs.InvalidateCache();
            await _vm.LoadFinancialStatementsAsync();
            _vm.StatusText = "Ficha financeira salva e vinculada ao militar.";
            ShellService.OpenPath(saved);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _vm.IsBusy = false; }
    }

    private async void RefreshFinancialStatements_Click(object sender, RoutedEventArgs e)
    {
        try { await _vm.LoadFinancialStatementsAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OpenFinancialStatement_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedFinancialStatement is null) { Notify("Selecione uma ficha financeira."); return; }
        ShellService.OpenPath(_vm.SelectedFinancialStatement.Path);
    }

    private void OpenFinancialStatementFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedFinancialStatement is not null && !string.IsNullOrWhiteSpace(_vm.SelectedFinancialStatement.Path))
        {
            ShellService.RevealInExplorer(_vm.SelectedFinancialStatement.Path);
            return;
        }
        var militaryFolder = PersonDocumentStorageService.PrepareRegisteredFolder(
            App.Paths, PersonDocumentStorageService.ResolveConfiguredRoot(App.Paths),
            _vm.Military.Rank, _vm.Military.Name, _vm.Military.Cpf, _vm.Military.PrecCp);
        Directory.CreateDirectory(militaryFolder);
        ShellService.OpenPath(militaryFolder);
        _vm.StatusText = "Pasta de fichas/contracheques do militar aberta.";
    }

    private async void DeleteFinancialStatement_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedFinancialStatement is null || string.IsNullOrWhiteSpace(_vm.SelectedFinancialStatement.Path) || !File.Exists(_vm.SelectedFinancialStatement.Path))
        {
            Notify("Selecione uma ficha financeira salva para excluir.");
            return;
        }
        if (SigfurDialog.Show(this, $"Excluir definitivamente esta ficha financeira?\n\n{_vm.SelectedFinancialStatement.FileName}", "Excluir ficha financeira", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            File.Delete(_vm.SelectedFinancialStatement.Path);
            App.Paystubs.InvalidateCache();
            await _vm.LoadFinancialStatementsAsync();
            _vm.StatusText = "Ficha financeira excluída da carteira.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void FinancialStatementsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is PaystubFileRecord row)
            _vm.SelectedFinancialStatement = row;
        if (_vm.SelectedFinancialStatement is null || !File.Exists(_vm.SelectedFinancialStatement.Path)) return;
        e.Handled = true;
        ShellService.OpenPath(_vm.SelectedFinancialStatement.Path);
        _vm.StatusText = $"Aberto: {_vm.SelectedFinancialStatement.FileName}";
    }
    private void MentionsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is WalletMentionRecord row)
            _vm.SelectedMention = row;
        if (_vm.SelectedMention is null) return;
        e.Handled = true;
        OpenSelectedMentionAndSearch();
    }

    private async void RefreshMentions_Click(object sender, RoutedEventArgs e)
    {
        try { await _vm.LoadFurrielMentionsAsync(); _mentionsLoaded = true; }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void WalletTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _suppressTabLoad || e.Source != WalletTabs) return;
        await LoadSelectedTabAsync();
    }

    private async Task LoadSelectedTabAsync()
    {
        try
        {
            switch (WalletTabs.SelectedIndex)
            {
                case 5 when !_mentionsLoaded:
                    _mentionsLoaded = true;
                    await _vm.LoadFurrielMentionsAsync();
                    break;
                case 6 when !_paystubsLoaded:
                    _paystubsLoaded = true;
                    await _vm.LoadPaystubsAsync();
                    break;
                case 7 when !_financialStatementsLoaded:
                    _financialStatementsLoaded = true;
                    await _vm.LoadFinancialStatementsAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            if (WalletTabs.SelectedIndex == 5) _mentionsLoaded = false;
            if (WalletTabs.SelectedIndex == 6) _paystubsLoaded = false;
            if (WalletTabs.SelectedIndex == 7) _financialStatementsLoaded = false;
            ShowError(ex);
        }
    }

    private void OpenMention_Click(object sender, RoutedEventArgs e)
    {
        if (FindAncestor<DataGridRow>((sender as DependencyObject))?.Item is WalletMentionRecord row)
            _vm.SelectedMention = row;
        OpenSelectedMentionAndSearch();
    }

    private void OpenSelectedMentionAndSearch()
    {
        var mention = _vm.SelectedMention;
        if (mention is null || string.IsNullOrWhiteSpace(mention.PdfPath))
        {
            Notify("Selecione uma menção que possua boletim vinculado.");
            return;
        }
        if (!File.Exists(mention.PdfPath))
        {
            Notify("O PDF vinculado à menção não foi encontrado no disco. Atualize/reindexe os boletins.");
            return;
        }
        try
        {
            var service = new FurrielBulletinService(App.Paths, App.Settings, App.MilitaryRepository, App.Log);
            service.OpenPdf(mention.PdfPath, _vm.Military.Name, mention.Page);
            _vm.StatusText = $"Boletim aberto; pesquisando por {_vm.Military.Name}.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void CopyMention_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedMention is null) { Notify("Selecione uma menção."); return; }
        var mention = _vm.SelectedMention;
        var subjectLine = string.IsNullOrWhiteSpace(mention.Subject) ? mention.SubjectNoteDisplay : mention.Subject;
        var noteLine = string.IsNullOrWhiteSpace(mention.SubjectNote) || mention.SubjectNote == "—" ? string.Empty : $"\nNota/Tipo: {mention.SubjectNote}";
        Clipboard.SetText($"{mention.OriginDisplay} {mention.Bulletin} • {mention.Date} • pág. {mention.Page}\nAssunto: {subjectLine}{noteLine}\nConsequência: {mention.ConsequenceDisplay}".Trim());
        _vm.StatusText = "Registro da menção copiado.";
    }

    private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // As tabelas possuem ações próprias de duplo clique (abrir PDF, boletim ou
        // editar intervalo). A cópia global só vale fora delas.
        if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null) return;
        if (FindAncestor<DataGrid>(e.OriginalSource as DependencyObject) is not null) return;
        CopyFromDoubleClick(e);
    }

    private void CopyFromDoubleClick(MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var text = source switch
        {
            TextBlock tb => tb.Text,
            TextBox box => box.SelectedText.Length > 0 ? box.SelectedText : box.Text,
            Label label => label.Content?.ToString() ?? string.Empty,
            _ => FindAncestor<TextBlock>(source)?.Text ?? string.Empty
        };
        if (string.IsNullOrWhiteSpace(text))
        {
            var cell = FindAncestor<DataGridCell>(source);
            text = cell?.Content?.ToString() ?? string.Empty;
        }
        text = text.Trim();
        if (text.Length == 0 || text.Length > 5000) return;
        try
        {
            Clipboard.SetText(text);
            _vm.StatusText = $"Copiado para colar: {text}";
            e.Handled = true;
        }
        catch { }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }


    private void LoadPhoto(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { HeaderPhoto.Source = null; return; }
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path, UriKind.Absolute); image.EndInit(); image.Freeze();
            HeaderPhoto.Source = image;
        }
        catch { HeaderPhoto.Source = null; }
    }

    private void Notify(string text) => SigfurDialog.Show(this, text, "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
    private void ShowError(Exception ex) => SigfurDialog.Show(this, ex.Message, "SIGFUR — Erro", MessageBoxButton.OK, MessageBoxImage.Error);
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) ToggleMaximize(); else if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
