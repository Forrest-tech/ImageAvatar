using ImageAvatar.Models;

namespace ImageAvatar.Contracts.Services;

public interface IBatchMockupService
{
    bool IsRunning { get; }

    Task RunAsync(
        string inputFolder,
        string outputFolder,
        string templatesFolder,
        IProgress<BatchProgressEventArgs>? progress = null,
        CancellationToken ct = default);

    event EventHandler<MockupResult> FileCompleted;
}
