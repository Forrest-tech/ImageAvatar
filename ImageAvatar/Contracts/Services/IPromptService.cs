namespace ImageAvatar.Contracts.Services;

public interface IPromptService
{
    bool IsRunning { get; }
    Task StartAsync(CancellationToken ct = default);
    void Stop();
}
