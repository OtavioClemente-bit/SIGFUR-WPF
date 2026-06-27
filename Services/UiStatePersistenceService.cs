using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Persiste automaticamente posição/tamanho das janelas, aba selecionada,
/// campos não vinculados e layout das tabelas. Campos ligados ao cadastro do
/// militar não são restaurados para evitar copiar dados de um militar para outro.
/// Senhas nunca são gravadas aqui; continuam sob a proteção própria das credenciais.
/// </summary>
public sealed class UiStatePersistenceService
{
    private readonly string _path;
    private readonly LogService _log;
    private readonly object _sync = new();
    private UiStateStore _store = new();
    private bool _loaded;
    private readonly Dictionary<Window, DispatcherTimer> _timers = [];
    private readonly HashSet<Window> _attached = [];

    public UiStatePersistenceService(AppPaths paths, LogService log)
    {
        _path = paths.UiStateFile;
        _log = log;
    }

    public void Attach(Window window, string? stateKey = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        lock (_sync)
        {
            if (!_attached.Add(window)) return;
            EnsureLoadedUnsafe();
        }

        var key = string.IsNullOrWhiteSpace(stateKey)
            ? window.GetType().FullName ?? window.GetType().Name
            : stateKey.Trim();

        window.SourceInitialized += (_, _) => RestoreWindowBounds(window, key);
        window.Loaded += (_, _) => RestoreWindowBounds(window, key);
        // Uma única restauração depois que o conteúdo realmente foi renderizado.
        // A versão anterior percorria a árvore visual três vezes (120/180/850 ms),
        // podendo congelar a primeira rolagem em computadores mais lentos.
        window.ContentRendered += (_, _) => ScheduleRestoreContent(window, key, 220);
        // Durante movimento/redimensionamento salva somente os limites. A versão
        // anterior percorria toda a árvore visual e serializava todos os campos após
        // quase qualquer clique/tecla, o que causava pausas perceptíveis em PCs mais
        // fracos e principalmente ao usar barras de rolagem.
        window.LocationChanged += (_, _) => ScheduleBoundsSave(window, key);
        window.SizeChanged += (_, _) => ScheduleBoundsSave(window, key);
        window.StateChanged += (_, _) => ScheduleBoundsSave(window, key);

        // Campos, abas e colunas continuam sendo preservados, mas somente quando a
        // janela perde o foco ou fecha — momentos em que a varredura completa não
        // interfere na rolagem nem na digitação.
        window.Deactivated += (_, _) => SaveWindowNow(window, key);
        window.Closing += (_, _) => SaveWindowNow(window, key);
        window.Closed += (_, _) =>
        {
            lock (_sync)
            {
                if (_timers.Remove(window, out var timer)) timer.Stop();
                _attached.Remove(window);
            }
        };
    }

    public void FlushOpenWindows()
    {
        var application = Application.Current;
        if (application is null) return;
        foreach (Window window in application.Windows)
        {
            try
            {
                var key = window.GetType().FullName ?? window.GetType().Name;
                SaveWindowNow(window, key);
            }
            catch { }
        }
    }

    private void ScheduleRestoreContent(Window window, string key, int delayMs)
    {
        _ = window.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await Task.Delay(Math.Max(0, delayMs));
                if (window.IsLoaded) RestoreContent(window, key);
            }
            catch { }
        }, DispatcherPriority.Background);
    }

    private void ScheduleBoundsSave(Window window, string key)
    {
        if (!window.IsLoaded) return;
        lock (_sync)
        {
            if (!_timers.TryGetValue(window, out var timer))
            {
                timer = new DispatcherTimer(DispatcherPriority.ContextIdle, window.Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(900)
                };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    SaveWindowBoundsOnly(window, key);
                };
                _timers[window] = timer;
            }
            timer.Stop();
            timer.Start();
        }
    }

    private void SaveWindowBoundsOnly(Window window, string key)
    {
        try
        {
            var bounds = window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.Width, window.Height)
                : window.RestoreBounds;

            lock (_sync)
            {
                EnsureLoadedUnsafe();
                if (!_store.Windows.TryGetValue(key, out var state))
                {
                    state = new UiWindowState();
                    _store.Windows[key] = state;
                }
                state.Left = bounds.Left;
                state.Top = bounds.Top;
                state.Width = bounds.Width;
                state.Height = bounds.Height;
                state.Maximized = window.WindowState == WindowState.Maximized;
                SaveUnsafe();
            }
        }
        catch (Exception ex)
        {
            _ = _log.WriteAsync($"Falha ao salvar posição da janela {key}.", ex);
        }
    }

    private void RestoreWindowBounds(Window window, string key)
    {
        UiWindowState? state;
        lock (_sync)
        {
            EnsureLoadedUnsafe();
            _store.Windows.TryGetValue(key, out state);
        }
        if (state is null) return;

        try
        {
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualWidth = Math.Max(640d, SystemParameters.VirtualScreenWidth);
            var virtualHeight = Math.Max(480d, SystemParameters.VirtualScreenHeight);
            var minWidth = Math.Max(320d, Math.Min(double.IsFinite(window.MinWidth) ? window.MinWidth : 0d, virtualWidth));
            var minHeight = Math.Max(220d, Math.Min(double.IsFinite(window.MinHeight) ? window.MinHeight : 0d, virtualHeight));
            var fallbackWidth = double.IsFinite(window.Width) && window.Width > 0 ? window.Width : Math.Min(1100d, virtualWidth);
            var fallbackHeight = double.IsFinite(window.Height) && window.Height > 0 ? window.Height : Math.Min(760d, virtualHeight);
            var width = ClampFinite(state.Width, minWidth, virtualWidth, Math.Min(Math.Max(fallbackWidth, minWidth), virtualWidth));
            var height = ClampFinite(state.Height, minHeight, virtualHeight, Math.Min(Math.Max(fallbackHeight, minHeight), virtualHeight));
            var left = double.IsFinite(state.Left) ? state.Left : virtualLeft + (virtualWidth - width) / 2;
            var top = double.IsFinite(state.Top) ? state.Top : virtualTop + (virtualHeight - height) / 2;

            // Mantém pelo menos uma faixa da janela visível na área virtual de monitores.
            const double visibleStrip = 90d;
            left = Math.Clamp(left, virtualLeft - width + visibleStrip, virtualLeft + virtualWidth - visibleStrip);
            top = Math.Clamp(top, virtualTop, virtualTop + virtualHeight - visibleStrip);

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = left;
            window.Top = top;
            window.Width = width;
            window.Height = height;
            if (state.Maximized) window.WindowState = WindowState.Maximized;
        }
        catch (Exception ex)
        {
            _ = _log.WriteAsync($"Falha ao restaurar posição da janela {key}.", ex);
        }
    }

    private void RestoreContent(Window window, string key)
    {
        if (IsBoundsOnly(window)) return;
        UiWindowState? state;
        lock (_sync)
        {
            EnsureLoadedUnsafe();
            _store.Windows.TryGetValue(key, out state);
        }
        if (state is null) return;

        try
        {
            var elements = EnumerateElements(window).ToList();
            foreach (var element in elements)
            {
                var controlKey = ControlKey(element);
                if (controlKey is null || !state.Controls.TryGetValue(controlKey, out var value)) continue;
                RestoreControl(element, value);
            }

            foreach (var grid in elements.OfType<DataGrid>())
            {
                var gridKey = GridKey(grid);
                if (gridKey is null || !state.Grids.TryGetValue(gridKey, out var columns)) continue;
                RestoreGrid(grid, columns);
            }
        }
        catch (Exception ex)
        {
            _ = _log.WriteAsync($"Falha ao restaurar controles da janela {key}.", ex);
        }
    }

    private void SaveWindowNow(Window window, string key)
    {
        try
        {
            var bounds = window.WindowState == WindowState.Normal ? new Rect(window.Left, window.Top, window.Width, window.Height) : window.RestoreBounds;
            var state = new UiWindowState
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                Maximized = window.WindowState == WindowState.Maximized
            };

            if (!IsBoundsOnly(window))
            {
                var elements = EnumerateElements(window).ToList();
                foreach (var element in elements)
                {
                    var controlKey = ControlKey(element);
                    if (controlKey is null) continue;
                    var value = CaptureControl(element);
                    if (value is not null) state.Controls[controlKey] = value;
                }

                foreach (var grid in elements.OfType<DataGrid>())
                {
                    var gridKey = GridKey(grid);
                    if (gridKey is null) continue;
                    state.Grids[gridKey] = CaptureGrid(grid);
                }
            }

            lock (_sync)
            {
                EnsureLoadedUnsafe();
                _store.Windows[key] = state;
                SaveUnsafe();
            }
        }
        catch (Exception ex)
        {
            _ = _log.WriteAsync($"Falha ao salvar estado da janela {key}.", ex);
        }
    }

    private static string? CaptureControl(FrameworkElement element)
    {
        switch (element)
        {
            case PasswordBox:
                return null;
            case TextBox text when ShouldPersistTextBox(text):
                return text.Text;
            case ComboBox combo when !IsComboBound(combo):
                return combo.SelectedValue?.ToString() ?? combo.SelectedItem?.ToString() ?? combo.Text ?? string.Empty;
            case DatePicker date when !BindingOperations.IsDataBound(date, DatePicker.SelectedDateProperty):
                return date.SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
            case CheckBox check when !BindingOperations.IsDataBound(check, ToggleButton.IsCheckedProperty):
                return check.IsChecked switch { true => "true", false => "false", _ => "null" };
            case RadioButton radio when !BindingOperations.IsDataBound(radio, ToggleButton.IsCheckedProperty):
                return radio.IsChecked == true ? "true" : "false";
            case TabControl tabs:
                return tabs.SelectedIndex.ToString(CultureInfo.InvariantCulture);
            case Expander expander:
                return expander.IsExpanded ? "true" : "false";
            default:
                return null;
        }
    }

    private static void RestoreControl(FrameworkElement element, string value)
    {
        switch (element)
        {
            case PasswordBox:
                return;
            case TextBox text when ShouldPersistTextBox(text):
                text.Text = value;
                break;
            case ComboBox combo when !IsComboBound(combo):
            {
                var item = combo.Items.Cast<object?>().FirstOrDefault(x => string.Equals(x?.ToString(), value, StringComparison.CurrentCultureIgnoreCase));
                if (item is not null) combo.SelectedItem = item;
                else if (combo.IsEditable) combo.Text = value;
                break;
            }
            case DatePicker date when !BindingOperations.IsDataBound(date, DatePicker.SelectedDateProperty):
                date.SelectedDate = DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed.Date : null;
                break;
            case CheckBox check when !BindingOperations.IsDataBound(check, ToggleButton.IsCheckedProperty):
                check.IsChecked = value.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : bool.TryParse(value, out var flag) && flag;
                break;
            case RadioButton radio when !BindingOperations.IsDataBound(radio, ToggleButton.IsCheckedProperty):
                radio.IsChecked = bool.TryParse(value, out var selected) && selected;
                break;
            case TabControl tabs when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index):
                if (index >= 0 && index < tabs.Items.Count) tabs.SelectedIndex = index;
                break;
            case Expander expander:
                expander.IsExpanded = bool.TryParse(value, out var expanded) && expanded;
                break;
        }
    }

    private static bool IsBoundsOnly(Window window)
        => (window.Tag?.ToString() ?? string.Empty).Contains("UiState.BoundsOnly", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldPersistTextBox(TextBox text)
    {
        if ((text.Tag?.ToString() ?? string.Empty).Contains("UiState.NoPersist", StringComparison.OrdinalIgnoreCase)) return false;
        if (BindingOperations.IsDataBound(text, TextBox.TextProperty)) return false;
        if (!text.IsReadOnly) return true;
        // Caminhos escolhidos pelo operador costumam ser exibidos em caixa somente
        // leitura. Resultados, logs e prévias não devem voltar na próxima abertura.
        var name = text.Name ?? string.Empty;
        return name.Contains("Folder", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Path", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Directory", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Pasta", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Caminho", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsComboBound(ComboBox combo)
        => BindingOperations.IsDataBound(combo, Selector.SelectedItemProperty)
           || BindingOperations.IsDataBound(combo, Selector.SelectedValueProperty)
           || BindingOperations.IsDataBound(combo, ComboBox.TextProperty);

    private static List<UiGridColumnState> CaptureGrid(DataGrid grid)
        => grid.Columns.Select((column, index) => new UiGridColumnState
        {
            Key = ColumnKey(column, index),
            DisplayIndex = column.DisplayIndex,
            Width = column.Width.UnitType == DataGridLengthUnitType.Pixel
                    && double.IsFinite(column.ActualWidth) && column.ActualWidth > 0
                ? column.ActualWidth
                : column.Width.Value,
            WidthUnit = column.Width.UnitType.ToString(),
            Visible = column.Visibility == Visibility.Visible,
            SortDirection = column.SortDirection?.ToString() ?? string.Empty
        }).ToList();

    private static void RestoreGrid(DataGrid grid, IReadOnlyList<UiGridColumnState> states)
    {
        var indexed = grid.Columns.Select((column, index) => (column, index)).ToList();
        foreach (var (column, index) in indexed)
        {
            var state = states.FirstOrDefault(x => x.Key.Equals(ColumnKey(column, index), StringComparison.OrdinalIgnoreCase));
            if (state is null) continue;
            column.Visibility = state.Visible ? Visibility.Visible : Visibility.Collapsed;
            if (double.IsFinite(state.Width) && state.Width >= 25)
            {
                column.Width = Enum.TryParse<DataGridLengthUnitType>(state.WidthUnit, true, out var unit)
                    ? new DataGridLength(state.Width, unit)
                    : new DataGridLength(state.Width, DataGridLengthUnitType.Pixel);
            }
            column.SortDirection = Enum.TryParse<ListSortDirection>(state.SortDirection, true, out var direction)
                ? direction
                : null;
        }

        var ordered = states.OrderBy(x => x.DisplayIndex).ToList();
        for (var desired = 0; desired < ordered.Count; desired++)
        {
            var state = ordered[desired];
            var pair = indexed.FirstOrDefault(x => state.Key.Equals(ColumnKey(x.column, x.index), StringComparison.OrdinalIgnoreCase));
            if (pair.column is null) continue;
            try { pair.column.DisplayIndex = Math.Clamp(desired, 0, Math.Max(0, grid.Columns.Count - 1)); } catch { }
        }

        // Restaura a ordenação real, não apenas a seta visual do cabeçalho.
        try
        {
            var sortedColumns = indexed
                .Select(x => (x.column, State: states.FirstOrDefault(s => s.Key.Equals(ColumnKey(x.column, x.index), StringComparison.OrdinalIgnoreCase))))
                .Where(x => x.State is not null
                            && !string.IsNullOrWhiteSpace(x.State.SortDirection)
                            && !string.IsNullOrWhiteSpace(x.column.SortMemberPath))
                .OrderBy(x => x.State!.DisplayIndex)
                .ToList();
            if (sortedColumns.Count > 0 && grid.ItemsSource is not null)
            {
                var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
                if (view.CanSort)
                {
                    view.SortDescriptions.Clear();
                    foreach (var item in sortedColumns)
                    {
                        if (Enum.TryParse<ListSortDirection>(item.State!.SortDirection, true, out var direction))
                            view.SortDescriptions.Add(new SortDescription(item.column.SortMemberPath, direction));
                    }
                }
            }
        }
        catch { }
    }

    private static IEnumerable<FrameworkElement> EnumerateElements(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current)) continue;
            if (current is FrameworkElement element) yield return element;
            var count = 0;
            try { count = VisualTreeHelper.GetChildrenCount(current); } catch { }
            for (var i = count - 1; i >= 0; i--)
            {
                try { stack.Push(VisualTreeHelper.GetChild(current, i)); } catch { }
            }
        }
    }

    private static string? ControlKey(FrameworkElement element)
    {
        if (string.IsNullOrWhiteSpace(element.Name)) return null;
        return $"{element.GetType().Name}:{element.Name}";
    }

    private static string? GridKey(DataGrid grid)
        => string.IsNullOrWhiteSpace(grid.Name) ? null : $"DataGrid:{grid.Name}";

    private static string ColumnKey(DataGridColumn column, int index)
    {
        var sort = (column.SortMemberPath ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(sort)) return "sort:" + sort;
        var header = column.Header?.ToString()?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(header) ? "header:" + header : "index:" + index.ToString(CultureInfo.InvariantCulture);
    }

    private void EnsureLoadedUnsafe()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path, Encoding.UTF8);
            _store = JsonSerializer.Deserialize<UiStateStore>(json, SerializerOptions) ?? new UiStateStore();
        }
        catch
        {
            _store = new UiStateStore();
        }
    }

    private void SaveUnsafe()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_store, SerializerOptions), new UTF8Encoding(false));
        File.Move(temp, _path, true);
    }

    private static double ClampFinite(double value, double min, double max, double fallback)
        => Math.Clamp(double.IsFinite(value) ? value : fallback, min, Math.Max(min, max));

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private sealed class UiStateStore
    {
        public Dictionary<string, UiWindowState> Windows { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class UiWindowState
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
        public Dictionary<string, string> Controls { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<UiGridColumnState>> Grids { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class UiGridColumnState
    {
        public string Key { get; set; } = string.Empty;
        public int DisplayIndex { get; set; }
        public double Width { get; set; }
        public string WidthUnit { get; set; } = nameof(DataGridLengthUnitType.Pixel);
        public bool Visible { get; set; } = true;
        public string SortDirection { get; set; } = string.Empty;
    }
}
