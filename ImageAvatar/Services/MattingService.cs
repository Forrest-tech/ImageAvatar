using ImageAvatar.Contracts.Services;
using ImageAvatar.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System.Diagnostics;
using System.IO;

namespace ImageAvatar.Services;

/// <summary>
/// Pattern Reconstruction Engine.
///
/// Upgrades raw matting into a clean, flat illustration suitable for POD.
/// Per-image pipeline:
///
///   1. Dual-path masking  — Path A (AI/GrabCut) + Path B (Laplacian+Sobel)
///      with adaptive brightness-driven thresholds.
///   2. Shadow / wrinkle removal (Intrinsic Image Decomposition lite):
///      • Lab L channel identifies low-brightness areas that are
///        white-fabric shadows (low chroma → not coloured design).
///      • Telea inpainting fills those wrinkle lines from surrounding pixels.
///      • Only activated for light garments (median Lab L > 175); skipped
///        and logged when image is too dark.
///   3. CLAHE — flattens residual luminance variation in the design area,
///      pushing it toward a clean, vector-like appearance.
///   4. White fabric removal — Lab L > 240 AND ab ≈ neutral (|a-128|&lt;8,
///      |b-128|&lt;8) → alpha = 0. Eliminates fabric background completely.
///   5. 2 px feathering — Gaussian 5×5 σ=1.0 on the alpha channel.
///   6. 32-bit transparent PNG output to 31_抠图完成.
///
/// Failures are logged as [Fail] and skipped; the batch never stops.
/// MaxDegreeOfParallelism is capped at 2 (inpainting is CPU-intensive).
/// </summary>
public sealed class MattingService : IMattingService, IDisposable
{
    private static readonly string[] SupportedExts =
        [".png", ".jpg", ".jpeg", ".bmp", ".tiff", ".tif", ".webp"];

    private static readonly float[] Mean = [0.5f, 0.5f, 0.5f];
    private static readonly float[] Std  = [0.5f, 0.5f, 0.5f];
    private const float PadPercent = 0.05f;

    private readonly ILogService             _log;
    private readonly IImageExtractionService _extraction;

    private InferenceSession?        _session;
    private CancellationTokenSource? _cts;
    private int _modelH = 512, _modelW = 512;

    public bool   IsModelLoaded { get; private set; }
    public bool   IsRunning     { get; private set; }
    public string ModelStatus   { get; private set; } = "未加载";

    public event EventHandler<string>? LogMessage;

    // ── Adaptive parameters ────────────────────────────────────────────────

    private readonly record struct AdaptiveParams(
        float  LoThreshold,
        float  HiThreshold,
        double ChromaMinDist,
        double DetailEdgeThr
    );

    private static AdaptiveParams ComputeAdaptiveParams(double imgMean) =>
        imgMean > 180 ? new(0.15f, 0.60f, 7.0,  15.0) :
        imgMean > 100 ? new(0.20f, 0.65f, 10.0, 22.0) :
                        new(0.25f, 0.70f, 13.0, 30.0);

    // ── Constructor ────────────────────────────────────────────────────────

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
        _session = new InferenceSession(modelPath, new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode          = ExecutionMode.ORT_SEQUENTIAL
        });

        var dims = _session.InputMetadata.Values.First().Dimensions;
        _modelH = dims.Length == 4 && dims[2] > 0 ? dims[2] : 512;
        _modelW = dims.Length == 4 && dims[3] > 0 ? dims[3] : 512;

        IsModelLoaded = true;
        ModelStatus   = $"✓ 已加载  (输入 {_modelW}×{_modelH})";
        Emit($"抠图模型已加载: {Path.GetFileName(modelPath)}  {_modelW}×{_modelH}");
        return Task.CompletedTask;
    }

    // ── Batch pipeline ─────────────────────────────────────────────────────

    public async Task<MattingRunResult> StartAsync(
        string inputFolder, string outputFolder, CancellationToken ct = default)
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

            string engine = IsModelLoaded ? "PaddleSeg + 重建引擎" : "GrabCut + 重建引擎";

            Emit($"开始重建 {files.Length} 张图片（{engine}）…");
            int ok = 0, fail = 0;

            // Inpainting is CPU-bound; cap at 2 threads to avoid overload.
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

            Emit($"重建完成  ✓{ok} 成功  ✗{fail} 失败  (引擎: {engine})");
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
        finally { IsRunning = false; }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts      = null;
        IsRunning = false;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  PER-IMAGE PROCESSING PATHS
    // ════════════════════════════════════════════════════════════════════════

    // ── ONNX / GrabCut path (parallel, max 2) ─────────────────────────────

    private (bool success, string message) ProcessOne(string inputPath, string outputDir)
    {
        var sw       = Stopwatch.StartNew();
        var fileName = Path.GetFileName(inputPath);
        try
        {
            using var original = Cv2.ImRead(inputPath, ImreadModes.Color);
            if (original.Empty())
            {
                Emit($"[Fail] {fileName}: 无法读取图像");
                return (false, $"[Fail] {fileName}: 无法读取图像");
            }
            int origH = original.Rows, origW = original.Cols;

            // Per-image adaptive params
            using var gray = new Mat();
            Cv2.CvtColor(original, gray, ColorConversionCodes.BGR2GRAY);
            double imgMean = Cv2.Mean(gray).Val0;
            var parms = ComputeAdaptiveParams(imgMean);

            // 1. Raw semantic alpha
            using var rawAlpha8 = _session is not null
                ? RunInferenceRaw(original, origW, origH)
                : FallbackRawAlpha(original);

            // 2. Dual-path masking + Lab boost + GrabCut refine
            using var alpha8 = ApplyDualPathEnhancement(original, rawAlpha8, parms);

            // 3. Pattern reconstruction on BGR pixels
            using var garmentMask = new Mat();
            Cv2.Threshold(alpha8, garmentMask, 20, 255, ThresholdTypes.Binary);
            using var labForSample = new Mat();
            Cv2.CvtColor(original, labForSample, ColorConversionCodes.BGR2Lab);
            double medianL = SampleMedianLabL(labForSample, garmentMask);
            using var reconstructed = RunPatternReconstruction(original, garmentMask, medianL, fileName);

            // 4. White fabric removal + feathering on alpha
            using var labRecon = new Mat();
            Cv2.CvtColor(reconstructed, labRecon, ColorConversionCodes.BGR2Lab);
            using var finalAlpha = alpha8.Clone();
            RemoveWhiteFabric(labRecon, finalAlpha);
            Cv2.GaussianBlur(finalAlpha, finalAlpha, new Size(5, 5), sigmaX: 1.0);

            // 5. Compose 32-bit BGRA
            using var bgra = new Mat();
            Cv2.CvtColor(reconstructed, bgra, ColorConversionCodes.BGR2BGRA);
            Mat[] ch = Cv2.Split(bgra);
            try   { finalAlpha.CopyTo(ch[3]); Cv2.Merge(ch, bgra); }
            finally { foreach (var c in ch) c.Dispose(); }

            // 6. Crop to visible bbox + padding
            using var nz = new Mat();
            Cv2.FindNonZero(finalAlpha, nz);
            Mat result;
            if (nz.Rows > 0)
            {
                var rect = ApplyPadding(Cv2.BoundingRect(nz), origW, origH, PadPercent);
                result   = new Mat(bgra, rect).Clone();
            }
            else
            {
                result = bgra.Clone();
                Emit($"⚠ 未检测到前景: {fileName}");
            }

            using (result)
            {
                var outName  = Path.GetFileNameWithoutExtension(inputPath) + ".png";
                var destPath = Path.Combine(outputDir, outName);
                Cv2.ImWrite(destPath, result);
                sw.Stop();
                return (true,
                    $"✓ {fileName} → {outName}  [{result.Cols}×{result.Rows}]  {sw.Elapsed.TotalSeconds:F1}s");
            }
        }
        catch (Exception ex)
        {
            Emit($"[Fail] {fileName}: {ex.Message}");
            return (false, $"[Fail] {fileName}: {ex.Message}");
        }
        finally
        {
            // Explicit gen-0 collect — reclaims native Mat memory between images.
            GC.Collect(0, GCCollectionMode.Optimized);
        }
    }

    // ── U-2-Net path (serial) ──────────────────────────────────────────────

    private async Task<(bool success, string message)> ProcessOneWithU2NetAsync(
        string inputPath, string outputDir, CancellationToken ct)
    {
        var sw       = Stopwatch.StartNew();
        var fileName = Path.GetFileName(inputPath);
        try
        {
            // Adaptive params
            using var origGray = Cv2.ImRead(inputPath, ImreadModes.Grayscale);
            double imgMean = origGray.Empty() ? 128.0 : Cv2.Mean(origGray).Val0;
            var parms = ComputeAdaptiveParams(imgMean);

            // U-2-Net inference → saves cropped transparent PNG
            var result = await _extraction.ExtractPatternAsync(inputPath, outputDir, null, ct);
            if (!result.Success)
            {
                Emit($"[Fail] {fileName}: {result.ErrorMessage}");
                return (false, $"[Fail] {fileName}: U-2-Net 失败");
            }

            // Load saved BGRA, split channels
            using var savedBgra = Cv2.ImRead(result.DestinationPath, ImreadModes.Unchanged);
            if (savedBgra.Empty() || savedBgra.Channels() != 4)
            {
                sw.Stop();
                return (true,
                    $"✓ {fileName} → {Path.GetFileName(result.DestinationPath)}  {sw.Elapsed.TotalSeconds:F1}s");
            }

            Mat[] allCh = Cv2.Split(savedBgra);
            using var bCh = allCh[0];
            using var gCh = allCh[1];
            using var rCh = allCh[2];
            using var aCh = allCh[3];

            using var bgrMat = new Mat();
            Cv2.Merge(new[] { bCh, gCh, rCh }, bgrMat);

            // Dual-path enhancement on U-2-Net alpha
            using var alpha8 = ApplyDualPathEnhancement(bgrMat, aCh, parms);

            // Pattern reconstruction
            using var garmentMask = new Mat();
            Cv2.Threshold(alpha8, garmentMask, 20, 255, ThresholdTypes.Binary);
            using var labSample = new Mat();
            Cv2.CvtColor(bgrMat, labSample, ColorConversionCodes.BGR2Lab);
            double medianL = SampleMedianLabL(labSample, garmentMask);
            using var reconstructed = RunPatternReconstruction(bgrMat, garmentMask, medianL, fileName);

            // White fabric removal + feathering
            using var labRecon = new Mat();
            Cv2.CvtColor(reconstructed, labRecon, ColorConversionCodes.BGR2Lab);
            using var finalAlpha = alpha8.Clone();
            RemoveWhiteFabric(labRecon, finalAlpha);
            Cv2.GaussianBlur(finalAlpha, finalAlpha, new Size(5, 5), sigmaX: 1.0);

            // Re-compose BGRA with reconstructed pixels + new alpha
            Mat[] reconCh = Cv2.Split(reconstructed);
            using var rb = reconCh[0]; using var rg = reconCh[1]; using var rr = reconCh[2];
            using var outBgra = new Mat();
            Cv2.Merge(new Mat[] { rb, rg, rr, finalAlpha }, outBgra);

            // Crop to new alpha bbox
            using var nz = new Mat();
            Cv2.FindNonZero(finalAlpha, nz);
            if (nz.Rows > 0)
            {
                var bb = ClampRect(Cv2.BoundingRect(nz), outBgra.Cols, outBgra.Rows);
                using var cropped = new Mat(outBgra, bb).Clone();
                Cv2.ImWrite(result.DestinationPath, cropped);
            }
            else
            {
                Cv2.ImWrite(result.DestinationPath, outBgra);
            }

            sw.Stop();
            return (true,
                $"✓ {fileName} → {Path.GetFileName(result.DestinationPath)}  {sw.Elapsed.TotalSeconds:F1}s");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Emit($"[Fail] {fileName}: {ex.Message}");
            return (false, $"[Fail] {fileName}: {ex.Message}");
        }
        finally
        {
            GC.Collect(0, GCCollectionMode.Optimized);
            GC.WaitForPendingFinalizers();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  PATTERN RECONSTRUCTION ENGINE
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Shadow removal (inpainting) + CLAHE.
    /// Skips inpainting for dark images or when shadow area is too large.
    /// Returns a new reconstructed BGR Mat; caller owns it.
    /// </summary>
    private Mat RunPatternReconstruction(
        Mat bgr, Mat garmentMask, double medianL, string fileName)
    {
        // ── Shadow removal (white shirts only) ────────────────────────────
        if (medianL < 80)
        {
            Emit($"[跳过阴影修复] {fileName}: 亮度过低（L={medianL:F0}），仅应用 CLAHE");
        }
        else
        {
            using var lab = new Mat();
            Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);

            using var shadowMask = BuildShadowMask(lab, garmentMask, medianL);
            int garmentArea = Math.Max(1, Cv2.CountNonZero(garmentMask));
            int shadowArea  = Cv2.CountNonZero(shadowMask);
            double shadowFrac = (double)shadowArea / garmentArea;

            if (shadowArea > 0 && shadowFrac < 0.08)
            {
                // Narrow wrinkle lines → inpaint from surrounding colours
                Emit($"{fileName}: 修复阴影 {shadowArea} px ({shadowFrac:P1})");
                using var inpainted = new Mat();
                Cv2.Inpaint(bgr, shadowMask, inpainted, inpaintRadius: 5,
                            flags: InpaintMethod.Telea);

                // Continue to CLAHE with inpainted image
                return ApplyClahe(inpainted, garmentMask);
            }
            else if (shadowFrac >= 0.08)
            {
                Emit($"{fileName}: 阴影区域过大（{shadowFrac:P1}），跳过修复，仅 CLAHE");
            }
        }

        // CLAHE only (no inpainting)
        return ApplyClahe(bgr, garmentMask);
    }

    /// <summary>
    /// Shadow mask: pixels that are darker than the garment median AND near-neutral
    /// in ab-chroma (still fabric/grey, not coloured design).
    /// Returns an 8-bit single-channel mask; caller owns it.
    /// </summary>
    private static Mat BuildShadowMask(Mat lab, Mat garmentMask, double medianL)
    {
        // Threshold: fabric shadows are darker than (medianL − 35), but
        // never below 100 (avoids flagging dark design elements on light shirts).
        double lThresh = Math.Max(100.0, medianL - 35.0);

        Mat[] ch = Cv2.Split(lab);
        using var lCh = ch[0];
        using var aCh = ch[1];
        using var bCh = ch[2];

        // Low-luminance area
        using var darkL = new Mat();
        Cv2.Threshold(lCh, darkL, lThresh, 255, ThresholdTypes.BinaryInv);

        // Near-neutral chroma: |a-128| < 15 AND |b-128| < 15
        using var aF = new Mat(); aCh.ConvertTo(aF, MatType.CV_32FC1);
        using var bF = new Mat(); bCh.ConvertTo(bF, MatType.CV_32FC1);
        using var dA = new Mat(); Cv2.Subtract(aF, new Scalar(128f), dA);
        using var dB = new Mat(); Cv2.Subtract(bF, new Scalar(128f), dB);
        using var dA2 = new Mat(); Cv2.Multiply(dA, dA, dA2);
        using var dB2 = new Mat(); Cv2.Multiply(dB, dB, dB2);
        using var cSq = new Mat(); Cv2.Add(dA2, dB2, cSq);
        using var chroma = new Mat(); Cv2.Sqrt(cSq, chroma);
        using var neutralF = new Mat();
        Cv2.Threshold(chroma, neutralF, 15.0, 255.0, ThresholdTypes.BinaryInv);
        using var neutral = new Mat();
        neutralF.ConvertTo(neutral, MatType.CV_8UC1);

        // Shadow = dark AND near-neutral AND inside garment
        using var raw = new Mat();
        Cv2.BitwiseAnd(darkL, neutral, raw);
        var shadowMask = new Mat();
        Cv2.BitwiseAnd(raw, garmentMask, shadowMask);

        // Dilate slightly to cover shadow borders
        using var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
        Cv2.Dilate(shadowMask, shadowMask, k);
        return shadowMask;
    }

    /// <summary>
    /// Applies CLAHE to the L channel of the full image, returns a new BGR Mat.
    /// Clip limit 2.0 flattens fabric texture without over-equalising design colours.
    /// </summary>
    private static Mat ApplyClahe(Mat bgr, Mat garmentMask)
    {
        using var lab = new Mat();
        Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);

        Mat[] ch = Cv2.Split(lab);
        using var lCh = ch[0];
        using var aCh = ch[1];
        using var bCh = ch[2];

        using var clahe       = Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new Size(8, 8));
        using var lEnhanced   = new Mat();
        clahe.Apply(lCh, lEnhanced);

        using var labOut = new Mat();
        Cv2.Merge(new[] { lEnhanced, aCh, bCh }, labOut);

        var result = new Mat();
        Cv2.CvtColor(labOut, result, ColorConversionCodes.Lab2BGR);
        return result;
    }

    // ── White fabric removal ───────────────────────────────────────────────

    /// <summary>
    /// In-place: sets alpha = 0 for pixels that are near-white
    /// (Lab L > 240, |a-128| &lt; 8, |b-128| &lt; 8).
    /// Eliminates residual shirt fabric from the alpha channel.
    /// </summary>
    private static void RemoveWhiteFabric(Mat lab, Mat alpha8)
    {
        Mat[] ch = Cv2.Split(lab);
        using var lCh = ch[0];
        using var aCh = ch[1];
        using var bCh = ch[2];

        // L > 240
        using var highL = new Mat();
        Cv2.Threshold(lCh, highL, 240, 255, ThresholdTypes.Binary);

        // 120 ≤ a ≤ 135  (|a − 128| < 8)
        using var aLow  = new Mat(); Cv2.Threshold(aCh, aLow,  119, 255, ThresholdTypes.Binary);
        using var aHigh = new Mat(); Cv2.Threshold(aCh, aHigh, 136, 255, ThresholdTypes.BinaryInv);
        using var aNear = new Mat(); Cv2.BitwiseAnd(aLow, aHigh, aNear);

        // 120 ≤ b ≤ 135  (|b − 128| < 8)
        using var bLow  = new Mat(); Cv2.Threshold(bCh, bLow,  119, 255, ThresholdTypes.Binary);
        using var bHigh = new Mat(); Cv2.Threshold(bCh, bHigh, 136, 255, ThresholdTypes.BinaryInv);
        using var bNear = new Mat(); Cv2.BitwiseAnd(bLow, bHigh, bNear);

        // Near-white = high L AND neutral ab
        using var whiteFabric = new Mat(); Cv2.BitwiseAnd(highL, aNear, whiteFabric);
        Cv2.BitwiseAnd(whiteFabric, bNear, whiteFabric);

        alpha8.SetTo(Scalar.Black, whiteFabric);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  DUAL-PATH ENHANCEMENT
    // ════════════════════════════════════════════════════════════════════════

    private static Mat ApplyDualPathEnhancement(Mat bgr, Mat aiAlpha8, AdaptiveParams p)
    {
        // Path B: Laplacian + Sobel high-pass map
        using var pathBMap = BuildHighPassDetailMap(bgr);

        // Weighted fusion
        using var merged8 = WeightedFusion(aiAlpha8, pathBMap, p.DetailEdgeThr);

        // Multi-level mask closing
        using var floatAlpha = new Mat();
        merged8.ConvertTo(floatAlpha, MatType.CV_32FC1, 1.0 / 255.0);
        using var mWide   = CreateBinaryMaskClosed(floatAlpha, p.LoThreshold, new Size(15, 15));
        using var mMedium = CreateBinaryMaskClosed(floatAlpha, 0.50f,         new Size(9,  9));
        using var mTight  = CreateBinaryMaskClosed(floatAlpha, p.HiThreshold, new Size(5,  5));
        using var combined = new Mat();
        Cv2.BitwiseAnd(mWide, mMedium, combined);
        Cv2.BitwiseOr(combined, mTight, combined);
        using var ck = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(7, 7));
        Cv2.MorphologyEx(combined, combined, MorphTypes.Close, ck);

        // Lab ab compensation for white garments
        using var boosted = BoostWhiteGarmentPatterns(bgr, combined, merged8, p.ChromaMinDist);

        // GrabCut edge refinement
        using var refined = RefineWithGrabCut(bgr, boosted);

        // 2 px feathering via box (mean) blur
        var feathered = new Mat();
        Cv2.Blur(refined, feathered, new Size(3, 3));
        return feathered;
    }

    private static Mat BuildHighPassDetailMap(Mat bgr)
    {
        using var gray    = new Mat(); Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        using var blurred = new Mat(); Cv2.GaussianBlur(gray, blurred, new Size(3, 3), 0);

        using var lapRaw = new Mat(); Cv2.Laplacian(blurred, lapRaw, MatType.CV_16SC1, ksize: 3);
        using var lap8   = new Mat(); Cv2.ConvertScaleAbs(lapRaw, lap8);

        using var sxRaw = new Mat(); Cv2.Sobel(blurred, sxRaw, MatType.CV_16SC1, 1, 0, ksize: 3);
        using var syRaw = new Mat(); Cv2.Sobel(blurred, syRaw, MatType.CV_16SC1, 0, 1, ksize: 3);
        using var sx8   = new Mat(); Cv2.ConvertScaleAbs(sxRaw, sx8);
        using var sy8   = new Mat(); Cv2.ConvertScaleAbs(syRaw, sy8);
        using var sobel8 = new Mat(); Cv2.AddWeighted(sx8, 0.5, sy8, 0.5, 0, sobel8);

        using var combined = new Mat(); Cv2.Max(lap8, sobel8, combined);

        int dRad = Math.Max(2, Math.Min(bgr.Cols, bgr.Rows) / 200);
        using var dKernel = Cv2.GetStructuringElement(
            MorphShapes.Ellipse, new Size(dRad * 2 + 1, dRad * 2 + 1));
        using var dilated = new Mat(); Cv2.Dilate(combined, dilated, dKernel);

        var pathBMap = new Mat();
        Cv2.Normalize(dilated, pathBMap, 0, 255, NormTypes.MinMax, (int)MatType.CV_8UC1);
        return pathBMap;
    }

    private static Mat WeightedFusion(Mat aiAlpha8, Mat pathBMap, double detailThresh)
    {
        using var aiFg = new Mat(); Cv2.Threshold(aiAlpha8, aiFg, 20, 255, ThresholdTypes.Binary);
        using var pBThr = new Mat(); Cv2.Threshold(pathBMap, pBThr, detailThresh, 255, ThresholdTypes.Binary);
        using var pBGated = new Mat();
        Cv2.BitwiseAnd(pathBMap, aiFg,  pBGated);
        Cv2.BitwiseAnd(pBGated,  pBThr, pBGated);

        using var pathAF = new Mat(); aiAlpha8.ConvertTo(pathAF, MatType.CV_32FC1);
        using var pathBF = new Mat(); pBGated.ConvertTo(pathBF, MatType.CV_32FC1, 0.4);
        using var sumF   = new Mat(); Cv2.Add(pathAF, pathBF, sumF);
        using var clamped = new Mat(); Cv2.Min(sumF, new Scalar(255.0), clamped);
        var merged8 = new Mat(); clamped.ConvertTo(merged8, MatType.CV_8UC1);
        return merged8;
    }

    private static Mat CreateBinaryMaskClosed(Mat floatAlpha, float threshold, Size closeSize)
    {
        using var bin32 = new Mat();
        Cv2.Threshold(floatAlpha, bin32, threshold, 1.0, ThresholdTypes.Binary);
        var bin8 = new Mat(); bin32.ConvertTo(bin8, MatType.CV_8UC1, 255.0);
        if (closeSize.Width > 1)
        {
            using var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, closeSize);
            Cv2.MorphologyEx(bin8, bin8, MorphTypes.Close, k);
        }
        return bin8;
    }

    private static Mat BoostWhiteGarmentPatterns(
        Mat bgr, Mat combinedMask, Mat alpha8, double chromaMin)
    {
        using var lab = new Mat(); Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);
        double medL = SampleMedianLabL(lab, combinedMask);
        var result = alpha8.Clone();
        if (medL < 175.0) return result;

        using var labF = new Mat(); lab.ConvertTo(labF, MatType.CV_32FC3);
        Mat[] ch = Cv2.Split(labF);
        using var Ac = ch[1]; using var Bc = ch[2]; ch[0].Dispose();
        using var dA = new Mat(); Cv2.Subtract(Ac, new Scalar(128f), dA);
        using var dB = new Mat(); Cv2.Subtract(Bc, new Scalar(128f), dB);
        using var dA2 = new Mat(); Cv2.Multiply(dA, dA, dA2);
        using var dB2 = new Mat(); Cv2.Multiply(dB, dB, dB2);
        using var cSq = new Mat(); Cv2.Add(dA2, dB2, cSq);
        using var cD = new Mat(); Cv2.Sqrt(cSq, cD);
        using var coloredF = new Mat(); Cv2.Threshold(cD, coloredF, chromaMin, 255.0, ThresholdTypes.Binary);
        using var colored8 = new Mat(); coloredF.ConvertTo(colored8, MatType.CV_8UC1);
        using var pattern  = new Mat(); Cv2.BitwiseAnd(colored8, combinedMask, pattern);
        result.SetTo(new Scalar(220), pattern);
        using var hc = new Mat(); Cv2.Threshold(alpha8, hc, 200, 255, ThresholdTypes.Binary);
        alpha8.CopyTo(result, hc);
        return result;
    }

    private static double SampleMedianLabL(Mat lab, Mat mask)
    {
        var s    = new List<double>(4096);
        int step = Math.Max(1, Math.Min(lab.Rows, lab.Cols) / 128);
        for (int y = 0; y < lab.Rows; y += step)
            for (int x = 0; x < lab.Cols; x += step)
            {
                if (mask.At<byte>(y, x) == 0) continue;
                s.Add(lab.At<Vec3b>(y, x).Item0);
            }
        if (s.Count == 0) return 0.0;
        s.Sort();
        return s[s.Count / 2];
    }

    private static Mat RefineWithGrabCut(Mat original, Mat alpha8)
    {
        int origH = original.Rows, origW = original.Cols;
        double scale = Math.Min(1.0, 512.0 / Math.Max(origW, origH));
        int smW = Math.Max(4, (int)(origW * scale));
        int smH = Math.Max(4, (int)(origH * scale));

        using var smBgr   = new Mat();
        using var smAlpha = new Mat();
        if (scale < 1.0) { Cv2.Resize(original, smBgr, new Size(smW, smH), 0, 0, InterpolationFlags.Area); }
        else              { original.CopyTo(smBgr); }
        if (scale < 1.0) { Cv2.Resize(alpha8, smAlpha, new Size(smW, smH), 0, 0, InterpolationFlags.Area); }
        else              { alpha8.CopyTo(smAlpha); }

        using var gcMask = new Mat(smH, smW, MatType.CV_8UC1, new Scalar((byte)GrabCutClasses.BGD));
        using var m30  = new Mat(); Cv2.Threshold(smAlpha, m30,  30,  255, ThresholdTypes.Binary);
        using var m100 = new Mat(); Cv2.Threshold(smAlpha, m100, 100, 255, ThresholdTypes.Binary);
        using var m200 = new Mat(); Cv2.Threshold(smAlpha, m200, 200, 255, ThresholdTypes.Binary);
        gcMask.SetTo(new Scalar((byte)GrabCutClasses.PR_BGD), m30);
        gcMask.SetTo(new Scalar((byte)GrabCutClasses.PR_FGD), m100);
        gcMask.SetTo(new Scalar((byte)GrabCutClasses.FGD),    m200);
        gcMask.Row(0).SetTo(new Scalar((byte)GrabCutClasses.BGD));
        gcMask.Row(smH - 1).SetTo(new Scalar((byte)GrabCutClasses.BGD));
        gcMask.Col(0).SetTo(new Scalar((byte)GrabCutClasses.BGD));
        gcMask.Col(smW - 1).SetTo(new Scalar((byte)GrabCutClasses.BGD));

        using var fgChk = new Mat(); Cv2.Compare(gcMask, new Scalar((byte)GrabCutClasses.FGD), fgChk, CmpType.EQ);
        using var bgChk = new Mat(); Cv2.Compare(gcMask, new Scalar((byte)GrabCutClasses.BGD), bgChk, CmpType.EQ);
        if (Cv2.CountNonZero(fgChk) == 0 || Cv2.CountNonZero(bgChk) == 0)
            return alpha8.Clone();

        try
        {
            using var bgM = new Mat(); using var fgM = new Mat();
            Cv2.GrabCut(smBgr, gcMask, default, bgM, fgM, 3, GrabCutModes.InitWithMask);
        }
        catch { return alpha8.Clone(); }

        using var gcFgD = new Mat(); Cv2.Compare(gcMask, new Scalar((byte)GrabCutClasses.FGD),    gcFgD, CmpType.EQ);
        using var gcFgP = new Mat(); Cv2.Compare(gcMask, new Scalar((byte)GrabCutClasses.PR_FGD), gcFgP, CmpType.EQ);
        using var gcAlpha = new Mat(smH, smW, MatType.CV_8UC1, Scalar.Black);
        gcAlpha.SetTo(new Scalar(255), gcFgD);
        gcAlpha.SetTo(new Scalar(200), gcFgP);
        using var gcFull = new Mat();
        Cv2.Resize(gcAlpha, gcFull, new Size(origW, origH), 0, 0, InterpolationFlags.Cubic);
        var merged = new Mat(); Cv2.Max(alpha8, gcFull, merged);
        return merged;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ONNX INFERENCE
    // ════════════════════════════════════════════════════════════════════════

    private Mat RunInferenceRaw(Mat original, int origW, int origH)
    {
        int tH = _modelH > 0 ? _modelH : RoundUp(origH, 32);
        int tW = _modelW > 0 ? _modelW : RoundUp(origW, 32);

        using var resized = new Mat(); Cv2.Resize(original, resized, new Size(tW, tH));
        var tensor = new DenseTensor<float>([1, 3, tH, tW]);
        for (int y = 0; y < tH; y++)
            for (int x = 0; x < tW; x++)
            {
                var px = resized.At<Vec3b>(y, x);
                tensor[0, 0, y, x] = (px.Item2 / 255f - Mean[0]) / Std[0];
                tensor[0, 1, y, x] = (px.Item1 / 255f - Mean[1]) / Std[1];
                tensor[0, 2, y, x] = (px.Item0 / 255f - Mean[2]) / Std[2];
            }

        var inName = _session!.InputMetadata.Keys.First();
        using var outputs   = _session.Run([NamedOnnxValue.CreateFromTensor(inName, tensor)]);
        var aT              = outputs.First().AsTensor<float>();
        var dims            = aT.Dimensions.ToArray();
        bool is4d = dims.Length == 4;
        int  outH = is4d ? dims[2] : dims[1];
        int  outW = is4d ? dims[3] : dims[2];

        var fd = new float[outH * outW];
        for (int y = 0; y < outH; y++)
            for (int x = 0; x < outW; x++)
                fd[y * outW + x] = Math.Clamp(is4d ? aT[0, 0, y, x] : aT[0, y, x], 0f, 1f);

        using var alphaF  = new Mat(outH, outW, MatType.CV_32FC1);
        alphaF.SetArray(fd);
        using var alphaUp = new Mat();
        Cv2.Resize(alphaF, alphaUp, new Size(origW, origH), 0, 0, InterpolationFlags.Cubic);
        var alpha8 = new Mat(); alphaUp.ConvertTo(alpha8, MatType.CV_8UC1, 255.0);
        return alpha8;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  CLASSICAL FALLBACK
    // ════════════════════════════════════════════════════════════════════════

    private const int    GrabCutTargetSize          = 512;
    private const int    GrabCutIterations          = 3;
    private const double StrongDesignDistance       = 25.0;
    private const double SoftAlphaLowDistance       = 6.0;
    private const double SoftAlphaHighDistance      = 28.0;
    private const double LightGarmentLBlendStart    = 140.0;
    private const double LightGarmentLBlendEnd      = 190.0;
    private const double LightShirtShadowFloor      = 50.0;
    private const double MinDesignFractionOfGarment = 0.005;
    private const double MaxDesignFractionOfGarment = 0.95;
    private const double MinComponentEdgeDensity    = 0.04;
    private const double NearbyComponentDistanceFraction = 0.6;

    private static Mat FallbackRawAlpha(Mat original)
    {
        try   { return ExtractDesignMask(original); }
        catch { try { return GrabCutMask(original); } catch { return BrightnessThresholdMask(original); } }
    }

    private static Mat ExtractDesignMask(Mat original)
    {
        int origH = original.Rows, origW = original.Cols;
        using var garment = GrabCutMask(original);
        Cv2.Threshold(garment, garment, 127, 255, ThresholdTypes.Binary);
        int ga = Cv2.CountNonZero(garment);
        if (ga < 100) return garment.Clone();

        var (b, g, r) = SampleMedianColor(original, garment);
        using var sp = new Mat(1, 1, MatType.CV_8UC3);
        sp.Set(0, 0, new Vec3b((byte)b, (byte)g, (byte)r));
        using var sl = new Mat(); Cv2.CvtColor(sp, sl, ColorConversionCodes.BGR2Lab);
        var gLab = sl.At<Vec3b>(0, 0);

        using var lab = new Mat(); Cv2.CvtColor(original, lab, ColorConversionCodes.BGR2Lab);
        using var labF = new Mat(); lab.ConvertTo(labF, MatType.CV_32FC3);
        Mat[] labCh = Cv2.Split(labF);
        using var Lc = labCh[0]; using var Ac = labCh[1]; using var Bc = labCh[2];

        using var dL = ComputeLabDistance(labF, gLab);
        using var dS = ComputeLightShirtScore(Lc, Ac, Bc, gLab);
        double lw = Math.Clamp((gLab.Item0 - LightGarmentLBlendStart) /
                               (LightGarmentLBlendEnd - LightGarmentLBlendStart), 0, 1);
        using var dL32 = new Mat(); dL.ConvertTo(dL32, MatType.CV_32FC1, 1 - lw);
        using var dS32 = new Mat(); dS.ConvertTo(dS32, MatType.CV_32FC1, lw);
        using var dist = new Mat(); Cv2.Add(dL32, dS32, dist);

        using var gray    = new Mat(); Cv2.CvtColor(original, gray, ColorConversionCodes.BGR2GRAY);
        using var gBlur   = new Mat(); Cv2.GaussianBlur(gray, gBlur, new Size(3, 3), 0);
        using var edges   = new Mat(); Cv2.Canny(gBlur, edges, 60, 150);
        int edR           = Math.Max(7, Math.Min(origW, origH) / 100);
        using var eDil    = new Mat();
        Cv2.Dilate(edges, eDil, Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(edR, edR)));

        using var sF   = new Mat(); Cv2.Threshold(dist, sF, StrongDesignDistance, 255, ThresholdTypes.Binary);
        using var sRaw = new Mat(); sF.ConvertTo(sRaw, MatType.CV_8UC1);
        Cv2.BitwiseAnd(sRaw, garment, sRaw);
        using var strong = new Mat(); Cv2.BitwiseAnd(sRaw, eDil, strong);
        using var kO = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
        Cv2.MorphologyEx(strong, strong, MorphTypes.Open, kO);

        int ckSz = Math.Max(31, Math.Min(origW, origH) / 15);
        using var kC     = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(ckSz, ckSz));
        using var merged = new Mat(); Cv2.MorphologyEx(strong, merged, MorphTypes.Close, kC);

        using var lbl = new Mat(); using var st = new Mat(); using var ce = new Mat();
        int nL = Cv2.ConnectedComponentsWithStats(merged, lbl, st, ce);
        if (nL <= 1) return garment.Clone();

        int ci = -1, ca = 0;
        for (int i = 1; i < nL; i++)
        {
            int a = st.At<int>(i, (int)ConnectedComponentsTypes.Area);
            if (a < ga * 0.0005) continue;
            using var cm = new Mat(); Cv2.Compare(lbl, new Scalar(i), cm, CmpType.EQ);
            using var ec = new Mat(); Cv2.BitwiseAnd(edges, cm, ec);
            if ((double)Cv2.CountNonZero(ec) / a < MinComponentEdgeDensity) continue;
            if (a > ca) { ca = a; ci = i; }
        }
        if (ci < 0)
        {
            for (int i = 1; i < nL; i++)
            { int a = st.At<int>(i, (int)ConnectedComponentsTypes.Area); if (a > ca) { ca = a; ci = i; } }
            if (ci < 0) return garment.Clone();
        }
        double frac = (double)ca / Math.Max(1, ga);
        if (frac < MinDesignFractionOfGarment || frac > MaxDesignFractionOfGarment)
            return garment.Clone();

        double mcx = ce.At<double>(ci, 0), mcy = ce.At<double>(ci, 1);
        int mbw = st.At<int>(ci, (int)ConnectedComponentsTypes.Width);
        int mbh = st.At<int>(ci, (int)ConnectedComponentsTypes.Height);
        double maxD = Math.Max(mbw, mbh) * NearbyComponentDistanceFraction;

        using var grouped = new Mat(origH, origW, MatType.CV_8UC1, Scalar.Black);
        for (int i = 1; i < nL; i++)
        {
            int a = st.At<int>(i, (int)ConnectedComponentsTypes.Area);
            if (a < ga * 0.0005) continue;
            double cx = ce.At<double>(i, 0), cy = ce.At<double>(i, 1);
            if (Math.Sqrt((cx - mcx) * (cx - mcx) + (cy - mcy) * (cy - mcy)) > maxD) continue;
            using var cm = new Mat(); Cv2.Compare(lbl, new Scalar(i), cm, CmpType.EQ);
            using var ec = new Mat(); Cv2.BitwiseAnd(edges, cm, ec);
            if ((double)Cv2.CountNonZero(ec) / a < MinComponentEdgeDensity) continue;
            Cv2.BitwiseOr(grouped, cm, grouped);
        }

        using var gClosed = new Mat(); Cv2.MorphologyEx(grouped, gClosed, MorphTypes.Close, kC);
        Cv2.FindContours(gClosed, out var ctrs, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        if (ctrs.Length == 0) return garment.Clone();

        using var filled = new Mat(origH, origW, MatType.CV_8UC1, Scalar.Black);
        foreach (var c in ctrs) Cv2.DrawContours(filled, [c], -1, Scalar.White, -1);
        using var hk = Cv2.GetStructuringElement(MorphShapes.Ellipse,
            new Size(Math.Max(9, ckSz / 2), Math.Max(9, ckSz / 2)));
        using var fC = new Mat(); Cv2.MorphologyEx(filled, fC, MorphTypes.Close, hk);
        Cv2.BitwiseAnd(fC, garment, fC);

        using var aF = new Mat(); Cv2.Subtract(dist, new Scalar(SoftAlphaLowDistance), aF);
        using var aS = new Mat(); aF.ConvertTo(aS, MatType.CV_32FC1,
            255.0 / (SoftAlphaHighDistance - SoftAlphaLowDistance));
        using var aC = new Mat(); Cv2.Min(aS, new Scalar(255), aC); Cv2.Max(aC, new Scalar(0), aC);
        using var soft = new Mat(); aC.ConvertTo(soft, MatType.CV_8UC1);

        var result = new Mat(origH, origW, MatType.CV_8UC1, Scalar.Black);
        soft.CopyTo(result, fC);
        return result;
    }

    private static Mat ComputeLabDistance(Mat labF, Vec3b gLab)
    {
        using var diff = new Mat(); Cv2.Subtract(labF, new Scalar(gLab.Item0, gLab.Item1, gLab.Item2), diff);
        using var sq   = new Mat(); Cv2.Multiply(diff, diff, sq);
        Mat[] ch = Cv2.Split(sq);
        using var c0 = ch[0]; using var c1 = ch[1]; using var c2 = ch[2];
        using var ss = new Mat(); Cv2.Add(c0, c1, ss); Cv2.Add(ss, c2, ss);
        var d = new Mat(); Cv2.Sqrt(ss, d); return d;
    }

    private static Mat ComputeLightShirtScore(Mat Lc, Mat Ac, Mat Bc, Vec3b gLab)
    {
        using var dA = new Mat(); Cv2.Subtract(Ac, new Scalar(gLab.Item1), dA);
        using var dB = new Mat(); Cv2.Subtract(Bc, new Scalar(gLab.Item2), dB);
        using var dA2 = new Mat(); Cv2.Multiply(dA, dA, dA2);
        using var dB2 = new Mat(); Cv2.Multiply(dB, dB, dB2);
        using var cSq = new Mat(); Cv2.Add(dA2, dB2, cSq);
        using var cD  = new Mat(); Cv2.Sqrt(cSq, cD);
        using var lD  = new Mat(); Cv2.Subtract(new Scalar(gLab.Item0), Lc, lD);
        using var lP  = new Mat(); Cv2.Max(lD, new Scalar(0), lP);
        using var lS  = new Mat(); Cv2.Subtract(lP, new Scalar(LightShirtShadowFloor), lS);
        Cv2.Max(lS, new Scalar(0), lS);
        using var cSc = new Mat(); cD.ConvertTo(cSc, MatType.CV_32FC1, 3.0);
        var score = new Mat(); Cv2.Max(cSc, lS, score); return score;
    }

    private static (int b, int g, int r) SampleMedianColor(Mat bgr, Mat mask)
    {
        var bs = new List<byte>(); var gs = new List<byte>(); var rs = new List<byte>();
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
        double scale = Math.Min(1.0, (double)GrabCutTargetSize / Math.Max(origW, origH));
        int smW = Math.Max(2, (int)(origW * scale));
        int smH = Math.Max(2, (int)(origH * scale));

        using var small = new Mat();
        if (scale < 1.0) Cv2.Resize(original, small, new Size(smW, smH), 0, 0, InterpolationFlags.Area);
        else              original.CopyTo(small);

        using var mask    = new Mat(smH, smW, MatType.CV_8UC1, new Scalar((byte)GrabCutClasses.BGD));
        int mx = Math.Max(1, (int)(smW * 0.08));
        int my = Math.Max(1, (int)(smH * 0.08));
        var fgRect = new Rect(mx, my, Math.Max(2, smW - 2 * mx), Math.Max(2, smH - 2 * my));
        using var bgM = new Mat(); using var fgM = new Mat();
        Cv2.GrabCut(small, mask, fgRect, bgM, fgM, GrabCutIterations, GrabCutModes.InitWithRect);

        using var fgD = new Mat(); Cv2.Compare(mask, new Scalar((byte)GrabCutClasses.FGD),    fgD, CmpType.EQ);
        using var fgP = new Mat(); Cv2.Compare(mask, new Scalar((byte)GrabCutClasses.PR_FGD), fgP, CmpType.EQ);
        using var smA = new Mat(); Cv2.BitwiseOr(fgD, fgP, smA);
        using var k   = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5));
        Cv2.MorphologyEx(smA, smA, MorphTypes.Close, k);
        Cv2.MorphologyEx(smA, smA, MorphTypes.Open,  k);

        var alpha8 = new Mat();
        Cv2.Resize(smA, alpha8, new Size(origW, origH), 0, 0, InterpolationFlags.Cubic);
        Cv2.Threshold(alpha8, alpha8, 127, 255, ThresholdTypes.Binary);
        return alpha8;
    }

    private static Mat BrightnessThresholdMask(Mat original)
    {
        using var gray = new Mat(); Cv2.CvtColor(original, gray, ColorConversionCodes.BGR2GRAY);
        using var blur = new Mat(); Cv2.GaussianBlur(gray, blur, new Size(5, 5), 0);
        var mean = Cv2.Mean(blur);
        var m    = new Mat();
        if      (mean.Val0 > 200) Cv2.Threshold(blur, m, 230, 255, ThresholdTypes.BinaryInv);
        else if (mean.Val0 > 128) Cv2.Threshold(blur, m, 200, 255, ThresholdTypes.BinaryInv);
        else                      Cv2.Threshold(blur, m,  30, 255, ThresholdTypes.Binary);
        using var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(7, 7));
        Cv2.MorphologyEx(m, m, MorphTypes.Close, k);
        Cv2.MorphologyEx(m, m, MorphTypes.Open,  k);
        return m;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static Rect ApplyPadding(Rect r, int w, int h, float pad)
    {
        int px = (int)(r.Width * pad); int py = (int)(r.Height * pad);
        int x  = Math.Max(0, r.X - px); int y = Math.Max(0, r.Y - py);
        int x2 = Math.Min(w, r.X + r.Width  + px);
        int y2 = Math.Min(h, r.Y + r.Height + py);
        return new Rect(x, y, x2 - x, y2 - y);
    }

    private static Rect ClampRect(Rect r, int w, int h)
    {
        int x = Math.Max(0, r.X); int y = Math.Max(0, r.Y);
        int x2 = Math.Min(w, r.X + r.Width); int y2 = Math.Min(h, r.Y + r.Height);
        return new Rect(x, y, Math.Max(1, x2 - x), Math.Max(1, y2 - y));
    }

    private static int RoundUp(int v, int m) => (v + m - 1) / m * m;

    private static string[] GetImageFiles(string folder) =>
        Directory.Exists(folder)
            ? [.. Directory.GetFiles(folder)
                .Where(f => SupportedExts.Contains(Path.GetExtension(f).ToLowerInvariant()))]
            : [];

    private void Emit(string msg) { _log.Log("抠图", msg); LogMessage?.Invoke(this, msg); }

    public void Dispose() { Stop(); _session?.Dispose(); _session = null; }
}
