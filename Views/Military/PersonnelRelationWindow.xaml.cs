using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views;

namespace SIGFUR.Wpf.Views.Military;

public partial class PersonnelRelationWindow : Window
{
    private sealed class RelationRow : INotifyPropertyChanged
    {
        private int _position;
        private bool _isMarked;
        private string _paymentSystem = "SIPPES";
        public required MilitaryRecord Military { get; init; }
        public int Position
        {
            get => _position;
            set { if (_position == value) return; _position = value; PropertyChanged?.Invoke(this, new(nameof(Position))); }
        }
        public bool IsMarked
        {
            get => _isMarked;
            set { if (_isMarked == value) return; _isMarked = value; PropertyChanged?.Invoke(this, new(nameof(IsMarked))); }
        }
        public string PaymentSystem
        {
            get => _paymentSystem;
            set
            {
                var normalized = PersonnelSystemRelationService.NormalizeSystem(value);
                if (string.Equals(_paymentSystem, normalized, StringComparison.Ordinal)) return;
                _paymentSystem = normalized;
                PropertyChanged?.Invoke(this, new(nameof(PaymentSystem)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly ObservableCollection<RelationRow> _rows = [];
    private ICollectionView? _view;
    private PersonnelRelationPreferences _preferences = new();
    private IReadOnlyList<int> _listOrder = [];
    private Point _dragStart;
    private RelationRow? _dragged;
    private bool _loading = true;
    private readonly HashSet<int> _initialMarkedIds;

    public PersonnelRelationWindow(IEnumerable<int>? initialMarkedIds = null)
    {
        _initialMarkedIds = initialMarkedIds?.Where(x => x > 0).ToHashSet() ?? [];
        InitializeComponent();
        InstitutionalSubtitleText.Text = $"Classifique em lote, respeite a ordem do Listar Militares e gere a relação da {OrganizationIdentity.Name}.";
        App.UiState.Attach(this);
        Loaded += OnLoaded;
        Closing += async (_, _) => await SavePreferencesAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _preferences = await App.Json.LoadAsync<PersonnelRelationPreferences>(App.Paths.PersonnelRelationPreferencesFile) ?? new PersonnelRelationPreferences();
            _preferences.PaymentSystemByMilitaryId ??= [];
            _listOrder = await App.MilitaryPreferences.LoadCustomOrderAsync();
            var military = await App.MilitaryRepository.GetAllAsync();
            await App.MilitaryPreferences.ApplyAsync(military);

            foreach (var item in military)
            {
                _rows.Add(new RelationRow
                {
                    Military = item,
                    IsMarked = _initialMarkedIds.Contains(item.Id),
                    PaymentSystem = _preferences.PaymentSystemByMilitaryId.GetValueOrDefault(item.Id, "SIPPES")
                });
            }
            _view = CollectionViewSource.GetDefaultView(_rows);
            _view.Filter = FilterRow;
            RelationGrid.ItemsSource = _view;

            // Versões antigas iniciavam pela ordem livre do Listar e podiam deixar o Subtenente no fim.
            // Somente uma ordem personalizada desta própria relação é preservada; nos demais casos a
            // abertura começa pela hierarquia real do Exército.
            var initialSort = string.Equals(_preferences.SortMode, "Ordem personalizada desta relação", StringComparison.OrdinalIgnoreCase)
                ? _preferences.SortMode
                : "Ordem salva em Listar Militares";
            SelectSortMode(initialSort);
            OpenAfterGenerateCheck.IsChecked = _preferences.OpenAfterGenerate;
            ApplySortMode(loadSavedCustomOrder: true);
            StatusText.Text = _initialMarkedIds.Count > 0
                ? $"{_initialMarkedIds.Count} militar(es) vieram marcados do Listar Militares. Escolha o sistema e clique em Aplicar sistema."
                : "Todos entram como SIPPES por padrão. Marque vários militares somente se precisar alterar a classificação.";
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha ao abrir Relação Pessoal.", ex);
            SigfurDialog.Show(this, ex.Message, "SIGFUR — Relação Pessoal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _loading = false; RefreshCounters(); }
    }

    private bool FilterRow(object item)
    {
        if (item is not RelationRow row) return false;
        var query = Normalize(SearchBox.Text);
        if (string.IsNullOrWhiteSpace(query)) return true;
        var military = row.Military;
        return Normalize($"{military.Rank} {military.ShortRank} {military.Name} {military.WarName} {military.Cpf} {military.PrecCp}").Contains(query);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _view?.Refresh();
        RefreshCounters();
    }

    private async void SortModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ApplySortMode(loadSavedCustomOrder: true);
        await SavePreferencesAsync();
    }

    private string CurrentSortMode => (SortModeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Hierarquia do Exército";

    private void SelectSortMode(string? mode)
    {
        SortModeBox.SelectedItem = SortModeBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(x => string.Equals(x.Content?.ToString(), mode, StringComparison.OrdinalIgnoreCase))
            ?? SortModeBox.Items[0];
    }

    private void SelectSortModeWithoutApplying(string mode)
    {
        var previous = _loading;
        _loading = true;
        try { SelectSortMode(mode); }
        finally { _loading = previous; }
    }

    private void ApplySortMode(bool loadSavedCustomOrder)
    {
        var records = _rows.Select(x => x.Military).ToList();
        IEnumerable<MilitaryRecord> ordered;
        switch (CurrentSortMode)
        {
            case "Hierarquia do Exército":
                ordered = records.OrderBy(x => x, Comparer<MilitaryRecord>.Create((a, b) => MilitaryRankService.Compare(a.Rank, a.Name, b.Rank, b.Name)));
                break;
            case "Nome (A→Z)":
                ordered = records.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase);
                break;
            case "Ordem personalizada desta relação" when loadSavedCustomOrder && _preferences.OrderedMilitaryIds.Count > 0:
            {
                var relationOrder = _preferences.OrderedMilitaryIds.Select((id, index) => (id, index)).GroupBy(x => x.id).ToDictionary(x => x.Key, x => x.Min(y => y.index));
                ordered = records.OrderBy(x => relationOrder.GetValueOrDefault(x.Id, int.MaxValue)).ThenBy(x => MilitaryRankService.GetOrder(x.Rank)).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase);
                break;
            }
            default:
            {
                var listOrder = _listOrder.Select((id, index) => (id, index)).GroupBy(x => x.id).ToDictionary(x => x.Key, x => x.Min(y => y.index));
                ordered = records.OrderBy(x => listOrder.GetValueOrDefault(x.Id, int.MaxValue)).ThenBy(x => MilitaryRankService.GetOrder(x.Rank)).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase);
                break;
            }
        }
        ReplaceRows(ordered);
    }

    private void ReplaceRows(IEnumerable<MilitaryRecord> ordered)
    {
        var state = _rows.ToDictionary(x => x.Military.Id, x => (x.IsMarked, x.PaymentSystem));
        _rows.Clear();
        foreach (var military in ordered)
        {
            var saved = state.GetValueOrDefault(military.Id, (false, "SIPPES"));
            _rows.Add(new RelationRow { Military = military, IsMarked = saved.Item1, PaymentSystem = saved.Item2 });
        }
        Renumber();
        _view?.Refresh();
    }

    private void Renumber()
    {
        for (var index = 0; index < _rows.Count; index++) _rows[index].Position = index + 1;
        RefreshCounters();
    }

    private void RefreshCounters()
    {
        var visible = _view?.Cast<object>().Count() ?? _rows.Count;
        CountText.Text = $"{visible} de {_rows.Count} militar(es)";
        var sippes = _rows.Count(x => x.PaymentSystem == "SIPPES");
        var siappes = _rows.Count - sippes;
        var marked = _rows.Count(x => x.IsMarked);
        SystemCountText.Text = $"SIPPES: {sippes}  |  SIAPPES: {siappes}  |  Marcados: {marked}";
    }

    private async Task SavePreferencesAsync()
    {
        if (_loading) return;
        _preferences.SortMode = CurrentSortMode;
        _preferences.OpenAfterGenerate = OpenAfterGenerateCheck.IsChecked == true;
        _preferences.OrderedMilitaryIds = _rows.Select(x => x.Military.Id).ToList();
        _preferences.PaymentSystemByMilitaryId = _rows.ToDictionary(x => x.Military.Id, x => x.PaymentSystem);
        await App.Json.SaveAsync(App.Paths.PersonnelRelationPreferencesFile, _preferences);
    }

    private void MarkVisible_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _view?.Cast<RelationRow>() ?? _rows) row.IsMarked = true;
        RefreshCounters();
        StatusText.Text = "Todos os militares visíveis foram marcados. Escolha SIPPES ou SIAPPES e aplique.";
    }

    private void ClearMarks_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.IsMarked = false;
        RelationGrid.UnselectAll();
        RefreshCounters();
        StatusText.Text = "Marcações removidas; as classificações já salvas foram mantidas.";
    }

    private async void ApplySystem_Click(object sender, RoutedEventArgs e)
    {
        var rows = _rows.Where(x => x.IsMarked).ToList();
        if (rows.Count == 0)
            rows = RelationGrid.SelectedItems.Cast<RelationRow>().Distinct().ToList();
        if (rows.Count == 0)
        {
            SigfurDialog.Show(this, "Marque ou selecione um ou mais militares antes de aplicar o sistema.", "Relação SIAPPES / SIPPES", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var system = PersonnelSystemRelationService.NormalizeSystem((PaymentSystemBox.SelectedItem as ComboBoxItem)?.Content?.ToString());
        foreach (var row in rows)
        {
            row.PaymentSystem = system;
            row.IsMarked = false;
        }
        RelationGrid.UnselectAll();
        RefreshCounters();
        await SavePreferencesAsync();
        StatusText.Text = $"{system} aplicado a {rows.Count} militar(es) e salvo para as próximas relações.";
    }


    private async void ApplyHierarchy_Click(object sender, RoutedEventArgs e)
    {
        SelectSortModeWithoutApplying("Hierarquia do Exército");
        ApplySortMode(loadSavedCustomOrder: false);
        await SavePreferencesAsync();
        StatusText.Text = "Hierarquia aplicada: oficiais, Subtenente, Sargentos, Cabos e Soldados na ordem correta.";
    }

    private async void SaveAsListOrder_Click(object sender, RoutedEventArgs e)
    {
        await App.MilitaryPreferences.SaveCustomOrderAsync(_rows.Select(x => x.Military.Id));
        _listOrder = _rows.Select(x => x.Military.Id).ToList();
        StatusText.Text = "Esta ordem agora também será usada em Listar Militares e como ordem inicial do Boletim.";
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private async void MoveSelected(int delta)
    {
        if (RelationGrid.SelectedItem is not RelationRow row) return;
        var index = _rows.IndexOf(row);
        var target = index + delta;
        if (target < 0 || target >= _rows.Count) return;
        _rows.Move(index, target);
        SelectSortModeWithoutApplying("Ordem personalizada desta relação");
        Renumber();
        RelationGrid.SelectedItem = row;
        await SavePreferencesAsync();
    }

    private void RelationGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(RelationGrid);
        _dragged = FindAncestor<DataGridRow>((DependencyObject)e.OriginalSource)?.Item as RelationRow;
    }

    private void RelationGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragged is null) return;
        var point = e.GetPosition(RelationGrid);
        if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(RelationGrid, _dragged, DragDropEffects.Move);
    }

    private async void RelationGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(RelationRow))) return;
        if (!string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            StatusText.Text = "Limpe a pesquisa antes de arrastar, para a posição salva não ficar ambígua.";
            return;
        }
        var dragged = (RelationRow)e.Data.GetData(typeof(RelationRow));
        var target = FindAncestor<DataGridRow>((DependencyObject)e.OriginalSource)?.Item as RelationRow;
        if (target is null || ReferenceEquals(target, dragged)) return;
        _rows.Move(_rows.IndexOf(dragged), _rows.IndexOf(target));
        SelectSortModeWithoutApplying("Ordem personalizada desta relação");
        Renumber();
        RelationGrid.SelectedItem = dragged;
        await SavePreferencesAsync();
        StatusText.Text = "Ordem personalizada salva automaticamente.";
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            SigfurDialog.Show(this, "Não há militares para gerar a relação.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Directory.CreateDirectory(App.Paths.GeneratedDocumentsDirectory);
        var dialog = new SaveFileDialog
        {
            Title = "Salvar Relação Pessoal",
            Filter = "Planilha Excel (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            AddExtension = true,
            FileName = $"Relacao_Pessoal_{DateTime.Now:yyyyMMdd}.xlsx",
            InitialDirectory = App.Paths.GeneratedDocumentsDirectory
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            IsEnabled = false;
            StatusText.Text = "Gerando planilha profissional…";
            await MilitaryExportService.ExportPersonnelRelationAsync(dialog.FileName, _rows.Select(x => x.Military).ToList());
            await SavePreferencesAsync();
            StatusText.Text = "Relação Pessoal gerada com sucesso: " + dialog.FileName;
            if (OpenAfterGenerateCheck.IsChecked == true) ShellService.OpenPath(dialog.FileName);
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha ao gerar Relação Pessoal em Excel.", ex);
            SigfurDialog.Show(this, ex.Message, "SIGFUR — Relação Pessoal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsEnabled = true; }
    }

    private async void GenerateSystemRelation_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            SigfurDialog.Show(this, "Não há militares para gerar a relação.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Directory.CreateDirectory(App.Paths.GeneratedDocumentsDirectory);
        var generatedAt = DateTime.Now;
        var dialog = new SaveFileDialog
        {
            Title = "Salvar relação de militares SIAPPES e SIPPES",
            Filter = "Documento OpenDocument (*.odt)|*.odt",
            DefaultExt = ".odt",
            AddExtension = true,
            FileName = $"Relacao_Militares_SIAPPES_SIPPES_{generatedAt:yyyyMMdd}.odt",
            InitialDirectory = App.Paths.GeneratedDocumentsDirectory
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            IsEnabled = false;
            StatusText.Text = $"Gerando a relação da {OrganizationIdentity.Name}…";
            var items = _rows.Select(x => new PersonnelSystemRelationItem
            {
                Military = x.Military,
                PaymentSystem = x.PaymentSystem
            }).ToList();
            await PersonnelSystemRelationService.ExportOdtAsync(dialog.FileName, items, generatedAt);
            await SavePreferencesAsync();
            StatusText.Text = "Relação SIAPPES/SIPPES gerada com a data de hoje: " + dialog.FileName;
            if (OpenAfterGenerateCheck.IsChecked == true) ShellService.OpenPath(dialog.FileName);
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha ao gerar relação SIAPPES/SIPPES em ODT.", ex);
            SigfurDialog.Show(this, ex.Message, "SIGFUR — Relação SIAPPES / SIPPES", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsEnabled = true; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private static string Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Select(char.ToUpperInvariant).ToArray());
    }
}
