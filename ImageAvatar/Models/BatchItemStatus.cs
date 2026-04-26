namespace ImageAvatar.Models;

public class BatchItemStatus
{
    public string  PatternName  { get; init; } = string.Empty;
    public string  TemplateName { get; init; } = string.Empty;
    public string  OutputPath   { get; init; } = string.Empty;
    public bool    Success      { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan Elapsed     { get; init; }

    public string ElapsedText => $"{Elapsed.TotalSeconds:F1}s";
}
