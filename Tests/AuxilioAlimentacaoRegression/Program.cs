using System.Runtime.CompilerServices;
using System.Text.Json;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views.Bulletin;

static void Equal(string expected, string? actual, string scenario)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
        throw new InvalidOperationException($"{scenario}: esperado '{expected}', obtido '{actual ?? "<nulo>"}'.");
}

static void False(bool condition, string scenario)
{
    if (condition) throw new InvalidOperationException(scenario);
}

static Dictionary<string, string> Values(params (string Key, string Value)[] values)
    => values.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);

var tipos = new[]
{
    (AuxilioAlimentacaoTipoLancamento.Normal, "NR"),
    (AuxilioAlimentacaoTipoLancamento.Atrasado, "AR"),
    (AuxilioAlimentacaoTipoLancamento.Diferenca, "FR"),
    (AuxilioAlimentacaoTipoLancamento.Devolucao, "DR")
};
foreach (var (tipo, prefixo) in tipos)
{
    Equal(prefixo + "0058", AuxilioAlimentacaoRubricRule.Resolve(1, tipo), $"comum/B {tipo}");
    Equal(prefixo + "0052", AuxilioAlimentacaoRubricRule.Resolve(5, tipo), $"5X {tipo}");
    Equal(prefixo + "0053", AuxilioAlimentacaoRubricRule.Resolve(10, tipo), $"10X {tipo}");
}

var service = (BulletinService)RuntimeHelpers.GetUninitializedObject(typeof(BulletinService));

var prmMixed = service.EnrichFormValues("AUXÍLIO-ALIMENTAÇÃO - Ordem de Saque (PRM)", Values(
    ("MES_REFERENCIA", "03/2026"), ("MES_PAGAMENTO", "04/2026"),
    ("QTD_DIAS_PRM_COMUM", "2"), ("DIAS_PRM_COMUM", "2 e 4"),
    ("QTD_DIAS_PRM_5X", "2"), ("DIAS_PRM_5X", "6 e 8"),
    ("VALOR_ETAPA_COMUM", "13,50")));
Equal("AR0058", prmMixed["CODIGO_PRM_COMUM"], "PRM comum atrasada");
Equal("AR0052", prmMixed["CODIGO_PRM_5X"], "PRM 5X atrasada");
Equal("AR0058 + AR0052", prmMixed["CODIGO_PRM"], "resumo PRM comum + 5X");
Equal("27,00", prmMixed["VALOR_PRM_COMUM"], "valor PRM comum");
Equal("135,00", prmMixed["VALOR_PRM_5X"], "valor PRM 5X");

var tenLate = service.EnrichFormValues("AUXÍLIO-ALIMENTAÇÃO - Ordem de Saque", Values(
    ("MES_REFERENCIA", "03/2026"), ("MES_PAGAMENTO", "04/2026"),
    ("QTD_DIAS_10X", "1"), ("DIAS_10X", "7")));
Equal("AR0053", tenLate["CODIGO_10X"], "10X atrasada");
Equal("AR0053", tenLate["CODIGO_SAQUE"], "resumo 10X atrasada");

var fiveNormal = service.EnrichFormValues("AUXÍLIO-ALIMENTAÇÃO - Ordem de Saque", Values(
    ("MES_REFERENCIA", "04/2026"), ("MES_PAGAMENTO", "04/2026"),
    ("QTD_DIAS_5X", "1"), ("DIAS_5X", "9")));
Equal("NR0052", fiveNormal["CODIGO_5X"], "5X normal");

var driverLate = service.EnrichFormValues("AUXÍLIO-ALIMENTAÇÃO - Ordem de Saque (Motorista do Cmt)", Values(
    ("MES_REFERENCIA", "03/2026"), ("MES_PAGAMENTO", "04/2026"),
    ("QTD_DIAS_5X", "1"), ("DIAS_5X", "10")));
Equal("AR0052", driverLate["CODIGO_5X"], "motorista do Comandante 5X atrasada");

var inconsistentLate = service.EnrichFormValues("AUXÍLIO-ALIMENTAÇÃO - Saque de Atrasado", Values(
    ("MES_REFERENCIA", "04/2026"), ("MES_SOLICITACAO_ATRASADO", "04/2026"),
    ("QTD_DIAS_5X", "1"), ("DIAS_5X", "10")));
False(inconsistentLate.ContainsKey("CODIGO_5X"), "Atrasado no mesmo mês não pode receber AR automaticamente.");

var templatePath = Path.Combine(Directory.GetCurrentDirectory(), "Resources", "Boletim", "boletins_padrao.json");
var templates = File.ReadAllText(templatePath);
False(templates.Contains("AR0053/5x", StringComparison.OrdinalIgnoreCase), "Template não pode associar AR0053 a 5X.");
False(templates.Contains("AR0052/10x", StringComparison.OrdinalIgnoreCase), "Template não pode associar AR0052 a 10X.");
False(templates.Contains("AR0048", StringComparison.OrdinalIgnoreCase)
      || templates.Contains("AR0043", StringComparison.OrdinalIgnoreCase)
      || templates.Contains("AR0042", StringComparison.OrdinalIgnoreCase),
    "Templates não podem reintroduzir as rubricas antigas 0048/0043/0042.");

var templateMap = JsonSerializer.Deserialize<Dictionary<string, string>>(templates)
                  ?? throw new InvalidOperationException("Não foi possível ler os templates de boletim.");
var prmTemplateName = "AUXÍLIO-ALIMENTAÇÃO - Ordem de Saque (PRM)";
var military = new MilitaryRecord
{
    Id = 1, Rank = "3º Sargento", Name = "MILITAR TESTE", WarName = "TESTE",
    PrecCp = "123456789", Cpf = "12345678901"
};
var renderedPrm = service.Render(
    new BulletinTemplate { Name = prmTemplateName, Text = templateMap[prmTemplateName] },
    [military],
    Values(("MES_REFERENCIA", "03/2026"), ("MES_PAGAMENTO", "04/2026"),
        ("DIEX_NUMERO", "1"), ("DIEX_DATA", "01/04/2026"), ("VALOR_ETAPA_COMUM", "13,50")),
    new Dictionary<string, string>(),
    new Dictionary<int, Dictionary<string, string>>
    {
        [military.Id] = Values(
            ("QTD_DIAS_PRM_COMUM", "2"), ("DIAS_PRM_COMUM", "2 e 4"),
            ("FUNDAMENTO_AR0058", "Art 68 do Dec Nr 4.307/02"),
            ("QTD_DIAS_PRM_5X", "2"), ("DIAS_PRM_5X", "6 e 8"))
    });
if (!renderedPrm.Text.Contains("Etapa comum (AR0058/1x)", StringComparison.Ordinal))
    throw new InvalidOperationException("Saída PRM não gerou a etapa comum AR0058 separadamente.");
if (!renderedPrm.Text.Contains("Etapa x5 (AR0052/5x)", StringComparison.Ordinal))
    throw new InvalidOperationException("Saída PRM não gerou a etapa 5X AR0052 separadamente.");
if (!renderedPrm.Text.Contains("Código(s): AR0058 + AR0052", StringComparison.Ordinal))
    throw new InvalidOperationException("Resumo PRM não montou AR0058 + AR0052.");
if (renderedPrm.Text.Contains("AR0053", StringComparison.Ordinal))
    throw new InvalidOperationException("Saída PRM comum + 5X não pode conter AR0053.");

var renderedPrmUiMonths = service.Render(
    new BulletinTemplate { Name = prmTemplateName, Text = templateMap[prmTemplateName] },
    [military],
    Values(("MES_REFERENCIA", "JUL 26"), ("MES_PAGAMENTO", "AGO 26"),
        ("DIEX_NUMERO", "123-ECM/BH/PRM 04/001/PRM Gu"), ("DIEX_DATA", "30/07/2026"),
        ("VALOR_ETAPA_COMUM", "13,50")),
    new Dictionary<string, string>(),
    new Dictionary<int, Dictionary<string, string>>
    {
        [military.Id] = Values(
            ("QTD_DIAS_PRM_COMUM", "9"), ("DIAS_PRM_COMUM", "7, 8, 15, 21, 29 JUL 26"),
            ("FUNDAMENTO_AR0058", "Art 68 do Dec Nr 4.307/02"),
            ("QTD_DIAS_PRM_5X", "10"), ("DIAS_PRM_5X", "1, 2, 6, 9, 13, 16, 22, 23, 27 e 30 JUL 26"))
    });
if (renderedPrmUiMonths.UnresolvedTokens.Any(token => token.Contains("CODIGO", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("A prévia PRM deixou token de rubrica sem resolver para os meses exibidos pela interface: " + string.Join(", ", renderedPrmUiMonths.UnresolvedTokens));
if (!renderedPrmUiMonths.Text.Contains("AR0058 + AR0052", StringComparison.Ordinal))
    throw new InvalidOperationException("A prévia PRM da interface não gerou o resumo AR0058 + AR0052.");

var zeroDaysOverride = service.Render(
    new BulletinTemplate { Name = prmTemplateName, Text = templateMap[prmTemplateName] },
    [military],
    Values(("MES_REFERENCIA", "JUL 26"), ("MES_PAGAMENTO", "AGO 26"),
        ("DIEX_NUMERO", "123"), ("DIEX_DATA", "30/07/2026"), ("VALOR_ETAPA_COMUM", "13,50")),
    new Dictionary<string, string>(),
    new Dictionary<int, Dictionary<string, string>>
    {
        [military.Id] = Values(
            ("QTD_DIAS_PRM_COMUM", "2"), ("DIAS_PRM_COMUM", "7 e 8 JUL 26"),
            ("FUNDAMENTO_AR0058", "Art 68 do Dec Nr 4.307/02"),
            ("QTD_DIAS_PRM_5X", "0"), ("DIAS_PRM_5X", "1, 2 e 3 JUL 26"))
    });
if (zeroDaysOverride.Text.Contains("Etapa x5", StringComparison.OrdinalIgnoreCase)
    || zeroDaysOverride.Text.Contains("AR0052", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Quantidade 0 deve dispensar os dias e suprimir integralmente a etapa 5X.");
if (zeroDaysOverride.UnresolvedTokens.Any(token => token.Contains("DIAS_PRM_5X", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("Quantidade 0 não pode deixar os dias da etapa dispensada como pendência.");

var zeroDaysEnriched = service.EnrichFormValues(prmTemplateName, Values(
    ("MES_REFERENCIA", "JUL 26"), ("MES_PAGAMENTO", "AGO 26"),
    ("QTD_DIAS_PRM_10X", "0"), ("DIAS_PRM_10X", "3, 4 e 5 JUL 26")));
Equal(string.Empty, zeroDaysEnriched["DIAS_PRM_10X"], "quantidade 0 limpa dias antigos da etapa 10X");
Equal("00", zeroDaysEnriched["QTD_DIAS_PRM_10X"], "quantidade 0 permanece explícita na etapa 10X");

Equal("None", BulletinPublicationFieldClassifier.Classify("MES_REFERENCIA").ToString(), "mês de referência não é publicação");
Equal("None", BulletinPublicationFieldClassifier.Classify("ANO_REFERENCIA").ToString(), "ano de referência não é publicação");
Equal("Generic", BulletinPublicationFieldClassifier.Classify("DOC_REFERENCIA").ToString(), "documento de referência aceita BI ou Adt Furr");
Equal("Generic", BulletinPublicationFieldClassifier.Classify("DOC_AMPARO").ToString(), "documento de amparo aceita publicação salva");
Equal("Bi", BulletinPublicationFieldClassifier.Classify("BI_REFERENCIA").ToString(), "referência de BI filtra Boletim Interno");
Equal("None", BulletinPublicationFieldClassifier.Classify("BOLETIM_ORGAO_FORMADOR").ToString(), "boletim do órgão formador não usa publicação local");

var savedBi = new SavedBulletinReference { Kind = "Boletim Interno", Number = "BI Nr 123/2026", Date = "14/04/2026" };
Equal("BI Nr 123, de 14 ABR 26, da 4ª Cia PE", savedBi.ReferenceText, "formatação completa do BI salvo");
var savedAdt = new SavedBulletinReference { Kind = "Aditamento do Furriel", Number = "Adt Furr Nr 45/26", Bar = "BAR Nr 7", Date = "14/04/2026" };
Equal("Adt Furr Nr 45 BAR 7, de 14 ABR 26, da 4ª Cia PE", savedAdt.ReferenceText, "formatação completa do Adt Furr salvo");

Console.WriteLine("Boletim e Auxílio-Alimentação: 43 verificações de regressão aprovadas.");
