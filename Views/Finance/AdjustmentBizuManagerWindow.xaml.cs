using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Finance;

public partial class AdjustmentBizuManagerWindow : Window
{
    public ObservableCollection<AdjustmentBizuRule> Rules { get; } = [];
    public AdjustmentBizuRule? SelectedRule { get; private set; }

    public AdjustmentBizuManagerWindow(IEnumerable<AdjustmentBizuRule> rules, string selectedTitle = "")
    {
        InitializeComponent();
        App.UiState.Attach(this);
        DataContext = this;
        foreach (var rule in rules) Rules.Add(rule.Clone());
        Loaded += (_, _) =>
        {
            RulesGrid.SelectedItem = Rules.FirstOrDefault(x => x.Title.Equals(selectedTitle, StringComparison.OrdinalIgnoreCase)) ?? Rules.FirstOrDefault();
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
    }

    private void RulesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => EditCurrent();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AdjustmentBizuEditorWindow(new AdjustmentBizuRule { IsCustom = true }) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        Rules.Add(dialog.Rule);
        RulesGrid.SelectedItem = dialog.Rule;
    }

    private void Edit_Click(object sender, RoutedEventArgs e) => EditCurrent();

    private void EditCurrent()
    {
        if (Current is not { } current) return;
        var index = Rules.IndexOf(current);
        var dialog = new AdjustmentBizuEditorWindow(current) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        Rules[index] = dialog.Rule;
        RulesGrid.SelectedIndex = index;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } current) return;
        if (SigfurDialog.Show(this, $"Excluir a regra '{current.Title}'?", "Bizu", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Rules.Remove(current);
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (SigfurDialog.Show(this, "Restaurar todas as regras padrão do código original?", "Bizu", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Rules.Clear();
        foreach (var rule in AdjustmentAccountsService.DefaultBizuRules()) Rules.Add(rule.Clone());
        RulesGrid.SelectedIndex = 0;
    }

    private void Use_Click(object sender, RoutedEventArgs e)
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
