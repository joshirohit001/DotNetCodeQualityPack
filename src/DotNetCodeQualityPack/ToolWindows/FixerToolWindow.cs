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
    /// <summary>View > Other Windows > Code Quality Fixer (docked next to Solution Explorer).</summary>
    public sealed class FixerToolWindow : BaseToolWindow<FixerToolWindow>
    {
        public override string GetTitle(int toolWindowId) => "Code Quality Fixer";

        public override Type PaneType => typeof(Pane);

        public override async Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            await Package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            IComponentModel componentModel = await VS.GetServiceAsync<SComponentModel, IComponentModel>();
            VisualStudioWorkspace workspace = componentModel.GetService<VisualStudioWorkspace>();

            var viewModel = new FixerViewModel(workspace);
            VS.Events.SolutionEvents.OnAfterCloseSolution += viewModel.Reset;

            return new FixerControl(viewModel);
        }

        [Guid("5b0f6c2e-8f5d-4a1e-9f7a-3c2d9e4b7a61")]
        internal sealed class Pane : ToolkitToolWindowPane
        {
            public Pane()
            {
                BitmapImageMoniker = KnownMonikers.StatusWarning;
            }
        }
    }
}
