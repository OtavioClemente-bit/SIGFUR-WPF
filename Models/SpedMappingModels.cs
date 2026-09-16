namespace SIGFUR.Wpf.Models;

public sealed class SpedMappingSettings
{
    public string Login { get; set; } = string.Empty;
    public bool SavePassword { get; set; }
    public string ProtectedPassword { get; set; } = string.Empty;
    public string SenderSearch { get; set; } = "PHABLLO";
    public string ExternalRecipientSearch { get; set; } = "E1";
    public string ClassificationSearch { get; set; } = "085.612 - GRATIFICAÇÕES";
    public string DocumentPurpose { get; set; } = "Geral";
    public string Subject { get; set; } = "Gratificação de Representação (2%)";
    public List<string> SenderHistory { get; set; } = ["PHABLLO"];
    public List<string> ExternalRecipientHistory { get; set; } = ["E1"];
    public List<string> ClassificationHistory { get; set; } = ["085.612 - GRATIFICAÇÕES"];
    public List<string> DocumentPurposeHistory { get; set; } = ["Geral"];
}

public sealed class SpedDiexDraft
{
    public string Subject { get; set; } = string.Empty;
    public string BodyHtml { get; init; } = string.Empty;
    public string DispatchSubject { get; init; } = string.Empty;
    public string DispatchBodyHtml { get; init; } = string.Empty;
    public List<string> AttachmentPaths { get; init; } = [];
    public bool FillDocumentPurpose { get; init; } = true;
}

public sealed class SpedProcessAutomationSettings
{
    public string Login { get; set; } = string.Empty;
    public bool SaveLoginPassword { get; set; }
    public string ProtectedLoginPassword { get; set; } = string.Empty;
    public string RecipientSearch { get; set; } = string.Empty;
    public string ProcessInterested { get; set; } = string.Empty;
    public string DispatchSignerSearch { get; set; } = "Ordenador de Despesas";
    public string ProcessForwardRecipientSearch { get; set; } = "Comandante";
    public bool SaveSignaturePassword { get; set; }
    public string ProtectedSignaturePassword { get; set; } = string.Empty;
}

public sealed class SpedProcessAutomationResult
{
    public bool Success { get; init; }
    public bool Signed { get; init; }
    public bool NeedsManualSignature { get; init; }
    public bool ProcessCreated { get; init; }
    public bool Autuated { get; init; }
    public bool Forwarded { get; init; }
    public bool NeedsManualAutuation { get; init; }
    public bool NeedsManualDispatch { get; init; }
    public string DocumentNumber { get; init; } = string.Empty;
    public string DocumentUrl { get; init; } = string.Empty;
    public string DocumentPdfPath { get; init; } = string.Empty;
    public string ProcessPdfPath { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
}

public enum SpedProcessAutomationMode
{
    CreateDiex,
    CreateProcess
}

public sealed class SpedBrowserEvent
{
    public int Sequence { get; set; }
    public string RecordedAt { get; set; } = string.Empty;
    public string BrowserTime { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string InputType { get; set; } = string.Empty;
    public string AriaLabel { get; set; } = string.Empty;
    public string Placeholder { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string ValueSummary { get; set; } = string.Empty;
    public string CssSelector { get; set; } = string.Empty;
    public string XPath { get; set; } = string.Empty;
    public string OuterHtml { get; set; } = string.Empty;
    public string Screenshot { get; set; } = string.Empty;
}

public sealed class SpedMappingStatus
{
    public bool BrowserOpen { get; init; }
    public bool IsRecording { get; init; }
    public int EventCount { get; init; }
    public string SessionDirectory { get; init; } = string.Empty;
}
