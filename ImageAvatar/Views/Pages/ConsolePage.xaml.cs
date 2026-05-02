using ImageAvatar.Models;
using ImageAvatar.ViewModels;
using System.Windows.Controls;

namespace ImageAvatar.Views.Pages;

public partial class ConsolePage : UserControl
{
    public ConsolePage(ConsoleViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        // Auto-scroll to bottom when new entries arrive
        viewModel.Entries.CollectionChanged += (_, _) =>
        {
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        };
    }
}
