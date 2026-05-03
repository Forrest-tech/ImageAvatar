namespace ImageAvatar.Models;

public enum MattingRunOutcome
{
    /// <summary>One or more files were processed (some may have failed individually).</summary>
    Completed,
    /// <summary>Input folder existed but contained no image files.</summary>
    EmptyQueue,
    /// <summary>Neither the PaddleSeg matting model nor the U-2-Net fallback was loaded.</summary>
    NoModelLoaded
}

public sealed class MattingRunResult
{
    public MattingRunOutcome Outcome      { get; init; }
    public int               InputCount   { get; init; }
    public int               SuccessCount { get; init; }
    public int               FailureCount { get; init; }
    public bool              UsedFallback { get; init; }
    public string            InputFolder  { get; init; } = string.Empty;
    public string            OutputFolder { get; init; } = string.Empty;

    public string ToShortStatus() => Outcome switch
    {
        MattingRunOutcome.Completed when SuccessCount > 0 && FailureCount == 0 =>
            $"● 抠图完成 ({SuccessCount} 张)",
        MattingRunOutcome.Completed when SuccessCount > 0 =>
            $"● 抠图完成 ({SuccessCount} 成功 / {FailureCount} 失败)",
        MattingRunOutcome.Completed =>
            $"● 全部失败 ({FailureCount} 张)",
        MattingRunOutcome.EmptyQueue  => "● 未产出：输入队列为空",
        MattingRunOutcome.NoModelLoaded => "● 未产出：未加载抠图模型",
        _ => "● 未产出"
    };
}
