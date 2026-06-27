using System.Text.Json.Nodes;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Models;

public sealed class PaystubAuditRow
{
    public int MilitaryId { get; init; }
    public string Rank { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string WarName { get; init; } = string.Empty;
    public string Cpf { get; init; } = string.Empty;
    public string Idt { get; init; } = string.Empty;
    public string Prec { get; init; } = string.Empty;
    public string Pdf { get; init; } = string.Empty;
    public string PdfPath { get; init; } = string.Empty;
    public bool PdfOk { get; init; }
    public string BankStatus { get; init; } = string.Empty;
    public string BankPdf { get; init; } = string.Empty;
    public string AgencyPdf { get; init; } = string.Empty;
    public string AccountPdf { get; init; } = string.Empty;
    public string BankDatabase { get; init; } = string.Empty;
    public string AgencyDatabase { get; init; } = string.Empty;
    public string AccountDatabase { get; init; } = string.Empty;
    public bool BankDivergent { get; init; }
    public string PaymentStatus { get; init; } = string.Empty;
    public bool PaymentDifferent { get; init; }
    public double AuxPdf { get; init; }
    public double? AuxDatabase { get; init; }
    public double? AuxDifference { get; init; }
    public string AuxStatus { get; init; } = string.Empty;
    public double AuxDr { get; init; }
    public double AuxAr { get; init; }
    public double Fusex { get; init; }
    public double DependentFusex { get; init; }
    public double MedicalFusex { get; init; }
    public double FamilySalary { get; init; }
    public int Dependents { get; init; }
    public double PreSchool { get; init; }
    public double Vacation { get; init; }
    public double FoodAid { get; init; }
    public double Differences { get; init; }
    public double Pension { get; init; }
    public double MilitaryPension { get; init; }
    public double Alimony { get; init; }
    public double Irrf { get; init; }
    public double QualificationAdditional { get; init; }
    public double AvailabilityCompensation { get; init; }
    public double Pnr { get; init; }
    public double Loans { get; init; }
    public double Revenue { get; init; }
    public double Expense { get; init; }
    public double Net { get; init; }
    public string Situation { get; init; } = string.Empty;
    public Dictionary<string, List<string>> Lines { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public MilitaryRecord? Military { get; set; }

    public string ShortRank => MilitaryRankService.ShortName(Rank);
    public string NameBeforeWar => SplitName().Before;
    public string NameWarBold => SplitName().War;
    public string NameAfterWar => SplitName().After;
    public string PdfStatus => PdfOk ? Pdf : "NÃO ENCONTRADO";
    public string AuxPdfText => MoneyOrDash(AuxPdf);
    public string AuxDatabaseText => AuxDatabase.HasValue ? MilitaryFormatting.FormatMoney(AuxDatabase.Value) : "—";
    public string AuxDifferenceText => AuxDifference.HasValue ? MilitaryFormatting.FormatMoney(AuxDifference.Value) : "—";
    public string AuxDrText => MoneyOrDash(AuxDr);
    public string AuxArText => MoneyOrDash(AuxAr);
    public string FusexText => MoneyOrDash(Fusex);
    public string DependentFusexText => MoneyOrDash(DependentFusex);
    public string MedicalFusexText => MoneyOrDash(MedicalFusex);
    public string FamilySalaryText => FamilySalary > 0 ? $"{MilitaryFormatting.FormatMoney(FamilySalary)} ({Dependents})" : "—";
    public string PreSchoolText => MoneyOrDash(PreSchool);
    public string VacationText => MoneyOrDash(Vacation);
    public string FoodAidText => MoneyOrDash(FoodAid);
    public string DifferencesText => MoneyOrDash(Differences);
    public string PensionText => MoneyOrDash(Pension);
    public string MilitaryPensionText => MoneyOrDash(MilitaryPension);
    public string AlimonyText => MoneyOrDash(Alimony);
    public string IrrfText => MoneyOrDash(Irrf);
    public string QualificationAdditionalText => MoneyOrDash(QualificationAdditional);
    public string AvailabilityCompensationText => MoneyOrDash(AvailabilityCompensation);
    public string PnrText => MoneyOrDash(Pnr);
    public string LoansText => MoneyOrDash(Loans);
    public string RevenueText => MoneyOrDash(Revenue);
    public string ExpenseText => MoneyOrDash(Expense);
    public string NetText => MoneyOrDash(Net);
    public string Summary => BuildSummary();
    public string Severity => !PdfOk || Situation.StartsWith("Erro", StringComparison.OrdinalIgnoreCase) ? "Critical"
        : BankDivergent || PaymentDifferent || AuxStatus.Contains("DIVERG", StringComparison.OrdinalIgnoreCase) || AuxStatus.Contains("NÃO RECEBEU", StringComparison.OrdinalIgnoreCase) ? "Warning"
        : HasFinding ? "Info" : "Ok";
    public bool HasFinding => !PdfOk
        || !string.IsNullOrWhiteSpace(Situation) && !Situation.Equals("Sem achado relevante", StringComparison.OrdinalIgnoreCase)
        || BankDivergent || PaymentDifferent;
    public string SearchBlob => MilitaryRankService.Normalize(string.Join(" ", Rank, Name, WarName, Cpf, Idt, Prec, Pdf, BankStatus, BankPdf, AgencyPdf, AccountPdf, BankDatabase, AgencyDatabase, AccountDatabase, PaymentStatus, AuxStatus, Situation, Summary));

    public static PaystubAuditRow FromJson(JsonObject json)
    {
        var lines = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (json["linhas"] is JsonObject lineObject)
        {
            foreach (var pair in lineObject)
            {
                if (pair.Value is JsonArray array)
                    lines[pair.Key] = array.Select(x => x?.ToString() ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                else if (pair.Value is not null)
                    lines[pair.Key] = [pair.Value.ToString()];
            }
        }

        return new PaystubAuditRow
        {
            MilitaryId = Int(json, "id_militar"),
            Rank = Text(json, "pg"), Name = Text(json, "nome"), WarName = Text(json, "nome_guerra"),
            Cpf = Text(json, "cpf"), Idt = Text(json, "idt"), Prec = Text(json, "prec"),
            Pdf = Text(json, "pdf"), PdfPath = Text(json, "pdf_path"), PdfOk = Bool(json, "pdf_ok") || !Text(json, "pdf").Contains("NÃO ENCONTRADO", StringComparison.OrdinalIgnoreCase),
            BankStatus = Text(json, "banco_status"), BankDivergent = Bool(json, "banco_divergente"),
            BankPdf = Text(json, "banco_pdf"), AgencyPdf = Text(json, "agencia_pdf"), AccountPdf = Text(json, "conta_pdf"),
            BankDatabase = Text(json, "banco_db"), AgencyDatabase = Text(json, "agencia_db"), AccountDatabase = Text(json, "conta_db"),
            PaymentStatus = Text(json, "situacao_pagamento"), PaymentDifferent = Bool(json, "situacao_diferente"),
            AuxPdf = Double(json, "aux_pdf"), AuxDatabase = NullableDouble(json, "aux_db"), AuxDifference = NullableDouble(json, "aux_diff"),
            AuxStatus = Text(json, "aux_status"), AuxDr = Double(json, "aux_dr"), AuxAr = Double(json, "aux_ar"),
            Fusex = Double(json, "fusex"), DependentFusex = Double(json, "dep_fusex"), MedicalFusex = Double(json, "med_fusex"),
            FamilySalary = Double(json, "salario_familia"), Dependents = Int(json, "dependentes"), PreSchool = Double(json, "pre_escolar"),
            Vacation = Double(json, "ferias"), FoodAid = Double(json, "aux_alimentacao"), Differences = Double(json, "atrasados"),
            Pension = Double(json, "pensao"), MilitaryPension = Double(json, "pensao_militar"),
            Alimony = Double(json, "pensao_alimenticia"), Irrf = Double(json, "irrf"),
            QualificationAdditional = Double(json, "adicional_habilitacao"), AvailabilityCompensation = Double(json, "ad_c_disp_mil"),
            Pnr = Double(json, "pnr"), Loans = Double(json, "emprestimos"),
            Revenue = Double(json, "receita"), Expense = Double(json, "despesa"), Net = Double(json, "liquido"),
            Situation = Text(json, "situacao"), Lines = lines
        };
    }

    private string BuildSummary()
    {
        var parts = new List<string>();
        if (!PdfOk) parts.Add("Sem contracheque");
        if (BankDivergent) parts.Add("Banco divergente");
        if (PaymentDifferent) parts.Add($"Pagamento: {PaymentStatus}");
        if (AuxStatus.Contains("DIVERG", StringComparison.OrdinalIgnoreCase) || AuxStatus.Contains("NÃO RECEBEU", StringComparison.OrdinalIgnoreCase)) parts.Add(AuxStatus);
        if (Fusex <= 0 && PdfOk) parts.Add("Sem FUSEx 3%");
        if (AuxDr > 0) parts.Add("Desconto AT");
        if (FamilySalary > 0) parts.Add($"Salário-família {Dependents} dep.");
        if (PreSchool > 0) parts.Add("Pré-escolar");
        if (Vacation > 0) parts.Add("Férias");
        if (FoodAid > 0) parts.Add("Aux. alimentação");
        if (Differences > 0) parts.Add("DR/AR/atrasados");
        if (Alimony > 0) parts.Add("Pensão alimentícia");
        if (Pnr > 0) parts.Add("PNR");
        if (Loans > 0) parts.Add("Empréstimo/seguro/FHE");
        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(Situation) && !Situation.Equals("Sem achado relevante", StringComparison.OrdinalIgnoreCase)) parts.Add(Situation);
        return parts.Count == 0 ? "OK" : string.Join(" • ", parts.Distinct());
    }

    private (string Before, string War, string After) SplitName()
    {
        var full = (Name ?? string.Empty).Trim();
        var war = (WarName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(war)) return (full, string.Empty, string.Empty);
        var index = full.IndexOf(war, StringComparison.CurrentCultureIgnoreCase);
        return index >= 0
            ? (full[..index], full.Substring(index, war.Length), full[(index + war.Length)..])
            : (full + " — ", war, string.Empty);
    }

    private static string MoneyOrDash(double value) => Math.Abs(value) < 0.005 ? "—" : MilitaryFormatting.FormatMoney(value);
    private static string Text(JsonObject source, string key) => source[key]?.ToString() ?? string.Empty;
    private static bool Bool(JsonObject source, string key) => source[key] is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    private static int Int(JsonObject source, string key)
    {
        if (source[key] is JsonValue value && value.TryGetValue<int>(out var result)) return result;
        return int.TryParse(source[key]?.ToString(), out result) ? result : 0;
    }
    private static double Double(JsonObject source, string key) => NullableDouble(source, key) ?? 0d;
    private static double? NullableDouble(JsonObject source, string key)
    {
        var node = source[key];
        if (node is null || node.ToString().Equals("None", StringComparison.OrdinalIgnoreCase)) return null;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var number)) return number;
            if (value.TryGetValue<int>(out var integer)) return integer;
        }
        return double.TryParse(node.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
}
