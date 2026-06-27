using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

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
            foreach (var period in await _service.GetPeriodsAsync(_year)) _periods.Add(period);
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
        var mine = (await _service.GetAllocationsForMilitaryAsync(_year, _military)).ToList();
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
            var result = await _service.AllocateAsync(_year, period.Id, [_military], SelectedDays());
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
        try { await _service.MoveAllocationAsync(row.Allocation, result.Value.PeriodId, result.Value.Days); HasChanges = true; await RefreshPlansAsync(); }
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
        try { await _service.SetPaidAnnualAsync(_year, _military.Id, true, needsFoodAid); HasChanges = true; await RefreshPlansAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void UnpaidYear_Click(object sender, RoutedEventArgs e)
    {
        try { await _service.SetPaidAnnualAsync(_year, _military.Id, false); HasChanges = true; await RefreshPlansAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void DeletePlan_Click(object sender, RoutedEventArgs e)
    {
        if (PlansGrid.SelectedItem is not PlanRow row) return;
        if (SigfurDialog.Show(this, $"Excluir o plano em {row.Period.DisplayName}?", "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await _service.RemoveAllocationAsync(row.Allocation.Id); HasChanges = true; await RefreshPlansAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task LoadFinancialAsync()
    {
        _financial = await _service.GetFinancialProfileAsync(_military, _year);
        ApplyFinancialToFields();
        await CalculateAsync(false);
    }

    private void ApplyFinancialToFields()
    {
        TypeBox.SelectedItem = TypeBox.Items.Cast<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Content?.ToString(), _financial.Type, StringComparison.OrdinalIgnoreCase)) ?? TypeBox.Items[0];
        QualificationBox.Text = FormatPercent(_financial.QualificationPercent);
        MilitaryAdditionalBox.Text = FormatPercent(_financial.MilitaryAdditionalPercent);
        AvailabilityBox.Text = FormatPercent(_financial.AvailabilityPercent);
    }

    private static string FormatPercent(decimal? value) => value?.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")) ?? string.Empty;
    private static decimal? ParsePercent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Replace("%", string.Empty, StringComparison.Ordinal).Trim();
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.GetCultureInfo("pt-BR"), out var value) || decimal.TryParse(text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value) ? value : null;
    }

    private async void SaveFinancial_Click(object sender, RoutedEventArgs e)
    {
        _financial.Type = (TypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Temporário";
        _financial.QualificationPercent = ParsePercent(QualificationBox.Text);
        _financial.MilitaryAdditionalPercent = ParsePercent(MilitaryAdditionalBox.Text);
        _financial.AvailabilityPercent = ParsePercent(AvailabilityBox.Text);
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
        var result = await _service.CalculateFinancialAsync(_military, _year, _financial);
        SalaryText.Text = result.Success ? result.Salary.ToString("C2", CultureInfo.GetCultureInfo("pt-BR")) : "—";
        FinancialResultText.Text = result.Success ? result.VacationAdditionalText : "Dados incompletos";
        FinancialWordsText.Text = result.Success
            ? $"{result.VacationAdditionalWords}. Base total: {result.BaseTotal.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"))}."
            : result.Error;
        StatusText.Text = saved ? "Dados financeiros salvos e cálculo atualizado." : "Cálculo financeiro atualizado.";
    }

    private async Task LoadPaystubsAsync()
    {
        PaystubsGrid.ItemsSource = await App.Paystubs.FindForMilitaryAsync(_military);
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
