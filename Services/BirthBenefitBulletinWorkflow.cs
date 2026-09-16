namespace SIGFUR.Wpf.Services;

public static class BirthBenefitBulletinWorkflow
{
    public const string EconomicDependency = "DEPENDÊNCIA ECONÔMICA - Implantação";
    public const string NatalityAid = "AUXÍLIO-NATALIDADE - Ordem de Saque";
    public const string PreSchool = "ASSISTÊNCIA PRÉ-ESCOLAR - Implantação";
    public const string NatalityAidLate = "AUXÍLIO-NATALIDADE - Saque de Atrasado";
    public const string FamilySalaryLate = "SALÁRIO-FAMÍLIA - Saque de Atrasado";
    public const string PreSchoolLate = "ASSISTÊNCIA PRÉ-ESCOLAR - Saque de Atrasado";

    public static IReadOnlyList<string> MainSequence { get; } =
        [EconomicDependency, NatalityAid, PreSchool];

    public static IReadOnlyList<string> LateSequence { get; } =
        [NatalityAidLate, FamilySalaryLate, PreSchoolLate];

    public static bool IsRelated(string? templateName)
    {
        var normalized = MilitaryRankService.Normalize(templateName);
        return normalized.Contains("dependencia economica", StringComparison.Ordinal)
               || normalized.Contains("auxilio natalidade", StringComparison.Ordinal)
               || normalized.Contains("assistencia pre escolar", StringComparison.Ordinal)
               || normalized.Contains("pre escolar", StringComparison.Ordinal)
               || normalized.Contains("salario familia", StringComparison.Ordinal);
    }

    public static string SendReminder(string currentTemplate)
        => $"Você está prestes a enviar: {currentTemplate}.\n\n" +
           "Confira o conjunto de direitos decorrentes do nascimento, nesta ordem:\n" +
           "1. Dependência Econômica - Implantação;\n" +
           "2. Auxílio-Natalidade - Ordem de Saque;\n" +
           "3. Assistência Pré-Escolar - Implantação, quando cabível.\n\n" +
           "Se o pagamento não ocorreu na competência própria, confira também os saques de atrasado de Auxílio-Natalidade, Salário-Família e Assistência Pré-Escolar.\n\n" +
           "Deseja continuar o envio desta matéria?";
}
