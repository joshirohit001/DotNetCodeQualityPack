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
using System.Windows.Media;
using Community.VisualStudio.Toolkit;
using DotNetCodeQualityPack.Ai;
using DotNetCodeQualityPack.Fixer;
using DotNetCodeQualityPack.Rules;
using DotNetCodeQualityPack.Services;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using RoslynDocument = Microsoft.CodeAnalysis.Document;
using RoslynSolution = Microsoft.CodeAnalysis.Solution;
using SourceText = Microsoft.CodeAnalysis.Text.SourceText;
using Task = System.Threading.Tasks.Task;

namespace DotNetCodeQualityPack.ToolWindows
{
    /// <summary>
    /// Drives the Team Rules window: load the Markdown rules file, sync it to .editorconfig, run custom rules and
    /// the AI review, and apply fixes after a preview. Used on the UI thread; heavy work runs in the background.
    /// </summary>
    internal sealed class TeamRulesViewModel : ObservableObject
    {
        private const string GroupOverrides = "Severity overrides";
        private const string GroupSettings = "EditorConfig settings";
        private const string GroupCustom = "Custom rules";
        private const string GroupGuidelines = "AI review guidelines";

        private static readonly string[] DefaultRuleFiles =
        {
            "CodeQualityRules.md",
            "TeamRules.md",
            "code-quality-rules.md",
            ".codequality/rules.md",
            "docs/CodeQualityRules.md",
            "docs/standards/CodeQualityRules.md",
        };

        private readonly VisualStudioWorkspace _workspace;
        private readonly FixEngine _engine = new FixEngine();
        private readonly AiReviewClient _ai = new AiReviewClient();
        private readonly PackSettings _settings;
        private readonly List<RuleFinding> _findings = new List<RuleFinding>();

        private RulesDocument _rules;
        private InstructionSet _copilot;
        private FileSystemWatcher _copilotWatcher;
        private string _rulesPath;
        private string _solutionPath;
        private string _solutionDirectory;
        private FileSystemWatcher _watcher;
        private CancellationTokenSource _busyCts;
        private int _previewVersion;
        private FixPreview _preview;
        private List<RuleFinding> _previewFindings = new List<RuleFinding>();

        private bool _isBusy;
        private string _busyText;
        private string _statusText = "Open a solution to load its team rules.";
        private string _subtitleText = "Markdown rules for your team";
        private bool _isFindingsTab;
        private FindingRowViewModel _selectedFinding;
        private string _previewTitle;
        private string _previewSubtitle;
        private string _previewHint;
        private bool _isPreviewLoading;
        private string _addedText;
        private string _removedText;
        private string _applyText = "Apply fix";
        private AiModel _selectedModel;
        private string _aiStatusText = "Models not loaded yet.";
        private bool _showAiSettings;
        private string _editorConfigStatusText = string.Empty;
        private Brush _editorConfigStatusBrush = Palette.Neutral;
        private readonly TokenUsage _sessionUsage = new TokenUsage();
        private TokenUsage _lastUsage;
        private string _lastUsageModel;
        private string _lastUsageFile;
        private bool _showUsageDetails;

        public TeamRulesViewModel(VisualStudioWorkspace workspace)
        {
            _workspace = workspace;
            _settings = PackSettings.Load();

            var rulesView = new ListCollectionView(RuleRows);
            rulesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RuleRowViewModel.Group)));
            RulesView = rulesView;

            var findingsView = new ListCollectionView(FindingRows);
            findingsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FindingRowViewModel.FileGroup)));
            findingsView.SortDescriptions.Add(new SortDescription(nameof(FindingRowViewModel.FileGroup), ListSortDirection.Ascending));
            findingsView.SortDescriptions.Add(new SortDescription(nameof(FindingRowViewModel.LineNumber), ListSortDirection.Ascending));
            FindingsView = findingsView;

            BrowseCommand = new RelayCommand(Browse, () => !IsBusy);
            OpenRulesCommand = new RelayCommand(() => Run(OpenRulesAsync), () => HasRulesFile);
            NewRulesCommand = new RelayCommand(() => Run(NewRulesAsync), () => !IsBusy && _solutionDirectory != null);
            ReloadCommand = new RelayCommand(() => Run(() => LoadForSolutionAsync(keepFile: true)), () => !IsBusy);
            ApplyEditorConfigCommand = new RelayCommand(() => Run(ApplyEditorConfigAsync), () => !IsBusy && _rules != null && _solutionDirectory != null);
            RemoveEditorConfigCommand = new RelayCommand(() => Run(RemoveEditorConfigAsync), () => !IsBusy && _solutionDirectory != null);
            CheckCustomCommand = new RelayCommand(() => Run(CheckCustomAsync), () => !IsBusy && _rules != null);
            AiReviewCommand = new RelayCommand(() => Run(AiReviewActiveFileAsync), () => !IsBusy && HasAnyRules);
            LoadModelsCommand = new RelayCommand(() => Run(() => LoadModelsAsync(quiet: false)), () => !IsBusy);
            ToggleAiSettingsCommand = new RelayCommand(() => ShowAiSettings = !ShowAiSettings);
            ApplyFixCommand = new RelayCommand(() => Run(ApplyAsync), () => !IsBusy && _preview != null && _preview.HasChanges);
            FixAllRuleCommand = new RelayCommand(() => Run(PreviewRuleAsync), () => !IsBusy && SelectedFinding != null && SelectedFinding.Finding.HasFix);
            GoToCodeCommand = new RelayCommand(() => Run(GoToCodeAsync), () => SelectedFinding != null);
            OpenRuleCommand = new RelayCommand(
                () => Run(() => OpenAtLineAsync(SelectedFinding?.Finding.RuleSourceFile, SelectedFinding?.Finding.RuleSourceLine ?? 0)),
                () => !string.IsNullOrEmpty(SelectedFinding?.Finding.RuleSourceFile));
            DismissCommand = new RelayCommand(Dismiss, () => !IsBusy && SelectedFinding != null);
            ClearFindingsCommand = new RelayCommand(ClearFindings, () => !IsBusy && _findings.Count > 0);
            UndoCommand = new RelayCommand(() => Run(UndoAsync), () => !IsBusy && _engine.PeekUndo() != null);
            CancelCommand = new RelayCommand(() => _busyCts?.Cancel(), () => IsBusy);
            ResetUsageCommand = new RelayCommand(() => Run(ResetUsageAsync), () => !IsBusy && (_settings.AllRequests > 0 || !_sessionUsage.IsEmpty));
            RebuildUsage();
        }

        // ------------------------------------------------------------------ bindable state

        public ObservableCollection<RuleRowViewModel> RuleRows { get; } = new ObservableCollection<RuleRowViewModel>();

        public ObservableCollection<FindingRowViewModel> FindingRows { get; } = new ObservableCollection<FindingRowViewModel>();

        public ObservableCollection<string> Problems { get; } = new ObservableCollection<string>();

        public ObservableCollection<AiModel> Models { get; } = new ObservableCollection<AiModel>();

        public ObservableCollection<DiffLine> PreviewLines { get; } = new ObservableCollection<DiffLine>();

        public ICollectionView RulesView { get; }

        public ICollectionView FindingsView { get; }

        public ICommand BrowseCommand { get; }

        public ICommand OpenRulesCommand { get; }

        public ICommand NewRulesCommand { get; }

        public ICommand ReloadCommand { get; }

        public ICommand ApplyEditorConfigCommand { get; }

        public ICommand RemoveEditorConfigCommand { get; }

        public ICommand CheckCustomCommand { get; }

        public ICommand AiReviewCommand { get; }

        public ICommand LoadModelsCommand { get; }

        public ICommand ToggleAiSettingsCommand { get; }

        public ICommand ApplyFixCommand { get; }

        public ICommand FixAllRuleCommand { get; }

        public ICommand GoToCodeCommand { get; }

        /// <summary>Opens the .md file (rules file or Copilot instruction file) at the line the finding's rule is written on.</summary>
        public ICommand OpenRuleCommand { get; }

        public ICommand DismissCommand { get; }

        public ICommand ClearFindingsCommand { get; }

        public ICommand UndoCommand { get; }

        public ICommand CancelCommand { get; }

        public ICommand ResetUsageCommand { get; }

        // ------------------------------------------------------------------ token usage

        public ObservableCollection<UsageRowViewModel> UsageRows { get; } = new ObservableCollection<UsageRowViewModel>();

        public bool ShowUsageDetails
        {
            get => _showUsageDetails;
            set => Set(ref _showUsageDetails, value);
        }

        /// <summary>One line for the collapsed section, e.g. "Token usage · last review 4.2k · today 15.5k".</summary>
        public string UsageHeader
        {
            get
            {
                long today = _settings.TodayInput + _settings.TodayOutput;
                if (_lastUsage == null && today == 0 && _settings.AllRequests == 0)
                {
                    return "Token usage · no AI reviews yet";
                }

                var parts = new List<string> { "Token usage" };
                if (_lastUsage != null)
                {
                    parts.Add("last review " + TokenUsage.Short(_lastUsage.Total));
                }

                parts.Add("today " + TokenUsage.Short(today));
                return string.Join(" · ", parts);
            }
        }

        public string UsageByModelText
        {
            get
            {
                if (_settings.ModelUsage.Count == 0)
                {
                    return "Counts come from each model server's reply. Nothing is sent anywhere else.";
                }

                return "All time by model: " + string.Join(
                    " · ",
                    _settings.ModelUsage
                        .OrderByDescending(p => p.Value[0] + p.Value[1])
                        .Take(6)
                        .Select(p => p.Key + " " + TokenUsage.Short(p.Value[0] + p.Value[1]) + " (" + p.Value[2] + " req.)"));
            }
        }

        public bool HasEstimatedUsage => (_lastUsage?.EstimatedRequests ?? 0) > 0 || _sessionUsage.EstimatedRequests > 0 || _settings.AllEstimatedRequests > 0;

        public string EstimatedUsageText => "≈ Some requests got no token counts from the server, so they were estimated at about 4 characters per token.";

        private void RebuildUsage()
        {
            UsageRows.Clear();
            UsageRows.Add(new UsageRowViewModel(
                "Last review",
                _lastUsage == null ? "No review yet in this session" : _lastUsageModel + " · " + _lastUsageFile,
                _lastUsage?.Input ?? 0,
                _lastUsage?.Output ?? 0,
                _lastUsage?.Requests ?? 0,
                (_lastUsage?.EstimatedRequests ?? 0) > 0));
            UsageRows.Add(new UsageRowViewModel(
                "Today",
                DateTime.Now.ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture),
                _settings.TodayInput,
                _settings.TodayOutput,
                _settings.TodayRequests,
                false));
            UsageRows.Add(new UsageRowViewModel(
                "This session",
                "Since Visual Studio started",
                _sessionUsage.Input,
                _sessionUsage.Output,
                _sessionUsage.Requests,
                _sessionUsage.EstimatedRequests > 0));
            UsageRows.Add(new UsageRowViewModel(
                "All time",
                string.IsNullOrEmpty(_settings.UsageSince) ? "On this computer" : "Since " + _settings.UsageSince,
                _settings.AllInput,
                _settings.AllOutput,
                _settings.AllRequests,
                _settings.AllEstimatedRequests > 0));

            Raise(nameof(UsageHeader));
            Raise(nameof(UsageByModelText));
            Raise(nameof(HasEstimatedUsage));
            CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>Called on the UI thread after every AI review, including failed or cancelled ones.</summary>
        private void RecordUsage(AiModel model, string fileName, TokenUsage usage)
        {
            if (usage == null || usage.IsEmpty)
            {
                return;
            }

            _lastUsage = usage.Snapshot();
            _lastUsageModel = model?.Name ?? "AI";
            _lastUsageFile = fileName;
            _sessionUsage.Add(_lastUsage);
            _settings.RecordUsage(_lastUsageModel, _lastUsage);
            RebuildUsage();
        }

        private static string UsageSentence(TokenUsage usage)
        {
            return usage == null || usage.IsEmpty
                ? string.Empty
                : string.Format(
                    " Tokens: {0}{1} in, {2} out ({3} request{4}).",
                    usage.EstimatedRequests > 0 ? "≈" : string.Empty,
                    TokenUsage.Full(usage.Input),
                    TokenUsage.Full(usage.Output),
                    usage.Requests,
                    usage.Requests == 1 ? string.Empty : "s");
        }

        private async Task ResetUsageAsync()
        {
            bool reset = await VS.MessageBox.ShowConfirmAsync(
                "Team Rules",
                "Reset the token counters (today, this session and all time)? The AI reviews themselves are not affected.");
            if (!reset)
            {
                return;
            }

            _settings.ResetUsage();
            _sessionUsage.Clear();
            _lastUsage = null;
            RebuildUsage();
            StatusText = "Token counters reset.";
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (Set(ref _isBusy, value))
                {
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

        public bool HasRulesFile => _rules != null;

        /// <summary>A rules .md file and/or Copilot instruction files were found.</summary>
        public bool HasAnyRules => _rules != null || _copilot != null;

        public bool HasCopilot => _copilot != null;

        /// <summary>Copilot instruction files exist but there's no CodeQualityRules.md yet.</summary>
        public bool ShowCreateRulesLink => _rules == null && _copilot != null;

        public int CopilotRuleCount => _copilot == null
            ? 0
            : _copilot.Files.Where(f => !_settings.DisabledInstructionFiles.Contains(f.Name)).Sum(f => f.ReviewableCount);

        public ObservableCollection<InstructionFileOption> InstructionFiles { get; } = new ObservableCollection<InstructionFileOption>();

        public bool UseCopilotInstructions
        {
            get => _settings.UseCopilotInstructions;
            set
            {
                _settings.UseCopilotInstructions = value;
                _settings.Save();
                Raise(nameof(UseCopilotInstructions));
            }
        }

        public bool ShowNoRulesState => !HasAnyRules;

        public bool HasSolution => _solutionDirectory != null;

        public string NoRulesTitle => HasSolution ? "No rules file yet" : "No solution open";

        public string NoRulesText => HasSolution
            ? "Write your team's rules in a Markdown file: severity overrides, .editorconfig settings, custom pattern rules and AI review guidelines. GitHub Copilot instruction files in .github/instructions are picked up automatically. Start from the template, or choose an existing .md file."
            : "Open a solution, then come back here to load its Markdown rules file.";

        public int OverrideCount => _rules?.Overrides.Count ?? 0;

        public int SettingCount => _rules?.Settings.Count ?? 0;

        public int CustomCount => _rules?.CustomRules.Count ?? 0;

        public int GuidelineCount => _rules?.Guidelines.Count ?? 0;

        public int RuleRowCount => RuleRows.Count;

        public int FindingCount => _findings.Count;

        public bool HasFindings => _findings.Count > 0;

        public bool ShowFindingsEmpty => !HasFindings;

        public bool HasProblems => Problems.Count > 0;

        public string ProblemsSummary => Problems.Count == 1
            ? "1 problem in the rules file"
            : Problems.Count + " problems in the rules file";

        public string EditorConfigStatusText
        {
            get => _editorConfigStatusText;
            private set => Set(ref _editorConfigStatusText, value);
        }

        public Brush EditorConfigStatusBrush
        {
            get => _editorConfigStatusBrush;
            private set => Set(ref _editorConfigStatusBrush, value);
        }

        public string FindingsSummary
        {
            get
            {
                int custom = _findings.Count(f => f.Source == FindingSource.Custom);
                int ai = _findings.Count(f => f.Source == FindingSource.Ai);
                int fixable = _findings.Count(f => f.HasFix);
                return string.Format("{0} from rules · {1} from AI · {2} with a fix", custom, ai, fixable);
            }
        }

        public bool IsRulesTab
        {
            get => !_isFindingsTab;
            set
            {
                if (value)
                {
                    SetTab(findings: false);
                }
            }
        }

        public bool IsFindingsTab
        {
            get => _isFindingsTab;
            set
            {
                if (value)
                {
                    SetTab(findings: true);
                }
            }
        }

        public bool ShowRulesContent => HasAnyRules && !_isFindingsTab;

        public bool ShowFindingsContent => HasAnyRules && _isFindingsTab;

        public FindingRowViewModel SelectedFinding
        {
            get => _selectedFinding;
            set
            {
                if (Set(ref _selectedFinding, value) && value != null)
                {
                    Run(() => LoadFindingPreviewAsync(value));
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

        public AiModel SelectedModel
        {
            get => _selectedModel;
            set
            {
                if (Set(ref _selectedModel, value) && value != null)
                {
                    _settings.SelectedModel = value.Name;
                    _settings.Save();
                    Raise(nameof(AiPrivacyText));
                    Raise(nameof(IsRemoteModel));
                    Raise(nameof(IsLocalModel));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string AiStatusText
        {
            get => _aiStatusText;
            private set => Set(ref _aiStatusText, value);
        }

        public bool ShowAiSettings
        {
            get => _showAiSettings;
            set => Set(ref _showAiSettings, value);
        }

        public string AiPrivacyText
        {
            get
            {
                if (SelectedModel == null)
                {
                    return "Choose AI providers to enable AI review. Nothing is sent anywhere until you run a review.";
                }

                string text = "AI review sends the active file's code and your guidelines to " + AiProviders.Destination(SelectedModel) +
                              ". Nothing is changed until you approve a fix.";
                return IsRemoteModel
                    ? text + " The code leaves this PC: use a provider your organization allows for this code."
                    : text;
            }
        }

        /// <summary>The selected model runs on another machine (code leaves this PC).</summary>
        public bool IsRemoteModel => SelectedModel != null && !AiProviders.IsLocal(SelectedModel);

        public bool IsLocalModel => !IsRemoteModel;

        /// <summary>"Using OpenAI, Ollama." or a hint when nothing is set up.</summary>
        public string ProvidersSummary
        {
            get
            {
                List<string> enabled = AiProviderSettings.Load().Enabled.Select(p => AiProviders.Get(p.Kind).DisplayName).ToList();
                return enabled.Count == 0
                    ? "No provider set up yet. Choose OpenAI, Anthropic or Ollama with your own API key and models."
                    : "Using " + string.Join(", ", enabled) + ". Shared with Code Compass.";
            }
        }

        /// <summary>Called after the user saved AI provider settings.</summary>
        public void OnProvidersChanged()
        {
            Raise(nameof(ProvidersSummary));
            Run(() => LoadModelsAsync(quiet: false));
        }

        // ------------------------------------------------------------------ lifecycle

        /// <summary>Called when the window is created and whenever a solution opens.</summary>
        public void Initialize()
        {
            Run(async () =>
            {
                await LoadForSolutionAsync(keepFile: false);
                if (Models.Count == 0)
                {
                    await LoadModelsAsync(quiet: true);
                }
            });
        }

        public void OnSolutionClosed()
        {
            _busyCts?.Cancel();
            StopWatching();
            StopWatchingCopilot();
            _solutionPath = null;
            _solutionDirectory = null;
            _rulesPath = null;
            _rules = null;
            _copilot = null;
            InstructionFiles.Clear();
            Problems.Clear();
            _findings.Clear();
            _engine.ForgetHistory();
            RebuildRuleRows();
            RebuildFindingRows();
            ClearPreview();
            SubtitleText = "Markdown rules for your team";
            StatusText = "Open a solution to load its team rules.";
            RaiseRulesState();
        }

        // ------------------------------------------------------------------ rules file

        private async Task LoadForSolutionAsync(bool keepFile)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            Solution solution = await VS.Solutions.GetCurrentSolutionAsync();
            string solutionPath = solution?.FullPath;
            if (string.IsNullOrEmpty(solutionPath) || !File.Exists(solutionPath))
            {
                OnSolutionClosed();
                return;
            }

            bool sameSolution = string.Equals(solutionPath, _solutionPath, StringComparison.OrdinalIgnoreCase);
            _solutionPath = solutionPath;
            _solutionDirectory = Path.GetDirectoryName(solutionPath);

            // GitHub Copilot instruction files (.github/instructions) from the solution folder up to the repository root.
            LoadCopilot();

            string path = keepFile && sameSolution && _rulesPath != null && File.Exists(_rulesPath) ? _rulesPath : null;

            if (path == null && _settings.RulesFiles.TryGetValue(solutionPath, out string saved) && File.Exists(saved))
            {
                path = saved;
            }

            if (path == null)
            {
                IEnumerable<string> roots = new[] { _solutionDirectory, _copilot?.RootDirectory }
                    .Where(r => r != null)
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                path = roots
                    .SelectMany(root => DefaultRuleFiles.Select(name => Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar))))
                    .FirstOrDefault(File.Exists);
            }

            if (!sameSolution)
            {
                _findings.Clear();
                _engine.ForgetHistory();
                RebuildFindingRows();
                ClearPreview();
            }

            LoadRules(path);
        }

        private void LoadRules(string path)
        {
            _rulesPath = path;

            if (path == null || !File.Exists(path))
            {
                _rules = null;
                StopWatching();
                SubtitleText = _copilot != null
                    ? "Copilot instructions · " + _copilot.Files.Count + " file(s)"
                    : _solutionDirectory == null ? "Markdown rules for your team" : "No rules file for this solution";
                StatusText = _solutionDirectory == null
                    ? "Open a solution to load its team rules."
                    : _copilot != null
                        ? CopilotSummary() + " Create CodeQualityRules.md for severity overrides and custom rules."
                        : "Create a rules file from the template, or choose an existing .md file.";
                RebuildProblems();
                RebuildRuleRows();
                RaiseRulesState();
                return;
            }

            try
            {
                _rules = RulesDocument.Load(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _rules = null;
                StatusText = "Couldn't read " + Path.GetFileName(path) + ": " + ex.Message;
                RebuildProblems();
                RebuildRuleRows();
                RaiseRulesState();
                return;
            }

            SubtitleText = (_solutionDirectory == null ? path : PathHelper.Relative(path, _solutionDirectory))
                + (_copilot != null ? " + Copilot instructions" : string.Empty);
            StatusText = (_rules.IsEmpty
                    ? "The rules file has no rules yet. See the template for the sections it understands."
                    : string.Format(
                        "Loaded {0}: {1} override(s), {2} setting(s), {3} custom rule(s), {4} AI guideline(s).",
                        Path.GetFileName(path),
                        _rules.Overrides.Count,
                        _rules.Settings.Count,
                        _rules.CustomRules.Count,
                        _rules.Guidelines.Count))
                + (_copilot != null ? " " + CopilotSummary() : string.Empty);

            RebuildProblems();
            RebuildRuleRows();
            Watch(path);
            RaiseRulesState();
        }

        // ------------------------------------------------------------------ Copilot instruction files

        private void LoadCopilot()
        {
            StopWatchingCopilot();
            _copilot = null;

            try
            {
                _copilot = InstructionSet.Discover(_solutionDirectory);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _copilot = null;
            }

            InstructionFiles.Clear();
            if (_copilot == null)
            {
                return;
            }

            foreach (InstructionFile file in _copilot.Files)
            {
                InstructionFiles.Add(new InstructionFileOption(
                    file.Name,
                    file.Description,
                    file.ReviewableCount,
                    string.Join(", ", file.ApplyTo),
                    !_settings.DisabledInstructionFiles.Contains(file.Name),
                    OnInstructionFileToggled));
            }

            try
            {
                _copilotWatcher = new FileSystemWatcher(_copilot.InstructionsDirectory, "*.md")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                _copilotWatcher.Changed += OnCopilotFilesChanged;
                _copilotWatcher.Created += OnCopilotFilesChanged;
                _copilotWatcher.Deleted += OnCopilotFilesChanged;
                _copilotWatcher.Renamed += OnCopilotFilesChanged;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException)
            {
                _copilotWatcher = null;
            }
        }

        private void StopWatchingCopilot()
        {
            if (_copilotWatcher != null)
            {
                _copilotWatcher.EnableRaisingEvents = false;
                _copilotWatcher.Dispose();
                _copilotWatcher = null;
            }
        }

        private void OnCopilotFilesChanged(object sender, FileSystemEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await Task.Delay(500);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!IsBusy && _solutionDirectory != null)
                {
                    LoadCopilot();
                    LoadRules(_rulesPath);
                    StatusText = "Copilot instruction files changed; reloaded. " + StatusText;
                }
            });
        }

        private void OnInstructionFileToggled(InstructionFileOption option)
        {
            if (option.IsEnabled)
            {
                _settings.DisabledInstructionFiles.Remove(option.Name);
            }
            else
            {
                _settings.DisabledInstructionFiles.Add(option.Name);
            }

            _settings.Save();
            Raise(nameof(CopilotRuleCount));
        }

        private string CopilotSummary()
        {
            return _copilot == null
                ? string.Empty
                : string.Format(
                    "Copilot instructions: {0} file(s), {1} reviewable rule(s) in {2}.",
                    _copilot.Files.Count,
                    _copilot.ReviewableCount,
                    PathHelper.Relative(_copilot.InstructionsDirectory, _solutionDirectory ?? _copilot.RootDirectory));
        }

        private void RebuildProblems()
        {
            Problems.Clear();

            if (_rules != null)
            {
                foreach (RulesProblem problem in _rules.Problems)
                {
                    Problems.Add(problem.ToString());
                }
            }

            if (_copilot != null)
            {
                foreach (RulesProblem problem in _copilot.Problems)
                {
                    Problems.Add(problem.ToString());
                }
            }
        }

        /// <summary>Guidelines for AI review of one file: the .md guidelines plus the matching, enabled Copilot rules.</summary>
        private List<ReviewGuideline> GuidelinesFor(string filePath, out List<string> sources)
        {
            var guidelines = new List<ReviewGuideline>();
            sources = new List<string>();

            if (_rules != null && _rules.Guidelines.Count > 0)
            {
                guidelines.AddRange(_rules.Guidelines);
                sources.Add(Path.GetFileName(_rules.FilePath));
            }

            if (_copilot != null && UseCopilotInstructions)
            {
                foreach (InstructionFile file in _copilot.FilesFor(filePath))
                {
                    if (_settings.DisabledInstructionFiles.Contains(file.Name))
                    {
                        continue;
                    }

                    List<InstructionRule> rules = file.Rules.Where(r => r.Reviewable).ToList();
                    if (rules.Count == 0)
                    {
                        continue;
                    }

                    guidelines.AddRange(rules.Select(r => new ReviewGuideline { Id = r.Id, Text = r.Text, SourceFile = file.FilePath, SourceLine = r.SourceLine }));
                    sources.Add(file.Name);
                }
            }

            return guidelines;
        }

        private void RebuildRuleRows()
        {
            RuleRows.Clear();
            if (_rules != null)
            {
                foreach (SeverityOverride o in _rules.Overrides)
                {
                    RuleRows.Add(new RuleRowViewModel(
                        GroupOverrides, o.RuleId, string.IsNullOrEmpty(o.Note) ? "Severity set by the team" : o.Note, null,
                        o.Severity, RuleRowViewModel.SeverityBrush(o.Severity), o.SourceLine, _rules.FilePath));
                }

                foreach (EditorConfigSetting s in _rules.Settings)
                {
                    RuleRows.Add(new RuleRowViewModel(
                        GroupSettings, s.Key, s.Value, s.Note, "setting", Palette.Neutral, s.SourceLine, _rules.FilePath));
                }

                foreach (CustomRule r in _rules.CustomRules)
                {
                    string detail = string.Format(
                        "{0}: {1} · files: {2}{3}{4}",
                        r.IsLiteral ? "text" : "pattern",
                        r.Pattern,
                        string.Join(", ", r.Files),
                        r.Exclude.Count > 0 ? " · exclude: " + string.Join(", ", r.Exclude) : string.Empty,
                        r.HasFix ? " · auto-fix" : string.Empty);
                    RuleRows.Add(new RuleRowViewModel(
                        GroupCustom, r.Id, r.Title, detail, r.Severity, RuleRowViewModel.SeverityBrush(r.Severity), r.SourceLine, _rules.FilePath));
                }

                foreach (ReviewGuideline g in _rules.Guidelines)
                {
                    RuleRows.Add(new RuleRowViewModel(GroupGuidelines, g.Id, g.Text, null, "AI", Palette.Ai, g.SourceLine, _rules.FilePath));
                }
            }

            if (_copilot != null)
            {
                foreach (InstructionFile file in _copilot.Files)
                {
                    string group = "Copilot · " + file.Name + ".instructions.md";
                    foreach (InstructionRule rule in file.Rules)
                    {
                        RuleRows.Add(new RuleRowViewModel(
                            group,
                            "§" + rule.Id.Substring(rule.Id.IndexOf('§') + 1),
                            rule.Text,
                            rule.Section,
                            rule.Reviewable ? "AI" : "process",
                            rule.Reviewable ? Palette.Ai : Palette.Neutral,
                            rule.SourceLine,
                            file.FilePath));
                    }
                }
            }

            Raise(nameof(RuleRowCount));
        }

        private void RaiseRulesState()
        {
            Raise(nameof(HasRulesFile));
            Raise(nameof(HasAnyRules));
            Raise(nameof(HasCopilot));
            Raise(nameof(CopilotRuleCount));
            Raise(nameof(ShowCreateRulesLink));
            Raise(nameof(ShowNoRulesState));
            Raise(nameof(HasSolution));
            Raise(nameof(NoRulesTitle));
            Raise(nameof(NoRulesText));
            Raise(nameof(OverrideCount));
            Raise(nameof(SettingCount));
            Raise(nameof(CustomCount));
            Raise(nameof(GuidelineCount));
            Raise(nameof(HasProblems));
            Raise(nameof(ProblemsSummary));
            Raise(nameof(ShowRulesContent));
            Raise(nameof(ShowFindingsContent));
            UpdateEditorConfigStatus();
            CommandManager.InvalidateRequerySuggested();
        }

        private void UpdateEditorConfigStatus()
        {
            if (_rules == null || _solutionDirectory == null)
            {
                EditorConfigStatusText = string.Empty;
                EditorConfigStatusBrush = Palette.Neutral;
                return;
            }

            TeamBlockStatus status;
            try
            {
                status = TeamEditorConfig.GetStatus(_solutionDirectory, _rules);
            }
            catch (IOException)
            {
                status = TeamBlockStatus.NotApplied;
            }

            switch (status)
            {
                case TeamBlockStatus.UpToDate:
                    EditorConfigStatusText = "✓ In .editorconfig";
                    EditorConfigStatusBrush = Palette.Ok;
                    break;
                case TeamBlockStatus.Outdated:
                    EditorConfigStatusText = ".editorconfig out of date";
                    EditorConfigStatusBrush = Palette.Warning;
                    break;
                case TeamBlockStatus.NotApplied:
                    EditorConfigStatusText = "Not in .editorconfig yet";
                    EditorConfigStatusBrush = Palette.Warning;
                    break;
                default:
                    EditorConfigStatusText = "Nothing for .editorconfig";
                    EditorConfigStatusBrush = Palette.Neutral;
                    break;
            }
        }

        private void Watch(string path)
        {
            StopWatching();
            try
            {
                _watcher = new FileSystemWatcher(Path.GetDirectoryName(path), Path.GetFileName(path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                _watcher.Changed += OnRulesFileChanged;
                _watcher.Created += OnRulesFileChanged;
                _watcher.Renamed += OnRulesFileChanged;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException)
            {
                _watcher = null;
            }
        }

        private void StopWatching()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }

        private void OnRulesFileChanged(object sender, FileSystemEventArgs e)
        {
            // Editors save in several steps; wait a moment, then reload on the UI thread.
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await Task.Delay(400);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!IsBusy && _rulesPath != null && string.Equals(_rulesPath, e.FullPath, StringComparison.OrdinalIgnoreCase))
                {
                    LoadRules(_rulesPath);
                    StatusText = "Rules file changed on disk; reloaded. " + StatusText;
                }
            });
        }

        // ------------------------------------------------------------------ commands: rules file

        private void Browse()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose your team's rules file",
                Filter = "Markdown files (*.md)|*.md|All files (*.*)|*.*",
                InitialDirectory = _solutionDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                CheckFileExists = true,
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            if (_solutionPath != null)
            {
                _settings.RulesFiles[_solutionPath] = dialog.FileName;
                _settings.Save();
            }

            LoadRules(dialog.FileName);
            SetTab(findings: false);
        }

        private async Task OpenRulesAsync()
        {
            if (_rulesPath != null)
            {
                await VS.Documents.OpenAsync(_rulesPath);
            }
        }

        private async Task NewRulesAsync()
        {
            if (_solutionDirectory == null)
            {
                return;
            }

            string path = Path.Combine(_solutionDirectory, "CodeQualityRules.md");
            bool created = false;

            if (!File.Exists(path))
            {
                File.WriteAllText(path, TemplateProvider.RulesMarkdown, new System.Text.UTF8Encoding(false));
                created = true;
            }

            if (_solutionPath != null)
            {
                _settings.RulesFiles[_solutionPath] = path;
                _settings.Save();
            }

            LoadRules(path);
            SetTab(findings: false);
            StatusText = created
                ? "Created CodeQualityRules.md from the template. Edit it; the window reloads when you save."
                : "CodeQualityRules.md already exists, so it was opened instead.";

            await VS.Documents.OpenAsync(path);
        }

        private async Task ApplyEditorConfigAsync()
        {
            if (_rules == null || _solutionDirectory == null)
            {
                return;
            }

            await RunBusyAsync("Writing team rules to .editorconfig...", async cancellationToken =>
            {
                var log = new List<string>();
                await Task.Run(() => TeamEditorConfig.Write(_solutionDirectory, _rules, log), cancellationToken);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

                UpdateEditorConfigStatus();
                StatusText = string.Join(" ", log)
                    + (_rules.Overrides.Count + _rules.Settings.Count > 0 ? " Visual Studio picks the change up automatically; rebuild to refresh the Error List." : string.Empty);
            });
        }

        private async Task RemoveEditorConfigAsync()
        {
            if (_solutionDirectory == null)
            {
                return;
            }

            bool proceed = await VS.MessageBox.ShowConfirmAsync(
                Vsix.Name,
                "Remove the team rules block from the solution's .editorconfig?\n\nYour rules .md file is not changed.");
            if (!proceed)
            {
                return;
            }

            var log = new List<string>();
            TeamEditorConfig.Remove(_solutionDirectory, log);
            UpdateEditorConfigStatus();
            StatusText = log.Count > 0 ? string.Join(" ", log) : "Nothing to remove.";
        }

        // ------------------------------------------------------------------ commands: checks

        private async Task CheckCustomAsync()
        {
            if (_rules == null)
            {
                return;
            }

            if (_rules.CustomRules.Count == 0)
            {
                StatusText = "The rules file has no custom rules. Add them under a '## Custom rules' heading (see the template).";
                return;
            }

            await RunBusyAsync("Checking custom rules...", async cancellationToken =>
            {
                RoslynSolution solution = _workspace.CurrentSolution;
                List<CustomRule> rules = _rules.CustomRules.ToList();
                var progress = new Progress<string>(text => BusyText = text);

                PatternCheckResult result = await Task.Run(
                    () => PatternRuleChecker.CheckAsync(solution, _solutionDirectory, rules, null, progress, cancellationToken),
                    cancellationToken);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

                _findings.RemoveAll(f => f.Source == FindingSource.Custom);
                _findings.AddRange(result.Findings);
                RebuildFindingRows();
                SetTab(findings: true);

                int fixable = result.Findings.Count(f => f.HasFix);
                StatusText = result.Findings.Count == 0
                    ? string.Format("No custom rule violations in {0} file(s).", result.FilesChecked)
                    : string.Format(
                        "Custom rules found {0} issue(s) in {1} file(s); {2} can be fixed automatically.{3}",
                        result.Findings.Count,
                        result.Findings.Select(f => f.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                        fixable,
                        result.Warnings.Count > 0 ? " Note: " + result.Warnings[0] : string.Empty);

                SelectFirstFinding();
            });
        }

        private async Task AiReviewActiveFileAsync()
        {
            if (!HasAnyRules)
            {
                return;
            }

            DocumentView view = await VS.Documents.GetActiveDocumentViewAsync();
            string filePath = view?.FilePath;
            if (string.IsNullOrEmpty(filePath) || view.TextView == null)
            {
                StatusText = "Open a file in the editor first; AI review checks the active file.";
                return;
            }

            List<ReviewGuideline> guidelines = GuidelinesFor(filePath, out List<string> sources);
            if (guidelines.Count == 0)
            {
                StatusText = string.Format(
                    "No guidelines apply to {0}: {1}",
                    Path.GetFileName(filePath),
                    _copilot != null && UseCopilotInstructions
                        ? "no enabled Copilot instruction file has an applyTo that matches it, and the rules file has no AI review guidelines."
                        : "add bullets under '## AI review guidelines' in the rules file, or turn on Copilot instructions in the AI settings.");
                return;
            }

            if (Models.Count == 0)
            {
                await LoadModelsAsync(quiet: false);
                if (Models.Count == 0)
                {
                    return;
                }
            }

            AiModel model = SelectedModel ?? Models[0];
            string editorText = view.TextView.TextBuffer.CurrentSnapshot.GetText();
            var usage = new TokenUsage();

            await VS.Commands.ExecuteAsync("File.SaveSelectedItems");

            await RunBusyAsync("Starting AI review...", async cancellationToken =>
            {
                RoslynSolution solution = _workspace.CurrentSolution;
                RoslynDocument document = solution.GetDocumentIdsWithFilePath(filePath)
                    .Select(id => solution.GetDocument(id))
                    .FirstOrDefault(d => d != null);

                // Files outside C# projects (.sql, .razor, .cshtml, .xaml, .yml, …) are reviewed from the editor
                // text; their findings are shown with the suggestion but fixed by hand.
                SourceText text = document != null
                    ? await document.GetTextAsync(cancellationToken)
                    : SourceText.From(editorText);

                string relative = _copilot != null
                    ? _copilot.RelativePath(filePath)
                    : PatternRuleChecker.RelativePath(filePath, _solutionDirectory);
                var progress = new Progress<string>(message => BusyText = message);
                List<string> context = ReviewContext(document);

                BusyText = string.Format("{0}: {1} rule(s) from {2}...", model.Name, guidelines.Count, string.Join(", ", sources));

                AiReviewResult review;
                try
                {
                    review = await Task.Run(
                        () => _ai.ReviewAsync(model, _settings, relative, text.ToString(), guidelines, context, usage, progress, cancellationToken),
                        cancellationToken);
                }
                finally
                {
                    // Count the tokens even when the review failed or was cancelled part-way: those requests were still used.
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    RecordUsage(model, Path.GetFileName(filePath), usage);
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

                List<RuleFinding> found = review.Findings
                    .Select(f => ToRuleFinding(f, filePath, document?.Id, relative, text, guidelines))
                    .ToList();

                _findings.RemoveAll(f => f.Source == FindingSource.Ai && string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
                _findings.AddRange(found);
                RebuildFindingRows();
                SetTab(findings: true);

                StatusText = found.Count == 0
                    ? string.Format(
                        "{0} checked {1} against {2} rule(s) from {3} and found nothing.{4}",
                        model.Name,
                        Path.GetFileName(filePath),
                        guidelines.Count,
                        string.Join(", ", sources),
                        UsageSentence(usage))
                    : string.Format(
                        "{0} found {1} possible issue(s) in {2} ({3} rule(s) from {4}); {5} with a fix. AI can be wrong: check each preview before applying.{6}{7}",
                        model.Name,
                        found.Count,
                        Path.GetFileName(filePath),
                        guidelines.Count,
                        string.Join(", ", sources),
                        found.Count(f => f.HasFix),
                        UsageSentence(usage),
                        review.Warnings.Count > 0 ? " Note: " + review.Warnings[0] : string.Empty);

                SelectFirstFinding(filePath);
            });
        }

        /// <summary>
        /// Facts sent with every AI review: the project's C# version and target framework (detected), plus the
        /// "## AI review context" lines from the rules file.
        /// </summary>
        private List<string> ReviewContext(RoslynDocument document)
        {
            var facts = new List<string>();

            if (document != null)
            {
                try
                {
                    Microsoft.CodeAnalysis.ParseOptions options = document.Project.ParseOptions;
                    object version = options?.GetType().GetProperty("LanguageVersion")?.GetValue(options);
                    string readable = ReadableLanguageVersion(version?.ToString());
                    if (readable != null)
                    {
                        facts.Add("C# language version: " + readable + ".");
                    }

                    if (!string.IsNullOrEmpty(document.Project.FilePath) && File.Exists(document.Project.FilePath))
                    {
                        string frameworks = ProjectScanner.ReadTargetFrameworks(System.Xml.Linq.XDocument.Load(document.Project.FilePath));
                        if (!string.IsNullOrEmpty(frameworks) && frameworks != "unknown")
                        {
                            facts.Add("Target framework: " + frameworks + ".");
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Xml.XmlException || ex is System.Reflection.TargetInvocationException)
                {
                    // Facts are a help, not a requirement.
                }
            }

            if (_rules != null)
            {
                facts.AddRange(_rules.Context);
            }

            return facts;
        }

        /// <summary>"CSharp12" -> "12", "CSharp7_3" -> "7.3".</summary>
        private static string ReadableLanguageVersion(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(value, @"^CSharp(\d+)(?:_(\d+))?$");
            if (m.Success)
            {
                return m.Groups[2].Success ? m.Groups[1].Value + "." + m.Groups[2].Value : m.Groups[1].Value;
            }

            return value; // Preview, Latest, …
        }

        private RuleFinding ToRuleFinding(
            AiFinding finding,
            string filePath,
            Microsoft.CodeAnalysis.DocumentId documentId,
            string relative,
            SourceText text,
            IReadOnlyList<ReviewGuideline> guidelines)
        {
            ReviewGuideline guideline = guidelines.FirstOrDefault(g => g.Id == finding.Guideline);
            int line = Math.Max(1, Math.Min(finding.Line, text.Lines.Count));

            var result = new RuleFinding
            {
                Source = FindingSource.Ai,
                RuleId = finding.Guideline,
                Title = guideline?.Text ?? "AI review",
                Message = finding.Message,
                Severity = IssueSeverity.Warning,
                FilePath = filePath,
                RelativePath = relative,
                DocumentId = documentId,
                Line = line,
                LineText = text.Lines[line - 1].ToString().Trim(),
                Snapshot = text,
                Original = finding.Original,
                Replacement = finding.Replacement,
                RuleSourceFile = guideline?.SourceFile,
                RuleSourceLine = guideline?.SourceLine ?? 0,
            };

            Locate(result, text);
            return result;
        }

        /// <summary>(Re)finds an AI suggestion in the given text and updates the finding's fix and line.</summary>
        private static void Locate(RuleFinding finding, SourceText text)
        {
            finding.Snapshot = text;

            // Only files in a C# project can be changed through the Roslyn workspace; others keep the suggestion as text.
            if (finding.DocumentId != null
                && FindingFixes.TryLocate(text, finding.Line, finding.Original, finding.Replacement, out var span, out string fix, out int line))
            {
                finding.FixSpan = span;
                finding.FixReplacement = fix;
                finding.Line = line;
                finding.LineText = text.Lines[line - 1].ToString().Trim();
            }
            else
            {
                finding.FixSpan = null;
                finding.FixReplacement = null;
            }
        }

        /// <summary>Loads the models of the AI providers the user enabled (OpenAI, Anthropic, Ollama).</summary>
        private async Task LoadModelsAsync(bool quiet)
        {
            AiStatusText = "Loading models...";
            ModelLoadResult result = await AiProviders.LoadModelsAsync(CancellationToken.None);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            Models.Clear();
            foreach (AiModel model in result.Models)
            {
                Models.Add(model);
            }

            SelectedModel = Models.FirstOrDefault(m => string.Equals(m.Name, _settings.SelectedModel, StringComparison.OrdinalIgnoreCase))
                ?? Models.FirstOrDefault();

            AiStatusText = (result.Models.Count > 0 ? "Models: " + string.Join(", ", result.Counts) + "." : "No models available.")
                + (result.Errors.Count > 0 ? " " + string.Join(" ", result.Errors) : string.Empty);

            if (result.Errors.Count > 0 && !quiet)
            {
                StatusText = AiStatusText;
                ShowAiSettings = true;
            }

            Raise(nameof(AiPrivacyText));
            Raise(nameof(IsRemoteModel));
            Raise(nameof(IsLocalModel));
            Raise(nameof(ProvidersSummary));
            CommandManager.InvalidateRequerySuggested();
        }

        // ------------------------------------------------------------------ commands: findings

        private async Task LoadFindingPreviewAsync(FindingRowViewModel row)
        {
            ClearPreview();
            int version = _previewVersion;
            RuleFinding finding = row.Finding;

            PreviewTitle = finding.RuleId + " · " + finding.Title;
            PreviewSubtitle = finding.RelativePath + " · line " + finding.Line;
            IsPreviewLoading = true;

            try
            {
                RoslynSolution solution = _workspace.CurrentSolution;

                if (finding.DocumentId == null)
                {
                    // Not a C# project file (.sql, .razor, .cshtml, …): show the code and the AI's suggestion.
                    string content = File.Exists(finding.FilePath) ? File.ReadAllText(finding.FilePath) : finding.Snapshot?.ToString() ?? string.Empty;
                    PreviewHint = finding.Message
                        + (string.IsNullOrWhiteSpace(finding.Replacement)
                            ? string.Empty
                            : "\nSuggested change: " + finding.Original?.Trim() + "  →  " + finding.Replacement.Trim())
                        + "\nThis file type is fixed by hand: use Go to code.";
                    PreviewLines.Clear();
                    foreach (DiffLine line in LineDiff.Snippet(content, finding.Line))
                    {
                        PreviewLines.Add(line);
                    }

                    return;
                }

                RoslynDocument document = solution.GetDocument(finding.DocumentId);
                if (document == null)
                {
                    PreviewHint = "This file is no longer part of the solution.";
                    return;
                }

                SourceText current = await document.GetTextAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (version != _previewVersion)
                {
                    return;
                }

                // AI suggestions can follow the code after edits; custom rule matches need a fresh check.
                if (finding.Source == FindingSource.Ai && !current.ContentEquals(finding.Snapshot))
                {
                    Locate(finding, current);
                }

                if (finding.HasFix)
                {
                    FixPreview preview = await FindingFixes.BuildPreviewAsync(solution, new[] { finding }, finding.RuleId, CancellationToken.None);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (version != _previewVersion)
                    {
                        return;
                    }

                    if (preview.HasChanges)
                    {
                        ShowPreview(preview, new List<RuleFinding> { finding }, PreviewTitle, PreviewSubtitle, finding.Message, "Apply fix");
                        return;
                    }

                    PreviewHint = preview.Problem + "\n" + finding.Message;
                }
                else
                {
                    PreviewHint = (finding.Source == FindingSource.Ai
                            ? "No automatic fix suggested: use Go to code to fix it by hand, or Dismiss if the AI is wrong."
                            : "This rule has no automatic fix: use Go to code to fix it by hand.")
                        + "\n" + finding.Message;
                }

                PreviewLines.Clear();
                foreach (DiffLine line in LineDiff.Snippet(current.ToString(), Math.Min(finding.Line, current.Lines.Count)))
                {
                    PreviewLines.Add(line);
                }
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

        private async Task PreviewRuleAsync()
        {
            FindingRowViewModel row = SelectedFinding;
            if (row == null)
            {
                return;
            }

            RuleFinding selected = row.Finding;
            List<RuleFinding> matching = _findings
                .Where(f => f.Source == selected.Source && f.RuleId == selected.RuleId && f.HasFix)
                .ToList();

            await RunBusyAsync("Preparing fixes for " + selected.RuleId + "...", async cancellationToken =>
            {
                RoslynSolution solution = _workspace.CurrentSolution;
                FixPreview preview = await Task.Run(
                    () => FindingFixes.BuildPreviewAsync(solution, matching, selected.RuleId, cancellationToken),
                    cancellationToken);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

                _previewVersion++;
                _selectedFinding = null;
                Raise(nameof(SelectedFinding));

                if (!preview.HasChanges)
                {
                    ClearPreview();
                    PreviewTitle = selected.RuleId + " · all files";
                    PreviewHint = preview.Problem;
                    return;
                }

                ShowPreview(
                    preview,
                    matching,
                    selected.RuleId + " · all files",
                    string.Format("{0} fix(es) in {1} file(s)", preview.FixCount, preview.Files.Count),
                    "Every change is listed below. Nothing is changed until you approve.",
                    string.Format("Approve & apply {0} fix(es)", preview.FixCount));
            });
        }

        private async Task ApplyAsync()
        {
            FixPreview preview = _preview;
            List<RuleFinding> applied = _previewFindings;
            if (preview == null || !preview.HasChanges)
            {
                return;
            }

            await RunBusyAsync("Applying the approved changes...", async cancellationToken =>
            {
                PreparedChange prepared = await CommitAsync(preview.Files, revert: false, cancellationToken);
                if (prepared == null || !prepared.Result.Succeeded)
                {
                    StatusText = "The code changed after the preview, so nothing was applied. Check again to refresh the findings.";
                    return;
                }

                var changedFiles = new HashSet<string>(prepared.Result.Applied.Select(c => c.FilePath), StringComparer.OrdinalIgnoreCase);
                var appliedSet = new HashSet<RuleFinding>(applied.Where(f => changedFiles.Contains(f.FilePath)));
                _findings.RemoveAll(appliedSet.Contains);

                await RefreshFindingsAsync(changedFiles, cancellationToken);

                StatusText = string.Format(
                    "Applied {0} fix(es) in {1} file(s). Undo is available at the top.",
                    preview.FixCount,
                    prepared.Result.Applied.Count);

                SelectFirstFinding(prepared.Result.Applied.Select(c => c.FilePath).FirstOrDefault());
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

                var changedFiles = new HashSet<string>(prepared.Result.Applied.Select(c => c.FilePath), StringComparer.OrdinalIgnoreCase);
                await RefreshFindingsAsync(changedFiles, cancellationToken);
                StatusText = "Undid the last change in " + prepared.Result.Applied.Count + " file(s). Run AI review again to refresh AI findings.";
            });
        }

        /// <summary>After an edit: re-run custom rules on the changed files and re-find AI suggestions in them.</summary>
        private async Task RefreshFindingsAsync(HashSet<string> changedFiles, CancellationToken cancellationToken)
        {
            RoslynSolution solution = _workspace.CurrentSolution;

            if (_rules != null && _rules.CustomRules.Count > 0)
            {
                List<CustomRule> rules = _rules.CustomRules.ToList();
                PatternCheckResult fresh = await Task.Run(
                    () => PatternRuleChecker.CheckAsync(solution, _solutionDirectory, rules, changedFiles, null, cancellationToken),
                    cancellationToken);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                bool hadCustom = _findings.Any(f => f.Source == FindingSource.Custom);
                _findings.RemoveAll(f => f.Source == FindingSource.Custom && changedFiles.Contains(f.FilePath));
                if (hadCustom)
                {
                    _findings.AddRange(fresh.Findings);
                }
            }

            foreach (RuleFinding finding in _findings.Where(f => f.Source == FindingSource.Ai && changedFiles.Contains(f.FilePath)).ToList())
            {
                RoslynDocument document = finding.DocumentId == null ? null : solution.GetDocument(finding.DocumentId);
                if (document != null)
                {
                    Locate(finding, await document.GetTextAsync(cancellationToken));
                }
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            RebuildFindingRows();
        }

        private void Dismiss()
        {
            FindingRowViewModel row = SelectedFinding;
            if (row == null)
            {
                return;
            }

            int index = FindingRows.IndexOf(row);
            _findings.Remove(row.Finding);
            RebuildFindingRows();
            StatusText = "Dismissed " + row.RuleId + " on line " + row.Finding.Line + ".";

            List<FindingRowViewModel> ordered = FindingsView.Cast<FindingRowViewModel>().ToList();
            if (ordered.Count > 0)
            {
                SelectedFinding = ordered[Math.Min(Math.Max(index, 0), ordered.Count - 1)];
            }
        }

        private void ClearFindings()
        {
            _findings.Clear();
            RebuildFindingRows();
            StatusText = "Findings cleared.";
        }

        private Task GoToCodeAsync()
        {
            RuleFinding finding = SelectedFinding?.Finding;
            return finding == null ? Task.CompletedTask : OpenAtLineAsync(finding.FilePath, finding.Line);
        }

        /// <summary>Called when a row in the Rules tab is double-clicked.</summary>
        public void OpenRuleRow(RuleRowViewModel row)
        {
            if (row != null && !string.IsNullOrEmpty(row.SourceFile))
            {
                Run(() => OpenAtLineAsync(row.SourceFile, row.SourceLine));
            }
        }

        private static async Task OpenAtLineAsync(string filePath, int line)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return;
            }

            DocumentView view = await VS.Documents.OpenAsync(filePath);
            IWpfTextView textView = view?.TextView;
            if (textView == null)
            {
                return;
            }

            ITextSnapshot snapshot = textView.TextSnapshot;
            int lineNumber = Math.Max(0, Math.Min(line - 1, snapshot.LineCount - 1));
            ITextSnapshotLine textLine = snapshot.GetLineFromLineNumber(lineNumber);

            textView.Caret.MoveTo(textLine.Start);
            textView.ViewScroller.EnsureSpanVisible(new SnapshotSpan(textLine.Start, textLine.End), EnsureSpanVisibleOptions.AlwaysCenter);
            textView.VisualElement.Focus();
        }

        // ------------------------------------------------------------------ findings list + preview

        private void RebuildFindingRows()
        {
            _previewVersion++;
            _selectedFinding = null;
            Raise(nameof(SelectedFinding));

            FindingRows.Clear();
            foreach (RuleFinding finding in _findings)
            {
                FindingRows.Add(new FindingRowViewModel(finding));
            }

            ClearPreview();
            Raise(nameof(FindingCount));
            Raise(nameof(HasFindings));
            Raise(nameof(ShowFindingsEmpty));
            Raise(nameof(FindingsSummary));
            CommandManager.InvalidateRequerySuggested();
        }

        private void SelectFirstFinding(string preferredFile = null)
        {
            List<FindingRowViewModel> ordered = FindingsView.Cast<FindingRowViewModel>().ToList();
            FindingRowViewModel first = (preferredFile == null
                    ? null
                    : ordered.FirstOrDefault(r => string.Equals(r.Finding.FilePath, preferredFile, StringComparison.OrdinalIgnoreCase)))
                ?? ordered.FirstOrDefault();

            if (first != null)
            {
                SelectedFinding = first;
            }
        }

        private void ShowPreview(FixPreview preview, List<RuleFinding> findings, string title, string subtitle, string hint, string applyText)
        {
            IsPreviewLoading = false;
            _preview = preview;
            _previewFindings = findings;
            PreviewTitle = title;
            PreviewSubtitle = subtitle;
            PreviewHint = hint;
            ApplyText = applyText;

            PreviewLines.Clear();
            int added = 0;
            int removed = 0;

            foreach (FileChange file in preview.Files)
            {
                added += file.Added;
                removed += file.Removed;

                if (preview.Files.Count > 1)
                {
                    PreviewLines.Add(new DiffLine(
                        DiffKind.File,
                        null,
                        null,
                        string.Format("{0}    +{1} −{2}", PatternRuleChecker.RelativePath(file.FilePath, _solutionDirectory), file.Added, file.Removed)));
                }

                foreach (DiffLine line in file.Diff)
                {
                    PreviewLines.Add(line);
                }
            }

            AddedText = "+" + added;
            RemovedText = "−" + removed;
            Raise(nameof(HasDiffStats));
            CommandManager.InvalidateRequerySuggested();
        }

        private void ClearPreview()
        {
            _previewVersion++;
            _preview = null;
            _previewFindings = new List<RuleFinding>();
            PreviewLines.Clear();
            PreviewTitle = null;
            PreviewSubtitle = null;
            PreviewHint = null;
            AddedText = null;
            RemovedText = null;
            ApplyText = "Apply fix";
            IsPreviewLoading = false;
            Raise(nameof(HasDiffStats));
        }

        private void SetTab(bool findings)
        {
            _isFindingsTab = findings;
            Raise(nameof(IsRulesTab));
            Raise(nameof(IsFindingsTab));
            Raise(nameof(ShowRulesContent));
            Raise(nameof(ShowFindingsContent));
        }

        // ------------------------------------------------------------------ helpers

        private async Task<PreparedChange> CommitAsync(IReadOnlyList<FileChange> files, bool revert, CancellationToken cancellationToken)
        {
            PreparedChange prepared = null;

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
            catch (AiServiceException ex)
            {
                StatusText = ex.Message;
                AiStatusText = ex.Message;
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
    }
}
