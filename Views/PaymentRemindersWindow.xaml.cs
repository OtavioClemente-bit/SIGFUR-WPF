using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views.Military;

namespace SIGFUR.Wpf.Views;

public partial class PaymentRemindersWindow : Window
{
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly ObservableCollection<PaymentReminderRow> _items = [];
    private readonly ObservableCollection<PaymentReminderRow> _visible = [];
    private readonly ObservableCollection<VacationPaymentAttentionRow> _vacationRows = [];
    private readonly ObservableCollection<TransportAttentionRow> _transportRows = [];
    private bool _essentialInitialized;

    public PaymentRemindersWindow(AppPaths paths, JsonFileService json)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        _paths = paths;
        _json = json;
        ReminderGrid.ItemsSource = _visible;
        VacationGrid.ItemsSource = _vacationRows;
        TransportGrid.ItemsSource = _transportRows;
        StatusFilterBox.SelectedIndex = 1;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _items.Clear();
        var root = await _json.LoadNodeAsync(_paths.PaymentRemindersFile);
        _essentialInitialized = root is JsonObject metadata && Bool(metadata, "essenciais_inicializados");
        var array = root switch { JsonArray direct => direct, JsonObject obj when obj["items"] is JsonArray arr => arr, _ => new JsonArray() };
        foreach (var node in array.OfType<JsonObject>())
            _items.Add(new PaymentReminderRow
            {
                Id = Text(node, "id"),
                Category = Text(node, "categoria", Text(node, "tipo", "Outro")),
                Description = Text(node, "descricao", Text(node, "detalhe")),
                Competence = FormatPaymentCompetence(Text(node, "competencia")),
                Deadline = Text(node, "prazo"),
                Priority = Text(node, "prioridade", "Normal"),
                Observation = Text(node, "observacao"),
                Status = Text(node, "status", "pendente"),
                IsPermanent = Bool(node, "permanente"),
                CreatedAt = Text(node, "criado_em"),
                UpdatedAt = Text(node, "atualizado_em"),
                CompletedAt = Text(node, "concluido_em")
            });

        // Cria os dois controles críticos somente na primeira abertura. Depois disso,
        // o operador pode editar ou remover e o SIGFUR não recria contra a vontade dele.
        if (!_essentialInitialized)
        {
            var defaults = await DefaultsAsync();
            var now = DateTime.Now.ToString("O");
            EnsureEssential("essencial_ferias", "Férias", "Conferir e registrar o pagamento de todos os militares que entram de férias", defaults.Competence, defaults.Deadline, now);
            EnsureEssential("essencial_aux_transporte", "Auxílio-Transporte", "Conferir quem ainda não está recebendo e efetuar o pagamento do Auxílio-Transporte", defaults.Competence, defaults.Deadline, now);
            _essentialInitialized = true;
            await SaveAsync();
        }
        else ApplyFilter();

        await LoadOperationalControlsAsync();
    }

    private async Task SaveAsync()
    {
        var array = new JsonArray();
        foreach (var item in _items)
            array.Add(new JsonObject
            {
                ["id"] = item.Id,
                ["categoria"] = item.Category,
                ["descricao"] = item.Description,
                ["competencia"] = item.Competence,
                ["prazo"] = item.Deadline,
                ["prioridade"] = item.Priority,
                ["observacao"] = item.Observation,
                ["permanente"] = item.IsPermanent,
                ["status"] = item.Status,
                ["criado_em"] = item.CreatedAt,
                ["atualizado_em"] = item.UpdatedAt,
                ["concluido_em"] = item.CompletedAt
            });
        await _json.SaveNodeAsync(_paths.PaymentRemindersFile, new JsonObject
        {
            ["schema"] = 3,
            ["updated_at"] = DateTime.Now.ToString("O"),
            ["essenciais_inicializados"] = _essentialInitialized,
            ["items"] = array
        });
        ApplyFilter();
    }

    private async Task LoadOperationalControlsAsync()
    {
        _vacationRows.Clear();
        _transportRows.Clear();

        var year = DateTime.Today.Year;
        var periods = (await App.Vacations.GetPeriodsAsync(year)).ToDictionary(x => x.Id);
        foreach (var allocation in (await App.Vacations.GetAllocationsAsync(year))
                     .OrderBy(x => x.IsPaid)
                     .ThenBy(x => MilitaryRankService.GetOrder(x.Military.Rank))
                     .ThenBy(x => x.Military.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            periods.TryGetValue(allocation.PeriodId, out var period);
            _vacationRows.Add(new VacationPaymentAttentionRow
            {
                AllocationId = allocation.Id,
                Year = year,
                Rank = allocation.Military.ShortRank,
                Name = allocation.Military.Name,
                Period = period?.FullLabel ?? $"Período #{allocation.PeriodId}",
                Days = allocation.Days,
                IsPaid = allocation.IsPaid,
                RequiresFoodAid = allocation.RequiresVacationFoodAid,
                PaidAt = allocation.PaidAt
            });
        }

        foreach (var military in (await App.MilitaryRepository.GetAllAsync())
                     .Where(x => !x.IsAttached && !MilitaryRecord.IsYes(x.ReceivesTransportAid))
                     .OrderBy(x => MilitaryRankService.GetOrder(x.Rank))
                     .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            _transportRows.Add(new TransportAttentionRow(military));
        }
    }

    private async void RefreshOperational_Click(object sender, RoutedEventArgs e)
    {
        try { await LoadOperationalControlsAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void MarkVacationPaid_Click(object sender, RoutedEventArgs e)
    {
        var ids = VacationGrid.SelectedItems.Cast<VacationPaymentAttentionRow>().Select(x => x.AllocationId).ToArray();
        if (ids.Length == 0) { SigfurDialog.Show(this, "Selecione uma ou mais férias para registrar o pagamento.", "Férias", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try
        {
            var requiresFoodAid = VacationGrid.SelectedItems.Cast<VacationPaymentAttentionRow>().Any(x => x.RequiresFoodAid);
            if (requiresFoodAid && SigfurDialog.Show(this,
                    "Há Cabo/Soldado na seleção. Confirme que o Auxílio-Alimentação de férias também foi publicado e pago.",
                    "Conferência obrigatória", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await App.Vacations.SetPaidAsync(ids, true, requiresFoodAid);
            await LoadOperationalControlsAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ReopenVacationPaid_Click(object sender, RoutedEventArgs e)
    {
        var ids = VacationGrid.SelectedItems.Cast<VacationPaymentAttentionRow>().Select(x => x.AllocationId).ToArray();
        if (ids.Length == 0) { SigfurDialog.Show(this, "Selecione uma ou mais férias.", "Férias", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try
        {
            await App.Vacations.SetPaidAsync(ids, false);
            await LoadOperationalControlsAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void CreateTransportReminder_Click(object sender, RoutedEventArgs e)
    {
        if (TransportGrid.SelectedItem is not TransportAttentionRow selected)
        {
            SigfurDialog.Show(this, "Selecione um militar na conferência de Auxílio-Transporte.", "Auxílio-Transporte", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var defaults = await DefaultsAsync();
        var now = DateTime.Now.ToString("O");
        _items.Add(new PaymentReminderRow
        {
            Id = Guid.NewGuid().ToString("N"),
            Category = "Auxílio-Transporte",
            Description = $"Conferir implantação/pagamento de {selected.Rank} {selected.Name}",
            Competence = defaults.Competence,
            Deadline = defaults.Deadline.ToString("dd/MM/yyyy"),
            Priority = "Alta",
            Observation = $"Cadastro atual: {selected.Status}. Endereço: {selected.Address}",
            IsPermanent = false,
            Status = "pendente",
            CreatedAt = now,
            UpdatedAt = now
        });
        await SaveAsync();
        StatusFilterBox.SelectedIndex = 1;
        ControlTabs.SelectedIndex = 0;
    }

    private void OpenTransportWallet_Click(object sender, RoutedEventArgs e)
    {
        if (TransportGrid.SelectedItem is not TransportAttentionRow selected)
        {
            SigfurDialog.Show(this, "Selecione um militar.", "Auxílio-Transporte", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var wallet = new MilitaryWalletWindow(App.MilitaryRepository, App.Paystubs, selected.Military, 2) { Owner = this };
        wallet.Show();
        wallet.Activate();
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        var (competence, deadline) = await DefaultsAsync();
        var editor = new PaymentReminderEditorWindow(null, competence, deadline) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is not null) { _items.Add(editor.Result); await SaveAsync(); }
    }

    private async void Edit_Click(object sender, RoutedEventArgs e) => await EditSelectedAsync();
    private async void ReminderGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await EditSelectedAsync();

    private async Task EditSelectedAsync()
    {
        if (ReminderGrid.SelectedItem is not PaymentReminderRow selected) { WarnSelection(); return; }
        var defaults = await DefaultsAsync();
        var editor = new PaymentReminderEditorWindow(selected, defaults.Competence, defaults.Deadline) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        var index = _items.IndexOf(selected);
        if (index >= 0) _items[index] = editor.Result;
        await SaveAsync();
    }

    private async void Complete_Click(object sender, RoutedEventArgs e)
    {
        if (ReminderGrid.SelectedItem is not PaymentReminderRow selected) { WarnSelection(); return; }
        var now = DateTime.Now.ToString("O");
        selected.Status = "ok";
        selected.CompletedAt = now;
        selected.UpdatedAt = now;
        await SaveAsync();
    }

    private async void Reopen_Click(object sender, RoutedEventArgs e)
    {
        if (ReminderGrid.SelectedItem is not PaymentReminderRow selected) { WarnSelection(); return; }
        selected.Status = "pendente";
        selected.CompletedAt = string.Empty;
        selected.UpdatedAt = DateTime.Now.ToString("O");
        await SaveAsync();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ReminderGrid.SelectedItem is not PaymentReminderRow selected) { WarnSelection(); return; }
        if (SigfurDialog.Show(this, $"Remover o controle ‘{selected.Description}’?\n\nItens permanentes também podem ser removidos.", "Remover controle", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _items.Remove(selected);
        await SaveAsync();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (ReminderGrid.SelectedItem is not PaymentReminderRow selected) { WarnSelection(); return; }
        Clipboard.SetText($"{selected.Category} — {selected.Description}\nCompetência: {selected.Competence}\nReferência: {selected.Deadline}\nSituação: {selected.StatusLabel}\nRegistrado em: {selected.CompletedAtLabel}" + (string.IsNullOrWhiteSpace(selected.Observation) ? "" : $"\nObservação: {selected.Observation}"));
    }

    private async void CreateEssential_Click(object sender, RoutedEventArgs e)
    {
        var defaults = await DefaultsAsync();
        var now = DateTime.Now.ToString("O");
        EnsureEssential("essencial_ferias", "Férias", "Conferir e registrar o pagamento de todos os militares que entram de férias", defaults.Competence, defaults.Deadline, now);
        EnsureEssential("essencial_aux_transporte", "Auxílio-Transporte", "Conferir quem ainda não está recebendo e efetuar o pagamento do Auxílio-Transporte", defaults.Competence, defaults.Deadline, now);
        _essentialInitialized = true;
        await SaveAsync();
        StatusFilterBox.SelectedIndex = 3;
    }

    private void EnsureEssential(string id, string category, string description, string competence, DateTime deadline, string now)
    {
        if (_items.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) return;
        _items.Add(new PaymentReminderRow
        {
            Id = id,
            Category = category,
            Description = description,
            Competence = competence,
            Deadline = deadline.ToString("dd/MM/yyyy"),
            Priority = "Alta",
            Observation = "Controle permanente: marque OK somente após conferir e reabra para a próxima competência.",
            IsPermanent = true,
            Status = "pendente",
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (SearchBox is null || StatusFilterBox is null) return;
        var query = (SearchBox.Text ?? "").Trim();
        var mode = (StatusFilterBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Todos";
        IEnumerable<PaymentReminderRow> rows = _items;
        if (!string.IsNullOrWhiteSpace(query)) rows = rows.Where(x => x.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        rows = mode switch
        {
            "A fazer" => rows.Where(x => !x.IsCompleted),
            "OK / concluídos" => rows.Where(x => x.IsCompleted),
            "Permanentes" => rows.Where(x => x.IsPermanent),
            _ => rows
        };
        rows = rows.OrderBy(x => x.IsCompleted).ThenBy(x => x.DeadlineDate ?? DateTime.MaxValue).ThenBy(x => x.PriorityOrder).ThenBy(x => x.Description, StringComparer.CurrentCultureIgnoreCase);
        _visible.Clear();
        foreach (var item in rows) _visible.Add(item);
        CountText.Text = $"{_items.Count(x => !x.IsCompleted)} a fazer • {_items.Count(x => x.IsCompleted)} OK • {_items.Count} total";
    }

    private async Task<(string Competence, DateTime Deadline)> DefaultsAsync()
    {
        var root = await _json.LoadNodeAsync(_paths.AppSettingsFile) as JsonObject;
        var cfg = root?["corrida_pagamento"] as JsonObject;
        var rawCompetence = cfg?["competencia"]?.GetValue<string>() ?? DateTime.Today.ToString("yyyy-MM");
        var (year, month) = ParsePaymentCompetence(rawCompetence) ?? (DateTime.Today.Year, DateTime.Today.Month);
        var competence = FormatPaymentCompetence(rawCompetence);
        var deadline = ThirdBusinessDay(year, month);
        return (competence, deadline);
    }


    private static readonly string[] PaymentMonthNames =
    [
        "JANEIRO", "FEVEREIRO", "MARÇO", "ABRIL", "MAIO", "JUNHO",
        "JULHO", "AGOSTO", "SETEMBRO", "OUTUBRO", "NOVEMBRO", "DEZEMBRO"
    ];

    private static (int Year, int Month)? ParsePaymentCompetence(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        var parts = text.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var year) && int.TryParse(parts[1], out var month) && year is >= 2000 and <= 2100 && month is >= 1 and <= 12)
            return (year, month);
        return null;
    }

    private static string FormatPaymentCompetence(string? value)
    {
        var parsed = ParsePaymentCompetence(value);
        return parsed is { } p ? $"Pagamento {PaymentMonthNames[p.Month - 1]} {p.Year:0000}" : (value ?? string.Empty);
    }


    private static bool IsPaymentNonBusinessDay(DateTime date)
        => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || IsBrazilianFederalHolidayOrBankClosing(date);

    private static bool IsBrazilianFederalHolidayOrBankClosing(DateTime date)
    {
        var fixedHoliday = (date.Month, date.Day) is
            (1, 1) or (4, 21) or (5, 1) or (9, 7) or (10, 12) or (11, 2) or (11, 15) or (11, 20) or (12, 25);
        if (fixedHoliday) return true;
        var easter = EasterSunday(date.Year);
        return date.Date == easter.AddDays(-48).Date || date.Date == easter.AddDays(-47).Date || date.Date == easter.AddDays(-2).Date || date.Date == easter.AddDays(60).Date;
    }

    private static DateTime EasterSunday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = ((h + l - 7 * m + 114) % 31) + 1;
        return new DateTime(year, month, day);
    }

    private void WarnSelection() => SigfurDialog.Show(this, "Selecione um controle na lista.", "Controle de pagamentos", MessageBoxButton.OK, MessageBoxImage.Information);
    private void ShowError(Exception ex) => SigfurDialog.Show(this, ex.Message, "Controle de pagamentos", MessageBoxButton.OK, MessageBoxImage.Error);
    private static string Text(JsonObject obj, string key, string fallback = "") => obj[key]?.ToString() ?? fallback;
    private static bool Bool(JsonObject obj, string key)
    {
        try { return obj[key]?.GetValue<bool>() ?? false; }
        catch { return bool.TryParse(obj[key]?.ToString(), out var value) && value; }
    }
    private static DateTime? ParseDate(string? value) => DateTime.TryParse(value, out var date) ? date : null;
    private static DateTime ThirdBusinessDay(int year, int month)
    {
        var count = 0;
        for (var day = 1; day <= DateTime.DaysInMonth(year, month); day++)
        {
            var current = new DateTime(year, month, day);
            if (IsPaymentNonBusinessDay(current)) continue;
            if (++count == 3) return current;
        }
        return new DateTime(year, month, 3);
    }
}

public sealed class PaymentReminderRow
{
    public string Id { get; set; } = string.Empty;
    public string Category { get; set; } = "Outro";
    public string Description { get; set; } = string.Empty;
    public string Competence { get; set; } = string.Empty;
    public string Deadline { get; set; } = string.Empty;
    public string Priority { get; set; } = "Normal";
    public string Observation { get; set; } = string.Empty;
    public bool IsPermanent { get; set; }
    public string Status { get; set; } = "pendente";
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
    public string CompletedAt { get; set; } = string.Empty;
    public bool IsCompleted => Status.Equals("concluido", StringComparison.OrdinalIgnoreCase) || Status.Equals("concluído", StringComparison.OrdinalIgnoreCase) || Status.Equals("ok", StringComparison.OrdinalIgnoreCase) || Status.Equals("feito", StringComparison.OrdinalIgnoreCase);
    public string StatusLabel => IsCompleted ? "OK" : "A fazer";
    public string PermanentLabel => IsPermanent ? "Permanente" : "Pontual";
    public string CompletedAtLabel => IsCompleted && DateTime.TryParse(CompletedAt, out var date) ? date.ToString("dd/MM/yyyy HH:mm") : "—";
    public DateTime? DeadlineDate => DateTime.TryParse(Deadline, out var date) ? date : null;
    public int PriorityOrder => Priority.Equals("Alta", StringComparison.OrdinalIgnoreCase) ? 0 : Priority.Equals("Baixa", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
    public string SearchText => $"{Category} {Description} {Competence} {Deadline} {Priority} {Observation} {StatusLabel} {PermanentLabel}";
}

public sealed class VacationPaymentAttentionRow
{
    public int AllocationId { get; set; }
    public int Year { get; set; }
    public string Rank { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Period { get; set; } = string.Empty;
    public int Days { get; set; }
    public bool IsPaid { get; set; }
    public bool RequiresFoodAid { get; set; }
    public DateTime? PaidAt { get; set; }
    public string Status => IsPaid ? "PAGO" : "A PAGAR";
    public string PaidAtText => PaidAt?.ToString("dd/MM/yyyy HH:mm") ?? "—";
}

public sealed class TransportAttentionRow
{
    public TransportAttentionRow(MilitaryRecord military) => Military = military;
    public MilitaryRecord Military { get; }
    public string Rank => Military.ShortRank;
    public string Name => Military.Name;
    public string Status => Military.TransportStatus;
    public string Value => string.IsNullOrWhiteSpace(Military.TransportAidValue) ? "R$ 0,00" : Military.TransportAidValue;
    public string Phone => Military.Phone;
    public string Address => Military.Address;
}
