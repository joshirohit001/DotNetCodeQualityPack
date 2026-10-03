using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using DotNetCodeQualityPack.Fixer;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;

namespace DotNetCodeQualityPack.ToolWindows
{
    internal abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            Raise(propertyName);
            return true;
        }

        protected void Raise(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    internal sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object parameter) => _canExecute == null || _canExecute();

        public void Execute(object parameter) => _execute();
    }

    internal static class Palette
    {
        public static readonly Brush Ok = Freeze(Color.FromRgb(0x2E, 0xA0, 0x43));
        public static readonly Brush Neutral = Freeze(Color.FromRgb(0x6E, 0x76, 0x81));
        public static readonly Brush Warning = Freeze(Color.FromRgb(0xD2, 0x99, 0x22));
        public static readonly Brush Error = Freeze(Color.FromRgb(0xE5, 0x53, 0x4B));
        public static readonly Brush Info = Freeze(Color.FromRgb(0x37, 0x94, 0xFF));
        public static readonly Brush Ai = Freeze(Color.FromRgb(0x8B, 0x5C, 0xF6));
        public static readonly Brush Teal = Freeze(Color.FromRgb(0x14, 0x9E, 0x9E));

        private static Brush Freeze(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>All occurrences of one rule on a page, shown as a group header with a checkbox.</summary>
    internal sealed class RuleGroupViewModel : ObservableObject
    {
        private bool _isChecked;

        public RuleGroupViewModel(string id, string title, IssueSeverity severity, bool isFixable, int count)
        {
            Id = id;
            Title = title;
            IsFixable = isFixable;
            Count = count;
            _isChecked = isFixable;

            switch (severity)
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

        public string Id { get; }

        public string Title { get; }

        public string TitleWithCount => Count > 1 ? Title + "  ×" + Count : Title;

        public bool IsFixable { get; }

        public int Count { get; }

        public ImageMoniker Moniker { get; }

        public string TagText => IsFixable ? "Auto-fix" : "Manual";

        public Brush TagBrush => IsFixable ? Palette.Ok : Palette.Neutral;

        /// <summary>Included in "Fix ticked on page".</summary>
        public bool IsChecked
        {
            get => _isChecked;
            set => Set(ref _isChecked, value && IsFixable);
        }
    }

    internal sealed class IssueViewModel : ObservableObject
    {
        private bool _isSkipped;

        public IssueViewModel(IssueInfo info, RuleGroupViewModel group, bool isSkipped)
        {
            Info = info;
            Group = group;
            _isSkipped = isSkipped;
        }

        public IssueInfo Info { get; }

        /// <summary>Grouping key for the list (the group header binds to it).</summary>
        public RuleGroupViewModel Group { get; }

        public string LineLabel => "L" + Info.Line;

        public string Code => string.IsNullOrEmpty(Info.LineText) ? "(empty line)" : Info.LineText;

        public string Message => Info.Message;

        public bool IsSkipped
        {
            get => _isSkipped;
            set
            {
                if (Set(ref _isSkipped, value))
                {
                    Raise(nameof(StateGlyph));
                }
            }
        }

        // Segoe MDL2 "Forward" glyph marks skipped items.
        public string StateGlyph => IsSkipped ? "" : string.Empty;

        public Brush StateBrush => Palette.Neutral;
    }

    /// <summary>One page = one source file with issues.</summary>
    internal sealed class PageViewModel
    {
        public PageViewModel(string filePath, string solutionDirectory, List<IssueViewModel> issues, List<RuleGroupViewModel> groups)
        {
            FilePath = filePath;
            FileName = Path.GetFileName(filePath);
            Issues = issues;
            Groups = groups;

            string folder = Path.GetDirectoryName(filePath) ?? string.Empty;
            Folder = string.IsNullOrEmpty(solutionDirectory) ? folder : Services.PathHelper.Relative(folder, solutionDirectory);

            int fixable = issues.Count(i => i.Info.HasFix);
            Summary = string.Format(
                "{0} issue{1} · {2} auto-fixable{3}",
                issues.Count,
                issues.Count == 1 ? string.Empty : "s",
                fixable,
                string.IsNullOrEmpty(Folder) ? string.Empty : " · " + Folder);
        }

        public string FilePath { get; }

        public string FileName { get; }

        public string Folder { get; }

        public string Summary { get; }

        public List<IssueViewModel> Issues { get; }

        public List<RuleGroupViewModel> Groups { get; }

        public Microsoft.CodeAnalysis.DocumentId DocumentId => Issues.Count > 0 ? Issues[0].Info.DocumentId : null;
    }
}
