using ImageAvatar.Contracts.Services;

namespace ImageAvatar.Services;

public sealed class PromptService : IPromptService
{
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }

    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts      = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IsRunning = true;
        // TODO: watch folder and generate SD prompts for incoming images
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
