using ImageAvatar.ViewModels;
using Wpf.Ui.Controls;

namespace ImageAvatar.Views.Pages;

public partial class QcPage : Page
{
    public QcPage(QcViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
