using ImageAvatar.ViewModels;
using System.Windows.Controls;

namespace ImageAvatar.Views.Pages;

public partial class GenConfigPage : UserControl
{
    public GenConfigPage(GenConfigViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
