using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Imaging;

namespace DotNetCodeQualityPack.CodeCompass
{
    /// <summary>Tools > .NET Code Quality > Open Code Compass: AI chat and code review with all configured models.</summary>
    public sealed class CodeCompassToolWindow : BaseToolWindow<CodeCompassToolWindow>
    {
        /// <summary>The window's content once it has been created (null before the first open).</summary>
        internal static CodeCompassControl Control { get; private set; }

        public override string GetTitle(int toolWindowId) => "Code Compass";

        public override Type PaneType => typeof(Pane);

        public override async Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            await Package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var control = new CodeCompassControl();
            Control = control;

            // Chats and rules follow the open solution (or folder).
            VS.Events.SolutionEvents.OnAfterOpenSolution += _ => control.OnSolutionChanged();
            VS.Events.SolutionEvents.OnAfterCloseSolution += control.OnSolutionChanged;

            return control;
        }

        [Guid("4f1d2c8e-7a3b-4e5f-9c61-0b8d2e7a5c34")]
        internal sealed class Pane : ToolkitToolWindowPane
        {
            public Pane()
            {
                BitmapImageMoniker = KnownMonikers.StatusInformation;
            }
        }
    }
}
