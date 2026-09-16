namespace SIGFUR.Wpf.Models;

public sealed record GratificationActivityOption(
    string Nature,
    string SuggestedDescription,
    string LegalBasis,
    string Group,
    DateTime? EffectiveFrom = null);

public static class GratificationActivityCatalog
{
    private const string Decree = "Decreto nº 11.002, de 17 MAR 2022";

    public static IReadOnlyList<GratificationActivityOption> Options { get; } =
    [
        new("Viagem de representação — evento militar", "Representação institucional em evento de natureza militar", $"Art. 5º, inciso I, do {Decree}.", "Viagem de representação"),
        new("Viagem de representação — evento civil", "Representação institucional em evento de natureza civil", $"Art. 5º, inciso I, do {Decree}.", "Viagem de representação"),
        new("Viagem de representação — evento cultural", "Representação institucional em evento de cunho cultural", $"Art. 5º, inciso I, do {Decree}.", "Viagem de representação"),
        new("Viagem de representação — evento desportivo", "Representação institucional em evento de cunho desportivo", $"Art. 5º, inciso I, do {Decree}.", "Viagem de representação"),

        new("Viagem de instrução — ensino", "Participação, fora da sede, em atividade relacionada ao ensino", $"Art. 5º, inciso II, do {Decree}.", "Viagem de instrução"),
        new("Viagem de instrução — instrução", "Participação, fora da sede, em atividade de instrução", $"Art. 5º, inciso II, do {Decree}.", "Viagem de instrução"),
        new("Viagem de instrução — orientação técnica", "Participação, fora da sede, em atividade de orientação técnica", $"Art. 5º, inciso II, do {Decree}.", "Viagem de instrução"),
        new("Viagem de instrução — inspeção de comando", "Participação, fora da sede, em inspeção de comando", $"Art. 5º, inciso II, do {Decree}.", "Viagem de instrução"),

        new("Às ordens de autoridade estrangeira no País", "Permanência às ordens de autoridade estrangeira no País", $"Art. 4º, inciso II, alínea e, do {Decree}.", "Autoridade estrangeira"),

        new("Emprego operacional — operação real", "Participação em operação real estabelecida para fins administrativos, operacionais ou logísticos", $"Art. 5º, inciso III, alínea a, do {Decree}.", "Emprego operacional"),
        new("Emprego operacional — adestramento", "Participação em operação de adestramento estabelecida para fins administrativos, operacionais ou logísticos", $"Art. 5º, inciso III, alínea a, do {Decree}.", "Emprego operacional"),
        new("Emprego operacional — vigilância de fronteira", "Participação em ação militar de vigilância de fronteira", $"Art. 5º, inciso III, alínea b, do {Decree}.", "Emprego operacional"),
        new("Emprego operacional — garantia da lei e da ordem", "Participação em operação de Garantia da Lei e da Ordem", $"Art. 5º, inciso III, alínea c, do {Decree}.", "Emprego operacional"),
        new("Emprego operacional — atribuição subsidiária das Forças Armadas", "Participação em ação relacionada às atribuições subsidiárias das Forças Armadas", $"Art. 5º, inciso III, alínea d, do {Decree}.", "Emprego operacional"),
        new("Emprego operacional — adestramento para missão de paz", "Adestramento para participação em missão de paz", $"Art. 5º, inciso III, alínea e, do {Decree}.", "Emprego operacional"),
        new("Emprego operacional — serviço de engenharia fora da sede", "Execução, fora da sede, de serviço de engenharia", $"Art. 5º, inciso III, alínea f, item 1, do {Decree}.", "Emprego operacional"),
        new("Emprego operacional — serviço de cartografia fora da sede", "Execução, fora da sede, de serviço de cartografia", $"Art. 5º, inciso III, alínea f, item 2, do {Decree}.", "Emprego operacional"),
        new("Emprego operacional — levantamento topográfico fora da sede", "Execução, fora da sede, de levantamento topográfico", $"Art. 5º, inciso III, alínea f, item 3, do {Decree}.", "Emprego operacional"),
        new("Emprego operacional — escolta fora da sede", "Escolta de material Classe V", $"Art. 5º, inciso III, alínea f, item 4, do {Decree}.", "Emprego operacional"),
        new("Emprego operacional — perícia fora da sede", "Realização, fora da sede, de atividade pericial", $"Art. 5º, inciso III, alínea f, item 5, do {Decree}.", "Emprego operacional"),
        new("Emprego operacional — produção de geoinformação fora da sede", "Produção, fora da sede, de geoinformação", $"Art. 5º, inciso III, alínea f, item 6, do {Decree}.", "Emprego operacional"),
        new("Emprego operacional — infraestrutura de tecnologia de comunicações", "Implantação ou manutenção, fora da sede, de infraestrutura de tecnologia de comunicações", $"Art. 5º, inciso III, alínea f, item 7, do {Decree}.", "Emprego operacional"),
        new("Emprego operacional — avaliação de sistemas, materiais ou produtos de defesa", "Avaliação, fora da sede, de sistema, material de emprego militar ou produto de defesa", $"Art. 5º, inciso III, alínea f, item 8, do {Decree}, com redação dada pelo Decreto nº 13.052, de 3 JUL 2026.", "Emprego operacional", new DateTime(2026, 7, 3)),
        new("Emprego operacional — atividade de manutenção fora da sede", "Execução, fora da sede, de atividade relacionada à manutenção", $"Art. 5º, inciso III, alínea f, item 9, do {Decree}, com redação dada pelo Decreto nº 13.052, de 3 JUL 2026.", "Emprego operacional", new DateTime(2026, 7, 3)),
        new("Emprego operacional — serviço de transporte fora da sede", "Busca, recebimento e transporte de material Classe V", $"Art. 5º, inciso III, alínea f, item 10, do {Decree}, incluído pelo Decreto nº 13.052, de 3 JUL 2026.", "Emprego operacional", new DateTime(2026, 7, 3))
    ];

    public static IReadOnlyList<string> NatureOptions { get; } = Options
        .Select(option => option.Nature)
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public static IReadOnlyList<string> DescriptionOptions { get; } = Options
        .Select(option => option.SuggestedDescription)
        .Concat([
            "Participação em curso de formação",
            "Participação em curso, estágio ou capacitação",
            "Participação em exercício de adestramento",
            "Escolta de material Classe V",
            "Escolta de comboio ou carga de interesse militar",
            "Busca, recebimento e transporte de material Classe V",
            "Transporte de material de emprego militar",
            "Recebimento e recolhimento de material de emprego militar"
        ])
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public static GratificationActivityOption? FindByNature(string? nature)
        => Options.FirstOrDefault(option => option.Nature.Equals(nature?.Trim(), StringComparison.CurrentCultureIgnoreCase));
}
