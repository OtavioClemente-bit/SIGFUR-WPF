namespace SIGFUR.Wpf.Models;

public sealed class SisbolMatterPayload
{
    public string GeneralSubject { get; set; } = "PAGAMENTO PESSOAL";
    public string SpecificSubject { get; set; } = string.Empty;
    public string OpeningTextPlain { get; set; } = string.Empty;
    public string OpeningTextHtml { get; set; } = string.Empty;
    public string ClosingTextPlain { get; set; } = string.Empty;
    public string ClosingTextHtml { get; set; } = string.Empty;
    public bool IncludeConsequences { get; set; } = true;
}
