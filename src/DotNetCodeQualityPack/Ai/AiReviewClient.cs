using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DotNetCodeQualityPack.Fixer;
using DotNetCodeQualityPack.Rules;
using DotNetCodeQualityPack.Services;

namespace DotNetCodeQualityPack.Ai
{
    internal sealed class AiFinding
    {
        public int Line { get; set; }

        public string Guideline { get; set; }

        public string Message { get; set; }

        public string Original { get; set; }

        public string Replacement { get; set; }
    }

    internal sealed class AiReviewResult
    {
        public List<AiFinding> Findings { get; } = new List<AiFinding>();

        public List<string> Warnings { get; } = new List<string>();

        /// <summary>Tokens used by this review's requests.</summary>
        public TokenUsage Usage { get; set; } = new TokenUsage();
    }

    /// <summary>
    /// Reviews code against the team guidelines with the model the user picked (OpenAI, Anthropic or Ollama, through
    /// <see cref="AiProviders"/>): splits the file and the guidelines into small requests and checks every finding
    /// against the real code before showing it.
    /// </summary>
    internal sealed class AiReviewClient
    {
        private const string SystemPrompt =
            "You are a careful senior C# code reviewer. You check code only against the team guidelines you are given. " +
            "You answer with JSON only, never with prose or markdown.";

        // ------------------------------------------------------------------ review

        public async Task<AiReviewResult> ReviewAsync(
            AiModel model,
            PackSettings settings,
            string relativePath,
            string code,
            IReadOnlyList<ReviewGuideline> guidelines,
            IReadOnlyList<string> context,
            TokenUsage usage,
            IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            // The caller passes its own counter so tokens are still counted when a review fails or is cancelled.
            var result = new AiReviewResult { Usage = usage ?? new TokenUsage() };
            int dropped = 0;
            string[] lines = LineDiff.SplitLines(code);
            int chunk = Math.Max(40, settings.ChunkLines);
            int chunks = (lines.Length + chunk - 1) / chunk;

            // Many rules (e.g. several Copilot instruction files) are sent in batches so each request stays small.
            int batchSize = Math.Max(5, settings.GuidelinesPerRequest);
            var batches = new List<List<ReviewGuideline>>();
            for (int i = 0; i < guidelines.Count; i += batchSize)
            {
                batches.Add(guidelines.Skip(i).Take(batchSize).ToList());
            }

            int total = chunks * batches.Count;
            int step = 0;

            for (int start = 0; start < lines.Length; start += chunk)
            {
                int end = Math.Min(lines.Length, start + chunk); // exclusive

                if (lines.Skip(start).Take(end - start).All(string.IsNullOrWhiteSpace))
                {
                    step += batches.Count;
                    continue;
                }

                foreach (List<ReviewGuideline> batch in batches)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    step++;

                    progress?.Report(string.Format(
                        "{0}: lines {1}–{2} of {3}, {4} rule(s){5}{6}...",
                        model.Name,
                        start + 1,
                        end,
                        lines.Length,
                        batch.Count,
                        total > 1 ? " (request " + step + " of " + total + ")" : string.Empty,
                        result.Usage.IsEmpty ? string.Empty : " · " + TokenUsage.Short(result.Usage.Total) + " tokens so far"));

                    string prompt = BuildPrompt(relativePath, lines, start, end, batch, context);
                    string answer = await GenerateAsync(model, settings, prompt, result.Usage, cancellationToken).ConfigureAwait(false);
                    result.Findings.AddRange(ParseFindings(answer, start + 1, end, lines, batch, result.Warnings, ref dropped));
                }
            }

            if (dropped > 0)
            {
                result.Warnings.Add(string.Format(
                    "{0} suggestion(s) were left out because they quoted code that isn't in the file or removed a constructor dependency.",
                    dropped));
            }

            // The same problem can be reported twice by overlapping answers.
            List<AiFinding> unique = result.Findings
                .GroupBy(f => f.Line + "|" + f.Guideline + "|" + f.Message)
                .Select(g => g.First())
                .OrderBy(f => f.Line)
                .ToList();
            result.Findings.Clear();
            result.Findings.AddRange(unique);
            return result;
        }

        internal static string BuildPrompt(
            string relativePath,
            string[] lines,
            int start,
            int end,
            IReadOnlyList<ReviewGuideline> guidelines,
            IReadOnlyList<string> context)
        {
            var sb = new StringBuilder();

            // Facts, not rules: the language version decides which syntax is valid.
            sb.AppendLine("Project facts (these are not rules, do not report them):");
            if (context != null)
            {
                foreach (string fact in context)
                {
                    sb.Append("- ").AppendLine(fact);
                }
            }

            sb.AppendLine("- Modern C# syntax is valid when the language version supports it. A primary constructor such as "
                + "`public class OrderController(IMediator mediator) : ControllerBase` IS constructor injection. Records, "
                + "file-scoped namespaces, collection expressions, required members and pattern matching are allowed.");
            sb.AppendLine();

            // Short IDs (G1, G2, …) are easier for small models to repeat back than "dotnet §3.12".
            sb.AppendLine("Team guidelines:");
            for (int i = 0; i < guidelines.Count; i++)
            {
                sb.Append('G').Append(i + 1).Append(": ").AppendLine(guidelines[i].Text);
            }

            sb.AppendLine();
            sb.AppendFormat("File: {0}, lines {1}-{2} of {3}. Each code line starts with its line number and \"| \".", relativePath, start + 1, end, lines.Length);
            sb.AppendLine();
            sb.AppendLine();

            for (int i = start; i < end; i++)
            {
                sb.Append(i + 1).Append("| ").AppendLine(lines[i].Replace("\t", "    "));
            }

            sb.AppendLine();
            sb.AppendLine("Task: find places in the code above that clearly break one of the team guidelines.");
            sb.AppendLine("For each problem return:");
            sb.AppendLine("- \"line\": the line number");
            sb.AppendLine("- \"guideline\": the guideline ID, for example \"G2\"");
            sb.AppendLine("- \"message\": one short sentence that explains the problem");
            sb.AppendLine("- \"original\": the exact code on that line that should change, copied without the line number, or \"\" if there is no simple fix");
            sb.AppendLine("- \"replacement\": the corrected code that replaces \"original\", or \"\"");
            sb.AppendLine("Only report real violations of the guidelines listed above. Don't report anything the guidelines don't mention.");
            sb.AppendLine("Report a line only if its code clearly breaks the guideline. If the code already follows the guideline, do not report it.");
            sb.AppendLine("Never suggest removing constructor parameters, injected dependencies or attributes. \"original\" must be copied exactly from the code above.");
            sb.AppendLine("When unsure, do not report. Fewer, correct findings are better than many doubtful ones.");
            sb.AppendLine("Answer with JSON only, in exactly this shape:");
            sb.AppendLine("{\"findings\":[{\"line\":12,\"guideline\":\"G2\",\"message\":\"...\",\"original\":\"...\",\"replacement\":\"...\"}]}");
            sb.AppendLine("If nothing breaks the guidelines, answer {\"findings\":[]}");
            return sb.ToString();
        }

        private static Task<string> GenerateAsync(AiModel model, PackSettings settings, string prompt, TokenUsage usage, CancellationToken cancellationToken)
        {
            var request = new AiRequest
            {
                Model = model,
                SystemPrompt = SystemPrompt,
                JsonMode = true,
                MaxOutputTokens = 4096,
                Temperature = 0.1,
                ContextSize = settings.ContextSize,
                TimeoutSeconds = settings.TimeoutSeconds,
            };
            request.Messages.Add(new AiMessage { Role = AiMessage.User, Text = prompt });
            return AiProviders.CompleteAsync(request, null, usage, cancellationToken);
        }

        internal static List<AiFinding> ParseFindings(
            string answer,
            int firstLine,
            int lastLine,
            string[] lines,
            IReadOnlyList<ReviewGuideline> guidelines,
            List<string> warnings,
            ref int dropped)
        {
            var findings = new List<AiFinding>();
            string json = ExtractJson(answer);
            string chunkText = NormalizeSpace(string.Join(" ", lines.Skip(firstLine - 1).Take(lastLine - firstLine + 1)));

            if (json == null || !MiniJson.TryParse(json, out object root))
            {
                warnings.Add(string.Format("Lines {0}–{1}: the model's answer wasn't valid JSON, so this part was skipped.", firstLine, lastLine));
                return findings;
            }

            List<object> items = root as List<object>
                ?? MiniJson.Get(root, "findings") as List<object>
                ?? MiniJson.Get(root, "issues") as List<object>
                ?? MiniJson.Get(root, "violations") as List<object>;

            if (items == null)
            {
                return findings;
            }

            foreach (object item in items)
            {
                string message = MiniJson.GetString(item, "message")
                    ?? MiniJson.GetString(item, "issue")
                    ?? MiniJson.GetString(item, "description");
                if (string.IsNullOrWhiteSpace(message))
                {
                    continue;
                }

                string original = MiniJson.GetString(item, "original") ?? string.Empty;
                string replacement = MiniJson.GetString(item, "replacement") ?? MiniJson.GetString(item, "fix") ?? string.Empty;
                int? line = MiniJson.AsInt(MiniJson.Get(item, "line"));

                // Evidence check: the quoted code must really be in this part of the file.
                if (original.Trim().Length > 0 && !chunkText.Contains(NormalizeSpace(original)))
                {
                    dropped++;
                    continue;
                }

                // Small models like to "fix" DI by deleting a (primary) constructor's parameters.
                if (RemovesConstructorParameters(original, replacement))
                {
                    dropped++;
                    continue;
                }

                if (line == null || line < firstLine || line > lastLine)
                {
                    // Small models sometimes count lines wrongly; fall back to where the quoted code is.
                    line = FindLine(lines, original, firstLine, lastLine);
                    if (line == null)
                    {
                        continue;
                    }
                }

                findings.Add(new AiFinding
                {
                    Line = line.Value,
                    Guideline = NormalizeGuideline(MiniJson.GetString(item, "guideline") ?? MiniJson.GetString(item, "rule"), guidelines),
                    Message = message.Trim(),
                    Original = original,
                    Replacement = replacement,
                });
            }

            return findings;
        }

        private static readonly Regex TypeWithParameters = new Regex(
            @"\b(?:class|record|struct)\s+\w+(?:<[^>]*>)?\s*\(([^)]*)\)",
            RegexOptions.CultureInvariant);

        private static readonly Regex ConstructorWithParameters = new Regex(
            @"\b(?:public|internal|protected|private)\s+(\w+)\s*\(([^)]*)\)\s*(?::|\{|$)",
            RegexOptions.CultureInvariant);

        /// <summary>True when the replacement drops parameters from a type's primary constructor or a constructor.</summary>
        internal static bool RemovesConstructorParameters(string original, string replacement)
        {
            if (string.IsNullOrWhiteSpace(original))
            {
                return false;
            }

            int before = ParameterCount(original, out bool isDeclaration);
            if (!isDeclaration || before == 0)
            {
                return false;
            }

            return ParameterCount(replacement ?? string.Empty, out _) < before;
        }

        private static int ParameterCount(string code, out bool isDeclaration)
        {
            Match m = TypeWithParameters.Match(code);
            string parameters = null;
            if (m.Success)
            {
                parameters = m.Groups[1].Value;
            }
            else
            {
                Match ctor = ConstructorWithParameters.Match(code);
                if (ctor.Success)
                {
                    parameters = ctor.Groups[2].Value;
                }
            }

            isDeclaration = parameters != null;
            if (string.IsNullOrWhiteSpace(parameters))
            {
                return 0;
            }

            // Count top-level commas (generic arguments like Dictionary<string, int> contain commas too).
            int depth = 0;
            int count = 1;
            foreach (char c in parameters)
            {
                if (c == '<' || c == '(' || c == '[')
                {
                    depth++;
                }
                else if (c == '>' || c == ')' || c == ']')
                {
                    depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    count++;
                }
            }

            return count;
        }

        private static string NormalizeSpace(string text)
        {
            return Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        }

        private static string ExtractJson(string answer)
        {
            if (string.IsNullOrWhiteSpace(answer))
            {
                return null;
            }

            string text = answer.Trim();

            // ```json ... ```
            Match fence = Regex.Match(text, "```(?:json)?\\s*(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (fence.Success)
            {
                text = fence.Groups[1].Value.Trim();
            }

            int objectStart = text.IndexOf('{');
            int arrayStart = text.IndexOf('[');
            bool isArray = arrayStart >= 0 && (objectStart < 0 || arrayStart < objectStart);
            int start = isArray ? arrayStart : objectStart;
            int end = text.LastIndexOf(isArray ? ']' : '}');

            return start >= 0 && end > start ? text.Substring(start, end - start + 1) : null;
        }

        private static int? FindLine(string[] lines, string original, int firstLine, int lastLine)
        {
            string wanted = (original ?? string.Empty).Trim();
            if (wanted.Length == 0 || wanted.Contains("\n"))
            {
                return null;
            }

            for (int n = firstLine; n <= lastLine && n <= lines.Length; n++)
            {
                if (lines[n - 1].Contains(wanted))
                {
                    return n;
                }
            }

            return null;
        }

        private static string NormalizeGuideline(string raw, IReadOnlyList<ReviewGuideline> guidelines)
        {
            string value = (raw ?? string.Empty).Trim().ToUpperInvariant();
            Match m = Regex.Match(value, @"^G?\s*(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int number) && number >= 1 && number <= guidelines.Count)
            {
                return guidelines[number - 1].Id;
            }

            return "AI";
        }
    }
}
