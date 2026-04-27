using ImageAvatar.ViewModels;
using System.Windows.Controls;

namespace ImageAvatar.Views.Pages;

public partial class SettingsPage : UserControl
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
