using ImageAvatar.ViewModels;
using System.Windows.Controls;

namespace ImageAvatar.Views.Pages;

public partial class CropConfigPage : UserControl
{
    public CropConfigPage(CropConfigViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
