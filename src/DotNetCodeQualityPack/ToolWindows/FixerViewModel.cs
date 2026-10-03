using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using Community.VisualStudio.Toolkit;
using DotNetCodeQualityPack.Fixer;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using DocumentId = Microsoft.CodeAnalysis.DocumentId;
using RoslynSolution = Microsoft.CodeAnalysis.Solution;
using Task = System.Threading.Tasks.Task;

namespace DotNetCodeQualityPack.ToolWindows
{
    internal enum PreviewKind
    {
        None,
        Snippet,
        Single,
        Page,
        Rule,
    }

    /// <summary>
    /// Drives the Code Quality Fixer window: scan, page through files, preview fixes, apply on approval, undo.
    /// All members are used on the UI thread; heavy work runs on background threads.
    /// </summary>
    internal sealed class FixerViewModel : ObservableObject
    {
        private readonly VisualStudioWorkspace _workspace;
        private readonly FixEngine _engine = new FixEngine();
        private readonly HashSet<string> _skipped = new HashSet<string>();

        private List<IssueInfo> _allIssues = new List<IssueInfo>();
        private string _solutionDirectory = string.Empty;
        private CancellationTokenSource _busyCts;
        private int _previewVersion;
        private FixPreview _preview;
        private PreviewKind _previewKind;

        private bool _hasScanned;
        private bool _isBusy;
        private string _busyText;
        private string _statusText = "Scan the solution to find warnings you can fix.";
        private string _subtitleText = "Find and fix warnings with a preview before every change";
        private string _searchText = string.Empty;
        private int _severityFilterIndex;
        private bool _fixableOnly;
        private int _currentPageIndex = -1;
        private PageViewModel _currentPage;
        private ICollectionView _issuesView;
        private IssueViewModel _selectedIssue;
        private string _previewTitle;
        private string _previewSubtitle;
        private string _previewHint;
        private bool _isPreviewLoading;
        private string _addedText;
        private string _removedText;
        private string _applyText = "Apply fix";
        private int _fixedThisSession;

        public FixerViewModel(VisualStudioWorkspace workspace)
        {
            _workspace = workspace;

            ScanCommand = new RelayCommand(() => Run(ScanAsync), () => !IsBusy);
            CancelCommand = new RelayCommand(() => _busyCts?.Cancel(), () => IsBusy);
            PrevPageCommand = new RelayCommand(() => GoToPage(_currentPageIndex - 1), () => !IsBusy && _currentPageIndex > 0);
            NextPageCommand = new RelayCommand(() => GoToPage(_currentPageIndex + 1), () => !IsBusy && _currentPageIndex < Pages.Count - 1);
            ApplyCommand = new RelayCommand(() => Run(ApplyAsync), () => !IsBusy && _preview != null && _preview.HasChanges);
            SkipCommand = new RelayCommand(Skip, () => !IsBusy && SelectedIssue != null);
            GoToCodeCommand = new RelayCommand(() => Run(GoToCodeAsync), () => SelectedIssue != null);
            PreviewPageCommand = new RelayCommand(() => Run(PreviewPageAsync), () => !IsBusy && CurrentPage != null && CurrentPage.Groups.Any(g => g.IsChecked));
            FixRuleEverywhereCommand = new RelayCommand(() => Run(PreviewRuleEverywhereAsync), () => !IsBusy && SelectedIssue != null && SelectedIssue.Info.HasFix);
            UndoCommand = new RelayCommand(() => Run(UndoAsync), () => !IsBusy && _engine.PeekUndo() != null);
            DiscardPreviewCommand = new RelayCommand(DiscardPreview, () => !IsBusy && IsBulkPreview);
            TickAllCommand = new RelayCommand(() => SetAllTicks(true), () => CurrentPage != null);
            TickNoneCommand = new RelayCommand(() => SetAllTicks(false), () => CurrentPage != null);
        }

        // ------------------------------------------------------------------ bindable state

        public ObservableCollection<PageViewModel> Pages { get; } = new ObservableCollection<PageViewModel>();

        public ObservableCollection<DiffLine> PreviewLines { get; } = new ObservableCollection<DiffLine>();

        public ICommand ScanCommand { get; }

        public ICommand CancelCommand { get; }

        public ICommand PrevPageCommand { get; }

        public ICommand NextPageCommand { get; }

        public ICommand ApplyCommand { get; }

        public ICommand SkipCommand { get; }

        public ICommand GoToCodeCommand { get; }

        public ICommand PreviewPageCommand { get; }

        public ICommand FixRuleEverywhereCommand { get; }

        public ICommand UndoCommand { get; }

        public ICommand DiscardPreviewCommand { get; }

        public ICommand TickAllCommand { get; }

        public ICommand TickNoneCommand { get; }

        public bool HasScanned
        {
            get => _hasScanned;
            private set
            {
                if (Set(ref _hasScanned, value))
                {
                    RaiseLayout();
                }
            }
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (Set(ref _isBusy, value))
                {
                    Raise(nameof(ScanButtonText));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string BusyText
        {
            get => _busyText;
            private set => Set(ref _busyText, value);
        }

        public string StatusText
        {
            get => _statusText;
            private set => Set(ref _statusText, value);
        }

        public string SubtitleText
        {
            get => _subtitleText;
            private set => Set(ref _subtitleText, value);
        }

        public string ScanButtonText => HasScanned ? "Rescan" : "Scan";

        public int ErrorCount => _allIssues.Count(i => i.Severity == IssueSeverity.Error);

        public int WarningCount => _allIssues.Count(i => i.Severity == IssueSeverity.Warning);

        public int MessageCount => _allIssues.Count(i => i.Severity == IssueSeverity.Message);

        public int FixableCount => _allIssues.Count(i => i.HasFix);

        public int FileCount => Pages.Count;

        public bool HasErrors => ErrorCount > 0;

        public int FixedThisSession
        {
            get => _fixedThisSession;
            private set
            {
                if (Set(ref _fixedThisSession, value))
                {
                    Raise(nameof(HasFixedThisSession));
                }
            }
        }

        public bool HasFixedThisSession => FixedThisSession > 0;

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (Set(ref _searchText, value ?? string.Empty))
                {
                    Raise(nameof(ShowSearchPlaceholder));
                    RebuildPages(CurrentPage?.FilePath);
                }
            }
        }

        public bool ShowSearchPlaceholder => string.IsNullOrEmpty(SearchText);

        /// <summary>0 = all, 1 = errors and warnings, 2 = messages.</summary>
        public int SeverityFilterIndex
        {
            get => _severityFilterIndex;
            set
            {
                if (Set(ref _severityFilterIndex, value))
                {
                    RebuildPages(CurrentPage?.FilePath);
                }
            }
        }

        public bool FixableOnly
        {
            get => _fixableOnly;
            set
            {
                if (Set(ref _fixableOnly, value))
                {
                    RebuildPages(CurrentPage?.FilePath);
                }
            }
        }

        public bool ShowPages => HasScanned && Pages.Count > 0;

        public bool ShowEmptyState => !ShowPages;

        public bool ShowEmptyScanButton => !HasScanned;

        public string EmptyGlyph => HasScanned ? "" : "";

        public string EmptyTitle => !HasScanned
            ? "Review and fix warnings, one page at a time"
            : _allIssues.Count == 0 ? "No warnings left" : "Nothing matches the filters";

        public string EmptyText => !HasScanned
            ? "Scan the solution, then go through it file by file. Every fix shows a before/after preview and is only applied when you approve it."
            : _allIssues.Count == 0 ? "The analyzers found nothing to fix in this solution." : "Clear the search box or change the filters to see more.";

        public PageViewModel CurrentPage
        {
            get => _currentPage;
            private set => Set(ref _currentPage, value);
        }

        public string PageLabel => Pages.Count == 0
            ? string.Empty
            : string.Format("PAGE {0} OF {1}", _currentPageIndex + 1, Pages.Count);

        public ICollectionView IssuesView
        {
            get => _issuesView;
            private set => Set(ref _issuesView, value);
        }

        public IssueViewModel SelectedIssue
        {
            get => _selectedIssue;
            set
            {
                if (Set(ref _selectedIssue, value) && value != null)
                {
                    Run(() => LoadIssuePreviewAsync(value));
                }
            }
        }

        public string PreviewTitle
        {
            get => _previewTitle;
            private set => Set(ref _previewTitle, value);
        }

        public string PreviewSubtitle
        {
            get => _previewSubtitle;
            private set => Set(ref _previewSubtitle, value);
        }

        public string PreviewHint
        {
            get => _previewHint;
            private set
            {
                if (Set(ref _previewHint, value))
                {
                    Raise(nameof(HasPreviewHint));
                }
            }
        }

        public bool HasPreviewHint => !string.IsNullOrEmpty(PreviewHint);

        public bool IsPreviewLoading
        {
            get => _isPreviewLoading;
            private set => Set(ref _isPreviewLoading, value);
        }

        public string AddedText
        {
            get => _addedText;
            private set => Set(ref _addedText, value);
        }

        public string RemovedText
        {
            get => _removedText;
            private set => Set(ref _removedText, value);
        }

        public bool HasDiffStats => _preview != null && _preview.HasChanges;

        public string ApplyText
        {
            get => _applyText;
            private set => Set(ref _applyText, value);
        }

        /// <summary>A page or whole-rule preview is waiting for approval.</summary>
        public bool IsBulkPreview => _previewKind == PreviewKind.Page || _previewKind == PreviewKind.Rule;

        // ------------------------------------------------------------------ lifecycle

        /// <summary>Called when the solution closes.</summary>
        public void Reset()
        {
            _busyCts?.Cancel();
            _allIssues = new List<IssueInfo>();
            _skipped.Clear();
            _engine.ForgetHistory();
            FixedThisSession = 0;
            Pages.Clear();
            ClearPreview();
            CurrentPage = null;
            IssuesView = null;
            _selectedIssue = null;
            Raise(nameof(SelectedIssue));
            _currentPageIndex = -1;
            HasScanned = false;
            SubtitleText = "Find and fix warnings with a preview before every change";
            StatusText = "Scan the solution to find warnings you can fix.";
            RaiseCounts();
            RaiseLayout();
        }

        // ------------------------------------------------------------------ commands

        private async Task ScanAsync()
        {
            await RunBusyAsync("Scanning the solution...", async cancellationToken =>
            {
                RoslynSolution solution = _workspace.CurrentSolution;
                _solutionDirectory = string.IsNullOrEmpty(solution.FilePath) ? string.Empty : Path.GetDirectoryName(solution.FilePath);
                SubtitleText = string.IsNullOrEmpty(solution.FilePath)
                    ? "Current solution"
                    : Path.GetFileName(solution.FilePath);

                var progress = new Progress<string>(text => BusyText = text);
                List<IssueInfo> issues = await Task.Run(
                    () => _engine.ScanSolutionAsync(solution, progress, cancellationToken),
                    cancellationToken);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

                // A rescan starts over, including items skipped earlier.
                _skipped.Clear();
                _allIssues = issues;
                HasScanned = true;
                RebuildPages(CurrentPage?.FilePath);

                StatusText = issues.Count == 0
                    ? "No warnings found."
                    : string.Format("Found {0} issue(s) in {1} file(s); {2} can be fixed automatically.", issues.Count, Pages.Count, FixableCount);
            });
        }

        private async Task ApplyAsync()
        {
            FixPreview preview = _preview;
            if (preview == null || !preview.HasChanges)
            {
                return;
            }

            await RunBusyAsync("Applying the approved changes...", async cancellationToken =>
            {
                PreparedChange prepared = await CommitAsync(preview.Files, revert: false, cancellationToken);

                if (prepared == null || !prepared.Result.Succeeded)
                {
                    StatusText = prepared != null && prepared.Result.Conflicts.Count > 0
                        ? "The code changed after the preview, so nothing was applied. The page has been refreshed; review the new preview."
                        : "Visual Studio couldn't apply the change. Try again, or rescan.";
                    await RefreshFilesAsync(preview.Files.Select(f => f.DocumentId), cancellationToken);
                    return;
                }

                FixedThisSession += preview.FixCount;
                StatusText = string.Format(
                    "Applied {0} fix(es) in {1} file(s).{2} Undo is available at the top.",
                    preview.FixCount,
                    prepared.Result.Applied.Count,
                    prepared.Result.Conflicts.Count > 0
                        ? " Skipped " + prepared.Result.Conflicts.Count + " file(s) that changed meanwhile."
                        : string.Empty);

                string pageBefore = CurrentPage?.FilePath;
                await RefreshFilesAsync(prepared.Result.Applied.Select(c => c.DocumentId), cancellationToken);

                if (pageBefore != null && CurrentPage?.FilePath != pageBefore)
                {
                    StatusText += " Page finished, moved to the next file.";
                }
            });
        }

        private async Task UndoAsync()
        {
            IReadOnlyList<FileChange> last = _engine.PeekUndo();
            if (last == null)
            {
                return;
            }

            await RunBusyAsync("Undoing the last change...", async cancellationToken =>
            {
                PreparedChange prepared = await CommitAsync(last, revert: true, cancellationToken);

                if (prepared == null || !prepared.Result.Succeeded)
                {
                    StatusText = "Couldn't undo: the files were edited after the fix. Use Ctrl+Z in the editor instead.";
                    return;
                }

                StatusText = string.Format(
                    "Undid the last change in {0} file(s).{1}",
                    prepared.Result.Applied.Count,
                    prepared.Result.Conflicts.Count > 0 ? " Skipped files edited since then." : string.Empty);

                await RefreshFilesAsync(prepared.Result.Applied.Select(c => c.DocumentId), cancellationToken);
            });
        }

        private async Task PreviewPageAsync()
        {
            PageViewModel page = CurrentPage;
            if (page?.DocumentId == null)
            {
                return;
            }

            List<string> ruleIds = page.Groups.Where(g => g.IsChecked).Select(g => g.Id).ToList();
            if (ruleIds.Count == 0)
            {
                StatusText = "Tick at least one auto-fixable rule on this page.";
                return;
            }

            await RunBusyAsync("Preparing the fixes for this page...", async cancellationToken =>
            {
                RoslynSolution solution = _workspace.CurrentSolution;
                FixPreview preview = await Task.Run(
                    () => _engine.PreviewDocumentAsync(solution, page.DocumentId, ruleIds, cancellationToken),
                    cancellationToken);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                ClearSelectionQuietly();

                if (!preview.HasChanges)
                {
                    ClearPreview();
                    PreviewTitle = "Page preview · " + page.FileName;
                    PreviewHint = preview.Problem;
                    return;
                }

                ShowPreview(
                    preview,
                    PreviewKind.Page,
                    "Page preview · " + page.FileName,
                    string.Format("{0} fix(es) for {1}", preview.FixCount, string.Join(", ", ruleIds)),
                    "Review the changes below. Nothing is changed until you approve.",
                    string.Format("Approve & apply {0} fix(es)", preview.FixCount));
            });
        }

        private async Task PreviewRuleEverywhereAsync()
        {
            IssueViewModel issue = SelectedIssue;
            if (issue == null)
            {
                return;
            }

            string ruleId = issue.Info.Id;
            List<DocumentId> documents = _allIssues
                .Where(i => i.Id == ruleId && i.HasFix)
                .GroupBy(i => i.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First().DocumentId)
                .ToList();

            await RunBusyAsync("Preparing " + ruleId + " fixes for the whole solution...", async cancellationToken =>
            {
                RoslynSolution solution = _workspace.CurrentSolution;
                var progress = new Progress<string>(text => BusyText = text);
                FixPreview preview = await Task.Run(
                    () => _engine.PreviewRuleAsync(solution, ruleId, documents, progress, cancellationToken),
                    cancellationToken);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                ClearSelectionQuietly();

                if (!preview.HasChanges)
                {
                    ClearPreview();
                    PreviewTitle = ruleId + " · whole solution";
                    PreviewHint = preview.Problem;
                    return;
                }

                ShowPreview(
                    preview,
                    PreviewKind.Rule,
                    ruleId + " · whole solution",
                    string.Format("{0} fix(es) in {1} file(s)", preview.FixCount, preview.Files.Count),
                    "Every file that changes is listed below. Nothing is changed until you approve.",
                    string.Format("Approve & apply {0} fix(es)", preview.FixCount));
            });
        }

        private void Skip()
        {
            IssueViewModel issue = SelectedIssue;
            if (issue == null)
            {
                return;
            }

            issue.IsSkipped = true;
            _skipped.Add(issue.Info.Key);

            IssueViewModel next = NextActionable(issue);
            if (next != null)
            {
                SelectedIssue = next;
                return;
            }

            if (_currentPageIndex < Pages.Count - 1)
            {
                StatusText = "End of page, moved to the next file.";
                GoToPage(_currentPageIndex + 1);
            }
            else
            {
                StatusText = "That was the last issue. Rescan to start over, including skipped items.";
                ClearPreview();
            }
        }

        private void DiscardPreview()
        {
            IssueViewModel first = CurrentPage == null ? null : NextActionable(null);
            ClearPreview();

            if (first != null)
            {
                SelectedIssue = first;
            }

            StatusText = "Preview discarded. Nothing was changed.";
        }

        private async Task GoToCodeAsync()
        {
            IssueViewModel issue = SelectedIssue;
            if (issue == null || string.IsNullOrEmpty(issue.Info.FilePath))
            {
                return;
            }

            DocumentView view = await VS.Documents.OpenAsync(issue.Info.FilePath);
            IWpfTextView textView = view?.TextView;
            if (textView == null)
            {
                return;
            }

            ITextSnapshot snapshot = textView.TextSnapshot;
            int lineNumber = Math.Max(0, Math.Min(issue.Info.Line - 1, snapshot.LineCount - 1));
            ITextSnapshotLine line = snapshot.GetLineFromLineNumber(lineNumber);

            textView.Caret.MoveTo(line.Start);
            textView.ViewScroller.EnsureSpanVisible(new SnapshotSpan(line.Start, line.End), EnsureSpanVisibleOptions.AlwaysCenter);
            textView.VisualElement.Focus();
        }

        private void SetAllTicks(bool value)
        {
            if (CurrentPage == null)
            {
                return;
            }

            foreach (RuleGroupViewModel group in CurrentPage.Groups)
            {
                group.IsChecked = value;
            }

            CommandManager.InvalidateRequerySuggested();
        }

        // ------------------------------------------------------------------ pages

        private void RebuildPages(string keepFilePath)
        {
            string search = SearchText.Trim();

            IEnumerable<IssueInfo> filtered = _allIssues;
            if (SeverityFilterIndex == 1)
            {
                filtered = filtered.Where(i => i.Severity != IssueSeverity.Message);
            }
            else if (SeverityFilterIndex == 2)
            {
                filtered = filtered.Where(i => i.Severity == IssueSeverity.Message);
            }

            if (FixableOnly)
            {
                filtered = filtered.Where(i => i.HasFix);
            }

            if (search.Length > 0)
            {
                filtered = filtered.Where(i =>
                    Contains(i.Id, search) || Contains(i.Message, search) || Contains(i.RuleTitle, search) || Contains(i.FilePath, search));
            }

            List<PageViewModel> pages = filtered
                .GroupBy(i => i.FilePath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(BuildPage)
                .ToList();

            Pages.Clear();
            foreach (PageViewModel page in pages)
            {
                Pages.Add(page);
            }

            int index = 0;
            if (keepFilePath != null)
            {
                int kept = pages.FindIndex(p => string.Equals(p.FilePath, keepFilePath, StringComparison.OrdinalIgnoreCase));
                if (kept >= 0)
                {
                    index = kept;
                }
                else
                {
                    // The page we were on is finished: stay at the same position, which is now the next file.
                    index = Math.Min(Math.Max(_currentPageIndex, 0), Math.Max(pages.Count - 1, 0));
                }
            }

            RaiseCounts();
            RaiseLayout();

            if (pages.Count == 0)
            {
                _currentPageIndex = -1;
                CurrentPage = null;
                IssuesView = null;
                ClearPreview();
                Raise(nameof(PageLabel));
                return;
            }

            GoToPage(index);
        }

        private PageViewModel BuildPage(IGrouping<string, IssueInfo> file)
        {
            var groups = new List<RuleGroupViewModel>();
            var issues = new List<IssueViewModel>();

            IEnumerable<IGrouping<string, IssueInfo>> byRule = file
                .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.First().HasFix ? 0 : 1)
                .ThenBy(g => g.Min(i => i.Line));

            foreach (IGrouping<string, IssueInfo> rule in byRule)
            {
                IssueInfo first = rule.First();
                var group = new RuleGroupViewModel(
                    first.Id,
                    string.IsNullOrWhiteSpace(first.RuleTitle) ? first.Message : first.RuleTitle,
                    rule.Min(i => i.Severity),
                    first.HasFix,
                    rule.Count());
                group.PropertyChanged += (s, e) => CommandManager.InvalidateRequerySuggested();
                groups.Add(group);

                foreach (IssueInfo issue in rule.OrderBy(i => i.Line))
                {
                    issues.Add(new IssueViewModel(issue, group, _skipped.Contains(issue.Key)));
                }
            }

            return new PageViewModel(file.Key, _solutionDirectory, issues, groups);
        }

        private void GoToPage(int index)
        {
            if (index < 0 || index >= Pages.Count)
            {
                return;
            }

            _currentPageIndex = index;
            PageViewModel page = Pages[index];
            CurrentPage = page;
            Raise(nameof(PageLabel));

            var view = new ListCollectionView(page.Issues);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(IssueViewModel.Group)));
            IssuesView = view;

            ClearPreview();
            _selectedIssue = null;
            Raise(nameof(SelectedIssue));

            IssueViewModel first = NextActionable(null);
            if (first != null)
            {
                SelectedIssue = first;
            }
            else
            {
                PreviewTitle = page.FileName;
                PreviewHint = "Everything on this page was skipped. Use the arrows to move on, or rescan.";
            }

            CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>Next issue on the page after <paramref name="after"/> that isn't skipped; auto-fixable ones first.</summary>
        private IssueViewModel NextActionable(IssueViewModel after)
        {
            if (CurrentPage == null)
            {
                return null;
            }

            List<IssueViewModel> issues = CurrentPage.Issues;
            int start = after == null ? 0 : issues.IndexOf(after) + 1;

            for (int i = start; i < issues.Count; i++)
            {
                if (!issues[i].IsSkipped)
                {
                    return issues[i];
                }
            }

            return null;
        }

        /// <summary>Re-analyzes changed files and refreshes the pages, staying on the current file where possible.</summary>
        private async Task RefreshFilesAsync(IEnumerable<DocumentId> documentIds, CancellationToken cancellationToken)
        {
            RoslynSolution solution = _workspace.CurrentSolution;
            var fresh = new List<IssueInfo>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (DocumentId id in documentIds.Distinct())
            {
                Microsoft.CodeAnalysis.Document document = solution.GetDocument(id);
                if (document?.FilePath == null || !paths.Add(document.FilePath))
                {
                    continue;
                }

                fresh.AddRange(await Task.Run(() => _engine.ScanDocumentAsync(solution, id, cancellationToken), cancellationToken));
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            _allIssues = _allIssues
                .Where(i => !paths.Contains(i.FilePath))
                .Concat(fresh)
                .ToList();

            RebuildPages(CurrentPage?.FilePath);
        }

        // ------------------------------------------------------------------ previews

        private async Task LoadIssuePreviewAsync(IssueViewModel issue)
        {
            // Clear first: ClearPreview bumps the version to cancel older loads, so take our version after it.
            ClearPreview();
            int version = ++_previewVersion;

            IssueInfo info = issue.Info;
            PreviewTitle = info.Id + " · " + (string.IsNullOrWhiteSpace(info.RuleTitle) ? info.Message : info.RuleTitle);
            PreviewSubtitle = Path.GetFileName(info.FilePath) + " · line " + info.Line;
            IsPreviewLoading = true;

            try
            {
                RoslynSolution solution = _workspace.CurrentSolution;
                string hint = null;

                if (info.HasFix)
                {
                    FixPreview preview = await Task.Run(() => _engine.PreviewIssueAsync(solution, info, CancellationToken.None));
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (version != _previewVersion)
                    {
                        return;
                    }

                    if (preview.HasChanges)
                    {
                        ShowPreview(
                            preview,
                            PreviewKind.Single,
                            PreviewTitle,
                            PreviewSubtitle + " · fix: " + preview.Title,
                            info.Message,
                            "Apply fix");
                        return;
                    }

                    hint = preview.Problem;
                }

                List<DiffLine> snippet = await Task.Run(() => _engine.GetSnippetAsync(solution, info, CancellationToken.None));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (version != _previewVersion)
                {
                    return;
                }

                _previewKind = PreviewKind.Snippet;
                SetLines(snippet);
                PreviewHint = (hint ?? "No automatic fix for this rule: use Go to code to fix it by hand, or Skip.") + "\n" + info.Message;
            }
            catch (Exception ex)
            {
                if (version == _previewVersion)
                {
                    PreviewHint = "Couldn't prepare a preview: " + ex.Message;
                }
            }
            finally
            {
                if (version == _previewVersion)
                {
                    IsPreviewLoading = false;
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        private void ShowPreview(FixPreview preview, PreviewKind kind, string title, string subtitle, string hint, string applyText)
        {
            IsPreviewLoading = false;
            _preview = preview;
            _previewKind = kind;
            PreviewTitle = title;
            PreviewSubtitle = subtitle;
            PreviewHint = hint;
            ApplyText = applyText;

            var lines = new List<DiffLine>();
            int added = 0;
            int removed = 0;

            foreach (FileChange file in preview.Files)
            {
                added += file.Added;
                removed += file.Removed;

                if (preview.Files.Count > 1 || kind != PreviewKind.Single)
                {
                    lines.Add(new DiffLine(
                        DiffKind.File,
                        null,
                        null,
                        string.Format("{0}    +{1} −{2}", Relative(file.FilePath), file.Added, file.Removed)));
                }

                lines.AddRange(file.Diff);
            }

            SetLines(lines);
            AddedText = "+" + added;
            RemovedText = "−" + removed;
            Raise(nameof(HasDiffStats));
            Raise(nameof(IsBulkPreview));
            CommandManager.InvalidateRequerySuggested();
        }

        private void ClearPreview()
        {
            _previewVersion++;
            _preview = null;
            _previewKind = PreviewKind.None;
            PreviewLines.Clear();
            PreviewTitle = null;
            PreviewSubtitle = null;
            PreviewHint = null;
            AddedText = null;
            RemovedText = null;
            ApplyText = "Apply fix";
            IsPreviewLoading = false;
            Raise(nameof(HasDiffStats));
            Raise(nameof(IsBulkPreview));
        }

        private void SetLines(IEnumerable<DiffLine> lines)
        {
            PreviewLines.Clear();
            foreach (DiffLine line in lines)
            {
                PreviewLines.Add(line);
            }
        }

        private void ClearSelectionQuietly()
        {
            // Also cancel any single-issue preview still loading, so it can't overwrite the page/rule preview.
            _previewVersion++;
            IsPreviewLoading = false;
            _selectedIssue = null;
            Raise(nameof(SelectedIssue));
        }

        // ------------------------------------------------------------------ helpers

        private async Task<PreparedChange> CommitAsync(IReadOnlyList<FileChange> files, bool revert, CancellationToken cancellationToken)
        {
            PreparedChange prepared = null;

            // The workspace can move on between preparing and committing (for example a background project update),
            // so try a few times against the latest solution.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                RoslynSolution current = _workspace.CurrentSolution;
                prepared = await Task.Run(() => _engine.PrepareAsync(current, files, revert, cancellationToken), cancellationToken);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                if (prepared.Result.Applied.Count == 0 || _engine.Commit(_workspace, prepared))
                {
                    break;
                }
            }

            return prepared;
        }

        private async Task RunBusyAsync(string text, Func<CancellationToken, Task> work)
        {
            if (IsBusy)
            {
                return;
            }

            IsBusy = true;
            BusyText = text;
            _busyCts = new CancellationTokenSource();

            try
            {
                await work(_busyCts.Token);
            }
            catch (OperationCanceledException)
            {
                StatusText = "Cancelled.";
            }
            catch (Exception ex)
            {
                StatusText = "Something went wrong: " + ex.Message;
                await ex.LogAsync();
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _busyCts.Dispose();
                _busyCts = null;
                IsBusy = false;
            }
        }

        private static void Run(Func<Task> action)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await action();
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();
                }
            });
        }

        private string Relative(string path)
        {
            return string.IsNullOrEmpty(_solutionDirectory) ? path : Services.PathHelper.Relative(path, _solutionDirectory);
        }

        private static bool Contains(string text, string search)
        {
            return text != null && text.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void RaiseCounts()
        {
            Raise(nameof(ErrorCount));
            Raise(nameof(WarningCount));
            Raise(nameof(MessageCount));
            Raise(nameof(FixableCount));
            Raise(nameof(FileCount));
            Raise(nameof(HasErrors));
        }

        private void RaiseLayout()
        {
            Raise(nameof(ShowPages));
            Raise(nameof(ShowEmptyState));
            Raise(nameof(ShowEmptyScanButton));
            Raise(nameof(EmptyGlyph));
            Raise(nameof(EmptyTitle));
            Raise(nameof(EmptyText));
            Raise(nameof(ScanButtonText));
        }
    }
}
