using System.Text.RegularExpressions;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core.Tools;

/// <summary>One targeted replacement. Port of the TS <c>Edit</c>.</summary>
public sealed record Edit(string OldText, string NewText);

/// <summary>Port of the TS <c>FuzzyMatchResult</c>.</summary>
public sealed record FuzzyMatchResult
{
    /// <summary>Whether a match was found.</summary>
    public required bool Found { get; init; }

    /// <summary>The index where the match starts (in the content that should be used for replacement).</summary>
    public required int Index { get; init; }

    /// <summary>Length of the matched text.</summary>
    public required int MatchLength { get; init; }

    /// <summary>Whether fuzzy matching was used (false = exact match).</summary>
    public required bool UsedFuzzyMatch { get; init; }

    /// <summary>
    /// The content to use for replacement operations. When exact match: original content. When
    /// fuzzy match: normalized content.
    /// </summary>
    public required string ContentForReplacement { get; init; }
}

/// <summary>Port of the TS <c>AppliedEditsResult</c>.</summary>
public sealed record AppliedEditsResult(string BaseContent, string NewContent)
{
    /// <summary>
    /// 上游 1.5.1 #8544：按 baseContent（LF 归一化文本）偏移升序的替换，供
    /// <see cref="EditDiff.ApplyReplacementsToOriginal"/> 映射回原始字节、保留未改动行的行尾。
    /// 保留为可空默认，兼容既有二元组解构。
    /// </summary>
    public IReadOnlyList<EditDiff.TextReplacement> Replacements { get; init; } = [];
}

/// <summary>Port of the TS <c>EditDiffResult</c>.</summary>
public sealed record EditDiffResult
{
    public required string Diff { get; init; }

    public int? FirstChangedLine { get; init; }
}

/// <summary>
/// Port of the TS <c>EditDiffError</c>. The TS returns a discriminated union; the port carries the
/// error text on the same shape and leaves <see cref="EditDiffResult"/> members null.
/// </summary>
public sealed record EditDiffOutcome
{
    public string? Diff { get; init; }

    public int? FirstChangedLine { get; init; }

    public string? Error { get; init; }

    public static EditDiffOutcome FromResult(EditDiffResult result) => new()
    {
        Diff = result.Diff,
        FirstChangedLine = result.FirstChangedLine,
    };

    public static EditDiffOutcome FromError(string error) => new() { Error = error };
}

/// <summary>
/// Shared diff computation utilities for the edit and similar tools. Port of
/// <c>core/tools/edit-diff.ts</c>.
/// </summary>
public static partial class EditDiff
{
    private sealed record LineSpan(int Start, int End);

    private sealed record MatchedEdit(int EditIndex, int MatchIndex, int MatchLength, string NewText);

    /// <summary>
    /// One replacement located in <c>baseContent</c>. Public because
    /// <see cref="ApplyReplacementsPreservingUnchangedLines"/> takes the caller's replacement list.
    /// </summary>
    public sealed record TextReplacement(int MatchIndex, int MatchLength, string NewText);

    // ---------- line endings ----------

    /// <summary>The TS <c>detectLineEnding</c>.</summary>
    public static string DetectLineEnding(string content)
    {
        var crlfIdx = content.IndexOf("\r\n", StringComparison.Ordinal);
        var lfIdx = content.IndexOf('\n');
        if (lfIdx == -1)
        {
            return "\n";
        }

        if (crlfIdx == -1)
        {
            return "\n";
        }

        return crlfIdx < lfIdx ? "\r\n" : "\n";
    }

    /// <summary>The TS <c>normalizeToLF</c>.</summary>
    public static string NormalizeToLf(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

    /// <summary>The TS <c>restoreLineEndings</c>.</summary>
    public static string RestoreLineEndings(string text, string ending) =>
        ending == "\r\n" ? text.Replace("\n", "\r\n", StringComparison.Ordinal) : text;

    /// <summary>
    /// 上游 1.5.1 #8544：把在 LF 归一化文本上匹配到的 replacements 映射回原始文本并就地拼接，
    /// 未改动区间连字节带行尾一起保留（原来是把整篇按一种行尾重写，会改掉未触碰行的 CRLF/孤立 CR）。
    /// <para>归一化只把 CRLF 缩成 LF（孤立 CR 变成 LF 但长度不变），故归一化偏移 = 原始偏移 −
    /// 其前的 CRLF 个数；覆盖到归一化 LF 的区间自然覆盖整个 CRLF。</para>
    /// <para>拼接缝：一段以 CR 结尾、下一段以 LF 开头时会并成一个 CRLF 行尾，导致写出的文件
    /// 归一化后与 diff/patch 描述不符；此时在缝上补一个 LF，把该 CR 变成独立的 CRLF。</para>
    /// </summary>
    public static string ApplyReplacementsToOriginal(
        string originalContent, IReadOnlyList<TextReplacement> replacements)
    {
        var crlfBefore = 0;
        var nextCrlf = originalContent.IndexOf("\r\n", StringComparison.Ordinal);

        // 归一化偏移 → 原始偏移。replacements 按 matchIndex 升序、区间不重叠，故 nextCrlf 只需前进。
        int ToOriginal(int offset)
        {
            while (nextCrlf != -1 && nextCrlf - crlfBefore < offset)
            {
                crlfBefore++;
                nextCrlf = originalContent.IndexOf("\r\n", nextCrlf + 2, StringComparison.Ordinal);
            }

            return offset + crlfBefore;
        }

        var parts = new List<string>();
        var endsWithCr = false;
        void Push(string part)
        {
            if (part.Length == 0)
            {
                return;
            }

            if (endsWithCr && part[0] == '\n')
            {
                parts.Add("\n"); // 缝：保留的 CR 与新 LF 相遇时补一个 LF
            }

            parts.Add(part);
            endsWithCr = part[^1] == '\r';
        }

        var position = 0;
        foreach (var replacement in replacements)
        {
            var start = ToOriginal(replacement.MatchIndex);
            Push(originalContent[position..start]);
            var ending = GetLineEndingAt(originalContent, start);
            Push(ending == "\n"
                ? replacement.NewText
                : replacement.NewText.Replace("\n", ending, StringComparison.Ordinal));
            position = ToOriginal(replacement.MatchIndex + replacement.MatchLength);
        }

        Push(originalContent[position..]);
        return string.Concat(parts);
    }

    /// <summary>
    /// 上游 1.5.1 #8544：offset 所在行的行尾（自身的 LF/CRLF，未终止末行用前一行的行尾）。
    /// 孤立 CR 只在完全不含 LF 的文件（经典 Mac）里才当行尾，否则更可能是行内控制字符。
    /// </summary>
    private static string GetLineEndingAt(string content, int offset)
    {
        var lineFeed = content.IndexOf('\n', offset);
        if (lineFeed == -1)
        {
            lineFeed = offset < content.Length
                ? content.LastIndexOf('\n', offset)
                : content.LastIndexOf('\n');
        }

        if (lineFeed == -1)
        {
            return content.Contains('\r') ? "\r" : "\n";
        }

        return lineFeed > 0 && content[lineFeed - 1] == '\r' ? "\r\n" : "\n";
    }

    /// <summary>
    /// Normalize text for fuzzy matching. Applies progressive transformations: strip trailing
    /// whitespace from each line, normalize smart quotes to ASCII equivalents, normalize Unicode
    /// dashes/hyphens to ASCII hyphen, normalize special Unicode spaces to regular space.
    /// </summary>
    public static string NormalizeForFuzzyMatch(string text)
    {
        var normalized = text.Normalize(System.Text.NormalizationForm.FormKC);

        // Strip trailing whitespace per line.
        var lines = normalized.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].TrimEnd();
        }

        normalized = string.Join("\n", lines);

        // Smart single quotes → '
        normalized = SmartSingleQuotesRegex().Replace(normalized, "'");

        // Smart double quotes → "
        normalized = SmartDoubleQuotesRegex().Replace(normalized, "\"");

        // Various dashes/hyphens → -
        // U+2010 hyphen, U+2011 non-breaking hyphen, U+2012 figure dash,
        // U+2013 en-dash, U+2014 em-dash, U+2015 horizontal bar, U+2212 minus
        normalized = DashesRegex().Replace(normalized, "-");

        // Special spaces → regular space
        // U+00A0 NBSP, U+2002-U+200A various spaces, U+202F narrow NBSP,
        // U+205F medium math space, U+3000 ideographic space
        normalized = SpecialSpacesRegex().Replace(normalized, " ");

        return normalized;
    }

    [GeneratedRegex("[\u2018\u2019\u201A\u201B]")]
    private static partial Regex SmartSingleQuotesRegex();

    [GeneratedRegex("[\u201C\u201D\u201E\u201F]")]
    private static partial Regex SmartDoubleQuotesRegex();

    [GeneratedRegex("[\u2010\u2011\u2012\u2013\u2014\u2015\u2212]")]
    private static partial Regex DashesRegex();

    [GeneratedRegex("[\u00A0\u2002-\u200A\u202F\u205F\u3000]")]
    private static partial Regex SpecialSpacesRegex();

    // ---------- line spans ----------

    /// <summary>The TS <c>splitLinesWithEndings</c>: <c>content.match(/[^\n]*\n|[^\n]+/g) ?? []</c>.</summary>
    private static List<string> SplitLinesWithEndings(string content)
    {
        var matches = LinesWithEndingsRegex().Matches(content);
        var lines = new List<string>(matches.Count);
        foreach (Match match in matches)
        {
            lines.Add(match.Value);
        }

        return lines;
    }

    [GeneratedRegex(@"[^\n]*\n|[^\n]+")]
    private static partial Regex LinesWithEndingsRegex();

    private static List<LineSpan> GetLineSpans(string content)
    {
        var offset = 0;
        var spans = new List<LineSpan>();
        foreach (var line in SplitLinesWithEndings(content))
        {
            var span = new LineSpan(offset, offset + line.Length);
            offset = span.End;
            spans.Add(span);
        }

        return spans;
    }

    private static (int StartLine, int EndLine) GetReplacementLineRange(List<LineSpan> lines, TextReplacement replacement)
    {
        var replacementStart = replacement.MatchIndex;
        var replacementEnd = replacement.MatchIndex + replacement.MatchLength;

        var startLine = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (replacementStart >= line.Start && replacementStart < line.End)
            {
                startLine = i;
                break;
            }
        }

        if (startLine == -1)
        {
            throw new InvalidOperationException("Replacement range is outside the base content.");
        }

        var endLine = startLine;
        while (endLine < lines.Count && lines[endLine].End < replacementEnd)
        {
            endLine++;
        }

        if (endLine >= lines.Count)
        {
            throw new InvalidOperationException("Replacement range is outside the base content.");
        }

        return (startLine, endLine + 1);
    }

    internal static string ApplyReplacements(string content, List<TextReplacement> replacements, int offset = 0)
    {
        var result = content;
        for (var i = replacements.Count - 1; i >= 0; i--)
        {
            var replacement = replacements[i];
            var matchIndex = replacement.MatchIndex - offset;
            result = result[..matchIndex] + replacement.NewText + result[(matchIndex + replacement.MatchLength)..];
        }

        return result;
    }

    /// <summary>
    /// 上游 1.5.1 #8544 的 <c>widenReplacementsToLines</c>：把在 <c>baseContent</c>（模糊归一化视图）
    /// 上匹配到的 replacements 转成在 <c>originalContent</c>（LF 归一化文本）上的替换，每条放宽到
    /// 它实际触及的整行；未触及的行保持在原始文本里，不重建整篇。
    /// 供 <see cref="ApplyReplacementsToOriginal"/> 映射回原始字节、保留未改动行行尾。
    /// </summary>
    public static List<TextReplacement> WidenReplacementsToLines(
        string originalContent,
        string baseContent,
        List<TextReplacement> replacements)
    {
        var originalLines = GetLineSpans(originalContent);
        var baseLines = GetLineSpans(baseContent);
        if (originalLines.Count != baseLines.Count)
        {
            throw new InvalidOperationException(
                "Cannot preserve unchanged lines because the base content has a different line count.");
        }

        var groups = new List<(int StartLine, int EndLine, List<TextReplacement> Replacements)>();
        // The JS sort is stable (V8 TimSort); OrderBy keeps that contract.
        var sortedReplacements = replacements.OrderBy(r => r.MatchIndex).ToList();
        foreach (var replacement in sortedReplacements)
        {
            var range = GetReplacementLineRange(baseLines, replacement);
            if (groups.Count > 0)
            {
                var current = groups[^1];
                if (range.StartLine < current.EndLine)
                {
                    current.Replacements.Add(replacement);
                    groups[^1] = (current.StartLine, Math.Max(current.EndLine, range.EndLine), current.Replacements);
                    continue;
                }
            }

            groups.Add((range.StartLine, range.EndLine, new List<TextReplacement> { replacement }));
        }

        var result = new List<TextReplacement>(groups.Count);
        foreach (var group in groups)
        {
            var groupStartOffset = baseLines[group.StartLine].Start;
            var groupEndOffset = baseLines[group.EndLine - 1].End;
            var originalStart = originalLines[group.StartLine].Start;
            var originalEnd = originalLines[group.EndLine - 1].End;
            result.Add(new TextReplacement(
                originalStart,
                originalEnd - originalStart,
                ApplyReplacements(
                    baseContent[groupStartOffset..groupEndOffset],
                    group.Replacements,
                    groupStartOffset)));
        }

        return result;
    }

    /// <summary>
    /// 兼容旧调用：把宽化后的替换应用到 <c>originalContent</c> 得到结果文本。
    /// 上游 1.5.1 起改用 <see cref="WidenReplacementsToLines"/> + <see cref="ApplyReplacementsToOriginal"/>，
    /// 这里保留供既有调用方按旧语义（在归一化文本上重建）使用。
    /// </summary>
    public static string ApplyReplacementsPreservingUnchangedLines(
        string originalContent,
        string baseContent,
        List<TextReplacement> replacements) =>
        ApplyReplacements(originalContent, WidenReplacementsToLines(originalContent, baseContent, replacements));

    // ---------- fuzzy matching ----------

    /// <summary>
    /// Find oldText in content, trying exact match first, then fuzzy match. When fuzzy matching is
    /// used, the returned <see cref="FuzzyMatchResult.ContentForReplacement"/> is the
    /// fuzzy-normalized version of the content.
    /// </summary>
    public static FuzzyMatchResult FuzzyFindText(string content, string oldText)
    {
        // Try exact match first.
        var exactIndex = content.IndexOf(oldText, StringComparison.Ordinal);
        if (exactIndex != -1)
        {
            return new FuzzyMatchResult
            {
                Found = true,
                Index = exactIndex,
                MatchLength = oldText.Length,
                UsedFuzzyMatch = false,
                ContentForReplacement = content,
            };
        }

        // Try fuzzy match - work entirely in normalized space.
        var fuzzyContent = NormalizeForFuzzyMatch(content);
        var fuzzyOldText = NormalizeForFuzzyMatch(oldText);
        var fuzzyIndex = fuzzyContent.IndexOf(fuzzyOldText, StringComparison.Ordinal);

        if (fuzzyIndex == -1)
        {
            return new FuzzyMatchResult
            {
                Found = false,
                Index = -1,
                MatchLength = 0,
                UsedFuzzyMatch = false,
                ContentForReplacement = content,
            };
        }

        return new FuzzyMatchResult
        {
            Found = true,
            Index = fuzzyIndex,
            MatchLength = fuzzyOldText.Length,
            UsedFuzzyMatch = true,
            ContentForReplacement = fuzzyContent,
        };
    }

    private static int CountOccurrences(string content, string oldText)
    {
        var fuzzyContent = NormalizeForFuzzyMatch(content);
        var fuzzyOldText = NormalizeForFuzzyMatch(oldText);
        if (fuzzyOldText.Length == 0)
        {
            // JS split("") splits into UTF-16 units; the count is the string length.
            return fuzzyContent.Length;
        }

        return fuzzyContent.Split(fuzzyOldText).Length - 1;
    }

    private static InvalidOperationException GetNotFoundError(string path, int editIndex, int totalEdits) =>
        totalEdits == 1
            ? new InvalidOperationException(
                $"Could not find the exact text in {path}. The old text must match exactly including all whitespace and newlines.")
            : new InvalidOperationException(
                $"Could not find edits[{editIndex}] in {path}. The oldText must match exactly including all whitespace and newlines.");

    private static InvalidOperationException GetDuplicateError(string path, int editIndex, int totalEdits, int occurrences) =>
        totalEdits == 1
            ? new InvalidOperationException(
                $"Found {occurrences} occurrences of the text in {path}. The text must be unique. Please provide more context to make it unique.")
            : new InvalidOperationException(
                $"Found {occurrences} occurrences of edits[{editIndex}] in {path}. Each oldText must be unique. Please provide more context to make it unique.");

    private static InvalidOperationException GetEmptyOldTextError(string path, int editIndex, int totalEdits) =>
        totalEdits == 1
            ? new InvalidOperationException($"oldText must not be empty in {path}.")
            : new InvalidOperationException($"edits[{editIndex}].oldText must not be empty in {path}.");

    private static InvalidOperationException GetNoChangeError(string path, int totalEdits) =>
        totalEdits == 1
            ? new InvalidOperationException(
                $"No changes made to {path}. The replacement produced identical content. This might indicate an issue with special characters or the text not existing as expected.")
            : new InvalidOperationException(
                $"No changes made to {path}. The replacements produced identical content.");

    /// <summary>
    /// Apply one or more exact-text replacements to LF-normalized content. All edits are matched
    /// against the same original content; replacements are applied in reverse order so offsets
    /// remain stable. If any edit needs fuzzy matching, the operation runs in fuzzy-normalized
    /// content space and then overlays those line-level changes onto the original content so
    /// unchanged line blocks keep their original bytes.
    /// </summary>
    public static AppliedEditsResult ApplyEditsToNormalizedContent(
        string normalizedContent,
        IReadOnlyList<Edit> edits,
        string path)
    {
        var normalizedEdits = edits
            .Select(edit => (OldText: NormalizeToLf(edit.OldText), NewText: NormalizeToLf(edit.NewText)))
            .ToList();

        for (var i = 0; i < normalizedEdits.Count; i++)
        {
            if (normalizedEdits[i].OldText.Length == 0)
            {
                throw GetEmptyOldTextError(path, i, normalizedEdits.Count);
            }
        }

        var initialMatches = normalizedEdits
            .Select(edit => FuzzyFindText(normalizedContent, edit.OldText))
            .ToList();
        var usedFuzzyMatch = initialMatches.Any(match => match.UsedFuzzyMatch);
        var replacementBaseContent = usedFuzzyMatch ? NormalizeForFuzzyMatch(normalizedContent) : normalizedContent;

        var matchedEdits = new List<MatchedEdit>();
        for (var i = 0; i < normalizedEdits.Count; i++)
        {
            var edit = normalizedEdits[i];
            var matchResult = FuzzyFindText(replacementBaseContent, edit.OldText);
            if (!matchResult.Found)
            {
                throw GetNotFoundError(path, i, normalizedEdits.Count);
            }

            var occurrences = CountOccurrences(replacementBaseContent, edit.OldText);
            if (occurrences > 1)
            {
                throw GetDuplicateError(path, i, normalizedEdits.Count, occurrences);
            }

            matchedEdits.Add(new MatchedEdit(i, matchResult.Index, matchResult.MatchLength, edit.NewText));
        }

        // The JS sort is stable; OrderBy keeps that contract.
        var sortedEdits = matchedEdits.OrderBy(edit => edit.MatchIndex).ToList();
        for (var i = 1; i < sortedEdits.Count; i++)
        {
            var previous = sortedEdits[i - 1];
            var current = sortedEdits[i];
            if (previous.MatchIndex + previous.MatchLength > current.MatchIndex)
            {
                throw new InvalidOperationException(
                    $"edits[{previous.EditIndex}] and edits[{current.EditIndex}] overlap in {path}. Merge them into one edit or target disjoint regions.");
            }
        }

        var baseContent = normalizedContent;
        // 上游 1.5.1 #8544：精确匹配直接用归一化文本上的匹配区间；模糊匹配先放宽到整行并映射回
        // 归一化文本坐标。replacements 供写盘时映射回原始字节、保留未改动行的行尾。
        var rawReplacements = sortedEdits
            .Select(edit => new TextReplacement(edit.MatchIndex, edit.MatchLength, edit.NewText))
            .ToList();
        var replacements = usedFuzzyMatch
            ? WidenReplacementsToLines(normalizedContent, replacementBaseContent, rawReplacements)
            : rawReplacements;
        var newContent = ApplyReplacements(baseContent, replacements);

        if (baseContent == newContent)
        {
            throw GetNoChangeError(path, normalizedEdits.Count);
        }

        return new AppliedEditsResult(baseContent, newContent) { Replacements = replacements };
    }

    // ---------- diff rendering ----------

    /// <summary>Generate a standard unified patch. The TS <c>generateUnifiedPatch</c>.</summary>
    public static string GenerateUnifiedPatch(string path, string oldContent, string newContent, int contextLines = 4) =>
        JsDiff.CreateTwoFilesPatch(
            path,
            path,
            oldContent,
            newContent,
            context: contextLines,
            headerOptions: HeaderOptions.FileHeadersOnly);

    /// <summary>
    /// Generate a display-oriented diff string with line numbers and context. Returns both the diff
    /// string and the first changed line number (in the new file). The TS <c>generateDiffString</c>.
    /// </summary>
    public static EditDiffResult GenerateDiffString(string oldContent, string newContent, int contextLines = 4)
    {
        var parts = JsDiff.DiffLines(oldContent, newContent);
        var output = new List<string>();

        var oldLines = oldContent.Split('\n');
        var newLines = newContent.Split('\n');
        var maxLineNum = Math.Max(oldLines.Length, newLines.Length);
        var lineNumWidth = maxLineNum.ToString().Length;

        var oldLineNum = 1;
        var newLineNum = 1;
        var lastWasChange = false;
        int? firstChangedLine = null;

        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var raw = part.Value.Split('\n');
            if (raw.Length > 0 && raw[^1] == "")
            {
                raw = raw[..^1];
            }

            if (part.Added || part.Removed)
            {
                // Capture the first changed line (in the new file).
                if (firstChangedLine is null)
                {
                    firstChangedLine = newLineNum;
                }

                // Show the change.
                foreach (var line in raw)
                {
                    if (part.Added)
                    {
                        output.Add($"+{newLineNum.ToString().PadLeft(lineNumWidth, ' ')} {line}");
                        newLineNum++;
                    }
                    else
                    {
                        // removed
                        output.Add($"-{oldLineNum.ToString().PadLeft(lineNumWidth, ' ')} {line}");
                        oldLineNum++;
                    }
                }

                lastWasChange = true;
            }
            else
            {
                // Context lines - only show a few before/after changes.
                var nextPartIsChange = i < parts.Count - 1 && (parts[i + 1].Added || parts[i + 1].Removed);
                var hasLeadingChange = lastWasChange;
                var hasTrailingChange = nextPartIsChange;

                if (hasLeadingChange && hasTrailingChange)
                {
                    if (raw.Length <= contextLines * 2)
                    {
                        foreach (var line in raw)
                        {
                            output.Add($" {oldLineNum.ToString().PadLeft(lineNumWidth, ' ')} {line}");
                            oldLineNum++;
                            newLineNum++;
                        }
                    }
                    else
                    {
                        var leadingLines = raw[..contextLines];
                        var trailingLines = raw[^contextLines..];
                        var skippedLines = raw.Length - leadingLines.Length - trailingLines.Length;

                        foreach (var line in leadingLines)
                        {
                            output.Add($" {oldLineNum.ToString().PadLeft(lineNumWidth, ' ')} {line}");
                            oldLineNum++;
                            newLineNum++;
                        }

                        output.Add($" {"".PadLeft(lineNumWidth, ' ')} ...");
                        oldLineNum += skippedLines;
                        newLineNum += skippedLines;

                        foreach (var line in trailingLines)
                        {
                            output.Add($" {oldLineNum.ToString().PadLeft(lineNumWidth, ' ')} {line}");
                            oldLineNum++;
                            newLineNum++;
                        }
                    }
                }
                else if (hasLeadingChange)
                {
                    var shownLines = raw[..Math.Min(contextLines, raw.Length)];
                    var skippedLines = raw.Length - shownLines.Length;

                    foreach (var line in shownLines)
                    {
                        output.Add($" {oldLineNum.ToString().PadLeft(lineNumWidth, ' ')} {line}");
                        oldLineNum++;
                        newLineNum++;
                    }

                    if (skippedLines > 0)
                    {
                        output.Add($" {"".PadLeft(lineNumWidth, ' ')} ...");
                        oldLineNum += skippedLines;
                        newLineNum += skippedLines;
                    }
                }
                else if (hasTrailingChange)
                {
                    var skippedLines = Math.Max(0, raw.Length - contextLines);
                    if (skippedLines > 0)
                    {
                        output.Add($" {"".PadLeft(lineNumWidth, ' ')} ...");
                        oldLineNum += skippedLines;
                        newLineNum += skippedLines;
                    }

                    foreach (var line in raw[skippedLines..])
                    {
                        output.Add($" {oldLineNum.ToString().PadLeft(lineNumWidth, ' ')} {line}");
                        oldLineNum++;
                        newLineNum++;
                    }
                }
                else
                {
                    // Skip these context lines entirely.
                    oldLineNum += raw.Length;
                    newLineNum += raw.Length;
                }

                lastWasChange = false;
            }
        }

        return new EditDiffResult { Diff = string.Join("\n", output), FirstChangedLine = firstChangedLine };
    }

    // ---------- preview ----------

    /// <summary>
    /// Compute the diff for one or more edit operations without applying them. Used for preview
    /// rendering in the TUI before the tool executes. The TS <c>computeEditsDiff</c>.
    /// </summary>
    public static async Task<EditDiffOutcome> ComputeEditsDiffAsync(string path, IReadOnlyList<Edit> edits, string cwd)
    {
        var absolutePath = ToolPathUtils.ResolveToCwd(path, cwd);

        try
        {
            // Check if file exists and is readable.
            if (!File.Exists(absolutePath))
            {
                return EditDiffOutcome.FromError($"Could not edit file: {path}. Error code: ENOENT.");
            }

            // Read the file.
            string rawContent;
            try
            {
                rawContent = await File.ReadAllTextAsync(absolutePath).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                var message = error is IOException or UnauthorizedAccessException
                    ? $"Error code: {GetNodeErrorCode(error)}"
                    : error.Message;
                return EditDiffOutcome.FromError($"Could not edit file: {path}. {message}.");
            }

            // Strip BOM before matching (the LLM won't include an invisible BOM in oldText).
            var content = Text.SplitBom(rawContent).Text;
            var normalizedContent = NormalizeToLf(content);
            var (baseContent, newContent) = ApplyEditsToNormalizedContent(normalizedContent, edits, path);

            // Generate the diff.
            return EditDiffOutcome.FromResult(GenerateDiffString(baseContent, newContent));
        }
        catch (Exception error)
        {
            return EditDiffOutcome.FromError(error is InvalidOperationException io ? io.Message : error.Message);
        }
    }

    /// <summary>
    /// Compute the diff for a single edit operation without applying it. Kept as a convenience
    /// wrapper for single-edit callers. The TS <c>computeEditDiff</c>.
    /// </summary>
    public static Task<EditDiffOutcome> ComputeEditDiffAsync(string path, string oldText, string newText, string cwd) =>
        ComputeEditsDiffAsync(path, [new Edit(oldText, newText)], cwd);

    private static string GetNodeErrorCode(Exception error) => error switch
    {
        FileNotFoundException => "ENOENT",
        DirectoryNotFoundException => "ENOENT",
        UnauthorizedAccessException => "EACCES",
        PathTooLongException => "ENAMETOOLONG",
        _ => "UNKNOWN",
    };
}
