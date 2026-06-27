using System.ComponentModel;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Controls;
using System.Windows.Documents;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.ViewModels.Military;

namespace SIGFUR.Wpf.Views.Military;

public partial class MilitaryListWindow : Window
{
    private readonly MilitaryRepository _repository;
    private readonly MilitaryPreferenceService _preferences;
    private readonly PaystubService _paystubs;
    private readonly MilitaryListViewModel _vm;
    private bool _loaded;
    private Point _dragStartPoint;
    private bool _dragInProgress;
    private DataGridRow? _dragSourceRow;
    private DataGridRow? _dropTargetRow;
    private DataGridDropIndicatorAdorner? _dropIndicator;
    private bool _dropAfter;
    private MilitarySavedListStore _savedListStore = new();
    private MilitarySavedList? _activeSavedList;
    private bool _latestPaystubDownloading;

    public MilitaryListWindow(MilitaryRepository repository, MilitaryPreferenceService preferences, PaystubService paystubs)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _repository = repository;
        _preferences = preferences;
        _paystubs = paystubs;
        _vm = new MilitaryListViewModel(repository, preferences, App.Log);
        DataContext = _vm;
        FitToAvailableWorkArea();
    }

    private void FitToAvailableWorkArea()
    {
        var work = SystemParameters.WorkArea;
        var availableWidth = Math.Max(480d, double.IsFinite(work.Width) ? work.Width : 1480d);
        var availableHeight = Math.Max(360d, double.IsFinite(work.Height) ? work.Height : 860d);

        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        Width = Math.Clamp(Math.Min(1480d, availableWidth - 20d), MinWidth, availableWidth);
        Height = Math.Clamp(Math.Min(860d, availableHeight - 20d), MinHeight, availableHeight);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            await _vm.LoadAsync();
            await _vm.RestoreSettingsAsync();
            ApplyOrderLockState();
            await LoadSavedListsAsync();
            UpdateMarkedCount();
            UpdateMarkedFilterButton();
            SearchBox.Focus();
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "SIGFUR — Lista de Militares", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        try { await PersistActiveListOrderAsync(); } catch { }
        try { await _vm.SaveSettingsAsync(); } catch { }
    }

    private MilitaryRecord? Selected => MilitaryGrid.SelectedItem as MilitaryRecord ?? _vm.SelectedMilitary;
    private List<MilitaryRecord> SelectedMany
    {
        get
        {
            IEnumerable<MilitaryRecord> scope = _vm.GetAllRecords();
            if (_vm.HasActiveNamedList)
            {
                var allowed = _vm.GetActiveListOrderIds().ToHashSet();
                scope = scope.Where(x => allowed.Contains(x.Id));
            }
            var marked = scope.Where(x => x.IsMarkedForBatch).DistinctBy(x => x.Id).ToList();
            return marked.Count > 0
                ? marked
                : MilitaryGrid.SelectedItems.Cast<MilitaryRecord>().DistinctBy(x => x.Id).ToList();
        }
    }
    private List<MilitaryRecord> StrictSelection()
    {
        var rows = SelectedMany;
        if (rows.Count == 0 && Selected is not null) rows.Add(Selected);
        return rows.DistinctBy(x => x.Id).ToList();
    }

    private int CurrentMarkedCount()
    {
        IEnumerable<MilitaryRecord> scope = _vm.GetAllRecords();
        if (_vm.HasActiveNamedList)
        {
            var allowed = _vm.GetActiveListOrderIds().ToHashSet();
            scope = scope.Where(x => allowed.Contains(x.Id));
        }
        return scope.Count(x => x.IsMarkedForBatch);
    }

    private void UpdateMarkedCount()
    {
        var marked = CurrentMarkedCount();
        if (MarkedCountText is not null)
            MarkedCountText.Text = marked == 1 ? "1 marcado" : $"{marked} marcados";
    }

    private void MarkCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: MilitaryRecord military }) return;

        // O binding TwoWay já gravou a marcação no registro. Atualizamos apenas
        // contadores/filtro depois que o clique terminou, evitando remover a linha
        // durante o PreviewMouseDown (causa do botão funcionar somente uma vez).
        UpdateMarkedCount();
        UpdateMarkedFilterButton();
        var marked = CurrentMarkedCount();
        _vm.StatusText = marked == 0
            ? "Nenhum militar marcado para ações em lote."
            : $"{marked} militar(es) marcados. A marca permanece mesmo ao mudar a pesquisa.";

        if (_vm.MarkedOnly)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _vm.RefreshFilter();
                UpdateMarkedCount();
                UpdateMarkedFilterButton();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void ShowMarked_Click(object sender, RoutedEventArgs e)
    {
        var marked = CurrentMarkedCount();
        if (!_vm.MarkedOnly && marked == 0)
        {
            SigfurDialog.Show(this,
                "Marque um ou mais militares na coluna Marcar antes de usar este filtro.",
                "SIGFUR — Mostrar marcados", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _vm.MarkedOnly = !_vm.MarkedOnly;
        MilitaryGrid.UnselectAll();
        MilitaryGrid.SelectedItem = null;
        _vm.SelectedMilitary = null;
        UpdateMarkedFilterButton();
        _vm.StatusText = _vm.MarkedOnly
            ? $"Mostrando somente {marked} militar(es) marcado(s). Pressione Esc para limpar e voltar à lista completa."
            : "Filtro de marcados desativado. A lista normal foi restaurada.";
    }

    private void UpdateMarkedFilterButton()
    {
        if (ShowMarkedButton is null) return;
        ShowMarkedButton.Content = _vm.MarkedOnly ? "Mostrar todos" : "Mostrar marcados";
        ShowMarkedButton.ToolTip = _vm.MarkedOnly
            ? "Volta à lista completa sem apagar as marcações. Esc apaga todas as marcações e volta à lista normal."
            : "Mostra somente os militares marcados. Pressione Esc para limpar todas as marcações e voltar à lista normal.";
        ShowMarkedButton.FontWeight = _vm.MarkedOnly ? FontWeights.Bold : FontWeights.SemiBold;
    }

    private void ClearMarked_Click(object sender, RoutedEventArgs e) => ClearAllSelection();

    private void ClearAllSelection()
    {
        foreach (var item in _vm.GetAllRecords()) item.IsMarkedForBatch = false;
        _vm.MarkedOnly = false;
        _vm.RefreshFilter();
        MilitaryGrid.UnselectAll();
        MilitaryGrid.SelectedItem = null;
        _vm.SelectedMilitary = null;
        UpdateMarkedCount();
        UpdateMarkedFilterButton();
        Keyboard.ClearFocus();
        _vm.StatusText = "Seleção e marcações removidas. A lista completa foi restaurada.";
    }

    private async Task OpenWalletAsync(int initialTab = 0)
    {
        var selected = Selected;
        if (selected is null) { NotifySelection(); return; }
        var window = new MilitaryWalletWindow(_repository, _paystubs, selected, initialTab) { Owner = this };
        window.Closed += async (_, _) => await _vm.RefreshRecordAsync(selected.Id);
        window.Show();
        window.Activate();
    }

    private async Task EditAsync(MilitaryRecord? military = null)
    {
        military ??= Selected;
        if (military is null) { NotifySelection(); return; }
        var window = new MilitaryEditorWindow(_repository, military, _preferences) { Owner = this };
        if (window.ShowDialog() == true) await _vm.RefreshRecordAsync(window.SavedMilitaryId);
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        var window = new MilitaryEditorWindow(_repository, new MilitaryRecord(), _preferences) { Owner = this };
        if (window.ShowDialog() == true)
        {
            await _vm.LoadAsync();
            _vm.SelectedMilitary = _vm.Military.FirstOrDefault(x => x.Id == window.SavedMilitaryId);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try { await _vm.LoadAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void OpenWallet_Click(object sender, RoutedEventArgs e) => await OpenWalletAsync();
    private async void Edit_Click(object sender, RoutedEventArgs e) => await EditAsync();
    private async void Documents_Click(object sender, RoutedEventArgs e) => await OpenWalletAsync(1);
    private async void OpenLatestPaystub_Click(object sender, RoutedEventArgs e)
    {
        if (_latestPaystubDownloading)
        {
            _vm.StatusText = "Já existe um download de contracheque em andamento.";
            return;
        }

        var selected = Selected;
        if (selected is null) { NotifySelection(); return; }

        try
        {
            _latestPaystubDownloading = true;
            if (LatestPaystubButton is not null) LatestPaystubButton.IsEnabled = false;
            _vm.IsBusy = true;
            _vm.StatusText = $"Baixando último contracheque de {selected.WarName}...";

            var progress = new Progress<CpexPaystubProgress>(p =>
            {
                if (string.IsNullOrWhiteSpace(p.Message)) return;
                _vm.StatusText = p.Total > 0
                    ? $"{p.Message} ({p.Current}/{p.Total})"
                    : p.Message;
            });

            var result = await App.CpexPaystubs.DownloadLatestPaystubForMilitaryAsync(
                selected,
                openAfterDownload: true,
                progress: progress);

            _paystubs.InvalidateCache();
            await _vm.RefreshRecordAsync(selected.Id);

            if (result.Success && !string.IsNullOrWhiteSpace(result.FilePath) && File.Exists(result.FilePath))
            {
                _vm.StatusText = $"Contracheque {result.Month:00}/{result.Year} salvo e aberto: {Path.GetFileName(result.FilePath)}";
                return;
            }

            var details = result.Attempts.Count == 0
                ? string.Empty
                : "\n\nTentativas realizadas:\n" + string.Join("\n", result.Attempts.Take(12));
            _vm.StatusText = result.Message;
            SigfurDialog.Show(this,
                result.Message + details,
                "SIGFUR — Último Contracheque",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _vm.StatusText = "Não foi possível baixar o último contracheque. Nenhum arquivo antigo foi aberto automaticamente.";
            ShowError(ex);
        }
        finally
        {
            _vm.IsBusy = false;
            _latestPaystubDownloading = false;
            if (LatestPaystubButton is not null) LatestPaystubButton.IsEnabled = true;
        }
    }

    private async void Paystubs_Click(object sender, RoutedEventArgs e) => await OpenWalletAsync(6);

    private async void Favorite_Click(object sender, RoutedEventArgs e)
    {
        var selected = Selected;
        if (selected is null) { NotifySelection(); return; }
        try { await _vm.ToggleFavoriteAsync(selected); MilitaryGrid.Items.Refresh(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Note_Click(object sender, RoutedEventArgs e)
    {
        var selected = Selected;
        if (selected is null) { NotifySelection(); return; }
        var window = new TextPromptWindow("Anotação do militar", $"Registre uma observação interna para {selected.ShortRank} {selected.Name}.", selected.Annotation) { Owner = this };
        if (window.ShowDialog() == true)
        {
            try { await _vm.SetNoteAsync(selected, window.Value); }
            catch (Exception ex) { ShowError(ex); }
        }
    }

    private void ColorLegend_Click(object sender, RoutedEventArgs e)
    {
        var sampleYears = new[] { "2023", "2024", "2025", "2026" };
        var window = new Window
        {
            Title = "Legenda de cores do efetivo",
            Width = 620,
            Height = 720,
            MinWidth = 520,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = Background,
            Icon = Icon
        };
        App.UiState.Attach(window, "MilitaryRankColorLegend");
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock
        {
            Text = "Leitura visual do efetivo",
            FontSize = 21,
            FontWeight = FontWeights.Bold,
            Foreground = TryFindResource("PrimaryDarkBrush") as Brush ?? Brushes.DarkSlateBlue
        });
        var panel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var rank in MilitaryRankService.AllRanks.Where(x => MilitaryRankService.GetOrder(x) < 16))
            panel.Children.Add(CreateColorLegendRow(rank, string.Empty));
        panel.Children.Add(new TextBlock
        {
            Text = "Soldados — cor por ano de formação/incorporação",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 6)
        });
        foreach (var year in sampleYears) panel.Children.Add(CreateColorLegendRow("Soldado Efetivo Profissional", year));
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        var close = new Button { Content = "Fechar", MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        if (TryFindResource("PrimaryButtonStyle") is Style style) close.Style = style;
        close.Click += (_, _) => window.Close();
        Grid.SetRow(close, 2);
        root.Children.Add(close);
        window.Content = root;
        window.ShowDialog();
    }

    private FrameworkElement CreateColorLegendRow(string rank, string year)
    {
        var color = MilitaryRankService.GetAutomaticRowColor(rank, year);
        Brush brush;
        try { brush = (Brush)new BrushConverter().ConvertFromString(color)!; } catch { brush = Brushes.White; }
        var border = new Border
        {
            Background = brush,
            BorderBrush = TryFindResource("BorderBrush") as Brush ?? Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 6)
        };
        border.Child = new TextBlock
        {
            Text = MilitaryRankService.GetColorDescription(rank, year),
            FontWeight = FontWeights.SemiBold
        };
        return border;
    }

    private async void Color_Click(object sender, RoutedEventArgs e)
    {
        var selected = StrictSelection();
        if (selected.Count == 0) { NotifySelection(); return; }

        var customColors = selected.Select(x => x.CustomColor?.Trim() ?? string.Empty).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var effectiveColors = selected.Select(x => x.RowColor?.Trim() ?? string.Empty).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var mixedColors = customColors.Count > 1 || effectiveColors.Count > 1;
        var currentCustomColor = customColors.Count == 1 ? customColors[0] : string.Empty;
        var currentEffectiveColor = effectiveColors.Count == 1 ? effectiveColors[0] : "#FFFFFF";

        var window = new RowColorWindow(currentCustomColor, currentEffectiveColor, selected.Count, mixedColors) { Owner = this };
        if (window.ShowDialog() != true) return;
        try
        {
            await _vm.SetColorsAsync(selected, window.SelectedColor);
            MilitaryGrid.Items.Refresh();
            _vm.StatusText = string.IsNullOrWhiteSpace(window.SelectedColor)
                ? $"Destaque manual removido de {selected.Count} militar(es)."
                : $"Cor de destaque aplicada a {selected.Count} militar(es).";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Trash_Click(object sender, RoutedEventArgs e)
    {
        var window = new MilitaryTrashWindow(_repository, _preferences) { Owner = this };
        window.ShowDialog();
        if (window.RestoredAny) await _vm.LoadAsync();
    }

    private async void UndoDelete_Click(object sender, RoutedEventArgs e) => await UndoLastDeleteAsync();

    private async Task UndoLastDeleteAsync()
    {
        try
        {
            var trash = await _preferences.LoadTrashAsync();
            var entry = trash.OrderByDescending(x => x.DeletedAt).ThenByDescending(x => x.Index).FirstOrDefault();
            if (entry is null)
            {
                SigfurDialog.Show(this, "A lixeira está vazia.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (SigfurDialog.Show(this, $"Restaurar {entry.Record.ShortRank} {entry.Record.Name}?", "Desfazer exclusão", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await _preferences.ApplyAsync(new[] { entry.Record });
            await _repository.RestoreAsync(entry.Record);
            await _preferences.RemoveTrashEntryAsync(entry.Index);
            await _vm.LoadAsync();
            _vm.SelectedMilitary = _vm.Military.FirstOrDefault(x => x.Id == entry.Record.Id);
            _vm.StatusText = $"{entry.Record.ShortRank} {entry.Record.Name} restaurado(a).";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Attached_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedMany;
        if (selected.Count == 0 && Selected is not null) selected.Add(Selected);
        if (selected.Count == 0) { NotifySelection(); return; }
        var newValue = !selected.All(x => x.IsAttached);
        var message = newValue
            ? "Marcar os selecionados como Adido/Encostado? O Auxílio-Transporte será bloqueado na próxima gravação."
            : "Retirar a marcação de Adido/Encostado dos selecionados?";
        if (SigfurDialog.Show(this, message, "SIGFUR", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            foreach (var item in selected)
            {
                await _vm.SetAttachedAsync(item, newValue);
                if (newValue)
                {
                    item.ReceivesTransportAid = "Não";
                    item.TransportAidValue = "0.00";
                }
                await _repository.SaveAsync(item);
            }
            MilitaryGrid.Items.Refresh();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.OrderLocked) { NotifyOrderLocked(); return; }
        if (Selected is null) { NotifySelection(); return; }
        await _vm.MoveAsync(Selected, -1);
        await PersistActiveListOrderAsync();
        MilitaryGrid.ScrollIntoView(Selected);
    }

    private async void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.OrderLocked) { NotifyOrderLocked(); return; }
        if (Selected is null) { NotifySelection(); return; }
        await _vm.MoveAsync(Selected, 1);
        await PersistActiveListOrderAsync();
        MilitaryGrid.ScrollIntoView(Selected);
    }

    private async void ResetOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.OrderLocked) { NotifyOrderLocked(); return; }
        if (SigfurDialog.Show(this, "Restaurar a ordem hierárquica por Posto/Graduação e nome?", "SIGFUR", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            await _vm.ResetOrderAsync();
            await PersistActiveListOrderAsync();
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedMany;
        if (selected.Count == 0 && Selected is not null) selected.Add(Selected);
        if (selected.Count == 0) { NotifySelection(); return; }
        var names = string.Join("\n", selected.Take(8).Select(x => $"• {x.ShortRank} {x.Name}"));
        if (selected.Count > 8) names += $"\n• … e mais {selected.Count - 8}";
        var result = SigfurDialog.Show(this,
            $"Excluir {selected.Count} militar(es) da lista ativa?\n\n{names}\n\nUma cópia dos dados será registrada na lixeira JSON antes da exclusão.",
            "Confirmar exclusão", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        try
        {
            foreach (var military in selected) await _vm.RemoveAsync(military);
            await PersistActiveListOrderAsync();
            _vm.StatusText = $"{selected.Count} registro(s) excluído(s).";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Transfer_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedMany;
        if (selected.Count == 0 && Selected is not null) selected.Add(Selected);
        if (selected.Count == 0) { NotifySelection(); return; }
        var summary = selected.Count == 1
            ? $"{selected[0].ShortRank} — {selected[0].Name}"
            : string.Join(", ", selected.GroupBy(x => x.ShortRank).OrderByDescending(x => x.Count()).Select(x => $"{x.Count()} {x.Key}"));
        var window = new TransferMilitaryWindow(selected.Count, summary) { Owner = this };
        if (window.ShowDialog() != true) return;
        var confirmed = SigfurDialog.Show(this,
            "A transferência remove os registros da lista ativa após copiar os dados para Licenciados/Transferidos. Continuar?",
            "Confirmação final", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmed != MessageBoxResult.Yes) return;
        var failures = new List<string>();
        var transferred = new List<int>();
        foreach (var military in selected)
        {
            try { await _repository.TransferToLicensedAsync(military, window.Reason, window.Destination); transferred.Add(military.Id); }
            catch (Exception ex) { failures.Add($"{military.Name}: {ex.Message}"); }
        }
        await _vm.RemoveTransferredAsync(transferred);
        await PersistActiveListOrderAsync();
        if (failures.Count == 0)
            SigfurDialog.Show(this, $"{transferred.Count} militar(es) transferido(s) para LT com sucesso.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
        else
            SigfurDialog.Show(this, $"Transferidos: {transferred.Count}\nFalhas: {failures.Count}\n\n{string.Join("\n", failures.Take(12))}", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void Promote_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedMany;
        if (selected.Count == 0 && Selected is not null) selected.Add(Selected);
        if (selected.Count == 0) { NotifySelection(); return; }
        var ranks = await _repository.GetRanksAsync();
        var window = new RankSelectionWindow(ranks, selected.Count) { Owner = this };
        if (window.ShowDialog() != true) return;
        try
        {
            await _repository.PromoteAsync(selected.Select(x => x.Id), window.SelectedRank);
            await _vm.LoadAsync();
            _vm.StatusText = $"Posto/Graduação atualizado para {selected.Count} militar(es).";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ExportPaystubs_Click(object sender, RoutedEventArgs e)
    {
        var selected = _vm.GetSelectedOrVisible(SelectedMany);
        if (selected.Count == 0) { NotifySelection(); return; }
        var window = new PaystubExportWindow(selected.Count) { Owner = this };
        if (window.ShowDialog() != true) return;
        var progress = new Progress<(int Current, int Total, string Name)>(value => _vm.StatusText = $"Exportando {value.Current}/{value.Total}: {value.Name}");
        try
        {
            _vm.IsBusy = true;
            var result = await _paystubs.ExportAsync(selected, window.Month, window.Year, window.Folder, progress);
            _vm.StatusText = $"Contracheques exportados: {result.Exported} | Falhas: {result.Failures.Count}.";
            SigfurDialog.Show(this,
                $"Pasta: {window.Folder}\n\nExportados: {result.Exported}\nFalhas: {result.Failures.Count}" +
                (result.Failures.Count > 0 ? "\n\nFoi criado um relatório de falhas na pasta escolhida." : string.Empty),
                "Exportar contracheques", MessageBoxButton.OK,
                result.Failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _vm.IsBusy = false; }
    }


    private async void Star_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MilitaryRecord military) return;
        _vm.SelectedMilitary = military;
        try
        {
            await _vm.ToggleFavoriteAsync(military);
            MilitaryGrid.Items.Refresh();
            _vm.StatusText = military.IsFavorite ? $"{military.WarName} favoritado." : $"{military.WarName} removido dos favoritos.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void Columns_Click(object sender, RoutedEventArgs e)
    {
        var entries = new List<ColumnChooserWindow.ColumnEntry>
        {
            new("Marcar", ColMarked, true, true),
            new("Favorito", ColFavorite, true, true),
            new("P/G", ColRank, true, true),
            new("Ano", ColYear, false, true),
            new("Nome completo", ColName, true, true),
            new("CPF", ColCpf, false, true),
            new("PREC-CP", ColPrec, false, true),
            new("IDT", ColIdt, false, true),
            new("Telefone", ColPhone, false, false),
            new("E-mail", ColEmail, false, false),
            new("Endereço", ColAddress, false, false),
            new("Auxílio-Transporte", ColTransport, false, true),
            new("Tempo de serviço", ColService, false, false),
            new("PNR", ColPnr, false, false)
        };
        var window = new ColumnChooserWindow(entries) { Owner = this };
        window.ShowDialog();
    }

    private void Quantity_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedMany;
        var rows = selected.Count > 0 ? selected : _vm.Military.ToList();
        if (rows.Count == 0) { NotifySelection(); return; }
        var scope = selected.Count > 0 ? "militares selecionados" : "militares visíveis";
        new MilitaryQuantityWindow(rows, scope) { Owner = this }.ShowDialog();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var visible = _vm.Military.ToList();
        if (visible.Count == 0) { NotifySelection(); return; }
        var window = new MilitaryExportWindow(SelectedMany, visible) { Owner = this };
        if (window.ShowDialog() == true)
            _vm.StatusText = "Exportação concluída com os campos escolhidos.";
    }


    private async void ExportBenefits_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedMany;
        var rows = selected.Count > 0 ? selected : _vm.Military.ToList();
        if (rows.Count == 0) { NotifySelection(); return; }
        var dialog = new SaveFileDialog
        {
            Title = "Exportar relação especial — Pré-Escolar, Pensão Judicial, PNR e Laranjeira",
            Filter = "Planilha Excel (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            AddExtension = true,
            FileName = $"relacao_pre_escolar_pensao_pnr_laranjeira_{DateTime.Now:yyyyMMdd_HHmm}.xlsx"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _vm.IsBusy = true;
            _vm.StatusText = "Gerando relação especial em planilha profissional...";
            await MilitaryExportService.ExportBenefitsReportAsync(dialog.FileName, rows);
            _vm.StatusText = "Relação especial gerada.";
            var open = SigfurDialog.Show(this,
                $"Relação especial gerada com sucesso.\n\nArquivo:\n{dialog.FileName}\n\nAbrir agora?",
                "Exportar relação especial", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (open == MessageBoxResult.Yes) Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _vm.IsBusy = false; }
    }

    private void ExportOptions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }


    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var modeWindow = new MilitaryImportModeWindow { Owner = this };
        if (modeWindow.ShowDialog() != true) return;
        var mode = modeWindow.SelectedMode;

        var dlg = new OpenFileDialog
        {
            Title = mode switch
            {
                MilitaryImportMode.GoogleForms => "Importar respostas do Forms dos recrutas",
                MilitaryImportMode.Sigfur => "Importar planilha padrão SIGFUR",
                _ => "Importar planilha normal de militares"
            },
            Filter = "Planilhas e CSV (*.xlsx;*.ods;*.csv;*.txt)|*.xlsx;*.ods;*.csv;*.txt|Excel (*.xlsx)|*.xlsx|ODS (*.ods)|*.ods|CSV/TXT (*.csv;*.txt)|*.csv;*.txt|Todos os arquivos (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        var confirmText = mode switch
        {
            MilitaryImportMode.GoogleForms => "Importar como Forms dos recrutas.\n\nO SIGFUR vai reconhecer cabeçalhos do Google Forms, usar Sd Ef Vrv como P/G padrão e aproveitar telefone, e-mail, escolaridade, endereço, CEP e dados bancários.\n\nCPFs já cadastrados serão atualizados, preservando dados antigos quando a planilha vier vazia.",
            MilitaryImportMode.Sigfur => "Importar como planilha SIGFUR.\n\nUse este modo para arquivos exportados pelo próprio SIGFUR. Os cabeçalhos serão reconhecidos automaticamente.",
            _ => "Importar como planilha normal.\n\nOrdem esperada se não houver cabeçalho:\nP/G;Nome;Nome de guerra;CPF;PREC;IDT;Banco;Agência;Conta;Ano;Nascimento;Praça;Endereço;CEP;Telefone;E-mail;Pré-Escolar;Valor Pré;SAT/AT;Valor SAT;PNR"
        };
        var confirm = SigfurDialog.Show(this, confirmText + "\n\nContinuar?", "Importar militares", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            _vm.IsBusy = true;
            _vm.StatusText = "Lendo planilha de importação...";
            var result = await ImportSpreadsheetAsync(dlg.FileName, mode);
            await _vm.LoadAsync();
            var details = result.Failures.Count == 0 ? string.Empty : "\n\nPrimeiras ocorrências:\n" + string.Join("\n", result.Failures.Take(12));
            SigfurDialog.Show(this,
                $"Importação concluída.\n\nIncluídos/atualizados: {result.Ok}\nIgnorados/falhas: {result.Failures.Count}{details}",
                "Importar", MessageBoxButton.OK, result.Failures.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _vm.IsBusy = false; }
    }

    private enum MilitaryImportMode { Sigfur, GoogleForms, Normal }

    private sealed class MilitaryImportModeWindow : Window
    {
        public MilitaryImportMode SelectedMode { get; private set; } = MilitaryImportMode.Sigfur;

        public MilitaryImportModeWindow()
        {
            Title = "Importar militares";
            Width = 560;
            Height = 360;
            MinWidth = 520;
            MinHeight = 320;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Application.Current.TryFindResource("AppBackgroundBrush") as Brush ?? Brushes.White;
            var root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = "Como deseja importar?",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = Application.Current.TryFindResource("PrimaryDarkBrush") as Brush ?? Brushes.DarkBlue
            };
            root.Children.Add(title);

            var panel = new StackPanel { Margin = new Thickness(0, 18, 0, 12) };
            Grid.SetRow(panel, 1);
            root.Children.Add(panel);
            AddChoice(panel, "Planilha SIGFUR", "Arquivo exportado pelo SIGFUR, com cabeçalho padrão e campos completos.", MilitaryImportMode.Sigfur);
            AddChoice(panel, "Forms dos recrutas", "Respostas do Google Forms: nome, CPF, telefone, e-mail, escolaridade, endereço, CEP e dados bancários.", MilitaryImportMode.GoogleForms);
            AddChoice(panel, "Planilha normal", "CSV/XLSX simples na ordem padrão antiga, com ou sem cabeçalho.", MilitaryImportMode.Normal);

            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetRow(bar, 2);
            root.Children.Add(bar);
            var cancel = new Button { Content = "Cancelar", MinWidth = 100, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 7, 12, 7) };
            cancel.Click += (_, _) => { DialogResult = false; Close(); };
            bar.Children.Add(cancel);
            Content = root;
        }

        private void AddChoice(Panel panel, string title, string description, MilitaryImportMode mode)
        {
            var button = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(14, 10, 14, 10),
                Background = Brushes.White,
                BorderBrush = Application.Current.TryFindResource("BorderBrush") as Brush ?? Brushes.LightGray
            };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Bold, FontSize = 13 });
            stack.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Foreground = Application.Current.TryFindResource("MutedBrush") as Brush ?? Brushes.Gray, Margin = new Thickness(0, 3, 0, 0) });
            button.Content = stack;
            button.Click += (_, _) => { SelectedMode = mode; DialogResult = true; Close(); };
            panel.Children.Add(button);
        }
    }

    private async Task<(int Ok, List<string> Failures)> ImportSpreadsheetAsync(string path, MilitaryImportMode mode)
    {
        var failures = new List<string>();
        var ok = 0;
        var table = await SpreadsheetService.ReadTabularFileAsync(path);
        table = table.Where(row => row.Any(cell => !string.IsNullOrWhiteSpace(cell))).ToList();
        if (table.Count == 0) throw new InvalidDataException("A planilha está vazia.");

        var headerRow = table.FirstOrDefault() ?? new List<string>();
        var hasHeader = LooksLikeHeader(headerRow);
        IReadOnlyList<string> headers = hasHeader ? headerRow : Array.Empty<string>();
        var dataRows = hasHeader ? table.Skip(1).ToList() : table;
        var existing = await _repository.GetAllAsync();
        var byCpf = existing.Where(x => !string.IsNullOrWhiteSpace(MilitaryFormatting.Digits(x.Cpf))).GroupBy(x => MilitaryFormatting.Digits(x.Cpf)).ToDictionary(x => x.Key, x => x.First());
        var byPrec = existing.Where(x => !string.IsNullOrWhiteSpace(NormalizeIdentity(x.PrecCp))).GroupBy(x => NormalizeIdentity(x.PrecCp)).ToDictionary(x => x.Key, x => x.First());
        var byIdt = existing.Where(x => !string.IsNullOrWhiteSpace(NormalizeIdentity(x.MilitaryId))).GroupBy(x => NormalizeIdentity(x.MilitaryId)).ToDictionary(x => x.Key, x => x.First());

        for (var i = 0; i < dataRows.Count; i++)
        {
            var row = dataRows[i];
            try
            {
                var record = mode switch
                {
                    MilitaryImportMode.GoogleForms => BuildRecordFromForms(row, headers),
                    MilitaryImportMode.Sigfur => BuildRecordFromHeaders(row, headers.Count > 0 ? headers : DefaultNormalHeaders(), true),
                    _ => headers.Count > 0 ? BuildRecordFromHeaders(row, headers, false) : BuildRecordFromFixed(row)
                };

                NormalizeImportedRecord(record, mode);
                var cpf = MilitaryFormatting.Digits(record.Cpf);
                if (string.IsNullOrWhiteSpace(record.Name) || cpf.Length != 11)
                {
                    failures.Add($"Linha {i + 1 + (hasHeader ? 1 : 0)}: nome ou CPF inválido.");
                    continue;
                }

                if (byCpf.TryGetValue(cpf, out var current)
                    || (!string.IsNullOrWhiteSpace(record.PrecCp) && byPrec.TryGetValue(NormalizeIdentity(record.PrecCp), out current))
                    || (!string.IsNullOrWhiteSpace(record.MilitaryId) && byIdt.TryGetValue(NormalizeIdentity(record.MilitaryId), out current)))
                {
                    record.Id = current.Id;
                    PreserveExistingWhenBlank(record, current);
                }

                await _repository.SaveAsync(record);
                if (record.IsOrange) await _preferences.SetColorAsync(record, "#FFB74D");
                ok++;
                byCpf[MilitaryFormatting.Digits(record.Cpf)] = record;
                if (!string.IsNullOrWhiteSpace(record.PrecCp)) byPrec[NormalizeIdentity(record.PrecCp)] = record;
                if (!string.IsNullOrWhiteSpace(record.MilitaryId)) byIdt[NormalizeIdentity(record.MilitaryId)] = record;
            }
            catch (Exception ex)
            {
                failures.Add($"Linha {i + 1 + (hasHeader ? 1 : 0)}: {ex.Message}");
            }
        }
        return (ok, failures);
    }

    private static MilitaryRecord BuildRecordFromFixed(IReadOnlyList<string> row) => new()
    {
        Rank = GetCell(row, 0),
        Name = GetCell(row, 1),
        WarName = GetCell(row, 2),
        Cpf = GetCell(row, 3),
        PrecCp = GetCell(row, 4),
        MilitaryId = GetCell(row, 5),
        Bank = GetCell(row, 6),
        Agency = GetCell(row, 7),
        Account = GetCell(row, 8),
        FormationYear = GetCell(row, 9),
        BirthDate = GetCell(row, 10),
        EnlistmentDate = GetCell(row, 11),
        Address = GetCell(row, 12),
        ZipCode = GetCell(row, 13),
        Phone = GetCell(row, 14),
        Email = GetCell(row, 15),
        ReceivesPreSchool = GetCell(row, 16),
        PreSchoolValue = GetCell(row, 17),
        ReceivesTransportAid = GetCell(row, 18),
        TransportAidValue = GetCell(row, 19),
        HasPnr = GetCell(row, 20),
        Education = GetCell(row, 21),
        IsOrange = MilitaryRecord.IsYes(GetCell(row, 22))
    };

    private static IReadOnlyList<string> DefaultNormalHeaders() =>
    [
        "P/G", "Nome", "Nome de guerra", "CPF", "PREC", "IDT", "Banco", "Agência", "Conta", "Ano", "Nascimento", "Praça",
        "Endereço", "CEP", "Telefone", "E-mail", "Recebe Pré-Escolar", "Valor Pré-Escolar", "SAT", "Valor SAT", "PNR", "Escolaridade", "Laranjeira"
    ];

    private static MilitaryRecord BuildRecordFromForms(IReadOnlyList<string> row, IReadOnlyList<string> headers)
    {
        if (headers.Count == 0) throw new InvalidDataException("Forms dos recrutas precisa de cabeçalho.");
        var record = new MilitaryRecord
        {
            Rank = Value(row, headers, "posto", "grad", "p/g", "posto graduacao"),
            Name = Value(row, headers, "nome completo", "nome"),
            WarName = Value(row, headers, "nome de guerra", "guerra"),
            Cpf = Value(row, headers, "cpf"),
            PrecCp = Value(row, headers, "prec", "prec-cp", "prec cp"),
            MilitaryId = Value(row, headers, "idt", "identidade militar", "identidade", "rg"),
            BirthDate = Value(row, headers, "data de nascimento", "nascimento"),
            Email = Value(row, headers, "email", "e-mail"),
            Phone = Value(row, headers, "telefone", "celular", "whatsapp"),
            Education = Value(row, headers, "grau de escolaridade", "escolaridade"),
            Address = Value(row, headers, "endereco", "endereço", "logradouro", "rua"),
            ZipCode = Value(row, headers, "cep"),
            Bank = Value(row, headers, "banco"),
            Agency = Value(row, headers, "agencia", "agência"),
            Account = Value(row, headers, "conta", "conta corrente"),
            FormationYear = Value(row, headers, "ano", "turma", "ano de formação"),
            ReceivesPreSchool = "Não",
            PreSchoolValue = "0,00",
            ReceivesTransportAid = "Não",
            TransportAidValue = "0,00",
            HasPnr = "Não"
        };
        return record;
    }

    private static MilitaryRecord BuildRecordFromHeaders(IReadOnlyList<string> row, IReadOnlyList<string> headers, bool sigfur)
    {
        var record = new MilitaryRecord
        {
            Rank = Value(row, headers, "p/g", "pg", "posto", "posto/graduação", "posto graduação"),
            Name = Value(row, headers, "nome completo", "nome"),
            WarName = Value(row, headers, "nome de guerra", "guerra"),
            Cpf = Value(row, headers, "cpf"),
            PrecCp = Value(row, headers, "prec-cp", "prec cp", "prec"),
            MilitaryId = Value(row, headers, "idt", "id militar", "identidade militar"),
            Bank = Value(row, headers, "banco"),
            Agency = Value(row, headers, "agência", "agencia"),
            Account = Value(row, headers, "conta"),
            FormationYear = Value(row, headers, "ano de formação", "ano", "turma"),
            BirthDate = Value(row, headers, "nascimento", "data de nascimento"),
            EnlistmentDate = Value(row, headers, "data de praça", "data praça", "praça", "praca"),
            Address = Value(row, headers, "endereço", "endereco"),
            ZipCode = Value(row, headers, "cep"),
            Phone = Value(row, headers, "telefone", "celular", "whatsapp"),
            Email = Value(row, headers, "e-mail", "email"),
            ReceivesPreSchool = Value(row, headers, "recebe pré-escolar", "pre escolar", "pré-escolar", "recebe pre escolar"),
            PreSchoolValue = Value(row, headers, "valor pré-escolar", "valor pre escolar"),
            ReceivesTransportAid = Value(row, headers, "sat", "auxílio-transporte", "auxilio transporte", "sat / auxílio-transporte", "recebe auxílio transp", "recebe auxilio transporte"),
            TransportAidValue = Value(row, headers, "valor sat", "valor at", "valor sat/at", "valor auxílio-transporte", "valor auxilio transporte"),
            HasPnr = Value(row, headers, "pnr", "possui pnr"),
            Education = Value(row, headers, "escolaridade", "grau de escolaridade"),
            IsOrange = MilitaryRecord.IsYes(Value(row, headers, "laranjeira", "laranja"))
        };
        if (sigfur && string.IsNullOrWhiteSpace(record.ReceivesTransportAid))
            record.ReceivesTransportAid = Value(row, headers, "sat/at", "sat aux transporte");
        return record;
    }

    private static void NormalizeImportedRecord(MilitaryRecord record, MilitaryImportMode mode)
    {
        record.Cpf = MilitaryFormatting.Digits(record.Cpf);
        record.ZipCode = MilitaryFormatting.Digits(record.ZipCode);
        record.Rank = string.IsNullOrWhiteSpace(record.Rank)
            ? (mode == MilitaryImportMode.GoogleForms ? "Soldado Efetivo Variável" : "Soldado Efetivo Profissional")
            : MilitaryRankService.Canonicalize(record.Rank);
        record.Name = NormalizeName(record.Name);
        record.WarName = string.IsNullOrWhiteSpace(record.WarName) ? InferWarName(record.Name) : NormalizeName(record.WarName);
        record.PrecCp = string.IsNullOrWhiteSpace(record.PrecCp) ? "PEND-" + record.Cpf : record.PrecCp.Trim();
        record.MilitaryId = string.IsNullOrWhiteSpace(record.MilitaryId) ? "IDT-PEND-" + record.Cpf : record.MilitaryId.Trim();
        record.FormationYear = string.IsNullOrWhiteSpace(record.FormationYear) && mode == MilitaryImportMode.GoogleForms ? DateTime.Now.Year.ToString(CultureInfo.InvariantCulture) : record.FormationYear.Trim();
        record.BirthDate = MilitaryFormatting.NormalizeDateText(record.BirthDate);
        record.EnlistmentDate = MilitaryFormatting.NormalizeDateText(record.EnlistmentDate);
        record.ReceivesPreSchool = NormalizeYesNo(record.ReceivesPreSchool, "Não");
        record.PreSchoolValue = string.IsNullOrWhiteSpace(record.PreSchoolValue) ? "0,00" : record.PreSchoolValue.Trim();
        record.ReceivesTransportAid = NormalizeYesNo(record.ReceivesTransportAid, "Não");
        record.TransportAidValue = string.IsNullOrWhiteSpace(record.TransportAidValue) ? "0,00" : record.TransportAidValue.Trim();
        record.HasPnr = NormalizeYesNo(record.HasPnr, "Não");
    }

    private static void PreserveExistingWhenBlank(MilitaryRecord imported, MilitaryRecord existing)
    {
        imported.Rank = Keep(imported.Rank, existing.Rank);
        imported.WarName = Keep(imported.WarName, existing.WarName);
        imported.PrecCp = Keep(imported.PrecCp, existing.PrecCp);
        imported.MilitaryId = Keep(imported.MilitaryId, existing.MilitaryId);
        imported.Bank = Keep(imported.Bank, existing.Bank);
        imported.Agency = Keep(imported.Agency, existing.Agency);
        imported.Account = Keep(imported.Account, existing.Account);
        imported.FormationYear = Keep(imported.FormationYear, existing.FormationYear);
        imported.BirthDate = Keep(imported.BirthDate, existing.BirthDate);
        imported.EnlistmentDate = Keep(imported.EnlistmentDate, existing.EnlistmentDate);
        imported.Address = Keep(imported.Address, existing.Address);
        imported.ZipCode = Keep(imported.ZipCode, existing.ZipCode);
        imported.Phone = Keep(imported.Phone, existing.Phone);
        imported.Email = Keep(imported.Email, existing.Email);
        imported.Education = Keep(imported.Education, existing.Education);
        imported.PhotoPath = existing.PhotoPath;
        imported.IsFavorite = existing.IsFavorite;
        imported.IsAttached = existing.IsAttached;
        imported.CustomColor = existing.CustomColor;
        imported.Annotation = existing.Annotation;
        if (string.IsNullOrWhiteSpace(imported.ReceivesPreSchool) || imported.ReceivesPreSchool == "Não") imported.ReceivesPreSchool = existing.ReceivesPreSchool;
        if (string.IsNullOrWhiteSpace(imported.PreSchoolValue) || imported.PreSchoolValue == "0,00" || imported.PreSchoolValue == "0.00") imported.PreSchoolValue = existing.PreSchoolValue;
        if (string.IsNullOrWhiteSpace(imported.ReceivesTransportAid) || imported.ReceivesTransportAid == "Não") imported.ReceivesTransportAid = existing.ReceivesTransportAid;
        if (string.IsNullOrWhiteSpace(imported.TransportAidValue) || imported.TransportAidValue == "0,00" || imported.TransportAidValue == "0.00") imported.TransportAidValue = existing.TransportAidValue;
        if (string.IsNullOrWhiteSpace(imported.HasPnr) || imported.HasPnr == "Não") imported.HasPnr = existing.HasPnr;
    }

    private static string Keep(string? incoming, string? existing)
        => string.IsNullOrWhiteSpace(incoming) ? (existing ?? string.Empty) : incoming.Trim();

    private static string GetCell(IReadOnlyList<string> row, int index)
        => index >= 0 && index < row.Count ? (row[index] ?? string.Empty).Trim() : string.Empty;

    private static string Value(IReadOnlyList<string> row, IReadOnlyList<string> headers, params string[] names)
    {
        var index = HeaderIndex(headers, names);
        return index >= 0 ? GetCell(row, index) : string.Empty;
    }

    private static int HeaderIndex(IReadOnlyList<string> headers, params string[] names)
    {
        var normalized = headers.Select(NormalizeHeader).ToList();
        var wanted = names.Select(NormalizeHeader).Where(x => x.Length > 0).ToList();
        for (var i = 0; i < normalized.Count; i++)
        {
            if (wanted.Any(w => normalized[i].Equals(w, StringComparison.OrdinalIgnoreCase))) return i;
        }
        for (var i = 0; i < normalized.Count; i++)
        {
            if (wanted.Any(w => normalized[i].Contains(w, StringComparison.OrdinalIgnoreCase) || w.Contains(normalized[i], StringComparison.OrdinalIgnoreCase))) return i;
        }
        return -1;
    }

    private static bool LooksLikeHeader(IReadOnlyList<string> row)
    {
        var text = string.Join(" ", row.Select(NormalizeHeader));
        return text.Contains("nome", StringComparison.OrdinalIgnoreCase)
            && (text.Contains("cpf", StringComparison.OrdinalIgnoreCase) || text.Contains("prec", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeHeader(string? value)
    {
        var s = RemoveAccents(value ?? string.Empty).ToLowerInvariant();
        return Regex.Replace(s, "[^a-z0-9]+", " ").Trim();
    }

    private static string NormalizeIdentity(string? value)
        => Regex.Replace(RemoveAccents(value ?? string.Empty).ToUpperInvariant(), "[^A-Z0-9]", string.Empty);

    private static string NormalizeName(string? value)
        => Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ").ToUpper(CultureInfo.GetCultureInfo("pt-BR"));

    private static string InferWarName(string name)
    {
        var parts = NormalizeName(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? string.Empty : parts[^1];
    }

    private static string NormalizeYesNo(string? value, string fallback)
    {
        var n = NormalizeHeader(value);
        if (string.IsNullOrWhiteSpace(n)) return fallback;
        if (n is "sim" or "s" or "yes" or "y" or "1" or "recebe" or "verdadeiro" or "true") return "Sim";
        if (n is "nao" or "n" or "no" or "0" or "nao recebe" or "false" or "falso") return "Não";
        if (n.Contains("recebe") && !n.Contains("nao")) return "Sim";
        return fallback;
    }

    private static string RemoveAccents(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(ch);
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private void CopyFormat_Click(object sender, RoutedEventArgs e)
    {
        var rows = StrictSelection();
        if (rows.Count == 0) { NotifySelection(); return; }
        var window = new CopyFormatWindow(rows) { Owner = this };
        if (window.ShowDialog() == true)
            _vm.StatusText = $"Formato personalizado de {rows.Count} militar(es) copiado.";
    }

    private void PaystubTools_Click(object sender, RoutedEventArgs e)
    {
        // Sem seleção: abre a central completa. Com seleção: trabalha somente com os marcados.
        var selected = SelectedMany;
        var rows = selected.Count > 0 ? selected : _vm.Military.ToList();
        var window = new PaystubCenterWindow(_repository, _paystubs, rows, 0, selected.Count > 0) { Owner = this };
        window.Show();
    }

    private void DownloadSelectedPaystub_Click(object sender, RoutedEventArgs e)
    {
        var selected = Selected;
        if (selected is null) { NotifySelection(); return; }
        var window = new PaystubCenterWindow(_repository, _paystubs, new[] { selected }, 0, true) { Owner = this };
        window.Show();
    }

    private void DownloadMonthlyPaystubs_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedMany;
        var rows = selected.Count > 0 ? selected : _vm.Military.ToList();
        var window = new PaystubCenterWindow(_repository, _paystubs, rows, 1, selected.Count > 0) { Owner = this };
        window.Show();
    }

    private void ExternalPaystub_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedMany;
        var rows = selected.Count > 0 ? selected : _vm.Military.ToList();
        var window = new PaystubCenterWindow(_repository, _paystubs, rows, 2, selected.Count > 0) { Owner = this };
        window.Show();
    }

    private void PaystubAudit_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedMany;
        var rows = selected.Count > 0 ? selected : _vm.Military.ToList();
        if (rows.Count == 0) { NotifySelection(); return; }
        _vm.StatusText = $"Abrindo auditoria de {rows.Count} militar(es)...";
        var window = new PaystubAuditWindow(_repository, _paystubs, rows, App.Paths.PaystubsDirectory) { Owner = this };
        window.Show();
    }

    private void GenerateDocuments_Click(object sender, RoutedEventArgs e)
        => OpenDocumentGeneration(GeneratedDocumentType.TransportAid);

    private void GenerateTransportDocument_Click(object sender, RoutedEventArgs e)
        => OpenDocumentGeneration(GeneratedDocumentType.TransportAid);
    private void GeneratePecuniaryDocument_Click(object sender, RoutedEventArgs e)
        => OpenDocumentGeneration(GeneratedDocumentType.PecuniaryCompensation);
    private void GeneratePostalDocument_Click(object sender, RoutedEventArgs e)
        => OpenDocumentGeneration(GeneratedDocumentType.PostalLabel);
    private void GeneratePaymentCopyDocument_Click(object sender, RoutedEventArgs e)
        => OpenDocumentGeneration(GeneratedDocumentType.AuthenticPaymentCopy);
    private void GenerateChristmasDocument_Click(object sender, RoutedEventArgs e)
        => OpenDocumentGeneration(GeneratedDocumentType.AdvanceChristmasBonus);
    private void GenerateCoverDocument_Click(object sender, RoutedEventArgs e)
        => OpenDocumentGeneration(GeneratedDocumentType.CoverSheet);
    private void GenerateEaRequestDocument_Click(object sender, RoutedEventArgs e)
        => OpenDocumentGeneration(GeneratedDocumentType.ExercisePreviousRequest);
    private void GeneratePensionWorksheetDocument_Click(object sender, RoutedEventArgs e)
        => OpenDocumentGeneration(GeneratedDocumentType.JudicialPensionWorksheet);
    private void GenerateIndexDocument_Click(object sender, RoutedEventArgs e)
        => OpenDocumentGeneration(GeneratedDocumentType.RemissiveIndex);
    private void GenerateGratDiexDocument_Click(object sender, RoutedEventArgs e)
        => OpenDocumentGeneration(GeneratedDocumentType.GratificationDiex);
    private void GenerateGratMapDocument_Click(object sender, RoutedEventArgs e)
        => OpenDocumentGeneration(GeneratedDocumentType.GratificationMap);

    private void OpenDocumentGeneration(GeneratedDocumentType initialType)
    {
        var rows = StrictSelection();
        if (rows.Count == 0) { NotifySelection(); return; }
        var window = new DocumentGenerationWindow(rows, initialType) { Owner = this };
        window.ShowDialog();
    }

    private void LegacyTools_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var header = new MenuItem { Header = "Recursos complementares", IsEnabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new Separator());
        foreach (var label in new[]
        {
            "Central SIPPES / Ficha Financeira",
            "Baixar contracheque automático",
            "Baixar contracheque do mês em lote",
            "Contracheque pessoa de fora",
            "Auditoria dos contracheques",
            "Importar / exportar / copiar formato avançado",
            "OCR de certidão",
            "Geração completa de documentos antigos",
            "Editor de links e automações web"
        })
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => await OpenLegacyListAsync(label);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private Task OpenLegacyListAsync(string feature)
    {
        // Todos os atalhos desta lista apontam para telas WPF nativas. A versão
        // anterior ainda abria o programa Python e gerava a mensagem de executável
        // não encontrado. Esse caminho foi definitivamente removido daqui.
        var selected = StrictSelection();
        var scope = selected.Count > 0 ? selected : _vm.Military.ToList();
        try
        {
            switch (feature)
            {
                case "Central SIPPES / Ficha Financeira":
                case "Baixar contracheque automático":
                    new PaystubCenterWindow(_repository, _paystubs, scope, initialTab: 0, restrictedToSelection: selected.Count > 0) { Owner = this }.Show();
                    break;
                case "Baixar contracheque do mês em lote":
                    new PaystubCenterWindow(_repository, _paystubs, scope, initialTab: 1, restrictedToSelection: selected.Count > 0) { Owner = this }.Show();
                    break;
                case "Contracheque pessoa de fora":
                    new PaystubCenterWindow(_repository, _paystubs, scope, initialTab: 2, restrictedToSelection: selected.Count > 0) { Owner = this }.Show();
                    break;
                case "Auditoria dos contracheques":
                    new PaystubAuditWindow(_repository, _paystubs, scope, App.Paths.PaystubsDirectory) { Owner = this }.ShowDialog();
                    break;
                case "Importar / exportar / copiar formato avançado":
                    new MilitaryExportWindow(selected, _vm.Military.ToList()) { Owner = this }.ShowDialog();
                    break;
                case "OCR de certidão":
                    if (selected.Count != 1) { Notify("Selecione exatamente um militar para abrir a carteira e incluir a certidão."); break; }
                    var wallet = new MilitaryWalletWindow(_repository, _paystubs, selected[0], initialTab: 1) { Owner = this };
                    wallet.Closed += async (_, _) => await _vm.RefreshRecordAsync(selected[0].Id);
                    wallet.Show();
                    wallet.Activate();
                    break;
                case "Geração completa de documentos antigos":
                    if (selected.Count == 0) { NotifySelection(); break; }
                    new DocumentGenerationWindow(selected, GeneratedDocumentType.TransportAid) { Owner = this }.ShowDialog();
                    break;
                case "Editor de links e automações web":
                    new PaystubCenterWindow(_repository, _paystubs, scope, initialTab: 3, restrictedToSelection: selected.Count > 0) { Owner = this }.Show();
                    break;
                default:
                    Notify("Este recurso já foi incorporado às telas nativas do SIGFUR.");
                    break;
            }
        }
        catch (Exception ex) { ShowError(ex); }
        return Task.CompletedTask;
    }


    private async Task LoadSavedListsAsync()
    {
        _savedListStore = await App.Json.LoadAsync<MilitarySavedListStore>(App.Paths.NamedMilitaryListsFile) ?? new MilitarySavedListStore();
        _savedListStore.Lists ??= [];
        _savedListStore.Lists = _savedListStore.Lists
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        RefreshSavedListsCombo(_savedListStore.LastOpenedListId);
        var last = _savedListStore.Lists.FirstOrDefault(x => string.Equals(x.Id, _savedListStore.LastOpenedListId, StringComparison.OrdinalIgnoreCase));
        if (last is not null) ApplySavedList(last);
    }

    private void RefreshSavedListsCombo(string? selectedId = null)
    {
        SavedListsCombo.ItemsSource = null;
        SavedListsCombo.ItemsSource = _savedListStore.Lists;
        SavedListsCombo.SelectedItem = _savedListStore.Lists.FirstOrDefault(x => string.Equals(x.Id, selectedId, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplySavedList(MilitarySavedList savedList)
    {
        _activeSavedList = savedList;
        _savedListStore.LastOpenedListId = savedList.Id;
        SavedListsCombo.SelectedItem = savedList;
        _vm.ApplyNamedList(savedList);
        UpdateMarkedCount();
        _vm.StatusText = $"Lista “{savedList.Name}” aberta. Arraste uma ou várias linhas para definir a ordem.";
    }

    private async void OpenSavedList_Click(object sender, RoutedEventArgs e)
    {
        if (SavedListsCombo.SelectedItem is not MilitarySavedList savedList)
        {
            SigfurDialog.Show(this, "Escolha uma lista salva.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ApplySavedList(savedList);
        await App.Json.SaveAsync(App.Paths.NamedMilitaryListsFile, _savedListStore);
    }

    private async void ImportSavedPositions_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Importar posições do Listar Militares em Python",
            Filter = "Ordem do Listar Militares (ordem_custom.json)|ordem_custom.json|Arquivos JSON (*.json)|*.json",
            FileName = "ordem_custom.json",
            InitialDirectory = Directory.Exists(App.Paths.DataDirectory) ? App.Paths.DataDirectory : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var importedIds = await ReadPositionIdsAsync(dialog.FileName);
            if (importedIds.Count == 0)
            {
                SigfurDialog.Show(this,
                    "O JSON não contém uma lista válida na chave 'ordem'. Selecione o ordem_custom.json salvo pelo programa em Python.",
                    "Importar posições", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var currentIds = _vm.GetAllRecords().Select(x => x.Id).Where(x => x > 0).Distinct().ToList();
            var currentSet = currentIds.ToHashSet();
            var orderedIds = importedIds.Where(currentSet.Contains).Distinct().ToList();
            if (orderedIds.Count == 0)
            {
                SigfurDialog.Show(this,
                    "Nenhum ID do arquivo corresponde aos militares do banco atualmente aberto. Nenhuma posição foi alterada.",
                    "Importar posições", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var positioned = orderedIds.ToHashSet();
            orderedIds.AddRange(currentIds.Where(id => !positioned.Contains(id)));
            var savedList = new MilitarySavedList
            {
                Name = CreateImportedListName(dialog.FileName),
                OrderedMilitaryIds = orderedIds,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            _savedListStore.Lists.Add(savedList);
            _savedListStore.Lists = _savedListStore.Lists.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            ApplySavedList(savedList);
            RefreshSavedListsCombo(savedList.Id);
            await App.Json.SaveAsync(App.Paths.NamedMilitaryListsFile, _savedListStore);

            var ignored = importedIds.Count(id => !currentSet.Contains(id));
            _vm.StatusText = $"Posições importadas: {positioned.Count} militar(es); {currentIds.Count - positioned.Count} novo(s) acrescentado(s) ao final.";
            var detail = ignored > 0 ? $"\n\n{ignored} ID(s) antigos não existem mais no banco e foram ignorados." : string.Empty;
            SigfurDialog.Show(this,
                $"A lista “{savedList.Name}” foi criada e aberta com {positioned.Count} posições recuperadas.{detail}\n\nSomente a ordem foi importada; nenhum cadastro foi alterado.",
                "Posições importadas", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, $"Não foi possível importar as posições.\n\n{ex.Message}", "Importar posições", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static async Task<List<int>> ReadPositionIdsAsync(string path)
    {
        var root = await App.Json.LoadNodeAsync(path);
        JsonArray? values = root as JsonArray;
        if (root is JsonObject obj)
        {
            values = obj.FirstOrDefault(x => x.Key.Equals("ordem", StringComparison.OrdinalIgnoreCase)
                || x.Key.Equals("order", StringComparison.OrdinalIgnoreCase)
                || x.Key.Equals("customOrder", StringComparison.OrdinalIgnoreCase)).Value as JsonArray;
        }

        if (values is null) return [];
        var ids = new List<int>();
        foreach (var value in values)
        {
            if (value is not JsonValue jsonValue) continue;
            if (jsonValue.TryGetValue<int>(out var number) && number > 0)
            {
                ids.Add(number);
                continue;
            }
            if (jsonValue.TryGetValue<string>(out var text) && int.TryParse(text, out number) && number > 0)
                ids.Add(number);
        }
        return ids.Distinct().ToList();
    }

    private string CreateImportedListName(string path)
    {
        var date = File.GetLastWriteTime(path);
        var baseName = $"Ordem Python {date:dd-MM-yyyy HH'h'mm}";
        var name = baseName;
        var suffix = 2;
        while (_savedListStore.Lists.Any(x => x.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
            name = $"{baseName} ({suffix++})";
        return name;
    }

    private async void SaveNewList_Click(object sender, RoutedEventArgs e)
    {
        var selectedIds = SelectedMany.Select(x => x.Id).ToHashSet();
        var orderedIds = selectedIds.Count > 0
            ? _vm.Military.Where(x => selectedIds.Contains(x.Id)).Select(x => x.Id).ToList()
            : _vm.GetCurrentVisibleOrderIds().ToList();
        if (orderedIds.Count == 0)
        {
            SigfurDialog.Show(this, "Não há militares visíveis para salvar.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var prompt = new TextPromptWindow("Salvar minha lista", "Dê um nome para esta relação. Depois você poderá arrastar as linhas e clicar em Atualizar lista.") { Owner = this };
        if (prompt.ShowDialog() != true || string.IsNullOrWhiteSpace(prompt.Value)) return;
        if (_savedListStore.Lists.Any(x => string.Equals(x.Name, prompt.Value, StringComparison.CurrentCultureIgnoreCase)))
        {
            SigfurDialog.Show(this, "Já existe uma lista com esse nome.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var savedList = new MilitarySavedList
        {
            Name = prompt.Value.Trim(),
            OrderedMilitaryIds = orderedIds,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
        _savedListStore.Lists.Add(savedList);
        _savedListStore.Lists = _savedListStore.Lists.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        ApplySavedList(savedList);
        RefreshSavedListsCombo(savedList.Id);
        await App.Json.SaveAsync(App.Paths.NamedMilitaryListsFile, _savedListStore);
    }

    private async void UpdateSavedList_Click(object sender, RoutedEventArgs e)
    {
        var savedList = _activeSavedList ?? SavedListsCombo.SelectedItem as MilitarySavedList;
        if (savedList is null)
        {
            SigfurDialog.Show(this, "Abra uma lista salva antes de atualizá-la.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!_vm.HasActiveNamedList || !string.Equals(_vm.ActiveListId, savedList.Id, StringComparison.OrdinalIgnoreCase))
            ApplySavedList(savedList);
        savedList.OrderedMilitaryIds = _vm.GetActiveListOrderIds().ToList();
        savedList.UpdatedAt = DateTime.Now;
        _savedListStore.LastOpenedListId = savedList.Id;
        await App.Json.SaveAsync(App.Paths.NamedMilitaryListsFile, _savedListStore);
        RefreshSavedListsCombo(savedList.Id);
        _vm.StatusText = $"Lista “{savedList.Name}” salva com {savedList.OrderedMilitaryIds.Count} militar(es), na ordem atual.";
    }

    private async void DeleteSavedList_Click(object sender, RoutedEventArgs e)
    {
        var savedList = SavedListsCombo.SelectedItem as MilitarySavedList ?? _activeSavedList;
        if (savedList is null) return;
        if (SigfurDialog.Show(this, $"Excluir a lista “{savedList.Name}”? Os cadastros dos militares não serão apagados.", "SIGFUR", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _savedListStore.Lists.RemoveAll(x => string.Equals(x.Id, savedList.Id, StringComparison.OrdinalIgnoreCase));
        if (_activeSavedList?.Id == savedList.Id)
        {
            _activeSavedList = null;
            _vm.ApplyNamedList(null);
            UpdateMarkedCount();
            _savedListStore.LastOpenedListId = string.Empty;
            RefreshSavedListsCombo();
        }
        else
        {
            _savedListStore.LastOpenedListId = _activeSavedList?.Id ?? string.Empty;
            RefreshSavedListsCombo(_activeSavedList?.Id);
        }
        await App.Json.SaveAsync(App.Paths.NamedMilitaryListsFile, _savedListStore);
    }

    private async void ShowAllList_Click(object sender, RoutedEventArgs e)
    {
        await PersistActiveListOrderAsync();
        _activeSavedList = null;
        _savedListStore.LastOpenedListId = string.Empty;
        SavedListsCombo.SelectedItem = null;
        _vm.ApplyNamedList(null);
        UpdateMarkedCount();
        await App.Json.SaveAsync(App.Paths.NamedMilitaryListsFile, _savedListStore);
    }

    private async Task PersistActiveListOrderAsync()
    {
        if (_activeSavedList is null || !_vm.HasActiveNamedList) return;
        _activeSavedList.OrderedMilitaryIds = _vm.GetActiveListOrderIds().ToList();
        _activeSavedList.UpdatedAt = DateTime.Now;
        _savedListStore.LastOpenedListId = _activeSavedList.Id;
        await App.Json.SaveAsync(App.Paths.NamedMilitaryListsFile, _savedListStore);
        RefreshSavedListsCombo(_activeSavedList.Id);
    }


    private void CopyTextBlock_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not TextBlock textBlock) return;
        var text = (textBlock.Tag?.ToString() ?? textBlock.Text)?.Trim();
        if (string.IsNullOrWhiteSpace(text) && textBlock.Inlines.Count > 0)
            text = string.Concat(textBlock.Inlines.OfType<Run>().Select(run => run.Text)).Trim();
        if (string.IsNullOrWhiteSpace(text)
            || text == "—"
            || text.Equals("Não informado", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Sem anotação", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Data de praça não informada", StringComparison.OrdinalIgnoreCase)) return;
        Clipboard.SetText(text);
        _vm.StatusText = "Dado copiado.";
        e.Handled = true;
    }

    private void CopyCpf_Click(object sender, RoutedEventArgs e) => CopyValue(Selected?.FormattedCpf, "CPF");
    private void CopyPrec_Click(object sender, RoutedEventArgs e) => CopyValue(Selected?.PrecCp, "PREC-CP");
    private void CopyIdt_Click(object sender, RoutedEventArgs e) => CopyValue(Selected?.MilitaryId, "IDT");

    private void CopySummary_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item is null) { NotifySelection(); return; }
        var text = new StringBuilder()
            .AppendLine($"{item.ShortRank} {item.Name}")
            .AppendLine($"Nome de guerra: {item.WarName}")
            .AppendLine($"CPF: {item.FormattedCpf}")
            .AppendLine($"PREC-CP: {item.PrecCp}")
            .AppendLine($"IDT: {item.MilitaryId}")
            .AppendLine($"Telefone: {item.Phone}")
            .AppendLine($"E-mail: {item.Email}")
            .AppendLine($"Endereço: {item.Address}")
            .AppendLine($"Tempo de serviço: {item.ServiceTimeText}")
            .ToString().Trim();
        Clipboard.SetText(text);
        _vm.StatusText = "Dados do militar copiados.";
    }

    private void CopyValue(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) { SigfurDialog.Show(this, $"{label} não informado.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        Clipboard.SetText(value);
        _vm.StatusText = $"{label} copiado.";
    }

    private async void MilitaryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await OpenWalletAsync();

    private void MilitaryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selecionar uma linha e marcar para lote são ações independentes. A versão
        // anterior marcava automaticamente qualquer linha selecionada e isso criava
        // um ciclo com o filtro “Mostrar marcados”, impedindo novas marcações.
        _vm.SelectedMilitary = MilitaryGrid.SelectedItem as MilitaryRecord;
        UpdateMarkedCount();
    }

    private void MilitaryGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = VisualTreeUtilities.FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is MilitaryRecord military && !row.IsSelected)
        {
            MilitaryGrid.SelectedItems.Clear();
            row.IsSelected = true;
            _vm.SelectedMilitary = military;
        }
    }

    private void MilitaryGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm.OrderLocked) { _dragSourceRow = null; return; }
        _dragStartPoint = e.GetPosition(MilitaryGrid);
        _dragInProgress = false;
        _dragSourceRow = VisualTreeUtilities.FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);

        // Não inicia arraste quando o clique partiu de um botão, barra de rolagem ou editor.
        if (VisualTreeUtilities.FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null
            || VisualTreeUtilities.FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) is not null
            || VisualTreeUtilities.FindAncestor<ScrollBar>(e.OriginalSource as DependencyObject) is not null
            || VisualTreeUtilities.FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null)
        {
            _dragSourceRow = null;
            return;
        }

        // Preserva a seleção múltipla durante o arraste e deixa Ctrl/Shift funcionarem
        // com o comportamento nativo do DataGrid.
        if (_dragSourceRow?.Item is MilitaryRecord clicked)
        {
            var modifiers = Keyboard.Modifiers;
            var rangeSelection = modifiers.HasFlag(ModifierKeys.Control) || modifiers.HasFlag(ModifierKeys.Shift);
            if (!rangeSelection && _dragSourceRow.IsSelected && MilitaryGrid.SelectedItems.Count > 1)
            {
                _vm.SelectedMilitary = clicked;
                e.Handled = true;
            }
            else if (!rangeSelection && !_dragSourceRow.IsSelected)
            {
                MilitaryGrid.UnselectAll();
                _dragSourceRow.IsSelected = true;
                _vm.SelectedMilitary = clicked;
                e.Handled = true;
            }
        }
    }

    private void MilitaryGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_vm.OrderLocked) return;
        if (e.LeftButton != MouseButtonState.Pressed || _dragInProgress || _dragSourceRow is null) return;
        var current = e.GetPosition(MilitaryGrid);
        if (Math.Abs(current.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var selected = SelectedMany;
        if (selected.Count == 0 && _dragSourceRow.Item is MilitaryRecord rowMilitary) selected.Add(rowMilitary);
        if (selected.Count == 0) return;

        _dragInProgress = true;
        _vm.StatusText = selected.Count == 1
            ? "Arraste até a linha desejada. A faixa azul mostra a posição final."
            : $"Arrastando {selected.Count} militares. A faixa azul mostra a posição final.";
        try
        {
            DragDrop.DoDragDrop(MilitaryGrid, new DataObject("SIGFUR.MilitaryRows", selected), DragDropEffects.Move);
        }
        finally
        {
            ClearDropIndicator();
            _dragInProgress = false;
            _dragSourceRow = null;
        }
    }

    private void MilitaryGrid_DragOver(object sender, DragEventArgs e)
    {
        if (_vm.OrderLocked) { e.Effects = DragDropEffects.None; ClearDropIndicator(); e.Handled = true; return; }
        if (!e.Data.GetDataPresent("SIGFUR.MilitaryRows"))
        {
            e.Effects = DragDropEffects.None;
            ClearDropIndicator();
            e.Handled = true;
            return;
        }

        var gridPoint = e.GetPosition(MilitaryGrid);
        var (row, after) = ResolveDropTarget(e.OriginalSource as DependencyObject, gridPoint);
        if (row?.Item is not MilitaryRecord)
        {
            e.Effects = DragDropEffects.None;
            ClearDropIndicator();
            e.Handled = true;
            return;
        }

        ShowDropIndicator(row, after);
        AutoScrollDuringDrag(gridPoint.Y);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void MilitaryGrid_DragLeave(object sender, DragEventArgs e)
    {
        var position = e.GetPosition(MilitaryGrid);
        if (position.X < 0 || position.Y < 0 || position.X > MilitaryGrid.ActualWidth || position.Y > MilitaryGrid.ActualHeight)
            ClearDropIndicator();
    }

    private async void MilitaryGrid_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (_vm.OrderLocked) { NotifyOrderLocked(); return; }
            if (!e.Data.GetDataPresent("SIGFUR.MilitaryRows")) return;
            var row = _dropTargetRow ?? VisualTreeUtilities.FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row?.Item is not MilitaryRecord target) return;
            if (e.Data.GetData("SIGFUR.MilitaryRows") is not List<MilitaryRecord> moving) return;

            await _vm.MoveItemsAsync(moving, target, _dropAfter);
            await PersistActiveListOrderAsync();
            MilitaryGrid.SelectedItems.Clear();
            foreach (var item in moving) item.IsMarkedForBatch = true;
            foreach (var item in moving.Where(_vm.Military.Contains)) MilitaryGrid.SelectedItems.Add(item);
            MilitaryGrid.Items.Refresh();
            UpdateMarkedCount();
            if (moving.Count > 0) MilitaryGrid.ScrollIntoView(moving[0]);
            _vm.StatusText = moving.Count == 1
                ? "Militar reposicionado e ordem salva."
                : $"{moving.Count} militares reposicionados e ordem salva.";
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            ClearDropIndicator();
            e.Handled = true;
        }
    }

    private void ShowDropIndicator(DataGridRow row, bool after)
    {
        if (ReferenceEquals(_dropTargetRow, row) && _dropAfter == after && _dropIndicator is not null) return;
        ClearDropIndicator();
        _dropTargetRow = row;
        _dropAfter = after;
        var layer = AdornerLayer.GetAdornerLayer(row);
        if (layer is null) return;
        _dropIndicator = new DataGridDropIndicatorAdorner(row, after);
        layer.Add(_dropIndicator);
    }

    private void ClearDropIndicator()
    {
        if (_dropTargetRow is not null && _dropIndicator is not null)
        {
            try { AdornerLayer.GetAdornerLayer(_dropTargetRow)?.Remove(_dropIndicator); } catch { }
        }
        _dropIndicator = null;
        _dropTargetRow = null;
        _dropAfter = false;
    }

    private (DataGridRow? Row, bool After) ResolveDropTarget(DependencyObject? source, Point gridPoint)
    {
        var direct = VisualTreeUtilities.FindAncestor<DataGridRow>(source);
        if (direct is not null)
        {
            var point = Mouse.GetPosition(direct);
            return (direct, point.Y > Math.Max(1, direct.ActualHeight) / 2d);
        }

        var visibleRows = MilitaryGrid.Items.Cast<object>()
            .Select(item => MilitaryGrid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow)
            .Where(row => row is not null && row.IsVisible)
            .Cast<DataGridRow>()
            .OrderBy(row => row.TranslatePoint(new Point(0, 0), MilitaryGrid).Y)
            .ToList();
        if (visibleRows.Count == 0) return (null, false);

        var first = visibleRows[0];
        var last = visibleRows[^1];
        var firstTop = first.TranslatePoint(new Point(0, 0), MilitaryGrid).Y;
        var lastTop = last.TranslatePoint(new Point(0, 0), MilitaryGrid).Y;
        if (gridPoint.Y <= firstTop) return (first, false);
        if (gridPoint.Y >= lastTop + last.ActualHeight) return (last, true);

        var nearest = visibleRows.OrderBy(row =>
        {
            var top = row.TranslatePoint(new Point(0, 0), MilitaryGrid).Y;
            return Math.Abs(gridPoint.Y - (top + row.ActualHeight / 2d));
        }).First();
        var nearestTop = nearest.TranslatePoint(new Point(0, 0), MilitaryGrid).Y;
        return (nearest, gridPoint.Y > nearestTop + nearest.ActualHeight / 2d);
    }

    private void AutoScrollDuringDrag(double y)
    {
        const double edge = 38;
        var scroll = VisualTreeUtilities.FindDescendant<ScrollViewer>(MilitaryGrid);
        if (scroll is null) return;
        if (y < edge) scroll.LineUp();
        else if (y > MilitaryGrid.ActualHeight - edge) scroll.LineDown();
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ClearAllSelection();
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.R) { Refresh_Click(sender, e); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N) { New_Click(sender, e); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { await UndoLastDeleteAsync(); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            if (_activeSavedList is not null)
            {
                await PersistActiveListOrderAsync();
                _vm.StatusText = $"Lista “{_activeSavedList.Name}” salva na ordem atual.";
            }
            else
            {
                await _vm.SaveSettingsAsync();
                _vm.StatusText = "Ordem e filtros da relação completa salvos.";
            }
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && !SearchBox.IsKeyboardFocusWithin) { await OpenWalletAsync(); e.Handled = true; return; }
        if (e.Key == Key.F2) { await EditAsync(); e.Handled = true; return; }
        if (e.Key == Key.Delete && !SearchBox.IsKeyboardFocusWithin) { Delete_Click(sender, e); e.Handled = true; }
    }

    private void Notify(string message) => SigfurDialog.Show(this, message, "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
    private void NotifySelection() => SigfurDialog.Show(this, "Selecione um militar primeiro.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
    private void ShowError(Exception ex) => SigfurDialog.Show(this, ex.Message, "SIGFUR — Erro", MessageBoxButton.OK, MessageBoxImage.Error);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button) return;
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) { e.Handled = true; WindowState = WindowState.Minimized; }
    private void Maximize_Click(object sender, RoutedEventArgs e) { e.Handled = true; ToggleMaximize(); }
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) { e.Handled = true; Close(); }
    private void LockOrderCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        ApplyOrderLockState();
        _vm.StatusText = _vm.OrderLocked
            ? "Ordem travada. Arrastar e mover militares está bloqueado."
            : "Ordem liberada. Você pode reorganizar a lista novamente.";
    }

    private void ApplyOrderLockState()
    {
        MilitaryGrid.AllowDrop = !_vm.OrderLocked;
        if (_vm.OrderLocked) ClearDropIndicator();
    }

    private void NotifyOrderLocked()
    {
        _vm.StatusText = "A ordem está travada. Desmarque ‘Travar ordem’ para reorganizar.";
    }

}
