using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using SIGFUR.Wpf.Controls;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views;

public partial class CalendarDayEditorWindow : Window
{
    private readonly DateTime _date;
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly ObservableCollection<CalendarEventRecord> _items = [];
    private List<CalendarEventRecord> _allItems = [];
    private CalendarEventRecord? _current;

    public CalendarDayEditorWindow(DateTime date, AppPaths paths, JsonFileService json)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _date = date.Date;
        _paths = paths;
        _json = json;
        EventsGrid.ItemsSource = _items;
        TitleText.Text = _date.ToString("dddd, dd 'de' MMMM 'de' yyyy", CultureInfo.GetCultureInfo("pt-BR"));
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _allItems = await LoadAllItemsAsync();
        RefreshDayItems();
        StartNew("Evento");
    }

    private async Task<List<CalendarEventRecord>> LoadAllItemsAsync()
    {
        try
        {
            var root = await _json.LoadNodeAsync(_paths.CalendarEventsFile) as JsonObject;
            var arr = root?["items"] as JsonArray;
            var list = new List<CalendarEventRecord>();
            if (arr is null) return list;
            foreach (var node in arr)
            {
                if (node is null) continue;
                try
                {
                    var item = node.Deserialize<CalendarEventRecord>();
                    if (item is not null) list.Add(item);
                }
                catch { }
            }
            return list;
        }
        catch { return []; }
    }

    private async Task SaveAllItemsAsync()
    {
        var root = new JsonObject
        {
            ["items"] = JsonSerializerHelper.SerializeNode(_allItems)
        };
        await _json.SaveNodeAsync(_paths.CalendarEventsFile, root);
    }

    private void RefreshDayItems()
    {
        _items.Clear();
        var dayKey = _date.ToString("yyyy-MM-dd");
        foreach (var item in _allItems.Where(x => string.Equals(x.Date, dayKey, StringComparison.OrdinalIgnoreCase))
                                      .OrderBy(x => TypeOrder(x.Type))
                                      .ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            _items.Add(Clone(item));
        }
        StatusText.Text = _items.Count == 0
            ? "Nenhum registro salvo neste dia."
            : $"{_items.Count} registro(s) salvo(s) neste dia.";
    }

    private static int TypeOrder(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "serviço" => 0,
        "pagamento" => 1,
        "evento" => 2,
        "observação" => 3,
        _ => 9
    };

    private static CalendarEventRecord Clone(CalendarEventRecord source) => new()
    {
        Id = source.Id,
        Date = source.Date,
        Type = source.Type,
        Title = source.Title,
        Description = source.Description,
        Status = source.Status
    };

    private void StartNew(string type)
    {
        _current = null;
        SelectType(type);
        TitleBox.Text = type.Equals("Serviço", StringComparison.OrdinalIgnoreCase) ? "Serviço" : string.Empty;
        DescriptionBox.Text = string.Empty;
        EventsGrid.SelectedItem = null;
        TitleBox.Focus();
    }

    private void SelectType(string type)
    {
        foreach (var item in TypeCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), type, StringComparison.OrdinalIgnoreCase))
            {
                TypeCombo.SelectedItem = item;
                return;
            }
        }
        TypeCombo.SelectedIndex = 0;
    }

    private string SelectedType()
        => (TypeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim() ?? "Evento";

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var type = SelectedType();
        var title = string.IsNullOrWhiteSpace(TitleBox.Text) ? type : TitleBox.Text.Trim();
        var description = DescriptionBox.Text?.Trim() ?? string.Empty;
        var target = _current is null
            ? new CalendarEventRecord { Id = Guid.NewGuid().ToString("N"), Date = _date.ToString("yyyy-MM-dd") }
            : _allItems.FirstOrDefault(x => x.Id == _current.Id) ?? new CalendarEventRecord { Id = _current.Id, Date = _date.ToString("yyyy-MM-dd") };

        target.Date = _date.ToString("yyyy-MM-dd");
        target.Type = type;
        target.Title = title;
        target.Description = description;

        var existingIndex = _allItems.FindIndex(x => x.Id == target.Id);
        if (existingIndex >= 0) _allItems[existingIndex] = target;
        else _allItems.Add(target);

        await SaveAllItemsAsync();
        RefreshDayItems();
        StartNew(type);
        DialogResult = true;
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            SigfurDialog.Show(this, "Selecione um registro para excluir.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var result = SigfurDialog.Show(this, "Deseja realmente excluir este registro do calendário?", "SIGFUR", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        _allItems.RemoveAll(x => x.Id == _current.Id);
        await SaveAllItemsAsync();
        RefreshDayItems();
        StartNew("Evento");
        DialogResult = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => StartNew("Evento");

    private void NewEvent_Click(object sender, RoutedEventArgs e) => StartNew("Evento");

    private void QuickService_Click(object sender, RoutedEventArgs e) => StartNew("Serviço");

    private void EventsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EventsGrid.SelectedItem is not CalendarEventRecord item) return;
        _current = Clone(item);
        SelectType(item.Type);
        TitleBox.Text = item.Title;
        DescriptionBox.Text = item.Description;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

internal static class JsonSerializerHelper
{
    public static JsonNode SerializeNode<T>(T value)
        => JsonSerializer.SerializeToNode(value) ?? new JsonArray();
}
