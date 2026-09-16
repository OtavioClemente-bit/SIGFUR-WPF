using System.Windows;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Finance;

public partial class GratificationProcessesWindow : Window
{
    private readonly GratificationProcessService _service;
    public GratificationProcessRecord? SelectedProcess { get; private set; }

    public GratificationProcessesWindow(GratificationProcessService service)
    {
        InitializeComponent();
        _service = service;
        App.UiState.Attach(this);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async Task ReloadAsync()
    {
        var rows = await _service.ListAsync();
        ProcessGrid.ItemsSource = rows;
        StatusText.Text = $"{rows.Count(x => !x.IsDeleted)} ativo(s) • {rows.Count(x => x.IsDeleted)} excluído(s)";
    }

    private GratificationProcessRecord? Current() => ProcessGrid.SelectedItem as GratificationProcessRecord;

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var row = Current();
        if (row is null) return;
        if (row.IsDeleted)
        {
            SigfurDialog.Show(this, "Desfaça a exclusão antes de abrir este processo.", "Processo excluído", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SelectedProcess = row;
        DialogResult = true;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var row = Current();
        if (row is not null) ShellService.OpenPath(_service.GetProcessDirectory(row.Id));
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var row = Current();
        if (row is null || row.IsDeleted) return;
        await _service.DeleteAsync(row.Id, deleted: true);
        await ReloadAsync();
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var row = Current();
        if (row is null || !row.IsDeleted) return;
        await _service.DeleteAsync(row.Id, deleted: false);
        await ReloadAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
