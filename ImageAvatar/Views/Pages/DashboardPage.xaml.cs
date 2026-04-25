using ImageAvatar.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ImageAvatar.Views.Pages;

public partial class DashboardPage : Wpf.Ui.Controls.Page
{
    public DashboardPage(DashboardViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
