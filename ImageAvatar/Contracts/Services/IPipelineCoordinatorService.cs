using ImageAvatar.Models;

namespace ImageAvatar.Contracts.Services;

public interface IPipelineCoordinatorService
{
    bool IsRunning { get; }
    void Start();
    void Stop();
    event EventHandler<ExtractionProgressEventArgs> ProgressChanged;
    event EventHandler<ExtractionResult> FileCompleted;
}
