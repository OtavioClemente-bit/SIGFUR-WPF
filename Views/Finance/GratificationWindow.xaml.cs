using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Finance;

public partial class GratificationWindow : Window
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private readonly GratificationService _service;
    private readonly ObservableCollection<MilitaryRecord> _available = [];
    private readonly ObservableCollection<GratificationParticipant> _selected = [];
    private readonly ObservableCollection<GratificationEffectiveRow> _effective = [];
    private readonly ObservableCollection<GratificationEffectiveRow> _effectiveLeft = [];
    private readonly ObservableCollection<GratificationEffectiveRow> _effectiveRight = [];
    private List<MilitaryRecord> _allMilitary = [];
    private ICollectionView? _availableView;
    private GratificationSettings _settings = new();
    private GratificationPeriodInfo _period = new();
    private GratificationPeriodInfo _requestPeriod = new();
    private bool _loading = true;
    private int _requestDaysOverride;
    private bool _requestDaysAdjusted;

    public GratificationWindow(GratificationService service)
    {
        InitializeComponent();
        _service = service;
        App.UiState.Attach(this);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = await _service.LoadSettingsAsync();
            var profile = await App.Settings.LoadProfileAsync();
            if (string.IsNullOrWhiteSpace(_settings.RequestOrganization) || _settings.RequestOrganization.Equals("4ª Cia PE", StringComparison.OrdinalIgnoreCase))
                _settings.RequestOrganization = string.IsNullOrWhiteSpace(profile.Organization) ? _settings.RequestOrganization : profile.Organization;
            if (string.IsNullOrWhiteSpace(_settings.RequestAuthority) && (!string.IsNullOrWhiteSpace(profile.CommanderName) || !string.IsNullOrWhiteSpace(profile.CommanderRank)))
                _settings.RequestAuthority = string.Join(" - ", new[] { profile.CommanderName, profile.CommanderRank }.Where(x => !string.IsNullOrWhiteSpace(x)));
            _allMilitary = await App.MilitaryRepository.GetAllAsync();
            await App.MilitaryPreferences.ApplyAsync(_allMilitary);
            _allMilitary = _allMilitary
                .OrderBy(x => MilitaryRankService.GetOrder(x.Rank))
                .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            foreach (var military in _allMilitary) _available.Add(military);
            _availableView = CollectionViewSource.GetDefaultView(_available);
            _availableView.Filter = FilterAvailable;
            if (_availableView is ListCollectionView militaryView) militaryView.CustomSort = new MilitaryComparer();
            AvailableGrid.ItemsSource = _availableView;
            SelectedGrid.ItemsSource = _selected;
            EffectiveLeftGrid.ItemsSource = _effectiveLeft;
            EffectiveRightGrid.ItemsSource = _effectiveRight;

            LoadSettingsIntoControls();
            _loading = false;
            await RecalculateMainAsync(_settings.SelectedMilitaryIds);
            await ReloadEffectiveRowsAsync();
            StatusText.Text = $"{_allMilitary.Count} militar(es) carregados. A janela usa o tema global do SIGFUR.";
        }
        catch (Exception ex)
        {
            _loading = false;
            await App.Log.WriteAsync("Falha ao abrir Gratificação de Representação.", ex);
            SigfurDialog.Show(this, ex.Message, "SIGFUR — Gratificação", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadSettingsIntoControls()
    {
        DestinationBox.Text = _settings.Destination;
        PurposeBox.Text = _settings.Purpose;
        DepartureDatePicker.SelectedDate = _settings.DepartureDate;
        DepartureTimeBox.Text = _settings.DepartureTime;
        ReturnDatePicker.SelectedDate = _settings.ReturnDate;
        ReturnTimeBox.Text = _settings.ReturnTime;
        BulletinBox.Text = _settings.BulletinReference;
        SisbolSubjectBox.Text = _settings.SisbolSubject;
        SisbolCodeBox.Text = _settings.SisbolSpecificCode;
        AvailableSearchBox.Text = _settings.Search;

        RequestNatureBox.Text = _settings.RequestNature;
        RequestDescriptionBox.Text = _settings.RequestDescription;
        RequestAuthorizingDocumentBox.Text = _settings.RequestAuthorizingDocument;
        RequestLegalBasisBox.Text = _settings.RequestLegalBasis;
        RequestLocationBox.Text = _settings.RequestLocation;
        RequestStartDatePicker.SelectedDate = _settings.RequestStartDate;
        RequestStartTimeBox.Text = _settings.RequestStartTime;
        RequestEndDatePicker.SelectedDate = _settings.RequestEndDate;
        RequestEndTimeBox.Text = _settings.RequestEndTime;
        RequestBulletinBox.Text = _settings.RequestBulletin;
        RequestContactBox.Text = _settings.RequestContact;
        RequestRitexBox.Text = _settings.RequestRitex;
        RequestEmailBox.Text = _settings.RequestEmail;
        RequestAuthorityBox.Text = _settings.RequestAuthority;
        RequestOrganizationBox.Text = _settings.RequestOrganization;
        RequestCityBox.Text = _settings.RequestCity;
        _requestDaysAdjusted = _settings.RequestManualDays > 0;
        _requestDaysOverride = Math.Max(0, _settings.RequestManualDays);
        UpdateRequestDaysDisplay();
    }

    private bool FilterAvailable(object item)
    {
        if (item is not MilitaryRecord military || _selected.Any(x => x.Military.Id == military.Id)) return false;
        var query = MilitaryRankService.Normalize(AvailableSearchBox.Text);
        if (string.IsNullOrWhiteSpace(query)) return true;
        var text = MilitaryRankService.Normalize($"{military.Rank} {military.Name} {military.WarName} {military.Cpf} {military.PrecCp} {military.MilitaryId}");
        var digits = MilitaryFormatting.Digits(AvailableSearchBox.Text);
        return text.Contains(query) || (!string.IsNullOrWhiteSpace(digits) && MilitaryFormatting.Digits($"{military.Cpf} {military.PrecCp} {military.MilitaryId}").Contains(digits));
    }

    private async Task RecalculateMainAsync(IEnumerable<int>? ids = null)
    {
        _period = ReadMainPeriod();
        if (!_period.IsValid)
        {
            PeriodSummaryText.Text = _period.Error;
            PeriodSummaryText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        }
        else
        {
            PeriodSummaryText.Text = $"{_period.DurationText}  •  {_period.IndemnifiableDays} dia(s) indenizável(is).  {_period.RuleText}";
            PeriodSummaryText.Foreground = (System.Windows.Media.Brush)FindResource(_period.IndemnifiableDays > 0 ? "SuccessBrush" : "WarningBrush");
        }

        var wantedIds = (ids ?? _selected.Select(x => x.Military.Id)).Distinct().ToList();
        var wanted = wantedIds.Select(id => _allMilitary.FirstOrDefault(x => x.Id == id)).Where(x => x is not null).Cast<MilitaryRecord>().ToList();
        var participants = await _service.BuildParticipantsAsync(wanted, _period.IsValid ? _period.IndemnifiableDays : 0);
        _selected.Clear();
        foreach (var participant in participants) _selected.Add(participant);
        _availableView?.Refresh();
        UpdateMainSummary();
    }

    private GratificationPeriodInfo ReadMainPeriod()
    {
        if (!GratificationService.TryCombine(DepartureDatePicker.SelectedDate, DepartureTimeBox.Text, out var start))
            return new GratificationPeriodInfo { IsValid = false, Error = "Informe uma data e hora de saída válidas." };
        if (!GratificationService.TryCombine(ReturnDatePicker.SelectedDate, ReturnTimeBox.Text, out var end))
            return new GratificationPeriodInfo { IsValid = false, Error = "Informe uma data e hora de retorno válidas." };
        return _service.CalculatePeriod(start, end);
    }

    private GratificationPeriodInfo ReadRequestPeriod()
    {
        if (!GratificationService.TryCombine(RequestStartDatePicker.SelectedDate, RequestStartTimeBox.Text, out var start))
            return new GratificationPeriodInfo { IsValid = false, Error = "Informe uma data e hora de início válidas na solicitação." };
        if (!GratificationService.TryCombine(RequestEndDatePicker.SelectedDate, RequestEndTimeBox.Text, out var end))
            return new GratificationPeriodInfo { IsValid = false, Error = "Informe uma data e hora de fim válidas na solicitação." };

        var period = _service.CalculatePeriod(start, end);
        if (period.IsValid && _requestDaysAdjusted)
        {
            period.IndemnifiableDays = Math.Max(0, _requestDaysOverride);
            period.ManualDaysOverride = true;
        }
        return period;
    }

    private void UpdateMainSummary()
    {
        SelectedCountText.Text = $"{_selected.Count} militar(es)";
        TotalParticipantsText.Text = _selected.Count.ToString(PtBr);
        DaysText.Text = (_period.IsValid ? _period.IndemnifiableDays : 0).ToString(PtBr);
        DurationText.Text = _period.IsValid ? _period.DurationText : "—";
        GrandTotalText.Text = _selected.Sum(x => x.Total).ToString("C2", PtBr);
    }

    private async void MainForm_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        await RecalculateMainAsync();
    }

    private void AvailableSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _availableView?.Refresh();
    }

    private async void AddSelected_Click(object sender, RoutedEventArgs e)
    {
        var ids = _selected.Select(x => x.Military.Id).Concat(AvailableGrid.SelectedItems.Cast<MilitaryRecord>().Select(x => x.Id)).Distinct().ToList();
        await RecalculateMainAsync(ids);
    }

    private async void AddAll_Click(object sender, RoutedEventArgs e)
    {
        var ids = _selected.Select(x => x.Military.Id).Concat((_availableView?.Cast<MilitaryRecord>() ?? Enumerable.Empty<MilitaryRecord>()).Select(x => x.Id)).Distinct().ToList();
        await RecalculateMainAsync(ids);
    }

    private async void AvailableGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AvailableGrid.SelectedItem is not MilitaryRecord item) return;
        var ids = _selected.Select(x => x.Military.Id).Append(item.Id).Distinct().ToList();
        await RecalculateMainAsync(ids);
    }

    private async void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var remove = SelectedGrid.SelectedItems.Cast<GratificationParticipant>().Select(x => x.Military.Id).ToHashSet();
        await RecalculateMainAsync(_selected.Where(x => !remove.Contains(x.Military.Id)).Select(x => x.Military.Id));
    }

    private async void ClearSelected_Click(object sender, RoutedEventArgs e) => await RecalculateMainAsync(Array.Empty<int>());

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateMain()) return;
        var dialog = new SaveFileDialog { Title = "Exportar gratificação em CSV", Filter = "Arquivo CSV|*.csv", FileName = $"gratificacao_2pct_{DateTime.Now:yyyyMMdd}.csv" };
        if (dialog.ShowDialog(this) != true) return;
        await _service.ExportCsvAsync(dialog.FileName, _selected.ToList(), _period);
        StatusText.Text = $"CSV exportado: {dialog.FileName}";
    }

    private async void ExportXlsx_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateMain()) return;
        var dialog = new SaveFileDialog { Title = "Exportar gratificação em Excel", Filter = "Planilha Excel|*.xlsx", FileName = $"gratificacao_2pct_{DateTime.Now:yyyyMMdd}.xlsx" };
        if (dialog.ShowDialog(this) != true) return;
        await _service.ExportXlsxAsync(dialog.FileName, _selected.ToList(), _period);
        StatusText.Text = $"Planilha exportada: {dialog.FileName}";
    }

    private void GenerateBulletin_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateMain()) return;
        ReadControlsIntoSettings();
        try
        {
            var render = _service.BuildBulletin(_settings, _period, _selected.ToList());
            var preview = new GratificationPreviewWindow(render, _selected.Select(x => x.Military).ToList(), _settings) { Owner = this };
            preview.ShowDialog();
            SisbolSubjectBox.Text = _settings.SisbolSubject;
            SisbolCodeBox.Text = _settings.SisbolSpecificCode;
        }
        catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "Gerar boletim", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private bool ValidateMain()
    {
        if (string.IsNullOrWhiteSpace(DestinationBox.Text)) { SigfurDialog.Show(this, "Informe o local de destino.", "Campo obrigatório", MessageBoxButton.OK, MessageBoxImage.Warning); DestinationBox.Focus(); return false; }
        if (string.IsNullOrWhiteSpace(PurposeBox.Text)) { SigfurDialog.Show(this, "Informe a finalidade do deslocamento.", "Campo obrigatório", MessageBoxButton.OK, MessageBoxImage.Warning); PurposeBox.Focus(); return false; }
        if (string.IsNullOrWhiteSpace(BulletinBox.Text)) { SigfurDialog.Show(this, "Informe o boletim ou documento que autorizou o deslocamento.", "Campo obrigatório", MessageBoxButton.OK, MessageBoxImage.Warning); BulletinBox.Focus(); return false; }
        if (!_period.IsValid) { SigfurDialog.Show(this, _period.Error, "Período inválido", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        if (_period.IndemnifiableDays <= 0) { SigfurDialog.Show(this, "O período é inferior a 8 horas e não gera dia indenizável.", "Gratificação", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        if (_selected.Count == 0) { SigfurDialog.Show(this, "Adicione ao menos um militar.", "Gratificação", MessageBoxButton.OK, MessageBoxImage.Information); return false; }
        return true;
    }

    private async Task ReloadEffectiveRowsAsync()
    {
        SaveEffectiveQuantities();
        _requestPeriod = ReadRequestPeriod();
        var rows = await _service.BuildEffectiveRowsAsync(_settings, _requestPeriod.IsValid ? _requestPeriod.IndemnifiableDays : 0);
        _effective.Clear();
        foreach (var row in rows) _effective.Add(row);
        RefreshEffectiveColumns();
        UpdateRequestSummary();
    }

    private async void RequestForm_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _requestPeriod = ReadRequestPeriod();
        if (!_requestDaysAdjusted && _requestPeriod.IsValid) _requestDaysOverride = _requestPeriod.IndemnifiableDays;
        UpdateRequestDaysDisplay();
        foreach (var row in _effective) row.Days = _requestPeriod.IsValid ? _requestPeriod.IndemnifiableDays : 0;
        UpdateRequestSummary();
        await Task.CompletedTask;
    }

    private void RequestDaysMinus_Click(object sender, RoutedEventArgs e)
    {
        EnsureRequestDaysAdjustmentBase();
        _requestDaysOverride = Math.Max(0, _requestDaysOverride - 1);
        UpdateRequestDaysAfterButton();
    }

    private void RequestDaysPlus_Click(object sender, RoutedEventArgs e)
    {
        EnsureRequestDaysAdjustmentBase();
        _requestDaysOverride = Math.Min(366, _requestDaysOverride + 1);
        UpdateRequestDaysAfterButton();
    }

    private void EnsureRequestDaysAdjustmentBase()
    {
        if (_requestDaysAdjusted) return;
        var automatic = ReadRequestPeriod();
        _requestDaysOverride = automatic.IsValid ? automatic.IndemnifiableDays : 0;
        _requestDaysAdjusted = true;
    }

    private void UpdateRequestDaysAfterButton()
    {
        UpdateRequestDaysDisplay();
        _requestPeriod = ReadRequestPeriod();
        foreach (var row in _effective) row.Days = _requestPeriod.IsValid ? _requestPeriod.IndemnifiableDays : 0;
        UpdateRequestSummary();
    }

    private void UpdateRequestDaysDisplay()
    {
        if (RequestDaysText is null) return;
        var value = _requestDaysAdjusted
            ? _requestDaysOverride
            : (_requestPeriod.IsValid ? _requestPeriod.IndemnifiableDays : _requestDaysOverride);
        RequestDaysText.Text = Math.Max(0, value).ToString(PtBr);
    }

    private void EffectiveMinus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GratificationEffectiveRow row })
        {
            row.Quantity = Math.Max(0, row.Quantity - 1);
            UpdateRequestSummary();
        }
    }

    private void EffectivePlus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GratificationEffectiveRow row })
        {
            row.Quantity = Math.Min(999, row.Quantity + 1);
            UpdateRequestSummary();
        }
    }

    private void EffectiveQuantity_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (sender is TextBox box)
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Dispatcher.BeginInvoke(new Action(UpdateRequestSummary), DispatcherPriority.Background);
    }

    private void RefreshEffectiveColumns()
    {
        _effectiveLeft.Clear();
        _effectiveRight.Clear();
        var split = (_effective.Count + 1) / 2;
        for (var index = 0; index < _effective.Count; index++)
        {
            if (index < split) _effectiveLeft.Add(_effective[index]);
            else _effectiveRight.Add(_effective[index]);
        }
    }

    private void UpdateRequestSummary()
    {
        SaveEffectiveQuantities();
        _requestPeriod = ReadRequestPeriod();
        UpdateRequestDaysDisplay();
        foreach (var row in _effective) row.Days = _requestPeriod.IsValid ? _requestPeriod.IndemnifiableDays : 0;
        if (!_requestPeriod.IsValid)
        {
            RequestSummaryText.Text = _requestPeriod.Error;
            RequestSummaryText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            return;
        }
        var active = _effective.Where(x => x.Quantity > 0).ToList();
        RequestSummaryText.Text = $"{_requestPeriod.IndemnifiableDays} dia(s) indenizável(is)  •  duração real: {_requestPeriod.DurationText}  •  efetivo: {active.Sum(x => x.Quantity)}  •  total: {active.Sum(x => x.Subtotal).ToString("C2", PtBr)}\n{_requestPeriod.RuleText}";
        RequestSummaryText.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
    }

    private void SaveEffectiveQuantities()
    {
        _settings.EffectiveByRank.Clear();
        foreach (var row in _effective) _settings.EffectiveByRank[row.Rank] = Math.Max(0, row.Quantity);
    }

    private async void ReloadEffective_Click(object sender, RoutedEventArgs e) => await ReloadEffectiveRowsAsync();

    private async void GenerateDiex_Click(object sender, RoutedEventArgs e)
    {
        ReadControlsIntoSettings();
        if (!ValidateRequest()) return;
        var dialog = new SaveFileDialog { Title = "Gerar DIEx da Gratificação", Filter = "Documento Word|*.docx", InitialDirectory = _service.DefaultOutputDirectory, FileName = $"DIEx_Grat_Rep_{DateTime.Now:yyyyMMdd}.docx" };
        if (dialog.ShowDialog(this) != true) return;
        try { await _service.GenerateDiexAsync(dialog.FileName, _settings, _requestPeriod, _effective.ToList()); ShellService.OpenPath(dialog.FileName); StatusText.Text = $"DIEx gerado: {dialog.FileName}"; }
        catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "Gerar DIEx", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void GenerateMap_Click(object sender, RoutedEventArgs e)
    {
        ReadControlsIntoSettings();
        if (!ValidateRequest()) return;
        var dialog = new SaveFileDialog { Title = "Gerar Mapa da Gratificação", Filter = "Documento Word|*.docx", InitialDirectory = _service.DefaultOutputDirectory, FileName = $"Mapa_Grat_Rep_{DateTime.Now:yyyyMMdd}.docx" };
        if (dialog.ShowDialog(this) != true) return;
        try { await _service.GenerateMapAsync(dialog.FileName, _settings, _requestPeriod, _effective.ToList()); ShellService.OpenPath(dialog.FileName); StatusText.Text = $"Mapa gerado: {dialog.FileName}"; }
        catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "Gerar Mapa", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void GenerateBoth_Click(object sender, RoutedEventArgs e)
    {
        ReadControlsIntoSettings();
        if (!ValidateRequest()) return;
        var dialog = new SaveFileDialog { Title = "Escolha o nome base dos documentos", Filter = "Documento Word|*.docx", InitialDirectory = _service.DefaultOutputDirectory, FileName = $"Grat_Rep_{DateTime.Now:yyyyMMdd}.docx" };
        if (dialog.ShowDialog(this) != true) return;
        var directory = Path.GetDirectoryName(dialog.FileName) ?? _service.DefaultOutputDirectory;
        var baseName = Path.GetFileNameWithoutExtension(dialog.FileName);
        var diex = Path.Combine(directory, baseName + "_DIEx.docx");
        var map = Path.Combine(directory, baseName + "_Mapa.docx");
        try
        {
            await _service.GenerateDiexAsync(diex, _settings, _requestPeriod, _effective.ToList());
            await _service.GenerateMapAsync(map, _settings, _requestPeriod, _effective.ToList());
            ShellService.OpenPath(directory);
            StatusText.Text = $"DIEx e Mapa gerados em {directory}.";
        }
        catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "Gerar documentos", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private bool ValidateRequest()
    {
        _requestPeriod = ReadRequestPeriod();
        UpdateRequestDaysDisplay();
        foreach (var row in _effective) row.Days = _requestPeriod.IsValid ? _requestPeriod.IndemnifiableDays : 0;
        if (!_requestPeriod.IsValid)
        {
            SigfurDialog.Show(this, _requestPeriod.Error, "Período da solicitação", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (_requestPeriod.IndemnifiableDays <= 0)
        {
            SigfurDialog.Show(this, "Informe uma quantidade válida de dias ou um período que gere ao menos um dia indenizável.", "Dias indenizáveis", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (_effective.All(x => x.Quantity <= 0))
        {
            SigfurDialog.Show(this, "Informe o efetivo de ao menos um posto/graduação.", "Efetivo", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        if (string.IsNullOrWhiteSpace(_settings.RequestOrganization))
        {
            SigfurDialog.Show(this, "Informe a Organização Militar.", "Organização Militar", MessageBoxButton.OK, MessageBoxImage.Information);
            RequestOrganizationBox.Focus();
            return false;
        }
        return true;
    }

    private async void SaveInstitutionalProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = await App.Settings.LoadProfileAsync();
            profile.Organization = RequestOrganizationBox.Text.Trim();
            var authority = RequestAuthorityBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(authority))
            {
                var parts = authority.Split('-', 2, StringSplitOptions.TrimEntries);
                profile.CommanderName = parts[0];
                profile.CommanderRank = parts.Length > 1 ? parts[1] : profile.CommanderRank;
            }
            await App.Settings.SaveProfileAsync(profile);
            StatusText.Text = "Comandante e Organização Militar salvos no perfil institucional.";
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "Salvar perfil institucional", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenSalaries_Click(object sender, RoutedEventArgs e) => new SalaryWindow(App.Salaries) { Owner = this }.ShowDialog();

    private void ReadControlsIntoSettings()
    {
        _settings.Destination = DestinationBox.Text.Trim();
        _settings.Purpose = PurposeBox.Text.Trim();
        _settings.DepartureDate = DepartureDatePicker.SelectedDate ?? DateTime.Today;
        _settings.DepartureTime = DepartureTimeBox.Text.Trim();
        _settings.ReturnDate = ReturnDatePicker.SelectedDate ?? DateTime.Today;
        _settings.ReturnTime = ReturnTimeBox.Text.Trim();
        _settings.BulletinReference = BulletinBox.Text.Trim();
        _settings.SisbolSubject = SisbolSubjectBox.Text.Trim();
        _settings.SisbolSpecificCode = SisbolCodeBox.Text.Trim();
        _settings.Search = AvailableSearchBox.Text;
        _settings.SelectedMilitaryIds = _selected.Select(x => x.Military.Id).ToList();

        _settings.RequestNature = RequestNatureBox.Text.Trim();
        _settings.RequestDescription = RequestDescriptionBox.Text.Trim();
        _settings.RequestAuthorizingDocument = RequestAuthorizingDocumentBox.Text.Trim();
        _settings.RequestLegalBasis = RequestLegalBasisBox.Text.Trim();
        _settings.RequestLocation = RequestLocationBox.Text.Trim();
        _settings.RequestStartDate = RequestStartDatePicker.SelectedDate ?? DateTime.Today;
        _settings.RequestStartTime = RequestStartTimeBox.Text.Trim();
        _settings.RequestEndDate = RequestEndDatePicker.SelectedDate ?? DateTime.Today;
        _settings.RequestEndTime = RequestEndTimeBox.Text.Trim();
        _settings.RequestBulletin = RequestBulletinBox.Text.Trim();
        _settings.RequestContact = RequestContactBox.Text.Trim();
        _settings.RequestRitex = RequestRitexBox.Text.Trim();
        _settings.RequestEmail = RequestEmailBox.Text.Trim();
        _settings.RequestAuthority = RequestAuthorityBox.Text.Trim();
        _settings.RequestOrganization = RequestOrganizationBox.Text.Trim();
        _settings.RequestCity = RequestCityBox.Text.Trim();
        _settings.RequestManualDays = _requestDaysAdjusted ? Math.Max(0, _requestDaysOverride) : 0;
        _requestPeriod = ReadRequestPeriod();
        foreach (var row in _effective) row.Days = _requestPeriod.IsValid ? _requestPeriod.IndemnifiableDays : 0;
        SaveEffectiveQuantities();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        try { ReadControlsIntoSettings(); await _service.SaveSettingsAsync(_settings); }
        catch (Exception ex) { await App.Log.WriteAsync("Falha ao salvar preferências da Gratificação.", ex); }
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { AvailableSearchBox.Focus(); AvailableSearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { AddAll_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.G && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { GenerateBulletin_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Delete) { RemoveSelected_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.F5) { await RecalculateMainAsync(); await ReloadEffectiveRowsAsync(); e.Handled = true; }
    }

    private void MilitaryGrid_Sorting(object sender, DataGridSortingEventArgs e) { }
    private void ParticipantGrid_Sorting(object sender, DataGridSortingEventArgs e) { }

    private sealed class MilitaryComparer : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not MilitaryRecord left || y is not MilitaryRecord right) return 0;
            return MilitaryRankService.Compare(left.Rank, left.Name, right.Rank, right.Name);
        }
    }
}
