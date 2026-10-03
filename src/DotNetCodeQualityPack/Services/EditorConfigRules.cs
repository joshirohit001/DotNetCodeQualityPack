using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DotNetCodeQualityPack.Services
{
    /// <summary>
    /// Maintains a marked block of rule severities in the solution's .editorconfig.
    /// .editorconfig is the most reliable place for severities: Visual Studio and the compiler always honor it.
    /// Rules the user has already set anywhere in their .editorconfig files are never overridden.
    /// </summary>
    internal static class EditorConfigRules
    {
        public const string BeginMarker = "# >>> .NET Code Quality Pack rules";
        public const string EndMarker = "# <<< .NET Code Quality Pack rules";

        private const string CreatedHeader = "# Created by .NET Code Quality Pack.";

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private static readonly Regex KeyLine = new Regex(@"^\s*([A-Za-z0-9_.\-]+)\s*=", RegexOptions.Compiled);

        private static readonly Regex Block = new Regex(
            @"(\r?\n)?" + Regex.Escape(BeginMarker) + @".*?" + Regex.Escape(EndMarker) + @"[^\r\n]*(\r?\n)?",
            RegexOptions.Singleline);

        public static void Ensure(string solutionDirectory, List<string> log)
        {
            string path = Path.Combine(solutionDirectory, SolutionConfigurator.EditorConfigFileName);
            bool exists = File.Exists(path);
            string existing = exists ? File.ReadAllText(path) : string.Empty;

            if (existing.IndexOf(BeginMarker, StringComparison.Ordinal) >= 0)
            {
                log.Add(".editorconfig already has the Code Quality Pack rules block (left as is, including any edits you made).");
                return;
            }

            List<string> inheritedFrom;
            HashSet<string> definedKeys = CollectDefinedKeys(solutionDirectory, out inheritedFrom);

            int skipped;
            string block = BuildBlock(TemplateProvider.EditorConfigRules, definedKeys, out skipped);
            string skippedNote = skipped > 0 ? " (" + skipped + " rule(s) skipped because you already set them)" : string.Empty;

            if (exists)
            {
                string backup = path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(path, backup);

                // Team rules from the Markdown file must stay last so they keep overriding these defaults.
                int teamBlock = existing.IndexOf(Rules.TeamEditorConfig.BeginMarker, StringComparison.Ordinal);
                string updated;
                if (teamBlock >= 0)
                {
                    updated = existing.Substring(0, teamBlock).TrimEnd('\r', '\n') + "\r\n" + block + "\r\n" + existing.Substring(teamBlock);
                }
                else
                {
                    string separator = existing.Length == 0 || existing.EndsWith("\n", StringComparison.Ordinal) ? string.Empty : "\r\n";
                    updated = existing + separator + block;
                }

                File.WriteAllText(path, updated, Utf8NoBom);
                log.Add("Added the rules block to your existing .editorconfig" + skippedNote
                    + " (backup: " + Path.GetFileName(backup) + ").");
                return;
            }

            if (inheritedFrom.Count > 0)
            {
                // A parent .editorconfig applies here; don't add root = true, just layer the rules on top.
                string content = CreatedHeader + " Adds code quality rules on top of " + inheritedFrom[0] + "\r\n" + block;
                File.WriteAllText(path, content, Utf8NoBom);
                log.Add("Created .editorconfig with the rules block" + skippedNote + "; your parent .editorconfig still applies.");
                return;
            }

            File.WriteAllText(path, TemplateProvider.EditorConfig + block, Utf8NoBom);
            log.Add("Created .editorconfig (formatting basics + rules block).");
        }

        public static void Remove(string solutionDirectory, List<string> log)
        {
            string path = Path.Combine(solutionDirectory, SolutionConfigurator.EditorConfigFileName);
            if (!File.Exists(path))
            {
                return;
            }

            string original = File.ReadAllText(path);
            string text = Block.Replace(original, "\r\n");
            if (text == original)
            {
                log.Add("Left .editorconfig unchanged (it has no Code Quality Pack rules block).");
                return;
            }

            bool createdByUs = original.StartsWith(CreatedHeader, StringComparison.Ordinal);
            bool nothingElse = text
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .All(l => l.Length == 0 || l.StartsWith("#", StringComparison.Ordinal) || l.StartsWith(";", StringComparison.Ordinal));

            if (createdByUs && nothingElse)
            {
                File.Delete(path);
                log.Add("Deleted .editorconfig (it only contained the Code Quality Pack rules).");
                return;
            }

            string backup = path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(path, backup);
            File.WriteAllText(path, text.TrimEnd('\r', '\n') + "\r\n", Utf8NoBom);
            log.Add("Removed the rules block from .editorconfig; the rest of the file is kept (backup: " + Path.GetFileName(backup) + ").");
        }

        public static bool IsPresent(string solutionDirectory)
        {
            string path = Path.Combine(solutionDirectory, SolutionConfigurator.EditorConfigFileName);
            return File.Exists(path) && File.ReadAllText(path).IndexOf(BeginMarker, StringComparison.Ordinal) >= 0;
        }

        /// <summary>Builds the marked block, leaving out rules whose key is already defined.</summary>
        internal static string BuildBlock(string template, ISet<string> definedKeys, out int skipped)
        {
            skipped = 0;
            var body = new StringBuilder();
            var pendingComments = new List<string>();

            foreach (string raw in template.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimEnd();

                if (line.Length == 0)
                {
                    // Section headings (comments followed by a blank line) are always kept.
                    foreach (string c in pendingComments)
                    {
                        body.Append(c).Append("\r\n");
                    }

                    pendingComments.Clear();
                    body.Append("\r\n");
                    continue;
                }

                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    pendingComments.Add(line);
                    continue;
                }

                Match key = KeyLine.Match(line);
                if (key.Success && definedKeys.Contains(key.Groups[1].Value.ToLowerInvariant()))
                {
                    skipped++;
                    pendingComments.Clear();
                    continue;
                }

                foreach (string c in pendingComments)
                {
                    body.Append(c).Append("\r\n");
                }

                pendingComments.Clear();
                body.Append(line).Append("\r\n");
            }

            string bodyText = body.ToString().TrimEnd('\r', '\n') + "\r\n";

            return "\r\n"
                + BeginMarker + " (Tools > .NET Code Quality > Remove deletes this block; edit values freely)\r\n"
                + "[*.cs]\r\n"
                + bodyText
                + EndMarker + "\r\n";
        }

        /// <summary>Keys set in the solution's .editorconfig and its parents, up to the first root = true.</summary>
        private static HashSet<string> CollectDefinedKeys(string solutionDirectory, out List<string> parentFiles)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            parentFiles = new List<string>();

            // DirectoryInfo.Parent handles drive roots correctly ("D:\" rather than "D:").
            DirectoryInfo directory = new DirectoryInfo(Path.GetFullPath(solutionDirectory));
            bool first = true;

            while (directory != null)
            {
                string file = Path.Combine(directory.FullName, SolutionConfigurator.EditorConfigFileName);
                if (File.Exists(file))
                {
                    if (!first)
                    {
                        parentFiles.Add(file);
                    }

                    bool isRoot = false;
                    foreach (string line in File.ReadAllLines(file))
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith(";", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        Match key = KeyLine.Match(trimmed);
                        if (!key.Success)
                        {
                            continue;
                        }

                        string name = key.Groups[1].Value.ToLowerInvariant();
                        if (name == "root")
                        {
                            isRoot |= trimmed.EndsWith("true", StringComparison.OrdinalIgnoreCase);
                            continue;
                        }

                        keys.Add(name);
                    }

                    if (isRoot)
                    {
                        break;
                    }
                }

                first = false;
                directory = directory.Parent;
            }

            return keys;
        }
    }
}
