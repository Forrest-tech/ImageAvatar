using ImageAvatar.Contracts.Services;
using ImageAvatar.Models;
using System.Collections.Concurrent;
using System.IO;

namespace ImageAvatar.Services;

/// <summary>
/// Watches 00_提图队列 for new image files and routes each through
/// ImageExtractionService, saving results to 01_提图完成.
/// Processes one file at a time; subsequent arrivals are queued.
/// </summary>
public sealed class PipelineCoordinatorService : IPipelineCoordinatorService, IDisposable
{
    private readonly IStorageService           _storage;
    private readonly IImageExtractionService   _extraction;
    private readonly ILogService               _log;
    private readonly ConcurrentQueue<string>   _queue = new();
    private readonly SemaphoreSlim             _gate  = new(1, 1);

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource _cts = new();

    public bool IsRunning => _watcher?.EnableRaisingEvents == true;

    public event EventHandler<ExtractionProgressEventArgs>? ProgressChanged;
    public event EventHandler<ExtractionResult>?            FileCompleted;

    public PipelineCoordinatorService(
        IStorageService storage,
        IImageExtractionService extraction,
        ILogService log)
    {
        _storage    = storage;
        _extraction = extraction;
        _log        = log;
    }

    // ── Start / Stop ───────────────────────────────────────────────────────

    public void Start()
    {
        if (IsRunning) return;

        var inputFolder = GetFolder("00_提图队列");
        Directory.CreateDirectory(inputFolder);

        _cts = new CancellationTokenSource();

        _watcher = new FileSystemWatcher(inputFolder)
        {
            Filter                = "*.*",
            NotifyFilter          = NotifyFilters.FileName | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
            EnableRaisingEvents   = true
        };

        _watcher.Created += OnFileCreated;

        _log.Log($"监控目录: {inputFolder}");

        // Process any files already sitting in the queue folder
        EnqueueExisting(inputFolder);
        _ = DrainQueueAsync();
    }

    public void Stop()
    {
        _cts.Cancel();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnFileCreated;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    // ── File arrival handler ───────────────────────────────────────────────

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        if (!ImageExtractionService.IsSupported(e.FullPath)) return;

        _queue.Enqueue(e.FullPath);
        _log.Log($"排队: {Path.GetFileName(e.FullPath)}");
        _ = DrainQueueAsync();
    }

    private void EnqueueExisting(string folder)
    {
        var existing = Directory.EnumerateFiles(folder)
            .Where(ImageExtractionService.IsSupported)
            .ToList();

        foreach (var file in existing)
            _queue.Enqueue(file);

        if (existing.Count > 0)
            _log.Log($"发现 {existing.Count} 个待处理文件");
    }

    // ── Queue drain loop (one file at a time) ──────────────────────────────

    private async Task DrainQueueAsync()
    {
        // Only one drain loop runs at a time
        if (!await _gate.WaitAsync(0)) return;

        try
        {
            var ct = _cts.Token;

            while (_queue.TryDequeue(out var filePath))
            {
                ct.ThrowIfCancellationRequested();

                if (!File.Exists(filePath)) continue;
                if (!_extraction.IsModelLoaded)
                {
                    _log.Log("模型未加载，跳过提取");
                    continue;
                }

                var fileName     = Path.GetFileName(filePath);
                var outputFolder = GetFolder("01_提图完成");

                _log.Log($"开始提取: {fileName}");

                var progress = new Progress<double>(pct =>
                    ProgressChanged?.Invoke(this, new ExtractionProgressEventArgs
                    {
                        FilePath     = filePath,
                        Progress     = pct,
                        PendingCount = _queue.Count
                    }));

                ExtractionResult result;
                try
                {
                    // Wait for file to finish being written
                    await WaitForReadyAsync(filePath, ct);
                    result = await _extraction.ExtractPatternAsync(
                        filePath, outputFolder, progress, ct);
                }
                catch (OperationCanceledException)
                {
                    _log.Log($"提取已取消: {fileName}");
                    break;
                }

                FileCompleted?.Invoke(this, result);
                _storage.RefreshAll();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private string GetFolder(string folderName) =>
        _storage.Folders.First(f => f.FolderName == folderName).FullPath;

    /// <summary>
    /// Polls until the file can be opened for reading (copy-in-progress guard).
    /// </summary>
    private static async Task WaitForReadyAsync(string path, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return;
            }
            catch (IOException) { }
            await Task.Delay(250, ct);
        }
    }

    public void Dispose()
    {
        Stop();
        _gate.Dispose();
        _cts.Dispose();
    }
}
