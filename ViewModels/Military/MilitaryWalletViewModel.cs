using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.ViewModels.Military;

public sealed class MilitaryWalletViewModel : ObservableObject
{
    private readonly MilitaryRepository _repository;
    private readonly PaystubService _paystubs;
    private MilitaryRecord _military;
    private TransportSummary _transport = new();
    private bool _isBusy;
    private string _statusText = "Carregando carteira…";
    private MilitaryDocumentRecord? _selectedDocument;
    private ServiceIntervalRecord? _selectedInterval;
    private PaystubFileRecord? _selectedPaystub;
    private PaystubFileRecord? _selectedFinancialStatement;
    private WalletMentionRecord? _selectedMention;
    private readonly List<WalletMentionRecord> _allMentions = [];
    private string _selectedMentionYear = "Todos";
    private string _selectedMentionMonth = "Todos";
    private string _mentionSearchText = string.Empty;
    private string _selectedPaystubYear = "Todos";
    private string _selectedFinancialStatementYear = "Todos";
    private readonly List<PaystubFileRecord> _allPaystubs = [];
    private readonly List<PaystubFileRecord> _allFinancialStatements = [];

    public MilitaryWalletViewModel(MilitaryRepository repository, PaystubService paystubs, MilitaryRecord military)
    {
        _repository = repository;
        _paystubs = paystubs;
        _military = military;
    }

    public MilitaryRecord Military { get => _military; private set => SetProperty(ref _military, value); }
    public ObservableCollection<MilitaryDocumentRecord> Documents { get; } = [];
    public ObservableCollection<ServiceIntervalRecord> ServiceIntervals { get; } = [];
    public ObservableCollection<PaystubFileRecord> Paystubs { get; } = [];
    public ObservableCollection<PaystubFileRecord> FinancialStatements { get; } = [];
    public ObservableCollection<string> PaystubYearOptions { get; } = ["Todos"];
    public ObservableCollection<string> FinancialStatementYearOptions { get; } = ["Todos"];
    public ObservableCollection<VacationPaymentRow> VacationPayments { get; } = [];
    public ObservableCollection<WalletMentionRecord> InternalBulletinMentions { get; } = [];
    public ObservableCollection<WalletMentionRecord> FurrielAddendumMentions { get; } = [];
    // Mantida por compatibilidade com versões anteriores do XAML.
    public ObservableCollection<WalletMentionRecord> FurrielMentions { get; } = [];
    public ObservableCollection<string> MentionYearOptions { get; } = ["Todos"];
    public ObservableCollection<string> MentionMonthOptions { get; } = ["Todos", "Janeiro", "Fevereiro", "Março", "Abril", "Maio", "Junho", "Julho", "Agosto", "Setembro", "Outubro", "Novembro", "Dezembro"];
    public TransportSummary Transport { get => _transport; private set => SetProperty(ref _transport, value); }
    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public MilitaryDocumentRecord? SelectedDocument { get => _selectedDocument; set => SetProperty(ref _selectedDocument, value); }
    public ServiceIntervalRecord? SelectedInterval { get => _selectedInterval; set => SetProperty(ref _selectedInterval, value); }
    public PaystubFileRecord? SelectedPaystub { get => _selectedPaystub; set => SetProperty(ref _selectedPaystub, value); }
    public PaystubFileRecord? SelectedFinancialStatement { get => _selectedFinancialStatement; set => SetProperty(ref _selectedFinancialStatement, value); }
    public WalletMentionRecord? SelectedMention { get => _selectedMention; set => SetProperty(ref _selectedMention, value); }
    public string SelectedMentionYear { get => _selectedMentionYear; set { if (SetProperty(ref _selectedMentionYear, value)) ApplyMentionFilter(); } }
    public string SelectedMentionMonth { get => _selectedMentionMonth; set { if (SetProperty(ref _selectedMentionMonth, value)) ApplyMentionFilter(); } }
    public string MentionSearchText { get => _mentionSearchText; set { if (SetProperty(ref _mentionSearchText, value)) ApplyMentionFilter(); } }
    public string SelectedPaystubYear { get => _selectedPaystubYear; set { if (SetProperty(ref _selectedPaystubYear, value)) ApplyPaystubFilter(); } }
    public string SelectedFinancialStatementYear { get => _selectedFinancialStatementYear; set { if (SetProperty(ref _selectedFinancialStatementYear, value)) ApplyFinancialStatementFilter(); } }
    public int TotalServiceDays => CalculateEffectiveServiceDays();
    public string TotalServiceText
    {
        get
        {
            if (ServiceIntervals.Count == 0)
            {
                var date = Military.EnlistmentDateValue;
                if (date is null) return "Data de praça não informada — cadastre a Data de Praça para calcular automaticamente.";
                return MilitaryFormatting.FormatServiceTime(date, DateTime.Today) + " — calculado pela Data de Praça do cadastro";
            }

            var days = TotalServiceDays;
            var years = days / 365;
            var months = (days % 365) / 30;
            var rest = (days % 365) % 30;
            return $"{years}a, {months:00}m e {rest:00}d ({days:N0} dias) — intervalos salvos, sem contar sobreposições";
        }
    }

    private int CalculateEffectiveServiceDays()
    {
        var ranges = ServiceIntervals
            .Select(x => (Start: MilitaryFormatting.ParseDate(x.StartDate), End: MilitaryFormatting.ParseDate(x.EndDate) ?? DateTime.Today))
            .Where(x => x.Start is not null && x.End.Date >= x.Start.Value.Date)
            .Select(x => (Start: x.Start!.Value.Date, End: x.End.Date))
            .OrderBy(x => x.Start)
            .ToList();

        if (ranges.Count == 0)
            return Military.ServiceTimeDays;

        var merged = new List<(DateTime Start, DateTime End)>();
        foreach (var range in ranges)
        {
            if (merged.Count == 0 || range.Start > merged[^1].End.AddDays(1))
            {
                merged.Add(range);
                continue;
            }
            var last = merged[^1];
            if (range.End > last.End) merged[^1] = (last.Start, range.End);
        }
        return merged.Sum(x => Math.Max(0, (x.End - x.Start).Days + 1));
    }

    public async Task LoadAsync(bool includePaystubs = true, bool includeMentions = true)
    {
        IsBusy = true;
        StatusText = "Atualizando carteira do militar…";
        try
        {
            Military = await _repository.GetByIdAsync(Military.Id) ?? Military;
            Documents.Clear();
            foreach (var item in await _repository.GetDocumentsAsync(Military.Id)) Documents.Add(item);
            Transport = await _repository.GetTransportSummaryAsync(Military);
            ServiceIntervals.Clear();
            foreach (var item in await _repository.GetServiceIntervalsAsync(Military.Id)) ServiceIntervals.Add(item);
            OnPropertyChanged(nameof(TotalServiceDays));
            OnPropertyChanged(nameof(TotalServiceText));
            await LoadVacationPaymentsAsync();
            if (includeMentions) await LoadFurrielMentionsAsync();
            if (includePaystubs)
            {
                await LoadPaystubsAsync();
                await LoadFinancialStatementsAsync();
            }
            StatusText = includeMentions || includePaystubs
                ? "Carteira e abas solicitadas atualizadas."
                : "Carteira pronta. Abas com arquivos serão carregadas ao abrir.";
        }
        finally { IsBusy = false; }
    }

    public async Task LoadPaystubsAsync()
    {
        var alreadyBusy = IsBusy;
        if (!alreadyBusy) IsBusy = true;
        StatusText = "Localizando contracheques salvos sem bloquear a interface…";
        try
        {
            _allPaystubs.Clear();
            _allPaystubs.AddRange(await _paystubs.FindForMilitaryAsync(Military));
            RefreshYearOptions(PaystubYearOptions, _allPaystubs, ref _selectedPaystubYear, nameof(SelectedPaystubYear));
            ApplyPaystubFilter();
            StatusText = $"{Paystubs.Count} contracheque(s) localizado(s).";
        }
        finally
        {
            if (!alreadyBusy) IsBusy = false;
        }
    }

    public async Task LoadFinancialStatementsAsync()
    {
        var alreadyBusy = IsBusy;
        if (!alreadyBusy) IsBusy = true;
        StatusText = "Localizando fichas financeiras separadamente…";
        try
        {
            _allFinancialStatements.Clear();
            _allFinancialStatements.AddRange(await _paystubs.FindFinancialStatementsForMilitaryAsync(Military));
            RefreshYearOptions(FinancialStatementYearOptions, _allFinancialStatements, ref _selectedFinancialStatementYear, nameof(SelectedFinancialStatementYear));
            ApplyFinancialStatementFilter();
            StatusText = $"{FinancialStatements.Count} ficha(s) financeira(s) localizada(s).";
        }
        finally
        {
            if (!alreadyBusy) IsBusy = false;
        }
    }

    private void ApplyPaystubFilter()
    {
        Paystubs.Clear();
        var year = int.TryParse(SelectedPaystubYear, out var y) ? y : 0;
        foreach (var item in OrderDocumentsByReference(_allPaystubs.Where(x => year == 0 || ExtractYear(x) == year), year))
            Paystubs.Add(item);
    }

    private void ApplyFinancialStatementFilter()
    {
        FinancialStatements.Clear();
        var year = int.TryParse(SelectedFinancialStatementYear, out var y) ? y : 0;
        foreach (var item in OrderDocumentsByReference(_allFinancialStatements.Where(x => year == 0 || ExtractYear(x) == year), year))
            FinancialStatements.Add(item);
    }

    private void RefreshYearOptions(ObservableCollection<string> target, IEnumerable<PaystubFileRecord> files, ref string selected, string propertyName)
    {
        var current = selected;
        target.Clear();
        target.Add("Todos");
        foreach (var year in files.Select(ExtractYear).Where(x => x > 0).Distinct().OrderByDescending(x => x))
            target.Add(year.ToString());
        if (!target.Contains(current)) current = "Todos";
        selected = current;
        OnPropertyChanged(propertyName);
    }

    private static IEnumerable<PaystubFileRecord> OrderDocumentsByReference(IEnumerable<PaystubFileRecord> files, int selectedYear)
    {
        var decorated = files
            .Select(item => (Item: item, Reference: ExtractMonthYear(item)))
            .ToList();

        if (selectedYear > 0)
        {
            return decorated
                .OrderBy(x => x.Reference.Month <= 0 ? 99 : x.Reference.Month)
                .ThenByDescending(x => x.Item.ModifiedAt)
                .Select(x => x.Item);
        }

        return decorated
            .OrderByDescending(x => x.Reference.Year)
            .ThenBy(x => x.Reference.Month <= 0 ? 99 : x.Reference.Month)
            .ThenByDescending(x => x.Item.ModifiedAt)
            .Select(x => x.Item);
    }

    private static (int Month, int Year) ExtractMonthYear(PaystubFileRecord item)
    {
        var text = NormalizeReferenceText(string.Join(" ", item.Reference, item.FileName, item.Path));

        foreach (Match match in Regex.Matches(text, @"(?<!\d)(0?[1-9]|1[0-2])[-_/\. ]+(20\d{2})(?!\d)"))
        {
            if (int.TryParse(match.Groups[1].Value, out var month) && int.TryParse(match.Groups[2].Value, out var year))
                return (MonthNumber(month), NormalizeYear(year, item.ModifiedAt.Year));
        }

        foreach (Match match in Regex.Matches(text, @"(?<!\d)(20\d{2})[-_/\. ]+(0?[1-9]|1[0-2])(?!\d)"))
        {
            if (int.TryParse(match.Groups[1].Value, out var year) && int.TryParse(match.Groups[2].Value, out var month))
                return (MonthNumber(month), NormalizeYear(year, item.ModifiedAt.Year));
        }

        var monthFromName = 0;
        var monthNames = new[]
        {
            ("janeiro", 1), ("jan", 1),
            ("fevereiro", 2), ("fev", 2),
            ("marco", 3), ("mar", 3),
            ("abril", 4), ("abr", 4),
            ("maio", 5), ("mai", 5),
            ("junho", 6), ("jun", 6),
            ("julho", 7), ("jul", 7),
            ("agosto", 8), ("ago", 8),
            ("setembro", 9), ("set", 9),
            ("outubro", 10), ("out", 10),
            ("novembro", 11), ("nov", 11),
            ("dezembro", 12), ("dez", 12)
        };
        foreach (var (token, number) in monthNames)
        {
            if (Regex.IsMatch(text, $@"(^|[^a-z]){Regex.Escape(token)}([^a-z]|$)"))
            {
                monthFromName = number;
                break;
            }
        }

        var yearMatch = Regex.Match(text, @"(?<!\d)(20\d{2})(?!\d)");
        var yearValue = yearMatch.Success && int.TryParse(yearMatch.Value, out var parsedYear)
            ? NormalizeYear(parsedYear, item.ModifiedAt.Year)
            : item.ModifiedAt.Year;

        return (monthFromName, yearValue);
    }

    private static string NormalizeReferenceText(string value)
    {
        var text = (value ?? string.Empty).Normalize(System.Text.NormalizationForm.FormD);
        var clean = new string(text.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
        return clean.ToLowerInvariant();
    }

    private static int NormalizeYear(int year, int fallback)
        => year is >= 2000 and <= 2200 ? year : fallback;

    private static int MonthNumber(int month)
        => month is >= 1 and <= 12 ? month : 0;

    private static int ExtractYear(PaystubFileRecord item)
    {
        var text = string.Join(" ", item.Reference, item.FileName, item.Path);
        var match = System.Text.RegularExpressions.Regex.Match(text, @"(?<!\d)(20\d{2})(?!\d)");
        return match.Success ? int.Parse(match.Value) : item.ModifiedAt.Year;
    }

    public async Task LoadVacationPaymentsAsync()
    {
        VacationPayments.Clear();
        foreach (var year in new[] { DateTime.Today.Year, DateTime.Today.Year - 1 })
        {
            var periods = (await App.Vacations.GetPeriodsAsync(year)).ToDictionary(x => x.Id);
            foreach (var allocation in await App.Vacations.GetAllocationsForMilitaryAsync(year, Military))
            {
                periods.TryGetValue(allocation.PeriodId, out var period);
                VacationPayments.Add(new VacationPaymentRow
                {
                    Year = year, Period = period?.FullLabel ?? $"Período #{allocation.PeriodId}", Days = allocation.Days,
                    IsPaid = allocation.IsPaid, PaidAt = allocation.PaidAt
                });
            }
        }
    }

    public async Task LoadFurrielMentionsAsync()
    {
        _allMentions.Clear();
        FurrielMentions.Clear();
        InternalBulletinMentions.Clear();
        FurrielAddendumMentions.Clear();
        try
        {
            var fullName = NormalizeName(Military.Name);

            await LoadWalletInternalIndexMentionsAsync();
            await LoadWalletFurrielAddendumMentionsAsync(fullName);

            var years = _allMentions.Select(x => x.Year).Where(x => x > 0).Distinct().OrderByDescending(x => x).Select(x => x.ToString()).ToList();
            MentionYearOptions.Clear();
            MentionYearOptions.Add("Todos");
            foreach (var year in years) MentionYearOptions.Add(year);
            if (!MentionYearOptions.Contains(SelectedMentionYear)) SelectedMentionYear = "Todos";
            ApplyMentionFilter();
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha ao carregar menções dos boletins na carteira.", ex);
        }
    }

    private async Task LoadWalletInternalIndexMentionsAsync()
    {
        var intelligentStore = await App.IntelligentBulletins.LoadAsync();
        var sisbolIndexRows = await App.SisbolPersonIndex.FindForMilitaryAsync(Military);
        foreach (var row in sisbolIndexRows)
        {
            var bulletinPath = FindBulletinPdfPath(intelligentStore, row);
            AddMention(new WalletMentionRecord
            {
                Source = "Boletim Interno — Índice SisBol",
                Origin = "BI",
                Category = CleanCell(row.NoteNumber),
                Description = BuildWalletSubjectNote(row.MainSubjectDisplay, row.NoteDisplay),
                Subject = CleanCell(row.MainSubjectDisplay),
                SubjectNote = CleanCell(row.NoteDisplay),
                MilitaryName = string.Empty,
                Bulletin = CleanCell(row.BulletinNumber),
                Date = row.DateText,
                Page = row.BulletinPage ?? 1,
                HasConsequence = false,
                Context = string.Empty,
                PdfPath = !string.IsNullOrWhiteSpace(bulletinPath) ? bulletinPath : row.SourcePdfPath
            });
        }
    }

    private async Task LoadWalletFurrielAddendumMentionsAsync(string normalizedFullName)
    {
        var furrielService = new FurrielBulletinService(App.Paths, App.Settings, App.MilitaryRepository, App.Log);
        var furrielStore = await furrielService.LoadIndexAsync();
        var option = new FurrielMilitaryOption
        {
            Id = Military.Id,
            Rank = Military.Rank,
            FullName = Military.Name,
            WarName = Military.WarName,
            Cpf = Military.Cpf,
            Identity = Military.MilitaryId,
            PrecCp = Military.PrecCp,
            Source = "Ativos"
        };

        // A Carteira deve espelhar exatamente a pesquisa oficial do módulo Boletim do Furriel.
        // O filtro de boletim precisa ficar vazio: "Todos" aqui é texto de UI e, se enviado
        // ao serviço, vira filtro literal e zera os ADT porque nenhum PDF contém a palavra.
        // O Search(...) já recebeu o militar aberto e já aplica a validação forte no serviço.
        var results = furrielService.Search(furrielStore, string.Empty, option, null, string.Empty, "Todos", 1000).ToList();
        await App.Log.WriteAsync($"Carteira Furriel: {results.Count} menção(ões) oficiais reaproveitadas para {Military.Name}.");

        var seenFurriel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results
                     .OrderByDescending(x => x.Date)
                     .ThenByDescending(x => x.Page))
        {
            var pdfPath = !string.IsNullOrWhiteSpace(result.SignedPdfPath) && File.Exists(result.SignedPdfPath)
                ? result.SignedPdfPath
                : result.PdfPath;

            // Para o Aditamento do Furriel, o Assunto/Nota oficial vem da letra da nota
            // validada pelo índice importado; nunca do corpo livre da nota. Aqui a Carteira
            // apenas divide o display oficial já limpo para preencher as colunas.
            var display = CleanCell(result.SubjectNoteDisplay);
            var (walletSubject, walletNote) = SplitOfficialFurrielSubjectNote(result.Subject, display);
            if (string.IsNullOrWhiteSpace(walletSubject) && string.IsNullOrWhiteSpace(walletNote) && string.IsNullOrWhiteSpace(display))
                continue;

            var bulletin = NormalizeWalletFurrielBulletin(result.Bulletin, result.FileName, pdfPath);
            var dedupeKey = string.Join("|",
                NormalizeName(Path.GetFileName(pdfPath ?? string.Empty)),
                NormalizeName(result.Date),
                result.Page <= 0 ? 1 : result.Page,
                NormalizeSearchText(display),
                NormalizeSearchText(result.Context));
            if (!seenFurriel.Add(dedupeKey)) continue;

            AddMention(new WalletMentionRecord
            {
                Source = "ADT Furriel",
                Origin = "ADT FURRIEL",
                Category = result.Type,
                Description = string.IsNullOrWhiteSpace(display) ? BuildWalletSubjectNote(walletSubject, walletNote) : display,
                Subject = walletSubject,
                SubjectNote = walletNote,
                MilitaryName = string.Empty,
                Bulletin = string.IsNullOrWhiteSpace(bulletin) ? "—" : bulletin,
                Date = result.Date,
                Page = result.Page <= 0 ? 1 : result.Page,
                HasConsequence = result.HasConsequence,
                Context = result.Context,
                PdfPath = pdfPath
            });
        }

        // A Carteira usa somente o resultado limpo do módulo Boletim do Furriel.
        // Não há segunda leitura livre do PDF para evitar títulos falsos do corpo da nota.
    }

    private bool MatchesWalletSelectedFurrielResult(FurrielSearchResult result)
    {
        var selectedName = NormalizeName(Military.Name);
        var resultName = NormalizeName(result.FullName);
        if (selectedName.Length > 0 && resultName.Length > 0)
            return resultName.Equals(selectedName, StringComparison.Ordinal);

        // Se o resultado não tem nome estruturado, só aceita quando o termo de pesquisa do PDF
        // ou o campo Militar da linha for exatamente o militar da carteira. Não usa nome de guerra
        // sozinho para não misturar militares parecidos.
        var searchTerm = NormalizeName(result.PdfSearchTerm);
        if (selectedName.Length > 0 && searchTerm.Length > 0)
            return searchTerm.Equals(selectedName, StringComparison.Ordinal);

        var display = NormalizeName(result.Military);
        return selectedName.Length > 0 && display.Equals(selectedName, StringComparison.Ordinal);
    }

    private static string BuildWalletSubjectNote(string subject, string note)
        => string.IsNullOrWhiteSpace(note) || note == "—" ? CleanCell(subject) : $"{CleanCell(subject)} — {CleanCell(note)}";

    private static (string Subject, string Note) SplitOfficialFurrielSubjectNote(string? subject, string? subjectNoteDisplay)
    {
        var cleanSubject = CleanCell(subject);
        var display = CleanCell(subjectNoteDisplay).Replace('–', '-').Replace('—', '-');
        if (string.IsNullOrWhiteSpace(display))
            return (cleanSubject, string.Empty);

        var displayParts = Regex.Split(display, @"\s+-\s+")
            .Select(CleanCell)
            .Where(x => x.Length > 0)
            .ToArray();

        if (displayParts.Length >= 2)
        {
            if (string.IsNullOrWhiteSpace(cleanSubject)) cleanSubject = displayParts[0];
            var normalizedSubject = CleanCell(cleanSubject).Replace('–', '-').Replace('—', '-');
            var startsWithSubject = display.StartsWith(normalizedSubject + " - ", StringComparison.CurrentCultureIgnoreCase);
            var note = startsWithSubject
                ? display[(normalizedSubject.Length + 3)..]
                : string.Join(" - ", displayParts.Skip(1));
            return (cleanSubject, CleanCell(note));
        }

        return (string.IsNullOrWhiteSpace(cleanSubject) ? display : cleanSubject, string.Empty);
    }

    private static string CleanCell(string? value)
        => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static (string Subject, string Note) ResolveWalletFurrielSubjectNote(FurrielSearchResult result)
    {
        var title = ExtractWalletFurrielTitle(string.Join(" ", result.SubjectNoteDisplay, result.Context, result.Preview));
        var subject = CleanCell(result.Subject);
        var note = ExtractNoteFromDisplay(result.SubjectNoteDisplay, subject);

        if (IsGenericWalletFurrielSubject(subject) && !string.IsNullOrWhiteSpace(title.Subject))
            subject = title.Subject;
        if (string.IsNullOrWhiteSpace(note) && !string.IsNullOrWhiteSpace(title.Note))
            note = title.Note;

        if (IsGenericWalletFurrielSubject(subject) && !string.IsNullOrWhiteSpace(result.SubjectNoteDisplay))
        {
            var displayTitle = ExtractWalletFurrielTitle(result.SubjectNoteDisplay);
            if (!string.IsNullOrWhiteSpace(displayTitle.Subject)) subject = displayTitle.Subject;
            if (string.IsNullOrWhiteSpace(note)) note = displayTitle.Note;
        }

        subject = CleanWalletFurrielTitle(subject);
        note = TrimWalletFurrielNote(note);
        if (IsGenericWalletFurrielSubject(subject)) subject = "Assunto não identificado";
        return (subject, note);
    }

    private static string ExtractNoteFromDisplay(string display, string subject)
    {
        var clean = CleanCell(display).Replace('–', '-').Replace('—', '-');
        var cleanSubject = CleanCell(subject).Replace('–', '-').Replace('—', '-');
        if (string.IsNullOrWhiteSpace(clean)) return string.Empty;
        var prefix = cleanSubject + " - ";
        if (!string.IsNullOrWhiteSpace(cleanSubject) && clean.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
            return TrimWalletFurrielNote(clean[prefix.Length..]);
        var parts = Regex.Split(clean, @"\s+-\s+").Select(x => CleanCell(x)).Where(x => x.Length > 0).ToArray();
        return parts.Length >= 2 ? TrimWalletFurrielNote(string.Join(" - ", parts.Skip(1))) : string.Empty;
    }

    private static (string Subject, string Note) ExtractWalletFurrielTitle(string text)
    {
        var source = CleanCell(text).Replace('–', '-').Replace('—', '-');
        if (string.IsNullOrWhiteSpace(source)) return (string.Empty, string.Empty);

        var match = Regex.Match(source, @"(?:^|\s)[a-z]\.\s+(?<subject>[^.]{4,160}?)\s+-\s+(?<note>[^.]{3,140})", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            match = Regex.Match(source, @"(?<subject>[A-ZÁÀÂÃÉÊÍÓÔÕÚÇ0-9][A-ZÁÀÂÃÉÊÍÓÔÕÚÇ0-9\s/()ºª.-]{4,120}?)\s+-\s+(?<note>Ordem de Saque|Despacho do Ordenador de Despesas|Cálculo\s*\d+|Implantação|Alteração|Atualização|Exclusão|Concessão|Saque|Suspensão|Reconhecimento|Transcrição)\b", RegexOptions.IgnoreCase);
        }
        if (!match.Success) return (string.Empty, string.Empty);

        var subject = CleanWalletFurrielTitle(match.Groups["subject"].Value);
        var note = TrimWalletFurrielNote(match.Groups["note"].Value);
        return IsGenericWalletFurrielSubject(subject) ? (string.Empty, string.Empty) : (subject, note);
    }

    private static string CleanWalletFurrielTitle(string value)
    {
        var clean = CleanCell(value).Replace('–', '-').Replace('—', '-');
        clean = Regex.Replace(clean, @"^[a-z]\.\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
        clean = Regex.Replace(clean, @"\s+-\s*$", string.Empty).Trim();
        return clean;
    }

    private static string TrimWalletFurrielNote(string value)
    {
        var clean = CleanCell(value).Replace('–', '-').Replace('—', '-');
        clean = Regex.Replace(clean, @"\s+-\s+(?:em\s+)?virtude\b.*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        clean = Regex.Replace(clean, @"\s+(?:em\s+)?virtude\s+de\b.*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        clean = Regex.Replace(clean, @"\s+(?:tendo\s+em\s+vista|conforme|referente|relativo|devido|por\s+ter|por\s+estar|para\s+fins?|correspondente)\b.*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        clean = Regex.Replace(clean, @"\s+(?:Seja|No requerimento|O militar|A militar|Os militares|As militares)\b.*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        clean = Regex.Replace(clean, @"\s+-\s*$", string.Empty).Trim();
        return clean.Length > 120 ? clean[..120] : clean;
    }

    private static bool IsGenericWalletFurrielSubject(string value)
    {
        var normalized = NormalizeName(value);
        return normalized.Length == 0
               || normalized is "pagamento pessoal" or "ocorrencia do militar no aditamento" or "assunto nao identificado" or "mencao" or "ferias"
               || normalized is "outros assuntos" or "assuntos gerais e administrativos";
    }

    private static bool IsProfessionalWalletFurrielMention(string subject, string note)
    {
        if (IsGenericWalletFurrielSubject(subject)) return false;
        if (string.IsNullOrWhiteSpace(note)) return false;
        var normalizedNote = NormalizeName(note);
        if (normalizedNote.Length == 0) return false;
        if (normalizedNote is "ferias" or "pagamento" or "mencao" or "ocorrencia") return false;
        return true;
    }

    private static int ScoreWalletFurrielMention(string subject, string note)
    {
        var score = 0;
        var normalizedSubject = NormalizeName(subject);
        var normalizedNote = NormalizeName(note);
        if (!IsGenericWalletFurrielSubject(subject)) score += 10;
        if (!string.IsNullOrWhiteSpace(note)) score += 10;
        if (normalizedSubject.Contains("adicional") || normalizedSubject.Contains("auxilio") || normalizedSubject.Contains("gratificacao") || normalizedSubject.Contains("ferias")) score += 4;
        if (normalizedNote.Contains("ordem de saque") || normalizedNote.Contains("despacho") || normalizedNote.Contains("implantacao") || normalizedNote.Contains("concessao")) score += 4;
        if (normalizedSubject is "pagamento pessoal" or "ocorrencia do militar no aditamento") score -= 50;
        if (normalizedNote is "ferias" or "pagamento" or "mencao" or "ocorrencia") score -= 20;
        return score;
    }

    private static string NormalizeWalletFurrielBulletin(string? bulletin, string? fileName, string? path)
    {
        var text = CleanCell(string.Join(' ', bulletin, fileName, Path.GetFileName(path ?? string.Empty)));
        var match = Regex.Match(text, @"(?<!\d)(?<number>\d{1,4})\s*[/_-]\s*(?<year>20\d{2})(?!\d)");
        if (match.Success)
            return $"{int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture)}/{match.Groups["year"].Value}";

        match = Regex.Match(text, @"ADT[_\s-]*FURRIEL[_\s-]*(?<number>\d{1,4}).*?(?<year>20\d{2})", RegexOptions.IgnoreCase);
        if (match.Success)
            return $"{int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture)}/{match.Groups["year"].Value}";

        var cleanBulletin = CleanCell(bulletin);
        return string.IsNullOrWhiteSpace(cleanBulletin) || Regex.IsMatch(cleanBulletin, @"^20\d{2}$") ? "—" : cleanBulletin;
    }

    private static string FindBulletinPdfPath(IntelligentBulletinStore store, SisbolPersonIndexItem item)
    {
        var number = item.BulletinNumber ?? string.Empty;
        var shortNumber = number.Split('/').FirstOrDefault() ?? number;
        var date = item.BulletinDate?.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR")) ?? string.Empty;
        var file = store.Items.FirstOrDefault(candidate =>
            candidate.BulletinNumber.Equals(number, StringComparison.OrdinalIgnoreCase)
            || ((candidate.BulletinNumber.Split('/').FirstOrDefault() ?? string.Empty).Equals(shortNumber, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(date) || candidate.BulletinDate.Equals(date, StringComparison.OrdinalIgnoreCase))));
        return file is not null && File.Exists(file.PdfPath) ? file.PdfPath : string.Empty;
    }

    private bool MatchesStructuredMention(BulletinMentionItem mention, string normalizedFullName)
    {
        if (mention.MilitaryId.HasValue && mention.MilitaryId.Value == Military.Id) return true;

        var mentionName = NormalizeName(mention.MentionedMilitaryName);
        if (mentionName.Length > 0 && mentionName.Equals(normalizedFullName, StringComparison.Ordinal)) return true;

        var haystack = NormalizeName(string.Join(" ", mention.MentionedMilitaryName, mention.MentionedMilitaryWarName, mention.NoteText, mention.ConsequenceText));
        if (normalizedFullName.Length >= 5 && haystack.Contains(normalizedFullName, StringComparison.Ordinal)) return true;

        var digits = MilitaryFormatting.Digits(string.Join(" ", mention.MentionedMilitaryCpf, mention.MentionedMilitaryPrecCp, mention.NoteText));
        foreach (var identifier in new[] { Military.Cpf, Military.PrecCp, Military.MilitaryId })
        {
            var value = MilitaryFormatting.Digits(identifier);
            if (value.Length >= 5 && digits.Contains(value, StringComparison.Ordinal)) return true;
        }

        var war = NormalizeName(Military.WarName);
        var rank = MilitaryRankService.Normalize(Military.Rank);
        var mentionRank = MilitaryRankService.Normalize(mention.MentionedMilitaryRank);
        return war.Length >= 3
               && haystack.Contains(war, StringComparison.Ordinal)
               && !string.IsNullOrWhiteSpace(rank)
               && (mentionRank.Equals(rank, StringComparison.OrdinalIgnoreCase)
                   || MilitaryRankService.Normalize(mention.NoteText).Contains(rank, StringComparison.OrdinalIgnoreCase));
    }

    private static string CleanSubjectNote(string primary, string fallbackSubject, string bulletin)
    {
        var value = string.IsNullOrWhiteSpace(primary) ? fallbackSubject : primary;
        value = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(value))
            value = string.IsNullOrWhiteSpace(bulletin) ? "Assunto não identificado" : $"Assunto não identificado — {bulletin}";
        return value;
    }

    private async Task AddGenericFurrielPdfMentionsAsync(FurrielIndexStore store, string fullName)
    {
        var candidates = new List<(string Path, string Bulletin, string Date)>();

        foreach (var file in store.Files ?? [])
        {
            if (!string.IsNullOrWhiteSpace(file.StoredPath) && File.Exists(file.StoredPath))
                candidates.Add((file.StoredPath, file.Bulletin, file.Date));
            var signed = new FurrielBulletinService(App.Paths, App.Settings, App.MilitaryRepository, App.Log).GetSignedPath(store, file);
            if (!string.IsNullOrWhiteSpace(signed) && File.Exists(signed))
                candidates.Add((signed, file.Bulletin, file.Date));
        }

        foreach (var info in (store.SignedFiles ?? new Dictionary<string, FurrielSignedFileInfo>(StringComparer.OrdinalIgnoreCase)).Values)
            if (!string.IsNullOrWhiteSpace(info.Path) && File.Exists(info.Path))
                candidates.Add((info.Path, info.Bulletin, info.Date));

        var furrielRoot = Path.Combine(App.Paths.DataDirectory, "boletim_furriel");
        if (Directory.Exists(furrielRoot))
        {
            foreach (var path in Directory.EnumerateFiles(furrielRoot, "*.pdf", SearchOption.AllDirectories).Take(1200))
                candidates.Add((path, InferBulletinFromPath(path), InferDateFromPath(path)));
        }

        candidates = candidates
            .Where(x => !string.IsNullOrWhiteSpace(x.Path) && File.Exists(x.Path))
            .DistinctBy(x => Path.GetFullPath(x.Path), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0) return;

        var cpf = MilitaryFormatting.Digits(Military.Cpf);
        var prec = MilitaryFormatting.Digits(Military.PrecCp);
        var idt = MilitaryFormatting.Digits(Military.MilitaryId);
        var war = NormalizeName(Military.WarName);
        var rank = MilitaryRankService.Normalize(Military.Rank);
        var seenGeneric = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            IReadOnlyList<string> pages;
            try { pages = await App.PdfText.ExtractPagesAsync(candidate.Path); }
            catch { continue; }
            for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
            {
                var lines = pages[pageIndex]
                    .Replace("\r", string.Empty)
                    .Split('\n')
                    .Select(x => System.Text.RegularExpressions.Regex.Replace(x ?? string.Empty, @"\s+", " ").Trim())
                    .Where(x => x.Length > 0)
                    .ToList();
                for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                {
                    var start = Math.Max(0, lineIndex - 6);
                    var end = Math.Min(lines.Count - 1, lineIndex + 8);
                    var context = string.Join(" ", lines.Skip(start).Take(end - start + 1));
                    if (!MatchesPersonText(context, fullName, cpf, prec, idt, war, rank)) continue;

                    var titleWindowStart = Math.Max(0, lineIndex - 80);
                    var titleContext = string.Join(" ", lines.Skip(titleWindowStart).Take(lineIndex - titleWindowStart + 1));
                    var title = ExtractWalletFurrielTitle(titleContext);
                    var fallbackSubject = FindNearestSubject(lines, lineIndex);
                    var subject = !string.IsNullOrWhiteSpace(title.Subject) ? title.Subject : fallbackSubject;
                    var note = title.Note;
                    var genericKey = string.Join("|", candidate.Bulletin, candidate.Date, pageIndex + 1, NormalizeName(subject), NormalizeName(note), Path.GetFileName(candidate.Path));
                    if (!seenGeneric.Add(genericKey)) continue;
                    AddMention(new WalletMentionRecord
                    {
                        Source = "ADT Furriel",
                        Origin = "ADT FURRIEL",
                        Category = "Menção",
                        Description = BuildWalletSubjectNote(subject, note),
                        Bulletin = string.IsNullOrWhiteSpace(candidate.Bulletin) ? InferBulletinFromText(string.Join(" ", lines.Take(12))) : candidate.Bulletin,
                        Date = string.IsNullOrWhiteSpace(candidate.Date) ? InferDateFromText(string.Join(" ", lines.Take(16))) : candidate.Date,
                        Page = pageIndex + 1,
                        Subject = subject,
                        SubjectNote = note,
                        MilitaryName = string.Empty,
                        Context = context,
                        PdfPath = candidate.Path
                    });
                }
            }
        }
    }

    private async Task AddGenericInternalMentionsAsync(IntelligentBulletinFile file, string normalizedFullName)
    {
        string rawText;
        try
        {
            rawText = await App.IntelligentBulletins.ReadCachedTextAsync(file);
            if (string.IsNullOrWhiteSpace(rawText) && File.Exists(file.PdfPath))
                rawText = string.Join("\f", await App.PdfText.ExtractPagesAsync(file.PdfPath));
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(rawText)) return;
        var pages = rawText.Split('\f');
        var cpf = MilitaryFormatting.Digits(Military.Cpf);
        var prec = MilitaryFormatting.Digits(Military.PrecCp);
        var idt = MilitaryFormatting.Digits(Military.MilitaryId);
        var war = NormalizeName(Military.WarName);
        var rank = MilitaryRankService.Normalize(Military.Rank);

        for (var pageIndex = 0; pageIndex < pages.Length; pageIndex++)
        {
            var lines = pages[pageIndex]
                .Replace("\r", string.Empty)
                .Split('\n')
                .Select(x => System.Text.RegularExpressions.Regex.Replace(x ?? string.Empty, @"\s+", " ").Trim())
                .Where(x => x.Length > 0)
                .ToList();

            for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                if (!lines[lineIndex].Contains("Em consequência", StringComparison.OrdinalIgnoreCase)) continue;
                var blockEnd = Math.Min(lines.Count - 1, lineIndex + 8);
                var consequence = string.Join(" ", lines.Skip(lineIndex).Take(blockEnd - lineIndex + 1));
                if (!consequence.Contains("furriel", StringComparison.OrdinalIgnoreCase)) continue;

                var start = Math.Max(0, lineIndex - 10);
                var context = string.Join(" ", lines.Skip(start).Take(blockEnd - start + 1));
                if (!MatchesPersonText(context, normalizedFullName, cpf, prec, idt, war, rank)) continue;

                AddMention(new WalletMentionRecord
                {
                    Source = "Boletim Interno",
                    Category = "Furriel",
                    Description = SafeContext(consequence, 500),
                    Bulletin = file.BulletinNumber,
                    Date = file.BulletinDate,
                    Page = pageIndex + 1,
                    Subject = "Em consequência — Furriel",
                    Context = context,
                    PdfPath = file.PdfPath
                });
            }
        }
    }

    private static bool IsFurrielConsequenceFinding(IntelligentBulletinFinding finding)
    {
        var text = string.Join(" ", finding.Category, finding.Type, finding.Detail, finding.Context);
        return text.Contains("furriel", StringComparison.OrdinalIgnoreCase)
               && text.Contains("consequ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesPersonText(string text, string normalizedFullName, string cpf, string prec, string idt, string war, string rank)
    {
        var normalizedContext = NormalizeName(text);
        var digits = MilitaryFormatting.Digits(text);
        var fullNameMatch = normalizedFullName.Length >= 5 && normalizedContext.Contains(normalizedFullName, StringComparison.Ordinal);
        var identifierMatch = (cpf.Length >= 5 && digits.Contains(cpf, StringComparison.Ordinal))
                              || (prec.Length >= 5 && digits.Contains(prec, StringComparison.Ordinal))
                              || (idt.Length >= 5 && digits.Contains(idt, StringComparison.Ordinal));
        // Nome de guerra isolado gera falso positivo em notas coletivas do Furriel
        // (ex.: ARTHUR AGUILAR x ARTHUR LUIZ). Para Carteira individual, usar
        // somente nome completo ou identificadores fortes.
        return fullNameMatch || identifierMatch;
    }

    private static string SafeContext(string context, int max)
        => context.Length > max ? context[..max].TrimEnd() + "…" : context;

    private static string ClassifyWalletFurrielMention(string context)
    {
        var text = context.ToLowerInvariant();
        if (text.Contains("auxílio-transporte") || text.Contains("auxilio transporte") || text.Contains("transporte")) return "Auxílio-Transporte";
        if (text.Contains("adicional de habilita")) return "Adicional Habilitação";
        if (text.Contains("férias") || text.Contains("ferias")) return "Férias";
        if (text.Contains("gratificação") || text.Contains("gratificacao") || text.Contains("representação") || text.Contains("representacao")) return "Grat Rep";
        if (text.Contains("saque") || text.Contains("pagamento")) return "Pagamento";
        return "Menção";
    }

    private static string FindNearestSubject(IReadOnlyList<string> lines, int index)
    {
        for (var i = Math.Max(0, index - 10); i <= index; i++)
        {
            var text = lines[i];
            if (text.Length <= 100 && System.Text.RegularExpressions.Regex.IsMatch(text, @"^[a-z]\.\s+|PAGAMENTO|AUX[IÍ]LIO|ADICIONAL|F[EÉ]RIAS|GRATIFICA", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return text;
        }
        return "Ocorrência do militar no aditamento";
    }

    private static string InferBulletinFromPath(string path)
        => System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(path), @"(?:Nr|nº|n°|_)\s*(\d{1,4})(?:[/_-](20\d{2}))?", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : "—";

    private static string InferDateFromPath(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var m = System.Text.RegularExpressions.Regex.Match(name, @"(20\d{2})[-_](\d{1,2})[-_](\d{1,2})");
        return m.Success ? $"{int.Parse(m.Groups[3].Value):00}/{int.Parse(m.Groups[2].Value):00}/{m.Groups[1].Value}" : string.Empty;
    }

    private static string InferBulletinFromText(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"ADITAMENTO\s+DO\s+FURRIEL\s+N[ºo°]?\s*(\d{1,4}/20\d{2}|\d{1,4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : "—";
    }

    private static string InferDateFromText(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"(\d{1,2})\s+de\s+([A-Za-zçÇéÉãÃ]+)\s+de\s+(20\d{2})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return string.Empty;
        var months = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["janeiro"] = 1, ["fevereiro"] = 2, ["março"] = 3, ["marco"] = 3, ["abril"] = 4, ["maio"] = 5, ["junho"] = 6,
            ["julho"] = 7, ["agosto"] = 8, ["setembro"] = 9, ["outubro"] = 10, ["novembro"] = 11, ["dezembro"] = 12
        };
        return months.TryGetValue(match.Groups[2].Value, out var month)
            ? $"{int.Parse(match.Groups[1].Value):00}/{month:00}/{match.Groups[3].Value}"
            : string.Empty;
    }

    private static bool IsFurrielRelevantFinding(IntelligentBulletinFinding finding)
    {
        var text = NormalizeName(string.Join(" ", finding.Category, finding.Type, finding.Subject, finding.SubjectNoteDisplay, finding.ConsequenceText, finding.NoteText, finding.Context));
        return text.Contains("FURRIEL", StringComparison.Ordinal);
    }

    private bool MatchesIntelligentFinding(IntelligentBulletinFinding finding, string normalizedFullName)
    {
        var findingFull = NormalizeName(finding.FullName);
        if (findingFull.Length > 0 && findingFull.Equals(normalizedFullName, StringComparison.Ordinal)) return true;
        var findingMilitary = NormalizeName(finding.Military);
        if (findingMilitary.Length > 0 && findingMilitary.Equals(normalizedFullName, StringComparison.Ordinal)) return true;

        var context = NormalizeName(string.Join(" ", finding.FullName, finding.Military, finding.Detail, finding.Context, finding.NoteText, finding.SubjectNoteDisplay, finding.PdfSearchTerm));
        if (normalizedFullName.Length >= 5 && context.Contains(normalizedFullName, StringComparison.Ordinal)) return true;

        foreach (var identifier in new[] { Military.Cpf, Military.PrecCp, Military.MilitaryId })
        {
            var digits = MilitaryFormatting.Digits(identifier);
            if (digits.Length >= 5 && MilitaryFormatting.Digits(string.Join(" ", finding.MentionedCpf, finding.MentionedPrecCp, finding.Detail, finding.Context, finding.NoteText, finding.Military)).Contains(digits, StringComparison.Ordinal))
                return true;
        }

        // Compatibilidade com índices antigos que gravavam somente nome de guerra:
        // exige também o mesmo posto/graduação para reduzir homônimos.
        var war = NormalizeName(Military.WarName);
        var rank = MilitaryRankService.Normalize(Military.Rank);
        var findingWar = NormalizeName(finding.WarName);
        var findingRank = MilitaryRankService.Normalize(finding.Rank);
        var findingContext = NormalizeName(string.Join(" ", finding.Military, finding.FullName, finding.WarName, finding.Detail, finding.Context, finding.NoteText));
        var warMatches = war.Length >= 3 &&
                         (findingWar.Equals(war, StringComparison.Ordinal) || findingContext.Contains(war, StringComparison.Ordinal));
        var rankMatches = rank.Length > 0 &&
                          (findingRank.Equals(rank, StringComparison.OrdinalIgnoreCase) ||
                           MilitaryRankService.Normalize(finding.Military).Contains(rank, StringComparison.OrdinalIgnoreCase));
        return warMatches && rankMatches;
    }

    private void AddMention(WalletMentionRecord mention)
    {
        mention.UpdatePeriod();
        var key = WalletMentionLogicalKey(mention);
        var duplicate = _allMentions.Any(x => WalletMentionLogicalKey(x).Equals(key, StringComparison.OrdinalIgnoreCase));
        if (!duplicate) _allMentions.Add(mention);
    }

    private static string WalletMentionLogicalKey(WalletMentionRecord mention)
    {
        var sourceFamily = mention.Source.Contains("Furriel", StringComparison.OrdinalIgnoreCase) || mention.Origin.Contains("FURRIEL", StringComparison.OrdinalIgnoreCase)
            ? "ADT_FURRIEL"
            : mention.Source.Contains("Índice", StringComparison.OrdinalIgnoreCase) || mention.Source.Contains("Indice", StringComparison.OrdinalIgnoreCase) || mention.Origin.Equals("BI", StringComparison.OrdinalIgnoreCase)
                ? "BI_INDICE"
                : NormalizeName(mention.Source);
        var bulletin = NormalizeMentionBulletinForKey(mention.Bulletin, mention.Date, mention.FileName, mention.PdfPath);
        var subject = NormalizeSearchText(mention.Subject);
        var note = NormalizeSearchText(mention.SubjectNote);
        var description = NormalizeSearchText(mention.Description);
        var subjectNote = string.IsNullOrWhiteSpace(subject + note) ? description : $"{subject}|{note}";
        return string.Join("|", sourceFamily, bulletin, mention.Page <= 0 ? 1 : mention.Page, subjectNote);
    }

    private static string NormalizeMentionBulletinForKey(string? bulletin, string? date, string? fileName, string? pdfPath)
    {
        var text = CleanCell(string.Join(' ', bulletin, fileName, Path.GetFileName(pdfPath ?? string.Empty)));
        var inferred = Regex.Match(text, @"(?<!\d)(?<number>\d{1,4})\s*[/_-]\s*(?<year>20\d{2})(?!\d)");
        if (inferred.Success)
            return $"{int.Parse(inferred.Groups["number"].Value, CultureInfo.InvariantCulture)}/{inferred.Groups["year"].Value}";

        inferred = Regex.Match(text, @"ADT[_\s-]*FURRIEL[_\s-]*0*(?<number>\d{1,4}).*?(?<year>20\d{2})", RegexOptions.IgnoreCase);
        if (inferred.Success)
            return $"{int.Parse(inferred.Groups["number"].Value, CultureInfo.InvariantCulture)}/{inferred.Groups["year"].Value}";

        var clean = CleanCell(bulletin);
        var number = Regex.Match(clean, @"\d+").Value;
        var year = ParseDateBr(date)?.Year.ToString(CultureInfo.InvariantCulture) ?? Regex.Match(clean, @"20\d{2}").Value;
        if (int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedNumber))
        {
            // Se o campo do boletim veio só com o ano, não usa isso como número.
            if (parsedNumber >= 1900 && parsedNumber <= 2099)
                return year.Length > 0 ? $"ANO/{year}" : "ANO";
            return year.Length > 0 ? $"{parsedNumber}/{year}" : parsedNumber.ToString(CultureInfo.InvariantCulture);
        }

        return NormalizeName(clean);
    }

    private static DateTime? ParseDateBr(string? value)
    {
        foreach (var format in new[] { "dd/MM/yyyy", "dd-MM-yyyy", "yyyy-MM-dd" })
            if (DateTime.TryParseExact(CleanCell(value), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        return null;
    }

    private void ApplyMentionFilter()
    {
        var year = int.TryParse(SelectedMentionYear, out var parsedYear) ? parsedYear : 0;
        var month = MentionMonthOptions.IndexOf(SelectedMentionMonth);
        var terms = SearchTermsNormalized(MentionSearchText);
        FurrielMentions.Clear();
        InternalBulletinMentions.Clear();
        FurrielAddendumMentions.Clear();
        foreach (var mention in _allMentions
                     .Where(x => year == 0 || x.Year == year)
                     .Where(x => month <= 0 || x.Month == month)
                     .Where(x => MentionMatches(x, terms))
                     .OrderByDescending(x => x.DateValue)
                     .ThenByDescending(x => x.Page))
        {
            FurrielMentions.Add(mention);
            if (mention.Source.Contains("Boletim Interno", StringComparison.OrdinalIgnoreCase)
                || mention.Source.Contains("Boletim Inteligente", StringComparison.OrdinalIgnoreCase))
                InternalBulletinMentions.Add(mention);
            else
                FurrielAddendumMentions.Add(mention);
        }
        StatusText = $"{InternalBulletinMentions.Count} menção(ões) em BI interno e {FurrielAddendumMentions.Count} em aditamento do Furriel.";
    }

    private static string[] SearchTermsNormalized(string? value)
    {
        var normalized = NormalizeSearchText(value);
        return string.IsNullOrWhiteSpace(normalized)
            ? []
            : normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool MentionMatches(WalletMentionRecord mention, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return true;
        var text = NormalizeSearchText(string.Join(' ', mention.Source, mention.Origin, mention.Category, mention.Description, mention.Bulletin,
            mention.Date, mention.Page, mention.Subject, mention.SubjectNote, mention.Context, mention.FileName));
        return terms.All(term => text.Contains(term, StringComparison.Ordinal));
    }

    private static string NormalizeSearchText(string? value)
    {
        var text = (value ?? string.Empty).Normalize(System.Text.NormalizationForm.FormD);
        var chars = text
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : ' ')
            .ToArray();
        return Regex.Replace(new string(chars), @"\s+", " ").Trim();
    }

    private static string NormalizeName(string? value)
    {
        var text = (value ?? string.Empty).Normalize(System.Text.NormalizationForm.FormD);
        return new string(text.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)).Select(char.ToUpperInvariant).ToArray());
    }

    private static string BuildMentionDescription(FurrielSearchResult result)
    {
        var text = $"{result.Subject} {result.Context}".ToLowerInvariant();
        var category = text.Contains("ferias") || text.Contains("férias") ? "Férias" :
            text.Contains("auxilio transporte") || text.Contains("auxílio-transporte") || text.Contains("transporte") ? "Auxílio-Transporte" :
            text.Contains("ajuste de contas") ? "Ajuste de Contas" :
            text.Contains("pagamento") || text.Contains("saque") ? "Pagamento" : result.Type;
        var detail = string.IsNullOrWhiteSpace(result.SubjectNoteDisplay) ? result.Subject : result.SubjectNoteDisplay;
        return string.IsNullOrWhiteSpace(detail) ? category : $"{category} — {detail}";
    }

    public async Task AddDocumentAsync(string source, string type, string title, string observation = "", string keysJson = "")
    {
        IsBusy = true;
        StatusText = "Copiando e registrando documento…";
        try
        {
            var document = await _repository.AddDocumentAsync(Military, source, type, title, observation, keysJson);
            Documents.Insert(0, document);
            StatusText = "Documento incluído na carteira.";
        }
        finally { IsBusy = false; }
    }

    public async Task UpdateDocumentOcrAsync(MilitaryDocumentRecord document, string observation, string keysJson)
    {
        IsBusy = true;
        StatusText = "Atualizando leitura OCR e chaves do Boletim…";
        try
        {
            await _repository.UpdateDocumentOcrAsync(document.Id, observation, keysJson);
            document.Observation = observation;
            document.KeysJson = keysJson;
            Documents.Clear();
            foreach (var item in await _repository.GetDocumentsAsync(Military.Id)) Documents.Add(item);
            StatusText = "OCR conferido e chaves atualizadas.";
        }
        finally { IsBusy = false; }
    }

    public async Task RemoveDocumentAsync(MilitaryDocumentRecord document, bool deleteFile)
    {
        await _repository.RemoveDocumentAsync(document, deleteFile);
        Documents.Remove(document);
        StatusText = "Documento removido da carteira.";
    }

    public async Task SaveFaresAsync(IEnumerable<TransportFareRecord> fares, int workingDays)
    {
        await _repository.SaveTransportFaresAsync(Military, fares, workingDays);
        Military = await _repository.GetByIdAsync(Military.Id) ?? Military;
        Transport = await _repository.GetTransportSummaryAsync(Military);
        StatusText = "Auxílio-Transporte recalculado e salvo.";
    }

    public async Task SaveIntervalAsync(ServiceIntervalRecord interval)
    {
        await _repository.SaveServiceIntervalAsync(interval);
        ServiceIntervals.Clear();
        foreach (var item in await _repository.GetServiceIntervalsAsync(Military.Id)) ServiceIntervals.Add(item);
        OnPropertyChanged(nameof(TotalServiceDays));
        OnPropertyChanged(nameof(TotalServiceText));
        StatusText = "Intervalo de tempo de serviço salvo.";
    }

    public async Task DeleteIntervalAsync(ServiceIntervalRecord interval)
    {
        await _repository.DeleteServiceIntervalAsync(interval.Id);
        ServiceIntervals.Remove(interval);
        OnPropertyChanged(nameof(TotalServiceDays));
        OnPropertyChanged(nameof(TotalServiceText));
        StatusText = "Intervalo excluído.";
    }
}


public sealed class VacationPaymentRow
{
    public int Year { get; set; }
    public string Period { get; set; } = string.Empty;
    public int Days { get; set; }
    public bool IsPaid { get; set; }
    public DateTime? PaidAt { get; set; }
    public string Status => IsPaid ? "Pago" : "Não pago";
    public string PaidAtText => PaidAt?.ToString("dd/MM/yyyy HH:mm") ?? "—";
}

public sealed class WalletMentionRecord
{
    public string Source { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Bulletin { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public int Page { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string SubjectNote { get; set; } = string.Empty;
    public string MilitaryName { get; set; } = string.Empty;
    public bool HasConsequence { get; set; }
    public string Context { get; set; } = string.Empty;
    public string PdfPath { get; set; } = string.Empty;
    public string OriginDisplay => string.IsNullOrWhiteSpace(Origin) ? Source : Origin;
    public string SubjectNoteDisplay => string.IsNullOrWhiteSpace(SubjectNote) ? (string.IsNullOrWhiteSpace(Subject) ? Description : Subject) : SubjectNote;
    public string MilitaryDisplay => string.IsNullOrWhiteSpace(MilitaryName) ? "Militar não identificado" : MilitaryName;
    public string ConsequenceDisplay => HasConsequence ? "Sim" : "Não";
    public string FileName => string.IsNullOrWhiteSpace(PdfPath) ? "—" : Path.GetFileName(PdfPath);
    public int Year { get; private set; }
    public int Month { get; private set; }
    public DateTime DateValue { get; private set; } = DateTime.MinValue;

    public void UpdatePeriod()
    {
        DateTime parsed;
        var formats = new[] { "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "dd-MM-yyyy", "d-M-yyyy" };
        if (DateTime.TryParseExact(Date, formats, System.Globalization.CultureInfo.GetCultureInfo("pt-BR"), System.Globalization.DateTimeStyles.None, out parsed)
            || DateTime.TryParse(Date, System.Globalization.CultureInfo.GetCultureInfo("pt-BR"), System.Globalization.DateTimeStyles.None, out parsed))
        {
            DateValue = parsed.Date; Year = parsed.Year; Month = parsed.Month;
            return;
        }
        var match = System.Text.RegularExpressions.Regex.Match(Date ?? string.Empty, @"(?<!\d)(20\d{2})(?!\d)");
        if (match.Success) Year = int.Parse(match.Value);
    }
}
