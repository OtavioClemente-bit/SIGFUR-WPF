using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views.Bulletin;

namespace SIGFUR.Wpf.Views.Finance;

public partial class AdjustmentBulletinWindow : Window
{
    private readonly AdjustmentSimulationResult _result;
    private readonly string _validatedReason;
    private readonly ObservableCollection<BulletinMilitarySelection> _allMilitary = [];
    private SavedBulletinReference? _selectedBulletin;
    public ObservableCollection<BulletinMilitarySelection> VisibleMilitary { get; } = [];
    public AdjustmentAccountsSettings Settings { get; }
    public bool DocumentBuilt { get; private set; }
    public string GeneratedDocumentPath { get; private set; } = string.Empty;

    public AdjustmentBulletinWindow(
        AdjustmentAccountsSettings settings,
        AdjustmentSimulationResult result,
        MilitaryRecord military)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        Settings = settings;
        _result = result;
        Settings.Rank = result.Draft.HistoricalRank;
        Settings.CutoffDate = result.Draft.EntitlementEnd?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? Settings.CutoffDate;
        _validatedReason = string.IsNullOrWhiteSpace(result.Draft.AdjustmentReason) ? Settings.BulletinReason : result.Draft.AdjustmentReason;
        Settings.BulletinReason = _validatedReason;
        Settings.VacationMonths = result.VacationQuotas;
        Settings.ChristmasMonths = Math.Clamp(result.Draft.ChristmasQuotas, 0, 12);
        Settings.PecuniaryQuotas = Math.Clamp(result.Draft.PecuniaryQuotas, 0, 50);
        Settings.ServedDays = result.ComputableDays;
        if (result.Draft.EntitlementEnd is { } periodEnd)
            Settings.DaysInMonth = DateTime.DaysInMonth(periodEnd.Year, periodEnd.Month);
        DataContext = this;

        _allMilitary.Add(new BulletinMilitarySelection { Military = military, IsSelected = true });

        var activeCount = _allMilitary.Count(x => x.Source.Equals("Ativo", StringComparison.OrdinalIgnoreCase));
        var licensedTransferredCount = _allMilitary.Count - activeCount;
        MilitaryFilterText.Text = $"Resultado validado e individual de {result.Draft.MilitaryName}. P/Grad real: {result.Draft.HistoricalRank}; P/Grad atual: {result.Draft.CurrentRank}.";
        RefreshVisibleMilitary();
        LoadSettings();
        Loaded += (_, _) =>
            StatusText.Text = "Clique em Gerar / atualizar para montar a prévia com o mesmo resultado imutável da simulação.";
        Closing += (_, _) => ReadSettings();
    }

    private void LoadSettings()
    {
        OrganizationBox.Text = Settings.Organization;
        BulletinNumberBox.Text = Settings.BulletinNumber;
        BulletinDateBox.Text = Settings.BulletinDate;
        CutoffDatePicker.SelectedDate = ParseBrazilianDate(Settings.CutoffDate);
        ReasonBox.Text = Settings.BulletinReason;
        IntroductionBox.Text = string.IsNullOrWhiteSpace(Settings.BulletinIntroduction)
            ? AdjustmentAccountsService.BulletinIntroductionForReason(Settings.BulletinReason)
            : AdjustmentAccountsService.SimplifyBulletinIntroduction(Settings.BulletinIntroduction);
        VacationYearBox.Text = Settings.VacationReferenceYear;
        ChristmasYearBox.Text = Settings.ChristmasReferenceYear;
        FinalObservationBox.Text = Settings.BulletinFinalObservation;
        SisbolSubjectBox.Text = Settings.SisbolSubject;
        SisbolCodeBox.Text = Settings.SisbolSpecificCode;
        IncludeIntroductionCheck.IsChecked = Settings.BulletinIncludeIntroduction;
        IncludeEarningsCheck.IsChecked = Settings.BulletinIncludeEarnings;
        IncludeDiscountsCheck.IsChecked = Settings.BulletinIncludeDiscounts;
        IncludeTotalsCheck.IsChecked = Settings.BulletinIncludeTotals;
        IncludeIdentificationCheck.IsChecked = Settings.BulletinIncludeIdentification;
        IncludeSeparatorCheck.IsChecked = Settings.BulletinIncludeSeparator;
        NumberBatchCheck.IsChecked = Settings.BulletinNumberBatch;
        ShowCodesCheck.IsChecked = Settings.BulletinShowCodes;
        SimplifyDescriptionsCheck.IsChecked = Settings.BulletinSimplifyDescriptions;
        HideZeroCheck.IsChecked = Settings.BulletinHideZeroLines;
    }

    private void ReadSettings()
    {
        Settings.Organization = OrganizationBox.Text.Trim();
        Settings.BulletinNumber = BulletinNumberBox.Text.Trim();
        Settings.BulletinDate = BulletinDateBox.Text.Trim();
        Settings.CutoffDate = CutoffDatePicker.SelectedDate?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? string.Empty;
        Settings.BulletinReason = _validatedReason;
        Settings.BulletinIntroduction = IntroductionBox.Text;
        Settings.VacationReferenceYear = VacationYearBox.Text.Trim();
        Settings.ChristmasReferenceYear = ChristmasYearBox.Text.Trim();
        Settings.BulletinFinalObservation = FinalObservationBox.Text;
        Settings.SisbolSubject = SisbolSubjectBox.Text.Trim();
        Settings.SisbolSpecificCode = SisbolCodeBox.Text.Trim();
        Settings.BulletinIncludeIntroduction = IncludeIntroductionCheck.IsChecked == true;
        Settings.BulletinIncludeEarnings = IncludeEarningsCheck.IsChecked == true;
        Settings.BulletinIncludeDiscounts = IncludeDiscountsCheck.IsChecked == true;
        Settings.BulletinIncludeTotals = IncludeTotalsCheck.IsChecked == true;
        Settings.BulletinIncludeIdentification = IncludeIdentificationCheck.IsChecked == true;
        Settings.BulletinIncludeSeparator = IncludeSeparatorCheck.IsChecked == true;
        Settings.BulletinNumberBatch = NumberBatchCheck.IsChecked == true;
        Settings.BulletinShowCodes = ShowCodesCheck.IsChecked == true;
        Settings.BulletinSimplifyDescriptions = SimplifyDescriptionsCheck.IsChecked == true;
        Settings.BulletinHideZeroLines = HideZeroCheck.IsChecked == true;
    }

    private void MilitarySearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshVisibleMilitary();

    private void RefreshVisibleMilitary()
    {
        var query = MilitaryRankService.Normalize(MilitarySearchBox?.Text);
        VisibleMilitary.Clear();
        foreach (var item in _allMilitary)
        {
            var haystack = MilitaryRankService.Normalize($"{item.Source} {item.Rank} {item.ShortRank} {item.Name} {item.WarName} {item.PrecCp} {item.Cpf}");
            if (string.IsNullOrWhiteSpace(query) || haystack.Contains(query, StringComparison.OrdinalIgnoreCase)) VisibleMilitary.Add(item);
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in VisibleMilitary) item.IsSelected = true;
        MilitaryGrid.Items.Refresh();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in VisibleMilitary) item.IsSelected = false;
        MilitaryGrid.Items.Refresh();
    }

    private async void Build_Click(object sender, RoutedEventArgs e) => await BuildDocumentAsync();

    private void ChooseSavedBulletin_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SavedBulletinPickerWindow("Boletim Interno") { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedReference is not { } reference) return;

        if (!reference.Kind.Equals("Boletim Interno", StringComparison.OrdinalIgnoreCase))
        {
            SigfurDialog.Show(this,
                "Para o Ajuste de Contas, selecione um Boletim Interno salvo. O modelo atual usa a referência de BI na abertura.",
                "Publicação incompatível", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(reference.Number) || ParseBrazilianDate(reference.Date) is null)
        {
            SigfurDialog.Show(this,
                "O boletim selecionado não possui número e data válidos no cadastro. Escolha outro boletim ou corrija a publicação salva.",
                "Dados incompletos", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _selectedBulletin = reference;
        BulletinNumberBox.Text = reference.PublicationNumber;
        BulletinDateBox.Text = ParseBrazilianDate(reference.Date)!.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        SelectedBulletinText.Text = $"{reference.ReferenceText} — {reference.Title}";
        StatusText.Text = "Boletim salvo selecionado. Confira somente a data de corte e gere a prévia.";
    }

    private Task BuildDocumentAsync()
    {
        ReadSettings();
        if (_selectedBulletin is null)
        {
            SigfurDialog.Show(this, "Selecione o Boletim Interno salvo que será usado como referência.",
                "Boletim de referência", MessageBoxButton.OK, MessageBoxImage.Information);
            return Task.CompletedTask;
        }
        if (CutoffDatePicker.SelectedDate is null)
        {
            SigfurDialog.Show(this, "Informe a data de corte do ajuste de contas.",
                "Data de corte", MessageBoxButton.OK, MessageBoxImage.Warning);
            CutoffDatePicker.Focus();
            return Task.CompletedTask;
        }
        var selected = _allMilitary
            .Where(x => x.IsSelected)
            .Select(x => x.Military)
            .ToList();
        if (selected.Count == 0)
        {
            SigfurDialog.Show(this, "Selecione ao menos um militar do mesmo posto/graduação do cálculo.", "Boletim", MessageBoxButton.OK, MessageBoxImage.Warning);
            return Task.CompletedTask;
        }

        var document = new FlowDocument
        {
            FontFamily = new FontFamily(BulletinTextFormatter.StandardFontFamily), FontSize = BulletinTextFormatter.StandardWpfFontSize,
            PagePadding = new Thickness(28), ColumnWidth = double.PositiveInfinity,
            TextAlignment = TextAlignment.Justify
        };

        for (var index = 0; index < selected.Count; index++)
        {
            if (index > 0) document.Blocks.Add(BlankLine());
            var military = selected[index];
            var settings = Settings.Clone();
            AppendMilitarySection(document, military, settings, _result, index + 1, selected.Count);
            document.Blocks.Add(BlankLine());
        }

        PreviewBox.Document = document;
        DocumentBuilt = true;
        StatusText.Text = "Boletim gerado a partir do CalculationResult validado; nenhuma fórmula foi reexecutada no documento.";
        return Task.CompletedTask;
    }

    private static DateTime? ParseBrazilianDate(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        var formats = new[] { "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd" };
        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)) return parsed;
        return DateTime.TryParse(text, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.AllowWhiteSpaces, out parsed) ? parsed : null;
    }

    private static string PublicationNumberWithoutYear(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return Regex.Replace(text, @"\b(?<num>\d{1,5})\s*/\s*(?:20)?\d{2}\b",
            match => match.Groups["num"].Value, RegexOptions.CultureInvariant).Trim();
    }


    private void AppendMilitarySection(FlowDocument document, MilitaryRecord military, AdjustmentAccountsSettings settings, AdjustmentSimulationResult result, int number, int total)
    {
        if (settings.BulletinNumberBatch && total > 1)
        {
            var numberParagraph = NewParagraph();
            numberParagraph.Inlines.Add(new Bold(new Run($"{number}. ")));
            AppendTextWithWarName(numberParagraph, AdjustmentAccountsService.FormatBulletinMilitaryName(military), military);
            document.Blocks.Add(numberParagraph);
        }

        if (settings.BulletinIncludeIntroduction)
        {
            var intro = AdjustmentAccountsService.ReplaceBulletinTokens(settings.BulletinIntroduction, settings, military);
            var paragraph = NewParagraph();
            AppendTextWithWarName(paragraph, intro, military);
            document.Blocks.Add(paragraph);
        }

        if (result.EffectiveEntitlementStart is { } periodStart && result.Draft.EntitlementEnd is { } periodEnd)
        {
            var period = NewParagraph();
            period.Inlines.Add(new Bold(new Run("Período de apuração: ")));
            period.Inlines.Add(new Run($"{periodStart:dd/MM/yyyy} a {periodEnd:dd/MM/yyyy} ({result.ComputableDays} dia(s) computável(is))."));
            document.Blocks.Add(period);
        }

        if (settings.BulletinIncludeEarnings)
            AppendRubricGroup(document, "a. Rendimentos", result.Components.Where(x => x.CountsTowardTotal && x.IsEarning), military, settings, result);
        if (settings.BulletinIncludeDiscounts)
            AppendRubricGroup(document, "b. Descontos", result.Components.Where(x => x.CountsTowardTotal && !x.IsEarning), military, settings, result);

        if (settings.BulletinIncludeTotals)
        {
            var paragraph = NewParagraph();
            paragraph.Inlines.Add(new Bold(new Run("Totais: ")));
            paragraph.Inlines.Add(new Run($"rendimentos {AdjustmentAccountsService.FormatMoney(result.Earnings)}; descontos {AdjustmentAccountsService.FormatMoney(result.Discounts)}; líquido {AdjustmentAccountsService.FormatMoney(result.Net)}."));
            document.Blocks.Add(paragraph);
        }

        if (!string.IsNullOrWhiteSpace(settings.BulletinFinalObservation))
        {
            var observation = NewParagraph();
            AppendTextWithWarName(observation, AdjustmentAccountsService.ReplaceBulletinTokens(settings.BulletinFinalObservation, settings, military), military);
            document.Blocks.Add(observation);
        }

        if (settings.BulletinIncludeIdentification)
        {
            // Padrão único do SIGFUR: P/G + nome na primeira linha e identificação na segunda.
            var identificationName = NewParagraph();
            identificationName.Margin = new Thickness(0, 10, 0, 0);
            AppendTextWithWarName(identificationName, AdjustmentAccountsService.FormatBulletinMilitaryName(military), military);
            document.Blocks.Add(identificationName);

            var identificationData = NewParagraph();
            identificationData.Inlines.Add(new Run($"Prec-CP {AdjustmentAccountsService.Digits(military.PrecCp)} CPF {AdjustmentAccountsService.FormatCpf(military.Cpf)}"));
            document.Blocks.Add(identificationData);
        }

        if (settings.BulletinIncludeSeparator && number < total)
        {
            var separator = new Paragraph(new Run(new string('—', 84))) { TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 10) };
            document.Blocks.Add(separator);
        }
        else if (number < total)
        {
            document.Blocks.Add(new Paragraph { Margin = new Thickness(0, 7, 0, 7) });
        }
    }

    private static Paragraph NewParagraph() => new() { Margin = new Thickness(0, 0, 0, 8), LineHeight = 21 };

    private static Paragraph BlankLine() => new(new Run(string.Empty))
    {
        Margin = new Thickness(0, 5, 0, 5),
        FontFamily = new FontFamily(BulletinTextFormatter.StandardFontFamily),
        FontSize = BulletinTextFormatter.StandardWpfFontSize
    };

    private static void AppendRubricGroup(FlowDocument document, string title, IEnumerable<AdjustmentComponentResult> source, MilitaryRecord military, AdjustmentAccountsSettings settings, AdjustmentSimulationResult result)
    {
        var rows = source.Where(x => x.IsEligible && (!settings.BulletinHideZeroLines || x.MonetaryValue != 0m)).ToList();
        if (rows.Count == 0) return;
        document.Blocks.Add(new Paragraph(new Bold(new Run(title))) { Margin = new Thickness(0, 5, 0, 4) });
        foreach (var row in rows)
        {
            var description = AdjustmentAccountsService.ProfessionalDescription(row.Description, row.RubricCode, settings.BulletinSimplifyDescriptions);
            var prefix = settings.BulletinShowCodes ? $"{row.RubricCode} — " : string.Empty;
            var suffix = ReferenceSuffix(row, settings, result);
            var paragraph = NewParagraph();
            paragraph.Margin = new Thickness(22, 0, 0, 4);
            paragraph.Inlines.Add(new Run($"- {prefix}{description}{suffix}: {AdjustmentAccountsService.FormatMoney(row.MonetaryValue)};"));
            document.Blocks.Add(paragraph);
        }
    }

    private static string ReferenceSuffix(AdjustmentComponentResult row, AdjustmentAccountsSettings settings, AdjustmentSimulationResult result)
        => row.RubricCode.ToUpperInvariant() switch
        {
            "AR0094" or "AR0096" => $" — Ano {settings.VacationReferenceYear} — {result.VacationQuotas}/12 avos — parâmetro SIPPES: {result.VacationQuotas} meses",
            "AR0070" or "AR0083" or "DR0083" => $" — Ano {settings.ChristmasReferenceYear} — {settings.ChristmasMonths}/12 avos",
            "AR0066" => $" — {result.Draft.PecuniaryQuotas} remuneração(ões)",
            "AR0003" => $" — {row.Quantity}/{result.DaysInMonth} dias",
            _ when row.ComponentType.Contains("mensal", StringComparison.OrdinalIgnoreCase)
                => $" — {result.ComputableDays}/{result.DaysInMonth} dias",
            _ => string.Empty
        };

    private static void AppendTextWithWarName(Paragraph paragraph, string text, MilitaryRecord military)
    {
        var formattedName = AdjustmentAccountsService.FormatBulletinMilitaryName(military);
        var fullName = AdjustmentAccountsService.BulletinFullName(military);
        var war = (military.WarName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(war) || string.IsNullOrWhiteSpace(formattedName) || string.IsNullOrWhiteSpace(fullName))
        {
            paragraph.Inlines.Add(new Run(text));
            return;
        }

        var start = 0;
        while (start < text.Length)
        {
            var index = text.IndexOf(formattedName, start, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0)
            {
                paragraph.Inlines.Add(new Run(text[start..]));
                break;
            }
            if (index > start) paragraph.Inlines.Add(new Run(text[start..index]));
            AppendFormattedName(paragraph, text.Substring(index, formattedName.Length), fullName, war);
            start = index + formattedName.Length;
        }
        if (text.Length == 0) paragraph.Inlines.Add(new Run(string.Empty));
    }

    private static void AppendFormattedName(Paragraph paragraph, string formattedName, string fullName, string war)
    {
        var nameIndex = formattedName.IndexOf(fullName, StringComparison.CurrentCultureIgnoreCase);
        var warIndex = fullName.IndexOf(war, StringComparison.CurrentCultureIgnoreCase);
        if (nameIndex < 0 || warIndex < 0)
        {
            paragraph.Inlines.Add(new Run(formattedName));
            return;
        }

        var boldStart = nameIndex + warIndex;
        if (boldStart > 0) paragraph.Inlines.Add(new Run(formattedName[..boldStart]));
        paragraph.Inlines.Add(new Bold(new Run(formattedName.Substring(boldStart, war.Length))));
        var after = boldStart + war.Length;
        if (after < formattedName.Length) paragraph.Inlines.Add(new Run(formattedName[after..]));
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => PreviewBox.Document = new FlowDocument();

    private void CopyWord_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var range = new TextRange(PreviewBox.Document.ContentStart, PreviewBox.Document.ContentEnd);
            using var stream = new MemoryStream();
            range.Save(stream, DataFormats.Rtf);
            var data = new DataObject();
            data.SetData(DataFormats.Rtf, Encoding.ASCII.GetString(stream.ToArray()));
            data.SetData(DataFormats.UnicodeText, range.Text.TrimEnd());
            Clipboard.SetDataObject(data, true);
            StatusText.Text = "Texto copiado com formatação RTF. O nome de guerra permanece em negrito no Word.";
        }
        catch (Exception ex)
        {
            SigfurDialog.Show(this, "Não foi possível copiar o texto formatado.\n\n" + ex.Message, "Boletim", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveRtf_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Documento RTF (*.rtf)|*.rtf", FileName = "boletim_ajuste_contas.rtf" };
        if (dialog.ShowDialog(this) != true) return;
        using var stream = File.Create(dialog.FileName);
        new TextRange(PreviewBox.Document.ContentStart, PreviewBox.Document.ContentEnd).Save(stream, DataFormats.Rtf);
        GeneratedDocumentPath = dialog.FileName;
        DocumentBuilt = true;
        StatusText.Text = "RTF salvo em " + dialog.FileName;
    }

    private void SaveText_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Texto (*.txt)|*.txt", FileName = "boletim_ajuste_contas.txt" };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllText(dialog.FileName, new TextRange(PreviewBox.Document.ContentStart, PreviewBox.Document.ContentEnd).Text.TrimEnd(), Encoding.UTF8);
        GeneratedDocumentPath = dialog.FileName;
        DocumentBuilt = true;
        StatusText.Text = "Texto salvo em " + dialog.FileName;
    }

    private async void Sisbol_Click(object sender, RoutedEventArgs e)
    {
        ReadSettings();
        var range = new TextRange(PreviewBox.Document.ContentStart, PreviewBox.Document.ContentEnd);
        var text = range.Text.TrimEnd();
        if (string.IsNullOrWhiteSpace(text))
        {
            SigfurDialog.Show(this, "Gere ou digite o texto do boletim antes de enviar.", "SisBol", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var selected = _allMilitary.Where(x => x.IsSelected).ToList();
        if (selected.Count == 0)
        {
            SigfurDialog.Show(this, "Selecione ao menos um militar.", "SisBol", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            if (!App.Sisbol.IsReady)
            {
                SigfurDialog.Show(this,
                    "O SisBol não está preparado. Vá na janela principal, clique em ‘Preparar SisBol’, conclua o login/captcha e valide a sessão.",
                    "SisBol não preparado", MessageBoxButton.OK, MessageBoxImage.Information);
                StatusText.Text = "SisBol não preparado. Prepare na janela principal antes de enviar.";
                return;
            }
            StatusText.Text = "Enviando ao controlador central do SisBol…";
            var subject = string.IsNullOrWhiteSpace(Settings.SisbolSubject) ? "AJUSTE DE CONTAS" : Settings.SisbolSubject;
            var sisbolSubject = string.IsNullOrWhiteSpace(Settings.SisbolSpecificCode)
                ? subject
                : $"{Settings.SisbolSpecificCode.Trim()} - {subject}";
            await App.Sisbol.SendAsync(
                text,
                selected.Select(x => x.Military).ToList(),
                sisbolSubject,
                IncludeConsequencesCheck.IsChecked == true,
                ConsequencesTextBox.Text);
            StatusText.Text = "Nota enviada ao SisBol pelo módulo nativo em C#.";
        }
        catch (Exception ex)
        {
            CopyWord_Click(sender, e);
            SigfurDialog.Show(this,
                "Não foi possível concluir o envio automático. O texto foi copiado com a formatação para conferência manual.\n\n" + ex.Message,
                "SisBol", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Envio automático não concluído; texto copiado para uso manual.";
        }
    }

    private static bool SameRank(string? left, string? right)
    {
        var leftOrder = MilitaryRankService.GetOrder(left);
        var rightOrder = MilitaryRankService.GetOrder(right);
        if (leftOrder != 999 || rightOrder != 999) return leftOrder == rightOrder;
        return string.Equals(MilitaryRankService.Normalize(left), MilitaryRankService.Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseDecimal(string? text, out decimal value)
    {
        var cleaned = (text ?? string.Empty).Trim().Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.GetCultureInfo("pt-BR"), out value)
               || decimal.TryParse(cleaned.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }
}
