using ImageAvatar.Contracts.Services;
using ImageAvatar.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System.Diagnostics;
using System.IO;

namespace ImageAvatar.Services;

/// <summary>
/// U-2-Net background removal service.
/// Model: u2net.onnx from https://github.com/danielgatis/rembg
/// Input  : [1, 3, 320, 320] float32, RGB, ImageNet-normalized
/// Output : [1, 1, 320, 320] float32, sigmoid mask (0–1)
/// </summary>
public sealed class ImageExtractionService : IImageExtractionService, IDisposable
{
    // ImageNet per-channel mean / std (RGB order) used by rembg / U-2-Net
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std  = [0.229f, 0.224f, 0.225f];

    private const int InputSize = 320;

    private static readonly string[] SupportedExts =
        [".png", ".jpg", ".jpeg", ".bmp", ".tiff", ".tif", ".webp"];

    private InferenceSession? _session;

    public bool IsModelLoaded => _session is not null;
    public event EventHandler<ExtractionResult>? ExtractionCompleted;

    // ── Model loading ──────────────────────────────────────────────────────

    public Task LoadModelAsync(string modelPath)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("ONNX model not found.", modelPath);

        _session?.Dispose();

        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode          = ExecutionMode.ORT_SEQUENTIAL
        };

        _session = new InferenceSession(modelPath, opts);
        return Task.CompletedTask;
    }

    // ── Public entry point ─────────────────────────────────────────────────

    public async Task<ExtractionResult> ExtractPatternAsync(
        string sourcePath,
        string targetFolder,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (_session is null)
            throw new InvalidOperationException(
                "Model not loaded. Call LoadModelAsync() first.");

        var result = await Task.Run(
            () => RunExtraction(sourcePath, targetFolder, progress, ct), ct);

        ExtractionCompleted?.Invoke(this, result);
        return result;
    }

    // ── Core pipeline ──────────────────────────────────────────────────────

    private ExtractionResult RunExtraction(
        string sourcePath,
        string targetFolder,
        IProgress<double>? p,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // 1 – Load ────────────────────────────────────────────────────
            p?.Report(0.05);
            using var original = Cv2.ImRead(sourcePath, ImreadModes.Color);
            if (original.Empty())
                throw new InvalidOperationException($"Cannot read image: {sourcePath}");

            int origH = original.Rows, origW = original.Cols;
            ct.ThrowIfCancellationRequested();

            // 2 – Resize to 320×320 ───────────────────────────────────────
            p?.Report(0.10);
            using var resized320 = new Mat();
            Cv2.Resize(original, resized320, new Size(InputSize, InputSize));

            // 3 – Build normalized CHW float tensor ───────────────────────
            p?.Report(0.18);
            var tensor = BuildInputTensor(resized320);
            ct.ThrowIfCancellationRequested();

            // 4 – ONNX inference ──────────────────────────────────────────
            p?.Report(0.25);
            var inputName = _session!.InputMetadata.Keys.First();
            var namedInput = new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) };

            using var outputs  = _session.Run(namedInput);
            var rawMask        = outputs.First().AsTensor<float>();
            ct.ThrowIfCancellationRequested();

            // 5 – Min-max normalize → float Mat 320×320 ───────────────────
            p?.Report(0.50);
            using var mask320f = BuildNormalizedMask(rawMask);

            // 6 – Upscale to original resolution (bicubic) ────────────────
            p?.Report(0.60);
            using var maskFullF = new Mat();
            Cv2.Resize(mask320f, maskFullF,
                new Size(origW, origH), 0, 0, InterpolationFlags.Cubic);

            // 7 – Slight Gaussian blur for edge anti-aliasing ─────────────
            p?.Report(0.68);
            using var maskBlurred = new Mat();
            Cv2.GaussianBlur(maskFullF, maskBlurred, new Size(3, 3), sigmaX: 1.0);

            // 8 – Convert mask to 8-bit ────────────────────────────────────
            using var mask8 = new Mat();
            maskBlurred.ConvertTo(mask8, MatType.CV_8UC1, 255.0);

            // 9 – Assemble 32-bit BGRA (replace alpha channel with mask) ──
            p?.Report(0.75);
            using var bgra = new Mat();
            Cv2.CvtColor(original, bgra, ColorConversionCodes.BGR2BGRA);

            Mat[] channels = Cv2.Split(bgra);
            try
            {
                mask8.CopyTo(channels[3]);       // swap in our alpha channel
                Cv2.Merge(channels, bgra);
            }
            finally { foreach (var ch in channels) ch.Dispose(); }

            ct.ThrowIfCancellationRequested();

            // 10 – Auto-crop: bounding box of alpha > 0 pixels ────────────
            p?.Report(0.85);
            using var nonZero = new Mat();
            Cv2.FindNonZero(mask8, nonZero);

            Mat output;
            if (nonZero.Rows > 0)
            {
                var cropRect = Cv2.BoundingRect(nonZero);
                // Ensure the rect stays within image bounds
                cropRect = new Rect(
                    Math.Max(0, cropRect.X),
                    Math.Max(0, cropRect.Y),
                    Math.Min(cropRect.Width,  origW - cropRect.X),
                    Math.Min(cropRect.Height, origH - cropRect.Y));
                output = new Mat(bgra, cropRect).Clone();
            }
            else
            {
                output = bgra.Clone();
            }

            // 11 – Save as 32-bit transparent PNG ─────────────────────────
            p?.Report(0.93);
            Directory.CreateDirectory(targetFolder);
            var destPath = Path.Combine(targetFolder,
                Path.GetFileNameWithoutExtension(sourcePath) + ".png");

            Cv2.ImWrite(destPath, output);
            output.Dispose();

            p?.Report(1.0);
            sw.Stop();

            return new ExtractionResult
            {
                SourcePath      = sourcePath,
                DestinationPath = destPath,
                Success         = true,
                Elapsed         = sw.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            return new ExtractionResult
            {
                SourcePath    = sourcePath,
                Success       = false,
                ErrorMessage  = "Cancelled",
                Elapsed       = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            return new ExtractionResult
            {
                SourcePath    = sourcePath,
                Success       = false,
                ErrorMessage  = ex.Message,
                Elapsed       = sw.Elapsed
            };
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a BGR 320×320 8-bit Mat into a [1,3,320,320] float tensor,
    /// applying ImageNet per-channel normalization (RGB order).
    /// </summary>
    private static DenseTensor<float> BuildInputTensor(Mat bgrMat)
    {
        var tensor = new DenseTensor<float>([1, 3, InputSize, InputSize]);

        for (int y = 0; y < InputSize; y++)
        {
            for (int x = 0; x < InputSize; x++)
            {
                var px = bgrMat.At<Vec3b>(y, x); // OpenCV: B=Item0, G=Item1, R=Item2
                tensor[0, 0, y, x] = (px.Item2 / 255f - Mean[0]) / Std[0]; // R
                tensor[0, 1, y, x] = (px.Item1 / 255f - Mean[1]) / Std[1]; // G
                tensor[0, 2, y, x] = (px.Item0 / 255f - Mean[2]) / Std[2]; // B
            }
        }

        return tensor;
    }

    /// <summary>
    /// Min-max normalizes the raw sigmoid output into a [0,1] float Mat.
    /// Matches rembg post-processing: (pred - min) / (max - min).
    /// </summary>
    private static Mat BuildNormalizedMask(Tensor<float> rawOutput)
    {
        float min = float.MaxValue, max = float.MinValue;
        for (int y = 0; y < InputSize; y++)
            for (int x = 0; x < InputSize; x++)
            {
                float v = rawOutput[0, 0, y, x];
                if (v < min) min = v;
                if (v > max) max = v;
            }

        float range = (max - min) < 1e-7f ? 1f : max - min;
        var data    = new float[InputSize * InputSize];

        for (int y = 0; y < InputSize; y++)
            for (int x = 0; x < InputSize; x++)
                data[y * InputSize + x] = (rawOutput[0, 0, y, x] - min) / range;

        var mask = new Mat(InputSize, InputSize, MatType.CV_32FC1);
        mask.SetArray(data);
        return mask;
    }

    public static bool IsSupported(string path) =>
        SupportedExts.Contains(Path.GetExtension(path).ToLowerInvariant());

    public void Dispose() => _session?.Dispose();
}
