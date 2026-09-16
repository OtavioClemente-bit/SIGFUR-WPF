using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Controls;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views;

public partial class BirthdaysWindow : Window
{
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly ObservableCollection<BirthdayItem> _items;

    public BirthdaysWindow(IEnumerable<BirthdayItem> birthdays, AppPaths paths, JsonFileService json)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _paths = paths; _json = json;
        _items = new ObservableCollection<BirthdayItem>(birthdays.Select(x => new BirthdayItem
        {
            MilitaryId = x.MilitaryId, Day = x.Day, Rank = x.Rank, WarName = x.WarName, Name = x.Name,
            Age = x.Age, IsToday = x.IsToday, Confirmed = x.Confirmed
        }));
        BirthdayGrid.ItemsSource = _items;
        SummaryText.Text = $"{DateTime.Today:MMMM 'de' yyyy} • {_items.Count} aniversariante(s) • marque a conferência das pastas.";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var root = await _json.LoadNodeAsync(_paths.BirthdayConferenceFile) as JsonObject ?? new JsonObject();
        var month = DateTime.Today.ToString("yyyy-MM");
        var bucket = new JsonObject();
        foreach (var item in _items.Where(x => x.Confirmed))
        {
            var key = item.MilitaryId > 0 ? item.MilitaryId.ToString() : $"sem_id::{Normalize(item.Name)}::{item.Day}/{DateTime.Today.Month}";
            bucket[key] = new JsonObject
            {
                ["conferido"] = true, ["em"] = DateTime.Now.ToString("dd/MM/yyyy HH:mm"), ["id"] = item.MilitaryId.ToString(),
                ["posto"] = item.Rank, ["nome"] = item.Name, ["nome_guerra"] = item.WarName
            };
        }
        if (bucket.Count > 0) root[month] = bucket; else root.Remove(month);
        await _json.SaveNodeAsync(_paths.BirthdayConferenceFile, root);
        SigfurDialog.Show(this, "Conferência salva.", "Aniversariantes", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CopyList_Click(object sender, RoutedEventArgs e)
    {
        var lines = _items.OrderBy(x => MilitaryRankService.GetOrder(x.Rank)).ThenBy(x => x.Name).Select(x => $"{x.Day} — {MilitaryRankService.ShortName(x.Rank)} {NameHighlightHelper.PlainDisplay(x.Name, x.WarName)}");
        Clipboard.SetText(string.Join(Environment.NewLine, lines));
    }

    private void GenerateBulletin_Click(object sender, RoutedEventArgs e)
        => new BirthdayBulletinWindow(_items, _paths) { Owner = this }.ShowDialog();

    private static string Normalize(string value) => value.Trim().ToLowerInvariant().Replace(' ', '_');
}
