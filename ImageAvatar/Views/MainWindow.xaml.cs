using ImageAvatar.ViewModels;
using ImageAvatar.Views.Pages;
using System.Windows;
using Wpf.Ui;
using Wpf.Ui.Appearance;

namespace ImageAvatar.Views;

public partial class MainWindow : Window
{
    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationService  navigationService,
        IPageService        pageService)
    {
        DataContext = viewModel;
        InitializeComponent();

        ApplicationThemeManager.Apply(ApplicationTheme.Dark);

        RootNavigation.SetPageService(pageService);
        navigationService.SetNavigationControl(RootNavigation);

        Loaded += (_, _) => navigationService.Navigate(typeof(DashboardPage));
    }
}
