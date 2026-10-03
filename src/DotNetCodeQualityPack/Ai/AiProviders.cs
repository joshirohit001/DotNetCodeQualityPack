using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNetCodeQualityPack.Ai.Providers;

namespace DotNetCodeQualityPack.Ai
{
    /// <summary>
    /// The provider registry and the one entry point the UI uses: list models for the user's enabled providers, and
    /// send a request to the provider of a model with the user's own URL and key. Adding a provider means adding an
    /// <see cref="IAiProvider"/> here; adding or changing a model needs no code at all.
    /// </summary>
    internal static class AiProviders
    {
        private static readonly IReadOnlyDictionary<AiProviderKind, IAiProvider> Registry = new Dictionary<AiProviderKind, IAiProvider>
        {
            [AiProviderKind.OpenAI] = new OpenAIProvider(),
            [AiProviderKind.Anthropic] = new AnthropicProvider(),
            [AiProviderKind.Ollama] = new OllamaProvider(),
        };

        /// <summary>Display order.</summary>
        public static IReadOnlyList<AiProviderKind> Kinds { get; } = new[] { AiProviderKind.OpenAI, AiProviderKind.Anthropic, AiProviderKind.Ollama };

        public static IAiProvider Get(AiProviderKind kind) => Registry[kind];

        /// <summary>Sends a request to the model's provider, using the user's saved URL and key.</summary>
        public static Task<string> CompleteAsync(AiRequest request, IProgress<string> onChunk, TokenUsage usage, CancellationToken cancellationToken)
        {
            if (request?.Model == null)
            {
                throw new AiServiceException("Choose a model first.");
            }

            // Off the UI thread: reading settings, decrypting the key, proxy detection and DNS can take a moment.
            // onChunk (a Progress<T>) still reports on the thread that created it.
            AiProviderKind kind = request.Model.Provider;
            return Task.Run(
                () => Get(kind).CompleteAsync(AiProviderSettings.Load().Connection(kind), request, onChunk, usage, cancellationToken),
                cancellationToken);
        }

        /// <summary>
        /// Models of every enabled provider: the names the user typed first, then the ones the provider lists.
        /// A provider that can't be reached is reported in <see cref="ModelLoadResult.Errors"/>, but its typed models
        /// are still offered.
        /// </summary>
        public static Task<ModelLoadResult> LoadModelsAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() => LoadModelsCoreAsync(cancellationToken), cancellationToken);
        }

        private static async Task<ModelLoadResult> LoadModelsCoreAsync(CancellationToken cancellationToken)
        {
            AiProviderSettings settings = AiProviderSettings.Load();
            var result = new ModelLoadResult();
            List<ProviderConfig> enabled = settings.Enabled.ToList();
            if (enabled.Count == 0)
            {
                result.Errors.Add("No AI provider is set up yet. Open AI provider settings and choose OpenAI, Anthropic or Ollama.");
                return result;
            }

            // Ask all providers at the same time.
            var lookups = enabled.Select(config => new
            {
                Config = config,
                Listed = ListSafelyAsync(config.Kind, settings, cancellationToken),
            }).ToList();

            foreach (var lookup in lookups)
            {
                IAiProvider provider = Get(lookup.Config.Kind);
                var names = new List<string>(lookup.Config.ModelNames);
                (IReadOnlyList<string> listed, string error) = await lookup.Listed.ConfigureAwait(false);

                if (error != null)
                {
                    result.Errors.Add(provider.DisplayName + ": " + error);
                }

                foreach (string name in listed)
                {
                    if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        names.Add(name);
                    }
                }

                foreach (string name in names)
                {
                    result.Models.Add(AiModel.Create(provider.Kind, name));
                }

                if (names.Count > 0)
                {
                    result.Counts.Add(names.Count + " " + provider.DisplayName);
                }
            }

            return result;
        }

        /// <summary>
        /// Where a model's requests go, for privacy notes: "your Ollama on this PC" or "OpenAI (api.openai.com)".
        /// </summary>
        public static string Destination(AiModel model)
        {
            if (model == null)
            {
                return "the AI provider";
            }

            string url = AiProviderSettings.Load().EffectiveBaseUrl(model.Provider);
            string name = Get(model.Provider).DisplayName;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
            {
                return name;
            }

            return uri.IsLoopback ? "your " + name + " on this PC" : name + " (" + uri.Host + ")";
        }

        /// <summary>True when the model runs on this computer (Ollama on localhost): code doesn't leave the PC.</summary>
        public static bool IsLocal(AiModel model)
        {
            if (model == null)
            {
                return false;
            }

            string url = AiProviderSettings.Load().EffectiveBaseUrl(model.Provider);
            return Uri.TryCreate(url, UriKind.Absolute, out Uri uri) && uri.IsLoopback;
        }

        private static async Task<(IReadOnlyList<string> Models, string Error)> ListSafelyAsync(AiProviderKind kind, AiProviderSettings settings, CancellationToken cancellationToken)
        {
            IAiProvider provider = Get(kind);
            AiConnection connection = settings.Connection(kind);
            if (provider.RequiresApiKey && !connection.HasApiKey)
            {
                return (Array.Empty<string>(), settings.HasApiKey(kind)
                    ? "the saved API key can't be decrypted on this PC or Windows account; enter it again in AI provider settings."
                    : "add your API key in AI provider settings.");
            }

            try
            {
                return (await provider.ListModelsAsync(connection, cancellationToken).ConfigureAwait(false), null);
            }
            catch (AiServiceException ex)
            {
                return (Array.Empty<string>(), ex.Message);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (Array.Empty<string>(), "no answer in time.");
            }
        }
    }
}
