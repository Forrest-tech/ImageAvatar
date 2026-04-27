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

    // WPF-UI's TargetPageType auto-navigation does not fire when pages are
    // UserControls resolved via IPageService, so we navigate explicitly here.
    private void OnNavigationSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (RootNavigation.SelectedItem is NavigationViewItem { TargetPageType: Type pageType })
            RootNavigation.Navigate(pageType);
    }
}
