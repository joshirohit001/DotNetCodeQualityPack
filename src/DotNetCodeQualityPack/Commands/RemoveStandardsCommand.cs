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
    /// <summary>Tools > .NET Code Quality > Remove Code Quality Standards: undoes what Apply added.</summary>
    [Command(PackageGuids.DotNetCodeQualityPackString, PackageIds.RemoveStandardsCommand)]
    internal sealed class RemoveStandardsCommand : BaseCommand<RemoveStandardsCommand>
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

                bool proceed = await VS.MessageBox.ShowConfirmAsync(
                    Vsix.Name,
                    "Remove the shared analyzers and rules from:\n" + snapshot.SolutionDirectory + "\n\n"
                    + "This deletes CodeQuality.props and CodeQuality.targets, takes the import lines out of "
                    + "Directory.Build.props/.targets and the rules block out of .editorconfig (backups are made). "
                    + "Your own .editorconfig settings are kept.\n\nContinue?");

                if (!proceed)
                {
                    return;
                }

                OutputWindowPane pane = await CodeQualityWorkflow.GetPaneAsync();

                await TaskScheduler.Default;
                IReadOnlyList<string> actions = SolutionConfigurator.Remove(snapshot.SolutionDirectory);

                if (actions.Count == 0)
                {
                    actions = new[] { "Nothing to remove: the standards weren't applied to this solution." };
                }

                await CodeQualityWorkflow.WriteActionsAsync(pane, "Removed:", actions);

                bool reload = await VS.MessageBox.ShowConfirmAsync(
                    Vsix.Name,
                    "Done. Reload the solution now?\n\nUnsaved changes are saved first.");

                if (reload)
                {
                    await CodeQualityWorkflow.ReloadSolutionAsync(snapshot.SolutionPath);
                }
            }
            catch (Exception ex)
            {
                await CodeQualityWorkflow.ShowErrorAsync(ex);
            }
        }
    }
}
