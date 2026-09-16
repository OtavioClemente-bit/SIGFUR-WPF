namespace SIGFUR.Wpf.Services;

public enum AuxilioAlimentacaoTipoLancamento
{
    Normal,
    Atrasado,
    Diferenca,
    Devolucao
}

/// <summary>
/// Fonte única para a composição das rubricas de Auxílio-Alimentação do SIPPES.
/// Os sufixos seguem o Manual Técnico do SIPPES de 17/07/2026.
/// </summary>
public static class AuxilioAlimentacaoRubricRule
{
    public static string Resolve(int multiplicador, AuxilioAlimentacaoTipoLancamento tipo)
        => Prefix(tipo) + Suffix(multiplicador);

    public static string Suffix(int multiplicador)
        => multiplicador switch
        {
            1 => "0058",
            5 => "0052",
            10 => "0053",
            _ => throw new ArgumentOutOfRangeException(nameof(multiplicador), multiplicador,
                "Auxílio-Alimentação admite somente etapa comum/B (1x), etapa 5x ou etapa 10x.")
        };

    public static int MultiplierFromSuffix(string? suffix)
        => (suffix ?? string.Empty).Trim().TrimStart('0') switch
        {
            "58" => 1,
            "52" => 5,
            "53" => 10,
            _ => throw new ArgumentException("Sufixo de Auxílio-Alimentação inválido.", nameof(suffix))
        };

    public static string Prefix(AuxilioAlimentacaoTipoLancamento tipo)
        => tipo switch
        {
            AuxilioAlimentacaoTipoLancamento.Normal => "NR",
            AuxilioAlimentacaoTipoLancamento.Atrasado => "AR",
            AuxilioAlimentacaoTipoLancamento.Diferenca => "FR",
            AuxilioAlimentacaoTipoLancamento.Devolucao => "DR",
            _ => throw new ArgumentOutOfRangeException(nameof(tipo), tipo, "Tipo de lançamento inválido.")
        };
}
