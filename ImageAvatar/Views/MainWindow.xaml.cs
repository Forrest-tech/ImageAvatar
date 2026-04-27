using ImageAvatar.ViewModels;
using ImageAvatar.Views.Pages;
using System.Windows;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ImageAvatar.Views;

public partial class MainWindow : FluentWindow
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

    // Fired when the user picks a nav item; navigation itself is handled
    // automatically by TargetPageType + IPageService.
    private void OnNavigationSelectionChanged(object sender, RoutedEventArgs e) { }
}
