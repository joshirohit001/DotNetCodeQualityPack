using System;
using Community.VisualStudio.Toolkit;
using DotNetCodeQualityPack.ToolWindows;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace DotNetCodeQualityPack.Commands
{
    /// <summary>Tools > .NET Code Quality > Open Team Rules.</summary>
    [Command(PackageGuids.DotNetCodeQualityPackString, PackageIds.OpenTeamRulesCommand)]
    internal sealed class OpenTeamRulesCommand : BaseCommand<OpenTeamRulesCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            try
            {
                await TeamRulesToolWindow.ShowAsync();
            }
            catch (Exception ex)
            {
                await CodeQualityWorkflow.ShowErrorAsync(ex);
            }
        }
    }
}
