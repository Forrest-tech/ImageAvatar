namespace ImageAvatar.Contracts.Services;

public interface ICropService
{
    bool IsRunning { get; }
    Task StartAsync(CancellationToken ct = default);
    void Stop();
}
