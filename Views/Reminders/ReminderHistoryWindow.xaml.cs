using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Windows;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Reminders;

public partial class ReminderHistoryWindow : Window
{
    private readonly ReminderService _service;
    private readonly ObservableCollection<ReminderRecord> _records = [];
    public bool Changed { get; private set; }
    public ReminderHistoryWindow(ReminderService service) { InitializeComponent(); App.UiState.Attach(this); _service = service; HistoryGrid.ItemsSource = _records; Loaded += async (_, _) => await LoadAsync(); }
    private ReminderRecord? Selected => HistoryGrid.SelectedItem as ReminderRecord;
    private async Task LoadAsync() { _records.Clear(); foreach (var item in await _service.LoadHistoryAsync()) _records.Add(item); StatusText.Text = $"{_records.Count} item(ns) no histórico."; }
    private async void Restore_Click(object sender, RoutedEventArgs e) { if (Selected is null) { StatusText.Text = "Selecione um item."; return; } await _service.RestoreFromHistoryAsync(Selected.Id); Changed = true; await LoadAsync(); }
    private async void Delete_Click(object sender, RoutedEventArgs e) { if (Selected is null) { StatusText.Text = "Selecione um item."; return; } if (SigfurDialog.Show(this, "Excluir definitivamente o lembrete selecionado?", "Histórico", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; await _service.DeleteHistoryAsync(Selected.Id); await LoadAsync(); }
    private async void Export_Click(object sender, RoutedEventArgs e) { if (_records.Count == 0) return; var dialog = new SaveFileDialog { Filter = "Texto|*.txt", FileName = "historico_lembretes.txt" }; if (dialog.ShowDialog(this) != true) return; await _service.ExportTextAsync(dialog.FileName, _records); StatusText.Text = "Histórico exportado."; }
}
