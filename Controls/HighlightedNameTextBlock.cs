using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace SIGFUR.Wpf.Controls;

public sealed class NameTextSegment
{
    public string Text { get; init; } = string.Empty;
    public bool IsBold { get; init; }
}

public static class NameHighlightHelper
{
    public static IReadOnlyList<NameTextSegment> BuildSegments(string? fullName, string? warName)
    {
        var full = (fullName ?? string.Empty).Trim();
        var war = (warName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(full))
            return string.IsNullOrWhiteSpace(war)
                ? Array.Empty<NameTextSegment>()
                : new[] { new NameTextSegment { Text = war, IsBold = true } };
        if (string.IsNullOrWhiteSpace(war))
            return new[] { new NameTextSegment { Text = full } };

        var normalizedFull = NormalizeWithMap(full, out var indexMap);
        var ranges = new List<(int Start, int End)>();
        var words = WordRanges(full);

        foreach (var warPart in SplitWarNames(war))
            AddWarNameRanges(full, normalizedFull, indexMap, words, warPart, ranges);

        if (ranges.Count == 0)
            return new[]
            {
                new NameTextSegment { Text = full },
                new NameTextSegment { Text = " — " },
                new NameTextSegment { Text = war, IsBold = true }
            };

        var merged = Merge(ranges);
        var segments = new List<NameTextSegment>();
        var cursor = 0;
        foreach (var range in merged)
        {
            if (cursor < range.Start)
                segments.Add(new NameTextSegment { Text = full[cursor..range.Start] });
            segments.Add(new NameTextSegment { Text = full[range.Start..range.End], IsBold = true });
            cursor = range.End;
        }
        if (cursor < full.Length) segments.Add(new NameTextSegment { Text = full[cursor..] });
        return segments;
    }

    public static string PlainDisplay(string? fullName, string? warName)
        => string.Concat(BuildSegments(fullName, warName).Select(x => x.Text));

    private static IReadOnlyList<string> SplitWarNames(string value)
    {
        var parts = Regex.Split(value ?? string.Empty, @"[;,|/]+")
            .Select(x => Regex.Replace(x, @"\s+", " ").Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(value)) parts.Add(value.Trim());
        return parts;
    }

    private static void AddWarNameRanges(
        string full,
        string normalizedFull,
        IReadOnlyList<int> indexMap,
        IReadOnlyList<(int Start, int End, string Text)> words,
        string war,
        List<(int Start, int End)> ranges)
    {
        var normalizedWar = Normalize(war);
        if (string.IsNullOrWhiteSpace(normalizedWar)) return;

        // Primeiro tenta o Nome de Guerra completo exatamente dentro do nome.
        var exact = normalizedFull.IndexOf(normalizedWar, StringComparison.OrdinalIgnoreCase);
        if (exact >= 0 && exact + normalizedWar.Length - 1 < indexMap.Count)
        {
            AddRange(ranges, indexMap[exact], indexMap[exact + normalizedWar.Length - 1] + 1);
            return;
        }

        // Quando o Nome de Guerra tem mais de uma parte, por exemplo "D TAVARES",
        // destaca o D de DOUGLAS e a palavra TAVARES, mesmo não sendo contíguos.
        var usedWords = new HashSet<int>();
        foreach (var token in normalizedWar.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length == 1)
            {
                var match = words
                    .Select((word, index) => (word, index))
                    .FirstOrDefault(x => !usedWords.Contains(x.index)
                                         && Normalize(x.word.Text).StartsWith(token, StringComparison.OrdinalIgnoreCase));
                if (match.word.Text is not null)
                {
                    AddRange(ranges, match.word.Start, Math.Min(match.word.Start + 1, match.word.End));
                    usedWords.Add(match.index);
                }
                continue;
            }

            var exactWord = words
                .Select((word, index) => (word, index))
                .FirstOrDefault(x => !usedWords.Contains(x.index)
                                     && string.Equals(Normalize(x.word.Text), token, StringComparison.OrdinalIgnoreCase));
            if (exactWord.word.Text is not null)
            {
                AddRange(ranges, exactWord.word.Start, exactWord.word.End);
                usedWords.Add(exactWord.index);
                continue;
            }

            var contains = normalizedFull.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (contains >= 0 && contains + token.Length - 1 < indexMap.Count)
                AddRange(ranges, indexMap[contains], indexMap[contains + token.Length - 1] + 1);
        }
    }

    private static void AddRange(List<(int Start, int End)> ranges, int start, int end)
    {
        if (start < 0 || end <= start) return;
        ranges.Add((start, end));
    }

    private static List<(int Start, int End)> Merge(IEnumerable<(int Start, int End)> source)
    {
        var ordered = source.OrderBy(x => x.Start).ThenBy(x => x.End).ToList();
        var result = new List<(int Start, int End)>();
        foreach (var item in ordered)
        {
            if (result.Count == 0 || item.Start > result[^1].End)
            {
                result.Add(item);
                continue;
            }
            var last = result[^1];
            result[^1] = (last.Start, Math.Max(last.End, item.End));
        }
        return result;
    }

    private static List<(int Start, int End, string Text)> WordRanges(string value)
    {
        var result = new List<(int Start, int End, string Text)>();
        var start = -1;
        for (var i = 0; i <= value.Length; i++)
        {
            var isWord = i < value.Length && !char.IsWhiteSpace(value[i]);
            if (isWord && start < 0) start = i;
            if ((!isWord || i == value.Length) && start >= 0)
            {
                result.Add((start, i, value[start..i]));
                start = -1;
            }
        }
        return result;
    }

    private static string NormalizeWithMap(string value, out List<int> map)
    {
        map = new List<int>();
        var sb = new StringBuilder();
        for (var index = 0; index < value.Length; index++)
        {
            var normalized = value[index].ToString().Normalize(NormalizationForm.FormD);
            foreach (var ch in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                sb.Append(char.ToUpperInvariant(ch));
                map.Add(index);
            }
        }
        return sb.ToString();
    }

    private static string Normalize(string value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(normalized
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }
}

public sealed class HighlightedNameTextBlock : TextBlock
{
    public static readonly DependencyProperty FullNameProperty = DependencyProperty.Register(
        nameof(FullName), typeof(string), typeof(HighlightedNameTextBlock),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender, OnNameChanged));

    public static readonly DependencyProperty WarNameProperty = DependencyProperty.Register(
        nameof(WarName), typeof(string), typeof(HighlightedNameTextBlock),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender, OnNameChanged));

    public string FullName
    {
        get => (string)GetValue(FullNameProperty);
        set => SetValue(FullNameProperty, value);
    }

    public string WarName
    {
        get => (string)GetValue(WarNameProperty);
        set => SetValue(WarNameProperty, value);
    }

    public HighlightedNameTextBlock()
    {
        DataContextChanged += (_, _) => Refresh();
        Loaded += (_, _) => Refresh();
    }

    private static void OnNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((HighlightedNameTextBlock)d).Refresh();

    private void Refresh()
    {
        Inlines.Clear();
        foreach (var segment in NameHighlightHelper.BuildSegments(FullName, WarName))
        {
            var run = new Run(segment.Text);
            if (segment.IsBold) run.FontWeight = FontWeights.Bold;
            Inlines.Add(run);
        }
    }
}
