using ImageAvatar.ViewModels;
using ImageAvatar.Views.Pages;
using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ImageAvatar.Views;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
    }

    private void OnNavigationViewLoaded(object sender, RoutedEventArgs e)
        => ShowPage(typeof(DashboardPage));

    private void OnNavigationSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (RootNavigation.SelectedItem is NavigationViewItem { Tag: Type pageType })
            ShowPage(pageType);
    }

    private void ShowPage(Type pageType)
    {
        PageContent.Content = App.Services.GetService(pageType);
    }
}
