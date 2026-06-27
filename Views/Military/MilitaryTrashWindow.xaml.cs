using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.ViewModels;

namespace SIGFUR.Wpf.Views.Military;

public partial class MilitaryTrashWindow : Window
{
    private readonly MilitaryRepository _repository;
    private readonly MilitaryPreferenceService _preferences;
    private readonly TrashViewModel _vm = new();

    public MilitaryTrashWindow(MilitaryRepository repository, MilitaryPreferenceService preferences)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _repository = repository;
        _preferences = preferences;
        DataContext = _vm;
    }

    public bool RestoredAny { get; private set; }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async Task ReloadAsync()
    {
        _vm.StatusText = "Carregando lixeira…";
        _vm.Items.Clear();
        foreach (var entry in await _preferences.LoadTrashAsync()) _vm.Items.Add(entry);
        _vm.Count = _vm.Items.Count;
        _vm.StatusText = _vm.Count == 0 ? "A lixeira está vazia." : $"{_vm.Count} registro(s) disponível(is) para restauração.";
    }

    private async Task RestoreSelectedAsync()
    {
        var entry = _vm.SelectedItem;
        if (entry is null) { Notify("Selecione um registro da lixeira."); return; }
        if (SigfurDialog.Show(this, $"Restaurar {entry.Record.ShortRank} {entry.Record.Name} para a lista ativa?", "SIGFUR", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await _preferences.ApplyAsync(new[] { entry.Record });
            await _repository.RestoreAsync(entry.Record);
            await _preferences.RemoveTrashEntryAsync(entry.Index);
            RestoredAny = true;
            await ReloadAsync();
            _vm.StatusText = "Registro restaurado com o mesmo ID.";
        }
        catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "Não foi possível restaurar", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e) => await RestoreSelectedAsync();
    private async void TrashGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await RestoreSelectedAsync();

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var entry = _vm.SelectedItem;
        if (entry is null) { Notify("Selecione um registro da lixeira."); return; }
        if (SigfurDialog.Show(this, "Remover definitivamente esta entrada da lixeira? Isso não apaga arquivos físicos já preservados.", "SIGFUR", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _preferences.RemoveTrashEntryAsync(entry.Index);
        await ReloadAsync();
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Items.Count == 0) return;
        if (SigfurDialog.Show(this, "Esvaziar toda a lixeira? Esta ação não poderá ser desfeita.", "SIGFUR", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _preferences.ClearTrashAsync();
        await ReloadAsync();
    }

    private void Notify(string message) => SigfurDialog.Show(this, message, "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) ToggleMaximize(); else if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class TrashViewModel : ObservableObject
    {
        private MilitaryTrashEntry? _selectedItem;
        private string _statusText = "Pronto.";
        private int _count;
        public ObservableCollection<MilitaryTrashEntry> Items { get; } = [];
        public MilitaryTrashEntry? SelectedItem { get => _selectedItem; set => SetProperty(ref _selectedItem, value); }
        public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
        public int Count { get => _count; set => SetProperty(ref _count, value); }
    }
}
