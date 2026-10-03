using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCodeQualityPack.Ai.Providers
{
    /// <summary>
    /// Ollama /api/chat (streamed JSON lines). Any model installed in the user's Ollama works: it is listed from
    /// /api/tags, and names typed in the settings (e.g. "qwen2.5-coder:7b") are used as they are.
    /// </summary>
    internal sealed class OllamaProvider : AiProviderBase
    {
        private static readonly string[] VisionHints =
        {
            "llava", "vision", "bakllava", "moondream", "minicpm-v", "qwen2.5vl", "qwen2-vl", "qwen3-vl",
            "gemma3", "llama4", "granite3.2-vision", "mistral-small3", "pixtral",
        };

        public override AiProviderKind Kind => AiProviderKind.Ollama;

        public override string DisplayName => "Ollama";

        public override string Description => "Models on your own PC or server with Ollama. No API key needed.";

        public override string DefaultBaseUrl => "http://localhost:11434";

        public override bool RequiresApiKey => false;

        public override string ModelExamples => "any installed model, e.g. llama3.2, qwen2.5-coder:7b, deepseek-r1:8b";

        protected override bool UsesServerSentEvents => false;

        public override async Task<IReadOnlyList<string>> ListModelsAsync(AiConnection connection, CancellationToken cancellationToken)
        {
            object root = await GetJsonAsync(connection.BaseUrl + "/api/tags", connection, cancellationToken).ConfigureAwait(false);
            var names = new List<string>();
            if (MiniJson.Get(root, "models") is List<object> models)
            {
                foreach (object m in models)
                {
                    string name = MiniJson.GetString(m, "name") ?? MiniJson.GetString(m, "model");
                    if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name))
                    {
                        names.Add(name);
                    }
                }
            }

            return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public override bool SupportsImages(string modelId)
        {
            string id = (modelId ?? string.Empty).ToLowerInvariant();
            return VisionHints.Any(h => id.Contains(h));
        }

        protected override string ChatUrl(AiConnection connection) => connection.BaseUrl + "/api/chat";

        /// <summary>Ollama needs no key; one is sent only when the user set it (e.g. Ollama behind an authenticating proxy).</summary>
        protected override void Authenticate(HttpRequestMessage request, AiConnection connection)
        {
            if (connection.HasApiKey)
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + connection.ApiKey);
            }
        }

        protected override Dictionary<string, object> BuildBody(AiRequest request, AiConnection connection)
        {
            var messages = new List<object>();
            if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            {
                messages.Add(new Dictionary<string, object> { ["role"] = "system", ["content"] = request.SystemPrompt });
            }

            foreach (AiMessage m in request.Messages)
            {
                var message = new Dictionary<string, object>
                {
                    ["role"] = m.Role == AiMessage.Assistant ? "assistant" : "user",
                    ["content"] = m.Text ?? string.Empty,
                };

                if (m.Images.Count > 0)
                {
                    message["images"] = m.Images.Cast<object>().ToList();
                }

                messages.Add(message);
            }

            var options = new Dictionary<string, object> { ["num_ctx"] = Math.Max(2048, request.ContextSize) };
            if (request.Temperature.HasValue)
            {
                options["temperature"] = request.Temperature.Value;
            }

            var body = new Dictionary<string, object>
            {
                ["model"] = request.Model.Model,
                ["messages"] = messages,
                ["stream"] = true,
                ["options"] = options,
            };

            if (request.JsonMode)
            {
                body["format"] = "json";
            }

            return body;
        }

        protected override string ReadChunk(object chunk, StreamState state)
        {
            if (MiniJson.Get(chunk, "done") is bool done && done)
            {
                state.Done = true;

                // prompt_eval_count is left out when the whole prompt came from Ollama's cache.
                state.Input = Number(chunk, "prompt_eval_count");
                state.Output = Number(chunk, "eval_count");
                state.HitOutputLimit = MiniJson.GetString(chunk, "done_reason") == "length";
            }

            return MiniJson.GetString(MiniJson.Get(chunk, "message"), "content");
        }
    }
}
