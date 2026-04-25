using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageAvatar.Contracts.Services;

namespace ImageAvatar.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;

    [ObservableProperty]
    private string _appTitle = "ImageAvatar";

    public MainWindowViewModel(ILocalizationService localization)
    {
        _localization = localization;
    }
}
