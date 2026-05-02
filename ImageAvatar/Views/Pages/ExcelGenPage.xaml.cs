using ImageAvatar.ViewModels;
using System.Windows.Controls;

namespace ImageAvatar.Views.Pages;

public partial class ExcelGenPage : UserControl
{
    public ExcelGenPage(ExcelGenViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
