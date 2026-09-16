using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed partial class PaymentConferenceService
{
    private readonly SemaphoreSlim _verificationGate = new(1, 1);
    private string VerificationFile => Path.Combine(ModuleDirectory, "verificados.json");

    private static string VerificationKey(PaymentConferenceResultRow row)
    {
        static string Version(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "sem-pdf";
            var file = new FileInfo(path);
            return file.Exists ? $"{file.FullName.ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}" : path + "|ausente";
        }
        return HashText(string.Join("\n", row.ConferencePeriod, Version(row.BulletinPath), row.BulletinPage,
            row.DocumentOccurrence, row.Cpf, row.Military, row.PaymentType, row.Context, Version(row.PaystubPath),
            row.ExpectedCodesText, row.RubricsFound, row.Status, row.PresenceOnly));
    }

    public async Task SetVerifiedAsync(PaymentConferenceResultRow row, bool verified)
    {
        await _verificationGate.WaitAsync();
        try
        {
            var marks = await _json.LoadAsync<Dictionary<string, DateTime>>(VerificationFile) ?? [];
            var key = VerificationKey(row);
            if (verified) marks[key] = DateTime.UtcNow; else marks.Remove(key);
            var temp = VerificationFile + ".tmp";
            await _json.SaveAsync(temp, marks);
            File.Move(temp, VerificationFile, true);
            row.IsVerified = verified;
        }
        finally { _verificationGate.Release(); }
    }

    public async Task ApplyVerificationsAsync(PaymentConferenceResult result)
    {
        await _verificationGate.WaitAsync();
        try
        {
            var marks = await _json.LoadAsync<Dictionary<string, DateTime>>(VerificationFile) ?? [];
            foreach (var row in result.Rows) row.IsVerified = marks.ContainsKey(VerificationKey(row));
        }
        finally { _verificationGate.Release(); }
    }
}
