using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class PaystubAuditWorkflow(string databasePath, string stateDirectory)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string MarksFile => Path.Combine(stateDirectory, "audit-verificados.json");
    private static string Key(PaystubAuditRow row) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{row.Year}|{row.Month}|{row.Cpf}|{row.PdfPath}|{row.PdfModifiedTicks}|{row.AuxDatabase}|{row.Situation}|PDF-FERIAS-v10|{row.Vacation}")));

    public async Task SetVerifiedAsync(PaystubAuditRow row, bool verified)
    {
        await _gate.WaitAsync();
        try
        {
            var json = new JsonFileService(); var marks = await json.LoadAsync<Dictionary<string, bool>>(MarksFile) ?? [];
            marks[Key(row)] = verified; Directory.CreateDirectory(stateDirectory);
            await json.SaveAsync(MarksFile + ".tmp", marks); File.Move(MarksFile + ".tmp", MarksFile, true);
            row.IsVerified = verified;
        }
        finally { _gate.Release(); }
    }

    public async Task EnrichAsync(IReadOnlyList<PaystubAuditRow> rows, int year, int month)
    {
        var marks = await new JsonFileService().LoadAsync<Dictionary<string, bool>>(MarksFile) ?? [];
        foreach (var row in rows)
        {
            row.Year = year;
            row.Month = month;
            row.IsVerified = marks.GetValueOrDefault(Key(row));
        }
    }

    public async Task<int> UpdateTransportAsync(IReadOnlyList<PaystubAuditRow> selected, double tolerance)
    {
        if (selected.Count == 0) return 0;
        if (tolerance is <= 0 or > 100 || selected.Any(r => !r.CanUpdateTransport(tolerance)) || selected.Select(r => r.MilitaryId).Distinct().Count() != selected.Count)
            throw new InvalidOperationException("A seleção contém valores fora do limite ou itens que precisam de conferência manual.");
        foreach (var row in selected)
            if (!File.Exists(row.PdfPath) || File.GetLastWriteTimeUtc(row.PdfPath).Ticks != row.PdfModifiedTicks)
                throw new InvalidOperationException($"O contracheque de {row.Name} mudou. Refaça a auditoria.");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite }.ToString());
        await connection.OpenAsync(); using var transaction = connection.BeginTransaction();
        using var history = connection.CreateCommand(); history.Transaction = transaction;
        history.CommandText = "CREATE TABLE IF NOT EXISTS auditoria_at_atualizacoes(id INTEGER PRIMARY KEY, militar_id INTEGER, nome TEXT, competencia TEXT, valor_anterior TEXT, valor_novo TEXT, pdf TEXT, criado_em TEXT)";
        await history.ExecuteNonQueryAsync();
        foreach (var row in selected)
        {
            using var read = connection.CreateCommand(); read.Transaction = transaction;
            read.CommandText = "SELECT cpf,valor_aux_transporte FROM militares WHERE id=$id"; read.Parameters.AddWithValue("$id", row.MilitaryId);
            string old;
            using (var reader = await read.ExecuteReaderAsync())
            {
                if (!await reader.ReadAsync() || MilitaryFormatting.Digits(reader.GetString(0)) != MilitaryFormatting.Digits(row.Cpf)) throw new InvalidOperationException("Identidade do cadastro mudou: " + row.Name);
                old = reader.GetValue(1).ToString() ?? "";
                if (!double.IsFinite(Money(old)) || Math.Abs(Money(old) - row.AuxDatabase!.Value) > 0.005) throw new InvalidOperationException("O valor cadastrado mudou: " + row.Name + ". Refaça a auditoria.");
            }
            var next = row.AuxPdf.ToString("0.00", CultureInfo.InvariantCulture);
            using var update = connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = "UPDATE militares SET valor_aux_transporte=$value WHERE id=$id";
            update.Parameters.AddWithValue("$value", next); update.Parameters.AddWithValue("$id", row.MilitaryId); await update.ExecuteNonQueryAsync();
            using var log = connection.CreateCommand(); log.Transaction = transaction;
            log.CommandText = "INSERT INTO auditoria_at_atualizacoes(militar_id,nome,competencia,valor_anterior,valor_novo,pdf,criado_em) VALUES($id,$name,$period,$old,$new,$pdf,$time)";
            log.Parameters.AddWithValue("$id", row.MilitaryId); log.Parameters.AddWithValue("$name", row.Name); log.Parameters.AddWithValue("$period", $"{row.Month:00}/{row.Year}");
            log.Parameters.AddWithValue("$old", old); log.Parameters.AddWithValue("$new", next); log.Parameters.AddWithValue("$pdf", row.PdfPath); log.Parameters.AddWithValue("$time", DateTime.UtcNow.ToString("O"));
            await log.ExecuteNonQueryAsync();
        }
        transaction.Commit();
        foreach (var row in selected) if (row.Military is not null) row.Military.TransportAidValue = row.AuxPdf.ToString("0.00", CultureInfo.InvariantCulture);
        return selected.Count;
    }
    private static double Money(string text) => double.TryParse(text.Replace("R$", "").Trim(), NumberStyles.Number,
        text.Contains(',') ? CultureInfo.GetCultureInfo("pt-BR") : CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
}
