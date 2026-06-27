using System.Text;
using System.Windows;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Military;

public partial class MilitaryQuantityWindow : Window
{
    private readonly IReadOnlyList<MilitaryRecord> _rows;
    private readonly string _scope;
    private string _summary = string.Empty;

    public MilitaryQuantityWindow(IReadOnlyList<MilitaryRecord> rows, string scope)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _rows = rows;
        _scope = scope;
        Loaded += (_, _) => Render();
    }

    private void Render()
    {
        ScopeText.Text = $"Escopo: {_scope} • {_rows.Count} registro(s)";
        var withAt = _rows.Count(x => MilitaryRecord.IsYes(x.ReceivesTransportAid));
        var withoutAt = _rows.Count(x => !MilitaryRecord.IsYes(x.ReceivesTransportAid) && !x.IsAttached);
        var favorites = _rows.Count(x => x.IsFavorite);
        TotalText.Text = _rows.Count.ToString("N0");
        WithAtText.Text = withAt.ToString("N0");
        WithoutAtText.Text = withoutAt.ToString("N0");
        FavoriteText.Text = favorites.ToString("N0");

        var groups = _rows
            .GroupBy(x => new { x.Rank, x.ShortRank })
            .OrderBy(x => MilitaryRankService.GetOrder(x.Key.Rank))
            .ThenBy(x => x.Key.ShortRank)
            .Select(x => new RankSummary
            {
                Rank = x.Key.ShortRank,
                Count = x.Count(),
                Percentage = _rows.Count == 0 ? "0%" : $"{x.Count() * 100d / _rows.Count:0.0}%"
            })
            .ToList();
        RankGrid.ItemsSource = groups;

        var sb = new StringBuilder();
        sb.AppendLine($"QUANTIDADE DE MILITARES — {_scope.ToUpperInvariant()}");
        sb.AppendLine($"Total: {_rows.Count}");
        sb.AppendLine();
        sb.AppendLine("Por P/G:");
        foreach (var item in groups) sb.AppendLine($"{item.Rank}: {item.Count} ({item.Percentage})");
        sb.AppendLine();
        sb.AppendLine($"Auxílio-Transporte — recebem: {withAt} | não recebem: {withoutAt}");
        sb.AppendLine($"Favoritos: {favorites}");
        _summary = sb.ToString().Trim();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_summary);
        SigfurDialog.Show(this, "Resumo copiado para a área de transferência.", "SIGFUR", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class RankSummary
    {
        public string Rank { get; init; } = string.Empty;
        public int Count { get; init; }
        public string Percentage { get; init; } = string.Empty;
    }
}
