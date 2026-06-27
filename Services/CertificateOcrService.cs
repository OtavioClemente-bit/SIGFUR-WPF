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
            var scale = Math.Min(2.4d, max / Math.Max(page.Size.Width, page.Size.Height));
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, page.Size.Width * scale),
                DestinationHeight = (uint)Math.Max(1, page.Size.Height * scale)
            };
            await page.RenderToStreamAsync(stream, options);
            stream.Seek(0);
            var text = await RecognizeStreamAsync(stream, engine);
            if (!string.IsNullOrWhiteSpace(text)) builder.AppendLine(text).AppendLine();
        }
        return builder.ToString().Trim();
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
        var scale = Math.Min(1d, maxDimension / (double)Math.Max(width, height));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, width * scale),
            ScaledHeight = (uint)Math.Max(1, height * scale),
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        var result = await engine.RecognizeAsync(bitmap);
        return result.Text?.Trim() ?? string.Empty;
    }

    private static CertificateOcrResult Parse(string? text, MilitaryRecord military)
    {
        var sourceText = text ?? string.Empty;
        var result = new CertificateOcrResult();
        var lines = CleanLines(sourceText);
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var name = FindChildName(sourceText, lines);
        Set(keys, "NOME_FILHO", name.ToUpper(PtBr));

        var cpfs = CpfRegex().Matches(sourceText).Select(m => FormatCpf(m.Value)).Where(IsValidCpf).Distinct().ToList();
        var militaryCpf = Digits(military.Cpf);
        var childCpf = cpfs.FirstOrDefault(x => Digits(x) != militaryCpf) ?? cpfs.FirstOrDefault() ?? string.Empty;
        Set(keys, "CPF_FILHO", childCpf);

        var matrixContext = FindContext(sourceText, "MATR[IÍ]CULA", 220);
        var matrixMatch = LongDigitsRegex().Match(matrixContext);
        if (!matrixMatch.Success) matrixMatch = LongDigitsRegex().Match(sourceText);
        if (matrixMatch.Success) Set(keys, "MATRICULA_CERTIDAO", FormatMatrix(matrixMatch.Value));

        var birth = FindDateAfterLabel(sourceText, ["DATA DO NASCIMENTO", "DATA DE NASCIMENTO", "NASCIMENTO"]);
        var registration = FindDateAfterLabel(sourceText, ["DATA DE REGISTRO", "DATA DA CERTIDAO", "DATA DA CERTIDÃO", "CONTEÚDO DA CERTIDÃO", "CONTEUDO DA CERTIDAO"]);
        var allDates = ExtractDates(sourceText).ToList();
        if (string.IsNullOrWhiteSpace(birth)) birth = allDates.FirstOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(registration)) registration = allDates.Skip(1).FirstOrDefault() ?? string.Empty;
        Set(keys, "DATA_NASCIMENTO", birth); Set(keys, "DATA_CERTIDAO", registration);

        var parents = FindParents(sourceText, lines, name);
        Set(keys, "FILIACAO_1", parents.ElementAtOrDefault(0)?.ToUpper(PtBr));
        Set(keys, "FILIACAO_2", parents.ElementAtOrDefault(1)?.ToUpper(PtBr));

        var sex = sourceText.Contains("FEMININO", StringComparison.OrdinalIgnoreCase) ? "feminino"
            : sourceText.Contains("MASCULINO", StringComparison.OrdinalIgnoreCase) ? "masculino" : string.Empty;
        Set(keys, "SEXO_FILHO", sex);
        Set(keys, "TIPO_FILHO", sex == "feminino" ? "filha" : sex == "masculino" ? "filho" : "filho(a)");
        Set(keys, "SEU_SUA_FILHO", sex == "feminino" ? "sua filha" : sex == "masculino" ? "seu filho" : "seu filho(a)");

        var registry = FindRegistryOffice(sourceText, lines);
        Set(keys, "CARTORIO", registry);
        var location = LocationRegex().Match(text ?? string.Empty);
        if (location.Success) Set(keys, "LOCAL_CERTIDAO", $"{TitleCase(location.Groups[1].Value)} - {location.Groups[2].Value.ToUpperInvariant()}");
        var address = FindRegistryAddress(sourceText, lines);
        Set(keys, "ENDERECO_CARTORIO", address);

        result.Keys = ExpandAliases(keys, military);
        result.ConfidenceScore = Score(result.Keys);
        if (string.IsNullOrWhiteSpace(name)) result.Warnings.Add("Nome da criança não identificado automaticamente.");
        if (string.IsNullOrWhiteSpace(birth)) result.Warnings.Add("Data de nascimento não identificada automaticamente.");
        if (string.IsNullOrWhiteSpace(childCpf)) result.Warnings.Add("CPF da criança não identificado automaticamente.");
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

    private static List<string> FindParents(string text, IReadOnlyList<string> lines, string childName)
    {
        var result = new List<string>();
        foreach (var source in lines.Concat([Regex.Replace(text ?? string.Empty, @"\s+", " ")]))
        {
            foreach (Match match in Regex.Matches(source, @"(?<left>[A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ]{2,}(?:\s+(?:DE|DA|DO|DOS|DAS|E|[A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ]{2,})){1,8})\s*[;:]\s*(?<right>[A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ]{2,}(?:\s+(?:DE|DA|DO|DOS|DAS|E|[A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ]{2,})){1,8})"))
            {
                Add(match.Groups["left"].Value);
                Add(match.Groups["right"].Value);
                if (result.Count >= 2) return result.Take(2).ToList();
            }
        }

        var fallback = FindValuesAfterLabel(lines, ["NOME DO(A) GENITOR(A)", "NOME DO GENITOR", "NOME DA GENITORA", "FILIAÇÃO", "FILIACAO"], 2, LooksLikeName);
        foreach (var item in fallback) Add(item);
        if (result.Count < 2)
        {
            foreach (var line in lines.Where(x => LooksLikeName(x) && !x.Equals(childName, StringComparison.OrdinalIgnoreCase) && !LooksLikeAdministrativeLine(x)))
            {
                Add(line);
                if (result.Count >= 2) break;
            }
        }
        return result.Take(2).ToList();

        void Add(string value)
        {
            value = CleanNameCandidate(value).ToUpper(PtBr);
            if (string.IsNullOrWhiteSpace(value) || value.Equals(CleanNameCandidate(childName).ToUpper(PtBr), StringComparison.OrdinalIgnoreCase)) return;
            if (!LooksLikeName(value) || LooksLikeAdministrativeLine(value)) return;
            if (!result.Contains(value, StringComparer.OrdinalIgnoreCase)) result.Add(value);
        }
    }

    private static string FindRegistryOffice(string text, IReadOnlyList<string> lines)
    {
        var preferred = lines.FirstOrDefault(x => Normalize(x).Contains("REGISTRO CIVIL DAS PESSOAS NATURAIS DE") && !Normalize(x).Contains("REPUBLICA"));
        if (!string.IsNullOrWhiteSpace(preferred)) return preferred;
        preferred = lines.FirstOrDefault(x => Normalize(x).Contains("OFICIAL DE REGISTRO CIVIL") && !Normalize(x).Contains("REPUBLICA"));
        if (!string.IsNullOrWhiteSpace(preferred)) return preferred;
        preferred = Regex.Match(text ?? string.Empty, @"REGISTRO\s+CIVIL\s+DAS\s+PESSOAS\s+NATURAIS\s+DE\s+[A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ ]{3,50}", RegexOptions.IgnoreCase).Value;
        return Regex.Replace(preferred, @"\s+", " ").Trim();
    }

    private static string FindRegistryAddress(string text, IReadOnlyList<string> lines)
    {
        var candidates = lines.Where(x => Regex.IsMatch(x, @"\b(RUA|R\.|AVENIDA|AV\.|PRAÇA|PRACA)\b", RegexOptions.IgnoreCase)).ToList();
        var registry = candidates.FirstOrDefault(x => Normalize(x).Contains("RUBEM") || Normalize(x).Contains("BOM JARDIM") || Normalize(x).Contains("CARTORIO") || Normalize(x).Contains("REGISTRO CIVIL"));
        if (!string.IsNullOrWhiteSpace(registry)) return registry;
        var match = Regex.Match(text ?? string.Empty, @"(?:Rua|R\.)\s+Rubem\s+Costa\s+Lima[^\r\n]{0,120}", RegexOptions.IgnoreCase);
        if (match.Success) return Regex.Replace(match.Value, @"\s+", " ").Trim();
        return candidates.FirstOrDefault() ?? string.Empty;
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
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || words.Any(x => x.Length == 1 && !"DE DA DO E".Contains(x, StringComparison.OrdinalIgnoreCase))) return false;
        return Regex.IsMatch(text, @"^[A-Za-zÁÀÂÃÉÊÍÓÔÕÚÜÇáàâãéêíóôõúüç' -]+$");
    }

    private static bool LooksLikeAdministrativeLine(string value)
    {
        var normalized = Normalize(value);
        return new[] { "CERTIDAO", "REGISTRO", "CARTORIO", "MUNICIPIO", "NASCIMENTO", "MATRICULA", "GENITOR", "FILIACAO", "OFICIAL", "ESCREVENTE", "REPUBLICA", "CPF" }.Any(normalized.Contains);
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
        var text = Normalize(value).ToLowerInvariant();
        var match = Regex.Match(text, @"(?<day>um|dois|tres|três|quatro|cinco|seis|sete|oito|nove|dez|onze|doze|treze|quatorze|catorze|quinze|dezesseis|dezasseis|dezessete|dezoito|dezenove|vinte(?: e um)?|vinte e dois|vinte e tres|vinte e três|vinte e quatro|vinte e cinco|vinte e seis|vinte e sete|vinte e oito|vinte e nove|trinta|trinta e um|\d{1,2})\s+de\s+(?<month>janeiro|fevereiro|marco|março|abril|maio|junho|julho|agosto|setembro|outubro|novembro|dezembro)\s+de\s+(?<year>dois mil(?: e .+)?|\d{4})", RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        var day = WordNumber(match.Groups["day"].Value);
        var month = MonthNumber(match.Groups["month"].Value);
        var year = ParseYearWords(match.Groups["year"].Value);
        return day > 0 && month > 0 && year > 0 && DateTime.TryParse($"{day:00}/{month:00}/{year:0000}", PtBr, DateTimeStyles.None, out date);
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
        var key = Normalize(value).ToLowerInvariant().Replace("três", "tres");
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
            ? $"{digits[..6]} {digits.Substring(6, 2)} {digits.Substring(8, 2)} {digits[10]} {digits.Substring(11, 5)} {digits.Substring(16, 2)} {digits.Substring(18, 7)} {digits.Substring(25, 3)} {digits.Substring(28, 4)}"
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
}
