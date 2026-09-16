using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Bulletin;

public partial class BulletinKeysWindow : Window
{
    private bool _builderReady;
    public ObservableCollection<KeyValueRow> Rows { get; } = [];
    public Dictionary<string, string> Values => Rows
        .Where(x => !string.IsNullOrWhiteSpace(x.Key))
        .GroupBy(x => x.Key.Trim(), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(x => x.Key, x => x.Last().Value?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    public BulletinKeysWindow(IReadOnlyDictionary<string, string> values)
    {
        InitializeComponent();
        foreach (var pair in values.Where(x => !BulletinService.IsAutomaticKey(x.Key)).OrderBy(x => x.Key, StringComparer.CurrentCultureIgnoreCase))
            Rows.Add(new KeyValueRow { Key = pair.Key, Value = pair.Value });
        KeysGrid.ItemsSource = Rows;
        SmartMonthBox.ItemsSource = Enumerable.Range(-24, 73).Select(i => DateTime.Today.AddMonths(i).ToString("MM/yyyy")).ToList();
        SmartMonthBox.Text = DateTime.Today.ToString("MM/yyyy");
        SmartDatePicker.SelectedDate = DateTime.Today;
        App.UiState.Attach(this);
        Loaded += (_, _) =>
        {
            _builderReady = true;
            // Combobox editável: SelectionChanged não dispara enquanto o usuário
            // digita manualmente 04/2026, por isso ouvimos também TextChanged.
            SmartMonthBox.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(SmartBuilder_Changed));
            ConfigureBuilder();
        };
    }

    private string SmartType => (SmartTypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "text";
    private string SmartFormat => (SmartFormatBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;

    private void ConfigureBuilder()
    {
        if (!_builderReady) return;
        var type = SmartType;
        SmartFormatBox.Items.Clear();
        SmartDatePicker.Visibility = type == "date" ? Visibility.Visible : Visibility.Collapsed;
        SmartMonthBox.Visibility = type == "month" ? Visibility.Visible : Visibility.Collapsed;
        SmartValueBox.Visibility = type is "date" or "month" ? Visibility.Collapsed : Visibility.Visible;

        if (type == "date")
        {
            AddFormat("22/04/2026", "BR"); AddFormat("22 ABR 26", "ABREV"); AddFormat("22 de abril de 2026", "EXTENSO"); AddFormat("2026-04-22", "ISO");
        }
        else if (type == "month")
        {
            AddFormat("Abril", "MES");
            AddFormat("ABR", "ABREV");
            AddFormat("2026", "ANO");
            AddFormat("Abril 2026", "MES_ANO");
            AddFormat("ABR 2026", "ABREV_ANO");
            AddFormat("04/2026", "NUMERO");
            AddFormat("30 dias", "DIAS");
            AddFormat("30 dias do mês de abril", "DIAS_MES");
            AddFormat("30 dias do mês de ABR", "DIAS_ABREV");
            AddFormat("30 dias do mês de abril de 2026", "DIAS_MES_ANO");
            AddFormat("30 dias do mês de ABR 2026", "DIAS_ABREV_ANO");
        }
        else if (type == "money")
        {
            AddFormat("R$ 1.234,56", "NUMERO");
            AddFormat("mil duzentos... reais", "EXTENSO");
            AddFormat("Extenso em MAIÚSCULO", "EXTENSO_MAIUSCULO");
            AddFormat("R$ 1.234,56 (mil duzentos...)", "AMBOS");
            AddFormat("R$ 1.234,56 (EXTENSO EM MAIÚSCULO)", "AMBOS_MAIUSCULO");
        }
        else AddFormat("Sem formatação", string.Empty);
        SmartFormatBox.SelectedIndex = 0;
        UpdateBuilderPreview();
    }

    private void AddFormat(string text, string tag) => SmartFormatBox.Items.Add(new ComboBoxItem { Content = text, Tag = tag });

    private void UpdateBuilderPreview()
    {
        if (!_builderReady) return;
        var key = NormalizeKey(SmartKeyBox.Text);
        var type = SmartType;
        var raw = type switch
        {
            "date" => SmartDatePicker.SelectedDate?.ToString("dd/MM/yyyy") ?? string.Empty,
            "month" => SmartMonthBox.Text?.Trim() ?? string.Empty,
            _ => SmartValueBox.Text?.Trim() ?? string.Empty
        };
        var meta = type switch { "date" => "DATA", "month" => "MES", "money" => "VALOR", _ => string.Empty };
        SmartTokenBox.Text = string.IsNullOrWhiteSpace(meta) ? $"[[{key}]]" : $"[[{key}:{meta}={SmartFormat}]]";
        SmartPreviewText.Text = string.IsNullOrWhiteSpace(raw)
            ? "Informe um valor para visualizar."
            : BulletinService.FormatSmartValue(raw, type, SmartFormat);
    }

    private static string NormalizeKey(string? value)
    {
        var key = Regex.Replace((value ?? string.Empty).Trim().ToUpperInvariant(), @"[^A-Z0-9_À-Ü]", "_");
        key = Regex.Replace(key, "_+", "_").Trim('_');
        return string.IsNullOrWhiteSpace(key) ? "NOVA_CHAVE" : key;
    }

    private void Add_Click(object sender, RoutedEventArgs e) => Rows.Add(new KeyValueRow { Key = "NOVA_CHAVE" });
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in KeysGrid.SelectedItems.Cast<KeyValueRow>().ToList()) Rows.Remove(row);
    }

    private void SaveSmartKey_Click(object sender, RoutedEventArgs e)
    {
        var key = NormalizeKey(SmartKeyBox.Text);
        if (BulletinService.IsAutomaticKey(key))
        {
            SigfurDialog.Show(this,
                $"{key} é uma chave automática do SIGFUR e não precisa ser cadastrada manualmente.\n\nUse o marcador exibido ao lado diretamente no modelo.",
                "SIGFUR — Chave automática", MessageBoxButton.OK, MessageBoxImage.Information);
            Clipboard.SetText(SmartTokenBox.Text);
            return;
        }
        var type = SmartType;
        var raw = type switch
        {
            "date" => SmartDatePicker.SelectedDate?.ToString("dd/MM/yyyy") ?? string.Empty,
            "month" => SmartMonthBox.Text?.Trim() ?? string.Empty,
            _ => SmartValueBox.Text?.Trim() ?? string.Empty
        };
        if (string.IsNullOrWhiteSpace(raw))
        {
            SigfurDialog.Show(this, "Informe o valor da chave.", "SIGFUR — Chaves", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var row = Rows.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (row is null) Rows.Add(new KeyValueRow { Key = key, Value = raw });
        else row.Value = raw;
        KeysGrid.Items.Refresh();
        Clipboard.SetText(SmartTokenBox.Text);
        SigfurDialog.Show(this, $"Chave {key} adicionada. O marcador foi copiado.", "SIGFUR — Chaves", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CopySmartToken_Click(object sender, RoutedEventArgs e)
    {
        UpdateBuilderPreview();
        if (!string.IsNullOrWhiteSpace(SmartTokenBox.Text)) Clipboard.SetText(SmartTokenBox.Text);
    }

    private void SmartTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ConfigureBuilder();
    private void SmartBuilder_Changed(object sender, RoutedEventArgs e) => UpdateBuilderPreview();
    private void SmartDatePicker_Changed(object sender, SelectionChangedEventArgs e) => UpdateBuilderPreview();
    private void SmartMonthBox_Changed(object sender, SelectionChangedEventArgs e) => UpdateBuilderPreview();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        KeysGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        KeysGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var duplicates = Rows.Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .GroupBy(x => x.Key.Trim(), StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1).Select(x => x.Key).ToList();
        if (duplicates.Count > 0)
        {
            SigfurDialog.Show(this, "Existem chaves repetidas: " + string.Join(", ", duplicates), "SIGFUR — Chaves", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    public sealed class KeyValueRow
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
}
