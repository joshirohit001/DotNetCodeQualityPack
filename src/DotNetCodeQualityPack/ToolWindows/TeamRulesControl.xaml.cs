using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DotNetCodeQualityPack.CodeCompass;

namespace DotNetCodeQualityPack.ToolWindows
{
    public partial class TeamRulesControl : UserControl
    {
        internal TeamRulesControl(TeamRulesViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        /// <summary>The same AI provider settings as Code Compass (OpenAI, Anthropic, Ollama).</summary>
        private void OnConfigureProviders(object sender, RoutedEventArgs e)
        {
            if (AiProvidersDialog.Show() && DataContext is TeamRulesViewModel viewModel)
            {
                viewModel.OnProvidersChanged();
            }
        }

        private void OnRuleDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is TeamRulesViewModel viewModel && sender is ListBox list && list.SelectedItem is RuleRowViewModel row)
            {
                viewModel.OpenRuleRow(row);
            }
        }

        private void OnFindingDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is TeamRulesViewModel viewModel && viewModel.GoToCodeCommand.CanExecute(null))
            {
                viewModel.GoToCodeCommand.Execute(null);
            }
        }
    }
}
