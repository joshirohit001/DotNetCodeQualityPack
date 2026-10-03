using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DotNetCodeQualityPack.Ai;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace DotNetCodeQualityPack.CodeCompass
{
    /// <summary>Opens the AI provider settings as a modal dialog. Used by Code Compass and Team Rules.</summary>
    internal static class AiProvidersDialog
    {
        /// <summary>True when the user saved changes.</summary>
        public static bool Show()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var control = new AiProvidersControl();
            var window = new DialogWindow
            {
                Title = "AI providers",
                Content = control,
                Width = 680,
                Height = 760,
                MinWidth = 480,
                MinHeight = 420,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                HasMinimizeButton = false,
                HasMaximizeButton = false,
            };

            control.Finished += saved => window.DialogResult = saved;
            return window.ShowModal() == true;
        }
    }

    /// <summary>
    /// Settings for OpenAI, Anthropic and Ollama: use it or not, API URL, API key (encrypted with DPAPI, never shown
    /// again) and model names. Every provider is edited the same way; nothing here is provider-specific except labels.
    /// </summary>
    internal partial class AiProvidersControl : UserControl
    {
        private readonly AiProviderSettings _settings = AiProviderSettings.Load();
        private readonly List<ProviderCard> _cards = new List<ProviderCard>();

        public AiProvidersControl()
        {
            InitializeComponent();
            ThemeManager.Apply(this, new CodeCompassStore().LoadSettings().Theme);

            foreach (ProviderConfig config in _settings.All)
            {
                var card = new ProviderCard(config, _settings, AiProviders.Get(config.Kind));
                _cards.Add(card);
                CardsPanel.Children.Add(card.Root);
            }
        }

        /// <summary>Raised with true after a successful save, false on cancel.</summary>
        public event Action<bool> Finished;

        private void OnSave(object sender, RoutedEventArgs e)
        {
            foreach (ProviderCard card in _cards)
            {
                string problem = card.Validate();
                if (problem != null)
                {
                    FooterText.Text = problem;
                    FooterText.SetResourceReference(TextBlock.ForegroundProperty, "CC.Error");
                    return;
                }
            }

            try
            {
                foreach (ProviderCard card in _cards)
                {
                    card.Apply();
                }

                _settings.Save();
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is System.Security.Cryptography.CryptographicException)
            {
                FooterText.Text = "Couldn't save the settings: " + ex.Message;
                FooterText.SetResourceReference(TextBlock.ForegroundProperty, "CC.Error");
                return;
            }

            Finished?.Invoke(true);
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            Finished?.Invoke(false);
        }

        /// <summary>The controls for one provider.</summary>
        private sealed class ProviderCard
        {
            private readonly ProviderConfig _config;
            private readonly AiProviderSettings _settings;
            private readonly IAiProvider _provider;
            private readonly CheckBox _enabled;
            private readonly TextBox _url;
            private readonly PasswordBox _key;
            private readonly TextBlock _keyStatus;
            private readonly Button _removeKey;
            private readonly TextBox _models;
            private readonly Button _test;
            private readonly TextBlock _testResult;
            private readonly StackPanel _details;
            private bool _removeKeyRequested;

            public ProviderCard(ProviderConfig config, AiProviderSettings settings, IAiProvider provider)
            {
                _config = config;
                _settings = settings;
                _provider = provider;

                Root = new Border { Margin = new Thickness(0, 0, 0, 12) };
                Root.SetResourceReference(FrameworkElement.StyleProperty, "CC.Card");
                var stack = new StackPanel();
                Root.Child = stack;

                _enabled = new CheckBox
                {
                    Content = new TextBlock { Text = "Use " + provider.DisplayName, FontWeight = FontWeights.SemiBold, FontSize = 14 },
                    IsChecked = config.Enabled,
                    VerticalContentAlignment = VerticalAlignment.Center,
                };
                stack.Children.Add(_enabled);
                stack.Children.Add(Subtle(provider.Description, new Thickness(22, 2, 0, 8)));

                _details = new StackPanel { Margin = new Thickness(22, 0, 0, 0) };
                stack.Children.Add(_details);

                // API URL
                _url = new TextBox { Text = config.BaseUrl ?? string.Empty, Tag = provider.DefaultBaseUrl };
                _url.SetResourceReference(FrameworkElement.StyleProperty, "CC.TextBox");
                AddRow("API URL", _url, "Leave empty for " + provider.DefaultBaseUrl + ".");

                // API key
                _key = new PasswordBox();
                _key.SetResourceReference(FrameworkElement.StyleProperty, "CC.PasswordBox");
                _keyStatus = Subtle(string.Empty, new Thickness(0));
                _removeKey = MarkdownRenderer.SmallButton("Remove key", "Delete the saved key");
                _removeKey.Click += (s, e) =>
                {
                    _removeKeyRequested = true;
                    _key.Password = string.Empty;
                    UpdateKeyStatus();
                };
                var keyNote = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
                keyNote.Children.Add(_keyStatus);
                keyNote.Children.Add(_removeKey);
                AddRow(provider.RequiresApiKey ? "API key" : "API key (optional)", _key, null, keyNote);
                UpdateKeyStatus();

                // Models
                _models = new TextBox
                {
                    Text = config.Models ?? string.Empty,
                    Tag = provider.ModelExamples,
                    TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = false,
                };
                _models.SetResourceReference(FrameworkElement.StyleProperty, "CC.TextBox");
                AddRow(
                    "Models",
                    _models,
                    "Comma-separated model names. They always appear in the model list; models the provider lists are added after them."
                        + (provider.Kind == AiProviderKind.Ollama ? " Any model you pulled with 'ollama pull' works." : string.Empty));

                // Test
                var testRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                _test = new Button { Content = "Test connection", Margin = new Thickness(0, 0, 10, 0) };
                _test.SetResourceReference(FrameworkElement.StyleProperty, "CC.OutlineButton");
                _test.Click += (s, e) => _ = ThreadHelper.JoinableTaskFactory.RunAsync(TestAsync);
                DockPanel.SetDock(_test, Dock.Left);
                testRow.Children.Add(_test);
                _testResult = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
                testRow.Children.Add(_testResult);
                _details.Children.Add(testRow);

                _enabled.Checked += (s, e) => UpdateEnabled();
                _enabled.Unchecked += (s, e) => UpdateEnabled();
                UpdateEnabled();
            }

            public Border Root { get; }

            /// <summary>Null when the card can be saved, otherwise what to fix.</summary>
            public string Validate()
            {
                if (_enabled.IsChecked != true)
                {
                    return null; // an unused provider may keep half-filled settings
                }

                if (!AiProviderSettings.IsValidUrl(_url.Text))
                {
                    return _provider.DisplayName + ": the API URL must be an http(s) address, for example " + _provider.DefaultBaseUrl + ".";
                }

                if (_provider.RequiresApiKey && !HasKeyAfterSave)
                {
                    return _provider.DisplayName + ": enter your API key, or untick \"Use " + _provider.DisplayName + "\".";
                }

                if (HasKeyAfterSave && SendsKeyUnencrypted(AiProviderSettings.ResolveBaseUrl(_config.Kind, _url.Text)))
                {
                    return _provider.DisplayName + ": the API URL uses http:// on another machine. Use https:// so your API key isn't sent unencrypted, or remove the key.";
                }

                return null;
            }

            private static bool SendsKeyUnencrypted(string url) =>
                Uri.TryCreate(url, UriKind.Absolute, out Uri uri) && uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback;

            public void Apply()
            {
                _config.Enabled = _enabled.IsChecked == true;
                _config.BaseUrl = (_url.Text ?? string.Empty).Trim();
                _config.Models = string.Join(", ", (_models.Text ?? string.Empty)
                    .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(m => m.Trim())
                    .Where(m => m.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase));

                if (_key.Password.Trim().Length > 0)
                {
                    _settings.SetApiKey(_config.Kind, _key.Password);
                }
                else if (_removeKeyRequested)
                {
                    _settings.SetApiKey(_config.Kind, null);
                }

                _key.Password = string.Empty;
            }

            private bool HasKeyAfterSave => _key.Password.Trim().Length > 0 || (!_removeKeyRequested && _settings.HasApiKey(_config.Kind));

            private async Task TestAsync()
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!AiProviderSettings.IsValidUrl(_url.Text))
                {
                    ShowResult("The API URL must be an http(s) address.", "CC.Error");
                    return;
                }

                string typed = _key.Password.Trim();
                string key = typed.Length > 0 ? typed : _removeKeyRequested ? null : _settings.GetApiKey(_config.Kind);
                var connection = new AiConnection(AiProviderSettings.ResolveBaseUrl(_config.Kind, _url.Text), key);
                if (_provider.RequiresApiKey && !connection.HasApiKey)
                {
                    ShowResult("Enter your API key first.", "CC.Warning");
                    return;
                }

                _test.IsEnabled = false;
                ShowResult("Connecting to " + connection.BaseUrl + "…", "CC.SubtleText");
                try
                {
                    IReadOnlyList<string> models;
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    {
                        models = await Task.Run(() => _provider.ListModelsAsync(connection, cts.Token));
                    }

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    ShowResult(
                        models.Count == 0
                            ? "Connected. The provider lists no models; type the model names you want to use."
                            : "Connected: " + models.Count + " model(s), e.g. " + string.Join(", ", models.Take(4)) + (models.Count > 4 ? "…" : "."),
                        "CC.Success");
                }
                catch (AiServiceException ex)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    ShowResult(ex.Message, "CC.Error");
                }
                catch (OperationCanceledException)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    ShowResult("No answer within 30 seconds.", "CC.Error");
                }
                catch (Exception ex)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    ShowResult("The test failed: " + ex.Message, "CC.Error");
                }
                finally
                {
                    _test.IsEnabled = true;
                }
            }

            private void ShowResult(string text, string brushKey)
            {
                _testResult.Text = text;
                _testResult.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            }

            private void UpdateKeyStatus()
            {
                bool saved = !_removeKeyRequested && _settings.HasApiKey(_config.Kind);
                _keyStatus.Text = saved
                    ? (_settings.GetApiKey(_config.Kind) != null ? "A key is saved (encrypted). Type a new one to replace it." : "The saved key can't be read on this PC or account; enter it again.")
                    : _removeKeyRequested ? "The key will be removed when you save." : "No key saved.";
                _removeKey.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
            }

            private void UpdateEnabled()
            {
                _details.Opacity = _enabled.IsChecked == true ? 1.0 : 0.55;
            }

            private void AddRow(string label, FrameworkElement input, string note, FrameworkElement extra = null)
            {
                var title = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 4, 0, 3) };
                _details.Children.Add(title);
                _details.Children.Add(input);
                if (extra != null)
                {
                    _details.Children.Add(extra);
                }

                if (!string.IsNullOrEmpty(note))
                {
                    _details.Children.Add(Subtle(note, new Thickness(0, 3, 0, 4)));
                }
                else
                {
                    input.Margin = new Thickness(0, 0, 0, 2);
                }
            }

            private static TextBlock Subtle(string text, Thickness margin)
            {
                var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = margin };
                t.SetResourceReference(TextBlock.ForegroundProperty, "CC.SubtleText");
                return t;
            }
        }
    }
}
