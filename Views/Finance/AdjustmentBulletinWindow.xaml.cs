using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Finance;

public partial class AdjustmentBulletinWindow : Window
{
    private readonly IReadOnlyList<AdjustmentRubric> _rubrics;
    private readonly ObservableCollection<BulletinMilitarySelection> _allMilitary = [];
    public ObservableCollection<BulletinMilitarySelection> VisibleMilitary { get; } = [];
    public AdjustmentAccountsSettings Settings { get; }

    private static readonly string[] Reasons =
    [
        "Licenciamento ex officio — término de prorrogação/tempo de serviço",
        "Licenciamento ex officio — conveniência do serviço",
        "Licenciamento ex officio",
        "Licenciamento a pedido",
        "Exclusão de incorporação",
        "Anulação de incorporação",
        "Desincorporação",
        "Exclusão a bem da disciplina",
        "Licenciamento a bem da disciplina",
        "Deserção",
        "Falecimento",
        "Revogação",
        "Outros / editar manualmente"
    ];

    public AdjustmentBulletinWindow(
        AdjustmentAccountsSettings settings,
        IReadOnlyList<AdjustmentRubric> rubrics,
        IReadOnlyList<MilitaryRecord> military)
    {
        InitializeComponent();
        App.UiState.Attach(this);
        Settings = settings;
        _rubrics = rubrics;
        DataContext = this;

        foreach (var item in military
                     .Where(x => SameRank(x.Rank, Settings.Rank))
                     .OrderBy(x => x, Comparer<MilitaryRecord>.Create((a, b) => MilitaryRankService.Compare(a.Rank, a.Name, b.Rank, b.Name))))
            _allMilitary.Add(new BulletinMilitarySelection { Military = item, IsSelected = false });

        MilitaryFilterText.Text = $"Cálculo vinculado a {Settings.Rank}. Apenas militares desse mesmo posto/graduação podem ser selecionados ({_allMilitary.Count:N0} encontrado(s)).";
        RefreshVisibleMilitary();
        ReasonBox.ItemsSource = Reasons;
        LoadSettings();
        Loaded += (_, _) =>
            StatusText.Text = $"Escolha os militares de {Settings.Rank} e clique em Gerar / atualizar.";
        Closing += (_, _) => ReadSettings();
    }

    private void LoadSettings()
    {
        OrganizationBox.Text = Settings.Organization;
        BulletinNumberBox.Text = Settings.BulletinNumber;
        BulletinDateBox.Text = Settings.BulletinDate;
        CutoffDateBox.Text = Settings.CutoffDate;
        ReasonBox.Text = Settings.BulletinReason;
        IntroductionBox.Text = string.IsNullOrWhiteSpace(Settings.BulletinIntroduction)
            ? AdjustmentAccountsService.BulletinIntroductionForReason(Settings.BulletinReason)
            : Settings.BulletinIntroduction;
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
        Settings.CutoffDate = CutoffDateBox.Text.Trim();
        Settings.BulletinReason = ReasonBox.Text.Trim();
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

    private void ReasonBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var reason = ReasonBox.Text.Trim();
            IntroductionBox.Text = AdjustmentAccountsService.BulletinIntroductionForReason(reason);
        }));
    }

    private void MilitarySearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshVisibleMilitary();

    private void RefreshVisibleMilitary()
    {
        var query = MilitaryRankService.Normalize(MilitarySearchBox?.Text);
        VisibleMilitary.Clear();
        foreach (var item in _allMilitary)
        {
            var haystack = MilitaryRankService.Normalize($"{item.Rank} {item.ShortRank} {item.Name} {item.WarName} {item.PrecCp} {item.Cpf}");
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

    private Task BuildDocumentAsync()
    {
        ReadSettings();
        var selected = _allMilitary
            .Where(x => x.IsSelected && SameRank(x.Rank, Settings.Rank))
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
            // O cálculo do boletim deve ser exatamente o mesmo ajuste feito na janela principal.
            // O militar fornece apenas a identificação textual; posto, soldo, direitos e descontos
            // permanecem vinculados às opções já calculadas.
            var result = AdjustmentAccountsService.Calculate(settings, _rubrics);
            AppendMilitarySection(document, military, settings, result, index + 1, selected.Count);
            document.Blocks.Add(BlankLine());
        }

        PreviewBox.Document = document;
        StatusText.Text = $"Boletim gerado para {selected.Count:N0} militar(es) de {Settings.Rank}, usando exatamente os valores do ajuste.";
        return Task.CompletedTask;
    }


    private void AppendMilitarySection(FlowDocument document, MilitaryRecord military, AdjustmentAccountsSettings settings, AdjustmentCalculationResult result, int number, int total)
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

        if (settings.BulletinIncludeEarnings)
            AppendRubricGroup(document, "a. Rendimentos", result.Rows.Where(IsEarning), military, settings);
        if (settings.BulletinIncludeDiscounts)
            AppendRubricGroup(document, "b. Descontos", result.Rows.Where(x => !IsEarning(x)), military, settings);

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

    private static bool IsEarning(AdjustmentRubric row) => row.Reference != "D" && row.Sign != "-";

    private static Paragraph NewParagraph() => new() { Margin = new Thickness(0, 0, 0, 8), LineHeight = 21 };

    private static Paragraph BlankLine() => new(new Run(string.Empty))
    {
        Margin = new Thickness(0, 5, 0, 5),
        FontFamily = new FontFamily(BulletinTextFormatter.StandardFontFamily),
        FontSize = BulletinTextFormatter.StandardWpfFontSize
    };

    private static void AppendRubricGroup(FlowDocument document, string title, IEnumerable<AdjustmentRubric> source, MilitaryRecord military, AdjustmentAccountsSettings settings)
    {
        var rows = source.Where(x => x.IsIncluded && (!settings.BulletinHideZeroLines || x.ProportionalValue != 0m)).ToList();
        if (rows.Count == 0) return;
        document.Blocks.Add(new Paragraph(new Bold(new Run(title))) { Margin = new Thickness(0, 5, 0, 4) });
        foreach (var row in rows)
        {
            var description = AdjustmentAccountsService.ProfessionalDescription(row.Description, row.Code, settings.BulletinSimplifyDescriptions);
            var prefix = settings.BulletinShowCodes ? $"{row.Code} — " : string.Empty;
            var suffix = ReferenceSuffix(row, settings);
            var paragraph = NewParagraph();
            paragraph.Margin = new Thickness(22, 0, 0, 4);
            paragraph.Inlines.Add(new Run($"- {prefix}{description}{suffix}: {AdjustmentAccountsService.FormatMoney(row.ProportionalValue)};"));
            document.Blocks.Add(paragraph);
        }
    }

    private static string ReferenceSuffix(AdjustmentRubric row, AdjustmentAccountsSettings settings)
        => row.Kind switch
        {
            "FORM_FER_PROP" or "FORM_FER_NGOZ" => $" — PERÍODO AQUISITIVO {settings.VacationReferenceYear} ({settings.VacationMonths}/12 AVOS TRABALHADOS)",
            "FORM_NAT_TOTAL" or "FORM_NAT_1P" or "FORM_NAT_DESC1" => $" — ANO CIVIL {settings.ChristmasReferenceYear} ({settings.ChristmasMonths}/12 AVOS)",
            _ when row.Base == "DIA" => $" ({settings.ServedDays}/{settings.DaysInMonth} DIAS)",
            _ => string.Empty
        };

    private static void AppendTextWithWarName(Paragraph paragraph, string text, MilitaryRecord military)
    {
        var war = (military.WarName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(war))
        {
            paragraph.Inlines.Add(new Run(text));
            return;
        }
        var start = 0;
        while (start < text.Length)
        {
            var index = text.IndexOf(war, start, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0)
            {
                paragraph.Inlines.Add(new Run(text[start..]));
                break;
            }
            if (index > start) paragraph.Inlines.Add(new Run(text[start..index]));
            paragraph.Inlines.Add(new Bold(new Run(text.Substring(index, war.Length))));
            start = index + war.Length;
        }
        if (text.Length == 0) paragraph.Inlines.Add(new Run(string.Empty));
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
        StatusText.Text = "RTF salvo em " + dialog.FileName;
    }

    private void SaveText_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Texto (*.txt)|*.txt", FileName = "boletim_ajuste_contas.txt" };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllText(dialog.FileName, new TextRange(PreviewBox.Document.ContentStart, PreviewBox.Document.ContentEnd).Text.TrimEnd(), Encoding.UTF8);
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
