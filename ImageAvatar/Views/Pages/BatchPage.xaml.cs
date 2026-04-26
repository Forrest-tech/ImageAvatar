using ImageAvatar.ViewModels;
using Wpf.Ui.Controls;

namespace ImageAvatar.Views.Pages;

public partial class BatchPage : Page
{
    public BatchPage(BatchProcessorViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
