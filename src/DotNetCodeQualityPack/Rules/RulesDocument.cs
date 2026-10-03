using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DotNetCodeQualityPack.Rules
{
    /// <summary>"| SA1101 | none | why |" in the Severity overrides table.</summary>
    internal sealed class SeverityOverride
    {
        public string RuleId { get; set; }

        public string Severity { get; set; }

        public string Note { get; set; }

        public int SourceLine { get; set; }
    }

    /// <summary>"| key | value | why |" in the EditorConfig settings table.</summary>
    internal sealed class EditorConfigSetting
    {
        public string Key { get; set; }

        public string Value { get; set; }

        public string Note { get; set; }

        public int SourceLine { get; set; }
    }

    /// <summary>A "### ID: Title" block in the Custom rules section.</summary>
    internal sealed class CustomRule
    {
        public string Id { get; set; }

        public string Title { get; set; }

        public string Severity { get; set; } = "warning";

        public List<string> Files { get; } = new List<string>();

        public List<string> Exclude { get; } = new List<string>();

        /// <summary>Regular expression, or plain text when <see cref="IsLiteral"/> is true.</summary>
        public string Pattern { get; set; }

        public bool IsLiteral { get; set; }

        public string Message { get; set; }

        /// <summary>Replacement text for the automatic fix; null when the rule has no fix.</summary>
        public string Replace { get; set; }

        public bool IgnoreComments { get; set; } = true;

        public bool CaseSensitive { get; set; } = true;

        public Regex Regex { get; set; }

        public string SourceFile { get; set; }

        public int SourceLine { get; set; }

        public bool HasFix => Replace != null;
    }

    /// <summary>A bullet in the AI review guidelines section (or a Copilot instruction bullet).</summary>
    internal sealed class ReviewGuideline
    {
        public string Id { get; set; }

        public string Text { get; set; }

        /// <summary>The .md file the rule is written in (for "Open rule").</summary>
        public string SourceFile { get; set; }

        public int SourceLine { get; set; }
    }

    internal sealed class RulesProblem
    {
        public RulesProblem(int line, string message)
        {
            Line = line;
            Message = message;
        }

        public int Line { get; }

        public string Message { get; }

        public override string ToString() => Line > 0 ? "Line " + Line + ": " + Message : Message;
    }

    /// <summary>
    /// A team rules file written in Markdown. Sections are found by their "##" heading:
    /// Severity overrides (table), EditorConfig settings (table), Custom rules ("###" blocks), AI review guidelines (bullets).
    /// </summary>
    internal sealed class RulesDocument
    {
        private static readonly Regex RuleIdPattern = new Regex(@"^[A-Za-z][A-Za-z0-9_]*[0-9]+$", RegexOptions.CultureInvariant);
        private static readonly Regex SettingKeyPattern = new Regex(@"^[A-Za-z_][A-Za-z0-9_.\-]*$", RegexOptions.CultureInvariant);
        private static readonly Regex CustomHeading = new Regex(@"^`?([A-Za-z][A-Za-z0-9_\-]*[0-9])`?\s*(?:[:\-–—]\s*(.*))?$", RegexOptions.CultureInvariant);
        private static readonly Regex NumberedItem = new Regex(@"^\d+[.)]\s+(.+)$", RegexOptions.CultureInvariant);
        private static readonly Regex SeparatorCell = new Regex(@"^:?-{2,}:?$", RegexOptions.CultureInvariant);

        private static readonly HashSet<string> HeaderCells = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "rule", "rule id", "id", "key", "setting", "name", "diagnostic",
        };

        private enum Section
        {
            None,
            Severity,
            Settings,
            Custom,
            Guidelines,
            Context,
        }

        public string FilePath { get; private set; }

        public List<SeverityOverride> Overrides { get; } = new List<SeverityOverride>();

        public List<EditorConfigSetting> Settings { get; } = new List<EditorConfigSetting>();

        public List<CustomRule> CustomRules { get; } = new List<CustomRule>();

        public List<ReviewGuideline> Guidelines { get; } = new List<ReviewGuideline>();

        /// <summary>Facts about the codebase sent with every AI review ("## AI review context"). Not rules.</summary>
        public List<string> Context { get; } = new List<string>();

        public List<RulesProblem> Problems { get; } = new List<RulesProblem>();

        public bool IsEmpty => Overrides.Count == 0 && Settings.Count == 0 && CustomRules.Count == 0 && Guidelines.Count == 0 && Context.Count == 0;

        public static RulesDocument Load(string path)
        {
            return Parse(File.ReadAllText(path), path);
        }

        public static RulesDocument Parse(string markdown, string filePath)
        {
            var doc = new RulesDocument { FilePath = filePath };
            string[] lines = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            Section section = Section.None;
            CustomRule current = null;
            bool inFence = false;
            bool inComment = false;

            for (int i = 0; i < lines.Length; i++)
            {
                int lineNumber = i + 1;
                string line = lines[i].Trim();

                if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                    continue;
                }

                if (inFence)
                {
                    continue;
                }

                // HTML comments (<!-- ... -->), possibly over several lines.
                if (inComment)
                {
                    if (line.Contains("-->"))
                    {
                        inComment = false;
                    }

                    continue;
                }

                if (line.StartsWith("<!--", StringComparison.Ordinal))
                {
                    inComment = !line.Contains("-->");
                    continue;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                int level = HeadingLevel(line, out string heading);
                if (level > 0)
                {
                    if (level <= 2)
                    {
                        doc.Finish(current);
                        current = null;
                        section = Classify(heading);
                    }
                    else if (level == 3)
                    {
                        doc.Finish(current);
                        current = section == Section.Custom ? doc.StartCustom(heading, lineNumber) : null;
                    }

                    continue;
                }

                switch (section)
                {
                    case Section.Severity:
                        if (line.StartsWith("|", StringComparison.Ordinal))
                        {
                            doc.ParseOverrideRow(line, lineNumber);
                        }

                        break;

                    case Section.Settings:
                        if (line.StartsWith("|", StringComparison.Ordinal))
                        {
                            doc.ParseSettingRow(line, lineNumber);
                        }

                        break;

                    case Section.Custom:
                        if (current != null && TryBullet(line, out string property))
                        {
                            doc.ParseCustomProperty(current, property, lineNumber);
                        }

                        break;

                    case Section.Guidelines:
                        if (TryBullet(line, out string item) || TryNumbered(line, out item))
                        {
                            doc.Guidelines.Add(new ReviewGuideline
                            {
                                Id = "G" + (doc.Guidelines.Count + 1),
                                Text = item,
                                SourceFile = filePath,
                                SourceLine = lineNumber,
                            });
                        }

                        break;

                    case Section.Context:
                        // Bullets and plain sentences are both accepted as facts.
                        if (TryBullet(line, out string fact) || TryNumbered(line, out fact))
                        {
                            doc.Context.Add(fact);
                        }
                        else if (!line.StartsWith("|", StringComparison.Ordinal) && !line.StartsWith(">", StringComparison.Ordinal))
                        {
                            doc.Context.Add(line);
                        }

                        break;
                }
            }

            doc.Finish(current);
            return doc;
        }

        /// <summary>none, silent, suggestion, warning or error; null when the text isn't a severity.</summary>
        public static string NormalizeSeverity(string text)
        {
            switch ((text ?? string.Empty).Trim().Trim('`', '*').ToLowerInvariant())
            {
                case "none":
                case "off":
                case "disable":
                case "disabled":
                    return "none";
                case "silent":
                case "hidden":
                    return "silent";
                case "suggestion":
                case "info":
                case "information":
                case "message":
                    return "suggestion";
                case "warning":
                case "warn":
                    return "warning";
                case "error":
                case "err":
                    return "error";
                default:
                    return null;
            }
        }

        // ------------------------------------------------------------------ sections

        private static int HeadingLevel(string line, out string text)
        {
            text = null;
            int level = 0;
            while (level < line.Length && level < 7 && line[level] == '#')
            {
                level++;
            }

            if (level == 0 || level > 6 || level >= line.Length || line[level] != ' ')
            {
                return 0;
            }

            text = line.Substring(level).Trim().TrimEnd('#').Trim();
            return level;
        }

        private static Section Classify(string heading)
        {
            string h = heading.ToLowerInvariant();

            if (h.Contains("severity") || h.Contains("override"))
            {
                return Section.Severity;
            }

            if (h.Contains("editorconfig") || h.Contains("setting"))
            {
                return Section.Settings;
            }

            // Before the guidelines check: "AI review context" also contains "ai review".
            if (h.Contains("context"))
            {
                return Section.Context;
            }

            if (h.Contains("guideline") || h.Contains("ai review") || Regex.IsMatch(h, @"\bai\b"))
            {
                return Section.Guidelines;
            }

            if (h.Contains("custom") || h.Contains("pattern"))
            {
                return Section.Custom;
            }

            return Section.None;
        }

        private static bool TryBullet(string line, out string item)
        {
            item = null;
            if (line.Length > 2 && (line[0] == '-' || line[0] == '*' || line[0] == '+') && line[1] == ' ')
            {
                item = line.Substring(2).Trim();
                return item.Length > 0;
            }

            return false;
        }

        private static bool TryNumbered(string line, out string item)
        {
            Match m = NumberedItem.Match(line);
            item = m.Success ? m.Groups[1].Value.Trim() : null;
            return m.Success;
        }

        // ------------------------------------------------------------------ tables

        private static List<string> SplitRow(string line)
        {
            string row = line.Replace("\\|", "\u0001").Trim();
            if (row.StartsWith("|", StringComparison.Ordinal))
            {
                row = row.Substring(1);
            }

            if (row.EndsWith("|", StringComparison.Ordinal))
            {
                row = row.Substring(0, row.Length - 1);
            }

            return row.Split('|').Select(c => c.Replace('\u0001', '|').Trim()).ToList();
        }

        private static bool IsSeparatorOrHeader(List<string> cells)
        {
            if (cells.All(c => c.Length == 0 || SeparatorCell.IsMatch(c)))
            {
                return true;
            }

            return HeaderCells.Contains(StripCode(cells[0]));
        }

        private void ParseOverrideRow(string line, int lineNumber)
        {
            List<string> cells = SplitRow(line);
            if (cells.Count == 0 || IsSeparatorOrHeader(cells))
            {
                return;
            }

            if (cells.Count < 2)
            {
                Problems.Add(new RulesProblem(lineNumber, "Expected '| rule | severity | why |'."));
                return;
            }

            string id = StripCode(cells[0]);
            if (!RuleIdPattern.IsMatch(id))
            {
                Problems.Add(new RulesProblem(lineNumber, "'" + id + "' doesn't look like a rule ID (for example SA1101 or CA2007)."));
                return;
            }

            string severity = NormalizeSeverity(cells[1]);
            if (severity == null)
            {
                Problems.Add(new RulesProblem(lineNumber, "Unknown severity '" + cells[1] + "' for " + id + ". Use none, silent, suggestion, warning or error."));
                return;
            }

            SeverityOverride existing = Overrides.FirstOrDefault(o => string.Equals(o.RuleId, id, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                Problems.Add(new RulesProblem(lineNumber, id + " is listed twice; this line replaces line " + existing.SourceLine + "."));
                Overrides.Remove(existing);
            }

            Overrides.Add(new SeverityOverride
            {
                RuleId = id.ToUpperInvariant(),
                Severity = severity,
                Note = cells.Count > 2 ? StripCode(cells[2]) : string.Empty,
                SourceLine = lineNumber,
            });
        }

        private void ParseSettingRow(string line, int lineNumber)
        {
            List<string> cells = SplitRow(line);
            if (cells.Count == 0 || IsSeparatorOrHeader(cells))
            {
                return;
            }

            if (cells.Count < 2)
            {
                Problems.Add(new RulesProblem(lineNumber, "Expected '| key | value | why |'."));
                return;
            }

            string key = StripCode(cells[0]);
            string value = StripCode(cells[1]);

            if (!SettingKeyPattern.IsMatch(key) || string.Equals(key, "root", StringComparison.OrdinalIgnoreCase))
            {
                Problems.Add(new RulesProblem(lineNumber, "'" + key + "' isn't a valid .editorconfig key."));
                return;
            }

            if (value.Length == 0)
            {
                Problems.Add(new RulesProblem(lineNumber, key + " has no value."));
                return;
            }

            EditorConfigSetting existing = Settings.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                Problems.Add(new RulesProblem(lineNumber, key + " is listed twice; this line replaces line " + existing.SourceLine + "."));
                Settings.Remove(existing);
            }

            Settings.Add(new EditorConfigSetting
            {
                Key = key,
                Value = value,
                Note = cells.Count > 2 ? StripCode(cells[2]) : string.Empty,
                SourceLine = lineNumber,
            });
        }

        // ------------------------------------------------------------------ custom rules

        private CustomRule StartCustom(string heading, int lineNumber)
        {
            var rule = new CustomRule { SourceLine = lineNumber, SourceFile = FilePath };
            Match m = CustomHeading.Match(heading);

            if (m.Success)
            {
                rule.Id = m.Groups[1].Value.ToUpperInvariant();
                rule.Title = m.Groups[2].Success && m.Groups[2].Value.Trim().Length > 0 ? m.Groups[2].Value.Trim() : rule.Id;
            }
            else
            {
                rule.Id = "CUSTOM" + (CustomRules.Count + 1).ToString("000");
                rule.Title = heading;
            }

            return rule;
        }

        private void ParseCustomProperty(CustomRule rule, string item, int lineNumber)
        {
            int colon = item.IndexOf(':');
            if (colon <= 0)
            {
                Problems.Add(new RulesProblem(lineNumber, rule.Id + ": expected '- key: value'."));
                return;
            }

            string key = item.Substring(0, colon).Trim().Trim('*', '`').Trim().ToLowerInvariant();
            string value = StripCode(item.Substring(colon + 1).Trim());

            switch (key)
            {
                case "severity":
                    string severity = NormalizeSeverity(value);
                    if (severity == null)
                    {
                        Problems.Add(new RulesProblem(lineNumber, rule.Id + ": unknown severity '" + value + "'."));
                    }
                    else
                    {
                        rule.Severity = severity;
                    }

                    break;

                case "files":
                case "include":
                    rule.Files.AddRange(SplitList(value));
                    break;

                case "exclude":
                    rule.Exclude.AddRange(SplitList(value));
                    break;

                case "pattern":
                case "regex":
                    rule.Pattern = value;
                    rule.IsLiteral = false;
                    break;

                case "text":
                case "contains":
                    rule.Pattern = value;
                    rule.IsLiteral = true;
                    break;

                case "message":
                    rule.Message = value;
                    break;

                case "replace":
                case "replacement":
                case "fix":
                    rule.Replace = value;
                    break;

                case "ignore-comments":
                case "ignore_comments":
                    rule.IgnoreComments = ParseBool(value, rule.IgnoreComments, rule.Id, key, lineNumber);
                    break;

                case "case-sensitive":
                case "case_sensitive":
                    rule.CaseSensitive = ParseBool(value, rule.CaseSensitive, rule.Id, key, lineNumber);
                    break;

                default:
                    Problems.Add(new RulesProblem(lineNumber, rule.Id + ": unknown key '" + key + "' (ignored)."));
                    break;
            }
        }

        private bool ParseBool(string value, bool fallback, string ruleId, string key, int lineNumber)
        {
            switch (value.Trim().ToLowerInvariant())
            {
                case "true":
                case "yes":
                case "on":
                    return true;
                case "false":
                case "no":
                case "off":
                    return false;
                default:
                    Problems.Add(new RulesProblem(lineNumber, ruleId + ": " + key + " should be true or false."));
                    return fallback;
            }
        }

        private void Finish(CustomRule rule)
        {
            if (rule == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(rule.Pattern))
            {
                Problems.Add(new RulesProblem(rule.SourceLine, rule.Id + " has no '- pattern:' or '- text:' line, so it was skipped."));
                return;
            }

            RegexOptions options = RegexOptions.CultureInvariant | RegexOptions.Multiline;
            if (!rule.CaseSensitive)
            {
                options |= RegexOptions.IgnoreCase;
            }

            try
            {
                rule.Regex = new Regex(rule.IsLiteral ? Regex.Escape(rule.Pattern) : rule.Pattern, options, TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException ex)
            {
                Problems.Add(new RulesProblem(rule.SourceLine, rule.Id + ": the pattern isn't a valid regular expression (" + ex.Message + ")."));
                return;
            }

            if (rule.Files.Count == 0)
            {
                rule.Files.Add("**/*.cs");
            }

            if (string.IsNullOrWhiteSpace(rule.Message))
            {
                rule.Message = rule.Title;
            }

            if (CustomRules.Any(r => string.Equals(r.Id, rule.Id, StringComparison.OrdinalIgnoreCase)))
            {
                Problems.Add(new RulesProblem(rule.SourceLine, rule.Id + " is used by more than one rule; give each rule its own ID."));
            }

            CustomRules.Add(rule);
        }

        // ------------------------------------------------------------------ helpers

        private static IEnumerable<string> SplitList(string value)
        {
            return value
                .Split(',')
                .Select(v => StripCode(v.Trim()))
                .Where(v => v.Length > 0);
        }

        /// <summary>Removes one pair of surrounding backticks: `x` -> x.</summary>
        private static string StripCode(string text)
        {
            string t = (text ?? string.Empty).Trim();
            if (t.Length >= 2 && t[0] == '`' && t[t.Length - 1] == '`')
            {
                t = t.Substring(1, t.Length - 2);
            }

            return t;
        }
    }
}
