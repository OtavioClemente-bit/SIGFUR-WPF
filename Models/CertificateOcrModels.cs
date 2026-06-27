using System.Collections.ObjectModel;

namespace SIGFUR.Wpf.Models;

public sealed class CertificateOcrResult
{
    public string RawText { get; set; } = string.Empty;
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; set; } = [];
    public string Engine { get; set; } = "OCR do Windows";
    public int ConfidenceScore { get; set; }
    public bool HasUsefulData => Keys.Any(x => !x.Key.StartsWith('_') && !string.IsNullOrWhiteSpace(x.Value));
}

public sealed class CertificateKeyRow
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
