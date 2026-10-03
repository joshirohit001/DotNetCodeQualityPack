using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCodeQualityPack.Ai
{
    /// <summary>
    /// One AI provider (OpenAI, Anthropic, Ollama). Every provider is used the same way, so chat, code review and the
    /// settings window never need provider-specific code: they pick a provider by <see cref="AiProviderKind"/>, pass the
    /// user's <see cref="AiConnection"/> and a provider-neutral <see cref="AiRequest"/>.
    /// </summary>
    internal interface IAiProvider
    {
        AiProviderKind Kind { get; }

        /// <summary>"OpenAI", "Anthropic", "Ollama".</summary>
        string DisplayName { get; }

        /// <summary>One line for the settings window.</summary>
        string Description { get; }

        /// <summary>Used when the user leaves the API URL empty.</summary>
        string DefaultBaseUrl { get; }

        /// <summary>False for Ollama (a key is optional there, e.g. behind a proxy).</summary>
        bool RequiresApiKey { get; }

        /// <summary>Example model names for the settings window.</summary>
        string ModelExamples { get; }

        /// <summary>The models this connection can use, as the provider lists them.</summary>
        Task<IReadOnlyList<string>> ListModelsAsync(AiConnection connection, CancellationToken cancellationToken);

        /// <summary>
        /// Sends the request and streams the answer: each piece of text is reported to <paramref name="onChunk"/> (may be
        /// null) and the whole answer is returned. Tokens are added to <paramref name="usage"/> (may be null).
        /// </summary>
        Task<string> CompleteAsync(AiConnection connection, AiRequest request, IProgress<string> onChunk, TokenUsage usage, CancellationToken cancellationToken);

        /// <summary>Best guess whether the model can read images (used only to warn before sending a screenshot).</summary>
        bool SupportsImages(string modelId);
    }
}
