using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public enum AssistantIntentKind
{
    Unknown,
    SearchPerson,
    SearchBulletins,
    OpenBulletin,
    OpenPaystub,
    PrintPaystub,
    OpenWallet,
    OpenFolder,
    Route,
    TransportAidConference,
    VacationSearch,
    GratificationSearch,
    PersonFullSummary,
    GeneratedFiles,
    LegislationResearch,
    GeneralQuestion,
    Help
}

public sealed class AssistantIntent
{
    public AssistantIntentKind Kind { get; set; } = AssistantIntentKind.Unknown;
    public string Query { get; set; } = string.Empty;
    public string PersonTerm { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string DocumentKind { get; set; } = string.Empty;
    public string RequestedAction { get; set; } = string.Empty;
    public int Month { get; set; }
    public int Year { get; set; }
    public int BulletinNumber { get; set; }
    public int AdtNumber { get; set; }
    public double Confidence { get; set; }
    public bool WantsOpen { get; set; }
    public bool WantsPrint { get; set; }
    public bool WantsConference { get; set; }
}

public static partial class AssistantIntentDetector
{
    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["janeiro"] = 1, ["jan"] = 1,
        ["fevereiro"] = 2, ["fev"] = 2,
        ["marco"] = 3, ["março"] = 3, ["mar"] = 3,
        ["abril"] = 4, ["abr"] = 4,
        ["maio"] = 5, ["mai"] = 5,
        ["junho"] = 6, ["jun"] = 6,
        ["julho"] = 7, ["jul"] = 7,
        ["agosto"] = 8, ["ago"] = 8,
        ["setembro"] = 9, ["set"] = 9,
        ["outubro"] = 10, ["out"] = 10,
        ["novembro"] = 11, ["nov"] = 11,
        ["dezembro"] = 12, ["dez"] = 12
    };

    public static AssistantIntent Detect(string prompt)
    {
        var raw = prompt ?? string.Empty;
        var normalized = Normalize(raw);
        var intent = new AssistantIntent
        {
            Query = raw.Trim(),
            Month = DetectMonth(normalized),
            Year = DetectYear(raw),
            WantsOpen = ContainsAny(normalized, "abre", "abrir", "mostra", "mostrar", "visualiza", "visualizar"),
            WantsPrint = ContainsAny(normalized, "imprime", "imprimir", "impressao", "impressão"),
            WantsConference = ContainsAny(normalized, "confere", "conferir", "conferencia", "conferência", "bater", "batendo", "divergencia", "divergência")
        };

        intent.Subject = DetectSubject(normalized);
        intent.DocumentKind = DetectDocumentKind(normalized);
        intent.RequestedAction = DetectRequestedAction(normalized);
        intent.BulletinNumber = DetectNumberAfter(raw, @"\b(?:BI|B\.?I\.?|boletim(?:\s+interno)?)\s*(?:n[ºo°]?\s*)?(\d{1,4})");
        intent.AdtNumber = DetectNumberAfter(raw, @"\b(?:ADT|aditamento(?:\s+do\s+furriel)?)\s*(?:n[ºo°]?\s*)?(\d{1,4})");
        intent.PersonTerm = ExtractLikelyPersonTerm(raw, intent.Subject);
        intent.Confidence = string.IsNullOrWhiteSpace(intent.PersonTerm) ? 0.55 : 0.75;

        if (string.IsNullOrWhiteSpace(normalized) || ContainsAny(normalized, "ajuda", "o que voce faz", "o que você faz", "como funciona"))
            intent.Kind = AssistantIntentKind.Help;
        else if (ContainsAny(normalized, "tudo sobre", "resumo do militar", "o que tem salvo", "painel do militar", "dossie", "dossiê"))
            intent.Kind = AssistantIntentKind.PersonFullSummary;
        else if (ContainsAny(normalized, "rota", "maps", "mapa", "endereco", "endereço"))
            intent.Kind = AssistantIntentKind.Route;
        else if (intent.WantsPrint && ContainsAny(normalized, "contracheque", "cc ", "contra cheque", "contra-cheque"))
            intent.Kind = AssistantIntentKind.PrintPaystub;
        else if (ContainsAny(normalized, "contracheque", "cc ", "contra cheque", "contra-cheque", "ficha financeira"))
            intent.Kind = AssistantIntentKind.OpenPaystub;
        else if (ContainsAny(normalized, "carteira"))
            intent.Kind = AssistantIntentKind.OpenWallet;
        else if (ContainsAny(normalized, "pasta", "documentos salvos", "documento salvo"))
            intent.Kind = AssistantIntentKind.OpenFolder;
        else if (ContainsAny(normalized, "despesa a anular", "auxilio transporte", "auxílio transporte", "aux transporte"))
            intent.Kind = intent.WantsConference ? AssistantIntentKind.TransportAidConference : AssistantIntentKind.SearchBulletins;
        else if (ContainsAny(normalized, "ferias", "férias"))
            intent.Kind = AssistantIntentKind.VacationSearch;
        else if (ContainsAny(normalized, "gratificacao", "gratificação", "representacao", "representação"))
            intent.Kind = AssistantIntentKind.GratificationSearch;
        else if (ContainsAny(normalized, "boletim", " bi ", "adt", "aditamento", "furriel", "indice", "índice", "nota", "publicacao", "publicação"))
            intent.Kind = intent.WantsOpen ? AssistantIntentKind.OpenBulletin : AssistantIntentKind.SearchBulletins;
        else if (ContainsAny(normalized, "arquivo gerado", "relatorio", "relatório", "documento", "pdf") && !LooksLikeLegislationQuestion(normalized))
            intent.Kind = AssistantIntentKind.GeneratedFiles;
        else if (LooksLikeLegislationQuestion(normalized))
            intent.Kind = AssistantIntentKind.LegislationResearch;
        else if (LooksLikeGeneralQuestion(normalized))
            intent.Kind = AssistantIntentKind.GeneralQuestion;
        else
            intent.Kind = AssistantIntentKind.SearchPerson;

        return intent;
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return Regex.Replace(builder.ToString(), "\\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    public static string Digits(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : Regex.Replace(value, "\\D+", string.Empty, RegexOptions.CultureInvariant);

    public static bool ContainsAny(string normalized, params string[] terms)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        foreach (var term in terms)
        {
            var value = Normalize(term);
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (value.Contains(' ', StringComparison.Ordinal))
            {
                if (normalized.Contains(value, StringComparison.Ordinal)) return true;
            }
            else if (Regex.IsMatch(normalized, $"(^|\\s){Regex.Escape(value)}($|\\s)", RegexOptions.CultureInvariant))
            {
                return true;
            }
        }
        return false;
    }

    private static int DetectMonth(string normalized)
    {
        foreach (var token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Months.TryGetValue(token, out var month)) return month;
        return 0;
    }

    private static int DetectYear(string raw)
    {
        var match = Regex.Match(raw ?? string.Empty, @"\b(20\d{2}|\d{2})\b", RegexOptions.CultureInvariant);
        if (!match.Success) return 0;
        if (!int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)) return 0;
        if (year < 100) year += 2000;
        return year is >= 2000 and <= 2100 ? year : 0;
    }

    private static string DetectSubject(string normalized)
    {
        if (ContainsAny(normalized, "lei", "legislacao", "legislação", "portaria", "decreto", "norma", "amparo legal", "base legal", "fundamento")) return "Legislação";
        if (ContainsAny(normalized, "auxilio natalidade", "auxílio natalidade", "natalidade")) return "Auxílio-Natalidade";
        if (ContainsAny(normalized, "despesa a anular")) return "Despesa a Anular";
        if (ContainsAny(normalized, "auxilio transporte", "auxílio transporte", "aux transporte")) return "Auxílio-Transporte";
        if (ContainsAny(normalized, "ferias regulamentares", "férias regulamentares")) return "Férias Regulamentares";
        if (ContainsAny(normalized, "ferias", "férias")) return "Férias";
        if (ContainsAny(normalized, "gratificacao", "gratificação", "representacao", "representação")) return "Gratificação";
        if (ContainsAny(normalized, "contracheque", "contra cheque", "contra-cheque")) return "Contracheque";
        if (ContainsAny(normalized, "furriel")) return "Furriel";
        if (ContainsAny(normalized, "boletim", "bi", "nota", "aditamento")) return "Boletim";
        return string.Empty;
    }

    private static string DetectDocumentKind(string normalized)
    {
        if (ContainsAny(normalized, "contracheque", "contra cheque", "contra-cheque")) return "Contracheque";
        if (ContainsAny(normalized, "ficha financeira")) return "Ficha Financeira";
        if (ContainsAny(normalized, "adt", "aditamento", "furriel")) return "ADT Furriel";
        if (ContainsAny(normalized, "boletim", " bi ", "b i")) return "BI";
        if (ContainsAny(normalized, "carteira")) return "Carteira";
        return string.Empty;
    }

    private static string DetectRequestedAction(string normalized)
    {
        if (ContainsAny(normalized, "inclusao", "inclusão", "incluido", "incluído", "incluida", "incluída")) return "Inclusão";
        if (ContainsAny(normalized, "apresentacao", "apresentação")) return "Apresentação";
        if (ContainsAny(normalized, "concessao", "concessão")) return "Concessão";
        if (ContainsAny(normalized, "ordem de saque", "saque")) return "Ordem de Saque";
        if (ContainsAny(normalized, "despesa a anular", "anular")) return "Despesa a Anular";
        if (ContainsAny(normalized, "implantacao", "implantação")) return "Implantação";
        if (ContainsAny(normalized, "atualizacao", "atualização")) return "Atualização";
        return string.Empty;
    }

    private static int DetectNumberAfter(string raw, string pattern)
    {
        var match = Regex.Match(raw ?? string.Empty, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }


    private static bool LooksLikeLegislationQuestion(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        if (ContainsAny(normalized,
                "legislacao", "legislação", "lei", "decreto", "portaria", "norma", "artigo", "inciso",
                "amparo legal", "base legal", "fundamento legal", "fundamento", "direito", "faz jus",
                "quem tem direito", "como funciona", "pode receber", "deve receber", "recebe",
                "pagamento", "remuneracao", "remuneração", "soldo", "adicional", "auxilio", "auxílio",
                "gratificacao", "gratificação", "ferias", "férias", "fusex", "irrf", "pensão", "pensao"))
        {
            return !ContainsAny(normalized, "boletim", "adt", "aditamento", "nota", "carteira", "contracheque", "ficha financeira");
        }

        return false;
    }

    private static bool LooksLikeGeneralQuestion(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        if (ContainsAny(normalized, "qual", "quais", "como", "porque", "por que", "o que", "me explica", "explique", "resuma", "orientacao", "orientação"))
            return !ContainsAny(normalized, "boletim", "adt", "aditamento", "nota", "contracheque", "carteira", "pasta", "militar", "cpf", "prec", "identidade");
        return false;
    }

    private static string ExtractLikelyPersonTerm(string raw, string subject)
    {
        var text = Normalize(raw);
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var subjectTokens = Normalize(subject).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var ignored = new HashSet<string>(StringComparer.Ordinal)
        {
            "qual", "quais", "tem", "existe", "nota", "boletim", "bi", "adt", "aditamento", "furriel", "inclusao", "inclusão",
            "abrir", "abre", "mostra", "mostrar", "ultimo", "último", "contracheque", "ficha", "financeira", "pesquisar", "pesquisa",
            "ferias", "férias", "auxilio", "auxílio", "transporte", "natalidade", "despesa", "anular", "sobre", "do", "da", "de", "dos", "das",
            "subtenente", "sargento", "sgt", "soldado", "cabo", "tenente", "capitao", "capitão", "militar", "para", "pra", "no", "na"
        };
        foreach (var token in subjectTokens) ignored.Add(token);
        return string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length >= 3 && !ignored.Contains(x))
            .Take(6));
    }
}
