using ImageAvatar.ViewModels;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ImageAvatar.Views.Pages;

public partial class DashboardPage : Wpf.Ui.Controls.Page
{
    public DashboardPage(DashboardViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    // ── Drag-and-drop: accept a folder and set it as the workspace root ────

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = ContainsFolder(e) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (ContainsFolder(e))
            DropHintBorder.Visibility = Visibility.Visible;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        DropHintBorder.Visibility = Visibility.Collapsed;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropHintBorder.Visibility = Visibility.Collapsed;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var paths  = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        var folder = paths.FirstOrDefault(Directory.Exists);
        if (folder is null) return;

        var vm = (DashboardViewModel)DataContext;
        vm.RootPath = folder;
        vm.ApplyRootPathCommand.Execute(null);
    }

    private static bool ContainsFolder(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) &&
        ((string[])e.Data.GetData(DataFormats.FileDrop)!).Any(Directory.Exists);
}
