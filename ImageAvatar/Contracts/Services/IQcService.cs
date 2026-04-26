using ImageAvatar.Models;

namespace ImageAvatar.Contracts.Services;

public interface IQcService
{
    IReadOnlyList<QcItem> LoadItems(
        string mockupFolder,
        string patternFolder,
        string sourceFolder);

    Task ApproveAsync(QcItem item);
    Task RejectAsync(QcItem item);
}
