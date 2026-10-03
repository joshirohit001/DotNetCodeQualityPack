using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace DotNetCodeQualityPack.Services
{
    internal enum ProjectKind
    {
        /// <summary>SDK-style project: .NET 5+, .NET Core, .NET Standard, or SDK-style .NET Framework.</summary>
        SdkStyle,

        /// <summary>Classic (non-SDK) .NET Framework project without packages.config.</summary>
        LegacyPackageReference,

        /// <summary>Project that still uses packages.config; NuGet can't add PackageReference items to it.</summary>
        LegacyPackagesConfig,

        NotCSharp,

        Unreadable,
    }

    internal sealed class ProjectScanResult
    {
        public string Name { get; set; }

        public string FullPath { get; set; }

        public ProjectKind Kind { get; set; }

        public string TargetFrameworks { get; set; } = "unknown";

        public string FrameworkFamily { get; set; } = "unknown";

        public bool IsUnderSolutionFolder { get; set; }

        /// <summary>
        /// Nested Directory.Build.props/.targets files that stop MSBuild from reaching the
        /// solution-level ones (MSBuild imports only the nearest file it finds walking up).
        /// </summary>
        public List<string> BlockingFiles { get; } = new List<string>();

        public bool IsCSharp => Kind == ProjectKind.SdkStyle
            || Kind == ProjectKind.LegacyPackageReference
            || Kind == ProjectKind.LegacyPackagesConfig;

        public bool WillReceiveAnalyzers =>
            (Kind == ProjectKind.SdkStyle || Kind == ProjectKind.LegacyPackageReference)
            && IsUnderSolutionFolder
            && BlockingFiles.Count == 0;
    }

    internal static class ProjectScanner
    {
        private static readonly string[] SolutionLevelFiles =
        {
            SolutionConfigurator.DirectoryBuildPropsFileName,
            SolutionConfigurator.DirectoryBuildTargetsFileName,
        };

        public static ProjectScanResult Scan(string projectPath, string solutionDirectory)
        {
            var result = new ProjectScanResult
            {
                Name = Path.GetFileNameWithoutExtension(projectPath),
                FullPath = projectPath,
                IsUnderSolutionFolder = PathHelper.IsUnder(projectPath, solutionDirectory),
            };

            if (!string.Equals(Path.GetExtension(projectPath), ".csproj", StringComparison.OrdinalIgnoreCase))
            {
                result.Kind = ProjectKind.NotCSharp;
                return result;
            }

            XDocument document;
            try
            {
                document = XDocument.Load(projectPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is XmlException)
            {
                result.Kind = ProjectKind.Unreadable;
                return result;
            }

            string projectDirectory = Path.GetDirectoryName(projectPath);
            bool isSdkStyle = IsSdkStyle(document);

            result.TargetFrameworks = ReadTargetFrameworks(document);
            result.FrameworkFamily = DescribeFrameworkFamily(result.TargetFrameworks);

            if (File.Exists(Path.Combine(projectDirectory, "packages.config")))
            {
                result.Kind = ProjectKind.LegacyPackagesConfig;
            }
            else
            {
                result.Kind = isSdkStyle ? ProjectKind.SdkStyle : ProjectKind.LegacyPackageReference;
            }

            if (result.IsUnderSolutionFolder)
            {
                result.BlockingFiles.AddRange(FindBlockingFiles(projectDirectory, solutionDirectory));
            }

            return result;
        }

        internal static bool IsSdkStyle(XDocument document)
        {
            XElement root = document.Root;
            if (root == null)
            {
                return false;
            }

            if (root.Attribute("Sdk") != null)
            {
                return true;
            }

            return root.Elements().Any(e =>
                e.Name.LocalName == "Sdk"
                || (e.Name.LocalName == "Import" && e.Attribute("Sdk") != null));
        }

        internal static string ReadTargetFrameworks(XDocument document)
        {
            XElement element = FindFirst(document, "TargetFrameworks") ?? FindFirst(document, "TargetFramework");
            if (element != null && !string.IsNullOrWhiteSpace(element.Value))
            {
                return element.Value.Trim();
            }

            // Classic projects: <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion> -> net472
            XElement version = FindFirst(document, "TargetFrameworkVersion");
            if (version != null && !string.IsNullOrWhiteSpace(version.Value))
            {
                string digits = version.Value.Trim().TrimStart('v', 'V').Replace(".", string.Empty);
                return "net" + digits;
            }

            return "unknown";
        }

        internal static string DescribeFrameworkFamily(string targetFrameworks)
        {
            if (string.IsNullOrWhiteSpace(targetFrameworks) || targetFrameworks == "unknown")
            {
                return "unknown";
            }

            if (targetFrameworks.Contains("$("))
            {
                return "set by an MSBuild property";
            }

            IEnumerable<string> families = targetFrameworks
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => ClassifyTfm(t.Trim().ToLowerInvariant()))
                .Distinct();

            return string.Join(" + ", families);
        }

        private static string ClassifyTfm(string tfm)
        {
            if (tfm.StartsWith("netstandard", StringComparison.Ordinal))
            {
                return ".NET Standard";
            }

            if (tfm.StartsWith("netcoreapp", StringComparison.Ordinal))
            {
                return ".NET Core";
            }

            if (tfm.StartsWith("net", StringComparison.Ordinal) && tfm.Length > 3 && char.IsDigit(tfm[3]))
            {
                // net5.0, net8.0-windows -> modern .NET; net48, net472, net461 -> .NET Framework
                return tfm.Contains(".") ? ".NET" : ".NET Framework";
            }

            return tfm;
        }

        private static XElement FindFirst(XDocument document, string localName)
        {
            return document.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);
        }

        /// <summary>
        /// MSBuild imports only the nearest Directory.Build.props (and .targets) found walking up from the project.
        /// A nested file that doesn't chain to its parent hides the solution-level one.
        /// </summary>
        private static IEnumerable<string> FindBlockingFiles(string projectDirectory, string solutionDirectory)
        {
            foreach (string fileName in SolutionLevelFiles)
            {
                string directory = projectDirectory;
                while (!string.IsNullOrEmpty(directory)
                    && PathHelper.IsUnder(directory, solutionDirectory)
                    && !PathHelper.SameDirectory(directory, solutionDirectory))
                {
                    string candidate = Path.Combine(directory, fileName);
                    if (File.Exists(candidate))
                    {
                        if (!ChainsToParent(candidate))
                        {
                            yield return candidate;
                        }

                        break;
                    }

                    directory = Path.GetDirectoryName(directory);
                }
            }
        }

        private static bool ChainsToParent(string file)
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }

            return text.IndexOf("GetPathOfFileAbove", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf(SolutionConfigurator.PropsFileName, StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf(SolutionConfigurator.TargetsFileName, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
