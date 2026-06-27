using System.Windows;
using System.Windows.Controls;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Views.Finance;

public partial class AdjustmentBizuEditorWindow : Window
{
    public AdjustmentBizuRule Rule { get; private set; }

    public AdjustmentBizuEditorWindow(AdjustmentBizuRule rule)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        Rule = rule.Clone();
        TitleBox.Text = Rule.Title;
        LegalBasisBox.Text = Rule.LegalBasis;
        ObservationBox.Text = Rule.Observation;
        Select(VacationAdditionalBox, Rule.VacationAdditional);
        Select(VacationIndemnityBox, Rule.VacationIndemnity);
        Select(ChristmasBox, Rule.ChristmasAdditional);
        Select(PecuniaryBox, Rule.Pecuniary);
    }

    private static void Select(ComboBox box, bool? value)
    {
        var tag = value is null ? "N" : value.Value ? "S" : "0";
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(x => Convert.ToString(x.Tag) == tag);
        box.SelectedIndex = Math.Max(0, box.SelectedIndex);
    }

    private static bool? Read(ComboBox box)
        => box.SelectedItem is ComboBoxItem item ? Convert.ToString(item.Tag) switch { "S" => true, "0" => false, _ => null } : null;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            SigfurDialog.Show(this, "Informe o título da hipótese.", "Bizu do Ajuste de Contas", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Rule = new AdjustmentBizuRule
        {
            Title = TitleBox.Text.Trim(),
            VacationAdditional = Read(VacationAdditionalBox),
            VacationIndemnity = Read(VacationIndemnityBox),
            ChristmasAdditional = Read(ChristmasBox),
            Pecuniary = Read(PecuniaryBox),
            LegalBasis = LegalBasisBox.Text.Trim(),
            Observation = ObservationBox.Text.Trim(),
            IsCustom = true
        };
        DialogResult = true;
    }
}
