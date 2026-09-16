using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace SIGFUR.Wpf.Services;

public static class ConferenceNameHighlight
{
    public sealed record Box(double X, double Y, double Width, double Height);
    public static IReadOnlyList<Box> Find(string path, int pageNumber, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return [];
        using var pdf = PdfDocument.Open(path);
        var page = pdf.GetPage(pageNumber);
        var lines = new List<List<Word>>();
        foreach (var word in page.GetWords().OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
        {
            var center = (word.BoundingBox.Top + word.BoundingBox.Bottom) / 2;
            var line = lines.FirstOrDefault(l => Math.Abs(center - (l[0].BoundingBox.Top + l[0].BoundingBox.Bottom) / 2) < 4);
            if (line is null) lines.Add([word]); else line.Add(word);
        }
        var words = lines.SelectMany(l => l.OrderBy(w => w.BoundingBox.Left)).ToList();
        var text = new StringBuilder(); var spans = new List<(int Start, int End, Word Word)>();
        foreach (var word in words) { var start = text.Length; text.Append(Clean(word.Text)); spans.Add((start, text.Length, word)); }
        var target = Clean(name);
        if (target.Length < 8) return [];
        var found = text.ToString().IndexOf(target, StringComparison.Ordinal);
        if (found < 0) return [];
        return spans.Where(s => s.End > found && s.Start < found + target.Length).Select(s =>
        {
            var b = s.Word.BoundingBox;
            return new Box(b.Left / page.Width, (page.Height - b.Top) / page.Height, b.Width / page.Width, b.Height / page.Height);
        }).ToList();
    }
    private static string Clean(string text) => Regex.Replace(new string(text.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToUpperInvariant(), "[^A-Z]", "");
}
