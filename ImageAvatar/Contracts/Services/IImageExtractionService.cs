using ImageAvatar.Models;

namespace ImageAvatar.Contracts.Services;

public interface IImageExtractionService
{
    bool IsModelLoaded { get; }

    Task LoadModelAsync(string modelPath);

    Task<ExtractionResult> ExtractPatternAsync(
        string sourcePath,
        string targetFolder,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    event EventHandler<ExtractionResult> ExtractionCompleted;
}
