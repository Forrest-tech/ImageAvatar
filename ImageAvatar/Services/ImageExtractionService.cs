using ImageAvatar.Contracts.Services;
using ImageAvatar.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System.Diagnostics;
using System.IO;

namespace ImageAvatar.Services;

/// <summary>
/// Generic AI background-removal service.
/// Supports any salient-object-detection ONNX model that follows the
/// standard [1,3,H,W] float32 input / [1,1,H,W] float32 output convention
/// with ImageNet normalisation (mean 0.485/0.456/0.406, std 0.229/0.224/0.225).
///
/// Tested with:
///   • U-2-Net   320×320  ~176 MB
///   • RMBG-2.0 1024×1024 ~270 MB  ← recommended (briaai/RMBG-2.0 on HuggingFace)
///   • IS-Net    1024×1024 ~176 MB
///
/// Input/output dimensions are auto-detected from ONNX metadata at load time.
/// </summary>
public sealed class ImageExtractionService : IImageExtractionService, IDisposable
{
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std  = [0.229f, 0.224f, 0.225f];

    private static readonly string[] SupportedExts =
        [".png", ".jpg", ".jpeg", ".bmp", ".tiff", ".tif", ".webp"];

    private InferenceSession? _session;
    private int _inputH = 1024;
    private int _inputW = 1024;

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

        // Auto-detect spatial dimensions from the first input tensor.
        // Expected shape: [1, 3, H, W] — fall back to 1024 if dynamic (-1).
        var dims = _session.InputMetadata.Values.First().Dimensions;
        _inputH = dims.Length >= 4 && dims[2] > 0 ? dims[2] : 1024;
        _inputW = dims.Length >= 4 && dims[3] > 0 ? dims[3] : 1024;

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
            // 1 ─ Load ────────────────────────────────────────────────────
            p?.Report(0.05);
            using var original = Cv2.ImRead(sourcePath, ImreadModes.Color);
            if (original.Empty())
                throw new InvalidOperationException($"Cannot read image: {sourcePath}");

            int origH = original.Rows, origW = original.Cols;
            ct.ThrowIfCancellationRequested();

            // 2 ─ Resize to model input size ─────────────────────────────
            p?.Report(0.10);
            using var resized = new Mat();
            Cv2.Resize(original, resized, new Size(_inputW, _inputH));

            // 3 ─ Build normalized CHW float tensor ──────────────────────
            p?.Report(0.18);
            var tensor = BuildInputTensor(resized, _inputH, _inputW);
            ct.ThrowIfCancellationRequested();

            // 4 ─ ONNX inference ──────────────────────────────────────────
            p?.Report(0.25);
            var inputName  = _session!.InputMetadata.Keys.First();
            var namedInput = new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) };

            using var outputs = _session.Run(namedInput);
            var rawMask       = outputs.First().AsTensor<float>();
            ct.ThrowIfCancellationRequested();

            // 5 ─ Detect actual output dimensions ────────────────────────
            p?.Report(0.45);
            var outShape = rawMask.Dimensions.ToArray();
            int outH = outShape.Length >= 4 && outShape[2] > 0 ? outShape[2] : _inputH;
            int outW = outShape.Length >= 4 && outShape[3] > 0 ? outShape[3] : _inputW;

            // 6 ─ Min-max normalise → float Mat ──────────────────────────
            p?.Report(0.55);
            using var maskF = BuildNormalizedMask(rawMask, outH, outW);

            // 7 ─ Upscale to original resolution (bicubic) ───────────────
            p?.Report(0.62);
            using var maskFullF = new Mat();
            Cv2.Resize(maskF, maskFullF,
                new Size(origW, origH), 0, 0, InterpolationFlags.Cubic);

            // 8 ─ Slight Gaussian blur for edge anti-aliasing ────────────
            p?.Report(0.70);
            using var maskBlurred = new Mat();
            Cv2.GaussianBlur(maskFullF, maskBlurred, new Size(3, 3), sigmaX: 1.0);

            // 9 ─ Convert to 8-bit alpha ──────────────────────────────────
            using var mask8 = new Mat();
            maskBlurred.ConvertTo(mask8, MatType.CV_8UC1, 255.0);

            // 10 ─ Assemble 32-bit BGRA ──────────────────────────────────
            p?.Report(0.78);
            using var bgra = new Mat();
            Cv2.CvtColor(original, bgra, ColorConversionCodes.BGR2BGRA);

            Mat[] channels = Cv2.Split(bgra);
            try
            {
                mask8.CopyTo(channels[3]);
                Cv2.Merge(channels, bgra);
            }
            finally { foreach (var ch in channels) ch.Dispose(); }

            ct.ThrowIfCancellationRequested();

            // 11 ─ Auto-crop to alpha bounding box ───────────────────────
            p?.Report(0.88);
            using var nonZero = new Mat();
            Cv2.FindNonZero(mask8, nonZero);

            Mat output;
            if (nonZero.Rows > 0)
            {
                var r = Cv2.BoundingRect(nonZero);
                r = new Rect(
                    Math.Max(0, r.X),
                    Math.Max(0, r.Y),
                    Math.Min(r.Width,  origW - r.X),
                    Math.Min(r.Height, origH - r.Y));
                output = new Mat(bgra, r).Clone();
            }
            else
            {
                output = bgra.Clone();
            }

            // 12 ─ Save as 32-bit transparent PNG ─────────────────────────
            p?.Report(0.95);
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
                SourcePath   = sourcePath,
                Success      = false,
                ErrorMessage = "Cancelled",
                Elapsed      = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            return new ExtractionResult
            {
                SourcePath   = sourcePath,
                Success      = false,
                ErrorMessage = ex.Message,
                Elapsed      = sw.Elapsed
            };
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// BGR 8-bit Mat → [1, 3, H, W] float tensor with ImageNet normalisation (RGB order).
    /// </summary>
    private static DenseTensor<float> BuildInputTensor(Mat bgrMat, int h, int w)
    {
        var tensor = new DenseTensor<float>([1, 3, h, w]);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var px = bgrMat.At<Vec3b>(y, x); // B=Item0, G=Item1, R=Item2
                tensor[0, 0, y, x] = (px.Item2 / 255f - Mean[0]) / Std[0]; // R
                tensor[0, 1, y, x] = (px.Item1 / 255f - Mean[1]) / Std[1]; // G
                tensor[0, 2, y, x] = (px.Item0 / 255f - Mean[2]) / Std[2]; // B
            }
        }

        return tensor;
    }

    /// <summary>
    /// Min-max normalises the raw model output into a [0,1] float Mat of size h×w.
    /// Expected tensor layout: [1, 1, H, W].
    /// </summary>
    private static Mat BuildNormalizedMask(Tensor<float> rawOutput, int h, int w)
    {
        float min = float.MaxValue, max = float.MinValue;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float v = rawOutput[0, 0, y, x];
                if (v < min) min = v;
                if (v > max) max = v;
            }

        float range = (max - min) < 1e-7f ? 1f : max - min;
        var data    = new float[h * w];

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                data[y * w + x] = (rawOutput[0, 0, y, x] - min) / range;

        var mask = new Mat(h, w, MatType.CV_32FC1);
        mask.SetArray(data);
        return mask;
    }

    public static bool IsSupported(string path) =>
        SupportedExts.Contains(Path.GetExtension(path).ToLowerInvariant());

    public void Dispose() => _session?.Dispose();
}
