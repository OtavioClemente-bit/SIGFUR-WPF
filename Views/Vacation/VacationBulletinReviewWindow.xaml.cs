using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Vacation;

public partial class VacationBulletinReviewWindow : Window
{
    private sealed class ReviewRow
    {
        public required IntelligentBulletinFinding Finding { get; init; }
        public MilitaryRecord? Military { get; init; }
        public string Summary => string.IsNullOrWhiteSpace(Finding.Detail) ? Finding.Context : Finding.Detail + (string.IsNullOrWhiteSpace(Finding.Context) ? string.Empty : " — " + Finding.Context);
        public string MatchStatus => Military is null ? "Não localizado" : "Localizado";
        public string SearchText => string.Join(" ", Finding.Bulletin, Finding.BulletinDate, Finding.Rank, Finding.DisplayMilitary, Finding.Detail, Finding.Context, MatchStatus);
    }

    private readonly VacationPlanService _service;
    private readonly int _year;
    private readonly IReadOnlyList<VacationPeriod> _periods;
    private readonly ObservableCollection<ReviewRow> _rows = [];
    private ICollectionView? _view;

    public VacationBulletinReviewWindow(VacationPlanService service, int year, IReadOnlyList<VacationPeriod> periods)
    {
        _service = service; _year = year; _periods = periods;
        InitializeComponent();
        App.UiState.Attach(this);
        PeriodBox.ItemsSource = _periods;
        PeriodBox.SelectedItem = _periods.FirstOrDefault();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var store = await App.IntelligentBulletins.LoadAsync();
            var military = await App.MilitaryRepository.GetAllAsync();
            var findings = store.Items.SelectMany(x => x.Findings)
                .Where(x => ContainsVacation(x.Category) || ContainsVacation(x.Type) || ContainsVacation(x.Detail) || ContainsVacation(x.Context))
                .OrderByDescending(x => ParseDate(x.BulletinDate)).ThenBy(x => x.DisplayMilitary, StringComparer.CurrentCultureIgnoreCase).ToList();
            _rows.Clear();
            foreach (var finding in findings) _rows.Add(new ReviewRow { Finding = finding, Military = MatchMilitary(finding, military) });
            _view = CollectionViewSource.GetDefaultView(_rows); _view.Filter = Filter; FindingsGrid.ItemsSource = _view;
            StatusText.Text = $"{_rows.Count} publicação(ões) relacionada(s) a férias encontrada(s).";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private static bool ContainsVacation(string? value) => Normalize(value).Contains("ferias", StringComparison.Ordinal);
    private static DateTime ParseDate(string? value) => DateTime.TryParse(value, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var date) ? date : DateTime.MinValue;

    private static MilitaryRecord? MatchMilitary(IntelligentBulletinFinding finding, IReadOnlyList<MilitaryRecord> military)
    {
        var full = Normalize(finding.FullName);
        var war = Normalize(finding.WarName);
        return military.FirstOrDefault(x => full.Length > 4 && Normalize(x.Name) == full)
               ?? military.FirstOrDefault(x => full.Length > 4 && (Normalize(x.Name).Contains(full, StringComparison.Ordinal) || full.Contains(Normalize(x.Name), StringComparison.Ordinal)))
               ?? military.FirstOrDefault(x => war.Length > 2 && Normalize(x.WarName) == war);
    }

    private bool Filter(object item)
    {
        if (item is not ReviewRow row) return false;
        var query = Normalize(SearchBox.Text);
        return string.IsNullOrWhiteSpace(query) || Normalize(row.SearchText).Contains(query, StringComparison.Ordinal);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { _view?.Refresh(); StatusText.Text = $"{_view?.Cast<object>().Count() ?? 0} item(ns) visível(is)."; }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (PeriodBox.SelectedItem is not VacationPeriod period) return;
        var selected = FindingsGrid.SelectedItems.Cast<ReviewRow>().ToList();
        if (selected.Count == 0) return;
        var withoutMatch = selected.Where(x => x.Military is null).ToList();
        var matched = selected.Where(x => x.Military is not null).Select(x => x.Military!).DistinctBy(x => x.Id).ToList();
        if (matched.Count == 0) { SigfurDialog.Show(this, "Nenhum militar selecionado foi localizado no cadastro ativo.", "Conferência", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (SigfurDialog.Show(this, $"Aplicar {matched.Count} militar(es) em {period.DisplayName}?\n\nA publicação continua disponível para conferência e será marcada como revisada.", "Confirmar aplicação", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var days = int.TryParse((DaysBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var value) ? value : 30;
            var result = await _service.AllocateAsync(_year, period.Id, matched, days);
            foreach (var row in selected) await App.IntelligentBulletins.SetReviewedAsync(row.Finding, true, $"Aplicado no Plano de Férias {_year}: {period.DisplayName}.");
            await LoadAsync();
            var message = $"{result.Added} militar(es) aplicado(s).";
            if (withoutMatch.Count > 0) message += $" {withoutMatch.Count} não localizado(s).";
            if (result.Failures.Count > 0) message += Environment.NewLine + string.Join(Environment.NewLine, result.Failures.Take(12));
            SigfurDialog.Show(this, message, "Plano de Férias", MessageBoxButton.OK, result.Failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void MarkReviewed_Click(object sender, RoutedEventArgs e)
    {
        var selected = FindingsGrid.SelectedItems.Cast<ReviewRow>().ToList();
        if (selected.Count == 0) return;
        try { foreach (var row in selected) await App.IntelligentBulletins.SetReviewedAsync(row.Finding, true, "Conferido no Plano de Férias."); await LoadAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void FindingsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedPdf();
    private void OpenPdf_Click(object sender, RoutedEventArgs e) => OpenSelectedPdf();
    private void OpenSelectedPdf()
    {
        if (FindingsGrid.SelectedItem is ReviewRow row) App.IntelligentBulletins.OpenPdf(row.Finding);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return string.Concat(value.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)).ToLowerInvariant().Trim();
    }
    private void ShowError(Exception ex) { _ = App.Log.WriteAsync("Falha conferindo férias publicadas.", ex); SigfurDialog.Show(this, ex.Message, "SIGFUR — Conferência", MessageBoxButton.OK, MessageBoxImage.Error); }
}
