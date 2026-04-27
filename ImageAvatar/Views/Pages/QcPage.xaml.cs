using ImageAvatar.ViewModels;
using System.Windows.Controls;

namespace ImageAvatar.Views.Pages;

public partial class QcPage : UserControl
{
    public QcPage(QcViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
