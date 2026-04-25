using ImageAvatar.ViewModels;

namespace ImageAvatar.Views.Pages;

public partial class SettingsPage : Wpf.Ui.Controls.Page
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
