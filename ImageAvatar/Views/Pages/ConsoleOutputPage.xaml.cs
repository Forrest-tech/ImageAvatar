using ImageAvatar.ViewModels;
using System.Collections.Specialized;
using System.Windows.Controls;

namespace ImageAvatar.Views.Pages;

public partial class ConsoleOutputPage : UserControl
{
    public ConsoleOutputPage(ConsoleViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        viewModel.Entries.CollectionChanged += OnEntriesChanged;
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => LogScroll.ScrollToBottom();
}
