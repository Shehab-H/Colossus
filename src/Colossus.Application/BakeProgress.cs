namespace Colossus.Application;

/// <summary>Stable application-level bake stages. Hosts may translate these events into console output,
/// job progress, logs, or UI updates without the use case knowing which transport is active.</summary>
public enum BakeStage
{
    Probing,
    Extracting,
    Tiling,
    Verifying,
    Publishing,
    Cleanup,
    Completed,
}

public sealed record BakeProgress(BakeStage Stage, string Message);
