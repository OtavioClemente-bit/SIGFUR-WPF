using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Finance;

public partial class AdjustmentAccountsWindow : Window
{
    private readonly MilitaryRepository _repository;
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private AdjustmentAccountsSettings _settings = new();
    private readonly ObservableCollection<MilitaryRecord> _military = [];
    private readonly ObservableCollection<AdjustmentRubric> _sourceRubrics = [];
    private readonly ObservableCollection<AdjustmentBizuRule> _bizuRules = [];
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private bool _loading;
    private readonly DispatcherTimer _recalcTimer;

    public ObservableCollection<AdjustmentRubric> CalculatedRows { get; } = [];

    public AdjustmentAccountsWindow(MilitaryRepository repository, AppPaths paths, JsonFileService json)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _repository = repository;
        _paths = paths;
        _json = json;
        DataContext = this;
        _recalcTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(280) };
        _recalcTimer.Tick += (_, _) => { _recalcTimer.Stop(); if (!_loading) Recalculate(); };
        Loaded += async (_, _) => { await InitializeAsync(); AttachAutomaticRecalculation(); };
        Closing += async (_, _) => await SaveStoreAsync();
    }


    private void AttachAutomaticRecalculation()
    {
        TextBox[] textBoxes =
        [
            SalaryBox, DaysInMonthBox, ServedDaysBox, VacationMonthsBox, ChristmasMonthsBox,
            QualificationBox, MilitaryAdditionalBox, AvailabilityBox, PermanenceBox, PreSchoolBox,
            FamilySalaryBox, FusexBox, MilitaryPensionBox, PnrBox, FusexDependentBox, FusexMedicalBox,
            AlimonyBox, IncomeTaxDependentsBox
        ];
        foreach (var box in textBoxes) box.TextChanged += InputChanged;
        CheckBox[] checks =
        [
            IncomeTaxReducerCheck, MonthlyIncomeTaxCheck, VacationIncomeTaxCheck,
            ChristmasIncomeTaxCheck, DeductMedicalCheck, DeductDependentCheck, DeductAlimonyCheck, DeductPnrCheck
        ];
        foreach (var check in checks) check.Click += InputChanged;

        ComboBox[] entitlementBoxes =
        [
            VacationAdditionalEntitlementBox, VacationIndemnityEntitlementBox,
            ChristmasEntitlementBox, ReceivedChristmasStatusBox, PecuniaryEntitlementBox
        ];
        foreach (var box in entitlementBoxes) box.SelectionChanged += InputChanged;
    }

    private void InputChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _recalcTimer.Stop();
        _recalcTimer.Start();
    }

    private async Task InitializeAsync()
    {
        _loading = true;
        try
        {
            var store = await _json.LoadAsync<AdjustmentAccountsStore>(_paths.AdjustmentAccountsSettingsFile) ?? new AdjustmentAccountsStore();
            _settings = store.Settings ?? new AdjustmentAccountsSettings();

            var source = store.Rubrics?.Where(x => !x.IsAuto).ToList() ?? [];
            if (source.Count == 0) source = AdjustmentAccountsService.CreateDefaultRubrics(_settings).Select(x => x.Clone()).ToList();
            EnsureRubricIds(source);
            ReplaceCollection(_sourceRubrics, source);

            var rules = store.CustomBizuRules is { Count: > 0 }
                ? store.CustomBizuRules.Select(x => x.Clone())
                : AdjustmentAccountsService.DefaultBizuRules().Select(x => x.Clone());
            ReplaceCollection(_bizuRules, rules);
            BizuBox.ItemsSource = _bizuRules;

            var military = (await _repository.GetAllAsync())
                .OrderBy(x => x, Comparer<MilitaryRecord>.Create((a, b) => MilitaryRankService.Compare(a.Rank, a.Name, b.Rank, b.Name)))
                .ToList();
            ReplaceCollection(_military, military);
            RankBox.ItemsSource = await _repository.GetRanksAsync();

            UpdateControlsFromSettings();
            if (string.IsNullOrWhiteSpace(RankBox.Text) && RankBox.Items.Count > 0)
                RankBox.SelectedIndex = 0;

            if (_settings.Salary <= 0m && !string.IsNullOrWhiteSpace(RankBox.Text))
            {
                var officialSalary = await _repository.GetSalaryByRankAsync(RankBox.Text);
                if (officialSalary > 0m)
                {
                    _settings.Salary = officialSalary;
                    Set(SalaryBox, officialSalary);
                }
            }

            BizuBox.SelectedItem = _bizuRules.FirstOrDefault(x => x.Title.Equals(_settings.SelectedBizuTitle, StringComparison.OrdinalIgnoreCase)) ?? _bizuRules.FirstOrDefault();
            Recalculate();
            StatusText.Text = $"Pronto. O cálculo usa apenas o posto/graduação. {_military.Count:N0} militar(es) estarão disponíveis somente na geração do boletim.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Falha ao carregar o módulo.";
            SigfurDialog.Show(this, "Não foi possível abrir o Ajuste de Contas.\n\n" + ex.Message, "Ajuste de Contas", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _loading = false; }
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }

    private static void EnsureRubricIds(IEnumerable<AdjustmentRubric> rows)
    {
        foreach (var row in rows)
            if (string.IsNullOrWhiteSpace(row.Id)) row.Id = row.IsCustom ? Guid.NewGuid().ToString("N") : row.Code;
    }

    private void UpdateControlsFromSettings()
    {
        RankBox.Text = _settings.Rank;
        Set(SalaryBox, _settings.Salary); Set(DaysInMonthBox, _settings.DaysInMonth); Set(ServedDaysBox, _settings.ServedDays);
        Set(VacationMonthsBox, _settings.VacationMonths); Set(ChristmasMonthsBox, _settings.ChristmasMonths);
        SetEntitlement(VacationAdditionalEntitlementBox, _settings.VacationAdditionalEntitlement);
        SetEntitlement(VacationIndemnityEntitlementBox, _settings.VacationIndemnityEntitlement);
        SetEntitlement(ChristmasEntitlementBox, _settings.ChristmasEntitlement);
        SetEntitlement(ReceivedChristmasStatusBox, _settings.ReceivedChristmasFirstInstallment);
        SetEntitlement(PecuniaryEntitlementBox, _settings.PecuniaryEntitlement);
        Set(QualificationBox, _settings.QualificationPercent); Set(MilitaryAdditionalBox, _settings.MilitaryAdditionalPercent);
        Set(AvailabilityBox, _settings.MilitaryAvailabilityPercent); Set(PermanenceBox, _settings.PermanencePercent);
        Set(PreSchoolBox, _settings.PreSchoolValue); Set(FamilySalaryBox, _settings.FamilySalaryValue);
        Set(FusexBox, _settings.FusexPercent); Set(MilitaryPensionBox, _settings.MilitaryPensionPercent);
        Set(PnrBox, _settings.PnrPercent); Set(FusexDependentBox, _settings.FusexDependentDiscount);
        Set(FusexMedicalBox, _settings.FusexMedicalExpense); Set(AlimonyBox, _settings.AlimonyValue);
        Set(IncomeTaxDependentsBox, _settings.IncomeTaxDependents);
        IncomeTaxReducerCheck.IsChecked = _settings.ApplyIncomeTaxReducer2026;
        MonthlyIncomeTaxCheck.IsChecked = _settings.IncludeMonthlyIncomeTax;
        VacationIncomeTaxCheck.IsChecked = _settings.IncludeVacationIncomeTax;
        ChristmasIncomeTaxCheck.IsChecked = _settings.IncludeChristmasIncomeTax;
        DeductMedicalCheck.IsChecked = _settings.DeductFusexMedicalExpense;
        DeductDependentCheck.IsChecked = _settings.DeductFusexDependent;
        DeductAlimonyCheck.IsChecked = _settings.DeductAlimony;
        DeductPnrCheck.IsChecked = _settings.DeductPnr;
    }

    private bool ReadControlsIntoSettings(bool showErrors)
    {
        var errors = new List<string>();
        _settings.Rank = RankBox.Text.Trim();
        Read(SalaryBox, "soldo", x => _settings.Salary = x, errors);
        ReadInt(DaysInMonthBox, "dias do mês", x => _settings.DaysInMonth = x, errors);
        ReadInt(ServedDaysBox, "dias trabalhados", x => _settings.ServedDays = x, errors);
        ReadInt(VacationMonthsBox, "avos de férias", x => _settings.VacationMonths = x, errors);
        ReadInt(ChristmasMonthsBox, "avos de 13º", x => _settings.ChristmasMonths = x, errors);
        _settings.VacationAdditionalEntitlement = IsEntitled(VacationAdditionalEntitlementBox);
        _settings.VacationIndemnityEntitlement = IsEntitled(VacationIndemnityEntitlementBox);
        _settings.ChristmasEntitlement = IsEntitled(ChristmasEntitlementBox);
        _settings.ReceivedChristmasFirstInstallment = IsEntitled(ReceivedChristmasStatusBox);
        _settings.PecuniaryEntitlement = IsEntitled(PecuniaryEntitlementBox);
        Read(QualificationBox, "adicional de habilitação", x => _settings.QualificationPercent = x, errors);
        Read(MilitaryAdditionalBox, "adicional militar", x => _settings.MilitaryAdditionalPercent = x, errors);
        Read(AvailabilityBox, "disponibilidade militar", x => _settings.MilitaryAvailabilityPercent = x, errors);
        Read(PermanenceBox, "permanência", x => _settings.PermanencePercent = x, errors);
        Read(PreSchoolBox, "pré-escolar", x => _settings.PreSchoolValue = x, errors);
        Read(FamilySalaryBox, "salário-família", x => _settings.FamilySalaryValue = x, errors);
        Read(FusexBox, "FUSEx", x => _settings.FusexPercent = x, errors);
        Read(MilitaryPensionBox, "pensão militar", x => _settings.MilitaryPensionPercent = x, errors);
        Read(PnrBox, "PNR", x => _settings.PnrPercent = x, errors);
        Read(FusexDependentBox, "FUSEx dependente", x => _settings.FusexDependentDiscount = x, errors);
        Read(FusexMedicalBox, "despesa médica FUSEx", x => _settings.FusexMedicalExpense = x, errors);
        Read(AlimonyBox, "pensão alimentícia", x => _settings.AlimonyValue = x, errors);
        ReadInt(IncomeTaxDependentsBox, "dependentes de IR", x => _settings.IncomeTaxDependents = x, errors);
        _settings.ApplyIncomeTaxReducer2026 = IncomeTaxReducerCheck.IsChecked == true;
        _settings.IncludeMonthlyIncomeTax = MonthlyIncomeTaxCheck.IsChecked == true;
        _settings.IncludeVacationIncomeTax = VacationIncomeTaxCheck.IsChecked == true;
        _settings.IncludeChristmasIncomeTax = ChristmasIncomeTaxCheck.IsChecked == true;
        _settings.DeductFusexMedicalExpense = DeductMedicalCheck.IsChecked == true;
        _settings.DeductFusexDependent = DeductDependentCheck.IsChecked == true;
        _settings.DeductAlimony = DeductAlimonyCheck.IsChecked == true;
        _settings.DeductPnr = DeductPnrCheck.IsChecked == true;
        if (errors.Count == 0) return true;
        if (showErrors) SigfurDialog.Show(this, "Corrija os campos:\n\n• " + string.Join("\n• ", errors), "Ajuste de Contas", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private static void Set(TextBox box, decimal value) => box.Text = value.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR"));
    private static void Set(TextBox box, int value) => box.Text = value.ToString(CultureInfo.InvariantCulture);
    private static void SetEntitlement(ComboBox box, bool value) => box.SelectedIndex = value ? 0 : 1;
    private static bool IsEntitled(ComboBox box) => box.SelectedIndex == 0;

    private static void Read(TextBox box, string label, Action<decimal> setter, ICollection<string> errors)
    {
        if (TryParseDecimal(box.Text, out var value)) setter(value); else errors.Add(label);
    }

    private static void ReadInt(TextBox box, string label, Action<int> setter, ICollection<string> errors)
    {
        if (int.TryParse(box.Text.Trim(), out var value)) setter(value); else errors.Add(label);
    }

    private static bool TryParseDecimal(string? text, out decimal value)
    {
        var cleaned = (text ?? string.Empty).Trim().Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.GetCultureInfo("pt-BR"), out value)
               || decimal.TryParse(cleaned.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private void Recalculate()
    {
        if (!ReadControlsIntoSettings(showErrors: false)) return;
        var result = AdjustmentAccountsService.Calculate(_settings, _sourceRubrics);
        ReplaceCollection(CalculatedRows, result.Rows);
        EarningsText.Text = AdjustmentAccountsService.FormatMoney(result.Earnings);
        DiscountsText.Text = AdjustmentAccountsService.FormatMoney(result.Discounts);
        NetText.Text = AdjustmentAccountsService.FormatMoney(result.Net);
        AnnualBaseText.Text = AdjustmentAccountsService.FormatMoney(result.AnnualBase);
        FullSummaryText.Text = BuildSummary(result, full: true);
        ProportionalSummaryText.Text = BuildSummary(result, full: false);
        StatusText.Text = $"{CalculatedRows.Count:N0} rubrica(s) calculadas • Base tributável proporcional: {AdjustmentAccountsService.FormatMoney(result.ProportionalTaxableIncome)} • IR mensal: {AdjustmentAccountsService.FormatMoney(result.ProportionalIncomeTax)}";
    }

    private static string BuildSummary(AdjustmentCalculationResult result, bool full)
    {
        var lines = result.Rows.Select(x => $"{(x.IsIncluded ? "✓" : "—"),-2} {x.Code,-8} {x.Description,-42} {(full ? x.FullValue : x.ProportionalValue),14:N2}");
        var earnings = result.Rows.Where(x => x.IsIncluded && x.Reference != "D" && x.Sign != "-").Sum(x => full ? x.FullValue : x.ProportionalValue);
        var discounts = result.Rows.Where(x => x.IsIncluded && (x.Reference == "D" || x.Sign == "-")).Sum(x => full ? x.FullValue : x.ProportionalValue);
        return string.Join(Environment.NewLine, lines) + Environment.NewLine + new string('─', 74) + Environment.NewLine +
               $"RENDIMENTOS {earnings,59:N2}\nDESCONTOS    {discounts,59:N2}\nLÍQUIDO      {earnings - discounts,59:N2}";
    }

    private async void RankBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        var rank = RankBox.SelectedItem as string ?? RankBox.Text;
        var salary = await _repository.GetSalaryByRankAsync(rank);
        if (salary <= 0m) return;
        _settings.Salary = salary; Set(SalaryBox, salary); Recalculate();
    }

    private void BizuBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var rule = BizuBox.SelectedItem as AdjustmentBizuRule;
        BizuRightsText.Text = rule is null
            ? "Selecione uma hipótese."
            : $"Adicional de férias: {rule.VacationAdditionalText}{Environment.NewLine}" +
              $"Indenização de férias: {rule.VacationIndemnityText}{Environment.NewLine}" +
              $"Adicional natalino: {rule.ChristmasAdditionalText}{Environment.NewLine}" +
              $"Compensação pecuniária: {rule.PecuniaryText}";
        BizuBasisText.Text = string.IsNullOrWhiteSpace(rule?.LegalBasis) ? string.Empty : "Fundamento: " + rule.LegalBasis;
        BizuObservationText.Text = rule?.Observation ?? string.Empty;
        if (!_loading && rule is not null)
        {
            PushUndo();
            AdjustmentAccountsService.ApplyBizu(_settings, rule);
            UpdateControlsFromSettings();
            Recalculate();
            StatusText.Text = rule.Title.StartsWith("MODO DE TESTE", StringComparison.OrdinalIgnoreCase)
                ? "Modo de teste ativo: todos os direitos foram liberados."
                : "Motivo aplicado: as rubricas sem direito foram retiradas automaticamente.";
        }
    }

    private void ApplyBizu_Click(object sender, RoutedEventArgs e)
    {
        if (BizuBox.SelectedItem is not AdjustmentBizuRule rule) return;
        PushUndo();
        AdjustmentAccountsService.ApplyBizu(_settings, rule);
        UpdateControlsFromSettings();
        Recalculate();
    }

    private void ManageBizu_Click(object sender, RoutedEventArgs e)
    {
        var window = new AdjustmentBizuManagerWindow(_bizuRules, (BizuBox.SelectedItem as AdjustmentBizuRule)?.Title ?? string.Empty) { Owner = this };
        var accepted = window.ShowDialog() == true;
        ReplaceCollection(_bizuRules, window.Rules.Select(x => x.Clone()));
        BizuBox.ItemsSource = null; BizuBox.ItemsSource = _bizuRules;
        if (accepted && window.SelectedRule is not null)
        {
            BizuBox.SelectedItem = _bizuRules.FirstOrDefault(x => x.Title.Equals(window.SelectedRule.Title, StringComparison.OrdinalIgnoreCase));
            ApplyBizu_Click(sender, e);
        }
    }

    private void Recalculate_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadControlsIntoSettings(showErrors: true)) return;
        Recalculate();
    }

    private void AddSippes_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SippesRubricPickerWindow { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedRubric is null) return;
        var official = picker.SelectedRubric;
        var rubric = new AdjustmentRubric
        {
            Id = Guid.NewGuid().ToString("N"), Code = official.Code, Description = official.Description,
            Reference = official.Effect == "D" ? "D" : "R", Sign = official.Effect == "D" ? "-" : "+",
            Base = "DIA", Kind = "FIXO", IsIncluded = true, IsCustom = true
        };
        OpenRubricEditor(rubric, add: true);
    }

    private void AddManual_Click(object sender, RoutedEventArgs e) => OpenRubricEditor(new AdjustmentRubric { Id = Guid.NewGuid().ToString("N"), IsCustom = true }, add: true);

    private AdjustmentRubric? SelectedCalculated => RubricsGrid.SelectedItem as AdjustmentRubric;
    private AdjustmentRubric? SelectedSource => SelectedCalculated is { } row ? _sourceRubrics.FirstOrDefault(x => x.Id == row.Id) : null;
    private List<AdjustmentRubric> SelectedSources => RubricsGrid.SelectedItems.OfType<AdjustmentRubric>()
        .Where(x => !x.IsAuto)
        .Select(x => _sourceRubrics.FirstOrDefault(s => s.Id == x.Id))
        .Where(x => x is not null)
        .Cast<AdjustmentRubric>()
        .Distinct()
        .ToList();

    private void EditRubric_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCalculated?.IsAuto == true) { SigfurDialog.Show(this, "Esta rubrica é calculada automaticamente pelas opções de Imposto de Renda.", "Ajuste de Contas", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (SelectedSource is { } source) OpenRubricEditor(source, add: false);
    }

    private void RubricsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => CopySelectedValue();

    private void OpenRubricEditor(AdjustmentRubric source, bool add)
    {
        var dialog = new AdjustmentRubricEditorWindow(source) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        PushUndo();
        dialog.Rubric.Id = source.Id;
        if (add) _sourceRubrics.Add(dialog.Rubric); else
        {
            var index = _sourceRubrics.IndexOf(source);
            if (index >= 0) _sourceRubrics[index] = dialog.Rubric;
        }
        Recalculate();
    }

    private void DuplicateRubric_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSource is not { } source) return;
        PushUndo();
        var copy = source.Clone(); copy.Id = Guid.NewGuid().ToString("N"); copy.Code = copy.Code + "C"; copy.Description += " — CÓPIA"; copy.IsCustom = true;
        _sourceRubrics.Add(copy); Recalculate();
    }

    private void ToggleRubric_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCalculated is { IsAuto: true } automatic)
        {
            DisableAutomaticIncomeTax(automatic.Code);
            return;
        }
        var selected = SelectedSources;
        if (selected.Count == 0) return;
        PushUndo();
        var include = selected.Any(x => !x.IsIncluded);
        foreach (var source in selected) source.IsIncluded = include;
        Recalculate();
    }

    private void RemoveRubric_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCalculated is { IsAuto: true } automatic)
        {
            DisableAutomaticIncomeTax(automatic.Code);
            return;
        }
        var selected = SelectedSources;
        if (selected.Count == 0) return;
        var label = selected.Count == 1 ? $"{selected[0].Code} — {selected[0].Description}" : $"{selected.Count} rubricas selecionadas";
        if (SigfurDialog.Show(this, $"Remover {label}?", "Ajuste de Contas", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        PushUndo();
        foreach (var source in selected) _sourceRubrics.Remove(source);
        Recalculate();
    }

    private void DisableAutomaticIncomeTax(string code)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        var label = normalized switch
        {
            "AD0010" or "ND0010" => "IR mensal",
            "AD0015" => "IR de férias",
            "AD0028" => "IR do adicional natalino",
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(label)) return;
        if (SigfurDialog.Show(this, $"Desativar {label}?", "Ajuste de Contas", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        PushUndo();
        switch (normalized)
        {
            case "AD0010":
            case "ND0010":
                _settings.IncludeMonthlyIncomeTax = false;
                MonthlyIncomeTaxCheck.IsChecked = false;
                break;
            case "AD0015":
                _settings.IncludeVacationIncomeTax = false;
                VacationIncomeTaxCheck.IsChecked = false;
                break;
            case "AD0028":
                _settings.IncludeChristmasIncomeTax = false;
                ChristmasIncomeTaxCheck.IsChecked = false;
                break;
        }
        Recalculate();
    }

    private void RestoreRubrics_Click(object sender, RoutedEventArgs e)
    {
        if (SigfurDialog.Show(this, "Restaurar todas as rubricas padrão do código original?", "Ajuste de Contas", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        PushUndo(); ReplaceCollection(_sourceRubrics, AdjustmentAccountsService.CreateDefaultRubrics(_settings).Select(x => x.Clone())); Recalculate();
    }

    private void RubricsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (e.Row.Item is not AdjustmentRubric calculated || calculated.IsAuto) return;
            var source = _sourceRubrics.FirstOrDefault(x => x.Id == calculated.Id);
            if (source is null || source.IsIncluded == calculated.IsIncluded) return;
            PushUndo();
            source.IsIncluded = calculated.IsIncluded;
            Recalculate();
        }));
    }

    private void GenerateBulletin_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadControlsIntoSettings(showErrors: true)) return;
        if (string.IsNullOrWhiteSpace(_settings.Rank))
        {
            SigfurDialog.Show(this, "Escolha o posto/graduação do ajuste antes de gerar o boletim.", "Boletim", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var matchingCount = _military.Count(x => SameRank(x.Rank, _settings.Rank));
        if (matchingCount == 0)
        {
            SigfurDialog.Show(this,
                $"Não há militares cadastrados com o posto/graduação '{_settings.Rank}'.\n\nO boletim somente permite militares que correspondam ao cálculo atual.",
                "Boletim", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var window = new AdjustmentBulletinWindow(
            _settings.Clone(),
            _sourceRubrics.Select(x => x.Clone()).ToList(),
            _military.ToList()) { Owner = this };
        window.ShowDialog();
        _settings = window.Settings;
        UpdateControlsFromSettings();
        Recalculate();
    }

    private static bool SameRank(string? left, string? right)
    {
        var leftOrder = MilitaryRankService.GetOrder(left);
        var rightOrder = MilitaryRankService.GetOrder(right);
        if (leftOrder != 999 || rightOrder != 999) return leftOrder == rightOrder;
        return string.Equals(MilitaryRankService.Normalize(left), MilitaryRankService.Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadControlsIntoSettings(showErrors: true)) return;
        await SaveStoreAsync(); StatusText.Text = "Configuração salva no AppData do SIGFUR.";
    }

    private async Task SaveStoreAsync()
    {
        try
        {
            ReadControlsIntoSettings(showErrors: false);
            await _json.SaveAsync(_paths.AdjustmentAccountsSettingsFile, new AdjustmentAccountsStore
            {
                Settings = _settings,
                Rubrics = _sourceRubrics.Where(x => !x.IsAuto).Select(x => x.Clone()).ToList(),
                CustomBizuRules = _bizuRules.Select(x => x.Clone()).ToList()
            });
        }
        catch { }
    }

    private void PushUndo()
    {
        if (_loading) return;
        _undo.Push(Snapshot()); _redo.Clear();
    }

    private string Snapshot()
    {
        ReadControlsIntoSettings(showErrors: false);
        return JsonSerializer.Serialize(new AdjustmentAccountsStore { Settings = _settings, Rubrics = _sourceRubrics.Select(x => x.Clone()).ToList(), CustomBizuRules = _bizuRules.Select(x => x.Clone()).ToList() });
    }

    private void RestoreSnapshot(string json)
    {
        var store = JsonSerializer.Deserialize<AdjustmentAccountsStore>(json);
        if (store is null) return;
        _loading = true;
        _settings = store.Settings ?? new AdjustmentAccountsSettings();
        ReplaceCollection(_sourceRubrics, store.Rubrics ?? []);
        ReplaceCollection(_bizuRules, store.CustomBizuRules ?? []);
        BizuBox.ItemsSource = null; BizuBox.ItemsSource = _bizuRules;
        UpdateControlsFromSettings();
        BizuBox.SelectedItem = _bizuRules.FirstOrDefault(x => x.Title.Equals(_settings.SelectedBizuTitle, StringComparison.OrdinalIgnoreCase));
        _loading = false;
        Recalculate();
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        _redo.Push(Snapshot()); RestoreSnapshot(_undo.Pop());
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_redo.Count == 0) return;
        _undo.Push(Snapshot()); RestoreSnapshot(_redo.Pop());
    }

    private void OfficialSalary_Click(object sender, RoutedEventArgs e)
    {
        var window = new OfficialSalaryReferenceWindow { Owner = this };
        window.ShowDialog();
    }

    private void SidebarExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not Expander opened) return;

        Expander[] sections = [PeriodsExpander, BizuExpander, RightsExpander, ValuesExpander];
        foreach (var section in sections)
        {
            if (!ReferenceEquals(section, opened)) section.IsExpanded = false;
        }
    }

    private void CopyValue_Click(object sender, RoutedEventArgs e) => CopySelectedValue();

    private void CopySelectedValue()
    {
        if (SelectedCalculated is not { } row) return;
        Clipboard.SetText(row.ProportionalValue.ToString("N2", CultureInfo.GetCultureInfo("pt-BR")));
        StatusText.Text = $"Valor proporcional de {row.Code} copiado: {AdjustmentAccountsService.FormatMoney(row.ProportionalValue)}.";
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        SigfurDialog.Show(this,
            "ATALHOS\n" +
            "• F2: editar a rubrica selecionada\n" +
            "• Delete: remover a(s) rubrica(s) selecionada(s)\n" +
            "• Espaço: incluir/excluir do cálculo\n" +
            "• Duplo clique: copiar o valor proporcional\n" +
            "• Ctrl+Z / Ctrl+Y: desfazer/refazer\n" +
            "• Ctrl+S: salvar a configuração\n\n" +
            "O Bizu orienta os direitos, mas o BI, o DIEx e o caso concreto sempre prevalecem. Férias e 13º usam avos separados para evitar pagamento incorreto. A pecuniária permanece como controle de conferência; inclua a rubrica específica quando o seu fluxo exigir.",
            "Ajuda — Ajuste de Contas", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.Key == Key.Z) { Undo_Click(sender, e); e.Handled = true; return; }
            if (e.Key == Key.Y) { Redo_Click(sender, e); e.Handled = true; return; }
            if (e.Key == Key.S) { Save_Click(sender, e); e.Handled = true; return; }
        }
        if (e.OriginalSource is TextBox or ComboBox) return;
        if (e.Key == Key.F2) { EditRubric_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Delete) { RemoveRubric_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Space) { ToggleRubric_Click(sender, e); e.Handled = true; }
    }

    private void Calculator_Click(object sender, RoutedEventArgs e) => ShellService.OpenCalculator();
}
