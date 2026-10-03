using System;
using System.Collections.Generic;

namespace DotNetCodeQualityPack.Fixer
{
    internal enum DiffKind
    {
        Context,
        Removed,
        Added,

        /// <summary>"... N unchanged lines" separator between hunks.</summary>
        Gap,

        /// <summary>The line a warning points at (code view for warnings without an automatic fix).</summary>
        Highlight,

        /// <summary>File header row in a multi-file preview.</summary>
        File,
    }

    internal sealed class DiffLine
    {
        public DiffLine(DiffKind kind, int? oldNumber, int? newNumber, string text)
        {
            Kind = kind;
            OldNumber = oldNumber;
            NewNumber = newNumber;
            Text = text;
        }

        public DiffKind Kind { get; }

        public int? OldNumber { get; }

        public int? NewNumber { get; }

        public string Text { get; }

        public string Sign
        {
            get
            {
                switch (Kind)
                {
                    case DiffKind.Removed: return "-";
                    case DiffKind.Added: return "+";
                    case DiffKind.Highlight: return ">";
                    default: return string.Empty;
                }
            }
        }
    }

    /// <summary>Line-based diff with context hunks, for the before/after preview.</summary>
    internal static class LineDiff
    {
        // Above this many cells the middle section is shown as "all removed, then all added" instead of an exact LCS.
        private const long MaxLcsCells = 2_000_000;

        public static string[] SplitLines(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        public static List<DiffLine> Compute(string oldText, string newText, int context = 3)
        {
            return Compute(SplitLines(oldText), SplitLines(newText), context);
        }

        public static List<DiffLine> Compute(string[] a, string[] b, int context)
        {
            List<Op> ops = Diff(a, b);
            return ToHunks(ops, a, b, context);
        }

        /// <summary>Shows the lines around <paramref name="line"/> (1-based), with that line highlighted.</summary>
        public static List<DiffLine> Snippet(string text, int line, int radius = 5)
        {
            string[] lines = SplitLines(text);
            int first = Math.Max(1, line - radius);
            int last = Math.Min(lines.Length, line + radius);
            var result = new List<DiffLine>();

            for (int n = first; n <= last; n++)
            {
                result.Add(new DiffLine(n == line ? DiffKind.Highlight : DiffKind.Context, n, n, Display(lines[n - 1])));
            }

            return result;
        }

        public static void Count(IEnumerable<DiffLine> lines, out int added, out int removed)
        {
            added = 0;
            removed = 0;
            foreach (DiffLine line in lines)
            {
                if (line.Kind == DiffKind.Added)
                {
                    added++;
                }
                else if (line.Kind == DiffKind.Removed)
                {
                    removed++;
                }
            }
        }

        private enum OpKind
        {
            Equal,
            Delete,
            Insert,
        }

        private struct Op
        {
            public Op(OpKind kind, int oldIndex, int newIndex)
            {
                Kind = kind;
                OldIndex = oldIndex;
                NewIndex = newIndex;
            }

            public OpKind Kind { get; }

            public int OldIndex { get; }

            public int NewIndex { get; }
        }

        private static List<Op> Diff(string[] a, string[] b)
        {
            int prefix = 0;
            while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
            {
                prefix++;
            }

            int suffix = 0;
            while (suffix < a.Length - prefix && suffix < b.Length - prefix
                && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
            {
                suffix++;
            }

            var ops = new List<Op>(Math.Max(a.Length, b.Length) + 8);
            for (int i = 0; i < prefix; i++)
            {
                ops.Add(new Op(OpKind.Equal, i, i));
            }

            int n = a.Length - prefix - suffix;
            int m = b.Length - prefix - suffix;

            if ((long)(n + 1) * (m + 1) <= MaxLcsCells)
            {
                // lcs[i, j] = LCS length of a[prefix+i ..] and b[prefix+j ..] within the middle section.
                var lcs = new int[n + 1, m + 1];
                for (int i = n - 1; i >= 0; i--)
                {
                    for (int j = m - 1; j >= 0; j--)
                    {
                        lcs[i, j] = a[prefix + i] == b[prefix + j]
                            ? lcs[i + 1, j + 1] + 1
                            : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                    }
                }

                int x = 0;
                int y = 0;
                while (x < n && y < m)
                {
                    if (a[prefix + x] == b[prefix + y])
                    {
                        ops.Add(new Op(OpKind.Equal, prefix + x, prefix + y));
                        x++;
                        y++;
                    }
                    else if (lcs[x + 1, y] >= lcs[x, y + 1])
                    {
                        ops.Add(new Op(OpKind.Delete, prefix + x, -1));
                        x++;
                    }
                    else
                    {
                        ops.Add(new Op(OpKind.Insert, -1, prefix + y));
                        y++;
                    }
                }

                for (; x < n; x++)
                {
                    ops.Add(new Op(OpKind.Delete, prefix + x, -1));
                }

                for (; y < m; y++)
                {
                    ops.Add(new Op(OpKind.Insert, -1, prefix + y));
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    ops.Add(new Op(OpKind.Delete, prefix + i, -1));
                }

                for (int j = 0; j < m; j++)
                {
                    ops.Add(new Op(OpKind.Insert, -1, prefix + j));
                }
            }

            for (int i = 0; i < suffix; i++)
            {
                ops.Add(new Op(OpKind.Equal, a.Length - suffix + i, b.Length - suffix + i));
            }

            return ops;
        }

        private static List<DiffLine> ToHunks(List<Op> ops, string[] a, string[] b, int context)
        {
            // Keep every change plus `context` equal lines on each side of it.
            var keep = new bool[ops.Count];
            for (int i = 0; i < ops.Count; i++)
            {
                if (ops[i].Kind == OpKind.Equal)
                {
                    continue;
                }

                int from = Math.Max(0, i - context);
                int to = Math.Min(ops.Count - 1, i + context);
                for (int k = from; k <= to; k++)
                {
                    keep[k] = true;
                }
            }

            var result = new List<DiffLine>();
            int skipped = 0;

            for (int i = 0; i < ops.Count; i++)
            {
                if (!keep[i])
                {
                    skipped++;
                    continue;
                }

                if (skipped > 0)
                {
                    result.Add(new DiffLine(DiffKind.Gap, null, null, string.Format("··· {0} unchanged line(s)", skipped)));
                    skipped = 0;
                }

                Op op = ops[i];
                switch (op.Kind)
                {
                    case OpKind.Equal:
                        result.Add(new DiffLine(DiffKind.Context, op.OldIndex + 1, op.NewIndex + 1, Display(a[op.OldIndex])));
                        break;
                    case OpKind.Delete:
                        result.Add(new DiffLine(DiffKind.Removed, op.OldIndex + 1, null, Display(a[op.OldIndex])));
                        break;
                    default:
                        result.Add(new DiffLine(DiffKind.Added, null, op.NewIndex + 1, Display(b[op.NewIndex])));
                        break;
                }
            }

            if (skipped > 0 && result.Count > 0)
            {
                result.Add(new DiffLine(DiffKind.Gap, null, null, string.Format("··· {0} unchanged line(s)", skipped)));
            }

            return result;
        }

        private static string Display(string line)
        {
            return line.Replace("\t", "    ");
        }
    }
}
