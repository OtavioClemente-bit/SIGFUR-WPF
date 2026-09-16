namespace SIGFUR.Wpf.Models;

public sealed class SippesPersonnelRow
{
    public string MilitaryId { get; set; } = string.Empty;
    public string Cpf { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Rank { get; set; } = string.Empty;
    public string Om { get; set; } = string.Empty;
    public string ReportScript { get; set; } = string.Empty;
    public string ReportPdfPath { get; set; } = string.Empty;
    public string ReportDownloadStatus { get; set; } = string.Empty;
    public string PaymentStatus { get; set; } = string.Empty;

    public string FormattedCpf => MilitaryFormatting.FormatCpf(Cpf);
    public string ShortRank => MilitaryRankService.ShortName(Rank);
    public string Key => !string.IsNullOrWhiteSpace(MilitaryFormatting.Digits(Cpf))
        ? MilitaryFormatting.Digits(Cpf)
        : !string.IsNullOrWhiteSpace(MilitaryFormatting.Digits(MilitaryId))
            ? MilitaryFormatting.Digits(MilitaryId)
            : NormalizeName(Name);

    private static string NormalizeName(string? value)
        => string.Join(' ', (value ?? string.Empty).ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

public sealed class SippesPersonnelConferenceRow
{
    public string Status { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string SippesMilitaryId { get; set; } = string.Empty;
    public string SippesCpf { get; set; } = string.Empty;
    public string SippesName { get; set; } = string.Empty;
    public string SippesRank { get; set; } = string.Empty;
    public string SippesOm { get; set; } = string.Empty;
    public string SigfurSource { get; set; } = string.Empty;
    public string SigfurRank { get; set; } = string.Empty;
    public string SigfurName { get; set; } = string.Empty;
    public string SigfurWarName { get; set; } = string.Empty;
    public string SigfurCpf { get; set; } = string.Empty;
    public string SigfurMilitaryId { get; set; } = string.Empty;
    public string MatchKind { get; set; } = string.Empty;
    public string ReportScript { get; set; } = string.Empty;
    public string ReportPdfPath { get; set; } = string.Empty;
    public string ReportDownloadStatus { get; set; } = string.Empty;
    public string SippesPaymentStatus { get; set; } = string.Empty;
    public string PaymentAlert { get; set; } = string.Empty;
    public int SortPriority { get; set; }

    public string DisplayCpf => !string.IsNullOrWhiteSpace(SippesCpf)
        ? MilitaryFormatting.FormatCpf(SippesCpf)
        : MilitaryFormatting.FormatCpf(SigfurCpf);
    public string DisplayMilitaryId => !string.IsNullOrWhiteSpace(SippesMilitaryId) ? SippesMilitaryId : SigfurMilitaryId;
    public string DisplayRank => !string.IsNullOrWhiteSpace(SippesRank) ? SippesRank : SigfurRank;
    public string DisplayName => !string.IsNullOrWhiteSpace(SippesName) ? SippesName : SigfurName;
    public string DisplayPaymentStatus => string.IsNullOrWhiteSpace(SippesPaymentStatus) ? "Não baixado" : SippesPaymentStatus;
}

public sealed class SippesPersonnelConferenceSummary
{
    public DateTime GeneratedAt { get; set; } = DateTime.Now;
    public int SippesCount { get; set; }
    public int ActiveOkCount { get; set; }
    public int ReceivingButLicensedTransferredCount { get; set; }
    public int ReceivingOutsideSigfurCount { get; set; }
    public int ActiveMissingFromSippesCount { get; set; }
    public int NameOnlyMatchCount { get; set; }
    public int PaymentNormalCount { get; set; }
    public int PaymentSuspendedCount { get; set; }
    public int PaymentOtherCount { get; set; }

    public string Display =>
        $"SIPPES: {SippesCount} | Ativos OK: {ActiveOkCount} | No SIPPES e em Lic./Transf.: {ReceivingButLicensedTransferredCount} | No SIPPES e fora do SIGFUR: {ReceivingOutsideSigfurCount} | Ativos sem aparecer no SIPPES: {ActiveMissingFromSippesCount}";
}


public sealed class SippesPersonnelConferenceCache
{
    public DateTime SavedAt { get; set; } = DateTime.Now;
    public List<SippesPersonnelConferenceRow> Rows { get; set; } = [];
    public SippesPersonnelConferenceSummary Summary { get; set; } = new();
}
public sealed class SippesOmPaystubRubricValue
{
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Value { get; set; }

    public string DisplayValue => Value.ToString("C", CultureInfo.GetCultureInfo("pt-BR"));
    public string CompactText => $"{Code} {Description} {DisplayValue}".Trim();
}

public sealed class SippesOmPaystubMirrorPerson
{
    public string MilitaryId { get; set; } = string.Empty;
    public string Cpf { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Rank { get; set; } = string.Empty;
    public string PrecCp { get; set; } = string.Empty;
    public string Om { get; set; } = string.Empty;
    public string PaymentSheet { get; set; } = string.Empty;
    public string PaymentStatus { get; set; } = string.Empty;
    public decimal? NetValue { get; set; }
    public string SourceText { get; set; } = string.Empty;
    public List<SippesOmPaystubRubricValue> Rubrics { get; set; } = [];

    public string FormattedCpf => MilitaryFormatting.FormatCpf(Cpf);
    public string DisplayNetValue => NetValue.HasValue ? NetValue.Value.ToString("C", CultureInfo.GetCultureInfo("pt-BR")) : string.Empty;
    public string Key => !string.IsNullOrWhiteSpace(MilitaryFormatting.Digits(Cpf))
        ? MilitaryFormatting.Digits(Cpf)
        : !string.IsNullOrWhiteSpace(MilitaryFormatting.Digits(MilitaryId))
            ? MilitaryFormatting.Digits(MilitaryId)
            : NormalizeName(Name);

    private static string NormalizeName(string? value)
        => string.Join(' ', (value ?? string.Empty).ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

public sealed class SippesOmPaystubMirrorResult
{
    public int Year { get; set; }
    public int Month { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.Now;
    public string HtmlPath { get; set; } = string.Empty;
    public string TextPath { get; set; } = string.Empty;
    public List<SippesOmPaystubMirrorPerson> People { get; set; } = [];
}

public sealed class SippesOmPaystubMirrorConferenceRow
{
    public string Status { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string SippesRank { get; set; } = string.Empty;
    public string SippesName { get; set; } = string.Empty;
    public string SippesCpf { get; set; } = string.Empty;
    public string SippesMilitaryId { get; set; } = string.Empty;
    public string SippesPrecCp { get; set; } = string.Empty;
    public string SippesOm { get; set; } = string.Empty;
    public string PaymentStatus { get; set; } = string.Empty;
    public string NetValue { get; set; } = string.Empty;
    public string SigfurRank { get; set; } = string.Empty;
    public string SigfurName { get; set; } = string.Empty;
    public string SigfurWarName { get; set; } = string.Empty;
    public string SigfurCpf { get; set; } = string.Empty;
    public string SigfurMilitaryId { get; set; } = string.Empty;
    public string MatchKind { get; set; } = string.Empty;
    public string SavedPaystubPath { get; set; } = string.Empty;
    public string SavedPaystubStatus { get; set; } = string.Empty;
    public string SippesDadosMaPath { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string SourceText { get; set; } = string.Empty;
    public string MirrorRubricsText { get; set; } = string.Empty;
    public string PaystubRubricsText { get; set; } = string.Empty;
    public string ValueCheckStatus { get; set; } = "Ainda não conferido";
    public int SortPriority { get; set; }

    public string DisplayRank => !string.IsNullOrWhiteSpace(SippesRank) ? SippesRank : SigfurRank;
    public string ShortDisplayRank => MilitaryRankService.ShortName(DisplayRank);
    public string DisplayName => !string.IsNullOrWhiteSpace(SippesName) ? SippesName : SigfurName;
    public string DisplayCpf => !string.IsNullOrWhiteSpace(SippesCpf) ? MilitaryFormatting.FormatCpf(SippesCpf) : MilitaryFormatting.FormatCpf(SigfurCpf);
    public string DisplayMilitaryId => !string.IsNullOrWhiteSpace(SippesMilitaryId) ? SippesMilitaryId : SigfurMilitaryId;
    public string DisplayWarName => SigfurWarName;
    public string OpenPaystubText => !string.IsNullOrWhiteSpace(SavedPaystubPath) && File.Exists(SavedPaystubPath) ? "Abrir" : "—";
    public string DisplayValueCheckStatus => FormatMultiline(ValueCheckStatus);
    public string DisplayMirrorRubricsText => string.IsNullOrWhiteSpace(MirrorRubricsText) ? "—" : FormatMultiline(MirrorRubricsText);
    public string DisplayPaystubRubricsText => string.IsNullOrWhiteSpace(PaystubRubricsText) ? "—" : FormatMultiline(PaystubRubricsText);
    public string ValueCheckPreview => Preview(DisplayValueCheckStatus, maxLines: 3, maxChars: 220);
    public string MirrorRubricsPreview => Preview(DisplayMirrorRubricsText, maxLines: 3, maxChars: 260);
    public string PaystubRubricsPreview => Preview(DisplayPaystubRubricsText, maxLines: 3, maxChars: 260);
    public string ValueCheckCompact => Compact(ValueCheckStatus, 70);
    public string MirrorRubricsCompact => Compact(MirrorRubricsText, 55);
    public string PaystubRubricsCompact => Compact(PaystubRubricsText, 55);

    private static string FormatMultiline(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "—";
        var text = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*;\s*", Environment.NewLine, System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", Environment.NewLine + Environment.NewLine, System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return text;
    }

    private static string Preview(string? value, int maxLines, int maxChars)
    {
        var text = FormatMultiline(value);
        if (text == "—") return text;
        var lines = text.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
        var preview = string.Join(Environment.NewLine, lines.Take(Math.Max(1, maxLines)));
        if (preview.Length > maxChars) preview = preview[..Math.Max(1, maxChars)].TrimEnd() + "…";
        if (lines.Length > maxLines || text.Length > preview.Length) preview += Environment.NewLine + "(selecione a linha para ver completo)";
        return preview;
    }

    private static string Compact(string? value, int maxChars)
    {
        var text = FormatMultiline(value)
            .Replace(Environment.NewLine, " · ", StringComparison.Ordinal)
            .Trim();
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return text.Length <= maxChars ? text : text[..Math.Max(1, maxChars)].TrimEnd() + "…";
    }
}



public sealed class SippesOmPaystubMirrorSummary
{
    public DateTime GeneratedAt { get; set; } = DateTime.Now;
    public int SippesCount { get; set; }
    public int ActiveMatchedCount { get; set; }
    public int OutsideActiveCount { get; set; }
    public int SavedPaystubFoundCount { get; set; }
    public int SavedPaystubMissingCount { get; set; }
    public int ActiveMissingFromMirrorCount { get; set; }
    public string HtmlPath { get; set; } = string.Empty;

    public string Display =>
        $"Espelho OM: {SippesCount} | Ativos conferidos: {ActiveMatchedCount} | Fora dos ativos: {OutsideActiveCount} | Contracheque salvo OK: {SavedPaystubFoundCount} | Sem contracheque salvo: {SavedPaystubMissingCount} | Ativos sem aparecer no espelho: {ActiveMissingFromMirrorCount}";
}

public sealed class SippesOmPaystubMirrorCache
{
    public DateTime SavedAt { get; set; } = DateTime.Now;
    public int Year { get; set; }
    public int Month { get; set; }
    public List<SippesOmPaystubMirrorConferenceRow> Rows { get; set; } = [];
    public SippesOmPaystubMirrorSummary Summary { get; set; } = new();
}
