using ImageAvatar.ViewModels;
using System.Windows.Controls;

namespace ImageAvatar.Views.Pages;

public partial class MattingConfigPage : UserControl
{
    public MattingConfigPage(MattingConfigViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
