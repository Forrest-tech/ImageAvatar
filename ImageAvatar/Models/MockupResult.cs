namespace ImageAvatar.Models;

public class MockupResult
{
    public string  PatternPath  { get; init; } = string.Empty;
    public string  TemplatePath { get; init; } = string.Empty;
    public string  OutputPath   { get; init; } = string.Empty;
    public bool    Success      { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan Elapsed     { get; init; }
}
