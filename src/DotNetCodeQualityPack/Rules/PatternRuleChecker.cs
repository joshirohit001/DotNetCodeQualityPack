using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DotNetCodeQualityPack.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DotNetCodeQualityPack.Rules
{
    internal sealed class PatternCheckResult
    {
        public List<RuleFinding> Findings { get; } = new List<RuleFinding>();

        public List<string> Warnings { get; } = new List<string>();

        public int FilesChecked { get; set; }
    }

    /// <summary>Runs the custom pattern rules from the Markdown file over the solution's source files.</summary>
    internal static class PatternRuleChecker
    {
        private const int MaxPerRulePerFile = 200;
        private const int MaxTotal = 5000;

        /// <param name="onlyFilePaths">Limit the check to these files (null = whole solution).</param>
        public static async Task<PatternCheckResult> CheckAsync(
            Solution solution,
            string solutionDirectory,
            IReadOnlyList<CustomRule> rules,
            ISet<string> onlyFilePaths,
            IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            var result = new PatternCheckResult();
            if (rules == null || rules.Count == 0)
            {
                return result;
            }

            // Multi-targeted projects list the same file once per target framework; check each file once.
            List<Document> documents = solution.Projects
                .SelectMany(p => p.Documents)
                .Where(d => !string.IsNullOrEmpty(d.FilePath))
                .Where(d => onlyFilePaths == null || onlyFilePaths.Contains(d.FilePath))
                .GroupBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int i = 0; i < documents.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Document document = documents[i];

                if (i % 25 == 0)
                {
                    progress?.Report(string.Format("Checking custom rules: file {0} of {1}...", i + 1, documents.Count));
                }

                string relative = RelativePath(document.FilePath, solutionDirectory);
                List<CustomRule> applicable = rules.Where(r => Applies(r, relative)).ToList();
                if (applicable.Count == 0)
                {
                    continue;
                }

                result.FilesChecked++;
                SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                string content = text.ToString();
                SyntaxNode root = applicable.Any(r => r.IgnoreComments)
                    ? await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                    : null;

                foreach (CustomRule rule in applicable)
                {
                    int count = 0;
                    try
                    {
                        foreach (Match match in rule.Regex.Matches(content))
                        {
                            if (match.Length == 0 || (rule.IgnoreComments && IsInComment(root, match.Index)))
                            {
                                continue;
                            }

                            result.Findings.Add(CreateFinding(rule, document, relative, text, match));

                            if (++count >= MaxPerRulePerFile)
                            {
                                result.Warnings.Add(string.Format("{0}: stopped after {1} matches in {2}.", rule.Id, MaxPerRulePerFile, relative));
                                break;
                            }

                            if (result.Findings.Count >= MaxTotal)
                            {
                                result.Warnings.Add("Stopped after " + MaxTotal + " findings. Narrow the rules' files or patterns.");
                                return result;
                            }
                        }
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        result.Warnings.Add(string.Format("{0}: the pattern took too long on {1} and was skipped for that file.", rule.Id, relative));
                    }
                }
            }

            return result;
        }

        public static string RelativePath(string filePath, string solutionDirectory)
        {
            string relative = string.IsNullOrEmpty(solutionDirectory) ? filePath : PathHelper.Relative(filePath, solutionDirectory);
            return relative.Replace('\\', '/');
        }

        private static bool Applies(CustomRule rule, string relativePath)
        {
            return rule.Files.Any(g => Glob.IsMatch(g, relativePath))
                && !rule.Exclude.Any(g => Glob.IsMatch(g, relativePath));
        }

        private static bool IsInComment(SyntaxNode root, int position)
        {
            if (root == null || position < 0 || position >= root.FullSpan.End)
            {
                return false;
            }

            SyntaxTrivia trivia = root.FindTrivia(position);
            if (!trivia.FullSpan.Contains(position))
            {
                return false;
            }

            string text = trivia.ToString();
            return text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith("/*", StringComparison.Ordinal);
        }

        private static RuleFinding CreateFinding(CustomRule rule, Document document, string relative, SourceText text, Match match)
        {
            TextLine line = text.Lines.GetLineFromPosition(match.Index);

            var finding = new RuleFinding
            {
                Source = FindingSource.Custom,
                RuleId = rule.Id,
                Title = rule.Title,
                Message = rule.Message,
                Severity = RuleFinding.ToSeverity(rule.Severity),
                FilePath = document.FilePath,
                RelativePath = relative,
                DocumentId = document.Id,
                Line = line.LineNumber + 1,
                LineText = line.ToString().Trim(),
                Snapshot = text,
                RuleSourceFile = rule.SourceFile,
                RuleSourceLine = rule.SourceLine,
            };

            if (rule.HasFix)
            {
                finding.FixSpan = new TextSpan(match.Index, match.Length);
                finding.FixReplacement = rule.IsLiteral ? rule.Replace : match.Result(rule.Replace);
            }

            return finding;
        }
    }
}
