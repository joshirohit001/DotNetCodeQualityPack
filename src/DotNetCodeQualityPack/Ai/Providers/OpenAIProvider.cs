using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCodeQualityPack.Ai.Providers
{
    /// <summary>
    /// OpenAI Chat Completions (streamed). Works with api.openai.com and with any OpenAI-compatible API
    /// (Azure OpenAI proxies, LM Studio, vLLM, OpenRouter...) when the user sets its URL.
    /// </summary>
    internal sealed class OpenAIProvider : AiProviderBase
    {
        private static readonly string[] NonChatModels =
        {
            "embedding", "audio", "realtime", "tts", "transcribe", "whisper", "dall-e", "image", "moderation", "search", "davinci", "babbage",
        };

        public override AiProviderKind Kind => AiProviderKind.OpenAI;

        public override string DisplayName => "OpenAI";

        public override string Description => "Your own OpenAI account, or any OpenAI-compatible API (set its URL).";

        public override string DefaultBaseUrl => "https://api.openai.com/v1";

        public override bool RequiresApiKey => true;

        public override string ModelExamples => "e.g. gpt-4.1, gpt-4.1-mini, o4-mini";

        protected override bool UsesServerSentEvents => true;

        public override async Task<IReadOnlyList<string>> ListModelsAsync(AiConnection connection, CancellationToken cancellationToken)
        {
            object root = await GetJsonAsync(connection.BaseUrl + "/models", connection, cancellationToken).ConfigureAwait(false);
            List<string> ids = ReadDataIds(root);

            // api.openai.com also lists embedding, audio and image models; keep the chat models there.
            // Compatible servers list only what they serve, so everything is kept.
            if (IsOfficialApi(connection))
            {
                ids = ids
                    .Where(id => id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
                        || id.StartsWith("chatgpt", StringComparison.OrdinalIgnoreCase)
                        || Regex.IsMatch(id, @"^o\d", RegexOptions.IgnoreCase))
                    .Where(id => !NonChatModels.Any(s => id.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0))
                    .ToList();
            }

            return ids.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool IsOfficialApi(AiConnection connection) =>
            Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out Uri uri) && uri.Host.EndsWith("openai.com", StringComparison.OrdinalIgnoreCase);

        public override bool SupportsImages(string modelId)
        {
            string id = (modelId ?? string.Empty).ToLowerInvariant();
            return !(id.StartsWith("gpt-3.5", StringComparison.Ordinal) || id.Contains("o1-mini") || id.Contains("o3-mini"));
        }

        protected override string ChatUrl(AiConnection connection) => connection.BaseUrl + "/chat/completions";

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
                string role = m.Role == AiMessage.Assistant ? "assistant" : "user";
                if (m.Images.Count == 0 || role != "user")
                {
                    messages.Add(new Dictionary<string, object> { ["role"] = role, ["content"] = m.Text ?? string.Empty });
                    continue;
                }

                var parts = new List<object> { new Dictionary<string, object> { ["type"] = "text", ["text"] = m.Text ?? string.Empty } };
                parts.AddRange(m.Images.Select(image => (object)new Dictionary<string, object>
                {
                    ["type"] = "image_url",
                    ["image_url"] = new Dictionary<string, object> { ["url"] = "data:image/png;base64," + image },
                }));
                messages.Add(new Dictionary<string, object> { ["role"] = "user", ["content"] = parts });
            }

            // No temperature or max_tokens: reasoning models reject them, and the defaults are fine.
            var body = new Dictionary<string, object>
            {
                ["model"] = request.Model.Model,
                ["messages"] = messages,
                ["stream"] = true,
            };

            // Token counts and JSON mode are sent to the official API only: several compatible servers (LM Studio,
            // older vLLM, some proxies) reject these fields. The prompts ask for JSON anyway.
            if (IsOfficialApi(connection))
            {
                body["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true };
                if (request.JsonMode)
                {
                    body["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" };
                }
            }

            return body;
        }

        protected override string ReadChunk(object chunk, StreamState state)
        {
            object usage = MiniJson.Get(chunk, "usage");
            if (usage is Dictionary<string, object>)
            {
                state.Input = Number(usage, "prompt_tokens");
                state.Output = Number(usage, "completion_tokens");
            }

            if (MiniJson.Get(chunk, "choices") is List<object> choices && choices.Count > 0)
            {
                return MiniJson.GetString(MiniJson.Get(choices[0], "delta"), "content");
            }

            return null;
        }
    }
}
