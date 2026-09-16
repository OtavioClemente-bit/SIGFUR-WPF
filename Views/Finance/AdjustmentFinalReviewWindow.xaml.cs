using System.Windows;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Finance;

public partial class AdjustmentFinalReviewWindow : Window
{
    public AdjustmentFinalReviewWindow(AdjustmentSimulationResult result)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        MilitaryText.Text = result.Draft.MilitaryName;
        CurrentRankText.Text = result.Draft.CurrentRank;
        HistoricalRankText.Text = result.Draft.HistoricalRank;
        PeriodText.Text = $"{result.EffectiveEntitlementStart:dd/MM/yyyy} a {result.Draft.EntitlementEnd:dd/MM/yyyy} ({result.ComputableDays} dia(s))";
        ReasonText.Text = result.Draft.AdjustmentReason;
        TableText.Text = $"{result.Remuneration.EffectivePeriod} — {result.Remuneration.LegalBasis}";
        QuotaText.Text = result.VacationQuotas.ToString();
        EarningsText.Text = AdjustmentAccountsService.FormatMoney(result.Earnings);
        NetText.Text = AdjustmentAccountsService.FormatMoney(result.Net);
        ComponentsGrid.ItemsSource = result.Components.Where(x => x.CountsTowardTotal);
        SippesGrid.ItemsSource = result.SippesParameters;
        ValidationGrid.ItemsSource = result.Validations;
    }

    private void ConfirmedCheck_Changed(object sender, RoutedEventArgs e)
        => ContinueButton.IsEnabled = ConfirmedCheck.IsChecked == true;

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmedCheck.IsChecked != true) return;
        DialogResult = true;
    }
}
