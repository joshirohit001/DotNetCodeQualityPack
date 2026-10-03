using System;
using System.Collections.Generic;

namespace DotNetCodeQualityPack.Ai
{
    /// <summary>The AI providers the extension can talk to. Each one is implemented by an <see cref="IAiProvider"/>.</summary>
    internal enum AiProviderKind
    {
        OpenAI,
        Anthropic,
        Ollama,
    }

    /// <summary>A model the user can pick: a provider plus the provider's model ID. Holds no URL or key.</summary>
    internal sealed class AiModel
    {
        public AiProviderKind Provider { get; set; }

        /// <summary>The provider's model ID, e.g. "gpt-4.1", "claude-sonnet-5-5" or "qwen2.5-coder:7b".</summary>
        public string Model { get; set; }

        /// <summary>Shown in dropdowns and remembered in settings, e.g. "Ollama · llama3.2".</summary>
        public string Name { get; set; }

        public static AiModel Create(AiProviderKind provider, string modelId)
        {
            string id = (modelId ?? string.Empty).Trim();
            return new AiModel { Provider = provider, Model = id, Name = AiProviders.Get(provider).DisplayName + " · " + id };
        }

        public override string ToString() => Name;
    }

    /// <summary>One turn of a conversation, in a provider-neutral form.</summary>
    internal sealed class AiMessage
    {
        public const string User = "user";
        public const string Assistant = "assistant";

        public string Role { get; set; } = User;

        public string Text { get; set; } = string.Empty;

        /// <summary>Base64-encoded PNG images attached to this turn (user turns only).</summary>
        public List<string> Images { get; } = new List<string>();
    }

    /// <summary>What to send to a model. Providers translate it into their own request format.</summary>
    internal sealed class AiRequest
    {
        public AiModel Model { get; set; }

        public string SystemPrompt { get; set; }

        public List<AiMessage> Messages { get; } = new List<AiMessage>();

        /// <summary>Ask for a JSON answer (native JSON mode where the provider has one; the prompt must ask for JSON too).</summary>
        public bool JsonMode { get; set; }

        /// <summary>Upper limit for the answer (required by Anthropic; ignored where the provider has no such field).</summary>
        public int MaxOutputTokens { get; set; } = 8192;

        /// <summary>Optional sampling temperature, used only by providers where every model accepts it (Ollama).</summary>
        public double? Temperature { get; set; }

        /// <summary>Context window to request where the provider lets the client choose it (Ollama num_ctx).</summary>
        public int ContextSize { get; set; } = 8192;

        /// <summary>How long to wait for the server to start answering. The answer itself may then stream for longer.</summary>
        public int TimeoutSeconds { get; set; } = 300;
    }

    /// <summary>
    /// Where and how to reach a provider: the end user's API URL and key, read from their own settings at the moment of
    /// the request. Never stored on models, never logged, never shown.
    /// </summary>
    internal sealed class AiConnection
    {
        public AiConnection(string baseUrl, string apiKey)
        {
            BaseUrl = baseUrl;
            ApiKey = apiKey;
        }

        /// <summary>The provider's API root without a trailing slash, e.g. https://api.openai.com/v1.</summary>
        public string BaseUrl { get; }

        public string ApiKey { get; }

        public bool HasApiKey => !string.IsNullOrEmpty(ApiKey);
    }

    /// <summary>Models found for the enabled providers, with a short summary and per-provider problems.</summary>
    internal sealed class ModelLoadResult
    {
        public List<AiModel> Models { get; } = new List<AiModel>();

        /// <summary>For example "3 OpenAI", "5 Ollama".</summary>
        public List<string> Counts { get; } = new List<string>();

        public List<string> Errors { get; } = new List<string>();
    }

    /// <summary>A problem the user can act on (bad key, unreachable server, timeout...). The message is written for people.</summary>
    internal sealed class AiServiceException : Exception
    {
        public AiServiceException(string message)
            : base(message)
        {
        }
    }
}
