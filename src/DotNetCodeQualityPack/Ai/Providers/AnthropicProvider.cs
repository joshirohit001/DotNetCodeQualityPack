using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCodeQualityPack.Ai.Providers
{
    /// <summary>Anthropic Messages API (Claude), streamed.</summary>
    internal sealed class AnthropicProvider : AiProviderBase
    {
        private const string ApiVersion = "2023-06-01";

        public override AiProviderKind Kind => AiProviderKind.Anthropic;

        public override string DisplayName => "Anthropic";

        public override string Description => "Claude models with your own Anthropic API key.";

        public override string DefaultBaseUrl => "https://api.anthropic.com/v1";

        public override bool RequiresApiKey => true;

        public override string ModelExamples => "e.g. claude-sonnet-5-5, claude-haiku-4-5";

        protected override bool UsesServerSentEvents => true;

        public override async Task<IReadOnlyList<string>> ListModelsAsync(AiConnection connection, CancellationToken cancellationToken)
        {
            object root = await GetJsonAsync(connection.BaseUrl + "/models?limit=100", connection, cancellationToken).ConfigureAwait(false);
            return ReadDataIds(root);
        }

        public override bool SupportsImages(string modelId) => true;

        protected override string ChatUrl(AiConnection connection) => connection.BaseUrl + "/messages";

        protected override void Authenticate(HttpRequestMessage request, AiConnection connection)
        {
            if (connection.HasApiKey)
            {
                request.Headers.TryAddWithoutValidation("x-api-key", connection.ApiKey);
            }

            request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        }

        protected override Dictionary<string, object> BuildBody(AiRequest request, AiConnection connection)
        {
            // The Messages API needs alternating user/assistant turns that start with the user.
            var messages = new List<object>();
            string lastRole = null;
            List<object> current = null;

            foreach (AiMessage m in request.Messages)
            {
                string role = m.Role == AiMessage.Assistant ? "assistant" : "user";
                if (current == null && role == "assistant")
                {
                    continue; // a conversation can't start with the assistant
                }

                if (role != lastRole)
                {
                    current = new List<object>();
                    messages.Add(new Dictionary<string, object> { ["role"] = role, ["content"] = current });
                    lastRole = role;
                }

                if (role == "user")
                {
                    foreach (string image in m.Images)
                    {
                        current.Add(new Dictionary<string, object>
                        {
                            ["type"] = "image",
                            ["source"] = new Dictionary<string, object> { ["type"] = "base64", ["media_type"] = "image/png", ["data"] = image },
                        });
                    }
                }

                current.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = string.IsNullOrWhiteSpace(m.Text) ? "(empty)" : m.Text });
            }

            var body = new Dictionary<string, object>
            {
                ["model"] = request.Model.Model,
                ["max_tokens"] = Math.Max(256, request.MaxOutputTokens),
                ["messages"] = messages,
                ["stream"] = true,
            };

            if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            {
                body["system"] = request.SystemPrompt;
            }

            return body;
        }

        protected override string ReadChunk(object chunk, StreamState state)
        {
            switch (MiniJson.GetString(chunk, "type"))
            {
                case "message_start":
                    object usage = MiniJson.Get(MiniJson.Get(chunk, "message"), "usage");
                    state.Input = Number(usage, "input_tokens") + Number(usage, "cache_read_input_tokens") + Number(usage, "cache_creation_input_tokens");
                    return null;

                case "message_delta":
                    object delta = MiniJson.Get(chunk, "usage");
                    if (MiniJson.Get(delta, "output_tokens") != null)
                    {
                        state.Output = Number(delta, "output_tokens");
                    }

                    if (MiniJson.GetString(MiniJson.Get(chunk, "delta"), "stop_reason") == "max_tokens")
                    {
                        state.HitOutputLimit = true;
                    }

                    return null;

                case "message_stop":
                    state.Done = true;
                    return null;

                case "content_block_delta":
                    return MiniJson.GetString(MiniJson.Get(chunk, "delta"), "text");

                default:
                    return null;
            }
        }
    }
}
