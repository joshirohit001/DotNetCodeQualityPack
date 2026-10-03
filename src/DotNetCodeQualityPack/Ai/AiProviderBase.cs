using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCodeQualityPack.Ai
{
    /// <summary>
    /// The HTTP part every provider shares: send a JSON request, stream the answer (server-sent events or JSON lines),
    /// count tokens, and turn failures into messages a developer can act on. A provider only describes its URLs,
    /// headers, request body and stream chunks.
    /// </summary>
    internal abstract class AiProviderBase : IAiProvider
    {
        private static readonly HttpClient Http = CreateClient();

        public abstract AiProviderKind Kind { get; }

        public abstract string DisplayName { get; }

        public abstract string Description { get; }

        public abstract string DefaultBaseUrl { get; }

        public abstract bool RequiresApiKey { get; }

        public abstract string ModelExamples { get; }

        /// <summary>True for server-sent events ("data: {...}"), false for one JSON object per line (Ollama).</summary>
        protected abstract bool UsesServerSentEvents { get; }

        public abstract Task<IReadOnlyList<string>> ListModelsAsync(AiConnection connection, CancellationToken cancellationToken);

        public abstract bool SupportsImages(string modelId);

        /// <summary>The URL the request is posted to.</summary>
        protected abstract string ChatUrl(AiConnection connection);

        /// <summary>The provider's request body. <paramref name="connection"/> tells which server it goes to (some fields only official APIs accept).</summary>
        protected abstract Dictionary<string, object> BuildBody(AiRequest request, AiConnection connection);

        /// <summary>Adds the API key (and any version header) to a request.</summary>
        protected abstract void Authenticate(HttpRequestMessage request, AiConnection connection);

        /// <summary>Text in one streamed chunk; also records token counts and the end of the stream in <paramref name="state"/>.</summary>
        protected abstract string ReadChunk(object chunk, StreamState state);

        public async Task<string> CompleteAsync(AiConnection connection, AiRequest request, IProgress<string> onChunk, TokenUsage usage, CancellationToken cancellationToken)
        {
            if (request?.Model == null || string.IsNullOrWhiteSpace(request.Model.Model))
            {
                throw new AiServiceException("Choose a model first.");
            }

            EnsureReady(connection);
            Uri uri = ToUri(ChatUrl(connection));
            string json = MiniJson.Serialize(BuildBody(request, connection));
            var state = new StreamState();
            var full = new StringBuilder();

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(json, Encoding.UTF8, "application/json") })
            {
                Authenticate(message, connection);
                int timeout = Math.Max(30, request.TimeoutSeconds);
                cts.CancelAfter(TimeSpan.FromSeconds(timeout));

                HttpResponseMessage response;
                try
                {
                    response = await Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new AiServiceException(uri.Host + " didn't start answering within " + timeout + " seconds. Try a smaller model, or raise the timeout in the AI settings.");
                }
                catch (HttpRequestException ex)
                {
                    throw new AiServiceException("Couldn't reach " + uri.Host + ": " + (ex.InnerException?.Message ?? ex.Message) + Hint(uri));
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        throw new AiServiceException(DescribeFailure(response.StatusCode, error, uri, connection));
                    }

                    // Headers arrived: the answer may now stream for as long as it needs.
                    cts.CancelAfter(Timeout.Infinite);

                    using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    using (cancellationToken.Register(() => reader.Dispose()))
                    {
                        string line;
                        while (!state.Done && (line = await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false)) != null)
                        {
                            line = line.Trim();
                            if (line.Length == 0)
                            {
                                continue;
                            }

                            string payload = line;
                            if (UsesServerSentEvents)
                            {
                                if (!line.StartsWith("data:", StringComparison.Ordinal))
                                {
                                    // "event:" lines and ": comments" belong to the stream; anything else is a server that
                                    // answered with one plain JSON document instead of a stream.
                                    if (!line.StartsWith("event:", StringComparison.Ordinal) && !line.StartsWith(":", StringComparison.Ordinal))
                                    {
                                        state.Unparsed.Append(line);
                                    }

                                    continue;
                                }

                                payload = line.Substring(5).Trim();
                                if (payload == "[DONE]")
                                {
                                    break;
                                }
                            }

                            if (!MiniJson.TryParse(payload, out object chunk))
                            {
                                // A server that ignores "stream": true may send one JSON document over several lines.
                                state.Unparsed.Append(payload);
                                continue;
                            }

                            string error = ErrorMessage(chunk);
                            if (!string.IsNullOrEmpty(error))
                            {
                                throw new AiServiceException(DisplayName + " reported: " + error);
                            }

                            string text = ReadChunk(chunk, state);
                            if (!string.IsNullOrEmpty(text))
                            {
                                full.Append(text);
                                onChunk?.Report(text);
                            }
                        }
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Non-streamed reply: read it as a whole document.
            if (full.Length == 0 && state.Unparsed.Length > 0 && MiniJson.TryParse(state.Unparsed.ToString(), out object whole))
            {
                string error = ErrorMessage(whole);
                if (!string.IsNullOrEmpty(error))
                {
                    throw new AiServiceException(DisplayName + " reported: " + error);
                }

                string text = ReadWhole(whole);
                if (!string.IsNullOrEmpty(text))
                {
                    full.Append(text);
                    onChunk?.Report(text);
                }

                if (TokenUsage.TryRead(state.Unparsed.ToString(), out long i, out long o))
                {
                    state.Input = i;
                    state.Output = o;
                }
            }

            string answer = full.ToString();
            if (answer.Length == 0 && state.HitOutputLimit)
            {
                throw new AiServiceException(DisplayName + " stopped at the output limit before writing an answer. Try a shorter request.");
            }

            if (state.Input >= 0 || state.Output >= 0)
            {
                usage?.Add(Math.Max(0, state.Input), Math.Max(0, state.Output), estimated: false);
            }
            else
            {
                usage?.Add(TokenUsage.Estimate(json), TokenUsage.Estimate(answer), estimated: true);
            }

            return answer;
        }

        /// <summary>Text of a whole (non-streamed) reply, in any of the three providers' formats.</summary>
        protected virtual string ReadWhole(object root)
        {
            // OpenAI: choices[0].message.content
            if (MiniJson.Get(root, "choices") is List<object> choices && choices.Count > 0)
            {
                return MiniJson.GetString(MiniJson.Get(choices[0], "message"), "content");
            }

            // Anthropic: content[].text
            if (MiniJson.Get(root, "content") is List<object> parts)
            {
                var sb = new StringBuilder();
                foreach (object part in parts)
                {
                    if (MiniJson.GetString(part, "type") == "text")
                    {
                        sb.Append(MiniJson.GetString(part, "text"));
                    }
                }

                return sb.ToString();
            }

            // Ollama: message.content (chat) or response (generate)
            return MiniJson.GetString(MiniJson.Get(root, "message"), "content") ?? MiniJson.GetString(root, "response");
        }

        // ------------------------------------------------------------------ helpers for providers

        /// <summary>GET a JSON document (model lists).</summary>
        protected async Task<object> GetJsonAsync(string url, AiConnection connection, CancellationToken cancellationToken)
        {
            EnsureReady(connection);
            Uri uri = ToUri(url);
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var message = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                Authenticate(message, connection);
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    using (HttpResponseMessage response = await Http.SendAsync(message, cts.Token).ConfigureAwait(false))
                    {
                        string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new AiServiceException(DescribeFailure(response.StatusCode, text, uri, connection));
                        }

                        if (!MiniJson.TryParse(text, out object root))
                        {
                            throw new AiServiceException(uri.Host + " didn't answer with JSON. Check the API URL for " + DisplayName + ".");
                        }

                        return root;
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new AiServiceException(uri.Host + " didn't answer within 30 seconds." + Hint(uri));
                }
                catch (HttpRequestException ex)
                {
                    throw new AiServiceException("Couldn't reach " + uri.Host + ": " + (ex.InnerException?.Message ?? ex.Message) + Hint(uri));
                }
            }
        }

        /// <summary>{ "data": [ { "id": ... } ] } — the list format OpenAI and Anthropic share.</summary>
        protected static List<string> ReadDataIds(object root)
        {
            var ids = new List<string>();
            if (MiniJson.Get(root, "data") is List<object> data)
            {
                foreach (object item in data)
                {
                    string id = MiniJson.GetString(item, "id");
                    if (!string.IsNullOrWhiteSpace(id) && !ids.Contains(id))
                    {
                        ids.Add(id);
                    }
                }
            }

            return ids;
        }

        /// <summary>"error": "text" (Ollama) or "error": { "message": "text" } (OpenAI, Anthropic).</summary>
        internal static string ErrorMessage(object root)
        {
            object error = MiniJson.Get(root, "error");
            return error is Dictionary<string, object> ? MiniJson.GetString(error, "message") ?? MiniJson.Serialize(error) : MiniJson.AsString(error);
        }

        protected static long Number(object node, string key)
        {
            return MiniJson.Get(node, key) is double d && d >= 0 && d < 1e15 ? (long)d : 0;
        }

        private void EnsureReady(AiConnection connection)
        {
            if (connection == null || string.IsNullOrWhiteSpace(connection.BaseUrl))
            {
                throw new AiServiceException(DisplayName + " has no API URL. Set it in AI provider settings.");
            }

            if (RequiresApiKey && !connection.HasApiKey)
            {
                throw new AiServiceException(DisplayName + " needs your API key. Add it in AI provider settings.");
            }

            // An API key must not travel unencrypted over the network.
            if (connection.HasApiKey && Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out Uri uri) && uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            {
                throw new AiServiceException("The " + DisplayName + " API URL uses http:// but an API key is set. Use https://, or remove the key, so the key isn't sent unencrypted.");
            }
        }

        private Uri ToUri(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new AiServiceException("The " + DisplayName + " API URL isn't a valid http(s) address. Fix it in AI provider settings.");
            }

            return uri;
        }

        private string Hint(Uri uri)
        {
            return Kind == AiProviderKind.Ollama && uri.IsLoopback
                ? " Is Ollama running? Start it, or run 'ollama serve'."
                : string.Empty;
        }

        private string DescribeFailure(HttpStatusCode status, string body, Uri uri, AiConnection connection)
        {
            // Some servers echo (part of) the key in their error; never show it.
            if (connection.HasApiKey)
            {
                body = (body ?? string.Empty).Replace(connection.ApiKey, "***");
            }

            string snippet = (body ?? string.Empty).Trim();
            if (snippet.Length > 300)
            {
                snippet = snippet.Substring(0, 300) + "…";
            }

            string serverError = MiniJson.TryParse(body, out object parsed) ? ErrorMessage(parsed) : null;
            string detail = string.IsNullOrWhiteSpace(serverError) ? snippet : serverError;

            switch ((int)status)
            {
                case 401:
                case 403:
                    return DisplayName + " refused the request (HTTP " + (int)status + "). Check your API key in AI provider settings. " + detail;
                case 404:
                    return DisplayName + " doesn't know this address or model (HTTP 404). Check the API URL and the model name"
                        + (Kind == AiProviderKind.Ollama ? " (run 'ollama list' to see installed models). " : ". ") + detail;
                case 429:
                    return DisplayName + " is rate limiting requests or your quota is used up (HTTP 429). Wait a moment and try again. " + detail;
                default:
                    return DisplayName + " returned HTTP " + (int)status + ". " + detail;
            }
        }

        private static async Task<string> ReadLineAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            try
            {
                return await reader.ReadLineAsync().ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        private static HttpClient CreateClient()
        {
            // Uses the Windows proxy settings. Streaming answers can take minutes, so timeouts are set per request.
            var client = new HttpClient(new HttpClientHandler { UseProxy = true }) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DotNetCodeQualityPack/" + Vsix.Version);
            return client;
        }

        /// <summary>What a provider learns while reading the stream.</summary>
        protected sealed class StreamState
        {
            /// <summary>Input tokens reported by the server; -1 while unknown.</summary>
            public long Input { get; set; } = -1;

            /// <summary>Output tokens reported by the server; -1 while unknown.</summary>
            public long Output { get; set; } = -1;

            public bool Done { get; set; }

            /// <summary>The answer stopped because it reached the request's output limit.</summary>
            public bool HitOutputLimit { get; set; }

            internal StringBuilder Unparsed { get; } = new StringBuilder();
        }
    }
}
