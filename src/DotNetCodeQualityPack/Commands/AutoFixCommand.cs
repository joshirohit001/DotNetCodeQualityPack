using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Community.VisualStudio.Toolkit;
using DotNetCodeQualityPack.Services;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;
using TaskScheduler = System.Threading.Tasks.TaskScheduler;

namespace DotNetCodeQualityPack.Commands
{
    /// <summary>
    /// Tools > .NET Code Quality > Auto-Fix Warnings: backs up the .cs files, runs dotnet format to apply every
    /// automatic fix (formatting, code style, analyzer code fixes), then reports what changed. Undo restores the backup.
    /// </summary>
    [Command(PackageGuids.DotNetCodeQualityPackString, PackageIds.AutoFixCommand)]
    internal sealed class AutoFixCommand : BaseCommand<AutoFixCommand>
    {
        private static bool _running;

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            if (_running)
            {
                await VS.StatusBar.ShowMessageAsync(Vsix.Name + ": an auto-fix is already running. See the Output window.");
                return;
            }

            try
            {
                _running = true;
                await RunAsync();
            }
            catch (Exception ex)
            {
                await CodeQualityWorkflow.ShowErrorAsync(ex);
            }
            finally
            {
                _running = false;
            }
        }

        private static async Task RunAsync()
        {
            SolutionSnapshot snapshot = await CodeQualityWorkflow.TryGetSnapshotAsync();
            if (snapshot == null)
            {
                return;
            }

            string dotnet = DotnetFormatRunner.FindDotnet();
            if (dotnet == null)
            {
                await VS.MessageBox.ShowErrorAsync(
                    Vsix.Name,
                    "The .NET SDK (dotnet.exe) wasn't found. Install it from https://dotnet.microsoft.com/download, then try again.");
                return;
            }

            bool proceed = await VS.MessageBox.ShowConfirmAsync(
                Vsix.Name,
                "Automatically fix warnings and messages in:\n" + snapshot.SolutionPath + "\n\n"
                + "This will:\n"
                + "  1. Save all open files\n"
                + "  2. Back up every .cs file (outside your solution folder)\n"
                + "  3. Apply every automatic fix: formatting, code style and analyzer fixes\n"
                + "  4. List what changed in the Output window\n\n"
                + "Warnings that need a human decision are left for you.\n"
                + "Undo any time with Tools > .NET Code Quality > Undo Last Auto-Fix.\n\n"
                + "It can take a few minutes on a large solution. Continue?");

            if (!proceed)
            {
                return;
            }

            await VS.Commands.ExecuteAsync("File.SaveAll");

            OutputWindowPane pane = await CodeQualityWorkflow.GetPaneAsync();
            await pane.ClearAsync();
            await pane.WriteLineAsync(".NET Code Quality Pack - auto-fix");
            await pane.WriteLineAsync("Solution: " + snapshot.SolutionPath);
            await pane.ActivateAsync();

            if (snapshot.Projects.Any(p => p.Kind == ProjectKind.LegacyPackageReference || p.Kind == ProjectKind.LegacyPackagesConfig))
            {
                await pane.WriteLineAsync("Note: dotnet format may not be able to load classic .NET Framework projects. "
                    + "For those, use Ctrl+. > Fix all occurrences in: Solution in the editor.");
            }

            await VS.StatusBar.ShowMessageAsync(Vsix.Name + ": backing up source files...");

            await TaskScheduler.Default;
            string backupFolder = SourceBackup.NewBackupFolder(snapshot.SolutionPath);
            Dictionary<string, string> before = SourceBackup.Take(snapshot.SolutionDirectory, backupFolder);
            await pane.WriteLineAsync(string.Format("Backed up {0} .cs file(s).", before.Count));

            await VS.StatusBar.ShowMessageAsync(Vsix.Name + ": fixing... (progress in the Output window)");

            string reportDirectory = Path.Combine(Path.GetTempPath(), "DotNetCodeQualityPack", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(reportDirectory);

            var output = new ConcurrentQueue<string>();
            System.Threading.Tasks.Task<int> run = DotnetFormatRunner.RunAsync(dotnet, snapshot.SolutionPath, reportDirectory, output);

            // Stream dotnet format's output to the Output window in order, in small batches.
            while (!run.IsCompleted)
            {
                await System.Threading.Tasks.Task.WhenAny(run, System.Threading.Tasks.Task.Delay(500));
                await FlushAsync(pane, output);
            }

            int exitCode = await run;
            await FlushAsync(pane, output);

            await TaskScheduler.Default;
            List<string> changed = SourceBackup.Complete(snapshot.SolutionDirectory, backupFolder, before);
            List<KeyValuePair<string, int>> fixesByRule = DotnetFormatRunner.ReadFixesByRule(reportDirectory);
            TryDelete(reportDirectory);

            await pane.WriteLineAsync(BuildSummary(exitCode, changed, fixesByRule));
            await pane.ActivateAsync();

            if (changed.Count == 0)
            {
                await VS.StatusBar.ShowMessageAsync(Vsix.Name + ": no automatic fixes were needed.");
                await VS.MessageBox.ShowAsync(
                    Vsix.Name,
                    exitCode == 0
                        ? "Nothing to fix automatically. The remaining warnings need a human decision."
                        : "dotnet format reported a problem and made no changes. See the Output window for details.");
                return;
            }

            await VS.StatusBar.ShowMessageAsync(string.Format("{0}: {1} file(s) fixed.", Vsix.Name, changed.Count));

            bool rebuild = await VS.MessageBox.ShowConfirmAsync(
                Vsix.Name,
                string.Format(
                    "Fixed {0} file(s) ({1} fix(es)). Details are in the Output window.\n\n"
                    + "Review the changes in View > Git Changes, or undo with Tools > .NET Code Quality > Undo Last Auto-Fix.\n\n"
                    + "Rebuild the solution now to see which warnings are left?",
                    changed.Count,
                    fixesByRule.Sum(p => p.Value)));

            if (rebuild)
            {
                await VS.Build.BuildSolutionAsync(BuildAction.Rebuild);
            }
        }

        private static async Task FlushAsync(OutputWindowPane pane, ConcurrentQueue<string> output)
        {
            if (output.IsEmpty)
            {
                return;
            }

            var batch = new StringBuilder();
            while (output.TryDequeue(out string line))
            {
                batch.AppendLine(line);
            }

            await pane.WriteLineAsync(batch.ToString().TrimEnd('\r', '\n'));
        }

        private static string BuildSummary(int exitCode, List<string> changed, List<KeyValuePair<string, int>> fixesByRule)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("---------------- Summary ----------------");

            if (exitCode != 0)
            {
                sb.AppendLine("dotnet format finished with exit code " + exitCode + ". Check the messages above; "
                    + "a common cause is a project that fails to restore or build.");
            }

            sb.AppendLine(string.Format("Files changed: {0}", changed.Count));

            if (fixesByRule.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Fixes by rule:");
                foreach (KeyValuePair<string, int> rule in fixesByRule)
                {
                    sb.AppendLine(string.Format("  {0,-12} {1}", rule.Key, rule.Value));
                }
            }

            if (changed.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Changed files:");
                foreach (string file in changed.Take(100))
                {
                    sb.AppendLine("  " + file);
                }

                if (changed.Count > 100)
                {
                    sb.AppendLine(string.Format("  ... and {0} more", changed.Count - 100));
                }

                sb.AppendLine();
                sb.AppendLine("Next: rebuild, then work through what's left (usually real issues such as S1854 or CS8600).");
                sb.AppendLine("Undo: Tools > .NET Code Quality > Undo Last Auto-Fix.");
            }

            return sb.ToString();
        }

        private static void TryDelete(string directory)
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
