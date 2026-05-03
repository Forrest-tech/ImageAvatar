using ImageAvatar.Models;

namespace ImageAvatar.Contracts.Services;

/// <summary>
/// High-quality image matting service backed by a PaddleSeg-exported ONNX model.
/// Input folder → transparent PNG output in 31_抠图完成.
/// </summary>
public interface IMattingService
{
    bool   IsModelLoaded { get; }
    bool   IsRunning     { get; }
    string ModelStatus   { get; }

    Task LoadModelAsync(string modelPath);

    /// <param name="inputFolder">Folder containing source images (e.g. 30_抠图队列).</param>
    /// <param name="outputFolder">Sibling folder for transparent-PNG results (31_抠图完成).</param>
    Task<MattingRunResult> StartAsync(string inputFolder, string outputFolder, CancellationToken ct = default);

    void Stop();

    event EventHandler<string> LogMessage;
}
