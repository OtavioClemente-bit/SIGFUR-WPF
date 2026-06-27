using System.Windows;
using System.Windows.Controls;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Personnel;

public sealed class AbsenceMilitaryOption
{
    public MilitaryRecord Military { get; init; } = new();
    public string DisplayName => $"{Military.ShortRank} {Military.Name} — CPF {Military.FormattedCpf}";
    public string SearchText => MilitaryRankService.Normalize($"{Military.Rank} {Military.Name} {Military.WarName} {Military.Cpf} {Military.PrecCp}");
}

public partial class AbsenceEditorWindow : Window
{
    private readonly List<AbsenceMilitaryOption> _options;
    private readonly int? _preselectedMilitaryId;
    public AbsenceOccurrence Value { get; private set; }

    public AbsenceEditorWindow(IEnumerable<MilitaryRecord> military, AbsenceOccurrence? existing = null, int? preselectedMilitaryId = null)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _preselectedMilitaryId = preselectedMilitaryId;
        _options = military
            .OrderBy(x => x, Comparer<MilitaryRecord>.Create((a, b) => MilitaryRankService.Compare(a.Rank, a.Name, b.Rank, b.Name)))
            .Select(x => new AbsenceMilitaryOption { Military = x })
            .ToList();
        MilitaryBox.ItemsSource = _options;
        Value = existing is null ? new AbsenceOccurrence() : new AbsenceOccurrence
        {
            Id = existing.Id,
            MilitaryId = existing.MilitaryId,
            Rank = existing.Rank,
            Name = existing.Name,
            WarName = existing.WarName,
            Date = existing.Date,
            Time = existing.Time,
            Type = existing.Type,
            Minutes = existing.Minutes,
            Justified = existing.Justified,
            Reason = existing.Reason,
            Measure = existing.Measure,
            Notes = existing.Notes,
            CreatedAt = existing.CreatedAt
        };
        if (existing is not null) TitleText.Text = "Editar ocorrência";
        var targetId = Value.MilitaryId > 0 ? Value.MilitaryId : _preselectedMilitaryId;
        MilitaryBox.SelectedItem = _options.FirstOrDefault(x => x.Military.Id == targetId) ?? _options.FirstOrDefault();
        DateBox.SelectedDate = Value.Date == default ? DateTime.Today : Value.Date;
        TimeBox.Text = string.IsNullOrWhiteSpace(Value.Time) ? DateTime.Now.ToString("HH:mm") : Value.Time;
        TypeBox.SelectedIndex = Value.Type.Equals("FALTA", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        MinutesBox.Text = Value.Minutes.ToString();
        JustifiedCheck.IsChecked = Value.Justified;
        ReasonBox.Text = Value.Reason;
        MeasureBox.Text = Value.Measure;
        NotesBox.Text = Value.Notes;
    }

    private void MilitarySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var currentId = (MilitaryBox.SelectedItem as AbsenceMilitaryOption)?.Military.Id;
        var terms = MilitaryRankService.Normalize(MilitarySearchBox.Text)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var filtered = terms.Length == 0
            ? _options
            : _options.Where(x => terms.All(term => x.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase))).ToList();
        MilitaryBox.ItemsSource = filtered;
        MilitaryBox.SelectedItem = filtered.FirstOrDefault(x => x.Military.Id == currentId) ?? filtered.FirstOrDefault();
        if (filtered.Count == 1) MilitaryBox.IsDropDownOpen = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (MilitaryBox.SelectedItem is not AbsenceMilitaryOption option)
        {
            SigfurDialog.Show(this, "Selecione o militar.", "Ocorrência", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var type = (TypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "ATRASO";
        if (!int.TryParse(MinutesBox.Text.Trim(), out var minutes)) minutes = 0;
        if (type == "ATRASO" && minutes <= 0)
        {
            SigfurDialog.Show(this, "Informe a quantidade de minutos do atraso.", "Ocorrência", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Value.MilitaryId = option.Military.Id;
        Value.Rank = option.Military.Rank;
        Value.Name = option.Military.Name;
        Value.WarName = option.Military.WarName;
        Value.Date = DateBox.SelectedDate ?? DateTime.Today;
        Value.Time = TimeBox.Text.Trim();
        Value.Type = type;
        Value.Minutes = type == "ATRASO" ? Math.Max(0, minutes) : 0;
        Value.Justified = JustifiedCheck.IsChecked == true;
        Value.Reason = ReasonBox.Text.Trim();
        Value.Measure = MeasureBox.Text.Trim();
        Value.Notes = NotesBox.Text.Trim();
        if (Value.CreatedAt == default) Value.CreatedAt = DateTime.Now;
        DialogResult = true;
    }
}
