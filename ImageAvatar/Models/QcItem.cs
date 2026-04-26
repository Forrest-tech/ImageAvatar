using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;

namespace ImageAvatar.Models;

public partial class QcItem : ObservableObject
{
    public string MockupPath   { get; init; } = string.Empty;
    public string PatternPath  { get; init; } = string.Empty;
    public string OriginalPath { get; init; } = string.Empty;

    // Tracks where the file was moved after a decision
    public string OutputPath { get; set; } = string.Empty;

    public string FileName => Path.GetFileNameWithoutExtension(MockupPath);

    [ObservableProperty] private QcStatus _status = QcStatus.Pending;

    public bool IsPending  => Status == QcStatus.Pending;
    public bool IsApproved => Status == QcStatus.Approved;
    public bool IsRejected => Status == QcStatus.Rejected;

    partial void OnStatusChanged(QcStatus value)
    {
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsApproved));
        OnPropertyChanged(nameof(IsRejected));
    }
}
