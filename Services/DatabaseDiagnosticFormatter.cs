using System.Text;

namespace SIGFUR.Wpf.Services;

public static class DatabaseDiagnosticFormatter
{
    public static string Format(DatabaseSafetyReport? report, AppPaths paths, string themeName = "")
    {
        var sb = new StringBuilder();
        sb.AppendLine("SIGFUR — DIAGNÓSTICO DO BANCO");
        sb.AppendLine();
        sb.AppendLine("Aplicação: SIGFUR");
        if (!string.IsNullOrWhiteSpace(themeName)) sb.AppendLine($"Tema ativo: {themeName}");
        sb.AppendLine($"Caminho oficial: {paths.DatabaseFile}");
        sb.AppendLine();

        if (report is null)
        {
            sb.AppendLine("A proteção do banco ainda não foi executada nesta sessão.");
            return sb.ToString();
        }

        var official = report.Official;
        sb.AppendLine("BANCO OFICIAL");
        sb.AppendLine($"• Existe: {(official.Exists ? "sim" : "não")}");
        sb.AppendLine($"• Integridade: {official.Integrity}");
        sb.AppendLine($"• Tabela militares: {(official.HasMilitaryTable ? "encontrada" : "ausente")}");
        sb.AppendLine($"• Militares: {Math.Max(0, official.MilitaryCount)}");
        sb.AppendLine($"• Tamanho: {official.SizeText}");
        sb.AppendLine($"• Última alteração: {(official.Exists ? official.LastWriteTime.ToString("dd/MM/yyyy HH:mm:ss") : "—")}");
        if (!string.IsNullOrWhiteSpace(official.Error)) sb.AppendLine($"• Erro: {official.Error}");
        sb.AppendLine();

        sb.AppendLine("PROTEÇÃO");
        sb.AppendLine($"• Recuperação automática: {(report.Recovered ? "realizada" : "não necessária")}");
        if (!string.IsNullOrWhiteSpace(report.RecoverySource)) sb.AppendLine($"• Origem recuperada: {report.RecoverySource}");
        sb.AppendLine($"• Snapshot: {(string.IsNullOrWhiteSpace(report.SnapshotPath) ? "não criado" : report.SnapshotPath)}");
        sb.AppendLine($"• Resultado: {report.Message}");
        sb.AppendLine();

        sb.AppendLine("BANCOS LOCALIZADOS");
        if (report.Candidates.Count == 0)
        {
            sb.AppendLine("• Nenhum arquivo alternativo encontrado.");
        }
        else
        {
            foreach (var candidate in report.Candidates)
            {
                var officialMarker = Path.GetFullPath(candidate.Path).Equals(
                    Path.GetFullPath(paths.DatabaseFile), StringComparison.OrdinalIgnoreCase) ? " [OFICIAL]" : string.Empty;
                sb.AppendLine($"• {candidate.Path}{officialMarker}");
                sb.AppendLine($"  militares={Math.Max(0, candidate.MilitaryCount)} | integridade={candidate.Integrity} | tamanho={candidate.SizeText}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"Log: {paths.DatabaseSafetyLogFile}");
        sb.AppendLine($"Relatório JSON: {paths.DatabaseLocationFile}");
        return sb.ToString();
    }
}
