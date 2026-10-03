using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DotNetCodeQualityPack.Services
{
    internal sealed class SolutionSnapshot
    {
        public SolutionSnapshot(string solutionPath, IReadOnlyList<ProjectScanResult> projects)
        {
            SolutionPath = solutionPath;
            SolutionDirectory = Path.GetDirectoryName(solutionPath);
            Projects = projects;
        }

        public string SolutionPath { get; }

        public string SolutionDirectory { get; }

        public IReadOnlyList<ProjectScanResult> Projects { get; }

        public int CSharpProjectCount => Projects.Count(p => p.IsCSharp);

        public int ReadyProjectCount => Projects.Count(p => p.WillReceiveAnalyzers);
    }

    /// <summary>Builds the plain-text report shown in the Output window.</summary>
    internal static class ReportBuilder
    {
        public static string Build(SolutionSnapshot snapshot)
        {
            var sb = new StringBuilder();
            string dir = snapshot.SolutionDirectory;

            sb.AppendLine(".NET Code Quality Pack - solution check");
            sb.AppendLine("Solution: " + snapshot.SolutionPath);
            sb.AppendLine("Status:   " + (SolutionConfigurator.IsApplied(dir)
                ? "standards applied (CodeQuality.props/.targets found at the solution root)"
                : "not applied yet (use Tools > .NET Code Quality > Apply Code Quality Standards)"));
            sb.AppendLine("Rules:    " + (EditorConfigRules.IsPresent(dir)
                ? "rules block present in .editorconfig"
                : "no rules block in .editorconfig yet (Apply adds it; safe to run again)"));

            if (SolutionConfigurator.UsesCentralPackageManagement(dir))
            {
                sb.AppendLine("Note:     Central Package Management detected; analyzer versions are applied with VersionOverride.");
            }

            sb.AppendLine();

            foreach (ProjectScanResult project in snapshot.Projects)
            {
                sb.AppendLine(Describe(project, dir));
            }

            sb.AppendLine();
            sb.AppendLine(string.Format(
                "{0} of {1} C# project(s) will get the analyzers.",
                snapshot.ReadyProjectCount,
                snapshot.CSharpProjectCount));

            if (snapshot.ReadyProjectCount < snapshot.CSharpProjectCount)
            {
                sb.AppendLine("Fix the [ACTION] items above, then run Check Solution again.");
            }

            return sb.ToString();
        }

        private static string Describe(ProjectScanResult p, string solutionDirectory)
        {
            string framework = p.FrameworkFamily + ": " + p.TargetFrameworks;

            switch (p.Kind)
            {
                case ProjectKind.NotCSharp:
                    return "[SKIP]   " + p.Name + " - not a C# project; only .csproj projects are configured.";

                case ProjectKind.Unreadable:
                    return "[SKIP]   " + p.Name + " - the project file couldn't be read.";

                case ProjectKind.LegacyPackagesConfig:
                    return "[ACTION] " + p.Name + " (" + framework + ") - uses packages.config, so analyzers are skipped for it. "
                        + "In Solution Explorer right-click packages.config > 'Migrate packages.config to PackageReference...', then check again.";
            }

            if (!p.IsUnderSolutionFolder)
            {
                return "[ACTION] " + p.Name + " (" + framework + ") - lives outside the solution folder, "
                    + "so the solution-level Directory.Build files don't reach it.";
            }

            if (p.BlockingFiles.Count > 0)
            {
                IEnumerable<string> fixes = p.BlockingFiles.Select(f =>
                    PathHelper.Relative(f, solutionDirectory)
                    + " -> add inside <Project>: <Import Project=\"$([MSBuild]::GetPathOfFileAbove('"
                    + Path.GetFileName(f) + "', '$(MSBuildThisFileDirectory)../'))\" />");

                return "[ACTION] " + p.Name + " (" + framework + ") - a nested file hides the solution-level one: "
                    + string.Join("; ", fixes);
            }

            string style = p.Kind == ProjectKind.SdkStyle ? "SDK-style" : "classic project, PackageReference";
            return "[OK]     " + p.Name + " (" + style + "; " + framework + ")";
        }
    }
}
