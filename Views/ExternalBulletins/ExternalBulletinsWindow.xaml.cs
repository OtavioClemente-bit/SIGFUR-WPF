using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.ExternalBulletins;

public partial class ExternalBulletinsWindow : Window
{
    private readonly ExternalBulletinService _service;
    private readonly ObservableCollection<ExternalBulletinFile> _regionFiles = [];
    private readonly ObservableCollection<ExternalBulletinFile> _cmlFiles = [];
    private readonly ObservableCollection<ExternalBulletinMention> _regionMentions = [];
    private readonly ObservableCollection<ExternalBulletinMention> _cmlMentions = [];
    private ExternalBulletinStore _store = new();
    private ExternalBulletinSettings _settings = new();
    private bool _loading;

    public ExternalBulletinsWindow(ExternalBulletinService service)
    {
        InitializeComponent();
        _service = service;
        RegionFilesGrid.ItemsSource = _regionFiles;
        CmlFilesGrid.ItemsSource = _cmlFiles;
        RegionMentionsGrid.ItemsSource = _regionMentions;
        CmlMentionsGrid.ItemsSource = _cmlMentions;
    }

    private ExternalBulletinFile? SelectedRegionFile => RegionFilesGrid.SelectedItem as ExternalBulletinFile;
    private ExternalBulletinFile? SelectedCmlFile => CmlFilesGrid.SelectedItem as ExternalBulletinFile;
    private ExternalBulletinMention? SelectedRegionMention => RegionMentionsGrid.SelectedItem as ExternalBulletinMention;
    private ExternalBulletinMention? SelectedCmlMention => CmlMentionsGrid.SelectedItem as ExternalBulletinMention;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            _settings = await _service.LoadSettingsAsync();
            SearchTermBox.Text = string.IsNullOrWhiteSpace(_settings.SearchTerm) ? "4ª Cia PE" : _settings.SearchTerm;
            RegionFilterBox.Text = _settings.RegionFilter ?? string.Empty;
            CmlFilterBox.Text = _settings.CmlFilter ?? string.Empty;
            await ReloadAsync();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _loading = false; }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        try { await SaveSettingsAsync(); } catch { }
    }

    private async Task SaveSettingsAsync()
    {
        _settings.SearchTerm = SearchTermBox.Text.Trim();
        _settings.RegionFilter = RegionFilterBox.Text.Trim();
        _settings.CmlFilter = CmlFilterBox.Text.Trim();
        await _service.SaveSettingsAsync(_settings);
    }

    private async Task ReloadAsync(string? selectRegionId = null, string? selectCmlId = null)
    {
        _store = await _service.LoadAsync();
        ApplyFilters(selectRegionId, selectCmlId);
        UpdateSummary();
        HeaderSearchBadge.Text = $"Pesquisa: {CurrentSearchTerm}";
    }

    private string CurrentSearchTerm => string.IsNullOrWhiteSpace(SearchTermBox.Text) ? "4ª Cia PE" : SearchTermBox.Text.Trim();

    private void ApplyFilters(string? selectRegionId = null, string? selectCmlId = null)
    {
        var oldRegionId = selectRegionId ?? SelectedRegionFile?.Id;
        var oldCmlId = selectCmlId ?? SelectedCmlFile?.Id;
        var regionFilter = Normalize(RegionFilterBox.Text);
        var cmlFilter = Normalize(CmlFilterBox.Text);

        var regions = _store.Items.Where(x => x.Kind == ExternalBulletinKinds.Region)
            .Where(x => ShowAllRegionCheck.IsChecked == true || x.MentionCount > 0)
            .Where(x => MatchesFile(x, regionFilter))
            .OrderByDescending(x => x.DateIso).ThenByDescending(x => x.DisplayNumber).ToList();
        Reset(_regionFiles, regions);

        var cml = _store.Items.Where(x => x.Kind == ExternalBulletinKinds.Cml)
            .Where(x => ShowAllCmlCheck.IsChecked == true || x.MentionCount > 0)
            .Where(x => MatchesFile(x, cmlFilter))
            .OrderByDescending(x => x.DateIso).ThenByDescending(x => x.DisplayNumber).ToList();
        Reset(_cmlFiles, cml);

        SelectFile(RegionFilesGrid, _regionFiles, oldRegionId);
        SelectFile(CmlFilesGrid, _cmlFiles, oldCmlId);
        RefreshRegionMentions();
        RefreshCmlMentions();
        RegionFilesCountText.Text = $"{_regionFiles.Count} boletim(ns) exibido(s)";
        CmlFilesCountText.Text = $"{_cmlFiles.Count} aditamento(s) exibido(s)";
    }

    private static bool MatchesFile(ExternalBulletinFile file, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var text = Normalize(string.Join(" ", file.DisplayNumber, file.DisplayDate, file.OriginalFileName,
            file.Mentions.Select(x => string.Join(" ", x.Section, x.Type, x.Summary, x.Context, x.Amount, x.Event))));
        return text.Contains(filter, StringComparison.Ordinal);
    }

    private static void SelectFile(DataGrid grid, ObservableCollection<ExternalBulletinFile> items, string? id)
    {
        var selected = !string.IsNullOrWhiteSpace(id) ? items.FirstOrDefault(x => x.Id == id) : null;
        selected ??= items.FirstOrDefault();
        grid.SelectedItem = selected;
        if (selected is not null) grid.ScrollIntoView(selected);
    }

    private void RefreshRegionMentions()
    {
        var selectedId = SelectedRegionMention?.Id;
        var rows = SelectedRegionFile?.Mentions ?? [];
        Reset(_regionMentions, rows.OrderBy(x => x.Page).ThenBy(x => x.DocumentOccurrence));
        RegionMentionsCountText.Text = SelectedRegionFile is null
            ? "Selecione um boletim."
            : $"{_regionMentions.Count} menção(ões) · {SelectedRegionFile.IgnoredText}";
        RegionMentionsGrid.SelectedItem = _regionMentions.FirstOrDefault(x => x.Id == selectedId) ?? _regionMentions.FirstOrDefault();
        RegionDetailText.Text = SelectedRegionMention?.DetailText ?? "Selecione uma menção para ver o trecho completo.";
    }

    private void RefreshCmlMentions()
    {
        var selectedId = SelectedCmlMention?.Id;
        var rows = SelectedCmlFile?.Mentions ?? [];
        Reset(_cmlMentions, rows.OrderBy(x => x.Page).ThenBy(x => x.DocumentOccurrence));
        CmlMentionsCountText.Text = SelectedCmlFile is null ? "Selecione um aditamento." : $"{_cmlMentions.Count} liberação(ões)/menção(ões)";
        CmlMentionsGrid.SelectedItem = _cmlMentions.FirstOrDefault(x => x.Id == selectedId) ?? _cmlMentions.FirstOrDefault();
        CmlDetailText.Text = SelectedCmlMention?.DetailText ?? "Selecione uma liberação para ver evento, valor, efetivo e contexto.";
    }

    private void UpdateSummary()
    {
        var regionFiles = _store.Items.Where(x => x.Kind == ExternalBulletinKinds.Region && x.MentionCount > 0).ToList();
        var cmlFiles = _store.Items.Where(x => x.Kind == ExternalBulletinKinds.Cml && x.MentionCount > 0).ToList();
        var mentions = regionFiles.Sum(x => x.MentionCount) + cmlFiles.Sum(x => x.MentionCount);
        RegionSummaryText.Text = $"{regionFiles.Count} boletim(ns)";
        CmlSummaryText.Text = $"{cmlFiles.Count} aditamento(s)";
        MentionsSummaryText.Text = $"{mentions} encontrada(s)";
    }

    private async void ReindexAll_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SearchTermBox.Text))
        {
            SigfurDialog.Show(this, "Informe o nome ou a OM que deseja pesquisar.", "Boletins de Fora", MessageBoxButton.OK, MessageBoxImage.Warning);
            SearchTermBox.Focus();
            return;
        }
        try
        {
            await RunBusyAsync("Relendo toda a biblioteca externa...", async progress =>
            {
                await SaveSettingsAsync();
                await _service.ReindexAllAsync(CurrentSearchTerm, progress);
                await ReloadAsync();
            });
            StatusText.Text = $"Pesquisa atualizada para “{CurrentSearchTerm}”.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ImportRegionFolder_Click(object sender, RoutedEventArgs e) => await ImportFolderAsync(ExternalBulletinKinds.Region);
    private async void ImportCmlFolder_Click(object sender, RoutedEventArgs e) => await ImportFolderAsync(ExternalBulletinKinds.Cml);
    private async void ImportRegionFiles_Click(object sender, RoutedEventArgs e) => await ImportFilesAsync(ExternalBulletinKinds.Region);
    private async void ImportCmlFiles_Click(object sender, RoutedEventArgs e) => await ImportFilesAsync(ExternalBulletinKinds.Cml);

    private async Task ImportFolderAsync(string kind)
    {
        var last = kind == ExternalBulletinKinds.Region ? _settings.LastRegionFolder : _settings.LastCmlFolder;
        var dialog = new OpenFolderDialog
        {
            Title = kind == ExternalBulletinKinds.Region ? "Selecionar pasta dos Boletins Regionais" : "Selecionar pasta dos Aditamentos do CML",
            Multiselect = false,
            InitialDirectory = Directory.Exists(last) ? last : null
        };
        if (dialog.ShowDialog(this) != true) return;
        if (kind == ExternalBulletinKinds.Region) _settings.LastRegionFolder = dialog.FolderName; else _settings.LastCmlFolder = dialog.FolderName;
        await ImportAsync(kind, [dialog.FolderName]);
    }

    private async Task ImportFilesAsync(string kind)
    {
        var last = kind == ExternalBulletinKinds.Region ? _settings.LastRegionFolder : _settings.LastCmlFolder;
        var dialog = new OpenFileDialog
        {
            Title = kind == ExternalBulletinKinds.Region ? "Adicionar Boletins Regionais" : "Adicionar Aditamentos do CML",
            Filter = "Arquivos PDF (*.pdf)|*.pdf",
            Multiselect = true,
            InitialDirectory = Directory.Exists(last) ? last : null
        };
        if (dialog.ShowDialog(this) != true) return;
        var folder = Path.GetDirectoryName(dialog.FileNames.FirstOrDefault() ?? string.Empty) ?? string.Empty;
        if (kind == ExternalBulletinKinds.Region) _settings.LastRegionFolder = folder; else _settings.LastCmlFolder = folder;
        await ImportAsync(kind, dialog.FileNames);
    }

    private async Task ImportAsync(string kind, IEnumerable<string> sources)
    {
        try
        {
            ExternalBulletinImportResult? result = null;
            var reindexExisting = _store.Items.Any(x => Normalize(x.IndexedSearchTerm) != Normalize(CurrentSearchTerm));
            await RunBusyAsync("Importando e lendo os PDFs...", async progress =>
            {
                await SaveSettingsAsync();
                result = await _service.ImportAsync(kind, sources, CurrentSearchTerm, progress);
                if (reindexExisting)
                    await _service.ReindexAllAsync(CurrentSearchTerm, progress);
                await ReloadAsync();
            });
            if (result is null) return;
            var message = $"Novos: {result.Imported}\nAtualizados: {result.Updated}\nDuplicados ignorados: {result.Duplicates}\nSem menção: {result.WithoutMention}";
            if (result.Errors.Count > 0) message += "\n\nOcorrências:\n" + string.Join("\n", result.Errors.Take(12));
            SigfurDialog.Show(this, message, "Importação concluída", MessageBoxButton.OK, result.Errors.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task RunBusyAsync(string initialStatus, Func<IProgress<string>, Task> action)
    {
        IsEnabled = false;
        BusyBar.Visibility = Visibility.Visible;
        StatusText.Text = initialStatus;
        var progress = new Progress<string>(message => StatusText.Text = message);
        try { await action(progress); }
        finally { BusyBar.Visibility = Visibility.Collapsed; IsEnabled = true; }
    }

    private void RegionFilterBox_TextChanged(object sender, TextChangedEventArgs e) { if (!_loading) ApplyFilters(); }
    private void CmlFilterBox_TextChanged(object sender, TextChangedEventArgs e) { if (!_loading) ApplyFilters(); }
    private void ShowAllRegionCheck_Changed(object sender, RoutedEventArgs e) { if (!_loading) ApplyFilters(); }
    private void ShowAllCmlCheck_Changed(object sender, RoutedEventArgs e) { if (!_loading) ApplyFilters(); }

    private void RegionFilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshRegionMentions();
    private void CmlFilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshCmlMentions();
    private void RegionMentionsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => RegionDetailText.Text = SelectedRegionMention?.DetailText ?? "Selecione uma menção.";
    private void CmlMentionsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => CmlDetailText.Text = SelectedCmlMention?.DetailText ?? "Selecione uma liberação.";

    private void RegionFilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenRegionPdf_Click(sender, e);
    private void CmlFilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenCmlPdf_Click(sender, e);
    private void RegionMentionsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenRegionMention_Click(sender, e);
    private void CmlMentionsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenCmlMention_Click(sender, e);

    private void OpenRegionPdf_Click(object sender, RoutedEventArgs e) => OpenFile(SelectedRegionFile);
    private void OpenCmlPdf_Click(object sender, RoutedEventArgs e) => OpenFile(SelectedCmlFile);
    private void OpenRegionMention_Click(object sender, RoutedEventArgs e) => OpenMention(SelectedRegionMention);
    private void OpenCmlMention_Click(object sender, RoutedEventArgs e) => OpenMention(SelectedCmlMention);

    private void OpenFile(ExternalBulletinFile? file)
    {
        if (file is null) { StatusText.Text = "Selecione um boletim."; return; }
        try
        {
                        var first = file.Mentions.FirstOrDefault();
            if (first is not null) _service.OpenPdf(first); else _service.OpenPdf(file, CurrentSearchTerm);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OpenMention(ExternalBulletinMention? mention)
    {
        if (mention is null) { StatusText.Text = "Selecione uma menção."; return; }
        try { _service.OpenPdf(mention); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RevealRegionPdf_Click(object sender, RoutedEventArgs e) => RevealFile(SelectedRegionFile);
    private void RevealCmlPdf_Click(object sender, RoutedEventArgs e) => RevealFile(SelectedCmlFile);
    private static void RevealFile(ExternalBulletinFile? file) { if (file is not null) ShellService.RevealInExplorer(file.StoredPath); }
    private void OpenRegionFolder_Click(object sender, RoutedEventArgs e) => ShellService.OpenPath(App.Paths.ExternalBulletinRegionDirectory);
    private void OpenCmlFolder_Click(object sender, RoutedEventArgs e) => ShellService.OpenPath(App.Paths.ExternalBulletinCmlDirectory);

    private async void RemoveRegion_Click(object sender, RoutedEventArgs e) => await RemoveAsync(SelectedRegionFile);
    private async void RemoveCml_Click(object sender, RoutedEventArgs e) => await RemoveAsync(SelectedCmlFile);

    private async Task RemoveAsync(ExternalBulletinFile? file)
    {
        if (file is null) { StatusText.Text = "Selecione um boletim."; return; }
        if (SigfurDialog.Show(this, $"Remover da biblioteca e apagar a cópia salva?\n\n{file.OriginalFileName}", "Remover boletim externo", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await _service.RemoveAsync(file.Id, true); await ReloadAsync(); StatusText.Text = "Boletim removido."; }
        catch (Exception ex) { ShowError(ex); }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (SourceTabs.SelectedIndex == 0) OpenMention(SelectedRegionMention); else OpenMention(SelectedCmlMention);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            ReindexAll_Click(sender, e);
            e.Handled = true;
        }
    }

    private void ShowError(Exception ex)
    {
        StatusText.Text = ex.Message;
        SigfurDialog.Show(this, ex.Message, "Boletins de Fora", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static void Reset<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }

    private static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Replace('ª', 'a').Replace('º', 'o').Replace('°', 'o').Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(ch));
        return System.Text.RegularExpressions.Regex.Replace(builder.ToString(), @"[^a-z0-9]+", " ").Trim();
    }
}
