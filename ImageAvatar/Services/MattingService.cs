using ImageAvatar.Contracts.Services;
using ImageAvatar.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System.Diagnostics;
using System.IO;

namespace ImageAvatar.Services;

/// <summary>
/// Image matting via a PaddleSeg-compatible ONNX model (PP-Matting / MODNet / HumanMatting).
///
/// Workflow per image:
///   1. Resize to model input size, normalize to [-1, 1]
///   2. ONNX inference → alpha matte [0, 1]
///   3. Upscale matte to original resolution (bicubic + Gaussian blur)
///   4. Compose BGRA: original pixels + alpha matte as alpha channel
///   5. Crop to bounding rect of visible pixels + 5% padding
///   6. Save as transparent PNG to outputFolder
///
/// Model compatibility: any single-input → single-output ONNX matting model whose
/// output is an alpha matte tensor shaped [N,1,H,W] or [N,H,W], values in [0,1].
/// This covers all PaddleSeg trimap-free matting exports (PP-Matting, MODNet, etc.).
/// </summary>
public sealed class MattingService : IMattingService, IDisposable
{
    private static readonly string[] SupportedExts =
        [".png", ".jpg", ".jpeg", ".bmp", ".tiff", ".tif", ".webp"];

    // PaddleSeg portrait/matting models normalise with mean=0.5, std=0.5 → values in [-1,1]
    private static readonly float[] Mean = [0.5f, 0.5f, 0.5f];
    private static readonly float[] Std  = [0.5f, 0.5f, 0.5f];
    private const float PadPercent = 0.05f;

    private readonly ILogService             _log;
    private readonly IImageExtractionService _extraction;

    private InferenceSession?        _session;
    private CancellationTokenSource? _cts;
    private int _modelH = 512, _modelW = 512;   // detected from ONNX metadata

    public bool   IsModelLoaded { get; private set; }
    public bool   IsRunning     { get; private set; }
    public string ModelStatus   { get; private set; } = "未加载";

    public event EventHandler<string>? LogMessage;

    public MattingService(ILogService log, IImageExtractionService extraction)
    {
        _log        = log;
        _extraction = extraction;
    }

    // ── Model loading ──────────────────────────────────────────────────────

    public Task LoadModelAsync(string modelPath)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("模型文件不存在", modelPath);

        _session?.Dispose();
        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode          = ExecutionMode.ORT_SEQUENTIAL
        };
        _session = new InferenceSession(modelPath, opts);

        // Detect fixed input H/W from ONNX metadata; use 512 for dynamic dims
        var dims = _session.InputMetadata.Values.First().Dimensions;
        _modelH = dims.Length == 4 && dims[2] > 0 ? dims[2] : 512;
        _modelW = dims.Length == 4 && dims[3] > 0 ? dims[3] : 512;

        IsModelLoaded = true;
        ModelStatus   = $"✓ 已加载  (输入 {_modelW}×{_modelH})";
        Emit($"抠图模型已加载: {Path.GetFileName(modelPath)}  输入尺寸 {_modelW}×{_modelH}");
        return Task.CompletedTask;
    }

    // ── Batch pipeline ─────────────────────────────────────────────────────

    public async Task<MattingRunResult> StartAsync(string inputFolder, string outputFolder, CancellationToken ct = default)
    {
        _cts      = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IsRunning = true;
        try
        {
            Directory.CreateDirectory(inputFolder);
            Directory.CreateDirectory(outputFolder);
            Emit($"输入: {inputFolder}");
            Emit($"输出: {outputFolder}");

            var files = GetImageFiles(inputFolder);
            if (files.Length == 0)
            {
                Emit("队列为空，无需处理");
                return new MattingRunResult
                {
                    Outcome      = MattingRunOutcome.EmptyQueue,
                    InputFolder  = inputFolder,
                    OutputFolder = outputFolder
                };
            }

            // Engine selection (priority high → low):
            //   PaddleSeg ONNX (loaded via 自动抠图配置)        — best quality
            //   U-2-Net via IImageExtractionService              — good quality
            //   OpenCV GrabCut (built-in, no model needed)       — always works
            string engine;
            bool useExtractionService;
            if (IsModelLoaded)
            {
                engine = "PaddleSeg";
                useExtractionService = false;
            }
            else if (_extraction.IsModelLoaded)
            {
                engine = "U-2-Net";
                useExtractionService = true;
            }
            else
            {
                engine = "OpenCV 设计提取";   // GrabCut + LAB-distance design isolation
                useExtractionService = false;
            }

            Emit($"开始处理 {files.Length} 张图片（{engine}，并发: 2）…");

            int ok = 0, fail = 0;

            if (useExtractionService)
            {
                // ImageExtractionService isn't documented as thread-safe — run serially.
                foreach (var file in files)
                {
                    if (_cts.Token.IsCancellationRequested) break;
                    var (success, msg) = await ProcessOneFallbackAsync(file, outputFolder, _cts.Token);
                    if (success) ok++; else fail++;
                    Emit(msg);
                }
            }
            else
            {
                // PaddleSeg ONNX and OpenCV GrabCut both run via ProcessOne (which
                // picks _session vs FallbackMask based on whether _session is null).
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
                        var (success, msg) = ProcessOne(file, outputFolder);
                        if (success) Interlocked.Increment(ref ok);
                        else         Interlocked.Increment(ref fail);
                        Emit(msg);
                        return ValueTask.CompletedTask;
                    });
            }

            Emit($"全部完成  ✓{ok} 成功  ✗{fail} 失败  (引擎: {engine})");
            return new MattingRunResult
            {
                Outcome      = MattingRunOutcome.Completed,
                InputCount   = files.Length,
                SuccessCount = ok,
                FailureCount = fail,
                UsedFallback = !IsModelLoaded,
                InputFolder  = inputFolder,
                OutputFolder = outputFolder
            };
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task<(bool success, string message)> ProcessOneFallbackAsync(
        string inputPath, string outputDir, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _extraction.ExtractPatternAsync(inputPath, outputDir, null, ct);
            sw.Stop();
            return result.Success
                ? (true,  $"✓ {Path.GetFileName(inputPath)} → {Path.GetFileName(result.DestinationPath)}  {sw.Elapsed.TotalSeconds:F1}s")
                : (false, $"✗ {Path.GetFileName(inputPath)}: {result.ErrorMessage}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (false, $"✗ {Path.GetFileName(inputPath)}: {ex.Message}");
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts      = null;
        IsRunning = false;
    }

    // ── Per-image matting pipeline ─────────────────────────────────────────

    private (bool success, string message) ProcessOne(string inputPath, string outputDir)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var original = Cv2.ImRead(inputPath, ImreadModes.Color);
            if (original.Empty())
                return (false, $"✗ 无法读取: {Path.GetFileName(inputPath)}");

            int origH = original.Rows, origW = original.Cols;

            // 1. Build 8-bit alpha matte
            using var alpha8 = _session is not null
                ? RunInference(original, origW, origH)
                : FallbackMask(original);

            // 2. Compose BGRA (original pixel colours + matte as alpha channel)
            using var bgra = new Mat();
            Cv2.CvtColor(original, bgra, ColorConversionCodes.BGR2BGRA);
            Mat[] ch = Cv2.Split(bgra);
            try { alpha8.CopyTo(ch[3]); Cv2.Merge(ch, bgra); }
            finally { foreach (var c in ch) c.Dispose(); }

            // 3. Auto-crop to visible region + 5% padding
            using var nonZero = new Mat();
            Cv2.FindNonZero(alpha8, nonZero);

            Mat result;
            if (nonZero.Rows > 0)
            {
                var rect = ApplyPadding(Cv2.BoundingRect(nonZero), origW, origH, PadPercent);
                result   = new Mat(bgra, rect).Clone();
            }
            else
            {
                result = bgra.Clone();
                Emit($"⚠ 未检测到有效前景，保留全图: {Path.GetFileName(inputPath)}");
            }

            // 4. Save as transparent PNG (lossless, supports alpha)
            using (result)
            {
                var outName  = Path.GetFileNameWithoutExtension(inputPath) + ".png";
                var destPath = Path.Combine(outputDir, outName);
                Cv2.ImWrite(destPath, result);
                sw.Stop();
                return (true,
                    $"✓ {Path.GetFileName(inputPath)} → {outName}  " +
                    $"[{result.Cols}×{result.Rows}]  {sw.Elapsed.TotalSeconds:F1}s");
            }
        }
        catch (Exception ex)
        {
            return (false, $"✗ {Path.GetFileName(inputPath)}: {ex.Message}");
        }
    }

    // ── ONNX inference ─────────────────────────────────────────────────────

    private Mat RunInference(Mat original, int origW, int origH)
    {
        // For dynamic-dim models, round to nearest multiple of 32
        int targetH = _modelH > 0 ? _modelH : RoundUp(origH, 32);
        int targetW = _modelW > 0 ? _modelW : RoundUp(origW, 32);

        using var resized = new Mat();
        Cv2.Resize(original, resized, new Size(targetW, targetH));

        // Build CHW float32 tensor, normalised to [-1, 1]
        var tensor = new DenseTensor<float>([1, 3, targetH, targetW]);
        for (int y = 0; y < targetH; y++)
            for (int x = 0; x < targetW; x++)
            {
                var px = resized.At<Vec3b>(y, x);                    // OpenCV BGR
                tensor[0, 0, y, x] = (px.Item2 / 255f - Mean[0]) / Std[0]; // R
                tensor[0, 1, y, x] = (px.Item1 / 255f - Mean[1]) / Std[1]; // G
                tensor[0, 2, y, x] = (px.Item0 / 255f - Mean[2]) / Std[2]; // B
            }

        var inName = _session!.InputMetadata.Keys.First();
        using var outputs    = _session.Run([NamedOnnxValue.CreateFromTensor(inName, tensor)]);
        var alphaTensor      = outputs.First().AsTensor<float>();
        var dims             = alphaTensor.Dimensions.ToArray();

        // Support [N,1,H,W] and [N,H,W] output layouts
        bool is4d = dims.Length == 4;
        int  outH = is4d ? dims[2] : dims[1];
        int  outW = is4d ? dims[3] : dims[2];

        // Copy to float Mat
        var floatData = new float[outH * outW];
        for (int y = 0; y < outH; y++)
            for (int x = 0; x < outW; x++)
                floatData[y * outW + x] = Math.Clamp(
                    is4d ? alphaTensor[0, 0, y, x] : alphaTensor[0, y, x], 0f, 1f);

        using var alphaF = new Mat(outH, outW, MatType.CV_32FC1);
        alphaF.SetArray(floatData);

        // Upscale to original resolution (bicubic for smooth matte edges)
        using var alphaUpF = new Mat();
        Cv2.Resize(alphaF, alphaUpF, new Size(origW, origH), 0, 0, InterpolationFlags.Cubic);

        // Slight Gaussian blur for anti-aliased matte edges
        using var alphaBlur = new Mat();
        Cv2.GaussianBlur(alphaUpF, alphaBlur, new Size(3, 3), sigmaX: 1.0);

        var alpha8 = new Mat();
        alphaBlur.ConvertTo(alpha8, MatType.CV_8UC1, 255.0);
        return alpha8;  // caller owns; Dispose via using
    }

    // ── Fallback: classical CV matting (no ML model required) ─────────────
    //
    // Two-stage design extraction tuned for POD garment photos, adaptive to
    // both DARK and LIGHT/WHITE garments:
    //
    //   Stage 1 — GrabCut isolates the garment from the model/background.
    //   Stage 2 — colour-distance scoring inside the garment isolates the
    //             printed design from the fabric.
    //
    // For DARK garments (median LAB-L < ~140) we use full LAB Euclidean
    // distance from the median garment colour — anything brighter or more
    // saturated counts as design.
    //
    // For LIGHT/WHITE garments (median LAB-L > ~190) the LAB metric falsely
    // flags shadows on the fabric as design (a shadow on white has LAB
    // distance ~60 from pure white, well above the design threshold).
    // Instead we score each pixel by:
    //
    //     designScore = max(chromaDist * 3, lumDrop − shadowFloor)
    //
    //   where chromaDist = sqrt((a-gA)² + (b-gB)²)  → colour saturation diff
    //         lumDrop    = max(0, gL − L)            → only DARKER counts
    //         shadowFloor = 50 LAB units             → drops below this are
    //                                                  treated as shadow noise
    //
    // Shadows have low chromaDist (same hue as garment) and modest lumDrop
    // (typically ≤ 60), so they score near zero. Coloured or starkly-dark
    // design parts score high.
    //
    // For mid-luminance garments (140 ≤ L ≤ 190) we linearly blend the two.

    private const int    GrabCutTargetSize          = 512;
    private const int    GrabCutIterations          = 3;
    private const double StrongDesignDistance       = 25.0;   // pixels clearly NOT garment
    private const double SoftAlphaLowDistance       = 6.0;    // ramp start (alpha = 0)
    private const double SoftAlphaHighDistance      = 28.0;   // ramp end   (alpha = 255)
    private const double LightGarmentLBlendStart    = 140.0;  // L below this → 100 % LAB metric
    private const double LightGarmentLBlendEnd      = 190.0;  // L above this → 100 % light-shirt metric
    private const double LightShirtShadowFloor      = 50.0;   // LAB-luminance drops below this = shadow
    private const double MinDesignFractionOfGarment = 0.005;
    private const double MaxDesignFractionOfGarment = 0.95;
    private const double MinComponentEdgeDensity    = 0.04;   // smooth shadow blobs score < 0.02
    private const double NearbyComponentDistanceFraction = 0.6; // multiplied by main-blob extent

    private static Mat FallbackMask(Mat original)
    {
        try
        {
            return ExtractDesignMask(original);
        }
        catch
        {
            try { return GrabCutMask(original); }
            catch { return BrightnessThresholdMask(original); }
        }
    }

    private static Mat ExtractDesignMask(Mat original)
    {
        int origH = original.Rows, origW = original.Cols;

        using var garment = GrabCutMask(original);
        Cv2.Threshold(garment, garment, 127, 255, ThresholdTypes.Binary);
        int garmentArea = Cv2.CountNonZero(garment);
        if (garmentArea < 100) return garment.Clone();   // garment too small

        // Sample dominant garment colour (median BGR over garment-foreground pixels).
        var (b, g, r) = SampleMedianColor(original, garment);
        using var samplePix = new Mat(1, 1, MatType.CV_8UC3);
        samplePix.Set(0, 0, new Vec3b((byte)b, (byte)g, (byte)r));
        using var sampleLab = new Mat();
        Cv2.CvtColor(samplePix, sampleLab, ColorConversionCodes.BGR2Lab);
        var gLab = sampleLab.At<Vec3b>(0, 0);

        using var lab = new Mat();
        Cv2.CvtColor(original, lab, ColorConversionCodes.BGR2Lab);
        using var labF = new Mat();
        lab.ConvertTo(labF, MatType.CV_32FC3);
        Mat[] labChans = Cv2.Split(labF);
        using var Lc = labChans[0]; using var Ac = labChans[1]; using var Bc = labChans[2];

        // Adaptive distance metric — blend LAB Euclidean (good for dark
        // garments) and the light-shirt score (rejects shadows on white) by
        // garment lightness.
        using var distLab   = ComputeLabDistance(labF, gLab);
        using var distLight = ComputeLightShirtScore(Lc, Ac, Bc, gLab);
        double lightWeight  = Math.Clamp(
            (gLab.Item0 - LightGarmentLBlendStart) /
            (LightGarmentLBlendEnd - LightGarmentLBlendStart), 0.0, 1.0);
        using var distLab32   = new Mat(); distLab.ConvertTo(distLab32, MatType.CV_32FC1, 1 - lightWeight);
        using var distLight32 = new Mat(); distLight.ConvertTo(distLight32, MatType.CV_32FC1, lightWeight);
        using var dist = new Mat();
        Cv2.Add(distLab32, distLight32, dist);

        // ── Sharp-edge map (Canny) for shadow rejection ────────────────────
        // Designs are sharp ink prints with edges every few pixels. Shadows
        // live in smooth gradients with no edges. Dilate the edges by a small
        // radius (~1 % of shorter side) so that the *interior* of thick design
        // strokes — which are flat ink between two edges — also qualifies.
        using var gray = new Mat();
        Cv2.CvtColor(original, gray, ColorConversionCodes.BGR2GRAY);
        using var grayBlur = new Mat();
        Cv2.GaussianBlur(gray, grayBlur, new Size(3, 3), 0);
        using var edges = new Mat();
        Cv2.Canny(grayBlur, edges, 60, 150);
        int edgeDilate = Math.Max(7, Math.Min(origW, origH) / 100);
        using var edgesDilated = new Mat();
        Cv2.Dilate(edges, edgesDilated,
            Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(edgeDilate, edgeDilate)));

        // Stage A — strong design pixels: high colour-distance AND near a sharp
        // Canny edge. Pixel-level edge gating separates shadows from design
        // BEFORE morphological close has a chance to merge them.
        using var strongF = new Mat();
        Cv2.Threshold(dist, strongF, StrongDesignDistance, 255, ThresholdTypes.Binary);
        using var strongRaw = new Mat();
        strongF.ConvertTo(strongRaw, MatType.CV_8UC1);
        Cv2.BitwiseAnd(strongRaw, garment, strongRaw);
        using var strong = new Mat();
        Cv2.BitwiseAnd(strongRaw, edgesDilated, strong);
        using var kOpen = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
        Cv2.MorphologyEx(strong, strong, MorphTypes.Open, kOpen);

        // Stage B — close with a kernel ~5 % of the shorter side so multi-part
        // designs (illustration + text + logo) merge into one main blob.
        int closeKernelSize = Math.Max(25, Math.Min(origW, origH) / 18);
        using var kClose = Cv2.GetStructuringElement(
            MorphShapes.Ellipse, new Size(closeKernelSize, closeKernelSize));
        using var merged = new Mat();
        Cv2.MorphologyEx(strong, merged, MorphTypes.Close, kClose);

        using var labels    = new Mat();
        using var stats     = new Mat();
        using var centroids = new Mat();
        int numLabels = Cv2.ConnectedComponentsWithStats(merged, labels, stats, centroids);
        if (numLabels <= 1) return garment.Clone();

        // Pick the largest *edge-rich* blob — components below MinComponentEdgeDensity
        // are smooth shadow regions even if they have high colour distance.
        int chosenIdx = -1;
        int chosenArea = 0;
        for (int i = 1; i < numLabels; i++)
        {
            int a = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            if (a < garmentArea * 0.0005) continue;
            using var compMask = new Mat();
            Cv2.Compare(labels, new Scalar(i), compMask, CmpType.EQ);
            using var edgesInComp = new Mat();
            Cv2.BitwiseAnd(edges, compMask, edgesInComp);
            double density = (double)Cv2.CountNonZero(edgesInComp) / a;
            if (density < MinComponentEdgeDensity) continue;
            if (a > chosenArea) { chosenArea = a; chosenIdx = i; }
        }
        if (chosenIdx < 0)
        {
            // No edge-rich blob found — fall back to plain largest.
            for (int i = 1; i < numLabels; i++)
            {
                int a = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
                if (a > chosenArea) { chosenArea = a; chosenIdx = i; }
            }
            if (chosenIdx < 0) return garment.Clone();
        }
        double frac = (double)chosenArea / Math.Max(1, garmentArea);
        if (frac < MinDesignFractionOfGarment || frac > MaxDesignFractionOfGarment)
            return garment.Clone();

        // Group: keep the chosen blob plus any *edge-rich* blob whose centroid
        // is within NearbyComponentDistanceFraction × main-blob-extent of the
        // chosen blob's centroid. Distance-from-centroid is tighter than
        // bbox-containment and rejects shadow blobs at the body edges.
        double mainCx = centroids.At<double>(chosenIdx, 0);
        double mainCy = centroids.At<double>(chosenIdx, 1);
        int mbw = stats.At<int>(chosenIdx, (int)ConnectedComponentsTypes.Width);
        int mbh = stats.At<int>(chosenIdx, (int)ConnectedComponentsTypes.Height);
        double maxDistFromMain = Math.Max(mbw, mbh) * NearbyComponentDistanceFraction;

        using var grouped = new Mat(origH, origW, MatType.CV_8UC1, Scalar.Black);
        for (int i = 1; i < numLabels; i++)
        {
            int a = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            if (a < garmentArea * 0.0005) continue;

            double cx = centroids.At<double>(i, 0);
            double cy = centroids.At<double>(i, 1);
            double d  = Math.Sqrt((cx - mainCx) * (cx - mainCx) + (cy - mainCy) * (cy - mainCy));
            if (d > maxDistFromMain) continue;

            using var compMask = new Mat();
            Cv2.Compare(labels, new Scalar(i), compMask, CmpType.EQ);
            using var edgesInComp = new Mat();
            Cv2.BitwiseAnd(edges, compMask, edgesInComp);
            if ((double)Cv2.CountNonZero(edgesInComp) / a < MinComponentEdgeDensity) continue;

            Cv2.BitwiseOr(grouped, compMask, grouped);
        }

        // Final close so the kept components merge into one outline.
        using var groupedClosed = new Mat();
        Cv2.MorphologyEx(grouped, groupedClosed, MorphTypes.Close, kClose);

        // Stage C — fill the merged outline to define the ROI in which design
        // pixels live.
        Cv2.FindContours(groupedClosed, out var contours, out _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        if (contours.Length == 0) return garment.Clone();
        using var filled = new Mat(origH, origW, MatType.CV_8UC1, Scalar.Black);
        foreach (var c in contours)
            Cv2.DrawContours(filled, [c], -1, Scalar.White, thickness: -1);
        Cv2.BitwiseAnd(filled, garment, filled);

        // Stage D — pure soft alpha (no floor) inside the ROI; 0 outside.
        // Background-coloured pixels inside the ROI naturally fall to alpha 0
        // (the close-padding around the design + any genuine shirt-colour gaps
        // between design parts), so the silhouette is NOT opaque-filled.
        // Real design pixels keep alpha proportional to colour distance.
        using var alphaF = new Mat();
        Cv2.Subtract(dist, new Scalar(SoftAlphaLowDistance), alphaF);
        using var alphaScaled = new Mat();
        alphaF.ConvertTo(alphaScaled, MatType.CV_32FC1,
            255.0 / (SoftAlphaHighDistance - SoftAlphaLowDistance));
        using var alphaClamped = new Mat();
        Cv2.Min(alphaScaled, new Scalar(255), alphaClamped);
        Cv2.Max(alphaClamped, new Scalar(0), alphaClamped);
        using var softAlpha = new Mat();
        alphaClamped.ConvertTo(softAlpha, MatType.CV_8UC1);

        var result = new Mat(origH, origW, MatType.CV_8UC1, Scalar.Black);
        softAlpha.CopyTo(result, filled);   // soft alpha where ROI != 0

        // Slight feather for anti-aliased edges (small kernel — preserves detail).
        Cv2.GaussianBlur(result, result, new Size(3, 3), sigmaX: 0.7);
        return result;
    }

    /// <summary>Full LAB Euclidean distance from `gLab`, returned as 32-bit float Mat.</summary>
    private static Mat ComputeLabDistance(Mat labF, Vec3b gLab)
    {
        using var diff = new Mat();
        Cv2.Subtract(labF, new Scalar(gLab.Item0, gLab.Item1, gLab.Item2), diff);
        using var sq = new Mat();
        Cv2.Multiply(diff, diff, sq);
        Mat[] channels = Cv2.Split(sq);
        using var c0 = channels[0]; using var c1 = channels[1]; using var c2 = channels[2];
        using var sumSq = new Mat();
        Cv2.Add(c0, c1, sumSq);
        Cv2.Add(sumSq, c2, sumSq);
        var dist = new Mat();
        Cv2.Sqrt(sumSq, dist);
        return dist;
    }

    /// <summary>
    /// Light-shirt-specific design score: combines chroma distance (catches
    /// coloured designs) with luminance drop above the shadow floor (catches
    /// dark line work). Shadows score near zero.
    /// </summary>
    private static Mat ComputeLightShirtScore(Mat Lc, Mat Ac, Mat Bc, Vec3b gLab)
    {
        // chromaDist = sqrt((A-gA)² + (B-gB)²)
        using var dA = new Mat(); Cv2.Subtract(Ac, new Scalar(gLab.Item1), dA);
        using var dB = new Mat(); Cv2.Subtract(Bc, new Scalar(gLab.Item2), dB);
        using var dA2 = new Mat(); Cv2.Multiply(dA, dA, dA2);
        using var dB2 = new Mat(); Cv2.Multiply(dB, dB, dB2);
        using var chromaSq = new Mat(); Cv2.Add(dA2, dB2, chromaSq);
        using var chromaDist = new Mat(); Cv2.Sqrt(chromaSq, chromaDist);

        // lumScored = max(0, max(0, gL − L) − shadowFloor)
        using var lumDrop = new Mat();
        Cv2.Subtract(new Scalar(gLab.Item0), Lc, lumDrop);
        using var lumPos = new Mat();
        Cv2.Max(lumDrop, new Scalar(0), lumPos);
        using var lumScored = new Mat();
        Cv2.Subtract(lumPos, new Scalar(LightShirtShadowFloor), lumScored);
        Cv2.Max(lumScored, new Scalar(0), lumScored);

        // score = max(chromaDist * 3, lumScored)
        using var chromaScaled = new Mat();
        chromaDist.ConvertTo(chromaScaled, MatType.CV_32FC1, 3.0);
        var score = new Mat();
        Cv2.Max(chromaScaled, lumScored, score);
        return score;
    }

    private static (int b, int g, int r) SampleMedianColor(Mat bgr, Mat mask)
    {
        var bs = new List<byte>();
        var gs = new List<byte>();
        var rs = new List<byte>();
        int step = Math.Max(1, Math.Min(bgr.Rows, bgr.Cols) / 256);
        for (int y = 0; y < bgr.Rows; y += step)
            for (int x = 0; x < bgr.Cols; x += step)
            {
                if (mask.At<byte>(y, x) == 0) continue;
                var px = bgr.At<Vec3b>(y, x);
                bs.Add(px.Item0); gs.Add(px.Item1); rs.Add(px.Item2);
            }
        if (bs.Count == 0) return (0, 0, 0);
        bs.Sort(); gs.Sort(); rs.Sort();
        return (bs[bs.Count / 2], gs[gs.Count / 2], rs[rs.Count / 2]);
    }

    private static Mat GrabCutMask(Mat original)
    {
        int origH = original.Rows, origW = original.Cols;

        // Downscale so GrabCut completes in ~0.5–1.5 s even on huge inputs.
        double scale = Math.Min(1.0, (double)GrabCutTargetSize / Math.Max(origW, origH));
        int smW = Math.Max(2, (int)(origW * scale));
        int smH = Math.Max(2, (int)(origH * scale));

        using var small = new Mat();
        if (scale < 1.0)
            Cv2.Resize(original, small, new Size(smW, smH), 0, 0, InterpolationFlags.Area);
        else
            original.CopyTo(small);

        // Init: outer 8 % border = definite background; rest = probable foreground.
        using var mask = new Mat(smH, smW, MatType.CV_8UC1,
                                 new Scalar((byte)GrabCutClasses.BGD));
        int mx = Math.Max(1, (int)(smW * 0.08));
        int my = Math.Max(1, (int)(smH * 0.08));
        int rw = Math.Max(2, smW - 2 * mx);
        int rh = Math.Max(2, smH - 2 * my);
        var fgRect = new Rect(mx, my, rw, rh);

        using var bgModel = new Mat();
        using var fgModel = new Mat();
        Cv2.GrabCut(small, mask, fgRect, bgModel, fgModel,
                    GrabCutIterations, GrabCutModes.InitWithRect);

        // Foreground = pixels marked FGD (definite) or PR_FGD (probable).
        using var fgDef = new Mat();
        using var fgPro = new Mat();
        Cv2.Compare(mask, new Scalar((byte)GrabCutClasses.FGD),    fgDef, CmpType.EQ);
        Cv2.Compare(mask, new Scalar((byte)GrabCutClasses.PR_FGD), fgPro, CmpType.EQ);
        using var smallAlpha = new Mat();
        Cv2.BitwiseOr(fgDef, fgPro, smallAlpha);

        // Smooth speckle + close small holes.
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5));
        Cv2.MorphologyEx(smallAlpha, smallAlpha, MorphTypes.Close, kernel);
        Cv2.MorphologyEx(smallAlpha, smallAlpha, MorphTypes.Open,  kernel);

        // Upscale to original resolution. Caller (ExtractDesignMask) needs a
        // clean binary mask, so re-threshold after bicubic upscale.
        var alpha8 = new Mat();
        Cv2.Resize(smallAlpha, alpha8, new Size(origW, origH), 0, 0, InterpolationFlags.Cubic);
        Cv2.Threshold(alpha8, alpha8, 127, 255, ThresholdTypes.Binary);
        return alpha8;
    }

    private static Mat BrightnessThresholdMask(Mat original)
    {
        using var gray = new Mat();
        Cv2.CvtColor(original, gray, ColorConversionCodes.BGR2GRAY);
        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(5, 5), 0);

        var mean  = Cv2.Mean(blurred);
        var mask8 = new Mat();
        if      (mean.Val0 > 200) Cv2.Threshold(blurred, mask8, 230, 255, ThresholdTypes.BinaryInv);
        else if (mean.Val0 > 128) Cv2.Threshold(blurred, mask8, 200, 255, ThresholdTypes.BinaryInv);
        else                      Cv2.Threshold(blurred, mask8,  30, 255, ThresholdTypes.Binary);

        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(7, 7));
        Cv2.MorphologyEx(mask8, mask8, MorphTypes.Close, kernel);
        Cv2.MorphologyEx(mask8, mask8, MorphTypes.Open,  kernel);
        return mask8;
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

    private static int RoundUp(int value, int multiple) =>
        (value + multiple - 1) / multiple * multiple;

    private static string[] GetImageFiles(string folder) =>
        Directory.Exists(folder)
            ? [.. Directory.GetFiles(folder)
                .Where(f => SupportedExts.Contains(
                    Path.GetExtension(f).ToLowerInvariant()))]
            : [];

    private void Emit(string msg)
    {
        _log.Log("抠图", msg);
        LogMessage?.Invoke(this, msg);
    }

    public void Dispose()
    {
        Stop();
        _session?.Dispose();
    }
}
