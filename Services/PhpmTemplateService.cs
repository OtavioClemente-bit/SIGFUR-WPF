using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SIGFUR.Wpf.Controls;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class PhpmTemplateService
{
    private static readonly Regex PlaceholderRegex = new(@"\{\{([A-Za-z0-9_\-]+)\}\}", RegexOptions.Compiled);
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly LogService _log;

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
        foreach (var fileName in new[] { "CAPA_TEMPLATE.docx", "INDICE_REMISSIVO_SIGFUR_TEMPLATE.odt" })
        {
            try
            {
                var candidates = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "Resources", "PHPM", fileName),
                    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Resources", "PHPM", fileName)),
                    Path.Combine(AppContext.BaseDirectory, "templates", "docs", fileName)
                };
                var source = candidates.FirstOrDefault(File.Exists);
                if (string.IsNullOrWhiteSpace(source)) continue;
                var target = Path.Combine(_paths.PhpmTemplatesDirectory, fileName);
                if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(source).Length)
                    File.Copy(source, target, true);
            }
            catch (Exception ex)
            {
                _ = _log.WriteAsync($"Falha ao instalar template PHPM {fileName}.", ex);
            }
        }
    }

    public async Task<PhpmTemplateCatalog> LoadCatalogAsync()
    {
        var catalog = await _json.LoadAsync<PhpmTemplateCatalog>(_paths.PhpmCatalogFile) ?? new PhpmTemplateCatalog();
        var defaults = BuiltInTemplates();
        foreach (var item in defaults)
        {
            var existing = catalog.Templates.FirstOrDefault(x => x.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is null) catalog.Templates.Add(item);
            else
            {
                existing.IsBuiltIn = true;
                existing.Title = item.Title;
                existing.Description = item.Description;
                // Para os modelos principais, o caminho oficial do módulo prevalece
                // quando o vínculo antigo está vazio/quebrado ou apontando fora da pasta PHPM.
                if (string.IsNullOrWhiteSpace(existing.TemplatePath)
                    || !File.Exists(existing.TemplatePath)
                    || !Path.GetFullPath(existing.TemplatePath).StartsWith(Path.GetFullPath(_paths.PhpmTemplatesDirectory), StringComparison.OrdinalIgnoreCase))
                {
                    existing.TemplatePath = item.TemplatePath;
                }
            }
        }
        await ImportLegacyTemplatesAsync(catalog);
        catalog.Templates = catalog.Templates.OrderByDescending(x => x.IsBuiltIn).ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        await SaveCatalogAsync(catalog);
        return catalog;
    }

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
        var target = UniquePath(Path.Combine(_paths.PhpmTemplatesDirectory, SafeFileName(Path.GetFileNameWithoutExtension(sourcePath)) + Path.GetExtension(sourcePath).ToLowerInvariant()));
        File.Copy(sourcePath, target, false);
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
        var target = UniquePath(Path.Combine(_paths.PhpmTemplatesDirectory, SafeFileName(definition.Id + "_" + Path.GetFileNameWithoutExtension(sourcePath)) + Path.GetExtension(sourcePath).ToLowerInvariant()));
        File.Copy(sourcePath, target, false);
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
        return PlaceholderRegex.Matches(text).Select(x => x.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
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
            ["OPERADOR"] = profile.Operator,
            ["POSTO_OPERADOR"] = profile.Rank,
            ["FUNCAO_OPERADOR"] = profile.Function,
            ["DATA"] = today.ToString("dd/MM/yyyy"),
            ["DATA_ATUAL"] = today.ToString("dd/MM/yyyy"),
            ["DATA_EXTENSO"] = DateToLongPortuguese(today),
            ["ANO"] = today.Year.ToString(CultureInfo.InvariantCulture),
            ["ANO_DOC"] = today.Year.ToString(CultureInfo.InvariantCulture),
            ["SECAO"] = "SSPP",
            ["ASSUNTO_TEXTO"] = "PASTA DE HABILITAÇÃO À PENSÃO MILITAR",
            ["MES"] = today.ToString("MMMM", CultureInfo.GetCultureInfo("pt-BR")),
            ["RECEBE_AUX_TRANSPORTE"] = MilitaryRecord.IsYes(military.ReceivesTransportAid) ? "SIM" : "NÃO",
            ["VALOR_AUX_TRANSPORTE"] = MilitaryFormatting.FormatMoney(ParseMoney(military.TransportAidValue)),
            ["RECEBE_PRE_ESCOLAR"] = MilitaryRecord.IsYes(military.ReceivesPreSchool) ? "SIM" : "NÃO",
            ["VALOR_PRE_ESCOLAR"] = MilitaryFormatting.FormatMoney(ParseMoney(military.PreSchoolValue)),
            ["POSSUI_PNR"] = MilitaryRecord.IsYes(military.HasPnr) ? "SIM" : "NÃO",
            ["PENSAO_JUDICIAL"] = MilitaryRecord.IsYes(military.Alimony) ? "SIM" : "NÃO"
        };
    }

    public IReadOnlyList<string> GetSuggestedFields(string templateId)
    {
        var common = new List<string>
        {
            "POSTO_GRAD", "POSTO_GRAD_ABREV", "NOME", "NOME_GUERRA", "CPF", "PREC_CP", "IDENTIDADE",
            "DATA_NASCIMENTO", "DATA_PRACA", "ENDERECO", "CEP", "TELEFONE", "EMAIL", "BANCO", "AGENCIA", "CONTA",
            "OM", "OPERADOR", "POSTO_OPERADOR", "FUNCAO_OPERADOR", "DATA", "DATA_EXTENSO"
        };
        var specific = templateId.ToLowerInvariant() switch
        {
            "phpm_capa" => new[] { "PAI", "MAE", "CONJUGE", "NOME_PAI", "NOME_MAE", "DECLARANTE", "LOCALIDADE" },
            "phpm_decl_benef" => new[] { "BENEFICIARIO_1", "BENEFICIARIO_2", "BENEFICIARIO_3", "PARENTESCO_1", "PARENTESCO_2", "PARENTESCO_3", "OBSERVACAO" },
            "phpm_pre_escolar" => new[] { "DEPENDENTE_NOME", "DEPENDENTE_DATA_NASCIMENTO", "DEPENDENTE_CPF", "DEPENDENTE_PARENTESCO", "ESCOLA", "MENSALIDADE", "BI" },
            "phpm_fusex_cadeben" => new[] { "DEPENDENTE_NOME", "DEPENDENTE_DATA_NASCIMENTO", "DEPENDENTE_CPF", "DEPENDENTE_PARENTESCO", "CADEBEN", "FUSEX", "BI" },
            "phpm_indice_remissivo" => new[] { "SOLDO_BI", "HABILITACAO_PERCENTUAL", "HABILITACAO_BI", "ADICIONAL_MILITAR_PERCENTUAL", "ADICIONAL_MILITAR_BI", "PERMANENCIA_PERCENTUAL", "PERMANENCIA_BI", "COMPENSACAO_ORGANICA", "AUX_TRANSPORTE", "PNR", "PENSAO_JUDICIAL", "OBSERVACAO" },
            _ => new[] { "PAI", "MAE", "CONJUGE", "DEPENDENTE_NOME", "DEPENDENTE_DATA_NASCIMENTO", "BI", "OBSERVACAO" }
        };
        return common.Concat(specific).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
            var fields = request.Fields.ToDictionary(pair => pair.Key, pair => pair.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);

            if (templateExtension == ".docx") RenderDocx(request.Template.TemplatePath, renderedPath, fields);
            else if (templateExtension == ".odt") RenderOdt(request.Template.TemplatePath, renderedPath, fields);
            else if (templateExtension == ".doc")
            {
                var convertedTemplate = await ConvertLegacyDocAsync(request.Template.TemplatePath);
                RenderDocx(convertedTemplate, renderedPath, fields);
            }
            else throw new InvalidOperationException("Formato de template não suportado.");

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

    private static void RenderDocx(string source, string destination, IReadOnlyDictionary<string, string> fields)
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
            foreach (var paragraph in root!.Descendants<Paragraph>().ToList()) ReplaceParagraph(paragraph, fields);
            root.Save();
        }
    }

    private static void ReplaceParagraph(Paragraph paragraph, IReadOnlyDictionary<string, string> fields)
    {
        var texts = paragraph.Descendants<Text>().ToList();
        if (texts.Count == 0) return;
        var original = string.Concat(texts.Select(x => x.Text));
        var replaced = ReplaceTokens(original, fields);
        if (replaced == original) return;
        texts[0].Text = replaced;
        texts[0].Space = SpaceProcessingModeValues.Preserve;
        foreach (var extra in texts.Skip(1)) extra.Text = string.Empty;
    }

    private static void RenderOdt(string source, string destination, IReadOnlyDictionary<string, string> fields)
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
                var replaced = ReplaceTokens(xml, fields, xmlEscape: true);
                using var writer = new StreamWriter(targetStream, new UTF8Encoding(false), leaveOpen: true);
                writer.Write(replaced);
            }
            else sourceStream.CopyTo(targetStream);
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
            ("phpm_capa", "Capa PHPM", "Capa da Pasta de Habilitação à Pensão Militar.", "CAPA_TEMPLATE.docx"),
            ("phpm_decl_benef", "Declaração de Beneficiários", "Declaração com dados do militar e beneficiários.", "DECL_BENEF_TEMPLATE.odt"),
            ("phpm_pre_escolar", "Ficha Pré-Escolar", "Cadastro de beneficiário da assistência pré-escolar.", "PRE-ESCOLAR_TEMPLATE.odt"),
            ("phpm_fusex_cadeben", "Declaração Cadeben FuSEx", "Declaração de beneficiários do FuSEx e dependentes.", "CADEBEN_FUSEX_TEMPLATE.odt"),
            ("phpm_indice_remissivo", "Índice Remissivo das Alterações Militares", "Índice das alterações financeiras e funcionais do militar.", "INDICE_REMISSIVO_SIGFUR_TEMPLATE.odt")
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
            text.AppendLine(reader.ReadToEnd());
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
