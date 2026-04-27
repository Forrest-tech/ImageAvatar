using ImageAvatar.ViewModels;
using System.Windows.Controls;

namespace ImageAvatar.Views.Pages;

public partial class BatchPage : UserControl
{
    public BatchPage(BatchProcessorViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
