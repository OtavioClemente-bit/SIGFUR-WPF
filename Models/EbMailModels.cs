namespace SIGFUR.Wpf.Models;

public sealed class EbMailSettings
{
    public int SchemaVersion { get; set; } = 2;
    public string LastPresetId { get; set; } = string.Empty;
    public List<EbMailRecipientPreset> Presets { get; set; } = [];
    public bool SendAutomatically { get; set; }
    public bool ReuseProtectedBrowserSession { get; set; } = true;
}

public sealed class EbMailRecipientPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Cc { get; set; } = string.Empty;
    public string Bcc { get; set; } = string.Empty;
    public string SubjectTemplate { get; set; } = "Encaminhamento de {TIPO} — {REFERENCIAS}";
    public string BodyTemplate { get; set; } = "Senhor(a),\n\nEncaminho, em anexo, {QUANTIDADE} documento(s):\n{LISTA_DOCUMENTOS}\n\nAtenciosamente,";

    public EbMailRecipientPreset Clone() => new()
    {
        Id = Id,
        Name = Name,
        To = To,
        Cc = Cc,
        Bcc = Bcc,
        SubjectTemplate = SubjectTemplate,
        BodyTemplate = BodyTemplate
    };
}

public sealed class EbMailDocument
{
    public string Kind { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string OriginalFilePath { get; set; } = string.Empty;
    public string SignedFilePath { get; set; } = string.Empty;
    public bool IsSigned { get; set; }
    public EbMailAttachmentMode AttachmentMode { get; set; }

    public string ResolvedOriginalPath => FirstExisting(OriginalFilePath, IsSigned ? string.Empty : FilePath);
    public string ResolvedSignedPath => FirstExisting(SignedFilePath, IsSigned ? FilePath : string.Empty);
    public bool HasOriginal => ResolvedOriginalPath.Length > 0;
    public bool HasSigned => ResolvedSignedPath.Length > 0;
    public bool HasAnyAttachment => HasOriginal || HasSigned;
    public string FileName => Path.GetFileName(SelectedFilePaths.FirstOrDefault() ?? FilePath);
    public string SignatureText => AttachmentMode switch
    {
        EbMailAttachmentMode.Both => "Normal + assinado",
        EbMailAttachmentMode.Signed => "Assinado",
        _ => "Normal"
    };
    public string AvailableVersionsText => HasOriginal && HasSigned ? "Normal e assinado" : HasSigned ? "Somente assinado" : "Somente normal";
    public IReadOnlyList<EbMailAttachmentOption> AttachmentOptions
    {
        get
        {
            var options = new List<EbMailAttachmentOption>();
            if (HasOriginal) options.Add(new(EbMailAttachmentMode.Original, "Normal"));
            if (HasSigned) options.Add(new(EbMailAttachmentMode.Signed, "Assinado"));
            if (HasOriginal && HasSigned) options.Add(new(EbMailAttachmentMode.Both, "Normal + assinado"));
            return options;
        }
    }
    public IReadOnlyList<string> SelectedFilePaths
    {
        get
        {
            var mode = AttachmentMode;
            if (mode == EbMailAttachmentMode.Signed && !HasSigned) mode = EbMailAttachmentMode.Original;
            if (mode == EbMailAttachmentMode.Original && !HasOriginal) mode = EbMailAttachmentMode.Signed;
            return mode switch
            {
                EbMailAttachmentMode.Both => new[] { ResolvedOriginalPath, ResolvedSignedPath }.Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                EbMailAttachmentMode.Signed => ResolvedSignedPath.Length > 0 ? [ResolvedSignedPath] : [],
                _ => ResolvedOriginalPath.Length > 0 ? [ResolvedOriginalPath] : []
            };
        }
    }
    public string DisplayReference => string.Join(" — ", new[] { Reference, Date }.Where(x => !string.IsNullOrWhiteSpace(x) && x != "—"));
    public string SelectedFilesDescription => string.Join(" + ", SelectedFilePaths.Select(Path.GetFileName));

    private static string FirstExisting(params string[] candidates)
        => candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path)) ?? string.Empty;
}

public enum EbMailAttachmentMode
{
    Original,
    Signed,
    Both
}

public sealed record EbMailAttachmentOption(EbMailAttachmentMode Mode, string Label);

public sealed class EbMailComposeRequest
{
    public string To { get; set; } = string.Empty;
    public string Cc { get; set; } = string.Empty;
    public string Bcc { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool SendAutomatically { get; set; }
    public List<EbMailDocument> Documents { get; set; } = [];
}

public sealed record EbMailProgress(string Stage, string Message, int Percent = 0);
public sealed record EbMailAutomationResult(bool Sent, string Message);
