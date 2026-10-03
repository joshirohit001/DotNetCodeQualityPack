using System;
using System.Collections.Generic;
using System.Globalization;

namespace DotNetCodeQualityPack.Ai
{
    /// <summary>
    /// Input/output token counts for AI requests. Safe to add to from the background thread that runs a review.
    /// Counts come from the server's reply when it reports them (Ollama, OpenAI and Anthropic all do);
    /// otherwise they are estimated at about 4 characters per token and marked as estimated.
    /// </summary>
    internal sealed class TokenUsage
    {
        private readonly object _gate = new object();
        private long _input;
        private long _output;
        private int _requests;
        private int _estimated;

        public long Input { get { lock (_gate) { return _input; } } }

        public long Output { get { lock (_gate) { return _output; } } }

        public long Total { get { lock (_gate) { return _input + _output; } } }

        public int Requests { get { lock (_gate) { return _requests; } } }

        /// <summary>Requests whose counts were estimated because the server didn't report usage.</summary>
        public int EstimatedRequests { get { lock (_gate) { return _estimated; } } }

        public bool IsEmpty => Requests == 0;

        public void Add(long input, long output, bool estimated)
        {
            lock (_gate)
            {
                _input += Math.Max(0, input);
                _output += Math.Max(0, output);
                _requests++;
                if (estimated)
                {
                    _estimated++;
                }
            }
        }

        public void Add(TokenUsage other)
        {
            if (other == null)
            {
                return;
            }

            long input = other.Input, output = other.Output;
            int requests = other.Requests, estimated = other.EstimatedRequests;
            lock (_gate)
            {
                _input += input;
                _output += output;
                _requests += requests;
                _estimated += estimated;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _input = _output = 0;
                _requests = _estimated = 0;
            }
        }

        public TokenUsage Snapshot()
        {
            var copy = new TokenUsage();
            copy.Add(this);
            return copy;
        }

        /// <summary>Rough count for text the server didn't report on: about 4 characters per token.</summary>
        public static long Estimate(string text) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

        /// <summary>
        /// Reads the token counts from a model server reply:
        /// OpenAI "usage": { prompt_tokens, completion_tokens };
        /// Anthropic "usage": { input_tokens, output_tokens, cache_creation_input_tokens, cache_read_input_tokens };
        /// Ollama prompt_eval_count / eval_count (on the reply, or on the last line of a streamed reply).
        /// </summary>
        public static bool TryRead(string body, out long input, out long output)
        {
            input = 0;
            output = 0;
            string text = (body ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return false;
            }

            if (MiniJson.TryParse(text, out object root))
            {
                return TryReadObject(root, out input, out output);
            }

            // Streamed reply: one JSON object per line; the counts are on the final line.
            bool found = false;
            foreach (string line in text.Split('\n'))
            {
                if (MiniJson.TryParse(line.Trim(), out object part) && TryReadObject(part, out long i, out long o))
                {
                    input = i;
                    output = o;
                    found = true;
                }
            }

            return found;
        }

        private static bool TryReadObject(object root, out long input, out long output)
        {
            input = 0;
            output = 0;

            object usage = MiniJson.Get(root, "usage");
            if (usage is Dictionary<string, object>)
            {
                long? prompt = Number(usage, "prompt_tokens") ?? Number(usage, "input_tokens");
                long? completion = Number(usage, "completion_tokens") ?? Number(usage, "output_tokens");
                long cached = (Number(usage, "cache_creation_input_tokens") ?? 0) + (Number(usage, "cache_read_input_tokens") ?? 0);
                if (prompt.HasValue || completion.HasValue)
                {
                    input = (prompt ?? 0) + cached;
                    output = completion ?? 0;
                    return true;
                }
            }

            // Ollama leaves prompt_eval_count out when the whole prompt came from its cache.
            long? promptEval = Number(root, "prompt_eval_count");
            long? eval = Number(root, "eval_count");
            if (promptEval.HasValue || eval.HasValue)
            {
                input = promptEval ?? 0;
                output = eval ?? 0;
                return true;
            }

            return false;
        }

        private static long? Number(object node, string key)
        {
            object value = MiniJson.Get(node, key);
            return value is double d && d >= 0 && d < 1e15 ? (long)d : (long?)null;
        }

        /// <summary>1,234 · 12.4k · 3.2M</summary>
        public static string Short(long n)
        {
            if (n < 10000)
            {
                return n.ToString("N0", CultureInfo.CurrentCulture);
            }

            return n < 1000000
                ? (n / 1000.0).ToString("0.#", CultureInfo.CurrentCulture) + "k"
                : (n / 1000000.0).ToString("0.##", CultureInfo.CurrentCulture) + "M";
        }

        public static string Full(long n) => n.ToString("N0", CultureInfo.CurrentCulture);
    }
}
