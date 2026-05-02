using ImageAvatar.ViewModels;
using System.Windows.Controls;

namespace ImageAvatar.Views.Pages;

public partial class GlobalConfigPage : UserControl
{
    public GlobalConfigPage(GlobalConfigViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
