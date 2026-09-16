using System.ComponentModel;
using System.Windows;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Views.Finance;

public partial class PaymentConferenceWindow
{
    private CancellationTokenSource? _auditCancellation;

    private async void AuditSelected_Click(object sender, RoutedEventArgs e)
        => await AuditPaymentRowsAsync(ResultsGrid.SelectedItems.OfType<PaymentConferenceResultRow>().ToList(), "linhas selecionadas");

    private async void AuditFiltered_Click(object sender, RoutedEventArgs e)
        => await AuditPaymentRowsAsync(_rows.ToList(), "todas as linhas exibidas pelo filtro");

    private void CancelAudit_Click(object sender, RoutedEventArgs e) => _auditCancellation?.Cancel();

    private async void MonthlyAiAudit_Click(object sender, RoutedEventArgs e)
    {
        if (_auditCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _auditCancellation = cancellation;
        MonthlyAiAuditButton.IsEnabled = AuditSelectedButton.IsEnabled = AuditFilteredButton.IsEnabled = false;
        CancelAuditButton.Visibility = Visibility.Visible;
        try
        {
            _settings = ReadSettingsFromUi();
            await _service.SaveSettingsAsync(_settings, cancellation.Token);
            var culture = CultureInfo.GetCultureInfo("pt-BR");
            var monthFiles = _bulletins.Where(file => !string.IsNullOrWhiteSpace(file.Path)
                    && (file.Selected || DateTime.TryParse(file.Date, culture,
                        DateTimeStyles.None, out var date) && date.Month == _settings.Month && date.Year == _settings.Year))
                .Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (monthFiles.Count == 0)
                monthFiles = _bulletins.Where(file => file.Selected && file.Exists).Select(file => file.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (monthFiles.Count == 0)
                throw new InvalidOperationException("Não há boletins datados na competência nem boletins marcados para compor o escopo mensal.");

            PaymentMonthlyAuditPreparation? preparation = null;
            await RunBusyAsync("Inventariando o mês e executando o parser determinístico...", async progress =>
                preparation = await App.PaymentMonthlyAudit.PrepareAsync(monthFiles, _settings, progress, cancellation.Token));
            if (preparation is null) return;
            _lastSelectedBulletins = monthFiles;
            _lastResult = preparation.Conference;
            LoadResult(_lastResult);

            var estimate = preparation.Estimate;
            var assistantSettings = await App.AssistantStorage.LoadSettingsAsync();
            var usage = await App.AssistantStorage.GetCurrentMonthUsageAsync(assistantSettings);
            var preview = $"""
                PRÉVIA DA AUDITORIA MENSAL {_settings.Month:00}/{_settings.Year}

                Boletins no escopo: {estimate.Bulletins}
                Linha de base anterior: {preparation.Conference.Inventory.FoundPreviousPaystubs}/{preparation.Conference.Inventory.ExpectedPreviousPaystubs} contracheque(s)
                Páginas/blocos ambíguos para o Luna: {estimate.AmbiguousBlocks}
                Divergências para julgamento semântico: {estimate.SemanticDivergences}
                Chamadas estimadas não cacheadas: {estimate.EstimatedCalls} ({estimate.CachedCalls} já reutilizável(is))
                Tokens aproximados: {estimate.EstimatedInputTokens:N0} entrada / {estimate.EstimatedOutputTokens:N0} saída
                Custo máximo aproximado desta execução: {estimate.EstimatedCostBrl:C4}
                Orçamento restante configurado: {usage.RemainingBrl:C2}

                A etapa determinística já foi executada e será preservada. A IA não alterará pagamentos nem marcará itens como conferidos.
                """;
            if (estimate.EstimatedCostBrl > usage.RemainingBrl)
                throw new InvalidOperationException($"A auditoria estimada em {estimate.EstimatedCostBrl:C4} excede o orçamento restante de {usage.RemainingBrl:C2}. O resultado determinístico foi preservado; aumente o orçamento ou reduza o escopo.");
            if (estimate.EstimatedCalls > 0 && SigfurDialog.Show(this, preview + "\n\nContinuar?", "Auditoria mensal IA",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                StatusText.Text = "Auditoria mensal cancelada antes das chamadas de IA; resultado determinístico preservado na tela.";
                return;
            }

            PaymentMonthlyAuditResult? audit = null;
            await RunBusyAsync(estimate.EstimatedCalls == 0 ? "Consolidando auditoria determinística..." : "Executando somente resgates e divergências...",
                async progress => audit = await App.PaymentMonthlyAudit.RunAsync(preparation, assistantSettings, progress, cancellation.Token));
            if (audit is null) return;
            _lastResult = preparation.Conference;
            LoadResult(_lastResult);
            var report = PaymentMonthlyAiAuditService.FormatReport(audit);
            var path = await AssistantWorkflowService.SaveReportAsync("auditoria_mensal_pagamento", report, cancellation.Token);
            StatusText.Text = $"{audit.Status}: {audit.ItemsReturned}/{audit.ItemsExpected} itens semânticos · relatório {path}";
            new AssistantReviewWindow(this, "Auditoria mensal IA de pagamento", "Arquivo: " + path + "\n\n" + report).ShowDialog();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Auditoria mensal cancelada. Resultados determinísticos e caches concluídos foram preservados.";
        }
        catch (Exception ex)
        {
            await App.Log.WriteAsync("Falha na auditoria mensal IA de pagamento.", ex);
            ShowError(ex);
        }
        finally
        {
            _auditCancellation = null;
            MonthlyAiAuditButton.IsEnabled = AuditSelectedButton.IsEnabled = AuditFilteredButton.IsEnabled = true;
            CancelAuditButton.Visibility = Visibility.Collapsed;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _auditCancellation?.Cancel();
        base.OnClosing(e);
    }

    private async Task AuditPaymentRowsAsync(IReadOnlyList<PaymentConferenceResultRow> rows, string selection)
    {
        if (_auditCancellation is not null) return;
        if (rows.Count == 0)
        {
            SigfurDialog.Show(this, "Execute a conferência e selecione os itens que deseja revisar com IA.", "Auditoria de pagamento", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _auditCancellation = cancellation;
        AuditSelectedButton.IsEnabled = AuditFilteredButton.IsEnabled = false;
        CancelAuditButton.Visibility = Visibility.Visible;
        var startedAt = DateTime.Now;
        var scope = $"{selection}: {rows.Count} de {_lastResult.Rows.Count} itens da folha {_lastResult.Month:00}/{_lastResult.Year}; {_lastResult.BulletinCount} boletim(ns) na conferência de origem";
        var warnings = string.Join("\n", _lastResult.Warnings);
        var report = new StringBuilder($"SIGFUR — PARECER IA DA CONFERÊNCIA DE PAGAMENTO\nGerado em {startedAt:dd/MM/yyyy HH:mm}\nEscopo: {scope}\n{_lastResult.ScopeText}\nAvisos: {warnings}\nNenhuma marcação de verificação ou valor foi alterado.\n\n");
        var responded = 0;
        var sent = 0;
        var cost = 0m;
        var requests = 0;
        var outcome = "CONCLUÍDO";
        try
        {
            var settings = await App.AssistantStorage.LoadSettingsAsync();
            // Serialize now to freeze the evidence even if another action changes the current grid.
            var batches = AssistantWorkflowService.BuildPaymentBatches(rows, settings.RedactSensitiveData,
                maxCharacters: Math.Min(20_000, Math.Max(3000, settings.MaxContextCharacters / 3 - 2000)));
            report.AppendLine("Identificador da evidência enviada: " + AssistantWorkflowService.Fingerprint(string.Join("\n", batches.Select(x => x.Json))));
            var subjects = string.Join(", ", rows.Select(x => x.PaymentType).Distinct());
            for (var index = 0; index < batches.Count; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var batch = batches[index];
                StatusText.Text = $"IA: lote {index + 1}/{batches.Count} · {responded}/{rows.Count} itens com resposta · cancelar preserva os lotes concluídos.";
                var answer = await App.Assistant.SendStructuredAsync<PaymentAiAuditResponse>(
                    "Revise somente as evidências fornecidas. Não recalcule valores. Não invente fatos, documentos, rubricas ou fundamentos. Retorne exatamente um resultado para cada item_id e nunca altere a conferência humana.",
                    AssistantWorkflowService.PaymentPrompt(batch, scope, warnings, subjects),
                    AssistantStructuredSchemas.PaymentAudit, settings, "sigfur-payment-review-v2", "medium", 1800,
                    value => PaymentMonthlyAuditRules.ValidateReturnedIds(batch.Items.Select(item => item.Id), value), cancellation.Token);
                requests++; cost += answer.EstimatedCostBrl; sent += batch.Items.Count;
                var missing = PaymentMonthlyAuditRules.ValidateReturnedIds(batch.Items.Select(item => item.Id), answer.Value) is null
                    ? new List<string>() : batch.Items.Select(item => item.Id).Except(answer.Value.Results.Select(item => item.ItemId)).ToList();
                responded += answer.Value.Results.Count;
                report.AppendLine($"\nLOTE {index + 1}/{batches.Count} — {string.Join(", ", batch.Items.Select(x => x.Id))}");
                foreach (var finding in answer.Value.Results)
                {
                    report.AppendLine($"[{finding.ItemId}] {finding.Status} — {finding.Military}");
                    report.AppendLine(finding.Finding);
                    report.AppendLine("Boletim: " + finding.BulletinEvidence);
                    report.AppendLine("Contracheque: " + finding.PaystubEvidence);
                    report.AppendLine("Lacuna: " + finding.EvidenceGap);
                    report.AppendLine("Ação: " + finding.RecommendedAction);
                }
                if (missing.Count > 0)
                {
                    outcome = "PARCIAL — RESPOSTA SEM TODOS OS ITENS";
                    report.AppendLine("\nITENS SEM BLOCO IDENTIFICADO (não considerar revisados): " + string.Join(", ", missing));
                }
                report.AppendLine("\nFONTES DOS ITENS DESTE LOTE (dados efetivamente enviados):\n" + batch.Json);
            }
        }
        catch (OperationCanceledException)
        {
            outcome = "CANCELADO — PARECER PARCIAL";
        }
        catch (Exception ex)
        {
            outcome = "INTERROMPIDO — PARECER PARCIAL";
            report.AppendLine("\nFalha: " + ex.Message);
            await App.Log.WriteAsync("Falha na auditoria IA da conferência de pagamento.", ex);
        }
        finally
        {
            _auditCancellation = null;
            AuditSelectedButton.IsEnabled = AuditFilteredButton.IsEnabled = true;
            CancelAuditButton.Visibility = Visibility.Collapsed;
        }

        var coverage = $"{outcome}\nCobertura: {responded}/{rows.Count} itens com bloco individual de resposta; {sent}/{rows.Count} itens em lotes retornados; {requests} chamada(s) concluída(s). Custo estimado retornado: {cost:C4}.";
        var finalReport = coverage + "\n\n" + report;
        try
        {
            var path = await AssistantWorkflowService.SaveReportAsync("conferencia_pagamento", finalReport);
            StatusText.Text = $"IA: {responded}/{rows.Count} itens com resposta. Parecer salvo em {path}";
            finalReport = $"{coverage}\nArquivo: {path}\n\n{report}";
        }
        catch (Exception ex)
        {
            finalReport += "\n\nNão foi possível salvar o parecer: " + ex.Message;
            StatusText.Text = "Parecer disponível para copiar; falha ao salvar o arquivo.";
        }
        if (IsVisible) new AssistantReviewWindow(this, "Parecer da conferência de pagamento", finalReport).ShowDialog();
    }
}
