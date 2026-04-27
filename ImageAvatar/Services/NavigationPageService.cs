using System.Windows;
using Wpf.Ui;

namespace ImageAvatar.Services;

internal sealed class NavigationPageService : IPageService
{
    private readonly IServiceProvider _serviceProvider;

    public NavigationPageService(IServiceProvider serviceProvider)
        => _serviceProvider = serviceProvider;

    public T? GetPage<T>() where T : class
        => (T?)_serviceProvider.GetService(typeof(T));

    public FrameworkElement? GetPage(Type pageType)
        => _serviceProvider.GetService(pageType) as FrameworkElement;
}
