using System;
using Community.VisualStudio.Toolkit;
using DotNetCodeQualityPack.Services;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace DotNetCodeQualityPack.Commands
{
    /// <summary>Tools > .NET Code Quality > Check Solution: read-only report, changes nothing.</summary>
    [Command(PackageGuids.DotNetCodeQualityPackString, PackageIds.CheckSolutionCommand)]
    internal sealed class CheckSolutionCommand : BaseCommand<CheckSolutionCommand>
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

                await CodeQualityWorkflow.ShowReportAsync(snapshot);
                await VS.StatusBar.ShowMessageAsync(string.Format(
                    "{0}: {1} of {2} C# projects ready. See the Output window for details.",
                    Vsix.Name,
                    snapshot.ReadyProjectCount,
                    snapshot.CSharpProjectCount));
            }
            catch (Exception ex)
            {
                await CodeQualityWorkflow.ShowErrorAsync(ex);
            }
        }
    }
}
