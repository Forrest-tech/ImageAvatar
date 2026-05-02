namespace ImageAvatar.Contracts.Services;

public interface IGenService
{
    bool IsRunning { get; }
    Task StartAsync(CancellationToken ct = default);
    void Stop();
}
