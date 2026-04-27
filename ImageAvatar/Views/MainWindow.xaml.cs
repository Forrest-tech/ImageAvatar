using ImageAvatar.ViewModels;
using ImageAvatar.Views.Pages;
using System.Windows;
using Wpf.Ui;
using Wpf.Ui.Appearance;

namespace ImageAvatar.Views;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    public MainWindow(MainWindowViewModel viewModel, IPageService pageService)
    {
        DataContext = viewModel;
        InitializeComponent();
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        RootNavigation.SetPageService(pageService);
    }

    private void OnNavigationViewLoaded(object sender, RoutedEventArgs e)
    {
        RootNavigation.Navigate(typeof(DashboardPage));
    }
}
