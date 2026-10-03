using System;
using Community.VisualStudio.Toolkit;
using DotNetCodeQualityPack.CodeCompass;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace DotNetCodeQualityPack.Commands
{
    /// <summary>Tools > .NET Code Quality > Open Code Compass (also View > Other Windows).</summary>
    [Command(PackageGuids.DotNetCodeQualityPackString, PackageIds.OpenCodeCompassCommand)]
    internal sealed class OpenCodeCompassCommand : BaseCommand<OpenCodeCompassCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            try
            {
                await CodeCompassToolWindow.ShowAsync();
            }
            catch (Exception ex)
            {
                await CodeQualityWorkflow.ShowErrorAsync(ex);
            }
        }
    }

    /// <summary>Editor right-click > Review Selection with Code Compass.</summary>
    [Command(PackageGuids.DotNetCodeQualityPackString, PackageIds.ReviewWithCodeCompassCommand)]
    internal sealed class ReviewWithCodeCompassCommand : BaseCommand<ReviewWithCodeCompassCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            try
            {
                await CodeCompassToolWindow.ShowAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                CodeCompassToolWindow.Control?.ReviewSelectionFromCommand();
            }
            catch (Exception ex)
            {
                await CodeQualityWorkflow.ShowErrorAsync(ex);
            }
        }
    }
}
