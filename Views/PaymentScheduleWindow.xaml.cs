using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views;

public partial class PaymentScheduleWindow : Window
{
    private static readonly CultureInfo PtBr = new("pt-BR");
    private static readonly string[] MonthNames =
    [
        "JANEIRO", "FEVEREIRO", "MARÇO", "ABRIL", "MAIO", "JUNHO",
        "JULHO", "AGOSTO", "SETEMBRO", "OUTUBRO", "NOVEMBRO", "DEZEMBRO"
    ];
    private static readonly string[] MilitaryMonthNames =
    [
        "JAN", "FEV", "MAR", "ABR", "MAIO", "JUN",
        "JUL", "AGO", "SET", "OUT", "NOV", "DEZ"
    ];

    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private bool _loading;
    private bool _firstDateChangedByUser;

    public PaymentScheduleWindow(AppPaths paths, JsonFileService json)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _paths = paths;
        _json = json;
        MonthBox.ItemsSource = MonthNames;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var root = await _json.LoadNodeAsync(_paths.AppSettingsFile) as JsonObject;
            var cfg = root?["corrida_pagamento"] as JsonObject;
            var (year, month) = ParseCompetence(cfg?["competencia"]?.GetValue<string>()) ?? (DateTime.Today.Year, DateTime.Today.Month);

            MonthBox.SelectedIndex = month - 1;
            YearBox.Text = year.ToString(CultureInfo.InvariantCulture);

            var first = ParseDate(cfg?["primeira"]?.GetValue<string>()) ?? DefaultFirstRunDate(year, month);
            FirstDatePicker.SelectedDate = first;
            _firstDateChangedByUser = !string.IsNullOrWhiteSpace(cfg?["primeira"]?.GetValue<string>());
        }
        finally
        {
            _loading = false;
        }

        UpdateSummary();
    }

    private static DateTime? ParseDate(string? value)
    {
        if (DateTime.TryParseExact(value, ["dd/MM/yyyy", "yyyy-MM-dd", "dd MMM yy", "dd MMM yyyy"], PtBr, DateTimeStyles.None, out var date))
            return date;
        if (DateTime.TryParse(value, PtBr, DateTimeStyles.None, out date))
            return date;
        return null;
    }

    private static (int Year, int Month)? ParseCompetence(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var year) && int.TryParse(parts[1], out var month) && IsValidCompetence(year, month))
            return (year, month);

        var normalized = Normalize(text.Replace("PAGAMENTO", string.Empty, StringComparison.OrdinalIgnoreCase));
        for (var i = 0; i < MonthNames.Length; i++)
        {
            if (!normalized.Contains(Normalize(MonthNames[i]), StringComparison.Ordinal)) continue;
            var yearMatch = System.Text.RegularExpressions.Regex.Match(text, @"\b(20\d{2})\b");
            if (yearMatch.Success && int.TryParse(yearMatch.Value, out year) && IsValidCompetence(year, i + 1))
                return (year, i + 1);
        }

        return null;
    }

    private static string Normalize(string value)
    {
        var form = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var chars = form.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray();
        return new string(chars).Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }

    private static bool IsValidCompetence(int year, int month) => year is >= 2000 and <= 2100 && month is >= 1 and <= 12;

    private static DateTime ThirdBusinessDay(int year, int month)
    {
        var count = 0;
        for (var day = 1; day <= DateTime.DaysInMonth(year, month); day++)
        {
            var date = new DateTime(year, month, day);
            if (IsPaymentNonBusinessDay(date)) continue;
            if (++count == 3) return date;
        }
        return new DateTime(year, month, Math.Min(3, DateTime.DaysInMonth(year, month)));
    }


    private static bool IsPaymentNonBusinessDay(DateTime date)
        => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || IsBrazilianFederalHolidayOrBankClosing(date);

    private static bool IsBrazilianFederalHolidayOrBankClosing(DateTime date)
    {
        var fixedHoliday = (date.Month, date.Day) is
            (1, 1) or   // Confraternização Universal
            (4, 21) or  // Tiradentes
            (5, 1) or   // Dia do Trabalho
            (9, 7) or   // Independência
            (10, 12) or // Nossa Senhora Aparecida
            (11, 2) or  // Finados
            (11, 15) or // Proclamação da República
            (11, 20) or // Consciência Negra
            (12, 25);   // Natal
        if (fixedHoliday) return true;

        var easter = EasterSunday(date.Year);
        return date.Date == easter.AddDays(-48).Date || // Carnaval - segunda
               date.Date == easter.AddDays(-47).Date || // Carnaval - terça
               date.Date == easter.AddDays(-2).Date ||  // Sexta-feira Santa
               date.Date == easter.AddDays(60).Date;    // Corpus Christi
    }

    private static DateTime EasterSunday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = ((h + l - 7 * m + 114) % 31) + 1;
        return new DateTime(year, month, day);
    }

    private static DateTime DefaultFirstRunDate(int year, int month)
    {
        var previousMonth = new DateTime(year, month, 1).AddMonths(-1);
        return new DateTime(previousMonth.Year, previousMonth.Month, Math.Min(15, DateTime.DaysInMonth(previousMonth.Year, previousMonth.Month)));
    }

    private bool TryCompetence(out int year, out int month)
    {
        year = month = 0;
        if (MonthBox.SelectedIndex < 0) return false;
        month = MonthBox.SelectedIndex + 1;
        return int.TryParse((YearBox.Text ?? string.Empty).Trim(), out year) && IsValidCompetence(year, month);
    }

    private static string StorageCompetence(int year, int month) => $"{year:0000}-{month:00}";
    private static string PaymentReferenceTitle(int year, int month) => $"Pagamento {MonthNames[month - 1]} {year:0000}";
    private static string FormatMilitaryDate(DateTime date) => $"{date:dd} {MilitaryMonthNames[date.Month - 1]} {date:yy}";

    private void Competence_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (TryCompetence(out var year, out var month))
        {
            // Ao trocar a referência, a 1ª corrida volta para o padrão operacional:
            // dia 15 do mês anterior, podendo ser alterada manualmente pelo operador.
            _loading = true;
            FirstDatePicker.SelectedDate = DefaultFirstRunDate(year, month);
            _loading = false;
            _firstDateChangedByUser = false;
        }
        UpdateSummary();
    }

    private void FirstDatePicker_SelectedDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_loading) _firstDateChangedByUser = true;
        UpdateSummary();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryCompetence(out var year, out var month))
            {
                SigfurDialog.Show(this, "Informe o mês e o ano do pagamento.", "Corrida de Pagamento", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var first = ResolveFirstRunDate(year, month);
            var second = ThirdBusinessDay(year, month);
            var root = await _json.LoadNodeAsync(_paths.AppSettingsFile) as JsonObject ?? new JsonObject();
            root["corrida_pagamento"] = new JsonObject
            {
                ["competencia"] = StorageCompetence(year, month),
                ["competencia_texto"] = PaymentReferenceTitle(year, month),
                ["primeira"] = first.ToString("dd/MM/yyyy", PtBr),
                ["primeira_texto"] = FormatMilitaryDate(first),
                ["segunda"] = second.ToString("dd/MM/yyyy", PtBr),
                ["segunda_texto"] = FormatMilitaryDate(second),
                ["segunda_regra"] = "3º dia útil do mês do pagamento",
                ["primeira_manual"] = _firstDateChangedByUser
            };
            await _json.SaveNodeAsync(_paths.AppSettingsFile, root);
            CloseAfterSuccessfulSave();
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha ao salvar corrida de pagamento.", ex);
            SigfurDialog.Show(
                this,
                "Não foi possível salvar a corrida de pagamento. O SIGFUR registrou o erro no log.\n\nDetalhe: " + ex.Message,
                "Corrida de Pagamento",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private DateTime ResolveFirstRunDate(int year, int month)
    {
        var typedText = FirstDatePicker.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(typedText) && ParseDate(typedText) is DateTime typedDate)
            return typedDate.Date;

        return FirstDatePicker.SelectedDate?.Date ?? DefaultFirstRunDate(year, month);
    }

    private void CloseAfterSuccessfulSave()
    {
        try
        {
            DialogResult = true;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }

    private void UpdateSummary()
    {
        if (!TryCompetence(out var year, out var month))
        {
            ReferenceText.Text = "Pagamento";
            FirstRunHintText.Text = "Selecione o mês e informe o ano do pagamento.";
            AutoSecondRunText.Text = "Aguardando referência válida.";
            SummaryText.Text = "Use mês + ano para montar a referência no padrão: Pagamento MARÇO 2026.";
            return;
        }

        var reference = PaymentReferenceTitle(year, month);
        var first = FirstDatePicker.SelectedDate ?? DefaultFirstRunDate(year, month);
        var second = ThirdBusinessDay(year, month);
        var defaultFirst = DefaultFirstRunDate(year, month);

        ReferenceText.Text = reference;
        FirstRunHintText.Text = _firstDateChangedByUser
            ? $"Data informada para a 1ª corrida: {FormatMilitaryDate(first)}."
            : $"Padrão inicial sugerido: {FormatMilitaryDate(defaultFirst)}. Altere se o calendário recebido vier diferente.";
        AutoSecondRunText.Text = $"{FormatMilitaryDate(second)} — 3º dia útil de {MonthNames[month - 1]}/{year:0000}";
        SummaryText.Text =
            $"{reference}: 1ª corrida em {FormatMilitaryDate(first)}; 2ª corrida em {FormatMilitaryDate(second)}. " +
            "A 1ª corrida fica manual porque pode mudar conforme o calendário divulgado. A 2ª corrida não precisa ser digitada: o SIGFUR calcula sempre pelo 3º dia útil do próprio mês do pagamento.";
    }
}
