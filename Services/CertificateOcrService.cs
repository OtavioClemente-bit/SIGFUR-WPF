using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;
using Windows.Data.Pdf;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Leitor nativo de certidão de nascimento. PDFs e imagens são renderizados e
/// reconhecidos pelo OCR do Windows; em seguida os campos são normalizados em
/// chaves compatíveis com os modelos de boletim do SIGFUR.
/// </summary>
public sealed partial class CertificateOcrService
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff"
    };
    private static readonly CertificateRegion[] CertificateRegions =
    [
        new("IDENTIFICACAO_OCR_AMPLIADA", 0.07, 0.20, 0.86, 0.16),
        new("NASCIMENTO_OCR_AMPLIADO", 0.07, 0.33, 0.86, 0.13),
        new("NASCIMENTO_FILIACAO_TRANSICAO_OCR", 0.07, 0.42, 0.86, 0.16),
        // A posição vertical da filiação varia bastante entre cartórios. A faixa
        // mais alta cobre os dois modelos mais comuns sem depender dos recortes
        // estreitos de cada genitor.
        new("FILIACAO_OCR_AMPLIADA", 0.07, 0.36, 0.86, 0.24),
        new("GENITOR_SUPERIOR_VALOR_OCR", 0.09, 0.390, 0.48, 0.035),
        new("GENITOR_INTERMEDIARIO_VALOR_OCR", 0.075, 0.420, 0.52, 0.055),
        new("GENITOR_INFERIOR_VALOR_OCR", 0.075, 0.490, 0.52, 0.055),
        new("DATA_REGISTRO_OCR_AMPLIADA", 0.08, 0.545, 0.58, 0.07),
        new("DNV_OCR_AMPLIADA", 0.61, 0.545, 0.32, 0.07),
        new("CARTORIO_OCR_AMPLIADO", 0.07, 0.62, 0.56, 0.18)
    ];
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly LogService _log;

    public CertificateOcrService(AppPaths paths, JsonFileService json, LogService log)
    {
        _paths = paths;
        _json = json;
        _log = log;
    }

    public async Task<CertificateOcrResult> ReadAsync(string filePath, MilitaryRecord military, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException("Certidão não encontrada.", filePath);
        if (!SupportsFile(filePath))
            throw new NotSupportedException("A leitura automática aceita certidões em PDF ou imagem (PNG, JPG, WEBP, BMP ou TIFF).");

        try
        {
            var engine = CreateEngine() ?? throw new InvalidOperationException(
                "O OCR do Windows não está disponível neste computador. Instale o pacote de idioma Português (Brasil) nas Configurações do Windows e tente novamente.");
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            var text = extension == ".pdf"
                ? await ReadPdfAsync(filePath, engine, cancellationToken)
                : await ReadImageAsync(filePath, engine, cancellationToken);

            var result = Parse(text, military);
            result.RawText = text;
            result.SourcePath = Path.GetFullPath(filePath);
            result.Engine = "OCR nativo do Windows";
            if (string.IsNullOrWhiteSpace(text)) result.Warnings.Add("O OCR não encontrou texto legível no arquivo.");
            if (!result.HasUsefulData) result.Warnings.Add("Nenhum dado confiável foi identificado. Revise os campos antes de salvar.");
            return result;
        }
        catch (Exception ex)
        {
            await _log.WriteAsync($"Falha ao processar certidão por OCR: {filePath}", ex);
            throw;
        }
    }

    /// <summary>
    /// Executa somente o reconhecimento de texto, sem aplicar a interpretação
    /// específica de certidões. Pode ser reutilizado por outros documentos do
    /// SIGFUR, inclusive PDFs formados exclusivamente por páginas digitalizadas.
    /// </summary>
    public async Task<string> ExtractDocumentTextAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException("Documento não encontrado.", filePath);
        if (!SupportsFile(filePath))
            throw new NotSupportedException("O OCR aceita documentos em PDF ou imagem (PNG, JPG, WEBP, BMP ou TIFF).");

        try
        {
            var engine = CreateEngine() ?? throw new InvalidOperationException(
                "O OCR do Windows não está disponível neste computador. Instale o pacote de idioma Português (Brasil) nas Configurações do Windows e tente novamente.");
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            return extension == ".pdf"
                ? await ReadPdfAsync(filePath, engine, cancellationToken)
                : await ReadImageAsync(filePath, engine, cancellationToken);
        }
        catch (Exception ex)
        {
            await _log.WriteAsync($"Falha ao reconhecer documento por OCR: {filePath}", ex);
            throw;
        }
    }

    public static bool SupportsFile(string? filePath)
        => !string.IsNullOrWhiteSpace(filePath) && SupportedExtensions.Contains(Path.GetExtension(filePath));

    public async Task SaveGlobalKeysAsync(IReadOnlyDictionary<string, string> keys, MilitaryRecord military)
    {
        var path = _paths.CertificateBulletinKeysFile;
        var root = await _json.LoadNodeAsync(path) as JsonObject ?? new JsonObject();
        foreach (var pair in ExpandAliases(keys, military))
            if (!string.IsNullOrWhiteSpace(pair.Value)) root[pair.Key] = pair.Value;
        root["_meta"] = new JsonObject
        {
            ["origem"] = "carteira_militar_ocr_wpf",
            ["militar_id"] = military.Id,
            ["militar_nome"] = military.Name,
            ["atualizado_em"] = DateTime.Now.ToString("s")
        };
        await _json.SaveNodeAsync(path, root);
    }

    public static Dictionary<string, string> ExpandAliases(IReadOnlyDictionary<string, string> source, MilitaryRecord? military = null)
    {
        var result = source.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        string Get(params string[] keys) => keys.Select(k => result.GetValueOrDefault(k, string.Empty)).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
        void Set(string key, string value) { if (!string.IsNullOrWhiteSpace(value)) result[key] = value.Trim(); }

        var name = Get("NOME_FILHO", "NOME_FILHA", "NOME_CRIANCA", "NOME_DEPENDENTE");
        var cpf = Get("CPF_FILHO", "CPF_FILHA", "CPF_CRIANCA", "CPF_DEPENDENTE");
        var birth = Get("DATA_NASCIMENTO", "NASCIMENTO");
        var registration = Get("DATA_CERTIDAO", "DATA_REGISTRO");
        var matrix = Get("MATRICULA_CERTIDAO", "MATRICULA");
        var parent1 = Get("FILIACAO_1", "PAI");
        var parent2 = Get("FILIACAO_2", "MAE");
        foreach (var key in new[] { "NOME_FILHO", "NOME_FILHA", "NOME_CRIANCA", "NOME_DEPENDENTE" }) Set(key, name);
        foreach (var key in new[] { "CPF_FILHO", "CPF_FILHA", "CPF_CRIANCA", "CPF_DEPENDENTE" }) Set(key, cpf);
        Set("DATA_NASCIMENTO", birth); Set("NASCIMENTO", birth);
        Set("DATA_CERTIDAO", registration); Set("DATA_REGISTRO", registration);
        Set("MATRICULA_CERTIDAO", matrix); Set("MATRICULA", matrix);
        Set("FILIACAO_1", parent1); Set("PAI", parent1);
        Set("FILIACAO_2", parent2); Set("MAE", parent2);

        var sex = Normalize(Get("SEXO_FILHO"));
        if (sex.Contains("FEMININO"))
        {
            Set("SEXO_FILHO", "feminino"); Set("TIPO_FILHO", "filha"); Set("SEU_SUA_FILHO", "sua filha");
        }
        else if (sex.Contains("MASCULINO"))
        {
            Set("SEXO_FILHO", "masculino"); Set("TIPO_FILHO", "filho"); Set("SEU_SUA_FILHO", "seu filho");
        }
        else
        {
            Set("TIPO_FILHO", "filho(a)"); Set("SEU_SUA_FILHO", "seu filho(a)");
        }

        if (TryParseDate(birth, out var birthDate))
        {
            Set("MES_IMPLANTACAO", birthDate.ToString("MM/yyyy"));
            Set("MES_REFERENCIA_SAQUE", birthDate.ToString("MM/yyyy"));
            Set("MES_TERMINO_PRE_ESCOLAR", birthDate.AddYears(6).ToString("MM/yyyy"));
        }
        if (military is not null)
        {
            Set("MILITAR_ID", military.Id.ToString()); Set("MILITAR_NOME", military.Name);
            Set("MILITAR_CPF", MilitaryFormatting.FormatCpf(military.Cpf)); Set("MILITAR_POSTO", military.Rank);
        }
        return result;
    }

    private static OcrEngine? CreateEngine()
    {
        try
        {
            var profile = OcrEngine.TryCreateFromUserProfileLanguages();
            if (profile is not null) return profile;
        }
        catch { }
        try { return OcrEngine.TryCreateFromLanguage(new Language("pt-BR")); }
        catch { return null; }
    }

    private static async Task<string> ReadPdfAsync(string path, OcrEngine engine, CancellationToken cancellationToken)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var pdf = await PdfDocument.LoadFromFileAsync(file);
        var builder = new StringBuilder();
        var pages = Math.Min(pdf.PageCount, 6u);
        for (uint index = 0; index < pages; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var page = pdf.GetPage(index);
            using var stream = new InMemoryRandomAccessStream();
            var max = Math.Min(2600d, OcrEngine.MaxImageDimension);
            var scale = Math.Min(3d, max / Math.Max(page.Size.Width, page.Size.Height));
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, page.Size.Width * scale),
                DestinationHeight = (uint)Math.Max(1, page.Size.Height * scale)
            };
            await page.RenderToStreamAsync(stream, options);
            stream.Seek(0);
            var text = await RecognizeStreamAsync(stream, engine);
            if (!string.IsNullOrWhiteSpace(text)) builder.AppendLine(text).AppendLine();

            if (index == 0)
            {
                // Certidões brasileiras usam várias caixas lado a lado. O OCR de
                // página inteira preserva as linhas, mas pode misturar colunas e
                // associar o valor ao rótulo vizinho. As leituras ampliadas por
                // região fornecem uma segunda fonte, com contexto e ordem locais.
                foreach (var region in CertificateRegions)
                {
                    var regionText = await RecognizePdfRegionAsync(page, engine, region);
                    if (string.IsNullOrWhiteSpace(regionText)) continue;
                    builder.Append('[').Append(region.Marker).AppendLine("]");
                    builder.AppendLine(regionText);
                    builder.Append("[/").Append(region.Marker).AppendLine("]").AppendLine();
                }
            }
        }
        return builder.ToString().Trim();
    }

    private static async Task<string> RecognizePdfRegionAsync(PdfPage page, OcrEngine engine, CertificateRegion region)
    {
        var source = new Windows.Foundation.Rect(
            page.Size.Width * region.X,
            page.Size.Height * region.Y,
            page.Size.Width * region.Width,
            page.Size.Height * region.Height);
        var destinationWidth = Math.Min(2500d, OcrEngine.MaxImageDimension);
        var destinationHeight = Math.Min(
            OcrEngine.MaxImageDimension,
            destinationWidth * source.Height / source.Width);

        using var stream = new InMemoryRandomAccessStream();
        var options = new PdfPageRenderOptions
        {
            SourceRect = source,
            DestinationWidth = (uint)Math.Max(1, destinationWidth),
            DestinationHeight = (uint)Math.Max(1, destinationHeight)
        };
        await page.RenderToStreamAsync(stream, options);
        stream.Seek(0);
        return await RecognizeStreamAsync(stream, engine);
    }

    private static async Task<string> ReadImageAsync(string path, OcrEngine engine, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        return await RecognizeStreamAsync(stream, engine);
    }

    private static async Task<string> RecognizeStreamAsync(IRandomAccessStream stream, OcrEngine engine)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var maxDimension = Math.Min(2600u, OcrEngine.MaxImageDimension);
        var width = decoder.PixelWidth;
        var height = decoder.PixelHeight;
        // Imagens pequenas também são ampliadas. Antes elas só eram reduzidas,
        // deixando fotos/recortes de celular com poucos pixels por caractere.
        var scale = Math.Min(2.5d, maxDimension / (double)Math.Max(width, height));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, width * scale),
            ScaledHeight = (uint)Math.Max(1, height * scale),
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        var result = await engine.RecognizeAsync(bitmap);
        // OcrResult.Text achata algumas certidões digitalizadas em um único
        // parágrafo. Isso fazia um endereço levar junto nome, CPF, matrícula e
        // todo o restante do documento. As linhas visuais preservam os limites
        // dos campos e permitem interpretar cada bloco com segurança.
        var visualLines = result.Lines
            .Select(x => Regex.Replace(x.Text ?? string.Empty, @"\s+", " ").Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        return visualLines.Count > 0
            ? string.Join(Environment.NewLine, visualLines)
            : result.Text?.Trim() ?? string.Empty;
    }

    private static CertificateOcrResult Parse(string? text, MilitaryRecord military)
    {
        var sourceText = text ?? string.Empty;
        var result = new CertificateOcrResult();
        var lines = CleanLines(sourceText);
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var identificationText = PreferMarkedSection(sourceText, "IDENTIFICACAO_OCR_AMPLIADA");
        // Dia/mês/ano e naturalidade podem cair no recorte de identificação,
        // enquanto local/município/sexo ficam no recorte seguinte. Interpreta os
        // dois como um único bloco de nascimento.
        var birthText = PreferMarkedSections(
            sourceText,
            "IDENTIFICACAO_OCR_AMPLIADA",
            "NASCIMENTO_OCR_AMPLIADO",
            "NASCIMENTO_FILIACAO_TRANSICAO_OCR");
        var registrationText = PreferMarkedSection(sourceText, "DATA_REGISTRO_OCR_AMPLIADA");
        var registryText = PreferMarkedSection(sourceText, "CARTORIO_OCR_AMPLIADO");

        var name = FindChildName(identificationText, CleanLines(identificationText));
        if (string.IsNullOrWhiteSpace(name)) name = FindChildName(sourceText, lines);
        Set(keys, "NOME_FILHO", name.ToUpper(PtBr));

        var cpfs = CpfRegex().Matches(identificationText + Environment.NewLine + sourceText).Select(m => FormatCpf(m.Value)).Where(IsValidCpf).Distinct().ToList();
        var militaryCpf = Digits(military.Cpf);
        var childCpf = cpfs.FirstOrDefault(x => Digits(x) != militaryCpf) ?? cpfs.FirstOrDefault() ?? string.Empty;
        Set(keys, "CPF_FILHO", childCpf);

        var matrixContext = FindContext(identificationText, "MATR[IÍ]CULA", 260);
        if (string.IsNullOrWhiteSpace(matrixContext)) matrixContext = identificationText;
        var matrixMatch = LongDigitsRegex().Match(matrixContext);
        if (!matrixMatch.Success) matrixMatch = LongDigitsRegex().Match(sourceText);
        if (matrixMatch.Success) Set(keys, "MATRICULA_CERTIDAO", FormatMatrix(matrixMatch.Value));

        var birthLines = CleanLines(birthText);
        var birth = ExtractDates(birthText).FirstOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(birth)) birth = FindBoxedBirthDate(birthLines);
        if (string.IsNullOrWhiteSpace(birth)) birth = FindBoxedBirthDate(lines);
        if (string.IsNullOrWhiteSpace(birth))
            birth = FindDateAfterLabel(birthText + Environment.NewLine + sourceText, ["DATA DO NASCIMENTO", "DATA DE NASCIMENTO", "NASCIMENTO"]);
        var registration = ExtractDates(registrationText).FirstOrDefault() ?? FindRegistrationDate(sourceText);
        if (string.IsNullOrWhiteSpace(registration))
            registration = FindDateAfterLabel(registrationText + Environment.NewLine + sourceText, ["DATA DE REGISTRO", "DATA DA CERTIDAO", "DATA DA CERTIDÃO", "CONTEÚDO DA CERTIDÃO", "CONTEUDO DA CERTIDAO"]);
        var allDates = ExtractDates(sourceText).ToList();
        if (string.IsNullOrWhiteSpace(birth)) birth = allDates.FirstOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(registration))
            registration = allDates.FirstOrDefault(x => !x.Equals(birth, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        if (TryParseDate(birth, out var birthDate) && TryParseDate(registration, out var registrationDate) && registrationDate < birthDate)
            registration = string.Empty;
        Set(keys, "DATA_NASCIMENTO", birth); Set(keys, "DATA_CERTIDAO", registration);

        var parents = FindParents(sourceText, lines, name, military);
        Set(keys, "FILIACAO_1", parents.ElementAtOrDefault(0)?.ToUpper(PtBr));
        Set(keys, "FILIACAO_2", parents.ElementAtOrDefault(1)?.ToUpper(PtBr));

        var normalizedDocument = Normalize(birthText + Environment.NewLine + sourceText);
        var sex = normalizedDocument.Contains("FEMININO", StringComparison.Ordinal)
                  || Regex.IsMatch(normalizedDocument, @"\bF[EO]M[IL1]N[IL1]N[O0]\b") ? "feminino"
            : normalizedDocument.Contains("MASCULINO", StringComparison.Ordinal) ? "masculino" : string.Empty;
        Set(keys, "SEXO_FILHO", sex);
        Set(keys, "TIPO_FILHO", sex == "feminino" ? "filha" : sex == "masculino" ? "filho" : "filho(a)");
        Set(keys, "SEU_SUA_FILHO", sex == "feminino" ? "sua filha" : sex == "masculino" ? "seu filho" : "seu filho(a)");

        SetAdditionalCertificateFields(keys, birthText, registrationText, registryText, sourceText);

        var location = LocationRegex().Match(registryText + Environment.NewLine + (text ?? string.Empty));
        var certificateLocation = location.Success
            ? $"{TitleCase(location.Groups[1].Value)} - {location.Groups[2].Value.ToUpperInvariant()}"
            : string.Empty;
        certificateLocation = CorrectCertificateLocation(
            certificateLocation,
            keys.GetValueOrDefault("MUNICIPIO_NASCIMENTO", string.Empty),
            keys.GetValueOrDefault("MUNICIPIO_NATURALIDADE", string.Empty));
        Set(keys, "LOCAL_CERTIDAO", certificateLocation);
        var registryLines = CleanLines(registryText);
        var registry = FindRegistryOffice(registryText + Environment.NewLine + sourceText, registryLines.Concat(lines).ToList(), certificateLocation);
        Set(keys, "CARTORIO", registry);
        var address = FindRegistryAddress(lines);
        if (string.IsNullOrWhiteSpace(address)) address = FindRegistryAddress(registryLines);
        Set(keys, "ENDERECO_CARTORIO", address);

        result.Keys = ExpandAliases(keys, military);
        result.ConfidenceScore = Score(result.Keys);
        if (string.IsNullOrWhiteSpace(name)) result.Warnings.Add("Nome da criança não identificado automaticamente.");
        if (string.IsNullOrWhiteSpace(birth)) result.Warnings.Add("Data de nascimento não identificada automaticamente.");
        if (string.IsNullOrWhiteSpace(registration)) result.Warnings.Add("Data do registro não identificada automaticamente.");
        if (string.IsNullOrWhiteSpace(childCpf)) result.Warnings.Add("CPF da criança não identificado automaticamente.");
        if (parents.Count < 2) result.Warnings.Add("Filiação não identificada com segurança; confira manualmente.");
        if (string.IsNullOrWhiteSpace(address)) result.Warnings.Add("Endereço do cartório não identificado com segurança.");
        return result;
    }

    private static List<string> CleanLines(string? text) => (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(x => Regex.Replace(x.Replace('|', ' ').Replace('_', ' '), @"\s+", " ").Trim(' ', '-', '–', ':', ';', '.'))
        .Where(x => x.Length > 1).ToList();

    private static string FindChildName(string text, IReadOnlyList<string> lines)
    {
        var oneLine = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        var inline = Regex.Match(oneLine,
            @"\bNome\s+(?<name>[A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ][A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ' -]{5,90}?)(?=\s+(?:Número|Numero|CPF|Matr[ií]cula|Data|Hor[aá]rio|Local|Munic[ií]pio|Sexo|feminino|masculino)\b)",
            RegexOptions.IgnoreCase);
        if (inline.Success)
        {
            var candidate = CleanNameCandidate(inline.Groups["name"].Value);
            if (LooksLikeName(candidate) && !LooksLikeAdministrativeLine(candidate)) return candidate;
        }

        var byLabel = FindLabelValue(lines, ["NOME DO REGISTRADO", "NOME DO(A) REGISTRADO(A)", "NOME DA CRIANCA", "NOME DA CRIANÇA", "NOME"], LooksLikeName);
        if (!string.IsNullOrWhiteSpace(byLabel)) return CleanNameCandidate(byLabel);
        return CleanNameCandidate(lines.FirstOrDefault(x => LooksLikeName(x) && !LooksLikeAdministrativeLine(x)) ?? string.Empty);
    }

    private static List<string> FindParents(string sourceText, IReadOnlyList<string> lines, string childName, MilitaryRecord military)
    {
        var result = new List<string>();

        // Primeiro usa a faixa ampliada. Mesmo quando o OCR troca "genitor"
        // por "gontlor", a estrutura permanece: rótulo e, logo abaixo, um
        // único nome. Isso separa pai/mãe dos avós, que vêm aos pares com ';'.
        var enlarged = ExtractMarkedSection(sourceText, "[FILIACAO_OCR_AMPLIADA]", "[/FILIACAO_OCR_AMPLIADA]");
        AddNamesAfterParentLabels(CleanLines(enlarged));

        // Mantém compatibilidade com imagens e certidões cujo OCR de página
        // inteira já seja suficiente.
        if (result.Count < 2) AddNamesAfterParentLabels(lines);

        // Compatibilidade com digitalizações antigas: os recortes estreitos só
        // entram como último recurso, pois em outro layout eles podem cair na
        // segunda filiação ou até sobre a data de registro.
        if (result.Count < 2) Add(BestNameFromRegion("GENITOR_SUPERIOR_VALOR_OCR"));
        if (result.Count < 2) Add(BestNameFromRegion("GENITOR_INTERMEDIARIO_VALOR_OCR"));
        if (result.Count < 2) Add(BestNameFromRegion("GENITOR_INFERIOR_VALOR_OCR"));
        return result.Take(2).ToList();

        string BestNameFromRegion(string marker)
        {
            var region = ExtractMarkedSection(sourceText, $"[{marker}]", $"[/{marker}]");
            return CleanLines(region)
                .Select(CleanExpectedNameCandidate)
                .Where(line => !line.Contains(';')
                               && line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3
                               && LooksLikeName(line)
                               && !LooksLikeAdministrativeLine(line))
                .OrderByDescending(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)
                .ThenByDescending(line => line.Length)
                .FirstOrDefault() ?? string.Empty;
        }

        void AddNamesAfterParentLabels(IReadOnlyList<string> candidateLines)
        {
            for (var i = 0; i < candidateLines.Count && result.Count < 2; i++)
            {
                // Os próprios delimitadores internos contêm "GENITOR ... OCR";
                // eles não são rótulos impressos da certidão.
                if (Normalize(candidateLines[i]).Contains("OCR")) continue;
                if (!LooksLikeParentLabel(candidateLines[i])) continue;
                for (var next = i + 1; next < Math.Min(candidateLines.Count, i + 4); next++)
                {
                    var candidate = candidateLines[next];
                    if (LooksLikeParentLabel(candidate)) break;
                    if (!LooksLikeName(candidate) || candidate.Contains(';') || LooksLikeAdministrativeLine(candidate)) continue;
                    Add(candidate);
                    break;
                }
            }
        }

        void Add(string value)
        {
            value = CleanNameCandidate(value).ToUpper(PtBr);
            if (string.IsNullOrWhiteSpace(value) || value.Equals(CleanNameCandidate(childName).ToUpper(PtBr), StringComparison.OrdinalIgnoreCase)) return;
            if (!LooksLikeName(value) || LooksLikeAdministrativeLine(value)) return;
            if (!string.IsNullOrWhiteSpace(military.Name) && IsLikelySamePersonName(value, military.Name))
                value = Regex.Replace(military.Name.Trim(), @"\s+", " ").ToUpper(PtBr);
            if (!result.Contains(value, StringComparer.OrdinalIgnoreCase)
                && !result.Any(existing => IsLikelySamePersonName(value, existing))) result.Add(value);
        }
    }

    private static string CleanExpectedNameCandidate(string value)
    {
        var clean = Regex.Replace(value ?? string.Empty, @"1[gG](?=\s|$)", "IE");
        clean = clean.Replace('1', 'I').Replace('0', 'O').Replace('5', 'S');
        return Regex.Replace(clean, @"\s+", " ").Trim(' ', '-', '–', ':', ';', '.');
    }

    private static bool IsLikelySamePersonName(string candidate, string knownName)
    {
        static HashSet<string> Tokens(string value) => Normalize(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 2)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidateTokens = Tokens(candidate);
        var knownTokens = Tokens(knownName);
        if (candidateTokens.Count == 0 || knownTokens.Count == 0) return false;
        var shared = candidateTokens.Count(knownTokens.Contains);
        return shared >= 2 && shared / (double)Math.Min(candidateTokens.Count, knownTokens.Count) >= 0.50;
    }

    private static string ExtractMarkedSection(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        start += startMarker.Length;
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        return end > start ? source[start..end] : source[start..];
    }

    private static string PreferMarkedSection(string source, string marker)
    {
        var marked = ExtractMarkedSection(source, $"[{marker}]", $"[/{marker}]");
        return string.IsNullOrWhiteSpace(marked) ? source : marked;
    }

    private static string PreferMarkedSections(string source, params string[] markers)
    {
        var sections = markers
            .Select(marker => ExtractMarkedSection(source, $"[{marker}]", $"[/{marker}]"))
            .Where(section => !string.IsNullOrWhiteSpace(section))
            .ToList();
        return sections.Count == 0 ? source : string.Join(Environment.NewLine, sections);
    }

    private static bool LooksLikeParentLabel(string value)
    {
        var normalized = Normalize(value);
        if (normalized.Contains("FILIACAO")) return true;
        var letters = Regex.Replace(normalized, @"[^A-Z]", string.Empty);
        var namePrefix = Regex.IsMatch(letters, @"^NO[MN][A-Z]{0,3}D[OA]");
        var parentWord = letters.Contains("GENITOR")
                         || letters.Contains("GEN")
                         || letters.Contains("GON")
                         || letters.Contains("ONTLOR");
        return namePrefix && parentWord;
    }

    private static void SetAdditionalCertificateFields(
        IDictionary<string, string> keys,
        string birthText,
        string registrationText,
        string registryText,
        string sourceText)
    {
        var birthLines = CleanLines(birthText);
        var allLines = CleanLines(sourceText);

        var timeMatch = Regex.Match(birthText + Environment.NewLine + sourceText,
            @"\b(?<hour>[0-2]?\d)\s*[hH:]\s*(?<minute>[0-5]\d)\s*(?:min)?\b",
            RegexOptions.IgnoreCase);
        if (timeMatch.Success)
            Set(keys, "HORARIO_NASCIMENTO", $"{timeMatch.Groups["hour"].Value.PadLeft(2, '0')}h{timeMatch.Groups["minute"].Value}min");

        var naturality = FindValueAfterFuzzyLabel(birthLines, ["NATURAL"], LooksLikePlaceValue);
        Set(keys, "MUNICIPIO_NATURALIDADE", TitleCase(naturality));

        var filiationLines = CleanLines(PreferMarkedSection(sourceText, "FILIACAO_OCR_AMPLIADA"));
        var birthplace = FindBirthplace(birthLines.Concat(filiationLines).ToList());
        if (string.IsNullOrWhiteSpace(birthplace))
            birthplace = FindValueAfterFuzzyLabel(birthLines, ["LOCAL", "NASCIMENTO"],
                value => LooksLikePlaceValue(value) && Normalize(value) != Normalize(naturality));
        Set(keys, "LOCAL_NASCIMENTO", TitleCase(birthplace));

        var birthMunicipality = FindValueAfterFuzzyLabel(birthLines, ["MUN", "NASCIMENTO"],
            value => LooksLikePlaceValue(value) && Normalize(value) != Normalize(birthplace));
        Set(keys, "MUNICIPIO_NASCIMENTO", TitleCase(birthMunicipality));

        var ufContext = FindContext(birthText, @"\bUF\b", 80);
        var ufMatch = Regex.Match(ufContext, @"\b(AC|AL|AP|AM|BA|CE|DF|ES|GO|MA|MG|MS|MT|PA|PB|PE|PI|PR|RJ|RN|RO|RR|RS|SC|SE|SP|TO)\b", RegexOptions.IgnoreCase);
        if (!ufMatch.Success)
            ufMatch = Regex.Match(string.Join(Environment.NewLine, birthLines.AsEnumerable().Reverse()), @"\b(AC|AL|AP|AM|BA|CE|DF|ES|GO|MA|MG|MS|MT|PA|PB|PE|PI|PR|RJ|RN|RO|RR|RS|SC|SE|SP|TO)\b", RegexOptions.IgnoreCase);
        if (ufMatch.Success) Set(keys, "UF_NASCIMENTO", ufMatch.Groups[1].Value.ToUpperInvariant());

        var dnvText = PreferMarkedSection(sourceText, "DNV_OCR_AMPLIADA");
        var dnvMatch = Regex.Match(dnvText + Environment.NewLine + registrationText + Environment.NewLine + sourceText,
            @"\b\d{2}[.\s-]?\d{8}[.\s-]?\d\b");
        if (dnvMatch.Success) Set(keys, "DNV", Regex.Replace(dnvMatch.Value, @"\s+", string.Empty));

        var filiationText = PreferMarkedSection(sourceText, "FILIACAO_OCR_AMPLIADA");
        var grandparentPairs = CleanLines(filiationText + Environment.NewLine + sourceText)
            .Where(line => line.Contains(';'))
            .Select(CleanGrandparentsCandidate)
            .Where(LooksLikeGrandparentsPair)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();
        Set(keys, "AVOS_PATERNOS", grandparentPairs.ElementAtOrDefault(0)?.ToUpper(PtBr));
        Set(keys, "AVOS_MATERNOS", grandparentPairs.ElementAtOrDefault(1)?.ToUpper(PtBr));

        var officeLines = CleanLines(registryText).Concat(allLines).ToList();
        var signedOfficial = officeLines
            .Where(line => Regex.IsMatch(line, @"[-–•]\s*OFICIAL\b", RegexOptions.IgnoreCase))
            .Select(line => Regex.Replace(line, @"\s*[-–•]\s*OFICIAL\b.*$", string.Empty, RegexOptions.IgnoreCase).Trim())
            .FirstOrDefault(LooksLikeName);
        if (!string.IsNullOrWhiteSpace(signedOfficial))
            Set(keys, "NOME_OFICIAL_CARTORIO", TitleCase(signedOfficial));

        for (var index = 0; index < officeLines.Count && !keys.ContainsKey("NOME_OFICIAL_CARTORIO"); index++)
        {
            var normalized = Normalize(officeLines[index]);
            var letters = Regex.Replace(normalized, @"[^A-Z]", string.Empty);
            if (!Regex.IsMatch(letters, @"^NO[MN][A-Z]{0,3}D[AO].{0,4}OFI")) continue;
            var official = officeLines.Skip(index + 1).Take(3).FirstOrDefault(LooksLikeName);
            if (!string.IsNullOrWhiteSpace(official)) Set(keys, "NOME_OFICIAL_CARTORIO", TitleCase(official));
            break;
        }
        var cnsMatch = Regex.Match(sourceText, @"\bCNS\s*(?:N[º°O0]?\s*)?(?<number>\d{5,8}(?:[-.]\d)?)\b", RegexOptions.IgnoreCase);
        if (cnsMatch.Success) Set(keys, "CNS_CARTORIO", cnsMatch.Groups["number"].Value);
    }

    private static bool LooksLikePlaceValue(string? value)
    {
        var text = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim(' ', '-', '–', ':', ';', '.');
        var normalized = Normalize(text);
        if (text.Length is < 3 or > 90 || text.Any(char.IsDigit) || LooksLikeAdministrativeLine(text)) return false;
        var letters = Regex.Replace(normalized, @"[^A-Z]", string.Empty);
        if (new[] { "FEMININO", "MASCULINO", "NAO CONSTA", "HORAS", "DIA", "MES", "ANO", "DNV" }.Any(normalized.Contains)) return false;
        if (Regex.IsMatch(letters, @"^(?:M[EO]S|S[AE]XO|D(?:IA|ID)|UF)$")) return false;
        return Regex.IsMatch(text, @"^[A-Za-zÁÀÂÃÉÊÍÓÔÕÚÜÇáàâãéêíóôõúüç' -]+$");
    }

    private static string FindValueAfterFuzzyLabel(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> labelFragments,
        Func<string, bool> validator)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var normalized = Normalize(lines[index]);
            if (!ContainsFuzzyLabelFragments(normalized, labelFragments)) continue;
            for (var next = index + 1; next < Math.Min(lines.Count, index + 16); next++)
            {
                if (validator(lines[next])) return lines[next];
                if (LooksLikeParentLabel(lines[next])) break;
            }
        }
        return string.Empty;
    }

    private static bool ContainsFuzzyLabelFragments(string normalizedLine, IReadOnlyList<string> fragments)
    {
        var letters = Regex.Replace(normalizedLine, @"[^A-Z]", string.Empty);
        return fragments.All(fragment =>
        {
            var expected = Regex.Replace(Normalize(fragment), @"[^A-Z]", string.Empty);
            if (expected == "NASCIMENTO")
                return letters.Contains("NASC") || letters.Contains("NSC") || letters.Contains("OSC");
            return letters.Contains(expected);
        });
    }

    private static string FindBirthplace(IReadOnlyList<string> lines)
    {
        var candidates = new List<string>();
        for (var index = 0; index < lines.Count; index++)
        {
            if (!Regex.IsMatch(Normalize(lines[index]), @"HOS.{0,5}TAL|MATER")) continue;
            var parts = new List<string> { lines[index] };
            var firstNormalized = Normalize(lines[index]);
            if (!Regex.IsMatch(firstNormalized, @"\b(?:DE|DA|DO)$"))
            {
                candidates.Add(lines[index]);
                continue;
            }
            for (var next = index + 1; next < Math.Min(lines.Count, index + 5); next++)
            {
                var normalized = Normalize(lines[next]);
                var letters = Regex.Replace(normalized, @"[^A-Z]", string.Empty);
                if (LooksLikeParentLabel(lines[next])
                    || (letters.Contains("MUN") && (letters.Contains("NASC") || letters.Contains("NSC") || letters.Contains("OSC") || letters.Contains("NATURAL")))
                    || normalized is "UF" or "SEXO" or "DNV"
                    || normalized.Contains("MASCULINO")
                    || normalized.Contains("FEMININO")
                    || normalized.Contains("DATA DE")) break;
                parts.Add(lines[next]);
            }
            candidates.Add(Regex.Replace(string.Join(" ", parts), @"\s+", " ").Trim(' ', '-', '–', ':', ';', '.'));
        }
        return candidates.OrderByDescending(candidate => candidate.Length).FirstOrDefault() ?? string.Empty;
    }

    private static string CleanGrandparentsCandidate(string value)
        => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim(' ', '-', '–', ':', ';', '.');

    private static bool LooksLikeGrandparentsPair(string value)
    {
        var parts = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 && parts.All(part => LooksLikeName(part) && !LooksLikeAdministrativeLine(part));
    }

    private static string FindRegistryOffice(string text, IReadOnlyList<string> lines, string certificateLocation)
    {
        var preferred = lines.FirstOrDefault(x => Normalize(x).Contains("REGISTRO CIVIL DAS PESSOAS NATURAIS DE") && !Normalize(x).Contains("REPUBLICA"));
        if (!string.IsNullOrWhiteSpace(preferred)) return preferred;
        preferred = lines.FirstOrDefault(x => Normalize(x).Contains("OFICIAL DE REGISTRO CIVIL") && !Normalize(x).Contains("REPUBLICA"));
        if (!string.IsNullOrWhiteSpace(preferred)) return preferred;
        var oneLine = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        var detailed = Regex.Match(oneLine,
            @"REGISTRO\s+CIVIL\s+(?:DAS?|DE)\s+PESSOAS\s+NATURAIS\s+DE\s+(?<place>[A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ ]{3,55}?)\s*-\s*(?<unit>\d{1,2}\s*(?:º|O|0)?\s*SUBDISTRITO)\s*-\s*(?<uf>AC|AL|AP|AM|BA|CE|DF|ES|GO|MA|MG|MS|MT|PA|PB|PE|PI|PR|RJ|RN|RO|RR|RS|SC|SE|SP|TO)\b",
            RegexOptions.IgnoreCase);
        if (detailed.Success)
        {
            // Usa a localidade mais limpa encontrada na própria certidão. O OCR
            // costuma transformar "1º" em "10"; só mantém o subdistrito quando
            // o símbolo ordinal foi realmente reconhecido.
            var place = !string.IsNullOrWhiteSpace(certificateLocation)
                ? certificateLocation
                : $"{TitleCase(detailed.Groups["place"].Value)} - {detailed.Groups["uf"].Value.ToUpperInvariant()}";
            var unit = detailed.Groups["unit"].Value;
            return unit.Contains('º')
                ? $"Registro Civil das Pessoas Naturais de {place} - {unit}"
                : $"Registro Civil das Pessoas Naturais de {place}";
        }

        var compact = Regex.Replace(Normalize(oneLine), @"[^A-Z]", string.Empty);
        var hasCivilRegistry = compact.Contains("REGISTROCIVILDASPES")
                               || compact.Contains("OFICIODECIVILDEPESSOASNATURAIS")
                               || (compact.Contains("REGISTROCIVIL") && compact.Contains("PESSOASNATURAIS"));
        if (hasCivilRegistry && !string.IsNullOrWhiteSpace(certificateLocation))
            return $"Registro Civil das Pessoas Naturais de {certificateLocation}";
        return string.Empty;
    }

    private static string CorrectCertificateLocation(string certificateLocation, params string[] municipalityHints)
    {
        var location = LocationRegex().Match(certificateLocation);
        if (!location.Success) return certificateLocation;

        var recognizedPlace = TitleCase(location.Groups[1].Value);
        var recognizedNormalized = Normalize(recognizedPlace);
        var corrected = municipalityHints
            .Where(LooksLikePlaceValue)
            .Select(TitleCase)
            .Where(hint => Math.Abs(Normalize(hint).Length - recognizedNormalized.Length) <= 2)
            .Select(hint => (Value: hint, Distance: EditDistance(Normalize(hint), recognizedNormalized)))
            .Where(candidate => candidate.Distance <= Math.Max(1, recognizedNormalized.Length / 6))
            .OrderBy(candidate => candidate.Distance)
            .Select(candidate => candidate.Value)
            .FirstOrDefault();

        return $"{corrected ?? recognizedPlace} - {location.Groups[2].Value.ToUpperInvariant()}";
    }

    private static int EditDistance(string first, string second)
    {
        var previous = Enumerable.Range(0, second.Length + 1).ToArray();
        var current = new int[second.Length + 1];
        for (var row = 1; row <= first.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= second.Length; column++)
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + (first[row - 1] == second[column - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[second.Length];
    }

    private static string FindRegistryAddress(IReadOnlyList<string> lines)
    {
        var scored = new List<(string Value, int Score)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var value = lines[i];
            if (!Regex.IsMatch(value, @"\b(RUA|R\.|AVENIDA|AV\.|ALAMEDA|TRAVESSA|PRAÇA|PRACA|RODOVIA)\b", RegexOptions.IgnoreCase)) continue;
            var normalized = Normalize(value);
            if (ContainsPersonalField(normalized)) continue;

            // Alguns cartórios quebram o CEP na linha seguinte ao endereço.
            // Reúne somente essa continuação curta, sem absorver telefone/e-mail.
            var addressValue = value;
            if (normalized.Contains("CEP") && !Regex.IsMatch(value, @"\d{5}[-.\s]?\d{3}"))
            {
                var continuation = lines.Skip(i + 1).Take(2)
                    .FirstOrDefault(line => Regex.IsMatch(line, @"\d{5}[-.\s]?\d{3}"));
                if (!string.IsNullOrWhiteSpace(continuation)) addressValue += " " + continuation;
            }

            var context = Normalize(string.Join(" ", lines.Skip(Math.Max(0, i - 6)).Take(10)));
            var score = 0;
            if (new[] { "CARTORIO", "REGISTRO CIVIL", "OFICIAL", "CNS", "SELO" }.Any(context.Contains)) score += 12;
            if (new[] { "CEP", "FONE", "TELEFONE", "E-MAIL" }.Any(normalized.Contains)) score += 5;
            if (new[] { "LOCAL DE NASCIMENTO", "HOSPITAL", "MATERNIDADE", "NATURALIDADE" }.Any(context.Contains)) score -= 15;
            scored.Add((CleanRegistryAddress(addressValue), score));
        }

        var selected = scored.Where(x => !string.IsNullOrWhiteSpace(x.Value)).OrderByDescending(x => x.Score).FirstOrDefault();
        return selected.Score > 0 ? selected.Value : string.Empty;
    }

    private static bool ContainsPersonalField(string normalized) =>
        new[] { "CPF", "MATRICULA", "DATA DE NASCIMENTO", "NOME DO", "NOME DA", "NOME ", "FILIACAO", "GENITOR" }.Any(normalized.Contains);

    private static string CleanRegistryAddress(string value)
    {
        var clean = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim(' ', '-', '–', ':', ';', '.');
        clean = Regex.Replace(clean, @"\s+-?\s*(?:FON[EA]|TELEFONE|TEL\.?|E-?MAIL|VALIDA(?:Ç|C)[AÃ]O)\b.*$", string.Empty, RegexOptions.IgnoreCase).Trim(' ', '-', '.', ';');
        clean = Regex.Replace(clean, @"\bN[O0]\s+(?=\d)", "nº ", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"\bCEP\s*[.:]?\s*(\d{5})[-.\s]?(\d{3})\b", "CEP: $1-$2", RegexOptions.IgnoreCase);
        return clean.Length <= 180 ? clean : string.Empty;
    }

    private static string FindBoxedBirthDate(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var label = Regex.Replace(Normalize(lines[i]), @"[^A-Z0-9]", string.Empty);
            if (!Regex.IsMatch(label, @"^D(?:IA|ID|1A|LA)$")) continue;

            var dayPosition = Enumerable.Range(i + 1, Math.Min(4, lines.Count - i - 1))
                .FirstOrDefault(index => int.TryParse(lines[index], out var candidate) && candidate is >= 1 and <= 31);
            if (dayPosition <= i || !int.TryParse(lines[dayPosition], out var day)) continue;

            var yearPosition = Enumerable.Range(dayPosition + 1, Math.Min(22, lines.Count - dayPosition - 1))
                .FirstOrDefault(index => Regex.IsMatch(lines[index], @"^(?:19|20)\d{2}$"));
            if (yearPosition <= dayPosition || !int.TryParse(lines[yearPosition], out var year)) continue;

            var monthLabel = Enumerable.Range(dayPosition + 1, Math.Min(10, yearPosition - dayPosition - 1))
                .FirstOrDefault(index => Regex.IsMatch(Regex.Replace(Normalize(lines[index]), @"[^A-Z]", string.Empty), @"^M[EO][S5]$"));
            var monthSearchStart = monthLabel > dayPosition ? monthLabel + 1 : dayPosition + 1;
            var monthText = lines.Skip(monthSearchStart).Take(Math.Max(0, yearPosition - monthSearchStart))
                .FirstOrDefault(x => int.TryParse(x, out var candidate) && candidate is >= 1 and <= 12);
            if (!int.TryParse(monthText, out var month)) continue;
            if (DateTime.TryParseExact($"{day:00}/{month:00}/{year:0000}", "dd/MM/yyyy", PtBr, DateTimeStyles.None, out var date))
                return date.ToString("dd/MM/yyyy");
        }
        return string.Empty;
    }

    private static string FindRegistrationDate(string text)
    {
        var oneLine = Regex.Replace(text ?? string.Empty, @"\s+", " ");
        foreach (var pattern in new[]
                 {
                     @"assinou\s+eletronicamente.{0,120}?\bdata\s+(?<date>\d{1,2}[./-]\d{1,2}[./-](?:19|20)\d{2})",
                     @"materializada.{0,120}?\bdata\s+(?<date>\d{1,2}[./-]\d{1,2}[./-](?:19|20)\d{2})"
                 })
        {
            var match = Regex.Match(oneLine, pattern, RegexOptions.IgnoreCase);
            if (match.Success && TryParseDate(match.Groups["date"].Value, out var date)) return date.ToString("dd/MM/yyyy");
        }
        return string.Empty;
    }

    private static string CleanNameCandidate(string value)
    {
        var text = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim(' ', '-', '–', ':', ';', '.');
        text = Regex.Replace(text, @"\b(N[uú]mero|CPF|Matr[ií]cula|Data|Hor[aá]rio|Local|Munic[ií]pio|Sexo|feminino|masculino)\b.*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        return text;
    }

    private static string FindLabelValue(IReadOnlyList<string> lines, IEnumerable<string> labels, Func<string, bool> validator)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var normalized = Normalize(lines[i]);
            foreach (var label in labels)
            {
                var nlabel = Normalize(label);
                if (!normalized.Contains(nlabel)) continue;
                var inline = ExtractValueAfterLabel(lines[i], label);
                if (validator(inline)) return inline;
                for (var next = i + 1; next < Math.Min(lines.Count, i + 4); next++)
                    if (validator(lines[next]) && !LooksLikeAdministrativeLine(lines[next])) return lines[next];
            }
        }
        return string.Empty;
    }

    private static string ExtractValueAfterLabel(string line, string label)
    {
        var raw = (line ?? string.Empty).Trim();
        var colon = raw.IndexOf(':');
        if (colon >= 0 && colon + 1 < raw.Length) return raw[(colon + 1)..].Trim(' ', ':', '-', '–');

        var direct = Regex.Match(raw, Regex.Escape(label) + @"\s*[-–:]?\s*(.+)$", RegexOptions.IgnoreCase);
        if (direct.Success) return direct.Groups[1].Value.Trim(' ', ':', '-', '–');

        // Quando o OCR altera acento ou pontuação do rótulo, tenta retirar apenas a
        // quantidade aproximada de palavras do começo, sem devolver o próprio rótulo.
        var normalizedLine = Normalize(raw);
        var normalizedLabel = Normalize(label);
        var position = normalizedLine.IndexOf(normalizedLabel, StringComparison.Ordinal);
        if (position == 0)
        {
            var wordsToSkip = label.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > wordsToSkip) return string.Join(' ', words.Skip(wordsToSkip)).Trim(' ', ':', '-', '–');
        }
        return string.Empty;
    }

    private static List<string> FindValuesAfterLabel(IReadOnlyList<string> lines, IEnumerable<string> labels, int max, Func<string, bool> validator)
    {
        var result = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!labels.Any(label => Normalize(lines[i]).Contains(Normalize(label)))) continue;
            for (var next = i + 1; next < Math.Min(lines.Count, i + 8) && result.Count < max; next++)
                if (validator(lines[next]) && !result.Contains(lines[next], StringComparer.OrdinalIgnoreCase)) result.Add(lines[next]);
            if (result.Count > 0) break;
        }
        return result;
    }

    private static bool LooksLikeName(string? value)
    {
        var text = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        if (text.Length is < 7 or > 90 || text.Any(char.IsDigit)) return false;
        if (LooksLikeDatePhrase(text)) return false;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || words.Any(x => x.Length == 1 && !"DE DA DO E".Contains(x, StringComparison.OrdinalIgnoreCase))) return false;
        return Regex.IsMatch(text, @"^[A-Za-zÁÀÂÃÉÊÍÓÔÕÚÜÇáàâãéêíóôõúüç' -]+$");
    }

    private static bool LooksLikeAdministrativeLine(string value)
    {
        var normalized = Normalize(value);
        var letters = Regex.Replace(normalized, @"[^A-Z]", string.Empty);
        return LooksLikeDatePhrase(value)
               || LooksLikeParentLabel(value)
               || (letters.Contains("MUN") && (letters.Contains("NASC") || letters.Contains("NSC") || letters.Contains("OSC") || letters.Contains("NATURAL")))
               || letters.Contains("HOSPITAL")
               || letters.Contains("MATERN")
               || new[] { "CERTIDAO", "REGISTRO", "CARTORIO", "MUNICIPIO", "NASCIMENTO", "MATRICULA", "GENITOR", "FILIACAO", "OFICIAL", "ESCREVENTE", "REPUBLICA", "CPF", "FEMININO", "MASCULINO", "NAO CONSTA" }.Any(normalized.Contains);
    }

    private static bool LooksLikeDatePhrase(string value)
    {
        var normalized = NormalizeOcrDateText(value).ToUpperInvariant();
        var hasMonth = new[] { "JANEIRO", "FEVEREIRO", "MARCO", "ABRIL", "MAIO", "JUNHO", "JULHO", "AGOSTO", "SETEMBRO", "OUTUBRO", "NOVEMBRO", "DEZEMBRO" }
            .Any(normalized.Contains);
        return hasMonth && (normalized.Contains(" MIL") || normalized.Contains("VINTE") || Regex.IsMatch(normalized, @"\b(?:19|20)\d{2}\b"));
    }

    private static string FindDateAfterLabel(string text, IEnumerable<string> labels)
    {
        foreach (var label in labels)
        {
            var labelMatch = Regex.Match(text ?? string.Empty, Regex.Escape(label), RegexOptions.IgnoreCase);
            if (!labelMatch.Success) continue;
            var context = (text ?? string.Empty).Substring(labelMatch.Index, Math.Min(180, (text ?? string.Empty).Length - labelMatch.Index));
            var extracted = ExtractDates(context).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(extracted)) return extracted;

            var match = Regex.Match(context, @"((?:\d{1,2}[./-]\d{1,2}[./-](?:19|20)\d{2})|(?:\d{1,2}\s+de\s+[a-zçãéíóú]+\s+de\s+(?:19|20)\d{2}))", RegexOptions.IgnoreCase);
            if (match.Success && TryParseDate(match.Groups[1].Value, out var date)) return date.ToString("dd/MM/yyyy");
        }
        return string.Empty;
    }

    private static IEnumerable<string> ExtractDates(string text)
    {
        var found = new HashSet<string>();
        foreach (var line in CleanLines(text))
        {
            var normalized = Normalize(line);
            var containsMonth = new[] { "JANEIRO", "FEVEREIRO", "MARCO", "ABRIL", "MAIO", "JUNHO", "JULHO", "AGOSTO", "SETEMBRO", "OUTUBRO", "NOVEMBRO", "DEZEMBRO" }
                .Any(normalized.Contains);
            if (containsMonth && (normalized.Contains("MIL") || Regex.IsMatch(normalized, @"\b(?:19|20)\d{2}\b"))
                && TryParsePortugueseWordsDate(line, out var lineDate)
                && found.Add(lineDate.ToString("dd/MM/yyyy")))
                yield return lineDate.ToString("dd/MM/yyyy");
        }
        foreach (Match match in WordDateRegex().Matches(text ?? string.Empty))
            if (TryParseDate(match.Value, out var date) && found.Add(date.ToString("dd/MM/yyyy"))) yield return date.ToString("dd/MM/yyyy");
        foreach (Match match in DateRegex().Matches(text ?? string.Empty))
            if (TryParseDate(match.Value, out var date) && found.Add(date.ToString("dd/MM/yyyy"))) yield return date.ToString("dd/MM/yyyy");
        foreach (Match match in LongDateRegex().Matches(text ?? string.Empty))
            if (TryParseDate(match.Value, out var date) && found.Add(date.ToString("dd/MM/yyyy"))) yield return date.ToString("dd/MM/yyyy");
    }

    private static bool TryParseDate(string? value, out DateTime date)
    {
        var raw = (value ?? string.Empty).Trim();
        foreach (var format in new[] { "d/M/yyyy", "dd/MM/yyyy", "d-M-yyyy", "dd-MM-yyyy", "d.M.yyyy", "dd.MM.yyyy", "d 'de' MMMM 'de' yyyy", "dd 'de' MMMM 'de' yyyy" })
            if (DateTime.TryParseExact(raw, format, PtBr, DateTimeStyles.AllowWhiteSpaces, out date)) return true;
        if (TryParsePortugueseWordsDate(raw, out date)) return true;
        return DateTime.TryParse(raw, PtBr, DateTimeStyles.AllowWhiteSpaces, out date);
    }

    private static bool TryParsePortugueseWordsDate(string value, out DateTime date)
    {
        date = default;
        var text = NormalizeOcrDateText(value);
        var match = Regex.Match(text, @"(?<day>um|dois|tres|quatro|cinco|seis|sete|oito|nove|dez|onze|doze|treze|quatorze|catorze|quinze|dezesseis|dezasseis|dezessete|dezoito|dezenove|vinte(?: e (?:um|dois|tres|quatro|cinco|seis|sete|oito|nove))?|trinta(?: e um)?|\d{1,2})\s+de\s+(?<month>janeiro|fevereiro|marco|abril|maio|junho|julho|agosto|setembro|outubro|novembro|dezembro)\s+de\s+(?<year>dois mil(?: e .+)?|\d{4})", RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        var day = WordNumber(match.Groups["day"].Value);
        var month = MonthNumber(match.Groups["month"].Value);
        var year = ParseYearWords(match.Groups["year"].Value);
        return day > 0 && month > 0 && year > 0 && DateTime.TryParse($"{day:00}/{month:00}/{year:0000}", PtBr, DateTimeStyles.None, out date);
    }

    private static string NormalizeOcrDateText(string value)
    {
        var text = Normalize(value).ToLowerInvariant();
        text = Regex.Replace(text, @"\bnov[oa]\b(?=\s+d[eo]\s+agosto)", "nove");
        text = Regex.Replace(text, @"\bv[i1l]n(?:t|c)[eo]\b", "vinte");
        text = Regex.Replace(text, @"\btrin[ct]?a\b", "trinta");
        text = Regex.Replace(text, @"\bd[o0][i1l][s5]\b", "dois");
        text = Regex.Replace(text, @"\bm[i1l][l1]\b", "mil");
        text = Regex.Replace(text, @"\bs[e0o][i1l][s5]\b", "seis");
        text = Regex.Replace(text, @"\b(?:do|da)\b", "de");
        text = Regex.Replace(text, @"\bo\b", "e");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static int MonthNumber(string value)
    {
        var key = Normalize(value).ToLowerInvariant();
        return key switch
        {
            "janeiro" => 1, "fevereiro" => 2, "marco" => 3, "março" => 3, "abril" => 4, "maio" => 5, "junho" => 6,
            "julho" => 7, "agosto" => 8, "setembro" => 9, "outubro" => 10, "novembro" => 11, "dezembro" => 12, _ => 0
        };
    }

    private static int WordNumber(string value)
    {
        var key = Normalize(value).ToLowerInvariant();
        key = Regex.Replace(key, @"\bv[i1l]n(?:t|c)e\b", "vinte");
        key = Regex.Replace(key, @"\bd[o0]i[s5]\b", "dois");
        if (int.TryParse(key, out var numeric)) return numeric;
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["um"] = 1, ["dois"] = 2, ["tres"] = 3, ["quatro"] = 4, ["cinco"] = 5, ["seis"] = 6, ["sete"] = 7, ["oito"] = 8, ["nove"] = 9,
            ["dez"] = 10, ["onze"] = 11, ["doze"] = 12, ["treze"] = 13, ["quatorze"] = 14, ["catorze"] = 14, ["quinze"] = 15,
            ["dezesseis"] = 16, ["dezasseis"] = 16, ["dezessete"] = 17, ["dezoito"] = 18, ["dezenove"] = 19, ["vinte"] = 20,
            ["vinte e um"] = 21, ["vinte e dois"] = 22, ["vinte e tres"] = 23, ["vinte e quatro"] = 24, ["vinte e cinco"] = 25, ["vinte e seis"] = 26,
            ["vinte e sete"] = 27, ["vinte e oito"] = 28, ["vinte e nove"] = 29, ["trinta"] = 30, ["trinta e um"] = 31
        };
        return map.TryGetValue(key, out var valueNumber) ? valueNumber : 0;
    }

    private static int ParseYearWords(string value)
    {
        var key = Normalize(value).ToLowerInvariant();
        if (int.TryParse(key, out var numeric)) return numeric;
        if (!key.StartsWith("dois mil", StringComparison.Ordinal)) return 0;
        var rest = key.Replace("dois mil", string.Empty).Trim();
        if (rest.StartsWith("e ")) rest = rest[2..].Trim();
        return 2000 + (string.IsNullOrWhiteSpace(rest) ? 0 : WordNumber(rest));
    }

    private static string FindContext(string? text, string pattern, int length)
    {
        var sourceText = text ?? string.Empty;
        var match = Regex.Match(sourceText, pattern, RegexOptions.IgnoreCase);
        if (!match.Success) return string.Empty;
        return sourceText.Substring(match.Index, Math.Min(length, sourceText.Length - match.Index));
    }

    private static string FormatMatrix(string value)
    {
        var digits = Digits(value);
        return digits.Length == 32
            ? $"{digits[..6]} {digits.Substring(6, 2)} {digits.Substring(8, 2)} {digits.Substring(10, 4)} {digits[14]} {digits.Substring(15, 5)} {digits.Substring(20, 3)} {digits.Substring(23, 7)} {digits.Substring(30, 2)}"
            : digits;
    }

    private static string FormatCpf(string value)
    {
        var digits = Digits(value);
        return digits.Length == 11 ? $"{digits[..3]}.{digits.Substring(3, 3)}.{digits.Substring(6, 3)}-{digits[9..]}" : value.Trim();
    }

    private static bool IsValidCpf(string value)
    {
        var digits = Digits(value);
        if (digits.Length != 11 || digits.Distinct().Count() == 1) return false;
        int Calculate(int length)
        {
            var sum = 0;
            for (var i = 0; i < length; i++) sum += (digits[i] - '0') * (length + 1 - i);
            var remainder = sum % 11;
            return remainder < 2 ? 0 : 11 - remainder;
        }
        return digits[9] - '0' == Calculate(9) && digits[10] - '0' == Calculate(10);
    }

    private static int Score(IReadOnlyDictionary<string, string> keys)
    {
        var score = 0;
        foreach (var key in new[] { "NOME_FILHO", "CPF_FILHO", "DATA_NASCIMENTO", "MATRICULA_CERTIDAO", "DATA_CERTIDAO", "FILIACAO_1", "FILIACAO_2", "CARTORIO" })
            if (!string.IsNullOrWhiteSpace(keys.GetValueOrDefault(key))) score += key is "NOME_FILHO" or "DATA_NASCIMENTO" ? 20 : 10;
        return Math.Min(100, score);
    }

    private static void Set(IDictionary<string, string> target, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) target[key] = Regex.Replace(value.Trim(), @"\s+", " ");
    }

    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
    private static string TitleCase(string value) => PtBr.TextInfo.ToTitleCase(Regex.Replace(value.Trim(), @"\s+", " ").ToLower(PtBr));
    private static string Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Select(char.ToUpperInvariant).ToArray());
    }

    [GeneratedRegex(@"\b\d{3}[.\s]?\d{3}[.\s]?\d{3}[-\s]?\d{2}\b")]
    private static partial Regex CpfRegex();
    [GeneratedRegex(@"(?:\d[.\s-]*){25,40}")]
    private static partial Regex LongDigitsRegex();
    [GeneratedRegex(@"\b\d{1,2}[./-]\d{1,2}[./-](?:19|20)\d{2}\b")]
    private static partial Regex DateRegex();
    [GeneratedRegex(@"\b\d{1,2}\s+de\s+[A-Za-zçãéíóúâêô]+\s+de\s+(?:19|20)\d{2}\b", RegexOptions.IgnoreCase)]
    private static partial Regex LongDateRegex();
    [GeneratedRegex(@"\b(?:um|dois|tres|três|quatro|cinco|seis|sete|oito|nove|dez|onze|doze|treze|quatorze|catorze|quinze|dezesseis|dezasseis|dezessete|dezoito|dezenove|vinte(?:\s+e\s+(?:um|dois|tres|três|quatro|cinco|seis|sete|oito|nove))?|trinta(?:\s+e\s+um)?)\s+de\s+[A-Za-zçãéíóúâêô]+\s+de\s+dois\s+mil(?:\s+e\s+[A-Za-zçãéíóúâêô ]+)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex WordDateRegex();
    [GeneratedRegex(@"\b([A-Za-zÁÀÂÃÉÊÍÓÔÕÚÜÇáàâãéêíóôõúüç ]{3,45})\s*[-–]\s*(AC|AL|AP|AM|BA|CE|DF|ES|GO|MA|MG|MS|MT|PA|PB|PE|PI|PR|RJ|RN|RO|RR|RS|SC|SE|SP|TO)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LocationRegex();

    private readonly record struct CertificateRegion(string Marker, double X, double Y, double Width, double Height);
}
