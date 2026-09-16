using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Finance;

public partial class AdjustmentBizuManagerWindow : Window
{
    public ObservableCollection<AdjustmentBizuRule> Rules { get; } = [];
    public AdjustmentBizuRule? SelectedRule { get; private set; }
    private ICollectionView? _rulesView;

    public AdjustmentBizuManagerWindow(IEnumerable<AdjustmentBizuRule> rules, string selectedTitle = "")
    {
        InitializeComponent();
        App.UiState.Attach(this);
        DataContext = this;
        foreach (var rule in rules) Rules.Add(rule.Clone());
        _rulesView = CollectionViewSource.GetDefaultView(Rules);
        _rulesView.Filter = MatchesSearch;
        RulesGrid.ItemsSource = _rulesView;
        Loaded += (_, _) =>
        {
            RulesGrid.SelectedItem = Rules.FirstOrDefault(x => x.Title.Equals(selectedTitle, StringComparison.OrdinalIgnoreCase)) ?? Rules.FirstOrDefault();
            SearchBox.Focus();
            UpdateResultCount();
        };
    }

    private AdjustmentBizuRule? Current => RulesGrid.SelectedItem as AdjustmentBizuRule;

    private void RulesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var rule = Current;
        RuleTitleText.Text = rule?.Title ?? "Selecione uma regra";
        LegalBasisText.Text = string.IsNullOrWhiteSpace(rule?.LegalBasis) ? "—" : rule.LegalBasis;
        ObservationText.Text = string.IsNullOrWhiteSpace(rule?.Observation) ? "—" : rule.Observation;
        OriginText.Text = rule?.OriginText ?? "—";
        RuleRightsPanel.Children.Clear();
        if (rule is null) return;
        RuleRightsPanel.Children.Add(AdjustmentAccountsWindow.BuildReasonRightCard("Adicional de férias", rule.VacationAdditional));
        RuleRightsPanel.Children.Add(AdjustmentAccountsWindow.BuildReasonRightCard("Indenização de férias", rule.VacationIndemnity));
        RuleRightsPanel.Children.Add(AdjustmentAccountsWindow.BuildReasonRightCard("Adicional natalino", rule.ChristmasAdditional));
        RuleRightsPanel.Children.Add(AdjustmentAccountsWindow.BuildReasonRightCard("Compensação pecuniária", rule.Pecuniary));
    }

    private void RulesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => UseCurrent();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_rulesView is null) return;
        _rulesView.Refresh();
        UpdateResultCount();
        if (RulesGrid.SelectedItem is null) RulesGrid.SelectedItem = _rulesView.Cast<object>().FirstOrDefault();
    }

    private bool MatchesSearch(object item)
    {
        if (item is not AdjustmentBizuRule rule) return false;
        var search = SearchBox.Text.Trim();
        if (search.Length == 0) return true;
        return rule.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase)
               || rule.LegalBasis.Contains(search, StringComparison.CurrentCultureIgnoreCase)
               || rule.Observation.Contains(search, StringComparison.CurrentCultureIgnoreCase)
               || rule.OriginText.Contains(search, StringComparison.CurrentCultureIgnoreCase);
    }

    private void UpdateResultCount()
    {
        if (_rulesView is null) return;
        var count = _rulesView.Cast<object>().Count();
        ResultCountText.Text = count == 1 ? "1 hipótese" : $"{count} hipóteses";
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AdjustmentBizuEditorWindow(new AdjustmentBizuRule { IsCustom = true }) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        Rules.Add(dialog.Rule);
        _rulesView?.Refresh();
        RulesGrid.SelectedItem = dialog.Rule;
        UpdateResultCount();
    }

    private void Edit_Click(object sender, RoutedEventArgs e) => EditCurrent();

    private void EditCurrent()
    {
        if (Current is not { } current) return;
        var index = Rules.IndexOf(current);
        var dialog = new AdjustmentBizuEditorWindow(current) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        Rules[index] = dialog.Rule;
        _rulesView?.Refresh();
        RulesGrid.SelectedItem = dialog.Rule;
        UpdateResultCount();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } current) return;
        if (!current.IsCustom)
        {
            SigfurDialog.Show(this, "As regras padrão não podem ser excluídas. Você pode editá-las e salvar uma versão personalizada.",
                "Regra padrão", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (SigfurDialog.Show(this, $"Excluir a regra '{current.Title}'?", "Bizu", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Rules.Remove(current);
        _rulesView?.Refresh();
        UpdateResultCount();
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (SigfurDialog.Show(this, "Restaurar todas as regras padrão do código original?", "Bizu", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Rules.Clear();
        foreach (var rule in AdjustmentAccountsService.DefaultBizuRules()) Rules.Add(rule.Clone());
        _rulesView?.Refresh();
        RulesGrid.SelectedIndex = 0;
        UpdateResultCount();
    }

    private void Use_Click(object sender, RoutedEventArgs e)
        => UseCurrent();

    private void UseCurrent()
    {
        SelectedRule = Current;
        if (SelectedRule is null)
        {
            SigfurDialog.Show(this, "Selecione uma regra.", "Bizu", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
