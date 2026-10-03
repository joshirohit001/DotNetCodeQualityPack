using System.Windows.Controls;
using System.Windows.Input;

namespace DotNetCodeQualityPack.ToolWindows
{
    public partial class FixerControl : UserControl
    {
        internal FixerControl(FixerViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        private void OnIssueDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is FixerViewModel viewModel && viewModel.GoToCodeCommand.CanExecute(null))
            {
                viewModel.GoToCodeCommand.Execute(null);
            }
        }
    }
}
