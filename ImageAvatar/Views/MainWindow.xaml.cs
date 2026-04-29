using ImageAvatar.ViewModels;
using ImageAvatar.Views.Pages;
using System.Windows;
using System.Windows.Controls;
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
        ShowPage(typeof(DashboardPage));
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: Type pageType })
            ShowPage(pageType);
    }

    private void ShowPage(Type pageType)
        => PageContent.Content = App.Services.GetService(pageType);
}
