using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views.Military;

namespace SIGFUR.Wpf.Views.Vacation;

public partial class VacationIndividualWindow : Window
{
    private sealed class PlanRow
    {
        public required VacationAllocation Allocation { get; init; }
        public required VacationPeriod Period { get; init; }
        public string PeriodLabel => Period.FullLabel;
    }

    private readonly VacationPlanService _service;
    private readonly MilitaryRecord _military;
    private readonly int _year;
    private readonly int _initialPeriodId;
    private readonly ObservableCollection<VacationPeriod> _periods = [];
    private readonly ObservableCollection<PlanRow> _plans = [];
    private VacationFinancialProfile _financial = new();
    public bool HasChanges { get; private set; }

    public VacationIndividualWindow(VacationPlanService service, MilitaryRecord military, int year, int initialPeriodId)
    {
        _service = service; _military = military; _year = year; _initialPeriodId = initialPeriodId;
        InitializeComponent();
        App.UiState.Attach(this);
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        MilitaryTitle.Text = $"{_military.ShortRank} {_military.Name}";
        MilitarySubtitle.Text = $"Nome de guerra: {_military.WarName} · CPF: {_military.FormattedCpf} · PREC-CP: {_military.PrecCp}";
        try
        {
            foreach (var period in await Task.Run(() => _service.GetPeriodsAsync(_year))) _periods.Add(period);
            PeriodBox.ItemsSource = _periods;
            PeriodBox.SelectedItem = _periods.FirstOrDefault(x => x.Id == _initialPeriodId) ?? _periods.FirstOrDefault();
            PlansGrid.ItemsSource = _plans;
            await RefreshPlansAsync();
            await Task.WhenAll(LoadFinancialAsync(), LoadPaystubsAsync());
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task RefreshPlansAsync()
    {
        var mine = (await Task.Run(() => _service.GetAllocationsForMilitaryAsync(_year, _military))).ToList();
        _plans.Clear();
        foreach (var allocation in mine)
        {
            var period = _periods.FirstOrDefault(x => x.Id == allocation.PeriodId);
            if (period is not null) _plans.Add(new PlanRow { Allocation = allocation, Period = period });
        }
        var days = mine.Sum(x => x.Days);
        AnnualSummaryText.Text = $"{days}/30 dias no ano · {mine.Count} período(s)";
        StatusText.Text = days > 30 ? "Atenção: o plano ultrapassou 30 dias." : "Plano individual atualizado.";
    }

    private int SelectedDays() => int.TryParse((DaysBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var days) ? days : 30;

    private async void AddPlan_Click(object sender, RoutedEventArgs e)
    {
        if (PeriodBox.SelectedItem is not VacationPeriod period) return;
        try
        {
            var days = SelectedDays();
            var result = await Task.Run(() => _service.AllocateAsync(_year, period.Id, [_military], days));
            if (result.Failures.Count > 0) SigfurDialog.Show(this, string.Join(Environment.NewLine, result.Failures), "Plano de Férias", MessageBoxButton.OK, MessageBoxImage.Warning);
            HasChanges |= result.Added > 0;
            await RefreshPlansAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void MovePlan_Click(object sender, RoutedEventArgs e)
    {
        if (PlansGrid.SelectedItem is not PlanRow row) return;
        var result = ShowMoveDialog(row);
        if (result is null) return;
        try { await Task.Run(() => _service.MoveAllocationAsync(row.Allocation, result.Value.PeriodId, result.Value.Days)); HasChanges = true; await RefreshPlansAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private (int PeriodId, int Days)? ShowMoveDialog(PlanRow row)
    {
        var dialog = new Window { Title = "Mover plano individual", Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Width = 500, Height = 250, ResizeMode = ResizeMode.NoResize, Background = FindResource("AppBackgroundBrush") as System.Windows.Media.Brush, Icon = Icon };
        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = row.PeriodLabel, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var period = new ComboBox { ItemsSource = _periods, DisplayMemberPath = "FullLabel", SelectedItem = row.Period, Margin = new Thickness(0, 14, 0, 10) }; Grid.SetRow(period, 1); grid.Children.Add(period);
        var days = new ComboBox { ItemsSource = new[] { 10, 15, 30 }, SelectedItem = VacationPlanService.NormalizeDays(row.Allocation.Days), Width = 120, HorizontalAlignment = HorizontalAlignment.Left }; Grid.SetRow(days, 2); grid.Children.Add(days);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancelar", Style = FindResource("SecondaryButtonStyle") as Style, Margin = new Thickness(0, 0, 8, 0) }; cancel.Click += (_, _) => dialog.DialogResult = false;
        var ok = new Button { Content = "Aplicar", Style = FindResource("PrimaryButtonStyle") as Style }; ok.Click += (_, _) => dialog.DialogResult = true;
        bar.Children.Add(cancel); bar.Children.Add(ok); Grid.SetRow(bar, 3); grid.Children.Add(bar); dialog.Content = grid;
        return dialog.ShowDialog() == true && period.SelectedItem is VacationPeriod p && days.SelectedItem is int d ? (p.Id, d) : null;
    }

    private async void PaidYear_Click(object sender, RoutedEventArgs e)
    {
        var rank = MilitaryRankService.Normalize(MilitaryRankService.Canonicalize(_military.Rank));
        var needsFoodAid = rank.Contains("cabo", StringComparison.OrdinalIgnoreCase) || rank.Contains("soldado", StringComparison.OrdinalIgnoreCase);
        if (needsFoodAid && SigfurDialog.Show(this,
                "Confirme que o Auxílio-Alimentação de férias também foi publicado e pago para este Cabo/Soldado.",
                "Conferência obrigatória", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await Task.Run(() => _service.SetPaidAnnualAsync(_year, _military.Id, true, needsFoodAid)); HasChanges = true; await RefreshPlansAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void UnpaidYear_Click(object sender, RoutedEventArgs e)
    {
        try { await Task.Run(() => _service.SetPaidAnnualAsync(_year, _military.Id, false)); HasChanges = true; await RefreshPlansAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void DeletePlan_Click(object sender, RoutedEventArgs e)
    {
        if (PlansGrid.SelectedItem is not PlanRow row) return;
        if (SigfurDialog.Show(this, $"Excluir o plano em {row.Period.DisplayName}?", "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await Task.Run(() => _service.RemoveAllocationAsync(row.Allocation.Id)); HasChanges = true; await RefreshPlansAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task LoadFinancialAsync()
    {
        _financial = await Task.Run(() => _service.GetFinancialProfileAsync(_military, _year));
        ApplyFinancialToFields();
        await CalculateAsync(false);
    }

    private void ApplyFinancialToFields()
    {
        var legal = VacationPlanService.VacationRemunerationProfile(_military.Rank);
        TypeBox.SelectedItem = TypeBox.Items.Cast<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Content?.ToString(), _financial.Type, StringComparison.OrdinalIgnoreCase)) ?? TypeBox.Items[0];
        var options = MilitaryRemunerationCatalog.QualificationOptions(legal);
        QualificationBox.ItemsSource = options;
        var selected = QualificationCatalog.Resolve(_financial.QualificationCode);
        QualificationBox.SelectedItem = options.FirstOrDefault(x => x.Code == selected.Code) ?? options[0];
        QualificationBox.IsEnabled = !legal.IsQualificationFixed;
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        var qualificationValue = _financial.QualificationAdditionalOverride
                                 ?? (_financial.PaystubQualificationAdditional is > 0m
                                     ? _financial.PaystubQualificationAdditional
                                     : PaystubAmountFromPercent(_financial.SalaryOverride ?? _financial.PaystubSalary, _financial.PaystubQualificationPercent));
        var militaryValue = _financial.MilitaryAdditionalOverride
                            ?? PaystubAmountFromPercent(_financial.PaystubSalary, _financial.PaystubMilitaryAdditionalPercent)
                            ?? _financial.PaystubMilitaryAdditional;
        var availabilityValue = _financial.AvailabilityAdditionalOverride
                                ?? PaystubAmountFromPercent(_financial.PaystubSalary, _financial.PaystubAvailabilityPercent)
                                ?? _financial.PaystubAvailabilityAdditional;
        QualificationAmountText.Text = qualificationValue.HasValue
            ? FormatPaystubAdditional(_financial.PaystubQualificationPercent, qualificationValue.Value, _financial.QualificationAdditionalOverride.HasValue, pt)
            : "Valor calculado pelo percentual selecionado";
        MilitaryAdditionalBox.Text = _financial.HasPaystubValues || _financial.MilitaryAdditionalOverride.HasValue
            ? FormatPaystubAdditional(_financial.PaystubMilitaryAdditionalPercent, militaryValue ?? 0m, _financial.MilitaryAdditionalOverride.HasValue, pt)
            : FormatPercent(_financial.MilitaryAdditionalPercent) + "% (padrão legal)";
        AvailabilityBox.Text = _financial.HasPaystubValues || _financial.AvailabilityAdditionalOverride.HasValue
            ? FormatPaystubAdditional(_financial.PaystubAvailabilityPercent, availabilityValue ?? 0m, _financial.AvailabilityAdditionalOverride.HasValue, pt)
            : FormatPercent(_financial.AvailabilityPercent) + "% (padrão legal)";
        PnrPercentBox.SelectedItem = PnrPercentBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item =>
            !_financial.IncludePnr ? Equals(item.Tag, "None")
            : _financial.PnrPercent == 3.5m ? Equals(item.Tag, "3.5")
            : _financial.PnrPercent == 5m && Equals(item.Tag, "5")) ?? PnrPercentBox.Items[0];
        PnrValueText.Text = _financial.IncludePnr
            ? (_financial.PaystubPnrDiscount.HasValue
                ? $"{_financial.PaystubPnrDiscount.Value.ToString("C2", pt)} no contracheque"
                : "Será calculado sobre o soldo")
            : "Sem desconto";
        FinancialRuleText.Text = _financial.HasPaystubValues
            ? $"Base importada do último contracheque salvo ({_financial.PaystubReference}). Use ‘Alterar valor’ para ajustar soldo ou qualquer adicional; as alterações ficam salvas somente para este militar."
            : legal.Explanation + " Base: " + MilitaryRemunerationCatalog.LegalBasis + ". Use ‘Alterar valor’ para qualquer exceção individual. " + _financial.PaystubImportError;
    }

    private static string FormatPercent(decimal? value) => value?.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")) ?? string.Empty;

    private static string FormatPaystubAdditional(decimal? percent, decimal value, bool exceptional, CultureInfo culture)
    {
        if (exceptional) return value.ToString("C2", culture) + " • excepcional";
        var percentage = percent.HasValue ? $"{percent.Value.ToString("0.##", culture)}% • " : string.Empty;
        return percentage + value.ToString("C2", culture) + " • contracheque";
    }

    private static decimal? PaystubAmountFromPercent(decimal? salary, decimal? percent)
        => salary is > 0m && percent.HasValue
            ? Math.Round(salary.Value * percent.Value / 100m, 2, MidpointRounding.AwayFromZero)
            : null;

    private async void EditAdditionalValue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key }) return;
        var (label, original, current) = await AdditionalValuesAsync(key);
        if (string.IsNullOrWhiteSpace(label)) return;
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        var source = _financial.HasPaystubValues ? $"contracheque {_financial.PaystubReference}" : "cálculo usual";
        var prompt = new TextPromptWindow(
            "Valor excepcional do adicional",
            $"Informe o novo valor de {label}. O valor do {source} é {original.ToString("C2", pt)}. A alteração será individual para este militar e ficará sinalizada no cálculo.",
            current.ToString("0.00", pt)) { Owner = this };
        if (prompt.ShowDialog() != true) return;
        if (!TryParseMoney(prompt.Value, out var informed) || informed < 0m)
        {
            SigfurDialog.Show(this, "Informe um valor monetário igual ou superior a zero.", "Valor inválido", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        informed = Math.Round(informed, 2, MidpointRounding.AwayFromZero);
        if (informed != original && SigfurDialog.Show(this,
                $"O valor informado para {label} ({informed.ToString("C2", pt)}) é diferente do valor encontrado no {source} ({original.ToString("C2", pt)}).\n\nConfirme somente quando houver fundamento para a alteração excepcional.",
                "Confirmar valor excepcional", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        SetAdditionalOverride(key, informed == original ? null : informed);
        await _service.SaveFinancialProfileAsync(_financial);
        ApplyFinancialToFields();
        await CalculateAsync(true);
        StatusText.Text = informed == original
            ? $"{label} restaurado ao valor do {source}."
            : $"Valor excepcional de {label} salvo para {_military.WarName}.";
    }

    private async void ResetAdditionalValue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key }) return;
        var (label, _, _) = await AdditionalValuesAsync(key);
        if (string.IsNullOrWhiteSpace(label)) return;
        SetAdditionalOverride(key, null);
        await _service.SaveFinancialProfileAsync(_financial);
        ApplyFinancialToFields();
        await CalculateAsync(true);
        StatusText.Text = $"{label} restaurado ao valor original do contracheque/cálculo usual.";
    }

    private async Task<(string Label, decimal Original, decimal Current)> AdditionalValuesAsync(string key)
    {
        decimal? savedOverride = key switch
        {
            "Salary" => _financial.SalaryOverride,
            "Qualification" => _financial.QualificationAdditionalOverride,
            "Military" => _financial.MilitaryAdditionalOverride,
            "Availability" => _financial.AvailabilityAdditionalOverride,
            _ => null
        };
        decimal? fromPaystub = key switch
        {
            "Salary" => _financial.PaystubSalary,
            "Qualification" => _financial.PaystubQualificationAdditional is > 0m
                ? _financial.PaystubQualificationAdditional
                : PaystubAmountFromPercent(_financial.SalaryOverride ?? _financial.PaystubSalary, _financial.PaystubQualificationPercent),
            "Military" => PaystubAmountFromPercent(_financial.PaystubSalary, _financial.PaystubMilitaryAdditionalPercent)
                          ?? _financial.PaystubMilitaryAdditional,
            "Availability" => PaystubAmountFromPercent(_financial.PaystubSalary, _financial.PaystubAvailabilityPercent)
                              ?? _financial.PaystubAvailabilityAdditional,
            _ => null
        };
        var label = key switch
        {
            "Salary" => "Soldo",
            "Qualification" => "Adicional de Habilitação",
            "Military" => "Adicional Militar",
            "Availability" => "Disponibilidade Militar",
            _ => string.Empty
        };
        if (fromPaystub.HasValue) return (label, fromPaystub.Value, savedOverride ?? fromPaystub.Value);

        SetAdditionalOverride(key, null);
        var usual = await _service.CalculateFinancialAsync(_military, _year, _financial);
        SetAdditionalOverride(key, savedOverride);
        var original = key switch
        {
            "Salary" => usual.Salary,
            "Qualification" => usual.QualificationAdditional,
            "Military" => usual.MilitaryAdditional,
            "Availability" => usual.AvailabilityAdditional,
            _ => 0m
        };
        return (label, original, savedOverride ?? original);
    }

    private void SetAdditionalOverride(string key, decimal? value)
    {
        switch (key)
        {
            case "Salary": _financial.SalaryOverride = value; break;
            case "Qualification": _financial.QualificationAdditionalOverride = value; break;
            case "Military": _financial.MilitaryAdditionalOverride = value; break;
            case "Availability": _financial.AvailabilityAdditionalOverride = value; break;
        }
    }

    private static bool TryParseMoney(string? text, out decimal value)
    {
        var raw = (text ?? string.Empty).Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.GetCultureInfo("pt-BR"), out value)
               || decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }
    private async void SaveFinancial_Click(object sender, RoutedEventArgs e)
    {
        _financial.Type = MilitaryCareerTypeService.Normalize((TypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString()) is { Length: > 0 } selectedType
            ? selectedType
            : MilitaryCareerTypeService.Temporary;
        var qualification = QualificationBox.SelectedItem as QualificationOption ?? QualificationCatalog.All[0];
        _financial.QualificationCode = qualification.Code;
        _financial.QualificationPercent = qualification.IsNone ? null : qualification.Percentage;
        var pnrTag = (PnrPercentBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (pnrTag == "Select")
        {
            SigfurDialog.Show(this,
                "O cadastro/contracheque indica ocupação de PNR. Selecione 3,5% para imóvel com despesas condominiais, 5% para imóvel sem despesas condominiais, ou escolha explicitamente não descontar PNR.",
                "Definir taxa de PNR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _financial.IncludePnr = pnrTag is "3.5" or "5";
        _financial.PnrPercent = pnrTag == "3.5" ? 3.5m : pnrTag == "5" ? 5m : null;
        try { await _service.SaveFinancialProfileAsync(_financial); await CalculateAsync(true); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ResetFinancial_Click(object sender, RoutedEventArgs e)
    {
        _financial = _service.CreateDefaultFinancialProfile(_military, _year);
        ApplyFinancialToFields();
        await CalculateAsync(false);
    }

    private async Task CalculateAsync(bool saved)
    {
        var result = await Task.Run(() => _service.CalculateFinancialAsync(_military, _year, _financial));
        SalaryText.Text = result.Success
            ? result.Salary.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"))
              + (_financial.SalaryOverride.HasValue ? " • excepcional" : _financial.PaystubSalary.HasValue ? " • contracheque" : " • tabela")
            : "—";
        FinancialResultText.Text = result.Success ? result.VacationAdditionalText : "Dados incompletos";
        FinancialWordsText.Text = result.Success
            ? $"{result.VacationAdditionalWords}. Base total: {result.BaseTotal.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"))}."
              + (result.PnrDiscount > 0m ? $" Taxa mensal de PNR: {result.PnrDiscount.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"))} ({_financial.PnrPercent:0.#}% do soldo), sem reduzir a base das férias." : string.Empty)
            : result.Error;
        PnrValueText.Text = result.Success && result.PnrDiscount > 0m
            ? result.PnrDiscount.ToString("C2", CultureInfo.GetCultureInfo("pt-BR")) + $" ({_financial.PnrPercent:0.#}% do soldo)"
            : _financial.IncludePnr ? "Selecione 3,5% ou 5%" : "Sem desconto";
        StatusText.Text = saved ? "Dados financeiros salvos e cálculo atualizado." : "Cálculo financeiro atualizado.";
    }

    private async Task LoadPaystubsAsync()
    {
        PaystubsGrid.ItemsSource = await Task.Run(() => App.Paystubs.FindForMilitaryAsync(_military));
    }
    private async void RefreshPaystubs_Click(object sender, RoutedEventArgs e) { App.Paystubs.InvalidateCache(); await LoadPaystubsAsync(); }
    private void OpenPaystub_Click(object sender, RoutedEventArgs e)
    {
        if (PaystubsGrid.SelectedItem is PaystubFileRecord file) ShellService.OpenPath(file.Path);
    }
    private void PaystubsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenPaystub_Click(sender, new RoutedEventArgs());
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowError(Exception ex)
    {
        _ = App.Log.WriteAsync("Falha na carteira individual de férias.", ex);
        SigfurDialog.Show(this, ex.Message, "SIGFUR — Carteira de Férias", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
