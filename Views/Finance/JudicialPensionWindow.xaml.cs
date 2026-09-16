using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Finance;

public partial class JudicialPensionWindow : Window
{
    private readonly JudicialPensionService _service;
    private readonly PaystubAuditService _paystubAudit;
    private readonly ObservableCollection<JudicialPensionExtra> _extras = [];
    private List<MilitaryRecord> _military = [];
    private JudicialPensionSettings _settings = new();
    private JudicialPensionCalculationResult _result = new();
    private decimal _databaseSalary;
    private decimal _paystubSalary;
    private int? _paystubMilitaryId;
    private bool _suppress;
    private readonly Dictionary<Control, (Brush BorderBrush, Thickness BorderThickness)> _requiredVisualState = new();
    private string _bulletinPreviewText = string.Empty;

    public JudicialPensionWindow(JudicialPensionService service)
    {
        _service = service;
        _paystubAudit = new PaystubAuditService(App.Paystubs, App.PdfText, App.Log);
        InitializeComponent();
        App.UiState.Attach(this);
        ExtrasGrid.ItemsSource = _extras;
        Loaded += OnLoaded;
        Closing += async (_, _) => await SaveSettingsQuietlyAsync();
    }

    private MilitaryRecord? SelectedMilitary => MilitaryBox.SelectedItem as MilitaryRecord;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "Carregando militares e parâmetros...");
            _suppress = true;
            _settings = await _service.LoadSettingsAsync();
            _military = await App.MilitaryRepository.GetAllAsync();
            MilitaryBox.ItemsSource = _military.OrderBy(x => MilitaryRankService.GetOrder(x.Rank)).ThenBy(x => x.Name).ToList();
            RankBox.ItemsSource = await App.MilitaryRepository.GetRanksAsync();
            ConfigurePercentageSelectors();
            ApplySettings(_settings);
            _suppress = false;
            if (SelectedMilitary is { } selectedMilitary)
                await LoadLatestPaystubAsync(selectedMilitary);
            await UpdateSalaryAsync();
            Recalculate();
            await ReloadHistoryAsync();
            StatusText.Text = "Pensão Judicial pronta para cálculo.";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _suppress = false; SetBusy(false); }
    }

    private void ApplySettings(JudicialPensionSettings s)
    {
        MilitaryBox.SelectedItem = s.MilitaryId is int id ? _military.FirstOrDefault(x => x.Id == id) : null;
        RankBox.Text = !string.IsNullOrWhiteSpace(s.Rank) ? s.Rank : SelectedMilitary?.Rank ?? string.Empty;
        ManualSalaryBox.IsChecked = s.UseManualSalary;
        ManualSalaryText.Text = MoneyInput(s.ManualSalary);
        SetPercent(QualificationText, s.QualificationPercent, "salvo");
        SetPercent(MilitaryAdditionalText, s.MilitaryAdditionalPercent, "salvo");
        SetPercent(AvailabilityText, s.AvailabilityPercent, "salvo");
        SetPercent(PermanenceText, s.PermanencePercent, "salvo");
        PreSchoolText.Text = MoneyInput(s.PreSchoolValue);
        FamilySalaryText.Text = MoneyInput(s.FamilySalaryValue);
        SetPercent(FusexText, s.FusexPercent, "salvo");
        SetPercent(Pension105Text, s.MilitaryPension105Percent, "salvo");
        SetPercent(Pension15Text, s.MilitaryPension15Percent, "salvo");
        FusexDependentText.Text = MoneyInput(s.FusexDependentDiscount);
        FusexMedicalText.Text = MoneyInput(s.FusexMedicalExpense);
        SetPercent(PnrText, s.PnrPercent, "salvo");
        ExistingAlimonyText.Text = MoneyInput(s.ExistingAlimony);
        OtherAlimonyText.Text = MoneyInput(s.OtherAlimony);
        AutomaticIrBox.IsChecked = s.AutomaticIncomeTax;
        ManualIrBox.IsChecked = !s.AutomaticIncomeTax;
        ManualIrText.Text = MoneyInput(s.ManualIncomeTax);
        DependentsText.Text = s.IncomeTaxDependents.ToString(CultureInfo.InvariantCulture);
        DeductMedicalBox.IsChecked = s.DeductMedicalFromIncomeTax;
        DeductDependentFusexBox.IsChecked = s.DeductFusexDependentFromIncomeTax;
        DeductAlimonyBox.IsChecked = s.DeductAlimonyFromIncomeTax;
        DeductPnrBox.IsChecked = s.DeductPnrFromIncomeTax;
        IrReducerBox.IsChecked = s.ApplyIncomeTaxReducer2026;
        PercentagePensionBox.IsChecked = !s.FixedPension;
        FixedPensionBox.IsChecked = s.FixedPension;
        PensionBaseBox.SelectedIndex = s.PensionBase switch { "RECEITA" => 1, "SOLDO" => 2, _ => 0 };
        PensionPercentText.Text = NumberInput(s.PensionPercent);
        FixedPensionText.Text = MoneyInput(s.FixedPensionValue);
        _extras.Clear();
        foreach (var extra in s.Extras ?? []) _extras.Add(extra);
        ApplyBulletin(s.Bulletin ?? new JudicialPensionBulletin());
    }

    private void ApplyBulletin(JudicialPensionBulletin b)
    {
        BulletinSubjectBox.Text = string.IsNullOrWhiteSpace(b.Subject) ? "PENSÃO JUDICIAL" : b.Subject;
        BulletinModeBox.SelectedIndex = NormalizeBulletinMode(b.Mode) switch
        {
            "ATUALIZAÇÃO" => 1,
            "DESCONTO DE ATRASADOS" => 2,
            "EXCLUSÃO" => 3,
            _ => 0
        };
        ProcessNumberBox.Text = b.ProcessNumber;
        DecisionTypeBox.Text = b.DecisionType;
        DecisionOriginBox.Text = b.DecisionOrigin;
        DecisionLocalityBox.Text = b.Locality;
        CourtBox.Text = b.Court;
        ActionNatureBox.Text = b.ActionNature;
        DecisionDateBox.Text = b.DecisionDate;
        DecisionSummaryBox.Text = b.DecisionSummary;
        PlaintiffNameBox.Text = b.PlaintiffName;
        PlaintiffCpfBox.Text = b.PlaintiffCpf;
        ReceivingLetterBox.Text = b.ReceivingLetter;
        ReceivingLetterOriginBox.Text = b.ReceivingLetterOrigin;
        ReceivingLetterDateBox.Text = b.ReceivingLetterDate;
        BeneficiaryNameBox.Text = b.BeneficiaryName;
        BeneficiaryCpfBox.Text = b.BeneficiaryCpf;
        GuardianNameBox.Text = b.GuardianName;
        GuardianCpfBox.Text = b.GuardianCpf;
        RelationshipBox.Text = b.Relationship;
        PensionTypeBox.Text = b.PensionType;
        CalculationRuleBox.Text = b.CalculationRule;
        VacationIncidenceBox.Text = b.VacationIncidence;
        ChristmasIncidenceBox.Text = b.ChristmasIncidence;
        CompensationIncidenceBox.Text = b.CompensationIncidence;
        PreviousPensionsBox.Text = b.PreviousPensions;
        BankCodeBox.Text = b.BankCode;
        BankNameBox.Text = b.BankName;
        AgencyBox.Text = b.Agency;
        AccountBox.Text = b.Account;
        BankOperationBox.Text = b.BankOperation;
        RubricBox.Text = b.Rubric;
        ReferenceMonthBox.Text = b.ReferenceMonth;
        ReferenceYearBox.Text = b.ReferenceYear;
        ArrearsTotalValueBox.Text = b.ArrearsTotalValue;
        ExclusionReasonBox.Text = b.ExclusionReason;
        ExclusionJustificationBox.Text = b.ExclusionJustification;
        PensionEndDateBox.Text = b.PensionEndDate;
        LegalBasisBox.Text = b.LegalBasis;
        RepresentationBox.Text = b.Representation;
        BulletinObservationBox.Text = b.Observation;
    }

    private async void MilitaryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (SelectedMilitary is MilitaryRecord military)
        {
            _suppress = true;
            RankBox.Text = military.Rank;
            _suppress = false;
            await LoadLatestPaystubAsync(military);
        }
        await UpdateSalaryAsync();
        Recalculate();
    }

    private async void RankBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        await UpdateSalaryAsync();
        Recalculate();
    }

    private void ConfigurePercentageSelectors()
    {
        SetOptions(QualificationText, QualificationCatalog.All.Select(x => new PercentageChoice(x.Percentage, $"{NumberInput(x.Percentage)}% — {x.Description}")));
        SetOptions(MilitaryAdditionalText, [0m, 13m, 16m, 19m, 22m, 25m, 28m]);
        SetOptions(AvailabilityText, [0m, 5m, 6m, 12m, 20m, 26m, 32m, 35m, 38m, 41m]);
        SetOptions(PermanenceText, Enumerable.Range(0, 21).Select(index => index * 5m));
        SetOptions(FusexText, [0m, 3m]);
        SetOptions(Pension105Text, [0m, 7.5m, 9.5m, 10.5m]);
        SetOptions(Pension15Text, [0m, 1.5m]);
        SetOptions(PnrText, [0m, 3.5m, 5m]);
        PensionPercentText.ItemsSource = new[] { "5", "10", "15", "20", "25", "30", "33,33", "35", "40", "50", "60", "70", "100" };
    }

    private static void SetOptions(ComboBox box, IEnumerable<decimal> values)
        => SetOptions(box, values.Select(value => new PercentageChoice(value, $"{NumberInput(value)}%")));

    private static void SetOptions(ComboBox box, IEnumerable<PercentageChoice> values)
        => box.ItemsSource = values.GroupBy(x => x.Value).Select(x => x.First()).OrderBy(x => x.Value).ToList();

    private static void SetPercent(ComboBox box, decimal value, string source)
    {
        var options = (box.ItemsSource as IEnumerable<PercentageChoice>)?.ToList() ?? [];
        if (options.All(option => option.Value != value))
        {
            options.Add(new PercentageChoice(value, $"{NumberInput(value)}% — {source}"));
            box.ItemsSource = options.OrderBy(option => option.Value).ToList();
        }
        box.SelectedValue = value;
    }

    private async Task LoadLatestPaystubAsync(MilitaryRecord military)
    {
        _paystubSalary = 0m;
        _paystubMilitaryId = null;
        PaystubSourceText.Text = "Procurando o último contracheque salvo...";
        try
        {
            var latest = (await App.Paystubs.FindForMilitaryAsync(military))
                .OrderByDescending(item => PaystubReferenceSortKey(item.Reference))
                .ThenByDescending(item => item.ModifiedAt)
                .FirstOrDefault();
            if (latest is null)
            {
                ApplyLegalProfileFallback(military, "Nenhum contracheque salvo foi localizado. Percentuais legais usuais carregados para conferência manual.");
                return;
            }

            var snapshot = await _paystubAudit.ReadRemunerationAsync(military, latest.Path);
            if (!snapshot.Success)
            {
                ApplyLegalProfileFallback(military, $"Contracheque {latest.Reference} localizado, mas não foi possível lê-lo: {snapshot.Error}");
                return;
            }

            var profile = MilitaryRemunerationCatalog.Resolve(military.Rank);
            var salary = snapshot.Salary;
            var qualification = ResolveSnapshotPercent(snapshot.QualificationPercent, snapshot.QualificationAdditional, salary, QualificationCatalog.All.Select(x => x.Percentage));
            var militaryAdditional = ResolveSnapshotPercent(snapshot.MilitaryAdditionalPercent, snapshot.MilitaryAdditional, salary, [0m, 13m, 16m, 19m, 22m, 25m, 28m]);
            var availability = ResolveSnapshotPercent(snapshot.AvailabilityPercent, snapshot.AvailabilityAdditional, salary, [0m, 5m, 6m, 12m, 20m, 26m, 32m, 35m, 38m, 41m]);
            var permanence = ResolveSnapshotPercent(snapshot.PermanencePercent, snapshot.PermanenceAdditional, salary, Enumerable.Range(0, 21).Select(index => index * 5m));
            var vencimentos = salary + snapshot.QualificationAdditional + snapshot.MilitaryAdditional + snapshot.AvailabilityAdditional + snapshot.PermanenceAdditional;
            var fusex = snapshot.Fusex > 0m
                ? ResolveSnapshotPercent(snapshot.FusexPercent, snapshot.Fusex, vencimentos, [0m, 3m])
                : 0m;
            var pension105 = snapshot.MilitaryPension > 0m
                ? snapshot.MilitaryPension105Percent
                  ?? (snapshot.MilitaryPension15Percent.HasValue ? 0m : profile.MilitaryPensionPercent)
                : 0m;
            var pension15 = snapshot.MilitaryPension15Percent ?? 0m;
            var pnr = snapshot.PnrDiscount > 0m
                ? ResolveSnapshotPercent(snapshot.PnrPercent, snapshot.PnrDiscount, salary, [3.5m, 5m])
                : 0m;

            _suppress = true;
            _paystubSalary = salary;
            _paystubMilitaryId = military.Id;
            _databaseSalary = salary;
            ManualSalaryBox.IsChecked = false;
            SetPercent(QualificationText, qualification, "contracheque");
            SetPercent(MilitaryAdditionalText, militaryAdditional, "contracheque");
            SetPercent(AvailabilityText, availability, "contracheque");
            SetPercent(PermanenceText, permanence, "contracheque");
            SetPercent(FusexText, fusex, "contracheque");
            SetPercent(Pension105Text, pension105, "contracheque");
            SetPercent(Pension15Text, pension15, "contracheque");
            SetPercent(PnrText, pnr, "contracheque");
            PreSchoolText.Text = MoneyInput(snapshot.PreSchool);
            FamilySalaryText.Text = MoneyInput(snapshot.FamilySalary);
            FusexDependentText.Text = MoneyInput(snapshot.FusexDependent);
            FusexMedicalText.Text = MoneyInput(snapshot.FusexMedical);
            ExistingAlimonyText.Text = MoneyInput(snapshot.ExistingAlimony);
            SalaryUsedText.Text = Money(salary);
            _suppress = false;

            PaystubSourceText.Text = $"Fonte automática: {latest.Reference} — {Path.GetFileName(latest.Path)}. Rubricas carregadas do PDF. A base e o percentual da nova pensão permanecem sob conferência obrigatória da decisão judicial.";
        }
        catch (Exception ex)
        {
            _suppress = false;
            await App.Log.WriteAsync("Falha ao preencher Pensão Judicial pelo último contracheque.", ex);
            ApplyLegalProfileFallback(military, "Falha ao ler o contracheque. Percentuais legais usuais carregados; confira tudo manualmente.");
        }
    }

    private void ApplyLegalProfileFallback(MilitaryRecord military, string message)
    {
        var profile = MilitaryRemunerationCatalog.Resolve(military.Rank);
        _suppress = true;
        SetPercent(MilitaryAdditionalText, profile.MilitaryAdditionalPercent, "perfil legal");
        SetPercent(AvailabilityText, profile.AvailabilityPercent, "perfil legal");
        SetPercent(FusexText, profile.FusexPercent, "perfil legal");
        SetPercent(Pension105Text, profile.MilitaryPensionPercent, "perfil legal");
        SetPercent(Pension15Text, 0m, "perfil legal");
        SetPercent(PnrText, 0m, "sem contracheque");
        if (AdjustmentNumericParser.TryParseDecimal(military.PreSchoolValue, out var preSchool)) PreSchoolText.Text = MoneyInput(preSchool);
        ExistingAlimonyText.Text = MoneyInput(ParseMoney(military.AlimonyValue));
        _suppress = false;
        PaystubSourceText.Text = message;
    }

    private static decimal ResolveSnapshotPercent(decimal? parsed, decimal amount, decimal baseValue, IEnumerable<decimal> known)
    {
        if (parsed is >= 0m and <= 100m) return parsed.Value;
        if (amount <= 0m || baseValue <= 0m) return 0m;
        var inferred = Math.Round(amount / baseValue * 100m, 2, MidpointRounding.AwayFromZero);
        var nearest = known.OrderBy(value => Math.Abs(value - inferred)).FirstOrDefault();
        return Math.Abs(nearest - inferred) <= 0.20m ? nearest : inferred;
    }

    private static int PaystubReferenceSortKey(string? reference)
    {
        var match = Regex.Match(reference ?? string.Empty, @"(?<month>0?[1-9]|1[0-2])\D+(?<year>20\d{2})");
        return match.Success
            ? int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture) * 100 + int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture)
            : 0;
    }

    private async Task UpdateSalaryAsync()
    {
        var rank = SelectedMilitary?.Rank;
        if (string.IsNullOrWhiteSpace(rank)) rank = RankBox.Text;
        _databaseSalary = SelectedMilitary is { } selected && _paystubMilitaryId == selected.Id && _paystubSalary > 0m
            ? _paystubSalary
            : await App.MilitaryRepository.GetSalaryByRankAsync(rank);
        SalaryUsedText.Text = Money(ManualSalaryBox.IsChecked == true ? ParseMoney(ManualSalaryText.Text) : _databaseSalary);
    }

    private void InputChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress || !IsLoaded) return;
        ToggleModes();
        Recalculate();
    }

    private void ToggleModes()
    {
        ManualSalaryText.IsEnabled = ManualSalaryBox.IsChecked == true;
        ManualIrText.IsEnabled = ManualIrBox.IsChecked == true;
        DependentsText.IsEnabled = AutomaticIrBox.IsChecked == true;
        FixedPensionText.IsEnabled = FixedPensionBox.IsChecked == true;
        PensionPercentText.IsEnabled = PercentagePensionBox.IsChecked == true;
    }

    private JudicialPensionCalculationInput BuildInput()
    {
        var salary = ManualSalaryBox.IsChecked == true ? ParseMoney(ManualSalaryText.Text) : _databaseSalary;
        return new JudicialPensionCalculationInput
        {
            Salary = salary,
            QualificationPercent = ParseMoney(QualificationText.Text),
            MilitaryAdditionalPercent = ParseMoney(MilitaryAdditionalText.Text),
            AvailabilityPercent = ParseMoney(AvailabilityText.Text),
            PermanencePercent = ParseMoney(PermanenceText.Text),
            PreSchoolValue = ParseMoney(PreSchoolText.Text),
            FamilySalaryValue = ParseMoney(FamilySalaryText.Text),
            FusexPercent = ParseMoney(FusexText.Text),
            MilitaryPension105Percent = ParseMoney(Pension105Text.Text),
            MilitaryPension15Percent = ParseMoney(Pension15Text.Text),
            FusexDependentDiscount = ParseMoney(FusexDependentText.Text),
            FusexMedicalExpense = ParseMoney(FusexMedicalText.Text),
            PnrPercent = ParseMoney(PnrText.Text),
            ExistingAlimony = ParseMoney(ExistingAlimonyText.Text),
            OtherAlimony = ParseMoney(OtherAlimonyText.Text),
            AutomaticIncomeTax = AutomaticIrBox.IsChecked == true,
            ManualIncomeTax = ParseMoney(ManualIrText.Text),
            IncomeTaxDependents = int.TryParse(DependentsText.Text, out var dependents) ? Math.Max(0, dependents) : 0,
            DeductMedicalFromIncomeTax = DeductMedicalBox.IsChecked == true,
            DeductFusexDependentFromIncomeTax = DeductDependentFusexBox.IsChecked == true,
            DeductAlimonyFromIncomeTax = DeductAlimonyBox.IsChecked == true,
            DeductPnrFromIncomeTax = DeductPnrBox.IsChecked == true,
            ApplyIncomeTaxReducer2026 = IrReducerBox.IsChecked == true,
            FixedPension = FixedPensionBox.IsChecked == true,
            PensionBase = (PensionBaseBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "BASE",
            PensionPercent = ParseMoney(PensionPercentText.Text),
            FixedPensionValue = ParseMoney(FixedPensionText.Text),
            Extras = _extras.ToList()
        };
    }

    private void Recalculate()
    {
        if (_suppress || !IsLoaded) return;
        try
        {
            _result = JudicialPensionService.Calculate(BuildInput());
            SalaryUsedText.Text = Money(_result.Salary);
            IrBaseText.Text = Money(_result.IncomeTaxBase);
            EarningsResultText.Text = Money(_result.GrossEarnings);
            DiscountsResultText.Text = Money(_result.MandatoryDiscounts);
            PensionBaseResultText.Text = Money(_result.PensionCalculationBase);
            PensionResultText.Text = Money(_result.JudicialPension);
            NetResultText.Text = Money(_result.NetAfterPension);
            CalculationDetailBox.Text = _result.Summary;
            RefreshBulletin();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Revise os valores informados: " + ex.Message;
        }
    }

    private JudicialPensionSettings BuildSettings()
    {
        var input = BuildInput();
        return new JudicialPensionSettings
        {
            MilitaryId = SelectedMilitary?.Id,
            Rank = SelectedMilitary?.Rank ?? RankBox.Text,
            UseManualSalary = ManualSalaryBox.IsChecked == true,
            ManualSalary = ParseMoney(ManualSalaryText.Text),
            QualificationPercent = input.QualificationPercent,
            MilitaryAdditionalPercent = input.MilitaryAdditionalPercent,
            AvailabilityPercent = input.AvailabilityPercent,
            PermanencePercent = input.PermanencePercent,
            PreSchoolValue = input.PreSchoolValue,
            FamilySalaryValue = input.FamilySalaryValue,
            FusexPercent = input.FusexPercent,
            MilitaryPension105Percent = input.MilitaryPension105Percent,
            MilitaryPension15Percent = input.MilitaryPension15Percent,
            FusexDependentDiscount = input.FusexDependentDiscount,
            FusexMedicalExpense = input.FusexMedicalExpense,
            PnrPercent = input.PnrPercent,
            ExistingAlimony = input.ExistingAlimony,
            OtherAlimony = input.OtherAlimony,
            AutomaticIncomeTax = input.AutomaticIncomeTax,
            ManualIncomeTax = input.ManualIncomeTax,
            IncomeTaxDependents = input.IncomeTaxDependents,
            DeductMedicalFromIncomeTax = input.DeductMedicalFromIncomeTax,
            DeductFusexDependentFromIncomeTax = input.DeductFusexDependentFromIncomeTax,
            DeductAlimonyFromIncomeTax = input.DeductAlimonyFromIncomeTax,
            DeductPnrFromIncomeTax = input.DeductPnrFromIncomeTax,
            ApplyIncomeTaxReducer2026 = input.ApplyIncomeTaxReducer2026,
            FixedPension = input.FixedPension,
            PensionBase = input.PensionBase,
            PensionPercent = input.PensionPercent,
            FixedPensionValue = input.FixedPensionValue,
            Extras = _extras.ToList(),
            OutputDirectory = _settings.OutputDirectory,
            Bulletin = BuildBulletin()
        };
    }

    private JudicialPensionBulletin BuildBulletin() => new()
    {
        Subject = string.IsNullOrWhiteSpace(BulletinSubjectBox.Text) ? "PENSÃO JUDICIAL" : BulletinSubjectBox.Text.Trim(),
        Mode = (BulletinModeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "IMPLANTAÇÃO",
        ProcessNumber = ProcessNumberBox.Text,
        DecisionType = DecisionTypeBox.Text,
        DecisionOrigin = DecisionOriginBox.Text,
        Locality = DecisionLocalityBox.Text,
        Court = CourtBox.Text,
        ActionNature = ActionNatureBox.Text,
        DecisionDate = DecisionDateBox.Text,
        DecisionSummary = DecisionSummaryBox.Text,
        PlaintiffName = PlaintiffNameBox.Text,
        PlaintiffCpf = PlaintiffCpfBox.Text,
        ReceivingLetter = ReceivingLetterBox.Text,
        ReceivingLetterOrigin = ReceivingLetterOriginBox.Text,
        ReceivingLetterDate = ReceivingLetterDateBox.Text,
        BeneficiaryName = BeneficiaryNameBox.Text,
        BeneficiaryCpf = BeneficiaryCpfBox.Text,
        GuardianName = GuardianNameBox.Text,
        GuardianCpf = GuardianCpfBox.Text,
        Relationship = RelationshipBox.Text,
        PensionType = PensionTypeBox.Text,
        CalculationRule = CalculationRuleBox.Text,
        VacationIncidence = VacationIncidenceBox.Text,
        ChristmasIncidence = ChristmasIncidenceBox.Text,
        CompensationIncidence = CompensationIncidenceBox.Text,
        PreviousPensions = PreviousPensionsBox.Text,
        BankCode = BankCodeBox.Text,
        BankName = BankNameBox.Text,
        Agency = AgencyBox.Text,
        Account = AccountBox.Text,
        BankOperation = BankOperationBox.Text,
        Rubric = RubricBox.Text,
        ReferenceMonth = ReferenceMonthBox.Text,
        ReferenceYear = ReferenceYearBox.Text,
        ArrearsTotalValue = ArrearsTotalValueBox.Text,
        ExclusionReason = ExclusionReasonBox.Text,
        ExclusionJustification = ExclusionJustificationBox.Text,
        PensionEndDate = PensionEndDateBox.Text,
        LegalBasis = LegalBasisBox.Text,
        Representation = RepresentationBox.Text,
        Observation = BulletinObservationBox.Text
    };

    private async void SaveCalculation_Click(object sender, RoutedEventArgs e)
    {
        if (_result.Salary <= 0m)
        {
            SigfurDialog.Show(this, "O soldo utilizado está zerado. Cadastre o soldo do posto ou informe um valor manual.", "Pensão Judicial", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var input = BuildInput();
        if (!input.FixedPension && input.PensionPercent is <= 0m or > 100m)
        {
            SigfurDialog.Show(this, "Selecione ou informe o percentual exatamente como consta na decisão judicial, entre 0% e 100%.", "Pensão Judicial — conferência obrigatória", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (input.FixedPension && input.FixedPensionValue <= 0m)
        {
            SigfurDialog.Show(this, "Informe o valor fixo exatamente como consta na decisão judicial.", "Pensão Judicial — conferência obrigatória", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var pensionRule = input.FixedPension
            ? $"Valor fixo: {Money(input.FixedPensionValue)}"
            : $"Base: {input.PensionBase} | Percentual: {NumberInput(input.PensionPercent)}%";
        if (SigfurDialog.Show(this,
                $"CONFIRA COM A DECISÃO/OFÍCIO JUDICIAL\n\n{pensionRule}\nPensão calculada: {Money(_result.JudicialPension)}\n\nO contracheque preencheu somente rubricas remuneratórias e descontos existentes. A regra da nova pensão não foi inferida. Confirmar estes dados?",
                "Pensão Judicial — revisão de segurança", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var answer = SigfurDialog.Show(this,
            "Salvar este cálculo no histórico?\n\nSIM: salva e também registra o valor da pensão na ficha do militar selecionado.\nNÃO: salva somente no histórico.\nCANCELAR: não salva.",
            "Salvar cálculo", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return;
        try
        {
            SetBusy(true, "Salvando cálculo...");
            _settings = BuildSettings();
            var id = await _service.SaveCalculationAsync(SelectedMilitary, _settings, _result, answer == MessageBoxResult.Yes);
            await _service.SaveSettingsAsync(_settings);
            await ReloadHistoryAsync();
            StatusText.Text = $"Cálculo #{id} salvo com sucesso.";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private void NewCalculation_Click(object sender, RoutedEventArgs e)
    {
        if (SigfurDialog.Show(this, "Limpar o formulário e iniciar um novo cálculo?", "Pensão Judicial", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _suppress = true;
        _settings = new JudicialPensionSettings();
        ApplySettings(_settings);
        _databaseSalary = 0m;
        _paystubSalary = 0m;
        _paystubMilitaryId = null;
        PaystubSourceText.Text = "Selecione um militar para carregar o último contracheque salvo.";
        _suppress = false;
        ToggleModes();
        Recalculate();
        StatusText.Text = "Novo cálculo iniciado.";
    }

    private async void ExportWord_Click(object sender, RoutedEventArgs e)
    {
        if (_result.Salary <= 0m) { SigfurDialog.Show(this, "Faça um cálculo válido antes de gerar o documento.", "Pensão Judicial", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var settings = BuildSettings();
        var dialog = new SaveFileDialog
        {
            Filter = "Documento Word|*.docx",
            DefaultExt = ".docx",
            FileName = $"Pensao_Judicial_{SafeName(SelectedMilitary?.WarName ?? SelectedMilitary?.Name ?? "Calculo")}_{DateTime.Today:yyyyMMdd}.docx",
            InitialDirectory = Directory.Exists(settings.OutputDirectory) ? settings.OutputDirectory : App.Paths.JudicialPensionOutputDirectory
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            SetBusy(true, "Gerando documento Word...");
            await _service.ExportDocxAsync(dialog.FileName, SelectedMilitary, settings, _result);
            _settings = settings;
            _settings.OutputDirectory = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
            await _service.SaveSettingsAsync(_settings);
            var walletSaved = false;
            if (SelectedMilitary is not null)
            {
                var mode = NormalizeBulletinMode((BulletinModeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? BulletinModeBox.Text);
                var title = $"Pensão Judicial - {mode}";
                var observation = "Documento gerado pelo módulo Pensão Judicial e salvo automaticamente na carteira do militar.";
                await App.MilitaryRepository.AddDocumentAsync(SelectedMilitary, dialog.FileName, "PENSAO_JUDICIAL", title, observation, BuildCurrentPensionKeysJson());
                walletSaved = true;
            }
            StatusText.Text = walletSaved
                ? $"Documento gerado e salvo na carteira: {Path.GetFileName(dialog.FileName)}"
                : $"Documento gerado: {Path.GetFileName(dialog.FileName)}";
            ShellService.OpenPath(dialog.FileName);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async void ImportPensionDocumentOcr_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMilitary is null)
        {
            SigfurDialog.Show(this, "Selecione o militar antes de anexar o documento da pensão judicial.", "Pensão Judicial", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Anexar decisão/ofício da Pensão Judicial",
            Filter = "Documentos suportados|*.pdf;*.docx;*.txt;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|PDF|*.pdf|Word|*.docx|Texto|*.txt|Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|Todos os arquivos|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            SetBusy(true, "Anexando documento e preparando chaves OCR...");
            var keys = await ExtractPensionKeysAsync(dialog.FileName);
            keys["TIPO_DOCUMENTO_SIGFUR"] = "PENSAO_JUDICIAL";
            keys["MILITAR_ID"] = SelectedMilitary.Id.ToString(CultureInfo.InvariantCulture);
            keys["MILITAR_NOME"] = SelectedMilitary.Name;
            keys["MILITAR_POSTO"] = SelectedMilitary.Rank;
            keys["MILITAR_CPF"] = MilitaryFormatting.FormatCpf(SelectedMilitary.Cpf);
            keys["ARQUIVO_ORIGEM"] = Path.GetFileName(dialog.FileName);
            keys["OCR_ATUALIZADO_EM"] = DateTime.Now.ToString("s", CultureInfo.InvariantCulture);

            var title = $"Pensão Judicial - documento judicial/OCR - {DateTime.Now:dd.MM.yyyy HH.mm}";
            var observation = keys.Any(k => k.Key != "OCR_TEXTO" && !string.IsNullOrWhiteSpace(k.Value))
                ? "Documento de pensão judicial anexado; chaves OCR preparadas para preenchimento do boletim."
                : "Documento de pensão judicial anexado. OCR preparado para revisão/chaves quando o modelo definitivo for informado.";
            await App.MilitaryRepository.AddDocumentAsync(SelectedMilitary, dialog.FileName, "PENSAO_JUDICIAL", title, observation, ToKeysJson(keys));

            if (keys.Any(k => k.Key != "OCR_TEXTO" && !string.IsNullOrWhiteSpace(k.Value)))
            {
                ApplyPensionKeysToForm(keys, overwrite: false);
                RefreshBulletin();
            }

            StatusText.Foreground = Brushes.DarkGreen;
            StatusText.Text = "Documento salvo na carteira do militar e OCR/chaves preparados.";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async void ApplyLatestPensionOcr_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMilitary is null)
        {
            SigfurDialog.Show(this, "Selecione o militar antes de puxar chaves de OCR da carteira.", "Pensão Judicial", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            SetBusy(true, "Buscando último OCR de pensão judicial na carteira...");
            var document = (await App.MilitaryRepository.GetDocumentsAsync(SelectedMilitary.Id))
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.KeysJson)
                                  && (x.Type.Contains("PENSAO", StringComparison.OrdinalIgnoreCase)
                                      || x.Type.Contains("PENSÃO", StringComparison.OrdinalIgnoreCase)));

            if (document is null)
            {
                StatusText.Foreground = Brushes.Firebrick;
                StatusText.Text = "Nenhum documento de Pensão Judicial localizado na carteira deste militar.";
                return;
            }

            var keys = FromKeysJson(document.KeysJson);
            if (keys.Count == 0)
            {
                StatusText.Foreground = Brushes.Firebrick;
                StatusText.Text = "O documento mais recente de Pensão Judicial ainda não possui chaves OCR salvas.";
                return;
            }

            ApplyPensionKeysToForm(keys, overwrite: false);
            RefreshBulletin();
            StatusText.Foreground = Brushes.DarkGreen;
            StatusText.Text = $"Chaves OCR aplicadas a partir da carteira: {document.Title}";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async Task<Dictionary<string, string>> ExtractPensionKeysAsync(string filePath)
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        string rawText = string.Empty;

        if (extension == ".pdf")
        {
            try { rawText = await new PdfTextService(App.Log, App.Paths).ExtractAsync(filePath); }
            catch (Exception ex)
            {
                await App.Log.WriteAsync("PDF sem texto pesquisável para OCR da Pensão Judicial. O arquivo será salvo na carteira e poderá ser revisado depois.", ex);
                keys["OCR_STATUS"] = "PDF sem texto pesquisável; arquivo salvo para OCR/revisão posterior.";
            }
        }
        else if (extension == ".txt")
        {
            rawText = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
        }
        else if (extension == ".docx")
        {
            rawText = ExtractDocxText(filePath);
        }
        else
        {
            keys["OCR_STATUS"] = "Imagem/documento anexado. Leitura OCR genérica deixada preparada para o modelo definitivo da pensão.";
        }

        if (!string.IsNullOrWhiteSpace(rawText))
        {
            foreach (var pair in ExtractPensionKeysFromText(rawText))
                if (!string.IsNullOrWhiteSpace(pair.Value)) keys[pair.Key] = pair.Value;
            keys["OCR_TEXTO"] = rawText.Length > 5000 ? rawText[..5000] : rawText;
        }

        return keys;
    }

    private static Dictionary<string, string> ExtractPensionKeysFromText(string text)
    {
        var source = CleanText(text);
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Set(string key, string? value)
        {
            value = CleanText(value ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(value) && !keys.ContainsKey(key)) keys[key] = value;
        }

        Set("ProcessNumber", MatchValue(source, @"\bprocesso\s*(?:n[ºo°\.]*)?\s*[:\-]?\s*([0-9]{5,7}[-\.]?\d{2}[\.]?\d{4}[\.]?\d[\.]?\d{2}[\.]?\d{4}|[0-9\.\-/]{8,35})"));
        Set("PROCESSO", keys.GetValueOrDefault("ProcessNumber"));
        Set("DecisionType", MatchValue(source, @"\b(ac[oó]rd[aã]o|liminar|senten[cç]a|transitad[ao]\s+em\s+julgad[ao]|tutela\s+antecipada)\b"));
        Set("DecisionDate", MatchValue(source, @"(?:data\s+da\s+decis[aã]o|decis[aã]o\s+judicial|senten[cç]a\s+de|em)\s*[:\-]?\s*(\d{1,2}[\/\.\-]\d{1,2}[\/\.\-]\d{2,4}|\d{1,2}\s+de\s+[a-zçãé]+\s+de\s+\d{4})"));
        if (!keys.ContainsKey("DecisionDate")) Set("DecisionDate", MatchValue(source, @"\b(\d{1,2}[\/\.\-]\d{1,2}[\/\.\-]\d{2,4})\b"));
        Set("DecisionOrigin", MatchValue(source, @"((?:\d+ª?\s*)?vara\s+[^\n,;]{5,120}|ju[ií]zo\s+[^\n,;]{5,120}|comarca\s+de\s+[^\n,;]{3,80})"));
        Set("Court", MatchValue(source, @"\b(STF|STJ|TJ[A-Z]{0,2}|TRF\s*\d?ª?|vara\s+estadual|vara\s+federal)\b"));
        Set("Locality", MatchValue(source, @"comarca\s+de\s+([A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ][A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ\s\-]{2,80}(?:\s*-\s*[A-Z]{2})?)"));
        Set("ActionNature", MatchValue(source, @"(a[cç][aã]o\s+de\s+alimentos|pensão\s+aliment[íi]cia|alimentos)"));
        Set("PlaintiffName", MatchValue(source, @"\bautor(?:a)?\s*[:\-]?\s*([A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ][A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ\s'\.\-]{5,90})"));
        Set("PlaintiffCpf", MatchValue(source, @"\bautor(?:a)?.{0,120}?CPF\s*[:\-]?\s*(\d{3}\.?\d{3}\.?\d{3}\-?\d{2})"));
        Set("ReceivingLetter", MatchValue(source, @"\bof[ií]cio(?:\s+de\s+recebimento)?\s*(?:n[ºo°\.]*)?\s*[:\-]?\s*([A-Z0-9][A-Z0-9\.\-/]{1,40})"));
        Set("ReceivingLetterOrigin", MatchValue(source, @"\borigem\s+do\s+of[ií]cio\s*[:\-]?\s*([A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ0-9][A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ0-9\s\.\-/]{3,90})"));
        Set("ReceivingLetterDate", MatchValue(source, @"(?:data\s+do\s+of[ií]cio|of[ií]cio.{0,80}?datad[oa]\s+de)\s*[:\-]?\s*(\d{1,2}[\/\.\-]\d{1,2}[\/\.\-]\d{2,4}|\d{1,2}\s+de\s+[a-zçãé]+\s+de\s+\d{4})"));
        Set("BeneficiaryName", MatchValue(source, @"\b(?:alimentad[oa]|benefici[aá]ri[oa])\s*[:\-]?\s*([A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ][A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ\s'\.\-]{5,90})"));
        Set("BeneficiaryCpf", MatchValue(source, @"\b(?:alimentad[oa]|benefici[aá]ri[oa]).{0,120}?CPF\s*[:\-]?\s*(\d{3}\.?\d{3}\.?\d{3}\-?\d{2})"));
        Set("GuardianName", MatchValue(source, @"\b(?:detentor(?:a)?\s+da\s+guarda(?:\s+legal)?|representante)\s*[:\-]?\s*([A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ][A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ\s'\.\-]{5,90})"));
        Set("GuardianCpf", MatchValue(source, @"\b(?:detentor(?:a)?\s+da\s+guarda(?:\s+legal)?|representante).{0,120}?CPF\s*[:\-]?\s*(\d{3}\.?\d{3}\.?\d{3}\-?\d{2})"));
        Set("Relationship", MatchValue(source, @"grau\s+de\s+parentesco\s*[:\-]?\s*([A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ][A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ\s]{2,40})"));
        Set("CalculationRule", MatchValue(source, @"((?:\d{1,3}(?:,\d+)?\s*%|R\$\s*\d[\d\.]*,\d{2})[^\n]{0,220})"));
        Set("BankCode", MatchValue(source, @"\bbanco\s*[:\-]?\s*(\d{3})"));
        Set("BankName", MatchValue(source, @"\b(banco\s+do\s+brasil|caixa\s+econ[oô]mica\s+federal|bradesco|ita[uú]|santander|sicoob)\b"));
        Set("Agency", MatchValue(source, @"ag[eê]ncia\s*[:\-]?\s*([0-9\-\.xX]{3,15})"));
        Set("Account", MatchValue(source, @"conta(?:\s+corrente)?\s*[:\-]?\s*([0-9\-\.xX]{3,25})"));
        Set("BankOperation", MatchValue(source, @"opera[cç][aã]o\s*[:\-]?\s*(\d{3})"));

        var summary = MatchValue(source, @"((?:julgo|condeno|determino|fixo|homologo)[^\n]{40,250})");
        Set("DecisionSummary", summary.Length > 250 ? summary[..250] : summary);

        foreach (var alias in new[]
        {
            ("NÚMERO DO PROCESSO", "ProcessNumber"), ("ORIGEM DA DECISÃO", "DecisionOrigin"), ("DECISÃO JUDICIAL", "DecisionType"),
            ("DATA DA DECISÃO", "DecisionDate"), ("NATUREZA DA AÇÃO", "ActionNature"), ("ALIMENTADO", "BeneficiaryName"),
            ("CPF ALIMENTADO", "BeneficiaryCpf"), ("DETENTOR DA GUARDA", "GuardianName"), ("CPF DETENTOR", "GuardianCpf"),
            ("OFÍCIO DE RECEBIMENTO", "ReceivingLetter"), ("ORIGEM DO OFÍCIO", "ReceivingLetterOrigin"), ("DATA DO OFÍCIO", "ReceivingLetterDate"),
            ("BANCO", "BankCode"), ("AGÊNCIA", "Agency"), ("CONTA", "Account"), ("REGRA DE CÁLCULO", "CalculationRule")
        })
            if (keys.TryGetValue(alias.Item2, out var v)) keys[alias.Item1] = v;

        return keys;
    }

    private void ApplyPensionKeysToForm(IReadOnlyDictionary<string, string> keys, bool overwrite)
    {
        void SetBox(TextBox box, params string[] keyNames)
        {
            if (!overwrite && !string.IsNullOrWhiteSpace(box.Text)) return;
            var value = keyNames.Select(k => keys.GetValueOrDefault(k, string.Empty)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (!string.IsNullOrWhiteSpace(value)) box.Text = value.Trim();
        }
        void SetCombo(ComboBox box, params string[] keyNames)
        {
            if (!overwrite && !string.IsNullOrWhiteSpace(box.Text)) return;
            var value = keyNames.Select(k => keys.GetValueOrDefault(k, string.Empty)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (!string.IsNullOrWhiteSpace(value)) box.Text = value.Trim();
        }

        SetBox(ProcessNumberBox, "ProcessNumber", "PROCESSO", "NÚMERO DO PROCESSO");
        SetCombo(DecisionTypeBox, "DecisionType", "DECISÃO JUDICIAL");
        SetBox(DecisionOriginBox, "DecisionOrigin", "ORIGEM DA DECISÃO");
        SetBox(DecisionLocalityBox, "Locality", "LOCALIDADE");
        SetBox(CourtBox, "Court", "TRIBUNAL");
        SetBox(ActionNatureBox, "ActionNature", "NATUREZA DA AÇÃO");
        SetBox(DecisionDateBox, "DecisionDate", "DATA DA DECISÃO");
        SetBox(DecisionSummaryBox, "DecisionSummary", "RESUMO DA DECISÃO");
        SetBox(PlaintiffNameBox, "PlaintiffName", "AUTOR");
        SetBox(PlaintiffCpfBox, "PlaintiffCpf", "CPF AUTOR");
        SetBox(ReceivingLetterBox, "ReceivingLetter", "OFICIO", "OFÍCIO DE RECEBIMENTO");
        SetBox(ReceivingLetterOriginBox, "ReceivingLetterOrigin", "ORIGEM DO OFICIO", "ORIGEM DO OFÍCIO");
        SetBox(ReceivingLetterDateBox, "ReceivingLetterDate", "DATA DO OFICIO", "DATA DO OFÍCIO");
        SetBox(BeneficiaryNameBox, "BeneficiaryName", "ALIMENTADO");
        SetBox(BeneficiaryCpfBox, "BeneficiaryCpf", "CPF ALIMENTADO");
        SetBox(GuardianNameBox, "GuardianName", "DETENTOR DA GUARDA");
        SetBox(GuardianCpfBox, "GuardianCpf", "CPF DETENTOR");
        SetBox(RelationshipBox, "Relationship", "GRAU DE PARENTESCO");
        SetCombo(PensionTypeBox, "PensionType", "TIPO DE PENSÃO");
        SetBox(CalculationRuleBox, "CalculationRule", "REGRA DE CÁLCULO");
        SetBox(BankCodeBox, "BankCode", "BANCO");
        SetBox(BankNameBox, "BankName", "NOME DO BANCO");
        SetBox(AgencyBox, "Agency", "AGÊNCIA");
        SetBox(AccountBox, "Account", "CONTA");
        SetBox(BankOperationBox, "BankOperation", "OPERAÇÃO");
    }

    private string BuildCurrentPensionKeysJson()
    {
        var b = BuildBulletin();
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TIPO_DOCUMENTO_SIGFUR"] = "PENSAO_JUDICIAL",
            ["MODALIDADE"] = NormalizeBulletinMode(b.Mode),
            ["ProcessNumber"] = b.ProcessNumber,
            ["PROCESSO"] = b.ProcessNumber,
            ["DecisionType"] = b.DecisionType,
            ["DecisionOrigin"] = b.DecisionOrigin,
            ["Locality"] = b.Locality,
            ["Court"] = b.Court,
            ["ActionNature"] = b.ActionNature,
            ["DecisionDate"] = b.DecisionDate,
            ["DecisionSummary"] = b.DecisionSummary,
            ["PlaintiffName"] = b.PlaintiffName,
            ["PlaintiffCpf"] = b.PlaintiffCpf,
            ["ReceivingLetter"] = b.ReceivingLetter,
            ["ReceivingLetterOrigin"] = b.ReceivingLetterOrigin,
            ["ReceivingLetterDate"] = b.ReceivingLetterDate,
            ["BeneficiaryName"] = b.BeneficiaryName,
            ["BeneficiaryCpf"] = b.BeneficiaryCpf,
            ["GuardianName"] = b.GuardianName,
            ["GuardianCpf"] = b.GuardianCpf,
            ["Relationship"] = b.Relationship,
            ["PensionType"] = b.PensionType,
            ["CalculationRule"] = b.CalculationRule,
            ["BankCode"] = b.BankCode,
            ["BankName"] = b.BankName,
            ["Agency"] = b.Agency,
            ["Account"] = b.Account,
            ["BankOperation"] = b.BankOperation,
            ["PENSAO_VALOR_CALCULADO"] = Money(_result.JudicialPension),
            ["PENSAO_BASE_CALCULADA"] = Money(_result.PensionCalculationBase),
            ["ATUALIZADO_EM"] = DateTime.Now.ToString("s", CultureInfo.InvariantCulture)
        };
        return ToKeysJson(keys);
    }

    private static string ToKeysJson(IReadOnlyDictionary<string, string> keys)
        => JsonSerializer.Serialize(keys.Where(x => !string.IsNullOrWhiteSpace(x.Value)).ToDictionary(x => x.Key, x => x.Value), new JsonSerializerOptions { WriteIndented = true });

    private static Dictionary<string, string> FromKeysJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
            return new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
    }

    private static string ExtractDocxText(string path)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(path);
            var entry = archive.GetEntry("word/document.xml");
            if (entry is null) return string.Empty;
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var xml = reader.ReadToEnd();
            xml = xml.Replace("</w:p>", "\n", StringComparison.OrdinalIgnoreCase).Replace("</w:tab>", " ", StringComparison.OrdinalIgnoreCase);
            return CleanText(Regex.Replace(xml, "<[^>]+>", " "));
        }
        catch { return string.Empty; }
    }

    private static string MatchValue(string text, string pattern)
    {
        var match = Regex.Match(text ?? string.Empty, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        return match.Success ? CleanText(match.Groups[1].Value) : string.Empty;
    }

    private static string CleanText(string? value)
        => Regex.Replace((value ?? string.Empty).Replace("\0", string.Empty).Replace('\r', '\n'), @"[ \t]+", " ").Replace("\n ", "\n").Trim();

    private void CopySummary_Click(object sender, RoutedEventArgs e)
    {
        Recalculate();
        if (string.IsNullOrWhiteSpace(CalculationDetailBox.Text)) return;
        Clipboard.SetText(CalculationDetailBox.Text);
        StatusText.Text = "Resumo do cálculo copiado.";
    }

    private void AddRevenue_Click(object sender, RoutedEventArgs e) => ShowExtraDialog("R", null);
    private void AddDiscount_Click(object sender, RoutedEventArgs e) => ShowExtraDialog("D", null);
    private void EditExtra_Click(object sender, RoutedEventArgs e)
    {
        if (ExtrasGrid.SelectedItem is JudicialPensionExtra extra) ShowExtraDialog(extra.Type, extra);
    }
    private void ToggleExtraIncluded_Click(object sender, RoutedEventArgs e)
    {
        if (ExtrasGrid.SelectedItem is not JudicialPensionExtra extra) return;
        extra.IsIncluded = !extra.IsIncluded;
        ExtrasGrid.Items.Refresh();
        Recalculate();
    }

    private void ToggleExtraTaxable_Click(object sender, RoutedEventArgs e)
    {
        if (ExtrasGrid.SelectedItem is not JudicialPensionExtra extra || extra.Type != "R") return;
        extra.IsTaxable = !extra.IsTaxable;
        ExtrasGrid.Items.Refresh();
        Recalculate();
    }

    private void RemoveExtra_Click(object sender, RoutedEventArgs e)
    {
        if (ExtrasGrid.SelectedItem is not JudicialPensionExtra extra) return;
        _extras.Remove(extra);
        Recalculate();
    }

    private void ShowExtraDialog(string type, JudicialPensionExtra? existing)
    {
        var dialog = new Window { Title = existing is null ? "Adicionar rubrica" : "Editar rubrica", Owner = this, Width = 520, Height = 320, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Icon = Icon };
        var root = new Grid { Margin = new Thickness(18) };
        for (var i = 0; i < 5; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var typeBox = new ComboBox { ItemsSource = new[] { "R — Receita", "D — Desconto" }, SelectedIndex = (existing?.Type ?? type) == "D" ? 1 : 0 };
        var description = new TextBox { Text = existing?.Description ?? string.Empty };
        var value = new TextBox { Text = MoneyInput(existing?.Value ?? 0m) };
        var included = new CheckBox { Content = "Incluir no cálculo", IsChecked = existing?.IsIncluded ?? true };
        var taxable = new CheckBox { Content = "Receita tributável no IR", IsChecked = existing?.IsTaxable ?? true };
        AddField("Tipo", typeBox, 0); AddField("Descrição", description, 1); AddField("Valor", value, 2);
        Grid.SetRow(included, 3); included.Margin = new Thickness(0, 10, 0, 0); root.Children.Add(included);
        Grid.SetRow(taxable, 4); taxable.Margin = new Thickness(0, 5, 0, 0); root.Children.Add(taxable);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = MakeButton("Cancelar", "GhostButtonStyle", new Thickness(0, 0, 8, 0));
        var save = MakeButton("Salvar", "PrimaryButtonStyle", new Thickness());
        cancel.Click += (_, _) => dialog.DialogResult = false;
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(description.Text)) { SigfurDialog.Show(dialog, "Informe a descrição.", "Rubrica", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            dialog.DialogResult = true;
        };
        bar.Children.Add(cancel); bar.Children.Add(save); Grid.SetRow(bar, 6); root.Children.Add(bar); dialog.Content = root;
        if (dialog.ShowDialog() != true) return;
        var target = existing ?? new JudicialPensionExtra();
        target.Type = typeBox.SelectedIndex == 1 ? "D" : "R";
        target.Description = description.Text.Trim();
        target.Value = ParseMoney(value.Text);
        target.IsIncluded = included.IsChecked == true;
        target.IsTaxable = target.Type == "R" && taxable.IsChecked == true;
        if (existing is null) _extras.Add(target);
        ExtrasGrid.Items.Refresh();
        Recalculate();

        void AddField(string label, Control control, int row)
        {
            var panel = new Grid { Margin = new Thickness(0, row == 0 ? 0 : 9, 0, 0) };
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) }); panel.ColumnDefinitions.Add(new ColumnDefinition());
            panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }); Grid.SetColumn(control, 1); panel.Children.Add(control); Grid.SetRow(panel, row); root.Children.Add(panel);
        }
    }

    private void BulletinInputChanged(object sender, RoutedEventArgs e)
    {
        if (!_suppress && IsLoaded) RefreshBulletin();
    }
    private void RefreshBulletin_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateBulletinFields()) return;
        RefreshBulletin();
    }
    private void RefreshBulletin()
    {
        if (!IsLoaded) return;
        var settings = BuildSettings();
        SetBulletinPreviewText(JudicialPensionService.BuildBulletinText(SelectedMilitary, settings, _result));
        ClearRequiredHighlights();
    }

    private string GetBulletinPreviewText()
    {
        try
        {
            var text = new TextRange(BulletinPreviewBox.Document.ContentStart, BulletinPreviewBox.Document.ContentEnd).Text;
            return (text ?? string.Empty).TrimEnd('\r', '\n');
        }
        catch { return _bulletinPreviewText; }
    }

    private void SetBulletinPreviewText(string text)
    {
        _bulletinPreviewText = text ?? string.Empty;
        var military = SelectedMilitary is null ? Array.Empty<MilitaryRecord>() : new[] { SelectedMilitary };
        var render = new BulletinRenderResult
        {
            Text = _bulletinPreviewText,
            BoldRanges = BulletinTextFormatter.FindWarNameRanges(_bulletinPreviewText, military),
            UnresolvedTokens = []
        };
        var document = new BulletinService(App.Paths, App.Json, App.Log).BuildDocument(render);
        document.PagePadding = new Thickness(12);
        BulletinPreviewBox.Document = document;
    }

    private void CopyBulletin_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateBulletinFields()) return;
        RefreshBulletin();
        var plain = GetBulletinPreviewText();
        var military = SelectedMilitary is null ? Array.Empty<MilitaryRecord>() : new[] { SelectedMilitary };
        var render = new BulletinRenderResult
        {
            Text = plain,
            BoldRanges = BulletinTextFormatter.FindWarNameRanges(plain, military),
            UnresolvedTokens = []
        };
        var document = new BulletinService(App.Paths, App.Json, App.Log).BuildDocument(render);
        BulletinService.CopyForWord(document, render.Text);
        StatusText.Foreground = Brushes.DarkGreen;
        StatusText.Text = "Texto do boletim copiado em Times New Roman 10 pt, com somente o nome de guerra em negrito.";
    }
    private async void SendBulletinToSisbol_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateBulletinFields()) return;
        RefreshBulletin();
        var plain = GetBulletinPreviewText();
        if (string.IsNullOrWhiteSpace(plain))
        {
            StatusText.Foreground = Brushes.Firebrick;
            StatusText.Text = "Gere o texto do boletim antes de enviar.";
            return;
        }
        try
        {
            if (!App.Sisbol.IsReady)
            {
                SigfurDialog.Show(this,
                    "O SisBol não está preparado. Vá na janela principal, clique em ‘Preparar SisBol’, conclua o login/captcha e valide a sessão.",
                    "SisBol não preparado", MessageBoxButton.OK, MessageBoxImage.Information);
                StatusText.Text = "SisBol não preparado. Prepare na janela principal antes de enviar.";
                return;
            }
            SetBusy(true, "Enviando boletim da Pensão Judicial ao SISBOL...");
            var military = SelectedMilitary is null ? Array.Empty<MilitaryRecord>() : new[] { SelectedMilitary };
            var subject = string.IsNullOrWhiteSpace(BulletinSubjectBox.Text) ? "PENSÃO JUDICIAL" : BulletinSubjectBox.Text.Trim();
            await App.Sisbol.SendAsync(
                plain,
                military,
                subject,
                IncludeConsequencesCheck.IsChecked == true,
                ConsequencesTextBox.Text);
            StatusText.Foreground = Brushes.DarkGreen;
            StatusText.Text = "Pensão Judicial incluída no SISBOL e registrada no histórico.";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private bool ValidateBulletinFields()
    {
        ClearRequiredHighlights();
        var mode = NormalizeBulletinMode((BulletinModeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? BulletinModeBox.Text);
        var required = RequiredBulletinFields().ToList();
        var missing = required.Where(x => string.IsNullOrWhiteSpace(x.Value)).ToList();
        var isCaixa = BankCodeBox.Text.Contains("104", StringComparison.OrdinalIgnoreCase) || BankNameBox.Text.Contains("Caixa", StringComparison.OrdinalIgnoreCase);
        if (mode is "IMPLANTAÇÃO" && isCaixa && string.IsNullOrWhiteSpace(BankOperationBox.Text))
            missing.Add(("operação CAIXA", BankOperationBox, BankOperationBox.Text));

        var invalid = new List<(string Label, Control Control, string Message)>();
        if (mode is "IMPLANTAÇÃO" or "ATUALIZAÇÃO" && DecisionSummaryBox.Text.Trim().Length > 250)
            invalid.Add(("resumo da decisão", DecisionSummaryBox, "resumo da decisão deve ter no máximo 250 caracteres"));
        if (mode is "EXCLUSÃO" && ExclusionJustificationBox.Text.Trim().Length > 250)
            invalid.Add(("justificativa da exclusão", ExclusionJustificationBox, "justificativa da exclusão deve ter no máximo 250 caracteres"));

        foreach (var item in missing) MarkRequiredMissing(item.Control);
        foreach (var item in invalid) MarkRequiredMissing(item.Control);
        if (missing.Count == 0 && invalid.Count == 0)
        {
            StatusText.Foreground = Brushes.DarkGreen;
            StatusText.Text = "Campos obrigatórios conferidos.";
            return true;
        }

        StatusText.Foreground = Brushes.Firebrick;
        var pending = missing.Select(x => x.Label).Concat(invalid.Select(x => x.Message)).Distinct().ToList();
        StatusText.Text = "Confira os campos destacados em vermelho: " + string.Join(", ", pending);
        (missing.FirstOrDefault().Control ?? invalid.First().Control).Focus();
        return false;
    }

    private IEnumerable<(string Label, Control Control, string Value)> RequiredBulletinFields()
    {
        var mode = NormalizeBulletinMode((BulletinModeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? BulletinModeBox.Text);
        yield return ("militar/alimentante", MilitaryBox, SelectedMilitary?.Name ?? string.Empty);

        if (mode is "IMPLANTAÇÃO")
        {
            yield return ("número do processo", ProcessNumberBox, ProcessNumberBox.Text);
            yield return ("tipo da decisão", DecisionTypeBox, DecisionTypeBox.Text);
            yield return ("origem da decisão", DecisionOriginBox, DecisionOriginBox.Text);
            yield return ("data da decisão", DecisionDateBox, DecisionDateBox.Text);
            yield return ("natureza da ação", ActionNatureBox, ActionNatureBox.Text);
            yield return ("resumo da decisão", DecisionSummaryBox, DecisionSummaryBox.Text);
            yield return ("autor da ação", PlaintiffNameBox, PlaintiffNameBox.Text);
            yield return ("CPF do autor", PlaintiffCpfBox, PlaintiffCpfBox.Text);
            yield return ("ofício de recebimento", ReceivingLetterBox, ReceivingLetterBox.Text);
            yield return ("origem do ofício", ReceivingLetterOriginBox, ReceivingLetterOriginBox.Text);
            yield return ("data do ofício", ReceivingLetterDateBox, ReceivingLetterDateBox.Text);
            yield return ("alimentado/beneficiário", BeneficiaryNameBox, BeneficiaryNameBox.Text);
            yield return ("CPF do alimentado", BeneficiaryCpfBox, BeneficiaryCpfBox.Text);
            yield return ("detentor da guarda", GuardianNameBox, GuardianNameBox.Text);
            yield return ("CPF do detentor", GuardianCpfBox, GuardianCpfBox.Text);
            yield return ("grau de parentesco", RelationshipBox, RelationshipBox.Text);
            yield return ("tipo de pensão", PensionTypeBox, PensionTypeBox.Text);
            yield return ("regra de cálculo", CalculationRuleBox, CalculationRuleBox.Text);
            yield return ("incidência sobre férias", VacationIncidenceBox, VacationIncidenceBox.Text);
            yield return ("incidência sobre adicional natalino", ChristmasIncidenceBox, ChristmasIncidenceBox.Text);
            yield return ("incidência sobre compensação", CompensationIncidenceBox, CompensationIncidenceBox.Text);
            yield return ("pensões anteriores/base", PreviousPensionsBox, PreviousPensionsBox.Text);
            yield return ("código do banco", BankCodeBox, BankCodeBox.Text);
            yield return ("nome do banco", BankNameBox, BankNameBox.Text);
            yield return ("agência", AgencyBox, AgencyBox.Text);
            yield return ("conta", AccountBox, AccountBox.Text);
        }
        else if (mode is "ATUALIZAÇÃO")
        {
            yield return ("alimentado/beneficiário", BeneficiaryNameBox, BeneficiaryNameBox.Text);
            yield return ("CPF do alimentado", BeneficiaryCpfBox, BeneficiaryCpfBox.Text);
            yield return ("detentor da guarda", GuardianNameBox, GuardianNameBox.Text);
            yield return ("CPF do detentor", GuardianCpfBox, GuardianCpfBox.Text);
            yield return ("item/motivo da atualização", BulletinObservationBox, BulletinObservationBox.Text);
            yield return ("resumo da decisão/ofício", DecisionSummaryBox, DecisionSummaryBox.Text);
            yield return ("regra/valor atualizado", CalculationRuleBox, CalculationRuleBox.Text);
        }
        else if (mode is "DESCONTO DE ATRASADOS")
        {
            yield return ("valor total do desconto", ArrearsTotalValueBox, ArrearsTotalValueBox.Text);
            yield return ("mês de referência", ReferenceMonthBox, ReferenceMonthBox.Text);
            yield return ("ano de referência", ReferenceYearBox, ReferenceYearBox.Text);
            yield return ("alimentado/beneficiário", BeneficiaryNameBox, BeneficiaryNameBox.Text);
            yield return ("representante/detentor da guarda", GuardianNameBox, GuardianNameBox.Text);
        }
        else if (mode is "EXCLUSÃO")
        {
            yield return ("número do processo", ProcessNumberBox, ProcessNumberBox.Text);
            yield return ("alimentado/beneficiário", BeneficiaryNameBox, BeneficiaryNameBox.Text);
            yield return ("CPF do alimentado", BeneficiaryCpfBox, BeneficiaryCpfBox.Text);
            yield return ("detentor da guarda", GuardianNameBox, GuardianNameBox.Text);
            yield return ("CPF do detentor", GuardianCpfBox, GuardianCpfBox.Text);
            yield return ("motivo da exclusão", ExclusionReasonBox, ExclusionReasonBox.Text);
            yield return ("justificativa da exclusão", ExclusionJustificationBox, ExclusionJustificationBox.Text);
            yield return ("data de encerramento", PensionEndDateBox, PensionEndDateBox.Text);
        }
    }

    private static string NormalizeBulletinMode(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToUpperInvariant();
        return text.Contains("DESCONTO") || text.Contains("ATRAS") ? "DESCONTO DE ATRASADOS"
            : text.Contains("EXCLU") ? "EXCLUSÃO"
            : text.Contains("ATUAL") ? "ATUALIZAÇÃO"
            : "IMPLANTAÇÃO";
    }

    private void MarkRequiredMissing(Control control)
    {
        if (!_requiredVisualState.ContainsKey(control))
            _requiredVisualState[control] = (control.BorderBrush, control.BorderThickness);
        control.BorderBrush = Brushes.Firebrick;
        control.BorderThickness = new Thickness(2);
        control.ToolTip = "Campo obrigatório para esta modalidade da Pensão Judicial.";
    }

    private void ClearRequiredHighlights()
    {
        foreach (var item in _requiredVisualState)
        {
            item.Key.BorderBrush = item.Value.BorderBrush;
            item.Key.BorderThickness = item.Value.BorderThickness;
        }
        _requiredVisualState.Clear();
    }

    private async void ReloadHistory_Click(object sender, RoutedEventArgs e) => await ReloadHistoryAsync();
    private async Task ReloadHistoryAsync() => HistoryGrid.ItemsSource = await _service.ListHistoryAsync();
    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || e.Source != MainTabs) return;
        if (MainTabs.SelectedIndex == 1) RefreshBulletin();
        if (MainTabs.SelectedIndex == 2) await ReloadHistoryAsync();
    }

    private async Task SaveSettingsQuietlyAsync()
    {
        try { _settings = BuildSettings(); await _service.SaveSettingsAsync(_settings); } catch { }
    }

    private Button MakeButton(string content, string styleKey, Thickness margin)
    {
        var button = new Button { Content = content, Margin = margin };
        if (FindResource(styleKey) is Style style) button.Style = style;
        return button;
    }
    private static decimal ParseMoney(string? value)
    {
        return AdjustmentNumericParser.TryParseDecimal(value, out var parsed) ? Math.Max(0m, parsed) : 0m;
    }

    private sealed record PercentageChoice(decimal Value, string Label);
    private static string NumberInput(decimal value) => value.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR"));
    private static string MoneyInput(decimal value) => value.ToString("0.00", CultureInfo.GetCultureInfo("pt-BR"));
    private static string Money(decimal value) => MilitaryFormatting.FormatMoney((double)value);
    private static string SafeName(string value) => string.Join('_', (value ?? string.Empty).Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim('_');
    private void SetBusy(bool busy, string? message = null) { BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; if (!string.IsNullOrWhiteSpace(message)) StatusText.Text = message; Cursor = busy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow; }
    private void ShowError(Exception ex) { _ = App.Log.WriteAsync("Falha no módulo nativo de Pensão Judicial.", ex); SigfurDialog.Show(this, ex.Message, "SIGFUR — Pensão Judicial", MessageBoxButton.OK, MessageBoxImage.Error); }
}
