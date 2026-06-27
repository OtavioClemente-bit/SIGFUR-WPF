using System.Net;

namespace SIGFUR.Wpf.Services;

public static class SisbolTexts
{
    public const string BulletinConsequencesText =
        "Em consequência, solicito que o Ch SSPP/Cmdo 4ª RM tome conhecimento e adote as providências administrativas decorrentes.";

    public const string PaymentConsequencesText =
        "Em consequência, solicito que o Ch SSPP/Cmdo 4ª RM processe o direito remuneratório acima especificado.";

    public const string AuxTransportConsequencesText =
        "Em consequência, solicito que o Ch SSPP/Cmdo 4ª RM processe o lançamento referente ao Auxílio-Transporte acima especificado.";

    public const string AuxFoodConsequencesText =
        "Em consequência, solicito que o Ch SSPP/Cmdo 4ª RM processe o saque do Auxílio-Alimentação acima especificado.";

    public const string VacationConsequencesText =
        "Em consequência, solicito que a Seção de Pessoal atualize o Plano de Férias e que o Ch SSPP/Cmdo 4ª RM processe os direitos remuneratórios decorrentes, quando houver.";

    public const string JudicialPensionConsequencesText =
        "Em consequência, solicito que o Ch SSPP/Cmdo 4ª RM processe o lançamento da Pensão Judicial acima especificada, conforme documentação de amparo.";

    public const string GratificationConsequencesText =
        "Em consequência, solicito que o Ch SSPP/Cmdo 4ª RM processe o lançamento da Gratificação de Representação acima especificada.";

    public const string AdjustmentAccountsConsequencesText =
        "Em consequência, solicito que o Ch SSPP/Cmdo 4ª RM processe o Ajuste de Contas acima especificado, observadas as verbas devidas e os descontos regulamentares.";

    public static string ForSubject(string? subject)
    {
        var value = (subject ?? string.Empty).ToUpperInvariant();
        if (value.Contains("AUXILIO-TRANSPORTE") || value.Contains("AUXÍLIO-TRANSPORTE")) return AuxTransportConsequencesText;
        if (value.Contains("AUXILIO-ALIMENTA") || value.Contains("AUXÍLIO-ALIMENTA")) return AuxFoodConsequencesText;
        if (value.Contains("FERIAS") || value.Contains("FÉRIAS") || value.Contains("PLANO DE FERIAS") || value.Contains("PLANO DE FÉRIAS")) return VacationConsequencesText;
        if (value.Contains("PENSAO") || value.Contains("PENSÃO")) return JudicialPensionConsequencesText;
        if (value.Contains("GRATIFICA") || value.Contains("REPRESENTA")) return GratificationConsequencesText;
        if (value.Contains("AJUSTE DE CONTAS")) return AdjustmentAccountsConsequencesText;
        if (value.Contains("SIPPES") || value.Contains("ADICIONAL") || value.Contains("SAQUE") || value.Contains("ATRASADO") || value.Contains("DIFEREN") || value.Contains("PNR") || value.Contains("PAGAMENTO")) return PaymentConsequencesText;
        return BulletinConsequencesText;
    }

    public static string ForTemplate(string? templateName, string? templateText = null)
    {
        var extracted = ExtractTrailingConsequences(templateText);
        return string.IsNullOrWhiteSpace(extracted) ? ForSubject(templateName) : extracted;
    }

    public static string ExtractTrailingConsequences(string? text)
    {
        var value = (text ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var match = System.Text.RegularExpressions.Regex.Match(
            value,
            @"(?:^|\n+)\s*(Em\s+consequ(?:ê|e)ncia\b[\s\S]*)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!match.Success) return string.Empty;

        var consequence = match.Groups[1].Value.Trim();
        consequence = System.Text.RegularExpressions.Regex.Replace(consequence, @"[ \t]+", " ").Trim();
        return consequence;
    }

    public static string ToHtml(string text)
    {
        var encoded = WebUtility.HtmlEncode((text ?? string.Empty).Trim())
            .Replace("\r\n", "<br>", StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal);
        return $"<p style=\"font-family:'Times New Roman';font-size:10pt;line-height:normal;margin:0;\">{encoded}</p>";
    }
}
