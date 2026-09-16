using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

/// <summary>Fila administrativa factual. Não gera prazos legais nem chama a API em segundo plano.</summary>
public sealed class AssistantOperationsService(AppPaths paths, ReminderService reminders, JsonFileService json, LogService log)
{
    public async Task<AssistantOperationalSnapshot> BuildSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<AssistantOperationalItem>();
        var warnings = new List<string>();
        try
        {
            foreach (var reminder in await reminders.LoadAsync(cancellationToken: cancellationToken))
            {
                if (reminder.Completed) continue;
                items.Add(new AssistantOperationalItem
                {
                    Id = "lembrete:" + reminder.Id, Module = "Lembretes", Title = reminder.Title,
                    Detail = reminder.Body, DueDate = reminder.DueDate,
                    Urgent = reminder.Urgent || reminder.Priority.StartsWith("Urgent", StringComparison.OrdinalIgnoreCase)
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add("Não foi possível consultar os lembretes.");
            await log.WriteAsync(warnings[^1], ex);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var paymentRoot = await json.LoadNodeAsync(paths.PaymentRemindersFile);
        var payments = paymentRoot as JsonArray ?? (paymentRoot as JsonObject)?["items"] as JsonArray;
        if (payments is null && File.Exists(paths.PaymentRemindersFile))
            warnings.Add("O arquivo de pendências de pagamento está ilegível ou tem formato desconhecido.");
        if (payments is not null)
        {
            foreach (var node in payments.OfType<JsonObject>())
            {
                var status = Read(node, "status");
                if (IsPaymentCompleted(status)) continue;
                items.Add(new AssistantOperationalItem
                {
                    Id = "pagamento:" + Read(node, "id"), Module = "Pagamento",
                    Title = Read(node, "descricao", "detalhe"),
                    Detail = $"{Read(node, "categoria", "tipo")} • {Read(node, "competencia")} • {Read(node, "observacao")}",
                    DueDate = ReminderDate.Parse(Read(node, "prazo")),
                    Urgent = Read(node, "prioridade").StartsWith("Urgent", StringComparison.OrdinalIgnoreCase)
                });
            }
        }

        if (File.Exists(paths.DatabaseFile))
        {
            try
            {
                await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = paths.DatabaseFile, Mode = SqliteOpenMode.ReadOnly }.ToString());
                await connection.OpenAsync(cancellationToken);
                await using var exists = connection.CreateCommand();
                exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ea_processos'";
                if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) > 0)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT id, posto_grad, nome_completo, motivo_pagamento, cpex_status, cpex_motivo_relatorio FROM ea_processos WHERE COALESCE(pago,0)=0 ORDER BY id DESC LIMIT 501";
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    var count = 0;
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        if (++count > 500) { warnings.Add("Exercícios anteriores: exibidos os 500 processos pendentes mais recentes. Consulte o módulo para a lista completa."); break; }
                        string Cell(int n) => reader.IsDBNull(n) ? string.Empty : reader.GetValue(n).ToString() ?? string.Empty;
                        items.Add(new AssistantOperationalItem
                        {
                            Id = "ea:" + Cell(0), Module = "Exercícios anteriores", Title = $"EA #{Cell(0)} • {Cell(1)} {Cell(2)}",
                            Detail = $"{Cell(3)} • Situação registrada: {Cell(4)} • {Cell(5)}"
                        });
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add("Não foi possível consultar os processos de exercícios anteriores.");
                await log.WriteAsync(warnings[^1], ex);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new AssistantOperationalSnapshot
        {
            Items = items.OrderBy(x => x.DueDate?.Date < DateTime.Today ? 0 : x.DueDate?.Date == DateTime.Today ? 1 : x.Urgent ? 2 : 3)
                .ThenBy(x => x.DueDate ?? DateTime.MaxValue).ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToList(),
            Warnings = warnings
        };
    }

    public static bool IsPaymentCompleted(string status)
        => status.Trim().ToLowerInvariant() is "concluido" or "concluído" or "ok" or "feito";

    public static IReadOnlyList<AssistantOperationalItem> SelectAttention(IEnumerable<AssistantOperationalItem> items, DateTime today, int days)
        => items.Where(x => x.Urgent || x.DueDate?.Date <= today.Date.AddDays(Math.Clamp(days, 1, 30))).ToList();

    // Vencimento, edição de prioridade ou mudança de dia tornam um alerta novo; sem conteúdo pessoal no registro.
    public static string AlertKey(AssistantOperationalItem item, DateTime today)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{today:yyyy-MM-dd}|{item.Id}|{item.DueDate:yyyy-MM-dd}|{item.Urgent}")));

    private static string Read(JsonObject obj, string key, string? fallback = null)
        => obj[key]?.ToString() ?? (fallback is null ? string.Empty : obj[fallback]?.ToString() ?? string.Empty);
}
