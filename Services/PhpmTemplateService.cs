using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Security;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UglyToad.PdfPig;
using SIGFUR.Wpf.Controls;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class PhpmTemplateService
{
    private static readonly Regex PlaceholderRegex = new(@"\{\{([A-Za-z0-9_\-]+)\}\}|\[\[([A-Za-z0-9_\-]+)\]\]|<<([A-Za-z0-9_\-]+)>>|\$([A-Za-z0-9_\-]+)\$|\{([A-Za-z0-9_\-]+)\}", RegexOptions.Compiled);
    private static readonly HashSet<string> RichNameFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "NOME", "NOME_COMPLETO", "NOME_FORMATADO", "PG_NOME", "MILITAR_NOME", "DECL_NOME", "BENEF_NOME"
    };
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly LogService _log;
    private readonly Dictionary<string, (DateTime LastWrite, int Pages)> _templatePageCounts = new(StringComparer.OrdinalIgnoreCase);

    public PhpmTemplateService(AppPaths paths, JsonFileService json, LogService log)
    {
        _paths = paths;
        _json = json;
        _log = log;
        Directory.CreateDirectory(_paths.PhpmTemplatesDirectory);
        Directory.CreateDirectory(_paths.PhpmOutputDirectory);
        EnsureBuiltInTemplateFiles();
    }

    private void EnsureBuiltInTemplateFiles()
    {
        foreach (var fileName in new[]
        {
            "PHPM_CAPA_TEMPLATE.docx",
            "PHPM_DECL_BENEF_TEMPLATE.odt",
            "PHPM_PRE_ESCOLAR_TEMPLATE.odt",
            "PHPM_CADEBEN_FUSEX_TEMPLATE.odt",
            "PHPM_INDICE_REMISSIVO_TEMPLATE.odt"
        })
        {
            try
            {
                var candidates = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "templates", "docs", fileName),
                    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "templates", "docs", fileName))
                };
                var source = candidates.FirstOrDefault(File.Exists);
                if (string.IsNullOrWhiteSpace(source)) continue;
                var target = Path.Combine(_paths.PhpmTemplatesDirectory, fileName);
                if (!File.Exists(target) || !FilesMatch(source, target))
                    File.Copy(source, target, true);
            }
            catch (Exception ex)
            {
                _ = _log.WriteAsync($"Falha ao instalar template PHPM {fileName}.", ex);
            }
        }
    }

    private static bool FilesMatch(string first, string second)
    {
        if (new FileInfo(first).Length != new FileInfo(second).Length) return false;
        using var firstStream = File.OpenRead(first);
        using var secondStream = File.OpenRead(second);
        return SHA256.HashData(firstStream).AsSpan().SequenceEqual(SHA256.HashData(secondStream));
    }

    public async Task<PhpmTemplateCatalog> LoadCatalogAsync()
    {
        var catalog = await _json.LoadAsync<PhpmTemplateCatalog>(_paths.PhpmCatalogFile) ?? new PhpmTemplateCatalog();
        foreach (var saved in catalog.Templates.Where(item => !string.IsNullOrWhiteSpace(item.TemplatePath) && !File.Exists(item.TemplatePath)))
        {
            var migrated = Path.Combine(_paths.UnifiedTemplatesDirectory, Path.GetFileName(saved.TemplatePath));
            if (File.Exists(migrated)) saved.TemplatePath = migrated;
        }
        var defaults = BuiltInTemplates();
        foreach (var item in defaults)
        {
            item.Placeholders = await ExtractPlaceholdersAsync(item.TemplatePath);
            item.UpdatedAt = File.Exists(item.TemplatePath) ? File.GetLastWriteTime(item.TemplatePath) : DateTime.Now;
            var existing = catalog.Templates.FirstOrDefault(x => x.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is null) catalog.Templates.Add(item);
            else
            {
                existing.IsBuiltIn = true;
                existing.Title = item.Title;
                existing.Description = item.Description;
                // Para os modelos principais, o caminho oficial do módulo prevalece
                // quando o vínculo antigo está vazio/quebrado ou apontando fora da pasta PHPM.
                existing.TemplatePath = item.TemplatePath;
                existing.Placeholders = item.Placeholders;
                existing.UpdatedAt = item.UpdatedAt;
            }
        }
        await ImportLegacyTemplatesAsync(catalog);
        catalog.Templates = catalog.Templates.OrderByDescending(x => x.IsBuiltIn).ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        await SaveCatalogAsync(catalog);
        return catalog;
    }

    public async Task<PhpmPublicationReferenceCatalog> LoadPublicationReferencesAsync(CancellationToken cancellationToken = default)
    {
        var bulletinTask = _json.LoadAsync<IntelligentBulletinStore>(_paths.BulletinIndexFile);
        var aditamentTask = _json.LoadAsync<FurrielIndexStore>(_paths.FurrielIndexFile);
        await Task.WhenAll(bulletinTask, aditamentTask);

        var bulletins = (await bulletinTask)?.Items ?? [];
        var aditaments = (await aditamentTask)?.Files ?? [];

        return new PhpmPublicationReferenceCatalog
        {
            Bulletins = bulletins
                .Select(item => (Text: FormatBulletinReference(item.BulletinNumber, item.BulletinDate, item.DateIso), Date: ParsePublicationDate(item.DateIso, item.BulletinDate), Number: PublicationNumber(item.BulletinNumber)))
                .Where(item => !string.IsNullOrWhiteSpace(item.Text))
                .OrderByDescending(item => item.Date)
                .ThenByDescending(item => item.Number)
                .Select(item => item.Text)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            Aditaments = aditaments
                .Select(item => (Text: FormatAditamentReference(item.Bulletin, item.Bar, item.Date), Date: ParsePublicationDate(item.Date), Number: PublicationNumber(item.Bulletin)))
                .Where(item => !string.IsNullOrWhiteSpace(item.Text))
                .OrderByDescending(item => item.Date)
                .ThenByDescending(item => item.Number)
                .Select(item => item.Text)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList()
        };
    }

    private static string FormatBulletinReference(string? number, string? displayDate, string? isoDate)
    {
        var publicationNumber = CanonicalPublicationNumber(number);
        var date = ParsePublicationDate(isoDate, displayDate);
        return publicationNumber.Length == 0 || date is null
            ? string.Empty
            : $"BI Nr {publicationNumber}, de {FormatMilitaryPublicationDate(date.Value)}, {OrganizationIdentity.BulletinIssuer}";
    }

    private static string FormatAditamentReference(string? number, string? bar, string? displayDate)
    {
        var publicationNumber = CanonicalPublicationNumber(number);
        var date = ParsePublicationDate(displayDate);
        if (publicationNumber.Length == 0 || date is null) return string.Empty;
        var barText = CanonicalPublicationNumber(bar);
        return $"Adt Furr Nr {publicationNumber}{(barText.Length == 0 ? string.Empty : $" BAR {barText}")}, de {FormatMilitaryPublicationDate(date.Value)}, {OrganizationIdentity.BulletinIssuer}";
    }

    private static string FormatMilitaryPublicationDate(DateTime date)
        => date.ToString("dd MMM yy", CultureInfo.GetCultureInfo("pt-BR")).Replace(".", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static DateTime? ParsePublicationDate(params string?[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            foreach (var format in new[] { "yyyy-MM-dd", "dd/MM/yyyy", "dd-MM-yyyy", "d/M/yyyy", "d-M-yyyy" })
                if (DateTime.TryParseExact(value.Trim(), format, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var exact))
                    return exact.Date;
            if (DateTime.TryParse(value, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var parsed))
                return parsed.Date;
        }
        return null;
    }

    private static string CanonicalPublicationNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() is "—" or "-") return string.Empty;
        var match = Regex.Match(value, @"\d+");
        return match.Success ? match.Value : string.Empty;
    }

    private static int PublicationNumber(string? value)
        => int.TryParse(CanonicalPublicationNumber(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;

    public Task SaveCatalogAsync(PhpmTemplateCatalog catalog) => _json.SaveAsync(_paths.PhpmCatalogFile, catalog);

    private async Task ImportLegacyTemplatesAsync(PhpmTemplateCatalog catalog)
    {
        // Reaproveita exatamente os modelos já cadastrados pela versão Python no
        // %LOCALAPPDATA%\SIGFUR, sem obrigar o usuário a vincular tudo novamente.
        var defaultsPath = Path.Combine(_paths.DataDirectory, "phpm_defaults.json");
        if (File.Exists(defaultsPath))
        {
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(defaultsPath));
                if (document.RootElement.TryGetProperty("templates", out var templates) && templates.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in templates.EnumerateObject())
                    {
                        var path = ResolveLegacyPath(property.Value.GetString());
                        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                        var definition = catalog.Templates.FirstOrDefault(x => x.Id.Equals(property.Name, StringComparison.OrdinalIgnoreCase));
                        if (definition is not null)
                        {
                            // Os modelos oficiais novos do módulo PHPM prevalecem. A importação antiga
                            // só entra quando o modelo embutido não existir, evitando capa/índice vazio
                            // por vínculo herdado de versão anterior.
                            if (definition.IsBuiltIn && File.Exists(definition.TemplatePath)) continue;
                            definition.TemplatePath = path;
                            definition.Placeholders = await ExtractPlaceholdersAsync(path);
                            definition.UpdatedAt = File.GetLastWriteTime(path);
                        }
                    }
                }
            }
            catch (Exception ex) { await _log.WriteAsync("Falha ao importar templates padrão antigos do PHPM.", ex); }
        }

        var customIndex = Path.Combine(_paths.DataDirectory, "phpm_custom_templates.json");
        if (File.Exists(customIndex))
        {
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(customIndex));
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in document.RootElement.EnumerateArray())
                    {
                        var id = item.TryGetProperty("id", out var idNode) ? idNode.GetString() ?? string.Empty : string.Empty;
                        var path = item.TryGetProperty("file", out var fileNode) ? ResolveLegacyPath(fileNode.GetString()) : string.Empty;
                        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                        var existing = catalog.Templates.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                            || (!string.IsNullOrWhiteSpace(x.TemplatePath) && Path.GetFullPath(x.TemplatePath).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)));
                        if (existing is null)
                        {
                            var title = item.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
                            var description = item.TryGetProperty("desc", out var descNode) ? descNode.GetString() : null;
                            catalog.Templates.Add(new PhpmTemplateDefinition
                            {
                                Id = id,
                                Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(path) : title.Trim(),
                                Description = description?.Trim() ?? "Template personalizado importado da versão anterior.",
                                TemplatePath = path,
                                IsBuiltIn = false,
                                Placeholders = await ExtractPlaceholdersAsync(path),
                                UpdatedAt = File.GetLastWriteTime(path)
                            });
                        }
                        else if (string.IsNullOrWhiteSpace(existing.TemplatePath) || !File.Exists(existing.TemplatePath))
                        {
                            existing.TemplatePath = path;
                            existing.Placeholders = await ExtractPlaceholdersAsync(path);
                        }
                    }
                }
            }
            catch (Exception ex) { await _log.WriteAsync("Falha ao importar templates personalizados antigos do PHPM.", ex); }
        }

        // Modelos padrão embutidos pela versão Python.
        var legacyTemplateDirectory = Path.Combine(_paths.DataDirectory, "templates", "phpm");
        foreach (var definition in catalog.Templates.Where(x => x.IsBuiltIn && (!File.Exists(x.TemplatePath))))
        {
            var candidate = Path.Combine(legacyTemplateDirectory, Path.GetFileName(definition.TemplatePath));
            if (!File.Exists(candidate)) continue;
            definition.TemplatePath = candidate;
            definition.Placeholders = await ExtractPlaceholdersAsync(candidate);
            definition.UpdatedAt = File.GetLastWriteTime(candidate);
        }
    }

    private string ResolveLegacyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        path = Environment.ExpandEnvironmentVariables(path.Trim());
        return Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(_paths.DataDirectory, path));
    }


    public async Task<PhpmTemplateDefinition> ImportCustomTemplateAsync(string sourcePath, string title, string description)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Template não encontrado.", sourcePath);
        ValidateExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        var importSource = sourcePath;
        if (extension == ".doc")
        {
            importSource = await ConvertLegacyDocAsync(sourcePath);
            extension = ".docx";
        }
        var target = UniquePath(Path.Combine(_paths.PhpmTemplatesDirectory,
            "PHPM_PERSONALIZADO_" + SafeFileName(Path.GetFileNameWithoutExtension(sourcePath)) + extension));
        File.Copy(importSource, target, false);
        return new PhpmTemplateDefinition
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(sourcePath) : title.Trim(),
            Description = description.Trim(),
            TemplatePath = target,
            IsBuiltIn = false,
            Placeholders = await ExtractPlaceholdersAsync(target),
            UpdatedAt = DateTime.Now
        };
    }

    public async Task<string> AttachTemplateFileAsync(PhpmTemplateDefinition definition, string sourcePath)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Template não encontrado.", sourcePath);
        ValidateExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        var importSource = sourcePath;
        if (extension == ".doc")
        {
            importSource = await ConvertLegacyDocAsync(sourcePath);
            extension = ".docx";
        }
        var target = UniquePath(Path.Combine(_paths.PhpmTemplatesDirectory,
            SafeFileName(definition.Id + "_" + Path.GetFileNameWithoutExtension(sourcePath)) + extension));
        File.Copy(importSource, target, false);
        definition.TemplatePath = target;
        definition.Placeholders = await ExtractPlaceholdersAsync(target);
        definition.UpdatedAt = DateTime.Now;
        return target;
    }

    public async Task<List<string>> ExtractPlaceholdersAsync(string templatePath)
    {
        if (!File.Exists(templatePath)) return [];
        var ext = Path.GetExtension(templatePath).ToLowerInvariant();
        string text = ext switch
        {
            ".docx" => await Task.Run(() => ExtractDocxText(templatePath)),
            ".odt" => await Task.Run(() => ExtractOdtXml(templatePath)),
            ".doc" => string.Empty,
            _ => throw new InvalidOperationException("O PHPM aceita templates DOCX, ODT e DOC.")
        };
        return PlaceholderRegex.Matches(text)
            .Select(match => match.Groups.Cast<Group>().Skip(1).FirstOrDefault(group => group.Success)?.Value ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();
    }

    public Dictionary<string, string> BuildAutomaticFields(MilitaryRecord military, UiProfile profile)
    {
        var today = DateTime.Today;
        var shortRank = military.ShortRank;
        var formattedName = NameHighlightHelper.PlainDisplay(military.Name, military.WarName);
        var nameParts = SplitNameForTemplate(military.Name, military.WarName);
        var commander = string.Join(" ", new[] { profile.CommanderRank, profile.CommanderName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MILITAR_ID"] = military.Id.ToString(CultureInfo.InvariantCulture),
            ["POSTO_GRAD"] = military.Rank,
            ["POSTO_GRAD_ABREV"] = shortRank,
            ["PG"] = shortRank,
            ["NOME"] = military.Name,
            ["NOME_COMPLETO"] = military.Name,
            ["NOME_FORMATADO"] = formattedName,
            ["PG_NOME"] = $"{shortRank} {formattedName}".Trim(),
            ["NOME_GUERRA"] = military.WarName,
            ["NOME_ANTES_GUERRA"] = nameParts.Before,
            ["NOME_DEPOIS_GUERRA"] = nameParts.After,
            ["MILITAR_NOME"] = military.Name,
            ["MILITAR_PG_ABREV"] = shortRank,
            ["MILITAR_PREC_CP"] = military.PrecCp,
            ["MILITAR_IDT"] = military.MilitaryId,
            ["MILITAR_CPF"] = military.FormattedCpf,
            ["CAPA_NOME"] = military.Name,
            ["DECL_NOME"] = military.Name,
            ["DECL_IDT"] = military.MilitaryId,
            ["DECL_CPF"] = military.FormattedCpf,
            ["DECL_DATA_PRACA"] = military.EnlistmentDate,
            ["BENEF_NOME"] = military.Name,
            ["POSTO_ABREV"] = shortRank,
            ["CPF"] = military.FormattedCpf,
            ["CPF_NUMEROS"] = new string((military.Cpf ?? string.Empty).Where(char.IsDigit).ToArray()),
            ["PREC_CP"] = military.PrecCp,
            ["PREC-CP"] = military.PrecCp,
            ["IDENTIDADE"] = military.MilitaryId,
            ["IDT"] = military.MilitaryId,
            ["DATA_NASCIMENTO"] = military.BirthDate,
            ["DATA_PRACA"] = military.EnlistmentDate,
            ["ENDERECO"] = military.Address,
            ["CEP"] = military.ZipCode,
            ["TELEFONE"] = military.Phone,
            ["EMAIL"] = military.Email,
            ["BANCO"] = military.Bank,
            ["AGENCIA"] = military.Agency,
            ["CONTA"] = military.Account,
            ["OM"] = profile.Organization,
            ["OM_NOME"] = profile.Organization,
            ["UNIDADE_SERVINDO"] = profile.Organization,
            ["ORGANIZACAO_MILITAR"] = profile.Organization,
            ["CMT_OM"] = string.IsNullOrWhiteSpace(profile.Organization) ? string.Empty : $"Cmt da {profile.Organization}",
            ["COMANDANTE"] = commander,
            ["COMANDANTE_NOME"] = profile.CommanderName,
            ["COMANDANTE_POSTO"] = profile.CommanderRank,
            ["CMT_NOME_PG"] = commander,
            ["CMT_FUNCAO"] = string.IsNullOrWhiteSpace(profile.Organization) ? "Comandante" : $"Comandante da {profile.Organization}",
            ["OPERADOR"] = profile.Operator,
            ["POSTO_OPERADOR"] = profile.Rank,
            ["FUNCAO_OPERADOR"] = profile.Function,
            ["DATA"] = today.ToString("dd/MM/yyyy"),
            ["DATA_ATUAL"] = today.ToString("dd/MM/yyyy"),
            ["DATA_EXTENSO"] = DateToLongPortuguese(today),
            ["LOCAL_DATA"] = $"Belo Horizonte, MG, {DateToLongPortuguese(today)}.",
            ["ANO"] = today.Year.ToString(CultureInfo.InvariantCulture),
            ["ANO_DOC"] = today.Year.ToString(CultureInfo.InvariantCulture),
            ["SECAO"] = "SSPP",
            ["ASSUNTO_TEXTO"] = "PASTA DE HABILITAÇÃO À PENSÃO MILITAR",
            ["MES"] = today.ToString("MMMM", CultureInfo.GetCultureInfo("pt-BR")),
            ["RECEBE_AUX_TRANSPORTE"] = MilitaryRecord.IsYes(military.ReceivesTransportAid) ? "SIM" : "NÃO",
            ["VALOR_AUX_TRANSPORTE"] = MilitaryFormatting.FormatMoney(ParseMoney(military.TransportAidValue)),
            ["RECEBE_PRE_ESCOLAR"] = MilitaryRecord.IsYes(military.ReceivesPreSchool) ? "SIM" : "NÃO",
            ["VALOR_PRE_ESCOLAR"] = MilitaryFormatting.FormatMoney(ParseMoney(military.PreSchoolValue)),
            ["VALOR_RECEBER"] = MilitaryFormatting.FormatMoney(ParseMoney(military.PreSchoolValue)),
            ["POSSUI_PNR"] = MilitaryRecord.IsYes(military.HasPnr) ? "SIM" : "NÃO",
            ["PENSAO_JUDICIAL"] = MilitaryRecord.IsYes(military.Alimony) ? "SIM" : "NÃO"
        };
    }

    public IReadOnlyList<string> GetSuggestedFields(string templateId)
    {
        return templateId.ToLowerInvariant() switch
        {
            "phpm_capa" => ["MILITAR_PG_ABREV", "MILITAR_NOME"],
            "phpm_decl_benef" => ["DECL_NOME", "DECL_IDT", "DECL_CPF", "DECL_DATA_PRACA", "DECL_PAI", "DECL_MAE", "LOCAL_DATA", "CMT_NOME_PG", "CMT_FUNCAO"],
            "phpm_pre_escolar" => ["BENEF_NOME", "DEP_NOME", "DEP_DATA_NASC", "BI_PREESCOLAR", "FAIXA_REMUNERACAO", "COTA_PARTE", "VALOR_RECEBER", "ENDERECO", "CONJUGE_DETENTOR", "LOCAL_DATA", "MILITAR_PG_ABREV"],
            "phpm_fusex_cadeben" => ["MILITAR_PG_ABREV", "MILITAR_NOME", "MILITAR_IDT", "MILITAR_CPF", "MILITAR_PREC_CP", "LOCAL_DATA"],
            "phpm_indice_remissivo" => ["MILITAR_NOME", "MILITAR_PG_ABREV", "MILITAR_IDT", "MILITAR_PREC_CP"],
            _ => ["MILITAR_PG_ABREV", "MILITAR_NOME", "MILITAR_IDT", "MILITAR_CPF", "MILITAR_PREC_CP", "LOCAL_DATA"]
        };
    }

    public IReadOnlySet<string> GetRequiredFields(PhpmTemplateDefinition template)
    {
        IEnumerable<string> required = template.Id.ToLowerInvariant() switch
        {
            "phpm_capa" => ["MILITAR_PG_ABREV", "MILITAR_NOME"],
            "phpm_decl_benef" => ["DECL_NOME", "DECL_IDT", "DECL_CPF", "DECL_DATA_PRACA", "DECL_PAI", "DECL_MAE", "LOCAL_DATA", "CMT_NOME_PG", "CMT_FUNCAO"],
            "phpm_pre_escolar" => ["BENEF_NOME", "DEP_NOME", "DEP_DATA_NASC", "BI_PREESCOLAR", "FAIXA_REMUNERACAO", "COTA_PARTE", "VALOR_RECEBER", "ENDERECO", "LOCAL_DATA", "MILITAR_PG_ABREV"],
            "phpm_fusex_cadeben" => ["MILITAR_PG_ABREV", "MILITAR_NOME", "MILITAR_IDT", "MILITAR_CPF", "MILITAR_PREC_CP", "LOCAL_DATA"],
            "phpm_indice_remissivo" => ["MILITAR_NOME", "MILITAR_PG_ABREV", "MILITAR_IDT", "MILITAR_PREC_CP"],
            _ => template.Placeholders
        };
        return required.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool IsLibreOfficeAvailable => !string.IsNullOrWhiteSpace(FindSoffice());

    public async Task<PhpmGenerationRecord> GenerateAsync(PhpmGenerationRequest request)
    {
        var record = new PhpmGenerationRecord
        {
            TemplateTitle = request.Template.Title,
            MilitaryId = request.Military.Id,
            MilitaryName = request.Military.Name,
            GeneratedAt = DateTime.Now,
            OutputFormat = request.OutputFormat
        };
        try
        {
            if (!File.Exists(request.Template.TemplatePath))
                throw new InvalidOperationException("Vincule o arquivo do template antes de gerar.");

            var templateExtension = Path.GetExtension(request.Template.TemplatePath).ToLowerInvariant();
            var safeName = SafeFileName(string.IsNullOrWhiteSpace(request.OutputName)
                ? $"{request.Template.Title}_{request.Military.ShortRank}_{request.Military.WarName}_{DateTime.Now:yyyyMMdd_HHmmss}"
                : request.OutputName);
            var renderedExtension = templateExtension == ".doc" ? ".docx" : templateExtension;
            var renderedPath = UniquePath(Path.Combine(_paths.PhpmOutputDirectory, safeName + renderedExtension));
            var fields = request.Fields.ToDictionary(
                pair => pair.Key,
                pair => NormalizeDocumentFieldValue(pair.Value),
                StringComparer.OrdinalIgnoreCase);

            if (templateExtension == ".docx") RenderDocx(request.Template.TemplatePath, renderedPath, request.Template.Id, fields);
            else if (templateExtension == ".odt") RenderOdt(request.Template.TemplatePath, renderedPath, request.Template.Id, fields);
            else if (templateExtension == ".doc")
            {
                var convertedTemplate = await ConvertLegacyDocAsync(request.Template.TemplatePath);
                RenderDocx(convertedTemplate, renderedPath, request.Template.Id, fields);
            }
            else throw new InvalidOperationException("Formato de template não suportado.");

            if (RequiresLayoutPreflight(request.Template.Id, fields))
                await FitGeneratedDocumentToTemplatePagesAsync(request.Template, renderedPath, renderedExtension, fields);

            record.SourceDocumentPath = renderedPath;
            var requestedFormat = (request.OutputFormat ?? "Original").Trim().ToUpperInvariant();
            var finalPath = renderedPath;
            if (requestedFormat == "PDF")
                finalPath = await ConvertWithLibreOfficeAsync(renderedPath, ".pdf", safeName);
            else if (requestedFormat == "DOCX" && !renderedExtension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
                finalPath = await ConvertWithLibreOfficeAsync(renderedPath, ".docx", safeName);

            if (!request.KeepIntermediateDocument && !finalPath.Equals(renderedPath, StringComparison.OrdinalIgnoreCase) && File.Exists(renderedPath))
                File.Delete(renderedPath);

            record.OutputPath = finalPath;
            record.Success = true;
            record.Message = requestedFormat == "ORIGINAL"
                ? "Documento gerado com sucesso."
                : $"Documento gerado e convertido para {requestedFormat}.";
        }
        catch (Exception ex)
        {
            record.Success = false;
            record.Message = ex.Message;
            await _log.WriteAsync("Falha ao gerar documento PHPM.", ex);
        }
        await AppendHistoryAsync(record);
        return record;
    }

    public async Task<List<PhpmGenerationRecord>> LoadHistoryAsync()
        => await _json.LoadAsync<List<PhpmGenerationRecord>>(_paths.PhpmHistoryFile) ?? [];

    private async Task AppendHistoryAsync(PhpmGenerationRecord record)
    {
        var history = await LoadHistoryAsync();
        history.Insert(0, record);
        if (history.Count > 300) history.RemoveRange(300, history.Count - 300);
        await _json.SaveAsync(_paths.PhpmHistoryFile, history);
    }

    private static void RenderDocx(string source, string destination, string templateId,
        IReadOnlyDictionary<string, string> fields, double fontScale = 1d)
    {
        File.Copy(source, destination, true);
        using var document = WordprocessingDocument.Open(destination, true);
        var roots = new List<OpenXmlPartRootElement?>
        {
            document.MainDocumentPart?.Document
        };
        if (document.MainDocumentPart is not null)
        {
            roots.AddRange(document.MainDocumentPart.HeaderParts.Select(x => x.Header));
            roots.AddRange(document.MainDocumentPart.FooterParts.Select(x => x.Footer));
        }
        foreach (var root in roots.Where(x => x is not null))
        {
            foreach (var paragraph in root!.Descendants<Paragraph>().ToList()) ReplaceParagraph(paragraph, templateId, fields, fontScale);
            root.Save();
        }
        CompactTrailingDocxParagraph(document);
    }

    private static void ReplaceParagraph(Paragraph paragraph, string templateId,
        IReadOnlyDictionary<string, string> fields, double fontScale)
    {
        var sourceRuns = paragraph.Descendants<Run>()
            .Select(run => new
            {
                Text = string.Concat(run.Descendants<Text>().Select(text => text.Text)),
                Properties = run.RunProperties?.CloneNode(true) as RunProperties
            })
            .Where(item => item.Text.Length > 0)
            .ToList();
        if (sourceRuns.Count == 0) return;

        var original = string.Concat(sourceRuns.Select(item => item.Text));
        var matches = PlaceholderRegex.Matches(original).Cast<Match>().ToList();
        if (matches.Count == 0) return;
        ApplyPlaceholderParagraphSafety(paragraph, matches.Select(MatchKey));

        var ranges = new List<(int Start, int End, RunProperties? Properties)>();
        var position = 0;
        foreach (var run in sourceRuns)
        {
            ranges.Add((position, position + run.Text.Length, run.Properties));
            position += run.Text.Length;
        }

        foreach (var child in paragraph.ChildElements.Where(element => element is not ParagraphProperties).ToList()) child.Remove();
        var cursor = 0;
        foreach (var match in matches)
        {
            AppendLiteralRuns(paragraph, original, cursor, match.Index, ranges);
            var key = MatchKey(match);
            var value = fields.TryGetValue(key, out var found) ? found ?? string.Empty : string.Empty;
            var baseProperties = ranges.FirstOrDefault(range => match.Index >= range.Start && match.Index < range.End).Properties;
            AppendReplacementRuns(paragraph, templateId, key, value, fields, baseProperties, fontScale);
            cursor = match.Index + match.Length;
        }
        AppendLiteralRuns(paragraph, original, cursor, original.Length, ranges);
    }

    private static string MatchKey(Match match)
        => match.Groups.Cast<Group>().Skip(1).FirstOrDefault(group => group.Success)?.Value ?? string.Empty;

    private static void AppendLiteralRuns(Paragraph paragraph, string source, int start, int end,
        IReadOnlyList<(int Start, int End, RunProperties? Properties)> ranges)
    {
        while (start < end)
        {
            var range = ranges.FirstOrDefault(item => start >= item.Start && start < item.End);
            var takeUntil = range.End > start ? Math.Min(end, range.End) : end;
            AppendRun(paragraph, source[start..takeUntil], range.Properties, false);
            start = takeUntil;
        }
    }

    private static void AppendReplacementRuns(Paragraph paragraph, string templateId, string key, string value,
        IReadOnlyDictionary<string, string> fields, RunProperties? properties, double fontScale)
    {
        var fontSize = ReplacementFontSizeHalfPoints(templateId, key, value, fields, fontScale);
        if (!RichNameFields.Contains(key)
            || !fields.TryGetValue("NOME_GUERRA", out var warName)
            || string.IsNullOrWhiteSpace(warName))
        {
            AppendRun(paragraph, value, properties, false, fontSize);
            return;
        }

        var segments = NameHighlightHelper.BuildSegments(value, warName);
        if (!string.Concat(segments.Select(segment => segment.Text)).Equals(value, StringComparison.CurrentCulture))
        {
            AppendRun(paragraph, value, properties, false, fontSize);
            return;
        }
        foreach (var segment in segments) AppendRun(paragraph, segment.Text, properties, segment.IsBold, fontSize);
    }

    private static void AppendRun(Paragraph paragraph, string value, RunProperties? properties, bool forceBold, int? fontSizeHalfPoints = null)
    {
        if (string.IsNullOrEmpty(value)) return;
        var run = new Run();
        var cloned = properties?.CloneNode(true) as RunProperties;
        if (cloned is not null) run.Append(cloned);
        if (forceBold)
        {
            run.RunProperties ??= new RunProperties();
            run.RunProperties.Bold = new Bold();
            run.RunProperties.BoldComplexScript = new BoldComplexScript();
        }
        if (fontSizeHalfPoints.HasValue)
        {
            run.RunProperties ??= new RunProperties();
            var size = fontSizeHalfPoints.Value.ToString(CultureInfo.InvariantCulture);
            run.RunProperties.FontSize = new FontSize { Val = size };
            run.RunProperties.FontSizeComplexScript = new FontSizeComplexScript { Val = size };
        }
        run.Append(new Text(value) { Space = SpaceProcessingModeValues.Preserve });
        paragraph.Append(run);
    }

    private static void RenderOdt(string source, string destination, string templateId,
        IReadOnlyDictionary<string, string> fields, double fontScale = 1d)
    {
        using var input = ZipFile.OpenRead(source);
        using var outputStream = File.Create(destination);
        using var output = new ZipArchive(outputStream, ZipArchiveMode.Create);
        var mime = input.GetEntry("mimetype");
        if (mime is not null)
        {
            var targetMime = output.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using var a = mime.Open(); using var b = targetMime.Open(); a.CopyTo(b);
        }
        foreach (var entry in input.Entries)
        {
            if (entry.FullName == "mimetype") continue;
            var target = output.CreateEntry(entry.FullName, CompressionLevel.Optimal);
            using var sourceStream = entry.Open();
            using var targetStream = target.Open();
            if (entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(sourceStream, Encoding.UTF8, true, leaveOpen: true);
                var xml = reader.ReadToEnd();
                var replaced = xml.Contains("<office:document-content", StringComparison.Ordinal)
                    ? ReplaceOdtStyledFields(xml, templateId, fields, fontScale)
                    : ReplaceTokens(xml, fields, xmlEscape: true);
                using var writer = new StreamWriter(targetStream, new UTF8Encoding(false), leaveOpen: true);
                writer.Write(replaced);
            }
            else sourceStream.CopyTo(targetStream);
        }
    }

    private static string ReplaceOdtStyledFields(string xml, string templateId,
        IReadOnlyDictionary<string, string> fields, double fontScale)
    {
        xml = PruneOdtDynamicRows(xml, templateId, fields);
        xml = CompactCadebenVerticalSpacing(xml, templateId, fields);
        xml = EnsureOdtAdaptiveStyles(xml, fontScale);
        fields.TryGetValue("NOME_GUERRA", out var warName);

        foreach (var pair in fields.OrderByDescending(pair => pair.Key.Length))
        {
            var key = pair.Key.Trim();
            var value = pair.Value ?? string.Empty;
            var styleName = OdtStyleForField(templateId, key, value, fields);
            string replacement;
            if (RichNameFields.Contains(key) && !string.IsNullOrWhiteSpace(warName))
            {
                var segments = NameHighlightHelper.BuildSegments(value, warName);
                var valid = string.Concat(segments.Select(segment => segment.Text)).Equals(value, StringComparison.CurrentCulture);
                replacement = valid
                    ? string.Concat(segments.Select(segment => OdtSpan(segment.Text, styleName + (segment.IsBold ? "Bold" : string.Empty))))
                    : OdtSpan(value, styleName);
            }
            else
            {
                replacement = OdtSpan(value, styleName);
            }

            foreach (var token in new[] { $"{{{{{key}}}}}", $"[[{key}]]", $"<<{key}>>", $"${key}$", $"{{{key}}}" })
            {
                xml = xml.Replace(token, replacement, StringComparison.OrdinalIgnoreCase);
                try
                {
                    var split = string.Join("(?:<[^>]+>)*", token.Select(ch => Regex.Escape(ch.ToString())));
                    xml = Regex.Replace(xml, split, _ => replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                }
                catch { }
            }
        }

        return PlaceholderRegex.Replace(xml, string.Empty);
    }

    private static string PruneOdtDynamicRows(string xml, string templateId, IReadOnlyDictionary<string, string> fields)
    {
        if (!templateId.Equals("phpm_fusex_cadeben", StringComparison.OrdinalIgnoreCase)) return xml;

        return Regex.Replace(xml, @"<table:table-row\b[^>]*>.*?</table:table-row>", match =>
        {
            var numbers = Regex.Matches(match.Value, @"DEP(?<number>\d+)_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Select(item => int.TryParse(item.Groups["number"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0)
                .Where(number => number > 0)
                .Distinct()
                .ToList();
            if (numbers.Count == 0) return match.Value;

            return numbers.Any(number => fields.Any(pair =>
                    pair.Key.StartsWith($"DEP{number}_", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(pair.Value)))
                ? match.Value
                : string.Empty;
        }, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }

    private static string CompactCadebenVerticalSpacing(string xml, string templateId, IReadOnlyDictionary<string, string> fields)
    {
        if (!templateId.Equals("phpm_fusex_cadeben", StringComparison.OrdinalIgnoreCase)) return xml;
        var activeRows = fields
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => Regex.Match(pair.Key, @"^DEP(?<number>\d+)_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Where(match => match.Success)
            .Select(match => int.TryParse(match.Groups["number"].Value, out var number) ? number : 0)
            .Where(number => number > 0)
            .Distinct()
            .Count();
        if (activeRows <= 3) return xml;

        // O modelo de referência reserva quatro linhas em branco entre data e assinatura.
        // Com uma tabela maior esse espaço vira uma folha vazia; acima de três beneficiários
        // a assinatura acompanha o conteúdo sem alterar margens ou a moldura oficial.
        xml = Regex.Replace(
            xml,
            @"<text:p\s+text:style-name=""P(?:1|2|4|5|12|15|16)""\s*/>",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Regex.Replace(
            xml,
            "<text:p\\b(?=[^>]*text:style-name=\\\"P(?:1|2|4|5|12|15|16)\\\")[^>]*(?:/>|>.*?</text:p>)",
            match =>
            {
                var visibleText = Regex.Replace(match.Value, "<[^>]+>", string.Empty)
                    .Replace("&nbsp;", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Trim();
                return visibleText.Length == 0 ? string.Empty : match.Value;
            },
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }

    private static string OdtSpan(string value, string styleName)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : $"<text:span text:style-name=\"{styleName}\">{SecurityElement.Escape(value) ?? string.Empty}</text:span>";

    private static string OdtStyleForField(string templateId, string key, string value,
        IReadOnlyDictionary<string, string> fields)
    {
        if (templateId.Equals("phpm_capa", StringComparison.OrdinalIgnoreCase))
            return "SIGFURCover" + AdaptiveSizeSuffix(CoverDisplayLength(fields));
        if (templateId.Equals("phpm_fusex_cadeben", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(key, @"^DEP\d+_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return value.Length switch { > 42 => "SIGFURCadebenXS", > 28 => "SIGFURCadebenS", _ => "SIGFURCadeben" };
        if (IsNameLikeField(key)) return "SIGFURName" + AdaptiveSizeSuffix(value.Length);
        return value.Length switch
        {
            > 100 => "SIGFURFieldXS",
            > 65 => "SIGFURFieldS",
            _ => "SIGFURField"
        };
    }

    private static string AdaptiveSizeSuffix(int length)
        => length switch { <= 30 => "XL", <= 42 => "L", <= 56 => "M", _ => "S" };

    private static string EnsureOdtAdaptiveStyles(string xml, double fontScale)
    {
        if (!xml.Contains("</office:automatic-styles>", StringComparison.Ordinal)) return xml;
        static string Number(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
        string Style(string name, double points, bool bold = false)
        {
            var size = Number(Math.Max(8d, points * fontScale));
            var weight = bold ? " fo:font-weight=\"bold\" style:font-weight-asian=\"bold\" style:font-weight-complex=\"bold\"" : string.Empty;
            return $"<style:style style:name=\"{name}\" style:family=\"text\"><style:text-properties fo:font-size=\"{size}pt\" style:font-size-asian=\"{size}pt\" style:font-size-complex=\"{size}pt\"{weight}/></style:style>";
        }

        var styles = string.Concat(
            Style("SIGFURField", 13), Style("SIGFURFieldS", 11.5), Style("SIGFURFieldXS", 10),
            Style("SIGFURCadeben", 12), Style("SIGFURCadebenS", 10.5), Style("SIGFURCadebenXS", 9),
            Style("SIGFURNameXL", 15), Style("SIGFURNameL", 14), Style("SIGFURNameM", 12.5), Style("SIGFURNameS", 11),
            Style("SIGFURNameXLBold", 15, true), Style("SIGFURNameLBold", 14, true), Style("SIGFURNameMBold", 12.5, true), Style("SIGFURNameSBold", 11, true),
            Style("SIGFURCoverXL", 26), Style("SIGFURCoverL", 22), Style("SIGFURCoverM", 18), Style("SIGFURCoverS", 14),
            Style("SIGFURCoverXLBold", 26, true), Style("SIGFURCoverLBold", 22, true), Style("SIGFURCoverMBold", 18, true), Style("SIGFURCoverSBold", 14, true));
        return xml.Replace("</office:automatic-styles>", styles + "</office:automatic-styles>", StringComparison.Ordinal);
    }

    private static void ApplyPlaceholderParagraphSafety(Paragraph paragraph, IEnumerable<string> keys)
    {
        var list = keys.Where(key => !string.IsNullOrWhiteSpace(key)).ToList();
        var properties = paragraph.ParagraphProperties ?? paragraph.PrependChild(new ParagraphProperties());
        // Marcadores de template às vezes herdam "manter com o próximo" e acabam
        // empurrando assinatura ou tabela inteira para uma folha adicional.
        properties.KeepNext?.Remove();
        properties.PageBreakBefore?.Remove();
        properties.WidowControl = new WidowControl { Val = false };
        properties.SuppressAutoHyphens = new SuppressAutoHyphens { Val = true };
        if (list.Any(IsNameLikeField))
            properties.KeepLines = new KeepLines { Val = true };
    }

    private static int ReplacementFontSizeHalfPoints(string templateId, string key, string value,
        IReadOnlyDictionary<string, string> fields, double fontScale)
    {
        double points;
        if (templateId.Equals("phpm_capa", StringComparison.OrdinalIgnoreCase))
        {
            points = CoverDisplayLength(fields) switch
            {
                <= 30 => 26,
                <= 42 => 22,
                <= 56 => 18,
                <= 68 => 15,
                _ => 13
            };
        }
        else if (IsNameLikeField(key))
        {
            points = value.Length switch
            {
                <= 30 => 15,
                <= 42 => 14,
                <= 56 => 12.5,
                _ => 11
            };
        }
        else
        {
            points = value.Length switch { > 100 => 10, > 65 => 11.5, _ => 13 };
        }
        return Math.Max(16, (int)Math.Round(points * fontScale * 2d, MidpointRounding.AwayFromZero));
    }

    private static bool IsNameLikeField(string key)
        => Regex.IsMatch(key ?? string.Empty, @"(?:^|_)(?:NOME|NAME)(?:_|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static int CoverDisplayLength(IReadOnlyDictionary<string, string> fields)
        => string.Join(' ', new[]
            {
                fields.GetValueOrDefault("MILITAR_PG_ABREV") ?? fields.GetValueOrDefault("PG") ?? string.Empty,
                fields.GetValueOrDefault("MILITAR_NOME") ?? fields.GetValueOrDefault("NOME") ?? string.Empty
            }.Where(value => !string.IsNullOrWhiteSpace(value))).Length;

    private static string NormalizeDocumentFieldValue(string? value)
        // Campos do PHPM são de linha única. Remove Enter/Tab colados de planilha ou
        // cadastro para que um valor não crie linhas e páginas invisíveis na impressão.
        => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static void CompactTrailingDocxParagraph(WordprocessingDocument document)
    {
        var mainDocument = document.MainDocumentPart?.Document;
        var body = mainDocument?.Body;
        if (mainDocument is null || body is null) return;
        foreach (var paragraph in body.ChildElements
                     .Where(element => element is not SectionProperties)
                     .Reverse()
                     .TakeWhile(element => element is Paragraph p && string.IsNullOrWhiteSpace(p.InnerText))
                     .Cast<Paragraph>())
        {
            paragraph.Descendants<Break>().Where(br => br.Type?.Value == BreakValues.Page).ToList().ForEach(br => br.Remove());
            var properties = paragraph.ParagraphProperties ?? paragraph.PrependChild(new ParagraphProperties());
            properties.PageBreakBefore?.Remove();
            properties.KeepNext?.Remove();
            properties.KeepLines?.Remove();
            properties.SpacingBetweenLines = new SpacingBetweenLines
            {
                Before = "0", After = "0", Line = "20", LineRule = LineSpacingRuleValues.Exact
            };
            var mark = properties.GetFirstChild<ParagraphMarkRunProperties>()
                       ?? properties.AppendChild(new ParagraphMarkRunProperties());
            mark.RemoveAllChildren<FontSize>();
            mark.RemoveAllChildren<FontSizeComplexScript>();
            mark.Append(new FontSize { Val = "2" }, new FontSizeComplexScript { Val = "2" });
        }
        mainDocument.Save();
    }

    private static bool RequiresLayoutPreflight(string templateId, IReadOnlyDictionary<string, string> fields)
        => templateId.Equals("phpm_fusex_cadeben", StringComparison.OrdinalIgnoreCase)
           || templateId.StartsWith("phpm_", StringComparison.OrdinalIgnoreCase)
           && fields.Any(pair => !string.IsNullOrWhiteSpace(pair.Value)
                                 && (pair.Value.Length > 65 || IsNameLikeField(pair.Key) && pair.Value.Length > 32));

    private async Task FitGeneratedDocumentToTemplatePagesAsync(PhpmTemplateDefinition template, string renderedPath,
        string renderedExtension, IReadOnlyDictionary<string, string> fields)
    {
        if (string.IsNullOrWhiteSpace(FindSoffice())) return;
        var templateExtension = Path.GetExtension(template.TemplatePath).ToLowerInvariant();
        if (templateExtension is not (".docx" or ".odt")) return;

        var expectedPages = template.Id.Equals("phpm_fusex_cadeben", StringComparison.OrdinalIgnoreCase)
            ? 1
            : await GetTemplatePageCountAsync(template.TemplatePath);
        if (expectedPages <= 0) return;
        var actualPages = await GetDocumentPageCountAsync(renderedPath);
        if (actualPages <= expectedPages) return;

        foreach (var scale in new[] { 0.90d, 0.82d, 0.74d })
        {
            if (renderedExtension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
                RenderDocx(template.TemplatePath, renderedPath, template.Id, fields, scale);
            else if (renderedExtension.Equals(".odt", StringComparison.OrdinalIgnoreCase))
                RenderOdt(template.TemplatePath, renderedPath, template.Id, fields, scale);
            else
                return;

            actualPages = await GetDocumentPageCountAsync(renderedPath);
            if (actualPages <= expectedPages) return;
        }
    }

    private async Task<int> GetTemplatePageCountAsync(string templatePath)
    {
        var changed = File.GetLastWriteTimeUtc(templatePath);
        if (_templatePageCounts.TryGetValue(templatePath, out var cached) && cached.LastWrite == changed)
            return cached.Pages;
        var pages = await GetDocumentPageCountAsync(templatePath);
        if (pages > 0) _templatePageCounts[templatePath] = (changed, pages);
        return pages;
    }

    private static async Task<int> GetDocumentPageCountAsync(string source)
    {
        var soffice = FindSoffice();
        if (string.IsNullOrWhiteSpace(soffice) || !File.Exists(source)) return 0;
        var temp = Path.Combine(Path.GetTempPath(), "SIGFUR_PHPM_LAYOUT_" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(temp, "profile");
        Directory.CreateDirectory(temp);
        Directory.CreateDirectory(profile);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = soffice,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--headless");
            startInfo.ArgumentList.Add("--nologo");
            startInfo.ArgumentList.Add("--nodefault");
            startInfo.ArgumentList.Add("--nofirststartwizard");
            startInfo.ArgumentList.Add("-env:UserInstallation=" + new Uri(profile + Path.DirectorySeparatorChar).AbsoluteUri);
            startInfo.ArgumentList.Add("--convert-to");
            startInfo.ArgumentList.Add("pdf");
            startInfo.ArgumentList.Add("--outdir");
            startInfo.ArgumentList.Add(temp);
            startInfo.ArgumentList.Add(source);
            using var process = Process.Start(startInfo);
            if (process is null) return 0;
            await process.WaitForExitAsync();
            var pdf = Directory.EnumerateFiles(temp, "*.pdf").FirstOrDefault();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(pdf)) return 0;
            using var document = PdfDocument.Open(pdf);
            return document.NumberOfPages;
        }
        catch
        {
            return 0;
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    private async Task<string> ConvertWithLibreOfficeAsync(string source, string targetExtension, string targetStem)
    {
        var soffice = FindSoffice();
        if (string.IsNullOrWhiteSpace(soffice))
            throw new InvalidOperationException("Para gerar PDF ou converter entre ODT e DOCX, instale o LibreOffice.");

        var tempDirectory = Path.Combine(Path.GetTempPath(), "SIGFUR_PHPM_CONVERT_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var target = targetExtension.TrimStart('.').ToLowerInvariant();
            var startInfo = new ProcessStartInfo
            {
                FileName = soffice,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--headless");
            startInfo.ArgumentList.Add("--nologo");
            startInfo.ArgumentList.Add("--nolockcheck");
            startInfo.ArgumentList.Add("--nodefault");
            startInfo.ArgumentList.Add("--nofirststartwizard");
            startInfo.ArgumentList.Add("--convert-to");
            startInfo.ArgumentList.Add(target);
            startInfo.ArgumentList.Add("--outdir");
            startInfo.ArgumentList.Add(tempDirectory);
            startInfo.ArgumentList.Add(source);

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Não foi possível iniciar o LibreOffice.");
            await process.WaitForExitAsync();
            var converted = Directory.EnumerateFiles(tempDirectory)
                .FirstOrDefault(path => Path.GetExtension(path).Equals(targetExtension, StringComparison.OrdinalIgnoreCase));
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(converted) || !File.Exists(converted))
            {
                var error = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException("O LibreOffice não conseguiu converter o documento." + (string.IsNullOrWhiteSpace(error) ? string.Empty : " " + error.Trim()));
            }
            var destination = UniquePath(Path.Combine(_paths.PhpmOutputDirectory, targetStem + targetExtension));
            File.Move(converted, destination);
            return destination;
        }
        finally
        {
            try { Directory.Delete(tempDirectory, true); } catch { }
        }
    }

    private async Task<string> ConvertLegacyDocAsync(string source)
    {
        var soffice = FindSoffice();
        if (string.IsNullOrWhiteSpace(soffice))
            throw new InvalidOperationException("Este template está em DOC antigo. Instale o LibreOffice ou salve o modelo como DOCX.");
        var temp = Path.Combine(Path.GetTempPath(), "SIGFUR_PHPM_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var startInfo = new ProcessStartInfo
        {
            FileName = soffice,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--headless");
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--nolockcheck");
        startInfo.ArgumentList.Add("--convert-to");
        startInfo.ArgumentList.Add("docx");
        startInfo.ArgumentList.Add("--outdir");
        startInfo.ArgumentList.Add(temp);
        startInfo.ArgumentList.Add(source);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Não foi possível iniciar o LibreOffice.");
        await process.WaitForExitAsync();
        var output = Directory.EnumerateFiles(temp, "*.docx").FirstOrDefault();
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output) || !File.Exists(output))
            throw new InvalidOperationException("O LibreOffice não conseguiu converter o template DOC para DOCX.");
        return output;
    }

    private static string ReplaceTokens(string text, IReadOnlyDictionary<string, string> fields, bool xmlEscape = false)
    {
        foreach (var pair in fields.OrderByDescending(x => x.Key.Length))
        {
            var key = pair.Key.Trim();
            var value = pair.Value ?? string.Empty;
            if (xmlEscape) value = System.Security.SecurityElement.Escape(value) ?? string.Empty;
            foreach (var token in new[] { $"{{{{{key}}}}}", $"[[{key}]]", $"<<{key}>>", $"${key}$", $"{{{key}}}" })
            {
                text = text.Replace(token, value, StringComparison.OrdinalIgnoreCase);
                try
                {
                    var split = string.Join("(?:<[^>]+>)*", token.Select(ch => Regex.Escape(ch.ToString())));
                    text = Regex.Replace(text, split, _ => value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                }
                catch { }
            }
        }
        return PlaceholderRegex.Replace(text, match =>
        {
            var key = match.Groups[1].Value;
            var value = fields.TryGetValue(key, out var found) ? found : string.Empty;
            return xmlEscape ? System.Security.SecurityElement.Escape(value) ?? string.Empty : value;
        });
    }

    private static (string Before, string War, string After) SplitNameForTemplate(string? fullName, string? warName)
    {
        var full = (fullName ?? string.Empty).Trim();
        var war = (warName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(full) || string.IsNullOrWhiteSpace(war)) return (full, war, string.Empty);
        var index = full.IndexOf(war, StringComparison.CurrentCultureIgnoreCase);
        return index < 0
            ? (full, war, string.Empty)
            : (full[..index].TrimEnd(), full.Substring(index, war.Length), full[(index + war.Length)..].TrimStart());
    }

    private List<PhpmTemplateDefinition> BuiltInTemplates()
    {
        var defs = new[]
        {
            ("phpm_capa", "Capa PHPM", "Capa oficial da Pasta de Habilitação à Pensão Militar.", "PHPM_CAPA_TEMPLATE.docx"),
            ("phpm_decl_benef", "Declaração de Beneficiários", "Declaração com dados do militar e beneficiários.", "PHPM_DECL_BENEF_TEMPLATE.odt"),
            ("phpm_pre_escolar", "Ficha Pré-Escolar", "Cadastro de beneficiário da assistência pré-escolar.", "PHPM_PRE_ESCOLAR_TEMPLATE.odt"),
            ("phpm_fusex_cadeben", "Declaração de Beneficiários FuSEx", "Ficha auxiliar oficial do Cadeben FuSEx com tabela ajustada à quantidade de beneficiários.", "PHPM_CADEBEN_FUSEX_TEMPLATE.odt"),
            ("phpm_indice_remissivo", "Índice Remissivo das Alterações Militares", "Índice das alterações financeiras e funcionais do militar.", "PHPM_INDICE_REMISSIVO_TEMPLATE.odt")
        };
        return defs.Select(x => new PhpmTemplateDefinition
        {
            Id = x.Item1,
            Title = x.Item2,
            Description = x.Item3,
            TemplatePath = Path.Combine(_paths.PhpmTemplatesDirectory, x.Item4),
            IsBuiltIn = true
        }).ToList();
    }

    private static string ExtractDocxText(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var parts = new List<string?> { doc.MainDocumentPart?.Document?.InnerText };
        if (doc.MainDocumentPart is not null)
        {
            parts.AddRange(doc.MainDocumentPart.HeaderParts.Select(x => x.Header?.InnerText));
            parts.AddRange(doc.MainDocumentPart.FooterParts.Select(x => x.Footer?.InnerText));
        }
        return string.Join(" ", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static string ExtractOdtXml(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var text = new StringBuilder();
        foreach (var entry in zip.Entries.Where(x => x.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            using var reader = new StreamReader(entry.Open());
            var xml = reader.ReadToEnd();
            try
            {
                var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                // A concatenação dos nós reconhece marcadores que o LibreOffice
                // dividiu em vários spans, por exemplo {{MILITAR_ + NOME}}.
                text.AppendLine(string.Concat(document.DescendantNodes().OfType<XText>().Select(node => node.Value)));
            }
            catch
            {
                text.AppendLine(Regex.Replace(xml, "<[^>]+>", string.Empty));
            }
        }
        return text.ToString();
    }

    private static void ValidateExtension(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".docx" or ".odt" or ".doc")) throw new InvalidOperationException("Selecione um template DOCX, ODT ou DOC.");
    }

    private static double ParseMoney(string? value)
    {
        var text = (value ?? string.Empty).Trim().Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase).Replace(" ", string.Empty);
        if (string.IsNullOrWhiteSpace(text)) return 0;
        if (text.Contains(',') && text.Contains('.')) text = text.Replace(".", string.Empty).Replace(',', '.');
        else if (text.Contains(',')) text = text.Replace(',', '.');
        return double.TryParse(text, NumberStyles.Number | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static string DateToLongPortuguese(DateTime date)
    {
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        return $"{date.Day} de {date.ToString("MMMM", culture)} de {date.Year}";
    }

    private static string FindSoffice()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice", "program", "soffice.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string((value ?? string.Empty).Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        safe = Regex.Replace(safe, @"\s+", "_").Trim('_', '.', ' ');
        return string.IsNullOrWhiteSpace(safe) ? "Documento_PHPM" : safe[..Math.Min(140, safe.Length)];
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(directory, $"{stem}_{i:00}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(directory, $"{stem}_{DateTime.Now:yyyyMMddHHmmss}{ext}");
    }
}
