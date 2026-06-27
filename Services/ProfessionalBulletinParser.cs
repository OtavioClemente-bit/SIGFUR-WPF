using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public static class ProfessionalBulletinParser
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly Regex RankRegex = new(@"^\s*(?:[a-z]\)|[a-z]\.|\d+[.)-]|[-–—•])?\s*(?<rank>S\s*Ten|Sub\s*Ten|[1-3][º°]?\s*Sgt|[1-2][º°]?\s*Ten|Asp(?:\s*Of)?|Cap|Maj|Ten\s*Cel|Cel|Cb(?:\s*Ef\s*(?:Profl|Vrv))?|Sd(?:\s*(?:EV|Ef\s*(?:Profl|Vrv)))?|Ex[- ]?militar)\s+(?<name>[A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ][A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇa-záàâãéêíóôõúüç' -]{3,100})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EmbeddedRankRegex = new(@"(?:^|[\s,;:])(?<rank>S\s*Ten|Sub\s*Ten|[1-3][º°]?\s*Sgt|[1-2][º°]?\s*Ten|Asp(?:\s*Of)?|Cap|Maj|Ten\s*Cel|Cel|Cb(?:\s*Ef\s*(?:Profl|Vrv))?|Sd(?:\s*(?:EV|Ef\s*(?:Profl|Vrv)))?|Ex[- ]?militar)\s+(?<name>[A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇ][A-ZÁÀÂÃÉÊÍÓÔÕÚÜÇa-záàâãéêíóôõúüç' -]{3,100})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NoteNumberRegex = new(@"\bNota\s*(?:n[ºo°]?|nr\.?)?\s*[:#-]?\s*(?<n>\d{2,8})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ConsequenceRegex = new(@"\bEm\s+consequ[êe]ncia\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FurrielConsequenceRegex = new(@"\b(?:furriel|sgt\s+furriel)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly (string Canonical, Regex Pattern)[] KnownSubjects =
    [
        ("ADICIONAL DE HABILITAÇÃO", Rx(@"adicional\s+(?:de\s+)?habilita[çc][ãa]o")),
        ("GRATIFICAÇÃO DE REPRESENTAÇÃO", Rx(@"gratifica[çc][ãa]o\s+de\s+representa[çc][ãa]o|grat\.?\s*rep")),
        ("PENSÃO JUDICIAL", Rx(@"pens[ãa]o\s+judicial")),
        ("AUXÍLIO-TRANSPORTE", Rx(@"aux[íi]lio[- ]?transporte|auxilio\s+transporte")),
        ("AUXÍLIO-ALIMENTAÇÃO", Rx(@"aux[íi]lio[- ]?alimenta[çc][ãa]o|auxilio\s+alimentacao")),
        ("AJUSTE DE CONTAS", Rx(@"ajuste\s+de\s+contas")),
        ("DESPESA A ANULAR", Rx(@"despesa\s+a\s+anular|despesa\s+anular")),
        ("EXERCÍCIO ANTERIOR", Rx(@"exerc[íi]cio\s+anterior")),
        ("PLANO DE FÉRIAS", Rx(@"plano\s+de\s+f[ée]rias")),
        ("FÉRIAS", Rx(@"\bf[ée]rias\b")),
        ("PAGAMENTO PESSOAL", Rx(@"pagamento\s+pessoal|pagamento\s+de\s+pessoal")),
        ("FICHA FINANCEIRA", Rx(@"ficha\s+financeira")),
        ("CONTRACHEQUE", Rx(@"contracheque")),
        ("ALTERAÇÃO DE OFICIAIS", Rx(@"altera[çc][ãa]o\s+de\s+oficiais")),
        ("ALTERAÇÃO DE PRAÇAS", Rx(@"altera[çc][ãa]o\s+de\s+pra[çc]as")),
        ("ANIVERSARIANTES", Rx(@"aniversariantes?")),
        ("IMPLANTAÇÃO", Rx(@"\bimplanta[çc][ãa]o\b")),
        ("SAQUE", Rx(@"\bsaque\b|sacado")),
        ("ANULAÇÃO", Rx(@"\banula[çc][ãa]o\b")),
        ("LICENCIAMENTO", Rx(@"\blicenciamento\b")),
        ("TRANSFERÊNCIA", Rx(@"\btransfer[êe]ncia\b")),
        ("APRESENTAÇÃO", Rx(@"\bapresenta[çc][ãa]o\b|apresentou-se")),
        ("INSPEÇÃO DE SAÚDE", Rx(@"inspe[çc][ãa]o\s+de\s+sa[úu]de"))
    ];

    public static List<BulletinMentionItem> Parse(
        IReadOnlyList<string> pages,
        string bulletinType,
        string bulletinNumber,
        DateTime? bulletinDate,
        string sourceFilePath,
        IReadOnlyList<BulletinMilitaryIdentity> military)
    {
        var result = new List<BulletinMentionItem>();
        var documentLines = pages
            .SelectMany((page, pageIndex) => CleanPageLines(page).Select(text => new DocumentLine(text, pageIndex + 1)))
            .ToList();
        documentLines = MergeWrappedNoteHeadingLines(documentLines);
        if (documentLines.Count == 0) return result;

        var starts = Enumerable.Range(0, documentLines.Count)
            .Where(i => IsNoteHeading(documentLines[i].Text))
            .ToList();
        if (starts.Count == 0) starts.Add(0);

        for (var blockIndex = 0; blockIndex < starts.Count; blockIndex++)
        {
            var start = starts[blockIndex];
            var end = blockIndex + 1 < starts.Count ? starts[blockIndex + 1] : documentLines.Count;
            var blockLines = documentLines.Skip(start).Take(end - start).Where(x => !IsStructural(x.Text)).ToList();
            if (blockLines.Count == 0) continue;
            var rows = blockLines.Select(x => x.Text).ToList();
            var noteText = string.Join(Environment.NewLine, rows).Trim();
            if (noteText.Length < 18) continue;

            var heading = rows[0];
            if (rows.Count == 1 && IsSubjectHeading(heading)) continue;
            var cleanHeading = CleanHeading(heading);
            var splitHeading = SplitSubjectAndNote(cleanHeading);
            var subject = splitHeading.Subject;
            if (string.IsNullOrWhiteSpace(subject) || !IsProfessionalSubject(subject))
                subject = DetectSubject(heading);
            if (string.IsNullOrWhiteSpace(subject))
                subject = FindPreviousSubject(documentLines, start);
            if (string.IsNullOrWhiteSpace(subject))
                subject = rows.Take(Math.Min(5, rows.Count)).Select(DetectSubject).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(subject)) subject = $"Assunto não identificado — {bulletinType} nº {bulletinNumber}";

            var noteMatch = NoteNumberRegex.Match(noteText);
            var noteTitle = noteMatch.Success ? $"Nota nº {noteMatch.Groups["n"].Value}" : splitHeading.Note;
            if (Normalize(noteTitle) == Normalize(subject)) noteTitle = string.Empty;

            var consequenceStart = rows.FindIndex(x => ConsequenceRegex.IsMatch(x));
            var consequenceText = consequenceStart >= 0 ? string.Join(" ", rows.Skip(consequenceStart)) : string.Empty;
            var hasConsequence = consequenceStart >= 0;
            var furrielConsequence = FurrielConsequenceRegex.IsMatch(consequenceText) || FurrielConsequenceRegex.IsMatch(noteText);
            if (furrielConsequence && string.IsNullOrWhiteSpace(consequenceText))
                consequenceText = ExtractFurrielContext(rows);
            if (furrielConsequence) hasConsequence = true;
            var matches = ResolveMilitary(noteText, rows, military);
            if (matches.Count == 0) matches.Add(new ResolvedMilitary(ExtractUnlinkedName(rows), string.Empty, string.Empty, string.Empty, string.Empty, null, false));

            foreach (var person in matches)
            {
                var structured = ResolveSubjectAndNoteForPerson(rows, subject, noteTitle, person);
                var personLineIndex = FindPersonLineIndex(rows, person);
                var pageNumber = personLineIndex >= 0 && personLineIndex < blockLines.Count ? blockLines[personLineIndex].Page : blockLines[0].Page;
                var display = BuildSubjectNoteDisplay(structured.Subject, structured.NoteTitle);
                var inConsequence = hasConsequence && PersonOccurs(consequenceText, person);
                var idSeed = string.Join('|', bulletinType, bulletinNumber, pageNumber, start, Normalize(structured.Subject), Normalize(structured.NoteTitle), Normalize(person.Name), Digits(person.Cpf), Digits(person.PrecCp));
                result.Add(new BulletinMentionItem
                {
                    Id = ShortHash(idSeed), BulletinType = bulletinType, BulletinNumber = bulletinNumber,
                    BulletinDate = bulletinDate, Subject = structured.Subject, NoteTitle = structured.NoteTitle,
                    SubjectNoteDisplay = display, NoteText = noteText, NoteExcerpt = Excerpt(noteText),
                    MilitaryId = person.Id, MentionedMilitaryName = person.Name,
                    MentionedMilitaryWarName = person.WarName, MentionedMilitaryRank = person.Rank,
                    MentionedMilitaryCpf = person.Cpf, MentionedMilitaryPrecCp = person.PrecCp,
                    IsDatabaseMatch = person.DatabaseMatch, IsConsequenceMention = inConsequence,
                    HasConsequence = hasConsequence, IsFurrielConsequence = furrielConsequence,
                    ConsequenceText = consequenceText, SourceFilePath = sourceFilePath, PageNumber = pageNumber
                });
            }
        }
        return result.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
    }


    private static (string Subject, string NoteTitle) ResolveSubjectAndNoteForPerson(IReadOnlyList<string> rows, string fallbackSubject, string fallbackNoteTitle, ResolvedMilitary person)
    {
        var personIndex = FindPersonLineIndex(rows, person);
        if (personIndex < 0) personIndex = Math.Min(rows.Count - 1, Math.Max(0, rows.Count / 2));

        var subjectIndex = -1;
        for (var i = personIndex; i >= 0; i--)
        {
            var candidate = OneLine(rows[i]);
            if (IsStructural(candidate) || RankRegex.IsMatch(candidate) || EmbeddedRankRegex.IsMatch(candidate)) continue;
            if (IsLetteredNoteHeading(candidate) || IsSubjectHeading(candidate) || LooksLikeGenericSubject(CleanHeading(candidate)))
            {
                subjectIndex = i;
                break;
            }
        }

        var subject = subjectIndex >= 0 ? CleanHeading(rows[subjectIndex]) : fallbackSubject;
        var note = string.Empty;

        if (subjectIndex >= 0)
        {
            var split = SplitSubjectAndNote(subject);
            subject = split.Subject;
            note = split.Note;

            for (var i = subjectIndex + 1; i < personIndex && i <= subjectIndex + 5; i++)
            {
                var candidate = CleanHeading(rows[i]);
                if (!IsUsefulNoteTitle(candidate, subject)) continue;
                note = candidate;
            }
        }

        if (string.IsNullOrWhiteSpace(note) && IsUsefulNoteTitle(fallbackNoteTitle, subject))
            note = CleanHeading(fallbackNoteTitle);

        subject = string.IsNullOrWhiteSpace(subject) ? fallbackSubject : subject;
        if (string.IsNullOrWhiteSpace(subject)) subject = "Assunto não identificado";
        return (FormatSubject(subject), FormatNoteTitle(note));
    }

    private static int FindPersonLineIndex(IReadOnlyList<string> rows, ResolvedMilitary person)
    {
        var personName = Normalize(person.Name);
        var war = Normalize(person.WarName);
        var cpf = Digits(person.Cpf);
        var prec = Digits(person.PrecCp);

        for (var i = 0; i < rows.Count; i++)
        {
            var normalized = Normalize(rows[i]);
            var digits = Digits(rows[i]);
            if (cpf.Length >= 6 && digits.Contains(cpf, StringComparison.Ordinal)) return i;
            if (prec.Length >= 6 && digits.Contains(prec, StringComparison.Ordinal)) return i;
            if (personName.Length >= 6 && normalized.Contains(personName, StringComparison.Ordinal)) return i;
            if (war.Length >= 3 && Regex.IsMatch(normalized, $@"\b{Regex.Escape(war)}\b") && (RankRegex.IsMatch(rows[i]) || EmbeddedRankRegex.IsMatch(rows[i]))) return i;
        }

        for (var i = 0; i < rows.Count; i++)
            if (RankRegex.IsMatch(rows[i]) || EmbeddedRankRegex.IsMatch(rows[i])) return i;
        return -1;
    }

    private static (string Subject, string Note) SplitSubjectAndNote(string value)
    {
        var clean = CleanHeading(value);
        var pieces = Regex.Split(clean, @"\s+[-–—]\s+")
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToArray();
        if (pieces.Length < 2) return (clean, string.Empty);
        var subject = string.Join(" - ", pieces.Take(pieces.Length - 1)).Trim();
        var note = pieces[^1].Trim();
        return IsUsefulNoteTitle(note, subject) ? (subject, note) : (clean, string.Empty);
    }

    private static bool IsUsefulNoteTitle(string? value, string subject)
    {
        var clean = CleanHeading(value ?? string.Empty);
        if (clean.Length is < 3 or > 120) return false;
        if (Normalize(clean) == Normalize(subject)) return false;
        if (IsStructural(clean) || RankRegex.IsMatch(clean) || EmbeddedRankRegex.IsMatch(clean)) return false;
        if (ConsequenceRegex.IsMatch(clean)) return false;
        if (Regex.IsMatch(clean, @"\b(?:CPF|Prec[- ]?CP|IDT|R\$|Banco|Ag[êe]ncia|Conta|Valor|militar(?:es)?|relacionad[oa]s?)\b", RegexOptions.IgnoreCase)) return false;
        if (LooksLikeSentence(clean) && !Regex.IsMatch(clean, @"^(?:Concess[ãa]o|Apresenta[çc][ãa]o|Transcri[çc][ãa]o|Inclus[ãa]o|Exclus[ãa]o|Altera[çc][ãa]o|Solu[çc][ãa]o|Reconhecimento|Publica[çc][ãa]o|Designa[çc][ãa]o|Nomea[çc][ãa]o|Realiza[çc][ãa]o|Participa[çc][ãa]o|Passagem|Prazo)\b", RegexOptions.IgnoreCase)) return false;
        return true;
    }

    private static string BuildSubjectNoteDisplay(string subject, string noteTitle)
        => string.IsNullOrWhiteSpace(noteTitle) ? FormatSubject(subject) : $"{FormatSubject(subject)} — {FormatNoteTitle(noteTitle)}";

    private static string FormatSubject(string value)
    {
        var text = CleanHeading(value);
        return text.Length == 0 ? string.Empty : text.ToUpper(PtBr);
    }

    private static string FormatNoteTitle(string value)
    {
        var text = CleanHeading(value);
        if (text.Length == 0) return string.Empty;
        return text.Length > 1 && text == text.ToUpper(PtBr)
            ? PtBr.TextInfo.ToTitleCase(text.ToLower(PtBr))
            : text;
    }

    private static string ExtractFurrielContext(IReadOnlyList<string> rows)
    {
        var selected = rows
            .Where(line => FurrielConsequenceRegex.IsMatch(line))
            .Take(4)
            .ToList();
        if (selected.Count == 0) return string.Empty;
        return string.Join(" ", selected);
    }

    private static string FindPreviousSubject(IReadOnlyList<DocumentLine> lines, int start)
    {
        for (var i = start - 1; i >= 0; i--)
        {
            if (IsPartHeading(lines[i].Text)) break;
            var subject = DetectSubject(lines[i].Text);
            if (!string.IsNullOrWhiteSpace(subject) && IsSubjectHeading(lines[i].Text)) return subject;
        }
        return string.Empty;
    }

    private static List<ResolvedMilitary> ResolveMilitary(string noteText, IReadOnlyList<string> rows, IReadOnlyList<BulletinMilitaryIdentity> people)
    {
        var normalized = $" {Normalize(noteText)} ";
        var digits = Digits(noteText);
        var result = new List<ResolvedMilitary>();
        foreach (var person in people)
        {
            var score = 0;
            var full = Normalize(person.FullName);
            var war = Normalize(person.WarName);
            foreach (var document in new[] { person.Cpf, person.PrecCp, person.Identity }.Select(Digits).Where(x => x.Length >= 6))
                if (digits.Contains(document, StringComparison.Ordinal)) score = Math.Max(score, 2000);
            if (full.Length >= 6 && normalized.Contains($" {full} ", StringComparison.Ordinal)) score = Math.Max(score, 1800);
            var tokens = SignificantTokens(full);
            var hits = tokens.Count(x => normalized.Contains($" {x} ", StringComparison.Ordinal));
            if (tokens.Count >= 3 && hits == tokens.Count) score = Math.Max(score, 1400);
            if (tokens.Count >= 2 && hits >= 2)
            {
                var first = tokens.First();
                var last = tokens.Last();
                if (rows.Any(row =>
                {
                    var line = Normalize(row);
                    return line.Contains(first, StringComparison.Ordinal) && line.Contains(last, StringComparison.Ordinal);
                }))
                    score = Math.Max(score, 1150);
            }
            // FURRIEL: não vincular militar ativo apenas por nome de guerra.
            // Exemplo real: selecionar 2º Ten LUCCA HABAEB PINTO não pode casar com
            // 3º Sgt LUCCA SOARES MACHADO só porque ambos possuem "LUCCA" no nome.
            // A vinculação ao cadastro deve exigir nome completo ou documento
            // (CPF/Prec/IDT). Quando o PDF trouxer apenas uma linha de militar, ela
            // será mantida como menção não vinculada pelo nome literal do documento.
            if (score >= 900) result.Add(new ResolvedMilitary(person.FullName.ToUpper(PtBr), person.WarName.ToUpper(PtBr), person.Rank, person.Cpf, person.PrecCp, person.Id, true));
        }

        foreach (var line in rows)
        {
            var match = RankRegex.Match(line);
            if (!match.Success) match = EmbeddedRankRegex.Match(line);
            if (!match.Success) continue;
            var name = Regex.Split(match.Groups["name"].Value, @"\b(?:CPF|Prec[- ]?CP|IDT|identidade|matr[ií]cula|conforme|referente|ref\.?|por|para|foi|fica|faz\s+jus|deixa\s+de|passa\s+a|dever[áaãa]o?|solicito|solicita|autorizo|autorizado|publica|seja|em\s+consequ)\b", RegexOptions.IgnoreCase)[0].Trim(' ', '-', ',', ';', '.');
            if (name.Length < 5 || result.Any(x => NamesEquivalent(x.Name, name))) continue;
            var lineIndex = rows is List<string> rowList
                ? rowList.IndexOf(line)
                : rows.ToList().IndexOf(line);
            var local = string.Join(' ', rows.Skip(Math.Max(0, lineIndex)).Take(3));
            result.Add(new ResolvedMilitary(name.ToUpper(PtBr), string.Empty, match.Groups["rank"].Value, ExtractCpf(local), ExtractPrec(local), null, false));
        }
        return result;
    }

    private static bool IsNoteHeading(string line)
    {
        var text = OneLine(line);
        if (IsStructural(text) || RankRegex.IsMatch(text) || text.Length is < 3 or > 220) return false;
        if (ConsequenceRegex.IsMatch(text)) return false;
        if (IsLetteredNoteHeading(text)) return true;
        if (!string.IsNullOrWhiteSpace(DetectSubject(text)) && IsSubjectHeading(text)) return true;
        return Regex.IsMatch(text, @"^(?:[a-z]{1,3}\.|\d+[.)-]\s+)?\s*Nota\s*(?:n[ºo°]?|nr\.?)?\s*[:#-]?\s*\d{2,8}\b", RegexOptions.IgnoreCase);
    }

    private static bool IsSubjectHeading(string text)
    {
        var clean = CleanHeading(text);
        if (LooksLikeSentence(clean) || ConsequenceRegex.IsMatch(clean)) return false;
        var letters = clean.Where(char.IsLetter).ToList();
        if (letters.Count < 4) return false;
        var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return letters.Count(char.IsUpper) >= letters.Count * 0.72
               || (words <= 10 && clean.Length <= 120 && char.IsUpper(clean.FirstOrDefault(char.IsLetter)));
    }

    private static bool IsPartHeading(string value)
        => Regex.IsMatch(OneLine(value), @"^\d+[ªa]?\s+PARTE\b", RegexOptions.IgnoreCase);

    private static bool LooksLikeSentence(string value) => Regex.IsMatch(value, @"\b(?:seja|conforme|referente|tendo|solicito|militar|abaixo|pagamento|publica[çc][ãa]o)\b", RegexOptions.IgnoreCase) || value.Count(char.IsPunctuation) > 3;

    private static string DetectSubject(string value)
    {
        var clean = CleanHeading(value);
        var split = SplitSubjectAndNote(clean);
        if (IsLetteredNoteHeading(value) && IsProfessionalSubject(split.Subject)) return split.Subject.ToUpper(PtBr);

        var canonical = KnownSubjects.FirstOrDefault(x => x.Pattern.IsMatch(clean)).Canonical;
        if (!string.IsNullOrWhiteSpace(canonical)) return canonical;

        if (LooksLikeGenericSubject(split.Subject)) return split.Subject.ToUpper(PtBr);
        return string.Empty;
    }

    private static bool LooksLikeGenericSubject(string value)
    {
        var text = OneLine(value);
        if (!IsProfessionalSubject(text)) return false;
        var letters = text.Where(char.IsLetter).ToList();
        if (letters.Count < 4) return false;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return words <= 16 && (letters.Count(char.IsUpper) >= letters.Count * 0.58 || char.IsUpper(text.FirstOrDefault(char.IsLetter)));
    }

    private static bool IsLetteredNoteHeading(string value)
        => Regex.IsMatch(OneLine(value), @"^[a-z]{1,3}\.\s+\S", RegexOptions.IgnoreCase);

    private static bool IsProfessionalSubject(string value)
    {
        var text = OneLine(value);
        if (text.Length is < 4 or > 180) return false;
        if (LooksLikeSentence(text) || ConsequenceRegex.IsMatch(text)) return false;
        if (Regex.IsMatch(text, @"^(?:MINIST[ÉE]RIO|EX[ÉE]RCITO|BOLETIM|ADITAMENTO|P[áa]gina|Pag\.|Continua[çc][ãa]o|Sem Altera[çc][ãa]o)", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(text, @"\b(?:CPF|Prec[- ]?CP|IDT|R\$|Valor|Banco|Ag[êe]ncia|Conta)\b", RegexOptions.IgnoreCase)) return false;
        return true;
    }

    private static List<DocumentLine> MergeWrappedNoteHeadingLines(List<DocumentLine> lines)
    {
        var result = new List<DocumentLine>();
        for (var i = 0; i < lines.Count; i++)
        {
            var current = lines[i];
            if (!IsLetteredNoteHeading(current.Text))
            {
                result.Add(current);
                continue;
            }

            var text = current.Text;
            while (i + 1 < lines.Count && lines[i + 1].Page == current.Page && IsHeadingContinuation(lines[i + 1].Text, text))
            {
                text = OneLine(text + " " + lines[i + 1].Text);
                i++;
            }
            result.Add(new DocumentLine(text, current.Page));
        }
        return result;
    }

    private static bool IsHeadingContinuation(string next, string accumulated)
    {
        var text = OneLine(next);
        if (text.Length is < 3 or > 90) return false;
        if (IsStructural(text) || IsLetteredNoteHeading(text) || IsPartHeading(text)) return false;
        if (RankRegex.IsMatch(text) || EmbeddedRankRegex.IsMatch(text)) return false;
        if (ConsequenceRegex.IsMatch(text)) return false;
        if (Regex.IsMatch(text, @"^(?:Seja|No requerimento|Em virtude|Tendo em vista|Apresentou-se|Referente|Solicito|Faz jus|Deixa|Passa|Os militares|O militar|A militar)\b", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(text, @"\b(?:CPF|Prec[- ]?CP|IDT|R\$|Valor|Banco|Ag[êe]ncia|Conta)\b", RegexOptions.IgnoreCase)) return false;
        var combined = OneLine(accumulated + " " + text);
        if (!Regex.IsMatch(combined, @"\s[-–—]\s")) return false;
        return char.IsUpper(text.FirstOrDefault(char.IsLetter));
    }

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<string> CleanPageLines(string page) => (page ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(OneLine).Where(x => x.Length > 0).ToList();
    private static bool IsStructural(string value)
    {
        var text = OneLine(value);
        if (Regex.IsMatch(text, @"^(?:Pag\.?\s*n?[ºo°]?\s*\d+|P[áa]gina\s+\d+|MINIST[ÉE]RIO DA DEFESA|EX[ÉE]RCITO BRASILEIRO|BOLETIM\s+INTERNO|ADITAMENTO\s+DO\s+FURRIEL|Para conhecimento deste aquartelamento|Quartel\b|Continua[çc][ãa]o|Confere\s+com\s+o\s+original)", RegexOptions.IgnoreCase)) return true;
        return IsPartHeading(text) || Normalize(text) is "sem alteracao" or "sem alteracoes";
    }

    private static string CleanHeading(string value)
    {
        var text = Regex.Replace(OneLine(value), @"^[a-z]{1,3}\.|^\d+[.)-]\s+", string.Empty, RegexOptions.IgnoreCase).Trim(' ', '-', ':');
        return text.Length > 140 ? text[..140].TrimEnd() : text;
    }

    private static string ExtractUnlinkedName(IEnumerable<string> rows)
    {
        foreach (var row in rows)
        {
            var match = RankRegex.Match(row);
            if (!match.Success) match = EmbeddedRankRegex.Match(row);
            if (match.Success) return match.Groups["name"].Value.Trim().ToUpper(PtBr);
        }
        return string.Empty;
    }

    private static bool PersonOccurs(string text, ResolvedMilitary person)
    {
        var normalized = Normalize(text);
        if (!string.IsNullOrWhiteSpace(person.Name) && normalized.Contains(Normalize(person.Name), StringComparison.Ordinal)) return true;
        if (!string.IsNullOrWhiteSpace(person.WarName) && Regex.IsMatch(normalized, $@"\b{Regex.Escape(Normalize(person.WarName))}\b")) return true;
        var digits = Digits(text);
        return new[] { person.Cpf, person.PrecCp }.Select(Digits).Any(x => x.Length >= 6 && digits.Contains(x, StringComparison.Ordinal));
    }

    private static string ExtractCpf(string value) => Regex.Match(value, @"\b\d{3}\.?\d{3}\.?\d{3}-?\d{2}\b").Value;
    private static string ExtractPrec(string value) => Regex.Match(value, @"Prec[- ]?CP\s*[:#-]?\s*(\d{6,12})", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : string.Empty;
    private static string JoinRank(string rank, string name) => string.Join(' ', new[] { MilitaryRankService.ShortName(rank), name }.Where(x => !string.IsNullOrWhiteSpace(x)));
    private static bool NamesEquivalent(string a, string b) { var x = Normalize(a); var y = Normalize(b); return x == y || x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal); }
    private static List<string> SignificantTokens(string value) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length >= 3 && x is not "dos" and not "das" and not "de" and not "da" and not "do").Distinct().ToList();
    private static string Excerpt(string text) { var value = OneLine(text); return value.Length <= 220 ? value : value[..219].TrimEnd() + "…"; }
    private static string OneLine(string? value) => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
    private static string Digits(string? value) => Regex.Replace(value ?? string.Empty, @"\D+", string.Empty);
    private static string Normalize(string? value)
    {
        var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var chars = decomposed.Where(x => CharUnicodeInfo.GetUnicodeCategory(x) != UnicodeCategory.NonSpacingMark).Select(x => char.IsLetterOrDigit(x) ? char.ToLowerInvariant(x) : ' ').ToArray();
        return Regex.Replace(new string(chars), @"\s+", " ").Trim();
    }
    private static string ShortHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];

    private sealed record DocumentLine(string Text, int Page);
    private sealed record ResolvedMilitary(string Name, string WarName, string Rank, string Cpf, string PrecCp, int? Id, bool DatabaseMatch);
}
