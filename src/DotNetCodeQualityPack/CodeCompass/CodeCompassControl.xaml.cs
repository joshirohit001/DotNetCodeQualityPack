using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;


using DotNetCodeQualityPack.Ai;
using DotNetCodeQualityPack.Services;
using DotNetCodeQualityPack.ToolWindows;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace DotNetCodeQualityPack.CodeCompass
{
    internal partial class CodeCompassControl : UserControl
    {
        private const string ChatSystemPrompt =
            "You are Code Compass, an AI coding assistant inside Visual Studio 2022 for .NET (C#) developers. " +
            "Be accurate and concise. Format answers in Markdown and put code in fenced code blocks with a language tag (```csharp). " +
            "When you change code, show the complete corrected snippet. " +
            "Follow the team's coding rules: never block on async code (.Result/.Wait), no async void, dispose IDisposable with using, " +
            "parameterized SQL only, no hard-coded secrets, structured ILogger messages, specific exception handling. " +
            "If an image is attached (screenshot of code, an error, a UI or a diagram), read it carefully and use what it shows. " +
            "If you are not sure about something, say so instead of guessing.";

        private const double MinZoom = 0.7;
        private const double MaxZoom = 2.0;
        private const int HistoryCharBudget = 48000;

        private readonly CodeCompassStore _store = new CodeCompassStore();
        private readonly CodeCompassSettings _settings;
        private readonly ChatClient _client = new ChatClient();
        private readonly ReviewService _reviewService;
        private readonly List<Attachment> _pending = new List<Attachment>();

        private ConversationFile _data = new ConversationFile();
        private Conversation _active;
        private string _solutionPath;
        private string _contextRoot;
        private string _contextKey;
        private bool _solutionLoaded;
        private bool _modelsLoaded;
        private bool _suppressEvents;
        private RuleSet _rules;
        private CancellationTokenSource _chatCts;
        private CancellationTokenSource _reviewCts;
        private EditorSnapshot _lastReviewSnapshot;
        private ReviewResult _lastReview;

        private enum StatusKind
        {
            Normal,
            Busy,
            Success,
            Warning,
            Error
        }

        public CodeCompassControl()
        {
            InitializeComponent();

            _settings = _store.LoadSettings();
            _reviewService = new ReviewService(_client);

            _suppressEvents = true;
            ThemeCombo.ItemsSource = ThemeManager.Themes;
            ThemeCombo.SelectedItem = ThemeManager.Themes.Contains(_settings.Theme) ? _settings.Theme : ThemeManager.VisualStudio;
            _suppressEvents = false;

            ApplyTheme();
            ApplyZoom(_settings.Zoom, save: false);
            RefreshAttachmentChips();

            VSColorTheme.ThemeChanged += OnVsThemeChanged;
            Loaded += OnLoaded;
            IsVisibleChanged += OnVisibleChanged;
            PreviewMouseWheel += OnPreviewMouseWheel;
            PreviewKeyDown += OnRootPreviewKeyDown;
            PreviewDragOver += OnPreviewDragOver;
            PreviewDrop += OnPreviewDrop;

            // Ctrl+V / Shift+Insert / right-click Paste in the input box: images and files become attachments.
            CommandManager.AddPreviewCanExecuteHandler(InputBox, OnInputPasteCanExecute);
            CommandManager.AddPreviewExecutedHandler(InputBox, OnInputPasteExecuted);
        }

        private AiModel CurrentModel => ModelCombo.SelectedItem as AiModel;

        /// <summary>Called by the tool window when a solution is opened or closed.</summary>
        public void OnSolutionChanged()
        {
            EnsureSolutionContext();
        }

        // ------------------------------------------------------------------ lifecycle

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            EnsureSolutionContext();
            if (!_modelsLoaded)
            {
                _modelsLoaded = true;
                Run(LoadModelsAsync);
            }
        }

        private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible) EnsureSolutionContext();
        }

        /// <summary>Loads chats and rules for the open solution; switches when the solution changes.</summary>
        private void EnsureSolutionContext()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Works with a solution, with File > Open Folder, and with a loose file (no .sln at all).
            EditorService.GetContext(out string solution, out string root);
            string key = solution ?? root;
            if (_solutionLoaded && string.Equals(key, _contextKey, StringComparison.OrdinalIgnoreCase)) return;

            if (_solutionLoaded) SaveChats();
            if (_chatCts != null) _chatCts.Cancel();

            _solutionPath = solution;
            _contextRoot = root;
            _contextKey = key;
            _solutionLoaded = true;
            _data = _store.Load(key);
            _active = _data.Conversations.FirstOrDefault(c => c.Id == _data.ActiveConversationId)
                      ?? _data.Conversations.OrderByDescending(c => c.UpdatedUtc).FirstOrDefault();

            RefreshConversationCombo();
            RenderConversation();
            ReloadRules();
        }

        /// <summary>Models of the AI providers the user enabled (OpenAI, Anthropic, Ollama).</summary>
        private async Task LoadModelsAsync()
        {
            SetStatus("Loading models…", StatusKind.Busy);
            PackSettings pack = PackSettings.Load();
            ModelLoadResult result;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
            {
                result = await _client.LoadModelsAsync(cts.Token);
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string wanted = _settings.SelectedModel ?? pack.SelectedModel;
            _suppressEvents = true;
            ModelCombo.ItemsSource = result.Models;
            ModelCombo.SelectedItem = result.Models.FirstOrDefault(m => string.Equals(m.Name, wanted, StringComparison.OrdinalIgnoreCase))
                ?? result.Models.FirstOrDefault();
            _suppressEvents = false;
            UpdateVisionNotice();

            if (result.Models.Count == 0)
            {
                SetStatus(string.Join(" ", result.Errors) + " Click the gear icon, then Configure…", StatusKind.Error);

                // First run: open the settings so the user sees where to choose a provider.
                if (!AiProviderSettings.Load().AnyEnabled)
                {
                    UpdateProvidersSummary();
                    HistoryCountBox.Text = _settings.HistoryMessagesToSend.ToString();
                    SettingsPanel.Visibility = Visibility.Visible;
                }
            }
            else if (result.Errors.Count > 0)
            {
                SetStatus("Models: " + string.Join(", ", result.Counts) + ". Not loaded: " + string.Join(" ", result.Errors), StatusKind.Warning);
            }
            else
            {
                SetStatus("Models: " + string.Join(", ", result.Counts), StatusKind.Success);
            }
        }

        /// <summary>OpenAI / Anthropic / Ollama settings, shared with Team Rules. Models reload after a save.</summary>
        private void OnConfigureProviders(object sender, RoutedEventArgs e)
        {
            if (AiProvidersDialog.Show())
            {
                UpdateProvidersSummary();
                Run(LoadModelsAsync);
            }
        }

        private void UpdateProvidersSummary()
        {
            var enabled = AiProviderSettings.Load().Enabled.Select(p => AiProviders.Get(p.Kind).DisplayName).ToList();
            ProvidersSummary.Text = enabled.Count == 0
                ? "None yet: choose OpenAI, Anthropic or Ollama, with your own key and models."
                : "Using " + string.Join(", ", enabled) + ".";
        }

        // ------------------------------------------------------------------ header: tabs, model, theme, zoom, settings

        private void OnTabChanged(object sender, RoutedEventArgs e)
        {
            if (ChatView == null || ReviewView == null) return; // during InitializeComponent
            var chat = ChatTab.IsChecked == true;
            ChatView.Visibility = chat ? Visibility.Visible : Visibility.Collapsed;
            ReviewView.Visibility = chat ? Visibility.Collapsed : Visibility.Visible;
            if (chat) InputBox.Focus();
        }

        private void OnModelChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || CurrentModel == null) return;
            _settings.SelectedModel = CurrentModel.Name;
            SaveSettings();
            UpdateVisionNotice();
            SetStatus("Model: " + CurrentModel.Name, StatusKind.Success);
        }

        private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            _settings.Theme = ThemeCombo.SelectedItem as string ?? ThemeManager.VisualStudio;
            ApplyTheme();
            SaveSettings();
        }

        private void OnVsThemeChanged(ThemeChangedEventArgs e)
        {
            if (_settings.Theme == ThemeManager.VisualStudio) ApplyTheme();
        }

        private void ApplyTheme()
        {
            ThemeManager.Apply(this, _settings.Theme);
        }

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
            ApplyZoom(_settings.Zoom + (e.Delta > 0 ? 0.1 : -0.1));
            e.Handled = true;
        }

        private void OnRootPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;

            if (e.Key == Key.D0 || e.Key == Key.NumPad0)
            {
                ApplyZoom(1.0);
                e.Handled = true;
            }
            else if (e.Key == Key.OemPlus || e.Key == Key.Add)
            {
                ApplyZoom(_settings.Zoom + 0.1);
                e.Handled = true;
            }
            else if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
            {
                ApplyZoom(_settings.Zoom - 0.1);
                e.Handled = true;
            }
        }

        private void OnZoomReset(object sender, RoutedEventArgs e)
        {
            ApplyZoom(1.0);
        }

        private void ApplyZoom(double zoom, bool save = true)
        {
            zoom = Math.Round(Math.Max(MinZoom, Math.Min(MaxZoom, zoom)), 2);
            _settings.Zoom = zoom;
            ContentHost.LayoutTransform = Math.Abs(zoom - 1.0) < 0.001 ? Transform.Identity : new ScaleTransform(zoom, zoom);
            ZoomButton.Content = (int)Math.Round(zoom * 100) + "%";
            if (save) SaveSettings();
        }

        private void OnToggleSettings(object sender, RoutedEventArgs e)
        {
            var show = SettingsPanel.Visibility != Visibility.Visible;
            if (show)
            {
                UpdateProvidersSummary();
                HistoryCountBox.Text = _settings.HistoryMessagesToSend.ToString();
            }

            SettingsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnSaveSettings(object sender, RoutedEventArgs e)
        {
            int history;
            if (!int.TryParse(HistoryCountBox.Text.Trim(), out history) || history < 0 || history > 100)
            {
                SetStatus("Chat memory must be a number from 0 to 100.", StatusKind.Error);
                HistoryCountBox.Focus();
                return;
            }

            _settings.HistoryMessagesToSend = history;
            SaveSettings();

            SettingsPanel.Visibility = Visibility.Collapsed;
            ReloadRules();
            Run(LoadModelsAsync);
        }

        /// <summary>The rules file and the Copilot instruction files are managed in the Team Rules window.</summary>
        private void OnOpenTeamRules(object sender, RoutedEventArgs e)
        {
            Run(async () =>
            {
                await TeamRulesToolWindow.ShowAsync();
                SetStatus("After changing rules in Team Rules, click Save & reload models here.", StatusKind.Normal);
            });
        }

        private void OnOpenDataFolder(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start("explorer.exe", "\"" + _store.RootFolder + "\"");
            }
            catch (Exception ex)
            {
                SetStatus("Could not open the folder: " + ex.Message, StatusKind.Error);
            }
        }

        private void ReloadRules(string filePath = null)
        {
            try
            {
                _rules = RuleSetProvider.Load(_contextRoot, _solutionPath, PackSettings.Load(), filePath);
            }
            catch (Exception ex)
            {
                _rules = new RuleSet { SourceDescription = "Could not read rules: " + ex.Message };
            }

            RulesLabel.Text = "Rules: " + _rules.SourceDescription;
            StatusRight.Text = _rules.Rules.Count > 0 ? _rules.Rules.Count + " rules" : string.Empty;
        }

        // ------------------------------------------------------------------ conversations

        private void RefreshConversationCombo()
        {
            _suppressEvents = true;
            ConversationCombo.ItemsSource = null;
            ConversationCombo.ItemsSource = _data.Conversations.OrderByDescending(c => c.UpdatedUtc).ToList();
            ConversationCombo.SelectedItem = _active;
            _suppressEvents = false;
        }

        private void OnConversationChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            var selected = ConversationCombo.SelectedItem as Conversation;
            if (selected == null || selected == _active) return;

            if (_chatCts != null)
            {
                // Don't switch while an answer is streaming.
                RefreshConversationCombo();
                SetStatus("Wait for the answer to finish (or press Stop) before switching chats.", StatusKind.Warning);
                return;
            }

            _active = selected;
            _data.ActiveConversationId = selected.Id;
            RenderConversation();
            SaveChats();
        }

        private void OnNewChat(object sender, RoutedEventArgs e)
        {
            if (_chatCts != null) return;
            if (_active == null || _active.Messages.Count > 0) StartNewConversation();
            RefreshConversationCombo();
            RenderConversation();
            InputBox.Focus();
        }

        private void OnDeleteChat(object sender, RoutedEventArgs e)
        {
            if (_active == null || _chatCts != null) return;
            if (_active.Messages.Count > 0 &&
                MessageBox.Show("Delete this chat? This can't be undone.", "Code Compass", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes)
            {
                return;
            }

            _data.Conversations.Remove(_active);
            _active = _data.Conversations.OrderByDescending(c => c.UpdatedUtc).FirstOrDefault();
            _data.ActiveConversationId = _active != null ? _active.Id : null;
            SaveChats();
            RefreshConversationCombo();
            RenderConversation();
        }

        private void StartNewConversation()
        {
            _active = new Conversation();
            _data.Conversations.Add(_active);
            _data.ActiveConversationId = _active.Id;
        }

        private void SaveChats()
        {
            try
            {
                _data.Conversations.RemoveAll(c => c.Messages.Count == 0 && c != _active);
                _store.Save(_contextKey, _data);
            }
            catch (Exception ex)
            {
                SetStatus("Could not save the chat: " + ex.Message, StatusKind.Warning);
            }
        }

        private void SaveSettings()
        {
            try
            {
                _store.SaveSettings(_settings);
            }
            catch (Exception)
            {
                // Settings are a convenience; ignore write failures.
            }
        }

        // ------------------------------------------------------------------ chat: sending

        private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0 && (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            {
                e.Handled = true;
                OnSend(sender, e);
            }
        }

        private void OnSend(object sender, RoutedEventArgs e)
        {
            Run(() => SendAsync(null));
        }

        private void OnStop(object sender, RoutedEventArgs e)
        {
            if (_chatCts != null) _chatCts.Cancel();
        }

        private async Task SendAsync(string textOverride)
        {
            if (_chatCts != null) return;
            EnsureSolutionContext();

            var text = (textOverride ?? InputBox.Text ?? string.Empty).Trim();
            if (text.Length == 0 && _pending.Count == 0) return;

            var model = CurrentModel;
            if (model == null)
            {
                ShowComposerNotice("Choose a model first (top of the window).");
                return;
            }

            if (text.Length == 0)
            {
                text = _pending.Any(a => a.Kind == AttachmentKind.Image)
                    ? "Review the attached image and tell me what you see and what should be improved."
                    : "Review the attached file(s) and suggest improvements with code.";
            }

            var user = new ChatMessage
            {
                Role = ChatMessage.UserRole,
                Content = text,
                Attachments = _pending.ToList()
            };

            _pending.Clear();
            RefreshAttachmentChips();
            InputBox.Clear();
            HideComposerNotice();

            if (_active == null) StartNewConversation();
            _active.Messages.Add(user);
            if (_active.Messages.Count(m => m.Role == ChatMessage.UserRole) == 1) _active.Title = MakeTitle(text);
            _active.UpdatedUtc = DateTime.UtcNow;
            _data.ActiveConversationId = _active.Id;

            EmptyChatPanel.Visibility = Visibility.Collapsed;
            MessagesPanel.Children.Add(CreateBubble(user));
            ScrollToEnd(force: true);

            TextBox streamBox;
            var streamingBubble = CreateStreamingBubble(model.Name, out streamBox);
            MessagesPanel.Children.Add(streamingBubble);
            ScrollToEnd(force: true);

            var history = BuildHistory();
            var received = new StringBuilder();
            var progress = new Progress<string>(chunk =>
            {
                received.Append(chunk);
                streamBox.Text = received.ToString();
                ScrollToEnd(force: false);
            });

            var assistant = new ChatMessage { Role = ChatMessage.AssistantRole, Model = model.Name };
            var usage = new TokenUsage();
            SetChatBusy(true);
            _chatCts = new CancellationTokenSource();
            SetStatus("Thinking with " + model.Name + "…", StatusKind.Busy);
            try
            {
                assistant.Content = await _client.ChatAsync(model, PackSettings.Load(), ChatSystemPrompt, history, false, progress, usage, _chatCts.Token);
                if (string.IsNullOrWhiteSpace(assistant.Content))
                {
                    assistant.Content = "The model returned an empty answer. Try rephrasing, or pick another model.";
                    assistant.IsError = true;
                }

                SetStatus(assistant.IsError ? "Empty answer from " + model.Name : "Answered by " + model.Name + UsageText(usage),
                    assistant.IsError ? StatusKind.Warning : StatusKind.Success);
            }
            catch (OperationCanceledException)
            {
                assistant.Content = received.Length > 0 ? received + "\n\n(Stopped)" : "(Stopped before the model answered.)";
                SetStatus("Stopped", StatusKind.Normal);
            }
            catch (Exception ex)
            {
                assistant.Content = FriendlyError(ex, user.Attachments.Any(a => a.Kind == AttachmentKind.Image), model);
                assistant.IsError = true;
                SetStatus("Request failed", StatusKind.Error);
            }
            finally
            {
                _chatCts.Dispose();
                _chatCts = null;
                SetChatBusy(false);
                RecordUsage(model, usage);
            }

            MessagesPanel.Children.Remove(streamingBubble);
            _active.Messages.Add(assistant);
            _active.UpdatedUtc = DateTime.UtcNow;
            MessagesPanel.Children.Add(CreateBubble(assistant));
            ScrollToEnd(force: false);

            SaveChats();
            RefreshConversationCombo();
            InputBox.Focus();
        }

        /// <summary>The last N messages (within a character budget) so the model continues the discussion.</summary>
        private List<ChatMessage> BuildHistory()
        {
            var source = _active.Messages.Where(m => !m.IsError).ToList();
            var maxMessages = Math.Max(1, _settings.HistoryMessagesToSend + 1);
            var picked = new List<ChatMessage>();
            var chars = 0;
            var imagesKept = 0;

            for (var i = source.Count - 1; i >= 0 && picked.Count < maxMessages; i--)
            {
                var m = source[i];
                var length = ChatClient.ComposeContent(m).Length;
                if (picked.Count > 0 && chars + length > HistoryCharBudget) break;
                chars += length;

                // Keep images only for the two most recent messages that have them (saves bandwidth and context).
                var hasImages = m.Attachments.Any(a => a.Kind == AttachmentKind.Image);
                if (hasImages && imagesKept >= 2)
                {
                    m = new ChatMessage
                    {
                        Role = m.Role,
                        Content = m.Content + "\n(An image was attached earlier.)",
                        Attachments = m.Attachments.Where(a => a.Kind != AttachmentKind.Image).ToList()
                    };
                }
                else if (hasImages)
                {
                    imagesKept++;
                }

                picked.Insert(0, m);
            }

            // Chat APIs expect the conversation to start with a user turn.
            while (picked.Count > 1 && picked[0].Role != ChatMessage.UserRole) picked.RemoveAt(0);
            return picked;
        }

        private void SetChatBusy(bool busy)
        {
            SendButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            StopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RetryAfterError(ChatMessage error)
        {
            if (_chatCts != null || _active == null) return;
            var index = _active.Messages.IndexOf(error);
            if (index < 1) return;

            var user = _active.Messages[index - 1];
            if (user.Role != ChatMessage.UserRole) return;

            _active.Messages.RemoveRange(index - 1, 2);
            RenderConversation();
            _pending.Clear();
            _pending.AddRange(user.Attachments);
            RefreshAttachmentChips();
            Run(() => SendAsync(user.Content));
        }

        private static string MakeTitle(string text)
        {
            var oneLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return oneLine.Length <= 40 ? oneLine : oneLine.Substring(0, 40).TrimEnd() + "…";
        }

        private static string FriendlyError(Exception ex, bool hadImages, AiModel model)
        {
            string message = ex is AiServiceException ? ex.Message : ex.GetBaseException().Message;
            if (hadImages && !ChatClient.LooksVisionCapable(model))
            {
                return model.Name + " may not be able to read images. Pick a vision model (for example llava, llama3.2-vision, " +
                       "an OpenAI GPT-4o/GPT-5 model or a Claude model) and try again.\n\nDetails: " + message;
            }

            return ex is AiServiceException ? message : "The request failed: " + message;
        }

        /// <summary>Adds the tokens to the usage counters shown in Team Rules (counts only; no prompts are stored).</summary>
        private static void RecordUsage(AiModel model, TokenUsage usage)
        {
            if (model == null || usage == null || usage.IsEmpty)
            {
                return;
            }

            PackSettings pack = PackSettings.Load();
            pack.RecordUsage(model.Name, usage);
        }

        private static string UsageText(TokenUsage usage)
        {
            return usage == null || usage.IsEmpty ? string.Empty : " · " + TokenUsage.Short(usage.Total) + " tokens";
        }

        // ------------------------------------------------------------------ chat: suggestions and attachments

        private void OnSuggestion(object sender, RoutedEventArgs e)
        {
            var tag = (sender as FrameworkElement)?.Tag as string;
            EditorSnapshot snapshot;
            string prompt;

            switch (tag)
            {
                case "tests":
                    snapshot = EditorService.GetSelection();
                    prompt = "Write unit tests for this code. Use the test framework already used in the solution (xUnit if unsure). " +
                             "Cover normal, edge and error cases and follow Arrange-Act-Assert.";
                    break;
                case "bugs":
                    snapshot = EditorService.GetActiveDocument();
                    prompt = "Find bugs, security issues and performance problems in this file. For each one, explain it briefly and show the fix as code.";
                    break;
                default:
                    snapshot = EditorService.GetSelection();
                    prompt = "Explain what this code does, step by step, and point out anything risky.";
                    break;
            }

            if (snapshot == null)
            {
                ShowComposerNotice(tag == "bugs" ? "Open a code file in the editor first." : "Select some code in the editor first.");
                return;
            }

            AddPending(new[] { SnapshotToAttachment(snapshot) });
            Run(() => SendAsync(prompt));
        }

        private void OnAttach(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Attach files or images",
                Filter = AttachmentFactory.FileFilter,
                Multiselect = true
            };
            if (dialog.ShowDialog() != true) return;

            var errors = new List<string>();
            AddPending(AttachmentFactory.FromPaths(dialog.FileNames, errors));
            ShowErrors(errors);
        }

        private void OnPasteClipboard(object sender, RoutedEventArgs e)
        {
            if (AttachmentFactory.ClipboardHasAttachable())
            {
                PasteAttachmentsFromClipboard();
                return;
            }

            try
            {
                if (Clipboard.ContainsText())
                {
                    InputBox.Focus();
                    InputBox.SelectedText = Clipboard.GetText();
                    InputBox.CaretIndex = InputBox.SelectionStart + InputBox.SelectionLength;
                    InputBox.SelectionLength = 0;
                    return;
                }
            }
            catch (ExternalException)
            {
                // Clipboard busy; fall through.
            }

            ShowComposerNotice("The clipboard is empty.");
        }

        private void OnAttachSelection(object sender, RoutedEventArgs e)
        {
            var snapshot = EditorService.GetSelection() ?? EditorService.GetActiveDocument();
            if (snapshot == null)
            {
                ShowComposerNotice("Open a file and select code in the editor first.");
                return;
            }

            AddPending(new[] { SnapshotToAttachment(snapshot) });
            InputBox.Focus();
        }

        private void OnInputPasteCanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            if (e.Command != ApplicationCommands.Paste) return;
            if (ShouldPasteAsAttachment())
            {
                e.CanExecute = true;
                e.Handled = true;
            }
        }

        private void OnInputPasteExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Command != ApplicationCommands.Paste || !ShouldPasteAsAttachment()) return;
            PasteAttachmentsFromClipboard();
            e.Handled = true;
        }

        /// <summary>Files and pure images become attachments; anything with text (e.g. Excel cells) pastes as text.</summary>
        private static bool ShouldPasteAsAttachment()
        {
            try
            {
                return Clipboard.ContainsFileDropList() || (Clipboard.ContainsImage() && !Clipboard.ContainsText());
            }
            catch (ExternalException)
            {
                return false;
            }
        }

        private void PasteAttachmentsFromClipboard()
        {
            var errors = new List<string>();
            var items = AttachmentFactory.FromClipboard(errors);
            AddPending(items);
            ShowErrors(errors);
            if (items.Count > 0) SetStatus("Pasted " + items.Count + " attachment(s) from the clipboard", StatusKind.Success);
        }

        private void OnPreviewDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        private void OnPreviewDrop(object sender, DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            e.Handled = true;

            ChatTab.IsChecked = true;
            var errors = new List<string>();
            AddPending(AttachmentFactory.FromPaths(files, errors));
            ShowErrors(errors);
        }

        private static Attachment SnapshotToAttachment(EditorSnapshot snapshot)
        {
            var name = snapshot.FileName ?? "code.cs";
            if (snapshot.IsSelection)
            {
                var lineCount = snapshot.Text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Length;
                var ext = Path.GetExtension(name);
                name = Path.GetFileNameWithoutExtension(name) + " (lines " + snapshot.StartLine + "-" + (snapshot.StartLine + lineCount - 1) + ")" + ext;
            }

            return AttachmentFactory.FromText(snapshot.Text, name);
        }

        private void AddPending(IEnumerable<Attachment> items)
        {
            foreach (var item in items)
            {
                if (item.Kind == AttachmentKind.Text && _pending.Any(p => p.Kind == AttachmentKind.Text && p.Name == item.Name && p.TextContent == item.TextContent))
                {
                    continue; // already attached
                }

                _pending.Add(item);
            }

            RefreshAttachmentChips();
            UpdateVisionNotice();
        }

        private void RefreshAttachmentChips()
        {
            var chips = new List<UIElement>();
            foreach (var attachment in _pending)
            {
                chips.Add(CreateAttachmentChip(attachment, removable: true));
            }

            AttachmentsList.ItemsSource = chips;
            AttachmentsList.Visibility = chips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private FrameworkElement CreateAttachmentChip(Attachment attachment, bool removable)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4),
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = attachment.Name + " · " + attachment.DisplaySize
            };
            border.SetResourceReference(Border.BackgroundProperty, "CC.SurfaceAlt");
            border.SetResourceReference(Border.BorderBrushProperty, "CC.Border");

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            if (attachment.Kind == AttachmentKind.Image)
            {
                var bitmap = AttachmentFactory.ToBitmap(attachment);
                var image = new Image
                {
                    Source = bitmap,
                    Height = 40,
                    MaxWidth = 80,
                    Stretch = Stretch.Uniform,
                    Cursor = Cursors.Hand,
                    ToolTip = "Click to preview"
                };
                image.MouseLeftButtonUp += (s, e) => ShowImagePreview(bitmap, attachment.Name);
                panel.Children.Add(image);
            }
            else
            {
                var icon = new TextBlock
                {
                    Text = "",
                    FontFamily = (FontFamily)FindResource("CC.IconFont"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 6, 0)
                };
                icon.SetResourceReference(TextBlock.ForegroundProperty, "CC.Accent");
                panel.Children.Add(icon);
            }

            var label = new TextBlock
            {
                Text = attachment.Name,
                MaxWidth = 180,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                Margin = new Thickness(6, 0, 4, 0)
            };
            panel.Children.Add(label);

            if (removable)
            {
                var remove = new Button
                {
                    Content = "",
                    ToolTip = "Remove",
                    Width = 22,
                    Height = 22,
                    FontSize = 10
                };
                remove.SetResourceReference(StyleProperty, "CC.IconButton");
                remove.Click += (s, e) =>
                {
                    _pending.Remove(attachment);
                    RefreshAttachmentChips();
                    UpdateVisionNotice();
                };
                panel.Children.Add(remove);
            }

            border.Child = panel;
            return border;
        }

        private void ShowImagePreview(BitmapSource bitmap, string title)
        {
            if (bitmap == null) return;
            var window = new Window
            {
                Title = title ?? "Image",
                Width = Math.Min(SystemParameters.WorkArea.Width * 0.8, bitmap.PixelWidth + 40),
                Height = Math.Min(SystemParameters.WorkArea.Height * 0.8, bitmap.PixelHeight + 60),
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new Image { Source = bitmap, Stretch = Stretch.None }
                },
                ShowInTaskbar = false
            };
            window.Background = ThemeManager.Get(this, "CC.Bg");
            window.KeyDown += (s, e) => { if (e.Key == Key.Escape) window.Close(); };
            window.Owner = Window.GetWindow(this);
            window.ShowDialog();
        }

        private void UpdateVisionNotice()
        {
            var hasImages = _pending.Any(a => a.Kind == AttachmentKind.Image);
            if (hasImages && CurrentModel != null && !ChatClient.LooksVisionCapable(CurrentModel))
            {
                ShowComposerNotice(CurrentModel.Name + " may not read images. Choose a vision model (e.g. llava) for screenshot review.");
            }
            else if (ComposerNotice.Text.Contains("may not read images"))
            {
                HideComposerNotice();
            }
        }

        private void ShowErrors(List<string> errors)
        {
            if (errors.Count > 0) ShowComposerNotice(string.Join("\n", errors));
        }

        private void ShowComposerNotice(string text)
        {
            ComposerNotice.Text = text;
            ComposerNotice.Visibility = Visibility.Visible;
        }

        private void HideComposerNotice()
        {
            ComposerNotice.Text = string.Empty;
            ComposerNotice.Visibility = Visibility.Collapsed;
        }

        // ------------------------------------------------------------------ chat: rendering

        private void RenderConversation()
        {
            MessagesPanel.Children.Clear();
            var hasMessages = _active != null && _active.Messages.Count > 0;
            EmptyChatPanel.Visibility = hasMessages ? Visibility.Collapsed : Visibility.Visible;
            if (!hasMessages) return;

            foreach (var message in _active.Messages)
            {
                MessagesPanel.Children.Add(CreateBubble(message));
            }

            Dispatcher.BeginInvoke(new Action(() => MessagesScroll.ScrollToEnd()), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private FrameworkElement CreateBubble(ChatMessage message)
        {
            if (message.Role == ChatMessage.UserRole) return CreateUserBubble(message);
            return message.IsError ? CreateErrorBubble(message) : CreateAssistantBubble(message);
        }

        private FrameworkElement CreateUserBubble(ChatMessage message)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(12, 12, 4, 12),
                Padding = new Thickness(12, 8, 12, 6),
                Margin = new Thickness(40, 6, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            border.SetResourceReference(Border.BackgroundProperty, "CC.UserBubble");

            var stack = new StackPanel();
            if (message.Attachments.Count > 0)
            {
                var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
                foreach (var attachment in message.Attachments)
                {
                    if (attachment.Kind == AttachmentKind.Image)
                    {
                        var bitmap = AttachmentFactory.ToBitmap(attachment);
                        var image = new Image
                        {
                            Source = bitmap,
                            MaxHeight = 160,
                            MaxWidth = 260,
                            Stretch = Stretch.Uniform,
                            Margin = new Thickness(0, 0, 6, 6),
                            Cursor = Cursors.Hand,
                            ToolTip = attachment.Name + " (click to enlarge)"
                        };
                        var name = attachment.Name;
                        image.MouseLeftButtonUp += (s, e) => ShowImagePreview(bitmap, name);
                        wrap.Children.Add(image);
                    }
                    else
                    {
                        wrap.Children.Add(CreateAttachmentChip(attachment, removable: false));
                    }
                }

                stack.Children.Add(wrap);
            }

            stack.Children.Add(CreateSelectableText(message.Content));
            stack.Children.Add(CreateBubbleFooter("You", null, message, alignRight: true));
            border.Child = stack;
            return border;
        }

        private FrameworkElement CreateAssistantBubble(ChatMessage message)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(12, 12, 12, 4),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 8, 12, 6),
                Margin = new Thickness(0, 6, 24, 6)
            };
            border.SetResourceReference(Border.BackgroundProperty, "CC.AssistantBubble");
            border.SetResourceReference(Border.BorderBrushProperty, "CC.Border");

            var stack = new StackPanel();
            stack.Children.Add(CreateAssistantHeader(message.Model, message.TimestampUtc, null));
            stack.Children.Add(MarkdownRenderer.Render(message.Content, CopyToClipboard, InsertIntoEditor));
            stack.Children.Add(CreateBubbleFooter(null, message.Model, message, alignRight: false));
            border.Child = stack;
            return border;
        }

        private FrameworkElement CreateErrorBubble(ChatMessage message)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 6, 24, 6)
            };
            border.SetResourceReference(Border.BackgroundProperty, "CC.ErrorSoft");

            var stack = new StackPanel();
            var text = CreateSelectableText(message.Content);
            text.SetResourceReference(ForegroundProperty, "CC.Error");
            stack.Children.Add(text);

            var retry = new Button { Content = "Retry", Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            retry.SetResourceReference(StyleProperty, "CC.OutlineButton");
            retry.Click += (s, e) => RetryAfterError(message);
            stack.Children.Add(retry);

            border.Child = stack;
            return border;
        }

        private FrameworkElement CreateStreamingBubble(string model, out TextBox streamBox)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(12, 12, 12, 4),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 8, 12, 10),
                Margin = new Thickness(0, 6, 24, 6)
            };
            border.SetResourceReference(Border.BackgroundProperty, "CC.AssistantBubble");
            border.SetResourceReference(Border.BorderBrushProperty, "CC.Accent");

            var stack = new StackPanel();
            stack.Children.Add(CreateAssistantHeader(model, DateTime.UtcNow, "thinking…"));
            streamBox = CreateSelectableText("…");
            stack.Children.Add(streamBox);
            border.Child = stack;
            return border;
        }

        private FrameworkElement CreateAssistantHeader(string model, DateTime timestampUtc, string state)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };

            var badge = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 8, 0) };
            badge.SetResourceReference(Border.BackgroundProperty, "CC.Accent");
            var glyph = new TextBlock
            {
                Text = "",
                FontFamily = (FontFamily)FindResource("CC.IconFont"),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "CC.AccentText");
            badge.Child = glyph;
            panel.Children.Add(badge);

            var name = new TextBlock { Text = "Code Compass", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(name);

            var meta = new TextBlock
            {
                Text = "  ·  " + (model ?? "model") + "  ·  " + (state ?? timestampUtc.ToLocalTime().ToString("HH:mm")),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            meta.SetResourceReference(TextBlock.ForegroundProperty, "CC.SubtleText");
            panel.Children.Add(meta);
            return panel;
        }

        private FrameworkElement CreateBubbleFooter(string who, string model, ChatMessage message, bool alignRight)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 0, 0)
            };

            if (who != null)
            {
                var meta = new TextBlock
                {
                    Text = who + " · " + message.TimestampUtc.ToLocalTime().ToString("HH:mm"),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                meta.SetResourceReference(TextBlock.ForegroundProperty, "CC.SubtleText");
                panel.Children.Add(meta);
            }

            var copy = MarkdownRenderer.SmallButton("Copy", "Copy the whole message");
            copy.Click += (s, e) =>
            {
                CopyToClipboard(message.Content);
                SetStatus("Message copied", StatusKind.Success);
            };
            panel.Children.Add(copy);
            return panel;
        }

        private TextBox CreateSelectableText(string text)
        {
            var box = new TextBox
            {
                Text = text ?? string.Empty,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                FontSize = 13.5
            };
            box.SetResourceReference(ForegroundProperty, "CC.Text");
            box.SetResourceReference(TextBox.SelectionBrushProperty, "CC.Accent");
            return box;
        }

        private void ScrollToEnd(bool force)
        {
            var nearBottom = MessagesScroll.VerticalOffset >= MessagesScroll.ScrollableHeight - 120;
            if (force || nearBottom) MessagesScroll.ScrollToEnd();
        }

        // ------------------------------------------------------------------ code review

        /// <summary>Called by the "Review Selection with Code Compass" editor command.</summary>
        public void ReviewSelectionFromCommand()
        {
            ReviewTab.IsChecked = true;
            SourceSelection.IsChecked = true;
            Run(ReviewAsync);
        }

        private void OnSourceChanged(object sender, RoutedEventArgs e)
        {
            if (PasteCodeBox == null) return; // during InitializeComponent
            PasteCodeBox.Visibility = SourcePaste.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnReview(object sender, RoutedEventArgs e)
        {
            Run(ReviewAsync);
        }

        private void OnStopReview(object sender, RoutedEventArgs e)
        {
            if (_reviewCts != null) _reviewCts.Cancel();
        }

        private async Task ReviewAsync()
        {
            if (_reviewCts != null) return;
            EnsureSolutionContext();

            var model = CurrentModel;
            if (model == null)
            {
                ShowReviewMessage("Choose a model first (top of the window).", "CC.Warning");
                return;
            }

            EditorSnapshot snapshot;
            if (SourcePaste.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(PasteCodeBox.Text))
                {
                    ShowReviewMessage("Paste some code into the box first.", "CC.Warning");
                    PasteCodeBox.Focus();
                    return;
                }

                snapshot = new EditorSnapshot { Text = PasteCodeBox.Text, StartLine = 1 };
            }
            else if (SourceSelection.IsChecked == true)
            {
                snapshot = EditorService.GetSelection();
                if (snapshot == null)
                {
                    ShowReviewMessage("Nothing is selected in the editor. Select some code, or choose Open file.", "CC.Warning");
                    return;
                }
            }
            else
            {
                snapshot = EditorService.GetActiveDocument();
                if (snapshot == null)
                {
                    ShowReviewMessage("Open a code file in the editor first.", "CC.Warning");
                    return;
                }
            }

            ReloadRules(snapshot.FilePath);
            var lineCount = snapshot.Text.Replace("\r\n", "\n").Split('\n').Length;

            ReviewResultsPanel.Children.Clear();
            ReviewEmptyPanel.Visibility = Visibility.Collapsed;
            ShowReviewMessage("Reviewing " + lineCount + " lines" + (snapshot.FileName != null ? " of " + snapshot.FileName : string.Empty) +
                              " with " + model.Name + "… Small models can take a minute.", "CC.SubtleText");
            SetReviewBusy(true);
            SetStatus("Reviewing with " + model.Name + "…", StatusKind.Busy);
            var usage = new TokenUsage();

            _reviewCts = new CancellationTokenSource();
            try
            {
                var request = new ReviewRequest
                {
                    Code = snapshot.Text,
                    FileName = snapshot.FileName ?? "pasted.cs",
                    StartLine = snapshot.StartLine,
                    IncludeImprovedCode = IncludeImprovedCheck.IsChecked == true
                };

                var result = await _reviewService.ReviewAsync(model, PackSettings.Load(), request, _rules, _settings.RuleCharBudget, usage, _reviewCts.Token);
                _lastReview = result;
                _lastReviewSnapshot = snapshot;
                RenderReview(result, snapshot, model.Name);

                var errors = result.Findings.Count(f => f.NormalizedSeverity == "Error");
                SetStatus("Review finished · " + result.Findings.Count + " finding(s)" + (errors > 0 ? ", " + errors + " error(s)" : string.Empty),
                    errors > 0 ? StatusKind.Warning : StatusKind.Success);
            }
            catch (OperationCanceledException)
            {
                ReviewResultsPanel.Children.Clear();
                ShowReviewMessage("Review stopped.", "CC.SubtleText");
                SetStatus("Review stopped", StatusKind.Normal);
            }
            catch (Exception ex)
            {
                ReviewResultsPanel.Children.Clear();
                ShowReviewMessage(FriendlyError(ex, false, model), "CC.Error");
                SetStatus("Review failed", StatusKind.Error);
            }
            finally
            {
                _reviewCts.Dispose();
                _reviewCts = null;
                SetReviewBusy(false);
                RecordUsage(model, usage);
            }
        }

        private void SetReviewBusy(bool busy)
        {
            ReviewButton.IsEnabled = !busy;
            StopReviewButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            ReviewProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowReviewMessage(string text, string brushKey)
        {
            ReviewEmptyPanel.Visibility = Visibility.Collapsed;
            var card = NewCard();
            var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            card.Child = block;
            ReviewResultsPanel.Children.Add(card);
        }

        private void RenderReview(ReviewResult result, EditorSnapshot snapshot, string model)
        {
            ReviewResultsPanel.Children.Clear();
            ReviewResultsPanel.Children.Add(CreateSummaryCard(result, snapshot, model));

            if (!string.IsNullOrEmpty(result.RawText))
            {
                var raw = NewCard();
                raw.Child = MarkdownRenderer.Render(result.RawText, CopyToClipboard, InsertIntoEditor);
                ReviewResultsPanel.Children.Add(raw);
                return;
            }

            foreach (var finding in result.Findings)
            {
                ReviewResultsPanel.Children.Add(CreateFindingCard(finding, snapshot));
            }

            if (!string.IsNullOrWhiteSpace(result.ImprovedCode))
            {
                var title = new TextBlock { Text = "Improved code", FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(2, 10, 0, 2) };
                ReviewResultsPanel.Children.Add(title);

                Action<string> replace = null;
                if (snapshot.IsSelection) replace = code => ReplaceReviewedSelection(code, snapshot);
                ReviewResultsPanel.Children.Add(MarkdownRenderer.CreateCodeBlock(result.ImprovedCode, "csharp", CopyToClipboard, replace, "Replace selection"));
            }

            ReviewScroll.ScrollToTop();
        }

        private Border CreateSummaryCard(ReviewResult result, EditorSnapshot snapshot, string model)
        {
            var card = NewCard();
            var stack = new StackPanel();

            var errors = result.Findings.Count(f => f.NormalizedSeverity == "Error");
            var warnings = result.Findings.Count(f => f.NormalizedSeverity == "Warning");
            var infos = result.Findings.Count(f => f.NormalizedSeverity == "Info");

            var verdict = string.IsNullOrWhiteSpace(result.Verdict)
                ? (errors > 0 ? "Merge after fixing errors" : result.Findings.Count == 0 && result.RawText == null ? "Safe to merge" : "Review the findings")
                : result.Verdict;
            var verdictKey = verdict.IndexOf("rework", StringComparison.OrdinalIgnoreCase) >= 0 || errors > 0 ? "CC.Error"
                : verdict.IndexOf("safe", StringComparison.OrdinalIgnoreCase) >= 0 ? "CC.Success" : "CC.Warning";

            var head = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
            var dot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, verdictKey);
            DockPanel.SetDock(dot, Dock.Left);
            head.Children.Add(dot);
            head.Children.Add(new TextBlock { Text = verdict, FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(head);

            var pills = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            pills.Children.Add(CreatePill(errors + (errors == 1 ? " Error" : " Errors"), "Error"));
            pills.Children.Add(CreatePill(warnings + (warnings == 1 ? " Warning" : " Warnings"), "Warning"));
            pills.Children.Add(CreatePill(infos + " Info", "Info"));
            stack.Children.Add(pills);

            if (!string.IsNullOrWhiteSpace(result.Summary))
            {
                stack.Children.Add(new TextBlock { Text = result.Summary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
            }

            var meta = new TextBlock
            {
                Text = (snapshot.FileName ?? "Pasted code") + (snapshot.IsSelection ? " · selection from line " + snapshot.StartLine : string.Empty) +
                       " · " + model + " · " + (_rules != null ? _rules.SourceDescription : "no rules"),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            meta.SetResourceReference(TextBlock.ForegroundProperty, "CC.SubtleText");
            stack.Children.Add(meta);

            var actions = new WrapPanel();
            var discuss = new Button { Content = "Discuss in chat", Margin = new Thickness(0, 0, 6, 0) };
            discuss.SetResourceReference(StyleProperty, "CC.OutlineButton");
            discuss.Click += (s, e) => DiscussReviewInChat();
            actions.Children.Add(discuss);

            var copyReport = new Button { Content = "Copy report" };
            copyReport.SetResourceReference(StyleProperty, "CC.OutlineButton");
            copyReport.Click += (s, e) =>
            {
                CopyToClipboard(BuildReportMarkdown(result, snapshot));
                SetStatus("Review report copied as Markdown", StatusKind.Success);
            };
            actions.Children.Add(copyReport);
            stack.Children.Add(actions);

            card.Child = stack;
            return card;
        }

        private Border CreateFindingCard(ReviewFinding finding, EditorSnapshot snapshot)
        {
            var severity = finding.NormalizedSeverity;
            var outer = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 10) };
            outer.SetResourceReference(Border.BackgroundProperty, "CC.Surface");
            outer.SetResourceReference(Border.BorderBrushProperty, "CC.Border");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var strip = new Border { CornerRadius = new CornerRadius(8, 0, 0, 8) };
            strip.SetResourceReference(Border.BackgroundProperty, "CC." + severity);
            grid.Children.Add(strip);

            var stack = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
            Grid.SetColumn(stack, 1);

            var header = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            header.Children.Add(CreatePill(severity, severity));
            if (!string.IsNullOrWhiteSpace(finding.RuleId))
            {
                var rule = new TextBlock { Text = finding.RuleId, FontFamily = MarkdownRenderer.CodeFont, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                rule.SetResourceReference(TextBlock.ForegroundProperty, "CC.SubtleText");
                header.Children.Add(rule);
            }

            if (finding.Line.HasValue)
            {
                var line = finding.Line.Value;
                var lineButton = MarkdownRenderer.SmallButton("Line " + line + " →", "Go to this line in the editor");
                lineButton.IsEnabled = !string.IsNullOrEmpty(snapshot.FilePath);
                lineButton.Click += (s, e) =>
                {
                    if (!EditorService.GoToLine(snapshot.FilePath, line)) SetStatus("Could not open line " + line, StatusKind.Warning);
                };
                header.Children.Add(lineButton);
            }

            stack.Children.Add(header);
            stack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(finding.Title) ? "Finding" : finding.Title,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            });

            if (!string.IsNullOrWhiteSpace(finding.Problem))
            {
                stack.Children.Add(new TextBlock { Text = finding.Problem, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
            }

            if (!string.IsNullOrWhiteSpace(finding.Suggestion))
            {
                var fix = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
                fix.Inlines.Add(new System.Windows.Documents.Run("How to fix: ") { FontWeight = FontWeights.SemiBold });
                fix.Inlines.Add(new System.Windows.Documents.Run(finding.Suggestion));
                stack.Children.Add(fix);
            }

            if (!string.IsNullOrWhiteSpace(finding.FixedCode))
            {
                stack.Children.Add(MarkdownRenderer.CreateCodeBlock(finding.FixedCode, "suggested fix", CopyToClipboard, InsertIntoEditor));
            }

            var ask = MarkdownRenderer.SmallButton("Ask in chat →", "Continue this finding in the Chat tab");
            ask.HorizontalAlignment = HorizontalAlignment.Left;
            ask.Click += (s, e) => AskAboutFinding(finding);
            stack.Children.Add(ask);

            grid.Children.Add(stack);
            outer.Child = grid;
            return outer;
        }

        private FrameworkElement CreatePill(string text, string severity)
        {
            var pill = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 1, 8, 2), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            pill.SetResourceReference(Border.BackgroundProperty, "CC." + severity + "Soft");
            var label = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold };
            label.SetResourceReference(TextBlock.ForegroundProperty, "CC." + severity);
            pill.Child = label;
            return pill;
        }

        private Border NewCard()
        {
            var card = new Border { Margin = new Thickness(0, 0, 0, 10) };
            card.SetResourceReference(StyleProperty, "CC.Card");
            return card;
        }

        private void ReplaceReviewedSelection(string code, EditorSnapshot snapshot)
        {
            var current = EditorService.GetSelection();
            if (current == null || current.Text != snapshot.Text ||
                !string.Equals(current.FilePath, snapshot.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("The selection changed. Select the reviewed code again, then click Replace selection.", StatusKind.Warning);
                return;
            }

            InsertIntoEditor(code);
        }

        private void AskAboutFinding(ReviewFinding finding)
        {
            if (_lastReviewSnapshot != null) AddPending(new[] { SnapshotToAttachment(_lastReviewSnapshot) });
            ChatTab.IsChecked = true;
            InputBox.Text = "Explain this review finding and show the complete fix for my code:\n" +
                            "[" + finding.NormalizedSeverity + "] " + finding.RuleId + " " + finding.Title +
                            (finding.Line.HasValue ? " (line " + finding.Line + ")" : string.Empty) + ": " + finding.Problem;
            InputBox.CaretIndex = InputBox.Text.Length;
            InputBox.Focus();
        }

        private void DiscussReviewInChat()
        {
            if (_lastReview == null || _lastReviewSnapshot == null) return;
            AddPending(new[]
            {
                SnapshotToAttachment(_lastReviewSnapshot),
                AttachmentFactory.FromText(BuildReportMarkdown(_lastReview, _lastReviewSnapshot), "code-review-report.md")
            });
            ChatTab.IsChecked = true;
            InputBox.Text = "Here is my code and its review report. Help me fix the issues one by one, starting with the errors.";
            InputBox.CaretIndex = InputBox.Text.Length;
            InputBox.Focus();
        }

        private static string BuildReportMarkdown(ReviewResult result, EditorSnapshot snapshot)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Code review: " + (snapshot.FileName ?? "pasted code"));
            if (!string.IsNullOrWhiteSpace(result.Verdict)) sb.AppendLine("**Verdict:** " + result.Verdict);
            if (!string.IsNullOrWhiteSpace(result.Summary)) sb.AppendLine().AppendLine(result.Summary);
            if (!string.IsNullOrEmpty(result.RawText)) sb.AppendLine().AppendLine(result.RawText);

            foreach (var f in result.Findings)
            {
                sb.AppendLine();
                sb.AppendLine("## [" + f.NormalizedSeverity + "] " + (f.RuleId ?? "General") + ": " + f.Title +
                              (f.Line.HasValue ? " (line " + f.Line + ")" : string.Empty));
                if (!string.IsNullOrWhiteSpace(f.Problem)) sb.AppendLine(f.Problem);
                if (!string.IsNullOrWhiteSpace(f.Suggestion)) sb.AppendLine().AppendLine("**How to fix:** " + f.Suggestion);
                if (!string.IsNullOrWhiteSpace(f.FixedCode))
                {
                    sb.AppendLine().AppendLine("```csharp").AppendLine(f.FixedCode).AppendLine("```");
                }
            }

            return sb.ToString();
        }

        // ------------------------------------------------------------------ helpers

        private void CopyToClipboard(string text)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Clipboard.SetText(text ?? string.Empty);
                    return;
                }
                catch (ExternalException)
                {
                    Thread.Sleep(60); // clipboard briefly locked by another app
                }
            }

            SetStatus("The clipboard is busy. Try again.", StatusKind.Warning);
        }

        private void InsertIntoEditor(string code)
        {
            if (EditorService.InsertOrReplaceSelection(code))
            {
                SetStatus("Inserted into the editor (Ctrl+Z to undo)", StatusKind.Success);
            }
            else
            {
                SetStatus("Open a code file in the editor first.", StatusKind.Warning);
            }
        }

        private void SetStatus(string text, StatusKind kind)
        {
            StatusText.Text = text;
            string key;
            switch (kind)
            {
                case StatusKind.Busy: key = "CC.Accent"; break;
                case StatusKind.Success: key = "CC.Success"; break;
                case StatusKind.Warning: key = "CC.Warning"; break;
                case StatusKind.Error: key = "CC.Error"; break;
                default: key = "CC.SubtleText"; break;
            }

            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key);
        }

        /// <summary>Runs async UI work on the VS main thread and reports failures in the status bar.</summary>
        private void Run(Func<Task> work)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await work();
                }
                catch (OperationCanceledException)
                {
                    SetStatus("Cancelled", StatusKind.Normal);
                }
                catch (Exception ex)
                {
                    SetStatus("Error: " + ex.Message, StatusKind.Error);
                }
            });
        }
    }
}
