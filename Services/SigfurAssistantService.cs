using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class SigfurAssistantService
{
    private readonly AppPaths _paths;
    private readonly MilitaryRepository _military;
    private readonly PaystubService _paystubs;
    private readonly SisbolPersonIndexImportService _personIndex;
    private readonly IntelligentBulletinService _bulletins;
    private readonly LegislationService _legislation;
    private readonly SettingsService _settings;
    private readonly LogService _log;

    public SigfurAssistantService(
        AppPaths paths,
        MilitaryRepository military,
        PaystubService paystubs,
        SisbolPersonIndexImportService personIndex,
        IntelligentBulletinService bulletins,
        LegislationService legislation,
        SettingsService settings,
        LogService log)
    {
        _paths = paths;
        _military = military;
        _paystubs = paystubs;
        _personIndex = personIndex;
        _bulletins = bulletins;
        _legislation = legislation;
        _settings = settings;
        _log = log;
    }

    public async Task<AssistantApiResult?> TryHandleAsync(string prompt, AssistantSettings assistantSettings, CancellationToken cancellationToken = default)
    {
        var intent = AssistantIntentDetector.Detect(prompt);
        if (!assistantSettings.EnableLocalDataTools) return null;
        if (intent.Kind == AssistantIntentKind.Unknown) return null;

        try
        {
            return intent.Kind switch
            {
                AssistantIntentKind.Help => BuildHelpResult(),
                AssistantIntentKind.OpenPaystub or AssistantIntentKind.PrintPaystub => await HandlePaystubAsync(prompt, intent, cancellationToken),
                AssistantIntentKind.OpenWallet => await HandleWalletAsync(prompt, cancellationToken),
                AssistantIntentKind.OpenFolder => await HandleFolderAsync(prompt, cancellationToken),
                AssistantIntentKind.Route => await HandleRouteAsync(prompt, cancellationToken),
                AssistantIntentKind.PersonFullSummary => await HandlePersonFullSummaryAsync(prompt, intent, cancellationToken),
                AssistantIntentKind.TransportAidConference => await HandleTransportConferenceAsync(prompt, intent, cancellationToken),
                AssistantIntentKind.SearchBulletins or AssistantIntentKind.OpenBulletin or AssistantIntentKind.VacationSearch or AssistantIntentKind.GratificationSearch => await HandleBulletinSearchAsync(prompt, intent, cancellationToken),
                AssistantIntentKind.LegislationResearch => await HandleLegislationResearchAsync(prompt, cancellationToken),
                AssistantIntentKind.GeneratedFiles => await HandleGeneratedFilesAsync(prompt, cancellationToken),
                AssistantIntentKind.SearchPerson => await HandlePersonSearchAsync(prompt, cancellationToken),
                AssistantIntentKind.GeneralQuestion => null,
                _ => null
            };
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha no modo local do Assistente SIGFUR.", ex);
            return new AssistantApiResult
            {
                Text = "Tentei resolver pelo modo local do SIGFUR, mas houve falha na consulta. O erro foi registrado no log. Nenhum dado foi alterado.",
                ToolSummaries = ["Modo local: falha registrada"]
            };
        }
    }

    public AssistantApiResult BuildNoApiGuidance(string prompt)
    {
        var help = BuildHelpResult();
        help.Text = "Consegui trabalhar no modo local, mas este pedido parece precisar de interpretação por IA ou de texto livre. Configure a chave da API para respostas abertas.\n\n" + help.Text;
        return help;
    }

    public async Task EnrichConversationLinksAsync(string prompt, AssistantApiResult result, CancellationToken cancellationToken = default)
    {
        if (result is null || string.IsNullOrWhiteSpace(result.Text)) return;

        var resolved = await ResolveLinksMentionedInTextAsync(prompt, result.Text, cancellationToken);
        if (resolved.Count == 0) return;

        result.PendingActions = result.PendingActions
            .Concat(resolved)
            .Where(x => x is not null)
            .GroupBy(ActionKey, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();

        if (!result.ToolSummaries.Contains("Links locais resolvidos", StringComparer.OrdinalIgnoreCase))
            result.ToolSummaries.Add("Links locais resolvidos");
    }

    private async Task<List<AssistantPendingAction>> ResolveLinksMentionedInTextAsync(string prompt, string assistantText, CancellationToken cancellationToken)
    {
        var text = string.Join(Environment.NewLine, prompt ?? string.Empty, assistantText ?? string.Empty);
        var actions = new List<AssistantPendingAction>();

        foreach (Match match in Regex.Matches(text, @"\b(?<kind>BI|B\.?I\.?|BOLETIM(?:\s+INTERNO)?|ADT|ADITAMENTO(?:\s+DO\s+FURRIEL)?)\s*(?:N[ºO°]?\s*)?(?<num>\d{1,4})(?:\s*/\s*(?<year>\d{4}))?", RegexOptions.IgnoreCase))
        {
            if (actions.Count >= 12) break;
            var kind = match.Groups["kind"].Value;
            var number = match.Groups["num"].Value;
            var year = ParseInt(match.Groups["year"].Value);
            var window = TextWindow(text, match.Index, 360);
            if (year == 0) year = ExtractYearNear(window);
            var page = ExtractPageNear(window);
            var labelHint = CleanLinkLine(window);

            if (kind.Contains("ADT", StringComparison.OrdinalIgnoreCase) || kind.Contains("ADITAMENTO", StringComparison.OrdinalIgnoreCase))
                actions.AddRange(await ResolveFurrielActionsAsync(number, year, page, labelHint, cancellationToken));
            else
            {
                actions.AddRange(await ResolvePersonIndexActionsAsync(number, year, page, labelHint, cancellationToken));
                actions.AddRange(await ResolveIntelligentBulletinActionsAsync(number, year, page, labelHint, cancellationToken));
            }
        }

        return actions
            .Where(x => x.FilePaths.Any(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path)) || x.Type.Equals("open_wallet", StringComparison.OrdinalIgnoreCase))
            .GroupBy(ActionKey, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
    }

    private async Task<List<AssistantPendingAction>> ResolvePersonIndexActionsAsync(string number, int year, int page, string labelHint, CancellationToken ct)
    {
        var actions = new List<AssistantPendingAction>();
        try
        {
            var rows = await _personIndex.SearchAsync(new SisbolPersonIndexQuery
            {
                Year = year > 0 ? year.ToString(CultureInfo.InvariantCulture) : "Todos",
                Search = string.Empty,
                Person = "Todos",
                Subject = "Todos",
                Note = "Todos",
                LinkFilter = "Todos",
                User = "Todos"
            }, 10000, ct);

            var candidates = rows
                .Where(row => BulletinNumberMatches(row.BulletinNumber, number, year))
                .Where(row => page <= 0 || row.BulletinPage is null || Math.Abs(row.BulletinPage.Value - page) <= 2)
                .Where(row => !string.IsNullOrWhiteSpace(row.SourcePdfPath) && File.Exists(row.SourcePdfPath))
                .OrderBy(row => page > 0 && row.BulletinPage == page ? 0 : 1)
                .ThenBy(row => row.BulletinPage ?? int.MaxValue)
                .Take(3)
                .ToList();

            foreach (var row in candidates)
            {
                var label = BuildBulletinLinkLabel(row);
                if (!string.IsNullOrWhiteSpace(labelHint) && labelHint.Contains(row.BulletinDisplay, StringComparison.OrdinalIgnoreCase))
                    label = MergeLabelHint(label, labelHint);
                var action = AssistantActionRegistry.OpenFile(label, row.SourcePdfPath, BuildOpenDescription(row.SourceFileName, row.PageText));
                action.Payload["display"] = label;
                actions.Add(action);
            }
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha ao resolver link do Índice por Pessoa para o Assistente SIGFUR.", ex);
        }
        return actions;
    }

    private async Task<List<AssistantPendingAction>> ResolveIntelligentBulletinActionsAsync(string number, int year, int page, string labelHint, CancellationToken ct)
    {
        var actions = new List<AssistantPendingAction>();
        try
        {
            var store = await _bulletins.LoadAsync(ct);
            var files = store.Items
                .Where(file => BulletinNumberMatches(file.BulletinNumber, number, year))
                .Where(file => !string.IsNullOrWhiteSpace(file.PdfPath) && File.Exists(file.PdfPath))
                .OrderByDescending(file => ParseDate(file.DateIso) ?? DateTime.MinValue)
                .Take(2)
                .ToList();

            foreach (var file in files)
            {
                var label = BuildIntelligentBulletinLinkLabel(file, page, labelHint);
                var description = BuildOpenDescription(file.FileName, page > 0 ? page.ToString(CultureInfo.InvariantCulture) : string.Empty);
                var action = AssistantActionRegistry.OpenFile(label, file.PdfPath, description);
                action.Payload["display"] = label;
                actions.Add(action);
            }
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha ao resolver link do Boletim Inteligente para o Assistente SIGFUR.", ex);
        }
        return actions;
    }

    private async Task<List<AssistantPendingAction>> ResolveFurrielActionsAsync(string number, int year, int page, string labelHint, CancellationToken ct)
    {
        var actions = new List<AssistantPendingAction>();
        try
        {
            if (!File.Exists(_paths.FurrielIndexFile)) return actions;
            var json = await File.ReadAllTextAsync(_paths.FurrielIndexFile, ct);
            var store = JsonSerializer.Deserialize<FurrielIndexStore>(json) ?? new FurrielIndexStore();
            var files = store.Files
                .Where(file => BulletinNumberMatches(file.Bulletin, number, year))
                .Where(file => !string.IsNullOrWhiteSpace(file.StoredPath) && File.Exists(file.StoredPath))
                .OrderByDescending(file => ParseDate(file.Date) ?? DateTime.MinValue)
                .Take(3)
                .ToList();

            foreach (var file in files)
            {
                var parts = new List<string>();
                parts.Add(file.DisplayBulletin.StartsWith("ADT", StringComparison.OrdinalIgnoreCase) ? file.DisplayBulletin : "ADT " + file.DisplayBulletin);
                if (!string.IsNullOrWhiteSpace(file.DisplayDate) && file.DisplayDate != "—") parts.Add(file.DisplayDate);
                if (page > 0) parts.Add("pág. " + page.ToString(CultureInfo.InvariantCulture));
                var label = MergeLabelHint(string.Join(" — ", parts), labelHint);
                var description = BuildOpenDescription(string.IsNullOrWhiteSpace(file.OriginalName) ? Path.GetFileName(file.StoredPath) : file.OriginalName, page > 0 ? page.ToString(CultureInfo.InvariantCulture) : string.Empty);
                var action = AssistantActionRegistry.OpenFile(label, file.StoredPath, description);
                action.Payload["display"] = label;
                actions.Add(action);
            }
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha ao resolver link do Aditamento do Furriel para o Assistente SIGFUR.", ex);
        }
        return actions;
    }

    private static string BuildIntelligentBulletinLinkLabel(IntelligentBulletinFile file, int page, string labelHint)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(file.BulletinNumber) && file.BulletinNumber != "—") parts.Add(file.BulletinNumber.StartsWith("BI", StringComparison.OrdinalIgnoreCase) ? file.BulletinNumber : "BI " + file.BulletinNumber);
        if (!string.IsNullOrWhiteSpace(file.BulletinDate) && file.BulletinDate != "—") parts.Add(file.BulletinDate);
        if (page > 0) parts.Add("pág. " + page.ToString(CultureInfo.InvariantCulture));
        return MergeLabelHint(parts.Count == 0 ? "Abrir boletim" : string.Join(" — ", parts), labelHint);
    }

    private static string BuildOpenDescription(string fileName, string pageText)
    {
        var text = string.IsNullOrWhiteSpace(fileName) || fileName == "—" ? "Abrir PDF salvo" : "Abrir PDF salvo: " + fileName;
        if (!string.IsNullOrWhiteSpace(pageText) && pageText != "0" && pageText != "—") text += " • página " + pageText;
        return text;
    }

    private static bool BulletinNumberMatches(string candidate, string number, int year)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(number)) return false;
        var matches = Regex.Matches(candidate, @"\d+").Select(x => x.Value).ToList();
        if (matches.Count == 0) return false;
        var candNumber = TrimLeadingZeros(matches[0]);
        var targetNumber = TrimLeadingZeros(number);
        if (!string.Equals(candNumber, targetNumber, StringComparison.Ordinal)) return false;
        if (year <= 0) return true;
        return matches.Any(x => x.Length == 4 && int.TryParse(x, out var parsed) && parsed == year);
    }

    private static string TrimLeadingZeros(string value)
    {
        var trimmed = (value ?? string.Empty).TrimStart('0');
        return string.IsNullOrWhiteSpace(trimmed) ? "0" : trimmed;
    }

    private static int ExtractYearNear(string text)
    {
        var yearMatch = Regex.Match(text ?? string.Empty, @"\b(20\d{2})\b");
        return yearMatch.Success && int.TryParse(yearMatch.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ? year : 0;
    }

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "—") return null;
        var formats = new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy" };
        if (DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact)) return exact.Date;
        if (DateTime.TryParse(value, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.AssumeLocal, out var parsed)) return parsed.Date;
        return null;
    }

    private static int ExtractPageNear(string text)
    {
        var pageMatch = Regex.Match(text ?? string.Empty, @"(?:p[áa]g(?:ina)?\.?|fl\.?|folha)\s*(?<page>\d{1,4})", RegexOptions.IgnoreCase);
        return pageMatch.Success && int.TryParse(pageMatch.Groups["page"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var page) ? page : 0;
    }

    private static int ParseInt(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static string TextWindow(string text, int index, int radius)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var start = Math.Max(0, index - radius / 3);
        var end = Math.Min(text.Length, index + radius);
        return text[start..end];
    }

    private static string CleanLinkLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lines = text.Replace("\r", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var line = lines.FirstOrDefault(x => Regex.IsMatch(x, @"\b(?:BI|B\.?I\.?|ADT|ADITAMENTO|BOLETIM)\b", RegexOptions.IgnoreCase)) ?? lines.FirstOrDefault() ?? string.Empty;
        line = Regex.Replace(line, @"^[•\-\s]+", string.Empty).Trim();
        line = Regex.Replace(line, @"\s{2,}", " ").Trim();
        return line.Length > 160 ? line[..160].Trim() : line;
    }

    private static string MergeLabelHint(string label, string hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return label;
        if (hint.Length < 18) return label;
        var normalizedHint = AssistantIntentDetector.Normalize(hint);
        var normalizedLabel = AssistantIntentDetector.Normalize(label);
        if (normalizedLabel.Length > 0 && normalizedHint.Contains(normalizedLabel.Split(' ')[0], StringComparison.Ordinal))
            return hint;
        return label;
    }

    private static string ActionKey(AssistantPendingAction action)
        => string.Join("|", action.Type, action.ConversationLinkLabel, string.Join(";", action.FilePaths), string.Join(";", action.Payload.Select(kv => kv.Key + "=" + kv.Value)));

    private static AssistantApiResult BuildHelpResult()
    {
        return new AssistantApiResult
        {
            Text = "Assistente SIGFUR pronto para o modo local.\n\n" +
                   "Consigo pesquisar militar, abrir carteira, abrir pasta de documentos, localizar contracheques salvos, preparar impressão, pesquisar no Índice por Pessoa, buscar notas/boletins, consultar PDFs salvos da Legislação e montar rota pelo endereço salvo. Com API configurada, perguntas gerais de legislação/pagamento podem complementar com internet e fonte oficial.\n\n" +
                   "Exemplos diretos:\n" +
                   "• Tem nota do Elton sobre auxílio-transporte?\n" +
                   "• Abre o contracheque de junho do Gustavo.\n" +
                   "• Imprime o contracheque de maio do Kaua.\n" +
                   "• Abre a carteira do Sgt Elton.\n" +
                   "• Mostra tudo salvo sobre esse militar.\n" +
                   "• Confere a DA de junho com o boletim.\n" +
                   "• Qual a base legal do auxílio-transporte?\n" +
                   "• Quem faz jus ao adicional de férias?",
            ToolSummaries = ["Modo local do Assistente"]
        };
    }

    private async Task<AssistantApiResult> HandleLegislationResearchAsync(string prompt, CancellationToken ct)
    {
        // Pesquisa local de legislação é somente leitura; internet fica a cargo da IA quando a API estiver configurada.
        var hits = await _legislation.SearchAsync(prompt, 8);
        if (hits.Count == 0)
        {
            return new AssistantApiResult
            {
                Text = "Não encontrei referência suficiente na biblioteca local de Legislação do SIGFUR.\n\n" +
                       "Como esta é uma pergunta geral de legislação/pagamento, o ideal é usar o modo híbrido com API para complementar com pesquisa na internet e citar a origem.\n\n" +
                       "Pendência:\n" +
                       "• importar/indexar PDFs oficiais no módulo Legislação; ou\n" +
                       "• configurar a API para o Assistente pesquisar na internet e informar as fontes.\n\n" +
                       "Nenhuma ação foi executada automaticamente.",
                ToolSummaries = ["Legislação local: 0 referência"]
            };
        }

        var answer = await _legislation.AnswerAsync(prompt, hits);
        var lines = new List<string>
        {
            "Encontrei referência na biblioteca local de Legislação do SIGFUR:",
            string.Empty,
            string.IsNullOrWhiteSpace(answer) ? "Resumo local indisponível; confira os trechos abaixo." : answer.Trim(),
            string.Empty,
            "Fontes locais:"
        };

        var actions = new List<AssistantPendingAction>();
        foreach (var hit in hits.Take(5))
        {
            var reference = $"{hit.Title} — pág. {hit.Page}";
            lines.Add($"• {reference}");
            if (!string.IsNullOrWhiteSpace(hit.Snippet))
                lines.Add($"  Trecho: {TrimInline(hit.Snippet, 260)}");

            if (!string.IsNullOrWhiteSpace(hit.Path) && File.Exists(hit.Path))
            {
                var action = AssistantActionRegistry.OpenFile("Abrir " + reference, hit.Path, "Abrir PDF/documento salvo na biblioteca de legislação do SIGFUR.");
                action.Payload["display"] = "Abrir " + reference;
                actions.Add(action);
            }
        }

        lines.Add(string.Empty);
        lines.Add("Observação: esta resposta veio da base local. Para complementar com internet e fonte oficial atualizada, use o modo híbrido/API.");

        return new AssistantApiResult
        {
            Text = string.Join(Environment.NewLine, lines),
            PendingActions = actions,
            ToolSummaries = [$"Legislação local: {hits.Count} referência(s)"]
        };
    }

    private static string TrimInline(string value, int max)
    {
        var text = Regex.Replace(value ?? string.Empty, "\\s+", " ").Trim();
        return text.Length <= max ? text : text[..max].TrimEnd() + "...";
    }

    private async Task<AssistantApiResult> HandlePersonSearchAsync(string prompt, CancellationToken ct)
    {
        var candidates = await ResolveMilitaryCandidatesAsync(prompt, ct);
        if (candidates.Count == 0)
            return TextOnly("Não localizei militar com esse termo no banco principal do SIGFUR. Pesquisei por nome, nome de guerra, CPF, Prec-CP e identidade.", "Banco de militares");

        var lines = new List<string> { $"Localizei {candidates.Count} militar(es) provável(eis) no banco do SIGFUR:" };
        var actions = new List<AssistantPendingAction>();
        foreach (var item in candidates.Take(8))
        {
            var m = item.Military;
            lines.Add($"\n• {m.ShortRank} {m.Name}" + (string.IsNullOrWhiteSpace(m.WarName) ? string.Empty : $" — Guerra: {m.WarName}") + $" — score {item.Score}");
            actions.Add(AssistantActionRegistry.OpenWallet(m));
        }
        lines.Add("\nFonte: Banco de militares. Ações disponíveis: abrir carteira e consultar documentos/contracheques.");
        return new AssistantApiResult { Text = string.Join(Environment.NewLine, lines), PendingActions = actions, ToolSummaries = ["Banco de militares"] };
    }

    private async Task<AssistantApiResult> HandleWalletAsync(string prompt, CancellationToken ct)
    {
        var resolved = await ResolveSingleMilitaryAsync(prompt, ct);
        if (resolved.Status != ResolveStatus.Resolved) return BuildResolveProblem(resolved, "abrir carteira");
        var m = resolved.Military!;
        return new AssistantApiResult
        {
            Text = $"Carteira localizada para {m.ShortRank} {m.Name}.\n\nFonte: Banco de militares. Ação disponível abaixo.",
            PendingActions = [AssistantActionRegistry.OpenWallet(m)],
            ToolSummaries = ["Banco de militares"]
        };
    }

    private async Task<AssistantApiResult> HandleFolderAsync(string prompt, CancellationToken ct)
    {
        var resolved = await ResolveSingleMilitaryAsync(prompt, ct);
        if (resolved.Status != ResolveStatus.Resolved) return BuildResolveProblem(resolved, "abrir pasta de documentos");
        var m = resolved.Military!;
        var folder = ResolvePersonFolder(m);
        var exists = Directory.Exists(folder);
        var actions = new List<AssistantPendingAction>
        {
            AssistantActionRegistry.OpenWallet(m),
            AssistantActionRegistry.OpenFolder("Abrir pasta", folder, exists ? "Abrir a pasta individual de documentos/contracheques." : "Pasta calculada pelo padrão do SIGFUR. Ela ainda pode não existir no disco.")
        };
        return new AssistantApiResult
        {
            Text = $"Pasta individual calculada para {m.ShortRank} {m.Name}:\n{folder}\n\nStatus: {(exists ? "pasta encontrada" : "pasta ainda não encontrada no disco")}.\nFonte: pasta oficial de contracheques/carteira do SIGFUR.",
            PendingActions = actions,
            ToolSummaries = ["Pasta da carteira/contracheques"]
        };
    }

    private async Task<AssistantApiResult> HandleRouteAsync(string prompt, CancellationToken ct)
    {
        var resolved = await ResolveSingleMilitaryAsync(prompt, ct);
        if (resolved.Status != ResolveStatus.Resolved) return BuildResolveProblem(resolved, "montar rota");
        var m = resolved.Military!;
        if (string.IsNullOrWhiteSpace(m.Address))
        {
            return new AssistantApiResult
            {
                Text = $"Não há endereço salvo na carteira do militar {m.ShortRank} {m.Name}.\n\nPesquisei no banco de militares. Ação disponível: abrir a carteira para cadastrar/conferir o endereço.",
                PendingActions = [AssistantActionRegistry.OpenWallet(m)],
                ToolSummaries = ["Banco de militares"]
            };
        }
        var profile = await _settings.LoadProfileAsync();
        var destination = string.IsNullOrWhiteSpace(profile.Organization) ? "4ª Cia PE, Belo Horizonte, MG" : profile.Organization + ", Belo Horizonte, MG";
        var url = "https://www.google.com/maps/dir/?api=1&origin=" + Uri.EscapeDataString(m.Address) + "&destination=" + Uri.EscapeDataString(destination);
        return new AssistantApiResult
        {
            Text = $"Rota montada para {m.ShortRank} {m.Name}.\n\nOrigem: endereço salvo na carteira.\nDestino: {destination}.\n\nFonte: Banco de militares e configuração da OM.",
            PendingActions = [AssistantActionRegistry.OpenRoute(m, url), AssistantActionRegistry.OpenWallet(m)],
            ToolSummaries = ["Endereço da carteira", "Configuração da OM"]
        };
    }

    private async Task<AssistantApiResult> HandlePaystubAsync(string prompt, AssistantIntent intent, CancellationToken ct)
    {
        var resolved = await ResolveSingleMilitaryAsync(prompt, ct, requireStrongPerson: true);
        if (resolved.Status != ResolveStatus.Resolved) return BuildResolveProblem(result: resolved, operation: intent.WantsPrint ? "imprimir contracheque" : "abrir contracheque");
        var m = resolved.Military!;
        IReadOnlyList<PaystubFileRecord> files;
        string? best = null;
        if (intent.Month > 0 && intent.Year > 0)
        {
            best = await _paystubs.FindBestAsync(m, intent.Month, intent.Year, ct);
            files = string.IsNullOrWhiteSpace(best)
                ? await _paystubs.FindForMilitaryAsync(m, ct)
                : [new PaystubFileRecord { Path = best, ModifiedAt = File.GetLastWriteTime(best), SizeBytes = new FileInfo(best).Length, Reference = $"{intent.Month:00}/{intent.Year:0000}", DocumentType = "Contracheque" }];
        }
        else
        {
            files = await _paystubs.FindForMilitaryAsync(m, ct);
            best = files.OrderByDescending(x => x.ModifiedAt).FirstOrDefault()?.Path;
        }

        var selected = files.Where(x => File.Exists(x.Path)).OrderByDescending(x => x.ModifiedAt).Take(6).ToList();
        if (selected.Count == 0)
        {
            return new AssistantApiResult
            {
                Text = $"Não encontrei contracheque salvo para {m.ShortRank} {m.Name}.\n\nPesquisei na pasta oficial de contracheques do SIGFUR. Sugestão: baixar pela Central de Contracheques ou conferir a pasta individual.",
                PendingActions = [AssistantActionRegistry.OpenWallet(m), AssistantActionRegistry.OpenFolder("Abrir pasta individual", ResolvePersonFolder(m))],
                ToolSummaries = ["Contracheques salvos"]
            };
        }

        var actions = new List<AssistantPendingAction> { AssistantActionRegistry.OpenWallet(m) };
        var position = 0;
        foreach (var file in selected.Take(4))
        {
            position++;
            var title = intent.WantsPrint
                ? $"Imprimir {file.Reference}"
                : position == 1 && intent.Month == 0 ? "Abrir último contracheque" : $"Abrir {file.Reference}";
            var action = intent.WantsPrint
                ? AssistantActionRegistry.PrintFile(title, file.Path, 1, $"Enviar para a fila de impressão: {file.FileName}")
                : AssistantActionRegistry.OpenFile(title, file.Path, $"Abrir contracheque salvo: {file.FileName}");
            action.Payload["display"] = title;
            actions.Add(action);
            actions.Add(AssistantActionRegistry.RevealFile($"Mostrar pasta {file.Reference}", file.Path, "Abrir o Explorer no arquivo selecionado."));
        }

        var lines = new List<string>
        {
            $"Localizei {selected.Count} contracheque(s) salvo(s) para {m.ShortRank} {m.Name}.",
            string.Empty
        };
        foreach (var file in selected.Take(6))
            lines.Add($"• {file.Reference} — {file.FileName} — {file.ModifiedAt:dd/MM/yyyy HH:mm}");
        lines.Add("\nFonte: pasta oficial de contracheques do SIGFUR. Ações disponíveis abaixo.");
        return new AssistantApiResult { Text = string.Join(Environment.NewLine, lines), PendingActions = actions, ToolSummaries = ["Contracheques salvos", "Banco de militares"] };
    }

    private async Task<AssistantApiResult> HandleBulletinSearchAsync(string prompt, AssistantIntent intent, CancellationToken ct)
    {
        var resolved = await ResolveSingleMilitaryAsync(prompt, ct, allowNoPerson: true, requireStrongPerson: true);
        var subject = string.IsNullOrWhiteSpace(intent.Subject) ? ExtractSubjectFromPrompt(prompt) : intent.Subject;
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<AssistantSearchResult>();

        await _log.WriteAsync($"Assistente SIGFUR: intenção detectada={intent.Kind}; documento={intent.DocumentKind}; assunto={subject}; ação={intent.RequestedAction}; pessoa extraída={intent.PersonTerm}; ano={intent.Year}; mês={intent.Month}.");

        if (resolved.Military is not null)
        {
            results.AddRange(await SearchPersonIndexStructuredAsync(resolved.Military, subject, intent, ct));
            sources.Add("Índice por Pessoa");

            results.AddRange(await SearchFurrielStructuredAsync(resolved.Military, subject, intent, ct));
            sources.Add("Aditamento do Furriel");
        }
        else
        {
            results.AddRange(await SearchPersonIndexLooseAsync(prompt, subject, intent, ct));
            sources.Add("Índice por Pessoa");

            results.AddRange(await SearchFurrielLooseAsync(prompt, subject, intent, ct));
            sources.Add("Aditamento do Furriel");
        }

        var ranked = RankAssistantResults(results, intent)
            .GroupBy(ResultKey, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .Take(12)
            .ToList();

        var actions = ranked.SelectMany(x => x.Actions).ToList();
        if (resolved.Military is not null) actions.Add(AssistantActionRegistry.OpenWallet(resolved.Military));
        actions = actions.GroupBy(ActionKey, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();

        await _log.WriteAsync($"Assistente SIGFUR: fontes consultadas={string.Join(", ", sources)}; resultados={ranked.Count}; escolhido={(ranked.FirstOrDefault()?.Title ?? "nenhum")}; ações={actions.Count}.");

        if (ranked.Count == 0)
        {
            var notFound = new StringBuilder();
            notFound.AppendLine(resolved.Military is not null
                ? $"Não encontrei referência segura para {resolved.Military.ShortRank} {resolved.Military.Name}."
                : "Não encontrei referência segura com os filtros informados.");
            notFound.AppendLine();
            notFound.AppendLine("Conferência realizada:");
            notFound.AppendLine("• Índice por Pessoa");
            notFound.AppendLine("• Aditamento do Furriel indexado");
            notFound.AppendLine("• PDFs vinculados nas bases oficiais do SIGFUR");
            notFound.AppendLine();
            notFound.AppendLine("Pendência: confirme nome completo, CPF, Prec-CP, identidade, ano, mês ou assunto/nota. Nome de guerra isolado não confirma identidade do militar.");
            return new AssistantApiResult { Text = notFound.ToString().Trim(), PendingActions = actions, ToolSummaries = sources.ToList() };
        }

        // Resultados estruturados têm prioridade sobre texto indexado.
        var confident = ranked.Where(x => x.Confidence >= 78).ToList();
        var possibleOnly = confident.Count == 0;
        var selected = possibleOnly ? ranked.Take(5).ToList() : confident.Take(6).ToList();

        var text = new StringBuilder();
        if (possibleOnly)
        {
            text.AppendLine("Encontrei possíveis resultados, mas não confirmei com segurança.");
            text.AppendLine();
            text.AppendLine("Motivo: a vinculação do militar não bateu por nome completo, CPF, Prec-CP ou identidade, ou o resultado veio como menção textual solta.");
        }
        else
        {
            text.AppendLine(selected.Count == 1 ? "Encontrei a referência mais segura no SIGFUR:" : "Encontrei referências seguras no SIGFUR:");
        }

        foreach (var result in selected)
        {
            text.AppendLine();
            text.AppendLine(FormatResultCard(result));
        }

        var discarded = ranked.Count - selected.Count;
        if (discarded > 0)
        {
            text.AppendLine();
            text.AppendLine($"Outros {discarded} resultado(s) ficaram ocultos por menor confiança ou duplicidade.");
        }

        text.AppendLine();
        text.AppendLine("Conferência realizada:");
        foreach (var source in sources.Where(x => !string.IsNullOrWhiteSpace(x)).OrderBy(x => x)) text.AppendLine("• Fonte: " + source);
        text.AppendLine("• Resultados estruturados tiveram prioridade sobre texto indexado.");
        text.AppendLine("• Nome de guerra isolado foi tratado apenas como sugestão, não como confirmação.");

        // O Assistente apenas sugere ações; execução depende de clique do operador.
        if (actions.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Ações disponíveis: use os links azuis dentro da conversa. Nenhum arquivo será aberto automaticamente.");
        }

        return new AssistantApiResult
        {
            Text = text.ToString().Trim(),
            PendingActions = actions,
            ToolSummaries = sources.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private async Task<List<AssistantSearchResult>> SearchPersonIndexStructuredAsync(MilitaryRecord military, string subject, AssistantIntent intent, CancellationToken ct)
    {
        var rows = (await _personIndex.FindForMilitaryAsync(military, ct)).ToList();
        rows = FilterPersonIndexRows(rows, subject, intent).ToList();
        return rows.Take(30).Select(row => BuildPersonIndexResult(row, military, intent, structured: true)).ToList();
    }

    private async Task<List<AssistantSearchResult>> SearchPersonIndexLooseAsync(string prompt, string subject, AssistantIntent intent, CancellationToken ct)
    {
        var cleanIndexSearch = BuildPersonIndexSearch(prompt, subject);
        var query = new SisbolPersonIndexQuery
        {
            Search = cleanIndexSearch,
            Person = string.IsNullOrWhiteSpace(cleanIndexSearch) ? "Todos" : cleanIndexSearch,
            SubjectOrNote = subject,
            Year = intent.Year > 0 ? intent.Year.ToString(CultureInfo.InvariantCulture) : "Todos",
            Month = intent.Month > 0 ? MonthName(intent.Month) : "Todos"
        };
        var rows = (await _personIndex.SearchAsync(query, 60, ct)).ToList();
        rows = FilterPersonIndexRows(rows, subject, intent).ToList();
        return rows.Take(30).Select(row => BuildPersonIndexResult(row, null, intent, structured: false)).ToList();
    }

    private IEnumerable<SisbolPersonIndexItem> FilterPersonIndexRows(IEnumerable<SisbolPersonIndexItem> rows, string subject, AssistantIntent intent)
    {
        foreach (var row in rows)
        {
            if (intent.Year > 0 && row.BulletinDate?.Year != intent.Year) continue;
            if (intent.Month > 0 && row.BulletinDate?.Month != intent.Month) continue;
            if (intent.BulletinNumber > 0 && !BulletinNumberMatches(row.BulletinNumber, intent.BulletinNumber.ToString(CultureInfo.InvariantCulture), intent.Year)) continue;
            if (!MatchesSubjectOrAction(row, subject, intent.RequestedAction)) continue;
            yield return row;
        }
    }

    private AssistantSearchResult BuildPersonIndexResult(SisbolPersonIndexItem row, MilitaryRecord? military, AssistantIntent intent, bool structured)
    {
        var source = "Índice por Pessoa";
        var militaryName = military is null ? row.PeopleDisplay : $"{military.ShortRank} {military.Name}".Trim();
        var confidence = structured ? 88d : 58d;
        if (structured && row.MilitaryId == military?.Id) confidence += 8;
        if (!string.IsNullOrWhiteSpace(row.MainSubject) && !string.IsNullOrWhiteSpace(intent.Subject) && MatchesSubject(row, intent.Subject)) confidence += 4;
        if (!string.IsNullOrWhiteSpace(intent.RequestedAction) && ContainsNormalized(row.DisplaySubjectNote, intent.RequestedAction)) confidence += 3;
        if (!string.IsNullOrWhiteSpace(row.SourcePdfPath) && File.Exists(row.SourcePdfPath)) confidence += 2;

        var result = new AssistantSearchResult
        {
            Kind = string.IsNullOrWhiteSpace(row.BulletinType) ? "BI" : row.BulletinType,
            Type = string.IsNullOrWhiteSpace(row.BulletinType) ? "BI" : row.BulletinType,
            Title = BuildBulletinLine(row),
            Subtitle = row.DisplaySubjectNote,
            MilitaryName = militaryName,
            Subject = row.MainSubjectDisplay,
            Note = row.NoteDisplay,
            BulletinNumber = row.BulletinDisplay,
            BulletinDate = row.BulletinDate,
            Year = row.BulletinDate?.Year ?? intent.Year,
            Page = row.BulletinPage,
            Source = source,
            Module = source,
            Confidence = Math.Min(99, confidence),
            Score = Math.Min(99, confidence),
            PersonId = military?.Id ?? row.MilitaryId,
            FilePath = row.SourcePdfPath,
            SearchTerm = row.OpenSearchTerm,
            Action = "open_file",
            Date = row.BulletinDate
        };

        if (!string.IsNullOrWhiteSpace(row.SourcePdfPath) && File.Exists(row.SourcePdfPath))
        {
            var action = BuildOpenBulletinAction(row);
            action.Title = "Abrir " + SafeReference(row.BulletinDisplay, "BI");
            action.Description = result.Title;
            action.Payload["display"] = action.Title;
            result.Actions.Add(action);
        }
        return result;
    }

    private async Task<List<AssistantSearchResult>> SearchFurrielStructuredAsync(MilitaryRecord military, string subject, AssistantIntent intent, CancellationToken ct)
    {
        var store = await LoadFurrielStoreSafeAsync(ct);
        if (store is null) return [];

        return await Task.Run(() =>
        {
            var results = new List<AssistantSearchResult>();
            foreach (var file in store.Files)
            {
                ct.ThrowIfCancellationRequested();
                if (!FurrielFileMatchesIntent(file, intent)) continue;
                foreach (var mention in file.Mentions)
                {
                    if (!MentionMatchesMilitary(mention, military)) continue;
                    if (!MentionMatchesSubjectOrAction(mention, subject, intent.RequestedAction)) continue;
                    results.Add(BuildFurrielMentionResult(file, mention, military, intent, structured: true));
                }
            }
            return results;
        }, ct);
    }

    private async Task<List<AssistantSearchResult>> SearchFurrielLooseAsync(string prompt, string subject, AssistantIntent intent, CancellationToken ct)
    {
        var store = await LoadFurrielStoreSafeAsync(ct);
        if (store is null) return [];
        var normalizedPrompt = AssistantIntentDetector.Normalize(prompt);
        var personTokens = ExtractIntentPersonTokens(intent, prompt);

        return await Task.Run(() =>
        {
            var results = new List<AssistantSearchResult>();
            foreach (var file in store.Files)
            {
                ct.ThrowIfCancellationRequested();
                if (!FurrielFileMatchesIntent(file, intent)) continue;
                foreach (var mention in file.Mentions)
                {
                    if (!MentionMatchesSubjectOrAction(mention, subject, intent.RequestedAction)) continue;
                    var mentionText = AssistantIntentDetector.Normalize(string.Join(' ', mention.MentionedMilitaryRank, mention.MentionedMilitaryName, mention.MentionedMilitaryWarName, mention.MentionedMilitaryCpf, mention.MentionedMilitaryPrecCp, mention.Subject, mention.NoteTitle, mention.SubjectNoteDisplay, mention.NoteExcerpt));
                    if (personTokens.Count > 0 && personTokens.Count(token => mentionText.Contains(token, StringComparison.Ordinal)) == 0) continue;
                    results.Add(BuildFurrielMentionResult(file, mention, null, intent, structured: false));
                }

                foreach (var line in file.Lines.Take(800))
                {
                    if (!LineMatchesSubjectOrAction(line, subject, intent.RequestedAction)) continue;
                    var lineText = string.IsNullOrWhiteSpace(line.Normalized) ? AssistantIntentDetector.Normalize(line.Text) : line.Normalized;
                    if (personTokens.Count > 0 && personTokens.Count(token => lineText.Contains(token, StringComparison.Ordinal)) == 0) continue;
                    results.Add(BuildFurrielLineResult(file, line, intent, normalizedPrompt));
                }
            }

            foreach (var entry in store.SubjectIndex.Take(4000))
            {
                ct.ThrowIfCancellationRequested();
                if (intent.AdtNumber > 0 && !BulletinNumberMatches(entry.BulletinNumber, intent.AdtNumber.ToString(CultureInfo.InvariantCulture), intent.Year)) continue;
                if (intent.Year > 0 && ParseDate(entry.BulletinDate)?.Year != intent.Year) continue;
                var entryText = AssistantIntentDetector.Normalize(string.Join(' ', entry.Subject, entry.NoteType, entry.SubjectNoteDisplay, entry.SearchTextNormalized));
                if (!SubjectActionTextMatches(entryText, subject, intent.RequestedAction)) continue;
                if (personTokens.Count > 0 && personTokens.Count(token => entryText.Contains(token, StringComparison.Ordinal)) == 0) continue;
                results.Add(BuildFurrielSubjectIndexResult(entry, intent));
            }

            return results;
        }, ct);
    }

    private async Task<FurrielIndexStore?> LoadFurrielStoreSafeAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_paths.FurrielIndexFile)) return null;
            var json = await File.ReadAllTextAsync(_paths.FurrielIndexFile, ct);
            return JsonSerializer.Deserialize<FurrielIndexStore>(json) ?? new FurrielIndexStore();
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Assistente SIGFUR: falha ao ler índice do Aditamento do Furriel.", ex);
            return null;
        }
    }

    private AssistantSearchResult BuildFurrielMentionResult(FurrielBulletinFile file, BulletinMentionItem mention, MilitaryRecord? military, AssistantIntent intent, bool structured)
    {
        var date = mention.BulletinDate ?? ParseDate(file.Date);
        var page = mention.PageNumber ?? 0;
        var adt = BuildAdtDisplay(FirstNonEmpty(mention.BulletinNumber, file.Bulletin));
        var path = FirstExisting(mention.SourceFilePath, file.StoredPath, file.SourcePath);
        var subject = FirstNonEmpty(mention.Subject, file.Title, "Aditamento do Furriel");
        var note = FirstNonEmpty(mention.SubjectNoteDisplay, mention.NoteTitle, mention.NoteExcerpt);
        var confidence = structured ? 90d : 62d;
        if ((military is not null && mention.MilitaryId == military.Id) || (structured && mention.IsDatabaseMatch)) confidence += 7;
        if (!string.IsNullOrWhiteSpace(intent.Subject) && ContainsNormalized(string.Join(' ', subject, note, mention.NoteText), intent.Subject)) confidence += 4;
        if (!string.IsNullOrWhiteSpace(intent.RequestedAction) && ContainsNormalized(string.Join(' ', note, mention.NoteText), intent.RequestedAction)) confidence += 3;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) confidence += 2;

        var result = new AssistantSearchResult
        {
            Kind = "ADT",
            Type = "ADT",
            Title = BuildResultLine("ADT", adt, date, subject, note, page),
            Subtitle = note,
            MilitaryName = military is not null ? $"{military.ShortRank} {military.Name}".Trim() : FirstNonEmpty(mention.DisplayMilitary, mention.MentionedMilitaryWarName),
            Subject = subject,
            Note = note,
            BulletinNumber = adt,
            BulletinDate = date,
            Year = date?.Year ?? intent.Year,
            Page = page > 0 ? page : null,
            Source = structured ? "Aditamento do Furriel / menção estruturada" : "Aditamento do Furriel / possível menção textual",
            Module = "Aditamento do Furriel",
            Confidence = Math.Min(99, confidence),
            Score = Math.Min(99, confidence),
            PersonId = military?.Id ?? mention.MilitaryId,
            FilePath = path,
            SearchTerm = FirstNonEmpty(military?.Name ?? string.Empty, mention.MentionedMilitaryName, mention.MentionedMilitaryWarName),
            Action = "open_file",
            Date = date
        };
        AddOpenResultAction(result, "Abrir " + adt);
        return result;
    }

    private AssistantSearchResult BuildFurrielLineResult(FurrielBulletinFile file, FurrielIndexedLine line, AssistantIntent intent, string normalizedPrompt)
    {
        var date = ParseDate(file.Date);
        var adt = BuildAdtDisplay(file.Bulletin);
        var path = FirstExisting(file.StoredPath, file.SourcePath);
        var subject = FirstNonEmpty(line.Subject, file.Title, "Aditamento do Furriel");
        var note = FirstNonEmpty(line.Major, line.Text);
        var result = new AssistantSearchResult
        {
            Kind = "ADT",
            Type = "ADT",
            Title = BuildResultLine("ADT", adt, date, subject, note, line.Page),
            Subtitle = "Possível referência textual: " + Shorten(line.Text, 140),
            Subject = subject,
            Note = note,
            BulletinNumber = adt,
            BulletinDate = date,
            Year = date?.Year ?? intent.Year,
            Page = line.Page > 0 ? line.Page : null,
            Source = "Aditamento do Furriel / texto indexado",
            Module = "Aditamento do Furriel",
            Confidence = 48,
            Score = 48,
            FilePath = path,
            SearchTerm = normalizedPrompt,
            Action = "open_file",
            Date = date
        };
        AddOpenResultAction(result, "Abrir " + adt);
        return result;
    }

    private AssistantSearchResult BuildFurrielSubjectIndexResult(FurrielSubjectIndexEntry entry, AssistantIntent intent)
    {
        var date = ParseDate(entry.BulletinDate);
        var adt = BuildAdtDisplay(entry.BulletinNumber);
        var subject = FirstNonEmpty(entry.Subject, "Aditamento do Furriel");
        var note = FirstNonEmpty(entry.NoteType, entry.SubjectNoteDisplay);
        var result = new AssistantSearchResult
        {
            Kind = "ADT",
            Type = "ADT",
            Title = BuildResultLine("ADT", adt, date, subject, note, entry.Page),
            Subtitle = entry.SubjectNoteDisplay,
            Subject = subject,
            Note = note,
            BulletinNumber = adt,
            BulletinDate = date,
            Year = date?.Year ?? intent.Year,
            Page = entry.Page > 0 ? entry.Page : null,
            Source = "Aditamento do Furriel / índice por assunto",
            Module = "Aditamento do Furriel",
            Confidence = 55,
            Score = 55,
            FilePath = entry.SourcePdfPath,
            SearchTerm = entry.SearchTextNormalized,
            Action = "open_file",
            Date = date
        };
        AddOpenResultAction(result, "Abrir " + adt);
        return result;
    }

    private static List<AssistantSearchResult> RankAssistantResults(IEnumerable<AssistantSearchResult> results, AssistantIntent intent)
    {
        return results
            .Select(result =>
            {
                var boost = 0d;
                if (!string.IsNullOrWhiteSpace(intent.Subject) && ContainsNormalized(string.Join(' ', result.Subject, result.Note, result.Title), intent.Subject)) boost += 3;
                if (!string.IsNullOrWhiteSpace(intent.RequestedAction) && ContainsNormalized(string.Join(' ', result.Note, result.Title), intent.RequestedAction)) boost += 2;
                if (!string.IsNullOrWhiteSpace(result.FilePath) && File.Exists(result.FilePath)) boost += 1;
                result.Score = Math.Min(100, Math.Max(result.Score, result.Confidence) + boost);
                result.Confidence = Math.Min(100, result.Confidence + boost);
                return result;
            })
            .OrderByDescending(x => x.Confidence >= 78)
            .ThenByDescending(x => x.Score)
            .ThenByDescending(x => x.BulletinDate ?? DateTime.MinValue)
            .ThenBy(x => x.Page ?? int.MaxValue)
            .ToList();
    }

    private static string FormatResultCard(AssistantSearchResult result)
    {
        var lines = new List<string>();
        lines.Add(result.Title);
        lines.Add($"Tipo: {FirstNonEmpty(result.Kind, result.Type, "Documento")}");
        if (!string.IsNullOrWhiteSpace(result.MilitaryName)) lines.Add("Militar: " + result.MilitaryName);
        if (!string.IsNullOrWhiteSpace(result.Subject)) lines.Add("Assunto: " + result.Subject);
        if (!string.IsNullOrWhiteSpace(result.Note) && result.Note != "—") lines.Add("Nota/Tipo: " + result.Note);
        if (result.Page is not null) lines.Add("Página: " + result.Page.Value.ToString(CultureInfo.InvariantCulture));
        lines.Add("Origem: " + FirstNonEmpty(result.Source, result.Module, "SIGFUR"));
        if (result.Confidence > 0) lines.Add($"Confiança: {result.Confidence:0}%");
        return string.Join(Environment.NewLine, lines.Select(x => "• " + x));
    }

    private static string BuildBulletinLine(SisbolPersonIndexItem row)
        => BuildResultLine("BI", row.BulletinDisplay, row.BulletinDate, row.MainSubjectDisplay, row.NoteDisplay, row.BulletinPage ?? 0);

    private static string BuildResultLine(string kind, string number, DateTime? date, string subject, string note, int page)
    {
        var parts = new List<string>();
        parts.Add(SafeReference(number, kind));
        if (date is not null) parts.Add(date.Value.ToString("dd/MM/yyyy"));
        if (!string.IsNullOrWhiteSpace(subject) && subject != "—" && !subject.Equals("Assunto não identificado", StringComparison.OrdinalIgnoreCase)) parts.Add(subject);
        if (!string.IsNullOrWhiteSpace(note) && note != "—" && !note.Equals(subject, StringComparison.OrdinalIgnoreCase)) parts.Add(note);
        if (page > 0) parts.Add("pág. " + page.ToString(CultureInfo.InvariantCulture));
        return string.Join(" — ", parts);
    }

    private static string SafeReference(string reference, string fallbackKind)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference == "—") return fallbackKind;
        return reference.Trim().StartsWith(fallbackKind, StringComparison.OrdinalIgnoreCase) ? reference.Trim() : $"{fallbackKind} {reference.Trim()}";
    }

    private static string BuildAdtDisplay(string bulletin)
    {
        var value = string.IsNullOrWhiteSpace(bulletin) ? "ADT" : bulletin.Trim();
        return value.StartsWith("ADT", StringComparison.OrdinalIgnoreCase) ? value : "ADT " + value;
    }

    private static void AddOpenResultAction(AssistantSearchResult result, string title)
    {
        if (string.IsNullOrWhiteSpace(result.FilePath) || !File.Exists(result.FilePath)) return;
        var action = AssistantActionRegistry.OpenFile(title, result.FilePath, result.Title);
        action.Payload["display"] = title;
        result.Actions.Add(action);
    }

    private static bool FurrielFileMatchesIntent(FurrielBulletinFile file, AssistantIntent intent)
    {
        if (intent.AdtNumber > 0 && !BulletinNumberMatches(file.Bulletin, intent.AdtNumber.ToString(CultureInfo.InvariantCulture), intent.Year)) return false;
        if (intent.Year > 0 && ParseDate(file.Date)?.Year != intent.Year) return false;
        if (intent.Month > 0 && ParseDate(file.Date)?.Month != intent.Month) return false;
        return true;
    }

    private static bool MentionMatchesMilitary(BulletinMentionItem mention, MilitaryRecord military)
    {
        if (mention.MilitaryId.HasValue && mention.MilitaryId.Value == military.Id) return true;
        var mentionCpf = AssistantIntentDetector.Digits(mention.MentionedMilitaryCpf);
        var mentionPrec = AssistantIntentDetector.Digits(mention.MentionedMilitaryPrecCp);
        var cpf = AssistantIntentDetector.Digits(military.Cpf);
        var prec = AssistantIntentDetector.Digits(military.PrecCp);
        if (!string.IsNullOrWhiteSpace(cpf) && mentionCpf == cpf) return true;
        if (!string.IsNullOrWhiteSpace(prec) && mentionPrec == prec) return true;
        var mentionedName = AssistantIntentDetector.Normalize(mention.MentionedMilitaryName);
        var fullName = AssistantIntentDetector.Normalize(military.Name);
        return !string.IsNullOrWhiteSpace(mentionedName) && mentionedName.Equals(fullName, StringComparison.Ordinal);
    }

    private static bool MentionMatchesSubjectOrAction(BulletinMentionItem mention, string subject, string action)
    {
        var text = AssistantIntentDetector.Normalize(string.Join(' ', mention.Subject, mention.NoteTitle, mention.SubjectNoteDisplay, mention.NoteText, mention.NoteExcerpt));
        return SubjectActionTextMatches(text, subject, action);
    }

    private static bool LineMatchesSubjectOrAction(FurrielIndexedLine line, string subject, string action)
    {
        var text = AssistantIntentDetector.Normalize(string.Join(' ', line.Subject, line.Major, line.Text, line.Normalized));
        return SubjectActionTextMatches(text, subject, action);
    }

    private static bool SubjectActionTextMatches(string normalizedText, string subject, string action)
    {
        var subjectTerms = AssistantIntentDetector.Normalize(subject).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var actionTerms = AssistantIntentDetector.Normalize(action).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var subjectOk = subjectTerms.Length == 0 || subjectTerms.All(term => normalizedText.Contains(term, StringComparison.Ordinal));
        var actionOk = actionTerms.Length == 0 || actionTerms.All(term => normalizedText.Contains(term, StringComparison.Ordinal));
        return subjectOk && actionOk;
    }

    private static bool MatchesSubjectOrAction(SisbolPersonIndexItem row, string subject, string action)
    {
        var text = AssistantIntentDetector.Normalize(string.Join(' ', row.MainSubject, row.SubSubject, row.SubjectNote, row.SearchText));
        return SubjectActionTextMatches(text, subject, action);
    }

    private static bool ContainsNormalized(string text, string term)
    {
        var normalized = AssistantIntentDetector.Normalize(text);
        var terms = AssistantIntentDetector.Normalize(term).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.Length == 0 || terms.All(x => normalized.Contains(x, StringComparison.Ordinal));
    }

    private static string ResultKey(AssistantSearchResult result)
        => string.Join("|", result.Kind, result.BulletinNumber, result.BulletinDate?.ToString("yyyyMMdd") ?? string.Empty, result.Page?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, result.Subject, result.Note, result.FilePath);

    private static string FirstExisting(params string[] paths)
        => paths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
           ?? paths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
           ?? string.Empty;

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && value != "—") ?? string.Empty;

    private static string Shorten(string value, int length)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = Regex.Replace(value.Trim(), "\\s+", " ");
        return text.Length <= length ? text : text[..length].Trim() + "...";
    }

    private async Task<AssistantApiResult> HandlePersonFullSummaryAsync(string prompt, AssistantIntent intent, CancellationToken ct)
    {
        var resolved = await ResolveSingleMilitaryAsync(prompt, ct);
        if (resolved.Status != ResolveStatus.Resolved) return BuildResolveProblem(resolved, "montar resumo completo");
        var m = resolved.Military!;
        var documents = await _military.GetDocumentsAsync(m.Id, ct);
        var paystubs = (await _paystubs.FindForMilitaryAsync(m, ct)).OrderByDescending(x => x.ModifiedAt).Take(6).ToList();
        var indexRows = (await _personIndex.FindForMilitaryAsync(m, ct)).Take(8).ToList();
        var folder = ResolvePersonFolder(m);

        var lines = new List<string>
        {
            $"Resumo SIGFUR — {m.ShortRank} {m.Name}",
            string.IsNullOrWhiteSpace(m.WarName) ? "Nome de guerra: não informado" : $"Nome de guerra: {m.WarName}",
            $"Auxílio-transporte cadastrado: {m.TransportStatus}" + (string.IsNullOrWhiteSpace(m.TransportAidValue) ? string.Empty : $" — valor: {m.TransportAidValue}"),
            $"PNR: {m.PnrStatus}",
            $"Foto: {m.PhotoStatus}",
            string.Empty,
            $"Documentos da carteira: {documents.Count}",
            $"Contracheques salvos localizados: {paystubs.Count}",
            $"Notas no Índice por Pessoa: {indexRows.Count}",
            string.Empty,
            "Últimos contracheques:"
        };
        foreach (var file in paystubs.Take(5)) lines.Add($"• {file.Reference} — {file.FileName}");
        if (paystubs.Count == 0) lines.Add("• Nenhum contracheque salvo localizado.");
        lines.Add("\nÚltimas notas/boletins:");
        foreach (var row in indexRows.Take(5)) lines.Add($"• {row.BulletinDisplay} — {row.DateText} — {row.MainSubjectDisplay} — {row.NoteDisplay}");
        if (indexRows.Count == 0) lines.Add("• Nenhuma nota localizada no Índice por Pessoa.");
        lines.Add("\nFontes: Banco de militares, carteira/documentos, contracheques salvos e Índice por Pessoa.");

        var actions = new List<AssistantPendingAction>
        {
            AssistantActionRegistry.OpenWallet(m),
            AssistantActionRegistry.OpenFolder("Abrir pasta individual", folder),
            AssistantActionRegistry.CopyText("Copiar resumo", string.Join(Environment.NewLine, lines))
        };
        if (paystubs.FirstOrDefault(x => File.Exists(x.Path)) is { } firstPaystub)
            actions.Add(AssistantActionRegistry.OpenFile("Abrir último contracheque", firstPaystub.Path));
        foreach (var row in indexRows.Where(x => !string.IsNullOrWhiteSpace(x.SourcePdfPath)).Take(3))
            actions.Add(BuildOpenBulletinAction(row));

        return new AssistantApiResult { Text = string.Join(Environment.NewLine, lines), PendingActions = actions, ToolSummaries = ["Banco de militares", "Carteira", "Contracheques", "Índice por Pessoa"] };
    }

    private async Task<AssistantApiResult> HandleTransportConferenceAsync(string prompt, AssistantIntent intent, CancellationToken ct)
    {
        var resolved = await ResolveSingleMilitaryAsync(prompt, ct, allowNoPerson: true, requireStrongPerson: true);
        if (resolved.Military is null)
        {
            var query = new SisbolPersonIndexQuery
            {
                SubjectOrNote = string.IsNullOrWhiteSpace(intent.Subject) ? "Despesa a Anular Auxílio-Transporte" : intent.Subject,
                Year = intent.Year > 0 ? intent.Year.ToString(CultureInfo.InvariantCulture) : "Todos",
                Month = intent.Month > 0 ? MonthName(intent.Month) : "Todos"
            };
            var rows = (await _personIndex.SearchAsync(query, 50, ct)).ToList();
            var text = new StringBuilder();
            text.AppendLine($"Conferência preliminar — {query.SubjectOrNote}");
            text.AppendLine();
            text.AppendLine($"Encontrei {rows.Count} nota(s) no Índice por Pessoa com esse assunto/filtro.");
            foreach (var row in rows.Take(12)) text.AppendLine($"• {row.BulletinDisplay} — {row.DateText} — {row.PeopleDisplay} — {row.MainSubjectDisplay} / {row.NoteDisplay}");
            text.AppendLine();
            text.AppendLine("Status: conferência preliminar. Para conferir militar por militar com valor, informe a lista da DA ou o nome do militar. O modo local não altera dados.");
            return new AssistantApiResult
            {
                Text = text.ToString(),
                PendingActions = rows.Where(x => !string.IsNullOrWhiteSpace(x.SourcePdfPath)).Take(6).Select(x => BuildOpenBulletinAction(x)).ToList(),
                ToolSummaries = ["Índice por Pessoa", "Conferência preliminar DA/AT"]
            };
        }

        var m = resolved.Military;
        var notes = (await _personIndex.FindForMilitaryAsync(m, ct)).Where(x => MatchesSubject(x, "Despesa a Anular") || MatchesSubject(x, "Auxílio-Transporte")).Take(12).ToList();
        var lines = new List<string>
        {
            $"Conferência preliminar de Auxílio-Transporte/DA — {m.ShortRank} {m.Name}",
            string.Empty,
            $"Situação na carteira: {m.TransportStatus}",
            $"Valor cadastrado: {(string.IsNullOrWhiteSpace(m.TransportAidValue) ? "não informado" : m.TransportAidValue)}",
            $"Notas AT/DA no Índice por Pessoa: {notes.Count}",
            string.Empty
        };
        foreach (var row in notes) lines.Add($"• {row.BulletinDisplay} — {row.DateText} — {row.MainSubjectDisplay} — {row.NoteDisplay}");
        if (notes.Count == 0) lines.Add("• Nenhuma nota de AT/DA localizada no Índice por Pessoa para esse militar.");
        lines.Add("\nStatus: conferência objetiva local. Para valor exato da DA por dia/pernoite, use a lista da DA ou o módulo Auxílio-Transporte.");
        lines.Add("Fontes: Carteira do militar e Índice por Pessoa.");

        var actions = new List<AssistantPendingAction> { AssistantActionRegistry.OpenWallet(m) };
        foreach (var row in notes.Where(x => !string.IsNullOrWhiteSpace(x.SourcePdfPath)).Take(5)) actions.Add(BuildOpenBulletinAction(row));
        return new AssistantApiResult { Text = string.Join(Environment.NewLine, lines), PendingActions = actions, ToolSummaries = ["Carteira", "Índice por Pessoa"] };
    }

    private async Task<AssistantApiResult> HandleGeneratedFilesAsync(string prompt, CancellationToken ct)
    {
        var root = _paths.GeneratedDocumentsDirectory;
        if (!Directory.Exists(root)) return TextOnly("A pasta de documentos gerados ainda não existe.", "Documentos gerados");
        var terms = AssistantIntentDetector.Normalize(prompt).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length >= 3).ToList();
        var files = await Task.Run(() => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => terms.Count == 0 || terms.All(term => AssistantIntentDetector.Normalize(Path.GetFileName(path)).Contains(term, StringComparison.Ordinal)))
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTime)
            .Take(12)
            .ToList(), ct);
        var lines = new List<string> { $"Localizei {files.Count} arquivo(s) gerado(s) no SIGFUR:" };
        var actions = new List<AssistantPendingAction>();
        foreach (var file in files)
        {
            lines.Add($"• {file.Name} — {file.LastWriteTime:dd/MM/yyyy HH:mm}");
            actions.Add(AssistantActionRegistry.OpenFile("Abrir " + file.Name, file.FullName));
        }
        actions.Add(AssistantActionRegistry.OpenFolder("Abrir pasta de documentos gerados", root));
        lines.Add("\nFonte: pasta oficial de documentos gerados.");
        return new AssistantApiResult { Text = string.Join(Environment.NewLine, lines), PendingActions = actions, ToolSummaries = ["Documentos gerados"] };
    }

    private AssistantApiResult TextOnly(string text, string source) => new() { Text = text, ToolSummaries = [source] };

    private async Task<ResolveResult> ResolveSingleMilitaryAsync(string prompt, CancellationToken ct, bool allowNoPerson = false, bool requireStrongPerson = false)
    {
        var candidates = await ResolveMilitaryCandidatesAsync(prompt, ct);
        if (candidates.Count == 0) return new ResolveResult { Status = ResolveStatus.NotFound };

        var best = candidates[0];
        if (requireStrongPerson && !IsStrongPersonResolution(prompt, best.Military, candidates))
            return allowNoPerson ? new ResolveResult { Status = ResolveStatus.NotFound } : new ResolveResult { Status = ResolveStatus.NotFound };

        var ambiguous = candidates.Count > 1 && candidates[1].Score >= best.Score - 8 && candidates[1].Score >= 50;
        if (ambiguous) return new ResolveResult { Status = ResolveStatus.Ambiguous, Candidates = candidates.Take(8).Select(x => x.Military).ToList() };
        return new ResolveResult { Status = ResolveStatus.Resolved, Military = best.Military };
    }

    private async Task<List<(MilitaryRecord Military, int Score)>> ResolveMilitaryCandidatesAsync(string prompt, CancellationToken ct)
    {
        var query = AssistantIntentDetector.Normalize(RemoveOperationalWords(prompt));
        var queryDigits = AssistantIntentDetector.Digits(prompt);
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length >= 3 && !StopWords.Contains(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var people = await _military.GetAllAsync(ct);
        return people.Select(m => (Military: m, Score: ScoreMilitary(m, query, queryDigits, tokens)))
            .Where(x => x.Score >= 35)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => MilitaryRankService.GetOrder(x.Military.Rank))
            .ThenBy(x => x.Military.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(20)
            .ToList();
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "tem", "nota", "notas", "sobre", "abre", "abrir", "mostra", "mostrar", "pesquisa", "pesquisar", "procura", "procurar", "localiza", "localizar", "contracheque", "contracheques", "ficha", "financeira", "boletim", "boletins", "bi", "adt", "aditamento", "furriel", "auxilio", "aux", "natalidade", "transporte", "despesa", "anular", "ordem", "saque", "atrasados", "ferias", "férias", "regulamentares", "apresentacao", "apresentação", "concessao", "concessão", "carteira", "pasta", "rota", "confere", "conferir", "conferencia", "conferência", "divergencia", "divergência", "militar", "militares", "sgt", "sargento", "soldado", "sd", "cabo", "cb", "tenente", "subtenente", "sub", "ten", "st", "s", "queria", "quero", "saber", "qual", "quais", "numero", "número", "esta", "está", "inclusao", "inclusão", "incluido", "incluído", "incluida", "incluída", "publicacao", "publicação", "do", "da", "de", "dos", "das", "o", "a", "os", "as", "em", "que", "pra", "para", "no", "na", "nos", "nas", "janeiro", "fevereiro", "marco", "março", "abril", "maio", "junho", "julho", "agosto", "setembro", "outubro", "novembro", "dezembro"
    };

    private static string RemoveOperationalWords(string prompt)
    {
        var value = AssistantIntentDetector.Normalize(prompt);
        foreach (var word in StopWords.OrderByDescending(x => x.Length))
            value = Regex.Replace(value, $"\\b{Regex.Escape(word)}\\b", " ", RegexOptions.CultureInvariant);
        return Regex.Replace(value, "\\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static int ScoreMilitary(MilitaryRecord m, string query, string queryDigits, IReadOnlyList<string> tokens)
    {
        var name = AssistantIntentDetector.Normalize(m.Name);
        var war = AssistantIntentDetector.Normalize(m.WarName);
        var rank = AssistantIntentDetector.Normalize(m.Rank);
        var all = string.Join(' ', rank, name, war);
        var cpf = AssistantIntentDetector.Digits(m.Cpf);
        var prec = AssistantIntentDetector.Digits(m.PrecCp);
        var identity = AssistantIntentDetector.Digits(m.MilitaryId);
        var score = 0;
        if (queryDigits.Length >= 5)
        {
            if (!string.IsNullOrWhiteSpace(cpf) && cpf.Contains(queryDigits, StringComparison.Ordinal)) score += 120;
            if (!string.IsNullOrWhiteSpace(prec) && prec.Contains(queryDigits, StringComparison.Ordinal)) score += 115;
            if (!string.IsNullOrWhiteSpace(identity) && identity.Contains(queryDigits, StringComparison.Ordinal)) score += 110;
        }
        if (!string.IsNullOrWhiteSpace(query))
        {
            if (name == query) score += 120;
            if (!string.IsNullOrWhiteSpace(war) && war == query) score += 115;
            if (name.Contains(query, StringComparison.Ordinal)) score += 80;
            if (!string.IsNullOrWhiteSpace(war) && war.Contains(query, StringComparison.Ordinal)) score += 78;
        }
        foreach (var token in tokens)
        {
            if (name.Split(' ').Contains(token)) score += 28;
            else if (name.Contains(token, StringComparison.Ordinal)) score += 16;
            if (!string.IsNullOrWhiteSpace(war) && war.Split(' ').Contains(token)) score += 34;
            else if (!string.IsNullOrWhiteSpace(war) && war.Contains(token, StringComparison.Ordinal)) score += 18;
            if (all.Contains(token, StringComparison.Ordinal)) score += 4;
        }
        if (tokens.Count > 0 && tokens.All(t => all.Contains(t, StringComparison.Ordinal))) score += 25;
        return score;
    }

    private AssistantApiResult BuildResolveProblem(ResolveResult result, string operation)
    {
        if (result.Status == ResolveStatus.Ambiguous)
        {
            var lines = new List<string> { $"Encontrei mais de um militar possível para {operation}. Escolha pela ação abaixo:" };
            var actions = new List<AssistantPendingAction>();
            foreach (var m in result.Candidates.Take(8))
            {
                lines.Add($"• {m.ShortRank} {m.Name}" + (string.IsNullOrWhiteSpace(m.WarName) ? string.Empty : $" — {m.WarName}"));
                actions.Add(AssistantActionRegistry.OpenWallet(m));
            }
            return new AssistantApiResult { Text = string.Join(Environment.NewLine, lines), PendingActions = actions, ToolSummaries = ["Banco de militares"] };
        }
        return new AssistantApiResult { Text = $"Não localizei o militar para {operation}. Pesquisei por nome, nome de guerra, CPF, Prec-CP e identidade no banco do SIGFUR.", ToolSummaries = ["Banco de militares"] };
    }

    private static bool IsStrongPersonResolution(string prompt, MilitaryRecord best, IReadOnlyList<(MilitaryRecord Military, int Score)> candidates)
    {
        var digits = AssistantIntentDetector.Digits(prompt);
        if (digits.Length >= 5)
        {
            var cpf = AssistantIntentDetector.Digits(best.Cpf);
            var prec = AssistantIntentDetector.Digits(best.PrecCp);
            var idt = AssistantIntentDetector.Digits(best.MilitaryId);
            if ((!string.IsNullOrWhiteSpace(cpf) && cpf.Contains(digits, StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(prec) && prec.Contains(digits, StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(idt) && idt.Contains(digits, StringComparison.Ordinal)))
                return true;
        }

        var tokens = ExtractPersonTokens(prompt);
        var fullName = AssistantIntentDetector.Normalize(best.Name);
        var warName = AssistantIntentDetector.Normalize(best.WarName);
        var fullNameTokenHits = tokens.Count(token => fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(token));

        if (tokens.Count >= 2 && fullNameTokenHits >= 2) return true;
        if (tokens.Count >= 3 && tokens.Count(token => fullName.Contains(token, StringComparison.Ordinal)) >= 2) return true;

        // Nome de guerra isolado não confirma identidade do militar.
        if (tokens.Count == 1 && !string.IsNullOrWhiteSpace(warName) && warName.Equals(tokens[0], StringComparison.Ordinal))
            return false;

        return false;
    }

    private static string BuildPersonIndexSearch(string prompt, string subject)
    {
        var tokens = ExtractPersonTokens(prompt);
        if (tokens.Count == 0)
        {
            var cleaned = AssistantIntentDetector.Normalize(prompt);
            var subjectTokens = AssistantIntentDetector.Normalize(subject).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var token in subjectTokens)
                cleaned = Regex.Replace(cleaned, $"\\b{Regex.Escape(token)}\\b", " ", RegexOptions.CultureInvariant);
            tokens = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => x.Length >= 3 && !StopWords.Contains(x))
                .Distinct(StringComparer.Ordinal)
                .Take(6)
                .ToList();
        }
        return string.Join(' ', tokens.Take(6));
    }


    private static List<string> ExtractIntentPersonTokens(AssistantIntent intent, string prompt)
    {
        var seed = string.IsNullOrWhiteSpace(intent.PersonTerm) ? prompt : intent.PersonTerm;
        var tokens = ExtractPersonTokens(seed);
        return tokens.Count > 0 ? tokens : ExtractPersonTokens(prompt);
    }

    private static List<string> ExtractPersonTokens(string prompt)
    {
        var cleaned = RemoveOperationalWords(prompt);
        return cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length >= 3 && !StopWords.Contains(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private string ResolvePersonFolder(MilitaryRecord m)
    {
        var root = PersonDocumentStorageService.ResolveConfiguredRoot(_paths);
        return PersonDocumentStorageService.BuildFolder(root, m.Rank, m.Name, m.Cpf, m.PrecCp, external: false);
    }

    private static bool MatchesSubject(SisbolPersonIndexItem row, string subject)
    {
        var text = AssistantIntentDetector.Normalize(string.Join(' ', row.MainSubject, row.SubSubject, row.SubjectNote));
        var terms = AssistantIntentDetector.Normalize(subject).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.Length == 0 || terms.All(term => text.Contains(term, StringComparison.Ordinal));
    }

    private static string ExtractSubjectFromPrompt(string prompt)
    {
        var normalized = AssistantIntentDetector.Normalize(prompt);
        if (AssistantIntentDetector.ContainsAny(normalized, "auxilio natalidade", "aux natalidade", "natalidade")) return "Auxílio-Natalidade";
        if (AssistantIntentDetector.ContainsAny(normalized, "auxilio transporte", "aux transporte")) return "Auxílio-Transporte";
        if (AssistantIntentDetector.ContainsAny(normalized, "despesa a anular")) return "Despesa a Anular";
        if (AssistantIntentDetector.ContainsAny(normalized, "ferias regulamentares")) return "Férias Regulamentares";
        if (AssistantIntentDetector.ContainsAny(normalized, "ferias")) return "Férias";
        if (AssistantIntentDetector.ContainsAny(normalized, "gratificacao", "representacao")) return "Gratificação";
        return string.Empty;
    }

    private static AssistantPendingAction BuildOpenBulletinAction(SisbolPersonIndexItem row)
    {
        var label = BuildBulletinLinkLabel(row);
        var description = $"Abrir PDF salvo: {row.SourceFileName}";
        if (!string.IsNullOrWhiteSpace(row.PageText) && row.PageText != "—")
            description += $" • página {row.PageText}";
        return AssistantActionRegistry.OpenFile(label, row.SourcePdfPath, description);
    }

    private static string BuildBulletinLinkLabel(SisbolPersonIndexItem row)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.BulletinDisplay) && row.BulletinDisplay != "—") parts.Add(row.BulletinDisplay);
        if (!string.IsNullOrWhiteSpace(row.DateText) && row.DateText != "—") parts.Add(row.DateText);

        var subject = row.DisplaySubjectNote;
        if (!string.IsNullOrWhiteSpace(subject) && !subject.Equals("Assunto não identificado", StringComparison.OrdinalIgnoreCase))
            parts.Add(subject);
        else
        {
            var subjectNote = string.Join(" — ", new[] { row.MainSubjectDisplay, row.NoteDisplay }
                .Where(x => !string.IsNullOrWhiteSpace(x) && x != "—" && !x.Equals("Assunto não identificado", StringComparison.OrdinalIgnoreCase)));
            if (!string.IsNullOrWhiteSpace(subjectNote)) parts.Add(subjectNote);
        }

        if (!string.IsNullOrWhiteSpace(row.PageText) && row.PageText != "—") parts.Add("pág. " + row.PageText);
        return parts.Count == 0 ? "Abrir boletim" : string.Join(" — ", parts);
    }

    private static string MonthName(int month)
    {
        var names = CultureInfo.GetCultureInfo("pt-BR").DateTimeFormat.MonthNames;
        return month is >= 1 and <= 12 ? names[month - 1] : "Todos";
    }

    private enum ResolveStatus { Resolved, NotFound, Ambiguous }

    private sealed class ResolveResult
    {
        public ResolveStatus Status { get; set; }
        public MilitaryRecord? Military { get; set; }
        public List<MilitaryRecord> Candidates { get; set; } = [];
    }
}
