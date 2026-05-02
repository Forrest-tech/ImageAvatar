using ImageAvatar.Contracts.Services;

namespace ImageAvatar.Services;

public sealed class GenService : IGenService
{
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }

    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts      = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IsRunning = true;
        // TODO: poll prompt queue and submit to image-generation API
        await Task.CompletedTask;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts      = null;
        IsRunning = false;
    }
}
