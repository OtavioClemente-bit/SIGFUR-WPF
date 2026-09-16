using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using Microsoft.Data.Sqlite;

static void Check(bool condition, string scenario)
{
    if (!condition) throw new InvalidOperationException("Falha: " + scenario);
}

var process = new ExercisePreviousProcess
{
    Id = 12,
    Rank = "3º Sgt",
    FullName = "JOÃO PEDRO DA SILVA",
    WarName = "SILVA",
    PrecCp = "1234567",
    Cpf = "123.456.789-09",
    Identity = "0123456789",
    ProcessNumber = "64300.001234",
    ProcessYear = 2026,
    RequestDate = "2026-09-10",
    SubjectText = "pagamento de adicional de habilitação não recebido na época própria",
    PreviousExerciseType = "AEA - Pagamento de EA tributável",
    PeriodStart = "2024-03-01",
    PeriodEnd = "2024-06-15",
    UpdatedThrough = "2026-08",
    GeneralProtocol = "2026.000123",
    PaymentReason = "o adicional não foi implantado durante o período devido",
    RightMaterializationDocument = "folhas de alterações e ficha financeira juntadas aos autos",
    AttachmentSheets = "01 a 18",
    Section = "1ª Seção"
};
var summary = new ExercisePreviousSummary { CorrectedNet = 1234.56m };
var draft = ExercisePreviousDocumentService.BuildBulletinRequestDraft(process, summary);

Check(draft.MissingFields.Count == 0, "processo completo não deve gerar pendências artificiais");
Check(draft.Text.Contains("10 de setembro de 2026", StringComparison.Ordinal), "a data real de entrada deve constar da nota");
Check(draft.Text.Contains("JOÃO PEDRO DA SILVA", StringComparison.Ordinal), "o nome completo deve identificar o requerente no BI");
Check(draft.Text.Contains("pagamento de adicional de habilitação", StringComparison.OrdinalIgnoreCase), "o assunto real deve constar da nota");
Check(draft.Text.Contains("R$ 1.234,56", StringComparison.Ordinal), "o valor corrigido determinístico deve preencher o valor quando o campo textual estiver vazio");
Check(draft.Text.Contains("1 de março a 15 de junho de 2024", StringComparison.OrdinalIgnoreCase), "o período preenchido deve contextualizar o pedido");
Check(!draft.Text.Contains("CPF", StringComparison.OrdinalIgnoreCase), "CPF não deve aparecer na publicação do requerimento");
Check(!draft.Text.Contains("Prec-CP", StringComparison.OrdinalIgnoreCase), "Prec-CP não deve aparecer na publicação do requerimento");
Check(!draft.Text.Contains("identidade", StringComparison.OrdinalIgnoreCase), "identidade não deve aparecer na publicação do requerimento");
Check(!draft.Text.Contains("Processo nº", StringComparison.OrdinalIgnoreCase), "número do processo não deve aparecer na publicação simplificada");
Check(!draft.Text.Contains("protocolo", StringComparison.OrdinalIgnoreCase), "protocolos não devem aparecer na publicação simplificada");
Check(!draft.Text.Contains("materializa", StringComparison.OrdinalIgnoreCase), "documentos que materializam o direito não devem aparecer na publicação simplificada");
Check(draft.Text.Contains("registro, à análise e à instrução do requerimento", StringComparison.OrdinalIgnoreCase), "a consequência deve determinar a instrução do pedido");
Check(draft.Text.Contains("submetidos à autoridade competente para decisão", StringComparison.OrdinalIgnoreCase), "a consequência deve prever a decisão da autoridade competente");

var incomplete = ExercisePreviousDocumentService.BuildBulletinRequestDraft(new ExercisePreviousProcess
{
    RequestDate = string.Empty,
    RequestDateInWords = string.Empty,
    PeriodStart = string.Empty,
    PeriodEnd = string.Empty,
    PreviousExerciseType = string.Empty
}, new ExercisePreviousSummary());
Check(incomplete.MissingFields.Count == 4 && incomplete.Text.Contains("[CONFIRMAR", StringComparison.Ordinal),
    "campos ausentes devem ficar explicitamente pendentes, nunca inventados");

var temporaryDirectory = Path.Combine(Path.GetTempPath(), "sigfur-ea-bulletin-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryDirectory);
try
{
    var paths = new AppPaths(temporaryDirectory);
    var assets = new ExercisePreviousAssetsService(paths);
    var documents = new ExercisePreviousDocumentService(assets);
    var log = new LogService(paths);
    await using (var legacyConnection = new SqliteConnection($"Data Source={paths.DatabaseFile}"))
    {
        await legacyConnection.OpenAsync();
        await using var legacyCommand = legacyConnection.CreateCommand();
        legacyCommand.CommandText = "CREATE TABLE ea_processos (id INTEGER PRIMARY KEY AUTOINCREMENT, created_at TEXT, updated_at TEXT)";
        await legacyCommand.ExecuteNonQueryAsync();
    }
    var repository = new ExercisePreviousRepository(paths, log);
    process.Id = 0;
    var id = await repository.SaveAsync(process);
    var bankOrderSource = Path.Combine(temporaryDirectory, "OB_2026.pdf");
    await File.WriteAllBytesAsync(bankOrderSource, "%PDF-1.4\nORDEM BANCARIA"u8.ToArray());
    var archived = documents.ArchiveBankOrder(process, bankOrderSource);
    process.BankOrderPath = archived;
    process.BankOrderAttachedAt = "2026-09-10 14:30:00";
    await repository.SaveAsync(process);

    var reloaded = await repository.GetAsync(id) ?? throw new InvalidOperationException("Processo EA não foi recarregado.");
    Check(reloaded.BankOrderPath == archived, "banco legado deve receber automaticamente as novas colunas da Ordem Bancária");
    Check(File.Exists(reloaded.BankOrderPath), "a Ordem Bancária deve ser copiada para a pasta documental do processo");
    Check(reloaded.BankOrderPath.Contains(Path.Combine("Documentos", "ORDEM_BANCARIA_EA_"), StringComparison.OrdinalIgnoreCase),
        "a Ordem Bancária deve ficar organizada dentro do processo");
    Check(reloaded.BankOrderAttachedAt == "2026-09-10 14:30:00", "data e vínculo da Ordem Bancária devem persistir no banco");
    var draftWithBankOrder = ExercisePreviousDocumentService.BuildBulletinRequestDraft(reloaded, summary);
    Check(!draftWithBankOrder.Text.Contains("Ordem Bancária", StringComparison.OrdinalIgnoreCase),
        "a publicação simplificada não deve listar documentos vinculados ao processo");
    var savedDraft = documents.SaveBulletinRequestText(reloaded, draftWithBankOrder.Text);
    Check(File.Exists(savedDraft) && Path.GetDirectoryName(savedDraft) == assets.GetProcessFolder(reloaded),
        "a minuta salva deve permanecer na pasta do próprio processo");

    var duplicateId = await repository.DuplicateAsync(id);
    var duplicate = await repository.GetAsync(duplicateId) ?? throw new InvalidOperationException("Cópia EA não foi recarregada.");
    Check(string.IsNullOrWhiteSpace(duplicate.BankOrderPath), "duplicar processo não pode reaproveitar Ordem Bancária de outro processo");
}
finally
{
    SqliteConnection.ClearAllPools();
    if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true);
}

Console.WriteLine("Exercício Anterior: verificações de Requerimento BI simplificado e Ordem Bancária aprovadas.");
