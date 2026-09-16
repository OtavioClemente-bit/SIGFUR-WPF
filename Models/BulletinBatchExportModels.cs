using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace SIGFUR.Wpf.Models;

public sealed class BulletinBatchExportItem
{
    public string Id { get; init; } = string.Empty;
    public string Prefix { get; init; } = "BI";
    public int Number { get; init; }
    public int Year { get; init; }
    public string DateText { get; init; } = string.Empty;
    public string OriginalName { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public string SignedPath { get; init; } = string.Empty;
    public string ExtraReference { get; init; } = string.Empty;

    public string DisplayReference => $"{Prefix} {Number}/{Year}{(string.IsNullOrWhiteSpace(ExtraReference) ? string.Empty : $" · {ExtraReference}")}";
    public bool HasSignedFile => !string.IsNullOrWhiteSpace(SignedPath) && File.Exists(SignedPath);

    public string ResolveSourcePath(bool preferSigned)
        => preferSigned && HasSignedFile ? SignedPath : SourcePath;

    public static BulletinBatchExportItem? Create(
        string id,
        string prefix,
        string bulletin,
        string dateReference,
        string sourcePath,
        string originalName,
        string? signedPath = null,
        string? extraReference = null)
    {
        var number = ResolveNumber(bulletin, originalName, sourcePath);
        var year = ResolveYear(bulletin, dateReference, originalName, sourcePath);
        if (number <= 0 || year <= 0 || string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return null;

        return new BulletinBatchExportItem
        {
            Id = id,
            Prefix = prefix,
            Number = number,
            Year = year,
            DateText = (dateReference ?? string.Empty).Trim(),
            OriginalName = string.IsNullOrWhiteSpace(originalName) ? Path.GetFileName(sourcePath) : originalName,
            SourcePath = sourcePath,
            SignedPath = signedPath ?? string.Empty,
            ExtraReference = extraReference ?? string.Empty
        };
    }

    private static int ResolveNumber(params string?[] values)
    {
        foreach (var value in values)
        {
            var text = value ?? string.Empty;
            var contextual = Regex.Match(text,
                @"(?:BOLETIM(?:\s+INTERNO)?|\bBI\b|\bBOL\b|ADITAMENTO|\bADT\b|\bNR\b|N[º°O])[^0-9]{0,18}(?<number>[0-9]{1,4})(?![0-9])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (contextual.Success && int.TryParse(contextual.Groups["number"].Value, out var contextualNumber) && contextualNumber is > 0 and < 1900)
                return contextualNumber;

            var leading = Regex.Match(text, @"^\s*(?<number>[0-9]{1,4})(?:\s*[/\-]\s*(?:19|20)[0-9]{2})?\b");
            if (leading.Success && int.TryParse(leading.Groups["number"].Value, out var leadingNumber) && leadingNumber is > 0 and < 1900)
                return leadingNumber;
        }

        return 0;
    }

    private static int ResolveYear(params string?[] values)
    {
        foreach (var value in values)
        {
            var match = Regex.Match(value ?? string.Empty, @"(?<![0-9])(?<year>(?:19|20)[0-9]{2})(?![0-9])");
            if (match.Success && int.TryParse(match.Groups["year"].Value, out var year)) return year;
        }
        return 0;
    }
}

public sealed class BulletinBatchExportRow : INotifyPropertyChanged
{
    private bool _isIncluded = true;

    public required BulletinBatchExportItem Item { get; init; }
    public int Sequence { get; set; }
    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (_isIncluded == value) return;
            _isIncluded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIncluded)));
            InclusionChanged?.Invoke();
        }
    }

    public string Reference => Item.DisplayReference;
    public string Date => Item.DateText;
    public string FileName => Item.OriginalName;
    public string Version => Item.HasSignedFile ? "Assinado disponível" : "PDF comum";
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? InclusionChanged;
}
