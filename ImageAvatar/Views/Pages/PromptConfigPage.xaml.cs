using ImageAvatar.ViewModels;
using System.Windows.Controls;

namespace ImageAvatar.Views.Pages;

public partial class PromptConfigPage : UserControl
{
    public PromptConfigPage(PromptConfigViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
