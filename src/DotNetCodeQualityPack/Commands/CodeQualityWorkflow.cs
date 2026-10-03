using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using DotNetCodeQualityPack.Services;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace DotNetCodeQualityPack.Commands
{
    /// <summary>Shared steps used by the Check, Apply and Remove commands.</summary>
    internal static class CodeQualityWorkflow
    {
        private static OutputWindowPane _pane;

        public static async Task<OutputWindowPane> GetPaneAsync()
        {
            if (_pane == null)
            {
                _pane = await VS.Windows.CreateOutputWindowPaneAsync(Vsix.Name);
            }

            return _pane;
        }

        /// <summary>Scans the open solution. Shows a message and returns null when no solution is open.</summary>
        public static async Task<SolutionSnapshot> TryGetSnapshotAsync()
        {
            Solution solution = await VS.Solutions.GetCurrentSolutionAsync();
            string solutionPath = solution?.FullPath;

            if (string.IsNullOrEmpty(solutionPath) || !File.Exists(solutionPath))
            {
                await VS.MessageBox.ShowAsync(
                    Vsix.Name,
                    "Open a solution (.sln or .slnx) first.",
                    OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK);
                return null;
            }

            IEnumerable<Project> projects = await VS.Solutions.GetAllProjectsAsync();
            List<string> projectPaths = projects
                .Select(p => p.FullPath)
                .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // File I/O off the UI thread.
            await TaskScheduler.Default;

            string solutionDirectory = Path.GetDirectoryName(solutionPath);
            List<ProjectScanResult> results = projectPaths
                .Select(p => ProjectScanner.Scan(p, solutionDirectory))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new SolutionSnapshot(solutionPath, results);
        }

        public static async Task<OutputWindowPane> ShowReportAsync(SolutionSnapshot snapshot)
        {
            OutputWindowPane pane = await GetPaneAsync();
            await pane.ClearAsync();
            await pane.WriteLineAsync(ReportBuilder.Build(snapshot));
            await pane.ActivateAsync();
            return pane;
        }

        public static async Task WriteActionsAsync(OutputWindowPane pane, string heading, IReadOnlyList<string> actions)
        {
            await pane.WriteLineAsync(heading);
            foreach (string action in actions)
            {
                await pane.WriteLineAsync("  - " + action);
            }

            await pane.WriteLineAsync(string.Empty);
            await pane.ActivateAsync();
        }

        /// <summary>Closes and reopens the solution so MSBuild picks up new Directory.Build files and NuGet restores.</summary>
        public static async Task ReloadSolutionAsync(string solutionPath)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            EnvDTE80.DTE2 dte = await VS.GetServiceAsync<EnvDTE.DTE, EnvDTE80.DTE2>();
            if (dte == null)
            {
                await VS.StatusBar.ShowMessageAsync("Reload the solution to load the analyzers.");
                return;
            }

            dte.Solution.Close(true);
            dte.Solution.Open(solutionPath);
        }

        public static async Task ShowErrorAsync(Exception ex)
        {
            await ex.LogAsync();
            await VS.MessageBox.ShowErrorAsync(Vsix.Name, ex.Message);
        }
    }
}
