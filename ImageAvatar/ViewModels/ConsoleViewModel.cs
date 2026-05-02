using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageAvatar.Contracts.Services;
using ImageAvatar.Models;
using System.Collections.ObjectModel;

namespace ImageAvatar.ViewModels;

public partial class ConsoleViewModel : ObservableObject
{
    private readonly ILogService _log;

    public ObservableCollection<LogEntry> Entries => _log.Entries;

    public ConsoleViewModel(ILogService log) => _log = log;

    [RelayCommand]
    private void Clear() => _log.Clear();
}
