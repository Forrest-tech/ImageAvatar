using ImageAvatar.Contracts.Services;
using ImageAvatar.Models;
using SkiaSharp;
using System.Diagnostics;
using System.IO;

namespace ImageAvatar.Services;

/// <summary>
/// Processes all PNG patterns in <c>50_成品队列</c> against every template in the
/// configured templates folder, saving composited results to <c>51_成品完成</c>.
///
/// Parallelism: patterns are processed concurrently (up to ProcessorCount/2 threads);
/// templates for each pattern are composed sequentially to bound peak memory.
/// Every SKBitmap and SKImage is disposed via <c>using</c> blocks.
/// </summary>
public sealed class BatchMockupService : IBatchMockupService
{
    private readonly IMockupService _mockup;
    private volatile bool _isRunning;

    public bool IsRunning => _isRunning;
    public event EventHandler<MockupResult>? FileCompleted;

    public BatchMockupService(IMockupService mockup)
    {
        _mockup = mockup;
    }

    public async Task RunAsync(
        string inputFolder,
        string outputFolder,
        string templatesFolder,
        IProgress<BatchProgressEventArgs>? progress = null,
        CancellationToken ct = default)
    {
        _isRunning = true;
        try
        {
            var patterns  = Directory.GetFiles(inputFolder,   "*.png", SearchOption.TopDirectoryOnly);
            var templates = Directory.GetFiles(templatesFolder, "*.png", SearchOption.TopDirectoryOnly);

            if (patterns.Length == 0 || templates.Length == 0)
                return;

            Directory.CreateDirectory(outputFolder);

            int total     = patterns.Length * templates.Length;
            int completed = 0;
            int failed    = 0;

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
                CancellationToken      = ct
            };

            await Parallel.ForEachAsync(patterns, parallelOptions, async (patternPath, token) =>
            {
                // Each pattern is decoded once and reused for all templates
                using var patternBmp = SKBitmap.Decode(patternPath);
                if (patternBmp is null)
                {
                    Interlocked.Add(ref failed,    templates.Length);
                    Interlocked.Add(ref completed, templates.Length);
                    return;
                }

                foreach (var templatePath in templates)
                {
                    token.ThrowIfCancellationRequested();

                    var sw = Stopwatch.StartNew();
                    MockupResult result;

                    try
                    {
                        using var templateBmp = SKBitmap.Decode(templatePath);
                        if (templateBmp is null)
                            throw new InvalidOperationException($"Cannot decode template: {templatePath}");

                        using var composedBmp = await _mockup.GenerateMockupAsync(patternBmp, templateBmp);

                        var patternStem  = Path.GetFileNameWithoutExtension(patternPath);
                        var templateStem = Path.GetFileNameWithoutExtension(templatePath);
                        var outputPath   = Path.Combine(outputFolder, $"{patternStem}_{templateStem}.png");

                        using var image = SKImage.FromBitmap(composedBmp);
                        using var data  = image.Encode(SKEncodedImageFormat.Png, 100);
                        await File.WriteAllBytesAsync(outputPath, data.ToArray(), token);

                        sw.Stop();
                        result = new MockupResult
                        {
                            PatternPath  = patternPath,
                            TemplatePath = templatePath,
                            OutputPath   = outputPath,
                            Success      = true,
                            Elapsed      = sw.Elapsed
                        };
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        Interlocked.Increment(ref failed);
                        result = new MockupResult
                        {
                            PatternPath  = patternPath,
                            TemplatePath = templatePath,
                            Success      = false,
                            ErrorMessage = ex.Message,
                            Elapsed      = sw.Elapsed
                        };
                    }

                    FileCompleted?.Invoke(this, result);

                    int done = Interlocked.Increment(ref completed);
                    progress?.Report(new BatchProgressEventArgs
                    {
                        Completed   = done,
                        Total       = total,
                        Failed      = Volatile.Read(ref failed),
                        CurrentFile = Path.GetFileName(patternPath)
                    });
                }
            });
        }
        finally
        {
            _isRunning = false;
        }
    }
}
