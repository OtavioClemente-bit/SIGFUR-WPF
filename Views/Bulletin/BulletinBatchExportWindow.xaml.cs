using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Bulletin;

public partial class BulletinBatchExportWindow : Window
{
    private readonly List<BulletinBatchExportItem> _items;
    private readonly ObservableCollection<BulletinBatchExportRow> _preview = [];
    private readonly string _moduleFolderName;
    private readonly string _prefix;
    private bool _loading = true;

    public BulletinBatchExportWindow(
        IEnumerable<BulletinBatchExportItem> items,
        string moduleFolderName,
        string prefix,
        IReadOnlyCollection<string>? initiallySelectedIds = null)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _moduleFolderName = moduleFolderName;
        _prefix = prefix;
        _items = items
            .Where(x => x.Number > 0 && x.Year > 0 && File.Exists(x.SourcePath))
            .OrderByDescending(x => x.Year)
            .ThenBy(x => x.Number)
            .ThenBy(x => x.OriginalName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        PreviewGrid.ItemsSource = _preview;
        HeaderSubtitleText.Text = $"{moduleFolderName}: escolha o ano e o intervalo. A pasta final ficará numerada e pronta para envio.";
        PreferSignedCheck.Visibility = _items.Any(x => x.HasSignedFile) ? Visibility.Visible : Visibility.Collapsed;
        PreferSignedCheck.IsChecked = PreferSignedCheck.Visibility == Visibility.Visible;
        DestinationBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        var years = _items.Select(x => x.Year).Distinct().OrderByDescending(x => x).ToList();
        YearBox.ItemsSource = years;

        var initial = _items.Where(x => initiallySelectedIds?.Contains(x.Id) == true).ToList();
        var initialYear = initial.Select(x => x.Year).Distinct().Count() == 1 ? initial[0].Year : years.FirstOrDefault();
        YearBox.SelectedItem = initialYear;
        var initialRange = initial.Where(x => x.Year == initialYear).ToList();
        var yearItems = _items.Where(x => x.Year == initialYear).ToList();
        StartNumberBox.Text = (initialRange.Count > 0 ? initialRange.Min(x => x.Number) : yearItems.Min(x => x.Number)).ToString(CultureInfo.InvariantCulture);
        EndNumberBox.Text = (initialRange.Count > 0 ? initialRange.Max(x => x.Number) : yearItems.Max(x => x.Number)).ToString(CultureInfo.InvariantCulture);
        _loading = false;
        RefreshPreview();
    }

    private void Filter_Changed(object sender, EventArgs e)
    {
        if (_loading || !IsInitialized) return;
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        _preview.Clear();
        if (YearBox.SelectedItem is not int year || !TryReadRange(out var start, out var end))
        {
            SummaryText.Text = "Informe um ano e um intervalo numérico válido.";
            MissingText.Text = string.Empty;
            ExportButton.IsEnabled = false;
            ExportAndEmailButton.IsEnabled = false;
            return;
        }

        var rows = _items.Where(x => x.Year == year && x.Number >= start && x.Number <= end)
            .OrderBy(x => x.Number)
            .ThenBy(x => x.DateText, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.OriginalName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = new BulletinBatchExportRow { Item = rows[index], Sequence = index + 1 };
            row.InclusionChanged += UpdateSelectionSummary;
            _preview.Add(row);
        }

        var availableNumbers = rows.Select(x => x.Number).ToHashSet();
        var missing = Enumerable.Range(start, end - start + 1).Where(number => !availableNumbers.Contains(number)).ToList();
        SummaryText.Text = $"{rows.Count:N0} arquivo(s) localizado(s) · intervalo {_prefix} {start} a {end} · ano {year}";
        MissingText.Text = missing.Count == 0
            ? "Intervalo completo: todos os números foram localizados."
            : $"Não localizados ({missing.Count:N0}): {FormatNumberList(missing)}";
        UpdateSelectionSummary();
    }

    private bool TryReadRange(out int start, out int end)
    {
        start = 0;
        end = 0;
        if (!int.TryParse(StartNumberBox.Text.Trim(), out start)
            || !int.TryParse(EndNumberBox.Text.Trim(), out end)
            || start <= 0
            || end <= 0)
            return false;
        if (end < start) (start, end) = (end, start);
        return end - start <= 5000;
    }

    private void UpdateSelectionSummary()
    {
        var selected = _preview.Count(x => x.IsIncluded);
        StatusText.Text = selected == 0
            ? "Nenhum arquivo marcado para exportação."
            : $"{selected:N0} arquivo(s) marcado(s). A exportação preservará os originais.";
        ExportButton.IsEnabled = selected > 0;
        ExportAndEmailButton.IsEnabled = selected > 0;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _preview) row.IsIncluded = true;
        PreviewGrid.Items.Refresh();
        UpdateSelectionSummary();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _preview) row.IsIncluded = false;
        PreviewGrid.Items.Refresh();
        UpdateSelectionSummary();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Escolha onde criar a pasta organizada dos boletins",
            InitialDirectory = Directory.Exists(DestinationBox.Text) ? DestinationBox.Text : null,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true) DestinationBox.Text = dialog.FolderName;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
        => await ExportSelectedAsync(sendByEmail: false);

    private async void ExportAndEmail_Click(object sender, RoutedEventArgs e)
        => await ExportSelectedAsync(sendByEmail: true);

    private async Task ExportSelectedAsync(bool sendByEmail)
    {
        if (YearBox.SelectedItem is not int year || !TryReadRange(out var start, out var end)) return;
        var selected = _preview.Where(x => x.IsIncluded).OrderBy(x => x.Item.Number).ThenBy(x => x.Sequence).ToList();
        if (selected.Count == 0) return;

        var baseDirectory = DestinationBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            SigfurDialog.Show(this, "Escolha a pasta de destino.", "SIGFUR — Exportar boletins", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ExportButton.IsEnabled = false;
        ExportAndEmailButton.IsEnabled = false;
        StatusText.Text = sendByEmail ? "Organizando os arquivos para o EBMail..." : "Criando a pasta organizada...";
        try
        {
            var preferSigned = PreferSignedCheck.IsChecked == true;
            var result = await Task.Run(() => ExportFiles(baseDirectory, year, start, end, selected, preferSigned));
            StatusText.Text = $"Exportação concluída: {result.Exported:N0} arquivo(s).";
            if (result.Errors.Count > 0)
            {
                SigfurDialog.Show(this,
                    $"A pasta foi criada, mas {result.Errors.Count:N0} arquivo(s) não puderam ser copiados.\n\n{string.Join(Environment.NewLine, result.Errors.Take(10))}",
                    "SIGFUR — Exportação concluída com observações", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            if (sendByEmail)
            {
                if (result.Documents.Count == 0)
                    throw new InvalidOperationException("Nenhum arquivo exportado ficou disponível para envio pelo EBMail.");
                StatusText.Text = $"{result.Exported:N0} arquivo(s) organizados. Preparando o EBMail...";
                new EbMailComposeWindow(result.Documents) { Owner = this }.ShowDialog();
            }
            else
            {
                ShellService.OpenPath(result.OutputDirectory);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Não foi possível concluir a exportação.";
            SigfurDialog.Show(this, ex.Message, "SIGFUR — Exportar boletins", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            var hasSelection = _preview.Any(x => x.IsIncluded);
            ExportButton.IsEnabled = hasSelection;
            ExportAndEmailButton.IsEnabled = hasSelection;
        }
    }

    private BulletinBatchExportResult ExportFiles(
        string baseDirectory,
        int year,
        int start,
        int end,
        IReadOnlyList<BulletinBatchExportRow> selected,
        bool preferSigned)
    {
        Directory.CreateDirectory(baseDirectory);
        var yearDirectory = Path.Combine(baseDirectory, SafeSegment(_moduleFolderName), year.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(yearDirectory);
        var outputDirectory = CreateUniqueDirectory(Path.Combine(yearDirectory, $"{_prefix} {start:0000} a {end:0000}"));
        var result = new BulletinBatchExportResult { OutputDirectory = outputDirectory };

        for (var index = 0; index < selected.Count; index++)
        {
            var item = selected[index].Item;
            var source = item.ResolveSourcePath(preferSigned);
            try
            {
                if (!File.Exists(source)) throw new FileNotFoundException("Arquivo de origem não localizado.", source);
                var isSigned = preferSigned && item.HasSignedFile;
                var signedSuffix = isSigned ? " (ASSINADO)" : string.Empty;
                var extra = string.IsNullOrWhiteSpace(item.ExtraReference) ? string.Empty : $" - {item.ExtraReference}";
                var extension = Path.GetExtension(source);
                if (string.IsNullOrWhiteSpace(extension)) extension = ".pdf";
                var fileName = SafeSegment($"{index + 1:000} - {item.Prefix} {item.Number:0000}-{item.Year}{extra}{signedSuffix}") + extension.ToLowerInvariant();
                var destinationPath = Path.Combine(outputDirectory, fileName);
                File.Copy(source, destinationPath, overwrite: false);
                result.Documents.Add(new EbMailDocument
                {
                    Kind = item.Prefix,
                    Reference = item.Prefix.Equals("ADT", StringComparison.OrdinalIgnoreCase)
                        ? $"ADT Furr Nr {item.Number}/{item.Year}{(string.IsNullOrWhiteSpace(item.ExtraReference) ? string.Empty : $" {item.ExtraReference}")}"
                        : $"BI Nr {item.Number}/{item.Year}",
                    Date = item.DateText,
                    FilePath = destinationPath,
                    OriginalFilePath = isSigned ? string.Empty : destinationPath,
                    SignedFilePath = isSigned ? destinationPath : string.Empty,
                    IsSigned = isSigned,
                    AttachmentMode = isSigned ? EbMailAttachmentMode.Signed : EbMailAttachmentMode.Original
                });
                result.Exported++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{item.DisplayReference}: {ex.Message}");
            }
        }

        return result;
    }

    private static string CreateUniqueDirectory(string desiredPath)
    {
        var candidate = desiredPath;
        for (var suffix = 2; Directory.Exists(candidate); suffix++) candidate = $"{desiredPath} ({suffix})";
        Directory.CreateDirectory(candidate);
        return candidate;
    }

    private static string SafeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var clean = new string((value ?? string.Empty).Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return Regex.Replace(clean, @"\s+", " ").Trim(' ', '.');
    }

    private static string FormatNumberList(IReadOnlyList<int> numbers)
    {
        var text = string.Join(", ", numbers.Take(40));
        return numbers.Count > 40 ? text + $" e mais {numbers.Count - 40}" : text;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class BulletinBatchExportResult
    {
        public string OutputDirectory { get; init; } = string.Empty;
        public int Exported { get; set; }
        public List<string> Errors { get; } = [];
        public List<EbMailDocument> Documents { get; } = [];
    }
}
