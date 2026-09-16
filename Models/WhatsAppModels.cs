namespace SIGFUR.Wpf.Models;

public sealed class WhatsAppShareSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string LastRecipient { get; set; } = string.Empty;
    public string MessageTemplate { get; set; } = "Olá! Seguem anexos os contracheques referentes a {REFERENCIA}.";
    public bool SendAutomatically { get; set; }
}

public sealed class WhatsAppShareRequest
{
    public string Recipient { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool SendAutomatically { get; set; }
    public List<string> Files { get; set; } = [];
}

public sealed record WhatsAppShareProgress(string Stage, string Message, int Percent);
public sealed record WhatsAppShareResult(bool Sent, string Message);
