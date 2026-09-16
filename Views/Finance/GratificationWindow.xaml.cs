using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views.Bulletin;

namespace SIGFUR.Wpf.Views.Finance;

public partial class GratificationWindow : Window
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private readonly GratificationService _service;
    private readonly GratificationProcessService _processService;
    private readonly ObservableCollection<MilitaryRecord> _available = [];
    private readonly ObservableCollection<GratificationParticipant> _selected = [];
    private readonly ObservableCollection<GratificationEffectiveRow> _effective = [];
    private readonly ObservableCollection<GratificationEffectiveRow> _effectiveLeft = [];
    private readonly ObservableCollection<GratificationEffectiveRow> _effectiveRight = [];
    private readonly ObservableCollection<GratificationSpedAttachment> _spedAttachments = [];
    private readonly ObservableCollection<GratificationMissionLot> _missionLots = [];
    private readonly HashSet<int> _confirmedNonCurrentExerciseYears = [];
    private List<MilitaryRecord> _allMilitary = [];
    private ICollectionView? _availableView;
    private GratificationSettings _settings = new();
    private GratificationPeriodInfo _period = new();
    private GratificationPeriodInfo _requestPeriod = new();
    private bool _loading = true;
    private int _requestDaysOverride;
    private bool _requestDaysAdjusted;
    private CancellationTokenSource? _mainRecalculateCts;
    private CancellationTokenSource? _availableSearchCts;
    private int _mainRecalculateVersion;
    private GratificationMissionLot? _activeMissionLot;
    private bool _switchingMissionLot;

    public GratificationWindow(GratificationService service)
    {
        InitializeComponent();
        _service = service;
        _processService = new GratificationProcessService(App.Paths);
        App.UiState.Attach(this);
    }

    private void OpenSisbolHistory_Click(object sender, RoutedEventArgs e)
    {
        var window = new SisbolSubmissionHistoryWindow { Owner = this };
        window.Show();
        window.Activate();
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
            SpedAttachmentList.ItemsSource = _spedAttachments;

            InitializeSalaryReferenceYears();
            InitializeActivityOptions();
            InitializeMissionLots();
            LoadSettingsIntoControls();
            RefreshSpedAttachments();
            await RefreshCurrentProcessLabelAsync();
            _loading = false;
            await RecalculateMainAsync(_settings.SelectedMilitaryIds, showStatus: true);
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
        RequestLotNameBox.Text = _activeMissionLot?.Name ?? "1ª leva";
        RequestStartDatePicker.SelectedDate = _activeMissionLot?.StartDate ?? _settings.RequestStartDate;
        RequestStartTimeBox.Text = _activeMissionLot?.StartTime ?? _settings.RequestStartTime;
        RequestEndDatePicker.SelectedDate = _activeMissionLot?.EndDate ?? _settings.RequestEndDate;
        RequestEndTimeBox.Text = _activeMissionLot?.EndTime ?? _settings.RequestEndTime;
        RequestBulletinBox.Text = _settings.RequestBulletin;
        RequestContactBox.Text = _settings.RequestContact;
        RequestRitexBox.Text = _settings.RequestRitex;
        RequestEmailBox.Text = _settings.RequestEmail;
        RequestAuthorityBox.Text = _settings.RequestAuthority;
        RequestOrganizationBox.Text = _settings.RequestOrganization;
        RequestCityBox.Text = _settings.RequestCity;
        GratSpedSenderBox.Text = _settings.SpedSender;
        GratSpedRecipientBox.Text = _settings.SpedRecipient;
        GratSpedClassificationBox.Text = _settings.SpedClassification;
        GratSpedPurposeBox.Text = _settings.SpedDocumentPurpose;
        GratSpedSubjectBox.Text = _settings.SpedSubject;
        var salaryReferenceYear = _settings.RequestSalaryReferenceYear >= 2020 && _settings.RequestSalaryReferenceYear <= DateTime.Today.Year
            ? _settings.RequestSalaryReferenceYear
            : DateTime.Today.Year;
        _settings.RequestSalaryReferenceYear = salaryReferenceYear;
        SalaryReferenceYearBox.SelectedValue = salaryReferenceYear;
        RefreshMandatoryDocumentStatus();
        // A solicitação usa por padrão o mesmo cálculo automático do boletim:
        // períodos de 24h e fração residual mínima de 8h.
        _requestDaysAdjusted = false;
        _requestDaysOverride = 0;
        _settings.RequestManualDays = 0;
        UpdateRequestDaysDisplay();
    }

    private void InitializeMissionLots()
    {
        _missionLots.Clear();
        foreach (var lot in _settings.RequestMissionLots ?? []) _missionLots.Add(lot);
        if (_missionLots.Count == 0)
        {
            _missionLots.Add(new GratificationMissionLot
            {
                Name = "1ª leva",
                StartDate = _settings.RequestStartDate,
                StartTime = _settings.RequestStartTime,
                EndDate = _settings.RequestEndDate,
                EndTime = _settings.RequestEndTime,
                EffectiveByRank = new Dictionary<string, int>(_settings.EffectiveByRank, StringComparer.OrdinalIgnoreCase)
            });
        }
        _settings.RequestMissionLots = _missionLots.ToList();
        RequestMissionLotsList.ItemsSource = _missionLots;
        _activeMissionLot = _missionLots[0];
        _settings.EffectiveByRank = new Dictionary<string, int>(_activeMissionLot.EffectiveByRank, StringComparer.OrdinalIgnoreCase);
        RequestMissionLotsList.SelectedIndex = 0;
        RefreshMissionLotsSummary();
    }

    private void CaptureActiveMissionLotFields()
    {
        if (_activeMissionLot is null || _switchingMissionLot) return;
        _activeMissionLot.Name = string.IsNullOrWhiteSpace(RequestLotNameBox.Text)
            ? $"{Math.Max(1, _missionLots.IndexOf(_activeMissionLot) + 1)}ª leva"
            : RequestLotNameBox.Text.Trim();
        _activeMissionLot.StartDate = RequestStartDatePicker.SelectedDate ?? DateTime.Today;
        _activeMissionLot.StartTime = RequestStartTimeBox.Text.Trim();
        _activeMissionLot.EndDate = RequestEndDatePicker.SelectedDate ?? _activeMissionLot.StartDate;
        _activeMissionLot.EndTime = RequestEndTimeBox.Text.Trim();
        _settings.RequestStartDate = _activeMissionLot.StartDate;
        _settings.RequestStartTime = _activeMissionLot.StartTime;
        _settings.RequestEndDate = _activeMissionLot.EndDate;
        _settings.RequestEndTime = _activeMissionLot.EndTime;
        RequestMissionLotsList.Items.Refresh();
    }

    private void LoadActiveMissionLotIntoControls()
    {
        if (_activeMissionLot is null) return;
        _switchingMissionLot = true;
        try
        {
            RequestLotNameBox.Text = _activeMissionLot.Name;
            RequestStartDatePicker.SelectedDate = _activeMissionLot.StartDate;
            RequestStartTimeBox.Text = _activeMissionLot.StartTime;
            RequestEndDatePicker.SelectedDate = _activeMissionLot.EndDate;
            RequestEndTimeBox.Text = _activeMissionLot.EndTime;
            _settings.EffectiveByRank = new Dictionary<string, int>(_activeMissionLot.EffectiveByRank, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _switchingMissionLot = false;
        }
    }

    private async void RequestMissionLotsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _switchingMissionLot || RequestMissionLotsList.SelectedItem is not GratificationMissionLot selectedLot) return;
        SaveEffectiveQuantities();
        CaptureActiveMissionLotFields();
        _activeMissionLot = selectedLot;
        LoadActiveMissionLotIntoControls();
        await ReloadEffectiveRowsAsync(captureCurrentQuantities: false);
        StatusText.Text = $"{selectedLot.Name} selecionada. Informe o período e o efetivo desta leva.";
    }

    private void AddMissionLot_Click(object sender, RoutedEventArgs e)
    {
        SaveEffectiveQuantities();
        CaptureActiveMissionLotFields();
        var source = _activeMissionLot;
        var lot = new GratificationMissionLot
        {
            Name = $"{_missionLots.Count + 1}ª leva",
            StartDate = source?.StartDate ?? DateTime.Today,
            StartTime = source?.StartTime ?? "06:00",
            EndDate = source?.EndDate ?? DateTime.Today,
            EndTime = source?.EndTime ?? "21:00"
        };
        _missionLots.Add(lot);
        _settings.RequestMissionLots = _missionLots.ToList();
        RequestMissionLotsList.SelectedItem = lot;
        RefreshMissionLotsSummary();
    }

    private async void RemoveMissionLot_Click(object sender, RoutedEventArgs e)
    {
        if (_missionLots.Count <= 1)
        {
            SigfurDialog.Show(this, "A missão deve possuir ao menos uma leva.", "Levas da missão", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (RequestMissionLotsList.SelectedItem is not GratificationMissionLot selectedLot) return;
        var index = _missionLots.IndexOf(selectedLot);
        _switchingMissionLot = true;
        _missionLots.Remove(selectedLot);
        _settings.RequestMissionLots = _missionLots.ToList();
        _activeMissionLot = _missionLots[Math.Min(index, _missionLots.Count - 1)];
        RequestMissionLotsList.SelectedItem = _activeMissionLot;
        _switchingMissionLot = false;
        LoadActiveMissionLotIntoControls();
        await ReloadEffectiveRowsAsync(captureCurrentQuantities: false);
        RefreshMissionLotsSummary();
        StatusText.Text = "Leva removida. Os totais foram recalculados.";
    }

    private void RefreshMissionLotsSummary()
    {
        var validLots = _missionLots.Where(lot => lot.IndemnifiableDays > 0 && lot.EffectiveTotal > 0).ToList();
        MissionLotsSummaryText.Text = $"{_missionLots.Count} leva(s) cadastrada(s)  •  {validLots.Sum(lot => lot.EffectiveTotal)} vínculo(s) de efetivo  •  {validLots.Sum(lot => lot.PersonDays)} homens-dia.";
        RequestMissionLotsList.Items.Refresh();
    }

    private void InitializeActivityOptions()
    {
        var currentNature = RequestNatureBox.Text;
        var currentDescription = RequestDescriptionBox.Text;
        RequestNatureBox.ItemsSource = GratificationActivityCatalog.NatureOptions
            .Concat(_settings.RequestNatureHistory ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        RequestDescriptionBox.ItemsSource = GratificationActivityCatalog.DescriptionOptions
            .Concat(_settings.RequestDescriptionHistory ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        RequestNatureBox.Text = currentNature;
        RequestDescriptionBox.Text = currentDescription;
    }

    private void RequestNature_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || RequestNatureBox.SelectedItem is not string selectedNature) return;
        var option = GratificationActivityCatalog.FindByNature(selectedNature);
        if (option is null) return;

        RequestDescriptionBox.Text = option.SuggestedDescription;
        RequestLegalBasisBox.Text = option.LegalBasis;
        if (option.EffectiveFrom is DateTime effectiveFrom && RequestStartDatePicker.SelectedDate < effectiveFrom)
            StatusText.Text = "Atenção: esta hipótese foi introduzida pelo Decreto nº 13.052, de 3 JUL 2026. Confira a aplicabilidade à data da atividade.";
        else
            StatusText.Text = $"Hipótese selecionada: {option.Group}. Enquadramento atualizado automaticamente.";
        UpdateRequestSummary();
    }

    private void ActivitySelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) UpdateRequestSummary();
    }

    private async void ActivityField_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_loading) return;
        try
        {
            _settings.RequestNature = RequestNatureBox.Text.Trim();
            _settings.RequestDescription = RequestDescriptionBox.Text.Trim();
            _settings.RequestLegalBasis = RequestLegalBasisBox.Text.Trim();
            RememberActivityOptions();
            await _service.SaveSettingsAsync(_settings);
            InitializeActivityOptions();
            StatusText.Text = "Natureza e descrição salvas para reutilização.";
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha ao salvar natureza e descrição da Gratificação de Representação.", ex);
        }
    }

    private void RememberActivityOptions()
    {
        _settings.RequestNatureHistory ??= [];
        _settings.RequestDescriptionHistory ??= [];
        AddActivityHistory(_settings.RequestNatureHistory, RequestNatureBox.Text);
        AddActivityHistory(_settings.RequestDescriptionHistory, RequestDescriptionBox.Text);
    }

    private static void AddActivityHistory(ICollection<string> history, string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text) || history.Contains(text, StringComparer.CurrentCultureIgnoreCase)) return;
        history.Add(text);
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

    private async Task RecalculateMainAsync(IEnumerable<int>? ids = null, bool showStatus = false)
    {
        _mainRecalculateCts?.Cancel();
        _mainRecalculateCts?.Dispose();
        var cts = new CancellationTokenSource();
        _mainRecalculateCts = cts;
        var version = ++_mainRecalculateVersion;

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
        if (showStatus && wanted.Count > 0) StatusText.Text = "Atualizando valores da gratificação...";
        List<GratificationParticipant> participants;
        try
        {
            participants = await _service.BuildParticipantsAsync(wanted, _period.IsValid ? _period.IndemnifiableDays : 0, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (version != _mainRecalculateVersion || cts.IsCancellationRequested) return;
        _selected.Clear();
        foreach (var participant in participants) _selected.Add(participant);
        _availableView?.Refresh();
        UpdateMainSummary();
        if (showStatus) StatusText.Text = $"{_selected.Count} militar(es) selecionado(s).";
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

        return _service.CalculatePeriod(start, end);
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
        if (sender is DatePicker datePicker && datePicker.SelectedDate is DateTime selectedDate && selectedDate.Year != DateTime.Today.Year)
            ConfirmNonCurrentExerciseYears([selectedDate.Year], "período da missão");
        if (sender is TextBox box && IsMainTextOnlyField(box))
        {
            ReadMainTextFieldsIntoSettings();
            StatusText.Text = "Texto atualizado.";
            return;
        }
        await DebounceMainRecalculateAsync();
    }

    private void AvailableSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        DebounceAvailableSearch();
    }

    private async void AddSelected_Click(object sender, RoutedEventArgs e)
    {
        var ids = _selected.Select(x => x.Military.Id).Concat(AvailableGrid.SelectedItems.Cast<MilitaryRecord>().Select(x => x.Id)).Distinct().ToList();
        await RecalculateMainAsync(ids, showStatus: true);
    }

    private async void AddAll_Click(object sender, RoutedEventArgs e)
    {
        var ids = _selected.Select(x => x.Military.Id).Concat((_availableView?.Cast<MilitaryRecord>() ?? Enumerable.Empty<MilitaryRecord>()).Select(x => x.Id)).Distinct().ToList();
        await RecalculateMainAsync(ids, showStatus: true);
    }

    private async void AvailableGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AvailableGrid.SelectedItem is not MilitaryRecord item) return;
        var ids = _selected.Select(x => x.Military.Id).Append(item.Id).Distinct().ToList();
        await RecalculateMainAsync(ids, showStatus: true);
    }

    private async void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var remove = SelectedGrid.SelectedItems.Cast<GratificationParticipant>().Select(x => x.Military.Id).ToHashSet();
        await RecalculateMainAsync(_selected.Where(x => !remove.Contains(x.Military.Id)).Select(x => x.Military.Id), showStatus: true);
    }

    private async void ClearSelected_Click(object sender, RoutedEventArgs e) => await RecalculateMainAsync(Array.Empty<int>(), showStatus: true);

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
        if (!_period.IsValid) { SigfurDialog.Show(this, _period.Error, "Período inválido", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        if (!ValidateCurrentExerciseYear(_period.Start, _period.End, "período do boletim")) return false;
        if (!ValidateMandatoryRequestDocuments()) return false;
        if (string.IsNullOrWhiteSpace(DestinationBox.Text)) { SigfurDialog.Show(this, "Informe o local de destino.", "Campo obrigatório", MessageBoxButton.OK, MessageBoxImage.Warning); DestinationBox.Focus(); return false; }
        if (string.IsNullOrWhiteSpace(PurposeBox.Text)) { SigfurDialog.Show(this, "Informe a finalidade do deslocamento.", "Campo obrigatório", MessageBoxButton.OK, MessageBoxImage.Warning); PurposeBox.Focus(); return false; }
        if (string.IsNullOrWhiteSpace(BulletinBox.Text)) { SigfurDialog.Show(this, "Informe o boletim ou documento que autorizou o deslocamento.", "Campo obrigatório", MessageBoxButton.OK, MessageBoxImage.Warning); BulletinBox.Focus(); return false; }
        if (_period.IndemnifiableDays <= 0) { SigfurDialog.Show(this, "O período é inferior a 8 horas e não gera dia indenizável.", "Gratificação", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        if (_selected.Count == 0) { SigfurDialog.Show(this, "Adicione ao menos um militar.", "Gratificação", MessageBoxButton.OK, MessageBoxImage.Information); return false; }
        return true;
    }

    private async Task ReloadEffectiveRowsAsync(bool captureCurrentQuantities = true)
    {
        if (captureCurrentQuantities && _effective.Count > 0)
            SaveEffectiveQuantities();
        _requestPeriod = ReadRequestPeriod();
        var rows = await _service.BuildEffectiveRowsAsync(_settings, _requestPeriod.IsValid ? _requestPeriod.IndemnifiableDays : 0);
        _effective.Clear();
        foreach (var row in rows) _effective.Add(row);
        RefreshEffectiveColumns();
        var salaryReference = await _service.GetSalaryReferenceAsync(_settings);
        SalaryReferenceStatusText.Text = $"{salaryReference.EffectivePeriod}. {salaryReference.RetrievalStatus}. Fonte: {salaryReference.LegalBasis}.";
        UpdateRequestSummary();
    }

    private async void RequestForm_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _switchingMissionLot) return;
        CaptureActiveMissionLotFields();
        if (sender is DatePicker datePicker && datePicker.SelectedDate is DateTime selectedDate && selectedDate.Year != DateTime.Today.Year)
            ConfirmNonCurrentExerciseYears([selectedDate.Year], _activeMissionLot?.Name ?? "período da solicitação");
        if (ReferenceEquals(sender, RequestStartDatePicker))
        {
            _settings.RequestStartDate = RequestStartDatePicker.SelectedDate ?? DateTime.Today;
            await ReloadEffectiveRowsAsync();
            return;
        }
        _requestPeriod = ReadRequestPeriod();
        if (!_requestDaysAdjusted && _requestPeriod.IsValid) _requestDaysOverride = _requestPeriod.IndemnifiableDays;
        UpdateRequestDaysDisplay();
        foreach (var row in _effective) row.Days = _requestPeriod.IsValid ? _requestPeriod.IndemnifiableDays : 0;
        UpdateRequestSummary();
        await Task.CompletedTask;
    }

    private void InitializeSalaryReferenceYears()
    {
        var options = new List<SalaryReferenceYearOption>();
        for (var year = DateTime.Today.Year; year >= 2020; year--)
            options.Add(new SalaryReferenceYearOption(year, year.ToString(PtBr)));
        SalaryReferenceYearBox.DisplayMemberPath = nameof(SalaryReferenceYearOption.Label);
        SalaryReferenceYearBox.SelectedValuePath = nameof(SalaryReferenceYearOption.Year);
        SalaryReferenceYearBox.ItemsSource = options;
    }

    private async void SalaryReferenceYear_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SalaryReferenceYearBox.SelectedValue is not int year) return;
        var currentYear = DateTime.Today.Year;
        if (year != currentYear)
        {
            if (!ConfirmNonCurrentExerciseYears([year], "ano-base do soldo"))
            {
                _loading = true;
                _settings.RequestSalaryReferenceYear = currentYear;
                SalaryReferenceYearBox.SelectedValue = currentYear;
                _loading = false;
                await ReloadEffectiveRowsAsync();
                StatusText.Text = $"Ano-base mantido em {currentYear}. A opção {year} não foi confirmada.";
                return;
            }
        }
        _settings.RequestSalaryReferenceYear = year;
        StatusText.Text = "Consultando a tabela oficial de soldos do ano-base...";
        try
        {
            await ReloadEffectiveRowsAsync();
            StatusText.Text = year == currentYear
                ? $"Soldo do exercício atual ({year}) aplicado ao cálculo."
                : $"Soldo do ano-base {year} aplicado após confirmação.";
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "Ano-base do soldo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenOfficialSalarySource_Click(object sender, RoutedEventArgs e)
    {
        if (!FileOpenService.TryOpenUrl(SalaryService.OfficialLawUrl, out var error))
            SigfurDialog.Show(this, error, "Fonte oficial de soldos", MessageBoxButton.OK, MessageBoxImage.Error);
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
        var validLots = _missionLots.Where(lot => lot.IndemnifiableDays > 0 && lot.EffectiveTotal > 0).ToList();
        decimal total = 0;
        foreach (var lot in validLots)
        {
            foreach (var row in _effective)
            {
                var quantity = FindMissionLotQuantity(lot, row.Rank, row.ShortRank);
                total += row.DailyRate * quantity * lot.IndemnifiableDays;
            }
        }
        RequestSummaryText.Text = $"Leva selecionada: {_requestPeriod.IndemnifiableDays} dia(s) indenizável(is), {_activeMissionLot?.EffectiveTotal ?? 0} militar(es).\nConsolidado: {validLots.Count} leva(s)  •  {validLots.Sum(lot => lot.EffectiveTotal)} vínculo(s) de efetivo  •  {validLots.Sum(lot => lot.PersonDays)} homens-dia  •  total: {total.ToString("C2", PtBr)}";
        RequestSummaryText.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
        RefreshMissionLotsSummary();
    }

    private void SaveEffectiveQuantities()
    {
        var quantities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _effective) quantities[row.Rank] = Math.Max(0, row.Quantity);
        _settings.EffectiveByRank = new Dictionary<string, int>(quantities, StringComparer.OrdinalIgnoreCase);
        if (_activeMissionLot is not null)
            _activeMissionLot.EffectiveByRank = new Dictionary<string, int>(quantities, StringComparer.OrdinalIgnoreCase);
        _settings.RequestMissionLots = _missionLots.ToList();
    }

    private static int FindMissionLotQuantity(GratificationMissionLot lot, params string[] ranks)
    {
        foreach (var pair in lot.EffectiveByRank)
            if (ranks.Any(rank => MilitaryRankService.Normalize(pair.Key).Equals(MilitaryRankService.Normalize(rank), StringComparison.OrdinalIgnoreCase)))
                return Math.Max(0, pair.Value);
        return 0;
    }

    private async void ReloadEffective_Click(object sender, RoutedEventArgs e) => await ReloadEffectiveRowsAsync();

    private async void ClearMissionLotEffective_Click(object sender, RoutedEventArgs e)
    {
        if (_activeMissionLot is null)
        {
            SigfurDialog.Show(this, "Selecione uma leva para zerar o efetivo.", "Zerar efetivo da leva", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var lotName = _activeMissionLot.Name;
        if (_effective.All(row => row.Quantity == 0))
        {
            StatusText.Text = $"O efetivo da {lotName} já está zerado.";
            return;
        }

        var confirmation = SigfurDialog.Show(
            this,
            $"Deseja zerar todo o efetivo da {lotName}?\n\nAs demais levas não serão alteradas.",
            "Zerar efetivo da leva",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes) return;

        foreach (var row in _effective)
            row.Quantity = 0;

        UpdateRequestSummary();

        try
        {
            ReadControlsIntoSettings();
            await _service.SaveSettingsAsync(_settings);
            if (!string.IsNullOrWhiteSpace(_settings.CurrentProcessId))
                await _processService.SaveDraftAsync(_settings, _service.GetSpedAttachments(_settings));
            StatusText.Text = $"Efetivo da {lotName} zerado. As demais levas foram mantidas.";
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, $"O efetivo foi zerado na tela, mas não foi possível salvar a alteração.\n\n{ex.Message}", "Zerar efetivo da leva", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = $"Efetivo da {lotName} zerado, com falha ao salvar.";
        }
    }

    private async void NewGratificationProcess_Click(object sender, RoutedEventArgs e)
    {
        _confirmedNonCurrentExerciseYears.Clear();
        _settings.CurrentProcessId = string.Empty;
        _settings.RequestSalaryReferenceYear = DateTime.Today.Year;
        SalaryReferenceYearBox.SelectedValue = DateTime.Today.Year;
        _settings.EffectiveByRank.Clear();
        _switchingMissionLot = true;
        _missionLots.Clear();
        _activeMissionLot = new GratificationMissionLot
        {
            Name = "1ª leva",
            StartDate = DateTime.Today,
            StartTime = "06:00",
            EndDate = DateTime.Today,
            EndTime = "21:00"
        };
        _missionLots.Add(_activeMissionLot);
        _settings.RequestMissionLots = _missionLots.ToList();
        RequestMissionLotsList.SelectedItem = _activeMissionLot;
        _switchingMissionLot = false;
        LoadActiveMissionLotIntoControls();
        foreach (var attachment in _service.GetSpedAttachments(_settings).ToList())
            _service.RemoveSpedAttachment(_settings, attachment.FileName);
        _settings.RequestAuthorizingAttachmentFile = string.Empty;
        _settings.RequestBulletinAttachmentFile = string.Empty;
        _settings.RequestAuthorizingDocument = string.Empty;
        _settings.RequestBulletin = string.Empty;
        RequestAuthorizingDocumentBox.Clear();
        RequestBulletinBox.Clear();
        BulletinBox.Clear();
        RefreshSpedAttachments();
        await ReloadEffectiveRowsAsync(captureCurrentQuantities: false);
        await _service.SaveSettingsAsync(_settings);
        CurrentGratificationProcessText.Text = "Novo processo — informe os dados e execute o SPED";
        StatusText.Text = "Novo processo de Gratificação de Representação iniciado.";
    }

    private async void SaveGratificationProcess_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ReadControlsIntoSettings();
            if (!ValidateMissionLotExerciseYears()) return;
            var attachments = _service.GetSpedAttachments(_settings);
            var process = await _processService.SaveDraftAsync(_settings, attachments);
            await _service.SaveSettingsAsync(_settings);
            await RefreshCurrentProcessLabelAsync();
            StatusText.Text = $"Processo salvo: {process.Title}";
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "Salvar processo", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OpenGratificationProcesses_Click(object sender, RoutedEventArgs e)
    {
        var window = new GratificationProcessesWindow(_processService) { Owner = this };
        if (window.ShowDialog() != true || window.SelectedProcess is null) return;
        try
        {
            _confirmedNonCurrentExerciseYears.Clear();
            _settings = _processService.RestoreWorkingCopy(window.SelectedProcess);
            _loading = true;
            InitializeActivityOptions();
            InitializeMissionLots();
            LoadSettingsIntoControls();
            RefreshSpedAttachments();
            _loading = false;
            await ReloadEffectiveRowsAsync(captureCurrentQuantities: false);
            await _service.SaveSettingsAsync(_settings);
            await RefreshCurrentProcessLabelAsync();
            StatusText.Text = $"Processo aberto: {window.SelectedProcess.Title}";
        }
        catch (Exception ex)
        {
            _loading = false;
            SigfurDialog.Show(this, ex.Message, "Abrir processo", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenCurrentGratificationProcessFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_settings.CurrentProcessId))
        {
            SigfurDialog.Show(this, "Salve ou execute este processo primeiro.", "Novo processo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ShellService.OpenPath(_processService.GetProcessDirectory(_settings.CurrentProcessId));
    }

    private async Task RefreshCurrentProcessLabelAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.CurrentProcessId))
        {
            CurrentGratificationProcessText.Text = "Novo processo — ainda não salvo";
            return;
        }
        var process = await _processService.FindAsync(_settings.CurrentProcessId);
        CurrentGratificationProcessText.Text = process is null
            ? "Novo processo — ainda não salvo"
            : $"{process.Title} • {process.Status} • atualizado em {process.UpdatedText}";
    }

    private async void AttachAuthorizingDocument_Click(object sender, RoutedEventArgs e)
        => await AttachRequiredRequestPdfAsync(authorizingDocument: true);

    private async void AttachRequestBulletin_Click(object sender, RoutedEventArgs e)
        => await AttachRequiredRequestPdfAsync(authorizingDocument: false);

    private async void AttachRequestBulletinFromLibrary_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SavedBulletinPickerWindow(attachmentMode: true) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedReference is not { } selected) return;
        await AttachRequiredRequestPdfPathAsync(
            authorizingDocument: false,
            selected.Path,
            selected.ReferenceText);
    }

    private async Task AttachRequiredRequestPdfAsync(bool authorizingDocument)
    {
        var dialog = new OpenFileDialog
        {
            Title = authorizingDocument ? "Selecionar o DIEx que autorizou a missão" : "Selecionar o BI ou BAR que publicou o deslocamento",
            Filter = "Documento PDF|*.pdf",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        await AttachRequiredRequestPdfPathAsync(authorizingDocument, dialog.FileName);
    }

    private async Task AttachRequiredRequestPdfPathAsync(
        bool authorizingDocument,
        string sourcePath,
        string knownReference = "")
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            SigfurDialog.Show(this, "O PDF selecionado não foi encontrado na biblioteca.",
                authorizingDocument ? "Documento autorizador" : "Publicação", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var attachmentSaved = false;
        var newFile = Path.GetFileName(sourcePath);
        try
        {
            StatusText.Text = "Salvando o documento no processo...";
            var oldFile = authorizingDocument ? _settings.RequestAuthorizingAttachmentFile : _settings.RequestBulletinAttachmentFile;
            _service.ImportSpedAttachments(_settings, [sourcePath]);
            var replacingDocument = !string.Equals(oldFile, newFile, StringComparison.OrdinalIgnoreCase);

            if (authorizingDocument)
            {
                _settings.RequestAuthorizingAttachmentFile = newFile;
                if (replacingDocument)
                {
                    _settings.RequestAuthorizingDocument = string.Empty;
                    RequestAuthorizingDocumentBox.Clear();
                }
            }
            else
            {
                _settings.RequestBulletinAttachmentFile = newFile;
                if (replacingDocument)
                {
                    _settings.RequestBulletin = string.Empty;
                    _settings.BulletinReference = string.Empty;
                    RequestBulletinBox.Clear();
                    BulletinBox.Clear();
                }
            }

            if (!string.IsNullOrWhiteSpace(oldFile)
                && !oldFile.Equals(newFile, StringComparison.OrdinalIgnoreCase)
                && !oldFile.Equals(authorizingDocument ? _settings.RequestBulletinAttachmentFile : _settings.RequestAuthorizingAttachmentFile, StringComparison.OrdinalIgnoreCase))
                _service.RemoveSpedAttachment(_settings, oldFile);

            RefreshSpedAttachments();
            await _service.SaveSettingsAsync(_settings);
            if (!string.IsNullOrWhiteSpace(_settings.CurrentProcessId))
                await _processService.SaveDraftAsync(_settings, _service.GetSpedAttachments(_settings));
            attachmentSaved = true;

            var reference = knownReference.Trim();
            var readByOcr = false;
            if (string.IsNullOrWhiteSpace(reference))
            {
                StatusText.Text = "Lendo número e data do documento...";
                var text = string.Empty;
                try
                {
                    text = await App.PdfText.ExtractAsync(sourcePath);
                }
                catch
                {
                    // PDFs digitalizados ou com estrutura inválida seguem diretamente para o OCR.
                }
                reference = authorizingDocument
                    ? ParseAuthorizingDiexReference(text)
                    : ParseBulletinReference(text, RequestOrganizationBox.Text);
                if (string.IsNullOrWhiteSpace(reference))
                {
                    StatusText.Text = "Documento digitalizado detectado. Executando OCR...";
                    var ocrText = await App.CertificateOcr.ExtractDocumentTextAsync(sourcePath);
                    reference = authorizingDocument
                        ? ParseAuthorizingDiexReference(ocrText)
                        : ParseBulletinReference(ocrText, RequestOrganizationBox.Text);
                    readByOcr = !string.IsNullOrWhiteSpace(reference);
                }
            }
            if (string.IsNullOrWhiteSpace(reference))
                throw new InvalidOperationException(authorizingDocument
                    ? "O PDF foi salvo e anexado ao processo, mas não consegui identificar o número e a data do DIEx, mesmo após o OCR. Você pode preencher a referência manualmente."
                    : "O PDF foi salvo e anexado ao processo, mas não consegui identificar o número e a data do BI/BAR/ADT, mesmo após o OCR. Você pode preencher a referência manualmente.");

            if (authorizingDocument)
            {
                _settings.RequestAuthorizingDocument = reference;
                RequestAuthorizingDocumentBox.Text = reference;
            }
            else
            {
                _settings.RequestBulletin = reference;
                _settings.BulletinReference = reference;
                RequestBulletinBox.Text = reference;
                BulletinBox.Text = reference;
            }

            RefreshSpedAttachments();
            await _service.SaveSettingsAsync(_settings);
            if (!string.IsNullOrWhiteSpace(_settings.CurrentProcessId))
                await _processService.SaveDraftAsync(_settings, _service.GetSpedAttachments(_settings));
            StatusText.Text = readByOcr
                ? $"Documento digitalizado lido por OCR e anexado ao SPED: {reference}"
                : $"Documento lido e anexado ao SPED: {reference}";
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, authorizingDocument ? "Documento autorizador" : "BI do deslocamento", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = attachmentSaved
                ? $"Arquivo salvo e anexado, mas a referência deve ser preenchida manualmente: {newFile}"
                : "Não foi possível salvar e vincular o documento obrigatório.";
        }
    }

    private static string ParseAuthorizingDiexReference(string text)
    {
        var sourceText = text ?? string.Empty;
        var numberMatch = Regex.Match(sourceText, @"(?im)^\s*D\s*I\s*E\s*X\s*(?:n\s*[º°o]?|nr\.?|número)?\s*[:\-]?\s*(?<number>[^\r\n]+)");
        if (!numberMatch.Success)
            numberMatch = Regex.Match(sourceText, @"(?im)\bD\s*I\s*E\s*X\s*(?:n\s*[º°o]?|nr\.?|número)?\s*[:\-]?\s*(?<number>[^\r\n]+)");
        if (!numberMatch.Success) return string.Empty;
        var number = Regex.Split(numberMatch.Groups["number"].Value.Trim(), @"(?:,\s*)?\bde\s+(?=\d{1,2}\s+de\s+)", RegexOptions.IgnoreCase)[0]
            .Trim().TrimEnd('.', ',', ';');
        var date = FindDocumentDate(sourceText);
        return date is null || string.IsNullOrWhiteSpace(number)
            ? string.Empty
            : $"DIEx nº {number}, de {FormatMilitaryDate(date.Value)}";
    }

    private static string ParseBulletinReference(string text, string organization)
    {
        var sourceText = text ?? string.Empty;
        var match = Regex.Match(sourceText, @"(?im)^\s*BOLETIM\s+DE\s+ACESSO\s+RESTRITO\s+N[º°o]?\s*(?<number>\d+)(?:\s*/\s*\d{4})?");
        var bulletinType = "BAR";
        if (!match.Success)
            match = Regex.Match(sourceText, @"(?im)\bB\s*A\s*R\s+(?:N[º°o]|Nr\.?)\s*(?<number>\d+)(?:\s*/\s*\d{4})?");
        if (!match.Success)
        {
            bulletinType = "BI";
            match = Regex.Match(sourceText, @"(?im)^\s*BOLETIM\s+INTERNO\s+N[º°o]?\s*(?<number>\d+)(?:\s*/\s*\d{4})?");
        }
        if (!match.Success)
            match = Regex.Match(sourceText, @"(?im)\bB\s*I\s+(?:N[º°o]|Nr\.?)\s*(?<number>\d+)(?:\s*/\s*\d{4})?");
        var date = FindDocumentDate(sourceText);
        if (!match.Success || date is null) return string.Empty;
        var om = ExtractBulletinOrganization(sourceText);
        if (string.IsNullOrWhiteSpace(om))
            om = string.IsNullOrWhiteSpace(organization) ? OrganizationIdentity.Name : organization.Trim();
        return $"{bulletinType} Nr {int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture)}, de {FormatMilitaryDate(date.Value)}, da {om}";
    }

    private static string ExtractBulletinOrganization(string text)
    {
        var continuation = Regex.Match(
            text ?? string.Empty,
            @"(?im)^\s*\(\s*Continua(?:ção|cao)\s+do\s+(?:B\s*A\s*R|B\s*I)\s+(?:N[º°o]|Nr\.?)\s*\d+(?:\s*/\s*\d{4})?.*?\bdo\s*\(\s*a\s*\)\s*(?<om>[^\r\n\)]+)");
        if (!continuation.Success) return string.Empty;
        return Regex.Replace(continuation.Groups["om"].Value, @"\s+", " ").Trim().TrimEnd('.', ',', ';');
    }

    private static DateTime? FindDocumentDate(string text)
    {
        var monthNames = "janeiro|fevereiro|março|marco|abril|maio|junho|julho|agosto|setembro|outubro|novembro|dezembro";
        var wordDate = Regex.Match(text ?? string.Empty, $@"(?i)\b(?<day>\d{{1,2}})\s+de\s+(?<month>{monthNames})\s+de\s+(?<year>20\d{{2}})\b");
        if (wordDate.Success)
        {
            var normalizedMonth = RemoveAccents(wordDate.Groups["month"].Value.ToLowerInvariant());
            var names = new[] { "janeiro", "fevereiro", "marco", "abril", "maio", "junho", "julho", "agosto", "setembro", "outubro", "novembro", "dezembro" };
            var month = Array.IndexOf(names, normalizedMonth) + 1;
            if (month > 0 && DateTime.TryParseExact($"{wordDate.Groups["day"].Value}/{month}/{wordDate.Groups["year"].Value}", "d/M/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return parsed;
        }
        var numericDate = Regex.Match(text ?? string.Empty, @"\b(?<day>\d{2})/(?<month>\d{2})/(?<year>20\d{2})\b");
        return numericDate.Success && DateTime.TryParseExact(numericDate.Value, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var numeric)
            ? numeric
            : null;
    }

    private static string RemoveAccents(string value)
        => new(value.Normalize(System.Text.NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());

    private static string FormatMilitaryDate(DateTime date)
    {
        string[] months = ["JAN", "FEV", "MAR", "ABR", "MAI", "JUN", "JUL", "AGO", "SET", "OUT", "NOV", "DEZ"];
        return $"{date:dd} {months[date.Month - 1]} {date:yy}";
    }

    private void RefreshMandatoryDocumentStatus()
    {
        var available = _spedAttachments.Select(x => x.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var authOk = !string.IsNullOrWhiteSpace(_settings.RequestAuthorizingAttachmentFile) && available.Contains(_settings.RequestAuthorizingAttachmentFile);
        var bulletinOk = !string.IsNullOrWhiteSpace(_settings.RequestBulletinAttachmentFile) && available.Contains(_settings.RequestBulletinAttachmentFile);
        AuthorizingAttachmentStatusText.Text = authOk ? $"Anexado: {_settings.RequestAuthorizingAttachmentFile}" : "Nenhum PDF anexado";
        BulletinAttachmentStatusText.Text = bulletinOk ? $"Anexado: {_settings.RequestBulletinAttachmentFile}" : "Nenhum PDF anexado";
        AuthorizingAttachmentStatusText.Foreground = (System.Windows.Media.Brush)FindResource(authOk ? "SuccessBrush" : "DangerBrush");
        BulletinAttachmentStatusText.Foreground = (System.Windows.Media.Brush)FindResource(bulletinOk ? "SuccessBrush" : "DangerBrush");
    }

    private bool ValidateMandatoryRequestDocuments()
    {
        var available = _service.GetSpedAttachments(_settings).Select(x => x.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(_settings.RequestAuthorizingAttachmentFile) || !available.Contains(_settings.RequestAuthorizingAttachmentFile))
        {
            SigfurDialog.Show(this, "Anexe o PDF do documento que autorizou a missão antes de continuar.", "Documento obrigatório", MessageBoxButton.OK, MessageBoxImage.Warning);
            RequestAuthorizingDocumentBox.Focus();
            return false;
        }
        if (string.IsNullOrWhiteSpace(RequestAuthorizingDocumentBox.Text))
        {
            SigfurDialog.Show(this, "A referência do documento autorizador não foi identificada.", "Documento obrigatório", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (string.IsNullOrWhiteSpace(_settings.RequestBulletinAttachmentFile) || !available.Contains(_settings.RequestBulletinAttachmentFile))
        {
            SigfurDialog.Show(this, "Anexe o PDF do BI ou BAR que publicou o deslocamento antes de continuar.", "BI/BAR obrigatório", MessageBoxButton.OK, MessageBoxImage.Warning);
            RequestBulletinBox.Focus();
            return false;
        }
        if (string.IsNullOrWhiteSpace(RequestBulletinBox.Text))
        {
            SigfurDialog.Show(this, "A referência do BI/BAR não foi identificada.", "BI/BAR obrigatório", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private async void AddSpedAttachments_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Selecionar anexos para o DIEx no SPED",
            Filter = "Todos os arquivos|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _service.ImportSpedAttachments(_settings, dialog.FileNames);
            RefreshSpedAttachments();
            await _service.SaveSettingsAsync(_settings);
            StatusText.Text = $"{_spedAttachments.Count} anexo(s) salvo(s) para envio ao SPED.";
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "Anexos do SPED", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void AddLibraryBulletinAttachment_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SavedBulletinPickerWindow(attachmentMode: true) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedReference is not { } selected) return;
        if (string.IsNullOrWhiteSpace(selected.Path) || !File.Exists(selected.Path))
        {
            SigfurDialog.Show(this, "O PDF desta publicação não foi encontrado na biblioteca.",
                "Anexo da Grat Rep", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _service.ImportSpedAttachments(_settings, [selected.Path]);
            RefreshSpedAttachments();
            await _service.SaveSettingsAsync(_settings);
            if (!string.IsNullOrWhiteSpace(_settings.CurrentProcessId))
                await _processService.SaveDraftAsync(_settings, _service.GetSpedAttachments(_settings));
            StatusText.Text = $"{selected.Kind} {selected.Number} adicionado diretamente da biblioteca aos anexos da Grat Rep.";
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "Anexo da Grat Rep", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void RemoveSpedAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GratificationSpedAttachment attachment }) return;
        try
        {
            _service.RemoveSpedAttachment(_settings, attachment.FileName);
            if (string.Equals(_settings.RequestAuthorizingAttachmentFile, attachment.FileName, StringComparison.OrdinalIgnoreCase))
                _settings.RequestAuthorizingAttachmentFile = string.Empty;
            if (string.Equals(_settings.RequestBulletinAttachmentFile, attachment.FileName, StringComparison.OrdinalIgnoreCase))
                _settings.RequestBulletinAttachmentFile = string.Empty;
            RefreshSpedAttachments();
            await _service.SaveSettingsAsync(_settings);
            StatusText.Text = $"Anexo removido: {attachment.FileName}";
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "Remover anexo", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenSpedAttachmentsFolder_Click(object sender, RoutedEventArgs e)
        => ShellService.OpenPath(_service.SpedAttachmentsDirectory);

    private void SpedAttachmentList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SpedAttachmentList.SelectedItem is GratificationSpedAttachment attachment && File.Exists(attachment.FullPath))
            ShellService.OpenPath(attachment.FullPath);
    }

    private void RefreshSpedAttachments()
    {
        var files = _service.GetSpedAttachments(_settings);
        _settings.RequestAttachmentFiles = files.Select(x => x.FileName).ToList();
        _spedAttachments.Clear();
        foreach (var file in files) _spedAttachments.Add(file);
        var total = files.Sum(x => x.SizeBytes);
        SpedAttachmentSummaryText.Text = files.Count == 0
            ? "Nenhum anexo selecionado — limite máximo total: 10 MB."
            : $"{files.Count} arquivo(s) • {total / 1_000_000d:0.00} MB de 10 MB";
        RefreshMandatoryDocumentStatus();
    }

    private async void OpenSpedMapping_Click(object sender, RoutedEventArgs e)
    {
        ReadControlsIntoSettings();
        if (!ValidateRequest()) return;
        try
        {
            var documentData = await BuildMissionDocumentDataAsync();
            var attachments = _service.GetSpedAttachments(_settings);
            var process = await _processService.SaveDraftAsync(_settings, attachments);
            await _service.SaveSettingsAsync(_settings);
            var draft = _service.BuildSpedDiexDraft(_settings, documentData.Period, documentData.Rows);
            var reviewDirectory = _processService.GetReviewDirectory(process.Id);
            var presets = new SpedMappingSettings
            {
                SenderSearch = _settings.SpedSender,
                ExternalRecipientSearch = _settings.SpedRecipient,
                ClassificationSearch = _settings.SpedClassification,
                DocumentPurpose = _settings.SpedDocumentPurpose,
                Subject = _settings.SpedSubject
            };
            var window = new SpedMappingWindow(new SpedMappingService(App.Paths), draft, reviewDirectory, presets) { Owner = this };
            window.ShowDialog();
            if (window.AutomationResult is not null)
                await _processService.CompleteAsync(process.Id, window.AutomationResult);
            else if (!string.IsNullOrWhiteSpace(window.LastError))
                await _processService.CompleteAsync(process.Id, new SpedAutomationResult
                {
                    Success = false,
                    Message = window.LastError,
                    ScreenshotPaths = Directory.GetFiles(reviewDirectory, "*.png").ToList()
                });
            await RefreshCurrentProcessLabelAsync();
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "Automação do SPED", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void GenerateDiex_Click(object sender, RoutedEventArgs e)
    {
        ReadControlsIntoSettings();
        if (!ValidateRequest()) return;
        var dialog = new SaveFileDialog { Title = "Gerar DIEx da Gratificação", Filter = "Documento Word|*.docx", InitialDirectory = _service.DefaultOutputDirectory, FileName = $"DIEx_Grat_Rep_{DateTime.Now:yyyyMMdd}.docx" };
        if (dialog.ShowDialog(this) != true) return;
        try { var data = await BuildMissionDocumentDataAsync(); await _service.GenerateDiexAsync(dialog.FileName, _settings, data.Period, data.Rows); ShellService.OpenPath(dialog.FileName); StatusText.Text = $"DIEx gerado: {dialog.FileName}"; }
        catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "Gerar DIEx", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void GenerateMap_Click(object sender, RoutedEventArgs e)
    {
        ReadControlsIntoSettings();
        if (!ValidateRequest()) return;
        var dialog = new SaveFileDialog { Title = "Gerar Mapa da Gratificação", Filter = "Documento Word|*.docx", InitialDirectory = _service.DefaultOutputDirectory, FileName = $"Mapa_Grat_Rep_{DateTime.Now:yyyyMMdd}.docx" };
        if (dialog.ShowDialog(this) != true) return;
        try { var data = await BuildMissionDocumentDataAsync(); await _service.GenerateMapAsync(dialog.FileName, _settings, data.Period, data.Rows); ShellService.OpenPath(dialog.FileName); StatusText.Text = $"Mapa gerado: {dialog.FileName}"; }
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
            var data = await BuildMissionDocumentDataAsync();
            await _service.GenerateDiexAsync(diex, _settings, data.Period, data.Rows);
            await _service.GenerateMapAsync(map, _settings, data.Period, data.Rows);
            ShellService.OpenPath(directory);
            StatusText.Text = $"DIEx e Mapa gerados em {directory}.";
        }
        catch (Exception ex) { SigfurDialog.Show(this, ex.Message, "Gerar documentos", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async Task<MissionDocumentData> BuildMissionDocumentDataAsync()
    {
        SaveEffectiveQuantities();
        CaptureActiveMissionLotFields();
        var periods = _missionLots.Select(lot =>
        {
            if (!lot.TryGetPeriod(out var start, out var end) || end <= start)
                throw new InvalidOperationException($"Período inválido na {lot.Name}.");
            return new { Lot = lot, Start = start, End = end };
        }).ToList();
        var overallStart = periods.Min(item => item.Start);
        var overallEnd = periods.Max(item => item.End);
        _settings.RequestStartDate = overallStart.Date;
        _settings.RequestStartTime = overallStart.ToString("HH:mm", CultureInfo.InvariantCulture);
        _settings.RequestEndDate = overallEnd.Date;
        _settings.RequestEndTime = overallEnd.ToString("HH:mm", CultureInfo.InvariantCulture);
        _settings.RequestMissionLots = _missionLots.ToList();
        var overallPeriod = _service.CalculatePeriod(overallStart, overallEnd);
        overallPeriod.IndemnifiableDays = periods.Sum(item => item.Lot.IndemnifiableDays);
        var rows = await _service.BuildMissionEffectiveRowsAsync(_settings);
        return new MissionDocumentData(overallPeriod, rows);
    }

    private bool ValidateRequest()
    {
        SaveEffectiveQuantities();
        CaptureActiveMissionLotFields();
        foreach (var lot in _missionLots)
        {
            if (!lot.TryGetPeriod(out var start, out var end) || end <= start)
            {
                RequestMissionLotsList.SelectedItem = lot;
                SigfurDialog.Show(this, $"Confira as datas e os horários da {lot.Name}.", "Período da leva", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (!ValidateCurrentExerciseYear(start, end, lot.Name))
            {
                RequestMissionLotsList.SelectedItem = lot;
                return false;
            }
            if (lot.IndemnifiableDays <= 0)
            {
                RequestMissionLotsList.SelectedItem = lot;
                SigfurDialog.Show(this, $"O período da {lot.Name} não gera dia indenizável.", "Dias indenizáveis", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (lot.EffectiveTotal <= 0)
            {
                RequestMissionLotsList.SelectedItem = lot;
                SigfurDialog.Show(this, $"Informe o efetivo da {lot.Name} por posto/graduação.", "Efetivo da leva", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
        }
        if (!ValidateMandatoryRequestDocuments()) return false;
        if (string.IsNullOrWhiteSpace(_settings.RequestNature))
        {
            SigfurDialog.Show(this, "Selecione ou informe a natureza da atividade.", "Natureza obrigatória", MessageBoxButton.OK, MessageBoxImage.Information);
            RequestNatureBox.Focus();
            return false;
        }
        if (string.IsNullOrWhiteSpace(_settings.RequestDescription))
        {
            SigfurDialog.Show(this, "Informe uma descrição objetiva da atividade executada.", "Descrição obrigatória", MessageBoxButton.OK, MessageBoxImage.Information);
            RequestDescriptionBox.Focus();
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

    private bool ValidateMissionLotExerciseYears()
    {
        SaveEffectiveQuantities();
        CaptureActiveMissionLotFields();
        foreach (var lot in _missionLots)
        {
            if (!lot.TryGetPeriod(out var start, out var end)) continue;
            if (ValidateCurrentExerciseYear(start, end, lot.Name)) continue;
            RequestMissionLotsList.SelectedItem = lot;
            return false;
        }
        return true;
    }

    private bool ValidateCurrentExerciseYear(DateTime start, DateTime end, string source)
    {
        var currentYear = DateTime.Today.Year;
        var informedYears = new[] { start.Year, end.Year }.Distinct().OrderBy(year => year).ToList();
        if (informedYears.Count == 1 && informedYears[0] == currentYear) return true;
        return ConfirmNonCurrentExerciseYears(informedYears, source);
    }

    private bool ConfirmNonCurrentExerciseYears(IEnumerable<int> informedYears, string source)
    {
        var currentYear = DateTime.Today.Year;
        var years = informedYears.Distinct().OrderBy(year => year).ToList();
        var nonCurrentYears = years.Where(year => year != currentYear).ToList();
        if (nonCurrentYears.Count == 0 || nonCurrentYears.All(_confirmedNonCurrentExerciseYears.Contains)) return true;

        var informed = years.Count == 0 ? "diferente" : string.Join(" e ", years);
        var result = SigfurDialog.Show(this,
            $"CONFIRMAÇÃO DE PERÍODO FORA DO EXERCÍCIO ATUAL\n\nO {source} contém o ano {informed}, enquanto o exercício atual é {currentYear}.\n\nEm regra, direitos ou despesas de anos anteriores devem ser tratados no módulo Exercício Anterior.\n\nSe os dados já foram conferidos e você precisa continuar excepcionalmente nesta tela, escolha Sim. O SIGFUR liberará a geração do mapa, do DIEx e dos demais documentos para o ano informado.\n\nEscolha Não para voltar e revisar o período.\n\nDeseja continuar com o ano {informed}?",
            $"Confirmar ano {informed}",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            StatusText.Text = $"Continuação não confirmada para {informed}. Revise o período antes de gerar os documentos.";
            return false;
        }

        foreach (var year in nonCurrentYears) _confirmedNonCurrentExerciseYears.Add(year);
        StatusText.Text = $"Ano {informed} confirmado. A geração dos documentos foi liberada nesta tela.";
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
            OrganizationIdentity.Apply(profile);
            StatusText.Text = "Comandante e Organização Militar salvos no perfil institucional.";
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, ex.Message, "Salvar perfil institucional", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OpenSalaries_Click(object sender, RoutedEventArgs e)
    {
        new SalaryWindow(App.Salaries) { Owner = this }.ShowDialog();
        _service.ClearSalaryCache();
        await RecalculateMainAsync(showStatus: true);
        await ReloadEffectiveRowsAsync();
    }

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
        RememberActivityOptions();
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
        _settings.RequestSalaryReferenceYear = SalaryReferenceYearBox.SelectedValue is int salaryYear ? salaryYear : 0;
        _settings.SpedSender = GratSpedSenderBox.Text.Trim();
        _settings.SpedRecipient = GratSpedRecipientBox.Text.Trim();
        _settings.SpedClassification = GratSpedClassificationBox.Text.Trim();
        _settings.SpedDocumentPurpose = GratSpedPurposeBox.Text.Trim();
        _settings.SpedSubject = GratSpedSubjectBox.Text.Trim();
        _settings.RequestManualDays = _requestDaysAdjusted ? Math.Max(0, _requestDaysOverride) : 0;
        _requestPeriod = ReadRequestPeriod();
        foreach (var row in _effective) row.Days = _requestPeriod.IsValid ? _requestPeriod.IndemnifiableDays : 0;
        SaveEffectiveQuantities();
        CaptureActiveMissionLotFields();
        _settings.RequestMissionLots = _missionLots.ToList();
    }

    private void ReadMainTextFieldsIntoSettings()
    {
        _settings.Destination = DestinationBox.Text.Trim();
        _settings.Purpose = PurposeBox.Text.Trim();
        _settings.BulletinReference = BulletinBox.Text.Trim();
        _settings.SisbolSubject = SisbolSubjectBox.Text.Trim();
        _settings.SisbolSpecificCode = SisbolCodeBox.Text.Trim();
        _settings.Search = AvailableSearchBox.Text;
    }

    private static bool IsMainTextOnlyField(TextBox box)
        => box.Name is nameof(DestinationBox) or nameof(PurposeBox) or nameof(BulletinBox) or nameof(SisbolSubjectBox) or nameof(SisbolCodeBox);

    private async Task DebounceMainRecalculateAsync()
    {
        _mainRecalculateCts?.Cancel();
        _mainRecalculateCts?.Dispose();
        var cts = new CancellationTokenSource();
        _mainRecalculateCts = cts;
        try
        {
            await Task.Delay(400, cts.Token);
            await RecalculateMainAsync(showStatus: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha ao recalcular Gratificação de Representação.", ex);
            StatusText.Text = "Não foi possível atualizar a gratificação.";
        }
    }

    private async void DebounceAvailableSearch()
    {
        _availableSearchCts?.Cancel();
        _availableSearchCts?.Dispose();
        var cts = new CancellationTokenSource();
        _availableSearchCts = cts;
        try
        {
            await Task.Delay(350, cts.Token);
            if (cts.IsCancellationRequested) return;
            _settings.Search = AvailableSearchBox.Text;
            _availableView?.Refresh();
            StatusText.Text = string.IsNullOrWhiteSpace(AvailableSearchBox.Text)
                ? "Filtro limpo."
                : "Filtro de militares atualizado.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha ao filtrar militares na Gratificação de Representação.", ex);
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        try
        {
            _mainRecalculateCts?.Cancel();
            _availableSearchCts?.Cancel();
            ReadControlsIntoSettings();
            await _service.SaveSettingsAsync(_settings);
        }
        catch (Exception ex) { await App.Log.WriteAsync("Falha ao salvar preferências da Gratificação.", ex); }
        finally
        {
            _mainRecalculateCts?.Dispose();
            _availableSearchCts?.Dispose();
        }
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { AvailableSearchBox.Focus(); AvailableSearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { AddAll_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.G && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { GenerateBulletin_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Delete) { RemoveSelected_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.F5) { await RecalculateMainAsync(showStatus: true); await ReloadEffectiveRowsAsync(); e.Handled = true; }
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

    private sealed record SalaryReferenceYearOption(int Year, string Label);
    private sealed record MissionDocumentData(GratificationPeriodInfo Period, List<GratificationEffectiveRow> Rows);
}
