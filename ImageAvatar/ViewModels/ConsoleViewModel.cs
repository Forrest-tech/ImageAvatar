using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageAvatar.Contracts.Services;
using System.Collections.ObjectModel;
using System.Windows;

namespace ImageAvatar.ViewModels;

public partial class ConsoleViewModel : ObservableObject
{
    private readonly ILogService _log;

    public ObservableCollection<string> Entries { get; } = [];

    public ConsoleViewModel(ILogService log)
    {
        _log = log;
        foreach (var e in log.GetEntries())
            Entries.Add(e);
        log.EntryAdded += OnEntryAdded;
    }

    private void OnEntryAdded(object? sender, string entry)
        => Application.Current.Dispatcher.Invoke(() => Entries.Add(entry));

    [RelayCommand]
    private void Clear()
    {
        _log.Clear();
        Entries.Clear();
    }
}
