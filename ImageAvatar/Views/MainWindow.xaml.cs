using ImageAvatar.ViewModels;
using ImageAvatar.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using Wpf.Ui;
using Wpf.Ui.Appearance;

namespace ImageAvatar.Views;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
    }

    private void OnNavigationViewLoaded(object sender, RoutedEventArgs e)
    {
        // Navigate to Dashboard on startup
        RootNavigation.Navigate(typeof(DashboardPage));
    }
}
