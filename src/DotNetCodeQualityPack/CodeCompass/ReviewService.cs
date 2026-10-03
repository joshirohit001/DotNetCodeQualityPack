using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DotNetCodeQualityPack.Ai;
using DotNetCodeQualityPack.Services;

namespace DotNetCodeQualityPack.CodeCompass
{
    internal sealed class ReviewRequest
    {
        public string Code { get; set; }

        public string FileName { get; set; }

        /// <summary>Line number in the file where <see cref="Code"/> starts (1 for a whole file).</summary>
        public int StartLine { get; set; } = 1;

        public bool IncludeImprovedCode { get; set; } = true;
    }

    /// <summary>Builds the review prompt from the team rules, calls the chosen model and parses its JSON answer.</summary>
    internal sealed class ReviewService
    {
        private const int MaxCodeChars = 24000;

        private readonly ChatClient _client;

        public ReviewService(ChatClient client)
        {
            _client = client;
        }

        public async Task<ReviewResult> ReviewAsync(
            AiModel model,
            PackSettings settings,
            ReviewRequest request,
            RuleSet rules,
            int ruleBudget,
            TokenUsage usage,
            CancellationToken cancellationToken)
        {
            string code = request.Code ?? string.Empty;
            bool truncated = false;
            if (code.Length > MaxCodeChars)
            {
                code = code.Substring(0, MaxCodeChars);
                truncated = true;
            }

            string system = BuildSystemPrompt(RuleSetProvider.SelectRelevant(rules, code, ruleBudget), rules?.Context, request.IncludeImprovedCode);
            var history = new List<ChatMessage>
            {
                new ChatMessage { Role = ChatMessage.UserRole, Content = BuildUserPrompt(request, code, truncated) },
            };

            string raw = await _client.ChatAsync(model, settings, system, history, jsonMode: true, onChunk: null, usage, cancellationToken).ConfigureAwait(false);
            ReviewResult result = Parse(raw);
            if (truncated)
            {
                result.Summary = "(Only the first " + MaxCodeChars + " characters were reviewed.) " + result.Summary;
            }

            return result;
        }

        private static string BuildSystemPrompt(string rules, IReadOnlyList<string> context, bool includeImproved)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a senior .NET code reviewer for the Code Compass tool.");
            sb.AppendLine("Review the user's code. Report real problems only: bugs, security issues, async misuse, resource leaks, performance problems, and violations of the team rules below.");
            sb.AppendLine("For every problem, give a concrete fix as a corrected code snippet that can replace the faulty lines.");
            sb.AppendLine("Do not invent issues. Modern C# syntax (primary constructors, records, pattern matching) is valid. If the code is fine, return an empty findings list.");
            sb.AppendLine();

            if (context != null && context.Count > 0)
            {
                sb.AppendLine("Project facts (not rules, do not report them):");
                foreach (string fact in context)
                {
                    sb.Append("- ").AppendLine(fact);
                }

                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(rules))
            {
                sb.AppendLine("TEAM RULES (put the rule ID in ruleId when a finding matches one; otherwise use \"General\"):");
                sb.AppendLine(rules);
            }
            else
            {
                sb.AppendLine("No team rules were provided. Apply standard Microsoft .NET and C# best practices and use \"General\" as ruleId.");
            }

            sb.AppendLine();
            sb.AppendLine("Respond with JSON only, exactly in this shape:");
            sb.AppendLine("{");
            sb.AppendLine("  \"summary\": \"one or two sentences about overall quality\",");
            sb.AppendLine("  \"verdict\": \"Safe to merge\" | \"Merge after fixing errors\" | \"Needs rework\",");
            sb.AppendLine("  \"findings\": [");
            sb.AppendLine("    {");
            sb.AppendLine("      \"severity\": \"Error\" | \"Warning\" | \"Info\",");
            sb.AppendLine("      \"ruleId\": \"rule ID from the team rules, or General\",");
            sb.AppendLine("      \"title\": \"short title\",");
            sb.AppendLine("      \"line\": <line number from the numbered code>,");
            sb.AppendLine("      \"problem\": \"what is wrong and why it matters\",");
            sb.AppendLine("      \"suggestion\": \"how to fix it\",");
            sb.AppendLine("      \"fixedCode\": \"corrected C# snippet for the affected lines\"");
            sb.AppendLine("    }");
            sb.AppendLine("  ],");
            sb.AppendLine(includeImproved
                ? "  \"improvedCode\": \"the full reviewed code rewritten with all fixes applied\""
                : "  \"improvedCode\": \"\"");
            sb.AppendLine("}");
            sb.AppendLine("Order findings by severity: Error, then Warning, then Info.");
            return sb.ToString();
        }

        private static string BuildUserPrompt(ReviewRequest request, string code, bool truncated)
        {
            var sb = new StringBuilder();
            sb.Append("Review this code");
            if (!string.IsNullOrWhiteSpace(request.FileName))
            {
                sb.Append(" from ").Append(request.FileName);
            }

            sb.AppendLine(". Each line starts with its real line number and '| '.");
            if (truncated)
            {
                sb.AppendLine("The code was truncated.");
            }

            sb.AppendLine("```" + ChatClient.LanguageHint(request.FileName ?? "code.cs"));
            string[] lines = code.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                sb.Append(request.StartLine + i).Append("| ").AppendLine(lines[i].Replace("\t", "    "));
            }

            sb.AppendLine("```");
            return sb.ToString();
        }

        internal static ReviewResult Parse(string raw)
        {
            string json = ExtractJson(raw);
            if (json != null && MiniJson.TryParse(json, out object root))
            {
                List<object> items = root as List<object>
                    ?? MiniJson.Get(root, "findings") as List<object>
                    ?? MiniJson.Get(root, "issues") as List<object>;

                var result = new ReviewResult
                {
                    Summary = MiniJson.GetString(root, "summary"),
                    Verdict = MiniJson.GetString(root, "verdict"),
                    ImprovedCode = StripFences(MiniJson.GetString(root, "improvedCode")),
                };

                if (items != null)
                {
                    result.Findings = items
                        .Where(i => i is Dictionary<string, object>)
                        .Select(ReviewFinding.FromJson)
                        .Where(f => !(string.IsNullOrWhiteSpace(f.Title) && string.IsNullOrWhiteSpace(f.Problem)))
                        .OrderBy(f => SeverityRank(f.NormalizedSeverity))
                        .ThenBy(f => f.Line ?? int.MaxValue)
                        .ToList();

                    foreach (ReviewFinding f in result.Findings)
                    {
                        f.FixedCode = StripFences(f.FixedCode);
                    }
                }

                if (items != null || result.Summary != null || result.Verdict != null)
                {
                    return result;
                }
            }

            return new ReviewResult
            {
                Summary = "The model did not return structured results. Its full answer is shown below.",
                RawText = raw,
            };
        }

        private static int SeverityRank(string severity)
        {
            switch (severity)
            {
                case "Error": return 0;
                case "Warning": return 1;
                default: return 2;
            }
        }

        private static string ExtractJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string text = raw.Trim();
            Match fence = Regex.Match(text, "```(?:json)?\\s*(?<body>[\\s\\S]*?)```");
            if (fence.Success)
            {
                text = fence.Groups["body"].Value.Trim();
            }

            int startObj = text.IndexOf('{');
            int startArr = text.IndexOf('[');
            if (startObj < 0 && startArr < 0)
            {
                return null;
            }

            bool useArray = startArr >= 0 && (startObj < 0 || startArr < startObj);
            int start = useArray ? startArr : startObj;
            int end = text.LastIndexOf(useArray ? ']' : '}');
            return end > start ? text.Substring(start, end - start + 1) : null;
        }

        private static string StripFences(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return code;
            }

            string t = code.Trim();
            if (t.StartsWith("```", StringComparison.Ordinal))
            {
                int firstNewLine = t.IndexOf('\n');
                t = firstNewLine >= 0 ? t.Substring(firstNewLine + 1) : string.Empty;
                if (t.EndsWith("```", StringComparison.Ordinal))
                {
                    t = t.Substring(0, t.Length - 3);
                }
            }

            // Remove "N| " line prefixes the model may have copied from the numbered input.
            t = Regex.Replace(t, @"^\s*\d+\|\s?", string.Empty, RegexOptions.Multiline);
            return t.TrimEnd();
        }
    }
}
