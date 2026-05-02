using ImageAvatar.ViewModels;
using System.Windows.Controls;

namespace ImageAvatar.Views.Pages;

public partial class SynthesisConfigPage : UserControl
{
    public SynthesisConfigPage(SynthesisConfigViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
