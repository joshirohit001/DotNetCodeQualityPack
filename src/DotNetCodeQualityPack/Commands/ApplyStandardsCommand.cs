using System;
using System.Collections.Generic;
using Community.VisualStudio.Toolkit;
using DotNetCodeQualityPack.Services;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;
using TaskScheduler = System.Threading.Tasks.TaskScheduler;

namespace DotNetCodeQualityPack.Commands
{
    /// <summary>
    /// Tools > .NET Code Quality > Apply Code Quality Standards: writes CodeQuality.props/.targets
    /// at the solution root, wires them in through Directory.Build.props/.targets, and adds the
    /// rules block to .editorconfig. Safe to run again: it only adds what's missing.
    /// </summary>
    [Command(PackageGuids.DotNetCodeQualityPackString, PackageIds.ApplyStandardsCommand)]
    internal sealed class ApplyStandardsCommand : BaseCommand<ApplyStandardsCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            try
            {
                SolutionSnapshot snapshot = await CodeQualityWorkflow.TryGetSnapshotAsync();
                if (snapshot == null)
                {
                    return;
                }

                OutputWindowPane pane = await CodeQualityWorkflow.ShowReportAsync(snapshot);

                bool proceed = await VS.MessageBox.ShowConfirmAsync(
                    Vsix.Name,
                    string.Format(
                        "Add shared analyzers and rules to:\n{0}\n\n{1} of {2} C# project(s) are ready (details in the Output window).\n\n"
                        + "Existing files are never overwritten. Existing Directory.Build.props/.targets get one import line "
                        + "and an existing .editorconfig gets one rules block, after a backup is made.\n\nContinue?",
                        snapshot.SolutionDirectory,
                        snapshot.ReadyProjectCount,
                        snapshot.CSharpProjectCount));

                if (!proceed)
                {
                    return;
                }

                await TaskScheduler.Default;
                IReadOnlyList<string> actions = SolutionConfigurator.Apply(snapshot.SolutionDirectory);

                await CodeQualityWorkflow.WriteActionsAsync(pane, "Applied:", actions);

                bool reload = await VS.MessageBox.ShowConfirmAsync(
                    Vsix.Name,
                    "Done. Reload the solution now so Visual Studio restores and loads the analyzers?\n\n"
                    + "Unsaved changes are saved first.");

                if (reload)
                {
                    await CodeQualityWorkflow.ReloadSolutionAsync(snapshot.SolutionPath);
                }
                else
                {
                    await VS.StatusBar.ShowMessageAsync(Vsix.Name + ": reload the solution to load the analyzers.");
                }
            }
            catch (Exception ex)
            {
                await CodeQualityWorkflow.ShowErrorAsync(ex);
            }
        }
    }
}
