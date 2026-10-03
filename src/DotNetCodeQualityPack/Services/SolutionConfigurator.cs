using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DotNetCodeQualityPack.Services
{
    /// <summary>
    /// Writes (and removes) the solution-level files. Never overwrites a file it didn't create:
    /// existing Directory.Build.props/.targets get one import line and an existing .editorconfig gets
    /// one marked rules block, each after a timestamped backup.
    /// </summary>
    internal static class SolutionConfigurator
    {
        public const string PropsFileName = "CodeQuality.props";
        public const string TargetsFileName = "CodeQuality.targets";
        public const string GlobalConfigFileName = "CodeQuality.globalconfig";
        public const string DirectoryBuildPropsFileName = "Directory.Build.props";
        public const string DirectoryBuildTargetsFileName = "Directory.Build.targets";
        public const string EditorConfigFileName = ".editorconfig";

        private const string CreatedMarker = "Created by .NET Code Quality Pack";

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        // <Project ...> or <Project ... />, but not <ProjectReference>
        private static readonly Regex ProjectOpenTag = new Regex(@"<Project\b[^>]*?(/?)>", RegexOptions.IgnoreCase);

        private static readonly Regex OurImportComment = new Regex(
            @"^[ \t]*<!-- \.NET Code Quality Pack:.*?-->[ \t]*\r?\n", RegexOptions.Multiline);

        private static readonly Regex OurImport = new Regex(
            @"^[ \t]*<Import Project=""\$\(MSBuildThisFileDirectory\)CodeQuality\.(props|targets)""[^>]*/>[ \t]*\r?\n",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);

        private static readonly Regex OurCreatedComment = new Regex(
            @"^[ \t]*<!-- " + Regex.Escape(CreatedMarker) + @".*?-->[ \t]*\r?\n", RegexOptions.Multiline);

        private static readonly Regex EmptyProject = new Regex(@"^\s*<Project>\s*</Project>\s*$");

        public static bool IsApplied(string solutionDirectory)
        {
            return File.Exists(Path.Combine(solutionDirectory, PropsFileName))
                && File.Exists(Path.Combine(solutionDirectory, TargetsFileName));
        }

        public static bool UsesCentralPackageManagement(string solutionDirectory)
        {
            string path = Path.Combine(solutionDirectory, "Directory.Packages.props");
            return File.Exists(path)
                && Regex.IsMatch(
                    File.ReadAllText(path),
                    @"<ManagePackageVersionsCentrally>\s*true\s*</ManagePackageVersionsCentrally>",
                    RegexOptions.IgnoreCase);
        }

        public static IReadOnlyList<string> Apply(string solutionDirectory)
        {
            var log = new List<string>();

            WriteIfMissing(solutionDirectory, PropsFileName, TemplateProvider.Props, log,
                "kept your existing copy (delete it and apply again to get the latest defaults)");
            WriteIfMissing(solutionDirectory, TargetsFileName, TemplateProvider.Targets, log,
                "kept your existing copy (delete it and apply again to get the latest defaults)");

            EnsureImport(solutionDirectory, DirectoryBuildPropsFileName, PropsFileName, log);
            EnsureImport(solutionDirectory, DirectoryBuildTargetsFileName, TargetsFileName, log);

            // Rule severities go into .editorconfig, which Visual Studio and the compiler always honor.
            EditorConfigRules.Ensure(solutionDirectory, log);

            return log;
        }

        public static IReadOnlyList<string> Remove(string solutionDirectory)
        {
            var log = new List<string>();

            RemoveImport(solutionDirectory, DirectoryBuildPropsFileName, log);
            RemoveImport(solutionDirectory, DirectoryBuildTargetsFileName, log);

            // GlobalConfigFileName: written by version 1.0.0; still cleaned up here.
            foreach (string fileName in new[] { PropsFileName, TargetsFileName, GlobalConfigFileName })
            {
                string path = Path.Combine(solutionDirectory, fileName);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    log.Add("Deleted " + fileName + ".");
                }
            }

            EditorConfigRules.Remove(solutionDirectory, log);

            return log;
        }

        private static void WriteIfMissing(string directory, string fileName, string content, List<string> log, string keptMessage)
        {
            string path = Path.Combine(directory, fileName);
            if (File.Exists(path))
            {
                log.Add(fileName + ": " + keptMessage + ".");
                return;
            }

            File.WriteAllText(path, content, Utf8NoBom);
            log.Add("Created " + fileName + ".");
        }

        private static string ImportBlock(string importedFileName)
        {
            return
                "  <!-- .NET Code Quality Pack: shared analyzers and rules. Use Tools > .NET Code Quality > Remove to take this out. -->\r\n" +
                "  <Import Project=\"$(MSBuildThisFileDirectory)" + importedFileName + "\" " +
                "Condition=\"Exists('$(MSBuildThisFileDirectory)" + importedFileName + "')\" />\r\n";
        }

        internal static void EnsureImport(string directory, string hostFileName, string importedFileName, List<string> log)
        {
            string path = Path.Combine(directory, hostFileName);

            if (!File.Exists(path))
            {
                string created =
                    "<Project>\r\n" +
                    "  <!-- " + CreatedMarker + ". Add your own solution-wide build settings below. -->\r\n" +
                    ImportBlock(importedFileName) +
                    "</Project>\r\n";
                File.WriteAllText(path, created, Utf8NoBom);
                log.Add("Created " + hostFileName + " (imports " + importedFileName + ").");
                return;
            }

            string text = File.ReadAllText(path);
            if (text.IndexOf(importedFileName, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                log.Add(hostFileName + " already imports " + importedFileName + ".");
                return;
            }

            string updated = InsertImport(text, importedFileName);
            if (updated == null)
            {
                log.Add(hostFileName + " has no <Project> element, so it was left unchanged. Add this line inside it yourself: "
                    + ImportBlock(importedFileName).Trim());
                return;
            }

            string backup = BackupPath(path);
            File.Copy(path, backup);
            File.WriteAllText(path, updated, Utf8NoBom);
            log.Add("Added an import of " + importedFileName + " to your existing " + hostFileName
                + " (backup: " + Path.GetFileName(backup) + ").");
        }

        /// <summary>Inserts the import as the first child of &lt;Project&gt;. Returns null when there is no Project element.</summary>
        internal static string InsertImport(string text, string importedFileName)
        {
            Match match = ProjectOpenTag.Match(text);
            if (!match.Success)
            {
                return null;
            }

            string block = ImportBlock(importedFileName);

            if (match.Groups[1].Value == "/")
            {
                // <Project /> -> <Project> ... </Project>
                string openTag = match.Value.Substring(0, match.Value.Length - 2).TrimEnd() + ">";
                return text.Substring(0, match.Index) + openTag + "\r\n" + block + "</Project>"
                    + text.Substring(match.Index + match.Length);
            }

            int insertAt = match.Index + match.Length;
            if (insertAt + 1 < text.Length && text[insertAt] == '\r' && text[insertAt + 1] == '\n')
            {
                insertAt += 2;
            }
            else if (insertAt < text.Length && text[insertAt] == '\n')
            {
                insertAt += 1;
            }
            else
            {
                block = "\r\n" + block;
            }

            return text.Substring(0, insertAt) + block + text.Substring(insertAt);
        }

        private static void RemoveImport(string directory, string hostFileName, List<string> log)
        {
            string path = Path.Combine(directory, hostFileName);
            if (!File.Exists(path))
            {
                return;
            }

            string original = File.ReadAllText(path);
            string text = OurImport.Replace(OurImportComment.Replace(original, string.Empty), string.Empty);
            if (text == original)
            {
                return;
            }

            bool createdByUs = original.IndexOf(CreatedMarker, StringComparison.Ordinal) >= 0;
            if (createdByUs && EmptyProject.IsMatch(OurCreatedComment.Replace(text, string.Empty)))
            {
                File.Delete(path);
                log.Add("Deleted " + hostFileName + " (it only contained the Code Quality Pack import).");
                return;
            }

            string backup = BackupPath(path);
            File.Copy(path, backup);
            File.WriteAllText(path, text, Utf8NoBom);
            log.Add("Removed the Code Quality Pack import from " + hostFileName + " (backup: " + Path.GetFileName(backup) + ").");
        }

        private static string BackupPath(string path)
        {
            return path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        }
    }
}
