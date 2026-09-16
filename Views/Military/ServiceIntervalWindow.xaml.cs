using System.Windows;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Views.Military;

public partial class ServiceIntervalWindow : Window
{
    private readonly ServiceIntervalRecord _source;
    public ServiceIntervalWindow(ServiceIntervalRecord source)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _source = source;
        StartBox.Text = source.StartDate;
        EndBox.Text = source.EndDate;
        ObservationBox.Text = source.Observation;
    }
    public ServiceIntervalRecord Result => new()
    {
        Id = _source.Id, MilitaryId = _source.MilitaryId, StartDate = StartBox.Text.Trim(), EndDate = EndBox.Text.Trim(),
        Observation = ObservationBox.Text.Trim(), Order = _source.Order, Active = true
    };
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (MilitaryFormatting.ParseDate(StartBox.Text) is null) { SigfurDialog.Show(this, "Informe uma data inicial válida.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!string.IsNullOrWhiteSpace(EndBox.Text) && MilitaryFormatting.ParseDate(EndBox.Text) is null) { SigfurDialog.Show(this, "Informe uma data final válida ou deixe o campo vazio.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        DialogResult = true;
    }
}
