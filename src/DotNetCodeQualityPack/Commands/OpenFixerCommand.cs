using System;
using Community.VisualStudio.Toolkit;
using DotNetCodeQualityPack.ToolWindows;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace DotNetCodeQualityPack.Commands
{
    /// <summary>Tools > .NET Code Quality > Open Code Quality Fixer.</summary>
    [Command(PackageGuids.DotNetCodeQualityPackString, PackageIds.OpenFixerCommand)]
    internal sealed class OpenFixerCommand : BaseCommand<OpenFixerCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            try
            {
                await FixerToolWindow.ShowAsync();
            }
            catch (Exception ex)
            {
                await CodeQualityWorkflow.ShowErrorAsync(ex);
            }
        }
    }
}
