using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views.Military;

namespace SIGFUR.Wpf.Views.Finance;

public partial class AdjustmentAccountsWindow : Window
{
    private readonly MilitaryRepository _repository;
    private readonly LicensedTransferredRepository _licensedTransferred;
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly ObservableCollection<MilitaryRecord> _military = [];
    private ICollectionView? _militaryView;
    private readonly DispatcherTimer _refreshTimer;
    private AdjustmentAccountsStore _store = new();
    private AdjustmentDraft _draft = new();
    private AdjustmentSimulationResult? _result;
    private bool _loading = true;
    private bool _applyingProfile;
    private int _refreshVersion;

    public AdjustmentAccountsWindow(
        MilitaryRepository repository,
        LicensedTransferredRepository licensedTransferred,
        AppPaths paths,
        JsonFileService json)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _repository = repository;
        _licensedTransferred = licensedTransferred;
        _paths = paths;
        _json = json;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        _refreshTimer.Tick += async (_, _) =>
        {
            _refreshTimer.Stop();
            await RefreshSimulationAsync(showInputErrors: false);
        };
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var loaded = await _json.LoadAsync<AdjustmentAccountsStore>(_paths.AdjustmentAccountsSettingsFile);
            _store = AdjustmentAccountsMigrationService.Migrate(loaded);
            _draft = _store.CurrentDraft?.Clone() ?? new AdjustmentDraft();

            var activeTask = _repository.GetAllAsync();
            var licensedTask = _licensedTransferred.GetAllAsync();
            await Task.WhenAll(activeTask, licensedTask);
            foreach (var item in activeTask.Result)
            {
                item.BulletinSource = "Ativo";
                _military.Add(item);
            }
            foreach (var item in licensedTask.Result)
            {
                var military = item.ToMilitaryRecord();
                military.BulletinSource = MilitaryRankService.Normalize(item.Reason).Contains("transfer", StringComparison.OrdinalIgnoreCase)
                    ? "Transferido"
                    : "Licenciado";
                _military.Add(military);
            }

            var ordered = _military
                .OrderBy(x => x, Comparer<MilitaryRecord>.Create((a, b) => MilitaryRankService.Compare(a.Rank, a.Name, b.Rank, b.Name)))
                .ToList();
            _military.Clear();
            foreach (var item in ordered) _military.Add(item);

            _militaryView = CollectionViewSource.GetDefaultView(_military);
            MilitaryBox.ItemsSource = _militaryView;
            HistoricalRankBox.ItemsSource = MilitaryRankService.AllRanks;
            SituationBox.ItemsSource = AdjustmentSituationCatalog.All.Select(x => new SituationOption(x.Key, x.Value)).ToList();
            RefreshReasonOptions();
            PermanenceBox.ItemsSource = Enumerable.Range(0, 6).Select(x => new PercentageChoice(x * 5m, x == 0 ? "Não possui" : $"{x * 5}%")).ToList();
            PermanenceBox.DisplayMemberPath = nameof(PercentageChoice.Label);
            PermanenceBox.SelectedValuePath = nameof(PercentageChoice.Value);
            LoadDraftIntoControls();
            _loading = false;
            await RefreshSimulationAsync(showInputErrors: false);
            StatusText.Text = "Módulo carregado. A simulação não gera publicação.";
        }
        catch (Exception ex)
        {
            _loading = false;
            StatusText.Text = "Falha ao carregar o módulo.";
            SigfurDialog.Show(this, "Não foi possível abrir o Ajuste de Contas.\n\n" + ex.Message,
                "Ajuste de Contas", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadDraftIntoControls()
    {
        if (_draft.IncomeTaxDependents <= 0 && _draft.FamilySalaryDependents > 0)
            _draft.IncomeTaxDependents = _draft.FamilySalaryDependents;
        _draft.FamilySalaryDependents = Math.Max(0, _draft.IncomeTaxDependents);
        _draft.FamilySalaryValue = FamilySalaryQuotaCatalog.Resolve(_draft.FamilySalaryDependents);
        ManualSimulationCheck.IsChecked = _draft.IsManualSimulation;
        TestModeCheck.IsChecked = _draft.IsTestMode;
        TestOverridesPanel.Visibility = _draft.IsTestMode ? Visibility.Visible : Visibility.Collapsed;
        PreSchoolBox.IsReadOnly = !_draft.IsTestMode;
        MilitaryBox.SelectedItem = _military.FirstOrDefault(x => x.Id == _draft.MilitaryId);
        if (MilitaryBox.SelectedItem is MilitaryRecord selected)
        {
            _draft.MilitaryName = selected.Name;
            _draft.CurrentRank = selected.Rank;
            if (!_draft.IsManualSimulation) _draft.HistoricalRank = selected.Rank;
            CurrentRankBox.Text = selected.Rank;
            EnlistmentDateBox.Text = selected.EnlistmentDate;
        }

        HistoricalRankBox.Text = _draft.HistoricalRank;
        HistoricalRankBox.IsEnabled = _draft.IsManualSimulation || _draft.IsTestMode;
        MilitaryBox.IsEnabled = !_draft.IsManualSimulation;
        StartDatePicker.SelectedDate = _draft.EntitlementStart;
        EndDatePicker.SelectedDate = _draft.EntitlementEnd;
        MonthlyAdjustmentOnlyCheck.IsChecked = _draft.MonthlyAdjustmentOnly;
        if (_draft.Situation == AdjustmentSituationKind.NotInformed
            && MilitaryRankService.Canonicalize(_draft.HistoricalRank) != "Aspirante"
            && !string.IsNullOrWhiteSpace(_draft.HistoricalRank))
            _draft.Situation = MilitaryRemunerationCatalog.SuggestedSituation(_draft.HistoricalRank);
        SituationBox.SelectedValue = _draft.Situation;
        ReasonBox.Text = _draft.AdjustmentReason;
        UpdateReasonRuleText();
        QualificationDatePicker.SelectedDate = _draft.QualificationEffectiveDate;
        VacationIndemnityCheck.IsChecked = _draft.VacationIndemnityEntitlement;
        VacationAdditionalCheck.IsChecked = _draft.VacationAdditionalEntitlement;
        ChristmasCheck.IsChecked = _draft.ChristmasEntitlement;
        ChristmasFirstInstallmentCheck.IsChecked = _draft.ReceivedChristmasFirstInstallment;
        PecuniaryCheck.IsChecked = _draft.PecuniaryEntitlement;
        Set(PecuniaryQuotasBox, _draft.PecuniaryQuotas);
        VacationQuotaStartPicker.SelectedDate = _draft.VacationQuotaStart;
        VacationQuotaEndPicker.SelectedDate = _draft.VacationQuotaEnd;
        VacationQuotasOverrideBox.Text = _draft.VacationQuotasOverride?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        Set(ChristmasQuotasBox, _draft.ChristmasQuotas);
        PermanenceBox.SelectedValue = NormalizePermanence(_draft.PermanencePercent);
        Set(PreSchoolBox, _draft.PreSchoolValue);
        IncludePreSchoolCheck.IsChecked = _draft.PreSchoolValue > 0m;
        Set(FamilySalaryBox, _draft.FamilySalaryValue);
        Set(FusexDependentBox, _draft.FusexDependentDiscount);
        Set(FusexMedicalBox, _draft.FusexMedicalExpense);
        Set(AlimonyBox, _draft.AlimonyValue);
        Set(IncomeTaxDependentsBox, _draft.IncomeTaxDependents);
        IncludeMonthlyIncomeTaxCheck.IsChecked = _draft.IncludeMonthlyIncomeTax;
        ApplyIncomeTaxReducerCheck.IsChecked = _draft.ApplyIncomeTaxReducer2026;
        DeductFusexMedicalCheck.IsChecked = _draft.DeductFusexMedicalExpense;
        DeductFusexDependentCheck.IsChecked = _draft.DeductFusexDependent;
        DeductAlimonyCheck.IsChecked = _draft.DeductAlimony;
        DeductPnrCheck.IsChecked = _draft.DeductPnr;
        TestSalaryBox.Text = _draft.TestSalaryOverride?.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")) ?? string.Empty;
        TestQualificationPercentBox.Text = _draft.TestQualificationPercentOverride?.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")) ?? string.Empty;
        Set(TestMilitaryAdditionalPercentBox, _draft.MilitaryAdditionalPercent);
        Set(TestAvailabilityPercentBox, _draft.AvailabilityPercent);
        Set(TestPermanencePercentBox, _draft.PermanencePercent);
        Set(TestFusexPercentBox, _draft.FusexPercent);
        Set(TestMilitaryPensionPercentBox, _draft.MilitaryPensionPercent);
        Set(TestPnrPercentBox, _draft.PnrPercent);
        TestDaysInMonthBox.Text = _draft.TestDaysInMonthOverride?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        TestServedDaysBox.Text = _draft.TestServedDaysOverride?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        ApplyLegalProfileToControls();
    }

    private void MilitaryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _applyingProfile || ManualSimulationCheck.IsChecked == true) return;
        if (MilitaryBox.SelectedItem is MilitaryRecord military)
        {
            _militaryView!.Filter = null;
            _draft.MilitaryId = military.Id;
            _draft.MilitaryName = military.Name;
            _draft.CurrentRank = military.Rank;
            _draft.EnlistmentDate = military.EnlistmentDateValue;
            CurrentRankBox.Text = military.Rank;
            EnlistmentDateBox.Text = military.EnlistmentDate;
            HistoricalRankBox.Text = military.Rank;
            HistoricalRankBox.IsEnabled = false;
            SituationBox.SelectedValue = MilitaryRemunerationCatalog.SuggestedSituation(military.Rank);
            LoadRegisteredFinancialValues(military);
            ApplyLegalProfileToControls();
        }
        ScheduleRefresh();
    }

    private void LoadRegisteredFinancialValues(MilitaryRecord military)
    {
        var preSchoolReference = MilitaryPreSchoolCatalog.Resolve(military.Rank, EndDatePicker.SelectedDate ?? DateTime.Today);
        var receivesPreSchool = MilitaryRecord.IsYes(military.ReceivesPreSchool)
                                || AdjustmentNumericParser.TryParseDecimal(military.PreSchoolValue, out var registeredPreSchool) && registeredPreSchool > 0m;
        var preSchool = receivesPreSchool ? preSchoolReference.NetValue : 0m;
        var alimony = RegisteredValue(military.Alimony, military.AlimonyValue);
        _draft.FamilySalaryDependents = Math.Max(0, _draft.IncomeTaxDependents);
        var familySalary = FamilySalaryQuotaCatalog.Resolve(_draft.FamilySalaryDependents);

        _draft.PreSchoolValue = preSchool;
        _draft.FamilySalaryValue = familySalary;
        _draft.AlimonyValue = alimony;
        Set(PreSchoolBox, preSchool);
        IncludePreSchoolCheck.IsChecked = receivesPreSchool;
        PreSchoolBox.ToolTip = $"Valor-teto {AdjustmentAccountsService.FormatMoney(preSchoolReference.Ceiling)}; categoria {preSchoolReference.Category}; cota-parte {preSchoolReference.MilitarySharePercent:0.##}%; líquido {AdjustmentAccountsService.FormatMoney(preSchoolReference.NetValue)}.";
        Set(FamilySalaryBox, familySalary);
        Set(AlimonyBox, alimony);
        DeductAlimonyCheck.IsChecked = alimony > 0m;

        static decimal RegisteredValue(string? receives, string? text)
            => (MilitaryRecord.IsYes(receives) || AdjustmentNumericParser.TryParseDecimal(text, out var parsed) && parsed > 0m)
               && AdjustmentNumericParser.TryParseDecimal(text, out var value)
                ? Math.Max(0m, value)
                : 0m;
    }

    private void MilitaryBox_KeyUp(object sender, KeyEventArgs e)
    {
        if (_loading || _militaryView is null || ManualSimulationCheck.IsChecked == true) return;
        if (e.Key == Key.Escape)
        {
            _militaryView.Filter = null;
            MilitaryBox.Text = string.Empty;
            MilitaryBox.IsDropDownOpen = false;
            return;
        }

        var query = MilitaryRankService.Normalize(MilitaryBox.Text);
        _militaryView.Filter = item =>
        {
            if (string.IsNullOrWhiteSpace(query) || item is not MilitaryRecord military) return true;
            var searchable = MilitaryRankService.Normalize(string.Join(' ', military.Name, military.WarName,
                military.Rank, military.Cpf, military.PrecCp, military.MilitaryId));
            return searchable.Contains(query, StringComparison.OrdinalIgnoreCase);
        };
        MilitaryBox.IsDropDownOpen = true;
        if (e.Key == Key.Enter && _militaryView.Cast<MilitaryRecord>().FirstOrDefault() is { } first)
            MilitaryBox.SelectedItem = first;
    }

    private void ManualSimulationCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var manual = ManualSimulationCheck.IsChecked == true;
        _draft.IsManualSimulation = manual;
        MilitaryBox.IsEnabled = !manual;
        HistoricalRankBox.IsEnabled = manual || TestModeCheck.IsChecked == true;
        if (manual)
        {
            _applyingProfile = true;
            MilitaryBox.SelectedItem = null;
            MilitaryBox.Text = string.Empty;
            _applyingProfile = false;
            _draft.MilitaryId = 0;
            _draft.MilitaryName = string.Empty;
            _draft.CurrentRank = string.Empty;
            CurrentRankBox.Text = "Simulação sem cadastro";
            EnlistmentDateBox.Text = "—";
            HistoricalRankBox.Text = string.Empty;
            SituationBox.SelectedValue = AdjustmentSituationKind.NotInformed;
        }
        ApplyLegalProfileToControls();
        ScheduleRefresh();
    }

    private void TestModeCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enabled = TestModeCheck.IsChecked == true;
        _draft.IsTestMode = enabled;
        TestOverridesPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        HistoricalRankBox.IsEnabled = enabled || ManualSimulationCheck.IsChecked == true;
        PreSchoolBox.IsReadOnly = !enabled;
        if (enabled)
        {
            if (string.IsNullOrWhiteSpace(TestSalaryBox.Text) && _result is not null)
                Set(TestSalaryBox, _result.Remuneration.Salary);
            if (string.IsNullOrWhiteSpace(TestQualificationPercentBox.Text))
                Set(TestQualificationPercentBox, (QualificationBox.SelectedItem as QualificationOption)?.Percentage ?? 0m);
            Set(TestMilitaryAdditionalPercentBox, _draft.MilitaryAdditionalPercent);
            Set(TestAvailabilityPercentBox, _draft.AvailabilityPercent);
            Set(TestPermanencePercentBox, _draft.PermanencePercent);
            Set(TestFusexPercentBox, _draft.FusexPercent);
            Set(TestMilitaryPensionPercentBox, _draft.MilitaryPensionPercent);
            Set(TestPnrPercentBox, _draft.PnrPercent);
            var end = EndDatePicker.SelectedDate ?? DateTime.Today;
            if (string.IsNullOrWhiteSpace(TestDaysInMonthBox.Text)) Set(TestDaysInMonthBox, DateTime.DaysInMonth(end.Year, end.Month));
            if (string.IsNullOrWhiteSpace(TestServedDaysBox.Text))
            {
                var start = StartDatePicker.SelectedDate ?? new DateTime(end.Year, end.Month, 1);
                Set(TestServedDaysBox, Math.Max(0, (end.Date - start.Date).Days + 1));
            }
        }
        else
        {
            _draft.TestSalaryOverride = null;
            _draft.TestQualificationPercentOverride = null;
            _draft.TestDaysInMonthOverride = null;
            _draft.TestServedDaysOverride = null;
            ApplyLegalProfileToControls();
        }
        ScheduleRefresh();
    }

    private void HistoricalRankBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _applyingProfile) return;
        if (ManualSimulationCheck.IsChecked == true)
        {
            SituationBox.SelectedValue = MilitaryRemunerationCatalog.SuggestedSituation(HistoricalRankBox.Text);
            ApplyLegalProfileToControls();
        }
        ScheduleRefresh();
    }

    private void DomainInputChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _applyingProfile) return;
        if (!(_draft.IsTestMode || TestModeCheck.IsChecked == true) && (ReferenceEquals(sender, SituationBox)
            || ReferenceEquals(sender, StartDatePicker)
            || ReferenceEquals(sender, EndDatePicker)
            || ReferenceEquals(sender, MonthlyAdjustmentOnlyCheck))) ApplyLegalProfileToControls();
        if (ReferenceEquals(sender, EndDatePicker) && IncludePreSchoolCheck.IsChecked == true)
            ApplyCurrentPreSchoolReference();
        if (ReferenceEquals(sender, QualificationBox) || ReferenceEquals(sender, TestQualificationPercentBox)) UpdateQualificationPercentPreview();
        ScheduleRefresh();
    }

    private void ReasonBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _applyingProfile) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var rule = ResolveCurrentBizuRule(ReasonBox.Text);
            if (rule is null)
            {
                UpdateReasonRuleText();
                ScheduleRefresh();
                return;
            }
            ApplyReasonRule(rule);
        }));
    }

    private void UpdateReasonRuleText()
    {
        var rule = ResolveCurrentBizuRule(ReasonBox.Text);
        ReasonRuleText.Text = rule is null
            ? "Motivo fora do catálogo: a geração ficará bloqueada. Se necessário, selecione 'Outros / editar manualmente'."
            : $"Férias: {AdjustmentBizuRule.StatusText(rule.VacationAdditional)}; indenização: {AdjustmentBizuRule.StatusText(rule.VacationIndemnity)}; 13º: {AdjustmentBizuRule.StatusText(rule.ChristmasAdditional)}; pecuniária: {AdjustmentBizuRule.StatusText(rule.Pecuniary)}. {rule.Observation}";
        RenderReasonRights(rule);
    }

    private IReadOnlyList<AdjustmentBizuRule> CurrentBizuRules()
        => AdjustmentAccountsService.AvailableBizuRules(_store.CustomBizuRules);

    private AdjustmentBizuRule? ResolveCurrentBizuRule(string? title)
        => AdjustmentAccountsService.ResolveBizuRule(title, _store.CustomBizuRules);

    private void RefreshReasonOptions()
        => ReasonBox.ItemsSource = CurrentBizuRules().Select(x => x.Title).ToList();

    private async void ChooseAdjustmentReason_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AdjustmentBizuManagerWindow(CurrentBizuRules(), ReasonBox.Text) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedRule is not { } selected) return;

        _store.CustomBizuRules = dialog.Rules
            .Where(x => x.IsCustom)
            .Select(x => x.Clone())
            .ToList();
        RefreshReasonOptions();
        ReasonBox.Text = selected.Title;
        var rule = ResolveCurrentBizuRule(selected.Title) ?? selected;
        ApplyReasonRule(rule);
        await _json.SaveAsync(_paths.AdjustmentAccountsSettingsFile, _store);
        StatusText.Text = $"Motivo aplicado: {rule.Title}. Direitos atualizados conforme a regra selecionada.";
    }

    private void ApplyReasonRule(AdjustmentBizuRule rule)
    {
        AdjustmentAccountsService.ApplyBizu(_draft, rule);
        VacationIndemnityCheck.IsChecked = _draft.VacationIndemnityEntitlement;
        VacationAdditionalCheck.IsChecked = _draft.VacationAdditionalEntitlement;
        ChristmasCheck.IsChecked = _draft.ChristmasEntitlement;
        PecuniaryCheck.IsChecked = _draft.PecuniaryEntitlement;
        Set(PecuniaryQuotasBox, _draft.PecuniaryQuotas);
        UpdateReasonRuleText();
        ScheduleRefresh();
    }

    private void RenderReasonRights(AdjustmentBizuRule? rule)
    {
        ReasonTitleText.Text = rule?.Title ?? "Nenhum motivo selecionado";
        ReasonOriginText.Text = rule?.OriginText ?? "Escolha uma hipótese para aplicar a matriz de direitos.";
        ReasonLegalBasisText.Text = string.IsNullOrWhiteSpace(rule?.LegalBasis) ? "—" : rule.LegalBasis;
        ReasonObservationText.Text = string.IsNullOrWhiteSpace(rule?.Observation)
            ? "Selecione um motivo para visualizar a orientação."
            : rule.Observation;

        ReasonRightsPanel.Children.Clear();
        if (rule is null)
        {
            var emptyState = new Border
            {
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10),
                Child = new TextBlock
                {
                    Text = "Selecione um motivo para conferir os direitos.",
                    TextWrapping = TextWrapping.Wrap
                }
            };
            emptyState.SetResourceReference(Border.BackgroundProperty, "SurfaceAltBrush");
            emptyState.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            ReasonRightsPanel.Children.Add(emptyState);
            return;
        }

        var rights = new (string Label, bool? Value)[]
        {
            ("Adicional de férias", rule.VacationAdditional),
            ("Indenização de férias", rule.VacationIndemnity),
            ("Adicional natalino", rule.ChristmasAdditional),
            ("Compensação pecuniária", rule.Pecuniary)
        };

        foreach (var right in rights)
            ReasonRightsPanel.Children.Add(BuildReasonRightCard(right.Label, right.Value));
    }

    internal static Border BuildReasonRightCard(string labelText, bool? status)
    {
        var background = status switch { true => "SuccessSoftBrush", false => "DangerSoftBrush", _ => "WarningSoftBrush" };
        var foreground = status switch { true => "SuccessBrush", false => "DangerBrush", _ => "WarningBrush" };
        var card = new Border
        {
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 5)
        };
        card.SetResourceReference(Border.BackgroundProperty, background);
        card.SetResourceReference(Border.BorderBrushProperty, foreground);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glyph = new TextBlock
        {
            Text = status switch { true => "✓", false => "×", _ => "!" },
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        row.Children.Add(glyph);
        var label = new TextBlock
        {
            Text = labelText,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 8, 0)
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        var value = new TextBlock
        {
            Text = AdjustmentBizuRule.StatusText(status),
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            TextWrapping = TextWrapping.Wrap
        };
        value.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        card.Child = row;
        return card;
    }

    private void UseEnlistmentDateForVacation_Click(object sender, RoutedEventArgs e)
    {
        if (_draft.EnlistmentDate is not { } enlistment)
        {
            SigfurDialog.Show(this, "O militar selecionado não possui data de praça cadastrada.", "Cotas de férias",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        VacationQuotaStartPicker.SelectedDate = enlistment.Date;
    }

    private void ApplyLegalProfileToControls()
    {
        if (_applyingProfile) return;
        _applyingProfile = true;
        try
        {
            var situation = SituationBox.SelectedValue is AdjustmentSituationKind selected
                ? selected
                : AdjustmentSituationKind.NotInformed;
            var profileDate = StartDatePicker.SelectedDate
                              ?? (MonthlyAdjustmentOnlyCheck.IsChecked == true && EndDatePicker.SelectedDate is { } end
                                  ? new DateTime(end.Year, end.Month, 1)
                                  : null);
            var profile = MilitaryRemunerationCatalog.Resolve(HistoricalRankBox.Text, situation, profileDate);
            var testMode = TestModeCheck.IsChecked == true || _draft.IsTestMode;
            if (!testMode)
            {
                if (!_draft.MilitaryAdditionalPercentOverridden) _draft.MilitaryAdditionalPercent = profile.MilitaryAdditionalPercent;
                if (!_draft.AvailabilityPercentOverridden) _draft.AvailabilityPercent = profile.AvailabilityPercent;
                if (!_draft.FusexPercentOverridden) _draft.FusexPercent = profile.FusexPercent;
                if (!_draft.MilitaryPensionPercentOverridden) _draft.MilitaryPensionPercent = profile.MilitaryPensionPercent;
            }
            MilitaryAdditionalValueText.Text = OverrideText(_draft.MilitaryAdditionalPercent, _draft.MilitaryAdditionalPercentOverridden);
            AvailabilityValueText.Text = OverrideText(_draft.AvailabilityPercent, _draft.AvailabilityPercentOverridden);
            FusexValueText.Text = _draft.FusexPercent == 0m ? "Isento" : OverrideText(_draft.FusexPercent, _draft.FusexPercentOverridden);
            MilitaryPensionValueText.Text = OverrideText(_draft.MilitaryPensionPercent, _draft.MilitaryPensionPercentOverridden);
            MinimumWageValueText.Text = profile.ReceivesMinimumWageComplement ? "Automático (NR0026)" : "Não se aplica";
            LegalProfileText.Text = profile.Explanation + " Base: " + MilitaryRemunerationCatalog.LegalBasis + ".";
            RenderSituationRights(profile);
            QualificationRuleText.Text = profile.Explanation;

            var options = testMode ? QualificationCatalog.All : MilitaryRemunerationCatalog.QualificationOptions(profile);
            var current = QualificationBox.SelectedItem as QualificationOption
                          ?? QualificationCatalog.Resolve(_draft.QualificationCode);
            QualificationBox.ItemsSource = options;
            QualificationBox.SelectedItem = options.FirstOrDefault(x => x.Code == current.Code) ?? options[0];

            var locked = profile.IsQualificationFixed && !testMode;
            QualificationBox.IsEnabled = !locked;
            QualificationDatePicker.IsEnabled = !locked;
            if (!testMode && profile.QualificationMode == QualificationSelectionMode.NotDue)
            {
                QualificationBox.SelectedItem = options[0];
                QualificationDatePicker.SelectedDate = null;
            }
            else if (!testMode && profile.QualificationMode == QualificationSelectionMode.FixedFormation)
            {
                QualificationBox.SelectedItem = options[0];
                QualificationDatePicker.SelectedDate = profileDate;
            }
            var fusexEnabled = profile.FusexPercent > 0m;
            FusexDependentBox.IsEnabled = fusexEnabled;
            FusexMedicalBox.IsEnabled = fusexEnabled;
            if (!fusexEnabled)
            {
                FusexDependentBox.Text = "0";
                FusexMedicalBox.Text = "0";
            }
            UpdateQualificationPercentPreview();
        }
        finally
        {
            _applyingProfile = false;
        }
    }

    private void UpdateQualificationPercentPreview()
    {
        if (TestModeCheck.IsChecked == true && AdjustmentNumericParser.TryParseDecimal(TestQualificationPercentBox.Text, out var testPercent))
        {
            QualificationPercentValueText.Text = FormatPercent(testPercent) + " (teste)";
            return;
        }
        var option = QualificationBox.SelectedItem as QualificationOption;
        QualificationPercentValueText.Text = option is null || option.IsNone ? "Não recebe" : FormatPercent(option.Percentage);
    }

    private void ContextDependentsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        var dependents = int.TryParse(IncomeTaxDependentsBox.Text.Trim(), out var parsed) ? Math.Max(0, parsed) : 0;
        _draft.IncomeTaxDependents = dependents;
        _draft.FamilySalaryDependents = dependents;
        _draft.FamilySalaryValue = FamilySalaryQuotaCatalog.Resolve(dependents);
        Set(FamilySalaryBox, _draft.FamilySalaryValue);
        ScheduleRefresh();
    }

    private void PreSchoolEntitlementChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _applyingProfile) return;
        ApplyCurrentPreSchoolReference();
        ScheduleRefresh();
    }

    private void ApplyCurrentPreSchoolReference()
    {
        var include = IncludePreSchoolCheck.IsChecked == true;
        var rank = !string.IsNullOrWhiteSpace(HistoricalRankBox.Text)
            ? HistoricalRankBox.Text
            : (MilitaryBox.SelectedItem as MilitaryRecord)?.Rank ?? string.Empty;
        var referenceDate = EndDatePicker.SelectedDate ?? DateTime.Today;
        var reference = MilitaryPreSchoolCatalog.Resolve(rank, referenceDate);
        _draft.PreSchoolValue = include ? reference.NetValue : 0m;
        Set(PreSchoolBox, _draft.PreSchoolValue);
        PreSchoolBox.ToolTip = include
            ? $"{reference.Category}: teto {AdjustmentAccountsService.FormatMoney(reference.Ceiling)}, cota-parte {reference.MilitarySharePercent:0.##}% e valor líquido {AdjustmentAccountsService.FormatMoney(reference.NetValue)}."
            : $"Marque para aplicar automaticamente o valor de {reference.Category}: {AdjustmentAccountsService.FormatMoney(reference.NetValue)}.";
    }

    private void EditPercentage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string key }) return;
        var profile = CurrentLegalProfile();
        var (label, current, usual) = key switch
        {
            "MilitaryAdditional" => ("Adicional Militar", _draft.MilitaryAdditionalPercent, profile.MilitaryAdditionalPercent),
            "Availability" => ("Adicional de Disponibilidade", _draft.AvailabilityPercent, profile.AvailabilityPercent),
            "Fusex" => ("FUSEx", _draft.FusexPercent, profile.FusexPercent),
            "MilitaryPension" => ("Pensão Militar", _draft.MilitaryPensionPercent, profile.MilitaryPensionPercent),
            _ => (string.Empty, 0m, 0m)
        };
        if (string.IsNullOrWhiteSpace(label)) return;

        var prompt = new TextPromptWindow("Percentual excepcional",
            $"Informe o percentual de {label}. O perfil usual para a situação selecionada é {FormatPercent(usual)}. Alterações ficam registradas com aviso na validação.",
            current.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR"))) { Owner = this };
        if (prompt.ShowDialog() != true) return;
        if (!AdjustmentNumericParser.TryParseDecimal(prompt.Value, out var informed) || informed is < 0m or > 100m)
        {
            SigfurDialog.Show(this, "Informe um percentual entre 0% e 100%.", "Percentual inválido", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (informed != usual && SigfurDialog.Show(this,
                $"O percentual informado ({FormatPercent(informed)}) é diferente do valor usual do perfil ({FormatPercent(usual)}).\n\nConfirme somente quando existir fundamento para o ajuste excepcional.",
                "Confirmar percentual excepcional", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        SetPercentageOverride(key, informed, informed != usual);
        ApplyLegalProfileToControls();
        ScheduleRefresh();
    }

    private void ResetPercentage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string key }) return;
        var profile = CurrentLegalProfile();
        var usual = key switch
        {
            "MilitaryAdditional" => profile.MilitaryAdditionalPercent,
            "Availability" => profile.AvailabilityPercent,
            "Fusex" => profile.FusexPercent,
            "MilitaryPension" => profile.MilitaryPensionPercent,
            _ => 0m
        };
        SetPercentageOverride(key, usual, false);
        ApplyLegalProfileToControls();
        ScheduleRefresh();
    }

    private void SetPercentageOverride(string key, decimal value, bool overridden)
    {
        switch (key)
        {
            case "MilitaryAdditional": _draft.MilitaryAdditionalPercent = value; _draft.MilitaryAdditionalPercentOverridden = overridden; break;
            case "Availability": _draft.AvailabilityPercent = value; _draft.AvailabilityPercentOverridden = overridden; break;
            case "Fusex": _draft.FusexPercent = value; _draft.FusexPercentOverridden = overridden; break;
            case "MilitaryPension": _draft.MilitaryPensionPercent = value; _draft.MilitaryPensionPercentOverridden = overridden; break;
        }
    }

    private MilitaryRemunerationProfile CurrentLegalProfile()
    {
        var situation = SituationBox.SelectedValue is AdjustmentSituationKind selected ? selected : AdjustmentSituationKind.NotInformed;
        var profileDate = StartDatePicker.SelectedDate
                          ?? (MonthlyAdjustmentOnlyCheck.IsChecked == true && EndDatePicker.SelectedDate is { } end
                              ? new DateTime(end.Year, end.Month, 1)
                              : null);
        return MilitaryRemunerationCatalog.Resolve(HistoricalRankBox.Text, situation, profileDate);
    }

    private void RenderSituationRights(MilitaryRemunerationProfile profile)
    {
        var qualification = profile.QualificationMode switch
        {
            QualificationSelectionMode.NotDue => ("Não se aplica", false),
            QualificationSelectionMode.FixedFormation => ("Formação", true),
            QualificationSelectionMode.FormationAfterStage => ("Formação após o estágio", true),
            _ => ("Conforme o curso informado", true)
        };

        var rights = new (string Label, string Value, bool Applies)[]
        {
            ("Adicional militar", profile.MilitaryAdditionalPercent > 0m ? FormatPercent(profile.MilitaryAdditionalPercent) : "Não se aplica", profile.MilitaryAdditionalPercent > 0m),
            ("Disponibilidade militar", profile.AvailabilityPercent > 0m ? FormatPercent(profile.AvailabilityPercent) : "Não se aplica", profile.AvailabilityPercent > 0m),
            ("Adicional de habilitação", qualification.Item1, qualification.Item2),
            ("Assistência FUSEx", profile.FusexPercent > 0m ? FormatPercent(profile.FusexPercent) : "Isento", profile.FusexPercent > 0m),
            ("Pensão militar", profile.MilitaryPensionPercent > 0m ? FormatPercent(profile.MilitaryPensionPercent) : "Não se aplica", profile.MilitaryPensionPercent > 0m),
            ("Complemento do salário mínimo", profile.ReceivesMinimumWageComplement ? "Faz jus — automático" : "Não se aplica", profile.ReceivesMinimumWageComplement)
        };

        SituationRightsPanel.Children.Clear();
        foreach (var right in rights)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 5)
            };
            card.SetResourceReference(Border.BackgroundProperty, right.Applies ? "SuccessSoftBrush" : "SurfaceAltBrush");
            card.SetResourceReference(Border.BorderBrushProperty, right.Applies ? "SuccessBrush" : "BorderBrush");

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var glyph = new TextBlock
            {
                Text = right.Applies ? "✓" : "—",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, right.Applies ? "SuccessBrush" : "MutedBrush");
            row.Children.Add(glyph);
            var label = new TextBlock
            {
                Text = right.Label,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 0, 8, 0)
            };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            var value = new TextBlock
            {
                Text = right.Value,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 155
            };
            value.SetResourceReference(TextBlock.ForegroundProperty, right.Applies ? "SuccessBrush" : "MutedBrush");
            Grid.SetColumn(value, 2);
            row.Children.Add(value);
            card.Child = row;
            SituationRightsPanel.Children.Add(card);
        }
    }

    private static string OverrideText(decimal value, bool overridden)
        => FormatPercent(value) + (overridden ? " • excepcional" : string.Empty);

    private void ScheduleRefresh()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private async void Simulate_Click(object sender, RoutedEventArgs e)
    {
        _refreshTimer.Stop();
        await RefreshSimulationAsync(showInputErrors: true);
        MainTabs.SelectedIndex = _result?.HasBlockingErrors == true ? 3 : 2;
    }

    private async Task RefreshSimulationAsync(bool showInputErrors)
    {
        if (_loading) return;
        var version = ++_refreshVersion;
        var inputErrors = ReadControlsIntoDraft();
        try
        {
            var remuneration = await AdjustmentRemunerationResolver.ResolveAsync(_draft, App.Salaries);
            if (version != _refreshVersion) return;
            _draft.RemunerationTableVersion = remuneration.TableVersion;
            var result = AdjustmentCalculationEngine.Simulate(_draft, remuneration);
            if (inputErrors.Count > 0)
                result = WithInputErrors(result, inputErrors);
            _result = result;
            ApplyResultToView(result);
            if (showInputErrors && inputErrors.Count > 0)
                SigfurDialog.Show(this, "Corrija os campos numéricos destacados pelas validações antes de gerar o boletim.",
                    "Dados inválidos", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Não foi possível atualizar a simulação: " + ex.Message;
            GenerateBulletinButton.IsEnabled = false;
        }
    }

    private List<AdjustmentValidationMessage> ReadControlsIntoDraft()
    {
        var inputErrors = new List<AdjustmentValidationMessage>();
        _draft.IsManualSimulation = ManualSimulationCheck.IsChecked == true;
        _draft.IsTestMode = TestModeCheck.IsChecked == true;
        if (!_draft.IsManualSimulation && MilitaryBox.SelectedItem is MilitaryRecord military)
        {
            _draft.MilitaryId = military.Id;
            _draft.MilitaryName = military.Name;
            _draft.CurrentRank = military.Rank;
            _draft.EnlistmentDate = military.EnlistmentDateValue;
        }
        else
        {
            _draft.MilitaryId = 0;
            _draft.MilitaryName = string.Empty;
            _draft.CurrentRank = string.Empty;
        }
        _draft.HistoricalRank = HistoricalRankBox.Text.Trim();
        _draft.EntitlementStart = StartDatePicker.SelectedDate?.Date;
        _draft.EntitlementEnd = EndDatePicker.SelectedDate?.Date;
        _draft.MonthlyAdjustmentOnly = MonthlyAdjustmentOnlyCheck.IsChecked == true;
        _draft.VacationQuotaStart = VacationQuotaStartPicker.SelectedDate?.Date;
        _draft.VacationQuotaEnd = VacationQuotaEndPicker.SelectedDate?.Date;
        _draft.VacationQuotasOverride = ReadNullableInt(VacationQuotasOverrideBox, "Cotas de férias", "VacationQuotasOverride", inputErrors);
        _draft.Situation = SituationBox.SelectedValue is AdjustmentSituationKind situation ? situation : AdjustmentSituationKind.NotInformed;
        _draft.AdjustmentReason = ReasonBox.Text.Trim();
        _draft.QualificationCode = (QualificationBox.SelectedItem as QualificationOption)?.Code ?? QualificationCatalog.NoneCode;
        _draft.QualificationEffectiveDate = QualificationDatePicker.SelectedDate?.Date;
        _draft.QualificationDocumentConfirmed = false;
        _draft.VacationIndemnityEntitlement = VacationIndemnityCheck.IsChecked == true;
        _draft.VacationAdditionalEntitlement = VacationAdditionalCheck.IsChecked == true;
        _draft.ChristmasEntitlement = ChristmasCheck.IsChecked == true;
        _draft.ReceivedChristmasFirstInstallment = ChristmasFirstInstallmentCheck.IsChecked == true;
        _draft.PecuniaryEntitlement = PecuniaryCheck.IsChecked == true;
        _draft.PecuniaryQuotas = ReadInt(PecuniaryQuotasBox, "Compensações pecuniárias", "PecuniaryQuotas", inputErrors);
        _draft.ChristmasQuotas = ReadInt(ChristmasQuotasBox, "Cotas de 13º", "ChristmasQuotas", inputErrors);
        _draft.PermanencePercent = PermanenceBox.SelectedValue is decimal permanence ? permanence : 0m;
        _draft.PreSchoolValue = ReadDecimal(PreSchoolBox, "Pré-escolar", "PreSchoolValue", inputErrors);
        _draft.IncomeTaxDependents = ReadInt(IncomeTaxDependentsBox, "Dependentes selecionados", "IncomeTaxDependents", inputErrors);
        _draft.FamilySalaryDependents = _draft.IncomeTaxDependents;
        Set(FamilySalaryBox, FamilySalaryQuotaCatalog.Resolve(_draft.FamilySalaryDependents));
        _draft.FamilySalaryValue = ReadDecimal(FamilySalaryBox, "Salário-família", "FamilySalaryValue", inputErrors);
        _draft.PnrPercent = 0m;
        _draft.FusexDependentDiscount = FusexDependentBox.IsEnabled
            ? ReadDecimal(FusexDependentBox, "FUSEx dependente", "FusexDependentDiscount", inputErrors)
            : 0m;
        _draft.FusexMedicalExpense = FusexMedicalBox.IsEnabled
            ? ReadDecimal(FusexMedicalBox, "Despesa médica FUSEx", "FusexMedicalExpense", inputErrors)
            : 0m;
        _draft.AlimonyValue = ReadDecimal(AlimonyBox, "Pensão alimentícia", "AlimonyValue", inputErrors);
        _draft.IncludeMonthlyIncomeTax = IncludeMonthlyIncomeTaxCheck.IsChecked == true;
        _draft.ApplyIncomeTaxReducer2026 = ApplyIncomeTaxReducerCheck.IsChecked == true;
        _draft.DeductFusexMedicalExpense = DeductFusexMedicalCheck.IsChecked == true;
        _draft.DeductFusexDependent = DeductFusexDependentCheck.IsChecked == true;
        _draft.DeductAlimony = DeductAlimonyCheck.IsChecked == true;
        _draft.DeductPnr = DeductPnrCheck.IsChecked == true;
        if (_draft.IsTestMode)
        {
            _draft.TestSalaryOverride = ReadNullableDecimal(TestSalaryBox, "Soldo de teste", "TestSalaryOverride", inputErrors);
            _draft.TestQualificationPercentOverride = ReadNullableDecimal(TestQualificationPercentBox, "Habilitação de teste", "TestQualificationPercentOverride", inputErrors);
            _draft.MilitaryAdditionalPercent = ReadDecimal(TestMilitaryAdditionalPercentBox, "Adicional militar de teste", "MilitaryAdditionalPercent", inputErrors);
            _draft.AvailabilityPercent = ReadDecimal(TestAvailabilityPercentBox, "Disponibilidade de teste", "AvailabilityPercent", inputErrors);
            _draft.PermanencePercent = ReadDecimal(TestPermanencePercentBox, "Permanência de teste", "PermanencePercent", inputErrors);
            _draft.FusexPercent = ReadDecimal(TestFusexPercentBox, "FUSEx de teste", "FusexPercent", inputErrors);
            _draft.MilitaryPensionPercent = ReadDecimal(TestMilitaryPensionPercentBox, "Pensão militar de teste", "MilitaryPensionPercent", inputErrors);
            _draft.PnrPercent = ReadDecimal(TestPnrPercentBox, "PNR de teste", "PnrPercent", inputErrors);
            _draft.TestDaysInMonthOverride = ReadNullableInt(TestDaysInMonthBox, "Dias do mês de teste", "TestDaysInMonthOverride", inputErrors);
            _draft.TestServedDaysOverride = ReadNullableInt(TestServedDaysBox, "Dias remunerados de teste", "TestServedDaysOverride", inputErrors);
        }
        else
        {
            _draft.TestSalaryOverride = null;
            _draft.TestQualificationPercentOverride = null;
            _draft.TestDaysInMonthOverride = null;
            _draft.TestServedDaysOverride = null;
        }
        // Reaplica o catálogo depois de ler P/Grad, situação e data. Assim, nem um
        // rascunho antigo nem uma alteração de vigência consegue conservar percentuais livres.
        if (!_draft.IsTestMode) MilitaryRemunerationCatalog.ApplyFixedPercentages(_draft);
        _draft.UpdatedAtUtc = DateTime.UtcNow;
        return inputErrors;
    }

    private static AdjustmentSimulationResult WithInputErrors(AdjustmentSimulationResult result, IEnumerable<AdjustmentValidationMessage> inputErrors)
        => new(result.Draft, result.Remuneration, result.QuotaDetails, result.Components, result.SippesParameters,
            result.Validations.Concat(inputErrors), result.CalculationMemory);

    private void ApplyResultToView(AdjustmentSimulationResult result)
    {
        ComponentsGrid.ItemsSource = result.Components;
        QuotaGrid.ItemsSource = result.QuotaDetails;
        SippesGrid.ItemsSource = result.SippesParameters;
        ValidationGrid.ItemsSource = result.Validations.OrderByDescending(x => x.Severity);
        CalculationMemoryBox.Text = result.MemoryText;
        EarningsText.Text = AdjustmentAccountsService.FormatMoney(result.Earnings);
        DiscountsText.Text = AdjustmentAccountsService.FormatMoney(result.Discounts);
        NetText.Text = AdjustmentAccountsService.FormatMoney(result.Net);
        TableVersionText.Text = result.Remuneration.EffectivePeriod;
        QuotaCountText.Text = result.VacationQuotas.ToString(CultureInfo.InvariantCulture);
        var errors = result.Validations.Count(x => x.Severity == AdjustmentValidationSeverity.BlockingError);
        var warnings = result.Validations.Count(x => x.Severity == AdjustmentValidationSeverity.Warning);
        ErrorCountText.Text = errors.ToString(CultureInfo.InvariantCulture);
        WarningCountText.Text = warnings.ToString(CultureInfo.InvariantCulture);
        GenerateBulletinButton.IsEnabled = result.CanGenerateBulletin;
        SafetyStatusText.Text = errors > 0
            ? $"Geração bloqueada: {errors} erro(s) precisam ser corrigidos."
            : result.Draft.IsTestMode
                ? "Modo de teste ativo. Valores livres calculados; geração de boletim bloqueada."
            : result.Draft.IsManualSimulation
                ? "Simulação sem militar concluída. O cálculo pode ser conferido, mas não gera boletim."
            : warnings > 0
                ? $"Simulação concluída com {warnings} aviso(s). Confira antes de gerar."
                : "Simulação consistente. Confira a memória e os parâmetros SIPPES.";

        var qualification = result.Components.FirstOrDefault(x => x.RubricCode == "AR0003");
        var militaryAdditional = result.Components.FirstOrDefault(x => x.RubricCode == "AR0014");
        var availability = result.Components.FirstOrDefault(x => x.RubricCode == "AR0170");
        var fusex = result.Components.FirstOrDefault(x => x.RubricCode == "AD0001");
        var pension = result.Components.FirstOrDefault(x => x.RubricCode == "AD0039");
        var minimumComplement = result.Components.FirstOrDefault(x => x.RubricCode == "NR0026");
        MilitaryAdditionalValueText.Text = ComponentSummary(militaryAdditional, result.Draft.MilitaryAdditionalPercent) + (result.Draft.MilitaryAdditionalPercentOverridden ? " • excepcional" : string.Empty);
        AvailabilityValueText.Text = ComponentSummary(availability, result.Draft.AvailabilityPercent) + (result.Draft.AvailabilityPercentOverridden ? " • excepcional" : string.Empty);
        QualificationPercentValueText.Text = qualification is null || qualification.Percentage == 0m
            ? "Não recebe"
            : ComponentSummary(qualification, qualification.Percentage);
        FusexValueText.Text = result.Draft.FusexPercent == 0m ? "Isento" : ComponentSummary(fusex, result.Draft.FusexPercent) + (result.Draft.FusexPercentOverridden ? " • excepcional" : string.Empty);
        MilitaryPensionValueText.Text = ComponentSummary(pension, result.Draft.MilitaryPensionPercent) + (result.Draft.MilitaryPensionPercentOverridden ? " • excepcional" : string.Empty);
        MinimumWageValueText.Text = minimumComplement is null ? "Não se aplica" : minimumComplement.UnitValueText;
        QualificationStatusText.Text = qualification?.EligibilityReason ?? "Não possui";
        QualificationBasisText.Text = qualification is null
            ? "Base: —"
            : $"Base: {qualification.CalculationBaseText} • Percentual: {qualification.PercentageText}";
        QualificationValueText.Text = qualification?.UnitValueText ?? AdjustmentAccountsService.FormatMoney(0m);
        StatusText.Text = $"Simulação atualizada às {DateTime.Now:HH:mm:ss}. Nenhum documento foi gerado.";
    }

    private async void SaveDraft_Click(object sender, RoutedEventArgs e)
    {
        await RefreshSimulationAsync(showInputErrors: true);
        await SaveDraftAsync("RASCUNHO_SALVO");
        StatusText.Text = "Rascunho e trilha de auditoria salvos no armazenamento do perfil.";
    }

    private async Task SaveDraftAsync(string action, string generatedDocument = "")
    {
        var before = _store.CurrentDraft is null ? string.Empty : JsonSerializer.Serialize(_store.CurrentDraft);
        _store.CurrentDraft = _draft.Clone();
        var existing = _store.Drafts.FindIndex(x => x.Id == _draft.Id);
        if (existing >= 0) _store.Drafts[existing] = _draft.Clone();
        else _store.Drafts.Add(_draft.Clone());
        _store.AuditTrail.Add(new AdjustmentAuditEntry
        {
            Action = action,
            AdjustmentId = _draft.Id,
            MilitaryId = _draft.MilitaryId,
            MilitaryName = _draft.MilitaryName,
            HistoricalRank = _draft.HistoricalRank,
            BeforeJson = before,
            AfterJson = JsonSerializer.Serialize(_draft),
            RemunerationTableVersion = _result?.Remuneration.TableVersion ?? _draft.RemunerationTableVersion,
            Alerts = _result?.Validations.Select(x => $"{x.SeverityText}: {x.Message}").ToList() ?? [],
            GeneratedDocument = generatedDocument
        });
        if (_store.AuditTrail.Count > 500)
            _store.AuditTrail.RemoveRange(0, _store.AuditTrail.Count - 500);

        _store.Settings.LastMilitaryId = _draft.MilitaryId;
        _store.Settings.Rank = _draft.HistoricalRank;
        _store.Settings.QualificationPercent = 0m;
        _store.Settings.MilitaryAdditionalPercent = _draft.MilitaryAdditionalPercent;
        _store.Settings.MilitaryAvailabilityPercent = _draft.AvailabilityPercent;
        _store.Settings.PermanencePercent = _draft.PermanencePercent;
        _store.Settings.VacationMonths = _result?.VacationQuotas ?? 0;
        _store.Settings.ChristmasMonths = Math.Clamp(_draft.ChristmasQuotas, 0, 12);
        _store.Settings.VacationIndemnityEntitlement = _draft.VacationIndemnityEntitlement;
        _store.Settings.VacationAdditionalEntitlement = _draft.VacationAdditionalEntitlement;
        _store.Settings.ChristmasEntitlement = _draft.ChristmasEntitlement;
        _store.Settings.ReceivedChristmasFirstInstallment = _draft.ReceivedChristmasFirstInstallment;
        _store.Settings.PecuniaryEntitlement = _draft.PecuniaryEntitlement;
        _store.Settings.PecuniaryQuotas = _draft.PecuniaryQuotas;
        _store.Settings.IncomeTaxDependents = _draft.IncomeTaxDependents;
        _store.Settings.IncludeMonthlyIncomeTax = _draft.IncludeMonthlyIncomeTax;
        _store.Settings.ApplyIncomeTaxReducer2026 = _draft.ApplyIncomeTaxReducer2026;
        _store.Settings.DeductFusexMedicalExpense = _draft.DeductFusexMedicalExpense;
        _store.Settings.DeductFusexDependent = _draft.DeductFusexDependent;
        _store.Settings.DeductAlimony = _draft.DeductAlimony;
        _store.Settings.DeductPnr = _draft.DeductPnr;
        _store.Settings.BulletinReason = _draft.AdjustmentReason;
        _store.SchemaVersion = AdjustmentAccountsMigrationService.CurrentSchemaVersion;
        await _json.SaveAsync(_paths.AdjustmentAccountsSettingsFile, _store);
    }

    private async void GenerateBulletin_Click(object sender, RoutedEventArgs e)
    {
        await RefreshSimulationAsync(showInputErrors: true);
        if (_result is null || _result.HasBlockingErrors)
        {
            MainTabs.SelectedIndex = 3;
            SigfurDialog.Show(this, "O boletim não pode ser gerado enquanto houver erro bloqueante.",
                "Geração bloqueada", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MilitaryBox.SelectedItem is not MilitaryRecord military) return;

        var review = new AdjustmentFinalReviewWindow(_result) { Owner = this };
        if (review.ShowDialog() != true) return;

        await SaveDraftAsync("CONFERENCIA_FINAL_CONFIRMADA");
        var bulletin = new AdjustmentBulletinWindow(_store.Settings.Clone(), _result, military) { Owner = this };
        bulletin.ShowDialog();
        _store.Settings = bulletin.Settings;
        if (bulletin.DocumentBuilt)
            await SaveDraftAsync("DOCUMENTO_GERADO", bulletin.GeneratedDocumentPath);
        StatusText.Text = bulletin.DocumentBuilt
            ? "Boletim gerado a partir do mesmo resultado imutável exibido na simulação."
            : "Geração cancelada; o rascunho permanece salvo.";
    }

    private static decimal ReadDecimal(TextBox box, string label, string field, ICollection<AdjustmentValidationMessage> errors)
    {
        var cleaned = (box.Text ?? string.Empty).Trim().Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        if (AdjustmentNumericParser.TryParseDecimal(cleaned, out var value))
            return value;
        errors.Add(new AdjustmentValidationMessage(AdjustmentValidationSeverity.BlockingError, "INVALID_NUMBER", $"{label}: informe um número válido; NaN, infinito e texto não são aceitos.", field));
        return 0m;
    }

    private static int ReadInt(TextBox box, string label, string field, ICollection<AdjustmentValidationMessage> errors)
    {
        if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
        errors.Add(new AdjustmentValidationMessage(AdjustmentValidationSeverity.BlockingError, "INVALID_INTEGER", $"{label}: informe uma quantidade inteira válida.", field));
        return 0;
    }

    private static int? ReadNullableInt(TextBox box, string label, string field, ICollection<AdjustmentValidationMessage> errors)
    {
        var text = box.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
        errors.Add(new AdjustmentValidationMessage(AdjustmentValidationSeverity.BlockingError, "INVALID_INTEGER", $"{label}: informe uma quantidade inteira válida ou deixe o campo vazio.", field));
        return null;
    }

    private static decimal? ReadNullableDecimal(TextBox box, string label, string field, ICollection<AdjustmentValidationMessage> errors)
    {
        var text = box.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (AdjustmentNumericParser.TryParseDecimal(text, out var value)) return value;
        errors.Add(new AdjustmentValidationMessage(AdjustmentValidationSeverity.BlockingError, "INVALID_NUMBER", $"{label}: informe um número válido ou deixe o campo vazio.", field));
        return null;
    }

    private static void Set(TextBox box, decimal value) => box.Text = value.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR"));
    private static void Set(TextBox box, int value) => box.Text = value.ToString(CultureInfo.InvariantCulture);

    private static decimal NormalizePermanence(decimal value)
        => Math.Clamp(Math.Round(value / 5m, MidpointRounding.AwayFromZero) * 5m, 0m, 25m);

    private static string FormatPercent(decimal value)
        => $"{value.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR"))}%";

    private static string ComponentSummary(AdjustmentComponentResult? component, decimal percentage)
        => component is null
            ? FormatPercent(percentage)
            : $"{FormatPercent(percentage)} · {component.UnitValueText}";

    private sealed record SituationOption(AdjustmentSituationKind Kind, string Label);
    private sealed record PercentageChoice(decimal Value, string Label);
}
