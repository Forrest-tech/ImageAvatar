using ImageAvatar.Contracts.Services;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System.Diagnostics;
using System.IO;

namespace ImageAvatar.Services;

/// <summary>
/// Batch-processes all images in 00_提图队列:
///   1. U-2-Net saliency inference → probability mask
///   2. Apply mask as alpha channel (transparent background)
///   3. Auto-crop to non-transparent bounding rect + 5% padding
///   4. Save as transparent PNG to 01_提图完成
/// Falls back to brightness-threshold masking when no ONNX model is present.
/// Auto-stops when the entire queue has been processed.
/// </summary>
public sealed class CropService : ICropService, IDisposable
{
    private static readonly string[] SupportedExts =
        [".png", ".jpg", ".jpeg", ".bmp", ".tiff", ".tif", ".webp"];

    // U-2-Net ImageNet normalization (RGB order)
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std  = [0.229f, 0.224f, 0.225f];
    private const int   ModelInputSize = 320;
    private const float PadPercent     = 0.05f;   // 5% padding each side

    private readonly AppSettingsService _settings;
    private readonly IStorageService    _storage;
    private readonly ILogService        _log;

    private InferenceSession?        _session;
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }

    public CropService(AppSettingsService settings, IStorageService storage, ILogService log)
    {
        _settings = settings;
        _storage  = storage;
        _log      = log;
    }

    // ── Public API ─────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts      = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IsRunning = true;

        LoadSession();          // lazy-load ONNX model for this batch

        // ── Resolve input / output directories ──────────────────────────────
        // Supports two conventions:
        //   (a) user browsed to workspace root → 00_提图队列 is a subfolder
        //   (b) user browsed directly into 00_提图队列 → output is a sibling
        var root        = _storage.RootPath;
        var queueInRoot = Path.Combine(root, "00_提图队列");
        string inputDir, outputDir;
        if (Directory.Exists(queueInRoot))
        {
            inputDir  = queueInRoot;
            outputDir = Path.Combine(root, "01_提图完成");
        }
        else
        {
            inputDir  = root;
            var parent = Path.GetDirectoryName(root) ?? root;
            outputDir = Path.Combine(parent, "01_提图完成");
        }
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);
        _log.Log("裁图", $"输入: {inputDir}");
        _log.Log("裁图", $"输出: {outputDir}");

        // ── Enumerate queue ────────────────────────────────────────────────
        var files = GetImageFiles(inputDir);
        if (files.Length == 0)
        {
            _log.Log("裁图", "队列为空，无需处理");
            return;                    // natural completion → ViewModel shows "裁剪完成"
        }

        _log.Log("裁图", $"开始处理 {files.Length} 张图片（并发: 2）…");
        int ok = 0, fail = 0;

        // ── Batch with bounded parallelism ─────────────────────────────────
        // InferenceSession.Run() is thread-safe; two concurrent inferences are safe.
        await Parallel.ForEachAsync(
            files,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 2,
                CancellationToken      = _cts.Token
            },
            (file, token) =>
            {
                token.ThrowIfCancellationRequested();
                var (success, msg) = ExtractAndCrop(file, outputDir);
                if (success) Interlocked.Increment(ref ok);
                else         Interlocked.Increment(ref fail);
                _log.Log("裁图", msg);
                return ValueTask.CompletedTask;
            });

        _log.Log("裁图", $"全部完成  ✓{ok} 成功  ✗{fail} 失败");

        // Release model memory after batch — next StartAsync will reload if needed
        _session?.Dispose();
        _session = null;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts      = null;
        IsRunning = false;
    }

    // ── Core pipeline ──────────────────────────────────────────────────────

    private (bool success, string message) ExtractAndCrop(string inputPath, string outputDir)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // 1. Load original image
            using var original = Cv2.ImRead(inputPath, ImreadModes.Color);
            if (original.Empty())
                return (false, $"✗ 无法读取: {Path.GetFileName(inputPath)}");

            int origH = original.Rows, origW = original.Cols;

            // 2. Build 8-bit alpha mask (U-2-Net or threshold fallback)
            using var mask8 = _session is not null
                ? BuildOnnxMask(original, origW, origH)
                : BuildThresholdMask(original);

            // 3. Compose BGRA: original pixels + mask as alpha channel
            using var bgra = new Mat();
            Cv2.CvtColor(original, bgra, ColorConversionCodes.BGR2BGRA);
            Mat[] ch = Cv2.Split(bgra);
            try
            {
                mask8.CopyTo(ch[3]);
                Cv2.Merge(ch, bgra);
            }
            finally { foreach (var c in ch) c.Dispose(); }

            // 4. Find bounding rect of visible (non-zero alpha) pixels
            using var nonZero = new Mat();
            Cv2.FindNonZero(mask8, nonZero);

            Mat cropped;
            if (nonZero.Rows > 0)
            {
                var rect = Cv2.BoundingRect(nonZero);
                rect     = ApplyPadding(rect, origW, origH, PadPercent);
                cropped  = new Mat(bgra, rect).Clone();
            }
            else
            {
                // Mask returned nothing — fall through with full image to avoid silent loss
                cropped = bgra.Clone();
                _log.Log("裁图", $"⚠ 未检测到有效区域，保留全图: {Path.GetFileName(inputPath)}");
            }

            // 5. Save as transparent PNG (lossless, supports alpha)
            using (cropped)
            {
                Directory.CreateDirectory(outputDir);
                var outName  = Path.GetFileNameWithoutExtension(inputPath) + ".png";
                var destPath = Path.Combine(outputDir, outName);
                Cv2.ImWrite(destPath, cropped);
                sw.Stop();
                return (true,
                    $"✓ {Path.GetFileName(inputPath)} → {outName}  " +
                    $"[{cropped.Cols}×{cropped.Rows}]  {sw.Elapsed.TotalSeconds:F1}s");
            }
        }
        catch (Exception ex)
        {
            return (false, $"✗ {Path.GetFileName(inputPath)}: {ex.Message}");
        }
    }

    // ── U-2-Net inference → 8-bit alpha mask ──────────────────────────────

    private Mat BuildOnnxMask(Mat original, int origW, int origH)
    {
        // Pre-process: resize to 320×320, build CHW float32 tensor (ImageNet-normalized, RGB)
        using var resized = new Mat();
        Cv2.Resize(original, resized, new Size(ModelInputSize, ModelInputSize));

        var tensor = new DenseTensor<float>([1, 3, ModelInputSize, ModelInputSize]);
        for (int y = 0; y < ModelInputSize; y++)
            for (int x = 0; x < ModelInputSize; x++)
            {
                var px = resized.At<Vec3b>(y, x);           // OpenCV stores BGR
                tensor[0, 0, y, x] = (px.Item2 / 255f - Mean[0]) / Std[0]; // R
                tensor[0, 1, y, x] = (px.Item1 / 255f - Mean[1]) / Std[1]; // G
                tensor[0, 2, y, x] = (px.Item0 / 255f - Mean[2]) / Std[2]; // B
            }

        // Inference
        var inName = _session!.InputMetadata.Keys.First();
        using var outputs = _session.Run([NamedOnnxValue.CreateFromTensor(inName, tensor)]);
        var raw = outputs.First().AsTensor<float>();

        // Min-max normalize sigmoid output → [0,1] float Mat at 320×320
        float min = float.MaxValue, max = float.MinValue;
        for (int y = 0; y < ModelInputSize; y++)
            for (int x = 0; x < ModelInputSize; x++)
            {
                float v = raw[0, 0, y, x];
                if (v < min) min = v;
                if (v > max) max = v;
            }
        float range = (max - min) < 1e-7f ? 1f : max - min;

        var floatData = new float[ModelInputSize * ModelInputSize];
        for (int y = 0; y < ModelInputSize; y++)
            for (int x = 0; x < ModelInputSize; x++)
                floatData[y * ModelInputSize + x] = (raw[0, 0, y, x] - min) / range;

        using var mask320f = new Mat(ModelInputSize, ModelInputSize, MatType.CV_32FC1);
        mask320f.SetArray(floatData);

        // Upscale to original resolution (bicubic for smooth edges)
        using var maskFullF = new Mat();
        Cv2.Resize(mask320f, maskFullF, new Size(origW, origH), 0, 0, InterpolationFlags.Cubic);

        // Slight blur for edge anti-aliasing
        using var maskBlur = new Mat();
        Cv2.GaussianBlur(maskFullF, maskBlur, new Size(3, 3), sigmaX: 1.0);

        // Convert to 8-bit (0–255 alpha)
        var mask8 = new Mat();
        maskBlur.ConvertTo(mask8, MatType.CV_8UC1, 255.0);
        return mask8;   // caller owns, must Dispose
    }

    // ── Brightness-threshold fallback (no model) ────────────────────────────
    // Works well for colorful prints on light/dark uniform backgrounds.
    private static Mat BuildThresholdMask(Mat original)
    {
        using var gray = new Mat();
        Cv2.CvtColor(original, gray, ColorConversionCodes.BGR2GRAY);

        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(5, 5), 0);

        var mean  = Cv2.Mean(blurred);
        var mask8 = new Mat();
        if      (mean.Val0 > 200) Cv2.Threshold(blurred, mask8, 230, 255, ThresholdTypes.BinaryInv); // white bg
        else if (mean.Val0 > 128) Cv2.Threshold(blurred, mask8, 200, 255, ThresholdTypes.BinaryInv); // light bg
        else                      Cv2.Threshold(blurred, mask8,  30, 255, ThresholdTypes.Binary);     // dark bg

        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(7, 7));
        Cv2.MorphologyEx(mask8, mask8, MorphTypes.Close, kernel);
        Cv2.MorphologyEx(mask8, mask8, MorphTypes.Open,  kernel);
        return mask8;   // caller owns, must Dispose
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static Rect ApplyPadding(Rect r, int imageW, int imageH, float pad)
    {
        int px = (int)(r.Width  * pad);
        int py = (int)(r.Height * pad);
        int x  = Math.Max(0,      r.X - px);
        int y  = Math.Max(0,      r.Y - py);
        int x2 = Math.Min(imageW, r.X + r.Width  + px);
        int y2 = Math.Min(imageH, r.Y + r.Height + py);
        return new Rect(x, y, x2 - x, y2 - y);
    }

    private void LoadSession()
    {
        if (_session is not null) return;   // already loaded
        try
        {
            if (!File.Exists(_settings.ModelPath))
            {
                _log.Log("裁图", "⚠ 未找到 ONNX 模型，使用 OpenCV 亮度阈值回退模式");
                return;
            }
            var opts = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode          = ExecutionMode.ORT_SEQUENTIAL
            };
            _session = new InferenceSession(_settings.ModelPath, opts);
            _log.Log("裁图", $"U2Net 已加载: {_settings.ModelPath}");
        }
        catch (Exception ex)
        {
            _log.Log("裁图", $"ONNX 加载失败，使用回退模式：{ex.Message}");
        }
    }

    private static string[] GetImageFiles(string folder) =>
        Directory.Exists(folder)
            ? [.. Directory.GetFiles(folder)
                .Where(f => SupportedExts.Contains(
                    Path.GetExtension(f).ToLowerInvariant()))]
            : [];

    public void Dispose()
    {
        Stop();
        _session?.Dispose();
    }
}
