using System.Windows.Media;
using DotNetCodeQualityPack.Fixer;
using DotNetCodeQualityPack.Rules;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;

namespace DotNetCodeQualityPack.ToolWindows
{
    /// <summary>One row in the Rules tab.</summary>
    internal sealed class RuleRowViewModel
    {
        public RuleRowViewModel(string group, string id, string title, string detail, string tag, Brush tagBrush, int sourceLine, string sourceFile = null)
        {
            Group = group;
            Id = id;
            Title = title;
            Detail = detail;
            Tag = tag;
            TagBrush = tagBrush;
            SourceLine = sourceLine;
            SourceFile = sourceFile;
        }

        /// <summary>The .md file the rule is written in (double-click opens it at <see cref="SourceLine"/>).</summary>
        public string SourceFile { get; }

        public string Group { get; }

        public string Id { get; }

        public string Title { get; }

        public string Detail { get; }

        public bool HasDetail => !string.IsNullOrEmpty(Detail);

        public string Tag { get; }

        public Brush TagBrush { get; }

        public int SourceLine { get; }

        public string SourceLineLabel => SourceLine > 0 ? "md:" + SourceLine : string.Empty;

        public static Brush SeverityBrush(string severity)
        {
            switch (severity)
            {
                case "error": return Palette.Error;
                case "warning": return Palette.Warning;
                case "suggestion": return Palette.Info;
                default: return Palette.Neutral;
            }
        }
    }

    /// <summary>A Copilot instruction file that can be switched on or off for AI review.</summary>
    internal sealed class InstructionFileOption : ObservableObject
    {
        private readonly System.Action<InstructionFileOption> _onChanged;
        private bool _isEnabled;

        public InstructionFileOption(string name, string description, int ruleCount, string applyTo, bool isEnabled, System.Action<InstructionFileOption> onChanged)
        {
            Name = name;
            Description = description;
            RuleCount = ruleCount;
            ApplyTo = applyTo;
            _isEnabled = isEnabled;
            _onChanged = onChanged;
        }

        public string Name { get; }

        public string Description { get; }

        public int RuleCount { get; }

        public string ApplyTo { get; }

        public string Label => Name + " (" + RuleCount + ")";

        public string ToolTip => (string.IsNullOrEmpty(Description) ? Name : Description) + "\napplyTo: " + ApplyTo;

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (Set(ref _isEnabled, value))
                {
                    _onChanged?.Invoke(this);
                }
            }
        }
    }

    /// <summary>One row in the Findings tab.</summary>
    internal sealed class FindingRowViewModel
    {
        public FindingRowViewModel(RuleFinding finding)
        {
            Finding = finding;

            switch (finding.Severity)
            {
                case IssueSeverity.Error:
                    Moniker = KnownMonikers.StatusError;
                    break;
                case IssueSeverity.Warning:
                    Moniker = KnownMonikers.StatusWarning;
                    break;
                default:
                    Moniker = KnownMonikers.StatusInformation;
                    break;
            }
        }

        public RuleFinding Finding { get; }

        /// <summary>Grouping key.</summary>
        public string FileGroup => Finding.RelativePath;

        /// <summary>Sort key inside a file.</summary>
        public int LineNumber => Finding.Line;

        public ImageMoniker Moniker { get; }

        public string SourceLabel => Finding.Source == FindingSource.Ai ? "AI" : "Rule";

        public Brush SourceBrush => Finding.Source == FindingSource.Ai ? Palette.Ai : Palette.Teal;

        public string RuleId => Finding.RuleId;

        public string LineLabel => "L" + Finding.Line;

        public string Code => string.IsNullOrEmpty(Finding.LineText) ? "(empty line)" : Finding.LineText;

        public string Message => Finding.Message;

        public string Title => Finding.Title;

        public string FixTag => Finding.HasFix ? "Fix" : "Manual";

        public Brush FixBrush => Finding.HasFix ? Palette.Ok : Palette.Neutral;
    }

    /// <summary>One row in the Token usage section: last review, today, this session or all time.</summary>
    internal sealed class UsageRowViewModel
    {
        public UsageRowViewModel(string label, string detail, long input, long output, int requests, bool estimated)
        {
            Label = label;
            Detail = detail;
            string prefix = estimated ? "≈" : string.Empty;
            Input = prefix + Ai.TokenUsage.Short(input);
            Output = prefix + Ai.TokenUsage.Short(output);
            Total = prefix + Ai.TokenUsage.Short(input + output);
            Requests = requests.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
            ToolTip = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                "{0}: {1} input + {2} output = {3} tokens in {4} request(s){5}",
                label,
                Ai.TokenUsage.Full(input),
                Ai.TokenUsage.Full(output),
                Ai.TokenUsage.Full(input + output),
                requests,
                estimated ? " (some estimated)" : string.Empty);
        }

        public string Label { get; }

        public string Detail { get; }

        public string Input { get; }

        public string Output { get; }

        public string Total { get; }

        public string Requests { get; }

        public string ToolTip { get; }
    }
}
