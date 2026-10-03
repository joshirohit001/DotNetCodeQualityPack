using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DotNetCodeQualityPack.Fixer
{
    internal enum IssueSeverity
    {
        Error,
        Warning,
        Message,
    }

    /// <summary>One warning or message found in a source file.</summary>
    internal sealed class IssueInfo
    {
        public string Id { get; set; }

        public string Message { get; set; }

        public string RuleTitle { get; set; }

        public IssueSeverity Severity { get; set; }

        public string FilePath { get; set; }

        public DocumentId DocumentId { get; set; }

        /// <summary>1-based line number.</summary>
        public int Line { get; set; }

        /// <summary>The source line the warning points at, trimmed (for display and for matching after edits).</summary>
        public string LineText { get; set; }

        public TextSpan Span { get; set; }

        public bool HasFix { get; set; }

        public string HelpLink { get; set; }

        public string Key => Id + "|" + FilePath + "|" + LineText;
    }

    /// <summary>The changes to one file inside a preview.</summary>
    internal sealed class FileChange
    {
        public DocumentId DocumentId { get; set; }

        public string FilePath { get; set; }

        public SourceText OldText { get; set; }

        public SourceText NewText { get; set; }

        public List<DiffLine> Diff { get; set; }

        public int Added { get; set; }

        public int Removed { get; set; }
    }

    /// <summary>A computed fix (single warning, a page, or a rule across the solution) waiting for approval.</summary>
    internal sealed class FixPreview
    {
        public string Title { get; set; }

        public int FixCount { get; set; }

        public List<FileChange> Files { get; set; } = new List<FileChange>();

        /// <summary>Set when no change could be produced; explains why.</summary>
        public string Problem { get; set; }

        public bool HasChanges => Problem == null && Files.Count > 0;
    }

    /// <summary>Result of applying a preview to the workspace.</summary>
    internal sealed class ApplyResult
    {
        public bool Succeeded { get; set; }

        public List<FileChange> Applied { get; set; } = new List<FileChange>();

        /// <summary>Files that changed after the preview was made, so they were left alone.</summary>
        public List<string> Conflicts { get; set; } = new List<string>();
    }

    /// <summary>A change ready to be committed to the workspace on the UI thread.</summary>
    internal sealed class PreparedChange
    {
        public Solution Solution { get; set; }

        public bool IsUndo { get; set; }

        public ApplyResult Result { get; } = new ApplyResult();
    }
}
