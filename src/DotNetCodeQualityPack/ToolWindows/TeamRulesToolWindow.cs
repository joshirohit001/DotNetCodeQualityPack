using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.LanguageServices;

namespace DotNetCodeQualityPack.ToolWindows
{
    /// <summary>Tools > .NET Code Quality > Open Team Rules: the Markdown rules file, custom rules and AI review.</summary>
    public sealed class TeamRulesToolWindow : BaseToolWindow<TeamRulesToolWindow>
    {
        public override string GetTitle(int toolWindowId) => "Team Rules";

        public override Type PaneType => typeof(Pane);

        public override async Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            await Package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            IComponentModel componentModel = await VS.GetServiceAsync<SComponentModel, IComponentModel>();
            VisualStudioWorkspace workspace = componentModel.GetService<VisualStudioWorkspace>();

            var viewModel = new TeamRulesViewModel(workspace);
            VS.Events.SolutionEvents.OnAfterOpenSolution += _ => viewModel.Initialize();
            VS.Events.SolutionEvents.OnAfterCloseSolution += viewModel.OnSolutionClosed;
            viewModel.Initialize();

            return new TeamRulesControl(viewModel);
        }

        [Guid("9c3e2a71-4b6d-4f0e-8a5c-2d7b1e6f3a90")]
        internal sealed class Pane : ToolkitToolWindowPane
        {
            public Pane()
            {
                BitmapImageMoniker = KnownMonikers.StatusOK;
            }
        }
    }
}
