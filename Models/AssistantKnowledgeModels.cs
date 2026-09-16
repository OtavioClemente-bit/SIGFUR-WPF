namespace SIGFUR.Wpf.Models;

public sealed class AssistantKnowledgeContext
{
    public string ContextText { get; set; } = string.Empty;
    public string CorpusFingerprint { get; set; } = string.Empty;
    public string ManualVersion { get; set; } = string.Empty;
    public bool CacheHit { get; set; }
    public List<AssistantKnowledgeSource> Sources { get; set; } = [];
    public List<string> Gaps { get; set; } = [];
}

public sealed class AssistantKnowledgeSource
{
    public string CitationId { get; set; } = string.Empty;
    public long DocumentId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int Page { get; set; }
    public string Excerpt { get; set; } = string.Empty;
    public string SourceFingerprint { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool IsManual { get; set; }
    public DateTime IndexedAt { get; set; }
    public string Reference => $"[{CitationId}] {Title}, arquivo {FileName}, p. {Page}";
}

/// <summary>Metadata only; never contains document text or personal operational data.</summary>
public sealed class AssistantKnowledgeDocument
{
    public long DocumentId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string SourceFingerprint { get; set; } = string.Empty;
    public long CurrentSize { get; set; }
    public long CurrentModifiedTicks { get; set; }
    public DateTime IndexedAt { get; set; }
    public bool IsCurrent { get; set; }
    public string Problem { get; set; } = string.Empty;
    public bool IsManual => FileName.Contains("sippes", StringComparison.OrdinalIgnoreCase)
                            || Title.Contains("sippes", StringComparison.OrdinalIgnoreCase);
}
