using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNetCodeQualityPack.Fixer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DotNetCodeQualityPack.Rules
{
    internal enum FindingSource
    {
        Custom,
        Ai,
    }

    /// <summary>A problem found by a custom rule or by the AI review.</summary>
    internal sealed class RuleFinding
    {
        public FindingSource Source { get; set; }

        public string RuleId { get; set; }

        public string Title { get; set; }

        public string Message { get; set; }

        public IssueSeverity Severity { get; set; }

        public string FilePath { get; set; }

        public string RelativePath { get; set; }

        public DocumentId DocumentId { get; set; }

        /// <summary>1-based.</summary>
        public int Line { get; set; }

        public string LineText { get; set; }

        /// <summary>The text the fix was computed against.</summary>
        public SourceText Snapshot { get; set; }

        public TextSpan? FixSpan { get; set; }

        public string FixReplacement { get; set; }

        /// <summary>AI only: the code the model suggested replacing, used to find it again after edits.</summary>
        public string Original { get; set; }

        /// <summary>AI only: the model's replacement for <see cref="Original"/>.</summary>
        public string Replacement { get; set; }

        /// <summary>The .md file the rule is written in, for "Open rule".</summary>
        public string RuleSourceFile { get; set; }

        public int RuleSourceLine { get; set; }

        public bool HasFix => FixSpan.HasValue && FixReplacement != null;

        public static IssueSeverity ToSeverity(string severity)
        {
            switch (severity)
            {
                case "error": return IssueSeverity.Error;
                case "warning": return IssueSeverity.Warning;
                default: return IssueSeverity.Message;
            }
        }
    }

    /// <summary>Builds previews for finding fixes, and finds AI suggestions in the code.</summary>
    internal static class FindingFixes
    {
        public static async Task<FixPreview> BuildPreviewAsync(Solution solution, IReadOnlyList<RuleFinding> findings, string title, CancellationToken cancellationToken)
        {
            var preview = new FixPreview { Title = title };
            int stale = 0;

            foreach (IGrouping<DocumentId, RuleFinding> file in findings.Where(f => f.HasFix && f.DocumentId != null).GroupBy(f => f.DocumentId))
            {
                Document document = solution.GetDocument(file.Key);
                if (document == null)
                {
                    stale++;
                    continue;
                }

                SourceText current = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                List<RuleFinding> usable = file
                    .Where(f => f.Snapshot != null && current.ContentEquals(f.Snapshot))
                    .OrderBy(f => f.FixSpan.Value.Start)
                    .ToList();

                stale += file.Count() - usable.Count;

                var changes = new List<TextChange>();
                int lastEnd = -1;
                foreach (RuleFinding f in usable)
                {
                    TextSpan span = f.FixSpan.Value;
                    if (span.Start < lastEnd || span.End > current.Length)
                    {
                        continue; // Overlaps a fix already taken.
                    }

                    changes.Add(new TextChange(span, f.FixReplacement));
                    lastEnd = span.End;
                }

                if (changes.Count == 0)
                {
                    continue;
                }

                SourceText updated = current.WithChanges(changes);
                if (updated.ContentEquals(current))
                {
                    continue;
                }

                List<DiffLine> diff = LineDiff.Compute(current.ToString(), updated.ToString());
                LineDiff.Count(diff, out int added, out int removed);

                preview.FixCount += changes.Count;
                preview.Files.Add(new FileChange
                {
                    DocumentId = file.Key,
                    FilePath = document.FilePath ?? document.Name,
                    OldText = current,
                    NewText = updated,
                    Diff = diff,
                    Added = added,
                    Removed = removed,
                });
            }

            if (preview.Files.Count == 0)
            {
                preview.Problem = stale > 0
                    ? "The code changed since this was found. Run the check again to refresh it."
                    : "The fix doesn't change anything.";
            }

            preview.Files.Sort((x, y) => string.Compare(x.FilePath, y.FilePath, StringComparison.OrdinalIgnoreCase));
            return preview;
        }

        /// <summary>
        /// Finds the code the AI suggested changing, as close as possible to <paramref name="line"/> (1-based).
        /// Tries the exact text first, then a single line matched without indentation.
        /// </summary>
        public static bool TryLocate(SourceText text, int line, string original, string replacement, out TextSpan span, out string fix, out int foundLine)
        {
            span = default;
            fix = null;
            foundLine = line;

            if (string.IsNullOrWhiteSpace(original) || replacement == null || original.Trim() == replacement.Trim())
            {
                return false;
            }

            string content = text.ToString();
            string newline = content.Contains("\r\n") ? "\r\n" : "\n";
            string wanted = original.Replace("\r\n", "\n").Replace("\n", newline);
            string with = replacement.Replace("\r\n", "\n").Replace("\n", newline);

            // 1. Exact text, nearest occurrence within 15 lines.
            int best = -1;
            int bestDistance = int.MaxValue;
            for (int index = content.IndexOf(wanted, StringComparison.Ordinal); index >= 0; index = content.IndexOf(wanted, index + 1, StringComparison.Ordinal))
            {
                int distance = Math.Abs(text.Lines.GetLineFromPosition(index).LineNumber + 1 - line);
                if (distance < bestDistance)
                {
                    best = index;
                    bestDistance = distance;
                }
            }

            if (best >= 0 && bestDistance <= 15)
            {
                span = new TextSpan(best, wanted.Length);
                fix = with;
                foundLine = text.Lines.GetLineFromPosition(best).LineNumber + 1;
                return true;
            }

            // 2. Single line, ignoring indentation, within 3 lines.
            string trimmed = original.Trim();
            if (trimmed.Contains("\n"))
            {
                return false;
            }

            for (int distance = 0; distance <= 3; distance++)
            {
                foreach (int candidate in new[] { line - distance, line + distance })
                {
                    if (candidate < 1 || candidate > text.Lines.Count)
                    {
                        continue;
                    }

                    TextLine textLine = text.Lines[candidate - 1];
                    string lineText = textLine.ToString();
                    int at = lineText.IndexOf(trimmed, StringComparison.Ordinal);
                    if (at >= 0)
                    {
                        span = new TextSpan(textLine.Start + at, trimmed.Length);
                        fix = replacement.Trim();
                        foundLine = candidate;
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
