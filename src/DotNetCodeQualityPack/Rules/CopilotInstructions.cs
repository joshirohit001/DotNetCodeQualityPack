using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DotNetCodeQualityPack.Services;

namespace DotNetCodeQualityPack.Rules
{
    /// <summary>One bullet from a Copilot instruction file.</summary>
    internal sealed class InstructionRule
    {
        /// <summary>For example "dotnet §3.12".</summary>
        public string Id { get; set; }

        public string Section { get; set; }

        public string Text { get; set; }

        /// <summary>False for process guidance (scope, deployment, versioning, …) that can't be seen in a single file.</summary>
        public bool Reviewable { get; set; }

        public int SourceLine { get; set; }
    }

    /// <summary>A .github/instructions/*.instructions.md file: applyTo globs + bullet rules.</summary>
    internal sealed class InstructionFile
    {
        private static readonly Regex SectionNumber = new Regex(@"^(\d+(?:\.\d+)*)\.?\s+(.*)$", RegexOptions.CultureInvariant);

        // Sections that describe process rather than code a reviewer can see in one file.
        private static readonly string[] ProcessSections =
        {
            "before", "inspection", "workflow", "review", "documentation", "command",
            "coverage", "pipeline", "dependencies", "supply chain", "packaging", "source control", "principles",
            "versioning", "deploy", "migration", "adoption", "balance",
        };

        // Only when they are the whole heading ("1. Scope"), not part of it ("Dependency Injection and Scope").
        private static readonly string[] ProcessHeadings = { "scope", "purpose", "overview", "introduction" };

        public string FilePath { get; private set; }

        /// <summary>File name without ".instructions.md", e.g. "dotnet".</summary>
        public string Name { get; private set; }

        public string Description { get; private set; }

        public List<string> ApplyTo { get; } = new List<string>();

        public List<InstructionRule> Rules { get; } = new List<InstructionRule>();

        public int ReviewableCount => Rules.Count(r => r.Reviewable);

        public bool AppliesTo(string relativePath)
        {
            return ApplyTo.Any(g => Glob.IsMatch(g, relativePath));
        }

        public static InstructionFile Parse(string text, string filePath)
        {
            string fileName = Path.GetFileName(filePath);
            var file = new InstructionFile
            {
                FilePath = filePath,
                Name = fileName.EndsWith(".instructions.md", StringComparison.OrdinalIgnoreCase)
                    ? fileName.Substring(0, fileName.Length - ".instructions.md".Length)
                    : Path.GetFileNameWithoutExtension(fileName),
            };

            string[] lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int i = 0;

            // YAML frontmatter: applyTo, description.
            if (lines.Length > 0 && lines[0].Trim() == "---")
            {
                for (i = 1; i < lines.Length && lines[i].Trim() != "---"; i++)
                {
                    int colon = lines[i].IndexOf(':');
                    if (colon <= 0)
                    {
                        continue;
                    }

                    string key = lines[i].Substring(0, colon).Trim();
                    string value = Unquote(lines[i].Substring(colon + 1).Trim());

                    if (key.Equals("applyTo", StringComparison.OrdinalIgnoreCase))
                    {
                        file.ApplyTo.AddRange(value.Split(',').Select(g => Unquote(g.Trim())).Where(g => g.Length > 0));
                    }
                    else if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
                    {
                        file.Description = value;
                    }
                }

                i++;
            }

            string section = string.Empty;
            string sectionNumber = "0";
            int sectionOrdinal = 0;
            int bulletIndex = 0;
            bool reviewable = true;
            string leadIn = null;
            bool inFence = false;
            StringBuilder bullet = null;
            int bulletLine = 0;

            void Flush()
            {
                if (bullet == null)
                {
                    return;
                }

                string body = Clean(bullet.ToString());
                bullet = null;
                if (body.Length == 0)
                {
                    return;
                }

                bulletIndex++;
                file.Rules.Add(new InstructionRule
                {
                    Id = file.Name + " §" + sectionNumber + "." + bulletIndex,
                    Section = section,
                    Text = leadIn != null ? leadIn + ": " + body : body,
                    Reviewable = reviewable && file.ApplyTo.Count > 0,
                    SourceLine = bulletLine,
                });
            }

            for (; i < lines.Length; i++)
            {
                string raw = lines[i];
                string line = raw.Trim();

                if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
                {
                    Flush();
                    inFence = !inFence;
                    continue;
                }

                if (inFence)
                {
                    continue;
                }

                if (line.Length == 0)
                {
                    Flush();
                    continue;
                }

                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    Flush();
                    int level = line.TakeWhile(c => c == '#').Count();
                    string heading = line.Substring(level).Trim();

                    if (level == 2)
                    {
                        sectionOrdinal++;
                        Match m = SectionNumber.Match(heading);
                        sectionNumber = m.Success ? m.Groups[1].Value : sectionOrdinal.ToString();
                        section = heading;
                        bulletIndex = 0;
                        leadIn = null;
                        reviewable = !IsProcessSection(m.Success ? m.Groups[2].Value : heading);
                    }
                    else if (level >= 3)
                    {
                        leadIn = ShortLeadIn(heading);
                    }

                    continue;
                }

                int indent = raw.Length - raw.TrimStart(' ', '\t').Length;
                bool isBullet = line.Length > 2 && (line[0] == '-' || line[0] == '*' || line[0] == '+') && line[1] == ' ';

                if (isBullet && indent <= 1)
                {
                    Flush();
                    bullet = new StringBuilder(line.Substring(2).Trim());
                    bulletLine = i + 1;
                }
                else if (isBullet && bullet != null)
                {
                    bullet.Append("; ").Append(line.Substring(2).Trim()); // nested bullet
                }
                else if (bullet != null && indent >= 2)
                {
                    bullet.Append(' ').Append(line); // continuation of the bullet
                }
                else
                {
                    // A paragraph: "Prefer:" / "Avoid:" introduce the bullets that follow.
                    Flush();
                    leadIn = line.EndsWith(":", StringComparison.Ordinal) ? ShortLeadIn(line.TrimEnd(':')) : null;
                }
            }

            Flush();
            return file;
        }

        private static bool IsProcessSection(string title)
        {
            string t = title.Trim().ToLowerInvariant();
            return ProcessHeadings.Contains(t) || ProcessSections.Any(t.Contains);
        }

        private static string ShortLeadIn(string text)
        {
            string t = Clean(text);
            return t.Length > 0 && t.Split(' ').Length <= 4 ? t : null;
        }

        private static string Clean(string text)
        {
            string t = text.Replace("**", string.Empty).Replace("__", string.Empty);
            return Regex.Replace(t, @"\s+", " ").Trim();
        }

        private static string Unquote(string value)
        {
            string v = value.Trim();
            if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[v.Length - 1] == v[0])
            {
                v = v.Substring(1, v.Length - 2);
            }

            return v;
        }
    }

    /// <summary>
    /// The repository's GitHub Copilot instruction files (.github/instructions/*.instructions.md).
    /// Like Copilot, a file's rules apply only to files that match its applyTo globs.
    /// </summary>
    internal sealed class InstructionSet
    {
        private InstructionSet(string rootDirectory)
        {
            RootDirectory = rootDirectory;
        }

        /// <summary>The folder that contains .github.</summary>
        public string RootDirectory { get; }

        public string InstructionsDirectory => Path.Combine(RootDirectory, ".github", "instructions");

        public List<InstructionFile> Files { get; } = new List<InstructionFile>();

        public List<RulesProblem> Problems { get; } = new List<RulesProblem>();

        public int ReviewableCount => Files.Sum(f => f.ReviewableCount);

        public int RuleCount => Files.Sum(f => f.Rules.Count);

        /// <summary>Looks for .github/instructions from the solution folder up to the repository root.</summary>
        public static InstructionSet Discover(string startDirectory)
        {
            if (string.IsNullOrEmpty(startDirectory))
            {
                return null;
            }

            DirectoryInfo directory = new DirectoryInfo(startDirectory);
            for (int depth = 0; directory != null && depth < 8; depth++, directory = directory.Parent)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, ".github", "instructions")))
                {
                    return Load(directory.FullName);
                }

                bool isRepositoryRoot = Directory.Exists(Path.Combine(directory.FullName, ".git"))
                    || File.Exists(Path.Combine(directory.FullName, ".git"));
                if (isRepositoryRoot)
                {
                    break;
                }
            }

            return null;
        }

        public static InstructionSet Load(string rootDirectory)
        {
            var set = new InstructionSet(rootDirectory);

            foreach (string path in Directory.GetFiles(set.InstructionsDirectory, "*.instructions.md").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    InstructionFile file = InstructionFile.Parse(File.ReadAllText(path), path);
                    if (file.ApplyTo.Count == 0)
                    {
                        set.Problems.Add(new RulesProblem(0, Path.GetFileName(path) + " has no applyTo frontmatter, so Copilot never attaches it and AI review skips it."));
                    }

                    set.Files.Add(file);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    set.Problems.Add(new RulesProblem(0, "Couldn't read " + Path.GetFileName(path) + ": " + ex.Message));
                }
            }

            return set;
        }

        public string RelativePath(string filePath)
        {
            return PathHelper.Relative(filePath, RootDirectory).Replace('\\', '/');
        }

        public List<InstructionFile> FilesFor(string filePath)
        {
            string relative = RelativePath(filePath);
            return Files.Where(f => f.AppliesTo(relative)).ToList();
        }

        /// <summary>The reviewable rules whose instruction file applies to <paramref name="filePath"/>.</summary>
        public List<InstructionRule> RulesFor(string filePath)
        {
            return FilesFor(filePath).SelectMany(f => f.Rules.Where(r => r.Reviewable)).ToList();
        }
    }
}
