using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DotNetCodeQualityPack.Rules;
using DotNetCodeQualityPack.Services;

namespace DotNetCodeQualityPack.CodeCompass
{
    internal sealed class RuleBlock
    {
        public string Id { get; set; }

        public string Text { get; set; }

        public bool IsError { get; set; }

        /// <summary>Rules from the team's own CodeQualityRules.md are sent before general ones.</summary>
        public bool IsTeamGuideline { get; set; }
    }

    internal sealed class RuleSet
    {
        public string SourceDescription { get; set; } = "No rules found (general .NET best practices)";

        public List<RuleBlock> Rules { get; } = new List<RuleBlock>();

        /// <summary>"Project facts" from CodeQualityRules.md (not rules; context for the model).</summary>
        public List<string> Context { get; } = new List<string>();

        public bool IsEmpty => Rules.Count == 0;
    }

    /// <summary>
    /// Collects the rules a Code Compass review is checked against, from the same places the Team Rules window uses:
    /// CodeQualityRules.md (AI review guidelines and custom rules), .github/instructions/*.instructions.md, and the
    /// CQ-style docs/coding-best-practices.md. Then picks the ones most relevant to the code within a character budget.
    /// </summary>
    internal static class RuleSetProvider
    {
        private static readonly string[] TeamRuleFiles =
        {
            "CodeQualityRules.md",
            "TeamRules.md",
            "code-quality-rules.md",
            ".codequality/rules.md",
            "docs/CodeQualityRules.md",
            "docs/standards/CodeQualityRules.md",
        };

        private static readonly string[] BestPracticeFiles =
        {
            "docs/coding-best-practices.md",
            "coding-best-practices.md",
            ".codecompass/rules.md",
        };

        private static readonly Regex HeadingRule = new Regex(
            @"^###\s+(?<id>[A-Z]{2,8}-[A-Z]{2,8}-\d{2,4})\b.*?(?=^###\s|^##\s|^---\s*$|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);

        private static readonly Regex ErrorMarker = new Regex(
            @"\[Error\]|Severity:\**\s*Error",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly string[] Hints =
        {
            "sql", "password", "token", "secret", "apikey", "connectionstring", "authorize", "controller", "html.raw",
            "process.start", "binaryformatter", "md5", "sha1", "random", "async", "await", "task", ".result", ".wait(",
            "thread", "lock", "configureawait", "catch", "throw", "exception", "dispose", "using", "httpclient", "stream",
            "sqlconnection", "reader", "foreach", "tolist", "linq", "where(", "count(", "stringbuilder", "dbcontext",
            "include(", "savechanges", "transaction", "iactionresult", "logger", "console.write", "configuration",
            "ioptions", "#if debug", "assert", "mock", "system.web", "httpcontext", "datetime", "tolower", "event",
        };

        /// <param name="contextRoot">Solution folder, or the open folder / file's folder when there is no solution.</param>
        /// <param name="solutionPath">The .sln path (used for the rules file chosen in Team Rules), or null.</param>
        /// <param name="filePath">The file under review (for .github/instructions applyTo), or null for pasted code.</param>
        public static RuleSet Load(string contextRoot, string solutionPath, PackSettings settings, string filePath)
        {
            var set = new RuleSet();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sources = new List<string>();

            if (string.IsNullOrEmpty(contextRoot) || !Directory.Exists(contextRoot))
            {
                return set;
            }

            InstructionSet copilot = null;
            try
            {
                copilot = InstructionSet.Discover(contextRoot);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // No instructions.
            }

            string[] roots = new[] { contextRoot, copilot?.RootDirectory }
                .Where(r => r != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // 1. CodeQualityRules.md (the file chosen in Team Rules wins).
            string teamFile = null;
            if (!string.IsNullOrEmpty(solutionPath) && settings.RulesFiles.TryGetValue(solutionPath, out string saved) && File.Exists(saved))
            {
                teamFile = saved;
            }

            teamFile = teamFile ?? FirstExisting(roots, TeamRuleFiles);
            if (teamFile != null)
            {
                try
                {
                    RulesDocument doc = RulesDocument.Load(teamFile);
                    foreach (ReviewGuideline g in doc.Guidelines)
                    {
                        Add(set, seen, g.Id, g.Id + ": " + g.Text, isError: false, team: true);
                    }

                    foreach (CustomRule r in doc.CustomRules)
                    {
                        string text = r.Id + " [" + r.Severity + "]: " + (r.Message ?? r.Title);
                        Add(set, seen, r.Id, text, string.Equals(r.Severity, "error", StringComparison.OrdinalIgnoreCase), team: true);
                    }

                    set.Context.AddRange(doc.Context);
                    sources.Add(Path.GetFileName(teamFile));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)
                {
                    sources.Add(Path.GetFileName(teamFile) + " (unreadable)");
                }
            }

            // 2. GitHub Copilot instruction files that apply to this file.
            if (copilot != null && settings.UseCopilotInstructions)
            {
                string target = string.IsNullOrEmpty(filePath) ? Path.Combine(copilot.RootDirectory, "Code.cs") : filePath;
                int before = set.Rules.Count;
                foreach (InstructionFile file in copilot.FilesFor(target).Where(f => !settings.DisabledInstructionFiles.Contains(f.Name)))
                {
                    foreach (InstructionRule rule in file.Rules.Where(r => r.Reviewable))
                    {
                        Add(set, seen, rule.Id, rule.Id + ": " + rule.Text, ErrorMarker.IsMatch(rule.Text ?? string.Empty), team: false);
                    }
                }

                if (set.Rules.Count > before)
                {
                    sources.Add(".github/instructions");
                }
            }

            // 3. CQ-style best practices (### CQ-XXX-000 headings).
            string bestPractices = FirstExisting(roots, BestPracticeFiles);
            if (bestPractices != null)
            {
                try
                {
                    int before = set.Rules.Count;
                    foreach (Match m in HeadingRule.Matches(File.ReadAllText(bestPractices)))
                    {
                        string block = Compact(m.Value);
                        Add(set, seen, m.Groups["id"].Value, block, ErrorMarker.IsMatch(block), team: false);
                    }

                    if (set.Rules.Count > before)
                    {
                        sources.Add(Path.GetFileName(bestPractices));
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    sources.Add(Path.GetFileName(bestPractices) + " (unreadable)");
                }
            }

            if (sources.Count > 0)
            {
                set.SourceDescription = string.Join(" + ", sources) + " · " + set.Rules.Count + " rules";
            }

            return set;
        }

        /// <summary>Team guidelines and Error rules first, then rules that mention what the code uses, up to the budget.</summary>
        public static string SelectRelevant(RuleSet set, string code, int charBudget)
        {
            if (set == null || set.IsEmpty)
            {
                return string.Empty;
            }

            string lower = (code ?? string.Empty).ToLowerInvariant();
            string[] inCode = Hints.Where(h => lower.Contains(h)).ToArray();

            int Score(RuleBlock r)
            {
                string text = r.Text.ToLowerInvariant();
                return (r.IsTeamGuideline ? 200 : 0) + (r.IsError ? 100 : 0) + inCode.Count(h => text.Contains(h)) * 10;
            }

            var picked = new StringBuilder();
            foreach (RuleBlock rule in set.Rules.OrderByDescending(Score).ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase))
            {
                if (picked.Length + rule.Text.Length + 2 > charBudget)
                {
                    continue;
                }

                picked.AppendLine(rule.Text).AppendLine();
            }

            return picked.ToString();
        }

        private static void Add(RuleSet set, HashSet<string> seen, string id, string text, bool isError, bool team)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(text) || !seen.Add(id))
            {
                return;
            }

            set.Rules.Add(new RuleBlock { Id = id, Text = text.Trim(), IsError = isError, IsTeamGuideline = team });
        }

        private static string FirstExisting(IEnumerable<string> roots, IEnumerable<string> names)
        {
            return roots
                .SelectMany(root => names.Select(name => Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar))))
                .FirstOrDefault(File.Exists);
        }

        /// <summary>Removes blank lines and long code samples so more rules fit into small context windows.</summary>
        private static string Compact(string block)
        {
            var sb = new StringBuilder();
            bool inCode = false;
            int codeLines = 0;
            foreach (string raw in block.Replace("\r", string.Empty).Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.StartsWith("```", StringComparison.Ordinal))
                {
                    inCode = !inCode;
                    codeLines = 0;
                    sb.AppendLine(line);
                    continue;
                }

                if ((inCode && ++codeLines > 6) || line.Length == 0)
                {
                    continue;
                }

                sb.AppendLine(line);
            }

            return sb.ToString().Trim();
        }
    }
}
